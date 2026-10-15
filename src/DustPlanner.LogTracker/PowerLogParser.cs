using System.Globalization;
using System.Text.RegularExpressions;

namespace DustPlanner.LogTracker;

/// <summary>
/// Incremental Power.log parser that reconstructs games (players, heroes, result, turns, metadata). It reads only the
/// <c>GameState.DebugPrintPower</c> / <c>DebugPrintGame</c> lines the game itself writes; nothing is sent to the client.
/// Feed lines in order; completed games are raised through <see cref="GameCompleted"/> and collected in <see cref="Games"/>.
/// </summary>
public sealed partial class PowerLogParser
{
    private readonly List<GameRecord> _games = new();
    private readonly DateOnly _baseDate;
    private readonly TimeZoneInfo _timeZone;
    private DateOnly _currentDate;
    private TimeOnly? _lastTime;
    private GameState? _game;

    /// <param name="baseDate">Date of the first line (Power.log only carries times); later lines roll over midnight automatically.</param>
    /// <param name="timeZone">Zone the log's clock times belong to: the game writes local time, so the machine's zone by default.</param>
    public PowerLogParser(DateOnly baseDate, TimeZoneInfo? timeZone = null)
    {
        _baseDate = baseDate;
        _currentDate = baseDate;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public event Action<GameRecord>? GameCompleted;

    public IReadOnlyList<GameRecord> Games => _games;

    /// <summary>
    /// The game currently being played, if a CREATE_GAME was seen without a matching COMPLETE. When no revealed hand has
    /// settled the local player yet, the fallback rules are applied tentatively so the overlay can show the opponent.
    /// </summary>
    public GameRecord? Current
    {
        get
        {
            if (_game is null)
            {
                return null;
            }

            var record = _game.ToRecord(completed: false);
            if (record.Friendly is null)
            {
                var guess = FallbackFriendly(_game);
                if (guess is not null)
                {
                    record = _game.ToRecord(completed: false, tentativeFriendlyId: guess.PlayerId);
                }
            }

            return record;
        }
    }

    public void Feed(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            Feed(line);
        }
    }

    public void Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = LineRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var time = TimeOnly.ParseExact(match.Groups["time"].Value[..8], "HH:mm:ss", CultureInfo.InvariantCulture);
        if (_lastTime is { } last && time < last && last - time > TimeSpan.FromHours(12))
        {
            _currentDate = _currentDate.AddDays(1); // midnight rollover
        }

        _lastTime = time;
        var wallClock = _currentDate.ToDateTime(time);
        var timestamp = new DateTimeOffset(wallClock, _timeZone.GetUtcOffset(wallClock));
        var method = match.Groups["method"].Value;
        var body = match.Groups["body"].Value;

        if (method == "GameState.DebugPrintPower")
        {
            HandlePower(body, timestamp);
        }
        else if (method == "GameState.DebugPrintGame")
        {
            HandleGameInfo(body);
        }
    }

    /// <summary>Call at end of input: an open game is reported as incomplete (result unknown).</summary>
    public GameRecord? Flush()
    {
        if (_game is null)
        {
            return null;
        }

        var game = _game;
        EndBlock(game);
        DecideFriendlyByFallback(game);
        var record = game.ToRecord(completed: false);
        _game = null;
        return record;
    }

    private void HandlePower(string body, DateTimeOffset timestamp)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("CREATE_GAME", StringComparison.Ordinal))
        {
            if (_game is not null)
            {
                // Previous game never completed (crash, disconnect): keep it as unknown.
                _games.Add(Flush()!);
            }

            _game = new GameState(timestamp);
            return;
        }

        if (_game is null)
        {
            return;
        }

        var game = _game;
        var tag = TagRegex().Match(trimmed);
        if (tag.Success)
        {
            ApplyBlockTag(game, tag.Groups["tag"].Value, tag.Groups["value"].Value, timestamp);
            return;
        }

        // Every other line ends the tag block that was open (and may decide the friendly player from it).
        EndBlock(game);

        if (trimmed.StartsWith("GameEntity EntityID=", StringComparison.Ordinal))
        {
            game.Block = Block.GameEntity;
            game.GameEntityId = int.Parse(trimmed["GameEntity EntityID=".Length..].Trim(), CultureInfo.InvariantCulture);
            return;
        }

        var player = PlayerRegex().Match(trimmed);
        if (player.Success)
        {
            var p = new ParsedPlayer
            {
                EntityId = int.Parse(player.Groups["entity"].Value, CultureInfo.InvariantCulture),
                PlayerId = int.Parse(player.Groups["player"].Value, CultureInfo.InvariantCulture),
                AccountHi = ulong.Parse(player.Groups["hi"].Value, CultureInfo.InvariantCulture),
                AccountLo = ulong.Parse(player.Groups["lo"].Value, CultureInfo.InvariantCulture),
            };
            game.Players[p.PlayerId] = p;
            game.Controllers[p.EntityId] = p.PlayerId;
            game.Block = Block.Player;
            game.BlockPlayerId = p.PlayerId;
            return;
        }

        var full = FullEntityRegex().Match(trimmed);
        if (full.Success)
        {
            game.Block = Block.Entity;
            game.BlockEntityId = int.Parse(full.Groups["id"].Value, CultureInfo.InvariantCulture);
            game.BlockCardId = full.Groups["card"].Value;
            game.BlockIsShow = false;
            game.BlockTags.Clear();
            return;
        }

        // The initial entity dump (GameEntity, players, FULL_ENTITY blocks) ends with the first line of any other kind.
        game.InitialDump = false;

        var show = ShowEntityRegex().Match(trimmed);
        if (show.Success)
        {
            game.Block = Block.Entity;
            game.BlockEntityId = ParseEntityRef(show.Groups["entity"].Value);
            game.BlockCardId = show.Groups["card"].Value;
            game.BlockIsShow = true;
            game.BlockTags.Clear();
            return;
        }

        var tagChange = TagChangeRegex().Match(trimmed);
        if (tagChange.Success)
        {
            ApplyTagChange(game, tagChange.Groups["entity"].Value, tagChange.Groups["tag"].Value, tagChange.Groups["value"].Value, timestamp);
        }

        // Anything else (BLOCK_START/END, META_DATA, HIDE_ENTITY, CHANGE_ENTITY, ...) needs no state.
    }

    /// <summary>
    /// Closes the open tag block. For entity blocks this is where the local player is inferred, following the hslog
    /// heuristic: a card revealed (CardID known) in HAND belongs to the local player, either in the initial entity dump
    /// (reconnects) or as the first SHOW_ENTITY into the hand (the mulligan). The opponent's hand is never revealed
    /// that way.
    /// </summary>
    private static void EndBlock(GameState game)
    {
        if (game.Block == Block.Entity)
        {
            NoteHero(game);
            if (game.BlockTags.TryGetValue("ZONE", out var zoneTag))
            {
                game.Zones[game.BlockEntityId] = zoneTag;
            }

            if (!game.FriendlyDecided && game.BlockCardId.Length > 0 && (game.BlockIsShow || game.InitialDump))
            {
                var zone = zoneTag ?? game.Zones.GetValueOrDefault(game.BlockEntityId);
                var cardType = game.BlockTags.GetValueOrDefault("CARDTYPE");
                if (zone == "HAND" && cardType is not ("HERO" or "HERO_POWER" or "PLAYER" or "GAME"))
                {
                    var controller = game.Controllers.GetValueOrDefault(game.BlockEntityId);
                    if (controller != 0 && game.Players.ContainsKey(controller))
                    {
                        SetFriendly(game, controller);
                    }
                }
            }
        }

        game.Block = Block.None;
    }

    private void ApplyBlockTag(GameState game, string tag, string value, DateTimeOffset timestamp)
    {
        switch (game.Block)
        {
            case Block.GameEntity:
                ApplyGameTag(game, tag, value, timestamp);
                break;
            case Block.Player when game.Players.TryGetValue(game.BlockPlayerId, out var player):
                if (tag == "FIRST_PLAYER" && value == "1")
                {
                    player.IsFirst = true;
                }
                else if (tag == "PLAYSTATE")
                {
                    player.PlayState = value;
                }

                break;
            case Block.Entity:
                game.BlockTags[tag] = value;
                if (tag == "CONTROLLER" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var controller))
                {
                    game.Controllers[game.BlockEntityId] = controller;
                }

                break;
        }
    }

    /// <summary>Heroes: the first HERO card a player controls in PLAY (FULL_ENTITY only; hero powers and skins in SETASIDE are ignored).</summary>
    private static void NoteHero(GameState game)
    {
        if (game.BlockIsShow || game.BlockTags.GetValueOrDefault("ZONE") != "PLAY" || game.BlockTags.GetValueOrDefault("CARDTYPE") != "HERO"
            || !game.BlockCardId.StartsWith("HERO_", StringComparison.Ordinal))
        {
            return;
        }

        var controller = game.Controllers.GetValueOrDefault(game.BlockEntityId);
        if (controller != 0 && game.Players.TryGetValue(controller, out var player) && player.HeroCardId is null)
        {
            player.HeroCardId = game.BlockCardId;
        }
    }

    private static void SetFriendly(GameState game, int playerId)
    {
        foreach (var p in game.Players.Values)
        {
            p.IsFriendly = p.PlayerId == playerId;
        }

        game.FriendlyDecided = true;
    }

    private void ApplyGameTag(GameState game, string tag, string value, DateTimeOffset timestamp)
    {
        switch (tag)
        {
            case "TURN" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var turn):
                game.Turns = Math.Max(game.Turns, turn);
                break;
            case "STATE" when value == "COMPLETE":
                game.EndedAt = timestamp;
                Complete(game);
                break;
        }
    }

    private void ApplyTagChange(GameState game, string entityRef, string tag, string value, DateTimeOffset timestamp)
    {
        if (tag is "CONTROLLER" or "ZONE")
        {
            // Card entities: keep the controller/zone maps current (names as refs are not resolvable; ids and [... id=N ...] are).
            var cardEntity = ParseEntityRef(entityRef);
            if (cardEntity != 0 && game.Players.Values.All(p => p.EntityId != cardEntity))
            {
                if (tag == "ZONE")
                {
                    game.Zones[cardEntity] = value;
                }
                else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var controller))
                {
                    game.Controllers[cardEntity] = controller;
                }

                return;
            }
        }

        if (entityRef == "GameEntity" || (int.TryParse(entityRef, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id == game.GameEntityId))
        {
            ApplyGameTag(game, tag, value, timestamp);
            return;
        }

        if (!PlayerTags.Contains(tag))
        {
            return; // entity refs for cards can be names in older logs; only player-level tags identify players
        }

        var player = ResolvePlayer(game, entityRef);
        if (player is null)
        {
            return;
        }

        switch (tag)
        {
            case "PLAYSTATE":
                player.PlayState = value;
                break;
            case "FIRST_PLAYER" when value == "1":
                player.IsFirst = true;
                break;
        }
    }

    private static readonly HashSet<string> PlayerTags = new(StringComparer.Ordinal)
    {
        "PLAYSTATE", "FIRST_PLAYER", "TURN", "MULLIGAN_STATE", "CURRENT_PLAYER", "TIMEOUT", "RESOURCES", "TEMP_RESOURCES", "FATIGUE",
    };

    private static ParsedPlayer? ResolvePlayer(GameState game, string entityRef)
    {
        if (int.TryParse(entityRef, NumberStyles.None, CultureInfo.InvariantCulture, out var entityId))
        {
            return game.Players.Values.FirstOrDefault(p => p.EntityId == entityId);
        }

        if (entityRef.StartsWith('[') || entityRef == "GameEntity")
        {
            return null;
        }

        var exact = game.Players.Values.FirstOrDefault(p => string.Equals(p.Name, entityRef, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        // Some lines drop the "#1234" discriminator ("Maievs" for "Maievs#11864").
        var prefix = game.Players.Values.FirstOrDefault(p => p.Name is not null && NameWithoutHash(p.Name) == NameWithoutHash(entityRef));
        if (prefix is not null)
        {
            return prefix;
        }

        // A name we have never seen: it belongs to the only player whose name is still unknown.
        var unknown = game.Players.Values.Where(p => p.Name is null || p.Name == UnknownHumanPlayer).ToList();
        if (unknown.Count == 1 && entityRef.Contains('#'))
        {
            unknown[0].Name = entityRef;
            return unknown[0];
        }

        return unknown.Count == 1 ? unknown[0] : null;
    }

    private void HandleGameInfo(string body)
    {
        if (_game is null)
        {
            return;
        }

        var trimmed = body.Trim();
        if (trimmed.StartsWith("BuildNumber=", StringComparison.Ordinal) && int.TryParse(trimmed["BuildNumber=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var build))
        {
            _game.BuildNumber = build;
        }
        else if (trimmed.StartsWith("GameType=", StringComparison.Ordinal))
        {
            _game.GameType = trimmed["GameType=".Length..];
        }
        else if (trimmed.StartsWith("FormatType=", StringComparison.Ordinal))
        {
            _game.FormatType = trimmed["FormatType=".Length..];
        }
        else if (trimmed.StartsWith("ScenarioID=", StringComparison.Ordinal) && int.TryParse(trimmed["ScenarioID=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var scenario))
        {
            _game.ScenarioId = scenario;
        }
        else
        {
            var name = PlayerNameRegex().Match(trimmed);
            if (name.Success && _game.Players.TryGetValue(int.Parse(name.Groups["id"].Value, CultureInfo.InvariantCulture), out var player))
            {
                var value = name.Groups["name"].Value.Trim();
                if (value != UnknownHumanPlayer || player.Name is null)
                {
                    player.Name = value;
                }
            }
        }
    }

    private void Complete(GameState game)
    {
        EndBlock(game);
        DecideFriendlyByFallback(game);
        var record = game.ToRecord(completed: true);
        _games.Add(record);
        _game = null;
        GameCompleted?.Invoke(record);
    }

    /// <summary>When no revealed hand card settled it: the only non-AI player, else the only player with a known name.</summary>
    private static void DecideFriendlyByFallback(GameState game)
    {
        if (game.FriendlyDecided)
        {
            return;
        }

        var guess = FallbackFriendly(game);
        if (guess is not null)
        {
            SetFriendly(game, guess.PlayerId);
        }
    }

    private static ParsedPlayer? FallbackFriendly(GameState game)
    {
        var human = game.Players.Values.Where(p => !p.IsAi).ToList();
        if (human.Count == 1)
        {
            return human[0];
        }

        var named = game.Players.Values.Where(p => p.Name is not null && p.Name != UnknownHumanPlayer).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static int ParseEntityRef(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        var inner = EntityIdInBracketsRegex().Match(value);
        return inner.Success ? int.Parse(inner.Groups["id"].Value, CultureInfo.InvariantCulture) : 0;
    }

    internal const string UnknownHumanPlayer = "UNKNOWN HUMAN PLAYER";

    internal static string NameWithoutHash(string name)
    {
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? name : name[..hash];
    }

    private enum Block
    {
        None,
        GameEntity,
        Player,
        Entity,
    }

    private sealed class GameState(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset? EndedAt { get; set; }

        public int GameEntityId { get; set; } = 1;

        public Dictionary<int, ParsedPlayer> Players { get; } = new();

        public Dictionary<int, int> Controllers { get; } = new();

        public Dictionary<int, string> Zones { get; } = new();

        public Dictionary<string, string> BlockTags { get; } = new(StringComparer.Ordinal);

        public Block Block { get; set; }

        /// <summary>True while reading the entity dump that follows CREATE_GAME (on reconnect it includes the local hand).</summary>
        public bool InitialDump { get; set; } = true;

        public int BlockPlayerId { get; set; }

        public int BlockEntityId { get; set; }

        public string BlockCardId { get; set; } = string.Empty;

        public bool BlockIsShow { get; set; }

        public bool FriendlyDecided { get; set; }

        public int Turns { get; set; }

        public int? BuildNumber { get; set; }

        public string? GameType { get; set; }

        public string? FormatType { get; set; }

        public int? ScenarioId { get; set; }

        public GameRecord ToRecord(bool completed, int? tentativeFriendlyId = null) => new()
        {
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            BuildNumber = BuildNumber,
            GameType = GameType,
            FormatType = FormatType,
            ScenarioId = ScenarioId,
            Turns = Turns,
            Players = Players.Values.OrderBy(p => p.PlayerId).Select(p => tentativeFriendlyId is null ? p : p.Copy(isFriendly: p.PlayerId == tentativeFriendlyId)).ToList(),
            Completed = completed,
        };
    }

    [GeneratedRegex(@"^[A-Z] (?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?) (?<method>[A-Za-z]+\.[A-Za-z]+)\(\) - (?<body>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^Player EntityID=(?<entity>\d+) PlayerID=(?<player>\d+) GameAccountId=\[hi=(?<hi>\d+) lo=(?<lo>\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerRegex();

    [GeneratedRegex(@"^FULL_ENTITY - (?:Creating|Updating) (?:ID|Entity)=(?<id>\d+) CardID=(?<card>[^\s]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FullEntityRegex();

    [GeneratedRegex(@"^SHOW_ENTITY - Updating Entity=(?<entity>.+?) CardID=(?<card>[^\s]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ShowEntityRegex();

    [GeneratedRegex(@"^TAG_CHANGE Entity=(?<entity>.+?) tag=(?<tag>[A-Z_0-9]+) value=(?<value>[^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagChangeRegex();

    [GeneratedRegex(@"^tag=(?<tag>[A-Z_0-9]+) value=(?<value>[^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^PlayerID=(?<id>\d+), PlayerName=(?<name>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerNameRegex();

    [GeneratedRegex(@"\bid=(?<id>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdInBracketsRegex();
}
