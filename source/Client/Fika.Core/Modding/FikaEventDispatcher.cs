using System;
using System.Collections.Generic;
using Fika.Core.Modding.Events;

namespace Fika.Core.Modding;

/// <summary>
/// 为 Fika 事件提供静态事件分发器，支持事件的订阅与分发。
/// </summary>
public static class FikaEventDispatcher
{
    /// <summary>
    /// 表示 Fika 事件的处理程序。
    /// </summary>
    /// <param name="e">事件实例。</param>
    public delegate void FikaEventHandler(FikaEvent e);

    /// <summary>
    /// 在分发任何 Fika 事件时发生。
    /// </summary>
    /// <remarks>建议消费方改为订阅具体事件。</remarks>
    public static event FikaEventHandler OnFikaEvent;

    /// <summary>
    /// 将 (Callback, EventType) 复合键映射到生成的包装委托，用于匹配取消订阅。
    /// </summary>
    private static readonly Dictionary<(Delegate Callback, Type EventType), FikaEventHandler> _delegateMap = [];

    /// <summary>
    /// 将 Fika 事件分发给所有已注册的处理程序。
    /// </summary>
    /// <typeparam name="TEvent">事件的类型，派生自 <see cref="FikaEvent"/>。</typeparam>
    /// <param name="e">要分发的事件实例。</param>
    public static void DispatchEvent<TEvent>(TEvent e) where TEvent : FikaEvent
    {
        OnFikaEvent?.Invoke(e);
    }

    /// <summary>
    /// 将回调订阅到特定类型的 Fika 事件。
    /// </summary>
    /// <typeparam name="TEvent">要订阅的事件类型。</typeparam>
    /// <param name="callback">事件分发时要调用的回调。</param>
    public static void SubscribeEvent<TEvent>(Action<TEvent> callback) where TEvent : FikaEvent
    {
        if (callback == null)
        {
            return;
        }

        void wrapper(FikaEvent e)
        {
            if (e is TEvent specificEvent)
            {
                callback(specificEvent);
            }
        }

        _delegateMap[(callback, typeof(TEvent))] = wrapper;
        OnFikaEvent += wrapper;
    }

    /// <summary>
    /// 从特定类型的 Fika 事件取消订阅回调。
    /// </summary>
    /// <typeparam name="TEvent">要取消订阅的事件类型。</typeparam>
    /// <param name="callback">要从事件订阅中移除的回调。</param>
    public static void UnsubscribeEvent<TEvent>(Action<TEvent> callback) where TEvent : FikaEvent
    {
        if (callback == null)
        {
            return;
        }

        var key = (callback, typeof(TEvent));
        if (_delegateMap.TryGetValue(key, out var wrapper))
        {
            OnFikaEvent -= wrapper;
            _delegateMap.Remove(key);
        }
    }
}