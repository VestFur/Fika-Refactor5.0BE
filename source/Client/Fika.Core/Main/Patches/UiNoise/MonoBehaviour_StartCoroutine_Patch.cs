using System;
using System.Reflection;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using SPTushonka.Reflection.Patching;
using UnityEngine;

namespace Fika.Core.Main.Patches.UiNoise;

/// <summary>
/// 移除红色报错「<c>Coroutine couldn't be started because the the game object 'TimerPanel' is inactive!</c>」
/// 这一族信息，同时不改变游戏的行为。
///
/// 分析：analysis/red-errors-coroutines（E2）。2026-10-10 21:35 的战局（BHERKP，Sandbox）在主机生成到
/// 本地玩家死亡之间写下了五次该错误。它们没有一次来自 Fika：Fika 对该面板的每次驱动
/// 都在 <see cref="FikaUiPanels.CanDrive"/> 之后（LogOutput.log 中有四行指明了被跳过的
/// 位置），而通过该守卫的驱动根本不可能产生这个错误（守卫要求对象处于激活状态），
/// 唯一一处未被守卫保护却触碰该面板的 Fika 语句
/// （<c>BaseGameController.cs:524 _abstractGame.GameUi.TimerPanel.ProfileId = profileId</c>）在那次战局中并未执行
/// ——诊断日志记录主开关「Skip Countdown Body」为 ON，语句 2/8
/// 被显式跳过。因此驱动代码是游戏自身的 HUD 代码，它一直在更新已关闭
/// 面板的计时器。
///
/// Unity 对该调用自身的回应是：写一条红色错误、什么都不启动、返回 null。本 prefix 产生的
/// 结果完全相同——<c>__result = null</c>，原方法被跳过——并通过
/// <see cref="FikaUiPanels.ReportRefusedEngineStart"/> 报告一次驱动方（组件类型、协程类型、游戏对象名）。协程
/// 类型正是 Unity 消息所隐去的部分：对于迭代器方法，运行时会按方法名命名其状态
/// 机（<c>EFT.UI.ExtractionTimersPanel+&lt;Co_MovePivot&gt;d__NN</c>），因此下一次战局
/// 能指出驱动已关闭面板的游戏方法，而不只是统计错误条数。
///
/// 为什么选引擎方法而不是面板：跳过 <c>ExtractionTimersPanel.SetTime</c> 的整次调用
/// 也会跳过它的非 UI 记账（profile 绑定、已存时间），而只抑制协程启动
/// 则精确复现了对象未激活时的原版结果。游戏在其他地方启动的每个协程都
/// 不受影响：处于激活状态的对象会在第一次检查时就从 prefix 返回。
///
/// 守卫是异常安全的：出现意外形态时会退化为一行警告，原版调用照常
/// 执行。
/// </summary>
internal sealed class MonoBehaviour_StartCoroutine_Patch : ModulePatch
{
    private static bool _guardFailed;

    protected override MethodBase GetTargetMethod()
    {
        // 只有 IEnumerator 重载能为面板做动画；两个 string 重载不可能产生这个错误。
        foreach (var method in typeof(MonoBehaviour).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != nameof(MonoBehaviour.StartCoroutine))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Il2CppSystem.Collections.IEnumerator))
            {
                return method;
            }
        }

        throw new MissingMethodException("UnityEngine.MonoBehaviour.StartCoroutine(" +
            typeof(Il2CppSystem.Collections.IEnumerator).FullName +
            ") does not exist in this build; the inactive-panel coroutine guard cannot be installed.");
    }

    [PatchPrefix]
    private static bool Prefix(MonoBehaviour __instance, Il2CppSystem.Collections.IEnumerator routine, ref Coroutine __result)
    {
        try
        {
            

            if (__instance == null)
            {
                return true;
            }

            var owner = __instance.gameObject;
            if (owner == null || owner.activeInHierarchy)
            {
                return true;
            }

            // 引擎无法在未激活的对象上启动协程：它会写下红色错误并返回
            // null。返回同样的 null，并让报告指明调用方。
            __result = null;
            FikaUiPanels.ReportRefusedEngineStart(__instance, routine);
            return false;
        }
        catch (Exception exception)
        {
            if (!_guardFailed)
            {
                _guardFailed = true;
                
            }

            return true;
        }
    }
}
