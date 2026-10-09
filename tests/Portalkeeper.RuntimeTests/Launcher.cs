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
            Check("patches-folder patch has a torrent name", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "patch-P.MPQ") == "patch-P.MPQ");
            Check("escaped name is decoded", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "Patch%2DH.MPQ") == "Patch-H.MPQ");
            Check("subfolders are not torrent patches", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "armory/x.MPQ") is null);
            Check("patches outside the patches folder are not torrent patches", RealmBranding.HostedPatchFileName(RealmBranding.RealmBaseUrl + "patch-P.MPQ") is null);
            Check("other hosts are not torrent patches", RealmBranding.HostedPatchFileName("https://example.com/realm/patch-P.MPQ") is null);
            Check("traversal is not a torrent patch", RealmBranding.HostedPatchFileName(RealmBranding.PatchBaseUrl + "..") is null);

            // Retired realm patches: removed only while still ours, the client file they replaced restored.
            await RetiredPatchChecks(root, Check);

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
            var withPassword = session with { GamePassword = "hunter2" };
            sessions.Save(withPassword);
            Check("saved login keeps the game password", sessions.Load()?.GamePassword == "hunter2");
            Check("saved login doesn't contain the game password in clear on Windows",
                !OperatingSystem.IsWindows() || !File.ReadAllText(sessionPath).Contains("hunter2", StringComparison.Ordinal));
            File.WriteAllText(sessionPath, "not json");
            Check("damaged saved login is ignored", sessions.Load() is null);
            sessions.Clear();
            Check("logout clears the saved login", !File.Exists(sessionPath));

            // Game password handed to the login patch through Config.wtf.
            var prefix = RealmLaunchService.GamePasswordPrefix;
            var config = "SET locale \"enUS\"\r\nSET accountList \"" + prefix + "hunter2\"\r\nSET accountName \"ALICE\"\r\n";
            Check("password line is blanked", RealmLaunchService.WithoutGamePassword(config)
                == "SET locale \"enUS\"\r\nSET accountList \"\"\r\nSET accountName \"ALICE\"\r\n");
            Check("an account list that isn't ours is kept",
                RealmLaunchService.WithoutGamePassword("SET accountList \"!ALICE|BOB|\"\n") == "SET accountList \"!ALICE|BOB|\"\n");
            var wtfClient = Path.Combine(root, "wtf-client");
            Directory.CreateDirectory(Path.Combine(wtfClient, "WTF"));
            var wtf = Path.Combine(wtfClient, "WTF", "Config.wtf");
            File.WriteAllText(wtf, config, new UTF8Encoding(true));
            RealmLaunchService.ForgetGamePassword(wtfClient);
            var forgotten = File.ReadAllBytes(wtf);
            Check("forgetting the password leaves no password", !Encoding.UTF8.GetString(forgotten).Contains("hunter2", StringComparison.Ordinal));
            Check("forgetting the password keeps the rest and the BOM", forgotten.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
                && Encoding.UTF8.GetString(forgotten).Contains("SET accountName \"ALICE\"", StringComparison.Ordinal));
            RealmLaunchService.ForgetGamePassword(Path.Combine(root, "no-such-client"));
            Check("forgetting without a Config.wtf does nothing", true);

            // Client files the realm dropped from its client torrent (TheraWoW's login screen).
            var removeClient = Path.Combine(root, "remove", "WoW");
            Directory.CreateDirectory(Path.Combine(removeClient, "Data", "enUS"));
            File.WriteAllBytes(Path.Combine(removeClient, "Wow.exe"), RandomNumberGenerator.GetBytes(40_000));
            var glue = RandomNumberGenerator.GetBytes(50_000);
            var gluePath = Path.Combine(removeClient, "Data", "enUS", "patch-enUS-4.MPQ");
            File.WriteAllBytes(gluePath, glue);
            var glueEntry = new RetiredPatchService.Entry
            {
                Path = "Data/enUS/patch-enUS-4.MPQ", Size = glue.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(glue)).ToLowerInvariant()
            };
            var withGlue = FixtureTorrent(removeClient, 16_384);
            Check("a file the client torrent still carries is kept",
                RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withGlue) == 0 && File.Exists(gluePath));
            File.Move(gluePath, gluePath + ".aside");
            var withoutGlue = FixtureTorrent(removeClient, 16_384);
            File.Move(gluePath + ".aside", gluePath);
            var changedGlue = (byte[])glue.Clone();
            changedGlue[10] ^= 0xFF;
            File.WriteAllBytes(gluePath, changedGlue);
            Check("a changed copy is the player's and is kept",
                RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withoutGlue) == 0 && File.Exists(gluePath));
            File.WriteAllBytes(gluePath, glue);
            Check("the shipped file is removed once the torrent drops it",
                RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withoutGlue) == 1 && !File.Exists(gluePath));
            Check("nothing is put back", !File.Exists(gluePath) && RetiredPatchService.RemoveClientFiles(removeClient, [glueEntry], withoutGlue) == 0);

            // INSTALL WOW brings the player's own addons and settings from the old client.
            var oldClient = Path.Combine(root, "import", "Old WoW");
            var newClient = Path.Combine(root, "import", "Evermore");
            var oldAddOns = Path.Combine(oldClient, "Interface", "AddOns");
            void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
            Write(Path.Combine(oldAddOns, "MyAddon", "MyAddon.toc"), "## Title: Mine");
            Write(Path.Combine(oldAddOns, "MyAddon", "Libs", "Lib.lua"), "lib");
            Write(Path.Combine(oldAddOns, "Shared", "Shared.toc"), "old");
            Write(Path.Combine(oldAddOns, "Blizzard_Old", "Blizzard_Old.toc"), "x");
            Write(Path.Combine(oldAddOns, "NotAnAddon", "readme.txt"), "x");
            Write(Path.Combine(oldClient, "WTF", "Config.wtf"), "SET gxApi \"d3d9\"");
            Write(Path.Combine(oldClient, "WTF", "Account", "ALICE", "SavedVariables", "MyAddon.lua"), "saved");
            Write(Path.Combine(oldClient, "WTF", "Account", "ALICE", "bindings-cache.wtf"), "old bindings");
            Write(Path.Combine(newClient, "Interface", "AddOns", "Shared", "Shared.toc"), "new");
            Write(Path.Combine(newClient, "WTF", "Account", "ALICE", "bindings-cache.wtf"), "new bindings");
            var outside = Path.Combine(root, "import", "outside");
            Write(Path.Combine(outside, "Linked.toc"), "x");
            Directory.CreateSymbolicLink(Path.Combine(oldAddOns, "Linked"), outside);
            var imported = ClientImportService.Import(oldClient, newClient);
            var newAddOns = Path.Combine(newClient, "Interface", "AddOns");
            Check("player's addon is copied whole", File.Exists(Path.Combine(newAddOns, "MyAddon", "Libs", "Lib.lua")));
            Check("an addon the new client has is not overwritten", File.ReadAllText(Path.Combine(newAddOns, "Shared", "Shared.toc")) == "new");
            Check("Blizzard_ folders, folders without a .toc and links are left out",
                !Directory.Exists(Path.Combine(newAddOns, "Blizzard_Old")) && !Directory.Exists(Path.Combine(newAddOns, "NotAnAddon"))
                && !Directory.Exists(Path.Combine(newAddOns, "Linked")));
            Check("SavedVariables are copied", File.ReadAllText(Path.Combine(newClient, "WTF", "Account", "ALICE", "SavedVariables", "MyAddon.lua")) == "saved");
            Check("settings the new client has are not overwritten", File.ReadAllText(Path.Combine(newClient, "WTF", "Account", "ALICE", "bindings-cache.wtf")) == "new bindings");
            Check("Config.wtf is not copied", !File.Exists(Path.Combine(newClient, "WTF", "Config.wtf")));
            Check("the old client is left as it was", File.ReadAllText(Path.Combine(oldAddOns, "Shared", "Shared.toc")) == "old"
                && File.Exists(Path.Combine(oldAddOns, "MyAddon", "MyAddon.toc")));
            Check("import counts", imported == new ClientImportResult(1, 1, 0));
            Check("importing again copies nothing", ClientImportService.Import(oldClient, newClient) == new ClientImportResult(0, 0, 0));
            Check("no import from inside the new client", ClientImportService.Import(newAddOns, newClient) == new ClientImportResult(0, 0, 0));
            Check("no import without an old client", ClientImportService.Import("", newClient) == new ClientImportResult(0, 0, 0));

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
    private static byte[] FixtureTorrent(string folder, int pieceLength, string? webSeed = null)
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
        if (webSeed is not null) torrent["url-list"] = new List<object> { webSeed };
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

    private static async Task RetiredPatchChecks(string root, Action<string, bool> check)
    {
        static byte[] Bytes(int seed, int length) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }
        var clientSrc = Path.Combine(root, "retired-src", "Evermore");
        Directory.CreateDirectory(Path.Combine(clientSrc, "Data"));
        var reforgedC = Bytes(1, 300_000);
        File.WriteAllBytes(Path.Combine(clientSrc, "Data", "patch-B.mpq"), Bytes(2, 70_001));
        File.WriteAllBytes(Path.Combine(clientSrc, "Data", "patch-C.mpq"), reforgedC);
        File.WriteAllBytes(Path.Combine(clientSrc, "Data", "patch-D.mpq"), Bytes(3, 50_003));
        var torrent = FixtureTorrent(clientSrc, 32_768, "https://seed.example/api/launcher/seed.php/key/");

        var ourC = Bytes(10, 8_396);
        var ourF = Bytes(11, 9_000);
        var ourK = Bytes(12, 7_000);
        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var known = new List<RetiredPatchService.Entry>
        {
            new() { Path = "Data/patch-C.MPQ", Size = ourC.Length, Sha256 = Hash(ourC) },
            new() { Path = "Data/Patch-F.MPQ", Size = ourF.Length, Sha256 = Hash(ourF) },
        };
        string NewClient(string name)
        {
            var c = Path.Combine(root, "retired-" + name);
            Directory.CreateDirectory(Path.Combine(c, "Data"));
            foreach (var f in Directory.GetFiles(Path.Combine(clientSrc, "Data")))
                File.Copy(f, Path.Combine(c, "Data", Path.GetFileName(f)));
            // Our old patch replaced the client's patch-C (one file on Windows; a second name elsewhere).
            File.Delete(Path.Combine(c, "Data", "patch-C.mpq"));
            File.WriteAllBytes(Path.Combine(c, "Data", "patch-C.MPQ"), ourC);
            File.WriteAllBytes(Path.Combine(c, "Data", "Patch-F.MPQ"), ourF);
            return c;
        }
        var current = new[] { "Data/patch-K.MPQ" };

        // 1. The original comes back from PatchService's backup once it passes the torrent's hashes.
        var a = NewClient("backup");
        var junk = Path.Combine(a, ".portalkeeper", "backups", "patches", "1", "patch-C.mpq");
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllBytes(junk, Bytes(99, reforgedC.Length)); // same size, wrong content
        var good = Path.Combine(a, ".portalkeeper", "backups", "patches", "2", "patch-C.mpq");
        Directory.CreateDirectory(Path.GetDirectoryName(good)!);
        File.WriteAllBytes(good, reforgedC);
        var downloads = 0;
        var stops = 0;
        var result = await RetiredPatchService.CleanupAsync(a, "r1", current, known, torrent,
            (_, _, _) => { downloads++; return Task.CompletedTask; },
            () => { stops++; return Task.CompletedTask; }, null, CancellationToken.None);
        check("sharing is stopped once before the first change", stops == 1);
        check("retired patch removed and original restored from a verified backup",
            result is { Removed: 2, Restored: 1, Pending: 0 } && downloads == 0
            && File.ReadAllBytes(Path.Combine(a, "Data", "patch-C.mpq")).AsSpan().SequenceEqual(reforgedC));
        check("a retired patch the client torrent doesn't have is just removed", !File.Exists(Path.Combine(a, "Data", "Patch-F.MPQ")));
        check("a backup failing the torrent's hashes is not restored", File.Exists(junk));
        check("a second run finds nothing to do",
            await RetiredPatchService.CleanupAsync(a, "r1", current, known, torrent, null, null, null, CancellationToken.None) is { Removed: 0, Pending: 0 });

        // 2. No backup: the file comes from the web seed, at the BEP 19 URL, and is verified.
        var b = NewClient("download");
        Uri? asked = null;
        result = await RetiredPatchService.CleanupAsync(b, "r1", current, known, torrent,
            (url, path, _) => { asked = url; File.WriteAllBytes(path, reforgedC); return Task.CompletedTask; }, null, null, CancellationToken.None);
        check("without a backup the original is downloaded from the web seed",
            result is { Restored: 1, Pending: 0 } && File.ReadAllBytes(Path.Combine(b, "Data", "patch-C.mpq")).AsSpan().SequenceEqual(reforgedC));
        check("web seed URL is seed + torrent name + path",
            asked?.ToString() == "https://seed.example/api/launcher/seed.php/key/Evermore/Data/patch-C.mpq");

        // 3. A bad download leaves our patch where it is (a stale patch beats a hole) and retries later.
        var c = NewClient("bad");
        result = await RetiredPatchService.CleanupAsync(c, "r1", current, known, torrent,
            (_, path, _) => { File.WriteAllBytes(path, Bytes(5, reforgedC.Length)); return Task.CompletedTask; }, null, null, CancellationToken.None);
        check("a download failing the torrent's hashes keeps our patch and reports it pending",
            result.Pending == 1 && File.ReadAllBytes(Path.Combine(c, "Data", "patch-C.MPQ")).AsSpan().SequenceEqual(ourC)
            && !Directory.EnumerateFiles(Path.Combine(c, ".portalkeeper", "downloads")).Any());

        // 4. Files that aren't byte-for-byte ours, and patches realm.conf still lists, are left alone.
        var d = NewClient("foreign");
        File.WriteAllBytes(Path.Combine(d, "Data", "Patch-F.MPQ"), Bytes(6, ourF.Length));
        result = await RetiredPatchService.CleanupAsync(d, "r1", new[] { "Data/patch-K.MPQ", "Data/patch-C.MPQ" }, known, torrent, null, null, null, CancellationToken.None);
        check("a same-size file that isn't ours stays", File.Exists(Path.Combine(d, "Data", "Patch-F.MPQ")));
        check("a patch realm.conf still lists stays", File.Exists(Path.Combine(d, "Data", "patch-C.MPQ")) && result.Removed == 0);

        // 5. Ledger: the first install's backup is the original; a later update keeps it.
        var e = NewClient("ledger");
        var k = Path.Combine(e, "Data", "patch-K.MPQ");
        var mine = Path.Combine(e, "Data", "mine.bak");
        File.WriteAllBytes(mine, Bytes(7, 1234)); // a player's own file our patch-K replaced
        File.WriteAllBytes(k, ourK);
        RetiredPatchService.RecordInstall(e, "r1", k, Hash(ourK), mine, known);
        var ourK2 = Bytes(13, 7_100);
        var k2Backup = Path.Combine(e, "Data", "k1.bak");
        File.WriteAllBytes(k2Backup, ourK);
        File.WriteAllBytes(k, ourK2);
        RetiredPatchService.RecordInstall(e, "r1", k, Hash(ourK2), k2Backup, known);
        var ledger = RetiredPatchService.Load(e);
        check("ledger keeps the first install's original across updates",
            ledger.Count == 1 && ledger[0].Original == "Data/mine.bak" && ledger[0].Sha256 == Hash(ourK2));
        var backupOfKnown = Path.Combine(e, "Data", "c.bak");
        File.WriteAllBytes(backupOfKnown, ourC);
        File.WriteAllBytes(Path.Combine(e, "Data", "patch-W.MPQ"), ourK);
        RetiredPatchService.RecordInstall(e, "r1", Path.Combine(e, "Data", "patch-W.MPQ"), Hash(ourK), backupOfKnown, known);
        check("an old realm patch is never recorded as the original",
            RetiredPatchService.Load(e).Single(x => x.Path == "Data/patch-W.MPQ").Original is null);
        result = await RetiredPatchService.CleanupAsync(e, "r1", new[] { "Data/patch-W.MPQ" }, known, torrent,
            (_, path, _) => { File.WriteAllBytes(path, reforgedC); return Task.CompletedTask; }, null, null, CancellationToken.None);
        var other = NewClient("other-realm");
        var ok = Path.Combine(other, "Data", "patch-K.MPQ");
        File.WriteAllBytes(ok, ourK);
        RetiredPatchService.RecordInstall(other, "r2", ok, Hash(ourK), null, known);
        await RetiredPatchService.CleanupAsync(other, "r1", current, [], torrent, null, null, null, CancellationToken.None);
        check("another realm's patches are never touched", File.Exists(ok) && RetiredPatchService.Load(other).Count == 1);
        check("a dropped ledger patch is removed and its original put back",
            File.ReadAllBytes(k).AsSpan().SequenceEqual(Bytes(7, 1234)) && !File.Exists(mine)
            && RetiredPatchService.Load(e).All(x => x.Path != "Data/patch-K.MPQ"));
    }
}
