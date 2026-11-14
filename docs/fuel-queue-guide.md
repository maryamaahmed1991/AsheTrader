# Fuel queue setup and first test

Version **0.5.0.0** adds independent fuel queues on each PC. Pairing is not required. Each queue uses that PC's selected characters, AutoRetainer roster and saved destinations. The new fuel workflow is ready for staged testing; supplier travel, purchases and repeated deposits have not yet been validated in-game by the development agent.

## Set up buyers

1. Update Ashe's Trader and open **Fuel & houses → Fuel queue**. Setup automatically refreshes AutoRetainer's character/FC data and imports Lifestream's saved houses. **Refresh setup** repeats the import.
2. Expand a world, then an FC. Select the characters that may buy fuel and use **Up/Down** to order buyers within that FC. Characters run in world and FC groups. Unknown FC memberships remain separate until checked after login; a character known to have no FC is skipped.
3. Expand **FC destination & credit reserve** and choose either a registered workshop house or **Resident Caretaker**. The caretaker needs no company house; choose an accessible residential district on the character's home world. FC rank and credit-spending permissions still apply.
4. Set **Credits to leave** for that FC. Zero converts every affordable credit into fuel. This is a shared FC reserve, rather than a separate budget per character. Cached balances help with setup; the shop's live balance controls purchases.
5. Select a character's name to edit its options. **Convert all credits above the FC reserve** enables repeated buying. For a smaller run, turn it off and set **Buy when carrying fewer than** and **Restock to**.
6. Leave **Store fuel in retainers and continue buying** enabled for repeated buy/deposit cycles. Set **Tanks to keep carried** to the amount that should stay in the bags. Deposits store **all other carried tanks, including fuel that was already there before this run**. They use the character's home-world bell and configured retainer selection.

An individual character can override the FC destination with another registered house on its home world. The house dropdown excludes other worlds, and queue validation rejects stale assignments to another world.

## Register houses

Open **Workshop houses**. Existing Lifestream house addresses are imported automatically. Use **Add a house** to enter its label, world, residential district, ward and plot, or **Record this workshop** while inside the intended workshop.

A registered address must have an accessible company workshop and fuel supplier. An address alone does not establish workshop access. Characters without a suitable house can use the Resident Caretaker option instead.

## Run a small test first

1. Finish or end any transfer job on this PC. AutoRetainer should be ready and idle with multi-mode paused; Lifestream and vnavmesh must be available.
2. Select one buyer and a familiar destination. For a character carrying zero tanks, disable credit exhaustion and set **Buy when carrying fewer than: 1**, **Restock to: 99**. If it already carries fuel, choose a threshold above that carried quantity and a target only a small amount higher.
3. Check the **Queue preview**, then press **Start selected**. Confirm the correct character logs in, reaches the chosen supplier, buys the intended fuel and stores fuel according to its keep-carried setting.
4. Check the bought/stored totals and actual inventory. Repeat with the other destination type if you intend to use both workshop houses and caretakers.
5. Expand to two selected buyers in one FC, then another FC/world. Finally enable credit exhaustion to test repeated purchases, full-bag deposits and continuation to the next buyer when retainer storage is full.

Each PC's queue is started and monitored separately. You can use one PC's fuel queue without connecting the other PC.

## Progress and recovery

The run bar shows the current action and accumulated tanks bought/stored. **Current queue** records each character's result. When a buyer has no more retainer room, it is marked **Storage full** and remaining selected buyers can continue. Once the FC reaches its credit reserve, later buyers in that FC do not spend more. Other FCs continue with their own balances.

**Pause** stops the queue and saves its state. Reloading the plugin leaves saved work paused. **Resume** reconciles any recorded purchase or deposit against current counts before scheduling more work. If a retainer deposit was interrupted and the character changed, return to the affected character when prompted so its inventory can be tallied. Ambiguous changes remain a visible pause rather than being repeated blindly.

**End queue** preserves completed progress. An outstanding purchase or deposit is tallied before the queue finishes; no new purchase begins during that ending step.

If something stalls, use **Copy diagnostics** in the fuel run bar on the affected PC before changing the game menu. Include which character, destination and screen were visible. The report contains identifiers and recent steps, but excludes pairing keys and chat.
