using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

/// <summary>
/// 催泪瓦斯手雷的 IL2CPP observed 实现。SPT 5 新增了一个
/// 独立的本机（native）类型，因此它必须与其他 observed 投掷物
/// 沿着同一条插值路径注册。
/// </summary>
public sealed class FikaClientTearGasGrenade : ObservedTearGasGrenade
{
    public FikaClientTearGasGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ObservedTearGasGrenade>(this);
    }

    public FikaClientTearGasGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientTearGasGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public override void ApplyNetPacket(GrenadeSyncPacket packet, float remoteTime)
    {
        ObservedGrenadeSync.Apply(this, packet, 10f);
    }
}
