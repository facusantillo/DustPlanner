-- Kick stage K1: samples of HDT's active deck, as exported to active-deck.json. Short history (30 days, pruned on
-- insert); games are linked to the sample taken around their start (see ActiveDeckRepository.LinkGame).
CREATE TABLE active_deck (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key   TEXT NOT NULL,
    taken_at      TEXT NOT NULL,            -- meta.exported_at (UTC ISO)
    hdt_deck_id   TEXT,                     -- HDT deck GUID; matches deck.external_id for origin = 'hdt'
    name          TEXT NOT NULL,
    deckstring    TEXT NOT NULL,
    card_class    TEXT,                     -- upper case, like game.player_class
    format        TEXT,                     -- standard | wild | classic | twist
    reason        TEXT NOT NULL,            -- meta.trigger: game_start | active_deck_changed | mode_changed:<MODE> | manual
    UNIQUE (account_key, taken_at)
);
CREATE INDEX ix_active_deck_account_taken ON active_deck(account_key, taken_at);
