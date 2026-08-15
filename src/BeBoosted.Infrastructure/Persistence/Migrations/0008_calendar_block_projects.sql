-- Optional direct project link for fixed commitments. Deleting a project keeps the
-- commitment and clears the link. NULL default keeps existing rows valid unchanged.
ALTER TABLE calendar_blocks
    ADD COLUMN project_id TEXT REFERENCES projects (id) ON DELETE SET NULL;

CREATE INDEX idx_blocks_project ON calendar_blocks (project_id);
