using System.Numerics;

namespace AsheTrader.Workflow.Native;

public enum GameOperationKind { Login, Inspect, TravelMeeting, ScanRetainers, Withdraw, Deposit }

public sealed class TravelLocation
{
    public uint WorldId { get; set; }
    public uint TerritoryId { get; set; }
    public uint AetheryteId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public Vector3 Position => new(X, Y, Z);
}

public sealed class GameOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public GameOperationKind Kind { get; set; }
    public ulong CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public bool HighQuality { get; set; }
    public uint Quantity { get; set; }
    public TravelLocation? Destination { get; set; }
    public ulong[] AllowedRetainerIds { get; set; } = [];
}

public sealed record RetainerInventorySnapshot(ulong RetainerId, string Name, uint Quantity, bool Verified);

public sealed class GameOperationResult
{
    public Guid Id { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public ulong CharacterId { get; set; }
    public uint BagQuantity { get; set; }
    public uint BagCapacity { get; set; }
    public uint StackSize { get; set; }
    public uint MovedQuantity { get; set; }
    public bool MovementUncertain { get; set; }
    public bool RetainerRosterVerified { get; set; }
    public List<RetainerInventorySnapshot> Retainers { get; set; } = [];
    public DateTimeOffset ObservedAt { get; set; }
}
