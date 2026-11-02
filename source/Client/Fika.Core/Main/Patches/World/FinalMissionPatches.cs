using System.Reflection;
using CommonAssets.Scripts.Cutscenes;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

internal sealed class FinalMissionZoneInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(FinallExitZone), nameof(FinallExitZone.InitZoneServer));
    [PatchPrefix]
    public static bool Prefix(FinallExitZone __instance)
    {
        if (!FikaFinalMissionDirector.Owns(__instance)) return true;
        if (FikaFinalMissionDirector.Arm(__instance)) return false;
        
        return FikaBackendUtils.IsServer;
    }
}

internal sealed class FinalMissionZoneUpdatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(FinallExitZone), nameof(FinallExitZone.Update));
    [PatchPrefix]
    public static bool Prefix(FinallExitZone __instance) => !FikaFinalMissionDirector.DrivesZone(__instance);
}

internal sealed class FinalMissionTimerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(StartCutsceneByTimer), nameof(StartCutsceneByTimer.Update));
    [PatchPrefix]
    public static bool Prefix(StartCutsceneByTimer __instance)
    {
        if (!FikaFinalMissionDirector.Bound) return true;
        FikaFinalMissionDirector.TickTimer(__instance);
        return false;
    }
}

internal sealed class FinalMissionClientExitPatch : ModulePatch
{
    // 拦截 SPT 的动作 shim，而不是游戏共享的空原生 Init 方法体。
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.ServerActionPatches.ClientInitActionPatch), "Postfix");
    [PatchPrefix]
    public static bool Prefix(CutscenesClientController __0, string __1)
    {
        // 目标是 SPT 的静态 Postfix；其名为 __instance 的参数
        // 是第零个实参，而不是目标方法的实例。
        if (!FikaFinalMissionDirector.Bound) return true;
        if (FikaFinalMissionDirector.IsLocalFinalExitAction(__0, __1))
        {
            FikaFinalMissionDirector.LocalExitAction(__0, __1);
            return false;
        }
        // (analysis/terminal-ending-settlement) 一局到达结局画面后所需的那一行：这台机器的结局
        // 退场动作是否被上面的守卫拒绝了？如果是，SPT 自己的 shim 就会
        // 接手——而它的 StopRaid 结束的是 LocalGame，而 Fika 突袭不是 LocalGame，所以那条路径是
        // 一个静默的死胡同（这也是本移植版自己掌管结局及其确认的原因）。
        if (FikaFinalMissionDirector.HasLocalEnding)
        {
            
        }

        return true;
    }
}

internal sealed class FinalMissionTimeoutPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(FinalCutsceneTimer), nameof(FinalCutsceneTimer.ImmediateExitFromRaid));

    [PatchPrefix]
    public static bool Prefix() => !FikaFinalMissionDirector.Bound || !FikaFinalMissionDirector.EndLocalOnTimeout();
}

internal sealed class FinalMissionServerExitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(SPTushonka.SinglePlayer.Patches.Cutscenes.ServerActionPatches.InitActionPatch), "Prefix");

    [PatchPrefix]
    public static bool Prefix(CutscenesServerController __0, string __1, string __3, ref bool __result)
    {
        if (!FikaFinalMissionDirector.Bound || __0?._actions == null ||
            !__0._actions.TryGetValue(__1, out var action) ||
            action.TryCast<CutsceneActionExitAfterCutscene>() == null ||
            !FikaFinalMissionDirector.SuppressExitAction(__3)) return true;
        // SPT 的 shim 会为整个 LocalGame 调用 StopRaid。最终任务
        // 结果是按玩家分别处理的，并由 FikaFinalMissionDirector 确认。
        __result = false;
        return false;
    }
}
