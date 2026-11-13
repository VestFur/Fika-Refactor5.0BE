namespace Fika.Core.Networking.Packets;

/// <summary>
/// 可与 <see cref="IFikaNetworkManager.RegisterReusable{T}(System.Action{T})"/> 一起使用的数据包 <br/>
/// 所有数据都必须是属性
/// </summary>
public interface IReusable
{
    /// <summary>
    /// 校验是否存在要发送的数据
    /// </summary>
    public bool HasData { get; }
    /// <summary>
    /// 在发送/接收之后清空数据包中的全部数据
    /// </summary>
    public void Flush();
}