using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Fika.Core.Networking.Pooling;

/// <summary>
/// 提供用于租用与归还池化 <see cref="ArraySegment{Byte}"/> 实例的方法，
/// 这些实例由共享的 <see cref="ArrayPool{Byte}"/> 中的数组支撑。
/// </summary>
internal static class ArraySegmentPooling
{
    /// <summary>
    /// 从共享池中租用一个具有指定最小长度的字节数组，
    /// 并以长度为零的 <see cref="ArraySegment{Byte}"/> 形式返回。
    /// </summary>
    /// <param name="bufferCount">要租用的数组的最小长度。</param>
    /// <returns>
    /// 一个包装所租用数组且长度为零的 <see cref="ArraySegment{Byte}"/>。
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArraySegment<byte> Get(int bufferCount)
    {
        return new(ArrayPool<byte>.Shared.Rent(bufferCount), 0, 0);
    }

    /// <summary>
    /// 将指定缓冲区复制到租用的数组中，并以 <see cref="ArraySegment{Byte}"/> 形式返回。
    /// </summary>
    /// <param name="buffer">要复制的源缓冲区。</param>
    /// <returns>
    /// 一个包装租用数组的 <see cref="ArraySegment{Byte}"/>，其中包含已复制的数据。
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArraySegment<byte> Get(byte[] buffer)
    {
        return Get(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// 将指定 <see cref="ArraySegment{Byte}"/> 的内容复制到租用的数组中，
    /// 并以新的 <see cref="ArraySegment{Byte}"/> 形式返回。
    /// </summary>
    /// <param name="buffer">要复制的源数组段。</param>
    /// <returns>
    /// 一个包装租用数组的 <see cref="ArraySegment{Byte}"/>，其中包含已复制的数据。
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArraySegment<byte> Get(ArraySegment<byte> buffer)
    {
        return Get(buffer.Array, buffer.Offset, buffer.Count);
    }

    /// <summary>
    /// 将指定 <see cref="ReadOnlySpan{Byte}"/> 的内容复制到租用的数组中，
    /// 并以新的 <see cref="ArraySegment{Byte}"/> 形式返回。
    /// </summary>
    /// <param name="source">要复制的源 span。</param>
    /// <returns>
    /// 一个包装租用数组的 <see cref="ArraySegment{Byte}"/>，其中包含已复制的数据。
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArraySegment<byte> Get(ReadOnlySpan<byte> source)
    {
        var array = ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(array);
        return new(array, 0, source.Length);
    }

    /// <summary>
    /// 将指定缓冲区的一部分复制到租用的数组中，并以 <see cref="ArraySegment{Byte}"/> 形式返回。
    /// </summary>
    /// <param name="buffer">要复制的源缓冲区。</param>
    /// <param name="bufferOffset">源缓冲区中从零开始的字节偏移量。</param>
    /// <param name="bufferCount">要复制的字节数。</param>
    /// <returns>
    /// 一个包装租用数组的 <see cref="ArraySegment{Byte}"/>，其中包含已复制的数据。
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArraySegment<byte> Get(byte[] buffer, int bufferOffset, int bufferCount)
    {
        var array = ArrayPool<byte>.Shared.Rent(bufferCount);
        Buffer.BlockCopy(buffer, bufferOffset, array, 0, bufferCount);
        return new(array, 0, bufferCount);
    }

    /// <summary>
    /// 将租用的数组归还到共享池。
    /// </summary>
    /// <param name="buffer">其底层数组应被归还的 <see cref="ArraySegment{Byte}"/>。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(ArraySegment<byte> buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer.Array, false);
    }
}