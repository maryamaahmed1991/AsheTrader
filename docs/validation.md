# Development build validation

Build: 0.2.0.0, 8 November 2026. Target: Dalamud API 15, .NET 10, Windows.

## Completed checks

- `scripts/Build.ps1` completed successfully, including restore, tests, Release compilation, and packaging.
- 116 automated tests passed, with zero failures and zero skipped tests.
- The plugin Release build produced zero warnings and zero errors against the local Dalamud development libraries.
- Tests exercise priority allocation, source/recipient overlap, relay accounting, exact item/HQ quantities, fuel calculations, settings conflicts, authenticated encrypted LAN transport, replay rejection, and reconnect behavior.
- Coordinator tests compile the production coordinator into a simulated two-client harness. They cover discovery, pickup, trades, repeated overflow deposits, return travel, priority/relay delivery, interrupted trades, serialized recovery, partial deposits, lost deposit responses, and refusal to repeat uncertain deposits. Native game adapters are replaced by the harness in those tests.
- The generated plugin archive contains the plugin, its core assembly, dependency metadata, manifest, license, and third-party notices. It contains no runtime settings, pairing keys, or journals.
- The custom-repository generator produced a valid one-entry feed with API level 15 and matching install/update release URLs using a test repository name. Nothing was published.

## Required live validation

No plugin installation, native game action, trade, retainer interaction, fuel purchase, or physical two-PC LAN test was performed during development. A successful build and simulated coordinator tests do not establish that version-sensitive game callbacks work in a live client.

Use [the staged two-PC test guide](two-pc-test-guide.md), beginning with pairing and scan-only discovery. Native callback validation deliberately pauses on unexpected UI state. Report the first failing stage and both clients' activity/log output before trying larger jobs.

The first build supports two Windows PCs, one selected item per job, and a reachable outdoor meeting point. Fuel purchases require the correct workshop shop to be open. Incomplete trade evidence requires explicit reconciliation; an uncertain retainer deposit remains paused and requires storage inspection and a freshly scanned replacement job for remaining deliveries.
