using System;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

public sealed class FikaServerAutoStartResponsePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(
        typeof(QuestController), nameof(QuestController.HandleAutoStartQuestsDTO));

    [PatchPrefix]
    private static void Prefix(QuestController __instance, AutoStartQuestsDTO autoStartQuestsDto, out IDisposable __state)
    {
        __state = FikaQuestController.Find<ClientQuestController>(__instance) != null
            ? ServerAutoStartQuests.Begin(__instance, autoStartQuestsDto) : null;
    }

    [PatchPostfix]
    private static void Postfix(IDisposable __state, ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (__state != null) __result = ServerAutoStartQuests.Complete(__result, __state);
    }
}

public sealed class FikaServerAutoStartTransactionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(
        typeof(Player.PlayerInventoryController), nameof(ItemController.TryRunNetworkTransaction));

    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController __instance, OperationResult operationResult,
        Callback callback, ref Il2CppSystem.Threading.Tasks.Task<IResult> __result)
    {
        if (operationResult.Failed ||
            !ServerAutoStartQuests.Contains(__instance, operationResult.Value?.TryCast<QuestAcceptResult>())) return true;

        // 后端确认的任务接受属于 profile 记账，死亡之后也一样。请保留
        // 原生事务的校验、操作、回调与
        // 失败处理；不要开启玩家其他的物品栏事务。
        __result = NativeBaseCalls.CallServerQuestTransaction(__instance, operationResult, callback);
        return false;
    }
}
