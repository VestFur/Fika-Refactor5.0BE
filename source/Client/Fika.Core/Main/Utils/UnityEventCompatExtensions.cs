using System;
using UnityEngine.Events;

namespace Fika.Core;

/// <summary>
/// Il2CppInterop 把 UnityAction 生成为一个托管代理类型，因此 C# 方法
/// 组和 lambda 无法直接绑定到 UnityEvent.AddListener。这些
/// 重载在保持调用点地道写法的同时，在一处边界上
/// 完成原生委托转换。
internal static class UnityEventCompatExtensions
{
    public static void AddListener(this UnityEvent evt, Action listener)
    {
        UnityAction action = listener;
        evt.AddListener(action);
    }

    /// <summary>
    /// <see cref="AddListener(UnityEvent, Action)"/> 的对应物。
    ///
    /// 转换按托管委托缓存（Il2CppInterop 维护一个以 CLR 委托为键的
    /// ConditionalWeakTable），所以把*同一个*委托实例再转换一次会
    /// 得到同一个原生监听器，移除操作才能真正找到它。
    /// 传入一个新的方法组则会创建第二个不同的监听器，并让
    /// 第一个继续注册着——原生事件最终持有一个其目标已被托管侧
    /// 丢弃的委托，就是这样发生的。
    /// </summary>
    public static void RemoveListener(this UnityEvent evt, Action listener)
    {
        UnityAction action = listener;
        evt.RemoveListener(action);
    }

    public static void AddListener<T>(this UnityEvent<T> evt, Action<T> listener)
    {
        UnityAction<T> action = listener;
        evt.AddListener(action);
    }

    public static void AddListener<T0, T1>(this UnityEvent<T0, T1> evt, Action<T0, T1> listener)
    {
        UnityAction<T0, T1> action = listener;
        evt.AddListener(action);
    }

    public static void AddListener<T0, T1, T2>(this UnityEvent<T0, T1, T2> evt, Action<T0, T1, T2> listener)
    {
        UnityAction<T0, T1, T2> action = listener;
        evt.AddListener(action);
    }

    public static void AddListener<T0, T1, T2, T3>(this UnityEvent<T0, T1, T2, T3> evt, Action<T0, T1, T2, T3> listener)
    {
        UnityAction<T0, T1, T2, T3> action = listener;
        evt.AddListener(action);
    }
}
