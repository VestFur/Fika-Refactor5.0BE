using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Unity <c>WaitForSeconds</c> 的纯托管替代品。
///
/// 存在的原因：协程泵驱动的是托管迭代器，因此一个 yield 引擎对象的
/// 移植协程会让泵通过 interop 包装器读取 <c>WaitForSeconds.m_Seconds</c>。正是
/// 这次读取终结了 2026-10-10 的主机战局：诊断写下了 "read WaitForSeconds.m_Seconds"，
/// 紧接着 coreclr 在 JIT_ReversePInvokeEnterRare 之后抛出了不可捕获的 0x80131506。
/// 把时长保存在托管状态中，就完全移除了这条路径上的引擎对象。
/// </summary>
internal sealed class WaitSeconds
{
    internal readonly float Seconds;

    internal WaitSeconds(float seconds)
    {
        Seconds = seconds;
    }
}
