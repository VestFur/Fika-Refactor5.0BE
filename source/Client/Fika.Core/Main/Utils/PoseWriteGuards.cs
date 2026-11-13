namespace Fika.Core.Main.Utils;

/// <summary>
/// 观察者姿态写入的「值未变就不写」守卫（对应上游 config 项 'Coop / Pose Write Guards'）。
///
/// 本作的原生 setter 会把值无条件推进 animator（<c>MovementContext.SmoothedCharacterMovementSpeed</c> 与
/// <c>SprintSpeed</c> 都会调到 PlayerAnimator），而这份移植版曾对每个观察者每帧都写一次、哪怕值没变——
/// 这正是 analysis/stutter-pose-reset 里「下压再回弹」那种卡顿的形状来源。
///
/// 设为 false 会精确恢复「没有守卫」时的行为：每个点位每帧都写。每个点位多付一次静态 bool 读取
/// （61 个观察者时约 500 次/帧）。
///
/// (2026-10-26 W13) 本轮从上游 3.0.4 原样恢复本文件与 6 个守卫点位（5 处在 <see cref="Fika.Core.Main.Players.ObservedPlayer"/>、
/// 1 处在 <see cref="Fika.Core.Main.ObservedClasses.ObservedMovementContext"/>）。配置项暂未接回，默认恒为 true，
/// 即与上游默认行为一致；等 R2 对账走到 FikaConfig 时再补 ConfigEntry。
/// </summary>
internal static class PoseWriteGuards
{
    internal static bool Enabled = true;
}
