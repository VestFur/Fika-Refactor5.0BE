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

        // The IL2CPP constructor subscribes to the vanilla game singleton.
        // Intercept the native factory (also called by InitPool/GetProjectile),
        // because the installed Harmony backend cannot detour constructors.
        var projectilePointer = IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<ArtilleryProjectileServer>.NativeClassPtr);
        if (projectilePointer == IntPtr.Zero)
            throw new InvalidOperationException("Could not allocate native artillery projectile.");

        // The generated IL2CPP wrapper constructor invokes the native
        // constructor, which dereferences the vanilla artillery singleton and
        // is not safe while the pool is being initialized. Allocate the
        // native object directly and initialize every field Fika owns.
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
        host.UpdateByUnity += projectile.OnUpdate;
        return false;
    }
}
