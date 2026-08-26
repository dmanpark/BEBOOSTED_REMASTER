-- 0010: unify user-created items on Tasks.
-- Every legacy local "fixed commitment" (kind 0, provider 'local', no task) becomes a
-- Task plus a task-backed schedule session (kind 1). The Task takes over the title and
-- the project link; a completed one-off's completion moves onto the Task itself, while
-- recurring per-date completion stays in the (renamed) occurrence_completions table.
-- External synced events are untouched. Re-running is safe: the version guard skips the
-- script, and the task_id IS NULL filter makes the conversion itself a no-op on rerun.

CREATE TEMP TABLE _commitment_tasks AS
SELECT
    b.id AS block_id,
    lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2))
          || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))) AS task_id
FROM calendar_blocks b
WHERE b.kind = 0 AND b.provider = 'local' AND b.task_id IS NULL;

INSERT INTO tasks (id, title, project_id, origin, is_completed, completed_at, created_at, modified_at)
SELECT
    ct.task_id,
    COALESCE(b.title, '(untitled)'),
    b.project_id,
    0,
    CASE WHEN b.recurrence IS NULL AND cc.completed_at IS NOT NULL THEN 1 ELSE 0 END,
    CASE WHEN b.recurrence IS NULL THEN cc.completed_at ELSE NULL END,
    b.created_at,
    b.modified_at
FROM _commitment_tasks ct
JOIN calendar_blocks b ON b.id = ct.block_id
LEFT JOIN commitment_completions cc
    ON cc.block_id = b.id AND cc.occurrence_date = b.date;

-- A one-off's completion now lives on its Task; drop the rows so a later reopen can
-- never be resurrected by a stale occurrence record.
DELETE FROM commitment_completions
WHERE block_id IN (
    SELECT ct.block_id
    FROM _commitment_tasks ct
    JOIN calendar_blocks b ON b.id = ct.block_id
    WHERE b.recurrence IS NULL);

UPDATE calendar_blocks
SET task_id = (SELECT ct.task_id FROM _commitment_tasks ct WHERE ct.block_id = calendar_blocks.id),
    kind = 1,
    title = NULL,
    project_id = NULL
WHERE id IN (SELECT block_id FROM _commitment_tasks);

-- Defensive: no local block may remain a legacy commitment kind.
UPDATE calendar_blocks SET kind = 1 WHERE provider = 'local' AND kind = 0;

DROP TABLE _commitment_tasks;

-- Completion records apply per occurrence of any repeating schedule, not just
-- "commitments" — rename to match the unified model.
ALTER TABLE commitment_completions RENAME TO occurrence_completions;
DROP INDEX IF EXISTS idx_commitment_completions_date;
CREATE INDEX idx_occurrence_completions_date ON occurrence_completions (occurrence_date);
