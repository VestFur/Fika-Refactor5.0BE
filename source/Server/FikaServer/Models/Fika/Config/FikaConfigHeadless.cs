using System.Text.Json.Serialization;

namespace FikaServer.Models.Fika.Config;

public record FikaConfigHeadless
{
    [JsonPropertyName("profiles")]
    public FikaConfigHeadlessProfiles Profiles { get; set; } = new();

    [JsonPropertyName("scripts")]
    public FikaConfigHeadlessScripts Scripts { get; set; } = new();

    [JsonPropertyName("display")]
    public FikaConfigHeadlessDisplay Display { get; set; } = new();

    [JsonPropertyName("setLevelToAverageOfLobby")]
    public bool SetLevelToAverageOfLobby { get; set; } = true;

    [JsonPropertyName("restartAfterAmountOfRaids")]
    public int RestartAfterAmountOfRaids { get; set; } = 1;
}

public record FikaConfigHeadlessProfiles
{
    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; set; } = [];
}

public record FikaConfigHeadlessScripts
{
    [JsonPropertyName("generate")]
    public bool Generate { get; set; } = true;

    [JsonPropertyName("forceIp")]
    public string ForceIp { get; set; } = "https://127.0.0.1:6969";
}

/// <summary>
/// 专用主机在其运行的机器上如何呈现自己。该值会被渲染进
/// <c>StartHeadless.cmd</c> 并写入 <c>HeadlessConfig.json</c>，因此启动器和
/// Fika.Headless 插件始终一致。
/// </summary>
public record FikaConfigHeadlessDisplay
{
    /// <summary>
    /// <c>Console</c>：以 <c>-batchmode -nographics</c> 启动主机——无游戏窗口、
    /// 无图形设备，因此不编译着色器、不提交绘制调用；只剩下控制台
    /// 和日志。<c>Hidden</c>：历史上带窗口的主机，以最小化／隐藏方式启动，
    /// 图形补丁仍然会拆掉每一个渲染器。
    /// <para>
    /// 缺失（<c>null</c>）表示「此处未配置」：
    /// 其 <c>HeadlessConfig.json</c> 已指明模式的档案保持该模式，其他所有档案使用
    /// <c>Console</c>。该键存在时对所有档案生效，因此服务器在启动时
    /// 重写的启动辅助脚本始终与本文件一致。
    /// </para>
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }
}

/// <summary>可接受的 <see cref="FikaConfigHeadlessDisplay.Mode"/> 值。</summary>
public static class HeadlessDisplayModes
{
    /// <summary>无客户端画面、无着色器、无 GPU：<c>-batchmode -nographics</c>。</summary>
    public const string Console = "Console";

    /// <summary>历史主机：使用真实设备，窗口隐藏／最小化。</summary>
    public const string Hidden = "Hidden";

    /// <summary>当 <paramref name="value"/> 未指明模式时为 null。</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.Equals(value, Console, StringComparison.OrdinalIgnoreCase))
        {
            return Console;
        }

        if (string.Equals(value, Hidden, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Windowed", StringComparison.OrdinalIgnoreCase))
        {
            return Hidden;
        }

        return null;
    }
}
