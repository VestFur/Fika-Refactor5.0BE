namespace Fika.Core.Networking.Pooling;

/// <summary>
/// 提供创建与释放所有相关数据包池的工具方法。
/// </summary>
internal static class PoolUtils
{
    /// <summary>
    /// 对每个池管理器实例调用 <c>CreatePool</c> 方法，从而创建所有数据包池。
    /// </summary>
    public static void CreateAll()
    {
        FirearmSubPacketPoolManager.Instance.CreatePool();
        CommonSubPacketPoolManager.Instance.CreatePool();
        GenericSubPacketPoolManager.Instance.CreatePool();
    }

    /// <summary>
    /// 对每个池管理器调用 <c>Release</c> 方法，从而释放所有数据包池。
    /// </summary>
    public static void ReleaseAll()
    {
        FirearmSubPacketPoolManager.Release();
        CommonSubPacketPoolManager.Release();
        GenericSubPacketPoolManager.Release();
    }
}
