// © 2026 Lacyway All Rights Reserved

using Diz.Utils;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using Fika.Core.Main.Utils;
using HarmonyLib;
using JsonType;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using static EFT.UI.PreloaderUI;

namespace Fika.Core.UI;

/// <summary>
/// 提供全局 UI 工具与辅助方法
/// </summary>
public static class FikaUIGlobals
{
    /// <summary>
    /// 把 <see cref="EColor"/> 值映射到对应十六进制颜色代码的字典。
    /// </summary>
    private static readonly Dictionary<EColor, string> _keyValuePairs = new()
    {
        { EColor.WHITE, "ffffff" },
        { EColor.BLACK, "000000" },
        { EColor.GREEN, "32a852" },
        { EColor.BROWN, "a87332" },
        { EColor.BLUE, "51c6db" },
        { EColor.RED, "a83232" }
    };

    /// <summary>
    /// 在 UI 中创建一个带有指定文本的覆盖层文本元素。
    /// </summary>
    /// <param name="overlayText">要在覆盖层中显示的文本。</param>
    /// <returns>创建出的 <see cref="TextMeshProUGUI"/> 组件。</returns>
    public static TextMeshProUGUI CreateOverlayText(string overlayText, Vector4 margin = default)
    {
        var obj = GameObject.Find("/Preloader UI/Preloader UI/Watermark");
        var labelObj = GameObject.Find("/Preloader UI/Preloader UI/Watermark/Label");

        if (labelObj != null)
        {
            UnityEngine.Object.Destroy(labelObj);
        }

        var watermarkText = obj.GetComponent<ClientWatermark>();
        if (watermarkText != null)
        {
            UnityEngine.Object.Destroy(watermarkText);
        }

        obj.SetActive(true);
        if (!obj.TryGetComponent<TextMeshProUGUI>(out var text))
        {
            text = obj.AddComponent<TextMeshProUGUI>();
        }
        text.horizontalAlignment = HorizontalAlignmentOptions.Center;
        text.verticalAlignment = VerticalAlignmentOptions.Bottom;
        text.margin = margin == default ? new Vector4(0f, 0f, 0f, -350f) : margin;
        text.SetText(overlayText);

        return text;
    }

    /// <summary>
    /// 使用 <see cref="PreloaderUI"/> 错误界面显示一条 Fika 消息。
    /// </summary>
    /// <param name="preloaderUI">预加载 UI 实例。</param>
    /// <param name="header">消息的标题文本。</param>
    /// <param name="message">消息正文文本。</param>
    /// <param name="buttonType">要显示的按钮类型。</param>
    /// <param name="waitingTime">自动关闭消息前等待的时间。</param>
    /// <param name="acceptCallback">消息被接受时调用的回调。</param>
    /// <param name="endTimeCallback">等待时间结束时调用的回调。</param>
    /// <returns>所显示消息的上下文对象。</returns>
    public static ErrorWindowContext ShowFikaMessage(this PreloaderUI preloaderUI, string header, string message,
        ErrorScreen.EButtonType buttonType, float waitingTime, Action acceptCallback, Action endTimeCallback)
    {
        if (!AsyncWorker.CheckIsMainThread())
        {
            FikaGlobals.LogError("You are trying to show error screen from non-main thread!");
            return new ErrorWindowContext();
        }

        var container = preloaderUI._criticalErrorScreenContainer;
        var screen = UnityEngine.Object.Instantiate(preloaderUI._criticalErrorScreenTemplate, container.transform, false);
        container.AddChildNode(screen);
        var context = screen.ShowFikaMessage(header, message, acceptCallback, waitingTime, endTimeCallback, buttonType);
        context.OnClose += (System.Action)(() =>
        {
            container.RemoveChildNode(screen);
            UnityEngine.Object.Destroy(screen.gameObject);
        });
        return context;
    }

    /// <summary>
    /// 使用 <see cref="ErrorScreen"/> 界面显示一条 Fika 消息。
    /// </summary>
    /// <param name="errorScreen">错误界面实例。</param>
    /// <param name="title">错误消息的标题。</param>
    /// <param name="message">消息正文文本。</param>
    /// <param name="closeManuallyCallback">消息被手动关闭时调用的回调。</param>
    /// <param name="waitingTime">自动关闭消息前等待的时间。</param>
    /// <param name="timeOutCallback">等待时间结束时调用的回调。</param>
    /// <param name="buttonType">要显示的按钮类型。</param>
    /// <param name="removeHtml">是否从消息中移除 HTML 标签。</param>
    /// <returns>所显示消息的上下文对象。</returns>
    public static ErrorWindowContext ShowFikaMessage(this ErrorScreen errorScreen, string title, string message,
        Action closeManuallyCallback = null, float waitingTime = 0f, Action timeOutCallback = null,
        ErrorScreen.EButtonType buttonType = ErrorScreen.EButtonType.OkButton, bool removeHtml = true)
    {
        if (!MonoBehaviourSingleton<PreloaderUI>.Instance.CanShowErrorScreen)
        {
            return new ErrorWindowContext();
        }
        ItemUiContext.Instance.CloseAllWindows();
        // Show 拥有原生上下文订阅、超时协程与清理逻辑。
        // 不要再注册一次它的 TimeOut/CloseSilent 处理器。
        var context = errorScreen.Show(title, message, waitingTime, buttonType, removeHtml);
        var completed = false;
        context.OnAccept += (System.Action)(() =>
        {
            if (completed) return;
            completed = true;
            (timeOutCallback ?? closeManuallyCallback)?.Invoke();
        });
        context.OnDecline += (System.Action)(() =>
        {
            completed = true;
            Application.Quit();
        });
        context.OnClose += (System.Action)(() =>
        {
            if (completed) return;
            completed = true;
            closeManuallyCallback?.Invoke();
        });

        var text = buttonType switch
        {
            ErrorScreen.EButtonType.OkButton => "I UNDERSTAND",
            ErrorScreen.EButtonType.CancelButton => "CANCEL",
            ErrorScreen.EButtonType.QuitButton => "I DECLINE",
            _ => throw new ArgumentOutOfRangeException()
        };

        var exitButton = errorScreen._exitButton;
        exitButton.SetHeaderText(text, exitButton.HeaderSize);
        errorScreen.RectTransform.anchoredPosition = Vector2.zero;

        return context;
    }

    /// <summary>
    /// 获取指定 <see cref="EColor"/> 的十六进制颜色代码。
    /// </summary>
    /// <param name="color">颜色枚举值。</param>
    /// <returns>以字符串形式返回的十六进制颜色代码。</returns>
    private static string GetHexByColor(EColor color)
    {
        return _keyValuePairs.TryGetValue(color, out var value) ? value : "ffffff";
    }

    /// <summary>
    /// 用于给 <see cref="CustomTextMeshProUGUI"/> 中的文本上色的工具方法。
    /// </summary>
    /// <param name="color">返回文本时使用的颜色。</param>
    /// <param name="text">原始文本。</param>
    /// <returns>带颜色的文本。</returns>
    public static string ColorizeText(EColor color, string text)
    {
        return $"<color=#{GetHexByColor(color)}>{text}</color>";
    }

    /// <summary>
    /// 用于把 <see cref="CustomTextMeshProUGUI"/> 中的文本加粗的工具方法。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>加粗后的文本。</returns>
    public static string BoldText(string text)
    {
        return $"<b>{text}</b>";
    }

    /// <summary>
    /// 获取一个供某些 UI 显示使用的静态日期时间值。
    /// </summary>
    public static DateTime StaticTime
    {
        get
        {
            return new DateTime(2016, 8, 4, 15, 28, 0);
        }
    }

    /// <summary>
    /// 根据指定的 <see cref="EDateTime"/> 与静态时间标志，返回格式化后的时间字符串。
    /// </summary>
    /// <param name="time">要格式化的时间类型（当前或上一次）。</param>
    /// <param name="staticTime">是否使用静态时间而不是后端时间。</param>
    /// <returns>格式化后的时间字符串；若不可用则为空字符串。</returns>
    public static string FormattedTime(EDateTime time, bool staticTime)
    {
        if (TarkovApplication.Exist(out var tarkovApplication))
        {
            if (tarkovApplication.Session != null)
            {
                var session = tarkovApplication.Session;

                if (staticTime)
                {
                    var staticDate = StaticTime;
                    return time == EDateTime.CURR ? staticDate.ToString("HH:mm:ss") : staticDate.AddHours(-12).ToString("HH:mm:ss");
                }

                var backendTime = session.GetCurrentLocationTime.ToManaged();
                if (backendTime == DateTime.MinValue)
                {
                    return "";
                }
                return time == EDateTime.CURR ? backendTime.ToString("HH:mm:ss") : backendTime.AddHours(-12).ToString("HH:mm:ss");
            }
        }
        return "";
    }

    /// <summary>
    /// 表示一名 Fika 玩家的在场状态。
    /// </summary>
    public enum EFikaPlayerPresence
    {
        /// <summary>
        /// 玩家位于菜单中。
        /// </summary>
        IN_MENU,
        /// <summary>
        /// 玩家位于战局中。
        /// </summary>
        IN_RAID,
        /// <summary>
        /// 玩家位于仓库中。
        /// </summary>
        IN_STASH,
        /// <summary>
        /// 玩家位于藏身处中。
        /// </summary>
        IN_HIDEOUT,
        /// <summary>
        /// 玩家位于跳蚤市场中。
        /// </summary>
        IN_FLEA
    }

    /// <summary>
    /// 供 <see cref="ColorizeText"/> 指定文本颜色使用的枚举。
    /// </summary>
    public enum EColor
    {
        /// <summary>
        /// 白色。
        /// </summary>
        WHITE,
        /// <summary>
        /// 黑色。
        /// </summary>
        BLACK,
        /// <summary>
        /// 绿色。
        /// </summary>
        GREEN,
        /// <summary>
        /// 棕色。
        /// </summary>
        BROWN,
        /// <summary>
        /// 蓝色。
        /// </summary>
        BLUE,
        /// <summary>
        /// 红色。
        /// </summary>
        RED
    }
}
