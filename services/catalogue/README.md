# Ashe private catalogue service

One Cloudflare Worker and D1 database per store. The public plugin repository contains code only; deployments, credentials, published inventory and production configuration are separate. The service has no public catalogue endpoint or public registration. `/health` exposes only the service name/version. All `/v1/*` endpoints require bearer authentication.

## Local development

Use Node 24 or later. Run `npm ci`, then `npm test`. Tests execute the production SQL against a real in-memory SQLite database (including JSON queries). `npx wrangler dev` provides a local Worker/D1 environment; apply migrations locally with `npx wrangler d1 migrations apply ashe-private-catalogue --local`.

## Deploy a new store

1. Authenticate the official Wrangler CLI to your account. Scope access to account/user read, Worker/script write and D1 write. Keep credentials outside the repository.
2. Create a D1 database. Copy `wrangler.jsonc` to ignored `wrangler.production.json`, set the database ID and account ID. Use a distinct Worker/database name if hosting more than one store. Keep the request rate-limit binding enabled.
3. Apply migrations with `npx wrangler d1 migrations apply ashe-private-catalogue --remote --config wrangler.production.json`.
4. Deploy with `npm run deploy`. Use the resulting HTTPS root URL.
5. Run `node scripts/bootstrap.mjs https://YOUR-WORKER.workers.dev/` once. It creates private local owner material and initialization SQL. Import `private/bootstrap.sql` using `wrangler d1 execute ... --remote --config wrangler.production.json --file private/bootstrap.sql`. Cloudflare's import handles the transaction. Re-running initialization against an existing store fails rather than replacing the owner's key.
6. Encode the owner JSON as `ashe-store:v1:` followed by standard base64 of its UTF-8 JSON. Connect it in the plugin. Keep the code private, or provision the encrypted credential file locally. Do not put it in logs, screenshots, the public repository or a release ZIP.
7. `node scripts/smoke.mjs` verifies a new empty deployment with artificial stock and temporary credentials, cleaning them up in `finally`. It reads only `private/owner.json`; do not use it on a store containing real stock because it asserts the final catalogue is empty.

The owner material under `private/` is sensitive. After setup, keep an encrypted offline backup and remove plaintext bootstrap material. Rotate a lost owner credential through the authenticated Cloudflare database console/CLI using a newly generated 256-bit key and SHA-256 hash; never store the plaintext key in D1.

## API and limits

Apply `0003_friend_orders.sql` before deploying 0.7.0. The new transfer endpoints require existing catalogue authentication plus explicit per-reader/per-publisher grants. They do not turn reader codes into publisher or trusted-device credentials. The `TRANSFER_LIMITER` binding permits bounded active-delivery polling separately from catalogue reads.

- `GET/PUT/DELETE /v1/transfers/grants`: owner manages request permissions.
- `GET/PUT /v1/transfers/offer`: publisher reads/replaces its offer with revision checks; reserved stock prevents replacement.
- `GET /v1/transfers/offers`: readers see only granted profiles and unreserved counts.
- `GET/POST /v1/transfers/orders`: scoped list or atomic, idempotent stock reservation (maximum 20 queued/active per publisher; one per reader).
- `POST /v1/transfers/orders/:id/claim`: supplying publisher claims a queued request with recent recipient presence; only one active per source.
- `POST .../presence`: the requesting reader advertises readiness and reads state.
- `POST .../exchange`: active participants exchange bounded messages. Readers cannot send game commands, and neither direction permits private inventory sync. Messages are deleted after acknowledged receipt; unacknowledged messages remain for recovery until settlement.
- `POST .../cancel`: cancel queued requests, releasing their reservation.
- `POST .../finish`: publisher settles an active request and writes an opaque JSON receipt string (preserving 64-bit identifiers). Its entire reservation is consumed conservatively, once; refresh stock after partial results.

Requests and receipts are hosted privately in D1. Selected character identities and game commands/results pass through the authenticated HTTPS mailbox during delivery. This is separate from the LAN-only trusted-device metadata endpoints below. No new grants or game automation are enabled by deploying the service. See [friend delivery setup and limitations](../../docs/friend-deliveries.md).

Apply migration `0002_devices.sql` before deploying 0.6.1. Device trust uses separate credentials/tables; catalogue readers and publishers cannot create or use device links.

- `POST /v1/device-links`: owner creates a coordinator credential and a ten-minute one-use invitation (maximum eight links).
- `POST /v1/device-links/redeem`: invitation claims exactly one worker identity atomically and returns its own device credential.
- `GET/PUT /v1/device-link`: linked devices read rendezvous metadata/update presence; only the coordinator can update a private IPv4 endpoint.
- `DELETE /v1/device-link`: either linked device revokes the entire pair.

No inventory or command relay is hosted by these endpoints. Device tokens cannot call the catalogue endpoints, and store tokens cannot act as a paired device. Cloud outages do not disable an already trusted local pairing; revocations are learned on successful authenticated service polls.

- `GET /v1/catalogue`: owner, publisher or reader. Aggregated NQ/HQ stock and publication timestamps only.
- `GET /v1/manage`: owner. Sources and revocable credential metadata (no plaintext secrets).
- `POST /v1/keys`: owner. Create reader/publisher; the new 256-bit secret is returned once.
- `DELETE /v1/keys/:id`: owner. Revoke; publisher removal also deletes its stock.
- `GET /v1/source`: publisher. Current revision and store-specific HMAC salt.
- `PUT /v1/source`: publisher. Replace its complete snapshot at the expected revision.
- `DELETE /v1/source`: publisher. Clear its stock at the expected revision.

Up to eight publishers, 100 reader invitations, 500 opaque inventory owners and 5,000 stock entries per publisher; maximum request body 512,000 bytes. Credentials are hashed with SHA-256 (high-entropy tokens, not passwords). The snapshot replacement is a single conditional SQL statement that checks revision, current authorization, and overlapping inventory owners across sources. Cross-PC overlaps fail instead of double-counting or preferring whichever stale cache uploaded last. Unknown payload fields are discarded.

All responses use `Cache-Control: no-store`, no CORS headers are enabled, clients refuse redirects, and server errors do not include request bodies, tokens or SQL. Worker observability logging is disabled. Cloudflare still handles infrastructure metadata under the account's service terms. Built-in rate limiting is approximate per Cloudflare location, not a strict global quota; Free-plan limits remain applicable.
