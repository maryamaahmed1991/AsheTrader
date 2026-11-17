# Private stores

The Stores tab publishes selected stock to a separately hosted, authenticated catalogue. Friends can browse the last published stock while the owner is offline. This feature does not require LAN pairing and cannot trigger trades or control a game client.

## Owner setup

1. Deploy the private catalogue service (see `services/catalogue/README.md`). Each deployment serves one private store.
2. In **Stores → Access & invitations**, paste the owner code. Review the service hostname and role before connecting.
3. Choose **Authorize this PC**. Each PC needs a separate publisher credential.
4. In **My store**, select the exact items and characters to include, then **Refresh preview**. This reads cached carried bags and retainer bags. It does not log into characters.
5. Review the totals, then **Publish this stock**. Optionally enable automatic refresh/publishing every five minutes while the plugin is running. Changing item or character selections disables automatic publishing until you review them again.

The preview includes NQ and HQ quantities separately. Equipped items, armoury storage, saddle bags, market listings, currencies and FC/shared storage are excluded from this first version. An unavailable or incomplete selected cache blocks the upload and preserves the previous published snapshot; it never substitutes an unknown inventory with zero. Stock is an estimate from Allagan Tools. Publication and cache retrieval timestamps are not the game's last inventory observation time.

## Second PC

Trusted-PC pairing under Transfers is separate from store publishing. It reconnects game clients and syncs private offline inventory automatically; see [Trusted PCs](trusted-pcs.md). A device invitation does not give a friend access to your private account inventories or grant catalogue publishing.

Give a name to **Authorize another PC**, copy the resulting publisher code, and connect it in the other PC's Access tab. Select separate characters on each PC. Overlapping physical inventories are rejected by the server using opaque, store-specific inventory identifiers; native character/retainer IDs and their names are never uploaded. Publishers replace only their own snapshots, with revision checks to prevent stale retries from overwriting newer stock.

## Invite friends

Name an invitation and choose **Invite a friend**. Copy the new code and send it yourself to that friend. They connect it in Access & invitations, then use **Browse stores**. Comma-separated partial names search the downloaded catalogue; an empty search shows all published items. No Cloudflare account is needed to browse.

Codes are bearer credentials: anyone holding a friend code has read access until it is revoked. Invitations are distinct, revocable and read-only. A friend may retain data already viewed; revocation prevents subsequent server reads. Do not share an owner or publisher code with readers.

## Stop sharing

Turning off automatic publishing keeps the last stock available offline. **Remove published stock** removes this PC's stock and disables automatic publishing. Revoking a publisher also removes that publisher's stock. Other PCs are unaffected. Revoking a reader rejects future requests from that invitation.

Access credentials are stored separately from settings using current-user Windows DPAPI, including Wine's implementation where available. If OS encryption fails, access remains usable only for the session and a warning is shown. Do not copy the encrypted file between Windows users/PCs: issue a new code for the other PC. No credentials enter activity diagnostics or request URLs.

## First in-game check

1. Publish one item from one character whose Allagan Tools cache is populated. Compare the preview against known carried and retainer totals.
2. Create a reader invitation and connect it on the other PC. Confirm the same stock appears without pairing.
3. Close the publishing client; refresh the reader and confirm the last stock still appears with its publication time.
4. Revoke the reader and confirm refresh is denied. Add a second publisher with a different character, then check the combined total.
5. Remove the test stock or expand the selection when satisfied.

Automated tests cover API roles, SQLite replacement/overlap logic, revision conflicts, invitation revocation, validation, credentials and hosted sample data. Actual Allagan Tools IPC publishing and this UI still need the in-game checks above. Existing transfers and fuel runs are independent of catalogue errors.
