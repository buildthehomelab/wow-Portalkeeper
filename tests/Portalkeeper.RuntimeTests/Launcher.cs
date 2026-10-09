using System.Security.Cryptography;
using System.Text;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Vaultrona fork: built-in realm, saved launcher login and the torrent sharing rules.
// Runs offline: torrents are made from fixture files and never announced anywhere reachable.
internal static partial class Program
{
    private static async Task<int> RunLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-launcher-" + Guid.NewGuid().ToString("N")[..8]);
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (!ok) failures++;
        }

        try
        {
            Console.WriteLine("== Launcher (Vaultrona fork) ==");

            // Built-in realm.
            var store = Path.Combine(root, "realms");
            Check("seed writes the built-in realm into an empty store", RealmBranding.SeedRealmStore(store));
            var seeded = new RealmConfigurationService().Load(Path.Combine(store, RealmBranding.RealmFileName));
            Check("built-in realm parses", seeded.IsConfigured && seeded.Name == "Azeroth" && seeded.ConfigUrl.Length > 0);
            Check("seed leaves an existing store alone", !RealmBranding.SeedRealmStore(store));
            var other = Path.Combine(root, "realms-other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "mine.realm.conf"), "x");
            Check("seed never adds to a store that has another realm", !RealmBranding.SeedRealmStore(other)
                && !File.Exists(Path.Combine(other, RealmBranding.RealmFileName)));

            // Which patches have torrents.
            Check("realm-folder patch has a torrent name", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "patch-P.MPQ") == "patch-P.MPQ");
            Check("escaped name is decoded", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "Patch%2DH.MPQ") == "Patch-H.MPQ");
            Check("subfolders are not torrent patches", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "armory/x.MPQ") is null);
            Check("other hosts are not torrent patches", RealmBranding.HostedPatchFileName("https://example.com/realm/patch-P.MPQ") is null);
            Check("traversal is not a torrent patch", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "..") is null);

            // Saved login.
            var sessionPath = Path.Combine(root, "session", "launcher-session.dat");
            var sessions = new LauncherSessionStore(sessionPath);
            Check("no saved login at first", sessions.Load() is null);
            var session = new LauncherSession("token-" + Guid.NewGuid().ToString("N"), "ALICE", DateTimeOffset.UtcNow.AddDays(30));
            sessions.Save(session);
            Check("saved login round trips", sessions.Load() == session);
            Check("saved login doesn't contain the token in clear on Windows",
                !OperatingSystem.IsWindows() || !File.ReadAllText(sessionPath).Contains(session.Token, StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
                Check("saved login is owner-only", (File.GetUnixFileMode(sessionPath) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);
            File.WriteAllText(sessionPath, "not json");
            Check("damaged saved login is ignored", sessions.Load() is null);
            sessions.Clear();
            Check("logout clears the saved login", !File.Exists(sessionPath));

            // Sharing rules.
            var client = Path.Combine(root, "client", "WoW");
            Directory.CreateDirectory(Path.Combine(client, "Data"));
            File.WriteAllBytes(Path.Combine(client, "Wow.exe"), RandomNumberGenerator.GetBytes(70_000));
            File.WriteAllBytes(Path.Combine(client, "Data", "common.MPQ"), RandomNumberGenerator.GetBytes(300_000));
            var torrent = FixtureTorrent(client, 32_768);
            Check("torrent name comes from the folder", TorrentService.NameOf(torrent) == "WoW");

            var cache = Path.Combine(root, "torrent-cache");
            var sharing = new TorrentService(cache);
            await sharing.ConfigureAsync(false, 0, 47391);
            Check("nothing is shared while sharing is off", !await sharing.ShareAsync(torrent, client));
            await sharing.ConfigureAsync(true, 0, 47391);
            Check("an exact copy is shared", await sharing.ShareAsync(torrent, client));
            Check("summary counts it", sharing.Summary().Shared == 1);
            await sharing.StopSharingExceptAsync(Array.Empty<string>());
            Check("stop sharing removes it", sharing.Summary().Shared == 0);

            var modified = Path.Combine(root, "modified", "WoW");
            Directory.CreateDirectory(Path.Combine(modified, "Data"));
            File.Copy(Path.Combine(client, "Wow.exe"), Path.Combine(modified, "Wow.exe"));
            var changed = File.ReadAllBytes(Path.Combine(client, "Data", "common.MPQ"));
            changed[1234] ^= 0xFF;
            File.WriteAllBytes(Path.Combine(modified, "Data", "common.MPQ"), changed);
            Check("a modified copy is not shared", !await sharing.ShareAsync(torrent, modified));
            Check("a modified copy is left untouched", File.ReadAllBytes(Path.Combine(modified, "Data", "common.MPQ")).SequenceEqual(changed));

            var partial = Path.Combine(root, "partial", "WoW");
            Directory.CreateDirectory(partial);
            File.Copy(Path.Combine(client, "Wow.exe"), Path.Combine(partial, "Wow.exe"));
            Check("an incomplete copy is not shared", !await sharing.ShareAsync(torrent, partial));
            Check("nothing is downloaded into an incomplete copy", !Directory.Exists(Path.Combine(partial, "Data")));

            Check("the copy is shared again", await sharing.ShareAsync(torrent, client));
            await sharing.ShutdownAsync();
            var hash = TorrentService.InfoHashOf(torrent);
            Check("shutdown saves resume data", File.Exists(Path.Combine(cache, "resume", hash + ".fresume")));

            var restarted = new TorrentService(cache);
            await restarted.ConfigureAsync(true, 0, 47391);
            Check("restart shares from resume data", await restarted.ShareAsync(torrent, client));
            await restarted.ShutdownAsync();

            // Discarding a download only ever deletes files in the folder it was downloading into.
            var copy = Path.Combine(root, "copy", "WoW");
            Directory.CreateDirectory(Path.Combine(copy, "Data"));
            File.Copy(Path.Combine(client, "Wow.exe"), Path.Combine(copy, "Wow.exe"));
            File.Copy(Path.Combine(client, "Data", "common.MPQ"), Path.Combine(copy, "Data", "common.MPQ"));
            var discard = new TorrentService(Path.Combine(root, "torrent-cache-discard"));
            await discard.ConfigureAsync(true, 0, 47392);
            Check("copy shared before discard", await discard.ShareAsync(torrent, copy));
            await discard.DiscardAsync(torrent, Path.Combine(root, "somewhere-else"));
            Check("discard with another folder keeps the files", File.Exists(Path.Combine(copy, "Data", "common.MPQ")) && discard.Summary().Shared == 1);
            await discard.DiscardAsync(torrent, copy);
            Check("discard removes the torrent and its files", !File.Exists(Path.Combine(copy, "Data", "common.MPQ")) && discard.Summary().Shared == 0);
            await discard.PauseAllAsync();
            await discard.ResumeAllAsync();
            Check("pause/resume with nothing running is harmless", discard.Summary().Shared == 0);
            await discard.ShutdownAsync();

            // Same size and timestamp but different bytes can't be caught by the stamp check, so a
            // changed timestamp is what forces the re-hash: prove a re-hash then refuses the copy.
            var mpq = Path.Combine(client, "Data", "common.MPQ");
            var original = File.ReadAllBytes(mpq);
            var tampered = (byte[])original.Clone();
            tampered[99] ^= 0xFF;
            File.WriteAllBytes(mpq, tampered);
            var rehash = new TorrentService(cache);
            await rehash.ConfigureAsync(true, 0, 47391);
            Check("a changed file forces a re-hash and is not shared", !await rehash.ShareAsync(torrent, client));
            Check("the changed file is left untouched", File.ReadAllBytes(mpq).SequenceEqual(tampered));
            await rehash.ShutdownAsync();

            // Self-update: the installer is only kept when it matches GitHub's published SHA-256.
            var payload = RandomNumberGenerator.GetBytes(50_000);
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string ReleaseJson(string? digest) => "{\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"Portalkeeper-Setup-9.9.9.exe\",\"size\":"
                + payload.Length + (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}]}";
            var downloads = 0;
            byte[] served = payload;
            var updates = Path.Combine(root, "updates");
            PortalkeeperUpdateService Updater(string? digest) => new(
                new HttpClient(new FakeHandler(_ => new System.Net.Http.StringContent(ReleaseJson(digest)))),
                new HttpClient(new FakeHandler(_ => { downloads++; return new System.Net.Http.ByteArrayContent(served); })),
                updates);
            var installer = await Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9");
            Check("verified installer is kept", File.ReadAllBytes(installer).SequenceEqual(payload) && Path.GetFileName(installer) == "Portalkeeper-Setup-9.9.9.exe");
            await Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9");
            Check("a verified installer isn't downloaded twice", downloads == 1);
            File.Delete(installer);
            served = (byte[])payload.Clone();
            served[10] ^= 0xFF;
            Check("a tampered installer is refused", await ThrowsAsync(() => Updater("sha256:" + payloadHash).DownloadInstallerAsync("v9.9.9")));
            Check("nothing is left behind after a refused download", !Directory.EnumerateFiles(updates).Any());
            Check("a release without a checksum is refused", await ThrowsAsync(() => Updater(null).DownloadInstallerAsync("v9.9.9")));
            Check("a non-release tag is refused", await ThrowsAsync(() => Updater("sha256:" + payloadHash).DownloadInstallerAsync("main")));
            Check("this test build can't update itself in place", !PortalkeeperUpdateService.CanUpdateInPlace);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            failures++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }

        Console.WriteLine(failures == 0 ? "Launcher: all checks passed." : $"Launcher: {failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception) { return true; }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpContent> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = respond(request) });
    }

    /// <summary>A private multi-file torrent of <paramref name="folder"/>, named after it.</summary>
    private static byte[] FixtureTorrent(string folder, int pieceLength)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetRelativePath(folder, p).Replace('\\', '/'), StringComparer.Ordinal).ToArray();
        using var data = new MemoryStream();
        foreach (var file in files)
            data.Write(File.ReadAllBytes(file));
        var all = data.ToArray();
        using var pieces = new MemoryStream();
        for (var offset = 0; offset < all.Length; offset += pieceLength)
            pieces.Write(SHA1.HashData(all.AsSpan(offset, Math.Min(pieceLength, all.Length - offset))));

        var fileList = files.Select(f => (object)new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["length"] = new FileInfo(f).Length,
            ["path"] = Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar).Cast<object>().ToList(),
        }).ToList();
        var info = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["files"] = fileList,
            ["name"] = Path.GetFileName(folder),
            ["piece length"] = (long)pieceLength,
            ["pieces"] = pieces.ToArray(),
            ["private"] = 1L,
        };
        var torrent = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["announce"] = "http://127.0.0.1:9/announce",
            ["info"] = info,
        };
        using var output = new MemoryStream();
        Bencode(output, torrent);
        return output.ToArray();
    }

    private static void Bencode(Stream output, object value)
    {
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        switch (value)
        {
            case long number: Write("i" + number + "e"); break;
            case string text: var bytes = Encoding.UTF8.GetBytes(text); Write(bytes.Length + ":"); output.Write(bytes); break;
            case byte[] raw: Write(raw.Length + ":"); output.Write(raw); break;
            case List<object> list: Write("l"); foreach (var item in list) Bencode(output, item); Write("e"); break;
            case SortedDictionary<string, object> dict:
                Write("d");
                foreach (var (key, item) in dict) { Bencode(output, key); Bencode(output, item); }
                Write("e");
                break;
            default: throw new ArgumentException("Can't bencode " + value.GetType());
        }
    }
}
