using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace Portalkeeper.Services;

public sealed record RetiredPatchResult(int Removed, int Restored, int Pending);

/// <summary>
/// Takes realm patches back out of a client once the realm stops listing them, and puts back the
/// file each one replaced. Without this an old patch stays in Data forever, and when it had the same
/// name as one of the client's own MPQs (Windows ignores case: our patch-P.MPQ is the client's
/// patch-P.mpq), that client file stays lost.
///
/// Safety rules:
/// - A file is only removed while it is byte-for-byte the patch we installed (size and SHA-256).
///   Anything else at that path is the player's, and is left alone.
/// - The original comes back from the backup PatchService made when our patch first replaced it, or,
///   for a file of the realm's client torrent, from any backup that passes the torrent's piece hashes,
///   else from the realm's web seed (also hash-checked). When no verified copy can be had yet, our
///   patch stays where it is and the next sync tries again: a stale patch is better than a hole.
/// </summary>
public static class RetiredPatchService
{
    public const string LedgerRelativePath = ".portalkeeper/realm-patches.json";
    private const string BackupsRelativePath = ".portalkeeper/backups/patches";

    /// <summary>A realm patch file the launcher put in the client.</summary>
    public sealed class Entry
    {
        /// <summary>Client-relative path with forward slashes, e.g. Data/patch-W.MPQ.</summary>
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        /// <summary>Client-relative backup of the file this patch replaced when first installed, if any.</summary>
        public string? Original { get; set; }
    }

    private sealed class Ledger
    {
        public int Version { get; set; } = 1;
        public List<Entry> Patches { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Normalize(string relative) => relative.Replace('\\', '/').TrimStart('/');

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static List<Entry> Load(string root)
    {
        try
        {
            var path = ManagedPath.Resolve(root, LedgerRelativePath);
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))?.Patches ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            return new();
        }
    }

    private static void Save(string root, List<Entry> entries)
    {
        var path = ManagedPath.Resolve(root, LedgerRelativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Ledger { Patches = entries }, Json));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Called after PatchService installs a patch at <paramref name="destination"/>. The first install
    /// at a path keeps <paramref name="backup"/> (what it replaced) as the original to restore later;
    /// updates keep the first one, so a newer patch version never "restores" an older one of ours.
    /// </summary>
    public static void RecordInstall(string root, string destination, string sha256, string? backup,
        IEnumerable<Entry> known)
    {
        if (sha256.Length == 0) return;
        var relative = Normalize(System.IO.Path.GetRelativePath(root, destination));
        var entries = Load(root);
        var entry = entries.FirstOrDefault(e => SamePath(e.Path, relative));
        if (entry is null)
        {
            entry = new Entry { Path = relative };
            if (backup is not null && !IsKnownPatch(backup, known))
                entry.Original = Normalize(System.IO.Path.GetRelativePath(root, backup));
            entries.Add(entry);
        }
        entry.Path = relative;
        entry.Sha256 = sha256.ToLowerInvariant();
        entry.Size = new FileInfo(destination).Length;
        Save(root, entries);
    }

    /// <summary>Adds installed, verified realm patches the ledger doesn't know yet (installed by an older launcher).</summary>
    public static void RecordCurrent(string root, IEnumerable<(string Destination, string Sha256)> installed)
    {
        var entries = Load(root);
        var added = false;
        foreach (var (destination, sha256) in installed)
        {
            if (sha256.Length == 0 || !File.Exists(destination)) continue;
            var relative = Normalize(System.IO.Path.GetRelativePath(root, destination));
            if (entries.Any(e => SamePath(e.Path, relative) && e.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)))
                continue;
            var entry = entries.FirstOrDefault(e => SamePath(e.Path, relative));
            if (entry is null) entries.Add(entry = new Entry { Path = relative });
            entry.Path = relative;
            entry.Sha256 = sha256.ToLowerInvariant();
            entry.Size = new FileInfo(destination).Length;
            added = true;
        }
        if (added) Save(root, entries);
    }

    /// <summary>
    /// Removes every patch in the ledger or in <paramref name="known"/> (patches the realm shipped
    /// before this launcher kept a ledger) that isn't at one of <paramref name="currentPaths"/>, and
    /// restores what it replaced. <paramref name="download"/> fetches one client-torrent file (URL,
    /// local temp path) from the realm's web seed.
    /// </summary>
    public static async Task<RetiredPatchResult> CleanupAsync(string root, IEnumerable<string> currentPaths,
        IEnumerable<Entry> known, byte[]? clientTorrent, Func<Uri, string, CancellationToken, Task>? download,
        IProgress<string>? status, CancellationToken cancellationToken)
    {
        var current = currentPaths.Select(Normalize).ToList();
        var ledger = Load(root);
        var candidates = ledger.Concat(known.Where(k => !ledger.Any(e => SamePath(e.Path, k.Path) &&
                e.Sha256.Equals(k.Sha256, StringComparison.OrdinalIgnoreCase))))
            .Where(e => !current.Any(c => SamePath(c, e.Path)))
            .ToList();
        if (candidates.Count == 0) return new(0, 0, 0);

        var torrent = clientTorrent is null ? null : Torrent.Load(clientTorrent);
        int removed = 0, restored = 0, pending = 0;
        var ledgerChanged = false;
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = ManagedPath.Resolve(root, entry.Path);
            ManagedRuntimeWriteGuard.Check(root, full);
            var inLedger = ledger.Contains(entry);
            if (!File.Exists(full) || new FileInfo(full).Length != entry.Size || !Sha256Matches(full, entry.Sha256))
            {
                // Gone, or replaced by something that isn't ours: nothing of ours left to remove.
                if (inLedger) { ledger.Remove(entry); ledgerChanged = true; }
                continue;
            }

            string? source = null, target = full;
            var sourceIsDownload = false;
            if (entry.Original is not null)
            {
                var original = ManagedPath.Resolve(root, entry.Original);
                if (File.Exists(original)) source = original;
            }
            var torrentFile = torrent?.Files.FirstOrDefault(f => SamePath(f.Path.ToString(), entry.Path));
            if (source is null && torrent is not null && torrentFile is not null)
            {
                target = ManagedPath.Resolve(root, Normalize(torrentFile.Path.ToString()));
                source = FindVerifiedBackup(root, torrent, torrentFile);
                if (source is null && download is not null && torrent.HttpSeeds.Count > 0)
                {
                    var temp = ManagedPath.Resolve(root, ".portalkeeper/downloads/" + Guid.NewGuid().ToString("N") + ".tmp");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temp)!);
                    try
                    {
                        status?.Report("Restoring client file " + System.IO.Path.GetFileName(target) + "...");
                        await download(WebSeedUrl(torrent, torrentFile), temp, cancellationToken);
                        if (MatchesTorrentFile(temp, torrent, torrentFile)) { source = temp; sourceIsDownload = true; }
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException) { }
                    finally { if (!sourceIsDownload && File.Exists(temp)) File.Delete(temp); }
                }
                if (source is null)
                {
                    pending++; // keep our patch until a verified original can be had
                    continue;
                }
            }

            File.Delete(full);
            removed++;
            if (source is not null)
            {
                ManagedRuntimeWriteGuard.Check(root, target);
                File.Move(source, target, false);
                restored++;
            }
            if (inLedger) { ledger.Remove(entry); ledgerChanged = true; }
        }
        if (ledgerChanged) Save(root, ledger);
        if (removed > 0) PatchService.ClearClientCache(root);
        return new(removed, restored, pending);
    }

    private static bool IsKnownPatch(string path, IEnumerable<Entry> known)
    {
        var size = new FileInfo(path).Length;
        return known.Any(k => k.Size == size && Sha256Matches(path, k.Sha256));
    }

    private static bool Sha256Matches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindVerifiedBackup(string root, Torrent torrent, ITorrentFile file)
    {
        var backups = ManagedPath.Resolve(root, BackupsRelativePath);
        if (!Directory.Exists(backups)) return null;
        var name = System.IO.Path.GetFileName(Normalize(file.Path.ToString()));
        foreach (var candidate in Directory.EnumerateFiles(backups, "*", SearchOption.AllDirectories))
        {
            if (!System.IO.Path.GetFileName(candidate).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(candidate).Length == file.Length && MatchesTorrentFile(candidate, torrent, file))
                return candidate;
        }
        return null;
    }

    /// <summary>BEP 19 web seed URL of one file of a multi-file torrent: seed + name + "/" + path.</summary>
    public static Uri WebSeedUrl(Torrent torrent, ITorrentFile file)
    {
        var seed = torrent.HttpSeeds[0].ToString();
        if (!seed.EndsWith('/')) seed += "/";
        var parts = new[] { torrent.Name }.Concat(Normalize(file.Path.ToString()).Split('/'));
        return new Uri(seed + string.Join("/", parts.Select(Uri.EscapeDataString)));
    }

    /// <summary>
    /// True when <paramref name="path"/> is this torrent file: same length, and every piece that lies
    /// wholly inside it passes the torrent's SHA-1. Pieces shared with a neighbouring file can't be
    /// checked from this file alone; a file too small to own a whole piece is accepted on length.
    /// </summary>
    public static bool MatchesTorrentFile(string path, Torrent torrent, ITorrentFile file)
    {
        if (new FileInfo(path).Length != file.Length) return false;
        var hashes = torrent.CreatePieceHashes();
        long pieceLength = torrent.PieceLength;
        var buffer = new byte[pieceLength];
        using var stream = File.OpenRead(path);
        for (var piece = file.StartPieceIndex; piece <= file.EndPieceIndex; piece++)
        {
            var start = piece * pieceLength;
            var end = Math.Min(start + pieceLength, torrent.Size);
            if (start < file.OffsetInTorrent || end > file.OffsetInTorrent + file.Length) continue;
            stream.Position = start - file.OffsetInTorrent;
            var length = (int)(end - start);
            stream.ReadExactly(buffer, 0, length);
            if (!SHA1.HashData(buffer.AsSpan(0, length)).AsSpan().SequenceEqual(hashes.GetHash(piece).V1Hash.Span))
                return false;
        }
        return true;
    }
}
