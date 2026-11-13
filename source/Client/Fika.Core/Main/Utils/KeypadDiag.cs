using System;
using System.Collections.Generic;

namespace Fika.Core.Main.Utils;

/// <summary>
/// BB (analysis/icebreaker-keypad-door)：为每次密码门判定写一行可 grep 的记录，由做出该判定的
/// 机器负责写出。这一族记录回答了只凭客户端日志无法判定的以下问题，而这正是 icebreaker 报告
/// 所欠缺的：
///
///   stage=menu        原生交互分发器是否为这个 PasscodeTerminal 提供了可执行动作
///                     （这里的名称是原生的、已本地化的名称 —— 这就是那句「哪个交互
///                     被吃掉了」的记录）；
///   stage=panel       原生 PasscodeInputPanel 是否打开，以及由哪个 controller 打开；
///   stage=submit      原生 submit 是否通过了游戏自身的校验，还是被以
///                     "Tried to enter code on location without passcodes" 拒绝（并转发给了主机）；
///   stage=init        主机的 passcode 快照是否到达本机，以及它的提示是否被应用；
///   stage=attempt     （host）对端的一次尝试发生了什么，以及这个对端是否得到了答复；
///   stage=result      （client）主机的答复是否到达了本机的面板；
///   stage=controller  本客户端的世界持有的是哪个 controller，以及该地点是否承载
///                     passcode 条目。BO (analysis/icebreaker-keypad-2) 扩展了这一阶段，加入了
///                     一个没有 controller 的客户端世界的三种状态：waiting（处于不含打印的
///                     宽限窗口内）、client-created（本移植自己构建了网络 controller，用的是
///                     它自己地点场景的 id）、以及 kept-native（连这个也失败了 —— 菜单保持为空）。
///                     'waited=' 在本族每一行中表示做出判定时 raid 已进行的时长。
///                     BU (analysis/icebreaker-keypad-3) 补上了主机自己那一半（result=host-created）。
///   stage=codes       BU：本次 raid 的密码表。一行说明 HOST 是怎么拿到它的（result=kept-native，来自
///                     游戏自身的 controller；result=generated，来自游戏自身的 PasscodeGenerator；或
///                     result=no-entries/no-codes，表示该机器所属地点没有任何可取的
///                     来源）、一行说明某个 CLIENT 收到了它（result=received/waiting）、还有一行说明
///                     客户端围绕它构建了游戏自身的本地 controller（result=local-native）。有了这一行
///                     之后，submit 阶段的 'settings=' 与 'terminals=' 就是该读的数字。
///   stage=keys        BU：本机场景中的键盘与提示分组已绑定到它的 controller
///                     （terminals=N hintGroups=M）。客户端场景在其世界之后加载，因此 raid 开始时这里的零值
///                     只是时序事实而非结论 —— 绑定会被重试。
///
/// 每一行对每个不同的文本只写一次，且本族记录达到 <see cref="MaxLines"/> 行后即停止，
/// 按每场 raid 计，因此一整场 raid 只耗费几次字典命中，而在
/// LogOutput.log 中 grep 一次 KeypadDiag 就是证据。这里的任何东西都不会向调用它的原生路径抛出异常。
/// </summary>
internal static class KeypadDiag
{
    /// <summary>
    /// 键盘修复的总开关（analysis/icebreaker-keypad-door）。关闭它时，既不会发生
    /// 主机对游戏所拒绝的 submit 的中继，也不会重试 passcode 快照请求，
    /// 本移植的行为与这一轮之前完全一致。它放在这里 —— 而不是 FikaConfig.cs，
    /// 那个文件属于另一个 agent —— 直到主 agent 把它接到自己的配置项上。
    /// </summary>
    internal static bool RepairEnabled = true;

    /// <summary>诊断族本身。这类记录很少出现，因此默认开启。</summary>
    // (2026-10-18 refactor W1b / tracker A21) 诊断族已关闭：下方的修复开关是本类目前唯一保留的行为。
    internal static bool Enabled = false;

    private const int MaxLines = 400;

    private static readonly HashSet<string> Written = new(StringComparer.Ordinal);

    /// <summary>host / client / headless / single —— 本移植每一行记录所用的词汇。</summary>
    internal static string Side => FikaRaidGate.Side;

    /// <summary>当本机不在 Fika raid 中、因而脱离原生流程时，值为 "yes"。</summary>
    internal static string Gated => FikaRaidGate.Inert ? "yes" : "no";

    internal static void Line(string stage, string detail)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var text = "stage=" + stage + " side=" + Side + " gated=" + Gated + " " + detail;
            lock (Written)
            {
                if (Written.Count >= MaxLines || !Written.Add(text))
                {
                    return;
                }
            }

            FikaGlobals.LogInfo(text, "KeypadDiag");
        }
        catch (Exception)
        {
            // 诊断绝不能成为原生交互失败的原因。
        }
    }

    internal static void Reset()
    {
        lock (Written)
        {
            Written.Clear();
        }
    }
}
