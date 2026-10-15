using System.Text;

namespace DustPlanner.LogTracker;

/// <summary>
/// Makes sure Hearthstone writes Power.log: <c>%LocalAppData%\Blizzard\Hearthstone\log.config</c> needs a
/// <c>[Power]</c> section with file printing on. Only that section is touched; every other section (HDT writes
/// several) is preserved byte for byte.
/// </summary>
public static class LogConfig
{
    public const string SectionName = "Power";

    public static readonly IReadOnlyDictionary<string, string> RequiredPowerSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LogLevel"] = "1",
        ["FilePrinting"] = "true",
        ["ConsolePrinting"] = "false",
        ["ScreenPrinting"] = "false",
        ["Verbose"] = "true",
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blizzard", "Hearthstone", "log.config");

    /// <summary>True when the file already has a [Power] section with every required setting.</summary>
    public static bool IsConfigured(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var section = ReadSection(content, SectionName);
        return section is not null && RequiredPowerSettings.All(kv => section.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the content with the [Power] section added or completed. Other sections and comments are untouched.</summary>
    public static string Apply(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (IsConfigured(content))
        {
            return content;
        }

        var lines = string.IsNullOrWhiteSpace(content)
            ? new List<string>()
            : content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var start = lines.FindIndex(l => l.Trim().Equals("[" + SectionName + "]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[" + SectionName + "]");
            foreach (var (key, value) in RequiredPowerSettings)
            {
                lines.Add(key + "=" + value);
            }

            return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        }

        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('['))
        {
            end++;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = start + 1; i < end; i++)
        {
            var eq = lines[i].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = lines[i][..eq].Trim();
            if (RequiredPowerSettings.TryGetValue(key, out var required))
            {
                lines[i] = key + "=" + required;
                seen.Add(key);
            }
        }

        var insertAt = end;
        foreach (var (key, value) in RequiredPowerSettings)
        {
            if (!seen.Contains(key))
            {
                lines.Insert(insertAt++, key + "=" + value);
            }
        }

        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    /// <summary>Applies to the file on disk; creates it when missing. Returns true when something was written.</summary>
    public static bool EnsureFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var updated = Apply(existing);
        if (updated == existing)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated, new UTF8Encoding(false));
        return true;
    }

    private static Dictionary<string, string>? ReadSection(string content, string name)
    {
        Dictionary<string, string>? current = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (current is not null)
                {
                    return current;
                }

                current = line.Equals("[" + name + "]", StringComparison.OrdinalIgnoreCase) ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (current is not null && eq > 0)
            {
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return current;
    }
}
