using System;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using Dissonance.Networking;
using Fika.Core.Bundles;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.UI.Custom;

namespace Fika.Core.Networking;

public static class NetManagerUtils
{
    public static GameObject FikaGameObject;

    private static readonly ManualLogSource _logger = Logger.CreateLogSource("NetManagerUtils");
    private static CancellationTokenSource _pingTokenSource;

    /// <summary>
    /// 按网络设置应用可选的人为延迟与丢包，
    /// 以便在一台机器上复现与延迟相关的问题。
    /// </summary>
    public static void ApplySimulationSettings(NetManager manager)
    {
        if (manager == null)
        {
            return;
        }

        var settings = FikaPlugin.Instance.Settings;
        var latency = Math.Max(0, settings.SimulatedLatencyMs.Value);
        var loss = Math.Clamp(settings.SimulatedPacketLossPercent.Value, 0, 100);

        manager.SimulateLatency = latency > 0;
        manager.SimulationMinLatency = latency;
        manager.SimulationMaxLatency = latency;
        manager.SimulatePacketLoss = loss > 0;
        manager.SimulationPacketLossChance = loss;

        if (manager.SimulateLatency || manager.SimulatePacketLoss)
        {
            _logger.LogWarning($"Network simulation active: latency {latency} ms, packet loss {loss} %");
        }
    }

    public static void CreateFikaGameObject()
    {
        FikaGameObject = new GameObject("FikaGameObject");
        UnityEngine.Object.DontDestroyOnLoad(FikaGameObject);
        _logger.LogInfo("FikaGameObject has been created!");
    }

    public static void CreateNetManager(bool isServer)
    {
        // VOIP 需要
        TrafficCounters.Reset();
        FikaChatUIScript.IsActive = false;

        if (FikaGameObject == null)
        {
            CreateFikaGameObject();
        }

        CreateLoadingScreenUI();
        

        if (isServer)
        {
            var server = FikaGameObject.AddComponent<FikaServer>();
            FikaSingleton<FikaServer>.Create(server);
            _logger.LogInfo("FikaServer has created!");
            FikaSingleton<IFikaNetworkManager>.Create(server);
            return;
        }

        var client = FikaGameObject.AddComponent<FikaClient>();
        FikaSingleton<FikaClient>.Create(client);
        _logger.LogInfo("FikaClient has created!");
        
        FikaSingleton<IFikaNetworkManager>.Create(client);
    }

    public static void CreateLoadingScreenUI()
    {
        if (LoadingScreenUI.Instance != null)
        {
            LoadingScreenUI.Instance.gameObject.SetActive(true);
            
            return;
        }

        var loadingPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.LoadingScreenUI);
        var loadingUi = GameObject.Instantiate(loadingPrefab, FikaGameObject.transform);
        LoadingScreenUI.Instance = loadingUi.GetComponent<LoadingScreenUI>();
        
    }

    /// <summary>
    /// 禁用 <see cref="LoadingScreenUI"/> 并清除其中的所有数据
    /// </summary>
    public static void DisableLoadingScreenUI()
    {
        if (LoadingScreenUI.Instance != null)
        {
            LoadingScreenUI.Instance.ClearData();
            LoadingScreenUI.Instance.gameObject.SetActive(false);
            
        }
    }

    /// <summary>
    /// 销毁 <see cref="LoadingScreenUI"/>
    /// </summary>
    public static void DestroyLoadingScreenUI()
    {
        if (LoadingScreenUI.Instance != null)
        {
            GameObject.Destroy(LoadingScreenUI.Instance);
            LoadingScreenUI.Instance = null;
            
        }
    }

    public static Task<FikaPingingClient> CreatePingingClient()
    {
        if (FikaGameObject == null)
        {
            CreateFikaGameObject();
        }

        var pingingClient = new FikaPingingClient();
        FikaSingleton<FikaPingingClient>.Create(pingingClient);
        _logger.LogInfo("FikaPingingClient has started!");
        return Task.FromResult(pingingClient);
    }

    public static void DestroyNetManager(bool isServer)
    {
        NetworkUtils.ResetReaderAndWriter();

        // Transit 保留 UDP 传输，但会创建另一个语音场景/玩家。
        // 在释放单例或启动那个下一个场景之前停止采集。
        if (isServer) FikaSingleton<FikaServer>.Instance?.ShutdownVOIP();
        else FikaSingleton<FikaClient>.Instance?.ShutdownVOIP();

        // (analysis/transit-crash-unload) 本方法会从世界自身的拆除流程
        // （FikaHostGameWorld.Dispose / FikaClientGameWorld.Dispose）以及 raid 结束路径到达，即
        // 恰好在本进程所用的世界消失的那一刻。所有仍然指向
        // 那个世界的东西——泵会继续步进的例程（尤其是原生会话枚举器）
        // 以及以其原生对象为键的静态缓存——都在此处一次性丢弃，然后才运行本
        // 拆除流程的其余部分。幂等：同一窗口内的第二次调用只写一行，而下一个
        // 世界（FikaClientWorld/FikaHostWorld.Create）会开启一个新的 epoch。
        WorldLifecycle.MarkWorldTornDown(isServer
            ? "NetManagerUtils.DestroyNetManager (host), transit=" + FikaBackendUtils.IsTransit
            : "NetManagerUtils.DestroyNetManager (client), transit=" + FikaBackendUtils.IsTransit);

        if (FikaBackendUtils.IsTransit)
        {
            // 判定行 (analysis/loading-screen-sync)：本分支刻意保留 UDP 会话、
            // 单例和 ClientType——同一次 transit 的下一个 raid 会用到它们。它
            // 只应当被用于确实还在继续的 transit（见 AbortFailedJoin，它会
            // 在拆除失败的加入之前清除该标志）。

            CreateLoadingScreenUI();
            LoadingScreenUI.Instance.ReInitAfterTransit();

            FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.CleanUpForTransit();

            if (!isServer)
            {
                // 本客户端的世界刚刚被拆除，而下一个世界正在被创建：为那个
                // 窗口打开世界封包队列（见 FikaClient.TryQueueWorldPacket）。
                FikaSingleton<FikaClient>.Instance?.ArmWorldPacketQueue("transit world swap");

                // (analysis/transit-reload-oom) 这就是所报告的崩溃缺少一次释放的
                // 边界处：上一个世界已消失，下一个世界尚未创建。把这次
                // 切换记录下来，好让 FikaRaidMemory 在新的关卡加载之前
                // 直接释放被替换 raid 的资源（FikaClientGameWorld.InitLevel），并打印此处仍持有的东西。
                FikaRaidMemory.NoteWorldSwap("DestroyNetManager: transit teardown, client world is down");
            }
            if (isServer)
            {
                var server = FikaSingleton<FikaServer>.Instance;
                server.HostReady = false;
                server.RaidInitialized = false;
                server.ResetReadyClients();
                server.LocationReceived = false;
                // (analysis/transit-reload-oom) 主机与客户端有同样的同时持有两张地图的
                // 暴露面——同一进程、同一个被替换的世界、同样缺失的资源释放——只是它运行在
                // 更大的机器上，所以不会先崩。在此记录这次切换；FikaHostGameWorld
                // 会在自己的新关卡加载之前直接释放被替换 raid 的资源（每次切换
                // 一次，以 FikaBackendUtils.IsTransit 为条件，且绝不会让加载失败）。
                FikaRaidMemory.NoteWorldSwap("DestroyNetManager: transit teardown, host world is down");
                return;
            }

            var client = FikaSingleton<FikaClient>.Instance;
            client.HostReady = false;
            client.HostLoaded = false;
            client.ReadyClients = 0;

            return;
        }


        DestroyLoadingScreenUI();

        FikaBackendUtils.ClientType = EClientType.None;

        if (FikaGameObject != null)
        {
            if (isServer)
            {
                var server = FikaSingleton<FikaServer>.Instance;
                if (server == null)
                {
                    _logger.LogWarning("DestroyNetManager: no FikaServer to destroy");
                    
                    return;
                }

                if (!FikaSingleton<IFikaNetworkManager>.TryRelease(server))
                {
                    _logger.LogError("Unable to release Server from Singleton!");
                }
                try
                {
                    server.PrintStatistics();
                    server.NetServer.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogError("DestroyNetManager: " + ex.Message);
                }
                FikaSingleton<FikaServer>.TryRelease(server);
                UnityEngine.Object.Destroy(server);
                _logger.LogInfo("Destroyed FikaServer");
                return;
            }

            var client = FikaSingleton<FikaClient>.Instance;
            if (client == null)
            {
                _logger.LogWarning("DestroyNetManager: no FikaClient to destroy");
                
                return;
            }

            if (!FikaSingleton<IFikaNetworkManager>.TryRelease(client))
            {
                _logger.LogError("Unable to release Client from Singleton!");
            }
            try
            {
                client.PrintStatistics();
                client.NetClient.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogError("DestroyNetManager: " + ex.Message);
            }
            FikaSingleton<FikaClient>.TryRelease(client);
            UnityEngine.Object.Destroy(client);
            _logger.LogInfo("Destroyed FikaClient");
        }
    }

    /// <summary>
    /// 清理一次*失败的*加入尝试留下的一切，让玩家落到一个
    /// 可用的主菜单，而不是卡在 0 % 的 Fika 加载界面。
    ///
    /// 由 raid 启动的失败处理器调用；它从不抛异常。
    /// </summary>
    public static void AbortFailedJoin(bool isServer, string reason)
    {
        var wasTransit = FikaBackendUtils.IsTransit;
        try
        {
            
            LoadSync.Warn("NetManagerUtils.AbortFailedJoin",
                "aborting role=" + (isServer ? "host" : "client") + " (clientType=" + FikaBackendUtils.ClientType
                + ", transit=" + wasTransit + "): " + reason + " => the raid start is given up, the session is torn down");

            // 失败的 raid 启动并不是一个还在继续的 transit：下一个 raid 会从其自身的
            // raid 设置中重新设置该标志，而 DestroyNetManager 在这里必须走完整的拆除路径。
            // 让标志保持设置（本方法过去的行为）会让整个 net manager、
            // UDP 连接以及对等端对本机的视图在 ClientType 已被清除的情况下继续存活——
            // 正是它让主机一直等待一个永远不会到来的角色
            // （analysis/loading-screen-sync，主机 LogOutput.log:4730/4731），也是它在一个仍然连接的客户端上
            // 产生了 "Received CharacterSyncPacket as server" 这些行。
            FikaBackendUtils.IsTransit = false;
            // 被放弃的 raid 启动不再属于 transit 重载：下一次重载的保持设置
            // 不得被后来的 raid 启动继承（analysis/transit-reload-sync）。
            TransitReloadGate.Disarm("the raid start was given up (" + reason + ")");
            DisableLoadingScreenUI();
            DestroyLoadingScreenUI();
            DestroyNetManager(isServer);
        }
        catch (Exception exception)
        {
            
        }
        finally
        {
            if (FikaBackendUtils.ClientType == EClientType.Client)
            {
                FikaBackendUtils.ClientType = EClientType.None;
            }

            LoadSync.Warn("NetManagerUtils.AbortFailedJoin",
                "aborted: clientType=" + FikaBackendUtils.ClientType + ", transit=" + FikaBackendUtils.IsTransit
                + ", connected=" + (FikaSingleton<FikaClient>.Instantiated && FikaSingleton<FikaClient>.Instance.ServerConnection != null)
                + " => a peer that gave up is no longer connected, so the host's pre-raid barrier cannot wait for it");
        }
    }

    public static async Task InitNetManager(bool isServer)
    {
        if (FikaGameObject != null)
        {
            if (isServer)
            {
                var server = FikaSingleton<FikaServer>.Instance;
                if (!server.Started)
                {
                    await server.Init();
                }
                FikaEventDispatcher.DispatchEvent(new FikaNetworkManagerCreatedEvent(server));
                return;
            }

            var client = FikaSingleton<FikaClient>.Instance;
            if (client == null)
            {
                // CreateNetManager 一定是失败了，或者单例在此期间被
                // 释放了。报告缺少哪一步，而不是解引用 null。
                _logger.LogError("InitNetManager: FikaClient singleton was null!");
                
                throw new InvalidOperationException(
                    "The Fika client was not created before the raid started (FikaClient singleton was null).");
            }

            if (!client.Started)
            {
                await client.Init();
            }
            
            FikaEventDispatcher.DispatchEvent(new FikaNetworkManagerCreatedEvent(client));
            return;
        }

        _logger.LogError("InitNetManager: FikaGameObject was null!");
        
        throw new InvalidOperationException("The Fika game object was not created before the raid started.");
    }

    public static Task SetupGameVariables(FikaPlayer fikaPlayer)
    {
        _logger.LogInfo("Setting up game variables...");
        FikaSingleton<IFikaNetworkManager>.Instance.SetupGameVariables(fikaPlayer);
        return Task.CompletedTask;
    }

    public static void StartPinger()
    {
#if DEBUG
        _logger.LogInfo("Starting pinger");
#endif
        _pingTokenSource = new();
        _ = Task.Run(() => PingBackend(_pingTokenSource.Token));
    }

    public static void StopPinger()
    {
#if DEBUG
        _logger.LogInfo("Stopping pinger");
#endif
        _pingTokenSource.Cancel();
    }

    private static async Task PingBackend(CancellationToken token)
    {
        PingRequest pingRequest = new();
        while (true)
        {
            if (token.IsCancellationRequested)
            {
#if DEBUG
                _logger.LogInfo("Pinger stopped successfully");
#endif
                return;
            }

            await FikaRequestHandler.UpdatePing(pingRequest);
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    public static Task CreateCoopHandler()
    {
        if (FikaBackendUtils.IsTransit)
        {
            return Task.CompletedTask;
        }

        _logger.LogInfo("Creating CoopHandler...");
        var networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        if (networkManager == null)
        {
            // 这里过去会抛回一个 NullReferenceException，它到达 raid 启动处时
            // 变成了泛化的 "Object reference not set to an instance of an object." 弹框。
            _logger.LogError("CreateCoopHandler: IFikaNetworkManager was null!");
            
            return Task.FromException(new InvalidOperationException(
                "The Fika network manager was not created, so the raid could not be joined."));
        }

        if (FikaGameObject == null)
        {
            _logger.LogError("CreateCoopHandler: FikaGameObject was null!");
            
            return Task.FromException(new InvalidOperationException(
                "The Fika game object was not created, so the raid could not be joined."));
        }

        var coopHandler = FikaGameObject.AddComponent<CoopHandler>();
        networkManager.CoopHandler = coopHandler;

        if (string.IsNullOrEmpty(FikaBackendUtils.GroupId))
        {
            UnityEngine.Object.Destroy(coopHandler);
            _logger.LogError("No ServerId found, deleting CoopHandler!");
            
            return Task.FromException(new InvalidOperationException(
                "The raid group id was empty, so the host could not be contacted."));
        }

        coopHandler.ServerId = FikaBackendUtils.GroupId;
        
        return Task.CompletedTask;
    }
}
