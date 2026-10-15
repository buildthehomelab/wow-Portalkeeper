using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

/// <summary>
/// The realm this build of Portalkeeper is made for. Its realm.conf is embedded in the executable and
/// copied into the realm store on first run, so players never have to find or place a realm file. The
/// file's ConfigURL keeps it current after that.
/// </summary>
public static class RealmBranding
{
    public const string RealmFileName = "azeroth.realm.conf";
    private const string ResourceName = "Portalkeeper.Branding." + RealmFileName;

    /// <summary>
    /// The portal's launcher API: login, client torrent and patch torrents. PORTALKEEPER_LAUNCHER_API
    /// points a development build at a test portal instead.
    /// </summary>
    public static readonly Uri LauncherApi = new(
        Environment.GetEnvironmentVariable("PORTALKEEPER_LAUNCHER_API") is { Length: > 0 } test
            ? test.TrimEnd('/') + "/"
            : "https://wow.vaultrona.com/api/launcher/");

    /// <summary>The project's name, shown as the launcher's title and logo.</summary>
    public const string LauncherName = "Evermore";

    /// <summary>The realm's patch notes page.</summary>
    public const string PatchNotesUrl = "https://wow.vaultrona.com/changelog.php";

    /// <summary>Where players register an account.</summary>
    public const string AccountSignupUrl = "https://wow.vaultrona.com/";

    /// <summary>The realm's public folder (realm.conf, cache-version.txt, the armory).</summary>
    public const string RealmBaseUrl = "https://wow.vaultrona.com/realm/";

    /// <summary>Patches served from here have a torrent at the launcher API (patch-torrent.php).</summary>
    public const string PatchBaseUrl = RealmBaseUrl + "patches/";

    /// <summary>
    /// Any short text (a date, a counter). When it changes, every launcher clears its client's Cache
    /// folder before the next launch: the server bumps it after changes the client caches, such as
    /// items, spells or quests. Missing file: nothing happens.
    /// </summary>
    public const string CacheVersionUrl = RealmBaseUrl + "cache-version.txt";

    /// <summary>The built-in realm's realm.conf URL (its ConfigURL), which identifies it among realms.</summary>
    public const string ConfigUrl = RealmBaseUrl + RealmFileName;

    public static bool IsBuiltIn(Portalkeeper.Models.RealmInfo? realm) =>
        realm is not null && string.Equals(realm.ConfigUrl, ConfigUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Patches the realm shipped before launchers kept a ledger of what they installed (0.5.6). Once
    /// realm.conf stops listing one, RetiredPatchService takes the file out of any client where it is
    /// still byte-for-byte ours and restores the client file it replaced. patch-C, patch-I and patch-P
    /// had the names of MPQs in the Evermore base client (Project Reforged HD), and Patch-F/Patch-G
    /// were the old HD creature pack Reforged replaces; they became patch-K, patch-R and patch-W.
    /// </summary>
    public static readonly IReadOnlyList<RetiredPatchService.Entry> RetiredPatches =
    [
        new() { Path = "Data/patch-C.MPQ", Size = 8396, Sha256 = "ad80017ca14b85d786f50035f808ab8939d8131a2463e9f4ca62cec07291227d" },
        new() { Path = "Data/patch-P.MPQ", Size = 8688441, Sha256 = "5941b1380570b6c9a0e27e6dc119d054a7bf595f4fa6e9c933c4fd3b8ae9fbc9" },
        new() { Path = "Data/patch-I.MPQ", Size = 228262988, Sha256 = "095d2b8325b58790b0ddfdc8241a7dfefd2c5de949c116733cc00472b44186c2" },
        new() { Path = "Data/Patch-F.MPQ", Size = 824714812, Sha256 = "03fba26b08cc35a62e8e0e06f429e450840ef0988c17d4704cec8e2dbeda8925" },
        new() { Path = "Data/Patch-G.MPQ", Size = 10047001, Sha256 = "98b74d8051e0b14974f31f6aa09ef01c72bf78dd504866150d307be0e3f46bc1" },
    ];

    /// <summary>
    /// The program files of the realm's client (the Evermore client torrent: TheraWoW's client, Wow.exe
    /// with the LongPatchNameService edit). They're pinned here rather than in realm.conf or the
    /// torrent, which both come from the portal: a client torrent listing any other program file in
    /// the client's top folder is refused, and the game isn't started when one of these doesn't match.
    /// Changing one of them on the server needs a launcher release with its new hash first.
    /// </summary>
    public static readonly IReadOnlyList<ClientBinary> ClientBinaries =
    [
        // Stock TheraWoW 3.3.5a (12340), and the same file after LongPatchNameService.Ensure.
        new("Wow.exe", 7704216, "c6f9209840a8a5941538bcda0e63c4bf445a154b6c1c9c7397be31cebabc289c"),
        new("Wow.exe", 7704216, "50b8931f4d3a8558752b3718447253bc92b4f488cd6c4803809d2f82ecd14441"),
        new("WowError.exe", 350360, "964eb9ed647c4614f3a3ecf977955750223ecbc8ff0a3bb4078e4dff779244f8"),
        new("Repair.exe", 975512, "ae98037ec2f858f0f9a6c69b3267e5268f20dbf08f324aa4453d2252c1b31d52"),
        new("Battle.net.dll", 15588224, "88116951b04723cbb1073a1a0ff72dfdc40367f6649dffd08c602caa3ef1141f"),
        new("DivxDecoder.dll", 413696, "ed34d37b575c91a56704218eb9f6abbefda8b7de0e2ed44c96191abd0f9915a5"),
        new("Scan.dll", 47876, "0ca274f1c6f38be6864a8ef18a5601d3139c9336e1de29328c50abc07bc74807"),
        new("d3d9.dll", 4493326, "00ecd422b3b12e9d3b309785f3f19431405389d80eb4a37018ebc63e2d87d900"),
        new("dbghelp.dll", 1039728, "3f9ad6986eb1f3a09eb0b5cad888ba656f9148b3339399076f2541b3bc59213c"),
        new("dinput8.dll", 785408, "2ddeb75d64c39da53efbd3980d27ae0ffff40104eda52580afc0be490ad25bd0"),
        new("ijl15.dll", 372736, "334aa12f7dee453d1c6cb1b661a3bb3494d3e4cc9c2ff3f9002064c78404e43a"),
        new("msvcr80.dll", 632656, "9382aaed2db19cd75a70e38964f06c63f19f63c9dfb5a33b0c2d445bb41b6e46"),
        new("skia.dll", 2833920, "ef23addd3f27c748f5ef07608da2c71c3551454debdcb8d4d40141b6e9a057d4"),
        new("unicows.dll", 245408, "22f23cc65698741184ec34f46e6f69717644e0b5aabf5d5bd015101f2d72e56e"),
    ];

    /// <summary>
    /// The realm patch whose AccountLogin.lua signs in with the launcher login (wow-server
    /// tools/client-patches/login). The game password is only handed to clients that install it.
    /// It is known by its realm.conf section, [Patch.LauncherLogin], whatever its file is called; the
    /// file name is how launchers before 0.5.12 knew it, and still counts.
    /// </summary>
    public const string LoginPatchId = "LauncherLogin";
    public const string LoginPatchFileName = "patch-L.MPQ";

    public static bool IsLoginPatch(PatchDefinition patch) => patch.InstallMode == PatchInstallMode.File
        && (string.Equals(patch.Id, LoginPatchId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(patch.FileName, LoginPatchFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Client files the realm's client torrent used to carry and doesn't want any more. Removed while
    /// still byte-for-byte the shipped file and no longer in the client torrent; never restored.
    /// patch-enUS-4.MPQ is the TheraWoW base client's login screen (its logo, video, music and links),
    /// which replaces the stock Wrath login screen and hides its cinematics button.
    /// </summary>
    public static readonly IReadOnlyList<RetiredPatchService.Entry> RemovedClientFiles =
    [
        new() { Path = "Data/enUS/patch-enUS-4.MPQ", Size = 413557768, Sha256 = "76c1438f276ed92ab5412ff66ef8997043f1b63d34dfe7f01f4a7bd86456b758" },
    ];

    /// <summary>
    /// Writes the embedded realm.conf into the store when the store holds no realm yet. Returns true
    /// when it wrote the file. A realm the player already has (including a newer refreshed copy of this
    /// one) is never touched.
    /// </summary>
    public static bool SeedRealmStore(string? directory = null)
    {
        directory ??= RealmConfigurationStore.DefaultDirectory;
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.realm.conf")
                .Any(p => !Path.GetFileName(p).Equals("example.realm.conf", StringComparison.OrdinalIgnoreCase)))
            return false;

        using var resource = typeof(RealmBranding).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The built-in realm configuration is missing from this build.");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, RealmFileName);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);
            File.Move(temp, target, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return true;
    }

    /// <summary>The server-side file name of a patch hosted in the realm's patches folder, or null for other sources.</summary>
    public static string? HostedPatchFileName(string sourceUrl)
    {
        if (!sourceUrl.StartsWith(PatchBaseUrl, StringComparison.OrdinalIgnoreCase)) return null;
        var name = Uri.UnescapeDataString(sourceUrl[PatchBaseUrl.Length..]);
        return name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && name != "." && name != ".." ? name : null;
    }
}
