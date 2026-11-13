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

        // Ensure the IsSpectator field is reset every time the matchmaker UI is created
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

            // Sort availableHeadlesses alphabetically by Alias
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

            // A scav raid never opens the offline raid settings screen, so its
            // TimeAndWeatherSettings stay at the struct default - hour 0, which
            // CoopGame treats as a real custom hour and starts every scav raid at
            // midnight. -1 means "keep the world clock" (a joining player still
            // receives the host's own choice from the server instead).
            if (RaidSettings.IsScav && RaidSettings.TimeAndWeatherSettings.HourOfDay == 0)
            {
                var scavTimeAndWeather = RaidSettings.TimeAndWeatherSettings;
                scavTimeAndWeather.HourOfDay = -1;
                RaidSettings.TimeAndWeatherSettings = scavTimeAndWeather;
                FikaGlobals.LogInfo("Scav raid: no raid time was configured, following the world clock");
            }

            // The application is only needed for the headless path below. It used
            // to be fetched here with a hard cast, which throws
            // "Unable to cast ... to TarkovApplication" while the game is between
            // screens and aborted the whole lobby action - the Fika match was then
            // never created and the raid could not start.
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);

            if (!_fikaMatchMakerUi.DedicatedToggle.Value.isOn)
            {
                if (FikaPlugin.Instance.Settings.ForceIP.Value != "")
                {
                    // We need to handle DNS entries as well
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

    public static Task<bool> JoinMatch(string profileId, string serverId, Button button, bool reconnect) =>
        JoinMatchCore(profileId, serverId, button, reconnect, false, CancellationToken.None);

    public static Task<bool> JoinMatch(string profileId, string serverId, Button button, bool reconnect,
        CancellationToken cancellationToken) =>
        JoinMatchCore(profileId, serverId, button, reconnect, false, cancellationToken);

    /// <summary>
    /// Join implementation. <paramref name="scavLateJoin"/> marks a player scav
    /// entering a raid that already started: it uses its own connection key, is
    /// spawned as a scav and stays outside the host's group.
    /// </summary>
    public static async Task<bool> JoinMatchCore(string profileId, string serverId, Button button, bool reconnect,
        bool scavLateJoin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (button != null)
        {
            button.enabled = false;
        }

        FikaBackendUtils.IsReconnect = reconnect;
        FikaBackendUtils.IsScavLateJoin = scavLateJoin;
        FikaNotifications.DisplayMessageNotification(LocaleUtils.CONNECTING_TO_SESSION.Localized(),
            iconType: EFT.Communications.ENotificationIconType.EntryPoint);
        using var pingingClient = await NetManagerUtils.CreatePingingClient();
        cancellationToken.ThrowIfCancellationRequested();

        if (pingingClient.Init(serverId))
        {
            FikaGlobals.LogInfo("Attempting to connect to host session...");
            var knockMessage = FikaBackendUtils.ServerGuid.ToString();
            // A player scav entering a running raid is allowed through, so the
            // host must answer the "you may connect" reply instead of the
            // "raid already started" one.
            var success = await pingingClient.AttemptToPingHost(knockMessage, reconnect || scavLateJoin, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!success)
            {
                Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                LocaleUtils.UI_UNABLE_TO_CONNECT.Localized(),
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
            Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                LocaleUtils.UI_PINGER_START_FAIL.Localized(),
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

    private void RefreshUI()
    {
        if (_matches == null)
        {
            // not initialized
            return;
        }

        if (_matchesListObjects != null)
        {
            // cleanup old objects
            foreach (var match in _matchesListObjects)
            {
                Destroy(match);
            }
        }

        // create lobby listings
        for (var i = 0; i < _matches.Length; ++i)
        {
            var entry = _matches[i];

            if (entry.ServerId == _profileId)
            {
                continue;
            }

            // server object
            var server = Instantiate(_fikaMatchMakerUi.RaidGroupDefaultToClone.Value, _fikaMatchMakerUi.RaidGroupDefaultToClone.Value.transform.parent);
            server.SetActive(true);
            _matchesListObjects.Add(server);

            server.name = entry.ServerId;

            var localPlayerInRaid = false;
            var localPlayerDead = false;
            foreach ((MongoID profileId, var player) in entry.Players)
            {
                if (profileId == _profileId)
                {
                    localPlayerInRaid = true;
                    localPlayerDead = player;
                }
            }

            // A player scav may walk into a PMC raid that is already running.
            // Such a join adopts the host's location and world clock on the fly
            // and is spawned as a scav, so it is never part of the host's group.
            var scavLateJoin = RaidSettings.Side == ESideType.Savage && entry.Side == ESideType.Pmc;

            // player label
            var playerLabel = GameObject.Find("PlayerLabel");
            playerLabel.name = "PlayerLabel" + i;
            var sessionName = entry.HostUsername;
            playerLabel.GetComponentInChildren<TextMeshProUGUI>().SetText(sessionName);

            // players count label
            var playerCountLabel = GameObject.Find("PlayerCountLabel");
            playerCountLabel.name = "PlayerCountLabel" + i;
            var playerCount = entry.IsHeadless ? entry.PlayerCount - 1 : entry.PlayerCount;
            playerCountLabel.GetComponentInChildren<TextMeshProUGUI>().SetFormattedText("{0}", playerCount);

            // player join button
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
                var success = await JoinMatchCore(_profileId, server.name, button, localPlayerInRaid, scavLateJoin,
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
                    if (!localPlayerInRaid)
                    {
                        if (scavLateJoin)
                        {
                            // Player scavs join a running raid by design.
                            tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                            tooltipArea.enabled = true;
                            tooltipArea.SetMessageText(LocaleUtils.UI_JOIN_RAID.Localized());
                            break;
                        }

                        button.enabled = false;
                        if (image != null)
                        {
                            image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                        }

                        tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                        tooltipArea.enabled = true;
                        tooltipArea.SetMessageText(LocaleUtils.UI_RAID_IN_PROGRESS.Localized());
                    }
                    else
                    {
                        if (!localPlayerDead)
                        {
                            tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                            tooltipArea.enabled = true;
                            tooltipArea.SetMessageText(LocaleUtils.UI_REJOIN_RAID.Localized());
                        }
                        else
                        {
                            button.enabled = false;
                            if (image != null)
                            {
                                image.color = new(0.5f, image.color.g / 2, image.color.b / 2, 0.75f);
                            }

                            tooltipArea = joinButton.GetOrAddComponent<HoverTooltipArea>();
                            tooltipArea.enabled = true;
                            tooltipArea.SetMessageText(LocaleUtils.UI_CANNOT_REJOIN_RAID_DIED.Localized());
                        }
                    }
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


