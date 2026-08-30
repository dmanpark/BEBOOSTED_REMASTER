-- Persisted, claimable folder identity for Projects and Files. ResourceLayout.FolderFor
-- currently derives a folder purely from the sanitized name/title with no
-- disambiguation, so two entities whose names sanitize identically can collide on one
-- physical directory. The empty string means "not yet claimed" — Task 7's backfill
-- looks for it. DEFAULT '' keeps the column non-null without running the sanitizing
-- logic, which lives in C# and cannot run in SQL.
ALTER TABLE projects ADD COLUMN folder_segment TEXT NOT NULL DEFAULT '';

ALTER TABLE project_files ADD COLUMN folder_segment TEXT NOT NULL DEFAULT '';
