namespace FikaServer.Models.Fika;

/// <summary>
/// 每个档案的 <c>HeadlessConfig.json</c> 的内容，由服务器写到
/// <c>StartHeadless.cmd</c> 旁边。启动器和 Fika.Headless 客户端插件都会读取
/// 这个文件，因此它始终是专用主机如何启动的唯一事实来源。
/// </summary>
public class HeadlessLaunchSettings
{
    /// <summary>
    /// 本文件的架构版本。在使用窗口状态之前的构建不写版本，
    /// 并硬编码 <c>StartMinimized = false</c>，因此没有版本的文件无法与
    /// 运维者刻意编辑过的文件区分开：它会被当作该属性
    /// 不存在处理，也就是「最小化」。
    /// </summary>
    public const int CurrentSettingsVersion = 2;

    /// <summary>文件在此属性出现之前写入时为零。</summary>
    public int SettingsVersion { get; set; }

    public string? ProfileId { get; set; }
    public Uri? BackendUrl { get; set; }

    /// <summary>
    /// 专用主机是否以最小化窗口启动。主机从不会
    /// 被人注视，因此属性缺失必须意味着「最小化」：该默认值既适用于
    /// 旧服务器构建生成的文件，也适用于新建的档案。只有显式
    /// 的 <c>false</c> 才会恢复窗口。
    /// </summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// 主机如何呈现自己：<c>Console</c>（<c>-batchmode -nographics</c>，无窗口、
    /// 无图形设备、无着色器）或 <c>Hidden</c>（历史上带窗口的主机）。该
    /// 值也会写入 <c>StartHeadless.cmd</c>，Fika.Headless 插件
    /// 会读回它以记录并执行同样的决策。属性缺失时回退到
    /// 服务器的 <c>headless.display.mode</c>；运维者在此处编辑的值优先。
    /// </summary>
    public string? Display { get; set; }
}
