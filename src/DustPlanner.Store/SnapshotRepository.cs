using System.Globalization;
using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Json;
using DustPlanner.Core.Rules;
using Microsoft.Data.Sqlite;

namespace DustPlanner.Store;

public sealed record StoredSnapshot(long Id, string AccountKey, DateTimeOffset TakenAt, string Source, int Dust, int? Gold, string ContentHash);

/// <summary>Collection snapshots and accounts.</summary>
public sealed class SnapshotRepository
{
    private readonly DustStore _store;

    public SnapshotRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void UpsertAccount(string accountKey, string? battleTag, string? region, DateTimeOffset seenAt)
    {
        _store.Execute(
            """
            INSERT INTO account(account_key, battle_tag, region, first_seen_at, last_seen_at)
            VALUES ($k, $b, $r, $t, $t)
            ON CONFLICT(account_key) DO UPDATE SET
                battle_tag = COALESCE(excluded.battle_tag, account.battle_tag),
                region = COALESCE(excluded.region, account.region),
                last_seen_at = MAX(account.last_seen_at, excluded.last_seen_at)
            """,
            ("$k", accountKey), ("$b", battleTag), ("$r", region), ("$t", DustStore.Iso(seenAt)));
    }

    /// <summary>Stores the snapshot unless an identical one (same content hash) already exists for the account. Returns the stored row and whether it was new.</summary>
    public (StoredSnapshot Snapshot, bool IsNew) Import(CollectionSnapshot collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        UpsertAccount(collection.AccountKey, collection.BattleTag, collection.Region, collection.TakenAt);
        var hash = ContentHash(collection);

        var existing = Find(collection.AccountKey, hash);
        if (existing is not null)
        {
            return (existing, false);
        }

        using var tx = _store.BeginTransaction();
        using (var cmd = _store.Command(
                   """
                   INSERT INTO collection_snapshot(account_key, taken_at, imported_at, source, dust, gold, unopened_packs, content_hash)
                   VALUES ($k, $taken, $imported, $source, $dust, $gold, $packs, $hash)
                   """,
                   ("$k", collection.AccountKey), ("$taken", DustStore.Iso(collection.TakenAt)), ("$imported", DustStore.Iso(DateTimeOffset.UtcNow)),
                   ("$source", collection.Source), ("$dust", collection.Dust), ("$gold", collection.Gold), ("$packs", collection.UnopenedPacks), ("$hash", hash)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        var id = (long)_store.Scalar("SELECT last_insert_rowid()")!;
        using (var cmd = _store.Command(
                   """
                   INSERT INTO collection_entry(snapshot_id, dbf_id, normal, golden, signature, diamond, unknown, raw_counts)
                   VALUES ($s, $d, $n, $g, $sig, $dia, $u, $raw)
                   """))
        {
            cmd.Transaction = tx;
            var pS = cmd.Parameters.AddWithValue("$s", id);
            var pD = cmd.Parameters.AddWithValue("$d", 0);
            var pN = cmd.Parameters.AddWithValue("$n", 0);
            var pG = cmd.Parameters.AddWithValue("$g", 0);
            var pSig = cmd.Parameters.AddWithValue("$sig", 0);
            var pDia = cmd.Parameters.AddWithValue("$dia", 0);
            var pU = cmd.Parameters.AddWithValue("$u", 0);
            var pRaw = cmd.Parameters.AddWithValue("$raw", "");
            foreach (var card in collection.Cards.OrderBy(c => c.DbfId))
            {
                pD.Value = card.DbfId;
                pN.Value = card.Count(PremiumKind.Normal);
                pG.Value = card.Count(PremiumKind.Golden);
                pSig.Value = card.Count(PremiumKind.Signature);
                pDia.Value = card.Count(PremiumKind.Diamond);
                pU.Value = card.Count(PremiumKind.Unknown);
                pRaw.Value = JsonSerializer.Serialize(card.RawCounts);
                cmd.ExecuteNonQuery();
            }

            _ = pS;
        }

        tx.Commit();
        return (new StoredSnapshot(id, collection.AccountKey, collection.TakenAt, collection.Source, collection.Dust, collection.Gold, hash), true);
    }

    public StoredSnapshot? Find(string accountKey, string contentHash)
    {
        using var cmd = _store.Command("SELECT id, account_key, taken_at, source, dust, gold, content_hash FROM collection_snapshot WHERE account_key = $k AND content_hash = $h",
            ("$k", accountKey), ("$h", contentHash));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public StoredSnapshot? Latest(string accountKey, long? beforeId = null)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, taken_at, source, dust, gold, content_hash FROM collection_snapshot WHERE account_key = $k AND ($before IS NULL OR id < $before) ORDER BY taken_at DESC, id DESC LIMIT 1",
            ("$k", accountKey), ("$before", beforeId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<StoredSnapshot> List(string accountKey, int limit = 50)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, taken_at, source, dust, gold, content_hash FROM collection_snapshot WHERE account_key = $k ORDER BY taken_at DESC, id DESC LIMIT $n",
            ("$k", accountKey), ("$n", limit));
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredSnapshot>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    }

    public IReadOnlyList<string> Accounts()
    {
        using var cmd = _store.Command("SELECT account_key FROM account ORDER BY last_seen_at DESC");
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Rebuilds a <see cref="CollectionSnapshot"/> from a stored row (kinds are stored explicitly, so no slot map is needed).</summary>
    public CollectionSnapshot Load(long snapshotId)
    {
        StoredSnapshot? meta;
        using (var cmd = _store.Command("SELECT id, account_key, taken_at, source, dust, gold, content_hash FROM collection_snapshot WHERE id = $id", ("$id", snapshotId)))
        using (var reader = cmd.ExecuteReader())
        {
            meta = reader.Read() ? Read(reader) : null;
        }

        if (meta is null)
        {
            throw new KeyNotFoundException($"Snapshot {snapshotId} not found.");
        }

        var cards = new List<OwnedCard>();
        using (var cmd = _store.Command("SELECT dbf_id, normal, golden, signature, diamond, unknown, raw_counts FROM collection_entry WHERE snapshot_id = $id", ("$id", snapshotId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var copies = new Dictionary<PremiumKind, int>();
                Add(copies, PremiumKind.Normal, reader.GetInt32(1));
                Add(copies, PremiumKind.Golden, reader.GetInt32(2));
                Add(copies, PremiumKind.Signature, reader.GetInt32(3));
                Add(copies, PremiumKind.Diamond, reader.GetInt32(4));
                Add(copies, PremiumKind.Unknown, reader.GetInt32(5));
                var raw = JsonSerializer.Deserialize<int[]>(reader.GetString(6)) ?? new int[ExportSchema.CountArrayLength];
                cards.Add(new OwnedCard(reader.GetInt32(0), copies, raw));
            }
        }

        string? battleTag = null;
        string? region = null;
        using (var cmd = _store.Command("SELECT battle_tag, region FROM account WHERE account_key = $k", ("$k", meta.AccountKey)))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                battleTag = reader.IsDBNull(0) ? null : reader.GetString(0);
                region = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        return new CollectionSnapshot(meta.AccountKey, meta.TakenAt, meta.Source, meta.Dust, cards, meta.Gold, null, battleTag, region);
    }

    /// <summary>Hash over account-independent content: dust and per-card counts per kind.</summary>
    public static string ContentHash(CollectionSnapshot collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var sb = new System.Text.StringBuilder();
        sb.Append(collection.Dust.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (var card in collection.Cards.OrderBy(c => c.DbfId))
        {
            sb.Append(card.DbfId.ToString(CultureInfo.InvariantCulture)).Append(':');
            foreach (var kind in Enum.GetValues<PremiumKind>())
            {
                sb.Append(card.Count(kind).ToString(CultureInfo.InvariantCulture)).Append(',');
            }

            sb.Append(';');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void Add(Dictionary<PremiumKind, int> copies, PremiumKind kind, int count)
    {
        if (count > 0)
        {
            copies[kind] = count;
        }
    }

    private static StoredSnapshot Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), DustStore.ParseIso(reader.GetString(2)), reader.GetString(3), reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetString(6));
}
