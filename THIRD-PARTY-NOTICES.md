# Third-party references and licensing

Ashe's Trader is distributed under GNU AGPL-3.0; see LICENSE.md. The plugin layout follows goatcorp's SamplePlugin template, and game-facing trade/fuel techniques were checked against the AGPL-licensed Henchman and AutoRetainer projects. Their projects and contributors retain their respective copyrights.

- goatcorp/SamplePlugin, reference b8477daaa678a5dd72cdcd4a32bc249d3c23a598: https://github.com/goatcorp/SamplePlugin
- Knightmore/Henchman, reference c5fa50e60d6e86acbac70ca255b29956fd9275ea: https://github.com/Knightmore/Henchman
- PunishXIV/AutoRetainer, reference cc020df0efee3ea65a45117e2b89999108c702e8: https://github.com/PunishXIV/AutoRetainer
- NightmareXIV/Lifestream, reference b36320170faafc8315f7585edb45cfbcd34e7046: https://github.com/NightmareXIV/Lifestream
- Critical-Impact/InventoryTools and CriticalCommonLib (IPC schema references): https://github.com/Critical-Impact/InventoryTools and https://github.com/Critical-Impact/CriticalCommonLib
- aers/FFXIVClientStructs (native structure/callback definitions): https://github.com/aers/FFXIVClientStructs
- PunishXIV/Artisan (retainer interaction references): https://github.com/PunishXIV/Artisan

Dalamud and its provided libraries are runtime dependencies supplied by XIVLauncher, not bundled into this plugin package. NuGet dependency versions are recorded in the project files and package lock file. External plugins remain separate installations; Ashe's Trader communicates with their published IPC interfaces.
