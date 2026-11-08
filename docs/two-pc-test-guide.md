# Two-PC validation

Test build: 0.2.0.0. The native game adapters have not yet been exercised in-game. Stop at the first unexpected action or error and send the activity text and relevant `/xllog` entries from both PCs. Do not start a large unattended job until the stages below pass.

## 1. Load and pair

Install the same ZIP on both PCs. Enable AutoRetainer, Lifestream, and vnavmesh, with each account's characters configured locally. Allagan Tools is recommended for cached inventory hints.

On the coordinator PC, open `/ashetrader` → Transfer jobs → Pair PCs. Choose its private LAN IPv4 address, generate a pairing key, and start hosting. On the worker PC, enter the coordinator's IP, the same port and pairing key, enable paired workflow control, and connect. If Windows prompts, allow the game/plugin listener on the private home network. No router port forwarding is required.

Refresh the rosters. Verify each character appears on the intended PC, each PC has a distinct peer identity, and the connection reports the expected partner. No character should log out, move, or trade from pairing alone.

## 2. Scan one character per account

Create a Ceruleum Tank job with one source and one recipient. Read the Allagan cache hints, then run **Scan inventories only**.

Expected: the required characters are logged in, each returns to its home world if needed, travels to an inn/bell, visits every retainer, and closes the retainer UI. The progress view reports verified bag and retainer quantities. No items move. Compare the numbers to the game.

If this stops, report the exact stage, local character, home/current world, whether an inn was reached, the visible addon/menu, and the log text. Strict callback validation may need adjustment based on the observed client UI; repeated clicking is not a fix.

## 3. Direct small transfer

Stand at a simple outdoor rendezvous near a main aetheryte and capture it. Use one source with 3–10 tanks in its bags and a recipient with free space. Set a small additional-delivery quantity. Start the job.

Expected: discovery completes, both characters meet, the receiver arms for the exact source/item/quantity, one trade occurs, both observed changes agree, and the job reports the exact delivered total.

## 4. Source retainer pickup

Repeat with the source's requested tanks stored only on a retainer. Expected: the source withdraws a bounded load, closes storage, returns to the rendezvous, and delivers the requested amount. Check the retainer decrease and final recipient increase.

## 5. Multiple batches and overflow

Use enough tanks to require several trade batches. Give the recipient limited bag space and enough permitted retainer capacity. Expected: it stops before an unfit batch, returns home, deposits received job items, returns to the rendezvous, and resumes. Other item types remain untouched. Confirm the delivered counter stays constant during deposits and increases only after new trades.

Do not begin with the receiver entirely full of unrelated stock: there must first be space to receive job items that can subsequently be deposited.

## 6. Priority and relay

Add two recipients with small finite targets and verify the first is fulfilled before the second. Then test a source and destination on the same account with a designated relay on the other account. Expected: source → relay → final recipient, with the account changing characters between the legs. Relay cargo must not count as final delivery or become a new source allocation.

## 7. Pause and disconnect

Pause between completed trades, then resume. Next disconnect the LAN link between completed trades, reconnect, and resume. Confirm no completed delivery repeats.

Only after those pass, test interruption while a trade is pending. The expected result is a pause for reconciliation if either durable outcome is missing. Confirmed results should recover without another trade; uncertain results require inspection of both original characters. Never use a reconciliation button merely to make an error disappear.

For interruption during a retainer deposit, a durable result should recover without repeating the deposit. An uncertain last movement must stay paused. Report it and inspect storage; use a new, freshly scanned job for remaining delivery quantities after ending the paused job. Do not repeatedly resume an uncertain storage operation.

## 8. Fuel purchase confirmation

At a recorded/imported house's workshop, open the Ceruleum Tank shop. Configure a small restock total and start purchases. Verify the expected quantity and FC credit decrease. House travel, workshop entry, and vendor opening are separate from this shop purchasing action in this build.

## What to send back

- Stage number and expected versus observed behavior.
- Activity messages and relevant `/xllog` lines from both PCs.
- Dalamud and dependency versions if they differ from this development environment.
- For a native UI stop, the visible window and the exact logged addon/parent IDs when provided.

Retain both journal files. If sharing settings, remove the pairing key. Do not delete journals to bypass an ambiguous transfer result.
