using CommonAssets.Scripts.Game;
using EFT.BufferZone;
using System.Reflection;
using EFT;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

public class GameWorld_Create_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GameWorld)
            .GetMethod(nameof(GameWorld.Create), BindingFlags.Static | BindingFlags.Public)
            .MakeGenericMethod(typeof(ClientLocalGameWorld));
    }

    [PatchPrefix]
    public static bool Prefix(ref ClientLocalGameWorld __result, GameObject gameObject, ObjectsFactory objectsFactory, EUpdateQueue updateQueue, Il2CppSystem.Nullable<MongoID> currentProfileId)
    {

        if (!FikaBackendUtils.RequestFikaWorld)
        {
            // 为藏身处/教程世界保留 SPT 5 的完整初始化，
            // 包括 Mono 构建中并不存在的字段。
            return true;
        }

        if (FikaBackendUtils.IsServer)
        {
            __result = FikaHostGameWorld.Create(gameObject, objectsFactory, updateQueue, currentProfileId);
        }
        else
        {
            __result = FikaClientGameWorld.Create(gameObject, objectsFactory, updateQueue, currentProfileId);
        }
        FikaBackendUtils.RequestFikaWorld = false;
        return false;
    }

}
