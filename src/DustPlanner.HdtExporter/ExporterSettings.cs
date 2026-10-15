using System;
using System.IO;
using Newtonsoft.Json;

namespace DustPlanner.HdtExporter;

/// <summary>User-editable settings, stored as <c>%LocalAppData%\DustPlanner\exporter-settings.json</c>.</summary>
public sealed class ExporterSettings
{
    public const string FileName = "exporter-settings.json";

    /// <summary>Folder that receives <c>inbox/&lt;account_key&gt;/*.json</c>. Defaults to <c>%LocalAppData%\DustPlanner</c>.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Timestamped copies to keep per export kind and account.</summary>
    public int HistoryKeep { get; set; } = 30;

    /// <summary>Minimum seconds between two automatic exports of the same kind.</summary>
    public int MinIntervalSeconds { get; set; } = 30;

    /// <summary>Also export decks and rank when the game returns to the main menu / play screen.</summary>
    public bool ExportOnModeChange { get; set; } = true;

    /// <summary>Also export HDT's own saved decks (PlayerDecks.xml) alongside the in-game ones.</summary>
    public bool IncludeHdtDecks { get; set; } = true;

    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DustPlanner");

    public string ResolvedRootDirectory =>
        string.IsNullOrWhiteSpace(OutputDirectory) ? DefaultRootDirectory : Environment.ExpandEnvironmentVariables(OutputDirectory!);

    public string InboxDirectory => Path.Combine(ResolvedRootDirectory, "inbox");

    public static ExporterSettings LoadOrCreate(string rootDirectory)
    {
        var path = Path.Combine(rootDirectory, FileName);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonConvert.DeserializeObject<ExporterSettings>(File.ReadAllText(path), JsonConventions.Settings);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            PluginLog.Warn("Could not read " + path + "; using defaults. " + ex.Message);
        }

        var settings = new ExporterSettings();
        try
        {
            Directory.CreateDirectory(rootDirectory);
            File.WriteAllText(path, JsonConvert.SerializeObject(settings, JsonConventions.Settings));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            PluginLog.Warn("Could not write default settings to " + path + ". " + ex.Message);
        }

        return settings;
    }
}
