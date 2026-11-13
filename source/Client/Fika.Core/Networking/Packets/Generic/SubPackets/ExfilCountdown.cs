using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT.Interactive;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class ExfilCountdown : IPoolSubPacket
{
    public string ExfilName;
    public float ExfilStartTime;

    private ExfilCountdown() { }

    public static ExfilCountdown CreateInstance()
    {
        return new ExfilCountdown();
    }

    public static ExfilCountdown FromValue(string exfilName, float exfilStartTime)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<ExfilCountdown>(EGenericSubPacketType.ExfilCountdown);
        packet.ExfilName = exfilName;
        packet.ExfilStartTime = exfilStartTime;
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

            var exfilController = ExfiltrationController.Instance;
            foreach (var exfiltrationPoint in exfilController.ExfiltrationPoints)
            {
                if (exfiltrationPoint.Settings.Name == ExfilName)
                {
                    // 主机的计时器数值是权威时间戳。
                    // 此处改用对端当前的 PastTime 会丢弃
                    // 数据包载荷，并在两个 Unity 计时器
                    // 不在同一帧时导致倒计时漂移。
                    // (2026-10-26 W32) 本机已有的激活绝不能被推后：先按下 "black car" 的客户端
                    // 在这里启动了自己的倒计时，而这个数据包随后带着主机更晚的时间戳到达并把它重置——
                    // 于是客户端等了第二次，并在主机撤离时
                    // 落在后面。
                    // (2026-10-27 T2-A) 该数值是发送方时钟上的裸时间戳（发送方写入自己的
                    // PastTime，接收方在 FikaExfilManager 的 tick 中拿它和自身的值比较），两个
                    // 时钟的差值正好是两个战局的载入偏移。因此比较这两个值实际上
                    // 是在两个不同的时钟之间做判断：按「较早者胜」，主机每个数据包都会用
                    // 客户端更小的标记替换自己的起始值，倒计时因此往前跳；而
                    // 客户端保留了一个主机并不共享的起始值。本机的规则更简单，且不需要
                    // 两个时钟之间存在共识：本机自己拥有的激活永不替换（它是这里
                    // 付费并撤离的那一个），只有没有激活的机器才采纳对端的。
                    var currentStart = exfiltrationPoint.ExfiltrationStartTime;
                    if (currentStart > 0f)
                    {
                        FikaGlobals.LogInfo($"[CarExtract] kept the local activation: point '{ExfilName}' " +
                            $"local={currentStart}, host={ExfilStartTime}, queued {exfiltrationPoint.QueuedPlayers?.Count ?? 0}");
                        return;
                    }

                    exfiltrationPoint.ExfiltrationStartTime = ExfilStartTime;

                    if (exfiltrationPoint.Status != EExfiltrationStatus.Countdown)
                    {
                        exfiltrationPoint.Status = EExfiltrationStatus.Countdown;
                    }

                    // [CarExtract] 主机激活已到达此对端的证据。
                    // 付费方也报告自己已排队的状态，正是这一状态
                    // 让该点在计时器结束时将它撤离。
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
                        // (2026-10-27 T2-A) 与公共点相同的规则：本机的激活
                        // 绝不被对端的标记替换，后者位于对端的时钟上。只有没有
                        // 激活的机器才会采纳它。
                        var secretStart = secretExfiltration.ExfiltrationStartTime;
                        if (!(secretStart > 0f))
                        {
                            secretExfiltration.ExfiltrationStartTime = ExfilStartTime;
                        }

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
    }

    public void Deserialize(NetDataReader reader)
    {
        ExfilName = reader.GetString();
        ExfilStartTime = reader.GetFloat();
    }

    public void Dispose()
    {
        ExfilName = null;
        ExfilStartTime = 0f;
    }
}
