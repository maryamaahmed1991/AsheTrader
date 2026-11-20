# Ashe's Trader

Dalamud API 15 / .NET 10 plugin for two-account item transfers across two PCs and local character queues that convert FC credits into Ceruleum Tanks.

This is a development build with automated coverage for planning, journals, LAN transport and coordinator behavior. The new fuel queue still needs in-game validation of supplier travel, purchases and repeated deposits. The development agent has not performed live game actions.

## Features

- Use a redesigned dark workspace with sidebar navigation, teal actions, gold headings, readable cards, searchable characters, responsive recipient editing and persistent stop controls. The coordinator configures transfer jobs; each PC configures its own fuel queue.
- Pair one coordinator PC and one worker PC using an authenticated, encrypted LAN connection.
- Find nearby coordinators by PC label without copying IP addresses. Hosting chooses a local network adapter automatically; manual connection remains available.
- Import each PC's AutoRetainer roster and plan from Allagan Tools' cached character and retainer inventories.
- Search comma-separated partial item names across this PC or both paired PCs, with combined totals and character/retainer storage details from Allagan Tools.
- Assign an individual item list to each recipient: one alt can receive Coke and Fuel, another only Fuel, and a third Hardened Sap. Each item keeps its providers, recipient priority, quantity rule and quality.
- Choose providers and relays using funded quantities, direct routes, home worlds and estimated relay loads. Only assigned providers, recipients and needed relays log in.
- Fill relays across multiple providers before delivering, then drain the collected load before gathering again.
- Prepare both PCs independently: login, retainer pickups/deposits and meeting travel overlap; trading waits for both characters to arrive.
- Preserve inn cutscenes and wait for their natural completion before continuing.
- Read status at a glance: green for ready/success, blue for active information, amber for waiting or attention, and red for blockers/errors. Text labels remain alongside colors.
- Plan from Allagan Tools' cached inventories without discovery logins or repeat retainer scans, including restock jobs. Characters with unavailable cache or roster data are skipped with a persistent notice while available work continues.
- Allocate additional quantities, collect all with a final remainder recipient, or restock to a combined bag/retainer total.
- Trade providers' carried stock first, then repeatedly collect bounded loads from their retainers and return to the meeting point until their assignments are complete.
- Return a full receiver home, deposit received job items into allowed retainers, then return and continue.
- Pace trade offers at a minimum of 250 ms, advancing on observed game readiness instead of stacked delays.
- Persist worker results and coordinator progress. Resume tallies both PCs' current inventories and reconciles interrupted transfers automatically.
- Run fuel buyers in world → FC → character order, with shared FC credit reserves, same-world workshop houses or Resident Caretakers, repeated retainer deposits and saved progress.
- Import saved Lifestream houses automatically, register other houses and choose individual fuel buyers without requiring the other PC to be paired.
- Preserve the local trade-list mode and existing transfer settings.

## Version 0.6.4.5

Recovery no longer interrupts a healthy cargo load after thirty seconds: it waits within the native operation budget, and verified saddlebag progress resets the inactivity timer. Interrupted cargo automatically reconciles against its original saved absolute target, with two bounded attempts before unresolved evidence requires attention. Explicit user pauses remain paused. Each verified slot movement proceeds without an additional stabilization sleep.

Consecutive trades keep the same prepared character pair when the planned route, carried items and conservative receiving headroom permit it. One fresh inventory pass still checks both sides before the next offer; login/travel preparation is skipped for that pair. Senders drain carried items before refilling from saddlebag cargo, instead of topping up after every five-slot batch. Full inventories, exhausted carried stock and changed routes use the normal preparation/deposit flow.

## Version 0.6.4.4

Silent inspections of already-loaded saddlebags no longer add a 300 ms settling delay for every item. They read current native slots after checking for pending operations, as ordinary bag inspections do. Initial window loading and actual cargo movement retain their stabilization and confirmation checks. This reduces inspection overhead between trade batches; partner login, travel, and trade readiness still take their normal time.

## Version 0.6.4.3

Saddlebag inspections now read live loaded containers silently after the first verified opening in a character/territory session. Multi-item trades no longer reopen the window for every item check. Logout, character or territory changes, premium-access changes, and unloaded containers require a fresh opening. Actual packing/unpacking still opens the window and verifies movement; inventory quantities are never cached by this optimization.

## Version 0.6.3.2

Retainer storage operations now advance the unnamed “Assign retainer a job” reminder automatically, using the client's localized dialogue text. This applies to transfer withdrawals, deposits, fuel deposits and storage recovery. The selected retainer and active bell interaction must match; unrelated dialogue, quantity confirmations and cutscenes are left untouched. Diagnostics identify the recognized reminder without recording dialogue bodies.

## Version 0.6.3.1

A brighter lavender-and-cream palette replaces the dark plum workspace. Dark plum text, deeper teal quantities, rose actions and amber status labels keep the light surfaces readable. Navigation, cards, tables, popups and progress bars use the same palette; help still opens only through the **i** buttons.

## Version 0.6.3.0

Item Search remembers the last query and quantity sort preference on each PC. Opening the page after a reload reruns that query against the available caches. Use **Save list** for named comma-separated searches, the saved-search picker to run them again, and **Edit list** to rename or update the selected list. Deleted lists can be restored with **Undo deletion** until another search or action replaces the notice. Lists are local to each PC and do not store inventory snapshots.

Search controls now have clearer labels, right-aligned quantity columns, and expandable source details. Selected-item storage has its own scrolling table with frozen headers. Wide workspaces show results and locations side by side; smaller windows stack them. Guidance stays behind the page's **i** button.

## Version 0.6.2.1

Illustrated guidance now opens only when you click the round **i** button beside a page heading. The normal workspace has no mascot portraits or expanded welcome cards. Close help with **Got it**, Escape or a click outside it. The illustration preference now applies inside help popups; all status, error and stop controls remain visible.

The installer icon URL and installed manifest were verified. Dalamud can retain a cached missing icon after an update in the same game session; restarting the game clears that session cache.

## Version 0.6.2.0

A warmer plum, rose, cream, mint and gold workspace with rounded cards, pill badges and softer navigation. Ashe's chibi illustrations accompany page headers and explain connection setup, first searches, store setup and fuel buyer selection. Artwork is bundled locally and loaded on demand. Hide it under Advanced → Preferences & local log → Show Ashe's helper illustrations; help text remains available. Status labels and stop controls remain prominent.

## Version 0.6.1.1

Adds the Gil Wink chibi mascot as the plugin installer icon, with a transparent 512 × 512 PNG included in the package. This is a branding-only update.

## Version 0.6.1.0

**Trusted PCs** reconnect automatically after one-time setup, retain a private inventory snapshot for offline Item Search, and allow transfer jobs to wait for the other PC to become ready. Existing pairings migrate without replacing their identities. New pairs can use an owner-created, ten-minute one-use invitation; the hosted service supplies connection metadata only. Inventory and game commands remain on the encrypted LAN connection. Saved jobs never start merely because a plugin reloads, and brief disconnects enter the existing reconciliation flow when recovery is enabled.

Update both PCs and see the [trusted-PC guide](docs/trusted-pcs.md) for setup and testing. Live trades still need a reachable local network. Store friend/publisher access remains separate from device trust.

## Version 0.6.0.0

**Stores** adds an optional private hosted catalogue. Select exact items and characters, preview cached bag/retainer totals, and publish them for invited friends to browse while you are offline. Each PC has separate publishing access; overlapping inventories and stale writes are rejected. Friend invitations are read-only and individually revocable. Automatic publishing is optional and defaults off. The public plugin contains no hosting credentials and uploads nothing until a store is connected and publishing is explicitly requested.

See the [private-store guide](docs/private-store.md) for setup, sharing and first in-game checks, and [catalogue service](services/catalogue/README.md) for self-hosting. Existing transfers and fuel queues are unchanged.

## Version 0.5.2.0

Fuel runs now keep the latest 100 completed/ended receipts on this PC under **Run overview → Run history**. Receipts include per-buyer outcomes, confirmed purchase costs, tanks bought/stored, destinations and elapsed time including pauses. Copy a receipt or retry only its skipped buyers using current setup; the retry creates a new queue without changing the saved buyer selection. An available older finished journal is imported with missing credit/timing information explicitly marked.

Choose **Automatic** in an FC's destination dropdown to select reachable suppliers per buyer. Selection prefers the current location, a previously successful destination, a matching FC estate workshop, an estate-accessible caretaker, then registered workshop/caretaker city routes. Only same-world registered houses and supported caretakers are considered. This is a deterministic preference ranking, not a measured shortest-path optimizer. Offline previews use that character's cached access; live access is checked before travel. A different eligible destination can be selected if the previous one is no longer reachable. Unknown access waits for login; no verified live choice skips that buyer with a reason. Explicit buyer house overrides and existing manual assignments remain fixed.

Supplier interaction failures still pause for diagnosis rather than abandoning an open menu or unresolved purchase. Pending purchases retain their original destination until tallied. No inventory publication or store hosting is included in this release.

## Version 0.5.1.0

The Fuel workspace now has three focused views: **Buyers**, **Destinations**, and **Run overview**. A persistent dashboard keeps Start/Pause/Resume, End queue, diagnostics and totals visible while the content scrolls. Wide windows show a world/FC navigator beside one company's setup; compact windows use a company selector. Destinations and the shared credit reserve are always above the buyer list. Select a buyer for purchase mode, storage preferences and route details.

Cached balances, route availability and queue outcomes use distinct colours and text labels. The destination library supports workshop registration and Lifestream imports; Resident Caretakers remain selectable directly in buyer setup. Run overview shows purchase plans before starting and bought/stored totals during a run. Saved settings, journals, purchasing, travel and deposit behavior are unchanged.

## Version 0.5.0.8

Resume and End queue now tally an exact saved fuel receipt directly from the original buyer's carried inventory before any supplier travel. A completed purchase can therefore be recovered while standing in a city, even when housing access is unavailable. Ending queues finish after that tally. Unchanged or ambiguous inventory retains its checkpoint rather than authorizing a blind repeat.

FC teleport access now resolves the actual packed estate identity. The shared-estate ward/plot fields are zero for FC entries and are no longer mistaken for an incomplete FC address. Both route selection and the check immediately before teleport use the resolved home-world estate. Diagnostics include the saved-receipt inventory and native estate identity.

## Version 0.5.0.7

Fuel setup shows each character's observed route availability, missing housing unlocks, and recommended alternatives. Access is cached separately per character from normal logins; selecting an offline character never borrows the current character's access or triggers a discovery login. Travel prefers an available FC estate teleport matching the chosen house, or uses that estate to reach a caretaker in the same district. City routes require the corresponding city attunement and housing unlock. Actual arrival and workshop permissions remain checked at the destination.

Fuel purchases now acknowledge the exact delivered tank quantity even when other FC activity changes the shared credit balance. This fixes the reported +99 tanks / 9,873 net credits decrease pause. Active buying and saved purchase recovery both use the original buyer, FC, quantity and unit price; credits alone never prove success, and partial deliveries are not retried. A conservative spending budget prevents a delayed credit display from renewing the next batch's allowance; the shop refreshes automatically when a fresh balance is needed. Existing 0.5.0.6 purchase checkpoints can be reconciled after updating, including queues already marked to end.

## Version 0.5.0.6

Fuel buying can recover automatically when the FC shop rejects a request with an inventory-space error before opening a confirmation. It waits for two seconds of unchanged inventory and credits in the original shop, then retries with a maximum of three total attempts. A confirmation or any observed item/credit change prevents retries; the saved purchase checkpoint remains intact. Diagnostics now include native shop capacity, displayed quantity, confirmation state and attempt history. Update and Resume the existing queue to tally the interrupted request and continue.

## Version 0.5.0.5

Fuel runs recognize the additional-chambers door by its localized name, as Lifestream does, instead of requiring one specific object ID. This fixes stopping after the saved workshop path completes in interiors with another door variant. The configured house and selected interaction target remain verified. Diagnostics now include nearby interior door IDs, names, targetability and matching results. Update and Resume the existing fuel queue; no path re-recording is needed.

## Version 0.5.0.4

Fuel runs now prefer Lifestream's recorded `PathToEntrance` and `PathToWorkshop` for the configured house, using Lifestream's own path follower. Registrations are matched by owner home world and exact district/ward/plot across this PC's roster; the two path sections stay together from one registration. Resume joins only a nearby recorded segment or returns to the plot drop-off once, preserving the recorded corners instead of cutting across a fence. Path completion requires observed arrival, and stalled movement stops with a useful diagnostic. Houses without a recorded section retain navigation for that section.

## Version 0.5.0.3

Fuel house entry now associates a visible entrance with the configured plot's game-sheet map marker, including subdivision plots. It no longer waits for a native entrance-VFX coordinate that can remain zero even when the house is visible. Competing plot markers and ambiguous entrances are rejected, and the full native house address is still checked before interaction. Existing paused queues can resume without recording a path or registering the house again.

## Version 0.5.0.2

Fuel travel now hands off from Lifestream's configured plot drop-off point to the house-entry route even when the game has not yet reported the character as standing on the plot. The handoff checks the home world, district, ward and proximity to that specific plot's entrance waypoint. Door approach remains bound to the selected estate, and interior/workshop checks retain the full address. Fuel diagnostics include the intended address and live arrival/door observations.

## Version 0.5.0.1

House discovery includes Lifestream's registered FC houses for local characters as well as house entries in its Address Book. Refresh reports how many houses were found, how many were newly added and how many are saved; an already-imported house no longer looks like a failed discovery. Workshop capture and destination verification share the active estate-address resolver, including the workshop's own housing data. Capture failures include diagnostic details for the unresolved address.

## Version 0.5.0

**Fuel & houses** now manages a sequential fuel queue on each PC. Characters are grouped by home world and FC, with an FC destination and optional credit reserve. Choose a registered workshop on the same world or a Resident Caretaker in an accessible residential district. Lifestream houses refresh automatically; additional houses can be registered by world, district, ward and plot. Character house overrides must also be on that character's home world.

Selected buyers log in, visit the supplier, buy fuel and deposit it with the same retainer movement engine used by transfer jobs. Credit-exhaustion runs repeat until the current FC balance reaches its reserve or no selected buyer can store more. A full buyer's storage leaves the remaining work for the next selected character in that FC. Deposits store **all carried tanks above the configured keep-carried amount, including pre-existing fuel**.

FC balances shown in setup are cached estimates; purchases check live credits and inventory changes. Progress is saved, reloads remain paused, and Resume tallies outstanding purchases or deposits before continuing. Fuel queues run independently of LAN pairing and cannot share a PC with an unfinished transfer job. See the [fuel queue setup and first-test guide](docs/fuel-queue-guide.md). This release's new fuel workflow has not yet been validated in-game.

## Version 0.4.1

**Item Search** is a read-only sidebar page available on either PC, including during transfer jobs. Type `coke, ceruleum, sap` to find any matching cached item. Results have one row per item, combined quantities, per-PC totals and a quality breakdown. Select a row to see the characters, retainers and storage holding it; sort by quantity or copy the results as a table.

Unpaired searches use only this PC. Pairing adds the other PC automatically; disconnecting removes its quantities. Allagan Tools supplies all cached personal inventory owners, including characters absent from AutoRetainer. Optional AutoRetainer data adds readable owner names; unavailable names use owner identifiers. Personal storage categories such as bags, retainers, saddlebags, armoury and market listings are included when cached; shared FC/house storage is excluded. Copied inventories on both PCs count once, preferring this PC's cache.

Searches run incrementally, support cancellation, and report partial/unavailable caches separately. Cache read time is not a last-observed game timestamp. A search never logs in, moves items, changes a job or pauses trading. Update both PCs to 0.4.1.0 before pairing (protocol 7).

## Version 0.4.0

Multi-item jobs plan all item budgets first, then group work around character visits. A provider prepares the compatible items for its current recipient or relay before travelling. Trades combine exact item/quality lines within the game's five-slot limit and shared receiving inventory capacity. Each item retains its own journal, recipient priority and quantity target. Full relays collect and deliver across item types; final-delivery stock can be deposited to make room, while relay cargo stays carried.

Interrupted mixed trades tally every item on both PCs before continuing. Existing saved jobs upgrade at an idle scheduling boundary after their pending operations are resolved, preserving allocations, completed deliveries and relay cargo. Both PCs must update to this build (protocol 6).

Temporary vnavmesh loading now waits and continues automatically for both meeting-point and bell approaches. Actual route failures or a mesh that still has not loaded after five minutes remain visible pauses.

## Runtime requirements

Version 0.3.11.1 skips characters missing from the configured roster or without complete Allagan Tools/AutoRetainer inventory data. Activity shows an amber notice with the character and reason, including results from earlier item lines. Missing inventory is unknown, never a fabricated zero: skipped providers supply nothing and skipped recipients receive nothing, including restock targets. An item with no usable providers, recipients, or required relay after these exclusions is marked skipped and the next item continues. Original job settings and already-issued journals are preserved.

Version 0.3.11.0 prepares both participants concurrently. One PC can travel while the other logs in or collects/deposits items, with one native operation per PC and a shared arrival barrier before trading. Activity shows each character's current action. Post-login teleport requests wait for game readiness and use bounded retries only when Lifestream has rejected the request; accepted requests are not repeated. Existing trade idle-state fixes remain in place.

Both PCs need the same Ashe's Trader build, Dalamud API 15, AutoRetainer configured for their own account's characters, Lifestream, vnavmesh, and Allagan Tools. Planning uses the cached counts returned by Allagan Tools. Unavailable or malformed character data produces a skip notice; it does not start character visits. Connection, journal and issued-operation failures still pause for recovery.

The accounts must be able to meet on the selected world. Use an outdoor meeting point in the same territory as its main aetheryte. Default retainer access uses Lifestream's home-world inn shortcut; a per-character home-world bell location can be captured instead. Characters must have the required travel unlocks and resources.

Pause AutoRetainer multi-mode and other conflicting automation before starting. Ashe's Trader temporarily suppresses AutoRetainer's automatic bell handling while it owns retainer operations and restores it afterward.

## Install through the custom repository

Custom repository URL:

`https://raw.githubusercontent.com/maryamaahmed1991/AsheTrader/main/repo.json`

Add that URL on both PCs under `/xlsettings` → Experimental → Custom Plugin Repositories, enable the repository, and save. Search for **Ashe's Trader** in `/xlplugins` and install it. Open `/ashetrader`, then follow [the two-PC test guide](docs/two-pc-test-guide.md) for transfers or the [fuel queue guide](docs/fuel-queue-guide.md) for fuel buying.

If you already loaded a development copy, disable that copy before installing the repository version. Keep your existing configuration and journals.

## Install a local development build

1. Build using `pwsh ./scripts/Build.ps1`, or use the supplied `AsheTrader-0.5.0.8.zip`.
2. On **each** PC, extract the ZIP to a stable local directory. Keep both `AsheTrader.dll` and `AsheTrader.Core.dll`, plus the manifest, together.
3. In `/xlsettings` → Experimental → Dev Plugin Locations, add the full path to `AsheTrader.dll`.
4. Enable it under `/xlplugins` → Dev Tools. Open `/ashetrader`.
5. Follow [the two-PC test guide](docs/two-pc-test-guide.md) or [fuel queue guide](docs/fuel-queue-guide.md).

The plugin does not open a network listener or resume gameplay merely by loading. Pairing and jobs are explicitly started in the UI. `/ashetrader stop` pauses the current operation and notifies the peer.

## Pair without entering an IP address

Open **Connection**. On the coordinator, choose **Coordinator · I create jobs here** and press **Host for my other PC**; an empty pairing key is generated automatically. The plugin selects an active home-network adapter, with an override under Advanced. On the worker, choose **Worker · follow my other PC**, enter the same key once, press **Find my coordinator**, and click **Connect to this PC** beside the coordinator's label. Enable **Allow the coordinator to run jobs on this PC**. Both PCs remember the key and authenticated peer identity, so later sessions need only Host, Find, and Connect. Update both PCs to the same build before pairing; version 0.3.0 exchanges the AutoRetainer slot metadata required for automatic relays and rejects older peers before exchanging commands.

Discovery uses small UDP queries and replies on port 45875 within the local subnet. It shares the host's label, peer ID, and TCP port; no pairing key, inventory, or commands are sent over discovery. The existing authenticated, encrypted TCP connection still verifies the selected peer before exchanging rosters or running jobs. Results expire when a coordinator stops responding, and discovery never starts gameplay or resumes a paused job.

If no coordinator appears, check that both PCs use the same home network and the coordinator is hosting. Guest Wi-Fi isolation, separate subnets, or a firewall can block discovery. **Advanced / manual connection** retains the original IP/port workflow. No internet discovery server or router port forwarding is used.

## Job setup

On **Connection**, connect the PCs and refresh both character lists, then create a job in **Jobs** on the coordinator. Each character must belong to only one PC's roster. Account labels are descriptive; peer identity and character content ID determine execution ownership. New jobs default to an additional delivery amount, with ten items for each new recipient; existing saved job quantities are preserved.

**Review & run** summarizes the intended transfer and explains missing setup. **Check cached inventories only** reads each PC's Allagan Tools cache without changing characters, traveling or opening retainers. Missing inventory or retainer ownership data skips the character and records the reason and owning PC. The meeting point is needed for transfers, not cache lookup. **Activity** labels cached estimates and live observations separately and keeps verified pickups, deposits and final deliveries separate.

Select a character under **Jobs**, search for an item and add it to that character, then set its amount. Repeat for other recipients. Adding Fuel to a second recipient reuses the canonical Fuel definition without assigning that recipient every other item. New items inherit the first item's provider list; choose or adjust providers in the provider card. Quantity mode is shared by all recipients of an item and is labeled explicitly. Items share visits, while their budgets and priority rules remain separate. The Review page lists only the actual recipient/item assignments.

Recipients are ordered within each item. A finite amount means additional delivery in Move all/Deliver amount modes and final stock in Restock to total mode. Only the final recipient may take an unlimited remainder, and Restock to total requires explicit finite targets. A shortage on one item is recorded while the remaining items continue. An interrupted operation pauses the job before moving to another item. If an earlier line's received items fill the current provider or recipient's bags, the job can store those received items in retainers to make room; that movement stays charged to the earlier item and never changes its delivered total.

Provider selection prefers direct cross-account routes, matching home worlds, reuse of already assigned providers, then larger available quantities. Recipient priority is preserved. Zero-stock providers, unused providers and recipients with no funded allocation do not log in. Relays are chosen only for funded same-account routes: estimated loads from cached free slots come first, then home-world locality and capacity. Only the selected relay is inspected. Unexpected fullness pauses with its name instead of logging through other alts. Known zero-slot candidates may still have mergeable stacks; unknown counts are not treated as verified zero.

The relay collects from multiple providers for the current priority recipient until full or until those providers have contributed their assigned quantities. It then travels to the final recipient's home world and drains the collected load there before collecting more. The recipient can make repeated retainer deposits without world/DC travel, while the relay waits. Relay cargo is never sent to its retainers to make space. Each source allocation retains its own trade accounting, even when cargo shares a physical stack; final delivery may therefore use several trade batches without switching characters. Compatible item lines share visits and trade windows. Allocations and relay identities remain fixed for recovery; each active travel/trade leg has one pinned meeting destination. A full relay with cargo delivers it; a full relay without cargo pauses for space.

The saved meeting point is a reusable territory, position and arrival aetheryte; its captured world does not constrain travel. For provider pickups, recipients and relays visit provider home worlds so providers can refill from retainers locally. The funded itinerary groups each collector's providers by DC, then world, beginning in the collector's observed DC where possible. Activity shows the itinerary and active meeting world. Recipient priority within each item remains strict; a full relay's delivery detour, storage trips or later recipients/items can require DC revisits. This is a practical grouping policy, not a global travel optimum. Lifestream must permit the necessary DC travel and the game's destination restrictions still apply. Existing journals adopt the itinerary at a safe scheduling boundary after pending outcomes are reconciled, retaining all allocations and delivered counts.

Capture the rendezvous while standing at an accessible outdoor point. Verify the aetheryte belongs to that territory. Capture a custom storage bell only on that character's home world; leaving it unset uses an inn. Retainer allowlists govern destination deposits; an empty allowlist means all available retainers.

Provider bags and retainer stored inventory are included automatically. When a provider runs out of carried stock, it returns home to collect another load, limited by its current bag capacity and the remaining assignment. It trades that load across as many batches as needed before another storage trip. Other storage types, equipped items, retainer sale listings, gil and crystals are outside this build's job scope. Items must be tradeable and match the selected quality.

## Progress and recovery

Version 0.3.11 runs preparation independently on the two PCs for each funded trade leg. Each PC still performs its own operations sequentially; a character never travels during its own retainer movement. Both participants share one pinned destination, and batch preview/arming cannot start until both are ready there. If either side pauses, Resume recovers both original command IDs and any storage progress before scheduling new work. Legacy pending operations complete through their existing recovery path; the next delivery uses parallel preparation. Item priorities, relay collection and cutscene preservation are unchanged.

Version 0.3.10 uses the offer spacing observed in Dropbox's public source: a single 250 ms minimum between item callbacks, readiness polling for quantities and synchronization, and consecutive matching observations before accepting an incoming offer or settling inventory. The previous stacked item delays and post-trade sleeps are removed. Trade openings retain a three-second minimum between requests across batches, stable partner readiness, distance checks and bounded retries. Game/server latency still determines actual throughput. The implementation uses current Dalamud structures and independently written logic; it does not embed Dropbox's older native offsets.

Interrupted work uses **Resume / tally inventories** instead of review checkboxes. Both PCs must run this version (wire protocol 5). Existing configuration and journals remain in place. The tally reads the original characters' live item/quality counts, reconciles a completed or unchanged trade, and continues the remaining assignment. Conflicting evidence or missing historical storage checkpoints remains visible in Activity.

Version 0.3.8 applies the 350 ms acknowledgement-driven movement loop to deposits too. It handles partial-stack quantity dialogs owned by either the retainer inventory or the depositing character's inventory, while checking the pending source slot, retainer identity, dialog ownership and limits. A 175/175 retainer can still accept matching partial-stack space; once item capacity reaches zero, collection proceeds to the next permitted retainer. Inn arrival now allows a fresh loading grace after Lifestream becomes idle, instead of measuring the entire journey against a ten-second missing-bell check. Loading, territory changes and cutscenes restart that grace. The coordinate-only meeting template and DC-grouped collection itinerary replace the earlier single-world meeting policy.

Version 0.3.7 makes retainer withdrawals readiness-driven, following the approach used by AutoRetainer reverse entrust. Full stacks use the native retrieve command; partial stacks use retrieve-quantity and the owned quantity dialog. A single 350 ms minimum dispatch interval replaces accumulated per-stack waits. Each next command still requires two observations of the exact matching changes in both bags and retainer storage, with all other item/HQ totals unchanged. Opening and closing storage advances when the required UI is ready; loaded inventory pages still receive a short stability check. Uncertain movements pause without being retried. Diagnostics include each confirmed withdrawal's elapsed time. Live game latency can lengthen the interval; this is not a measured throughput guarantee.

Version 0.3.6 adds relay collection/delivery phases, funded-character selection, source/relay cost ranking and automatic meeting-world choice. Saved relay cargo and pending trades are reconciled before scheduling new work. Inn cutscenes pause gameplay and deadlines until they finish naturally. Before relog, Ashe's Trader checks AutoRetainer's independent inn-skip setting. If it would skip, set **AutoRetainer → Advanced → Expert Settings → Skip Inn Login Cutscene → Never**, then resume. External plugin settings are not changed automatically.

Version 0.3.5 removes automatic discovery visits and full scans after login, including recipient restock scans. Complete empty groups returned by Allagan Tools are accepted as cached zero estimates; version 0.3.11.1 skips characters whose groups are absent or malformed. Actual stock and receiving capacity are checked when moving items. Bell opening now waits for interaction readiness and uses bounded retries if the first attempt receives no acknowledgement. Copied diagnostics keep the newest trace within a paste-friendly size.

Version 0.3.4 fixed the confirmed retainer-menu stall: the game inserts a slot count into the storage option, so comparing it with the unformatted sheet text never matched. Storage selection uses the first localized text payload and requires one unique matching option. Quit still requires an exact match. User diagnostics confirm that all ten retainers on one character opened and closed successfully.

Version 0.3.3 adds automatic detailed diagnostics. On the affected PC, open **Transfers → 4. Activity → Copy diagnostics** while the menu is still stuck, then paste the report when reporting the issue. The bounded session trace includes menu text/match results, native action attempts, elapsed stages, retainer identity and inventory loading. It excludes pairing keys, full configuration, chat and dialogue bodies. Capture before reloading the plugin; detailed records also appear in Dalamud's log under `[WorkflowTrace]`. Capturing a report is read-only and does not resume or repeat an action.

AutoRetainer supplies the offline retainer ownership list; Allagan Tools supplies cached item/HQ bag and retainer counts. Cache retrieval time is not the last inventory observation time. **Restock to total** calculates the amount to deliver from the cached total, so a stale cache can change the final stock relative to the target. Actual pickups, receiving space and trades are checked live. A source overestimate pauses when stock runs out; newly added stock outside the cached budget needs a later job. Issued plans remain fixed so recovery cannot duplicate work.

Version 0.3.1 fixes character switching from inside the game. AutoRetainer's `CanAutoLogin` check and `Relog` IPC only allow the title screen; the adapter now uses its documented `/autoretainer relog Character Name@World` command, applying title readiness only while logged out. A dispatched command is not treated as a completed switch: the workflow waits for the requested character CID and idle AutoRetainer/Lifestream. If the switch stops without reaching the target, it pauses instead of repeatedly issuing commands.

Trades commit from matching native results or from a paired recovery round with matching sender/receiver inventory changes and durable worker acknowledgements. Completed transfers are credited once. Delivery totals do not decrease when recipients store items on retainers. Relay holdings remain pending delivery until the final leg commits.

If a client disconnects or the user pauses, reconnect and press **Resume / tally inventories**. The plugin retrieves saved outcomes and current inventories before issuing more work. Both original trade characters must be logged in for a fresh tally. Matching full transfer deltas credit the batch; unchanged inventories permit a bounded retry. Partial, one-sided or otherwise conflicting trade changes stop with the observed discrepancy instead of asking the user to choose an outcome.

Retainer pickups and deposits save a checkpoint before each item callback and after each confirmed movement. Recovery compares the current inventory with that checkpoint, accounts for only the newly observed quantity and continues the remaining work. Old ambiguous records created before these checkpoints may lack enough evidence to recover automatically; preserve their diagnostics. Saved successful outcomes remain recoverable without repeating a movement.

Overflow deposits are limited to received job-owned quantities. If a receiver starts completely full with no received job items to store, free space before resuming. If all permitted retainers are full, the job pauses with the unresolved quantity. When source stock is insufficient, available allocations finish and the result lists unmet targets; it is a terminal partial result, not a claim that the requested totals were reached. Create a new job for the remaining amounts after replenishing sources.

Settings and journals live under Dalamud's `pluginConfigs/AsheTrader` directory: `settings.json`, `transfer-job.json`, and `worker-journal.json`. Preserve both PCs' journals when reporting a recovery issue. Settings contain the pairing secret; remove that field before sharing them. A running job uses a saved copy of its rules, so editing a saved template does not rewrite in-flight quantities.

## Build and distribution

### Saddlebag cargo and Utilities (0.6.4)

Update both PCs before testing saddlebag cargo. Each character has **Use spare saddlebag space for trade cargo** under its storage settings. It is enabled by default. Providers can pack collected items before another retainer pickup; relays and recipients can pack received job items to extend a trip. Outgoing trades still use ordinary inventory, so cargo is unpacked as room becomes available. Premium slots are included only when the current character has premium access and those native containers have loaded. Unrelated contents are preserved. Packing uses whole stacks or matching-stack merges within the job allowance; it does not force a stack split or guarantee that every spare slot can be used. Existing saddlebag contents are not added to source discovery. Activity shows quantities packed by the job.

Cargo operations save an absolute saddlebag target and a conserved bag-plus-saddlebag total before movement. Resume waits for pending native operations, checks both inventories and reconciles the original target rather than repeating a quantity. Keep the affected character logged in if a cargo operation is interrupted. Losing premium access while it holds job cargo pauses that cargo rather than treating it as missing.

**Utilities → Unlock saddlebags** runs **My Feisty Little Chocobo** for selected local AutoRetainer characters. Use **Refresh AutoRetainer characters**, **Select All / Select None**, or individual checkboxes, then **Start selected characters**. Questionable must already be installed with working navigation/combat dependencies. The queue uses its `StartSingleQuest("1162")` IPC and leaves its priority list unchanged. The installed Questionable 15.756.3.60 IPC signatures were checked directly; older versions without these endpoints must be updated. After login, completed characters are skipped, while level/prerequisite failures are listed with a reason. This utility does not level characters, complete unrelated prerequisite quests, or grant premium saddlebag access.

Utilities is independent on each PC and excludes simultaneous local fuel/transfer work. **Pause**, **Resume**, **End queue**, and **Copy diagnostics** are available in the tab. Reloaded queues remain paused; resume rechecks live quest completion. Queue progress is stored in `utility-queue.json` beside the existing journals.

**Enable AR Multi Mode after completed job** is an opt-in checkbox on Transfers → Review & run, Fuel, and Utilities. The choice is saved with a transfer template or copied into a new fuel/utility run. Transfers hand both paired PCs back to their existing AutoRetainer setup; fuel and utility runs hand back only their own PC. Update both PCs to 0.6.4.1 or later for paired handoff. The entire queue must finish naturally; pausing, ending early, errors, and discovery-only scans do not enable Multi Mode. Naturally finished transfers with shortages or skipped characters still enable it; unmet quantities and skipped/ineligible entries remain visible in the results. The handoff waits up to 60 seconds for local game operations to become idle, runs once, and reports failures in Activity/diagnostics. A new job or Stop cancels a waiting handoff. Reloading the plugin does not replay completion actions.

First live validation: select one already-unlocked character and one eligible level-30 character in Utilities; confirm the first is skipped and Questionable completes only the specified quest for the second. Then test a small transfer that exceeds one character's ordinary free space, checking Activity's packed counts and final totals. Repeat with a premium character, a relay, and pause/resume during cargo preparation. Automated tests cover queue sequencing and simulated cargo movement; live game callbacks still need this validation.

The solution is `AsheTrader.slnx`. `scripts/Build.ps1` runs tests, builds against the local Dalamud development libraries, and produces a package under `artifacts/`. The GitHub workflow performs the same checks and uploads build artifacts; it does not publish releases automatically.

Once a GitHub repository exists:

1. Push this source, preserving the license and notices.
2. Upload the built `AsheTrader-<version>.zip` as a public release asset.
3. Run `pwsh ./scripts/New-Repository.ps1 -GitHubRepository OWNER/REPOSITORY -ReleaseTag v0.3.10.1`.
4. Commit the generated root `repo.json` on `main`. Its raw GitHub URL is the permanent custom-repository feed; update that file for each release.

Source is hosted in [maryamaahmed1991/AsheTrader](https://github.com/maryamaahmed1991/AsheTrader), with installable packages under [Releases](https://github.com/maryamaahmed1991/AsheTrader/releases). Pairing secrets and runtime journals must not be committed or included in release artifacts.

See [the product specification](docs/transfer-jobs.md) and [third-party notices](THIRD-PARTY-NOTICES.md).
