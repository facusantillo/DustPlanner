using DustPlanner.Core.Planner;

namespace DustPlanner.Kick;

/// <summary>
/// Built-in templates, fallbacks and words, per language. Spanish uses the Rioplatense register the panel already uses
/// (<c>copialo</c>, <c>vos</c>); the streamer can override any template from Settings.
/// </summary>
public static class ChatTexts
{
    public const string Spanish = "es";
    public const string English = "en";

    public static readonly IReadOnlyList<string> Languages = new[] { Spanish, English };

    /// <summary>Every placeholder a template may use, with the English description shown in Settings.</summary>
    public static readonly IReadOnlyList<(string Name, string Meaning)> Placeholders = new[]
    {
        ("deck_name", "name of the active deck"),
        ("deckstring", "deck code of the active deck"),
        ("deck_class", "class of the active deck"),
        ("deck_format", "format of the active deck"),
        ("deck_url", "public page of the deck, empty when the page is off"),
        ("deck_link", "' · list: <url>' when the page is on, empty otherwise"),
        ("rank", "ladder standing (Diamond 3, Legend #1,234)"),
        ("rank_format", "format of that standing"),
        ("today_w", "wins today"),
        ("today_l", "losses today"),
        ("today_t", "draws today"),
        ("today_wr", "win rate today, with the % sign"),
        ("session_w", "wins this session"),
        ("session_l", "losses this session"),
        ("session_wr", "win rate this session"),
        ("streak", "current streak as a signed number"),
        ("streak_text", "' · 3-win streak' or similar, empty below 2"),
        ("last_result", "result of the last game"),
        ("player_class", "class you played the last game with"),
        ("opponent_class", "class of the last opponent"),
        ("turns", "turns of the last game"),
        ("dust", "arcane dust balance"),
        ("user", "viewer who asked, when the bot knows it"),
    };

    public static string Template(string language, string command) => (language, command) switch
    {
        (Spanish, CommandNames.Deck) => "{deck_name} · {deckstring} · copialo y pegalo en Hearthstone{deck_link}",
        (Spanish, CommandNames.Rank) => "{rank} · {rank_format}",
        (Spanish, CommandNames.WinRate) => "Hoy {today_w}-{today_l} ({today_wr}) · sesión {session_w}-{session_l}{streak_text}",
        (Spanish, CommandNames.Last) => "Última: {last_result} contra {opponent_class} en {turns} turnos",
        (Spanish, CommandNames.Dust) => "{dust} de polvo",
        (_, CommandNames.Deck) => "{deck_name} · {deckstring} · copy and paste it in Hearthstone{deck_link}",
        (_, CommandNames.Rank) => "{rank} · {rank_format}",
        (_, CommandNames.WinRate) => "Today {today_w}-{today_l} ({today_wr}) · session {session_w}-{session_l}{streak_text}",
        (_, CommandNames.Last) => "Last: {last_result} vs {opponent_class} in {turns} turns",
        (_, CommandNames.Dust) => "{dust} dust",
        _ => "{" + command + "}",
    };

    /// <summary>What the command says when the data it needs is not there yet. Never an error: the chat is not the place for one.</summary>
    public static string Fallback(string language, string command) => (language, command) switch
    {
        (Spanish, CommandNames.Deck) => "Todavía no hay mazo activo",
        (Spanish, CommandNames.Rank) => "Todavía no hay rango exportado",
        (Spanish, CommandNames.WinRate) => "Todavía no hay partidas hoy",
        (Spanish, CommandNames.Last) => "Todavía no terminó ninguna partida",
        (Spanish, CommandNames.Dust) => "Todavía no hay export de la colección",
        (_, CommandNames.Deck) => "No active deck yet",
        (_, CommandNames.Rank) => "No rank exported yet",
        (_, CommandNames.WinRate) => "No games today yet",
        (_, CommandNames.Last) => "No game finished yet",
        (_, CommandNames.Dust) => "No collection export yet",
        _ => "—",
    };

    public static string DeckLink(string language, string url) => language == Spanish ? " · lista: " + url : " · list: " + url;

    public static string StreakText(string language, int streak)
    {
        if (Math.Abs(streak) < 2)
        {
            return string.Empty;
        }

        return (language, streak > 0) switch
        {
            (Spanish, true) => " · racha de " + streak + " victorias",
            (Spanish, false) => " · " + (-streak) + " derrotas seguidas",
            (_, true) => " · " + streak + "-win streak",
            (_, false) => " · " + (-streak) + " losses in a row",
        };
    }

    public static string Result(string language, string? result) => (language, result) switch
    {
        (Spanish, "win") => "victoria",
        (Spanish, "loss") => "derrota",
        (Spanish, "tie") => "empate",
        (_, "win") => "win",
        (_, "loss") => "loss",
        (_, "tie") => "draw",
        _ => "?",
    };

    /// <summary>Game class ids (<c>PRIEST</c>) to the names the game client uses in each language.</summary>
    public static string ClassName(string language, string? classId)
    {
        if (string.IsNullOrEmpty(classId))
        {
            return "?";
        }

        var id = classId.ToUpperInvariant();
        if (language == Spanish)
        {
            return id switch
            {
                "DEATHKNIGHT" => "Caballero de la Muerte",
                "DEMONHUNTER" => "Cazador de demonios",
                "DRUID" => "Druida",
                "HUNTER" => "Cazador",
                "MAGE" => "Mago",
                "PALADIN" => "Paladín",
                "PRIEST" => "Sacerdote",
                "ROGUE" => "Pícaro",
                "SHAMAN" => "Chamán",
                "WARLOCK" => "Brujo",
                "WARRIOR" => "Guerrero",
                _ => TitleCase(id),
            };
        }

        return id switch
        {
            "DEATHKNIGHT" => "Death Knight",
            "DEMONHUNTER" => "Demon Hunter",
            _ => TitleCase(id),
        };
    }

    public static string FormatName(string language, string? format) => (language, format?.ToLowerInvariant()) switch
    {
        (Spanish, "standard") => "Estándar",
        (Spanish, "wild") => "Salvaje",
        (Spanish, "classic") => "Clásico",
        (Spanish, "twist") => "Twist",
        (_, null or "") => "?",
        (_, var f) => TitleCase(f),
    };

    /// <summary>Bronze..Diamond with the rank number (10 → 1), or Legend with its position.</summary>
    public static string RankLabel(string language, ChatRank rank)
    {
        ArgumentNullException.ThrowIfNull(rank);
        if (rank.LegendRank > 0)
        {
            return (language == Spanish ? "Leyenda #" : "Legend #") + ChatFormat.Number(language, rank.LegendRank);
        }

        var league = (language, rank.LeagueId) switch
        {
            (Spanish, 1) => "Bronce",
            (Spanish, 2) => "Plata",
            (Spanish, 3) => "Oro",
            (Spanish, 4) => "Platino",
            (Spanish, 5) => "Diamante",
            (_, 1) => "Bronze",
            (_, 2) => "Silver",
            (_, 3) => "Gold",
            (_, 4) => "Platinum",
            (_, 5) => "Diamond",
            (Spanish, _) => "Liga " + rank.LeagueId,
            (_, _) => "League " + rank.LeagueId,
        };
        var number = rank.StarLevel > 0 ? 11 - rank.StarLevel : 0;
        return number > 0 ? league + " " + number : league;
    }

    private static string TitleCase(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id.Substring(1).ToLowerInvariant();
}
