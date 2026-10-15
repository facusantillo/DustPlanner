using System.Reflection;
using System.Text.Json;
using DustPlanner.Core.Json;

namespace DustPlanner.Core.Rules;

/// <summary>
/// Loads the rule files from <c>/data</c>: embedded defaults, optionally overridden by files in a user directory
/// (so a rotation or a confirmed premium mapping can be fixed without rebuilding).
/// </summary>
public static class GameDataFiles
{
    public const string FormatRotationFile = "format-rotation.json";
    public const string DustRulesFile = "dust-rules.json";
    public const string PremiumSlotsFile = "premium-slots.json";
    public const string NonCraftableFile = "non-craftable.json";

    public static string ReadText(string fileName, string? overrideDirectory = null)
    {
        if (overrideDirectory is not null)
        {
            var path = Path.Combine(overrideDirectory, fileName);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        var resource = "DustPlanner.Core.Data." + fileName;
        using var stream = typeof(GameDataFiles).Assembly.GetManifestResourceStream(resource)
                           ?? throw new FileNotFoundException("Embedded rule file not found: " + resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static T Read<T>(string fileName, string? overrideDirectory = null) =>
        JsonSerializer.Deserialize<T>(ReadText(fileName, overrideDirectory), CoreJson.Options)
        ?? throw new InvalidDataException("Rule file is empty: " + fileName);

    /// <summary>Convenience: load everything with one call.</summary>
    public static GameRules LoadRules(string? overrideDirectory = null) => new(
        FormatRotation.Load(overrideDirectory),
        DustRules.Load(overrideDirectory),
        PremiumSlotMap.Load(overrideDirectory),
        Craftability.Load(overrideDirectory));
}

/// <summary>All rule tables the planner needs, loaded together.</summary>
public sealed record GameRules(FormatRotation Rotation, DustRules Dust, PremiumSlotMap PremiumSlots, Craftability Craftability);
