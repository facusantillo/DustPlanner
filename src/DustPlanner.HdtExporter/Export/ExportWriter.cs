using System;
using System.IO;
using System.Text;
using DustPlanner.Contracts;
using DustPlanner.HdtExporter.Logic;
using Newtonsoft.Json;

namespace DustPlanner.HdtExporter.Export;

/// <summary>Writes export files atomically (temp file + replace) and keeps a bounded timestamped history.</summary>
internal sealed class ExportWriter
{
    private readonly string _inboxDirectory;
    private readonly int _historyKeep;

    public ExportWriter(string inboxDirectory, int historyKeep)
    {
        _inboxDirectory = inboxDirectory;
        _historyKeep = historyKeep;
    }

    public string AccountDirectory(string accountKey) => Path.Combine(_inboxDirectory, accountKey);

    /// <summary>Writes <c>{kind}.json</c> (latest) and <c>history/{kind}-{stamp}.json</c>; returns the latest path.</summary>
    public string Write<T>(T payload, string kind, string accountKey, DateTimeOffset exportedAt, bool keepHistory)
    {
        var dir = AccountDirectory(accountKey);
        Directory.CreateDirectory(dir);
        var json = JsonConvert.SerializeObject(payload, JsonConventions.Settings);

        var latest = Path.Combine(dir, kind + ".json");
        WriteAtomic(latest, json);

        if (keepHistory && _historyKeep > 0)
        {
            var historyDir = Path.Combine(dir, ExportSchema.HistoryFolderName);
            Directory.CreateDirectory(historyDir);
            WriteAtomic(Path.Combine(historyDir, HistoryRetention.HistoryFileName(kind, exportedAt)), json);
            Prune(historyDir, kind);
        }

        return latest;
    }

    private void Prune(string historyDir, string kind)
    {
        string[] names;
        try
        {
            names = Directory.GetFiles(historyDir, kind + "-*.json");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            PluginLog.Warn("Could not list history in " + historyDir + ": " + ex.Message);
            return;
        }

        for (var i = 0; i < names.Length; i++)
        {
            names[i] = Path.GetFileName(names[i]);
        }

        foreach (var name in HistoryRetention.FilesToPrune(names, kind, _historyKeep))
        {
            try
            {
                File.Delete(Path.Combine(historyDir, name));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("Could not prune " + name + ": " + ex.Message);
            }
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
