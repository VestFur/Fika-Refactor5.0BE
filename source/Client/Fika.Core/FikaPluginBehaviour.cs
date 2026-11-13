using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Main.GameMode;

namespace Fika.Core;

/// <summary>Unity lifecycle entry point for the BepInEx IL2CPP plugin.</summary>
public sealed class FikaPluginBehaviour : MonoBehaviour
{
    public FikaPluginBehaviour(IntPtr pointer) : base(pointer) { }

    private void Awake() => MainThreadDispatcher.Initialize();

    private void Start() => FikaPlugin.Instance.StartDeferredChecks();

    private void Update()
    {
        FikaPlugin.Instance.UpdateInitialization();
        if (FikaSingleton<IFikaGame>.Instantiated && FikaSingleton<IFikaGame>.Instance is CoopGame coopGame)
        {
            coopGame.RepairLocalInteractionState();
            coopGame.RepairLocalInventoryState();
            coopGame.RepairLocalHands();
        }
        MainThreadDispatcher.Drain(exception =>
            FikaPlugin.Instance.FikaLogger.LogError($"Fika main-thread callback failed: {exception}"));
    }

    private void OnDestroy()
    {
        FikaPlugin.Instance?.NotificationManager?.Close();
        FikaPlugin.HeadlessRequesterWebSocket?.Close();
    }
}
