-- 0011: proposal-integrity backstop.
-- proposed_blocks gains a cascading foreign key to tasks so deleting a Task can
-- never leave proposal rows referencing it. SQLite cannot add a foreign key in
-- place, so the table is rebuilt: legacy orphan rows (their task no longer
-- exists) are dropped instead of failing the migration, every other row keeps
-- all of its columns and statuses, and drafts left with nothing pending are
-- discarded so no empty active draft survives the upgrade. The application
-- reconciles proposals during deletion itself — this constraint is the backstop.

CREATE TABLE proposed_blocks_new (
    id TEXT PRIMARY KEY NOT NULL,
    proposal_id TEXT NOT NULL REFERENCES planning_proposals (id) ON DELETE CASCADE,
    task_id TEXT NOT NULL REFERENCES tasks (id) ON DELETE CASCADE,
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

INSERT INTO proposed_blocks_new
    (id, proposal_id, task_id, date, start_time, end_time, status, session_label,
     why_deadline, why_duration, why_priority, why_availability, why_source)
SELECT
    id, proposal_id, task_id, date, start_time, end_time, status, session_label,
    why_deadline, why_duration, why_priority, why_availability, why_source
FROM proposed_blocks
WHERE task_id IN (SELECT id FROM tasks);

DROP TABLE proposed_blocks;
ALTER TABLE proposed_blocks_new RENAME TO proposed_blocks;
CREATE INDEX idx_proposed_blocks_proposal ON proposed_blocks (proposal_id);

-- A draft whose pending blocks were all orphans must not survive as an empty
-- active draft: discard it (drafts with approved blocks left become approved).
UPDATE planning_proposals
SET state = CASE
        WHEN EXISTS (
            SELECT 1 FROM proposed_blocks pb
            WHERE pb.proposal_id = planning_proposals.id AND pb.status = 1)
        THEN 1
        ELSE 2
    END
WHERE state = 0
  AND NOT EXISTS (
        SELECT 1 FROM proposed_blocks pb
        WHERE pb.proposal_id = planning_proposals.id AND pb.status = 0);
