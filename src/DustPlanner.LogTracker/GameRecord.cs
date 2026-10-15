namespace DustPlanner.LogTracker;

/// <summary>One player of a parsed game.</summary>
public sealed class ParsedPlayer
{
    public int PlayerId { get; init; }

    public int EntityId { get; init; }

    public ulong AccountHi { get; set; }

    public ulong AccountLo { get; set; }

    /// <summary>BattleTag as seen in the log (may lack the <c>#1234</c> part on some lines), null when never revealed.</summary>
    public string? Name { get; set; }

    /// <summary>First hero card the player controlled (e.g. <c>HERO_09</c>, <c>HERO_04y</c>).</summary>
    public string? HeroCardId { get; set; }

    /// <summary><c>WON</c>, <c>LOST</c>, <c>TIED</c>; <c>CONCEDED</c> is always followed by <c>LOST</c>.</summary>
    public string? PlayState { get; set; }

    public bool IsFirst { get; set; }

    /// <summary>True when the log revealed this player's hand (the local player), decided like hslog's FriendlyPlayerExporter.</summary>
    public bool IsFriendly { get; set; }

    public bool IsAi => AccountLo == 0;

    public string AccountKey => Contracts.AccountKeys.Format(AccountHi, AccountLo);

    /// <summary>A detached copy (used for tentative views of a game in progress).</summary>
    public ParsedPlayer Copy(bool isFriendly) => new()
    {
        PlayerId = PlayerId,
        EntityId = EntityId,
        AccountHi = AccountHi,
        AccountLo = AccountLo,
        Name = Name,
        HeroCardId = HeroCardId,
        PlayState = PlayState,
        IsFirst = IsFirst,
        IsFriendly = isFriendly,
    };
}

/// <summary>A complete game reconstructed from Power.log.</summary>
public sealed class GameRecord
{
    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    public int? BuildNumber { get; set; }

    /// <summary><c>GT_RANKED</c>, <c>GT_CASUAL</c>, <c>GT_ARENA</c>, ... as written in the log.</summary>
    public string? GameType { get; set; }

    /// <summary><c>FT_STANDARD</c>, <c>FT_WILD</c>, ... as written in the log.</summary>
    public string? FormatType { get; set; }

    public int? ScenarioId { get; set; }

    public int Turns { get; set; }

    public IReadOnlyList<ParsedPlayer> Players { get; init; } = Array.Empty<ParsedPlayer>();

    public bool Completed { get; set; }

    public ParsedPlayer? Friendly => Players.FirstOrDefault(p => p.IsFriendly);

    /// <summary>The other player; null until the friendly player is known (otherwise it could name the local player).</summary>
    public ParsedPlayer? Opponent => Friendly is null ? null : Players.FirstOrDefault(p => !p.IsFriendly);

    /// <summary><c>win</c>, <c>loss</c>, <c>tie</c> or <c>unknown</c> from the friendly player's point of view.</summary>
    public string Result => Friendly?.PlayState switch
    {
        "WON" => "win",
        "LOST" => "loss",
        "TIED" => "tie",
        _ => "unknown",
    };

    /// <summary>Stable identity of the game for de-duplication across re-reads of the same log.</summary>
    public string Key
    {
        get
        {
            var ids = string.Join("-", Players.OrderBy(p => p.PlayerId).Select(p => p.AccountLo));
            return $"{StartedAt:yyyyMMddTHHmmss}:{BuildNumber ?? 0}:{ids}";
        }
    }
}
