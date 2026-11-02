#if DEBUG
using Fika.Core.Main.Utils;
#endif
using System;
using System.Runtime.CompilerServices;

namespace Fika.Core.Networking.Pooling;

/// <summary>
/// 管理 <typeparamref name="TType"/> 类型的数据包对象池，并按 <typeparamref name="TEnum"/> 类型的键分类。
/// 提供创建、清空、取出与归还数据包的功能。
/// </summary>
/// <typeparam name="TEnum">用作标识数据包类型的键的枚举类型。</typeparam>
/// <typeparam name="TType">实现 <see cref="IDisposable"/> 的数据包类型。</typeparam>
public abstract class BasePacketPoolManager<TEnum, TType>
    where TEnum : Enum
    where TType : class, IDisposable
{
    /// <summary>
    /// 包含每种数据包类型构造方法的数组
    /// </summary>
    protected Func<TType>[] _subPacketFactories;

    private bool _poolExists;

    private PacketPool<TType>[] _pool;

    /// <summary>
    /// 基于已注册的工厂函数，为每种数据包类型创建一个数据包池。
    /// 各池以初始容量 1 初始化。
    /// </summary>
    public void CreatePool()
    {
        if (_pool == null)
        {
            _pool = new PacketPool<TType>[_subPacketFactories.Length];
            for (var i = 0; i < _subPacketFactories.Length; i++)
            {
                _pool[i] = new(1, _subPacketFactories[i]);
            }
        }

        _poolExists = true;
    }

    /// <summary>
    /// 清空并释放所有数据包池，释放其持有的任何资源。
    /// </summary>
    public void ClearPool()
    {
        if (_pool != null)
        {
            for (var i = 0; i < _pool.Length; i++)
            {
                _pool[i].Dispose();
            }
        }

        _pool = null;
        _poolExists = false;
    }

    /// <summary>
    /// 从池中取出指定类型的数据包。
    /// 若该池尚未创建，则会先初始化它。
    /// </summary>
    /// <typeparam name="T">要取出的具体数据包类型，必须派生自 <typeparamref name="TType"/>。</typeparam>
    /// <param name="type">在池中标识该数据包类型的键。</param>
    /// <returns>来自池的一个数据包实例。</returns>
    /// <exception cref="ArgumentException">当指定类型在该池中不存在时抛出。</exception>
    public T GetPacket<T>(TEnum type) where T : TType
    {
        if (!_poolExists)
        {
#if DEBUG
            FikaGlobals.LogError($"Pool did not exist when retrieving [{type.GetType().Name}]");
#endif
            CreatePool();
        }

        var pool = WithdrawPacket(type);
        if (pool == null)
        {
            // 本构建不认识的类型（见 WithdrawPacket）：调用方跳过该包。
            return default;
        }

        return (T)pool.Get();
    }

    /// <summary>
    /// 把数据包归还到池中并释放它。
    /// </summary>
    /// <param name="type">在池中标识该数据包类型的键。</param>
    /// <param name="packet">要归还到池中的数据包实例。</param>
    /// <remarks>若该池尚未创建则什么都不做</remarks>
    public void ReturnPacket(TEnum type, TType packet)
    {
        if (!_poolExists)
        {
#if DEBUG
            FikaGlobals.LogError($"Pool did not exist when returning [{type.GetType().Name}] - [{packet.GetType().Name}]");
#endif
            return;
        }

        packet.Dispose();

        // (2026-10-26 W23) WithdrawPacket 现在对本构建不认识的类型返回 default（W22）；旧的
        // 一行写法随后会在这里解引用 null。该包已被释放，因此丢弃它就是
        // 「归还」一个未知包的完整工作。
        var pool = WithdrawPacket(type);
        if (pool == null)
        {
            return;
        }

        pool.Return(packet);
    }

    /// <summary>
    /// 获取与指定类型关联的 <see cref="PacketPool{TType}"/>。
    /// </summary>
    /// <param name="type">标识该数据包类型的键。</param>
    /// <returns>对应的 <see cref="PacketPool{TType}"/> 实例。</returns>
    /// <exception cref="ArgumentException">当指定类型在该池中不存在时抛出。</exception>
    private PacketPool<TType> WithdrawPacket(TEnum type)
    {
        // (2026-10-26 W22) 由不同包构建的对端可能发来本构建不认识的子包类型；
        // 数组索引随后会在网络轮询循环内部抛出 IndexOutOfRange。改为上报它，
        // 并让调用方跳过该包 —— 一个未知类型不得拖垮整个网络循环。
        var index = ToInt(type);
        if ((System.UInt32)index >= (System.UInt32)_pool.Length)
        {
            global::Fika.Core.Main.Utils.FikaGlobals.LogError($"packet pool has no entry for {typeof(TEnum).Name}={index} " +
                $"(this build has {_pool.Length}); the peer runs a different build");
            return null;
        }
#if DEBUG
        return _pool[ToInt(type)]
            ?? throw new ArgumentException("Could not find given type in the packet pool manager!", nameof(type));
#else
        return _pool[ToInt(type)];
#endif

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToInt(TEnum value)
    {
#if DEBUG
        if (Unsafe.SizeOf<TEnum>() != 1)
        {
            throw new InvalidOperationException($"{value.GetType().Name} must be backed by byte for this method.");
        }
#endif
        return Unsafe.As<TEnum, byte>(ref value);
    }
}