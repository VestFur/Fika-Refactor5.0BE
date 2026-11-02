using EFT;

namespace Fika.Core.Main.HostClasses;

/// <summary>主机侧的催泪瓦斯手雷，会发出 Fika 快照。</summary>
public sealed class FikaHostTearGasGrenade : TearGasGrenade
{
    public FikaHostTearGasGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<TearGasGrenade>(this);
    }

    public FikaHostTearGasGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostTearGasGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public override bool HasNetData => true;
}
