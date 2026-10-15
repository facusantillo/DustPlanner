namespace DustPlanner.Host.Api;

/// <summary>Bodies the panel posts (snake_case on the wire, like every DustPlanner JSON).</summary>
internal sealed record NeedRequest(string Deck, string? Account, string? AsOf, string? Locale);

internal sealed record PlanRequest(string Deck, string? Account, string? AsOf, string? Locale, int? Need, IReadOnlyList<string>? ProtectDeckCodes, bool AllowNoDecks, bool Save);

internal sealed record SyncRequest(string? Account);

internal sealed record GamesImportRequest(string? Logs);

internal sealed record MetaRequest(string Text);

internal sealed record CardOverrideRequest(string Card, bool Golden, bool Uncraftable);

internal sealed record ServiceInfo(string Name, string Version, string Url, string Home, int Pid, DateTimeOffset StartedAt);
