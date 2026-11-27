CREATE TABLE transfer_offers (
 source_id TEXT PRIMARY KEY REFERENCES sources(id) ON DELETE CASCADE,
 revision INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 0,
 label TEXT NOT NULL, peer_id TEXT NOT NULL, items TEXT NOT NULL DEFAULT '[]', updated_at TEXT NOT NULL
);
CREATE TABLE transfer_grants (
 reader_id TEXT NOT NULL REFERENCES credentials(id) ON DELETE CASCADE,
 source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
 PRIMARY KEY(reader_id,source_id)
);
CREATE TABLE transfer_orders (
 id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
 reader_id TEXT NOT NULL,
 host_peer TEXT NOT NULL, guest_peer TEXT NOT NULL, recipient TEXT NOT NULL, items TEXT NOT NULL,
 state TEXT NOT NULL CHECK(state IN ('queued','active','completed','cancelled')),
 created_at TEXT NOT NULL, guest_seen TEXT, receipt TEXT, ended_at TEXT
);
CREATE UNIQUE INDEX one_active_transfer_per_source ON transfer_orders(source_id) WHERE state='active';
CREATE INDEX transfer_order_queue ON transfer_orders(source_id,state,created_at);
CREATE INDEX transfer_order_source_history ON transfer_orders(source_id,created_at);
CREATE INDEX transfer_order_reader_history ON transfer_orders(reader_id,created_at);
CREATE TABLE transfer_messages (
 sequence INTEGER PRIMARY KEY AUTOINCREMENT, order_id TEXT NOT NULL REFERENCES transfer_orders(id) ON DELETE CASCADE,
 sender TEXT NOT NULL, message_id TEXT NOT NULL, payload TEXT NOT NULL,
 UNIQUE(order_id,sender,message_id)
);
