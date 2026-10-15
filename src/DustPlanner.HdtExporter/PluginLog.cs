using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace DustPlanner.HdtExporter;

/// <summary>
/// Writes to HDT's log when available and always to a small rolling file of our own, so a problem can be
/// diagnosed even when HDT's logging is off. Never throws.
/// </summary>
internal static class PluginLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new object();
    private static string? _filePath;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, "exporter.log");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _filePath = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, HdtLogInfo);

    public static void Warn(string message) => Write("WARN", message, HdtLogWarn);

    public static void Error(string message) => Write("ERROR", message, HdtLogError);

    public static void Error(string message, Exception ex) => Error(message + Environment.NewLine + ex);

    private static void Write(string level, string message, Action<string> hdtSink)
    {
        try
        {
            hdtSink(message);
        }
        catch (Exception)
        {
            // HDT's logger is a convenience, not a requirement.
        }

        var path = _filePath;
        if (path is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path,
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + " [" + level + "] " + message + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Logging must never break the plugin.
            }
        }
    }

    // Each HDT call lives in its own non-inlined method: if the Log type is missing at runtime the
    // TypeLoadException surfaces here and is swallowed by the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HdtLogInfo(string message) => Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[DustPlanner] " + message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HdtLogWarn(string message) => Hearthstone_Deck_Tracker.Utility.Logging.Log.Warn("[DustPlanner] " + message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HdtLogError(string message) => Hearthstone_Deck_Tracker.Utility.Logging.Log.Error("[DustPlanner] " + message);
}
