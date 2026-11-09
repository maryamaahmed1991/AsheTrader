# Two-PC testing: start here

Test build: **0.3.5.0**. Update Ashe's Trader on **both PCs** through `/xlplugins` before testing. The repository URL is unchanged. If you use a development DLL, disable that copy before loading the repository build. Keep existing settings and journals. Both PCs still use the same shared pairing key; this update does not require generating new keys. Both PCs should use this build together; the wire protocol remains 4.

## Reporting a stuck retainer menu

Detailed logging is automatic in 0.3.3. Reproduce the problem once. On the PC whose character is stuck, leave the game menu open and select **Transfers → 4. Activity → Copy diagnostics**. The copy button is at the top of Activity and works while a job is running. Paste the report into your bug report, then pause the workflow. If both PCs were involved, copy each PC separately and label them coordinator/worker. If the job has already timed out, copy before reloading the plugin; the last failure snapshot remains in the trace.

The report includes build/dependency versions, operation IDs and elapsed stages, expected/active retainer identity, addon visibility/readiness, expected menu text and visible menu entries, selection attempts, and inventory-page load state. It excludes pairing keys, configuration dumps, chat and dialogue bodies, but includes character/retainer identifiers and menu labels. The latest 500 bounded entries are retained for this plugin session; copied reports select the newest complete entries within a 28,000-character trace budget so the failure is preserved when pasted. Detailed entries also appear in Dalamud's log under `[WorkflowTrace]`. Repeated unchanged waits are sampled every five seconds.

Version 0.3.4 fixed the storage-label mismatch; the supplied trace confirms a successful ten-retainer scan. The next character's bell attempt occurred immediately after login and received no acknowledgement. Version 0.3.5 guards interaction readiness and retries bell opening a bounded number of times. Actual item movements are not automatically retried by that logic. Close leftover retainer menus, reconnect both PCs and use **Resume / reconcile**. Report the next failure with **Copy diagnostics** before reloading.

This version uses Allagan Tools caches for all planning, including restock totals. It performs no discovery logins or second full scan after login. Complete empty cache groups are accepted as estimates. Unavailable or malformed cache data pauses with an explanation instead of visiting the character. A paused pre-plan discovery resumes with a cache lookup; already-issued transfer plans keep their existing amounts and recovery checkpoints.

If version 0.3.0 stopped with **AutoRetainer cannot relog from the current game state**, update, reconnect both PCs, and use **Resume / reconcile** on the coordinator. That message was caused by checking AutoRetainer's title-screen-only login API while already logged in. Version 0.3.1 uses its supported relog command. Keep AutoRetainer multi mode off and Lifestream idle; the character switch must complete before scanning or trading proceeds.

The automated tests simulate game operations; native travel, retainer menus and trades still need this live test. Stop at the first unexpected action or error using **Pause workflow** or `/ashetrader stop`.

## First session: one provider, one recipient, ten tanks

### 1. Prepare both PCs

- Enable AutoRetainer, Lifestream, vnavmesh and Allagan Tools. AutoRetainer must know the characters and their retainers belonging to that PC's account. Without Allagan Tools, planning pauses.
- Turn AutoRetainer **multi mode off**, let its tasks finish, and leave Lifestream idle. Close any trade, retainer or confirmation windows. Keep AutoRetainer itself enabled for character switching.
- Choose one provider on the controlling PC and one recipient on the other account. Put **3 Ceruleum Tanks in the provider's bags and 17 on one of its retainers**. Leave room for ten tanks in the recipient's bags. Note its starting tank quantity.
- Both characters must be able to visit the chosen meeting world and access their own retainers on their home worlds. Default storage access uses Lifestream's inn shortcut.

### 2. Connect the PCs

Open `/ashetrader` → **Transfers** → **1. Connect PCs**.

On the controlling PC, choose **This PC controls transfers**, then **Host for my other PC**. A pairing key is generated if needed. Copy it to the other PC once.

On the other PC, choose **This PC receives instructions**, enter the shared key, click **Find my coordinator**, then **Connect to this PC** beside the expected PC name. Enable **Allow the coordinator to run jobs on this PC**. Previously paired PCs can keep their saved key.

Click **Refresh characters on both PCs**. Verify each character belongs to the intended PC. Use **Check required plugins on this PC** on each PC. The worker should show that it is ready for the coordinator; do not create a duplicate job there.

Pairing and discovery must not move, log out or trade on either PC. If no PC appears, verify both are on the same home network. Private-network firewall access must allow discovery UDP 45875 and the configured TCP port (45874 by default). A guest network or separate subnet can prevent discovery. Manual connection remains under Advanced; router port forwarding is unnecessary.

### 3. Set up the small transfer

On the controlling PC, open **2. Set up** and create a job:

- Item: **Ceruleum Tank**.
- Provider: only the prepared character with 3 carried and 17 stored tanks.
- Recipient: only the chosen character on the other PC.
- Quantity: **10 additional items**, not a final stock target or unlimited remainder.
- Meeting point: capture an accessible **outdoor position near a main aetheryte**. Set the matching main aetheryte for that territory. This is required for the transfer; cache lookup does not travel.

Leave the default inn storage route for this first test. No relay is needed when the provider and recipient are on different PCs. Relay selection is automatic when a same-account allocation needs one.

### 4. Check cached inventories first

Open **3. Review & run**, resolve the listed setup issues, then choose **Check inventories only**.

Expected: no character switching, travel or retainer windows. Activity should show **3 carried + 17 stored** for the provider, marked **Cached**, plus recipient counts. Compare the totals with Allagan Tools. Cached retrieval times must not be labeled as live observation times. If a character or retainer lacks usable data, Activity names the character and PC; refresh Allagan Tools there, then resume. No automatic live scan or item movement occurs during this check.

If scanning stops, report the character, home/current world, visible menu, and exact activity/log message. Do not repeatedly resume an unexpected native UI state.

### 5. Run and check the refill

Choose **Start transfer** from **3. Review & run**. The job reads the caches again, then logs in the characters needed for the transfer, checks carried stock and space, and travels. **Restock to total** also uses cached totals without a full recipient scan. If those totals are stale, the final stock can differ from the target; confirmed delivery counts remain based on actual trades.

Expected sequence:

1. The characters meet and trade the **3 carried tanks**.
2. The provider returns home to storage and withdraws **7 tanks** from its retainer.
3. The provider returns to the meeting point and trades the remaining **7**.
4. The job finishes with **10 delivered**. The recipient has its original quantity plus 10; the provider has 0 in bags and **10 still on its retainer**.

Pickup progress is separate from delivery progress: withdrawing seven must not itself increase the delivered total. The provider should not withdraw all 17. This test proves one pickup between trades and the requested amount limit.

## After the first session passes

### Multiple items and automatic relays

1. In the same saved job, add a second, different tradable item. Select that line and set its providers, recipients and a small additional amount, such as 2. Review must show both items and their separate amounts. All lines share the meeting point.
2. Start the job and confirm the first item finishes before the second begins. Each item gets a new cache lookup, without live discovery visits. Activity must retain the first item's delivered total while showing the second's progress. An inventory-only run should check both items without moving either. Then test a recipient whose bags fill from the first item: received first-item stock should be stored to make room for the second, while the first item's delivery total stays unchanged.
3. Next, use a provider and recipient on the same PC/account. Refresh AutoRetainer characters on both PCs. Do not select a relay manually: the other PC's registered alt with the highest cached free-slot count should be tried automatically. Check the chosen name and live capacity in Activity.
4. Confirm the route is provider → automatically chosen alt → final recipient. Relay cargo must not appear as final delivery. Cached providers and recipients skip discovery retainer scans; a relay-only alt needs a live bag-capacity check.
5. After normal selection passes, test stale hints deliberately: give the highest-ranked alt no capacity for the item while AutoRetainer still reports space, with another eligible alt available. Expect a live check followed by automatic fallback to the next candidate before trading. If every candidate is full, expect a pause; free space, refresh character lists and resume.
6. Check the colored status labels remain readable: green ready/success, blue active information, amber waiting/attention, red blocked/error. Text must explain every status even without its color.

AutoRetainer's free-slot figures are cached. The plugin checks current capacity before accepting a candidate. After a per-item plan has issued work, its relay stays fixed so recovery cannot assign old cargo to a different character. Unexpected later capacity loss pauses that item; subsequent items select again.

### Larger runs and recovery

Run these as separate, supervised tests. Each one checks a different behavior:

1. **Repeated provider refills:** use a provider with more retainer stock than its available carrying capacity, plus a recipient with sufficient space. Expect multiple bounded home/bell → pickup → meeting → trade trips. Several trades from one carried load should not cause unnecessary storage trips.
2. **Recipient overflow:** give the recipient limited bag space but enough permitted retainer capacity. It must first have space to receive job items. Expect home-world deposits of received items, return to the meeting point, then further trades. Depositing must not reduce the delivered count. Other item types remain untouched.
3. **Multiple providers and priority:** add a second provider and two recipients with small finite quantities. Verify the first recipient is fulfilled before the second and each provider's retained stock agrees with the amounts moved.
4. **Same-account route across items:** let the job choose its relay for each item. Expect provider → relay → final recipient. Relay holdings must not count as final delivery or become new source stock.
5. **Pause and reconnect:** pause between completed trades, then resume. Repeat with a LAN disconnect between trades. Completed deliveries must not repeat.

Only after those pass, test interruption while a trade or storage movement is pending, and then between completed item lines. Missing or conflicting evidence should pause for reconciliation. Saved completed results should recover without repeating the movement or rerunning a completed item. Never confirm a reconciliation choice merely to dismiss an error.

For an uncertain retainer pickup or deposit, inspect the original character's bags and retainers. Preserve the journals and report the stop. Do not repeatedly resume an uncertain storage movement. When the plugin permits ending the paused job, create a new, freshly scanned job for the remaining quantities.

## What to send back

- Both PCs' plugin versions and which PC controls the job.
- The step above, expected result, and actual result.
- Activity text and relevant `/xllog` lines from both PCs; for a menu failure, include the visible window and any logged addon/parent IDs.
- Provider bag/retainer counts and recipient counts before and after the failed step.

Keep `transfer-job.json` and `worker-journal.json` from both PCs. If sharing `settings.json`, remove the pairing key. Do not delete journals to bypass an ambiguous outcome.

## Fuel purchasing is a separate test

At a configured house's workshop, open the Ceruleum Tank shop and test a small restock amount. Check the inventory increase and FC-credit decrease. This build still requires the correct shop to be open; house travel, workshop entry and vendor opening are separate from the purchasing action.
