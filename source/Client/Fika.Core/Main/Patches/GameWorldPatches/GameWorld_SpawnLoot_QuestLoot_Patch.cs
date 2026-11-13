using System;
using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

/// <summary>
/// SPT 5.0 defers every QuestItem loot entry: the world loot pass routes the descriptor through
/// GameWorld.GetQuestLootReady, which moves the quest entries into GameWorld._questLootItems, and only
/// GameWorld.ManageQuestLoot(Player) ever puts them into the world (per-player, which is why the headless
/// mode is the only place in the port that called GetQuestLootReady). Fika drives the raid through its own
/// game mode, so the deferred entries stayed queued forever and forced quest spawns - the Interchange cargo
/// manifests of 库存清单 / 损失惨重 among them - never appeared in raid.
/// Materialise the local player's share right after the world loot pass.
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
