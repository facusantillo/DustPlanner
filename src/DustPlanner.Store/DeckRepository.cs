using DustPlanner.Contracts;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
using Microsoft.Data.Sqlite;

namespace DustPlanner.Store;

public sealed record StoredDeck(long Id, string AccountKey, string Origin, string ExternalId, string Name, string? CardClass, int? FormatType, string? Deckstring, bool Archived, DateTimeOffset CapturedAt);

/// <summary>Saved decks (in-game, HDT, targets, manual, meta) and their cards.</summary>
public sealed class DeckRepository
{
    private readonly DustStore _store;

    public DeckRepository(DustStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Upserts every deck of the export (keyed by origin + external id). Returns the number of decks written.</summary>
    public int ImportExport(DecksExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var accountKey = export.Meta.Account.AccountKey;
        var count = 0;
        using var tx = _store.BeginTransaction();
        foreach (var deck in export.Decks)
        {
            var cards = deck.Cards.Where(c => c.DbfId > 0).Select(c => (c.DbfId, Owner: 0, c.Count))
                .Concat(deck.Sideboards.SelectMany(s => s.Cards.Where(c => c.DbfId > 0).Select(c => (c.DbfId, Owner: s.OwnerDbfId, c.Count))));
            Upsert(tx, accountKey, deck.Origin, deck.ExternalId, deck.Name, deck.CardClass, deck.FormatType, null, deck.Archived, export.Meta.ExportedAt, cards);
            count++;
        }

        tx.Commit();
        return count;
    }

    public long SaveDeckCode(string accountKey, DeckCode deck, string origin, string externalId, string name, string? cardClass, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(deck);
        using var tx = _store.BeginTransaction();
        var cards = deck.Cards.Select(c => (c.DbfId, Owner: 0, c.Count)).Concat(deck.Sideboards.Select(s => (s.DbfId, Owner: s.OwnerDbfId, s.Count)));
        var id = Upsert(tx, accountKey, origin, externalId, name, cardClass, (int)deck.Format, DeckstringCodec.Encode(deck), false, capturedAt, cards);
        tx.Commit();
        return id;
    }

    public IReadOnlyList<StoredDeck> List(string accountKey, bool includeArchived = false)
    {
        using var cmd = _store.Command(
            "SELECT id, account_key, origin, external_id, name, card_class, format_type, deckstring, archived, captured_at FROM deck WHERE account_key = $k AND ($all OR archived = 0) ORDER BY origin, name",
            ("$k", accountKey), ("$all", includeArchived));
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredDeck>();
        while (reader.Read())
        {
            list.Add(new StoredDeck(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8) != 0, DustStore.ParseIso(reader.GetString(9))));
        }

        return list;
    }

    /// <summary>Decks as the planner wants them (excluding targets/meta, which are not "saved decks").</summary>
    public IReadOnlyList<ProtectedDeck> ProtectedDecks(string accountKey, UserPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var result = new List<ProtectedDeck>();
        foreach (var deck in List(accountKey, includeArchived: !prefs.IgnoreArchivedDecks))
        {
            if (deck.Origin is Core.Planner.ProtectedDecks.TargetOrigin or "meta")
            {
                continue;
            }

            var copies = new Dictionary<int, int>();
            using var cmd = _store.Command("SELECT dbf_id, count FROM deck_card WHERE deck_id = $id", ("$id", deck.Id));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var dbfId = reader.GetInt32(0);
                copies[dbfId] = copies.GetValueOrDefault(dbfId) + reader.GetInt32(1);
            }

            var protectedDeck = new ProtectedDeck(deck.Name, deck.Origin, deck.ExternalId, copies);
            if (!Core.Planner.ProtectedDecks.IsExcluded(protectedDeck, prefs))
            {
                result.Add(protectedDeck);
            }
        }

        return result;
    }

    private long Upsert(SqliteTransaction tx, string accountKey, string origin, string externalId, string name, string? cardClass, int? formatType, string? deckstring,
        bool archived, DateTimeOffset capturedAt, IEnumerable<(int DbfId, int Owner, int Count)> cards)
    {
        using (var cmd = _store.Command(
                   """
                   INSERT INTO deck(account_key, origin, external_id, name, card_class, format_type, deckstring, archived, captured_at)
                   VALUES ($k, $o, $e, $n, $c, $f, $ds, $a, $t)
                   ON CONFLICT(account_key, origin, external_id) DO UPDATE SET
                       name = excluded.name, card_class = excluded.card_class, format_type = excluded.format_type,
                       deckstring = COALESCE(excluded.deckstring, deck.deckstring), archived = excluded.archived, captured_at = excluded.captured_at
                   """,
                   ("$k", accountKey), ("$o", origin), ("$e", externalId), ("$n", name), ("$c", cardClass), ("$f", formatType), ("$ds", deckstring), ("$a", archived ? 1 : 0), ("$t", DustStore.Iso(capturedAt))))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        long id;
        using (var cmd = _store.Command("SELECT id FROM deck WHERE account_key = $k AND origin = $o AND external_id = $e", ("$k", accountKey), ("$o", origin), ("$e", externalId)))
        {
            cmd.Transaction = tx;
            id = (long)cmd.ExecuteScalar()!;
        }

        using (var cmd = _store.Command("DELETE FROM deck_card WHERE deck_id = $id", ("$id", id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _store.Command("INSERT OR REPLACE INTO deck_card(deck_id, dbf_id, sideboard_owner_dbf_id, count) VALUES ($id, $d, $o, $c)"))
        {
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$id", id);
            var pD = cmd.Parameters.AddWithValue("$d", 0);
            var pO = cmd.Parameters.AddWithValue("$o", 0);
            var pC = cmd.Parameters.AddWithValue("$c", 0);
            foreach (var group in cards.GroupBy(c => (c.DbfId, c.Owner)))
            {
                pD.Value = group.Key.DbfId;
                pO.Value = group.Key.Owner;
                pC.Value = group.Sum(c => c.Count);
                cmd.ExecuteNonQuery();
            }
        }

        return id;
    }
}
