using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Portalkeeper.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        if (settingsPath is not null)
        {
            _settingsPath = settingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            return;
        }
        var applicationData =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

        var portalkeeperDirectory =
            Path.Combine(applicationData, "Portalkeeper");

        Directory.CreateDirectory(portalkeeperDirectory);

        _settingsPath =
            Path.Combine(portalkeeperDirectory, "settings.json");
    }

    public PortalkeeperSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PortalkeeperSettings();
            }

            var json = File.ReadAllText(_settingsPath);

            var settings = JsonSerializer.Deserialize<PortalkeeperSettings>(json) ?? new PortalkeeperSettings();
            settings.ShowTransmogrifiedAppearancesByRealm ??= new();
            return settings;
        }
        catch
        {
            return new PortalkeeperSettings();
        }
    }

    public void Save(PortalkeeperSettings settings)
    {
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(_settingsPath, json);
    }
}

public sealed class PortalkeeperSettings
{
    public Dictionary<string, bool> ShowTransmogrifiedAppearancesByRealm { get; set; } = new();
    public bool ShowTransmogFor(string realmKey) =>
        !ShowTransmogrifiedAppearancesByRealm.TryGetValue(realmKey, out var value) || value;
    public string? SelectedRealmPath { get; set; }
    public DateTimeOffset? LastPortalkeeperUpdateCheckUtc { get; set; }
    public string? LatestPortalkeeperReleaseTag { get; set; }
    public string ClientPath { get; set; } = string.Empty;
    public bool HidePortalkeeperWhileGameRuns { get; set; } = true;
    // Sharing downloads with other players (TorrentService).
    public bool ShareDownloads { get; set; } = true;
    public int ShareUploadLimitKiB { get; set; } = 2048;
    public int TorrentPort { get; set; }
    // A client install that was paused or interrupted, resumed by INSTALL WOW.
    public string? PendingClientInstallPath { get; set; }
    // Installed Windows builds download and run new releases by themselves.
    public bool AutoUpdatePortalkeeper { get; set; } = true;
    // The last self-update started: if Portalkeeper comes back still on the old version, the install
    // failed, so automatic retries of that version wait a while (UPDATE NOW always tries).
    public string? LastSelfUpdateVersion { get; set; }
    public DateTimeOffset? LastSelfUpdateUtc { get; set; }
}