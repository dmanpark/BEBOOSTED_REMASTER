-- One level of containers inside a File. A group is a row of its own, mirroring how
-- project_files relates to projects, so it can be renamed, ordered, and exist while
-- empty. folder_segment carries the group's claimed on-disk directory name: resolved
-- once at create and re-resolved on rename, never derived from the title at read time.
--
-- Two delete rules, deliberately different:
--   resource_groups.file_id CASCADE  — deleting a File takes its groups with it.
--   resources.group_id      SET NULL — deleting a group alone leaves its members alive
--                                      and loose. That is what makes Ungroup safe;
--                                      destructive "Delete group" removes the members
--                                      explicitly, so the database never has to.
--
-- Additive only: no table rebuild, no default groups, no backfill. Existing resources
-- get group_id = NULL and are therefore loose, so every File renders as it does today.
CREATE TABLE resource_groups (
    id TEXT PRIMARY KEY NOT NULL,
    file_id TEXT NOT NULL REFERENCES project_files (id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    folder_segment TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_resource_groups_file ON resource_groups (file_id);

ALTER TABLE resources ADD COLUMN group_id TEXT
    REFERENCES resource_groups (id) ON DELETE SET NULL;

CREATE INDEX idx_resources_group ON resources (group_id);
