CREATE TABLE calendar_blocks (
    id TEXT PRIMARY KEY NOT NULL,
    task_id TEXT REFERENCES tasks (id) ON DELETE CASCADE,
    title TEXT,
    date TEXT NOT NULL,
    start_time TEXT NOT NULL,
    end_time TEXT NOT NULL,
    kind INTEGER NOT NULL,
    recurrence TEXT,
    provider TEXT NOT NULL DEFAULT 'local',
    external_id TEXT,
    sync_state INTEGER NOT NULL DEFAULT 0,
    outcome INTEGER NOT NULL DEFAULT 0,
    outcome_recorded_at TEXT,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_blocks_date ON calendar_blocks (date);
CREATE INDEX idx_blocks_task ON calendar_blocks (task_id);
