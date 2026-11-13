using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Player;

namespace Fika.Core.Networking;

/// <summary>
/// 定义 Fika 网络管理器的契约，负责处理网络通信、
/// 玩家同步、VOIP 以及数据包注册。
/// </summary>
public interface IFikaNetworkManager
{
    /// <summary>
    /// 获取或设置此网络管理器实例的网络标识符。
    /// </summary>
    int NetId { get; set; }

    /// <summary>
    /// 获取或设置负责管理游戏逻辑的 <see cref="CoopHandler"/>。
    /// </summary>
    CoopHandler CoopHandler { get; set; }

    /// <summary>
    /// 获取或设置当前会话的战局阵营类型。
    /// </summary>
    ESideType RaidSide { get; set; }

    /// <summary>
    /// 获取网络数据包的发送速率。
    /// </summary>
    int SendRate { get; }

    /// <summary>
    /// 获取或设置一个值，指示是否允许 VOIP。
    /// </summary>
    bool AllowVOIP { get; set; }

    /// <summary>
    /// 是否启用严格的库存同步
    /// </summary>
    bool StrictInventorySync { get; set; }

    /// <summary>
    /// 获取或设置会话中被观察的玩家列表。
    /// </summary>
    List<ObservedPlayer> ObservedPlayers { get; set; }

    /// <summary>
    /// 用于生成物品的临时仓库
    /// </summary>
    public Stash TemporaryStash { get; set; }

    /// <summary>
    /// 获取或设置会话中的玩家总数。
    /// </summary>
    int PlayerAmount { get; set; }

    /// <summary>
    /// 创建用于游戏内交流的 Fika 聊天系统。
    /// </summary>
    void CreateFikaChat();

    /// <summary>
    /// 为指定的 <see cref="FikaPlayer"/> 设置游戏变量。
    /// </summary>
    /// <param name="fikaPlayer">要为其设置游戏变量的玩家。</param>
    void SetupGameVariables(FikaPlayer fikaPlayer);

    /// <summary>
    /// 发送数据包。
    /// </summary>
    /// <typeparam name="T">要发送的数据包类型，必须实现 <see cref="INetSerializable"/>。</typeparam>
    /// <param name="packet">要发送的数据包实例，按引用传递。</param>
    /// <param name="deliveryMethod">发送数据包所使用的投递方式（可靠、不可靠等）。</param>
    /// <param name="broadcast">如果为 <see langword="true"/>，数据包将发送给多个接收者；否则将发送给单个目标（服务器始终为广播）。</param>
    void SendData<T>(ref T packet, DeliveryMethod deliveryMethod, bool broadcast = false) where T : INetSerializable;

    /// <summary>
    /// 向一个或多个对等端发送通用网络数据包。
    /// </summary>
    /// <param name="type">通用子数据包类型标识符，用于确定数据包的处理方式。</param>
    /// <param name="subpacket">要发送的子数据包负载，必须实现 <see cref="IPoolSubPacket"/>。</param>
    /// <param name="broadcast">如果为 <see langword="true"/>，数据包将发送给多个接收者；否则将发送给单个目标（服务器始终为广播）。</param>
    /// <param name="peerToIgnore">可选的对等端，将其排除在数据包接收方之外，通常是发送方。</param>
    void SendGenericPacket(EGenericSubPacketType type, IPoolSubPacket subpacket, bool broadcast = false, NetPeer peerToIgnore = null);

    /// <summary>
    /// 发送实现 <see cref="INetReusable"/> 的数据包，并手动控制序列化。
    /// </summary>
    /// <typeparam name="T">数据包类型，必须实现 <see cref="INetReusable"/>。</typeparam>
    /// <param name="packet">对要发送的数据包实例的引用。</param>
    /// <param name="deliveryMethod">发送数据包所使用的投递方式（可靠、不可靠等）。</param>
    /// <param name="broadcast">如果为 <see langword="true"/>，数据包将发送给多个接收者；否则将发送给单个目标（服务器始终为广播）。</param>
    /// <param name="peerToIgnore">可选的对等端，将其排除在数据包接收方之外，通常是发送方。</param>
    void SendNetReusable<T>(ref T packet, DeliveryMethod deliveryMethod, bool broadcast = false, NetPeer peerToIgnore = null) where T : INetReusable;

    /// <summary>
    /// 将数据包直接发送给指定的对等端。
    /// </summary>
    /// <typeparam name="T">要发送的数据包类型，必须实现 <see cref="INetSerializable"/>。</typeparam>
    /// <param name="packet">要发送的数据包实例，按引用传递。</param>
    /// <param name="deliveryMethod">发送数据包所使用的投递方式（可靠、不可靠等）。</param>
    /// <param name="peer">将接收该数据包的目标 <see cref="NetPeer"/>。</param>
    /// <remarks>
    /// 只应作为 <see cref="FikaServer"/> 使用，因为 <see cref="FikaClient"/> 只有一个 <see cref="NetPeer"/>。
    /// </remarks>
    void SendDataToPeer<T>(ref T packet, DeliveryMethod deliveryMethod, NetPeer peer) where T : INetSerializable;

    /// <summary>
    /// 使用快速序列化发送玩家状态。
    /// </summary>
    /// <param name="packet">要发送的玩家状态数据包。</param>
    void SendPlayerState(ref PlayerStateData packet);

    /// <summary>
    /// 将原始 VOIP 音频数据发送给指定对等端或多个接收者。
    /// </summary>
    /// <param name="data">要发送的音频数据，以字节的 <see cref="ArraySegment{T}"/> 形式提供。</param>
    /// <param name="deliveryMethod">发送数据包所使用的投递方式（可靠、不可靠等）。</param>
    /// <param name="peer">接收音频数据的目标对等端（仅服务器使用）。</param>
    void SendVOIPData(ArraySegment<byte> data, DeliveryMethod deliveryMethod, NetPeer peer = null);

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册数据包。
    /// </summary>
    /// <typeparam name="T">数据包类型。</typeparam>
    /// <param name="handle">接收数据包时要运行的 <see cref="Action"/>。</param>
    void RegisterPacket<T>(Action<T> handle) where T : INetSerializable, new();

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册带用户数据的数据包。
    /// </summary>
    /// <typeparam name="T">数据包类型。</typeparam>
    /// <typeparam name="TUserData">用户数据类型。</typeparam>
    /// <param name="handle">接收数据包时要运行的 <see cref="Action"/>。</param>
    void RegisterPacket<T, TUserData>(Action<T, TUserData> handle) where T : INetSerializable, new();

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册可复用数据包。可复用数据包在 <see cref="NetManager"/> 的整个生命周期内使用同一实例。
    /// 自定义类型必须先用 <see cref="RegisterCustomType{T}(Action{NetDataWriter, T}, Func{NetDataReader, T})"/> 注册。
    /// </summary>
    /// <typeparam name="T">数据包类型。</typeparam>
    /// <param name="handle">接收数据包时要运行的 <see cref="Action"/>。</param>
    void RegisterReusable<T>(Action<T> handle) where T : class, IReusable, new();

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册带用户数据的可复用数据包。可复用数据包在 <see cref="NetManager"/> 的整个生命周期内使用同一实例。
    /// 自定义类型必须先用 <see cref="RegisterCustomType{T}(Action{NetDataWriter, T}, Func{NetDataReader, T})"/> 注册。
    /// </summary>
    /// <typeparam name="T">数据包类型。</typeparam>
    /// <typeparam name="TUserData">用户数据类型。</typeparam>
    /// <param name="handle">接收数据包时要运行的 <see cref="Action"/>。</param>
    void RegisterReusable<T, TUserData>(Action<T, TUserData> handle) where T : class, IReusable, new();

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册可复用网络数据包。
    /// </summary>
    /// <typeparam name="T">数据包类型。</typeparam>
    /// <param name="handle">接收数据包时要运行的 <see cref="Action"/>。</param>
    void RegisterNetReusable<T>(Action<T> handle) where T : class, INetReusable, new();

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册带用户数据的可复用网络数据包。
    /// </summary>
    /// <typeparam name="T">数据包类型。</typeparam>
    /// <typeparam name="TUserData">用户数据类型。</typeparam>
    /// <param name="handle">接收数据包时要运行的 <see cref="Action"/>。</param>
    void RegisterNetReusable<T, TUserData>(Action<T, TUserData> handle) where T : class, INetReusable, new();

    /// <summary>
    /// 向 <see cref="NetPacketProcessor"/> 注册自定义类型，以便其能够对该类型进行序列化。
    /// </summary>
    /// <typeparam name="T">要注册的类型。</typeparam>
    /// <param name="writeDelegate">序列化方法。</param>
    /// <param name="readDelegate">反序列化方法。</param>
    void RegisterCustomType<T>(Action<NetDataWriter, T> writeDelegate, Func<NetDataReader, T> readDelegate);

    /// <summary>
    /// 从数据包管理器中注销 <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T">要注销的 <see cref="INetSerializable"/></typeparam>
    void UnregisterPacket<T>() where T : INetSerializable;

    /// <summary>
    /// 从数据包管理器中注销 <typeparamref name="T"/>
    /// </summary>
    /// <typeparam name="T">要注销的 <see cref="INetReusable"/></typeparam>
    void UnregisterNetReusable<T>() where T : INetReusable;

    /// <summary>
    /// 异步初始化 VOIP 系统。
    /// </summary>
    /// <returns>表示异步操作的 <see cref="Task"/>。</returns>
    Task InitializeVOIP();

    /// <summary>
    /// 将网络统计信息输出到日志（仅供内部使用）。
    /// </summary>
    internal void PrintStatistics();

    /// <summary>
    /// 根据 id 返回 <see cref="NetPeer"/>
    /// </summary>
    /// <param name="id">要查找的 id</param>
    /// <returns>如果找到则返回 <see cref="NetPeer"/>；否则返回 <see langword="null"/></returns>
    public NetPeer GetPeerById(int id);

    /// <summary>
    /// 表示 <see cref="IFikaNetworkManager"/> 的发送速率选项。
    /// </summary>
    public enum ESendRate : byte
    {
        /// <summary>
        /// 低发送速率（每秒 10 次）。
        /// </summary>
        Low,
        /// <summary>
        /// 中等发送速率（每秒 20 次）。
        /// </summary>
        Medium,
        /// <summary>
        /// 高发送速率（每秒 30 次）。
        /// </summary>
        High
    }
}
