namespace Fika.Core.Networking.Packets;

/// <summary>
/// 表示一个请求数据包，包含请求、响应与序列化的处理。
/// </summary>
public interface IRequestPacket
{
    /// <summary>
    /// 在服务器上下文中处理来自网络对端的传入请求。
    /// </summary>
    /// <param name="peer">
    /// 发送该请求的网络对端。
    /// </param>
    /// <param name="server">
    /// 处理该请求的服务器实例。
    /// </param>
    public void HandleRequest(NetPeer peer, FikaServer server);

    /// <summary>
    /// 处理对某个请求的响应。
    /// </summary>
    public void HandleResponse();

    /// <summary>
    /// 把请求数据包的数据序列化到给定的 <see cref="NetDataWriter"/>。
    /// </summary>
    /// <param name="writer">
    /// 用于序列化数据的 writer。
    /// </param>
    public void Serialize(NetDataWriter writer);
}
