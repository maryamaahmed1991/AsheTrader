# Scheduled transfers and Past Trades

## Set up a schedule

1. Update both PCs to 0.6.5.0 or newer. Pair them and enable workflow control on the worker.
2. On the coordinator, save a transfer job with its item rules, providers, recipients and meeting point.
3. Open **Schedules**, choose the saved job, and enter a date and 24-hour time. The default time zone is this PC's zone. Daily schedules repeat at that local time; weekly schedules repeat on the starting weekday.
4. Choose a catch-up window. The default is 60 minutes. Use 0 to skip missed starts after a one-minute dispatch tolerance. A busy or disconnected pair waits within this window. After it expires, that occurrence is skipped and the next future occurrence is retained; no backlog is replayed.
5. Select **Create enabled schedule**. If the queue is paused, use **Resume schedules**. The expanded schedule shows its copied item/recipient rules, next due time, last result and timing controls.

The schedule takes a snapshot of the saved job. Later job edits do not silently change it. To change its item rules, remove the schedule and create another from the edited job. **Edit timing** updates the name, time zone, recurrence and catch-up window while preserving the snapshot. Removing a schedule does not cancel a run that already started.

## Running and stopping

Both games, plugins and their existing network connection must be available. This feature does not launch games or provide internet connectivity. The current transfer architecture coordinates two PCs.

At the scheduled start, both PCs disable AR Multi Mode through IPC. Existing AR/Lifestream activity is allowed to finish, with a ten-minute readiness limit; it is not aborted. Both PCs must confirm idle before any job inventory preparation begins. AR Multi Mode is enabled on both PCs after the job completes normally, including a completed run with shortages. This uses the existing completion handoff, which waits for game readiness and reports failures in Activity. A failure or user pause does not enable AR over unfinished or unresolved cargo.

Transfer, fuel and utility runs block scheduled starts, including paused unfinished work. Only one due schedule starts at a time. **Pause schedules** prevents new automatic starts without stopping the current transfer. **STOP ALL** or a workflow error pauses the schedule queue as well as the current work; use **Resume schedules** to enable future automatic starts again. A paused transfer still needs **Resume / tally inventories** in Activity.

Enabled future schedules survive plugin reload. An interrupted transfer never resumes merely because the plugin loads. A crash between claiming a scheduled occurrence and saving its run is displayed as an interrupted start; choose **Resume schedules** to retry that claimed start or **Discard interrupted start** to withdraw it. The durable occurrence identity prevents the same saved run from being started twice.

In a spring daylight-saving gap, a nonexistent scheduled time moves to the first valid minute. In an autumn overlap, a repeated time runs once at its later occurrence. The following recurrence returns to the originally selected wall-clock time.

## Past Trades

Receipts are saved locally on the coordinator in `past-trades.json`, separately from the current recovery journal. They are retained across updates and reloads. Expand a run for its outcome, start/end timestamps, scheduled origin, item/quality rules, planned and delivered totals, recipient breakdowns, retainer withdrawal/deposit totals, committed movements and interruptions/skips. Search by job, schedule, item or character; the issues filter shows ended runs and completed shortages/skips. **Copy detailed receipt** copies the full receipt as JSON, including character identifiers.

Delivery totals count only committed final legs. A provider-to-relay hop is shown in the movement details but is not counted a second time as delivery. Retainer deposits are separate storage totals and are not additional deliveries. Restock requests show their target total; delivered quantities show only new items actually transferred. Uncommitted, cancelled or unresolved movements never become confirmed deliveries.

Only finished or safely ended jobs enter Past Trades. Current paused runs remain in Activity until completed or safely ended. Discovery scans do not produce receipts. The existing latest finished journal can be imported, but older overwritten journals cannot be reconstructed; their original start time may be unknown. A failed history write keeps the current journal and blocks its replacement by a new transfer.

## First live test

1. Save a small transfer with a known quantity, such as 10 tanks, and ordinary recipients on opposite PCs.
2. Enable AR Multi on both PCs. Schedule the job a few minutes ahead with a 15-minute catch-up window.
3. Confirm both PCs turn AR Multi off, wait for current AR activity, perform the transfer, and enable Multi afterward.
4. Open **Past Trades** on the coordinator. Check requested and delivered quantities against the received stock, expand movements, and copy the receipt if any total looks wrong.
5. For a later test, leave the worker offline until the due time. Reconnect within the catch-up window and confirm a single run starts. Test a shorter expired window separately and confirm it is skipped.

Build and coordinator/persistence tests cover the logic; live AR state, game travel, and UI behavior need testing on the actual two PCs. Friend-triggered requests are not included in this release.
