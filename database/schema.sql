-- SQLite. HistoryStore executes this resource inside its creation/upgrade transaction.
CREATE TABLE IF NOT EXISTS entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    text TEXT NOT NULL CHECK (length(CAST(text AS BLOB)) BETWEEN 1 AND 1048576),
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tags (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE CHECK (length(name) BETWEEN 1 AND 40 AND instr(name, ',') = 0)
);
CREATE TABLE IF NOT EXISTS entry_tags (
    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (entry_id, tag_id)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS ix_entry_tags_tag ON entry_tags(tag_id, entry_id);
CREATE TRIGGER IF NOT EXISTS discard_unused_tag AFTER DELETE ON entry_tags
BEGIN
    DELETE FROM tags WHERE id = OLD.tag_id
        AND NOT EXISTS (SELECT 1 FROM entry_tags WHERE tag_id = OLD.tag_id);
END;
PRAGMA user_version = 2;
