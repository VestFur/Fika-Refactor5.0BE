using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using BepInEx.Unity.IL2CPP;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 提供对 UI Fixes 多选功能的访问
/// </summary>
internal static class MultiSelect
{
    private static readonly Version _requiredVersion = new(2, 5);
    private static readonly Item[] _emptyArray = [];

    private static bool? _uIFixesLoaded;

    private static Type _multiSelectType;
    private static MethodInfo _getCountMethod;
    private static MethodInfo _getItemsMethod;
    private static MethodInfo _applyMethod;

    /// <summary>
    /// 当前选择中的物品数量，UI Fixes 不存在时为 0。
    /// </summary>
    public static int Count
    {
        get
        {
            if (!Loaded)
            {
                return 0;
            }

            return (int)_getCountMethod.Invoke(null, null);
        }
    }

    /// <summary>
    /// 当前选择中物品的可枚举列表，UI Fixes 不存在时为空
    /// </summary>
    public static IEnumerable<Item> Items
    {
        get
        {
            if (!Loaded)
            {
                return _emptyArray;
            }

            return (IEnumerable<Item>)_getItemsMethod.Invoke(null, null);
        }
    }

    /// <summary>
    /// 该方法接收一个 <c>Action</c>，并按顺序在当前选择中的每个物品上调用它。
    /// UI Fixes 不存在时不做任何操作。
    /// </summary>
    /// <param name="action">要在每个物品上调用的操作。</param>
    /// <param name="itemUiContext">可选的 <c>ItemUiContext</c>；未提供时使用 <c>ItemUiContext.Instance</c>。</param>
    public static void Apply(Action<Item> action, ItemUiContext itemUiContext = null)
    {
        if (!Loaded)
        {
            return;
        }

        Func<Item, Task> func = item =>
        {
            action(item);
            return Task.CompletedTask;
        };

        _applyMethod.Invoke(null, [func, itemUiContext]);
    }

    /// <summary>
    /// 该方法接收一个返回 <c>Task</c> 的 <c>Func</c>，并按顺序在当前选择中的每个物品上调用它。
    /// UI Fixes 不存在时立即返回一个已完成的任务。
    /// </summary>
    /// <param name="func">要在每个物品上调用的函数</param>
    /// <param name="itemUiContext">可选的 <c>ItemUiContext</c>；未提供时使用 <c>ItemUiContext.Instance</c>。</param>
    /// <returns>在所有函数调用完成后完成的 <c>Task</c>。</returns>
    public static Task Apply(Func<Item, Task> func, ItemUiContext itemUiContext = null)
    {
        if (!Loaded)
        {
            return Task.CompletedTask;
        }

        return (Task)_applyMethod.Invoke(null, [func, itemUiContext]);
    }

    private static bool Loaded
    {
        get
        {
            if (!_uIFixesLoaded.HasValue)
            {
                var present = IL2CPPChainloader.Instance.Plugins.TryGetValue("com.tyfon.uifixes", out var pluginInfo);
                _uIFixesLoaded = present && pluginInfo.Metadata.Version.ToString().CompareTo(_requiredVersion.ToString()) >= 0;

                if (_uIFixesLoaded.Value)
                {
                    _multiSelectType = Type.GetType("UIFixes.MultiSelectController, Tyfon.UIFixes");
                    if (_multiSelectType != null)
                    {
                        _getCountMethod = AccessTools.Method(_multiSelectType, "GetCount");
                        _getItemsMethod = AccessTools.Method(_multiSelectType, "GetItems");
                        _applyMethod = AccessTools.Method(_multiSelectType, "Apply");
                    }

                    // 插件列表中存在、但控制器被重命名的版本
                    // 不可用：下面的每个访问器都会在未做 null 检查的情况下解引用这些 MethodInfo，
                    // 因此未解析的成员必须在这里关闭该功能，
                    // 而不是在第一个多选操作时抛异常。
                    if (_multiSelectType == null || _getCountMethod == null || _getItemsMethod == null || _applyMethod == null)
                    {
                        _uIFixesLoaded = false;
                    }
                }
            }

            return _uIFixesLoaded.Value;
        }
    }
}
