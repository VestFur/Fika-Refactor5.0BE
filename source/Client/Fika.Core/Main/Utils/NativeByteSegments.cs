using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core;

internal static class NativeByteSegments
{
    // 复制载荷切片；传输层的读取端和 Dissonance 都会回收缓冲区。
    public static Il2CppSystem.ArraySegment<byte> ToNative(this ArraySegment<byte> source)
    {
        var buffer = new Il2CppStructArray<byte>(source.Count);
        for (var i = 0; i < source.Count; i++)
            buffer[i] = source.Array[source.Offset + i];
        return new Il2CppSystem.ArraySegment<byte>(buffer, 0, source.Count);
    }

    public static ArraySegment<byte> ToManaged(this Il2CppSystem.ArraySegment<byte> source)
    {
        var buffer = new byte[source.Count];
        var array = source.Array;
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = array[source.Offset + i];
        return new ArraySegment<byte>(buffer);
    }
}
