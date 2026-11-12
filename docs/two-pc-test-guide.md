# Two-PC testing: start here

Test build: **0.3.10.1**. Update Ashe's Trader on **both PCs** through `/xlplugins` before testing. The repository URL is unchanged. If you use a development DLL, disable that copy before loading the repository build. Keep existing settings and journals. Both PCs still use the same shared pairing key; this update does not require generating new keys. Both PCs should use this build together; the wire protocol is now 5, so older peers must also update.

## Test faster trades and automatic recovery

Version 0.3.10.1 fixes the idle-state check that left Resume waiting for a trade window that was already closed. Update both PCs, keep the original characters logged in, reconnect and press **Resume / tally inventories**. The tally should advance automatically and a subsequent trade should send its opening request. Existing settings, pairing and saved progress are retained. If it still waits, Copy diagnostics now includes the raw native state even after a plugin reload.

Update both PCs to 0.3.10.1 and keep the existing settings, pairing key and journals. Reconnect the same coordinator and worker. With the original trade characters logged in, press **Resume / tally inventories**. There are no outcome-confirmation checkboxes. The plugin compares both PCs' current counts, credits a matching completed batch once or retries when both sides show no movement, and continues the remaining job. If counts conflict, Activity explains the discrepancy.

For a small speed test, send five stacks of an inexpensive item, including a partial stack. Offers should follow the game responses with at least 250 ms between item callbacks, without the old pauses after each quantity and filled slot. Verify the exact total on both PCs. Actual speed depends on game/server response time.

Then watch a return from the inn. Both characters should reach the meeting point and the next trade should open. A request ignored by the game may be retried at three-second intervals, with at most three opening requests per attempt. An acknowledged opening is not repeatedly requested. Two automatic unchanged-batch recoveries are allowed before a persistent opening problem pauses for diagnosis.

After a normal batch, test a user pause before an offer is made, then Resume. It should tally and continue with no manual outcome approval. Test an interruption near completion separately: a completed batch must be credited once. Do not change bags or characters while reconciliation is running. If it pauses, use **Activity → Copy diagnostics** on both PCs before reloading.

## Test faster deposits, full retainers and travel

For an interrupted retainer movement made by this build, use **Resume / tally inventories**. The saved movement checkpoint and current inventory determine how much moved; owned retainer menus close and the remaining work continues automatically. Cancel a leftover quantity dialog if the status requests it. A legacy uncertain record without the required baseline stays paused with an explanation because that older version did not record the starting count.

Update both PCs and keep the existing settings and journal. The meeting location now applies on automatically chosen worlds; recording it again is unnecessary if the territory, aetheryte and position are still suitable. Enable the required DC travel in Lifestream on both PCs.

Use providers in two DCs, including two worlds in the same DC, and give the relay enough capacity to collect from them. Activity's **Collection itinerary** should group the providers' DCs/worlds. Providers remain on their home worlds. When the relay fills or finishes gathering, it should join the recipient on the recipient's home world and remain there while that load is delivered. Limit recipient bag space to require two deposits: both deposits and the return to the meeting coordinates should happen on that same world. A full relay may need to revisit the collection DC for another load; priorities and item boundaries can also force revisits.

For the reported partial-stack case, use a permitted retainer with all 175 slots occupied but 681 spaces in a matching item stack, followed by another permitted retainer with free space. It should deposit exactly 681, then continue to the next retainer. A completely full retainer with no merge space should receive no item command. Full-stack deposits should use the faster pacing; partial deposits should submit the owned quantity dialog once. Watch an inn return too: loading should finish and the bell should open without needing Resume.

## Test faster retainer withdrawals

After updating both PCs, use one provider with empty carried stock and several full stacks of the chosen item on its retainers. Request enough to collect those stacks plus part of another stack, while leaving sufficient bag space. For Ceruleum Tanks, for example, put three full 999 stacks on a retainer and request 2,100. Confirm two full stacks and exactly 102 from the third are withdrawn, leaving 897 behind; then let the job trade the 2,100. If multiple providers are selected, ensure this test provider is the one allocated that quantity.

Full stacks should advance promptly after the game acknowledges them; only the partial stack should need a quantity dialog. The single minimum interval is 350 ms; network/server delay can take longer. Watch one transition between retainers too. Inn cutscenes must still play normally. If it stalls or a quantity is unexpected, pause and use **Transfers → 4. Activity → Copy diagnostics** on the affected PC before reloading. The trace now includes `withdrawal confirmed`, quantities and elapsed milliseconds per stack. Do not retry an uncertain movement without reconciling the inventories.

## Test relay collection and cutscenes

For a small new test, select three providers on one account with 100 items each and a recipient on that same account requesting 300 additional items. Leave at least 300 items' worth of capacity on the selected relay on the other account. The three providers should contribute to the relay before the destination logs in; then the relay delivers the accumulated 300. Several trade batches may be used for ledger accounting, but the destination should remain logged in while the load drains. Confirm the relay's original stock remains unchanged at the end.

Add a zero-stock provider and an extra provider that the final allocations do not need. Neither should log in. A recipient with no funded quantity should also stay offline. Activity shows the selected relay, collecting/delivering phase, cargo, itinerary and active meeting world. The captured world is ignored. Existing pending commands retain their destination until reconciled; subsequent scheduling uses the new itinerary without changing allocations.

If a relay fills before all providers finish, it should deliver its cargo, then resume collection. It must not deposit relay cargo onto retainers. Preserve recipient priority: the higher-priority recipient finishes before collecting for the next. Item lines still run one at a time.

For an inn login, watch the cutscene complete naturally. Set **AutoRetainer → Advanced → Expert Settings → Skip Inn Login Cutscene → Never** on each PC. Ashe's Trader will pause before relog if AutoRetainer is configured to skip; it does not edit that setting itself. During a cutscene the workflow should show a waiting status, send no progression input, and resume afterward without using up its gameplay timeout.

## Reporting a stuck retainer menu

Detailed logging is automatic in 0.3.3. Reproduce the problem once. On the PC whose character is stuck, leave the game menu open and select **Transfers → 4. Activity → Copy diagnostics**. The copy button is at the top of Activity and works while a job is running. Paste the report into your bug report, then pause the workflow. If both PCs were involved, copy each PC separately and label them coordinator/worker. If the job has already timed out, copy before reloading the plugin; the last failure snapshot remains in the trace.

The report includes build/dependency versions, operation IDs and elapsed stages, expected/active retainer identity, addon visibility/readiness, expected menu text and visible menu entries, selection attempts, and inventory-page load state. It excludes pairing keys, configuration dumps, chat and dialogue bodies, but includes character/retainer identifiers and menu labels. The latest 500 bounded entries are retained for this plugin session; copied reports select the newest complete entries within a 28,000-character trace budget so the failure is preserved when pasted. Detailed entries also appear in Dalamud's log under `[WorkflowTrace]`. Repeated unchanged waits are sampled every five seconds.

Version 0.3.4 fixed the storage-label mismatch; the supplied trace confirms a successful ten-retainer scan. The next character's bell attempt occurred immediately after login and received no acknowledgement. Version 0.3.5 guards interaction readiness and retries bell opening a bounded number of times. Actual item movements are not automatically retried by that logic. Close leftover retainer menus, reconnect both PCs and use **Resume / tally inventories**. Report the next failure with **Copy diagnostics** before reloading.

This version uses Allagan Tools caches for all planning, including restock totals. It performs no discovery logins or second full scan after login. Complete empty cache groups are accepted as estimates. Unavailable or malformed cache data pauses with an explanation instead of visiting the character. A paused pre-plan discovery resumes with a cache lookup; already-issued transfer plans keep their existing amounts and recovery checkpoints.

If version 0.3.0 stopped with **AutoRetainer cannot relog from the current game state**, update, reconnect both PCs, and use **Resume / tally inventories** on the coordinator. That message was caused by checking AutoRetainer's title-screen-only login API while already logged in. Version 0.3.1 uses its supported relog command. Keep AutoRetainer multi mode off and Lifestream idle; the character switch must complete before scanning or trading proceeds.

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
3. Next, use a provider and recipient on the same PC/account. Refresh AutoRetainer characters on both PCs. The other PC's relay is selected by estimated required loads, home-world locality, then free slots. Check the chosen name and live capacity in Activity.
4. Confirm the route is provider → automatically chosen alt → final recipient. Relay cargo must not appear as final delivery. Cached providers and recipients skip discovery retainer scans; a relay-only alt needs a live bag-capacity check.
5. After normal selection passes, test stale hints deliberately: give the selected relay no capacity while AutoRetainer still reports space. Expect a pause naming that relay, without logging through other alts. Free space or refresh character lists, then resume. If a relay becomes full after receiving cargo, it should deliver that load instead.
6. Check the colored status labels remain readable: green ready/success, blue active information, amber waiting/attention, red blocked/error. Text must explain every status even without its color.

AutoRetainer's free-slot figures are cached. The plugin checks current capacity on the selected relay. After a per-item plan has issued work, its relay stays fixed so recovery cannot assign old cargo to a different character. Later fullness triggers delivery if cargo is available, otherwise a pause; subsequent items select again.

### Larger runs and recovery

Run these as separate, supervised tests. Each one checks a different behavior:

1. **Repeated provider refills:** use a provider with more retainer stock than its available carrying capacity, plus a recipient with sufficient space. Expect multiple bounded home/bell → pickup → meeting → trade trips. Several trades from one carried load should not cause unnecessary storage trips.
2. **Recipient overflow:** give the recipient limited bag space but enough permitted retainer capacity. It must first have space to receive job items. Expect home-world deposits of received items, return to the meeting point, then further trades. Depositing must not reduce the delivered count. Other item types remain untouched.
3. **Multiple providers and priority:** add a second provider and two recipients with small finite quantities. Verify the first recipient is fulfilled before the second and each provider's retained stock agrees with the amounts moved.
4. **Same-account route across items:** let the job choose its relay for each item. Expect provider → relay → final recipient. Relay holdings must not count as final delivery or become new source stock.
5. **Pause and reconnect:** pause between completed trades, then resume. Repeat with a LAN disconnect between trades. Completed deliveries must not repeat.

Only after those pass, test interruption while a trade or storage movement is pending, and then between completed item lines. Resume should tally current counts and continue automatically when they agree with the saved checkpoint. Saved completed results must recover without repeating movement or rerunning a completed item. Conflicting evidence should pause with the specific mismatch.

For an uncertain retainer pickup or deposit, keep the original character logged in and preserve both journals. Use Resume to perform the tally. A missing historical checkpoint or out-of-range movement stays paused; copy diagnostics rather than clearing a journal that still tracks relay cargo.

## What to send back

- Both PCs' plugin versions and which PC controls the job.
- The step above, expected result, and actual result.
- Activity text and relevant `/xllog` lines from both PCs; for a menu failure, include the visible window and any logged addon/parent IDs.
- Provider bag/retainer counts and recipient counts before and after the failed step.

Keep `transfer-job.json` and `worker-journal.json` from both PCs. If sharing `settings.json`, remove the pairing key. Do not delete journals to bypass an ambiguous outcome.

## Fuel purchasing is a separate test

At a configured house's workshop, open the Ceruleum Tank shop and test a small restock amount. Check the inventory increase and FC-credit decrease. This build still requires the correct shop to be open; house travel, workshop entry and vendor opening are separate from the purchasing action.
