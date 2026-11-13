using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Main.HostClasses;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

/// <summary>
/// 在 SPT 触发原生销毁事件时，保留主机手雷的最终
/// 网络状态。SPT 5 也可能把该状态排入队列，来源是
/// <c>UnregisterGrenade</c>；主机世界在每次刷新后清空自己的逐 tick 队列，
/// 以此对投递去重。
/// </summary>
public sealed class GameWorld_GrenadeDestroyEvent_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(GameWorld).GetMethod(nameof(GameWorld.GrenadeDestroyEvent));

    [PatchPrefix]
    private static bool Prefix(Throwable grenade)
    {
        if (grenade == null || !grenade.HasNetData ||
            !Singleton<FikaHostGameWorld>.Instantiated)
            return true;

        var hostWorld = Singleton<FikaHostGameWorld>.Instance?.FikaHostWorld;
        if (hostWorld == null)
            return true;

        // 当 SPT 5 的原生 unregister 路径已经排入关键最终状态时，
        // 它才是权威。避免在旧的销毁回调随后到达时
        // 再加一个完全相同的数据包。
        if (!hostWorld.TryQueueGrenadeDestroy(grenade))
            return true;

        return true;
    }
}
