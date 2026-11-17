-- Device trust is separate from catalogue reader/publisher permissions.
CREATE TABLE IF NOT EXISTS device_links (
 id TEXT PRIMARY KEY, host_peer TEXT NOT NULL, host_name TEXT NOT NULL,
 host_hash TEXT NOT NULL UNIQUE, guest_peer TEXT, guest_name TEXT, guest_hash TEXT UNIQUE,
 invite_hash TEXT UNIQUE, invite_expires TEXT NOT NULL,
 address TEXT NOT NULL DEFAULT '', port INTEGER NOT NULL DEFAULT 0,
 host_seen TEXT, guest_seen TEXT
);
