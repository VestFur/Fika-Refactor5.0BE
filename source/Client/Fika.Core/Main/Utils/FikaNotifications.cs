using EFT.Communications;

namespace Fika.Core;

internal static class FikaNotifications
{
    // IL2CPP 生成的包装会拆箱 Nullable<Color>。C# 的可选 null
    // 是一个 null 包装对象，而不是 HasValue == false 的原生值类型。
    public static void DisplayMessageNotification(string message,
        ENotificationDurationType duration = ENotificationDurationType.Default,
        ENotificationIconType iconType = ENotificationIconType.Default,
        Il2CppSystem.Nullable<Color> textColor = null, bool rawText = true)
    {
        NotificationManager.DisplayMessageNotification(message, duration, iconType,
            textColor ?? new Il2CppSystem.Nullable<Color>(), rawText);
    }
}
