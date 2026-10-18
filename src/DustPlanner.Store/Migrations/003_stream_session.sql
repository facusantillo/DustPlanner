-- Kick stage K7: one row per stream, opened by the relay's stream.status (is_live: true) and closed by the next
-- is_live: false. Games are not linked by id: a session's games are the ones started inside its time range, so the
-- record and the decks used are computed on read (StreamSessionRepository). Rank at both ends is copied from the
-- latest rank_sample of the time, as a label and as the fields the chat texts need.
CREATE TABLE stream_session (
    id                     INTEGER PRIMARY KEY AUTOINCREMENT,
    account_key            TEXT NOT NULL,
    platform               TEXT NOT NULL DEFAULT 'kick',
    channel_slug           TEXT NOT NULL,
    started_at             TEXT NOT NULL,            -- UTC ISO, from Kick
    ended_at               TEXT,                     -- NULL while live (or when the end was never seen)
    title                  TEXT,
    rank_start             TEXT,                     -- RankRepository.Describe at the start, e.g. "Diamond 3 (5 stars)"
    rank_start_format      TEXT,
    rank_start_league      INTEGER,
    rank_start_star_level  INTEGER,
    rank_start_legend      INTEGER,
    rank_end               TEXT,
    rank_end_format        TEXT,
    rank_end_league        INTEGER,
    rank_end_star_level    INTEGER,
    rank_end_legend        INTEGER,
    summary_posted_at      TEXT,                     -- when the end-of-stream line went to chat (NULL = not posted)
    UNIQUE (account_key, platform, started_at)
);
CREATE INDEX ix_stream_session_account_started ON stream_session(account_key, started_at);
