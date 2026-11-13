namespace FikaServer.Models.Fika;

/// <summary>
/// Contents of the per-profile <c>HeadlessConfig.json</c> the server writes next to
/// <c>StartHeadless.cmd</c>. Both the launcher and the Fika.Headless client plugin read
/// this file, so it stays the single source of truth for how a dedicated host starts.
/// </summary>
public class HeadlessLaunchSettings
{
    /// <summary>
    /// Schema version of this file. Builds before the window state was consumed wrote no version
    /// and hardcoded <c>StartMinimized = false</c>, so a file without the version cannot be told
    /// apart from one an operator deliberately edited: it is treated as if the property were
    /// absent, which means "minimised".
    /// </summary>
    public const int CurrentSettingsVersion = 2;

    /// <summary>Zero when the file was written before this property existed.</summary>
    public int SettingsVersion { get; set; }

    public string? ProfileId { get; set; }
    public Uri? BackendUrl { get; set; }

    /// <summary>
    /// Whether the dedicated host comes up with a minimised window. A host is never
    /// watched, so an absent property must mean "minimised": this default applies to a
    /// file from an older server build as well as to a newly created profile. Only an
    /// explicit <c>false</c> restores the window.
    /// </summary>
    public bool StartMinimized { get; set; } = true;
}
