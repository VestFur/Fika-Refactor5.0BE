using System;
using EFT;
using EFT.InventoryLogic;

namespace Fika.Core.Main.Utils;

/// <summary>The host's inventory allocator at the time of the reconnect profile snapshot.</summary>
public sealed class ReconnectInventoryState
{
    private readonly string _profileId;
    private readonly string _controllerId;
    private readonly ushort _nextOperationId;

    public ReconnectInventoryState(string profileId, MongoID controllerId, ushort nextOperationId)
    {
        if (string.IsNullOrEmpty(profileId)) throw new ArgumentException("Missing reconnect profile", nameof(profileId));
        if (controllerId is null) throw new ArgumentNullException(nameof(controllerId));
        _profileId = profileId;
        _controllerId = controllerId.ToString();
        _nextOperationId = nextOperationId;
    }

    internal void Apply(ItemController controller, string profileId)
    {
        if (!string.Equals(profileId, _profileId, StringComparison.Ordinal))
            throw new InvalidOperationException("Reconnect inventory belongs to a different profile");
        controller.IdSource = new MongoID(_controllerId);
        controller._nextOperationId = _nextOperationId;
    }
}
