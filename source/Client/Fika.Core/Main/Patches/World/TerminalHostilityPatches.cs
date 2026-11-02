using System;
using System.Linq;
using System.Reflection;
using EFT;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

// (analysis/terminal-hostility-graph, hotfix 2026-10-14) 本文件的七个观察补丁均处于禁用状态。
//
// 原因：在 terminal-hostility-graph 轮次的首次实机联机测试中，客户端完全没有受到伤害
// （「客户端无敌」），而主机进程在自己的玩家受到坠落伤害时立刻崩溃——也就是
// 这些补丁与免疫分发所依附的那两件事。本文件的每个补丁都通过
// PatchAll 安装，因此会给原生伤害／区域路径加上托管 detour（Player.ApplyDamageInfo、
// SniperFiringZone.IsPlayerCanBeDamaged、BorderZone.ProcessIncomingPacket、两个免疫写入者、
// SniperFireTargetManager、BotGroupHostility.AddEnemyAction），而其中某一个发生 fail-fast 中止时，
// 其函数体自带的 try/catch 是无法捕获的。在它们能够被重新引入、由启动
// 开关决定是否安装之前，伤害管线的安全性优先：这次
// 移植不会在那里添加任何 detour。
//
// 仍然生效的部分：FikaTerminalHostility 本身（那些线与分发）由 FikaSentryAccess 调用，
// 因此 BJ/BM/BT 的哨兵访问链路继续工作；FikaTerminalHostility.ImmunityEnabled 默认
// 为 false（见该文件），所以狙击手免疫容器也不再从这里写入。
//
// 本文件之前的完整内容保存在报告旁边：
//     mods/fika-47888/analysis/terminal-hostility-graph/_disabled-patches/TerminalHostilityPatches.cs.txt
// 恢复它意味着恢复两部分：本文件以及 FikaTerminalHostility 的默认值。
internal static class TerminalHostilityPatches
{
    /// <summary>观察文件存在但其补丁未安装时为 true。</summary>
    internal const bool Disabled = true;

    /// <summary>保留以便后续轮次无需翻查报告就能找到保存的源码。</summary>
    internal const string PreservedSource =
        "analysis/terminal-hostility-graph/_disabled-patches/TerminalHostilityPatches.cs.txt";
}
