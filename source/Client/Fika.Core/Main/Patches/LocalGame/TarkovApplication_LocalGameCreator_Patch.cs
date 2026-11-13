using Diz.Resources;
using EFT.Ballistics;
using EFT.Communications;
using EFT.UI.Matchmaker;
using EFT.Utilities;
using JsonType;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.UI.Custom;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LocalGame;

/// <summary>
/// 创建者：Paulov
/// Paulov：改为覆盖并使用我们自己的 CoopGame 实例
/// </summary>
public sealed class TarkovApplication_LocalGameCreator_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication)
            .GetMethod(nameof(TarkovApplication.LocalGameCreate));
    }

    [PatchPrefix]
    public static bool Prefix(ref Il2CppSystem.Threading.Tasks.Task __result, TarkovApplication __instance, TimeAndWeatherSettings timeAndWeather, ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig, GameWorld gameWorld)
    {
        

        // 独立的 Headless 插件在专用主机上拥有 LocalGameCreate。
        // 如果 Harmony 在启用 Headless 自己的补丁时仍保留我们的 prefix，
        // 就不要让普通的、由玩家拥有的游戏工厂与它竞争。
        if (FikaBackendUtils.IsHeadless) return true;

        // 失败的结果操作绝不能消耗上一场 raid 的
        // Scav 描述符。启动任何新 raid 都会使那份快照失效，
        // 包括下一场 raid 是原生单人会话的情况。
        Main.Patches.Overrides.RaidEndProfile.Clear();
        if (FikaBackendUtils.ClientType == EClientType.None)
        {
            // AP (analysis/nonfika-raid-inertness)：这是决定性的时刻 —— 现在启动的这场 raid
            // 是原生本地游戏（不带 Fika 的地图选择界面），因此移植层必须
            // 在它再次创建属于自己的游戏之前一直置身事外。每次状态转换只上报一次；
            // 该行在游戏日志中是 [Inert] raid kind=native source=...
            FikaRaidGate.NativeRaidStarted("LocalGameCreate with clientType=None");
            return true;
        }
#if DEBUG
        Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Prefix");

#endif
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => CreateFikaGame(__instance, timeAndWeather, __instance._raidSettings,
            __instance._inputTree, __instance._localGameDateTime, __instance._fixedDeltaTime,
            metricsEvents, metricsConfig, gameWorld, __instance._menuOperation,
            __instance._unsubscriber, __instance.BundleLock)));
        return false;
    }

    /// <summary>
    /// 合作 raid 启动的入口点。会话建立的任何失败都会在这里被转成
    /// 一次干净的终止：原因连同完整堆栈写入 BepInEx/Fika-diagnostics.log，
    /// 半创建状态的会话（客户端、加载界面）被拆除，好让玩家
    /// 落在一个可用的主菜单上，并用一个可读的错误界面替代原来那个干巴巴的
    /// "Object reference not set to an instance of an object." 对话框。
    /// </summary>
    public static async Task CreateFikaGame(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather,
        RaidSettings raidSettings, InputTree inputTree, GameDateTime localGameDateTime, float fixedDeltaTime,
        ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig,
        GameWorld gameWorld, MainMenuShowOperation menuOperation,
        CompositeDisposable unsubscriber, BundleLock bundleLock)
    {
        var isServer = FikaBackendUtils.IsServer;
        
        try
        {
            await CreateFikaGameCore(instance, timeAndWeather, raidSettings, inputTree, localGameDateTime,
                fixedDeltaTime, metricsEvents, metricsConfig, gameWorld, menuOperation, unsubscriber, bundleLock);
            
        }
        catch (Exception exception)
        {
            
            FikaRaidGate.Disarm("the Fika game creation failed");
            FikaBackendUtils.MarkJoinFailed(exception.Message, exception);
            NetManagerUtils.AbortFailedJoin(isServer, "raid start failed: " + exception.Message);

            try
            {
                Singleton<PreloaderUI>.Instance.ShowErrorScreen("FIKA RAID START FAILED",
                    exception.Message + "\n\nSee BepInEx/LogOutput.log for the full stack trace.");
            }
            catch (Exception screenException)
            {
                
            }

            throw;
        }
    }

    private static async Task CreateFikaGameCore(TarkovApplication instance, TimeAndWeatherSettings timeAndWeather,
        RaidSettings raidSettings, InputTree inputTree, GameDateTime localGameDateTime, float fixedDeltaTime,
        ClientMetricsEvents metricsEvents, ClientMetricsConfig metricsConfig,
        GameWorld gameWorld, MainMenuShowOperation menuOperation,
        CompositeDisposable unsubscriber, BundleLock bundleLock)
    {
        var isServer = FikaBackendUtils.IsServer;
        // AP (analysis/nonfika-raid-inertness)：唯一知道"我们的 raid 真的开始了"的地方。
        // 所有在 raid 内动手之前会询问 FikaRaidGate.Skip 的代码，都由这一行启用，
        // 而不是别的任何东西 —— 既不是插件被加载，也不是 FikaBackendUtils.ClientType，
        // 因为一场结束的 raid 以前会把后者留在 Host/Client 上，直到会话结束。
        FikaRaidGate.Arm("CreateFikaGameCore(location=" + (raidSettings?.LocationId ?? "-") + ", role="
            + (isServer ? "host" : "client") + ")");
        
        var isTransit = FikaBackendUtils.IsTransit;

        // 失败的准备工作（没有网络管理器、没有主机端点、加入被拒）
        // 必须在这里终止 raid 启动：继续下去正是产生"0 % 加载界面
        // 加上通用 NullReferenceException"报告的原因。
        if (FikaBackendUtils.JoinFailed)
        {
            throw new InvalidOperationException("The raid could not be started: " + FikaBackendUtils.JoinFailure);
        }

        if (FikaPlugin.Instance.Settings.NoAI.Value)
        {
            FikaGlobals.LogWarning("No AI enabled - stopping bot spawns");
            raidSettings.BotSettings.BotAmount = EFT.Bots.EBotAmount.NoBots;
            var waves = raidSettings.WavesSettings;
            waves.BotAmount = EFT.Bots.EBotAmount.NoBots;
            waves.IsBosses = false;
            raidSettings.WavesSettings = waves;
        }

        if (isServer && !isTransit)
        {
            FikaBackendUtils.CachedRaidSettings = raidSettings;
        }
        else if (isServer && isTransit && FikaBackendUtils.CachedRaidSettings != null)
        {
            Logger.LogInfo("Applying cached raid settings from previous raid");
            var cachedSettings = FikaBackendUtils.CachedRaidSettings;
            raidSettings.WavesSettings = cachedSettings.WavesSettings;
            raidSettings.BotSettings = cachedSettings.BotSettings;
            raidSettings.MetabolismDisabled = cachedSettings.MetabolismDisabled;
            raidSettings.PlayersSpawnPlace = cachedSettings.PlayersSpawnPlace;
        }

        metricsEvents.SetGamePrepared();

        if (Singleton<NotificationManager>.Instantiated)
        {
            Singleton<NotificationManager>.Instance.Deactivate();
        }

        var session = instance.Session;

        if (session == null)
        {
            throw new InvalidOperationException(
                "The backend session was null when the raid was initialized (the client is not logged in?).");
        }

        var profile = session.GetProfileBySide(raidSettings.Side);

        if (profile == null)
        {
            // 当所请求阵营的 profile 从未被加载时，session.GetProfileBySide 返回 null，
            // 而下一条语句以前就会在它上面出错。
            throw new InvalidOperationException(
                $"The backend session has no {raidSettings.Side} profile, so the raid cannot be started for that side.");
        }

        profile.Inventory.Stash = null;
        profile.Inventory.QuestStashItems = null;
        profile.Inventory.DiscardLimits = Singleton<ItemFactory>.Instance.GetDiscardLimits();

#if DEBUG
        Logger.LogInfo("TarkovApplication_LocalGameCreator_Patch:Postfix: Attempt to set Raid Settings");
        Logger.LogInfo($"RaidSettings Location: {raidSettings.LocationId}, TransitType: {raidSettings.transitionType}");
#endif

        if (!isServer)
        {
#if DEBUG
            Logger.LogInfo("Waiting for host to receive location");
#endif
            await WaitForServerToReceiveLocation();
#if DEBUG
            Logger.LogInfo("Host has received location, continuing");
#endif
        }

        if (!raidSettings.isInTransition)
        {
            await session.SendRaidSettings(raidSettings).AsManaged();
        }
        LocalRaidSettings localRaidSettings = new()
        {
            location = raidSettings.LocationId,
            timeVariant = raidSettings.SelectedDateTime,
            mode = ELocalMode.PVE_OFFLINE,
            playerSide = raidSettings.Side,
            transitionType = raidSettings.transitionType
        };
        instance._localRaidSettings = localRaidSettings;

        var localSettings = await instance.Session.LocalRaidStarted(localRaidSettings).AsManaged();
        raidSettings.BotSettings.ExcludedBosses = localSettings.excludedBosses;
        var raidSettingsToUpdate = instance._localRaidSettings;
        // SPT 5 会对返回的位置应用 scav 的 raid 时间调整。
        var escapeTimeLimit = raidSettings.IsScav ? localSettings.locationLoot.EscapeTimeLimit : raidSettings.SelectedLocation.EscapeTimeLimit;
        raidSettings.SelectedLocation = localSettings.locationLoot;
        raidSettings.SelectedLocation.EscapeTimeLimit = escapeTimeLimit;
        raidSettingsToUpdate.serverId = localSettings.serverId;
        raidSettingsToUpdate.selectedLocation = localSettings.locationLoot;
        raidSettingsToUpdate.selectedLocation.EscapeTimeLimit = escapeTimeLimit;

        var transitData = FikaBackendUtils.TransitData;
        transitData.transitionType = raidSettings.transitionType;
        // 在被赋值之前，TransitData 的默认 getter 会一直创建一个新的本机对象。
        // 把配置好的 transition 保留在本场 raid 以及
        // 队伍换图时 Stop() 所使用的同一个对象上。
        FikaBackendUtils.TransitData = transitData;
        raidSettingsToUpdate.transition = transitData;

        var profileInsurance = localSettings.profileInsurance;
        if ((profileInsurance?.insuredItems) != null)
        {
            profile.InsuredItems = localSettings.profileInsurance.insuredItems;
        }

        if (!isServer)
        {
            instance.Matchmaker.UpdateMatchingStatus(EMatchingStage.ServerConnecting, new Il2CppSystem.Nullable<float>());

            RaidSettingsRequest data = new();
            var raidSettingsResponse = await FikaRequestHandler.GetRaidSettings(data);

            if (!raidSettingsResponse.Received)
            {
                throw new InvalidDataException("Failed to retrieve raid settings");
            }

            raidSettings.MetabolismDisabled = raidSettingsResponse.MetabolismDisabled;
            raidSettings.PlayersSpawnPlace = raidSettingsResponse.PlayersSpawnPlace;
            Logger.LogInfo($"Raid settings from the server: hourOfDay={raidSettingsResponse.HourOfDay}, " +
                $"scav={FikaBackendUtils.IsScav}, requested={timeAndWeather.HourOfDay}");
            timeAndWeather.HourOfDay = raidSettingsResponse.HourOfDay;
            timeAndWeather.TimeFlowType = raidSettingsResponse.TimeFlowType;

            FikaBackendUtils.CustomRaidSettings = raidSettingsResponse.CustomRaidSettings;
            Logger.LogInfo($"Received CustomRaidSettings: {raidSettingsResponse.CustomRaidSettings}");
        }
        else
        {
            instance.Matchmaker.UpdateMatchingStatus(EMatchingStage.LocalGameStarting, new Il2CppSystem.Nullable<float>());
            FikaSingleton<FikaServer>.Instance.LocationReceived = true;
        }

        // 这个值会被服务器错误地重置，因此在 transit 期间在这里手动更新它
        if (!FikaBackendUtils.IsHeadless && isTransit && MainMenuUIScript.Exist)
        {
            MainMenuUIScript.Instance.UpdatePresence(UI.FikaUIGlobals.EFikaPlayerPresence.IN_RAID);
        }

        StartHandler startHandler = new(instance, session.Profile, session.ProfileOfPet, raidSettings.SelectedLocation);

        var raidLimits = GetRaidMinutes(raidSettings.SelectedLocation.EscapeTimeLimit);

        var coopGame = CoopGame.Create(inputTree, profile, gameWorld, localGameDateTime, instance.Session.InsuranceCompany,
            MonoBehaviourSingleton<GameUI>.Instance, raidSettings.SelectedLocation,
            timeAndWeather, raidSettings.WavesSettings, raidSettings.SelectedDateTime,
            NativeDelegates.Bind<Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics>>(startHandler, nameof(StartHandler.HandleStop)),
            fixedDeltaTime, instance.PlayerUpdateQueue, instance.Session, raidLimits, metricsEvents,
            new ClientMetricsCollector(metricsConfig, instance), localRaidSettings, raidSettings);

        startHandler.CoopGame = coopGame;

        Singleton<AbstractGame>.Create(coopGame);
        // 通过游戏自己的 NON-generic（非泛型）
        // CompositeDisposable.AddDisposable(Il2CppSystem.Action) 重载注册这两个 raid 作用域清理。绝不能从这里
        // 走到生成的 AddDisposable<T>(T) 重载：它的 interop 包装器会通过
        // Il2CppClassPointerStore<T> 解析类型实参，而那个 store 只对
        // interop 生成的类型、枚举、基元类型/字符串以及 ClassInjector 注册的类型存在。
        // 传给它一个 CLR 委托（System.Action）会让 NativeClassPtr 保持为 0，生成的
        // MethodInfoStoreGeneric cctor 会在 Il2CppInterop 的
        // Class::FromIl2CppType 钩子里以 TypeInitializationException 挂掉（47888 run 2026-10-09 23:38，frame 9425）。游戏自己的字段是
        // 一个本机 List<Action>，所以 Action 重载在语义上也是正确的那个。
        // 这两个清理都是声明在本类上的普通托管 lambda，绝不是绑定到
        // 注入实例方法上的 il2cpp 委托（见 AddRaidDisposable）：注入方法所注册的 MethodInfo
        // 会被引擎经由 interop invoker 抵达，而那正是
        // analysis/delegate-targets/REPORT.md 中的致命路径。
        AddRaidDisposable(unsubscriber, "CoopGame.Dispose", () => coopGame.Dispose());
        AddRaidDisposable(unsubscriber, "StartHandler.ReleaseSingleton", () => startHandler.ReleaseSingleton());
        metricsEvents.SetGameCreated();
        FikaEventDispatcher.DispatchEvent(new AbstractGameCreatedEvent(coopGame));

        using ScreenUpdater updater = new(instance.Matchmaker, coopGame);
        if (!isServer)
        {
            coopGame.SetMatchmakerStatus("Coop game joined");
        }
        else
        {
            coopGame.SetMatchmakerStatus("Coop game created");
        }

        await coopGame.InitPlayer(raidSettings.BotSettings);
        UnityEngine.Object.DestroyImmediate(MonoBehaviourSingleton<MenuUI>.Instance.gameObject);
        menuOperation?.Unsubscribe();
        bundleLock.MaxConcurrentOperations = FikaPlugin.Instance.Settings.MaxBundleLock.Value;
        gameWorld.OnGameStarted();

        if (FikaBackendUtils.IsSpectator)
        {
            Logger.LogInfo("Starting game as spectator");
            await HandleJoinAsSpectator();
        }
    }

    /// <summary>
    /// 每个会话记录一次：本文件绝不能实例化 AddDisposable&lt;System.Action&gt;。
    /// 读取该 store 只会运行类型实参的托管类型初始化器
    /// （RunClassConstructor，不涉及 il2cpp 调用），因此这次测量不会出错；它把
    /// 导致 23:38 那次运行中止的状态记录成一条具名日志行。
    /// </summary>
    private static void ReportGenericOverloadHazard()
    {
        if (_genericAddDisposableHazardReported)
        {
            return;
        }

        _genericAddDisposableHazardReported = true;

        long classPointer;
        try
        {
            classPointer = Il2CppInterop.Runtime.Il2CppClassPointerStore<System.Action>.NativeClassPtr.ToInt64();
        }
        catch (Exception exception)
        {
            
            return;
        }

        
    }

    /// <summary>
    /// 把一个 raid 清理注册到游戏的 CompositeDisposable 上。
    ///
    /// 委托目标始终是声明在本类（普通托管、非注入类）上的托管 lambda。
    /// 它绝不能是基于注入实例方法构建的 il2cpp 委托
    /// （<c>Il2CppSystem.Delegate.CreateDelegate</c> / <c>NativeDelegates.Bind</c>）：这种委托携带
    /// 注入方法所注册的 MethodInfo，而引擎会经由 interop invoker 抵达它，
    /// 后者的收尾 <c>calli</c> 指向该方法的 trampoline thunk —— coreclr 正是对这一对执行 fail-fast
    /// （"attempted to call a UnmanagedCallersOnly method from managed code"，
    /// analysis/delegate-targets/REPORT.md）。<c>CoopGame.Dispose</c> 与
    /// <c>StartHandler.ReleaseSingleton</c> 直到第 8 轮都是以这种方式绑定的。
    ///
    /// 下面的转换是 interop 的托管到本机委托桥
    /// （<c>DelegateSupport.ConvertDelegate</c>）：它构建自己的 reverse-P/Invoke trampoline，其主体
    /// 对 lambda 执行一次普通的托管调用，也就是说引擎的 invoke 停在托管代码处，
    /// 绝不进入注入方法的 invoker。结果类型仍是 <c>Il2CppSystem.Action</c>，因此
    /// 声明类型自身的非泛型成员是更好的候选：不会创建任何泛型方法实例化，
    /// 而本机委托仍像以前一样存进游戏的 List&lt;Action&gt; 里。
    /// </summary>
    private static void AddRaidDisposable(CompositeDisposable unsubscriber, string label, System.Action cleanup)
    {
        if (unsubscriber is null)
        {
            throw new InvalidOperationException(
                "The raid unsubscriber was null, so " + label + " could not be registered.");
        }

        ReportGenericOverloadHazard();

        Il2CppSystem.Action action;
        try
        {
            action = (Il2CppSystem.Action)cleanup;
        }
        catch (Exception exception)
        {
            
            throw;
        }

        if (action is null)
        {
            throw new InvalidOperationException(
                "The il2cpp delegate for " + label
                + " came back null, so the raid cannot clean up after itself.");
        }

        try
        {
            unsubscriber.AddDisposable(action);
        }
        catch (Exception exception) when (exception is TypeInitializationException or TypeLoadException)
        {
            
            throw;
        }
        catch (Exception exception)
        {
            
            throw;
        }

        
    }

    /// <summary>
    /// 上面测得的探针是 interop 的一种性质，而不是某一次调用的性质，因此只记录一次。
    /// </summary>
    private static bool _genericAddDisposableHazardReported;

    /// <summary>
    /// 等待主机确认它已收到本地玩家的位置。
    ///
    /// 这个循环以前会无条件解引用 <see cref="FikaSingleton{T}.Instance"/>，
    /// 而且永不超时：从未被创建的客户端（准备阶段被终止）会抛出那个通用的
    /// NullReferenceException，而一个始终连不上主机的客户端会在这里永远空转，
    /// 而 Fika 加载界面一直停在 0 %。现在它会等待客户端出现，每秒上报一次
    /// 它的连接状态，并以可读的原因失败。
    /// </summary>
    /// <summary>
    /// 等待主机确认它已收到本地玩家的位置。
    ///
    /// 三种不同的状态以前在这里看起来一模一样，而正常情况为此付出了代价：
    ///   * 仍在加载自己地图的主机对每次轮询都回答 HostReceivedLocation=false；
    ///   * 主线程卡在长帧里的主机完全不作答，而它的
    ///     socket 线程仍然保活着连接；
    ///   * 已经消失的主机停止作答，*并且*传输层会断开。
    /// 因此单一的固定上限会终止那些只需要更多时间的加入 —— 在 2026-10-11 18:58
    /// 那一轮，主机为 TarkovStreets 需要 145 秒（主机 Fika-diagnostics.log 464548 -> 609164 ms），
    /// 而客户端在 89 秒后才进入自己的位置等待，并在固定的 40 秒后中止
    /// （客户端 Fika-diagnostics.log 477018 -> 517290 ms）；正是那次中止让主机一直等待一个
    /// 从未到来的角色。
    ///
    /// 以下情况会结束这次等待，顺序如下：
    ///   * 传输层：<c>ServerConnection.ConnectionState != Connected</c> 是主机已消失的唯一证据
    ///     （LiteNetLib 自己的线程维护连接、ack 与 keepalive，因此它能在主机主线程繁忙时
    ///     依然存活）；
    ///   * <see cref="LoadSyncGate.ClientCeilingSeconds"/>，它在 transit 重载窗口内会更长
    ///     —— 一个始终没有任何进展的已连接主机不能被永远等待下去；
    ///   * 长时间的应用层沉默会以警告形式上报（每 15 秒一次），而绝不会自行
    ///     结束等待。这正是与那个让本次加入死掉的固定上限的区别。
    /// 主机应答计数器和本地加载进度出现在每一行 [LoadSync] 里，因此无论一次运行
    /// 成功与否都能据此判定。
    /// </summary>
    private static async Task WaitForServerToReceiveLocation()
    {
        var packet = new InformationPacket();
        var span = TimeSpan.FromSeconds(1);
        var attempts = 0;
        var discoveryLimit = (FikaPlugin.Instance.Settings.ConnectionTimeout?.Value ?? 30) + 10;

        FikaClient client = null;
        while (client == null)
        {
            client = FikaSingleton<FikaClient>.Instance;
            if (client != null)
            {
                break;
            }

            if (++attempts > discoveryLimit)
            {
                throw new InvalidOperationException(
                    "The Fika client was never created, so the host could not be asked for the raid location.");
            }

            await Task.Delay(span);
        }

        attempts = 0;
        var locationCeiling = LoadSyncGate.ClientCeilingSeconds(FikaBackendUtils.IsTransit);
        var silentSeconds = 0;
        var lastResponseCount = client.HostResponseCount;
        var reportedAt = 0;

        string DescribeLocationWait()
        {
            return "stage=location role=client elapsed=" + attempts + "s responses=" + client.HostResponseCount
                + " hostReceivedLocation=" + client.HostReceivedLocation + " hostLoaded=" + client.HostLoaded
                + " hostReady=" + client.HostReady + ", transport="
                + (client.ServerConnection == null ? "no peer" : client.ServerConnection.ConnectionState.ToString())
                + " silentFor=" + silentSeconds + "s, ceiling=" + locationCeiling + "s, "
                + LoadSync.DescribePeerTraffic() + ", " + DescribeLocalProgress()
                + " (" + LoadSync.DescribeScreen() + ")";
        }

        while (!client.HostReceivedLocation)
        {
            if (client.ServerConnection == null && attempts > 5)
            {
                throw new InvalidOperationException(
                    "The Fika client is not connected to the host, so the raid cannot be started. "
                    + "The host session was reachable while joining (the ping handshake succeeded) but the game "
                    + "connection never completed - check the host's UDP port and the firewall.");
            }

            if (client.ServerConnection != null && client.ServerConnection.ConnectionState != ConnectionState.Connected
                && attempts > 5)
            {
                throw new InvalidOperationException(
                    "The connection to the raid host was lost while this client waited for the location "
                    + "acknowledgement. " + DescribeLocationWait());
            }

            try
            {
                client.SendData(ref packet, DeliveryMethod.ReliableUnordered);
            }
            catch (Exception exception)
            {
                
                throw;
            }

            // 应答计数器在增长，说明主机的*应用层*正在作答；
            // 这些应答里的标志说明它自己的 raid 启动进行到哪一步。传输层存活却沉默，说明
            // 主机的主线程正忙（在加载它自己的地图）—— 那是警告，不是失败。
            if (client.HostResponseCount != lastResponseCount)
            {
                lastResponseCount = client.HostResponseCount;
                silentSeconds = 0;
            }
            else
            {
                silentSeconds++;
            }

            if (attempts >= reportedAt)
            {
                reportedAt = attempts + 5;
            }

            if (silentSeconds >= LoadSyncGate.ClientSilentWarnSeconds && silentSeconds % 15 == 0)
            {
                LoadSync.Warn("WaitForServerToReceiveLocation", DescribeLocationWait()
                    + " => no application-level response for " + silentSeconds + "s while the transport is "
                    + (client.ServerConnection?.ConnectionState.ToString() ?? "gone")
                    + ": the host's main thread is most likely loading its own copy of the raid; the wait continues until "
                    + locationCeiling + "s");
            }

            if (++attempts > locationCeiling)
            {
                throw new InvalidOperationException(
                    "The host has not acknowledged the raid location within " + locationCeiling + " seconds. "
                    + DescribeLocationWait() + " - see the [LoadSync] lines for how far the host's own raid start got.");
            }

            await Task.Delay(span);
        }

    }

    /// <summary>
    /// 本机为自己公布的上报进度，供 [LoadSync] 行使用（"local=92.5"）。
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

    private static TimeSpan GetRaidMinutes(int defaultMinutes)
    {
        return TimeSpan.FromSeconds(60 * defaultMinutes);
    }

    private sealed class StartHandler : Il2CppSystem.Object
    {
        private readonly TarkovApplication _tarkovApplication;
        private readonly Profile _pmcProfile;
        private readonly Profile _scavProfile;
        private readonly LocationSettings.Location _location;
        public CoopGame CoopGame;

        public StartHandler(IntPtr pointer) : base(pointer) { }

        public StartHandler(TarkovApplication tarkovApplication, Profile pmcProfile, Profile scavProfile,
            LocationSettings.Location location)
            : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<StartHandler>())
        {
            _tarkovApplication = tarkovApplication;
            _pmcProfile = pmcProfile;
            _scavProfile = scavProfile;
            _location = location;
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
            Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this);
        }

        public void HandleStop(Result<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> result)
        {
            _tarkovApplication.OnGameEnd(_pmcProfile.Id, _scavProfile, _location, result);
        }

        public void ReleaseSingleton()
        {
            Singleton<AbstractGame>.Release(CoopGame);
            FikaSingleton<IFikaGame>.TryRelease(CoopGame);
        }
    }

    private static async Task HandleJoinAsSpectator()
    {
        var MainPlayer = Singleton<GameWorld>.Instance.MainPlayer;

        // 把玩家传送到地下，以免它被搜刮
        var currentPosition = MainPlayer.Position;
        MainPlayer.Teleport(new(currentPosition.x, currentPosition.y - 75, currentPosition.z));

        // 短暂延迟，以确保传送命令先被处理
        await Task.Delay(250);

        DamageInfo damageInfo = new()
        {
            Damage = 1000,
            DamageType = EDamageType.Impact
        };

        // 杀死玩家以使其进入观战模式
        MainPlayer.ApplyDamageInfo(damageInfo, EBodyPart.Head, EBodyPartColliderType.Eyes, 0);
    }
}

internal class ScreenUpdater : IDisposable
{
    public static string StatusText { get; private set; }

    private readonly CoopGame _coopGame;
    private readonly System.Action<EMatchingStage, Il2CppSystem.Nullable<float>> _updateStatus;

    public ScreenUpdater(MatchmakerPlayersController controller, CoopGame game)
    {
        _coopGame = game;
        
    }

    public static void SetStatusText(string text) => StatusText = text;

    public void Dispose()
    {
        if (_updateStatus is not null)
        {
            _coopGame.OnMatchingStatusChanged -= _updateStatus;
        }

        StatusText = null;
    }
}
