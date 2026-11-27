# Friend-triggered deliveries

**Stores → Friend deliveries** lets a friend reserve explicitly offered items and request delivery to one of their AutoRetainer characters. Update both PCs to 0.7.1.0 or later. Internet access is sufficient; this uses a temporary authenticated HTTPS mailbox, not LAN pairing or port forwarding.

## Invite your friend

1. On the PC with your owner credential, open **Stores → Access & invitations**. Enter a label such as your friend's name, click **Invite a friend**, then **Copy new access code**. Send that reader code privately to that friend. Do not share your owner or publisher code.
2. Your friend installs Ashe's Trader using the same custom repository URL, updates it, and opens **Stores → Access & invitations**. They paste the code and click **Connect**. They can now read your published catalogue under **Browse stores**.
3. On your PC, open **Friend deliveries → Request permissions → Refresh invitations & permissions**. Under your friend's invitation, select **May request from** the appropriate supplying PC, then click **Apply permission**. This is separate from read-only catalogue access.

The friend needs AutoRetainer, Allagan Tools, Lifestream and vnavmesh installed and working for automatic delivery, travel and retainer deposits. Refresh their AutoRetainer roster so the intended receiving character is listed. Keep its cached inventory available in Allagan Tools, and use a recipient that can reach the selected meeting territory. No owner credential, publisher credential, Cloudflare account, or LAN pairing is needed on their PC. Both games must be running for delivery; catalogue viewing works while the supplier is offline.

## Supplying PC

1. Keep this PC's existing publisher credential in Stores. Each supplying PC needs its own publisher credential.
2. In **Friend deliveries → Supply from this PC**, name the supply profile and select provider characters. **Select All** and **Select None** apply to this PC's roster; the filter helps find individual characters or worlds.
3. Search for the items to provide and select Normal or HQ. Each item uses all selected providers by default. Expand an item to choose a smaller set of providers for that item. Stand at an outdoor meeting spot with a main aetheryte and click **Record my current location**, then **Read cache & publish requestable stock**. The meeting world is chosen automatically during delivery. Allagan Tools supplies stock from carried inventory and retainer bags; no character login is required for publication. Missing caches block publication.
4. On a PC with the owner credential, open **Request permissions**, refresh invitations and permissions, and grant a specific reader invitation access to the desired supplying PC. Existing readers remain read-only until explicitly granted access.
5. Enable **Accept permitted friend requests automatically on this PC**. Publication alone does not enable game actions. Keep the game and plugin running.

No saved transfer job or recipient is needed to configure supplies. Draft selections are saved independently and do not change the published profile until you publish again. Existing 0.7.0.0 profiles carry over, including item-specific providers and meeting points. Requests with existing reservations must finish or be cancelled before replacing stock. Supply settings are independent on each PC. Publish again after inventory changes or a delivery to refresh available counts.

## Friend's PC

1. Connect the reader code in **Stores → Access & invitations**. No owner/publisher credential or Cloudflare account is needed.
2. Open **Friend deliveries → Request items**, select the store, refresh stock and choose a supplying profile.
3. Refresh AutoRetainer characters and choose the receiving character. Enter quantities within the displayed availability, then **Continue → Request & receive**.
4. This authorizes the selected character's login, travel, receipt and retainer deposits for these item types, plus the AR Multi handoff. It never authorizes sending the recipient's items away or controlling other characters.

One request is allowed per reader invitation at a time, up to 50 item/quality lines. One supplying PC handles one active delivery at a time. A request uses one supplying PC and one recipient; request another publisher separately afterward. It does not gather stock from all of the owner's PCs into a three-PC delivery. This first version uses carried bags and retainers; saddlebag packing is disabled only for friend sessions.

## Stock and execution

- Requests cannot exceed published available stock. The service reserves quantities atomically and subtracts queued/active reservations from availability. Concurrent requests cannot claim the same published quantity. Changed stock or permissions cause a refresh prompt rather than an oversized order.
- Published stock is a cache estimate with a timestamp, not a live inventory guarantee. If stock changes outside the plugin after publication, the existing transfer planner delivers what remains and records a shortfall; it never expands the requested amount.
- Both participants wait for other plugin jobs. The supplier disables AR Multi on both delivery PCs, waits for current AR work, then runs the existing multi-item transfer workflow. It collects the selected items from the selected providers and deposits on the recipient as space is needed. Successful completion enables AR Multi on both PCs.
- Saved trusted-PC pairing is retained while the friend connection temporarily occupies the workflow. It reconnects afterward according to the previous auto-connect setting. Private trusted-PC inventory synchronization is disabled during friend delivery; the friend session exchanges only selected character information and delivery commands/results.
- A completed reservation is conservatively consumed in full, even after partial delivery. Refresh the supplying profile from Allagan Tools after the run to offer any leftovers again. This avoids advertising stale stock immediately after a delivery.

## Pauses and receipts

Queued requests can be cancelled from either side. Active requests must be ended safely on the supplying PC: ambiguous trades or retainer movements must reconcile first, so an unknown outcome cannot release stock for another request. Network interruptions use existing tally/recovery behavior when automatic reconnection recovery is enabled. An explicit stop or plugin reload requires reconnecting/resuming the saved delivery; it does not create a new order.

An uncertain submission retains its original request ID. Reconnect retries that ID; **Check / clear unsubmitted request** checks whether the server accepted it before clearing the local draft.

The supplier's **Past Trades** contains the full local receipt, including confirmed movements. The hosted receipt and the recipient's Past Trades contain item totals, requested quantities, final delivered quantities, shortfalls and outcome. Hosted receipts omit individual movement rows to keep large runs bounded. **Requests & receipts** also shows completed results. A failed or ended job does not automatically enable AR Multi.

Revoking request permission cancels queued requests and stops communication for an active delivery. Active reservations remain held until the supplier settles them. Do not revoke a live delivery as a routine cancellation; end and reconcile it first. Reader codes are bearer credentials: use a separate invitation per friend, and keep owner/publisher credentials private.

## First live test

Use one provider, one recipient and two inexpensive item types, with small counts. Confirm:

1. An ordinary reader sees no requestable profile until permission is granted.
2. The request refuses a quantity above stock and reduces available stock when accepted.
3. AR Multi stops on both PCs, the correct characters meet, and both item types are delivered during the visit.
4. AR Multi returns after completion, both PCs receive a receipt, and the supplying PC reconnects to its usual trusted PC.
5. A queued cancellation restores availability. Refresh the profile after the completed test.

Tests cover server authorization, simultaneous stock reservations, replayed request IDs, revocation, mailbox message direction, receipt settlement and recipient command limits. Real game travel, trading and UI operation still require this live test. If it stops, copy Activity diagnostics from both participants; never include access codes.
