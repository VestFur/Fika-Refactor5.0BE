using Il2CppSystem.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

public sealed class OfflineRaidSettingsMenuPatch_Override : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(RaidSettingsWindow),
            nameof(RaidSettingsWindow.Show));
    }

    private static RaidSettingsWindow _instance;
    private static List<CanvasGroup> _weatherCanvasGroups;

    [PatchPostfix]
    private static void PatchPostfix(RaidSettingsWindow __instance, RaidSettings raidSettings)
    {
        FikaBackendUtils.CustomRaidSettings.UseCustomWeather = false;
        // 总是禁用 Coop Mode 复选框
        __instance._coopModeBlocker.SetBlock(true, LocaleUtils.UI_FIKA_ALWAYS_COOP.Localized());

        var captionText = __instance.gameObject.transform.GetChild(0).GetChild(1).GetComponent<LocalizedText>();
        if (captionText != null)
        {
            captionText.SetLabelText(LocaleUtils.UI_COOP_RAID_SETTINGS.Localized());
        }

        // 重置这一项，否则它的状态会保留下来
        var timeAndWeather = raidSettings.TimeAndWeatherSettings;
        timeAndWeather.HourOfDay = -1;
        timeAndWeather.TimeFlowType = ETimeFlowType.x1;
        raidSettings.TimeAndWeatherSettings = timeAndWeather;

        if (__instance._weatherCanvasGroups != null)
        {
            _weatherCanvasGroups = __instance._weatherCanvasGroups;
        }

        foreach (var canvasGroup in __instance._waterAndFoodCanvasGroups)
        {
            canvasGroup.SetUnlockStatus(true, true);
        }

        foreach (var canvasGroup in __instance._playersSpawnPlaceCanvasGroups)
        {
            canvasGroup.SetUnlockStatus(true, true);
        }

        foreach (var canvasGroup in __instance._timeCanvasGroups)
        {
            canvasGroup.SetUnlockStatus(true, true);
        }

        var overloadCheckmark = GameObject.Find("DIsableOverloadCheckmark");
        if (overloadCheckmark != null)
        {
            overloadCheckmark.GetComponent<CanvasGroup>().SetUnlockStatus(true, true);
            overloadCheckmark.transform.GetChild(2).gameObject.SetActive(false); // tooltip
            overloadCheckmark.GetComponent<UpdatableToggle>().Bind((System.Action<bool>)(value => FikaBackendUtils.CustomRaidSettings.DisableOverload = value));
        }

        var legsStaminaCheckmark = GameObject.Find("DisableLegsStaminaCheckmark");
        if (legsStaminaCheckmark != null)
        {
            legsStaminaCheckmark.GetComponent<CanvasGroup>().SetUnlockStatus(true, true);
            legsStaminaCheckmark.transform.GetChild(2).gameObject.SetActive(false); // tooltip
            legsStaminaCheckmark.GetComponent<UpdatableToggle>().Bind((System.Action<bool>)(value => FikaBackendUtils.CustomRaidSettings.DisableLegStamina = value));
        }

        var armsStaminaCheckmark = GameObject.Find("DisableArmsStaminaCheckmark");
        if (armsStaminaCheckmark != null)
        {
            armsStaminaCheckmark.GetComponent<CanvasGroup>().SetUnlockStatus(true, true);
            armsStaminaCheckmark.transform.GetChild(2).gameObject.SetActive(false); // tooltip
            armsStaminaCheckmark.GetComponent<UpdatableToggle>().Bind((System.Action<bool>)(value => FikaBackendUtils.CustomRaidSettings.DisableArmStamina = value));
        }

        // 移除多余的设置项，并加上我们自己的 "Random" 让该设置更明确，同时把索引 0 重命名为 "Together"
        var labelList = __instance._playersSpawnPlaceDropdown._values;
        labelList.Clear();
        labelList.Add(new()
        {
            Label = "Together",
            Enabled = true
        });
        labelList.Add(new()
        {
            Label = "Random",
            Enabled = true
        });
        __instance._playersSpawnPlaceDropdown.SetTextInternal("Together");

        _instance = __instance;

        var timeFlowDropDown = GameObject.Find("TimeFlowDropdown");
        if (timeFlowDropDown != null)
        {
            var tooltip = timeFlowDropDown.transform.parent.GetChild(1).gameObject;
            tooltip.SetActive(false);

            var timeFlowCanvasGroup = timeFlowDropDown.GetComponent<CanvasGroup>();
            if (timeFlowCanvasGroup != null)
            {
                timeFlowCanvasGroup.SetUnlockStatus(true, true);
            }
            __instance._timeFlowDropdown.Interactable = true;

            var timeFlowText = GameObject.Find("TimeFlowText");
            if (timeFlowText != null)
            {
                var group = timeFlowText.GetComponent<CanvasGroup>();
                if (group != null)
                {
                    group.SetUnlockStatus(true, true);
                }
            }
        }

        // 若由服务器强制，则默认即为 true
        if (!raidSettings.TimeAndWeatherSettings.IsRandomWeather)
        {
            __instance._randomWeatherToggle.Bind((System.Action<bool>)ToggleWeather);
            __instance._randomTimeToggle.gameObject.GetComponent<CanvasGroup>().SetUnlockStatus(false, false);

            var weatherToggle = GameObject.Find("RandomWeatherCheckmark");
            if (weatherToggle != null)
            {
                var customTmp = weatherToggle.GetComponentInChildren<CustomTextMeshProUGUI>();
                if (customTmp != null)
                {
                    customTmp.SetText("Use custom weather");
                }
            }
        }
    }

    private static void ToggleWeather(bool enabled)
    {
        if (_instance == null)
        {
            return;
        }

        if (_weatherCanvasGroups == null)
        {
            return;
        }

        foreach (var item in _weatherCanvasGroups)
        {
            item.SetUnlockStatus(enabled, enabled);
        }

        FikaBackendUtils.CustomRaidSettings.UseCustomWeather = enabled;
        _instance.UpdateRaidSettings();
    }
}
