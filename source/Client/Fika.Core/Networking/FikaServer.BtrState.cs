using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Vehicle;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private List<ReconnectPacket.BtrPassengerReconnectState> CaptureBtrPassengers()
    {
        var result = new List<ReconnectPacket.BtrPassengerReconnectState>();
        var vehicle = Singleton<GameWorld>.Instance?.BtrController?.BtrVehicle;
        if (vehicle?.Passengers == null) return result;
        foreach (var passenger in vehicle.Passengers)
        {
            var player = passenger?.Player;
            if (player == null || !player.IsYourPlayer && player.TryCast<ObservedPlayer>() == null) continue;
            result.Add(new ReconnectPacket.BtrPassengerReconnectState
            {
                PlayerRaidId = player.RaidId,
                SideId = passenger.SideId,
                SlotId = passenger.SlotId,
                State = player.BtrState
            });
        }
        return result;
    }

    private List<TraderServiceAvailabilityData> CaptureBtrServices(FikaPlayer player)
    {
        if (player == null) return new List<TraderServiceAvailabilityData>();
        try
        {
            // The host's local profile is the authoritative trader profile;
            // FikaServer itself does not carry a Profile property.
            var services = player.GetAvailableTraderServices(Profile.TraderInfo.BTR_TRADER_ID);
            return services?.AsManaged().ToList() ?? new List<TraderServiceAvailabilityData>();
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning($"Failed to capture BTR services for reconnect: {ex.Message}");
            return new List<TraderServiceAvailabilityData>();
        }
    }
}
