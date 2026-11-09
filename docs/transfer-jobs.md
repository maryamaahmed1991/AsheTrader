# Ashe's Trader: transfer-job requirements

Status: agreed product requirements, implemented in the 0.3.7.0 development build for staged two-PC testing. Automated tests cover planning, relay collection/delivery, accounting, LAN discovery/transport, and the transfer coordinator. User diagnostics confirm a complete ten-retainer scan; new route scheduling, cutscene preservation, native movement/trades and purchases still require live validation.

## Confirmed requirements

- Two FFXIV accounts run on separate Windows PCs on the same home network.
- Ashe's Trader coordinates both clients and automatically changes characters as required to move items to the chosen final recipients.
- Source discovery includes the selected characters' carried inventories and all of their retainers' stored items.
- Recipients are processed in a user-defined priority order.
- Transfers support moving all available items and specifying quantities.
- When a recipient needs inventory space, both sides pause, the recipient returns to its home world, deposits received items into permitted retainers, returns to the meeting point, and continues.
- Trade jobs, character settings, fuel settings, and confirmed progress survive sessions.
- AutoRetainer supplies known characters and supported relog operations. Allagan Tools supplies cached inventory information. Lifestream supports configured travel destinations.
- Fuel purchasing remains in scope: per-character workshop choices, configured restock quantities, and a mode that spends available FC credits subject to inventory capacity.
- The plugin will be distributed through a custom repository. Runtime coordination is separate from release hosting.

## Job definition

A job specifies multiple item lines and a shared meeting point. Each line has an item and quality, source characters, ordered recipients and quantity semantics. Storage policies remain per character. Relay selection is automatic when an actual allocation requires another character on the opposite account.

Item lines run sequentially. Each gets fresh inventory discovery and its own ledger, withdrawals, deposits and completed result. Completion of one line and activation of the next are saved together before any new command is issued. A shortfall does not skip the later lines; unresolved movement or trade evidence pauses the whole job. Duplicate item/quality lines are rejected. Existing single-item templates and in-flight journals retain their meaning.

Quantity modes, independently selectable for each item:

1. **Move all:** collect eligible source stock and allocate it in recipient order. A recipient may have a cap; at most the last recipient can have an unlimited remainder allocation.
2. **Deliver an amount:** deliver the configured additional quantity during this job. Depositing those items on a recipient's retainers still counts as delivered.
3. **Restock to a total:** include existing stock in the recipient's configured storage scope when calculating the shortfall.

Example: collect Ceruleum Tanks from both accounts' selected characters and retainers, deliver 20,000 additional tanks to the first recipient, 10,000 to the second, and the remainder to the third.

Existing stock assigned to a recipient's target is protected from collection. Delivered items and relay cargo are tracked separately from ordinary source stock so the job cannot collect its own output or create transfer loops. Intermediate holdings never count as final delivery.

Retainer scope means stored inventory items. Selling/equipped storage, FC chests, housing storage, and saddlebags are not implicitly added to the source set. These can be separately scoped later. Quality remains explicit for items that support HQ.

## Discovery and inventory truth

Each PC imports its local AutoRetainer roster and obtains local Allagan Tools snapshots. The paired clients exchange only the roster, ownership mappings, and item/capacity data needed for the job.

Allagan Tools cached counts accelerate planning but are not proof that storage is complete or current. Its IPC initialization flag only establishes API availability. A missing owner, empty result, or omitted zero-count entry must not be interpreted as a verified empty retainer.

Track two different timestamps: when a cached result was retrieved and when Ashe's Trader actually observed a loaded inventory. Do not relabel cache retrieval time as inventory freshness. AutoRetainer's offline character data supplies the known retainer IDs and names, since Allagan Tools' ownership query is scoped to the active character. Validate all expected cached bag pages and every known retainer. Complete empty groups returned by Allagan Tools, including its owner-zero placeholders, are accepted as cached zero estimates under the requested trust-cache policy. They do not prove a live observation occurred. Missing pages, foreign owners and malformed or duplicate records still pause planning.

Initial discovery reads cached inventory from each character's owning PC without login or retainer visits. Unavailable cache data pauses with the character and PC requiring attention. RestockToTotal uses cached bag and retainer totals to calculate delivery amounts, without a second full scan after login. Stale totals can affect final stock relative to the target. Completed transfers report verified deliveries of planned stock, not proof that stale caches included every item in the account.

Before withdrawing, trading, or depositing, verify current inventory and capacity. Source overestimates pause if stock is exhausted; underestimates leave additional stock for a later job. Never rewrite an issued plan from cache changes, since its allocations and movement history must remain stable for recovery. Allagan Tools is an information provider; withdrawal/deposit actions use the separate native retainer executor.

## Execution and capacity recovery

The coordinator schedules one exclusive gameplay operation per client: character change, travel, retainer access, trade, or fuel purchase. It must explicitly hand control to and from AutoRetainer/Lifestream and avoid competing with their unrelated work.

For each transfer leg:

1. Select the source and recipient (or relay) and log them in through their local supported setup.
2. Trade a provider's carried stock first. When it is empty, return the provider to its home world and withdraw a load bounded by bag capacity and the remaining allocation. Repeat these pickup/trade trips as needed.
3. Travel both characters to the meeting point and verify identity and readiness.
4. Check capacity for the next batch, including mergeable stacks and empty slots.
5. Agree on the exact batch, execute the trade, and reconcile both clients' observations.
6. Continue until the assigned amount is delivered or a condition requires replanning or pausing.

When the receiver needs space:

1. Stop proposing new batches and reconcile any active trade before travel.
2. Leave the sender waiting with its remaining assignment intact.
3. Return the receiver to its home world and configured summoning bell.
4. Deposit only eligible job items into permitted retainers, preferring compatible existing stacks, and verify each movement.
5. Return to the meeting point and resume only after both clients agree on identity, capacity, and remaining work.

If source withdrawal itself needs inventory space, schedule bounded pickup/trade trips rather than assuming all retainer stock fits into one carried inventory.

Fund allocations in recipient-priority order, preferring direct routes, matching home worlds, reused providers and larger available quantities. When a funded allocation needs a relay, rank opposite-account AutoRetainer characters by estimated load count, preferred home world, free slots and stable identity. Inspect only the selected relay; if unexpectedly full before receiving cargo, pause rather than logging through other candidates. A zero free-slot hint can still permit merging into an existing stack. Relay-only inspections do not claim that unrelated retainers were scanned. Characters with no funded provider, recipient or relay role do not log in.

Collect all compatible funded contributions for the current priority recipient until the relay fills or its incoming work is complete. Then drain that load before collecting more. Physical cargo may mix in a stack, while ledger accounting remains per allocation. Never deposit relay cargo merely to make space. Persist the collection/delivery phase and capacity hints; recover pending trades before choosing further work. Unexpected fullness with carried cargo triggers delivery; fullness without cargo pauses.

Choose the meeting world from public worlds in the captured location's data center by the number of distinct funded participants leaving their recorded home worlds. Include relays, exclude unfunded characters, and keep the captured world on ties. This is a fixed-hub estimate rather than a global trip optimum. Keep the chosen world, relay and allocations fixed once the per-item plan is issued; legacy issued plans keep their original world. A later item performs its own selection.

Never send skip or progression input during inn cutscenes. Wait for natural completion and suspend gameplay deadlines while watching. Check AutoRetainer's separate inn-skip policy before relog and pause with its setting path if it would skip or cannot be read. Do not silently alter external plugin configuration.

If every permitted retainer is full, travel is unavailable, a retainer cannot be accessed, or the peer disconnects, pause the job with a specific reason and unresolved quantities. Do not convert incomplete work into success.

## LAN coordination and recovery

One paired plugin instance is coordinator; the other is a worker connected over the home network. Nearby discovery resolves the coordinator's IP/port from bounded UDP queries and replies after the user starts hosting/searching. Discovery provides connection hints only; the selected identity and pairing key must authenticate before roster exchange or job control. No hosted relay is required for this topology, and loading the plugin starts no listener. Public plugin hosting does not expose a runtime coordination endpoint.

Use stable peer IDs, character content IDs, job IDs, immutable rule revisions, and unique operation/batch IDs. Repeated messages are harmless. Only one coordinator may issue work for a job; loss of the coordinator pauses the worker rather than electing a competing controller.

Persist planned, issued, observed, and committed operations. Both clients report their observed item changes. Commit final delivery only after the two observations reconcile. Retainer deposits alter storage location but do not reduce confirmed delivery; relay transfers are separate legs.

The game operation and local journal cannot be committed atomically. After interruption, inspect both sides and reconcile the pending operation before resuming. An ambiguous completed-or-not-completed trade must not be automatically replayed.

## Acceptance cases

- Collect one item from multiple characters and their retainers across both accounts.
- Fill recipient one before recipient two; deliver a configured remainder to the final recipient.
- Route a transfer through an allowed character on the other account when required.
- Exclude recipient-protected stock and relay cargo from source collection.
- Perform multiple source withdrawal trips when carried inventory is too small.
- Process multiple item/quality lines with independent quantities, shortages and restart checkpoints.
- Choose automatic relays only for funded same-account routes; estimate loads and locality from AutoRetainer slots, inspect only the selected relay, and gather a full load before delivery.
- Pause, home-world deposit, return, and resume multiple times during one delivery.
- Preserve delivered totals after retainer deposits.
- Respect exact quantities and HQ selection without over-withdrawing or over-delivering.
- Distinguish unscanned storage from verified zero stock.
- Stop cleanly when retainers are full or travel is unavailable.
- Reconcile a disconnect before, during, and after a trade; duplicate messages cannot duplicate delivery.
- Resume a saved job from verified progress after restart.

## Verified integration references

- [AutoRetainer API](https://github.com/PunishXIV/AutoRetainerAPI/blob/main/AutoRetainerAPI/AutoRetainerApi.cs): local registered characters and character snapshots.
- [AutoRetainer state IPC](https://github.com/PunishXIV/AutoRetainer/blob/master/AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs): busy state and relog operations.
- [Allagan Tools IPC](https://github.com/Critical-Impact/InventoryTools/blob/main/InventoryTools/IPC/IPCService.cs): `AllaganTools.GetItemCountsByCharacter`, `GetCharacterItemsByType`, `ItemCount`, `ItemCountHQ`, and active-character ownership queries. Counts omit zero holders; aggregate counts include NQ and HQ.
- [Allagan Tools inventory overview](https://allagan.tools/allagan-tools/showcase/): cached inventory tracking across characters and retainers.
- [Lifestream IPC](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/IPC/IPCProvider.cs): saved housing addresses and travel operations. House travel does not itself prove successful workshop entry or bell access.

## Implementation status and validation

The development build includes paired LAN coordination, AR roster/relog integration, Allagan Tools cached planning without discovery visits, priority allocation, same-account relay routes, bounded withdrawals, trade batches, overflow deposits, travel, and saved progress. The configuration window provides inventory-only discovery and explicit interrupted-trade reconciliation. Source allocations remain fixed after planning; actual stock and capacity are checked during execution, and exhaustion pauses rather than silently changing issued allocations.

The next required work is the [staged two-PC validation](two-pc-test-guide.md). Automated native workflows have compiled against the local game libraries but have not been exercised in-game. Validate retainer callbacks, menu transitions, native trade observations, and travel before running large jobs.

Fuel confirmation is now bound to the expected shop, quantity, cost, and inventory/credit state. Purchasing still requires the correct workshop shop to be open; house travel, workshop entry, and vendor opening are separate actions in this build. A successful compile is not evidence of validated native game behavior.
