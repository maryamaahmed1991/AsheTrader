# Two-PC testing: start here

Test build: **0.2.2.0**. Update Ashe's Trader on **both PCs** through `/xlplugins` before testing. The repository URL is unchanged. If you use a development DLL, disable that copy before loading the repository build. Keep existing settings and journals.

The automated tests simulate game operations; native travel, retainer menus and trades still need this live test. Stop at the first unexpected action or error using **Pause workflow** or `/ashetrader stop`.

## First session: one provider, one recipient, ten tanks

### 1. Prepare both PCs

- Enable AutoRetainer, Lifestream and vnavmesh. AutoRetainer must know the characters belonging to that PC's account. Allagan Tools is optional.
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
- Meeting point: capture an accessible **outdoor position near a main aetheryte**. Set the matching main aetheryte for that territory. Record this before scanning, because scanning travels to storage.

Leave the default inn storage route for this first test. No relay is needed when the provider and recipient are on different PCs.

### 4. Scan first

Open **3. Review & run**, resolve the listed setup issues, then choose **Scan inventories only**.

Expected: the plugin logs into the selected characters, returns them to their home worlds if needed, travels to storage, and visits every retainer. It moves no items. The activity/progress view should show **3 carried + 17 stored** for the provider and verified recipient counts. Compare those counts with the game before continuing.

If scanning stops, report the character, home/current world, visible menu, and exact activity/log message. Do not repeatedly resume an unexpected native UI state.

### 5. Run and check the refill

Choose **Start transfer** from **3. Review & run**. A fresh inventory scan at the beginning is expected; the earlier scan is a validation step, not permission to reuse potentially stale counts.

Expected sequence:

1. The characters meet and trade the **3 carried tanks**.
2. The provider returns home to storage and withdraws **7 tanks** from its retainer.
3. The provider returns to the meeting point and trades the remaining **7**.
4. The job finishes with **10 delivered**. The recipient has its original quantity plus 10; the provider has 0 in bags and **10 still on its retainer**.

Pickup progress is separate from delivery progress: withdrawing seven must not itself increase the delivered total. The provider should not withdraw all 17. This test proves one pickup between trades and the requested amount limit.

## After the first session passes

Run these as separate, supervised tests. Each one checks a different behavior:

1. **Repeated provider refills:** use a provider with more retainer stock than its available carrying capacity, plus a recipient with sufficient space. Expect multiple bounded home/bell → pickup → meeting → trade trips. Several trades from one carried load should not cause unnecessary storage trips.
2. **Recipient overflow:** give the recipient limited bag space but enough permitted retainer capacity. It must first have space to receive job items. Expect home-world deposits of received items, return to the meeting point, then further trades. Depositing must not reduce the delivered count. Other item types remain untouched.
3. **Multiple providers and priority:** add a second provider and two recipients with small finite quantities. Verify the first recipient is fulfilled before the second and each provider's retained stock agrees with the amounts moved.
4. **Same-account route:** designate a relay on the other account. Expect provider → relay → final recipient. Relay holdings must not count as final delivery or become new source stock.
5. **Pause and reconnect:** pause between completed trades, then resume. Repeat with a LAN disconnect between trades. Completed deliveries must not repeat.

Only after those pass, test interruption while a trade or storage movement is pending. Missing or conflicting evidence should pause for reconciliation. Saved completed results should recover without repeating the movement. Never confirm a reconciliation choice merely to dismiss an error.

For an uncertain retainer pickup or deposit, inspect the original character's bags and retainers. Preserve the journals and report the stop. Do not repeatedly resume an uncertain storage movement. When the plugin permits ending the paused job, create a new, freshly scanned job for the remaining quantities.

## What to send back

- Both PCs' plugin versions and which PC controls the job.
- The step above, expected result, and actual result.
- Activity text and relevant `/xllog` lines from both PCs; for a menu failure, include the visible window and any logged addon/parent IDs.
- Provider bag/retainer counts and recipient counts before and after the failed step.

Keep `transfer-job.json` and `worker-journal.json` from both PCs. If sharing `settings.json`, remove the pairing key. Do not delete journals to bypass an ambiguous outcome.

## Fuel purchasing is a separate test

At a configured house's workshop, open the Ceruleum Tank shop and test a small restock amount. Check the inventory increase and FC-credit decrease. This build still requires the correct shop to be open; house travel, workshop entry and vendor opening are separate from the purchasing action.
