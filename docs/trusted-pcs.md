# Trusted PCs

Version 0.6.1 remembers a coordinator/worker pair and reconnects whenever both plugins are running. This is independent of the Stores catalogue: a friend invitation never grants device control or private inventory access.

## Already paired PCs

Update both PCs. Existing authenticated pairings are retained and automatic reconnection is enabled. The coordinator is inferred from the saved job/previous remote-control setting on first migration. If the role is wrong, disconnect for the session and select the correct role under Manual LAN pairing. After a successful connection the exact role is saved.

No job starts just because a plugin loads. Saved jobs stay paused; use Resume once you want them running.

## New pair

1. On the PC with store owner access, open **Transfers → Connection → Trusted PCs**, name the PC, and select **Add another PC**.
2. Copy the one-time PC invitation. On the second PC, paste it in **Trusted-PC invitation**, review the coordinator/service, and choose **Trust and connect**. It expires after ten minutes and can only be redeemed once. If it expires, forget the unused link and create another.
3. On the worker, enable **Allow the coordinator to run jobs on this PC** when you want automated transfers. Private inventory sharing and reconnection work without this game-control permission.

The new invitation is a device credential, not a store publisher credential. Publishing the second account to your friend catalogue still uses Stores → Authorize another PC and its own item/character selection.

New device keys are saved in an OS-encrypted file separate from normal configuration. The service stores only credential hashes, device names/IDs, and local-network rendezvous metadata. It does not receive private inventory snapshots, pairing encryption keys, or game commands. Existing manual LAN pairings remain supported without a hosted service.

## Connection behavior

The coordinator listens automatically. The worker searches for the remembered coordinator and falls back to the last address or the hosted address. Authentication checks the saved peer identity and shared key. Address changes are rediscovered; a missing adapter is retried. Manual **Disconnect for this session** suppresses reconnect until **Reconnect now** or the next plugin load. Turn off the automatic-connection checkbox to disable it persistently.

Live transfers require reachable PCs on the same private IPv4 network and firewall access to TCP 45874 (or your configured port). UDP 45875 provides discovery. Hosted rendezvous can supply an address when broadcast discovery is unavailable, but it is not an internet relay and does not bypass NAT or a blocked TCP port.

**Forget this device link** stops local access and removes its offline snapshot. For a hosted link it revokes both sides; the other online PC discovers revocation on its next service poll. Friend and publisher credentials are unaffected.

## Private inventory and offline jobs

Updated paired PCs exchange a private Allagan Tools snapshot after connecting and approximately every five minutes. Reads cover characters/retainers registered with AutoRetainer and their personal cached containers. Missing caches are reported; no character login is performed to populate them. This snapshot is saved only on the other PC. Retrieval time is not the game's last inventory observation time.

Item Search uses a live cache query while connected and the last synced private snapshot while offline, clearly labelled as saved/offline. The saved character roster also remains available for configuring jobs. Unknown or changed inventories can make old totals inaccurate; offline snapshots never authorize item movements or replace live transfer reconciliation.

**Start when other PC is ready** saves a copy of the plan and waits for connection, worker control permission, and an idle worker. Cancel waiting to withdraw that request. Reloading either coordinator plugin never arms a saved waiting job: use **Start waiting** again.

With **Recover a running transfer after a brief disconnect** enabled, a run that was active in this coordinator session can reconnect and enter the existing inventory tally/reconciliation workflow. It does not blindly replay a trade. Explicit pauses, other errors, stopping the connection, and plugin reloads require a deliberate resume.

## Two-PC verification

1. Update both PCs, leave jobs paused, and verify they connect without pressing Host/Connect.
2. Wait for private inventory sync. Close the other plugin and search for an item held only on that PC; it should remain visible with the offline warning and retrieval time.
3. Start a small transfer while the worker is offline. Confirm the coordinator waits. Open the worker and enable control; confirm it begins only once ready.
4. With no game work running, disconnect/reconnect the network and verify automatic connection recovery. Manually disconnect and verify it stays disconnected for that session.
5. Restart the coordinator with a queued or paused job. Confirm no character moves until you explicitly resume.
6. For a controlled interruption test, use a small known quantity. Reconnect and verify the existing reconciliation accounts for the actual inventory before continuing; report diagnostics from both PCs if it pauses.
