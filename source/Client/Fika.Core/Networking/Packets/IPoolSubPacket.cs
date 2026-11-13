using System;
using Fika.Core.Main.Players;

namespace Fika.Core.Networking.Packets;

/// <summary>
/// 表示一个具备执行与序列化能力的子数据包。
/// </summary>
public interface IPoolSubPacket : IDisposable
{
    /// <summary>
    /// 执行子数据包逻辑，可选择带 <see cref="FikaPlayer"/> 上下文。
    /// </summary>
    /// <param name="player">
    /// 参与执行的玩家。该参数可选，可以为 null。
    /// </param>
    public void Execute(FikaPlayer player = null);

    /// <summary>
    /// 把子数据包的数据序列化到给定的 <paramref name="writer"/>
    /// </summary>
    /// <param name="writer">
    /// 用于序列化数据的 writer。
    /// </param>
    public void Serialize(NetDataWriter writer);
    /// <summary>
    /// 从 <paramref name="reader"/> 反序列化子数据包
    /// </summary>
    /// <param name="reader">
    /// 用于反序列化数据的 reader
    /// </param>
    public void Deserialize(NetDataReader reader);
}
