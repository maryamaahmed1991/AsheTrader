using AsheTrader.Integrations;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    // This command reads only this PC's plugin caches. It never enters the native executor,
    // changes characters, or promotes cache retrieval time to a live observation time.
    private static GameOperationResult ReadCachedInventory(GameOperation operation)
    {
        var result = new GameOperationResult { Id = operation.Id, CharacterId = operation.CharacterId, Success = true };
        var cache = new AllaganToolsClient();
        if (!cache.TryReadCharacter(operation.CharacterId, operation.ItemId, operation.HighQuality, out var snapshot, out var error)
            || snapshot == null)
        {
            result.Error = error;
            return result;
        }
        result.CachedInventoryAvailable = true;
        result.RetainerRosterKnown = true;
        result.CacheRetrievedAt = snapshot.RetrievedAt;
        result.BagQuantity = snapshot.BagQuantity;
        result.BagCapacity = snapshot.BagCapacity;
        result.StackSize = snapshot.StackSize;
        result.Retainers = snapshot.Retainers.Select(retainer =>
            new RetainerInventorySnapshot(retainer.RetainerId, retainer.Name, retainer.Quantity, false)).ToList();
        return result;
    }
}
