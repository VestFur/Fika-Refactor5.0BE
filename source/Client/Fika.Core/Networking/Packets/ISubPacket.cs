using Fika.Core.Main.Players;

namespace Fika.Core.Networking.Packets;

/// <summary>
/// 表示一个具备执行与序列化能力的子数据包。
/// </summary>
public interface ISubPacket
{
    /// <summary>
    /// 执行子数据包逻辑，可选地传入一个 <see cref="FikaPlayer"/> 上下文。
    /// </summary>
    /// <param name="player">
    /// 参与本次执行的玩家。该参数可选，可以为 null。
    /// </param>
    public void Execute(FikaPlayer player = null);

    /// <summary>
    /// 把子数据包的数据序列化到提供的 <see cref="NetDataWriter"/> 中。
    /// </summary>
    /// <param name="writer">
    /// 用于序列化数据的写入器。
    /// </param>
    public void Serialize(NetDataWriter writer);
}
