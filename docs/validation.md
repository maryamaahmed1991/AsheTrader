# Development build validation

Build: 0.2.1.0, 8 November 2026. Target: Dalamud API 15, .NET 10, Windows.

## Completed checks

- `scripts/Build.ps1` completed successfully, including restore, tests, Release compilation, and packaging.
- 139 automated tests passed, with zero failures and zero skipped tests.
- The plugin Release build produced zero warnings and zero errors against the local Dalamud development libraries.
- Tests exercise priority allocation, source/recipient overlap, relay accounting, exact item/HQ quantities, fuel calculations, settings conflicts, authenticated encrypted LAN transport, replay rejection, and reconnect behavior.
- Discovery tests cover bounded packets, protocol validation, source/nonce/identity filtering, result expiry, broadcast calculation, localhost UDP discovery, and cancellation. A separate smoke test on the local private network interface successfully used directed broadcast to discover a test host, then authenticated and exchanged an encrypted message. This used two endpoints on one PC; physical two-PC discovery and the game's firewall rules still need user validation.
- A reconnect regression confirms an old accepted socket generation cannot send into or consume messages from its replacement. The service accepts the selected/remembered identity before exchanging rosters.
- Coordinator tests compile the production coordinator into a simulated two-client harness. They cover discovery, pickup, trades, repeated overflow deposits, return travel, priority/relay delivery, interrupted trades, serialized recovery, partial deposits, lost deposit responses, and refusal to repeat uncertain deposits. Native game adapters are replaced by the harness in those tests.
- The generated plugin archive contains the plugin, its core assembly, dependency metadata, manifest, license, and third-party notices. It contains no runtime settings, pairing keys, or journals.
- The custom-repository generator produces a one-entry feed with API level 15 and matching install/update release URLs. The permanent feed is hosted at `https://raw.githubusercontent.com/maryamaahmed1991/AsheTrader/main/repo.json`.

## Required live validation

No plugin installation, native game action, trade, retainer interaction, fuel purchase, or physical two-PC LAN test was performed during development. A successful build and simulated coordinator tests do not establish that version-sensitive game callbacks work in a live client.

Use [the staged two-PC test guide](two-pc-test-guide.md), beginning with pairing and scan-only discovery. Native callback validation deliberately pauses on unexpected UI state. Report the first failing stage and both clients' activity/log output before trying larger jobs.

The first build supports two Windows PCs, one selected item per job, and a reachable outdoor meeting point. Fuel purchases require the correct workshop shop to be open. Incomplete trade evidence requires explicit reconciliation; an uncertain retainer deposit remains paused and requires storage inspection and a freshly scanned replacement job for remaining deliveries.
