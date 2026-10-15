-- DustPlanner schema v1. Every table carries account_key so several Battle.net accounts/regions coexist.
-- Card references are dbfId (HearthstoneJSON / deckstrings); premium kinds are the PremiumKind names in lower case.

CREATE TABLE account (
    account_key   TEXT PRIMARY KEY,
    battle_tag    TEXT,
    region        TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at  TEXT NOT NULL
);

CREATE TABLE collection_snapshot (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL REFERENCES account(account_key),
    taken_at      TEXT NOT NULL,
    imported_at   TEXT NOT NULL,
    source        TEXT NOT NULL,
    dust          INTEGER NOT NULL,
    gold          INTEGER,
    unopened_packs INTEGER,
    content_hash  TEXT NOT NULL,
    UNIQUE (account_key, content_hash)
);
CREATE INDEX ix_snapshot_account_taken ON collection_snapshot(account_key, taken_at);

CREATE TABLE collection_entry (
    snapshot_id   INTEGER NOT NULL REFERENCES collection_snapshot(id) ON DELETE CASCADE,
    dbf_id        INTEGER NOT NULL,
    normal        INTEGER NOT NULL DEFAULT 0,
    golden        INTEGER NOT NULL DEFAULT 0,
    signature     INTEGER NOT NULL DEFAULT 0,
    diamond       INTEGER NOT NULL DEFAULT 0,
    unknown       INTEGER NOT NULL DEFAULT 0,
    raw_counts    TEXT NOT NULL,
    PRIMARY KEY (snapshot_id, dbf_id)
);

CREATE TABLE deck (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL REFERENCES account(account_key),
    origin        TEXT NOT NULL,          -- ingame | hdt | target | manual | meta
    external_id   TEXT NOT NULL,
    name          TEXT NOT NULL,
    card_class    TEXT,
    format_type   INTEGER,
    deckstring    TEXT,
    archived      INTEGER NOT NULL DEFAULT 0,
    captured_at   TEXT NOT NULL,
    UNIQUE (account_key, origin, external_id)
);

CREATE TABLE deck_card (
    deck_id       INTEGER NOT NULL REFERENCES deck(id) ON DELETE CASCADE,
    dbf_id        INTEGER NOT NULL,
    sideboard_owner_dbf_id INTEGER NOT NULL DEFAULT 0,
    count         INTEGER NOT NULL,
    PRIMARY KEY (deck_id, dbf_id, sideboard_owner_dbf_id)
);

CREATE TABLE disenchant_plan (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL REFERENCES account(account_key),
    created_at    TEXT NOT NULL,
    target_deck_name TEXT,
    target_deckstring TEXT NOT NULL,
    need          INTEGER NOT NULL,
    total_dust    INTEGER NOT NULL,
    total_pain    INTEGER NOT NULL,
    status        TEXT NOT NULL,          -- proposed | partial | confirmed | discarded
    payload_json  TEXT NOT NULL
);

CREATE TABLE disenchant_plan_pick (
    plan_id       INTEGER NOT NULL REFERENCES disenchant_plan(id) ON DELETE CASCADE,
    dbf_id        INTEGER NOT NULL,
    premium       TEXT NOT NULL,
    copies        INTEGER NOT NULL,
    dust_each     INTEGER NOT NULL,
    pain_each     INTEGER NOT NULL,
    matched_copies INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (plan_id, dbf_id, premium)
);

CREATE TABLE collection_event (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL REFERENCES account(account_key),
    dbf_id        INTEGER NOT NULL,
    premium       TEXT NOT NULL,
    delta         INTEGER NOT NULL,       -- negative = copies gone (disenchanted), positive = copies gained
    kind          TEXT NOT NULL,          -- disenchanted | gained
    dust_estimate INTEGER,                -- dust the disenchant should have returned, by the rules at detection time
    detected_at   TEXT NOT NULL,
    from_snapshot_id INTEGER NOT NULL REFERENCES collection_snapshot(id),
    to_snapshot_id   INTEGER NOT NULL REFERENCES collection_snapshot(id),
    plan_id       INTEGER REFERENCES disenchant_plan(id),
    confidence    TEXT NOT NULL           -- high | medium | low
);
CREATE INDEX ix_event_account_detected ON collection_event(account_key, detected_at);

CREATE TABLE rank_sample (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL REFERENCES account(account_key),
    taken_at      TEXT NOT NULL,
    format        TEXT NOT NULL,          -- standard | wild | classic | twist
    league_id     INTEGER NOT NULL,
    star_level    INTEGER NOT NULL,
    stars         INTEGER NOT NULL,
    legend_rank   INTEGER NOT NULL,
    season_id     INTEGER NOT NULL,
    season_games  INTEGER NOT NULL,
    season_wins   INTEGER NOT NULL,
    UNIQUE (account_key, taken_at, format)
);

-- Filled by the Power.log tracker (stage 6).
CREATE TABLE game (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL,
    game_key      TEXT NOT NULL UNIQUE,   -- stable id derived from the log (start time + player entity ids)
    started_at    TEXT NOT NULL,
    ended_at      TEXT,
    game_type     TEXT,
    format        TEXT,
    player_class  TEXT,
    opponent_class TEXT,
    player_hero_card_id TEXT,
    opponent_hero_card_id TEXT,
    opponent_name TEXT,
    result        TEXT,                   -- win | loss | tie | unknown
    went_first    INTEGER,
    turns         INTEGER,
    deck_id       INTEGER REFERENCES deck(id),
    source        TEXT NOT NULL DEFAULT 'powerlog'
);
CREATE INDEX ix_game_account_started ON game(account_key, started_at);
