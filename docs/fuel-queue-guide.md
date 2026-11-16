# Fuel queue setup and first test

Version **0.5.0.0** adds independent fuel queues on each PC. Pairing is not required. Each queue uses that PC's selected characters, AutoRetainer roster and saved destinations. The new fuel workflow is ready for staged testing; supplier travel, purchases and repeated deposits have not yet been validated in-game by the development agent.

## Fuel workspace in 0.5.1.0

- **Buyers:** search by world, FC or character, then configure one FC at a time. Search retains the complete FC buyer list so priority changes include every sibling. The selected-company filter shows FCs with at least one enabled buyer.
- **Destinations:** browse saved workshops by world, add a house, record the current workshop, or expand Rename house. Caretakers are chosen in Buyers and require no saved house.
- **Run overview:** inspect the next queue and its purchase plans, follow the live itinerary, or view last-run results. Starting or resuming a run opens this view automatically.

The dashboard and run controls stay above the scrollable view. Unfinished runs lock edits, but you can still browse companies and inspect buyer preferences. Cached credit balances are explicitly marked; live checks still control spending and travel.

## Set up buyers

### Automatic destinations and receipts (0.5.2.0)

In **Buyers → 01 Destination & budget**, choose **Automatic** to let each buyer use a reachable supplier. The buyer row shows the cached recommendation; open its route details for the selection reason. No cached route means “checked on login,” not a permanent rejection. Live selection checks home-world access and prefers the current location, a successful previous supplier, and direct FC estate routes before city routes. Register any workshop houses you want considered under Destinations. Caretakers need no registration. Individual house overrides remain fixed. Existing queues retain their saved destination policy; enable Automatic after finishing the current queue.

When a queue finishes or is ended after its pending movements are reconciled, open **Run overview → Run history**. The latest 100 receipts survive reloads in `fuel-history.json`. Spending totals come from confirmed quantity × unit price, rather than net changes in shared FC credits. Deposited totals can include fuel carried before the run. Elapsed time includes pauses and time offline. Older runs cannot be reconstructed beyond the last available journal; missing costs/times are labeled accordingly.

**Retry skipped buyers** starts a new run for skipped characters still in the local roster, using their current destinations and budgets even if they are not currently selected. Completed and full-storage buyers are excluded. It does not replay purchases or change saved selections. Correct the reason for a skip before retrying; an exhausted FC will otherwise be skipped again.

For a first test, select one buyer, use Automatic, and choose a small restock target. Verify the suggested route and final receipt, then reload the plugin and confirm the receipt remains. An unfinished old queue can still be resumed before testing Automatic in a new run.

1. Update Ashe's Trader and open **Fuel & houses → Buyers**. Setup automatically refreshes AutoRetainer's character/FC data and imports Lifestream's saved houses. **Setup details & refresh → Refresh setup** repeats the import.
2. Choose an FC in the world-grouped navigator (or the company dropdown in a compact window). Select the characters that may buy fuel and use **Up/Down** to order buyers within that FC. Characters run in world and FC groups. Unknown FC memberships remain separate until checked after login; a character known to have no FC is skipped.
3. Under **01 Destination & budget** and choose either a registered workshop house or **Resident Caretaker**. The caretaker needs no company house; choose an accessible residential district on the character's home world. FC rank and credit-spending permissions still apply.
4. Set **FC credits to keep** for that FC. Zero converts every affordable credit into fuel. This is a shared FC reserve, rather than a separate budget per character. Cached balances help with setup; the shop's live balance controls purchases.
5. Select a character's name to edit its options. **Use FC budget** enables repeated buying. For a smaller run, select **Restock to an amount** and set **Buy when carrying fewer than** and **Restock to**.
6. Leave **Store fuel in retainers and continue buying** enabled for repeated buy/deposit cycles. Set **Tanks to keep carried** to the amount that should stay in the bags. Deposits store **all other carried tanks, including fuel that was already there before this run**. They use the character's home-world bell and configured retainer selection.

An individual character can override the FC destination with another registered house on its home world. The house dropdown excludes other worlds, and queue validation rejects stale assignments to another world.

### Route checks in 0.5.0.7

Each buyer shows **Route available**, **Route unavailable**, or **Check on login**. Select the character's name and expand **Route details & access requirements** for the route explanation, missing requirements, suggested alternative and observation time. Changing the FC destination or character override immediately recalculates the result from that character's saved access snapshot. **Refresh setup** refreshes the logged-in character's access as well as house imports. Offline characters are never logged in merely to populate these checks.

The queue prefers a verified FC estate teleport that matches the selected house exactly. For a caretaker, an FC estate teleport into that district can provide entry without the city housing menu. Otherwise, the city route requires both attunement and the district's housing-unlock quest. Already being in the selected district or estate can also allow the local approach. A cached unavailable route is rechecked live when that buyer logs in; a verified unavailable route is skipped with its reason while other buyers continue. Unknown access is shown as unknown, not asserted to be a permanent lockout. Workshop entry and FC credit-spending permissions are still checked at the destination.

### Recovering a shared-credit purchase pause

If all requested tanks arrive while another FC member earns or spends credits, the net credit change can differ from the purchase cost. Version **0.5.0.7** accepts an exact delivered item quantity for the original buyer and FC, with the original unit price. It checks current affordability and the credit reserve again before another purchase. Partial or conflicting item changes remain unresolved rather than triggering a repeat purchase.

The queue also tracks its acknowledged spending separately from the displayed balance. If that budget is used up while the shop still shows spendable credits, it automatically closes and reopens the same supplier to obtain a fresh balance before starting another batch. A delayed display therefore cannot renew the queue's allowance below the configured reserve.

Update and **Resume** the saved queue. If **End queue** was previously pressed, Resume reconciles the outstanding purchase and finishes that queue; start a new queue afterward to buy more. Configuration and journals do not need to be deleted.

## Register houses

Open **Destinations**. Version **0.5.0.1** imports house entries from Lifestream's **Address Book** and the registered **FC houses** under **House Registration → Free Company House** for this PC's known characters. Shared FC addresses are deduplicated. Private housing registrations and apartments are excluded because they cannot provide company workshops. The refresh summary distinguishes houses found from newly added houses and shows the total saved count.

Registered FC plots do not need recorded entrance or workshop paths for discovery. Lifestream's separate **Custom House** entries do not expose enough owner/world information through this integration; add those addresses to its Address Book or register them here.

Use **Add a house** to enter its label, world, residential district, ward and plot, or **Record this workshop** while inside the intended workshop. If capture fails, use the fuel page's **Copy diagnostics** before reloading; the report includes the unresolved housing fields.

A registered address must have an accessible company workshop and fuel supplier. An address alone does not establish workshop access. Characters without a suitable house can use the Resident Caretaker option instead.

Version **0.5.0.3** handles Lifestream stopping just outside the plot: the queue verifies the selected plot's drop-off location, identifies its visible entrance using the plot's map marker, and then continues through the house and workshop. This also handles a zero native entrance-VFX coordinate, which blocked 0.5.0.2 at some visible houses. A paused queue can be resumed after updating; no new path or house registration is required. If entry still fails, copy diagnostics while the character is at the stopping point.

## Run a small test first

Version **0.5.0.4** uses Lifestream's saved house paths when available. Under **Lifestream → House Registration → Free Company House**, check both **Path to entrance** (outside gate to house door) and **Path to workshop** (inside the estate to the workshop entrance). Existing recordings are read automatically on Start or Resume; no house re-registration is needed. A matching registration may belong to another known character on this PC, provided its home world and full address match the selected fuel destination. Custom House records are not exposed by this registration lookup.

The queue follows the recorded waypoints using Lifestream itself, then performs the verified door/menu interactions. It checks endpoint arrival after movement ends. Resume preserves route corners, and a character stopped outside the recorded route can return to the plot drop-off once before trying it. If a path section is empty, that section uses normal navigation; a workshop-only recording does not replace the outside gate approach. Copy diagnostics reports the chosen registration, both point counts, the active endpoint and any short stop or stall.

Version **0.5.0.5** fixes stopping at **Entrance to Additional Chambers** after the saved route finishes. It recognizes the game's different door variants by their shared localized name. Update, close any manually opened menu, and Resume the existing queue. The existing house registrations and recorded paths remain usable. If entry still fails, copy diagnostics before ending the queue so the report includes nearby door names and IDs.

1. Finish or end any transfer job on this PC. AutoRetainer should be ready and idle with multi-mode paused; Lifestream and vnavmesh must be available.
2. Select one buyer and a familiar destination. For a character carrying zero tanks, select **Restock to an amount** and set **Buy when carrying fewer than: 1**, **Restock to: 99**. If it already carries fuel, choose a threshold above that carried quantity and a target only a small amount higher.
3. Check the **Run overview**, then press **Start fuel run**. Confirm the correct character logs in, reaches the chosen supplier, buys the intended fuel and stores fuel according to its keep-carried setting.
4. Check the bought/stored totals and actual inventory. Repeat with the other destination type if you intend to use both workshop houses and caretakers.
5. Expand to two selected buyers in one FC, then another FC/world. Finally enable credit exhaustion to test repeated purchases, full-bag deposits and continuation to the next buyer when retainer storage is full.

Each PC's queue is started and monitored separately. You can use one PC's fuel queue without connecting the other PC.

## Progress and recovery

Version **0.5.0.8** fixes Resume attempting housing travel before tallying an already delivered purchase. Stay on the original buyer and press **Resume**; an exact receipt is counted directly from carried inventory wherever the character is standing. If **End queue** was already requested, the queue then finishes without returning to the supplier. Start a new queue afterward to continue buying. Do not clear the saved journal. If no receipt is observed, the pending intent remains and the supplier evidence is still needed before any repeat purchase.

FC estate route checks now read the packed house identity rather than the shared-estate ward/plot fields. Existing house registrations and recorded paths remain valid. Refresh setup after the old queue ends to inspect the corrected route.

The run bar shows the current action and accumulated tanks bought/stored. **Current queue** records each character's result. When a buyer has no more retainer room, it is marked **Storage full** and remaining selected buyers can continue. Once the FC reaches its credit reserve, later buyers in that FC do not spend more. Other FCs continue with their own balances.

**Pause** stops the queue and saves its state. Reloading the plugin leaves saved work paused. **Resume** reconciles any recorded purchase or deposit against current counts before scheduling more work. If a retainer deposit was interrupted and the character changed, return to the affected character when prompted so its inventory can be tallied. Ambiguous changes remain a visible pause rather than being repeated blindly.

Version **0.5.0.6** automatically handles the specific FC-shop inventory-space rejection when it occurs before a confirmation appears. With the original shop still open and inventory/credits unchanged for two seconds, it can retry twice with increasing delay. Confirmed requests, changed totals, unrelated errors and unanswered requests without an explicit rejection are not retried. Persistent failures still pause; **Copy diagnostics** now includes the purchase's native state and attempt history. After updating an already-paused run, Resume tallies its saved request before continuing.

**End queue** preserves completed progress. An outstanding purchase or deposit is tallied before the queue finishes; no new purchase begins during that ending step.

If something stalls, use **Copy diagnostics** in the fuel run bar on the affected PC before changing the game menu. Include which character, destination and screen were visible. The report contains identifiers and recent steps, but excludes pairing keys and chat.
