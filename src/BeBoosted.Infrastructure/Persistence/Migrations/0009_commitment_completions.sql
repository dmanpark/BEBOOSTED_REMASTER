-- Per-occurrence completion for local fixed commitments. One row per checked-off
-- occurrence; reopening deletes the row. Deleting a block removes its completions.
CREATE TABLE commitment_completions (
    block_id TEXT NOT NULL REFERENCES calendar_blocks (id) ON DELETE CASCADE,
    occurrence_date TEXT NOT NULL,
    completed_at TEXT NOT NULL,
    PRIMARY KEY (block_id, occurrence_date)
) STRICT;

CREATE INDEX idx_commitment_completions_date ON commitment_completions (occurrence_date);
