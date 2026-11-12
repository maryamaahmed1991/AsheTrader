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

    private IReadOnlyList<RouteWorld> RoutingWorlds() => Plugin.DataManager.GetExcelSheet<World>()
        .Where(world => world.IsPublic && world.DataCenter.RowId != 0)
        .Select(world => new RouteWorld(world.RowId, world.Name.ToString(), world.DataCenter.RowId,
            world.DataCenter.Value.Name.ToString(), checked((int)world.DataCenter.Value.Region.RowId))).ToArray();
}
