#if DEBUG
using Fika.Core.Main.Utils;
#endif
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Fika.Core.Networking.Pooling;

/// <summary>
/// 类型为 <typeparamref name="T"/> 的可复用包池。
/// </summary>
/// <typeparam name="T">包类型，必须是引用类型。</typeparam>
public class PacketPool<T> : IDisposable
    where T : class
{
    /// <summary>
    /// 存储可用包实例的内部栈。
    /// </summary>
    private readonly Stack<T> _pool;

    /// <summary>
    /// 池为空时用于构造新实例的函数。
    /// </summary>
    private readonly Func<T> _constructor;

    /// <summary>
    /// 初始化 <see cref="PacketPool{T}"/> 类的新实例。
    /// 预分配 <paramref name="size"/> 个实例。
    /// </summary>
    /// <param name="size">初始池大小。</param>
    /// <param name="constructor">创建新实例的工厂函数。</param>
    public PacketPool(int size, Func<T> constructor)
    {
        _pool = new Stack<T>(size);
        _constructor = constructor;
        for (var i = 0; i < size; i++)
        {
            _pool.Push(_constructor());
        }
    }

    /// <summary>
    /// 从池中取出一个实例，池为空时创建新实例。
    /// </summary>
    /// <returns>一个可复用的 <typeparamref name="T"/> 实例。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Get()
    {
        if (_pool.TryPop(out var item))
        {
            return item;
        }
#if DEBUG
        var concreteType = _constructor.Method.ReturnType;
        FikaGlobals.LogError($"[{concreteType.Name}] Pool empty. Allocating new instance.");
#endif
        return _constructor();
    }

    /// <summary>
    /// 将实例归还到池中。
    /// </summary>
    /// <param name="item">要归还的实例。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item)
    {
        _pool.Push(item);
    }

    /// <summary>
    /// 清空池并释放引用。
    /// </summary>
    public void Dispose()
    {
        _pool.Clear();
    }
}
