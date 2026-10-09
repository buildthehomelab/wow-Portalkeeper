using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Portalkeeper.Services;

public sealed record ClientImportResult(int Addons, int SettingsFiles, int Failed);

/// <summary>
/// Brings a player's own addons and game settings from the client they used before into a freshly
/// installed one (INSTALL WOW downloads into a new folder, so they'd otherwise stay behind).
///
/// Copy only, never overwrite: the old client is left as it was, and anything the new client
/// already has wins. Copied are Interface/AddOns folders (not Blizzard_*: the 3.3.5 client renames
/// those) and the files under WTF/Account (SavedVariables, macros, key bindings, chat layout).
/// Config.wtf isn't: it holds the old client's graphics settings and realm, and the launcher writes
/// what it needs at launch.
///
/// Addons the realm offers (<c>realmAddonFolders</c>) aren't copied either, nor addons that need
/// one of them (## Dependencies): the launcher installs the realm's own version, and an Optional one
/// is the player's choice in its Addons list. A base client's bundled copy (TheraWoW's DragonUI with
/// its NewEra panels) would otherwise come along unasked.
/// </summary>
public static class ClientImportService
{
    public static ClientImportResult Import(string sourceClient, string targetClient,
        IEnumerable<string>? realmAddonFolders = null)
    {
        if (string.IsNullOrWhiteSpace(sourceClient) || !Directory.Exists(sourceClient))
            return new(0, 0, 0);
        var source = Path.GetFullPath(sourceClient).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(targetClient).TrimEnd(Path.DirectorySeparatorChar);
        if (IsSameOrInside(source, target) || IsSameOrInside(target, source))
            return new(0, 0, 0);

        int addons = 0, failed = 0;
        var sourceAddOns = Path.Combine(source, "Interface", "AddOns");
        if (Directory.Exists(sourceAddOns))
        {
            var targetAddOns = ManagedPath.Resolve(target, "Interface/AddOns");
            var candidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.EnumerateDirectories(sourceAddOns, "*", NoLinks(false)))
            {
                var name = Path.GetFileName(folder);
                var tocs = Directory.EnumerateFiles(folder, "*.toc", NoLinks(false)).ToArray();
                if (!name.StartsWith("Blizzard", StringComparison.OrdinalIgnoreCase) && tocs.Length > 0)
                    candidates[name] = tocs.SelectMany(Dependencies).ToArray();
            }
            var skipped = new HashSet<string>(realmAddonFolders ?? [], StringComparer.OrdinalIgnoreCase);
            for (var more = true; more;)
            {
                more = false;
                foreach (var (name, dependencies) in candidates)
                    if (!skipped.Contains(name) && dependencies.Any(skipped.Contains))
                        more = skipped.Add(name);
            }
            foreach (var name in candidates.Keys)
            {
                var folder = Path.Combine(sourceAddOns, name);
                if (skipped.Contains(name)) continue;
                var destination = Path.Combine(targetAddOns, name);
                if (Directory.Exists(destination) || File.Exists(destination)) continue;
                try
                {
                    CopyDirectory(folder, destination);
                    addons++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
        }

        var settings = 0;
        var sourceAccounts = Path.Combine(source, "WTF", "Account");
        if (Directory.Exists(sourceAccounts))
        {
            var targetAccounts = ManagedPath.Resolve(target, "WTF/Account");
            foreach (var file in Directory.EnumerateFiles(sourceAccounts, "*", NoLinks(true)))
            {
                var destination = Path.Combine(targetAccounts, Path.GetRelativePath(sourceAccounts, file));
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, false);
                    settings++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
        }
        return new(addons, settings, failed);
    }

    /// <summary>Addons a .toc can't load without (## Dependencies, ## RequiredDeps, ## Dep...).</summary>
    private static IEnumerable<string> Dependencies(string toc)
    {
        foreach (var line in File.ReadLines(toc).Take(64))
        {
            var match = Regex.Match(line, @"^##\s*(?:Dependencies|RequiredDeps|Dep\w*)\s*:(.*)$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            foreach (var name in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return name;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        // Into a temporary folder first, so a failed copy never leaves half an addon behind.
        var temporary = destination + ".portalkeeper-import";
        if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", NoLinks(true)))
            {
                var copy = Path.Combine(temporary, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, false);
            }
            Directory.CreateDirectory(temporary);
            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    // Links (and junctions) are skipped, never followed out of the old client.
    private static EnumerationOptions NoLinks(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
