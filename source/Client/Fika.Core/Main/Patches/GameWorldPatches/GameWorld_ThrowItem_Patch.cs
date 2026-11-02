using System.Linq;
using System.Reflection;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

public sealed class GameWorld_ThrowItem_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GameWorld).GetMethods()
            .First(x => x.Name == nameof(GameWorld.ThrowItem) && x.GetParameters().Length == 3);
    }

    [PatchPostfix]
    public static void Postfix(LootItem __result, IPlayer player)
    {
        // AP（analysis/nonfika-raid-inertness）：抛掷正是 2026-10-12 23:52 那次崩溃最终落入的调用链
        // （武器的原生丢弃，"Perform UNLOAD" 作为它的最后一行）。在不是 Fika 突袭的
        // 突袭里，这个后置过去会把注入的组件（ItemPositionSyncer）挂到一个*原生*
        // 战利品物品上，并解析这里并不存在的 FikaClient/FikaServer——该组件随后
        // 每个物理帧都出错，而且从不销毁自己。在我们自己的突袭之外，我们的任何东西都
        // 不碰被丢下的物品；[Inert] skip=GameWorld.ThrowItem 后置这一行就是证据。
        if (FikaRaidGate.Skip("GameWorld.ThrowItem postfix"))
        {
            return;
        }

        // ThrowItem 对 IL2CPP 下的原生对象返回一个 LootItem
        // 包装器。通过原生身份解析被观测的子类型；
        // CLR 的 `is` 检查否则可能错过网络位置钩子。
        if (__result?.TryCast<ObservedLootItem>() is { } observedLootItem)
        {
            if (player.IsYourPlayer || player.IsAI)
            {
                ItemPositionSyncer.Create(observedLootItem.gameObject, FikaBackendUtils.IsServer, observedLootItem);
                observedLootItem._isNetworkGame = false;
                return;
            }

            observedLootItem._isNetworkGame = true;
        }
    }
}
