using System;
using System.Reflection;
using CommonAssets.Scripts.ArtilleryShelling;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Artillery;

public class ArtilleryServerProjectilesPool_CreateNewPoolElement_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(ArtilleryServerProjectilesPool), nameof(ArtilleryServerProjectilesPool.CreateNewPoolElement));

    [PatchPrefix]
    public static bool Prefix(ArtilleryServerProjectilesPool __instance)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        if (FikaSingleton<IFikaGame>.Instance?.GameController is not HostGameController host)
            throw new InvalidOperationException("Server artillery requires a Fika host game controller.");

        // IL2CPP 构造函数会订阅原版游戏单例。
        // 因此改为拦截原生工厂方法（InitPool/GetProjectile 也会调用它），
        // 因为当前安装的 Harmony 后端无法对构造函数进行 detour。
        var projectilePointer = IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<ArtilleryProjectileServer>.NativeClassPtr);
        if (projectilePointer == IntPtr.Zero)
            throw new InvalidOperationException("Could not allocate native artillery projectile.");

        // 生成的 IL2CPP 包装构造函数会调用原生
        // 构造函数，而后者会解引用原版火炮单例，
        // 在池正在初始化的过程中并不安全。因此直接分配
        // 原生对象，并初始化 Fika 拥有的每一个字段。
        var projectile = new ArtilleryProjectileServer(projectilePointer)
        {
            speed = 50f,
            arcHeight = -150f,
            explosionDistnaceRange = new(3f, 5f),
            zoneID = "",
            _explosiveItem = new(),
            id = UnityEngine.Random.Range(1, int.MaxValue)
        };
        projectile.SetExplosiveItemParams(__instance._artilleryShellingSettings.ProjectileExplosionParams);
        __instance._poolItems.Add(projectile);
        host.add_UpdateByUnity(projectile.OnUpdate);
        return false;
    }
}
