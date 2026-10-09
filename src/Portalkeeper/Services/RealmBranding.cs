using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
