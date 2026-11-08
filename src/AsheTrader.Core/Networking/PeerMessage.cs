namespace AsheTrader.Core.Networking;

/// <summary>An application message. The transport never executes its payload.</summary>
public sealed record PeerMessage(string Kind, Guid Id, Guid? CorrelationId, string Payload);
