CREATE TABLE planning_proposals (
    id TEXT PRIMARY KEY NOT NULL,
    period_key TEXT NOT NULL,
    state INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_proposals_state ON planning_proposals (state);

CREATE TABLE proposed_blocks (
    id TEXT PRIMARY KEY NOT NULL,
    proposal_id TEXT NOT NULL REFERENCES planning_proposals (id) ON DELETE CASCADE,
    task_id TEXT NOT NULL,
    date TEXT NOT NULL,
    start_time TEXT NOT NULL,
    end_time TEXT NOT NULL,
    status INTEGER NOT NULL DEFAULT 0,
    session_label TEXT,
    why_deadline TEXT,
    why_duration TEXT NOT NULL,
    why_priority TEXT,
    why_availability TEXT NOT NULL,
    why_source TEXT
) STRICT;

CREATE INDEX idx_proposed_blocks_proposal ON proposed_blocks (proposal_id);
