using System.Reflection;
using Comfort.Common;
using SPTushonka.Reflection.Patching;
using Il2CppSystems.Effects;

namespace Fika.Core.Main.Patches.Muzzle;

/// <summary>
/// 本补丁在 <c>MuzzleManager.IMuzzleParticlePivot.Play(EMuzzleParticlePivot, Transform)</c> 期间跳过一次 LINQ 分配
/// </summary>
/// <remarks>该分配由 <b>ifp</b> 发现</remarks>
/// <remarks>
/// 在 EFT 1.2.0.47888 上有意不安装。目标是显式接口实现，而
/// Il2CppInterop 1.6.7 针对 <c>NativeInterfaceMethodInfoPtr_IMuzzleParticlePivot_Play_...</c> 生成其 stub；
/// <c>Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod</c> 会在 stub 中扫描以
/// <c>NativeMethodInfoPtr_</c> 开头的字段，因此什么也找不到，于是
/// <c>Il2CppDetourMethodPatcher.IsValid</c> 保持 false，Harmony 回退到 ManagedMethodPatcher（修补
/// CLR stub，而原生代码从不调用它）。这正是 Fika.Core 的 PatchManager 所拒绝的：
/// "requires an IL2CPP detour; resolved HarmonyLib.Public.Patching.ManagedMethodPatcher"。同样的优化
/// 也随 SPTushonka.Custom（<c>MemoryImprovements.MuzzleManagerPlayPatch</c>）发布，prefix 体完全一致；
/// 再安装一次同样不会改变运行时行为。没有其他东西调用该方法，因此缺少该补丁的
/// 唯一影响就是每次枪口播放多一次 LINQ 分配。一旦该构建（或更新版本的
/// Il2CppInterop）把该实现暴露在可 detour 的 <c>NativeMethodInfoPtr_</c> 字段之后，例如
/// 以 <c>MuzzleManager.Shot(bool, float)</c> 作为非接口调用者，就可以去掉 [IgnoreAutoPatch]。
/// </remarks>
[IgnoreAutoPatch]
public sealed class MuzzleManager_Play_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MuzzleManager).GetMethod("IMuzzleParticlePivot.Play", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    [PatchPrefix]
    public static bool Prefix(EMuzzleParticlePivot pivot, Transform pTransform)
    {
        var instance = Singleton<Effects>.Instance;
        var commonSystems = instance.MuzzleEffect.CommonSystems;
        instance.TryAddToMBOITParticleManager(commonSystems);
        for (var i = 0; i < commonSystems.Length; i++)
        {
            var container = commonSystems[i];
            if (container.Pivot == pivot)
            {
                var rootParticleSystem = container.RootParticleSystem;
                var t = rootParticleSystem.transform;
                t.SetPositionAndRotation(pTransform.position, pTransform.rotation);

                rootParticleSystem.Stop(true);
                rootParticleSystem.Play(true);
                break;
            }
        }

        return false;
    }
}
