using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DustPlanner.LogTracker;

/// <summary>A Power.log file with the date its first line belongs to.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="BaseDate">Date of the first line (from the session folder name, else the file dates).</param>
/// <param name="LastWriteUtc">Last write time of the file.</param>
public sealed record PowerLogFile(string Path, DateOnly BaseDate, DateTimeOffset LastWriteUtc)
{
    /// <summary>Folder the file lives in: one per client launch for the current game (<c>Hearthstone_&lt;date&gt;</c>).</summary>
    public string Session => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>
    /// True for <c>Power_old.log</c>: Hearthstone Deck Tracker renames <c>Power.log</c> to it whenever its own log
    /// watcher starts (HDT start, game relaunch), and the game then writes a fresh <c>Power.log</c>. The rotated file
    /// holds the earlier part of the same session.
    /// </summary>
    public bool Rotated => string.Equals(System.IO.Path.GetFileName(Path), LogLocator.PowerLogOldName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds Hearthstone's log folder and the Power.log files in it (current client writes one dated session folder per launch).</summary>
public static partial class LogLocator
{
    public const string LogsFolderName = "Logs";
    public const string PowerLogName = "Power.log";

    /// <summary>What HDT renames <see cref="PowerLogName"/> to when its log watcher starts (see <see cref="PowerLogFile.Rotated"/>).</summary>
    public const string PowerLogOldName = "Power_old.log";

    /// <summary>File names read per session, in content order: the rotated file first.</summary>
    private static readonly string[] FileNamesInOrder = [PowerLogOldName, PowerLogName];

    /// <summary>Candidate Hearthstone install folders: HDT's config (it already found the game), then the default paths.</summary>
    public static IEnumerable<string> CandidateGameDirectories()
    {
        var hdtConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "config.xml");
        var fromHdt = ReadHdtHearthstoneDirectory(hdtConfig);
        if (fromHdt is not null)
        {
            yield return fromHdt;
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Hearthstone");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "Hearthstone");
        }

        yield return @"C:\Program Files (x86)\Hearthstone";
    }

    /// <summary>Reads <c>HearthstoneDirectory</c> from HDT's config.xml, when present.</summary>
    public static string? ReadHdtHearthstoneDirectory(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            var value = XDocument.Load(configPath).Root?.Element("HearthstoneDirectory")?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex) when (ex is IOException || ex is System.Xml.XmlException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FindLogsDirectory(IEnumerable<string>? gameDirectories = null) =>
        (gameDirectories ?? CandidateGameDirectories())
            .Select(d => Path.Combine(d, LogsFolderName))
            .FirstOrDefault(Directory.Exists);

    /// <summary>
    /// Every Power.log under the logs folder, oldest first: dated session folders (<c>Hearthstone_2026_10_15_20_11_03</c>)
    /// and the legacy flat file. Within a session the rotated <c>Power_old.log</c> (HDT's rename) comes before
    /// <c>Power.log</c>, because it holds the earlier lines.
    /// </summary>
    public static IReadOnlyList<PowerLogFile> FindPowerLogs(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        if (!Directory.Exists(logsDirectory))
        {
            return Array.Empty<PowerLogFile>();
        }

        var files = new List<(PowerLogFile File, string SessionKey)>();
        foreach (var dir in Directory.GetDirectories(logsDirectory))
        {
            var sessionName = Path.GetFileName(dir);
            foreach (var name in FileNamesInOrder)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    files.Add((new PowerLogFile(path, BaseDateFor(path, sessionName), File.GetLastWriteTimeUtc(path)), sessionName));
                }
            }
        }

        foreach (var name in FileNamesInOrder)
        {
            var flat = Path.Combine(logsDirectory, name);
            if (File.Exists(flat))
            {
                files.Add((new PowerLogFile(flat, BaseDateFor(flat, null), File.GetLastWriteTimeUtc(flat)), string.Empty));
            }
        }

        // Session folder names sort chronologically (Hearthstone_YYYY_MM_DD_HH_MM_SS); the legacy flat file has no session.
        return files
            .OrderBy(f => f.File.BaseDate)
            .ThenBy(f => f.SessionKey, StringComparer.Ordinal)
            .ThenByDescending(f => f.File.Rotated)
            .ThenBy(f => f.File.LastWriteUtc)
            .Select(f => f.File)
            .ToList();
    }

    /// <summary>Date of the first line: from the session folder name when it has one, else the file's creation time (local).</summary>
    public static DateOnly BaseDateFor(string path, string? sessionFolderName)
    {
        if (sessionFolderName is not null)
        {
            var match = SessionFolderRegex().Match(sessionFolderName);
            if (match.Success)
            {
                return new DateOnly(
                    int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture));
            }
        }

        var created = File.GetCreationTime(path);
        var written = File.GetLastWriteTime(path);
        return DateOnly.FromDateTime(created <= written ? created : written);
    }

    [GeneratedRegex(@"^Hearthstone_(?<y>\d{4})_(?<m>\d{2})_(?<d>\d{2})_\d{2}_\d{2}_\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionFolderRegex();
}
