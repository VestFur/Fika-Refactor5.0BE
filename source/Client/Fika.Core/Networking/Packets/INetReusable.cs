namespace Fika.Core.Networking.Packets;

/// <summary>
/// 可与 <see cref="IFikaNetworkManager.RegisterNetReusable{T}(System.Action{T}){T}(System.Action{T})"/> 一起使用的数据包 <br/>
/// 需要手动序列化
/// </summary>
public interface INetReusable
{
    /// <summary>
    /// 在发送之后清空数据包中的全部数据
    /// </summary>
    public void Clear();
    /// <summary>
    /// 在接收之后清空数据包中的全部数据
    /// </summary>
    public void Flush();

    public void Serialize(NetDataWriter writer);
    public void Deserialize(NetDataReader reader);
}