CREATE TABLE ai_provenance (
    id TEXT PRIMARY KEY NOT NULL,
    operation INTEGER NOT NULL,
    needs_review INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
) STRICT;

CREATE TABLE ai_provenance_sources (
    provenance_id TEXT NOT NULL REFERENCES ai_provenance (id) ON DELETE CASCADE,
    resource_id TEXT NOT NULL,
    PRIMARY KEY (provenance_id, resource_id)
) STRICT;

CREATE INDEX idx_provenance_sources_resource ON ai_provenance_sources (resource_id);

CREATE TABLE ai_answers (
    id TEXT PRIMARY KEY NOT NULL,
    provenance_id TEXT NOT NULL REFERENCES ai_provenance (id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    question TEXT NOT NULL,
    answer TEXT NOT NULL,
    created_at TEXT NOT NULL
) STRICT;

CREATE INDEX idx_ai_answers_project ON ai_answers (project_id);
