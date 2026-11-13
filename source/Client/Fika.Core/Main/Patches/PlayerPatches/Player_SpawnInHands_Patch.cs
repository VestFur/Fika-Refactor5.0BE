using System.Reflection;
using Audio.SpatialSystem;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Utils;
using RootMotion.FinalIK;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

/// <summary>
/// 修复 BSG 的 <see cref="SpatialAudioSystem"/> 导致空引用无限刷屏、从而降低每个玩家帧率的问题
/// </summary>
public class Player_SpawnInHands_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.SpawnInHands));
    }

    [PatchPrefix]
    public static bool Prefix(Player __instance, Item item, string parentBone)
    {
        if (__instance.IsYourPlayer)
        {
            return true;
        }

        __instance._spawnedKey = Singleton<ObjectsFactory>.Instance.CreateItem(item, Player.GetVisibleToCamera(__instance), __instance, true);
        var transform = __instance._spawnedKey.transform.FindTransform("pivot");
        var transform2 = __instance._limbs[0].solver.bone3.transform.FindTransform(parentBone);
        __instance._spawnedKey.transform.SetParent(transform2, false);
        __instance._spawnedKey.transform.localRotation = Quaternion.identity;
        __instance._spawnedKey.transform.localPosition = Vector3.zero;
        __instance._spawnedKey.SetActive(true);
        if (transform != null)
        {
            var quaternion = Quaternion.Inverse(transform.rotation) * transform2.rotation;
            __instance._spawnedKey.transform.localRotation *= quaternion;
            var vector = transform2.position - transform.position;
            __instance._spawnedKey.transform.position += vector;
        }
        else
        {
#if DEBUG
            FikaGlobals.LogError($"pivot not found in, {__instance._spawnedKey}, for keyId = {item.Id}");
#endif
        }

        return false;
    }
}
