using Mirror;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Fika.Core.Networking.Pooling;

/// <summary>
/// 为 <see cref="NetworkWriter"/> 实例提供静态池管理器，以优化对象复用并减少分配。
/// </summary>
[Obsolete("Use EFTSerializationExtensions instead", true)]
public static class WriterPoolManager
{
    /// <summary>
    /// 初始化 <see cref="WriterPoolManager"/> 类，并预先把一个 <see cref="NetworkWriter"/> 实例放入池中。
    /// </summary>
    static WriterPoolManager()
    {
        _writers.Push(new());
    }

    /// <summary>
    /// <see cref="NetworkWriter"/> 对象的基于栈的池。
    /// </summary>
    private static readonly Stack<NetworkWriter> _writers = new(1);

    /// <summary>
    /// 把一个 <see cref="NetworkWriter"/> 实例归还到池中以便复用。
    /// </summary>
    /// <param name="writer">要归还到池中的 <see cref="NetworkWriter"/> 实例。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Obsolete("Use EFTSerializationExtensions instead", true)]
    public static void ReturnWriter(NetworkWriter writer)
    {
        _writers.Push(writer);
    }

    /// <summary>
    /// 从池中取出一个 <see cref="NetworkWriter"/> 实例。若池为空，则会创建一个新实例。
    /// 返回的 writer 在交付之前会被重置。
    /// </summary>
    /// <returns>可供使用的 <see cref="NetworkWriter"/> 实例。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Obsolete("Use EFTSerializationExtensions instead", true)]
    public static NetworkWriter GetWriter()
    {
        if (_writers.Count == 0)
        {
            return new();
        }

        var writer = _writers.Pop();
        writer.Reset();
        return writer;
    }
}
