using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portalkeeper.Services;

namespace Portalkeeper.RuntimeTests;

// Vaultrona fork: sharing combat logs with the realm (CombatLogUploadService), against a stand-in
// for the portal's combatlog.php that keeps what it's sent and checks size and hash at the end.
internal static partial class Program
{
    private static async Task CombatLogChecks(string root, Action<string, bool> check)
    {
        var api = new Uri("https://portal.test/api/launcher/");
        var session = new LauncherSession(FakeCombatLogPortal.Token, "ALICE", DateTimeOffset.UtcNow.AddDays(30));
        var clock = new DateTimeOffset(2026, 10, 13, 18, 30, 0, TimeSpan.Zero);
        const int part = 16 * 1024;
        var offer = new CombatLogOffer(part, 8 << 20);

        static byte[] LogBytes(int seed, int lines)
        {
            // Lines with enough noise that the gzip takes several parts.
            var random = new Random(seed);
            var noise = new byte[24];
            var text = new StringBuilder();
            for (var line = 0; line < lines; line++)
            {
                random.NextBytes(noise);
                text.Append(CultureInfo.InvariantCulture, $"10/13 18:{line / 60 % 60:00}:{line % 60:00}.000  SPELL_DAMAGE,0x")
                    .Append(Convert.ToHexString(noise)).Append(",\"Ragnaros\"\r\n");
            }
            return Encoding.UTF8.GetBytes(text.ToString());
        }
        static byte[] Gunzip(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)) gzip.CopyTo(output);
            return output.ToArray();
        }
        string NewClient(string name)
        {
            var client = Path.Combine(root, "combatlog", name);
            Directory.CreateDirectory(Path.Combine(client, "Logs"));
            return client;
        }
        static string LogPath(string client) => Path.Combine(client, "Logs", CombatLogUploadService.LogFileName);
        static string Pending(string client) => Path.Combine(client, "Logs", CombatLogUploadService.PendingFolder);
        static string[] Left(string client) => Directory.Exists(Pending(client))
            ? Directory.GetFiles(Pending(client)).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray()
            : [];
        string Queue(string client, DateTimeOffset queued, byte[] bytes)
        {
            var path = Path.Combine(Pending(client), "WoWCombatLog-" + queued.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            Directory.CreateDirectory(Pending(client));
            File.WriteAllBytes(path, bytes);
            return path;
        }
        CombatLogUploadService Service(FakeCombatLogPortal portal) => new(api, new HttpClient(portal), () => clock);

        // What the portal offers.
        var asked = new FakeCombatLogPortal { PartSize = 4 << 20, MaxSize = 512 << 20 };
        check("a realm that collects combat logs says so, with its limits",
            await Service(asked).GetOfferAsync(session) == new CombatLogOffer(4 << 20, 512 << 20));
        asked.Enabled = false;
        check("a realm that has it switched off doesn't offer it", await Service(asked).GetOfferAsync(session) is null);
        asked.Offered = false;
        check("404 means the realm doesn't collect combat logs", await Service(asked).GetOfferAsync(session) is null);
        var refused = false;
        try { await Service(asked).GetOfferAsync(session with { Token = "stale" }); }
        catch (LauncherApiException ex) { refused = ex.Status == HttpStatusCode.Unauthorized; }
        check("a refused login is reported, not taken for 'no'", refused);
        asked.Offline = true;
        check("an unreachable portal is reported, not taken for 'no'", await ThrowsAsync(() => Service(asked).GetOfferAsync(session)));

        // The addon the launcher writes itself.
        var addonClient = NewClient("addon");
        File.WriteAllBytes(Path.Combine(addonClient, "Wow.exe"), RandomNumberGenerator.GetBytes(20_000));
        Directory.CreateDirectory(Path.Combine(addonClient, "Data"));
        File.WriteAllBytes(Path.Combine(addonClient, "Data", "common.MPQ"), RandomNumberGenerator.GetBytes(40_000));
        var clientTorrent = FixtureTorrent(addonClient, 16_384);
        var mine = Path.Combine(addonClient, "Interface", "AddOns", "Mine", "Mine.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "## Title: Mine");
        var addon = Path.Combine(addonClient, "Interface", "AddOns", CombatLogUploadService.AddonFolder);
        var toc = Path.Combine(addon, CombatLogUploadService.AddonFolder + ".toc");
        var lua = Path.Combine(addon, CombatLogUploadService.AddonFolder + ".lua");
        CombatLogUploadService.SyncAddon(addonClient, false);
        check("with the option off there is no addon", !Directory.Exists(addon));
        CombatLogUploadService.SyncAddon(addonClient, true);
        var tocLines = File.Exists(toc) ? File.ReadAllLines(toc) : [];
        check("with the option on the addon is written for 3.3.5a", tocLines.Contains("## Interface: 30300")
            && tocLines.Contains(Path.GetFileName(lua)) && File.Exists(lua)
            && !CombatLogUploadService.AddonFolder.StartsWith("Blizzard", StringComparison.OrdinalIgnoreCase));
        var script = File.Exists(lua) ? File.ReadAllText(lua) : "";
        check("the addon logs in dungeons and raids and stops only what it started",
            script.Contains("PLAYER_ENTERING_WORLD") && script.Contains("ZONE_CHANGED_NEW_AREA") && script.Contains("IsInInstance()")
            && script.Contains("\"party\"") && script.Contains("\"raid\"") && script.Contains("LoggingCombat(1)")
            && script.Contains("startedHere") && script.Contains("LoggingCombat(0)"));
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(lua, written);
        File.WriteAllText(toc, "## Interface: 11302");
        CombatLogUploadService.SyncAddon(addonClient, true);
        check("a changed addon file is put back and an unchanged one is left alone",
            File.ReadAllLines(toc).Contains("## Interface: 30300") && File.GetLastWriteTimeUtc(lua) == written);
        Queue(addonClient, clock, LogBytes(1, 10));
        check("the addon and pending logs aren't files the realm didn't ship",
            ClientConformanceService.Check(addonClient, clientTorrent, [], BinaryPins(addonClient)).Matches);
        check("the realm's addons are judged as before", new AddonService().InspectAddons(addonClient,
            new Portalkeeper.Models.AddonManifest { Addons = [new() { Id = "mine", Name = "Mine", Folder = "Mine", Required = true }] })
            .Single() is { IsInstalled: true, IsUpdateAvailable: false });
        CombatLogUploadService.SyncAddon(addonClient, false);
        check("turning the option off deletes the addon and nothing else", !Directory.Exists(addon) && File.Exists(mine));

        // Happy path: the log is moved aside, gzipped and sent in several parts.
        var happy = NewClient("happy");
        var happyLog = LogBytes(2, 4000);
        File.WriteAllBytes(LogPath(happy), happyLog);
        var loggedUntil = new DateTime(2026, 10, 13, 2, 15, 30, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LogPath(happy), loggedUntil);
        var portal = new FakeCombatLogPortal { PartSize = part };
        var lines = new List<string>();
        var result = await Service(portal).UploadPendingAsync(happy, session, offer, new SyncProgress<string>(lines.Add));
        var sent = portal.Completed.SingleOrDefault() ?? [];
        check("a log is uploaded and arrives whole", result == new CombatLogUploadResult(1, 0, 0) && Gunzip(sent).AsSpan().SequenceEqual(happyLog));
        var parts = portal.Requests.Where(r => r.StartsWith("part@")).ToArray();
        check($"it goes in several parts, in order ({parts.Length})", parts.Length >= 3 && portal.OversizedParts == 0 && portal.Conflicts == 0
            && parts.SequenceEqual(Enumerable.Range(0, parts.Length).Select(i => "part@" + (long)i * part))
            && portal.Requests[0] == "begin" && portal.Requests[^1] == "finish" && portal.Requests.Count == parts.Length + 2);
        check("every call carries the login and parts are sent as bytes", portal.Unauthorized == 0 && portal.WrongContentType == 0);
        var begin = portal.Begins.Single();
        check("begin describes the gzip and when the log was written",
            begin.GetProperty("size").GetInt64() == sent.Length
            && begin.GetProperty("sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(sent))
            && begin.GetProperty("raw_size").GetInt64() == happyLog.Length
            && begin.GetProperty("logged_until").GetInt64() == new DateTimeOffset(loggedUntil).ToUnixTimeSeconds()
            && begin.GetProperty("utc_offset_minutes").GetInt32() == (int)TimeZoneInfo.Local.GetUtcOffset(loggedUntil).TotalMinutes
            && begin.GetProperty("client").GetString() == "Portalkeeper/" + RealmConfigurationService.CurrentVersion);
        check("the game starts a new log and nothing is left behind", !File.Exists(LogPath(happy)) && Left(happy).Length == 0);
        check("progress goes from 0% to 100%", lines.Count >= 3 && lines[0] == "Preparing combat log..."
            && lines[1] == "Uploading combat log... 0%" && lines[^1] == "Uploading combat log... 100%");
        lines.Clear();
        var before = portal.Requests.Count;
        check("with nothing to upload nothing is asked or shown",
            await Service(portal).UploadPendingAsync(happy, session, offer, new SyncProgress<string>(lines.Add)) == new CombatLogUploadResult(0, 0, 0)
            && portal.Requests.Count == before && lines.Count == 0);

        // The same file again (the answer to finish never arrived): begin says it's complete.
        Queue(happy, clock, happyLog);
        result = await Service(portal).UploadPendingAsync(happy, session, offer);
        check("a log the portal already has is done at begin", result == new CombatLogUploadResult(1, 0, 0)
            && portal.Requests.Skip(before).SequenceEqual(["begin"]) && Left(happy).Length == 0 && portal.Completed.Count == 1);

        // A part fails: everything stays, and the next run continues where the portal says it stopped.
        var resumed = NewClient("resume");
        var resumedLog = LogBytes(3, 4000);
        File.WriteAllBytes(LogPath(resumed), resumedLog);
        portal = new FakeCombatLogPortal { PartSize = part, FailPartNumber = 2 };
        result = await Service(portal).UploadPendingAsync(resumed, session, offer);
        var queued = "WoWCombatLog-20261013-183000.txt";
        check("a failed part leaves the log and its gzip for the next run", result == new CombatLogUploadResult(0, 0, 1)
            && Left(resumed).SequenceEqual([queued, queued + ".gz"]) && portal.Completed.Count == 0 && !portal.Requests.Contains("finish"));
        var gzipBefore = File.ReadAllBytes(Path.Combine(Pending(resumed), queued + ".gz"));
        before = portal.Requests.Count;
        result = await Service(portal).UploadPendingAsync(resumed, session, offer);
        check("the next run continues from what the portal has", result == new CombatLogUploadResult(1, 0, 0)
            && portal.Requests.Skip(before).Take(2).SequenceEqual(["begin", "part@" + 2 * part])
            && portal.Completed.Single().AsSpan().SequenceEqual(gzipBefore) && Gunzip(portal.Completed.Single()).AsSpan().SequenceEqual(resumedLog)
            && Left(resumed).Length == 0);

        // 409: the portal is further along than begin said; the upload continues from "received".
        var conflict = NewClient("conflict");
        var conflictLog = LogBytes(4, 4000);
        File.WriteAllBytes(LogPath(conflict), conflictLog);
        portal = new FakeCombatLogPortal { PartSize = part, FailPartNumber = 3 };
        await Service(portal).UploadPendingAsync(conflict, session, offer);
        portal.BeginForgetsReceived = true;
        before = portal.Requests.Count;
        result = await Service(portal).UploadPendingAsync(conflict, session, offer);
        check("after a 409 the upload continues from the portal's offset", result == new CombatLogUploadResult(1, 0, 0) && portal.Conflicts == 1
            && portal.Requests.Skip(before).Take(3).SequenceEqual(["begin", "part@0", "part@" + 3 * part])
            && Gunzip(portal.Completed.Single()).AsSpan().SequenceEqual(conflictLog) && Left(conflict).Length == 0);
        File.WriteAllBytes(LogPath(conflict), LogBytes(5, 2000));
        portal = new FakeCombatLogPortal { PartSize = part, AlwaysConflict = true };
        result = await Service(portal).UploadPendingAsync(conflict, session, offer);
        check("a portal that keeps answering 409 isn't followed forever", result == new CombatLogUploadResult(0, 0, 1)
            && portal.Conflicts is > 1 and < 20 && Left(conflict).Length == 2);

        // finish 422: the portal threw the upload away. Nothing half-done is kept; next time from begin.
        var rejected = NewClient("rejected");
        var rejectedLog = LogBytes(6, 4000);
        File.WriteAllBytes(LogPath(rejected), rejectedLog);
        portal = new FakeCombatLogPortal { PartSize = part, RejectFinishes = 1 };
        result = await Service(portal).UploadPendingAsync(rejected, session, offer);
        check("a refused finish keeps the log and nothing half-done", result == new CombatLogUploadResult(0, 0, 1)
            && Left(rejected).SequenceEqual([queued]) && portal.Completed.Count == 0 && portal.Requests[^1] == "finish");
        var firstRun = portal.Requests.Count;
        result = await Service(portal).UploadPendingAsync(rejected, session, offer);
        check("the next run uploads it again from the beginning", result == new CombatLogUploadResult(1, 0, 0)
            && portal.Requests.Skip(firstRun).Take(2).SequenceEqual(["begin", "part@0"]) && portal.Requests.Count == 2 * firstRun
            && Gunzip(portal.Completed.Single()).AsSpan().SequenceEqual(rejectedLog) && Left(rejected).Length == 0);

        // No log, an empty one, and one the game still has open.
        var quiet = NewClient("quiet");
        portal = new FakeCombatLogPortal { PartSize = part };
        result = await Service(portal).UploadPendingAsync(quiet, session, offer);
        check("without a combat log nothing happens", result == new CombatLogUploadResult(0, 0, 0) && portal.Requests.Count == 0
            && !Directory.Exists(Pending(quiet)));
        File.WriteAllBytes(LogPath(quiet), []);
        result = await Service(portal).UploadPendingAsync(quiet, session, offer);
        check("an empty combat log is left where it is", result == new CombatLogUploadResult(0, 0, 0) && portal.Requests.Count == 0
            && File.Exists(LogPath(quiet)) && Left(quiet).Length == 0);
        File.Delete(LogPath(quiet));
        check("a client without a Logs folder is fine too", Service(portal).CollectLog(Path.Combine(root, "combatlog", "nowhere")) is null
            && await Service(portal).UploadPendingAsync(Path.Combine(root, "combatlog", "nowhere"), session, offer) == new CombatLogUploadResult(0, 0, 0));
        var lockedLog = LogBytes(7, 2000);
        File.WriteAllBytes(LogPath(quiet), lockedLog);
        // Windows refuses to move a file that's open without sharing; elsewhere a folder that can't be
        // written to refuses the same way (not for root, which may write anywhere).
        if (OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            var logs = Path.GetDirectoryName(LogPath(quiet))!;
            FileStream? open = null;
            if (OperatingSystem.IsWindows()) open = new FileStream(LogPath(quiet), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                result = await Service(portal).UploadPendingAsync(quiet, session, offer);
                check("a log the game still has open is skipped quietly", result == new CombatLogUploadResult(0, 0, 0)
                    && portal.Requests.Count == 0 && File.Exists(LogPath(quiet)) && Left(quiet).Length == 0);
            }
            finally
            {
                open?.Dispose();
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        result = await Service(portal).UploadPendingAsync(quiet, session, offer, collectLog: false);
        check("while a game may be writing the log it isn't collected", result == new CombatLogUploadResult(0, 0, 0) && File.Exists(LogPath(quiet)));
        result = await Service(portal).UploadPendingAsync(quiet, session, offer);
        check("once it's free it is uploaded", result == new CombatLogUploadResult(1, 0, 0)
            && Gunzip(portal.Completed.Single()).AsSpan().SequenceEqual(lockedLog));

        // Collecting: the name carries the UTC time, the log keeps its date, and nothing is overwritten.
        var collect = NewClient("collect");
        File.WriteAllBytes(LogPath(collect), LogBytes(8, 5));
        File.SetLastWriteTimeUtc(LogPath(collect), loggedUntil);
        var first = Service(portal).CollectLog(collect);
        File.WriteAllBytes(LogPath(collect), LogBytes(9, 5));
        var second = Service(portal).CollectLog(collect);
        check("a collected log is named after the UTC time and keeps its date", first is not null
            && Path.GetFileName(first) == queued && File.GetLastWriteTimeUtc(first) == loggedUntil);
        check("two logs in the same second don't overwrite each other", second is not null
            && Path.GetFileName(second) == "WoWCombatLog-20261013-183000-2.txt" && File.ReadAllBytes(first!).AsSpan().SequenceEqual(LogBytes(8, 5)));

        // Opting in: a log that was already there is renamed in place, not queued, uploaded or deleted.
        var earlier = NewClient("earlier");
        check("with no earlier log there's nothing to set aside", Service(portal).SetAsideLog(earlier)
            && Directory.GetFiles(Path.Combine(earlier, "Logs")).Length == 0);
        var earlierLog = LogBytes(20, 40);
        File.WriteAllBytes(LogPath(earlier), earlierLog);
        portal = new FakeCombatLogPortal();
        var aside = Path.Combine(earlier, "Logs", "WoWCombatLog-before-sharing-20261013-183000.txt");
        check("a log from before opting in is renamed in place", Service(portal).SetAsideLog(earlier)
            && !File.Exists(LogPath(earlier)) && File.Exists(aside) && File.ReadAllBytes(aside).AsSpan().SequenceEqual(earlierLog));
        result = await Service(portal).UploadPendingAsync(earlier, session, offer);
        check("and is never queued or sent", result == new CombatLogUploadResult(0, 0, 0) && portal.Requests.Count == 0
            && Left(earlier).Length == 0 && File.Exists(aside));
        var laterLog = LogBytes(21, 40);
        File.WriteAllBytes(LogPath(earlier), laterLog);
        result = await Service(portal).UploadPendingAsync(earlier, session, offer);
        check("what is logged afterwards is uploaded, the earlier log stays", result == new CombatLogUploadResult(1, 0, 0)
            && portal.Completed.Count == 1 && Gunzip(portal.Completed[0]).AsSpan().SequenceEqual(laterLog) && File.Exists(aside));
        File.WriteAllBytes(LogPath(earlier), LogBytes(22, 5));
        check("a second earlier log in the same second gets its own name", Service(portal).SetAsideLog(earlier)
            && File.Exists(Path.Combine(earlier, "Logs", "WoWCombatLog-before-sharing-20261013-183000-2.txt")));
        check("a client without a Logs folder has nothing to set aside", Service(portal).SetAsideLog(Path.Combine(root, "combatlog", "nowhere")));

        // Housekeeping: logs queued more than 14 days ago go, and so does what an interrupted run left.
        var old = NewClient("old");
        var expired = Queue(old, clock.AddDays(-15), LogBytes(10, 50));
        File.WriteAllBytes(expired + ".gz", [1, 2, 3]);
        var recentLog = LogBytes(11, 50);
        Queue(old, clock.AddDays(-13), recentLog);
        File.WriteAllBytes(Path.Combine(Pending(old), "WoWCombatLog-20260901-000000.txt.gz"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Pending(old), "WoWCombatLog-20261012-000000.txt.gz.tmp"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(Pending(old), "notes.txt"), "the player's own file");
        portal = new FakeCombatLogPortal { PartSize = part };
        result = await Service(portal).UploadPendingAsync(old, session, offer);
        check("a log pending for more than 14 days is deleted unsent, a newer one is uploaded",
            result == new CombatLogUploadResult(1, 1, 0) && portal.Begins.Count == 1
            && Gunzip(portal.Completed.Single()).AsSpan().SequenceEqual(recentLog));
        check("leftovers of an interrupted run are cleared, a file that isn't ours stays", Left(old).SequenceEqual(["notes.txt"]));
        Queue(old, clock.AddDays(-20), LogBytes(12, 50));
        var kept = Queue(old, clock.AddDays(-1), LogBytes(13, 50));
        check("with the option off old pending logs are still cleared away", Service(portal).DeleteExpired(old) == 1
            && Left(old).SequenceEqual([Path.GetFileName(kept), "notes.txt"]) && portal.Begins.Count == 1);

        // Oldest first.
        var smaller = LogBytes(14, 100);
        Queue(old, clock.AddDays(-3), smaller);
        lines.Clear();
        result = await Service(portal).UploadPendingAsync(old, session, offer, new SyncProgress<string>(lines.Add));
        check("pending logs go oldest first", result == new CombatLogUploadResult(2, 0, 0) && portal.Begins.Count == 3
            && portal.Begins[1].GetProperty("raw_size").GetInt64() == smaller.Length
            && lines.Contains("Uploading combat log 1 of 2... 0%") && lines[^1] == "Uploading combat log 2 of 2... 100%");

        // Too large: by the portal's limit before anything is sent, or by its 413 at begin.
        var large = NewClient("large");
        File.WriteAllBytes(LogPath(large), LogBytes(15, 2000));
        portal = new FakeCombatLogPortal { PartSize = part };
        result = await Service(portal).UploadPendingAsync(large, session, offer with { MaxSize = 1000 });
        check("a log over the portal's limit is deleted unsent", result == new CombatLogUploadResult(0, 1, 0)
            && portal.Requests.Count == 0 && Left(large).Length == 0);
        File.WriteAllBytes(LogPath(large), LogBytes(16, 2000));
        portal.MaxSize = 1000;
        result = await Service(portal).UploadPendingAsync(large, session, offer);
        check("a log the portal calls too large (413) is deleted", result == new CombatLogUploadResult(0, 1, 0)
            && portal.Requests.SequenceEqual(["begin"]) && Left(large).Length == 0);

        // Stopping: the game starts or the launcher closes in the middle of an upload.
        var stopped = NewClient("stopped");
        var stoppedLog = LogBytes(17, 4000);
        File.WriteAllBytes(LogPath(stopped), stoppedLog);
        using (var cancel = new CancellationTokenSource())
        {
            portal = new FakeCombatLogPortal { PartSize = part };
            portal.OnRequest = name => { if (name == "part@" + 2 * part) cancel.Cancel(); };
            result = await Service(portal).UploadPendingAsync(stopped, session, offer, null, cancel.Token);
            check("a cancelled upload stops without an error and keeps the log", result == new CombatLogUploadResult(0, 0, 1)
                && Left(stopped).SequenceEqual([queued, queued + ".gz"]) && !portal.Requests.Contains("finish"));
            before = portal.Requests.Count;
            result = await Service(portal).UploadPendingAsync(stopped, session, offer, null, cancel.Token);
            check("nothing is sent once stopped", result == new CombatLogUploadResult(0, 0, 1) && portal.Requests.Count == before);
        }
        portal.OnRequest = null;
        result = await Service(portal).UploadPendingAsync(stopped, session, offer);
        check("the next start carries on and finishes it", result == new CombatLogUploadResult(1, 0, 0)
            && portal.Requests.Skip(before).Take(2).SequenceEqual(["begin", "part@" + 2 * part])
            && Gunzip(portal.Completed.Single()).AsSpan().SequenceEqual(stoppedLog) && Left(stopped).Length == 0);

        // The portal can't be reached, or refuses the login: quiet, and everything stays.
        var offline = NewClient("offline");
        File.WriteAllBytes(LogPath(offline), LogBytes(18, 500));
        portal = new FakeCombatLogPortal { PartSize = part, Offline = true };
        result = await Service(portal).UploadPendingAsync(offline, session, offer);
        check("an unreachable portal fails quietly and keeps the log", result == new CombatLogUploadResult(0, 0, 1)
            && Left(offline).SequenceEqual([queued, queued + ".gz"]));
        portal.Offline = false;
        result = await Service(portal).UploadPendingAsync(offline, session with { Token = "stale" }, offer);
        check("a refused login fails quietly and keeps the log", result == new CombatLogUploadResult(0, 0, 1)
            && portal.Unauthorized == 1 && Left(offline).Length == 2);
        result = await Service(portal).UploadPendingAsync(offline, session, offer);
        check("and it goes out with the next run", result == new CombatLogUploadResult(1, 0, 0) && Left(offline).Length == 0);
    }

    /// <summary>The portal's combatlog.php as the launcher sees it, with switches for what can go wrong.</summary>
    private sealed class FakeCombatLogPortal : HttpMessageHandler
    {
        public const string Token = "combat-log-test-token";
        public bool Offered = true;
        public bool Enabled = true;
        public int PartSize = 4 << 20;
        public long MaxSize = 512 << 20;
        /// <summary>No answer at all: every call fails like a dead connection.</summary>
        public bool Offline;
        /// <summary>This part call (counted from 0 over the portal's lifetime) answers 500, once.</summary>
        public int FailPartNumber = -1;
        /// <summary>begin answers "received: 0" although parts are stored, so the first part gets a 409.</summary>
        public bool BeginForgetsReceived;
        public bool AlwaysConflict;
        /// <summary>This many finish calls answer 422 and discard the upload.</summary>
        public int RejectFinishes;
        public Action<string>? OnRequest;

        public readonly List<string> Requests = [];
        public readonly List<JsonElement> Begins = [];
        public readonly List<byte[]> Completed = [];
        public int Unauthorized, WrongContentType, OversizedParts, Conflicts;

        private sealed class Upload
        {
            public string Id = "";
            public long Size;
            public string Sha256 = "";
            public MemoryStream Data = new();
            public bool Complete;
        }
        private readonly List<Upload> _uploads = [];
        private int _partCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("No route to host.");
            var uri = request.RequestUri!;
            if (!uri.AbsolutePath.EndsWith("/api/launcher/combatlog.php", StringComparison.Ordinal)) return Json(404, """{"error":"Not found."}""");
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
            {
                Unauthorized++;
                return Json(401, """{"error":"Log in again."}""");
            }
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var upload = _uploads.FirstOrDefault(u => u.Id == query["upload"]);
            switch (request.Method.Method + " " + query["action"])
            {
                case "GET ":
                    Requests.Add("offer");
                    return Offered
                        ? Json(200, $$"""{"enabled":{{(Enabled ? "true" : "false")}},"part_size":{{PartSize}},"max_size":{{MaxSize}}}""")
                        : Json(404, """{"error":"This realm does not collect combat logs."}""");

                case "POST begin":
                    Requests.Add("begin");
                    OnRequest?.Invoke("begin");
                    var begin = JsonDocument.Parse(body).RootElement.Clone();
                    Begins.Add(begin);
                    if (begin.GetProperty("size").GetInt64() > MaxSize) return Json(413, """{"error":"That log is too large."}""");
                    var sha256 = begin.GetProperty("sha256").GetString()!;
                    upload = _uploads.FirstOrDefault(u => u.Sha256 == sha256);
                    if (upload is { Complete: true }) return Json(200, $$"""{"upload":"{{upload.Id}}","complete":true}""");
                    if (upload is null)
                        _uploads.Add(upload = new Upload { Id = Guid.NewGuid().ToString("N"), Size = begin.GetProperty("size").GetInt64(), Sha256 = sha256 });
                    return Json(200, $$"""{"upload":"{{upload.Id}}","received":{{(BeginForgetsReceived ? 0 : upload.Data.Length)}},"part_size":{{PartSize}}}""");

                case "POST part":
                    var name = "part@" + query["offset"];
                    Requests.Add(name);
                    OnRequest?.Invoke(name);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (upload is null || upload.Complete) return Json(404, """{"error":"No such upload."}""");
                    if (request.Content?.Headers.ContentType?.MediaType != "application/octet-stream") WrongContentType++;
                    if (body.Length > PartSize) { OversizedParts++; return Json(400, """{"error":"That part is too large."}"""); }
                    if (_partCalls++ == FailPartNumber) return Json(500, """{"error":"The portal fell over."}""");
                    if (AlwaysConflict || long.Parse(query["offset"]!, CultureInfo.InvariantCulture) != upload.Data.Length)
                    {
                        Conflicts++;
                        return Json(409, $$"""{"error":"That is not where this upload is.","received":{{upload.Data.Length}}}""");
                    }
                    upload.Data.Write(body);
                    return Json(200, $$"""{"received":{{upload.Data.Length}}}""");

                case "POST finish":
                    Requests.Add("finish");
                    OnRequest?.Invoke("finish");
                    if (upload is null) return Json(404, """{"error":"No such upload."}""");
                    var data = upload.Data.ToArray();
                    if (RejectFinishes-- > 0 || data.Length != upload.Size || Convert.ToHexStringLower(SHA256.HashData(data)) != upload.Sha256)
                    {
                        _uploads.Remove(upload);
                        return Json(422, """{"error":"The upload does not match its size or checksum."}""");
                    }
                    upload.Complete = true;
                    Completed.Add(data);
                    return Json(200, """{"complete":true}""");

                default:
                    return Json(400, """{"error":"Unknown action."}""");
            }
        }

        private static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
