using EFT.Communications;
using EFT.Utilities;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.UI.Models;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPTushonka.Reflection.Patching;
using TMPro;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Patches;

/// <summary>
/// 用于把物品发送给其他玩家。
///
/// SPT 5 为每个泛型方法只编译一份原生方法体，并通过一个隐藏参数把实例化
/// 信息传进来。IL2CPP Harmony detour 会用当初安装时所用的实例化上下文
/// 重放那份方法体，因此补丁
/// <c>SimpleContextMenu.ShowMenu&lt;EItemInfoButton&gt;</c> 会让所有其他的
/// <c>ShowMenu&lt;T&gt;</c> 都像 T 是 EItemInfoButton 那样运行。杂志、
/// 杂志预设与愿望单菜单随后抛出了下面的异常：
/// 异常消息：MethodAccessException: ... IEnumerable&lt;EItemInfoButton&gt;.GetEnumerator ...
/// 发生在 ReadOnlyCollection&lt;EMagInteraction&gt; 上。改为挂钩那个把物品交互
/// 分发出来的非泛型接缝。
/// </summary>
[IgnoreAutoPatch]
public sealed class ItemContext_Patch : ModulePatch
{
    private static int _lastIndex;
    private static readonly MongoID _uiFixesParent = new("55d7217a4bdc2d86028b456d");

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ItemUiContext).GetMethod(nameof(ItemUiContext.GetItemContextInteractions));
    }

    [PatchPostfix]
    private static void Postfix(ContextInteractions<EItemInfoButton> __result, ItemContext itemContext)
    {
        if (__result?.TryCast<BaseItemContextInteractions>() is not { } itemContextInteractions
            || itemContext?.Item is not { } item)
        {
            return;
        }

        if (itemContext.ViewType == EItemViewType.Inventory)
        {
            if (InGameStatus.InRaid)
            {
                return;
            }

            // 先存为变量，以便之后需要时添加更多检查...
            var menuUI = Singleton<MenuUI>.Instance;

            if (menuUI.HideoutAreaTransferItemsScreen.isActiveAndEnabled
                || menuUI.HideoutMannequinEquipmentScreen.isActiveAndEnabled
                || menuUI.HideoutCircleOfCultistsScreen.isActiveAndEnabled)
            {
                return;
            }

            var parentItems = item.GetAllParentItems().AsManaged();
            if (parentItems.Any(x => x?.TryCast<InventoryEquipment>() != null))
            {
                return;
            }

            if (item.Parent?.Container?.ParentItem?.TemplateId == _uiFixesParent) // UI Fixes 的修复
            {
                return;
            }

            if (item.PinLockState is EItemPinLockState.Pinned or EItemPinLockState.Locked) // 不发送已固定或已锁定的物品（谁会这么干？）
            {
                return;
            }

            // 检查 GClass 的增量
            var dynamicInteractions = itemContextInteractions._dynamicInteractions;
            if (dynamicInteractions == null)
            {
                dynamicInteractions = new Il2CppSystem.Collections.Generic.Dictionary<string, DynamicContextInteraction>();
                itemContextInteractions._dynamicInteractions = dynamicInteractions;
            }

            System.Action sendAction = () =>
            {
                var selectedItems = MultiSelect.Count > 0 ? MultiSelect.Items.ToArray() : [item];
                if (selectedItems.Length == 0)
                {
                    return;
                }

                foreach (var itemId in FikaPlugin.Instance.Settings.BlacklistedItems)
                {
                    foreach (var selectedItem in selectedItems)
                    {
                        foreach (var attachedItem in selectedItem.GetAllItems())
                        {
                            if (attachedItem.TemplateId != itemId)
                            {
                                continue;
                            }

                            var itemText = ColorizeText(EColor.BLUE, selectedItem.ShortName.Localized());
                            if (attachedItem.Id == selectedItem.Id)
                            {
                                FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.ITEM_BLACKLISTED.Localized(), itemText),
                                    iconType: ENotificationIconType.Alert);
                            }
                            else
                            {
                                var itemName = attachedItem.ShortName.Localized();
                                var attachedItemText = ColorizeText(EColor.BLUE, itemName);
                                FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.ITEM_CONTAINS_BLACKLISTED.Localized(),
                                    [itemText, attachedItemText]),
                                    iconType: ENotificationIconType.Alert);
                            }
                            return;
                        }
                    }
                }

                AvailableReceiversRequest body = new(itemContext.Item.Id);
                var availableUsers = FikaRequestHandler.AvailableReceivers(body);

                var items = selectedItems.Select(i => i.Id).ToArray();
#if DEBUG
                FikaGlobals.LogInfo($"{items.Length} items are selected");
#endif

                // Fika 服务器以 nickname -> profile id 的形式作答。下拉框
                // 只显示昵称，但载荷必须携带 profile
                // id：发送昵称会让服务器以
                // "Target profile not found" 失败，因为它是按 id 查找目标的。
                List<TMP_Dropdown.OptionData> optionDatas = [];
                var profileIdByNickname = new Dictionary<string, string>(System.StringComparer.Ordinal);
                foreach (var user in availableUsers)
                {
                    optionDatas.Add(new()
                    {
                        text = user.Key
                    });
                    profileIdByNickname[user.Key] = user.Value;
                }

                // 获取菜单项
                var currentUI = GameObject.Find("SendItemMenu(Clone)");
                if (currentUI != null)
                {
                    GameObject.Destroy(currentUI);
                }

                // 创建窗口
                var matchMakerUiPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.SendItemMenu);
                var uiGameObj = Object.Instantiate(matchMakerUiPrefab);
                uiGameObj.transform.SetParent(GameObject.Find("Preloader UI/Preloader UI/UIContext/").transform);
                var screenController = CommonUI.Instance.InventoryScreen.ScreenController;
                screenController.OnClose += (System.Action)(() => Object.Destroy(uiGameObj));
                var sendItemUI = uiGameObj.GetComponent<SendItemUI>();
                sendItemUI.PlayersDropdown.Value.ClearOptions();
                sendItemUI.PlayersDropdown.Value.AddOptions(optionDatas.ToNativeList());

                if (_lastIndex >= 0 && _lastIndex < sendItemUI.PlayersDropdown.Value.options.Count)
                {
                    sendItemUI.PlayersDropdown.Value.value = _lastIndex;
                }

                sendItemUI.PlayersDropdown.Value.onValueChanged.AddListener((_) => Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuDropdownSelect));
                sendItemUI.CloseButton.Value.onClick.AddListener(() => Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick));
                sendItemUI.CloseButton.Value.onClick.AddListener(() => Object.Destroy(uiGameObj));

                sendItemUI.PlayersFilter.Value.onValueChanged.AddListener((input) =>
                {
                    if (string.IsNullOrEmpty(input))
                    {
                        sendItemUI.PlayersDropdown.Value.ClearOptions();
                        sendItemUI.PlayersDropdown.Value.AddOptions(optionDatas.ToNativeList());
                        sendItemUI.PlayersDropdown.Value.interactable = true;
                        return;
                    }

                    sendItemUI.PlayersDropdown.Value.ClearOptions();

                    var filtered = availableUsers.Keys.Where(option => option
                        .Contains(input, System.StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    sendItemUI.PlayersDropdown.Value.AddOptions(filtered.ToNativeList());
                    sendItemUI.PlayersDropdown.Value.interactable = filtered.Count > 0;
                });

                sendItemUI.SendButton.Value.onClick.AddListener(() =>
                {
                    if (sendItemUI.PlayersDropdown.Value.value < 0 || sendItemUI.PlayersDropdown.Value.value >= sendItemUI.PlayersDropdown.Value.options.Count)
                    {
                        return;
                    }
                    if (sendItemUI.PlayersDropdown.Value.options[sendItemUI.PlayersDropdown.Value.value].text != null)
                    {
                        var player = sendItemUI.PlayersDropdown.Value.options[sendItemUI.PlayersDropdown.Value.value].text;
                        _lastIndex = sendItemUI.PlayersDropdown.Value.value;
                        if (Singleton<ClientApplication<IEftSession>>.Instantiated)
                        {
                            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.TradeOperationComplete);

                            // 把载荷构造成 JSON 文本。BepInEx/plugins 下的托管
                            // Newtonsoft.Json 会遮蔽 IL2CPP 的代理，因此 new JObject()
                            // 会产生一个原生后端调用无法接受的对象；
                            // 改为由游戏自身的解析器把这段文本
                            // 转换成一个原生 JObject。
                            var builder = new System.Text.StringBuilder("{\"Action\":\"SendToPlayer\",\"itemIds\":[");
                            var first = true;
                            foreach (var id in items)
                            {
                                if (!first) builder.Append(',');
                                first = false;
                                builder.Append('"').Append(id).Append('"');
                            }

                            // 较旧的服务器本来就以 id 作答；在优先为昵称解析 id 的同时，
                            // 保持那条路径仍可用。
                            var target = profileIdByNickname.TryGetValue(player, out var targetId) && !string.IsNullOrWhiteSpace(targetId)
                                ? targetId
                                : player;
                            builder.Append("],\"target\":\"").Append(target).Append("\"}");

                            var callback = (Callback)(ar =>
                            {
                                if (ar.Failed)
                                {
                                    PreloaderUI.Instance.ShowErrorScreen("Fika.Core.ItemContextPatch", ar.Error ?? "An unknown error has occurred");
                                }
                            });

                            SendOperation(builder.ToString(), callback);
                        }
                        else
                        {
                            PreloaderUI.Instance.ShowErrorScreen("Fika.Core.ItemContextPatch", "!Singleton<IEftSession>.Instantiated");
                        }
                    }
                    GameObject.Destroy(uiGameObj);
                });
            };
            dynamicInteractions["SEND"] = new DynamicContextInteraction("SEND", "SEND", (System.Action)sendAction,
                ResourcesCache.Pop<Sprite>("Characteristics/Icons/UnloadAmmo"), null, null);
        }
    }

    /// <summary>
    /// 通过游戏的后端会话提交该操作。载荷以 JSON 文本
    /// 的形式交出，因为原生调用接收的是一个 Newtonsoft
    /// JObject，而当托管的 Newtonsoft.Json 遮蔽了 IL2CPP interop
    /// 代理时，托管代码无法构造这种对象。
    /// </summary>
    private static void SendOperation(string payloadJson, Callback callback)
    {
        if (NativeBackendSession.TrySend(GetBackendSession(), payloadJson, callback))
        {
            return;
        }

        SendOperationThroughInterop(payloadJson, callback);
    }

    private static EFT.ClientBackendSession GetBackendSession() =>
        Singleton<ClientApplication<IEftSession>>.Instance.GetClientBackEndSession()
            .TryCast<EFT.ClientBackendSession>();

    /// <summary>
    /// 刻意放在自己的非内联方法里：当存在遮蔽程序集时，链接失败会在
    /// 此方法被编译时暴露出来，因此调用方必须处于
    /// 另一个栈帧才能观察到它。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SendOperationThroughInterop(string payloadJson, Callback callback)
    {
        var payload = Newtonsoft.Json.Linq.JToken.Parse(payloadJson);
        GetBackendSession().SendOperationRightNow(payload, callback);
    }
}
