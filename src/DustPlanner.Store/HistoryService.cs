using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;

namespace DustPlanner.Store;

public static class EventKinds
{
    public const string Disenchanted = "disenchanted";
    public const string Gained = "gained";
}

public static class Confidence
{
    /// <summary>Copies vanished exactly as a proposed plan said.</summary>
    public const string High = "high";

    /// <summary>Copies vanished for a card that a proposed plan mentions, but not the same number.</summary>
    public const string Medium = "medium";

    /// <summary>No plan mentions the card: could be a disenchant, a trade-in or an HDT read glitch.</summary>
    public const string Low = "low";
}

public sealed record CollectionEvent(long Id, string AccountKey, int DbfId, PremiumKind Kind, int Delta, string EventKind, int? DustEstimate, DateTimeOffset DetectedAt,
    long FromSnapshotId, long ToSnapshotId, long? PlanId, string ConfidenceLevel);

public sealed record SyncResult(StoredSnapshot Snapshot, bool NewSnapshot, IReadOnlyList<CollectionEvent> Events, int DustDelta);

/// <summary>Detects collection changes between consecutive snapshots and matches them against proposed plans.</summary>
public sealed class HistoryService
{
    private readonly DustStore _store;
    private readonly SnapshotRepository _snapshots;
    private readonly PlanRepository _plans;

    public HistoryService(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = new SnapshotRepository(store);
        _plans = new PlanRepository(store);
    }

    public static TimeSpan PlanMatchWindow { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Imports the snapshot; when it is new and an older snapshot exists for the account, records the per-card deltas
    /// as events (negative = disenchanted, positive = gained) and links disenchants to open plans.
    /// </summary>
    public SyncResult Sync(CollectionSnapshot collection, CardCatalog catalog, GameRules rules, Refunds refunds, DateOnly asOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(refunds);

        var previous = _snapshots.Latest(collection.AccountKey);
        var (stored, isNew) = _snapshots.Import(collection);
        if (!isNew || previous is null || previous.TakenAt > collection.TakenAt)
        {
            return new SyncResult(stored, isNew, Array.Empty<CollectionEvent>(), previous is null ? 0 : collection.Dust - previous.Dust);
        }

        var before = _snapshots.Load(previous.Id);
        var events = new List<CollectionEvent>();
        var openPlans = _plans.Open(collection.AccountKey, now, PlanMatchWindow);
        var openPicks = openPlans.ToDictionary(p => p.Id, p => _plans.Picks(p.Id));

        var dbfIds = before.Cards.Select(c => c.DbfId).Concat(collection.Cards.Select(c => c.DbfId)).Distinct();
        using var tx = _store.BeginTransaction();
        foreach (var dbfId in dbfIds.OrderBy(d => d))
        {
            foreach (var kind in Enum.GetValues<PremiumKind>())
            {
                var delta = collection.Copies(dbfId, kind) - before.Copies(dbfId, kind);
                if (delta == 0)
                {
                    continue;
                }

                long? planId = null;
                var confidence = Confidence.Low;
                int? dustEstimate = null;
                if (delta < 0)
                {
                    var gone = -delta;
                    foreach (var plan in openPlans)
                    {
                        var pick = openPicks[plan.Id].FirstOrDefault(p => p.DbfId == dbfId && p.Kind == kind && p.MatchedCopies < p.Copies);
                        if (pick is null)
                        {
                            continue;
                        }

                        planId = plan.Id;
                        confidence = gone == pick.Copies - pick.MatchedCopies ? Confidence.High : Confidence.Medium;
                        dustEstimate = pick.DustEach * Math.Min(gone, pick.Copies - pick.MatchedCopies);
                        _plans.RecordMatch(plan.Id, dbfId, kind, gone);
                        break;
                    }

                    if (dustEstimate is null && catalog.GetByDbfId(dbfId) is { } card && EstimateDust(card, kind, rules, refunds, asOf) is { } each)
                    {
                        dustEstimate = each * gone;
                    }
                }

                using var cmd = _store.Command(
                    """
                    INSERT INTO collection_event(account_key, dbf_id, premium, delta, kind, dust_estimate, detected_at, from_snapshot_id, to_snapshot_id, plan_id, confidence)
                    VALUES ($k, $d, $p, $delta, $kind, $dust, $t, $from, $to, $plan, $conf)
                    """,
                    ("$k", collection.AccountKey), ("$d", dbfId), ("$p", PlanRepository.KindName(kind)), ("$delta", delta),
                    ("$kind", delta < 0 ? EventKinds.Disenchanted : EventKinds.Gained), ("$dust", dustEstimate), ("$t", DustStore.Iso(now)),
                    ("$from", previous.Id), ("$to", stored.Id), ("$plan", planId), ("$conf", delta < 0 ? confidence : Confidence.Low));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
                var id = (long)_store.Scalar("SELECT last_insert_rowid()")!;
                events.Add(new CollectionEvent(id, collection.AccountKey, dbfId, kind, delta, delta < 0 ? EventKinds.Disenchanted : EventKinds.Gained, dustEstimate, now, previous.Id, stored.Id, planId, delta < 0 ? confidence : Confidence.Low));
            }
        }

        tx.Commit();
        return new SyncResult(stored, true, events, collection.Dust - previous.Dust);
    }

    public IReadOnlyList<CollectionEvent> Events(string accountKey, int limit = 100)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, dbf_id, premium, delta, kind, dust_estimate, detected_at, from_snapshot_id, to_snapshot_id, plan_id, confidence FROM collection_event WHERE account_key = $k ORDER BY detected_at DESC, id DESC LIMIT $n",
            ("$k", accountKey), ("$n", limit));
        using var reader = cmd.ExecuteReader();
        var list = new List<CollectionEvent>();
        while (reader.Read())
        {
            list.Add(new CollectionEvent(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), PlanRepository.ParseKind(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6), DustStore.ParseIso(reader.GetString(7)), reader.GetInt64(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.GetString(11)));
        }

        return list;
    }

    /// <summary>Dust one disenchanted copy should have returned, by the rules at detection time.</summary>
    public static int? EstimateDust(CardDef card, PremiumKind kind, GameRules rules, Refunds refunds, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(refunds);
        if (!rules.Craftability.IsCraftable(card, kind))
        {
            return null;
        }

        return refunds.Active(card.DbfId, kind, asOf) is null ? rules.Dust.DisenchantValue(card.Rarity, kind) : rules.Dust.RefundValue(card.Rarity, kind);
    }
}
