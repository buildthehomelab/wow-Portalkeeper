using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace Portalkeeper.Services;

public sealed record TorrentProgress(double Percent, long DoneBytes, long TotalBytes, long DownloadRate, long UploadRate, int Peers);

public sealed record SharingSummary(int Shared, long UploadRate, int Peers);

/// <summary>
/// BitTorrent for the realm's client and patches (MonoTorrent). Downloads come from other players
/// and from the realm's web seed; while sharing is on, finished files are shared back.
///
/// Two rules keep a player's install safe:
/// - Only a complete, hash-verified copy is shared. If any file is missing, a different size or
///   fails the hash check, that torrent is not shared at all, and nothing is ever downloaded into an
///   install the player already has.
/// - Fast-resume data (which skips re-hashing gigabytes on every start) is only trusted while every
///   file still has the size and modification time it had when the data was saved.
/// </summary>
public sealed class TorrentService
{
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ClientEngine? _engine;
    private int _port;
    private int _uploadLimit;

    public TorrentService(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Portalkeeper", "torrents");
    }

    public bool SharingEnabled { get; private set; }

    /// <summary>Applies the sharing settings. Turning sharing off stops every torrent that's only sharing.</summary>
    public async Task ConfigureAsync(bool share, int uploadBytesPerSecond, int port)
    {
        await _lock.WaitAsync();
        try
        {
            SharingEnabled = share;
            _uploadLimit = Math.Max(0, uploadBytesPerSecond);
            var portChanged = _port != port;
            _port = port;
            if (_engine is null) return;
            if (portChanged)
            {
                // The listen port is fixed when the engine starts; restart it.
                foreach (var manager in _engine.Torrents.ToArray())
                    await StopAndSaveAsync(manager);
                _engine.Dispose();
                _engine = null;
                return;
            }
            await _engine.UpdateSettingsAsync(BuildSettings());
            if (!share)
                foreach (var manager in _engine.Torrents.Where(m => m.Complete).ToArray())
                    await RemoveAsync(manager);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Downloads a torrent's files into <paramref name="saveDirectory"/> (the files go directly in it,
    /// not in a folder named after the torrent) and verifies every piece. Cancelling keeps what was
    /// downloaded; the next call resumes. With <paramref name="keepSharing"/> and sharing on, the
    /// finished download is shared from where it is.
    /// </summary>
    public async Task DownloadAsync(byte[] torrentBytes, string saveDirectory, bool keepSharing,
        IProgress<TorrentProgress>? progress, CancellationToken cancellationToken)
    {
        var torrent = Torrent.Load(torrentBytes);
        TorrentManager manager;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            manager = await AddAsync(torrent, saveDirectory);
            if (manager.State is TorrentState.Stopped or TorrentState.Error)
            {
                foreach (var file in manager.Files)
                    await manager.SetFilePriorityAsync(file, Priority.Normal);
                if (!await TryLoadFastResumeAsync(manager) && manager.Files.Any(f => File.Exists(f.FullPath)))
                    await manager.HashCheckAsync(false);
                await manager.StartAsync();
            }
        }
        finally { _lock.Release(); }

        try
        {
            while (!manager.Complete)
            {
                if (manager.State == TorrentState.Error)
                    throw new IOException("Download failed: " + (manager.Error?.Exception?.Message ?? "unknown error") + ".");
                progress?.Report(Progress(manager));
                await Task.Delay(500, cancellationToken);
            }
            progress?.Report(Progress(manager));
        }
        catch (OperationCanceledException)
        {
            await _lock.WaitAsync();
            try { await StopAndSaveAsync(manager); }
            finally { _lock.Release(); }
            throw;
        }

        await _lock.WaitAsync();
        try
        {
            if (keepSharing && SharingEnabled)
                await SaveFastResumeAsync(manager);
            else
                await RemoveAsync(manager);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Shares an existing copy of a torrent's files from <paramref name="saveDirectory"/>. Returns false
    /// (and shares nothing) when sharing is off or the copy isn't complete and identical.
    /// </summary>
    public async Task<bool> ShareAsync(byte[] torrentBytes, string saveDirectory)
    {
        if (!SharingEnabled) return false;
        var torrent = Torrent.Load(torrentBytes);
        TorrentManager manager;
        await _lock.WaitAsync();
        try
        {
            if (!SharingEnabled) return false;
            manager = await AddAsync(torrent, saveDirectory);
            if (manager.State is TorrentState.Seeding or TorrentState.Downloading or TorrentState.Hashing or TorrentState.Starting
                or TorrentState.Paused)
                return true;

            if (manager.Files.Any(f => !File.Exists(f.FullPath) || new FileInfo(f.FullPath).Length != f.Length))
            {
                await RemoveAsync(manager);
                return false;
            }
            if (await TryLoadFastResumeAsync(manager))
                return await StartIfCompleteAsync(manager);
        }
        finally { _lock.Release(); }

        // A whole client takes minutes to hash: do it without holding up other downloads.
        await manager.HashCheckAsync(false);

        await _lock.WaitAsync();
        try
        {
            if (_engine is null || !_engine.Torrents.Contains(manager)) return false;
            return await StartIfCompleteAsync(manager);
        }
        finally { _lock.Release(); }
    }

    private async Task<bool> StartIfCompleteAsync(TorrentManager manager)
    {
        if (!SharingEnabled || !manager.Complete)
        {
            await RemoveAsync(manager);
            return false;
        }
        await SaveFastResumeAsync(manager);
        await manager.StartAsync();
        return true;
    }

    /// <summary>Stops sharing every torrent whose info hash isn't in <paramref name="keep"/>.</summary>
    public async Task StopSharingExceptAsync(IReadOnlyCollection<string> keep)
    {
        await _lock.WaitAsync();
        try
        {
            if (_engine is null) return;
            foreach (var manager in _engine.Torrents.Where(m => m.Complete && !keep.Contains(Key(m))).ToArray())
                await RemoveAsync(manager);
        }
        finally { _lock.Release(); }
    }

    public static string InfoHashOf(byte[] torrentBytes) => Key(Torrent.Load(torrentBytes).InfoHashes);

    /// <summary>The torrent's name: the client's folder name, or a patch's file name.</summary>
    public static string NameOf(byte[] torrentBytes)
    {
        var name = Torrent.Load(torrentBytes).Name;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("The torrent has an unusable name.");
        return name;
    }

    /// <summary>Pauses every transfer (while the game runs, so sharing never costs the player latency).</summary>
    public async Task PauseAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_engine is null) return;
            foreach (var manager in _engine.Torrents.Where(m => m.State is TorrentState.Seeding or TorrentState.Downloading).ToArray())
                await manager.PauseAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task ResumeAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_engine is null) return;
            foreach (var manager in _engine.Torrents.Where(m => m.State == TorrentState.Paused).ToArray())
                await manager.StartAsync();
        }
        finally { _lock.Release(); }
    }

    public SharingSummary Summary()
    {
        var engine = _engine;
        if (engine is null) return new SharingSummary(0, 0, 0);
        var sharing = engine.Torrents.Where(m => m.Complete
            && m.State is TorrentState.Seeding or TorrentState.Starting or TorrentState.Downloading).ToArray();
        return new SharingSummary(sharing.Length, sharing.Sum(m => m.Monitor.UploadRate), sharing.Sum(m => m.OpenConnections));
    }

    /// <summary>Stops everything and saves resume data. Called when Portalkeeper closes.</summary>
    public async Task ShutdownAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_engine is null) return;
            foreach (var manager in _engine.Torrents.ToArray())
                await StopAndSaveAsync(manager);
            _engine.Dispose();
            _engine = null;
        }
        finally { _lock.Release(); }
    }

    private EngineSettings BuildSettings()
    {
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = _cacheDirectory,
            AllowPortForwarding = true,
            // The realm's torrents are private: peers come only from the realm's tracker.
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            UsePartialFiles = false,
            MaximumUploadRate = _uploadLimit,
            // Prefer other players; fall back to the realm's web seed when they're slow or absent.
            WebSeedDelay = TimeSpan.FromSeconds(5),
            WebSeedSpeedTrigger = 2 * 1024 * 1024,
        };
        builder.ListenEndPoints = new Dictionary<string, IPEndPoint>
        {
            ["ipv4"] = new IPEndPoint(IPAddress.Any, _port),
            ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, _port),
        };
        // Tell the realm's tracker our home-network address. Players behind the same router are
        // then handed each other's LAN address first, so a copy next door beats the internet.
        if (LanAddress() is { } lan)
            builder.ReportedListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new IPEndPoint(lan, _port) };
        return builder.ToSettings();
    }

    /// <summary>The private IPv4 address this machine uses for outgoing traffic, if it has one.</summary>
    internal static IPAddress? LanAddress()
    {
        try
        {
            // Connecting a UDP socket only picks the route; no packet is sent.
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("192.0.2.1", 9); // TEST-NET-1, never routed anywhere real
            var address = (socket.LocalEndPoint as IPEndPoint)?.Address;
            if (address is null) return null;
            var b = address.GetAddressBytes();
            var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
            return isPrivate ? address : null;
        }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    private async Task<TorrentManager> AddAsync(Torrent torrent, string saveDirectory)
    {
        _engine ??= new ClientEngine(BuildSettings());
        var full = Path.GetFullPath(saveDirectory);
        var existing = _engine.Torrents.FirstOrDefault(m => m.InfoHashes == torrent.InfoHashes);
        if (existing is not null)
        {
            if (string.Equals(Path.GetFullPath(existing.SavePath), full, StringComparison.Ordinal))
                return existing;
            await RemoveAsync(existing);
        }
        var settings = new TorrentSettingsBuilder { CreateContainingDirectory = false }.ToSettings();
        return await _engine.AddAsync(torrent, full, settings);
    }

    private async Task RemoveAsync(TorrentManager manager)
    {
        await StopAndSaveAsync(manager);
        await _engine!.RemoveAsync(manager, RemoveMode.KeepAllData);
    }

    private async Task StopAndSaveAsync(TorrentManager manager)
    {
        if (manager.State != TorrentState.Stopped)
            await manager.StopAsync(TimeSpan.FromSeconds(3));
        await SaveFastResumeAsync(manager);
    }

    private static TorrentProgress Progress(TorrentManager manager)
    {
        var total = manager.Torrent?.Size ?? 0;
        return new TorrentProgress(manager.Progress, (long)(total * manager.Progress / 100.0), total,
            manager.Monitor.DownloadRate, manager.Monitor.UploadRate, manager.OpenConnections);
    }

    // Resume data lives next to a snapshot of each file's size and modification time.

    private sealed record FileStamp(string Path, long Length, long ModifiedTicks);

    private static string Key(TorrentManager manager) => Key(manager.InfoHashes);
    private static string Key(InfoHashes hashes) => (hashes.V1 ?? hashes.V2!).ToHex().ToLowerInvariant();
    private string ResumePath(TorrentManager manager) => Path.Combine(_cacheDirectory, "resume", Key(manager) + ".fresume");
    private string StampPath(TorrentManager manager) => Path.Combine(_cacheDirectory, "resume", Key(manager) + ".files.json");

    private static FileStamp[] Stamps(TorrentManager manager) => manager.Files.Select(f =>
    {
        var info = new FileInfo(f.FullPath);
        return new FileStamp(Path.GetRelativePath(manager.SavePath, f.FullPath).Replace('\\', '/'),
            info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
    }).ToArray();

    private async Task SaveFastResumeAsync(TorrentManager manager)
    {
        if (!manager.HashChecked) return;
        try
        {
            var resume = await manager.SaveFastResumeAsync();
            Directory.CreateDirectory(Path.Combine(_cacheDirectory, "resume"));
            await File.WriteAllBytesAsync(ResumePath(manager), resume.Encode());
            await File.WriteAllBytesAsync(StampPath(manager), JsonSerializer.SerializeToUtf8Bytes(Stamps(manager)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Without resume data the next start hashes the files again; nothing is lost.
        }
    }

    private async Task<bool> TryLoadFastResumeAsync(TorrentManager manager)
    {
        try
        {
            if (!File.Exists(ResumePath(manager)) || !File.Exists(StampPath(manager))) return false;
            var saved = JsonSerializer.Deserialize<FileStamp[]>(await File.ReadAllBytesAsync(StampPath(manager)));
            if (saved is null || !saved.SequenceEqual(Stamps(manager))) return false;
            if (!FastResume.TryLoad(ResumePath(manager), out var resume) || resume is null) return false;
            await manager.LoadFastResumeAsync(resume);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
