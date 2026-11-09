using AsheTrader.Core.Jobs;
using Lumina.Excel.Sheets;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private uint PlanningItemStackSize()
    {
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(Run!.ActiveDefinition.ItemId);
        return item is { StackSize: > 0 } ? item.Value.StackSize
            : throw new InvalidOperationException("The planned item has no valid stack size.");
    }

    private string PreferredMeetingWorld(TransferPlan funding) => CalculateMeetingWorld(funding).Name;

    private MeetingWorldChoice CalculateMeetingWorld(TransferPlan plan)
    {
        var run = Run!;
        var worlds = Plugin.DataManager.GetExcelSheet<World>();
        var configured = worlds.GetRowOrDefault(run.Job.Meeting.WorldId)
            ?? throw new InvalidOperationException("The configured meeting world is unavailable.");
        var homes = run.Snapshots.Values.Select(character => character.World).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The captured outdoor coordinates remain valid on another public world in this DC.
        // Do not introduce a new data-center journey or infer an accessible housing instance.
        var candidates = worlds.Where(world => world.RowId == configured.RowId
                || world.IsPublic && world.DataCenter.RowId == configured.DataCenter.RowId && homes.Contains(world.Name.ToString()))
            .Select(world => new MeetingWorldOption(world.RowId, world.Name.ToString())).ToArray();
        return MeetingWorldPlanner.Choose(configured.RowId, candidates, plan, run.Snapshots.Values);
    }

    private void ChooseMeetingWorld(TransferPlan plan)
    {
        var run = Run!;
        if (run.EffectiveMeeting != null) return;
        var choice = CalculateMeetingWorld(plan);
        run.EffectiveMeeting = Clone(run.Job.Meeting);
        run.EffectiveMeeting.WorldId = choice.WorldId;
        run.MeetingWorldName = choice.Name;
        run.EstimatedWorldTravelers = choice.WorldTravelers;
        run.ConfiguredWorldTravelers = choice.ConfiguredWorldTravelers;
        RecordDiagnostic($"Meeting world selected: {choice.Name}; fundedCharacters={choice.ActiveCharacters}; estimatedWorldTravelers={choice.WorldTravelers}; configuredWorldTravelers={choice.ConfiguredWorldTravelers}; estimate uses home worlds");
    }
}
