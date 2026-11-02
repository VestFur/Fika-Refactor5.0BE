using EFT.Communications;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Models.Headless;
using Fika.Core.Networking.Websocket;
using Fika.Core.UI.Models;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Custom;

public class MatchMakerUIScript : MonoBehaviour
{
    public MatchMakerUIScript(System.IntPtr pointer) : base(pointer) { }


    public MatchMakerUIScript() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MatchMakerUIScript>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private MatchMakerUI _fikaMatchMakerUi;
    private LobbyEntry[] _matches;
    private readonly List<GameObject> _matchesListObjects = [];
    private bool _stopQuery;
    private GameObject _newBackButton;
    private string _profileId;
    private float _lastRefreshed;
    private bool _started;
    private Coroutine _serverQueryRoutine;
    private float _dotTimer;
    private int _dotCount;
    private GameObject _mmGameObject;

    private const float _dotInterval = 0.5f;

    internal DefaultUIButton AcceptButton;
    internal RaidSettings RaidSettings;
    internal DefaultUIButton BackButton;

    protected void OnEnable()
    {
        if (_started)
        {
            _stopQuery = false;
            if (_serverQueryRoutine == null)
            {
                _serverQueryRoutine = Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, ServerQuery());
            }
        }
    }

    protected void OnDisable()
    {
        FikaPlugin.HeadlessRequesterWebSocket?.Close();
        FikaPlugin.HeadlessRequesterWebSocket = null;
        _stopQuery = true;
        if (_serverQueryRoutine != null)
        {
            StopCoroutine(_serverQueryRoutine);
            _serverQueryRoutine = null;
        }
        DestroyThis();
    }

    protected void Start()
    {
        _profileId = FikaBackendUtils.Profile.ProfileId;
        CreateMatchMakerUI();
        _serverQueryRoutine = Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, ServerQuery());
        _started = true;
    }

    protected void Update()
    {
        if (_stopQuery && _serverQueryRoutine != null)
        {
            StopCoroutine(_serverQueryRoutine);
            _serverQueryRoutine = null;
        }

        if (!_fikaMatchMakerUi.LoadingScreen.Value.activeSelf)
        {
            return;
        }

        _dotTimer += Time.deltaTime;

        if (_dotTimer >= _dotInterval)
        {
            _dotTimer = 0f;
            _dotCount = (_dotCount % 3) + 1;

            _fikaMatchMakerUi.LoadingAnimationText.Value.text =
                new string('.', _dotCount);
        }
    }

    private void DestroyThis()
    {
        _stopQuery = true;
        if (_serverQueryRoutine != null)
        {
            StopCoroutine(_serverQueryRoutine);
            _serverQueryRoutine = null;
        }

        Destroy(_fikaMatchMakerUi);
        Destroy(this);
        Destroy(_mmGameObject);
    }

    protected void OnDestroy()
    {
        _stopQuery = true;
        if (_newBackButton != null)
        {
            Destroy(_newBackButton);
        }
    }

    private void CreateMatchMakerUI()
    {
        FikaPlugin.HeadlessRequesterWebSocket?.Close();
        FikaPlugin.HeadlessRequesterWebSocket = null;
        FikaBackendUtils.IsHeadlessRequester = false;

        var availableHeadlesses = FikaRequestHandler.GetAvailableHeadlesses();

        var matchMakerUiPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.MatchmakerUI);
        var uiGameObj = Instantiate(matchMakerUiPrefab);
        _mmGameObject = uiGameObj;
        _fikaMatchMakerUi = uiGameObj.GetComponent<MatchMakerUI>();
        _fikaMatchMakerUi.transform.parent = transform;

        var rectTransform = _fikaMatchMakerUi.transform.GetChild(0).RectTransform();
        rectTransform.gameObject.AddComponent<UIDragComponent>().Init(rectTransform, true);

        if (_fikaMatchMakerUi.RaidGroupDefaultToClone.Value.active)
        {
            _fikaMatchMakerUi.RaidGroupDefaultToClone.Value.SetActive(false);
        }

        if (_fikaMatchMakerUi.DediSelection.Value.active)
        {
            _fikaMatchMakerUi.DediSelection.Value.SetActive(false);
        }

        // 确保每次创建 matchmaker UI 时都重置 IsSpectator 字段
        FikaBackendUtils.IsSpectator = false;

        _fikaMatchMakerUi.SpectatorToggle.Value.isOn = false;
        _fikaMatchMakerUi.SpectatorToggle.Value.onValueChanged.AddListener((arg) =>
        {
            FikaBackendUtils.IsSpectator = !FikaBackendUtils.IsSpectator;
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuCheckBox);
        });

        _fikaMatchMakerUi.LoadingAnimationText.Value.SetText(string.Empty);

        _fikaMatchMakerUi.DedicatedToggle.Value.isOn = false;
        _fikaMatchMakerUi.DedicatedToggle.Value.onValueChanged.AddListener((_) => Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuCheckBox));

        if (availableHeadlesses.Length == 0)
        {
            _fikaMatchMakerUi.DedicatedToggle.Value.interactable = false;
            var dedicatedText = _fikaMatchMakerUi.DedicatedToggle.Value.gameObject.GetComponentInChildren<TextMeshProUGUI>();
            if (dedicatedText != null)
            {
                dedicatedText.color = new(1f, 1f, 1f, 0.5f);
            }

            var dediTooltipArea = _fikaMatchMakerUi.DedicatedToggle.Value.GetOrAddComponent<HoverTooltipArea>();
            dediTooltipArea.enabled = true;
            dediTooltipArea.SetMessageText(LocaleUtils.UI_NO_DEDICATED_CLIENTS.Localized());
        }

        if (availableHeadlesses.Length >= 1)
        {
            if (FikaPlugin.Instance.Settings.UseHeadlessIfAvailable.Value)
            {
                _fikaMatchMakerUi.DedicatedToggle.Value.isOn = true;
            }

            _fikaMatchMakerUi.HeadlessSelection.Value.gameObject.SetActive(true);
            _fikaMatchMakerUi.HeadlessSelection.Value.onValueChanged.AddListener((_) => Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuDropdownSelect));

            _fikaMatchMakerUi.HeadlessSelection.Value.ClearOptions();

            List<TMP_Dropdown.OptionData> optionDatas = [];

            // 按 Alias 的字母顺序排序 availableHeadlesses
            Array.Sort(availableHeadlesses,
                (x, y) => string.Compare(x.Alias, y.Alias, StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < availableHeadlesses.Length; i++)
            {
                var user = availableHeadlesses[i];
                optionDatas.Add(new()
                {
                    text = user.Alias
                });
            }

            _fikaMatchMakerUi.HeadlessSelection.Value.AddOptions(optionDatas.ToNativeList());
        }

        var hostTooltipArea = _fikaMatchMakerUi.RaidGroupHostButton.Value.GetOrAddComponent<HoverTooltipArea>();
        hostTooltipArea.enabled = true;
        hostTooltipArea.SetMessageText(LocaleUtils.UI_HOST_RAID_TOOLTIP.Localized());

        _fikaMatchMakerUi.RaidGroupHostButton.Value.onClick.AddListener(() =>
        {
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
            if (!_fikaMatchMakerUi.DediSelection.Value.activeSelf)
            {
                _fikaMatchMakerUi.DediSelection.Value.SetActive(true);
            }
            else
            {
                _fikaMatchMakerUi.DediSelection.Value.SetActive(false);
            }
        });

        _fikaMatchMakerUi.CloseButton.Value.onClick.AddListener(() =>
        {
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
            if (_fikaMatchMakerUi.DediSelection.Value.active)
            {
                _fikaMatchMakerUi.DediSelection.Value.SetActive(false);
            }
        });

        _fikaMatchMakerUi.DedicatedToggle.Value.onValueChanged.AddListener((_) => Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuCheckBox));

        _fikaMatchMakerUi.StartButton.Value.onClick.AddListener(() => RunLobbyAction(async () =>
        {
            ToggleLoading(true);

            // scav raid 从不打开离线 raid 设置界面，因此它的
            // TimeAndWeatherSettings 保持结构体默认值——小时 0，而
            // CoopGame 会把它当作真实的定制小时，从而让每个 scav raid 都在
            // 午夜开始。-1 表示 "保持世界时钟"（加入的玩家仍会
            // 从服务器收到主机自己的选择）。
            if (RaidSettings.IsScav && RaidSettings.TimeAndWeatherSettings.HourOfDay == 0)
            {
                var scavTimeAndWeather = RaidSettings.TimeAndWeatherSettings;
                scavTimeAndWeather.HourOfDay = -1;
                RaidSettings.TimeAndWeatherSettings = scavTimeAndWeather;
                FikaGlobals.LogInfo("Scav raid: no raid time was configured, following the world clock");
            }

            // 该 application 只在下面的 headless 路径中需要。它过去
            // 在这里用强制转换获取，而强制转换会在游戏处于
            // 界面切换期间时抛出 "Unable to cast ... to TarkovApplication"，并
            // 中止了整个大厅操作——Fika 对局随后
            // 从未被创建，raid 也无法开始。
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);

            if (!_fikaMatchMakerUi.DedicatedToggle.Value.isOn)
            {
                if (FikaPlugin.Instance.Settings.ForceIP.Value != "")
                {
                    // 我们还需要处理 DNS 条目
                    var ip = FikaPlugin.Instance.Settings.ForceIP.Value;
                    try
                    {
                        var dnsAddress = Dns.GetHostAddresses(FikaPlugin.Instance.Settings.ForceIP.Value);
                        if (dnsAddress.Length > 0)
                        {
                            ip = dnsAddress[0].ToString();
                        }
                    }
                    catch
                    {

                    }

                    if (!IPAddress.TryParse(ip, out _))
                    {
                        Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(LocaleUtils.UI_ERROR_FORCE_IP_HEADER.Localized(),
                            string.Format(LocaleUtils.UI_ERROR_FORCE_IP.Localized(), ip),
                            ErrorScreen.EButtonType.OkButton, 10f);

                        ToggleLoading(false);
                        return;
                    }
                }

                if (FikaPlugin.Instance.Settings.ForceBindIP.Value != "Disabled")
                {
                    if (!IPAddress.TryParse(FikaPlugin.Instance.Settings.ForceBindIP.Value, out _))
                    {
                        Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(LocaleUtils.UI_ERROR_BIND_IP_HEADER.Localized(),
                            string.Format(LocaleUtils.UI_ERROR_BIND_IP.Localized(), FikaPlugin.Instance.Settings.ForceBindIP.Value),
                            ErrorScreen.EButtonType.OkButton, 10f);

                        ToggleLoading(false);
                        return;
                    }
                }

                FikaGlobals.LogInfo($"Creating a match: hourOfDay={RaidSettings.TimeAndWeatherSettings.HourOfDay}, " +
                    $"scav={RaidSettings.IsScav}, time={RaidSettings.SelectedDateTime}");
                await FikaBackendUtils.CreateMatch(FikaBackendUtils.Profile.ProfileId, FikaBackendUtils.PMCName, RaidSettings);
                AcceptButton.OnClick.Invoke();
            }
            else
            {
                FikaPlugin.HeadlessRequesterWebSocket?.Close();
                var requesterSocket = new HeadlessRequesterWebSocket();
                FikaPlugin.HeadlessRequesterWebSocket = requesterSocket;
                await requesterSocket.EnsureConnectedAsync(TimeSpan.FromSeconds(15));
                if (_stopQuery || this == null) { requesterSocket.Close(); return; }

                if (!TarkovApplication.Exist(out var tarkovApplication))
                {
                    FikaGlobals.LogError("StartButton: TarkovApplication was not available for the headless request");
                    ToggleLoading(false);
                    return;
                }

                var raidSettings = tarkovApplication._raidSettings;

                var headlessSessionId = availableHeadlesses[0].HeadlessSessionID;
                var multipleHeadlesses = availableHeadlesses.Length > 1;

                if (multipleHeadlesses)
                {
                    var selectedHeadless = _fikaMatchMakerUi.HeadlessSelection.Value.value;
                    headlessSessionId = availableHeadlesses[selectedHeadless].HeadlessSessionID;
                }

                StartHeadlessRequest request = new()
                {
                    HeadlessSessionID = headlessSessionId,
                    Time = raidSettings.SelectedDateTime,
                    LocationId = raidSettings.SelectedLocation._Id,
                    SpawnPlace = raidSettings.PlayersSpawnPlace,
                    MetabolismDisabled = raidSettings.MetabolismDisabled,
                    BotSettings = raidSettings.BotSettings,
                    Side = raidSettings.Side,
                    TimeAndWeatherSettings = raidSettings.TimeAndWeatherSettings,
                    WavesSettings = raidSettings.WavesSettings,
                    CustomRaidSettings = FikaBackendUtils.CustomRaidSettings,
                    UseEvent = raidSettings.transitionType.HasFlagNoBox(ELocationTransition.Event)
                };

                FikaBackendUtils.IsHeadlessRequester = true;
                var response = await FikaRequestHandler.StartHeadless(request);
                if (_stopQuery || this == null) return;

                if (!string.IsNullOrEmpty(response.Error))
                {
                    PreloaderUI.Instance.ShowErrorScreen(LocaleUtils.UI_DEDICATED_ERROR.Localized(), response.Error);
                    ToggleLoading(false);
                    FikaBackendUtils.IsHeadlessRequester = false;
                    requesterSocket.Close();
                }
                else
                {
                    NotificationManager.DisplaySingletonWarningNotification(LocaleUtils.STARTING_RAID_ON_DEDICATED.Localized());
                }
            }
        }));

        _fikaMatchMakerUi.RefreshButton.Value.onClick.AddListener(ManualRefresh);

        var tooltipArea = _fikaMatchMakerUi.RefreshButton.Value.GetOrAddComponent<HoverTooltipArea>();
        tooltipArea.enabled = true;
        tooltipArea.SetMessageText(LocaleUtils.UI_REFRESH_RAIDS.Localized());

        AcceptButton.gameObject.SetActive(false);
        AcceptButton.enabled = false;
        AcceptButton.Interactable = false;

        _newBackButton = Instantiate(BackButton.gameObject, BackButton.transform.parent);
        UnityEngine.Events.UnityEvent newEvent = new();
        newEvent.AddListener(BackButton.OnClick.Invoke);
        var newButtonComponent = _newBackButton.GetComponent<DefaultUIButton>();
        newButtonComponent.OnClick = newEvent;

        if (!_newBackButton.active)
        {
            _newBackButton.SetActive(true);
        }

        BackButton.gameObject.SetActive(false);
    }

    private void RunLobbyAction(Func<Task> action)
    {
        MainThreadDispatcher.RunAsync(async () =>
        {
            try { await action(); }
            catch (Exception exception)
            {
                FikaPlugin.Instance.FikaLogger.LogError($"Fika lobby action failed: {exception}");
                FikaPlugin.HeadlessRequesterWebSocket?.Close();
                FikaPlugin.HeadlessRequesterWebSocket = null;
                FikaBackendUtils.IsHeadlessRequester = false;
                if (_stopQuery || this == null) return;
                ToggleLoading(false);
                PreloaderUI.Instance.ShowErrorScreen("Fika", exception.Message);
            }
        }).Forget();
    }

    private void ToggleLoading(bool enabled)
    {
        _fikaMatchMakerUi.RaidGroupHostButton.Value.interactable = !enabled;
        _fikaMatchMakerUi.DediSelection.Value.SetActive(!enabled);
        _fikaMatchMakerUi.StartButton.Value.interactable = !enabled;
        _fikaMatchMakerUi.ServerBrowserPanel.Value.SetActive(!enabled);

        _fikaMatchMakerUi.LoadingScreen.Value.SetActive(enabled);

        if (enabled)
        {
            if (_serverQueryRoutine != null)
            {
                StopCoroutine(_serverQueryRoutine);
                _serverQueryRoutine = null;
            }
            return;
        }

        _serverQueryRoutine = Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, ServerQuery());
    }

    private void AutoRefresh()
    {
        _matches = FikaRequestHandler.LocationRaids(RaidSettings);

        _lastRefreshed = Time.time;

        RefreshUI();
    }

    private void ManualRefresh()
    {
        Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
        _matches = FikaRequestHandler.LocationRaids(RaidSettings);

        _lastRefreshed = Time.time;

        RefreshUI();
    }

    public static Task<bool> JoinMatch(string profileId, string serverId, Button button) =>
        JoinMatchCore(profileId, serverId, button, false, CancellationToken.None);

    public static Task<bool> JoinMatch(string profileId, string serverId, Button button,
        CancellationToken cancellationToken) =>
        JoinMatchCore(profileId, serverId, button, false, cancellationToken);

    /// <summary>
    /// 加入实现。<paramref name="scavLateJoin"/> 标记一个玩家 scav
    /// 进入已经开始的 raid：它使用自己的连接键，
    /// 以 scav 身份生成，并留在主机的小组之外。
    /// </summary>
    public static async Task<bool> JoinMatchCore(string profileId, string serverId, Button button, bool scavLateJoin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (button != null)
        {
            button.enabled = false;
        }

        // 新的尝试从干净状态开始；上一次尝试的失败在此处报告（并记录日志），
        // 而不是在 raid 开始时用一个过期的原因中止本次尝试。
        FikaBackendUtils.ClearLeftoverJoinFailure();

        FikaBackendUtils.IsScavLateJoin = scavLateJoin;
        FikaNotifications.DisplayMessageNotification(LocaleUtils.CONNECTING_TO_SESSION.Localized(),
            iconType: EFT.Communications.ENotificationIconType.EntryPoint);
        using var pingingClient = await NetManagerUtils.CreatePingingClient();
        cancellationToken.ThrowIfCancellationRequested();

        if (pingingClient.Init(serverId))
        {
            FikaGlobals.LogInfo("Attempting to connect to host session...");
            var knockMessage = FikaBackendUtils.ServerGuid.ToString();
            // 玩家 scav 进入正在运行的 raid 是被允许的，因此
            // 主机必须给出 "你可以连接" 的应答，而不是
            // "raid 已经开始" 的应答。
            var success = await pingingClient.AttemptToPingHost(knockMessage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!success)
            {
                Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                LocaleUtils.UI_UNABLE_TO_CONNECT.Localized() + PingingFailureDetail(pingingClient),
                ErrorScreen.EButtonType.OkButton, 10f);

                if (button != null)
                {
                    button.enabled = true;
                }
                return false;
            }
        }
        else
        {
            // 过去两种不同的失败共用 "could not start the FikaPinger" 这段文本。应答时
            // 没有主机记录的服务器属于主机侧问题（他们的 raid 从未发布），
            // 传输失败则属于我们；末尾的细节行会说明是哪一种。
            Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                (pingingClient.HostAddressMissing
                    ? LocaleUtils.UI_ERROR_CONNECTING_TO_RAID.Localized()
                    : LocaleUtils.UI_UNABLE_TO_CONNECT.Localized()) + PingingFailureDetail(pingingClient),
                ErrorScreen.EButtonType.OkButton, 10f);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (FikaBackendUtils.JoinMatch(profileId, serverId, out var result, out var errorMessage))
        {
            FikaBackendUtils.GroupId = result.ServerId;
            FikaBackendUtils.ClientType = EClientType.Client;

            AddPlayerRequest data = new(FikaBackendUtils.GroupId, profileId, FikaBackendUtils.IsSpectator);
            FikaRequestHandler.UpdateAddPlayer(data);

            return true;
        }
        else
        {
            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING", errorMessage, null);
            return false;
        }
    }

    /// <summary>
    /// 把 ping 无法启动的具体原因追加到对话框正文，这样玩家不必
    /// 打开日志就能区分 "主机从未发布地址" 与 "我们无法访问
    /// 服务器"。没有额外细节时为空。
    /// </summary>
    private static string PingingFailureDetail(FikaPingingClient pingingClient) =>
        string.IsNullOrEmpty(pingingClient?.FailureReason) ? string.Empty : "\n\n" + pingingClient.FailureReason;

    private void RefreshUI()
    {
        if (_matches == null)
        {
            // 未初始化
            return;
        }

        if (_matchesListObjects != null)
        {
            // 清理旧对象
            foreach (var match in _matchesListObjects)
            {
                Destroy(match);
            }
        }

        // 创建大厅列表
        for (var i = 0; i < _matches.Length; ++i)
        {
            var entry = _matches[i];

            if (entry.ServerId == _profileId)
            {
                continue;
            }

            // 服务器对象
            var server = Instantiate(_fikaMatchMakerUi.RaidGroupDefaultToClone.Value, _fikaMatchMakerUi.RaidGroupDefaultToClone.Value.transform.parent);
            server.SetActive(true);
            _matchesListObjects.Add(server);

            server.name = entry.ServerId;

            // 玩家 scav 可能走进一个已经在运行的 PMC raid。
            // 这种加入会在运行过程中采用主机的位置和世界时钟，
            // 并以 scav 身份生成，因此它从不属于主机的小组。
            var scavLateJoin = RaidSettings.Side == ESideType.Savage && entry.Side == ESideType.Pmc;

            // 玩家标签
            var playerLabel = GameObject.Find("PlayerLabel");
            playerLabel.name = "PlayerLabel" + i;
            var sessionName = entry.HostUsername;
            playerLabel.GetComponentInChildren<TextMeshProUGUI>().SetText(sessionName);

            // 玩家数量标签
            var playerCountLabel = GameObject.Find("PlayerCountLabel");
            playerCountLabel.name = "PlayerCountLabel" + i;
            var playerCount = entry.IsHeadless ? entry.PlayerCount - 1 : entry.PlayerCount;
            playerCountLabel.GetComponentInChildren<TextMeshProUGUI>().SetFormattedText("{0}", playerCount);

            // 玩家加入按钮
            var joinButton = GameObject.Find("JoinButton");
            joinButton.name = "JoinButton" + i;
            var button = joinButton.GetComponent<Button>();
            button.onClick.AddListener(() => RunLobbyAction(async () =>
            {
                if (_fikaMatchMakerUi.DediSelection.Value.activeSelf)
                {
                    _fikaMatchMakerUi.DediSelection.Value.SetActive(false);
                }

                Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
                FikaBackendUtils.HostLocationId = entry.Location;
                ToggleLoading(true);
                var success = await JoinMatchCore(_profileId, server.name, button, scavLateJoin,
                    CancellationToken.None);
                if (success)
                {
                    AcceptButton.OnClick.Invoke();
                    return;
                }
                ToggleLoading(false);
            }));

            HoverTooltipArea tooltipArea;
            var image = server.GetComponent<Image>();

            if (RaidSettings.LocationId != entry.Location && !(RaidSettings.LocationId.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase) &&
                entry.Location.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase)))
            {
                button.enabled = false;
                if (image != null)
                {
                    image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                }

                tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                tooltipArea.enabled = true;
                tooltipArea.SetMessageText(string.Format(LocaleUtils.UI_CANNOT_JOIN_RAID_OTHER_MAP.Localized(),
                    ColorizeText(EColor.BLUE, entry.Location.Localized())));

                continue;
            }

            if (!scavLateJoin && RaidSettings.SelectedDateTime != entry.Time)
            {
                button.enabled = false;
                if (image != null)
                {
                    image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                }

                tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                tooltipArea.enabled = true;
                tooltipArea.SetMessageText(LocaleUtils.UI_CANNOT_JOIN_RAID_OTHER_TIME.Localized());

                continue;
            }

            if (!scavLateJoin && RaidSettings.Side != entry.Side)
            {
                var errorText = "ERROR";
                if (RaidSettings.Side == ESideType.Pmc)
                {
                    errorText = LocaleUtils.UI_CANNOT_JOIN_RAID_SCAV_AS_PMC.Localized();
                }
                else if (RaidSettings.Side == ESideType.Savage)
                {
                    errorText = LocaleUtils.UI_CANNOT_JOIN_RAID_PMC_AS_SCAV.Localized();
                }

                button.enabled = false;
                if (image != null)
                {
                    image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                }

                tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                tooltipArea.enabled = true;
                tooltipArea.SetMessageText(errorText);

                continue;
            }

            switch (entry.Status)
            {
                case LobbyEntry.ELobbyStatus.LOADING:
                    {
                        button.enabled = false;
                        if (image != null)
                        {
                            image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                        }

                        tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                        tooltipArea.enabled = true;
                        tooltipArea.SetMessageText(LocaleUtils.UI_HOST_STILL_LOADING.Localized());
                    }
                    break;
                case LobbyEntry.ELobbyStatus.IN_GAME:
                    if (scavLateJoin)
                    {
                        // 玩家 scav 按设计可以加入正在运行的 raid。
                        tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                        tooltipArea.enabled = true;
                        tooltipArea.SetMessageText(LocaleUtils.UI_JOIN_RAID.Localized());
                        break;
                    }

                    // 已经在运行的 raid 无法再次进入：本客户端
                    // 留下的角色会留在主机处，与原生断线处理所做的
                    // 完全一致。重新加入它属于已被移除的断线重连功能。
                    button.enabled = false;
                    if (image != null)
                    {
                        image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                    }

                    tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                    tooltipArea.enabled = true;
                    tooltipArea.SetMessageText(LocaleUtils.UI_RAID_IN_PROGRESS.Localized());
                    break;
                case LobbyEntry.ELobbyStatus.COMPLETE:
                    tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                    tooltipArea.enabled = true;
                    tooltipArea.SetMessageText(LocaleUtils.UI_JOIN_RAID.Localized());
                    break;
            }
        }
    }

    public IEnumerator ServerQuery()
    {
        while (!_stopQuery)
        {
            AutoRefresh();

            while (Time.time < _lastRefreshed + 5)
            {
                yield return null;
            }
        }
    }
}


