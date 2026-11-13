using EFT.Communications;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Models.Presence;
using HarmonyLib;
using JsonType;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Custom;

public class MainMenuUIScript : MonoBehaviour
{
    public MainMenuUIScript(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUIScript() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUIScript>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public static bool Exist
    {
        get
        {
            return _instance != null;
        }
    }

    public static MainMenuUIScript Instance
    {
        get
        {
            return _instance;
        }
    }

    public bool JoinInProgress
    {
        get => _joinInProgress;
        set
        {
            lock (_joinInProgressLock)
            {
                _joinInProgress = value;
            }
        }
    }

    private static MainMenuUIScript _instance;

    private Coroutine _queryRoutine;
    private MainMenuUI _mainMenuUI;
    private GameObject _playerTemplate;
    private GameObject _userInterface;
    private List<GameObject> _players;
    private DateTime _lastRefresh;
    private DateTime _lastSet;
    private RectTransform _transformToScale;
    private IMatchmakerSession<RaidSettings> _backendSession;
    private bool _initialized;

    private bool _joinInProgress;
    private readonly object _joinInProgressLock = new();

    private const int _minSecondsToWait = 2;

    // SPT 5's menu canvas applies a larger scale factor than the Mono menu
    // used by the original Fika prefab. Keep the user setting as a relative
    // scale, but apply the corrected prefab baseline so existing config files
    // (which contain the old default of 1.0) are migrated automatically.
    private static float GetOnlinePlayersScale()
    {
        var configured = FikaPlugin.Instance.Settings.OnlinePlayersScale.Value;
        return Math.Clamp(configured * 0.75f, 0.4f, 1.0f);
    }

    private DateTime BackendTime
    {
        get
        {
            if (_backendSession == null)
            {
                return DateTime.MinValue;
            }
            return _backendSession.GetCurrentLocationTime.ToManaged();
        }
    }

    private DateTime StaticTime
    {
        get
        {
            return new DateTime(2016, 8, 4, 15, 28, 0);
        }
    }

    protected void Start() => Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, Initialize());

    private IEnumerator Initialize()
    {
        _instance = this;
        _players = [];
        _lastRefresh = DateTime.Now;
        _lastSet = DateTime.Now;
        while (!FikaPlugin.Instance.LocalesLoaded)
        {
            if (FikaPlugin.Instance.InitializationError != null) yield break;
            yield return null;
        }
        if (TarkovApplication.Exist(out var tarkovApplication) && tarkovApplication.Session != null)
        {
            _backendSession = tarkovApplication.Session;
        }
        CreateMainMenuUI();
        _initialized = true;
        OnEnable();
    }

    protected void OnEnable()
    {
        // Unity calls OnEnable before Start, when config/UI may not exist yet.
        if (!_initialized) return;
        if (!FikaPlugin.Instance.Settings.EnableOnlinePlayers.Value)
        {
            if (_userInterface != null)
            {
                _userInterface.SetActive(false);
            }
            return;
        }

        if (_userInterface != null)
        {
            _userInterface.SetActive(true);
        }

        if (_transformToScale != null)
        {
            var scale = GetOnlinePlayersScale();
            _transformToScale.localScale = new(scale, scale, scale);
        }
        _queryRoutine = Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, QueryPlayers());
    }

    protected void OnDisable()
    {
        if (_queryRoutine != null)
        {
            StopCoroutine(_queryRoutine);
            _queryRoutine = null;
        }
    }

    private void CreateMainMenuUI()
    {
        var mainMenuUIPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.MainMenuUI);
        var mainMenuUI = GameObject.Instantiate(mainMenuUIPrefab);
        _mainMenuUI = mainMenuUI.GetComponent<MainMenuUI>();
        _playerTemplate = _mainMenuUI.PlayerTemplate.Value;
        var newParent = Singleton<CommonUI>.Instance.MenuScreen.gameObject.transform;
        mainMenuUI.transform.SetParent(newParent, false);

        var mainMenuUITransform = _mainMenuUI.gameObject.transform;
        var objectToAttach = mainMenuUITransform.GetChild(0).GetChild(0).gameObject;
        _userInterface = objectToAttach;
        _transformToScale = objectToAttach.RectTransform();
        var scale = GetOnlinePlayersScale();
        _transformToScale.localScale = new(scale, scale, scale);
        objectToAttach.AddComponent<UIDragComponent>().Init(_transformToScale, true);
        _mainMenuUI.RefreshButton.Value.onClick.AddListener(ManualRefresh);

    }

    private void ManualRefresh()
    {
        if ((DateTime.Now - _lastRefresh).TotalSeconds >= 5)
        {
            _lastRefresh = DateTime.Now;
            ClearAndQueryPlayers();
        }
    }

    private IEnumerator QueryPlayers()
    {
        WaitForSeconds waitForSeconds = new(10);
        while (true)
        {
            yield return null;
            ClearAndQueryPlayers();
            yield return waitForSeconds;
        }
    }

    private void ClearAndQueryPlayers()
    {
        foreach (var item in _players)
        {
            GameObject.Destroy(item);
        }
        _players.Clear();

        var response = FikaRequestHandler.GetPlayerPresences();
        _mainMenuUI.UpdateLabel(response.Length);
        SetupPlayers(ref response);
    }

    private string ConvertToTime(EDateTime dateTime, bool staticTime)
    {
        if (staticTime)
        {
            var staticDate = StaticTime;
            return dateTime == EDateTime.CURR ? staticDate.ToString("HH:mm:ss") : staticDate.AddHours(-12).ToString("HH:mm:ss");
        }

        var backendTime = BackendTime;
        if (backendTime == DateTime.MinValue)
        {
            return "ERROR";
        }
        return dateTime == EDateTime.CURR ? backendTime.ToString("HH:mm:ss") : backendTime.AddHours(-12).ToString("HH:mm:ss");
    }

    private bool IsStaticTimeLocation(string location)
    {
        return location is "factory4_day" or "factory4_night" or "laboratory";
    }

    private void SetupPlayers(ref FikaPlayerPresence[] responses)
    {
        foreach (var presence in responses)
        {
            var newPlayer = GameObject.Instantiate(_playerTemplate, _playerTemplate.transform.parent);
            var mainMenuUIPlayer = newPlayer.GetComponent<MainMenuUIPlayer>();
            mainMenuUIPlayer.SetActivity(presence.Nickname, presence.Level, presence.Activity);
            if (presence.Activity is EFikaPlayerPresence.IN_RAID && presence.RaidInformation.HasValue)
            {
                var information = presence.RaidInformation.Value;
#if DEBUG
                FikaGlobals.LogInfo($"Got presense: {information.Started}, {information.MatchId}");
#endif
                var side = information.Side == ESideType.Pmc ? "RaidSidePmc".Localized() : "RaidSideScav".Localized();
                var time = ConvertToTime(information.Time, IsStaticTimeLocation(information.Location));
                var tooltip = newPlayer.AddComponent<HoverTooltipArea>();
                tooltip.enabled = true;
                tooltip.SetMessageText(string.Format(LocaleUtils.UI_MMUI_RAID_DETAILS.Localized(), side,
                    ColorizeText(EColor.BLUE, information.Location.Localized()),
                    BoldText(time)));

                if (!information.Started)
                {
                    var buttonGo = mainMenuUIPlayer.JoinButton.Value.transform.parent.gameObject;
                    buttonGo.SetActive(true);
                    var tooltip2 = buttonGo.AddComponent<HoverTooltipArea>();
                    tooltip2.enabled = true;
                    tooltip2.SetMessageText(LocaleUtils.UI_JOIN_RAID.Localized());
                    mainMenuUIPlayer.JoinButton.Value.onClick.AddListener(async () =>
                    {
                        if (JoinInProgress)
                        {
                            return;
                        }

                        JoinInProgress = true;

                        if (!TarkovApplication.Exist(out var tarkovApplication))
                        {
                            FikaGlobals.LogError("OnFikaStartRaid: Could not find TarkovApplication");
                            JoinInProgress = false;
                            return;
                        }

                        var session = tarkovApplication.Session;
                        if (session == null)
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                "Session was null when starting the raid");
                            FikaGlobals.LogError("Session was null when starting the raid");
                            JoinInProgress = false;
                            return;
                        }

                        FikaBackendUtils.Profile = session.Profile;

                        var location = session.LocationSettings.locations.Values.AsManaged()
                            .FirstOrDefault(l => l.Id == information.Location);

                        if (location == null)
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                $"Failed to find location {information.Location}");
                            FikaGlobals.LogError($"Failed to find location {information.Location}");
                            JoinInProgress = false;
                            return;
                        }
                        var mmc = tarkovApplication._menuOperation;

#if !DEBUG
                        var access = location.GetAccessRequirements(EGameMode.Pve, JsonType.LocationSettings.Location.EAccessUsageType.Menu);
                        if (access != null && access.Count > 0 && information.Side is ESideType.Pmc)
                        {
                            var playerLevel = session.Profile.Info.Level;
                            if (playerLevel >= location.MinPlayerLvlAccessKeys)
                            {
                                var playerItems = mmc.InventoryController.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                                if (!LocationAccessRequirementsUtility.HasAnyValidAccessRequirement(playerItems, access,
                                    (Il2CppSystem.Func<EFT.InventoryLogic.Item, bool>)(item => mmc.InventoryController.Examined(item))))
                                {
                                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.MISSING_KEY_FOR_LOCATION.Localized(),
                                        string.Join(", ", access.Keys.AsManaged().Select(key => $"{key} Name".Localized()))),
                                        iconType: EFT.Communications.ENotificationIconType.Alert);
                                    JoinInProgress = false;
                                    return;
                                }
                            }
                        }
#endif

                        FikaNotifications.DisplayMessageNotification(LocaleUtils.CONNECTING_TO_SESSION.Localized(),
                            iconType: EFT.Communications.ENotificationIconType.EntryPoint);
                        using var pingingClient = await NetManagerUtils.CreatePingingClient();

                        if (pingingClient.Init(information.MatchId))
                        {
                            FikaGlobals.LogInfo("Attempting to connect to host session...");
                            var knockMessage = FikaBackendUtils.ServerGuid.ToString();
                            var success = await pingingClient.AttemptToPingHost(knockMessage, false);

                            if (!success)
                            {
                                Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                                LocaleUtils.UI_UNABLE_TO_CONNECT.Localized(),
                                ErrorScreen.EButtonType.OkButton, 10f);
                                JoinInProgress = false;
                                return;
                            }
                        }
                        else
                        {
                            Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                                LocaleUtils.UI_PINGER_START_FAIL.Localized(),
                                ErrorScreen.EButtonType.OkButton, 10f);
                            JoinInProgress = false;
                            return;
                        }

                        if (FikaBackendUtils.JoinMatch(FikaBackendUtils.Profile.ProfileId, information.MatchId, out var result, out var errorMessage))
                        {
                            FikaBackendUtils.GroupId = result.ServerId;
                            FikaBackendUtils.ClientType = EClientType.Client;

                            AddPlayerRequest data = new(FikaBackendUtils.GroupId, FikaBackendUtils.Profile.ProfileId, FikaBackendUtils.IsSpectator);
                            FikaRequestHandler.UpdateAddPlayer(data);

                            // -1 keeps the world clock; a default struct here would
                            // mean hour 0 (midnight) and force every joined raid to
                            // night. The host's own choice arrives from the server a
                            // moment later and overwrites this.
                            var hostTimeAndWeather = new TimeAndWeatherSettings
                            {
                                HourOfDay = -1,
                                TimeFlowType = ETimeFlowType.x1
                            };

                            RaidSettings raidSettings = new()
                            {
                                Side = information.Side,
                                PlayersSpawnPlace = default,
                                MetabolismDisabled = default,
                                BotSettings = default,
                                WavesSettings = default,
                                TimeAndWeatherSettings = hostTimeAndWeather,
                                SelectedDateTime = information.Time,
                                SelectedLocation = location,
                                isInTransition = false,
                                RaidMode = ERaidMode.Local,
                                IsPveOffline = true,
                                OnlinePveRaid = false
                            };

                            FikaBackendUtils.IsScav = raidSettings.IsScav;

                            var controller = mmc.Matchmaker.MatchmakerPlayersController;

                            if (raidSettings.Side is ESideType.Savage && ScavBlocked(session, out var minutesLeft))
                            {
                                Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                    string.Format(LocaleUtils.UI_SCAV_NOT_READY.Localized(), minutesLeft.ToString("F0")));
                                JoinInProgress = false;
                                return;
                            }

                            tarkovApplication._raidSettings = raidSettings;
                            controller.MatchingProgress.SetStartTime(DateTimeExtensions.Now);

                            try
                            {
                                await tarkovApplication.LocalGameMatching(raidSettings.TimeAndWeatherSettings).AsManaged();
                            }
                            catch (Exception ex)
                            {
                                Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                    $"Exception caught during raid init: {ex.Message}", (Il2CppSystem.Action)Application.Quit);
                                JoinInProgress = false;
                            }
                        }
                        else
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING", errorMessage, null);
                        }

                        JoinInProgress = false;
                    });
                }
            }
            newPlayer.SetActive(true);
            _players.Add(newPlayer);
        }
    }

    private bool ScavBlocked(IEftSession session, out double remainingMinutes)
    {
        var utcNowUnixInt = DateTimeExtensions.UtcNowUnixInt;
        var diffSeconds = session.ProfileOfPet.Info.SavageLockTime - (double)utcNowUnixInt;

        if (diffSeconds > 0.0d)
        {
            remainingMinutes = diffSeconds / 60.0d;
            return true;
        }

        remainingMinutes = 0.0d;
        return false;
    }

    public void UpdatePresence(EFikaPlayerPresence presence)
    {
        // Prevent spamming going back and forth to the main menu causing server lag for no reason
        if ((DateTime.Now - _lastSet).TotalSeconds < _minSecondsToWait)
        {
            return;
        }

        _lastSet = DateTime.Now;
        FikaSetPresence data = new(presence);
        FikaRequestHandler.SetPresence(data);
    }
}


