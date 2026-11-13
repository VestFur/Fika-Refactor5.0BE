#nullable enable

namespace FikaServer.Services.Headless;

internal static class HeadlessReservationToken
{
    // Headless 房间创建必须证明自己拥有对应的 reservation。
    // 缺少 token 时，无法区分延迟创建与更早的一次战局。
    public static bool Matches(string? expected, string? received) =>
        !string.IsNullOrEmpty(expected) &&
        !string.IsNullOrEmpty(received) &&
        string.Equals(expected, received, StringComparison.Ordinal);
}
