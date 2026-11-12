# Ashe's Trader

Dalamud API 15 / .NET 10 plugin for two-account item transfers across two PCs on a home network.

This is a development build prepared for staged two-PC testing. The planner, journals, LAN transport, and coordinator have automated tests; native game callbacks, retainer UI transitions, and travel need in-game validation. No live trades or purchases have been performed during development.

## Features

- Follow four guided steps: Connect PCs, Set up, Review & run, and Activity. The worker has its own connected-and-ready view; only the coordinator configures jobs.
- Pair one coordinator PC and one worker PC using an authenticated, encrypted LAN connection.
- Find nearby coordinators by PC label without copying IP addresses. Hosting chooses a local network adapter automatically; manual connection remains available.
- Import each PC's AutoRetainer roster and plan from Allagan Tools' cached character and retainer inventories.
- Save multiple item lines in one job, each with providers, ordered recipients, quantities and quality, sharing one meeting point.
- Choose providers and relays using funded quantities, direct routes, home worlds and estimated relay loads. Only assigned providers, recipients and needed relays log in.
- Fill relays across multiple providers before delivering, then drain the collected load before gathering again.
- Preserve inn cutscenes and wait for their natural completion before continuing.
- Read status at a glance: green for ready/success, blue for active information, amber for waiting or attention, and red for blockers/errors. Text labels remain alongside colors.
- Plan from Allagan Tools' cached inventories without discovery logins or repeat retainer scans, including restock jobs. Unavailable cache data pauses with the affected character.
- Allocate additional quantities, collect all with a final remainder recipient, or restock to a combined bag/retainer total.
- Trade providers' carried stock first, then repeatedly collect bounded loads from their retainers and return to the meeting point until their assignments are complete.
- Return a full receiver home, deposit received job items into allowed retainers, then return and continue.
- Persist both worker results and coordinator progress. Interrupted trades with incomplete evidence require explicit reconciliation.
- Preserve the local trade-list mode, workshop fuel purchasing, and Lifestream house import.

## Runtime requirements

Both PCs need the same Ashe's Trader build, Dalamud API 15, AutoRetainer configured for their own account's characters, Lifestream, vnavmesh, and Allagan Tools. Planning uses the cached counts returned by Allagan Tools. Unavailable or malformed cache data pauses planning; it does not start character visits.

The accounts must be able to meet on the selected world. Use an outdoor meeting point in the same territory as its main aetheryte. Default retainer access uses Lifestream's home-world inn shortcut; a per-character home-world bell location can be captured instead. Characters must have the required travel unlocks and resources.

Pause AutoRetainer multi-mode and other conflicting automation before starting. Ashe's Trader temporarily suppresses AutoRetainer's automatic bell handling while it owns retainer operations and restores it afterward.

## Install through the custom repository

Custom repository URL:

`https://raw.githubusercontent.com/maryamaahmed1991/AsheTrader/main/repo.json`

Add that URL on both PCs under `/xlsettings` → Experimental → Custom Plugin Repositories, enable the repository, and save. Search for **Ashe's Trader** in `/xlplugins` and install it. Open `/ashetrader`, then follow [the two-PC test guide](docs/two-pc-test-guide.md).

If you already loaded a development copy, disable that copy before installing the repository version. Keep your existing configuration and journals.

## Install a local development build

1. Build using `pwsh ./scripts/Build.ps1`, or use the supplied `AsheTrader-0.3.8.0.zip`.
2. On **each** PC, extract the ZIP to a stable local directory. Keep both `AsheTrader.dll` and `AsheTrader.Core.dll`, plus the manifest, together.
3. In `/xlsettings` → Experimental → Dev Plugin Locations, add the full path to `AsheTrader.dll`.
4. Enable it under `/xlplugins` → Dev Tools. Open `/ashetrader`.
5. Follow [the two-PC test guide](docs/two-pc-test-guide.md).

The plugin does not open a network listener or resume gameplay merely by loading. Pairing and jobs are explicitly started in the UI. `/ashetrader stop` pauses the current operation and notifies the peer.

## Pair without entering an IP address

Open **Transfers → 1. Connect PCs**. On the coordinator, choose **This PC controls transfers** and press **Host for my other PC**; an empty pairing key is generated automatically. The plugin selects an active home-network adapter, with an override under Advanced. On the worker, choose **This PC receives instructions**, enter the same key once, press **Find my coordinator**, and click **Connect to this PC** beside the coordinator's label. Enable **Allow the coordinator to run jobs on this PC**. Both PCs remember the key and authenticated peer identity, so later sessions need only Host, Find, and Connect. Update both PCs to the same build before pairing; version 0.3.0 exchanges the AutoRetainer slot metadata required for automatic relays and rejects older peers before exchanging commands.

Discovery uses small UDP queries and replies on port 45875 within the local subnet. It shares the host's label, peer ID, and TCP port; no pairing key, inventory, or commands are sent over discovery. The existing authenticated, encrypted TCP connection still verifies the selected peer before exchanging rosters or running jobs. Results expire when a coordinator stops responding, and discovery never starts gameplay or resumes a paused job.

If no coordinator appears, check that both PCs use the same home network and the coordinator is hosting. Guest Wi-Fi isolation, separate subnets, or a firewall can block discovery. **Advanced / manual connection** retains the original IP/port workflow. No internet discovery server or router port forwarding is used.

## Job setup

Under **Transfers**, connect the PCs and refresh both character lists, then create a job in **2. Set up** on the coordinator. Each character must belong to only one PC's roster. Account labels are descriptive; peer identity and character content ID determine execution ownership. New jobs default to an additional delivery amount, with ten items for each new recipient; existing saved job quantities are preserved.

**3. Review & run** summarizes the intended transfer and explains missing setup. **Check inventories only** reads each PC's Allagan Tools cache without changing characters, traveling or opening retainers. Missing inventory or retainer ownership data pauses with the character and PC that need attention. The meeting point is needed for transfers, not cache lookup. **4. Activity** labels cached estimates and live observations separately and keeps verified pickups, deposits and final deliveries separate.

Add each requested item under the same saved job. Each item line has its own providers, ordered recipients and quantity mode. Lines run in order, with a new cache lookup and separate delivery accounting for each. The same item and quality may appear once per job; combine its recipient rules on that line. Existing single-item jobs remain usable.

Recipients are ordered within each item. A finite amount means additional delivery in Move all/Deliver amount modes and final stock in Restock to total mode. Only the final recipient may take an unlimited remainder, and Restock to total requires explicit finite targets. A shortage on one item is recorded while the remaining items continue. An interrupted operation pauses the job before moving to another item. If an earlier line's received items fill the current provider or recipient's bags, the job can store those received items in retainers to make room; that movement stays charged to the earlier item and never changes its delivered total.

Provider selection prefers direct cross-account routes, matching home worlds, reuse of already assigned providers, then larger available quantities. Recipient priority is preserved. Zero-stock providers, unused providers and recipients with no funded allocation do not log in. Relays are chosen only for funded same-account routes: estimated loads from cached free slots come first, then home-world locality and capacity. Only the selected relay is inspected. Unexpected fullness pauses with its name instead of logging through other alts. Known zero-slot candidates may still have mergeable stacks; unknown counts are not treated as verified zero.

The relay collects from multiple providers for the current priority recipient until full or until those providers have contributed their assigned quantities. It then travels to the final recipient's home world and drains the collected load there before collecting more. The recipient can make repeated retainer deposits without world/DC travel, while the relay waits. Relay cargo is never sent to its retainers to make space. Each source allocation retains its own trade accounting, even when cargo shares a physical stack; final delivery may therefore use several trade batches without switching characters. Item lines still run sequentially. Allocations and relay identities remain fixed for recovery; each active travel/trade leg has one pinned meeting destination. A full relay with cargo delivers it; a full relay without cargo pauses for space.

The saved meeting point is a reusable territory, position and arrival aetheryte; its captured world does not constrain travel. For provider pickups, recipients and relays visit provider home worlds so providers can refill from retainers locally. The funded itinerary groups each collector's providers by DC, then world, beginning in the collector's observed DC where possible. Activity shows the itinerary and active meeting world. Recipient priority and item order remain strict; a full relay's delivery detour, storage trips or later recipients/items can require DC revisits. This is a practical grouping policy, not a global travel optimum. Lifestream must permit the necessary DC travel and the game's destination restrictions still apply. Existing journals adopt the itinerary at a safe scheduling boundary after pending outcomes are reconciled, retaining all allocations and delivered counts.

Capture the rendezvous while standing at an accessible outdoor point. Verify the aetheryte belongs to that territory. Capture a custom storage bell only on that character's home world; leaving it unset uses an inn. Retainer allowlists govern destination deposits; an empty allowlist means all available retainers.

Provider bags and retainer stored inventory are included automatically. When a provider runs out of carried stock, it returns home to collect another load, limited by its current bag capacity and the remaining assignment. It trades that load across as many batches as needed before another storage trip. Other storage types, equipped items, retainer sale listings, gil and crystals are outside this build's job scope. Items must be tradeable and match the selected quality.

## Progress and recovery

Version 0.3.8 applies the 350 ms acknowledgement-driven movement loop to deposits too. It handles partial-stack quantity dialogs owned by either the retainer inventory or the depositing character's inventory, while checking the pending source slot, retainer identity, dialog ownership and limits. A 175/175 retainer can still accept matching partial-stack space; once item capacity reaches zero, collection proceeds to the next permitted retainer. Inn arrival now allows a fresh loading grace after Lifestream becomes idle, instead of measuring the entire journey against a ten-second missing-bell check. Loading, territory changes and cutscenes restart that grace. The coordinate-only meeting template and DC-grouped collection itinerary replace the earlier single-world meeting policy.

Version 0.3.7 makes retainer withdrawals readiness-driven, following the approach used by AutoRetainer reverse entrust. Full stacks use the native retrieve command; partial stacks use retrieve-quantity and the owned quantity dialog. A single 350 ms minimum dispatch interval replaces accumulated per-stack waits. Each next command still requires two observations of the exact matching changes in both bags and retainer storage, with all other item/HQ totals unchanged. Opening and closing storage advances when the required UI is ready; loaded inventory pages still receive a short stability check. Uncertain movements pause without being retried. Diagnostics include each confirmed withdrawal's elapsed time. Live game latency can lengthen the interval; this is not a measured throughput guarantee.

Version 0.3.6 adds relay collection/delivery phases, funded-character selection, source/relay cost ranking and automatic meeting-world choice. Saved relay cargo and pending trades are reconciled before scheduling new work. Inn cutscenes pause gameplay and deadlines until they finish naturally. Before relog, Ashe's Trader checks AutoRetainer's independent inn-skip setting. If it would skip, set **AutoRetainer → Advanced → Expert Settings → Skip Inn Login Cutscene → Never**, then resume. External plugin settings are not changed automatically.

Version 0.3.5 removes automatic discovery visits and full scans after login, including recipient restock scans. Complete empty groups returned by Allagan Tools are accepted as cached zero estimates; absent or malformed groups still pause. Actual stock and receiving capacity are checked when moving items. Bell opening now waits for interaction readiness and uses bounded retries if the first attempt receives no acknowledgement. Copied diagnostics keep the newest trace within a paste-friendly size.

Version 0.3.4 fixed the confirmed retainer-menu stall: the game inserts a slot count into the storage option, so comparing it with the unformatted sheet text never matched. Storage selection uses the first localized text payload and requires one unique matching option. Quit still requires an exact match. User diagnostics confirm that all ten retainers on one character opened and closed successfully.

Version 0.3.3 adds automatic detailed diagnostics. On the affected PC, open **Transfers → 4. Activity → Copy diagnostics** while the menu is still stuck, then paste the report when reporting the issue. The bounded session trace includes menu text/match results, native action attempts, elapsed stages, retainer identity and inventory loading. It excludes pairing keys, full configuration, chat and dialogue bodies. Capture before reloading the plugin; detailed records also appear in Dalamud's log under `[WorkflowTrace]`. Capturing a report is read-only and does not resume or repeat an action.

AutoRetainer supplies the offline retainer ownership list; Allagan Tools supplies cached item/HQ bag and retainer counts. Cache retrieval time is not the last inventory observation time. **Restock to total** calculates the amount to deliver from the cached total, so a stale cache can change the final stock relative to the target. Actual pickups, receiving space and trades are checked live. A source overestimate pauses when stock runs out; newly added stock outside the cached budget needs a later job. Issued plans remain fixed so recovery cannot duplicate work.

Version 0.3.1 fixes character switching from inside the game. AutoRetainer's `CanAutoLogin` check and `Relog` IPC only allow the title screen; the adapter now uses its documented `/autoretainer relog Character Name@World` command, applying title readiness only while logged out. A dispatched command is not treated as a completed switch: the workflow waits for the requested character CID and idle AutoRetainer/Lifestream. If the switch stops without reaching the target, it pauses instead of repeatedly issuing commands.

Only matching two-sided native results commit a trade automatically. Delivery totals do not decrease when recipients store items on their retainers. Relay holdings are pending delivery until the final leg commits.

If a client disconnects, a character changes unexpectedly, storage fills, or a native interaction cannot be verified, the job pauses. Reconnect and press Resume. Saved completed results are reconciled; an ambiguous trade is not replayed. If the original outcome was never saved, log in the original characters, inspect both inventories, and use the explicit reconciliation controls only after checking the evidence.

Retainer pickups and deposits recover their original saved result before moving another load. If a crash or interrupted callback leaves the last storage movement uncertain, the job remains paused. Inspect the bags and retainers, end the paused job once no trade or relay cargo remains, then create a freshly scanned job for the remaining delivery amounts. This build does not infer an uncertain storage movement from an aggregate count change.

Overflow deposits are limited to received job-owned quantities. If a receiver starts completely full with no received job items to store, free space before resuming. If all permitted retainers are full, the job pauses with the unresolved quantity. When source stock is insufficient, available allocations finish and the result lists unmet targets; it is a terminal partial result, not a claim that the requested totals were reached. Create a new job for the remaining amounts after replenishing sources.

Settings and journals live under Dalamud's `pluginConfigs/AsheTrader` directory: `settings.json`, `transfer-job.json`, and `worker-journal.json`. Preserve both PCs' journals when reporting a recovery issue. Settings contain the pairing secret; remove that field before sharing them. A running job uses a saved copy of its rules, so editing a saved template does not rewrite in-flight quantities.

## Build and distribution

The solution is `AsheTrader.slnx`. `scripts/Build.ps1` runs tests, builds against the local Dalamud development libraries, and produces a package under `artifacts/`. The GitHub workflow performs the same checks and uploads build artifacts; it does not publish releases automatically.

Once a GitHub repository exists:

1. Push this source, preserving the license and notices.
2. Upload the built `AsheTrader-<version>.zip` as a public release asset.
3. Run `pwsh ./scripts/New-Repository.ps1 -GitHubRepository OWNER/REPOSITORY -ReleaseTag v0.3.8.0`.
4. Commit the generated root `repo.json` on `main`. Its raw GitHub URL is the permanent custom-repository feed; update that file for each release.

Source is hosted in [maryamaahmed1991/AsheTrader](https://github.com/maryamaahmed1991/AsheTrader), with installable packages under [Releases](https://github.com/maryamaahmed1991/AsheTrader/releases). Pairing secrets and runtime journals must not be committed or included in release artifacts.

See [the product specification](docs/transfer-jobs.md) and [third-party notices](THIRD-PARTY-NOTICES.md).
