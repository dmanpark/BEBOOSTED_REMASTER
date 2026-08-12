CREATE TABLE projects (
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    accent_color TEXT NOT NULL,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE TABLE project_files (
    id TEXT PRIMARY KEY NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    description TEXT,
    created_at TEXT NOT NULL,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_project_files_project ON project_files (project_id);

CREATE TABLE resources (
    id TEXT PRIMARY KEY NOT NULL,
    file_id TEXT NOT NULL REFERENCES project_files (id) ON DELETE CASCADE,
    kind INTEGER NOT NULL,
    title TEXT NOT NULL,
    url TEXT,
    content TEXT,
    original_file_name TEXT,
    stored_path TEXT,
    added_at TEXT NOT NULL,
    index_state INTEGER NOT NULL DEFAULT 0,
    index_text TEXT,
    modified_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_resources_file ON resources (file_id);
