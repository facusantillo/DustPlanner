using System.Text.Json;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Json;

namespace DustPlanner.Core.Planner;

public enum GoldenPolicy
{
    /// <summary>Keep the premium copy; a regular copy of a card in use becomes "redundant" when a premium copy covers it (default).</summary>
    KeepGolden,

    /// <summary>Keep the regular copy and turn premium copies into dust.</summary>
    DustGolden,
}

/// <summary>Pain weights (0–100 per copy). Lower pain = better candidate. All configurable in prefs.json.</summary>
public sealed class PainWeights
{
    public int Surplus { get; set; }

    public int Refund { get; set; }

    public int Redundant { get; set; } = 1;

    public int PremiumUnusedWild { get; set; } = 3;

    public int PremiumUnusedStandard { get; set; } = 10;

    public int WildOnly { get; set; } = 2;

    public int WildOnlyLegendaryBonus { get; set; } = 3;

    public int StandardNoSignal { get; set; } = 25;

    /// <summary>Added pain = weight × meta share for Standard cards below the protection threshold.</summary>
    public int StandardSignalWeight { get; set; } = 50;

    public int ClassNotPlayedWild { get; set; } = 5;

    public int ClassNotPlayedStandard { get; set; } = 12;

    public int RotatingDiscount { get; set; } = 3;

    public int Blacklisted { get; set; }
}

public sealed class MetaProtection
{
    /// <summary>Protect Standard legendaries that appear in at least this share of meta decks.</summary>
    public double LegendaryShare { get; set; } = 0.10;

    /// <summary>Protect Standard epics that appear in at least this share of meta decks.</summary>
    public double EpicShare { get; set; } = 0.20;

    /// <summary>Also protect a legendary/epic that appears in any meta deck of a class you play.</summary>
    public bool ProtectPlayedClassDecks { get; set; } = true;
}

public sealed class OptimizerSettings
{
    /// <summary>Penalty per dust point above the target (keeps plans tight).</summary>
    public double SurplusPenaltyPerDust { get; set; } = 0.01;

    /// <summary>Penalty per copy in the plan (prefers fewer cards at equal pain).</summary>
    public double PerCopyPenalty { get; set; } = 0.1;

    public int Alternatives { get; set; } = 2;
}

/// <summary>User preferences (<c>prefs.json</c>). Defaults: Standard, Priest, no Wild, keep golden.</summary>
public sealed class UserPrefs
{
    public const string FileName = "prefs.json";

    /// <summary><c>standard</c> or <c>wild</c>: the format you build decks for.</summary>
    public string Format { get; set; } = "standard";

    /// <summary>Classes you play (HearthstoneJSON names, e.g. <c>PRIEST</c>).</summary>
    public List<string> ClassesPlayed { get; set; } = new() { "PRIEST" };

    public bool PlaysWild { get; set; }

    public GoldenPolicy GoldenPolicy { get; set; } = GoldenPolicy.KeepGolden;

    public MetaProtection MetaProtection { get; set; } = new();

    public PainWeights Pain { get; set; } = new();

    public OptimizerSettings Optimizer { get; set; } = new();

    /// <summary>dbfIds never proposed.</summary>
    public List<int> Whitelist { get; set; } = new();

    /// <summary>dbfIds you are happy to dust (pain 0) — deck protection still applies.</summary>
    public List<int> Blacklist { get; set; } = new();

    /// <summary>Saved decks to ignore for protection, as <c>origin:external_id</c> (e.g. <c>hdt:3f2a...</c>) or exact deck names.</summary>
    public List<string> ExcludedDecks { get; set; } = new();

    /// <summary>Ignore archived HDT decks for protection (default true).</summary>
    public bool IgnoreArchivedDecks { get; set; } = true;

    /// <summary>Refuse to plan when the collection snapshot is older than this.</summary>
    public int MaxSnapshotAgeDays { get; set; } = 30;

    /// <summary>Refuse to plan when no saved decks are known (the "in use" rule would be blind).</summary>
    public bool RequireKnownDecks { get; set; } = true;

    public bool IsStandardPlayer => !string.Equals(Format, "wild", StringComparison.OrdinalIgnoreCase) && !PlaysWild;

    public bool PlaysClass(CardDef card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.IsNeutral || card.Classes.Any(c => ClassesPlayed.Contains(c, StringComparer.OrdinalIgnoreCase));
    }

    public static UserPrefs Load(string path) =>
        JsonSerializer.Deserialize<UserPrefs>(File.ReadAllText(path), CoreJson.Options) ?? new UserPrefs();

    public static UserPrefs LoadOrDefault(string? path) =>
        path is not null && File.Exists(path) ? Load(path) : new UserPrefs();

    public string ToJson() => JsonSerializer.Serialize(this, CoreJson.Options);
}
