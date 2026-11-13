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
    /// Applies the optional artificial latency and packet loss from the network
    /// settings so a lag dependent problem can be reproduced on one machine.
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
        // Required for VOIP
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
    /// Disables the <see cref="LoadingScreenUI"/> and clears all data in it
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
    /// Destroys the <see cref="LoadingScreenUI"/>
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

        // Transit retains UDP transport but creates another voice scene/player.
        // Stop capture before releasing singletons or starting that next scene.
        if (isServer) FikaSingleton<FikaServer>.Instance?.ShutdownVOIP();
        else FikaSingleton<FikaClient>.Instance?.ShutdownVOIP();

        // (analysis/transit-crash-unload) This method is reached from the world's own teardown
        // (FikaHostGameWorld.Dispose / FikaClientGameWorld.Dispose) and from the raid-end path, i.e. at
        // exactly the moment the world this process was using is gone. Everything that still points into
        // that world - the routines the pump would keep stepping (the native session enumerator above all)
        // and the static caches keyed by its native objects - is dropped here, once, before the rest of this
        // teardown runs. Idempotent: a second call inside the same window only writes a line, and the next
        // world (FikaClientWorld/FikaHostWorld.Create) opens a fresh epoch.
        WorldLifecycle.MarkWorldTornDown(isServer
            ? "NetManagerUtils.DestroyNetManager (host), transit=" + FikaBackendUtils.IsTransit
            : "NetManagerUtils.DestroyNetManager (client), transit=" + FikaBackendUtils.IsTransit);

        if (FikaBackendUtils.IsTransit)
        {
            // Judgement line (analysis/loading-screen-sync): this branch keeps the UDP session, the
            // singletons and ClientType on purpose - the next raid of the same transit uses them. It
            // must only ever be taken for a transit that is really continuing (see AbortFailedJoin,
            // which clears the flag before it tears a failed join down).
            LoadSync.Report("NetManagerUtils.DestroyNetManager",
                "teardown role=" + (isServer ? "host" : "client") + ": transit=" + FikaBackendUtils.IsTransit
                + " => the net manager, the connection and clientType=" + FikaBackendUtils.ClientType
                + " survive into the next raid, only the per-raid state is reset");

            CreateLoadingScreenUI();
            LoadingScreenUI.Instance.ReInitAfterTransit();

            FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.CleanUpForTransit();

            if (!isServer)
            {
                // The world of this client was just torn down and the next one is being created: open
                // the world packet queue for that window (see FikaClient.TryQueueWorldPacket).
                FikaSingleton<FikaClient>.Instance?.ArmWorldPacketQueue("transit world swap");

                // (analysis/transit-reload-oom) This is the boundary the reported crash is missing a
                // release at: the previous world is gone, the next one is not created yet. Record the
                // swap so FikaRaidMemory releases the replaced raid's assets directly before the new
                // level is loaded (FikaClientGameWorld.InitLevel), and print what is still held here.
                FikaRaidMemory.NoteWorldSwap("DestroyNetManager: transit teardown, client world is down");
            }
            if (isServer)
            {
                var server = FikaSingleton<FikaServer>.Instance;
                server.HostReady = false;
                server.RaidInitialized = false;
                server.ResetReadyClients();
                server.LocationReceived = false;
                // (analysis/transit-reload-oom) The host has the same two-maps-at-once exposure as the
                // client - same process, same replaced world, same missing asset release - it only runs on
                // a bigger machine, so it does not crash first. Record the swap here; FikaHostGameWorld
                // releases the replaced raid's assets directly before its own new level is loaded (one shot
                // per swap, gated on FikaBackendUtils.IsTransit, and never able to fail the load).
                FikaRaidMemory.NoteWorldSwap("DestroyNetManager: transit teardown, host world is down");
                return;
            }

            var client = FikaSingleton<FikaClient>.Instance;
            client.HostReady = false;
            client.HostLoaded = false;
            client.ReadyClients = 0;

            return;
        }

        LoadSync.Report("NetManagerUtils.DestroyNetManager",
            "teardown role=" + (isServer ? "host" : "client") + ": transit=" + FikaBackendUtils.IsTransit
            + " => full teardown, clientType=" + FikaBackendUtils.ClientType + " is cleared and the connection is closed");

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
    /// Cleans up everything a *failed* join attempt left behind, so the player lands on a
    /// usable main menu instead of a Fika loading screen stuck at 0 %.
    ///
    /// Called from the failure handler of the raid start; it never throws.
    /// </summary>
    public static void AbortFailedJoin(bool isServer, string reason)
    {
        var wasTransit = FikaBackendUtils.IsTransit;
        try
        {
            
            LoadSync.Warn("NetManagerUtils.AbortFailedJoin",
                "aborting role=" + (isServer ? "host" : "client") + " (clientType=" + FikaBackendUtils.ClientType
                + ", transit=" + wasTransit + "): " + reason + " => the raid start is given up, the session is torn down");

            // A raid start that failed is not a transit that continues: the next raid will set the flag
            // again from its own raid settings, while DestroyNetManager must take its full-teardown path
            // here. Leaving the flag set (what this method used to do) kept the whole net manager, the
            // UDP connection and the peer's view of this machine alive while ClientType was already
            // cleared - which is what left the host waiting for a character that could never come
            // (analysis/loading-screen-sync, host LogOutput.log:4730/4731) and what produced the
            // "Received CharacterSyncPacket as server" lines on a client that was still connected.
            FikaBackendUtils.IsTransit = false;
            // A raid start that was given up is not a transit reload any more: the hold of the next reload
            // must not be inherited by a later raid start (analysis/transit-reload-sync).
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
                // CreateNetManager must have failed or the singleton was released in
                // between. Report which step is missing instead of dereferencing null.
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
            // This used to hand back a NullReferenceException, which reached the raid start
            // as the generic "Object reference not set to an instance of an object." box.
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
