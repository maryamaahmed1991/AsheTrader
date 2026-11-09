# Third-party references and licensing

Ashe's Trader is distributed under GNU AGPL-3.0; see LICENSE.md. The plugin layout follows goatcorp's SamplePlugin template, and game-facing techniques were checked against Henchman (AGPL) and AutoRetainer (BSD 3-Clause). Their projects and contributors retain their respective copyrights. Withdrawal pacing is independently implemented after comparing AutoRetainer's reverse entrust behavior; no reverse entrust source text is incorporated.

- goatcorp/SamplePlugin, reference b8477daaa678a5dd72cdcd4a32bc249d3c23a598: https://github.com/goatcorp/SamplePlugin
- Knightmore/Henchman, reference c5fa50e60d6e86acbac70ca255b29956fd9275ea: https://github.com/Knightmore/Henchman
- PunishXIV/AutoRetainer, reference cc020df0efee3ea65a45117e2b89999108c702e8: https://github.com/PunishXIV/AutoRetainer
- NightmareXIV/Lifestream, reference b36320170faafc8315f7585edb45cfbcd34e7046: https://github.com/NightmareXIV/Lifestream
- Critical-Impact/InventoryTools and CriticalCommonLib (IPC schema references): https://github.com/Critical-Impact/InventoryTools and https://github.com/Critical-Impact/CriticalCommonLib
- aers/FFXIVClientStructs (native structure/callback definitions): https://github.com/aers/FFXIVClientStructs
- PunishXIV/Artisan (retainer interaction references): https://github.com/PunishXIV/Artisan
- NightmareXIV/ECommons (Talk input event sequence): https://github.com/NightmareXIV/ECommons
- karashiiro/TextToTalk (Talk speaker node reference): https://github.com/karashiiro/TextToTalk

Dalamud and its provided libraries are runtime dependencies supplied by XIVLauncher, not bundled into this plugin package. NuGet dependency versions are recorded in the project files and package lock file. External plugins remain separate installations; Ashe's Trader communicates with their published IPC interfaces.

## MIT notices for Talk interaction references

ECommons: Copyright (c) 2023 NightmareXIV

TextToTalk: Copyright (c) 2021 karashiiro

The following MIT license applies to the referenced portions of each project:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
