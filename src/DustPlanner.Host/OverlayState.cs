namespace DustPlanner.Host;

/// <summary>What the overlay shows. Immutable; a new instance is published on every change.</summary>
public sealed record OverlayState
{
    public DateTimeOffset UpdatedAt { get; init; }

    public string? AccountKey { get; init; }

    public string? BattleTag { get; init; }

    public RankState? Rank { get; init; }

    public DustState? Dust { get; init; }

    /// <summary>Games completed since the host started.</summary>
    public RecordState Session { get; init; } = new();

    /// <summary>Games started today (local), from the database plus the session.</summary>
    public RecordState Today { get; init; } = new();

    public CurrentGameState? CurrentGame { get; init; }

    public LastGameState? LastGame { get; init; }

    /// <summary>The deck selected in HDT (<c>active-deck.json</c>); null until the exporter sees one, or when it is an Arena deck.</summary>
    public ActiveDeckState? ActiveDeck { get; init; }

    /// <summary>Result of the last automatic or requested import of the exporter files into the database.</summary>
    public SyncState? LastSync { get; init; }

    /// <summary>Things the user should fix (no Power.log, no export yet), shown in the status page, never in the overlay.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static OverlayState Empty { get; } = new() { UpdatedAt = DateTimeOffset.UtcNow };
}

public sealed record RankState(string Label, string Format, int LeagueId, int StarLevel, int Stars, int LegendRank, DateTimeOffset TakenAt);

public sealed record DustState(int Value, DateTimeOffset TakenAt);

public sealed record RecordState
{
    public int Wins { get; init; }

    public int Losses { get; init; }

    public int Ties { get; init; }

    public int Games => Wins + Losses + Ties;

    /// <summary>Wins over decided games, 0..1; null when nothing was decided yet.</summary>
    public double? WinRate => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);

    /// <summary>Positive for a winning streak, negative for a losing one.</summary>
    public int Streak { get; init; }

    public RecordState Add(string result) => result switch
    {
        "win" => this with { Wins = Wins + 1, Streak = Streak >= 0 ? Streak + 1 : 1 },
        "loss" => this with { Losses = Losses + 1, Streak = Streak <= 0 ? Streak - 1 : -1 },
        "tie" => this with { Ties = Ties + 1 },
        _ => this,
    };
}

public sealed record CurrentGameState(DateTimeOffset StartedAt, string? GameType, string? FormatType, string? PlayerClass, string? OpponentClass, string? OpponentName, int Turn);

public sealed record LastGameState(DateTimeOffset EndedAt, string Result, string? PlayerClass, string? OpponentClass, string? OpponentName, int Turns);

/// <summary>HDT's active deck as exported: name, deck code, upper-case class, format, and when the exporter sampled it.</summary>
public sealed record ActiveDeckState(string? HdtDeckId, string Name, string Deckstring, string? CardClass, string? Format, DateTimeOffset UpdatedAt);

/// <summary>The sync state plus the per-account summaries the CLI prints.</summary>
public sealed record SyncOutcome(SyncState State, IReadOnlyList<DustPlanner.App.SyncSummary> Accounts);

/// <summary>What the last sync did: when, whether the export was new, how many card changes it recorded (and their plan matches).</summary>
public sealed record SyncState(DateTimeOffset At, DateTimeOffset SnapshotTakenAt, bool NewSnapshot, int Dust, int DustDelta, int Events, int MatchedEvents, string? Error);
