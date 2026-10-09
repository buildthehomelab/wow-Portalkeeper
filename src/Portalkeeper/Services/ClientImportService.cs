using System;
using System.IO;
using System.Linq;

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
/// </summary>
public static class ClientImportService
{
    public static ClientImportResult Import(string sourceClient, string targetClient)
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
            foreach (var folder in Directory.EnumerateDirectories(sourceAddOns, "*", NoLinks(false)))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith("Blizzard", StringComparison.OrdinalIgnoreCase)
                    || !Directory.EnumerateFiles(folder, "*.toc", NoLinks(false)).Any())
                    continue;
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
