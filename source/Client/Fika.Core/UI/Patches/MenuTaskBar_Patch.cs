using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using SPTushonka.Reflection.Patching;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Patches;

public class MenuTaskBar_Patch : ModulePatch
{
    private static DateTime _lastDownload = DateTime.Now.AddMinutes(-2);

    // 下面的 toggle 与 tooltip 会被交给运行中的 MenuTaskBar，它会在每次界面切换时
    // 调用它们。之所以保留它们，是为了让下一个主菜单能够重新核对
    // 当初注册的那些确切指针（引擎持有的引用正是本类中的
    // 防护无法看见的东西）。
    private static AnimatedToggle _registeredToggle;
    private static HoverTooltipArea _registeredTooltip;
    private static bool _disabledReported;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(MenuTaskBar).GetMethod(nameof(MenuTaskBar.Awake));
    }

    [PatchPostfix]
    public static void Postfix(EFT.UI.MenuTaskBar __instance)
    {
        // Coop / 'Download Profile Button' 是这个装饰自身的 kill switch：taskbar
        // 会在每次界面切换时调用此处注册的 toggle 与 tooltip，
        // 因此可以关掉它而让在线玩家面板保持启用。
        if (!(FikaPlugin.Instance.Settings.DownloadProfileButton?.Value ?? true))
        {
            if (!_disabledReported)
            {
                _disabledReported = true;
                FikaGlobals.LogWarning("[MenuTaskBar] the download-profile button is disabled in the config (Coop / 'Download Profile Button' = false); the taskbar is left untouched");
            }

            return;
        }

        // 本补丁重接的节点是运行中的 SPT 5 taskbar 对象。缺少节点过去会直接从
        // MenuTaskBar.Awake 抛出并中断主菜单；现在改为上报它，并
        // 让 taskbar 保持原状。
        try
        {
            Apply(__instance);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[MenuTaskBar] the download-profile button was not created: {exception}");
        }
    }

    /// <summary>
    /// 查找某个 taskbar 节点的第一个子图像，且绝不会解引用一个并非真实对象的
    /// 子节点（这条链过去是无条件求值的）。
    /// </summary>
    private static Image FirstImageOf(GameObject root)
    {
        if (!NativeRefGuard.IsUsable(root))
        {
            return null;
        }

        var rootTransform = root.transform;
        if (!NativeRefGuard.IsUsable(rootTransform) || rootTransform.childCount < 1)
        {
            return null;
        }

        var child = rootTransform.GetChild(0);
        if (!NativeRefGuard.IsUsable(child))
        {
            return null;
        }

        var childObject = child.gameObject;
        if (!NativeRefGuard.IsUsable(childObject))
        {
            return null;
        }

        var image = childObject.GetComponent<Image>();
        return NativeRefGuard.IsUsable(image) ? image : null;
    }

    private static void Apply(EFT.UI.MenuTaskBar __instance)
    {
        
        FikaGlobals.LogInfo("[MenuTaskBar] 1/2 looking up the SPT 5 taskbar nodes");
        var watchlistGameobject = GameObject.Find("Preloader UI/Preloader UI/BottomPanel/Content/TaskBar/Tabs/Watchlist");
        if (NativeRefGuard.IsUsable(watchlistGameobject))
        {
            // SPT 5 在 Awake 之后仍保留对 taskbar 控件的序列化引用。
            // 为后续的界面切换保持隐藏控件存活。
            watchlistGameobject.SetActive(false);
            var fleaMarketGameObject = GameObject.Find("Preloader UI/Preloader UI/BottomPanel/Content/TaskBar/Tabs/FleaMarket");
            if (NativeRefGuard.IsUsable(fleaMarketGameObject))
            {
                var downloadProfileGameObject = GameObject.Instantiate(fleaMarketGameObject);
                
                downloadProfileGameObject.name = "DownloadProfile";
                // 运行中的 taskbar 节点被用作父节点，因此在交给 SetParent 之前
                // 会先验证它 —— 无效的父引用会被引擎存下来并
                // 在之后被解引用。
                var taskBarParent = fleaMarketGameObject.transform.parent;
                if (!NativeRefGuard.IsUsable(taskBarParent))
                {
                    FikaGlobals.LogError("[MenuTaskBar] the flea market tab has no usable parent; the download-profile button is not created");
                    return;
                }

                var cloneTransform = downloadProfileGameObject.transform;
                if (!NativeRefGuard.IsProvablyUsable(cloneTransform, "Transform", out string cloneTransformDetail))
                {
                    FikaGlobals.LogError($"[MenuTaskBar] the cloned tab has no provably usable transform ({cloneTransformDetail}); the download-profile button is not created");
                    return;
                }

                
                cloneTransform.SetParent(taskBarParent, false);
                // 同级索引只会应用在已证实为真实的 transform 上：
                // 引擎会把它存进父节点的子列表并在之后解引用。
                cloneTransform.SetSiblingIndex(10);

                var downloadProfileButton = downloadProfileGameObject.transform.GetChild(0).gameObject;
                
                downloadProfileButton.name = "DownloadProfileButton";

                var text = downloadProfileGameObject.GetComponentInChildren<LocalizedText>();
                
                if (NativeRefGuard.IsProvablyUsable(text, "LocalizedText", out _))
                {
                    text.SetLabelText(LocaleUtils.UI_DOWNLOAD_PROFILE.Localized());
                    text.LocalizationKey = "";
                }

                var buildListObject = GameObject.Find("/Menu UI/UI/EquipmentBuildsScreen/Panels/BuildsListPanel/Header/SizeSample/Selected/Icon");
                if (NativeRefGuard.IsUsable(buildListObject))
                {
                    // 提供 sprite 的对象是运行中的 SPT 5 对象；这条链的每一步都会被
                    // 检查，因此缺失的节点不会变成一次原生解引用。
                    var downloadImage = buildListObject.GetComponent<Image>();
                    var downloadProfileImage = FirstImageOf(downloadProfileButton);
                    if (NativeRefGuard.IsUsable(downloadProfileImage) && NativeRefGuard.IsUsable(downloadImage))
                    {
                        downloadProfileImage.sprite = downloadImage.sprite;
                    }
                }

                var animatedToggle = downloadProfileGameObject.GetComponentInChildren<AnimatedToggle>();
                if (NativeRefGuard.IsProvablyUsable(animatedToggle, "AnimatedToggle", out string toggleDetail))
                {
                    
                    // 这两处都是对运行中对象的序列化字段的写入，而该对象会被
                    // taskbar 在每次界面切换时复用；只有在对象已证实为期望的
                    // 组件之后才会发生。
                    animatedToggle.group = null;
                    animatedToggle.onValueChanged = new Toggle.ToggleEvent();
                    
                    animatedToggle.onValueChanged.AddListener(async (selected) =>
                    {
                        if (!selected) return;
                        var elapsed = DateTime.Now - _lastDownload;
                        if (elapsed.TotalMinutes <= 1)
                        {
                            FikaNotifications.DisplayMessageNotification(
                                $"Please wait {(int)Math.Ceiling(60 - elapsed.TotalSeconds)} seconds before attempting to download your profile again!",
                                iconType: ENotificationIconType.Alert, textColor: new Il2CppSystem.Nullable<Color>(Color.red));
                            return;
                        }

                        try
                        {
                            var profileResponse = await FikaRequestHandler.GetProfile();
                            var responseHasError = !string.IsNullOrEmpty(profileResponse.ErrorMessage);
                            var error = responseHasError ? profileResponse.ErrorMessage : "Failed to retrieve profile";
                            if (!responseHasError && profileResponse?.Profile != null)
                            {
                                Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonBottomBarClick);
                                var installDir = Environment.CurrentDirectory;
                                var fikaDir = installDir + @"\SPT\user\fika";

                                if (!string.IsNullOrEmpty(installDir))
                                {
                                    if (!Directory.Exists(fikaDir))
                                    {
                                        Directory.CreateDirectory(fikaDir);
                                    }

                                    // 创建目录，把档案和 moddata 放进目录，切换为字典，key = modName

                                    var profileId = Singleton<ClientApplication<IEftSession>>.Instance.Session.Profile.ProfileId;

                                    var profileDir = Path.Combine(fikaDir, profileId);
                                    if (!Directory.Exists(profileDir))
                                    {
                                        Directory.CreateDirectory(profileDir);
                                    }

                                    if (File.Exists(@$"{profileDir}\{profileId}.json"))
                                    {
                                        File.Copy(Path.Combine(profileDir, $"{profileId}.json"), Path.Combine(profileDir, $"{profileId}.json.BAK"), true);
                                    }

                                    if (profileResponse.ModData != null)
                                    {
                                        foreach ((var modName, var modData) in profileResponse.ModData)
                                        {
                                            File.WriteAllText(Path.Combine(profileDir, $"{modName}.json"), modData);
                                        }
                                    }

                                    File.WriteAllText(Path.Combine(profileDir, $"{profileId}.json"), profileResponse.Profile.ToString());
                                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.SAVED_PROFILE.Localized(),
                                        [ColorizeText(EColor.BLUE, profileId), fikaDir]));

                                    /* ____toggleButtons.Remove(EMenuType.NewsHub);
                                     ____hoverTooltipAreas.Remove(EMenuType.NewsHub);
                                     GameObject.Destroy(downloadProfileGameObject);*/
                                }
                            }
                            else
                            {
                                NotificationManager.DisplayWarningNotification(error);
                            }
                        }
                        catch (Exception ex)
                        {
                            NotificationManager.DisplayWarningNotification(LocaleUtils.UNKNOWN_ERROR.Localized());
                            FikaGlobals.LogError(ex.Message);
                        }

                        _lastDownload = DateTime.Now;
                    });

                    FikaGlobals.LogInfo("[MenuTaskBar] 2/2 rewiring the news hub entry");
                    if (!__instance._hoverTooltipAreas.TryGetValue(EMenuType.NewsHub, out var surveyButton) ||
                        !__instance._toggleButtons.ContainsKey(EMenuType.NewsHub))
                    {
                        FikaGlobals.LogWarning("[MenuTaskBar] the SPT 5 taskbar has no NewsHub entry; the download-profile button is left unwired");
                        return;
                    }

                    __instance._toggleButtons.Remove(EMenuType.NewsHub);
                    __instance._hoverTooltipAreas.Remove(EMenuType.NewsHub);
                    if (NativeRefGuard.IsUsable(surveyButton))
                    {
                        surveyButton.gameObject.SetActive(false);
                    }

                    // 这两个对象会被交给运行中的 taskbar，它会在每次界面切换时调用它们。
                    // 注册一个并非真实原生对象的引用，会让菜单之后解引用到垃圾数据，
                    // 因此只有在两个对象都被证明确实可用之后，
                    // 才会进行注册。
                    var tooltip = downloadProfileGameObject.GetComponentInChildren<HoverTooltipArea>(true);
                    if (!NativeRefGuard.IsProvablyUsable(tooltip, "HoverTooltipArea", out _))
                    {
                        tooltip = downloadProfileGameObject.AddComponent<HoverTooltipArea>();
                    }

                    bool toggleUsable = NativeRefGuard.IsProvablyUsable(animatedToggle, "AnimatedToggle", out string toggleCheck);
                    bool tooltipUsable = NativeRefGuard.IsProvablyUsable(tooltip, "HoverTooltipArea", out string tooltipCheck);
                    if (!toggleUsable || !tooltipUsable)
                    {
                        FikaGlobals.LogError($"[MenuTaskBar] the download-profile button is not wired: toggle={toggleCheck} tooltip={tooltipCheck}; the taskbar is left untouched");
                        return;
                    }

                    // taskbar 会保留这两个对象，并在每次界面
                    // 切换时调用它们：它只会收到被证明确为期望组件的引用，
                    // 因此引擎永远不会持有类似 0x0A 这样的值。
                    __instance._toggleButtons.Add(EMenuType.NewsHub, animatedToggle);
                    __instance._hoverTooltipAreas.Add(EMenuType.NewsHub, tooltip);
                    _registeredToggle = animatedToggle;
                    _registeredTooltip = tooltip;
                    FikaGlobals.LogInfo($"[MenuTaskBar] download-profile button wired: toggle={toggleCheck} tooltip={tooltipCheck} surveyButton={NativeRefGuard.Describe(surveyButton)}");
                }
            }
        }
    }
}
