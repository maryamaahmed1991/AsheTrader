# Ashe's Trader

Dalamud API 15 / .NET 10 plugin for two-account item transfers across two PCs on a home network.

This is a development build prepared for staged two-PC testing. The planner, journals, LAN transport, and coordinator have automated tests; native game callbacks, retainer UI transitions, and travel need in-game validation. No live trades or purchases have been performed during development.

## Features

- Pair one coordinator PC and one worker PC using an authenticated, encrypted LAN connection.
- Import each PC's AutoRetainer roster and read optional Allagan Tools inventory hints.
- Save one-item transfer jobs with source characters, ordered recipients, quantities, HQ selection, relay characters, and a meeting point.
- Discover selected characters' carried inventories and every retainer before planning. A scan-only action performs no trades.
- Allocate additional quantities, collect all with a final remainder recipient, or restock to a combined bag/retainer total.
- Change characters, collect items from retainers, travel to the meeting point, and execute agreed batches.
- Return a full receiver home, deposit received job items into allowed retainers, then return and continue.
- Persist both worker results and coordinator progress. Interrupted trades with incomplete evidence require explicit reconciliation.
- Preserve the local trade-list mode, workshop fuel purchasing, and Lifestream house import.

## Runtime requirements

Both PCs need the same Ashe's Trader build, Dalamud API 15, AutoRetainer configured for their own account's characters, Lifestream, and vnavmesh. Allagan Tools is optional: its cache supplies hints, while live discovery supplies the verified counts used by a job.

The accounts must be able to meet on the selected world. Use an outdoor meeting point in the same territory as its main aetheryte. Default retainer access uses Lifestream's home-world inn shortcut; a per-character home-world bell location can be captured instead. Characters must have the required travel unlocks and resources.

Pause AutoRetainer multi-mode and other conflicting automation before starting. Ashe's Trader temporarily suppresses AutoRetainer's automatic bell handling while it owns retainer operations and restores it afterward.

## Install through the custom repository

Custom repository URL:

`https://raw.githubusercontent.com/maryamaahmed1991/AsheTrader/main/repo.json`

Add that URL on both PCs under `/xlsettings` → Experimental → Custom Plugin Repositories, enable the repository, and save. Search for **Ashe's Trader** in `/xlplugins` and install it. Open `/ashetrader`, then follow [the two-PC test guide](docs/two-pc-test-guide.md).

If you already loaded a development copy, disable that copy before installing the repository version. Keep your existing configuration and journals.

## Install a local development build

1. Build using `pwsh ./scripts/Build.ps1`, or use the supplied `AsheTrader-0.2.0.0.zip`.
2. On **each** PC, extract the ZIP to a stable local directory. Keep both `AsheTrader.dll` and `AsheTrader.Core.dll`, plus the manifest, together.
3. In `/xlsettings` → Experimental → Dev Plugin Locations, add the full path to `AsheTrader.dll`.
4. Enable it under `/xlplugins` → Dev Tools. Open `/ashetrader`.
5. Follow [the two-PC test guide](docs/two-pc-test-guide.md).

The plugin does not open a network listener or resume gameplay merely by loading. Pairing and jobs are explicitly started in the UI. `/ashetrader stop` pauses the current operation and notifies the peer.

## Job setup

Under **Transfer jobs**, pair the PCs, refresh both rosters, and create a job. Each character must belong to only one PC's roster. Account labels are descriptive; peer identity and character content ID determine execution ownership.

Recipients are ordered. A finite amount means additional delivery in Move all/Deliver amount modes and final stock in Restock to total mode. Only the final recipient may take an unlimited remainder, and Restock to total requires explicit finite targets. Designate a relay on the other account for any source-to-recipient route within the same account.

Capture the rendezvous while standing at an accessible outdoor point. Verify the aetheryte belongs to that territory. Capture a custom storage bell only on that character's home world; leaving it unset uses an inn. Retainer allowlists govern destination deposits; an empty allowlist means all available retainers.

Source bags and retainer stored inventory are included. Other storage types, equipped items, retainer sale listings, gil and crystals are outside this build's job scope. Items must be tradeable and match the selected quality.

## Progress and recovery

Only matching two-sided native results commit a trade automatically. Delivery totals do not decrease when recipients store items on their retainers. Relay holdings are pending delivery until the final leg commits.

If a client disconnects, a character changes unexpectedly, storage fills, or a native interaction cannot be verified, the job pauses. Reconnect and press Resume. Saved completed results are reconciled; an ambiguous trade is not replayed. If the original outcome was never saved, log in the original characters, inspect both inventories, and use the explicit reconciliation controls only after checking the evidence.

Retainer deposits recover their original saved result before spending more of the deposit allowance. If a crash or interrupted callback leaves the last storage movement uncertain, that deposit remains paused. Inspect the bags and retainers, end the paused job once no trade or relay cargo remains, then create a freshly scanned job for the remaining delivery amounts. This build does not infer an uncertain storage movement from an aggregate count change.

Overflow deposits are limited to received job-owned quantities. If a receiver starts completely full with no received job items to store, free space before resuming. If all permitted retainers are full, the job pauses with the unresolved quantity. When source stock is insufficient, available allocations finish and the result lists unmet targets; it is a terminal partial result, not a claim that the requested totals were reached. Create a new job for the remaining amounts after replenishing sources.

Settings and journals live under Dalamud's `pluginConfigs/AsheTrader` directory: `settings.json`, `transfer-job.json`, and `worker-journal.json`. Preserve both PCs' journals when reporting a recovery issue. Settings contain the pairing secret; remove that field before sharing them. A running job uses a saved copy of its rules, so editing a saved template does not rewrite in-flight quantities.

## Build and distribution

The solution is `AsheTrader.slnx`. `scripts/Build.ps1` runs tests, builds against the local Dalamud development libraries, and produces a package under `artifacts/`. The GitHub workflow performs the same checks and uploads build artifacts; it does not publish releases automatically.

Once a GitHub repository exists:

1. Push this source, preserving the license and notices.
2. Upload the built `AsheTrader-<version>.zip` as a public release asset.
3. Run `pwsh ./scripts/New-Repository.ps1 -GitHubRepository OWNER/REPOSITORY -ReleaseTag v0.2.0.0`.
4. Commit the generated root `repo.json` on `main`. Its raw GitHub URL is the permanent custom-repository feed; update that file for each release.

Source is hosted in [maryamaahmed1991/AsheTrader](https://github.com/maryamaahmed1991/AsheTrader), with installable packages under [Releases](https://github.com/maryamaahmed1991/AsheTrader/releases). Pairing secrets and runtime journals must not be committed or included in release artifacts.

See [the product specification](docs/transfer-jobs.md) and [third-party notices](THIRD-PARTY-NOTICES.md).
