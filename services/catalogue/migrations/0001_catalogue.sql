CREATE TABLE store (id INTEGER PRIMARY KEY CHECK(id=1), name TEXT NOT NULL, salt TEXT NOT NULL);
CREATE TABLE sources (
 id TEXT PRIMARY KEY, label TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0,
 snapshot TEXT NOT NULL DEFAULT '{"owners":[]}', published_at TEXT
);
CREATE TABLE credentials (
 id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, role TEXT NOT NULL CHECK(role IN ('owner','publisher','reader')),
 label TEXT NOT NULL, source_id TEXT REFERENCES sources(id), created_at TEXT NOT NULL
);
