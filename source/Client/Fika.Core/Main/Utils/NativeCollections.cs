using System;
using System.Collections.Generic;

namespace Fika.Core;

public static class NativeCollections
{
    // 遍历 native 集合时不复制，也不改变其元素类型。
    public static IEnumerable<T> AsManaged<T>(this Il2CppSystem.Collections.Generic.IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var iterator = source.GetEnumerator();
        try
        {
            while (iterator.MoveNext())
                yield return iterator.Current;
        }
        finally
        {
            iterator.Dispose();
        }
    }

    public static Il2CppSystem.Collections.Generic.List<T> ToNativeList<T>(this IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new Il2CppSystem.Collections.Generic.List<T>();
        foreach (var item in source)
            result.Add(item);
        return result;
    }

    public static Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue> ToNativeDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>();
        foreach (var item in source)
            result.Add(item.Key, item.Value);
        return result;
    }
}
