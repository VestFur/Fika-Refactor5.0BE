using Fika.Core.Main.BaseClasses;
using System;
using System.Collections;
using System.Threading.Tasks;
using Comfort.Common;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Bots;
using EFT.Communications;
using EFT.Counters;
using EFT.Game.Spawning;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using EFT.UI;
using EFT.UI.Screens;
using EFT.Weather;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.FreeCamera;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using JsonType;
using static JsonType.LocationSettings;

namespace Fika.Core.Main.GameMode;

public class ClientGameController : BaseGameController
{
    public ClientGameController(IFikaGame game, EUpdateQueue updateQueue, GameWorld gameWorld, IEftSession session)
        : base(game, updateQueue, gameWorld, session) { }


    public bool ExfiltrationReceived { get; set; }
    public bool HasReceivedLoot { get; set; }
    public bool InteractablesInitialized { get; set; }

    public override IEnumerator WaitForHostInit(int timeBeforeDeployLocal)
    {
        Logger.LogInfo("Waiting for host init");
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_HOST_FINISH_INIT.Localized());

        var client = FikaSingleton<FikaClient>.Instance;
        WaitForEndOfFrame waitForEndOfFrame = new();
        do
        {
            yield return waitForEndOfFrame;
        } while (!client.HostReady);
        LootItems = null;

        Logger.LogInfo("Host init complete");
    }

    public override async Task WaitForHostToStart()
    {
        await base.WaitForHostToStart();

        GameObject startButton = null;
        if (FikaBackendUtils.IsHeadlessRequester || FikaPlugin.Instance.Settings.AnyoneCanStartRaid)
        {
            startButton = CreateStartButton() ?? throw new NullReferenceException("Start button could not be created!");
            if (FikaPlugin.Instance.Settings.DevMode.Value)
            {
                Logger.LogWarning("DevMode is enabled, skipping wait...");
                FikaNotifications.DisplayMessageNotification("DevMode enabled, starting automatically...", iconType: EFT.Communications.ENotificationIconType.Note);
                var fikaClient = FikaSingleton<FikaClient>.Instance ?? throw new NullReferenceException("CreateStartButton::FikaClient was null!");
                InformationPacket devModePacket = new()
                {
                    RequestStart = true
                };
                fikaClient.SendData(ref devModePacket, DeliveryMethod.ReliableOrdered);
            }
        }
        var client = FikaSingleton<FikaClient>.Instance;
        InformationPacket packet = new();
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(250);
        }
        while (!RaidStarted);

        if (startButton != null)
        {
            GameObject.Destroy(startButton);
        }
    }

    public override async Task WaitForOtherPlayersToLoad()
    {
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        var client = FikaSingleton<FikaClient>.Instance;
        var initialPlayerCount = Math.Max(1, networkManager.PlayerAmount);
        // 主机在断线时发送玩家数更新。迟到加入不得
        // 扩大本客户端开始加载时建立的屏障。
        int ExpectedPlayers() => Math.Max(1, Math.Min(initialPlayerCount, networkManager.PlayerAmount));
        int ExpectedHumans() => Math.Max(0, ExpectedPlayers() - (FikaBackendUtils.IsHeadlessGame ? 1 : 0));
        void EnsureConnected()
        {
            if (client.ServerConnection?.ConnectionState != ConnectionState.Connected)
                throw new InvalidOperationException("Connection to the raid host was lost while waiting for players to load.");
        }

#if DEBUG
        Logger.LogWarning("Client: Waiting for coopHandler.AmountOfHumans < expected players, expected: " + ExpectedHumans());
#endif
        // 判定行 (analysis/loading-screen-sync)：同一屏障的客户端一侧。它等待
        // 角色（其他真人玩家），并在第二个循环中等待主机的就绪计数。
        //
        // 结束这次等待的情况，按顺序如下：
        //   * EnsureConnected——传输层（LiteNetLib 自身的线程会在主机主线程忙于加载地图
        //     时仍保持连接及其 ack 存活，因此 "未连接" 是
        //     主机确实消失的唯一证据）；
        //   * LoadSyncGate 给出的上限，在 transit 重载窗口内该上限更长——
        //     一个从不取得进展的已连接主机不能被永远等待下去；
        //   * 长时间的应用层静默会被*报告*（警告），但绝不会自行结束等待。
        // 正是这种区分让一个缓慢的主机得以存活：中止了 2026-10-11
        // 18:58 那次 transit 加入的固定 40 秒上限是一个应用层秒表，而不是存活检查。
        var charactersStartedAt = UnityEngine.Time.realtimeSinceStartup;
        var charactersCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var nextCharacterReport = 0f;
        var silentSeconds = 0;
        var lastResponseCount = client.HostResponseCount;

        string DescribeCharacterWait(int expectedHumans, int silentFor, float elapsed)
        {
            return "stage=characters role=client humans=" + _coopHandler.AmountOfHumans + "/" + expectedHumans
                + " playerAmount=" + networkManager.PlayerAmount + " responses=" + client.HostResponseCount
                + " hostLoaded=" + client.HostLoaded + " hostReady=" + client.HostReady
                + " elapsed=" + elapsed.ToString("0") + "s, " + LoadSync.DescribePeerTraffic()
                + ", " + DescribeLocalProgress() + " (" + LoadSync.DescribeScreen() + ")";
        }

        do
        {
            await Task.Delay(100);
            EnsureConnected();
            var expectedHumans = ExpectedHumans();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                expectedHumans == 0 ? 1f : Math.Min(1f, (float)_coopHandler.AmountOfHumans / expectedHumans));

            if (client.HostResponseCount != lastResponseCount)
            {
                lastResponseCount = client.HostResponseCount;
                silentSeconds = 0;
            }
            else
            {
                silentSeconds++;
            }

            var elapsed = UnityEngine.Time.realtimeSinceStartup - charactersStartedAt;
            if (elapsed >= nextCharacterReport)
            {
                nextCharacterReport = elapsed + 5f;
            }

            if (silentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && silentSeconds % 15 == 0)
            {
                LoadSync.Warn("ClientGameController.WaitForOtherPlayersToLoad",
                    DescribeCharacterWait(expectedHumans, silentSeconds, elapsed)
                    + " => no application-level response for " + silentSeconds + "s while the transport is connected: "
                    + "the host's main thread is most likely loading its own copy of the raid; the wait continues until "
                    + charactersCeiling + "s");
            }

            if (elapsed >= charactersCeiling)
            {
                throw new InvalidOperationException(
                    "This client waited " + charactersCeiling + "s for the other players' characters. "
                    + DescribeCharacterWait(expectedHumans, silentSeconds, elapsed)
                    + " - see the [LoadSync] lines for the host's side of the barrier.");
            }
        } while (_coopHandler.AmountOfHumans < ExpectedHumans());

        InformationPacket packet = new()
        {
            ReadyPlayers = 1
        };

        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
#if DEBUG
        Logger.LogWarning("Client: Waiting for client.ReadyClients < expected players, expected: " + ExpectedPlayers());
#endif
        var readyStartedAt = UnityEngine.Time.realtimeSinceStartup;
        var readyCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var nextReadyReport = 0f;
        var readySilentSeconds = 0;
        var lastReadyResponseCount = client.HostResponseCount;

        string DescribeReadyWait(int expectedPlayers, int silentFor, float elapsed)
        {
            return "stage=ready role=client readyClients=" + client.ReadyClients + "/" + expectedPlayers
                + " playerAmount=" + networkManager.PlayerAmount + " responses=" + client.HostResponseCount
                + " hostLoaded=" + client.HostLoaded + " hostReady=" + client.HostReady
                + " elapsed=" + elapsed.ToString("0") + "s, " + LoadSync.DescribePeerTraffic()
                + ", " + DescribeLocalProgress() + " (" + LoadSync.DescribeScreen() + ")";
        }

        do
        {
            await Task.Delay(100);
            EnsureConnected();
            var expectedPlayers = ExpectedPlayers();
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_OTHER_PLAYERS.Localized(),
                Math.Min(1f, (float)client.ReadyClients / expectedPlayers));

            if (client.HostResponseCount != lastReadyResponseCount)
            {
                lastReadyResponseCount = client.HostResponseCount;
                readySilentSeconds = 0;
            }
            else
            {
                readySilentSeconds++;
            }

            var elapsed = UnityEngine.Time.realtimeSinceStartup - readyStartedAt;
            if (elapsed >= nextReadyReport)
            {
                nextReadyReport = elapsed + 5f;
            }

            if (readySilentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && readySilentSeconds % 15 == 0)
            {
                LoadSync.Warn("ClientGameController.WaitForOtherPlayersToLoad",
                    DescribeReadyWait(expectedPlayers, readySilentSeconds, elapsed)
                    + " => no application-level response for " + readySilentSeconds + "s while the transport is connected; "
                    + "the wait continues until " + readyCeiling + "s");
            }

            if (elapsed >= readyCeiling)
            {
                throw new InvalidOperationException(
                    "This client waited " + readyCeiling + "s for the host's ready barrier. "
                    + DescribeReadyWait(expectedPlayers, readySilentSeconds, elapsed)
                    + " - see the [LoadSync] lines for the host's side of the barrier.");
            }
        } while (client.ReadyClients < ExpectedPlayers());

        var finalPacket = new InformationPacket
        {
            RemoteNetId = client.NetId
        };
        client.SendData(ref finalPacket, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// 本机为自身发布的进度，用于 [LoadSync] 行（"local=92.5"）。
    /// 从不抛异常也从不阻塞：该界面是一次纯托管读取。
    /// </summary>
    private static string DescribeLocalProgress()
    {
        try
        {
            var screen = LoadingScreenUI.Instance;
            return screen == null ? "local=absent (no screen)" : screen.DescribeLocalRow();
        }
        catch (Exception)
        {
            return "local=absent";
        }
    }

    public override async Task GenerateWeathers()
    {
        if (WeatherController.Instance != null)
        {
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_INIT_WEATHER.Localized());
            Logger.LogInfo("Generating and initializing weather...");
            await GetWeather();
            WeatherController.Instance.SetWeatherNodes(WeatherClasses);
        }
    }

    /// <summary>
    /// 从主机获取天气
    /// </summary>
    /// <returns></returns>
    private async Task GetWeather()
    {
        RequestPacket packet = new()
        {
            Type = ERequestSubPacketType.Weather
        };

        var client = FikaSingleton<FikaClient>.Instance;
        client.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        while (!WeatherReady)
        {
            await Task.Delay(1000);
            if (!WeatherReady)
            {
                client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    public override IEnumerator CountdownScreen(Profile profile, string profileId)
    {
        yield return base.CountdownScreen(profile, profileId);
        _localPlayer.PacketSender.Init();
    }

    public override async Task ReceiveSpawnPoint(Profile profile)
    {
        var spawnTogether = CheckSpawnTogether();
        if (!spawnTogether)
        {
            Logger.LogInfo("Using random spawn points!");
            FikaNotifications.DisplayMessageNotification(LocaleUtils.RANDOM_SPAWNPOINTS.Localized(),
                iconType: EFT.Communications.ENotificationIconType.Alert);

            CreateSpawnSystem(profile);
            return;
        }

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_SPAWN_INFO.Localized());

        RequestPacket packet = new()
        {
            Type = ERequestSubPacketType.SpawnPoint
        };
        var client = FikaSingleton<FikaClient>.Instance;

        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(1000);
            if (string.IsNullOrEmpty(InfiltrationPoint))
            {
                await Task.Delay(2000);
            }
        } while (string.IsNullOrEmpty(InfiltrationPoint));

        Logger.LogInfo($"Retrieved infiltration point '{InfiltrationPoint}' from server");
    }

    private bool CheckSpawnTogether()
    {
        // (2026-10-19) Labyrinth：五个密室正是这张地图的全部意义，因此小队在那里从不一起生成
        // ——默认如此，直接加入和 transit 加入都一样。raid 设置仍是显式的
        // 覆盖手段：关闭 'Random Labyrinth Spawns' 会恢复朴素的 "一起生成" 行为，而一个
        // 已经要求分开位置的 raid 设置同样会被遵从。其他每张地图保持
        // 旧的默认值（一起生成，除非设置另有说明）。
        if (string.Equals(RaidSettings.SelectedLocation.Id, "labyrinth", StringComparison.OrdinalIgnoreCase))
        {
            return !FikaPlugin.Instance.Settings.RandomLabyrinthSpawns
                && RaidSettings.PlayersSpawnPlace == EPlayersSpawnPlace.SamePlace;
        }

        return RaidSettings.PlayersSpawnPlace == EPlayersSpawnPlace.SamePlace;
    }

    public override void CreateSpawnSystem(Profile profile)
    {
        _spawnPoints = SpawnPointsCollection.CreateFromScene(new Il2CppSystem.Nullable<Il2CppSystem.DateTime>(DateTimeExtensions.LocalDateTimeFromUnixTime(Location.UnixDateTime)),
                                Location.SpawnPointParams);
        var spawnSafeDistance = (Location.SpawnSafeDistanceMeters > 0) ? Location.SpawnSafeDistanceMeters : 100;
        SpawnSystemSettings settings = new(Location.MinDistToFreePoint, Location.MaxDistToFreePoint, Location.MaxBotPerZone, spawnSafeDistance, Location.NoGroupSpawn, Location.OneTimeSpawn);
        SpawnSystem = SpawnSystemFactory.CreateSpawnSystem(settings, (Il2CppSystem.Func<float>)FikaGlobals.GetApplicationTime, Singleton<GameWorld>.Instance, null, _spawnPoints);
        _spawnPoint = SpawnSystem.SelectSpawnPoint(ESpawnCategory.Player, profile.Info.Side, null, null, null, null, profile.Id);
        InfiltrationPoint = string.IsNullOrEmpty(_spawnPoint.Infiltration) ? "MissingInfiltration" : _spawnPoint.Infiltration;
        ClientSpawnPosition = _spawnPoint.Position;
        ClientSpawnRotation = _spawnPoint.Rotation;
    }

    /// <summary>
    /// 等待主机完成其自身的 raid 初始化（HostLoaded，即主机在其
    /// CoopGame.InitPlayer 末尾设置了 FikaServer.RaidInitialized）。
    ///
    /// 以静默为界，而不是以秒表为界：仍在应答 InformationPackets 的主机
    /// 仍在加载它自己那份 raid，放弃它会中止一次仅仅是慢的加入（
    /// 即那个在固定 40 秒后杀死 2026-10-11 18:58 transit 加入的错误）。停止
    /// 应答的主机就是一个死会话，而客户端会照此报告。
    /// </summary>
    public async Task WaitForHostToLoad()
    {
        var client = FikaSingleton<FikaClient>.Instance;

        Logger.LogInfo("Waiting for host to init");

        InformationPacket packet = new();
        var startedAt = UnityEngine.Time.realtimeSinceStartup;
        var hostInitCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var nextReport = 0f;
        var silentSeconds = 0;
        var lastResponseCount = client.HostResponseCount;

        string DescribeHostInitWait(int silentFor, float elapsed)
        {
            return "stage=host-init role=client hostLoaded=" + client.HostLoaded + " hostReady=" + client.HostReady
                + " responses=" + client.HostResponseCount + " elapsed=" + elapsed.ToString("0")
                + "s, " + LoadSync.DescribePeerTraffic() + ", " + DescribeLocalProgress()
                + " (" + LoadSync.DescribeScreen() + ")";
        }

        do
        {
            _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_WAIT_FOR_HOST_INIT.Localized());

            // 传输层就是存活检查：主线程忙碌的主机会通过 LiteNetLib 自身的线程
            // 保持连接（及其 ack）存活，而死掉的主机不会。
            if (client.ServerConnection?.ConnectionState != ConnectionState.Connected)
            {
                throw new InvalidOperationException(
                    "Connection to the raid host was lost while waiting for its raid initialisation. "
                    + DescribeHostInitWait(silentSeconds, UnityEngine.Time.realtimeSinceStartup - startedAt));
            }

            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);

            if (client.HostResponseCount != lastResponseCount)
            {
                lastResponseCount = client.HostResponseCount;
                silentSeconds = 0;
            }
            else
            {
                silentSeconds++;
            }

            var elapsed = UnityEngine.Time.realtimeSinceStartup - startedAt;
            if (elapsed >= nextReport)
            {
                nextReport = elapsed + 5f;
            }

            if (silentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && silentSeconds % 15 == 0)
            {
                LoadSync.Warn("ClientGameController.WaitForHostToLoad",
                    DescribeHostInitWait(silentSeconds, elapsed)
                    + " => no application-level response for " + silentSeconds + "s while the transport is connected; "
                    + "the wait continues until " + hostInitCeiling + "s");
            }

            if (elapsed >= hostInitCeiling)
            {
                throw new InvalidOperationException(
                    "This client waited " + hostInitCeiling + "s for the host's raid initialisation. "
                    + DescribeHostInitWait(silentSeconds, elapsed) + " - see the [LoadSync] lines.");
            }

            await Task.Delay(1000);
        } while (!client.HostLoaded);

    }

    public override async Task InitializeLoot(LocationSettings.Location location)
    {
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_LOOT.Localized());
        await RetrieveLootFromServer(true, location);
        location.Loot = LootItems;
    }

    private async Task RetrieveLootFromServer(bool register,
        LocationSettings.Location location)
    {
        var client = FikaSingleton<FikaClient>.Instance;
        WorldLootPacket packet = new()
        {
            Data = []
        };
        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(TimeSpan.FromSeconds(1));
            // WaitForHostInit 会清除 LootItems，因此过期数据无法被复用。
            // 在主机描述符到达之前它保持为 null。
            if (!HasReceivedLoot && (LootItems == null || LootItems.Count < 1))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        } while (!HasReceivedLoot);

        if (register)
        {
            RegisterPlayerRequest request = new(0, location.Id, 0);
            await FikaRequestHandler.RegisterPlayer(request);
        }
    }

    public async Task InitExfils()
    {
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_EXFIL_DATA.Localized());
        var client = FikaSingleton<FikaClient>.Instance;
        RequestPacket request = new()
        {
            Type = ERequestSubPacketType.Exfiltration
        };

        var attempts = 0;
        do
        {
            client.SendData(ref request, DeliveryMethod.ReliableOrdered);
            attempts++;
            if (attempts % 10 == 0)
            {
                // 卡住的撤离请求过去是看不见的：客户端就那样
                // 永远停留在 "正在获取撤离数据" 界面上。
                FikaGlobals.LogWarning($"InitExfils: still waiting for the exfiltration data " +
                    $"after {attempts} requests (controller={(ExfiltrationController.Instance == null ? "null" : "ready")})");
            }

            await Task.Delay(1000);
        } while (!ExfiltrationReceived);
    }

    public async Task InitInteractables()
    {
        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_RETRIEVE_INTERACTABLES.Localized());
        var client = FikaSingleton<FikaClient>.Instance;
        InteractableInitPacket packet = new(true);

        do
        {
            client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            await Task.Delay(1000);
            if (!InteractablesInitialized)
            {
                await Task.Delay(2000);
            }
        } while (!InteractablesInitialized);
    }

    public override void SetupEventsAndExfils(Player player)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            throw new NullReferenceException("Could not find CoopGame");
        }

        coopGame.GameTimer.Start(GameTime.ToNative(), SessionTime.ToNative());
        coopGame.Spawn();

        var skills = coopGame.Profile.Skills.Skills;
        var skillsLength = skills.Length;
        for (var i = 0; i < skillsLength; i++)
        {
            skills[i].SetPointsEarnedInSession(0f, false);
        }

        coopGame.Profile.Info.EntryPoint = InfiltrationPoint;
        Logger.LogInfo("[CLIENT] SpawnPosition: " + ClientSpawnPosition + ", InfiltrationPoint: " + InfiltrationPoint);

        var exfilController = ExfiltrationController.Instance;
        var isScav = player.Side is EPlayerSide.Savage;
        ExfiltrationPoint[] exfilPoints;
        SecretExfiltrationPoint[] secretExfilPoints;
        ExfiltrationController.Instance.InitSecretExfils(player);

        if (isScav)
        {
            exfilController.ScavExfiltrationClaim(player.Position, player.ProfileId, player.Profile.FenceInfo.AvailableExitsCount);
            var mask = exfilController.GetScavExfiltrationMask(player.ProfileId);
            exfilPoints = exfilController.ScavExfiltrationClaim(mask, player.ProfileId);
            secretExfilPoints = ExfiltrationController.Instance.GetScavSecretExits();
        }
        else
        {
            exfilPoints = exfilController.EligiblePoints(coopGame.Profile);
            secretExfilPoints = ExfiltrationController.Instance.SecretEligiblePoints();
        }

        // 游戏的 ExtractionTimersPanel 会在 SetTime 内部启动其滑动/变色协程，
        // 而 Unity 在已关闭（未激活）的面板上会拒绝这样做。只在它存活时驱动它。
        if (FikaUiPanels.CanDrive(coopGame.GameUi.TimerPanel, "ClientGameController.Spawn.TimerPanel.SetTime"))
        {
            coopGame.GameUi.TimerPanel.SetTime(DateTimeExtensions.UtcNow,
                coopGame.Profile.Info.Side, coopGame.GameTimer.EscapeTimeSeconds(),
                exfilPoints, secretExfilPoints);
        }

        if (FikaWorldControllers.Find<ClientClasses.ClientTransitController>(Singleton<GameWorld>.Instance.TransitController) is { } transitController)
        {
            transitController.Init();
        }

        if (Location.EventTrapsData != null)
        {
            _gameWorld.SyncModule = new();
        }

        ExfilManager.Run(exfilPoints, secretExfilPoints);

        coopGame.Status = GameStatus.Started;

        ConsoleScreen.ApplyStartCommands();
    }

    public override void Extract(FikaPlayer player, ExfiltrationPoint exfiltrationPoint, TransitPoint transitPoint = null)
    {
        if (_fikaGame is not CoopGame coopGame)
        {
            throw new NullReferenceException("CoopGame was missing");
        }

        if (coopGame.ExtractedPlayers.Contains(player.NetId)) return;

        var preloaderUI = Singleton<PreloaderUI>.Instance;
        _localTriggerZones = [.. player.TriggerZones];

        player.ClientMovementContext.SetGravity(false);
        var position = player.Position;
        position.y += 500f;
        player.Teleport(position);

        if (coopGame.ExitStatus == ExitStatus.MissingInAction)
        {
            FikaNotifications.DisplayMessageNotification(LocaleUtils.PLAYER_MIA.Localized(), iconType: EFT.Communications.ENotificationIconType.Alert, textColor: new Il2CppSystem.Nullable<Color>(Color.red));
        }

        if (player.FikaQuests is ClientQuestController clientQuestController)
        {
            clientQuestController.ToggleSend(false);
        }

        if (player.FikaQuests is ClientSharedQuestController sharedQuestController)
        {
            sharedQuestController.ToggleQuestSharing(false);
        }

        // 原生统计基类会把生命与代谢经验入队直到撤离；
        // 在 Runner 和结果评估之前消费它。
        player.StatisticsManager?.TryCast<HealthStatisticsManager>()?.ConsumeExperience();

#if !DEBUG
        var matchEndConfig = Singleton<GlobalConfiguration>.Instance.Experience.MatchEnd;
        if (player.Profile.EftStats.SessionCounters.GetAllInt(NativeCounters.ExperienceTags()) < matchEndConfig.SurvivedExpRequirement && coopGame.PastTime < matchEndConfig.SurvivedTimeRequirement)
        {
            coopGame.ExitStatus = ExitStatus.Runner;
        }
#endif

        if (exfiltrationPoint != null)
        {
            exfiltrationPoint.Disable();

            if (exfiltrationPoint.HasRequirements && exfiltrationPoint.TransferItemRequirement?.Met(player, exfiltrationPoint) == true && player.IsYourPlayer)
            {
                // 似乎已由 SPT 处理，因此我们只做可见的添加
                player.Profile.EftStats.SessionCounters.AddDouble(0.2d, NativeCounters.ExitStanding());
            }
        }

        if (player.Side == EPlayerSide.Savage)
        {
            // 似乎已由 SPT 处理，因此我们只做可见的添加
            player.Profile.EftStats.SessionCounters.AddDouble(0.01d, NativeCounters.ExitStanding());
        }

        var transitController = Singleton<GameWorld>.Instance.TransitController;
        if (transitController != null && transitPoint != null)
        {
            if (transitController.alreadyTransits.TryGetValue(player.ProfileId, out var data))
            {
                coopGame.ExitStatus = ExitStatus.Transit;
                coopGame.ExitLocation = transitPoint.parameters.name;
                FikaBackendUtils.IsTransit = true;
            }
            else
            {
                Logger.LogError("Exit was transit, but could not find alreadyTransits data");
            }
        }

        if (_coopHandler != null)
        {
            try // This is to allow clients to extract if they lose connection
            {
                FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.ClientExtract,
                    ClientExtract.FromValue(player.NetId), true);
            }
            catch
            {

            }

            var fikaPlayer = player;
            coopGame.ExtractedPlayers.Add(fikaPlayer.NetId);
            _coopHandler.ExtractedPlayers.Add(fikaPlayer.NetId);
            _coopHandler.Players.Remove(fikaPlayer.NetId);

            preloaderUI.StartBlackScreenShow(2f, 2f, (System.Action)(() => preloaderUI.FadeBlackScreen(2f, -2f)));

            player.ActiveHealthController.SetDamageCoeff(0);
            player.ActiveHealthController.DamageMultiplier = 0;
            player.ActiveHealthController.DisableMetabolism();
            player.ActiveHealthController.PauseAllEffects();

            _extractRoutine = coopGame.StartCoroutine(ExtractRoutine(player, coopGame));

            // 防止玩家在撤离后继续搜刮
            EftScreenManager.Instance.CloseAllScreensForced();

            // 销毁会话计时器
            if (TimeManager != null)
            {
                UnityEngine.Object.Destroy(TimeManager);
            }
            if (FikaUiPanels.CanDrive(coopGame.GameUi.TimerPanel, "ClientGameController.Extract.TimerPanel.Close"))
            {
                coopGame.GameUi.TimerPanel.Close();
            }

            if (FikaPlugin.Instance.Settings.AutoExtract.Value || FikaBackendUtils.IsTransit)
            {
                coopGame.Stop(_localPlayer.ProfileId, coopGame.ExitStatus, _localPlayer.ActiveHealthController.IsAlive ? coopGame.ExitLocation : null, 0);
            }
        }
        else
        {
            throw new NullReferenceException("Extract: CoopHandler was null!");
        }
    }

    public override async Task StartBotSystemsAndCountdown(BotControllerSettings controllerSettings, GameWorld gameWorld)
    {
        LoadingScreenUI.Instance.UpdateAndBroadcast(80f);

        if (Location.Id == "laboratory")
        {
            Logger.LogInfo("Location is 'Laboratory', skipping weather generation");
            Season = ESeason.Summer;
            FikaBackendUtils.CustomRaidSettings.UseCustomWeather = false;
        }
        else
        {
            await GenerateWeathers();
        }

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());

        gameWorld.RegisterRestrictableZones();
        gameWorld.RegisterBorderZones();

#if DEBUG
        Logger.LogWarning("Starting " + nameof(BaseGameController.WaitForOtherPlayersToLoad));
#endif
        await WaitForOtherPlayersToLoad();

        _abstractGame.SetMatchmakerStatus(LocaleUtils.UI_FINISHING_RAID_INIT.Localized());
        Logger.LogInfo("All players are loaded, continuing...");

        // 将 FreeCamController 添加到 GameWorld 游戏对象
        var freeCamController = gameWorld.gameObject.AddComponent<FreeCameraController>();
        FikaSingleton<FreeCameraController>.Create(freeCamController);

        await SetupRaidCode();

        LoadingScreenUI.Instance.UpdateAndBroadcast(85f);

        Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal = Math.Max(Singleton<GlobalConfiguration>.Instance.TimeBeforeDeployLocal, 3);
    }
}

