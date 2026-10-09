using System;
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

    /// <summary>Patches served from here have a torrent at the launcher API (patch-torrent.php).</summary>
    public const string PatchBaseUrl = "https://wow.vaultrona.com/realm/";

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

    /// <summary>The server-side file name of a patch hosted in the realm folder, or null for other sources.</summary>
    public static string? HostedPatchFileName(string sourceUrl)
    {
        if (!sourceUrl.StartsWith(PatchBaseUrl, StringComparison.OrdinalIgnoreCase)) return null;
        var name = Uri.UnescapeDataString(sourceUrl[PatchBaseUrl.Length..]);
        return name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && name != "." && name != ".." ? name : null;
    }
}
