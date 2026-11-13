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

        if (FikaBackendUtils.IsTransit)
        {
            CreateLoadingScreenUI();
            LoadingScreenUI.Instance.ReInitAfterTransit();

            FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.CleanUpForTransit();
            if (isServer)
            {
                var server = FikaSingleton<FikaServer>.Instance;
                server.HostReady = false;
                server.RaidInitialized = false;
                server.ResetReadyClients();
                server.LocationReceived = false;
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
            if (!client.Started)
            {
                await client.Init();
            }
            FikaEventDispatcher.DispatchEvent(new FikaNetworkManagerCreatedEvent(client));
            return;
        }

        _logger.LogError("InitNetManager: FikaGameObject was null!");
        throw new NullReferenceException("FikaGameObject was null");
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
        if (networkManager != null)
        {
            if (FikaGameObject != null)
            {
                var coopHandler = FikaGameObject.AddComponent<CoopHandler>();
                networkManager.CoopHandler = coopHandler;

                if (!string.IsNullOrEmpty(FikaBackendUtils.GroupId))
                {
                    coopHandler.ServerId = FikaBackendUtils.GroupId;
                    return Task.CompletedTask;
                }

                UnityEngine.Object.Destroy(coopHandler);
                _logger.LogError("No ServerId found, deleting CoopHandler!");
                throw new InvalidOperationException("No Server Id found");
            }
        }

        return Task.FromException(new NullReferenceException("CreateCoopHandler: IFikaNetworkManager or FikaGameObject was null"));
    }
}
