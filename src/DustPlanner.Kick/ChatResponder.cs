using System.Text.RegularExpressions;
using DustPlanner.Core.Planner;

namespace DustPlanner.Kick;

public enum ChatReplyStatus
{
    /// <summary>Text to post.</summary>
    Ok,

    /// <summary>Not a command of ours (nor an alias): the endpoint answers 404, the connector ignores the message.</summary>
    UnknownCommand,

    /// <summary>A known command the streamer turned off: same as unknown from the outside.</summary>
    Disabled,

    /// <summary>Answered too recently (globally or to this viewer): nothing is posted.</summary>
    Cooldown,
}

/// <summary>What the responder decided: <c>Text</c> is set only for <see cref="ChatReplyStatus.Ok"/>; <c>Command</c> is the canonical name when the word was recognised.</summary>
public sealed record ChatReply(ChatReplyStatus Status, string? Text, string? Command)
{
    public static ChatReply Unknown { get; } = new(ChatReplyStatus.UnknownCommand, null, null);
}

/// <summary>
/// The one place chat texts are produced: the local endpoints (Streamer.bot) and the native connector both come here.
/// Holds the cooldown clocks; settings are passed on every call so a saved <c>prefs.json</c> applies at once.
/// Thread-safe. Knows nothing about Kick: "command", "viewer", "stream".
/// </summary>
public sealed partial class ChatResponder
{
    private const int UserClockLimit = 2000;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastByCommand = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Command, string User), DateTimeOffset> _lastByUser = new();

    /// <summary>
    /// Answers <paramref name="word"/> (canonical name or alias, prefix already stripped) for <paramref name="user"/>
    /// (null when the caller does not know who asked: only the global cooldown applies).
    /// </summary>
    public ChatReply Respond(ChatPrefs prefs, string word, string? user, string? languageOverride, ChatContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(context);
        var command = prefs.Resolve(word);
        if (command is null || !prefs.Commands.TryGetValue(command, out var settings))
        {
            return ChatReply.Unknown;
        }

        if (!settings.Enabled)
        {
            return new ChatReply(ChatReplyStatus.Disabled, null, command);
        }

        var viewer = string.IsNullOrWhiteSpace(user) ? null : user.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (_lastByCommand.TryGetValue(command, out var last) && now - last < TimeSpan.FromSeconds(prefs.CooldownSeconds))
            {
                return new ChatReply(ChatReplyStatus.Cooldown, null, command);
            }

            if (viewer is not null && prefs.UserCooldownSeconds > 0
                && _lastByUser.TryGetValue((command, viewer), out var lastUser) && now - lastUser < TimeSpan.FromSeconds(prefs.UserCooldownSeconds))
            {
                return new ChatReply(ChatReplyStatus.Cooldown, null, command);
            }

            _lastByCommand[command] = now;
            if (viewer is not null)
            {
                if (_lastByUser.Count >= UserClockLimit)
                {
                    foreach (var stale in _lastByUser.Where(p => now - p.Value > TimeSpan.FromHours(1)).Select(p => p.Key).ToList())
                    {
                        _lastByUser.Remove(stale);
                    }
                }

                _lastByUser[(command, viewer)] = now;
            }
        }

        var language = ResolveLanguage(prefs, languageOverride);
        return new ChatReply(ChatReplyStatus.Ok, Render(prefs, command, language, context, user), command);
    }

    /// <summary>Forgets every cooldown (tests, and the Settings page after a save).</summary>
    public void ResetCooldowns()
    {
        lock (_gate)
        {
            _lastByCommand.Clear();
            _lastByUser.Clear();
        }
    }

    /// <summary><c>es</c> or <c>en</c>: the override when valid, else the saved preference, else the system language.</summary>
    public static string ResolveLanguage(ChatPrefs prefs, string? languageOverride)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var requested = (languageOverride ?? prefs.Language)?.Trim().ToLowerInvariant();
        if (requested is ChatTexts.Spanish or ChatTexts.English)
        {
            return requested;
        }

        return ChatFormat.SystemLanguage();
    }

    /// <summary>The text for a command, without touching cooldowns: fallback when its data is missing, otherwise the template with placeholders filled.</summary>
    public static string Render(ChatPrefs prefs, string command, string language, ChatContext context, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(context);
        var custom = prefs.Commands.TryGetValue(command, out var settings) ? settings.Template : null;
        string text;
        if (!HasData(command, context))
        {
            text = ChatTexts.Fallback(language, command);
        }
        else
        {
            var values = Placeholders(language, context, user);
            text = PlaceholderPattern().Replace(custom ?? ChatTexts.Template(language, command), m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        }

        return ChatFormat.WithoutLeadingPrefix(ChatFormat.Fit(text), prefs.Prefix);
    }

    private static bool HasData(string command, ChatContext context) => command switch
    {
        CommandNames.Deck => context.Deck is { Deckstring.Length: > 0 },
        CommandNames.Rank => context.Rank is not null,
        CommandNames.WinRate => context.Today.Games > 0 || context.Session.Games > 0,
        CommandNames.Last => context.LastGame is not null,
        CommandNames.Dust => context.Dust is not null,
        _ => true,
    };

    /// <summary>Every placeholder, even those the command does not use, so custom templates can mix them freely.</summary>
    internal static Dictionary<string, string> Placeholders(string language, ChatContext c, string? user)
    {
        var deck = c.Deck;
        var last = c.LastGame;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deck_name"] = deck?.Name ?? string.Empty,
            ["deckstring"] = deck?.Deckstring ?? string.Empty,
            ["deck_class"] = deck is null ? string.Empty : ChatTexts.ClassName(language, deck.CardClass),
            ["deck_format"] = deck is null ? string.Empty : ChatTexts.FormatName(language, deck.Format),
            ["deck_url"] = c.DeckUrl ?? string.Empty,
            ["deck_link"] = string.IsNullOrEmpty(c.DeckUrl) ? string.Empty : ChatTexts.DeckLink(language, c.DeckUrl),
            ["rank"] = c.Rank is null ? string.Empty : ChatTexts.RankLabel(language, c.Rank),
            ["rank_format"] = c.Rank is null ? string.Empty : ChatTexts.FormatName(language, c.Rank.Format),
            ["today_w"] = ChatFormat.Number(language, c.Today.Wins),
            ["today_l"] = ChatFormat.Number(language, c.Today.Losses),
            ["today_t"] = ChatFormat.Number(language, c.Today.Ties),
            ["today_wr"] = ChatFormat.Percent(language, c.Today.WinRate),
            ["session_w"] = ChatFormat.Number(language, c.Session.Wins),
            ["session_l"] = ChatFormat.Number(language, c.Session.Losses),
            ["session_wr"] = ChatFormat.Percent(language, c.Session.WinRate),
            ["streak"] = c.Streak.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["streak_text"] = ChatTexts.StreakText(language, c.Streak),
            ["last_result"] = last is null ? string.Empty : ChatTexts.Result(language, last.Result),
            ["player_class"] = last is null ? string.Empty : ChatTexts.ClassName(language, last.PlayerClass),
            ["opponent_class"] = last is null ? string.Empty : ChatTexts.ClassName(language, last.OpponentClass),
            ["turns"] = last is null ? string.Empty : ChatFormat.Number(language, last.Turns),
            ["dust"] = c.Dust is null ? string.Empty : ChatFormat.Number(language, c.Dust.Value),
            ["user"] = user?.Trim() ?? string.Empty,
        };
    }

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex PlaceholderPattern();
}
