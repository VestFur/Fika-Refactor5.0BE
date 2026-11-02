using System.Globalization;
using TMPro;

namespace Fika.Core;

internal static class FikaTextExtensions
{
    // 使用数值型的
    // SetText 重载时，旧 bundle 里的 TMP 标签可能仍保留它们序列化时的文本。请通过可正常工作的
    // 文本输入路径设置完整字符串，对仍处于非激活状态的标签也一样。
    internal static void SetFormattedText(this TMP_Text label, string format, params object[] values) =>
        label.SetText(string.Format(CultureInfo.InvariantCulture, format, values));
}
