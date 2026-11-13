using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class ExfilCountdown : IPoolSubPacket
{
    public string ExfilName;
    public float ExfilStartTime;
    /// <summary>
    /// Profile that unlocked a flare gated exit countdown. Empty means the
    /// countdown is point wide, which is the untouched behaviour of paid exits
    /// and of every countdown started before this build's flare relay.
    /// </summary>
    public string OwnerProfileId;
    /// <summary>True when the owner's squad shares the countdown (default mode).</summary>
    public bool SquadWide = true;

    private ExfilCountdown() { }

    public static ExfilCountdown CreateInstance()
    {
        return new ExfilCountdown();
    }

    public static ExfilCountdown FromValue(string exfilName, float exfilStartTime)
    {
        return FromValue(exfilName, exfilStartTime, string.Empty, true);
    }

    public static ExfilCountdown FromValue(string exfilName, float exfilStartTime, string ownerProfileId, bool squadWide)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<ExfilCountdown>(EGenericSubPacketType.ExfilCountdown);
        packet.ExfilName = exfilName;
        packet.ExfilStartTime = exfilStartTime;
        packet.OwnerProfileId = ownerProfileId ?? string.Empty;
        packet.SquadWide = squadWide;
        return packet;
    }

    public void Execute(FikaPlayer player = null)
    {
        var coopHandler = FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler;
        if (coopHandler == null)
        {
            FikaGlobals.LogError("ClientExtract: CoopHandler was null!");
            return;
        }

        if (ExfiltrationController.Instance != null)
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame == null)
            {
                FikaGlobals.LogError("ExfilCountdown: FikaGame was null");
                return;
            }

            // A flare gated countdown only belongs to the player that unlocked
            // it (or to that player's squad in the default mode). Carrying the
            // owner lets every peer decide locally instead of unlocking the exit
            // for the whole raid.
            if (!FikaFlareExfilManager.ShouldApplyScope(OwnerProfileId, SquadWide))
            {
                FikaGlobals.LogInfo($"[FlareExfil] ignored (report profile=<{OwnerProfileId}> != me)");
                return;
            }

            var exfilController = ExfiltrationController.Instance;
            foreach (var exfiltrationPoint in exfilController.ExfiltrationPoints)
            {
                if (exfiltrationPoint.Settings.Name == ExfilName)
                {
                    // The host's timer value is the authoritative timestamp.
                    // Using the peer's current PastTime here discards the
                    // packet payload and causes countdowns to drift when the
                    // two Unity timers are not on the same frame.
                    exfiltrationPoint.ExfiltrationStartTime = ExfilStartTime;

                    if (exfiltrationPoint.Status != EExfiltrationStatus.Countdown)
                    {
                        exfiltrationPoint.Status = EExfiltrationStatus.Countdown;
                    }

                    // [CarExtract] Proof that the host's activation reached this
                    // peer. The payer also reports its own queued state, which is
                    // what makes the point extract it when the timer runs out.
                    if (Fika.Core.Main.Components.FikaExfilManager.IsPaidExfiltrationPoint(exfiltrationPoint))
                    {
                        FikaGlobals.LogInfo($"[CarExtract] activation received: point '{ExfilName}' " +
                            $"countdown set to {ExfilStartTime}, queued {exfiltrationPoint.QueuedPlayers?.Count ?? 0}");
                    }
                    return;
                }
            }

            if (exfilController.SecretExfiltrationPoints != null)
            {
                foreach (var secretExfiltration in exfilController.SecretExfiltrationPoints)
                {
                    if (secretExfiltration.Settings.Name == ExfilName)
                    {
                        secretExfiltration.ExfiltrationStartTime = ExfilStartTime;

                        if (secretExfiltration.Status != EExfiltrationStatus.Countdown)
                        {
                            secretExfiltration.Status = EExfiltrationStatus.Countdown;
                        }
                        return;
                    }
                }
            }

            FikaGlobals.LogError("ExfilCountdown: Could not find ExfiltrationPoint: " + ExfilName);
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ExfilName);
        writer.Put(ExfilStartTime);
        writer.Put(OwnerProfileId ?? string.Empty);
        writer.Put(SquadWide);
    }

    public void Deserialize(NetDataReader reader)
    {
        ExfilName = reader.GetString();
        ExfilStartTime = reader.GetFloat();
        OwnerProfileId = reader.GetString();
        SquadWide = reader.GetBool();
    }

    public void Dispose()
    {
        ExfilName = null;
        ExfilStartTime = 0f;
        OwnerProfileId = null;
        SquadWide = true;
    }
}
