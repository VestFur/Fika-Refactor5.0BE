using System.Reflection;
using EFT;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.HandsController;

/// <summary>
/// 被观察方（远端/机器人）的武器控制器不得在本地调整射击向量——射手同步过来的那一发
/// 已经处在它的最终位置上。上游 Fika 把这一点表达成注入的 <see cref="ObservedFirearmController"/> 上
/// 的一个空 override。
///
/// 注入类型的每个方法都会获得一个 Il2CppInterop 反向 P/Invoke 入口点（一个
/// 位于 InvokerDelegateMetadataV29 stub 之后的 "Invoker_&lt;signature&gt;" 动态方法，以及一个
/// 位于方法指针之后的 "Trampoline_&lt;signature&gt;&lt;type&gt;&lt;method&gt;" 动态方法）。这些入口是
/// 惰性 JIT 编译的——在第一次原生调用时，也就是在到达它们的那个原生 callout 内部；
/// JIT 在那里拒绝编译就是致命的 0x80131506 fail fast，而不是托管异常
/// （见 analysis/adjust-shot-vectors/REPORT.md）。没有 override 就意味着
/// 这个方法根本没有注入入口点。
///
/// 因此同样的行为被保留为原生方法上的一个 prefix：原生
/// 实现对每一个其他控制器都保持不变（本地玩家、没有 Fika 的
/// 观察控制器的 AI），只对被观察的那个跳过——这正是那个空
/// override 所做的事。这个 prefix 有意只声明 __instance，所以补丁自身的可调用
/// 形状也不携带任何结构体参数。
/// </summary>
public sealed class FirearmController_AdjustShotVectors_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(Player.FirearmController), nameof(Player.FirearmController.AdjustShotVectors),
        [typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType()]);

    [PatchPrefix]
    private static bool Prefix(Player.FirearmController __instance)
    {
        if (__instance is not ObservedFirearmController) return true;
        
        return false;
    }
}
