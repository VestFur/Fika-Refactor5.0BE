using EFT;
using EFT.Counters;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core;

internal static class NativeCounters
{
    // 计数器键包含 native 装箱的枚举，而不是 CLR 装箱的枚举值。
    public static Il2CppReferenceArray<Il2CppSystem.Object> ExperienceTags() =>
        new(new Il2CppSystem.Object[]
        {
            Il2CppSystem.Enum.ToObject(Il2CppType.Of<CounterTag>(), (int)CounterTag.Exp)
        });

    public static CountersCollection.Identifier ExitStanding() =>
        new(CounterValueType.Float, new Il2CppSystem.Object[]
        {
            Il2CppSystem.Enum.ToObject(Il2CppType.Of<CounterTag>(), (int)CounterTag.FenceStanding),
            Il2CppSystem.Enum.ToObject(Il2CppType.Of<EFenceStandingSource>(), (int)EFenceStandingSource.ExitStanding)
        });
}
