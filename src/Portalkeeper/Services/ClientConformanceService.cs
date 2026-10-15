using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MonoTorrent;

namespace Portalkeeper.Services;

/// <summary>What a client folder has that the realm didn't ship, and what it's missing.</summary>
/// <param name="Different">Client torrent files that are missing or have the wrong size.</param>
/// <param name="TooLong">The ones of those that are longer than the torrent's file.</param>
public sealed record ClientConformance(IReadOnlyList<string> Different, IReadOnlyList<string> TooLong, IReadOnlyList<string> Extra, int Expected)
{
    public bool Matches => Different.Count == 0 && Extra.Count == 0;
    /// <summary>Every file of the client torrent is there with its size: this is the realm's client.</summary>
    public bool HasAllClientFiles => Different.Count == 0;
}

/// <summary>A program file the realm's client has in its top folder (Wow.exe, d3d9.dll), by size and SHA-256.</summary>
public sealed record ClientBinary(string Name, long Size, string Sha256);

/// <summary>
/// The launcher won't do what the client torrent asks: it lists a program file the launcher doesn't
/// know, or it would take too many files out of the player's folder. Nothing was changed.
/// </summary>
public sealed class ClientChangeRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Keeps the game folder exactly what the realm ships: the files of its client torrent plus the realm
/// patches realm.conf lists. Anything else there (another MPQ, a dll, an exe) is moved to
/// .portalkeeper/removed, kept there for two weeks. Only what make-client-torrent.py leaves out of the torrent is
/// the player's: the top-level Interface (addons), WTF, Cache, Logs, Screenshots and Errors folders,
/// realmlist.wtf and Config.wtf, and logs and temp files. The rules below must match that script.
///
/// The torrent comes from the portal, so it isn't trusted with what runs: program files in the
/// client's top folder (which Wow.exe loads or starts) must be ones compiled into the launcher
/// (RealmBranding.ClientBinaries), and a torrent that would take more than a handful of files out of
/// the folder is refused rather than followed.
/// </summary>
public static class ClientConformanceService
{
    public const string InstallMarkerRelativePath = ".portalkeeper/client-install.json";
    public const string CachedTorrentRelativePath = ".portalkeeper/client.torrent";
    public const string RemovedRelativePath = ".portalkeeper/removed";

    /// <summary>
    /// More files than this to take out, or more than <see cref="MaxRemovedBytes"/> of files the realm
    /// doesn't ship, means the torrent is wrong, not the folder: nothing is touched. The realm retires
    /// more than that through a launcher release (RemovedClientFiles), not the torrent alone.
    /// </summary>
    public const int MaxRemovals = 10;
    public const long MaxRemovedBytes = 1L << 30;
    /// <summary>How long files taken out of the client are kept in .portalkeeper/removed.</summary>
    public static readonly TimeSpan RemovedKeptFor = TimeSpan.FromDays(14);
    private const string RemovedStampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Kinds of file Windows runs or Wow.exe loads from its own folder.</summary>
    private static readonly string[] ProgramSuffixes = { ".exe", ".dll", ".asi", ".bat", ".cmd", ".com", ".scr" };

    private static readonly HashSet<string> PlayerDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "wtf", "cache", "logs", "interface", "screenshots", "errors", ".portalkeeper" };
    private static readonly HashSet<string> PlayerFiles = new(StringComparer.OrdinalIgnoreCase)
        { "realmlist.wtf", "config.wtf", "thumbs.db", "desktop.ini", ".ds_store" };
    private static readonly string[] PlayerSuffixes = { ".log", ".tmp", ".bak" };

    /// <summary>
    /// Compares <paramref name="root"/> with the client torrent. <paramref name="realmFiles"/> are the
    /// full paths of the realm's patches, which belong there too (installed or not).
    /// </summary>
    public static ClientConformance Check(string root, byte[] clientTorrent, IEnumerable<string> realmFiles,
        IReadOnlyList<ClientBinary>? binaries = null)
    {
        var torrent = Torrent.Load(clientTorrent);
        RequireAcceptedBinaries(torrent, binaries);
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in torrent.Files)
            expected[RetiredPatchService.Normalize(file.Path.ToString())] = file.Length;
        var allowed = new HashSet<string>(realmFiles.Select(p => RetiredPatchService.Normalize(Path.GetRelativePath(root, p))),
            StringComparer.OrdinalIgnoreCase);

        var different = new List<string>();
        var tooLong = new List<string>();
        foreach (var (path, length) in expected)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            var actual = File.Exists(full) ? new FileInfo(full).Length : -1;
            if (actual == length) continue;
            different.Add(path);
            if (actual > length) tooLong.Add(path);
        }

        var extra = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var full in ShippedArea(root))
            {
                var relative = RetiredPatchService.Normalize(Path.GetRelativePath(root, full));
                if (!expected.ContainsKey(relative) && !allowed.Contains(relative)) extra.Add(relative);
            }
        }
        return new ClientConformance(different, tooLong, extra, expected.Count);
    }

    /// <summary>Every file in the part of the folder the realm decides, as make-client-torrent.py walks it.</summary>
    private static IEnumerable<string> ShippedArea(string root)
    {
        var pending = new Stack<(string Directory, bool Top)>();
        pending.Push((root, true));
        while (pending.Count > 0)
        {
            var (directory, top) = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || (top && PlayerDirectories.Contains(name))) continue;
                if (new DirectoryInfo(sub).LinkTarget is not null) continue;
                pending.Push((sub, false));
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || PlayerFiles.Contains(name)
                    || PlayerSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    continue;
                yield return file;
            }
        }
    }

    private static bool IsProgramFile(string name) =>
        ProgramSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Program files Wow.exe loads by itself when they're next to it.</summary>
    private static bool IsLibrary(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws <see cref="ClientChangeRefusedException"/> when <paramref name="clientTorrent"/> has a
    /// program file in the client's top folder that isn't one of <paramref name="binaries"/> (by name
    /// and size; default RealmBranding.ClientBinaries), or a path Windows would write under another
    /// name (version.dll. is saved as version.dll). The content is checked once it's on disk, by
    /// <see cref="VerifyBinaries"/>.
    /// </summary>
    public static void RequireAcceptedBinaries(byte[] clientTorrent, IReadOnlyList<ClientBinary>? binaries = null) =>
        RequireAcceptedBinaries(Torrent.Load(clientTorrent), binaries);

    private static void RequireAcceptedBinaries(Torrent torrent, IReadOnlyList<ClientBinary>? binaries)
    {
        binaries ??= RealmBranding.ClientBinaries;
        foreach (var file in torrent.Files)
        {
            var path = RetiredPatchService.Normalize(file.Path.ToString());
            try { ManagedPath.Relative(path); }
            catch (InvalidDataException)
            {
                throw new ClientChangeRefusedException($"The realm's client download lists a file named \"{path}\", which this launcher doesn't accept, so nothing was changed.");
            }
            if (path.Contains('/') || !IsProgramFile(path)) continue;
            if (!binaries.Any(b => b.Name.Equals(path, StringComparison.OrdinalIgnoreCase) && b.Size == file.Length))
                throw new ClientChangeRefusedException($"The realm's client download lists {path}, which this launcher doesn't accept as part of World of Warcraft, so nothing was changed. A launcher update may be needed.");
        }
    }

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> VerifiedHashes = new();

    /// <summary>SHA-256 of <paramref name="file"/>, remembered while its size and write time stay the same.</summary>
    private static string HashOf(string file, long length)
    {
        var key = (Path.GetFullPath(file), length, File.GetLastWriteTimeUtc(file));
        if (VerifiedHashes.TryGetValue(key, out var known)) return known;
        var hash = RetiredPatchService.Sha256Of(file);
        VerifiedHashes[key] = hash;
        return hash;
    }

    /// <summary>
    /// Before a launch: <paramref name="executable"/> has to be one of <paramref name="binaries"/>
    /// (default RealmBranding.ClientBinaries) byte for byte, and so does every other program file in
    /// its folder that has the name of one. A dll next to it that isn't one of them stops the launch
    /// too, since Wow.exe may load it; other program files (a tool's exe) are left alone. Hashes are
    /// remembered, so calling this off the UI thread first makes the launch's own check cheap.
    /// </summary>
    public static void VerifyBinaries(string executable, IReadOnlyList<ClientBinary>? binaries = null)
    {
        binaries ??= RealmBranding.ClientBinaries;
        var executableName = Path.GetFileName(executable);
        if (!binaries.Any(b => b.Name.Equals(executableName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{executableName} isn't the realm's World of Warcraft. INSTALL WOW puts the realm's client in place.");
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!))
        {
            var name = Path.GetFileName(file);
            var pins = binaries.Where(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pins.Count == 0)
            {
                if (IsLibrary(name))
                    throw new InvalidDataException($"{name} in the World of Warcraft folder isn't part of the realm's client, so the game wasn't started. Move it out of the folder.");
                continue;
            }
            var length = new FileInfo(file).Length;
            var hash = pins.Any(p => p.Size == length) ? HashOf(file, length) : null;
            if (hash is null || !pins.Any(p => p.Size == length && p.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{name} in the World of Warcraft folder isn't the realm's, so the game wasn't started. The launcher updates it from the realm's client.");
        }
    }

    /// <summary>
    /// Moves files the realm didn't ship to .portalkeeper/removed/&lt;time&gt;/, where they stay for
    /// <see cref="RemovedKeptFor"/>, and deletes client files longer than the torrent's (a torrent
    /// rewrites a file in place and never shortens it, so those are downloaded again from scratch, and a
    /// kept copy would only double their size on disk).
    /// Shorter files stay: they may be a paused download, and the torrent's hash check keeps every piece
    /// of them that's right. More than <see cref="MaxRemovals"/> files, or more than
    /// <see cref="MaxRemovedBytes"/> to put aside, throws <see cref="ClientChangeRefusedException"/>
    /// and touches none. Returns how many were taken out.
    /// </summary>
    public static int RemoveNonConforming(string root, ClientConformance conformance)
    {
        var tooLong = new HashSet<string>(conformance.TooLong, StringComparer.OrdinalIgnoreCase);
        var files = conformance.Extra.Concat(conformance.TooLong).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(relative => File.Exists(ManagedPath.Resolve(root, relative))).ToList();
        if (files.Count == 0) return 0;
        if (files.Count > MaxRemovals)
            throw new ClientChangeRefusedException($"{files.Count} files in the World of Warcraft folder aren't part of the realm's client ("
                + string.Join(", ", files.Take(5)) + (files.Count > 5 ? ", ..." : "")
                + $"). The launcher only takes out up to {MaxRemovals} by itself, so nothing was changed. Move them out of the folder yourself, or install into an empty folder.");
        var setAside = files.Where(f => !tooLong.Contains(f)).ToList();
        var bytes = setAside.Sum(f => new FileInfo(ManagedPath.Resolve(root, f)).Length);
        if (bytes > MaxRemovedBytes)
            throw new ClientChangeRefusedException($"{bytes / (1024 * 1024):N0} MB of files in the World of Warcraft folder aren't part of the realm's client ("
                + string.Join(", ", setAside.Take(5)) + (setAside.Count > 5 ? ", ..." : "")
                + "). That's more than the launcher takes out by itself, so nothing was changed. Move them out of the folder yourself, or install into an empty folder.");

        var batch = RemovedRelativePath + "/" + DateTime.UtcNow.ToString(RemovedStampFormat, CultureInfo.InvariantCulture) + "/";
        var removed = 0;
        foreach (var relative in files)
        {
            var full = ManagedPath.Resolve(root, relative);
            ManagedRuntimeWriteGuard.Check(root, full);
            if (tooLong.Contains(relative))
                File.Delete(full);
            else
            {
                var target = ManagedPath.Resolve(root, batch + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(full, target, overwrite: true);
            }
            removed++;
        }
        PatchService.ClearClientCache(root);
        return removed;
    }

    /// <summary>Deletes what <see cref="RemoveNonConforming"/> put aside more than <see cref="RemovedKeptFor"/> ago. Best effort, folder by folder.</summary>
    public static void PruneRemoved(string root)
    {
        string removed;
        try
        {
            removed = ManagedPath.Resolve(root, RemovedRelativePath);
            if (!Directory.Exists(removed)) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
        foreach (var batch in Directory.EnumerateDirectories(removed).ToList())
        {
            if (!DateTime.TryParseExact(Path.GetFileName(batch), RemovedStampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                || DateTime.UtcNow - time < RemovedKeptFor)
                continue;
            try { Directory.Delete(ManagedPath.Resolve(root, RemovedRelativePath + "/" + Path.GetFileName(batch)), recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    public enum Verdict { Ok, NotRealmClient, NeedsUpdate }

    /// <param name="Verdict">Ok: playable. NotRealmClient: has to go through INSTALL WOW. NeedsUpdate: client files differ.</param>
    /// <param name="Conformance">The check the verdict came from (null when no torrent could be used).</param>
    /// <param name="Removed">How many files were taken out of the client.</param>
    /// <param name="Notice">Why the launcher didn't do everything the realm's torrent asks, for the player.</param>
    /// <param name="TorrentRefused">The torrent given was refused; the last one seen here was used, if any.</param>
    public sealed record Enforcement(Verdict Verdict, ClientConformance? Conformance, int Removed, string Notice, bool TorrentRefused);

    /// <summary>
    /// The automatic check of a client (every sync and before ENTER REALM): tidies the launcher's own
    /// folders, compares the client with <paramref name="clientTorrent"/> and takes out what the realm
    /// doesn't ship. A refused torrent falls back to the last one this client was checked against
    /// (which passed); without one the client is left alone. Doesn't download anything: the caller
    /// starts the update when the verdict is NeedsUpdate and the torrent wasn't refused.
    /// </summary>
    public static Enforcement Enforce(string root, byte[] clientTorrent, IEnumerable<string> realmFiles,
        IReadOnlyList<ClientBinary>? binaries = null)
    {
        PruneRemoved(root);
        PatchService.PruneBackups(root);
        var files = realmFiles.ToList();
        var notice = "";
        var refused = false;
        ClientConformance result;
        try { result = Check(root, clientTorrent, files, binaries); }
        catch (ClientChangeRefusedException ex)
        {
            notice = ex.Message;
            refused = true;
            var cached = LoadCachedTorrent(root);
            if (cached is null) return new(Verdict.Ok, null, 0, notice, true);
            try { result = Check(root, cached, files, binaries); }
            catch (Exception) // refused too, or unreadable
            {
                return new(Verdict.Ok, null, 0, notice, true);
            }
        }
        if (!result.HasAllClientFiles && !IsRealmInstall(root))
            return new(Verdict.NotRealmClient, result, 0, notice, refused);
        var removed = 0;
        if (result.Extra.Count > 0)
        {
            try { removed = RemoveNonConforming(root, result with { TooLong = [] }); }
            catch (ClientChangeRefusedException ex) { notice = notice.Length > 0 ? notice + " " + ex.Message : ex.Message; }
        }
        if (result.HasAllClientFiles)
        {
            if (!refused) SaveCachedTorrent(root, clientTorrent);
            return new(Verdict.Ok, result, removed, notice, refused);
        }
        return new(Verdict.NeedsUpdate, result, removed, notice, refused);
    }

    /// <summary>The realm's client was installed here, or a client was made into it (INSTALL WOW leaves a marker).</summary>
    public static bool IsRealmInstall(string root) => File.Exists(Path.Combine(root, InstallMarkerRelativePath));

    /// <summary>The last client torrent seen for this install, for checking it while the portal is unreachable.</summary>
    public static byte[]? LoadCachedTorrent(string root)
    {
        var path = Path.Combine(root, CachedTorrentRelativePath);
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void SaveCachedTorrent(string root, byte[] torrent)
    {
        var path = Path.Combine(root, CachedTorrentRelativePath);
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(torrent)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, torrent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
