// © 2026 Lacyway All Rights Reserved

using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses.HandsControllers;

/// <summary>
/// 仅由 AI 使用
/// </summary>
internal sealed class ObservedQuickGrenadeController : Player.QuickGrenadeThrowHandsController
{
    public ObservedQuickGrenadeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.QuickGrenadeThrowHandsController>(this);
    }


    public ObservedQuickGrenadeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedQuickGrenadeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public static ObservedQuickGrenadeController Create(FikaPlayer player, ThrowWeap item)
    {
        return CreateController<ObservedQuickGrenadeController>(player, item);
    }

    public override bool CanChangeCompassState(bool newState)
    {
        return false;
    }

    public override bool CanRemove()
    {
        return true;
    }

    public override void OnCanUsePropChanged(bool canUse)
    {
        // 不做任何处理
    }

    public override void SetCompassState(bool active)
    {
        // 不做任何处理
    }

    /// <summary>
    /// 原生的投掷手雷方法，我们改用 <see cref="SpawnGrenade(float, Vector3, Quaternion, Vector3, bool)"/>
    /// </summary>
    /// <param name="timeSinceSafetyLevelRemoved"></param>
    /// <param name="position"></param>
    /// <param name="rotation"></param>
    /// <param name="force"></param>
    /// <param name="lowThrow"></param>
    public override void ThrowGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        // 不做任何处理
    }

    /// <summary>
    /// 生成一枚手雷，使用 <see cref="SubPackets.GrenadePacket"/> 中的数据
    /// </summary>
    /// <param name="timeSinceSafetyLevelRemoved">保险解除后经过的时间，使用 0f</param>
    /// <param name="position">起始的 <see cref="Vector3"/> 位置</param>
    /// <param name="rotation">手雷的 <see cref="Quaternion"/> 旋转</param>
    /// <param name="force">施加于手雷的 <see cref="Vector3"/> 力</param>
    /// <param name="lowThrow">是否为低抛</param>
    public void SpawnGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call57(this, timeSinceSafetyLevelRemoved, position, rotation, force, lowThrow);
    }
}
