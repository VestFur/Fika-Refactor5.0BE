using EFT;
using System.Reflection;
using EFT.UI;
using HarmonyLib;
using SPTushonka.Common.Http;
using SPTushonka.Common.Utils;
using SPTushonka.Custom.Models;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 最初由 SPT 团队开发
/// </summary>
public class FikaVersionLabel_Patch : ModulePatch
{
    private static string _versionLabel;
    private static Version _version;
    private static string _officialVersion;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(Version).GetMethod(nameof(Version.Create),
            BindingFlags.Static | BindingFlags.Public);
    }

    [PatchPostfix]
    internal static void PatchPostfix(string major, Version __result)
    {
        FikaPlugin.EFTVersionMajor = major;

        if (string.IsNullOrEmpty(_versionLabel))
        {
            var json = RequestHandler.GetJson("/singleplayer/settings/version");
            _versionLabel = Fika.Core.Networking.Serialization.FikaJson.Deserialize<VersionResponse>(json).Version;
            Logger.LogInfo($"Server version: {_versionLabel}");
        }

        _version = __result;
        _officialVersion = major;

        UpdateVersionLabel();
    }

    public static void UpdateVersionLabel()
    {
        if (_version == null) return;
        var preloader = MonoBehaviourSingleton<PreloaderUI>.Instance;
        string label;
        if (FikaPlugin.Instance.Settings.OfficialVersion != null && FikaPlugin.Instance.Settings.OfficialVersion.Value)
        {
            label = $"{_officialVersion} Beta version";
            _version.Major = _officialVersion;
        }
        else
        {
#if DEBUG
            label = $"FIKA {FikaPlugin.FikaVersion} (DEBUG) | {_versionLabel}";
#else
            label = $"FIKA {FikaPlugin.FikaVersion} | {_versionLabel}";
#endif
            _version.Major = $"{FikaPlugin.FikaVersion} {_versionLabel}";
        }

        // Version.Create 可能在 PreloaderUI 完成 Awake 之前就执行。现在先
        // 保留版本数据；由 Awake 后置补丁应用可见的标签。
        if (preloader == null || preloader._alphaVersionLabel == null) return;
        preloader._alphaVersionLabel.LocalizationKey = "{0}";
        preloader._alphaVersionText = label;
        preloader.RefreshCornerLabel();
    }
}

internal sealed class FikaPreloaderVersionLabel_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PreloaderUI), nameof(PreloaderUI.Awake));

    [PatchPostfix]
    private static void Postfix() => FikaVersionLabel_Patch.UpdateVersionLabel();
}
