using EFT.UI;

namespace Fika.Core.UI;

internal static class FikaInputEnablers
{
    // UIEventSystem 使用标志位掩码。各自独立的位可防止 Fika 窗口
    // 互相禁用，或禁用控制台 / 游戏的记事本窗口。
    public const UIEventSystem.EInputEnabler Chat = (UIEventSystem.EInputEnabler)(1 << 16);
    public const UIEventSystem.EInputEnabler RaidAdmin = (UIEventSystem.EInputEnabler)(1 << 17);
}
