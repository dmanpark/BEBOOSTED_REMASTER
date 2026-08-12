CREATE TABLE tasks (
    id TEXT PRIMARY KEY NOT NULL,
    title TEXT NOT NULL,
    estimated_minutes INTEGER,
    deadline TEXT,
    project_id TEXT,
    not_before TEXT,
    earliest_time TEXT,
    latest_time TEXT,
    recurrence TEXT,
    origin INTEGER NOT NULL DEFAULT 0,
    provenance_id TEXT,
    is_completed INTEGER NOT NULL DEFAULT 0,
    completed_at TEXT,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_tasks_open ON tasks (is_completed, created_at);
