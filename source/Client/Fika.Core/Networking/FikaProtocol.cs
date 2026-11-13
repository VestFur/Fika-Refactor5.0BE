using System;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace Fika.Core.Networking;

/// <summary>Rejects incompatible wire layouts before any player or world packets are accepted.</summary>
internal static class FikaProtocol
{
    // Increment whenever a packet layout or native descriptor schema changes.
    // v18 adds Kolotun state synchronization for the SPT 5 Christmas cold event.
    // Keep this in the connection key so peers with the previous wire layout are
    // rejected before they can consume a packet using the new fields.
    // v19 lets a player scav walk into a raid that is already running.
    // v20 adds the flare activation relay (generic sub-packet 19) and the
    // player-scoped fields of the exit countdown and the exfiltration snapshot.
    internal const int Revision = 20;
    internal const string JoinKey = "fika.spt50.il2cpp.v20.join";
    internal const string ReconnectKey = "fika.spt50.il2cpp.v20.reconnect";
    internal const string ScavJoinKey = "fika.spt50.il2cpp.v20.scavjoin";

    internal static string ConnectionKey(bool reconnect, bool scavLateJoin = false) =>
        scavLateJoin ? ScavJoinKey : reconnect ? ReconnectKey : JoinKey;

    internal static bool TryReadConnection(NetDataReader data, bool raidStarted, out string rejection) =>
        TryReadConnection(data, raidStarted, out rejection, out _);

    internal static bool TryReadConnection(NetDataReader data, bool raidStarted, out string rejection, out bool scavLateJoin)
    {
        rejection = "Incompatible Fika protocol. All peers must use the same SPT 5 IL2CPP build.";
        scavLateJoin = false;
        if (!data.TryGetString(out var key) || data.AvailableBytes != 0) return false;
        if (!string.Equals(key, JoinKey, StringComparison.Ordinal) &&
            !string.Equals(key, ReconnectKey, StringComparison.Ordinal) &&
            !string.Equals(key, ScavJoinKey, StringComparison.Ordinal)) return false;

        scavLateJoin = string.Equals(key, ScavJoinKey, StringComparison.Ordinal);

        // A player scav may enter a raid that is already running; a regular join
        // still has to wait for the host to start it.
        if (raidStarted && string.Equals(key, JoinKey, StringComparison.Ordinal))
        {
            rejection = "Raid already started";
            return false;
        }
        rejection = null;
        return true;
    }
}
