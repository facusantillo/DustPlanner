using System.Text.Json;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;

namespace DustPlanner.Store;

public static class PlanStatus
{
    public const string Proposed = "proposed";
    public const string Partial = "partial";
    public const string Confirmed = "confirmed";
    public const string Discarded = "discarded";
}

public sealed record StoredPlan(long Id, string AccountKey, DateTimeOffset CreatedAt, string? TargetDeckName, string TargetDeckstring, int Need, int TotalDust, int TotalPain, string Status);

public sealed record StoredPick(long PlanId, int DbfId, PremiumKind Kind, int Copies, int DustEach, int PainEach, int MatchedCopies);

/// <summary>Proposed disenchant plans and their picks, so later snapshots can be matched against them.</summary>
public sealed class PlanRepository
{
    private readonly DustStore _store;

    public PlanRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public long Save(string accountKey, DeckCode targetDeck, DisenchantPlan plan, object payload, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(targetDeck);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Main is not { Feasible: true } main)
        {
            throw new InvalidOperationException("Only feasible plans are stored.");
        }

        using var tx = _store.BeginTransaction();
        using (var cmd = _store.Command(
                   """
                   INSERT INTO disenchant_plan(account_key, created_at, target_deck_name, target_deckstring, need, total_dust, total_pain, status, payload_json)
                   VALUES ($k, $t, $n, $ds, $need, $dust, $pain, $status, $json)
                   """,
                   ("$k", accountKey), ("$t", DustStore.Iso(createdAt)), ("$n", targetDeck.Name), ("$ds", DeckstringCodec.Encode(targetDeck)),
                   ("$need", plan.Need), ("$dust", main.TotalDust), ("$pain", main.TotalPain), ("$status", PlanStatus.Proposed),
                   ("$json", JsonSerializer.Serialize(payload, CoreJson.Options))))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        var id = (long)_store.Scalar("SELECT last_insert_rowid()")!;
        using (var cmd = _store.Command("INSERT INTO disenchant_plan_pick(plan_id, dbf_id, premium, copies, dust_each, pain_each) VALUES ($p, $d, $k, $c, $dust, $pain)"))
        {
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$p", id);
            var pD = cmd.Parameters.AddWithValue("$d", 0);
            var pK = cmd.Parameters.AddWithValue("$k", "");
            var pC = cmd.Parameters.AddWithValue("$c", 0);
            var pDust = cmd.Parameters.AddWithValue("$dust", 0);
            var pPain = cmd.Parameters.AddWithValue("$pain", 0);
            foreach (var pick in main.Picks)
            {
                pD.Value = pick.Group.Card.DbfId;
                pK.Value = KindName(pick.Group.Kind);
                pC.Value = pick.Copies;
                pDust.Value = pick.Group.DustEach;
                pPain.Value = pick.Group.PainEach;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return id;
    }

    public IReadOnlyList<StoredPlan> List(string accountKey, int limit = 20)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, created_at, target_deck_name, target_deckstring, need, total_dust, total_pain, status FROM disenchant_plan WHERE account_key = $k ORDER BY created_at DESC, id DESC LIMIT $n",
            ("$k", accountKey), ("$n", limit));
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredPlan>();
        while (reader.Read())
        {
            list.Add(new StoredPlan(reader.GetInt64(0), reader.GetString(1), DustStore.ParseIso(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8)));
        }

        return list;
    }

    /// <summary>Plans still open (proposed or partial), newest first, created at most <paramref name="maxAge"/> ago.</summary>
    public IReadOnlyList<StoredPlan> Open(string accountKey, DateTimeOffset now, TimeSpan maxAge) =>
        List(accountKey, 100).Where(p => (p.Status == PlanStatus.Proposed || p.Status == PlanStatus.Partial) && now - p.CreatedAt <= maxAge).ToList();

    public IReadOnlyList<StoredPick> Picks(long planId)
    {
        using var cmd = _store.Command("SELECT plan_id, dbf_id, premium, copies, dust_each, pain_each, matched_copies FROM disenchant_plan_pick WHERE plan_id = $p", ("$p", planId));
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredPick>();
        while (reader.Read())
        {
            list.Add(new StoredPick(reader.GetInt64(0), reader.GetInt32(1), ParseKind(reader.GetString(2)), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        }

        return list;
    }

    public void RecordMatch(long planId, int dbfId, PremiumKind kind, int matchedCopies)
    {
        _store.Execute("UPDATE disenchant_plan_pick SET matched_copies = MIN(copies, matched_copies + $m) WHERE plan_id = $p AND dbf_id = $d AND premium = $k",
            ("$m", matchedCopies), ("$p", planId), ("$d", dbfId), ("$k", KindName(kind)));
        var picks = Picks(planId);
        var status = picks.All(p => p.MatchedCopies >= p.Copies) ? PlanStatus.Confirmed
            : picks.Any(p => p.MatchedCopies > 0) ? PlanStatus.Partial
            : PlanStatus.Proposed;
        _store.Execute("UPDATE disenchant_plan SET status = $s WHERE id = $p", ("$s", status), ("$p", planId));
    }

    public void SetStatus(long planId, string status) =>
        _store.Execute("UPDATE disenchant_plan SET status = $s WHERE id = $p", ("$s", status), ("$p", planId));

    public static string KindName(PremiumKind kind) => kind.ToString().ToLowerInvariant();

    public static PremiumKind ParseKind(string name) => Enum.TryParse<PremiumKind>(name, ignoreCase: true, out var kind) ? kind : PremiumKind.Unknown;
}
