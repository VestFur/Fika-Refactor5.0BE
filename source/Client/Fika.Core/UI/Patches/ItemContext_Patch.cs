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
/// Used to send items to other players.
///
/// SPT 5 compiles one native body per generic method and hands the instantiation
/// over in a hidden argument. The IL2CPP Harmony detour replays that body with
/// the context of the instantiation it was installed for, so patching
/// <c>SimpleContextMenu.ShowMenu&lt;EItemInfoButton&gt;</c> made every other
/// <c>ShowMenu&lt;T&gt;</c> run as if T were EItemInfoButton. The magazine,
/// magazine preset and wishlist menus then threw
/// MethodAccessException: ... IEnumerable&lt;EItemInfoButton&gt;.GetEnumerator ...
/// on ReadOnlyCollection&lt;EMagInteraction&gt;. Hook the non-generic seam that
/// hands the item interactions out instead.
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

            // Save as variable in case we need to add more checks later...
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

            if (item.Parent?.Container?.ParentItem?.TemplateId == _uiFixesParent) // Fix for UI Fixes
            {
                return;
            }

            if (item.PinLockState is EItemPinLockState.Pinned or EItemPinLockState.Locked) // Don't send pinned or locked items (who even does this?)
            {
                return;
            }

            // Check for GClass increments
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

                // The Fika server answers with nickname -> profile id. The dropdown
                // only shows the nickname, but the payload has to carry the profile
                // id: sending the nickname makes the server fail with
                // "Target profile not found" because it looks the target up by id.
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

                // Get menu item
                var currentUI = GameObject.Find("SendItemMenu(Clone)");
                if (currentUI != null)
                {
                    GameObject.Destroy(currentUI);
                }

                // Create the window
                var matchMakerUiPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.SendItemMenu);
                var uiGameObj = Object.Instantiate(matchMakerUiPrefab);
                uiGameObj.transform.SetParent(GameObject.Find("Preloader UI/Preloader UI/UIContext/").transform);
                var screenController = CommonUI.Instance.InventoryScreen.ScreenController;
                screenController.OnClose += (Il2CppSystem.Action)(() => Object.Destroy(uiGameObj));
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

                            // Build the payload as JSON text. A managed
                            // Newtonsoft.Json in BepInEx/plugins shadows the IL2CPP
                            // proxy, so new JObject() would produce an object the
                            // native backend call cannot accept; the game's own
                            // parser turns the text into a native JObject instead.
                            var builder = new System.Text.StringBuilder("{\"Action\":\"SendToPlayer\",\"itemIds\":[");
                            var first = true;
                            foreach (var id in items)
                            {
                                if (!first) builder.Append(',');
                                first = false;
                                builder.Append('"').Append(id).Append('"');
                            }

                            // Older servers answered with ids already; keep that
                            // working while preferring the id for the nickname.
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
            dynamicInteractions["SEND"] = new DynamicContextInteraction("SEND", "SEND", (Il2CppSystem.Action)sendAction,
                ResourcesCache.Pop<Sprite>("Characteristics/Icons/UnloadAmmo"), null, null);
        }
    }

    /// <summary>
    /// Posts the operation through the game's backend session. The payload is
    /// handed over as JSON text because the native call takes a Newtonsoft
    /// JObject that managed code cannot construct while a managed
    /// Newtonsoft.Json shadows the IL2CPP interop proxy.
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
    /// Kept in its own non-inlined method: with a shadowing assembly the linkage
    /// failure surfaces while this method is compiled, so the caller has to be a
    /// different frame to observe it.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SendOperationThroughInterop(string payloadJson, Callback callback)
    {
        var payload = Newtonsoft.Json.Linq.JToken.Parse(payloadJson);
        GetBackendSession().SendOperationRightNow(payload, callback);
    }
}
