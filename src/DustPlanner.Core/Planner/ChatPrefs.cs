namespace DustPlanner.Core.Planner;

/// <summary>
/// The <c>chat</c> block of <c>prefs.json</c>: how the stream-chat commands answer (prefix, aliases, per-language
/// templates, cooldowns). Platform-neutral on purpose: nothing here knows which chat the text ends up in.
/// </summary>
public sealed class ChatPrefs
{
    /// <summary>Canonical command names, in the order the Settings page shows them.</summary>
    public static readonly IReadOnlyList<string> KnownCommands = new[] { CommandNames.Deck, CommandNames.Rank, CommandNames.WinRate, CommandNames.Last, CommandNames.Dust };

    public const string DefaultPrefix = "!";
    public const int DefaultCooldownSeconds = 10;
    public const int DefaultUserCooldownSeconds = 30;
    public const int MaxTemplateLength = 500;

    /// <summary>What a viewer types before a command (<c>!deck</c>). 1–3 characters, no spaces.</summary>
    public string Prefix { get; set; } = DefaultPrefix;

    /// <summary><c>es</c>, <c>en</c>, or null to follow the system UI language (Spanish → <c>es</c>, anything else → <c>en</c>).</summary>
    public string? Language { get; set; }

    /// <summary>Keyed by canonical command name; missing commands take their defaults on validation.</summary>
    public Dictionary<string, ChatCommandPrefs> Commands { get; set; } = Defaults();

    /// <summary>Minimum seconds between two answers to the same command, whoever asks.</summary>
    public int CooldownSeconds { get; set; } = DefaultCooldownSeconds;

    /// <summary>Minimum seconds between two answers to the same command for the same viewer (0 disables).</summary>
    public int UserCooldownSeconds { get; set; } = DefaultUserCooldownSeconds;

    public static Dictionary<string, ChatCommandPrefs> Defaults() => new(StringComparer.Ordinal)
    {
        [CommandNames.Deck] = new ChatCommandPrefs { Enabled = true, Aliases = { "mazo" } },
        [CommandNames.Rank] = new ChatCommandPrefs { Enabled = true, Aliases = { "rango" } },
        [CommandNames.WinRate] = new ChatCommandPrefs { Enabled = true, Aliases = { "winrate" } },
        [CommandNames.Last] = new ChatCommandPrefs { Enabled = true, Aliases = { "ultima" } },
        // Dust is the streamer's own business: off unless they turn it on.
        [CommandNames.Dust] = new ChatCommandPrefs { Enabled = false, Aliases = { "polvo" } },
    };

    /// <summary>
    /// Normalises (trims, lower-cases, fills missing commands) and returns the problems found, in plain words. An empty
    /// list means the block is valid and ready to save.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        Prefix = (Prefix ?? string.Empty).Trim();
        if (Prefix.Length is < 1 or > 3 || Prefix.Any(char.IsWhiteSpace) || Prefix.Any(char.IsLetterOrDigit))
        {
            errors.Add("chat.prefix must be 1 to 3 characters, no letters, digits or spaces (for example '!').");
        }

        Language = string.IsNullOrWhiteSpace(Language) ? null : Language.Trim().ToLowerInvariant();
        if (Language is not null && Language is not ("es" or "en"))
        {
            errors.Add("chat.language must be 'es', 'en' or empty (follow the system language).");
        }

        if (CooldownSeconds is < 0 or > 3600)
        {
            errors.Add("chat.cooldown_seconds must be between 0 and 3600.");
        }

        if (UserCooldownSeconds is < 0 or > 3600)
        {
            errors.Add("chat.user_cooldown_seconds must be between 0 and 3600.");
        }

        var commands = new Dictionary<string, ChatCommandPrefs>(StringComparer.Ordinal);
        foreach (var (key, value) in Commands ?? new Dictionary<string, ChatCommandPrefs>())
        {
            var name = (key ?? string.Empty).Trim().ToLowerInvariant();
            if (!KnownCommands.Contains(name, StringComparer.Ordinal))
            {
                errors.Add($"chat.commands.{key}: unknown command (known: {string.Join(", ", KnownCommands)}).");
                continue;
            }

            commands[name] = value ?? new ChatCommandPrefs();
        }

        foreach (var (name, defaults) in Defaults())
        {
            if (!commands.ContainsKey(name))
            {
                commands[name] = defaults;
            }
        }

        var taken = new HashSet<string>(KnownCommands, StringComparer.Ordinal);
        foreach (var name in KnownCommands)
        {
            var command = commands[name];
            var aliases = new List<string>();
            foreach (var raw in command.Aliases ?? new List<string>())
            {
                var alias = (raw ?? string.Empty).Trim().ToLowerInvariant();
                if (alias.Length == 0)
                {
                    continue;
                }

                if (!alias.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    errors.Add($"chat.commands.{name}: alias '{raw}' must be one word of letters, digits or '_', without the prefix.");
                    continue;
                }

                if (aliases.Contains(alias, StringComparer.Ordinal) || string.Equals(alias, name, StringComparison.Ordinal))
                {
                    continue; // repeated, or the command's own name: nothing to add
                }

                if (!taken.Add(alias))
                {
                    errors.Add($"chat.commands.{name}: alias '{alias}' is already a command or an alias of another command.");
                    continue;
                }

                aliases.Add(alias);
            }

            command.Aliases = aliases;
            command.Template = string.IsNullOrWhiteSpace(command.Template) ? null : command.Template.Trim();
            if (command.Template is { Length: > MaxTemplateLength })
            {
                errors.Add($"chat.commands.{name}: template is longer than {MaxTemplateLength} characters.");
            }

            if (command.Template is not null && command.Template.Contains('\n', StringComparison.Ordinal))
            {
                errors.Add($"chat.commands.{name}: template must be a single line.");
            }
        }

        Commands = commands;
        return errors;
    }

    /// <summary>Resolves a typed word (canonical name or alias, without the prefix) to the canonical command name.</summary>
    public string? Resolve(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        var w = word.Trim().ToLowerInvariant();
        foreach (var (name, command) in Commands)
        {
            if (string.Equals(name, w, StringComparison.Ordinal) || command.Aliases.Contains(w, StringComparer.Ordinal))
            {
                return name;
            }
        }

        return null;
    }
}

public sealed class ChatCommandPrefs
{
    public bool Enabled { get; set; } = true;

    /// <summary>Other words that trigger the command (<c>mazo</c> for <c>deck</c>), without the prefix.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Custom one-line template with <c>{placeholders}</c>; null uses the built-in text for the language.</summary>
    public string? Template { get; set; }
}

/// <summary>Canonical command names; aliases map onto these.</summary>
public static class CommandNames
{
    public const string Deck = "deck";
    public const string Rank = "rank";
    public const string WinRate = "wr";
    public const string Last = "last";
    public const string Dust = "dust";
}
