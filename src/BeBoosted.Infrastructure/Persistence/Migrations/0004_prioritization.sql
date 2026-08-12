CREATE TABLE comparisons (
    id TEXT PRIMARY KEY NOT NULL,
    period_key TEXT NOT NULL,
    left_task_id TEXT NOT NULL,
    right_task_id TEXT NOT NULL,
    result INTEGER NOT NULL,
    decided_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_comparisons_period ON comparisons (period_key);

CREATE TABLE priority_ranks (
    period_key TEXT NOT NULL,
    task_id TEXT NOT NULL,
    rank INTEGER NOT NULL,
    tier INTEGER NOT NULL,
    PRIMARY KEY (period_key, task_id)
) STRICT;
