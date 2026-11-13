using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

/// <summary>
/// Relay for flare-gated exfiltration points (the "signal flare" exits that
/// require the player to fire a flare into a <c>FlareShootDetectorZone</c>).
/// <para>
/// The native flare logic (detector zone -> shot list -> sniper immunity ->
/// exit requirement) only runs on the machine that owns the flare, so the host
/// can never see a client's activation. A client therefore reports its shot to
/// the host, the host decides who the activation covers (squad wide by default,
/// matching the native <c>PlayerByPartyAddedInShotList</c> channel) and then
/// broadcasts that canonical set to every peer.
/// </para>
/// </summary>
public sealed class FlareShootReport : IPoolSubPacket
{
    /// <summary>Detector zone id (<c>FlareShootDetectorZone.zoneID</c>).</summary>
    public string ZoneId;

    /// <summary>Flare type the zone listens for, as a raw <c>FlareEventType</c> value.</summary>
    public byte FlareType;

    /// <summary>Profile id of the player that fired the flare.</summary>
    public string ShooterProfileId;

    /// <summary>Raid id of the player that fired the flare, used for validation.</summary>
    public int ShooterRaidId;

    /// <summary>True when this instance was produced by the host (authoritative).</summary>
    public bool Canonical;

    /// <summary>Profiles this activation covers (shooter plus, when enabled, its squad).</summary>
    public string[] ProfileIds;

    // A malformed or hostile packet must never allocate unbounded state.
    private const int MaxProfiles = 32;

    private FlareShootReport() { }

    public static FlareShootReport CreateInstance()
    {
        return new();
    }

    public static FlareShootReport FromValue(string zoneId, byte flareType, string shooterProfileId,
        int shooterRaidId, string[] profileIds, bool canonical)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<FlareShootReport>(EGenericSubPacketType.FlareShootReport);
        packet.ZoneId = zoneId;
        packet.FlareType = flareType;
        packet.ShooterProfileId = shooterProfileId;
        packet.ShooterRaidId = shooterRaidId;
        packet.ProfileIds = profileIds;
        packet.Canonical = canonical;
        return packet;
    }

    public void Execute(FikaPlayer player = null)
    {
        // Client side: the host broadcast is the authoritative activation.
        FikaFlareExfilManager.HandleReport(this);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ZoneId ?? string.Empty);
        writer.Put(FlareType);
        writer.Put(ShooterProfileId ?? string.Empty);
        writer.Put(ShooterRaidId);
        writer.Put(Canonical);
        var count = ProfileIds?.Length ?? 0;
        writer.Put(count);
        for (var i = 0; i < count; i++)
        {
            writer.Put(ProfileIds[i] ?? string.Empty);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        ZoneId = reader.GetString();
        FlareType = reader.GetByte();
        ShooterProfileId = reader.GetString();
        ShooterRaidId = reader.GetInt();
        Canonical = reader.GetBool();
        var count = reader.GetInt();
        if (count is < 0 or > MaxProfiles)
        {
            ProfileIds = [];
            return;
        }

        var profiles = new string[count];
        for (var i = 0; i < count; i++)
        {
            profiles[i] = reader.GetString();
        }
        ProfileIds = profiles;
    }

    public void Dispose()
    {
        ZoneId = null;
        ShooterProfileId = null;
        ProfileIds = null;
    }
}
