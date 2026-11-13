using System;
using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

/// <summary>
/// SPT 5.0 会推迟每一个 QuestItem 战利品条目：世界战利品流程把描述符交给
/// GameWorld.GetQuestLootReady，后者把任务条目移入 GameWorld._questLootItems，而只有
/// GameWorld.ManageQuestLoot(Player) 才会真正把它们放进世界（按玩家处理，这也是移植版中
/// 只有无头模式调用 GetQuestLootReady 的原因）。Fika 用自己的游戏模式驱动突袭，
/// 因此被推迟的条目永远排在队列中，被强制生成的任务战利品——其中包括立交桥的货运
/// 清单 库存清单 / 损失惨重——从不在突袭中出现。
/// 在世界战利品流程之后立即把本地玩家的那一份实体化。
/// </summary>
internal sealed class GameWorld_SpawnLoot_QuestLoot_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(GameWorld).GetMethod("SpawnLoot",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(JsonType.LootData), typeof(bool) }, null);
        if (target == null)
        {
            throw new MissingMethodException("GameWorld.SpawnLoot(JsonType.LootData, bool)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(GameWorld __instance)
    {
        try
        {
            // AP（analysis/nonfika-raid-inertness）：这个补丁之所以存在，是因为 *Fika 的*游戏模式从不
            // 调用 GetQuestLootReady，于是被推迟的任务战利品一直留在队列里。原生单人
            // 突袭由原生本地游戏驱动，它会在合适的时机把自己的那一份实体化——
            // 我们提前调用往好里说是多余的，往坏里说就是第二次实体化。在 Fika 突袭
            // 之外，原生流程不被干涉；[Inert] skip=GameWorld.SpawnLoot 后置会点名这一点。
            if (FikaRaidGate.Skip("GameWorld.SpawnLoot postfix"))
            {
                return;
            }

            if (__instance == null)
            {
                return;
            }

            var player = __instance.MainPlayer;
            if (player == null)
            {
                return;
            }

            var questLoot = __instance._questLootItems;
            if (questLoot == null || questLoot.Count == 0)
            {
                return;
            }

            FikaGlobals.LogInfo($"[QuestLoot] materialising {questLoot.Count} deferred quest loot entries " +
                $"for {player.ProfileId}");
            _ = __instance.ManageQuestLoot(player);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[QuestLoot] materialising the deferred quest loot failed: {exception}");
        }
    }
}
