using System.Text.Json;
using System.Text.Json.Nodes;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;

namespace DustPlanner.Kick;

/// <summary>The relay's WebSocket protocol (PRD §5.3), version 1. JSON text frames, snake_case like the rest of the project.</summary>
public static class RelayProtocol
{
    public const int Version = 1;

    /// <summary>Sent byte for byte: the relay answers it without waking the channel's Durable Object.</summary>
    public const string PingFrame = "{\"v\":1,\"type\":\"ping\"}";

    public static readonly JsonSerializerOptions Json = new(CoreJson.Options) { WriteIndented = false };

    /// <summary>What the service tells the relay after every hello: how to filter chat before it reaches the service.</summary>
    public static JsonObject ConfigMessage(ChatPrefs chat, KickPrefs kick)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(kick);
        var commands = new JsonArray();
        foreach (var name in ChatPrefs.KnownCommands)
        {
            if (chat.Commands.TryGetValue(name, out var command) && command.Enabled)
            {
                commands.Add(new JsonObject { ["name"] = name, ["aliases"] = new JsonArray(command.Aliases.Select(a => (JsonNode?)a).ToArray()) });
            }
        }

        return new JsonObject
        {
            ["v"] = Version,
            ["type"] = "config",
            ["prefix"] = chat.Prefix,
            ["commands"] = commands,
            ["public_page"] = kick.PublicPage,
            ["forward"] = new JsonObject
            {
                // Stream status is cheap and the host needs it for sessions (K7); redemptions only when they are shown.
                ["stream_status"] = true,
                ["redemptions"] = kick.ShowRedemptions,
                ["redemption_filter"] = new JsonArray(kick.RedemptionFilter.Select(r => (JsonNode?)r).ToArray()),
            },
        };
    }

    public static JsonObject ChatSendMessage(string text, string? replyToMessageId)
    {
        var message = new JsonObject { ["v"] = Version, ["type"] = "chat.send", ["text"] = text };
        if (!string.IsNullOrEmpty(replyToMessageId))
        {
            message["reply_to_message_id"] = replyToMessageId;
        }

        return message;
    }

    public static JsonObject DeckPublishMessage(string name, string deckstring, string? cardClass, string? format) => new()
    {
        ["v"] = Version,
        ["type"] = "deck.publish",
        ["name"] = name,
        ["deckstring"] = deckstring,
        ["card_class"] = cardClass,
        ["format"] = format,
    };

    /// <summary>Parses a frame from the relay; null for anything that is not a v1 message.</summary>
    public static RelayMessage? Parse(string frame)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(frame);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject o || (int?)o["v"] != Version || o["type"] is not JsonValue typeValue || typeValue.GetValue<string>() is not { } type)
        {
            return null;
        }

        switch (type)
        {
            case "hello":
                return new HelloMessage(
                    ReadChannel(o["channel"]),
                    (bool?)o["needs_reconnect"] ?? false,
                    o["missing_scopes"] is JsonArray scopes ? scopes.Select(s => (string?)s ?? string.Empty).Where(s => s.Length > 0).ToArray() : Array.Empty<string>(),
                    ReadLive(o["live"]),
                    (string?)o["server_version"] ?? string.Empty);
            case "chat.command":
                var sender = o["sender"] as JsonObject;
                return new ChatCommandMessage(
                    (string?)o["message_id"] ?? string.Empty,
                    (string?)o["command"] ?? string.Empty,
                    (string?)o["args"] ?? string.Empty,
                    (string?)sender?["user_id"] ?? string.Empty,
                    (string?)sender?["username"] ?? string.Empty,
                    sender?["badges"] is JsonArray badges ? badges.Select(b => (string?)b ?? string.Empty).Where(b => b.Length > 0).ToArray() : Array.Empty<string>(),
                    ParseTime(o["at"]));
            case "stream.status":
                return new StreamStatusMessage(ReadLive(o));
            case "reward.redemption":
                var reward = o["reward"] as JsonObject;
                return new RewardRedemptionMessage(
                    (string?)o["id"] ?? string.Empty,
                    (string?)o["status"] ?? string.Empty,
                    (string?)reward?["id"] ?? string.Empty,
                    (string?)reward?["title"] ?? string.Empty,
                    (int?)reward?["cost"] ?? 0,
                    (string?)(o["redeemer"] as JsonObject)?["username"] ?? string.Empty,
                    (string?)o["user_input"] ?? string.Empty);
            case "error":
                return new RelayErrorMessage((string?)o["code"] ?? "unknown", (string?)o["detail"] ?? string.Empty);
            case "pong":
                return PongMessage.Instance;
            default:
                return null;
        }
    }

    private static RelayChannel ReadChannel(JsonNode? node) =>
        node is JsonObject c
            ? new RelayChannel((string?)c["user_id"] ?? string.Empty, (string?)c["slug"] ?? string.Empty, (string?)c["username"] ?? string.Empty)
            : new RelayChannel(string.Empty, string.Empty, string.Empty);

    private static LiveStatus ReadLive(JsonNode? node) =>
        node is JsonObject l
            ? new LiveStatus((bool?)l["is_live"] ?? false, (string?)l["title"], ParseTime(l["started_at"]), ParseTime(l["ended_at"]))
            : new LiveStatus(false, null, null, null);

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}

public abstract record RelayMessage;

public sealed record RelayChannel(string UserId, string Slug, string Username);

public sealed record LiveStatus(bool IsLive, string? Title, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

public sealed record HelloMessage(RelayChannel Channel, bool NeedsReconnect, IReadOnlyList<string> MissingScopes, LiveStatus Live, string ServerVersion) : RelayMessage;

public sealed record ChatCommandMessage(string MessageId, string Command, string Args, string SenderUserId, string SenderUsername, IReadOnlyList<string> Badges, DateTimeOffset? At) : RelayMessage;

public sealed record StreamStatusMessage(LiveStatus Live) : RelayMessage;

public sealed record RewardRedemptionMessage(string Id, string Status, string RewardId, string RewardTitle, int Cost, string RedeemerUsername, string UserInput) : RelayMessage;

public sealed record RelayErrorMessage(string Code, string Detail) : RelayMessage;

public sealed record PongMessage : RelayMessage
{
    public static PongMessage Instance { get; } = new();
}
