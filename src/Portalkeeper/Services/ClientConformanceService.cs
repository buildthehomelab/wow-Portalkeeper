using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

    /// <summary>More files than this the realm doesn't ship means the torrent is wrong, not the folder: nothing is moved.</summary>
    public const int MaxRemovals = 10;
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
        RequireAcceptedBinaries(clientTorrent, binaries);
        var torrent = Torrent.Load(clientTorrent);
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

    /// <summary>
    /// Throws <see cref="ClientChangeRefusedException"/> when <paramref name="clientTorrent"/> has a
    /// program file in the client's top folder that isn't one of <paramref name="binaries"/> (by name
    /// and size; default RealmBranding.ClientBinaries). The content is checked once it's on disk, by
    /// <see cref="VerifyBinaries"/>.
    /// </summary>
    public static void RequireAcceptedBinaries(byte[] clientTorrent, IReadOnlyList<ClientBinary>? binaries = null)
    {
        binaries ??= RealmBranding.ClientBinaries;
        foreach (var file in Torrent.Load(clientTorrent).Files)
        {
            var path = RetiredPatchService.Normalize(file.Path.ToString());
            if (path.Contains('/') || !IsProgramFile(path)) continue;
            if (!binaries.Any(b => b.Name.Equals(path, StringComparison.OrdinalIgnoreCase) && b.Size == file.Length))
                throw new ClientChangeRefusedException($"The realm's client download lists {path}, which this launcher doesn't accept as part of World of Warcraft, so nothing was changed. A launcher update may be needed.");
        }
    }

    /// <summary>
    /// Before a launch: <paramref name="executable"/> has to be one of <paramref name="binaries"/>
    /// (default RealmBranding.ClientBinaries) byte for byte, and so does every other program file in
    /// its folder that has the name of one. Program files the realm doesn't ship are the player's
    /// business (conformance takes them out of a realm client).
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
            if (pins.Count == 0) continue;
            var length = new FileInfo(file).Length;
            string? hash = null;
            if (pins.Any(p => p.Size == length))
            {
                using var stream = File.OpenRead(file);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            if (hash is null || !pins.Any(p => p.Size == length && p.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{name} in the World of Warcraft folder isn't the realm's, so the game wasn't started. The launcher updates it from the realm's client.");
        }
    }

    /// <summary>
    /// Moves files the realm didn't ship, and client files longer than the torrent's (a torrent
    /// rewrites a file in place and never shortens it, so those are downloaded again from scratch), to
    /// .portalkeeper/removed/&lt;time&gt;/, where they stay for <see cref="RemovedKeptFor"/>.
    /// Shorter files stay: they may be a paused download, and the torrent's hash check keeps every piece
    /// of them that's right. More than <see cref="MaxRemovals"/> files throws
    /// <see cref="ClientChangeRefusedException"/> and moves none. Returns how many were moved.
    /// </summary>
    public static int RemoveNonConforming(string root, ClientConformance conformance)
    {
        var files = conformance.Extra.Concat(conformance.TooLong).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(relative => File.Exists(ManagedPath.Resolve(root, relative))).ToList();
        if (files.Count == 0) return 0;
        if (files.Count > MaxRemovals)
            throw new ClientChangeRefusedException($"{files.Count} files in the World of Warcraft folder aren't part of the realm's client ("
                + string.Join(", ", files.Take(5)) + (files.Count > 5 ? ", ..." : "")
                + $"). The launcher only puts aside up to {MaxRemovals} by itself, so nothing was moved. Move them out of the folder yourself, or install into an empty folder.");

        var batch = RemovedRelativePath + "/" + DateTime.UtcNow.ToString(RemovedStampFormat, CultureInfo.InvariantCulture) + "/";
        var moved = 0;
        foreach (var relative in files)
        {
            var full = ManagedPath.Resolve(root, relative);
            ManagedRuntimeWriteGuard.Check(root, full);
            var target = ManagedPath.Resolve(root, batch + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(full, target, overwrite: true);
            moved++;
        }
        PatchService.ClearClientCache(root);
        return moved;
    }

    /// <summary>Deletes what <see cref="RemoveNonConforming"/> put aside more than <see cref="RemovedKeptFor"/> ago. Best effort.</summary>
    public static void PruneRemoved(string root)
    {
        try
        {
            var removed = ManagedPath.Resolve(root, RemovedRelativePath);
            if (!Directory.Exists(removed)) return;
            foreach (var batch in Directory.EnumerateDirectories(removed).ToList())
            {
                if (!DateTime.TryParseExact(Path.GetFileName(batch), RemovedStampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                    || DateTime.UtcNow - time < RemovedKeptFor)
                    continue;
                Directory.Delete(ManagedPath.Resolve(root, RemovedRelativePath + "/" + Path.GetFileName(batch)), recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
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
