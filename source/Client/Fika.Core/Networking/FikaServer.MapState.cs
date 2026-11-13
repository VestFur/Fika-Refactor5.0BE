using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using JsonType;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.World;
using System.Collections.Generic;
using static Fika.Core.Networking.NetworkUtils;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private GameWorld _btrSpawnWorld;
    private uint _btrSpawnGeneration;
    private int _btrSpawnPlayerRaidId;
    private Vector3 _btrSpawnPosition;
    private Quaternion _btrSpawnRotation;

    internal void BroadcastBtrSpawn(BtrSpawnOnThePathEvent spawn)
    {
        var world = Singleton<GameWorld>.Instance;
        if (world == null) return;
        if (_btrSpawnWorld == null || _btrSpawnWorld.Pointer != world.Pointer) _btrSpawnGeneration = 0;
        _btrSpawnWorld = world;
        _btrSpawnGeneration++;
        _btrSpawnPlayerRaidId = spawn.PlayerRaidId;
        _btrSpawnPosition = spawn.Position;
        _btrSpawnRotation = spawn.Rotation;
        SendGenericPacket(EGenericSubPacketType.SpawnBTR,
            BtrSpawn.FromValue(spawn.Position, spawn.Rotation, spawn.PlayerRaidId, _btrSpawnGeneration), true);
    }

    /// <summary>
    /// 把一个加入进行中 raid 的对端不可能看到过的 world 状态交给它：中转历史与
    /// 点位、事件生成器、Kolotun 以及 BTR。在对端上报它的中转
    /// 控制器时发送，即每次迟到加入只发一次。
    /// </summary>
    private void SendMapStateSnapshot(Player player, NetPeer peer)
    {
        var world = Singleton<GameWorld>.Instance;
        // 可选的 Headless 插件提供自己的 TransitController
        // 子类。读取共享的原生状态，使这些快照对
        // 可玩主机和 Headless 主机都成立。
        var transit = world?.TransitController;
        if (transit != null)
        {
            if (transit.summonedTransits.TryGetValue(player.ProfileId, out var history))
            {
                var historyPacket = new SyncTransitControllersPacket
                {
                    ProfileId = player.ProfileId, RaidId = history.raidId,
                    Count = history.count, Maps = history.maps, Events = history.events
                };
                SendDataToPeer(ref historyPacket, DeliveryMethod.ReliableOrdered, peer);
            }
            // CreateInitialization（Fika 中转控制器）需要该 profile 在主机侧有一个
            // 历史条目。下面的原生回退会在没有它的情况下构建快照，
            // 因此一个在 raid 已运行期间连接的对端无论走哪条路径都能得到服务，
            // 永远不会没有中转点位。
            var init = FikaWorldControllers.Find<FikaHostTransitController>(transit)?.CreateInitialization(player)
                ?? CreateNativeTransitInitialization(transit, player,
                    FikaSingleton<Main.GameMode.IFikaGame>.Instance?.GameController?.RaidSettings?.SelectedDateTime ?? default);
            if (init != null)
            {
                SendDataToPeer(ref init, DeliveryMethod.ReliableOrdered, peer);
            }
            else
            {
                _logger.LogWarning($"[Transit] No initialization snapshot could be built for {player?.ProfileId}; the peer keeps its locally created transit state");
            }
            foreach (var point in transit.pointsById.Values)
            {
                if (!transit.transitPlayers.ContainsKey(player.ProfileId) ||
                    !point._playerGroups.TryGetValue(player.ProfileId, out var group)) continue;
                point.SendTimer(player.ProfileId, group);
                if (point._groupSize.TryGetValue(group, out var size))
                {
                    var sizes = new Il2CppSystem.Collections.Generic.Dictionary<int, byte>();
                    sizes.Add(player.RaidId, (byte)System.Math.Clamp(size, 0, byte.MaxValue));
                    var sizePacket = new TransitEventPacket
                    {
                        EventType = TransitEventPacket.ETransitEventType.GroupSize,
                        TransitEvent = new TransitGroupSizeEvent { PointId = point.parameters.id, Sizes = sizes }
                    };
                    SendDataToPeer(ref sizePacket, DeliveryMethod.ReliableOrdered, peer);
                }
            }
        }
        if (FikaWorldControllers.Find<HostRunddansController>(world.RunddansController) is { } events)
        {
            var state = events.CreateSnapshot();
            SendDataToPeer(ref state, DeliveryMethod.ReliableOrdered, peer);
            // 仅凭生成器状态无法说明该事件是否已经
            // 开始：一个正在运行的生成器之后可能会损坏或完成。
            // 在状态快照之后向迟到对端重发一次中转锁。
            if (events.EventStarted)
            {
                var started = new EventControllerEventPacket
                {
                    Type = EventControllerEventPacket.EEventType.StartedEvent
                };
                SendDataToPeer(ref started, DeliveryMethod.ReliableOrdered, peer);
            }
        }
        if (world.KolotunController is { } kolotun)
        {
            var state = new KolotunStatePacket
            {
                IsActive = kolotun.IsActive,
                EventStage = kolotun.EventStage
            };
            SendDataToPeer(ref state, DeliveryMethod.ReliableOrdered, peer);
        }
        if (_btrSpawnGeneration > 0 && _btrSpawnWorld != null && _btrSpawnWorld.Pointer == world.Pointer && world.BtrController is { } btr)
        {
            var view = btr.BtrView;
            var position = view != null ? view.transform.position : _btrSpawnPosition;
            var rotation = view != null ? view.transform.rotation : _btrSpawnRotation;
            SendGenericPacketToPeer(EGenericSubPacketType.SpawnBTR,
                BtrSpawn.FromValue(position, rotation, _btrSpawnPlayerRaidId, _btrSpawnGeneration), peer);
            _dataWriter.Reset();
            _dataWriter.PutEnum(EPacketType.BTR);
            _dataWriter.PutUnmanaged(btr._offlineSyncPacket);
            peer.Send(_dataWriter.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// 为一个本服务器能描述其状态的玩家构建中转初始化快照。
    /// 它刻意不要求主机侧的中转历史：对端会通过
    /// BaseGameController.SyncTransitControllers 上报一条，而该上报只会从
    /// 倒计时语句 6/8 运行，而合作设置可以跳过它。没有历史
    /// 回退时，一个在 raid 中途连接的对端完全收不到初始化，它的
    /// 中转点位也一直不可用。
    /// </summary>
    private TransitEventPacket CreateNativeTransitInitialization(TransitController transit, Player player,
        EDateTime fallbackTime)
    {
        if (player == null || transit == null)
        {
            return null;
        }

        ushort transitionCount;
        bool eventPlayer;
        if (transit.summonedTransits.TryGetValue(player.ProfileId, out var history))
        {
            transitionCount = (ushort)System.Math.Clamp(history.count, 0, ushort.MaxValue);
            eventPlayer = history.events;
        }
        else
        {
            // 对端保留它依据所收到的 raid 设置算出的计数，所以上报
            // 同一份 raid 级转移状态（本地 raid 设置所指向的
            // 那个对象），而不是一份清零的历史，后者会夺走它的中转点位。
            var transitData = FikaBackendUtils.TransitData;
            transitionCount = (ushort)System.Math.Clamp(transitData?.transitionCount ?? 0, 0, ushort.MaxValue);
            eventPlayer = transitData != null && transitData.transitionType.HasFlagNoBox(ELocationTransition.Event);
            _logger.LogWarning($"[Transit] No host side transit history for {player.ProfileId}; sending an initialization snapshot from the raid transition state (count={transitionCount}, event={eventPlayer})");
        }

        var points = new Il2CppSystem.Collections.Generic.Dictionary<int, JsonType.LocationSettings.Location.TransitParameters>();
        var requirements = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        var enabled = new List<int>();
        var registered = -1;
        foreach (var entry in transit.pointsById)
        {
            var point = entry.Value;
            if (point.parameters.active) points.Add(entry.Key, point.parameters);
            // 这是客户端真正读取的初始化器（日志中记作 [Init] Host
            // transit Init）。上报原始的需求值会在每个客户端 UI 中禁用该点位，
            // 与另一个初始化器完全一样，所以这里同样上报为
            // 已满足；逐玩家的校验留给显示交互提示的客户端，
            // 以及主机上的 HasMetEntryRequirements。
            requirements.Add(entry.Key, true);
            if (point.Enabled) enabled.Add(entry.Key);
            if (transit.transitPlayers.ContainsKey(player.ProfileId) &&
                point._playerGroups.ContainsKey(player.ProfileId)) registered = entry.Key;
        }
        transit.profileKeys.TryGetValue(player.ProfileId, out var key);
        var time = transit.profileTime.TryGetValue(player.ProfileId, out var selected)
            ? selected : fallbackTime;
        return new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Init,
            TransitEvent = new TransitInitEvent
            {
                PlayerRaidId = player.RaidId, Points = points,
                CompletedQuestRequirementMetByPointId = requirements,
                TransitionCount = transitionCount, EventPlayer = eventPlayer
            },
            EnabledPointIds = enabled.ToArray(), RegisteredPointId = registered,
            KeyId = key, SelectedTime = time
        };
    }
}
