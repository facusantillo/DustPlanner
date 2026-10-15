using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Json;

namespace DustPlanner.Core.Collection;

/// <summary>One account folder in the exporter's inbox.</summary>
public sealed record InboxAccount(string AccountKey, string Directory, string? BattleTag, string? Region, DateTimeOffset? CollectionExportedAt,
    bool HasDecks, bool HasRank, bool HasDiagnostics);

/// <summary>Finds the exporter's output (<c>inbox/&lt;account_key&gt;/*.json</c>) and picks an account.</summary>
public static class InboxLocator
{
    public static IReadOnlyList<InboxAccount> ListAccounts(string inboxDirectory)
    {
        if (!Directory.Exists(inboxDirectory))
        {
            return Array.Empty<InboxAccount>();
        }

        var accounts = new List<InboxAccount>();
        foreach (var dir in Directory.GetDirectories(inboxDirectory))
        {
            var collectionFile = Path.Combine(dir, ExportSchema.CollectionFileName);
            if (!File.Exists(collectionFile))
            {
                continue;
            }

            string? battleTag = null;
            string? region = null;
            DateTimeOffset? exportedAt = null;
            try
            {
                var export = JsonSerializer.Deserialize<CollectionExport>(File.ReadAllText(collectionFile), CoreJson.Options);
                battleTag = export?.Meta.Account.BattleTag;
                region = export?.Meta.Account.Region;
                exportedAt = export?.Meta.ExportedAt;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                // Listed anyway; the import will report the problem.
            }

            accounts.Add(new InboxAccount(Path.GetFileName(dir), dir, battleTag, region, exportedAt,
                File.Exists(Path.Combine(dir, ExportSchema.DecksFileName)),
                File.Exists(Path.Combine(dir, ExportSchema.RankFileName)),
                File.Exists(Path.Combine(dir, ExportSchema.DiagnosticsFileName))));
        }

        return accounts.OrderByDescending(a => a.CollectionExportedAt ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>
    /// Picks the account to use: the requested key, or the only account, or the most recently exported one when
    /// <paramref name="allowMostRecent"/>. Returns null (with a reason) when the choice is ambiguous.
    /// </summary>
    public static (InboxAccount? Account, string? Reason) Select(IReadOnlyList<InboxAccount> accounts, string? requestedKey, bool allowMostRecent = true)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Count == 0)
        {
            return (null, "No exported collection found. Install the HDT exporter plugin or pass --collection <file>.");
        }

        if (requestedKey is not null)
        {
            var match = accounts.FirstOrDefault(a => string.Equals(a.AccountKey, requestedKey, StringComparison.Ordinal)
                                                     || string.Equals(a.BattleTag, requestedKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // "Facu" for "Facu#1234", or the first digits of a key: accepted when it identifies exactly one account.
                var partial = accounts.Where(a => (a.BattleTag is not null && a.BattleTag.StartsWith(requestedKey, StringComparison.OrdinalIgnoreCase))
                                                  || a.AccountKey.StartsWith(requestedKey, StringComparison.Ordinal)).ToList();
                if (partial.Count == 1)
                {
                    match = partial[0];
                }
                else if (partial.Count > 1)
                {
                    return (null, $"Account '{requestedKey}' is ambiguous: {string.Join(", ", partial.Select(a => a.BattleTag ?? a.AccountKey))}.");
                }
            }

            return match is null
                ? (null, $"Account '{requestedKey}' not found. Known: {string.Join(", ", accounts.Select(a => a.BattleTag ?? a.AccountKey))}.")
                : (match, null);
        }

        if (accounts.Count == 1 || allowMostRecent)
        {
            return (accounts[0], null);
        }

        return (null, "Several accounts found; pass --account <key or BattleTag>: " + string.Join(", ", accounts.Select(a => a.BattleTag ?? a.AccountKey)));
    }
}
