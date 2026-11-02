namespace Fika.Core.Networking.Packets.FirearmController;

/// <summary>
/// 描述一次带弹药的换弹操作所处的状态。
/// </summary>
public enum EReloadWithAmmoStatus : byte
{
    /// <summary>
    /// 当前没有换弹在进行。
    /// </summary>
    None,

    /// <summary>
    /// 换弹已开始。
    /// </summary>
    StartReload,

    /// <summary>
    /// 换弹已完成。
    /// </summary>
    EndReload,

    /// <summary>
    /// 换弹已中止。
    /// </summary>
    AbortReload
}
