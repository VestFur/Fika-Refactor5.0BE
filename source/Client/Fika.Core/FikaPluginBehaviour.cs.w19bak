using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Main.Components;
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
        // This method is a native -> managed boundary: il2cpp calls it once per frame. Anything that
        // escapes from here is an exception with no managed frame above it, which is exactly the shape
        // the runtime turns into a crash instead of a log line (analysis/crash-clr). Every segment is
        // therefore guarded on its own, so one broken segment cannot starve the others - above all the
        // routine pump, which is the last statement and drives the whole raid start sequence.

        FikaPlugin.Instance.UpdateInitialization();

        RepairLocalStateInUpdate();

        // AP (analysis/nonfika-raid-inertness): the raid kind is re-verified here - at most one singleton
        // read per 500 ms - so a gate that was armed for a raid of ours cannot survive into a native one
        // even if the raid-start or raid-end patch never ran.
        FikaRaidGate.Verify();

        // Night vision / thermal self-heal: the camera effects cannot re-enable themselves once the game
        // disabled them (analysis/night-vision/REPORT.md). Read-only unless the camera effect was left
        // disabled or drifted away from the equipped headwear device.
        // AP: the self-heal writes into native camera state, so outside a Fika raid this port does not
        // touch it. "Coop / Inert Outside Fika Raids" off restores the old behaviour for every caller.
        if (!FikaRaidGate.Skip("NvgDiagnostics.Tick"))
        {
            NvgDiagnostics.Tick();
        }

        DrainMainThreadQueue();

        // The same tick drives Fika's managed routines (the session start, the countdown path, the raid view
        // restore): they used to be engine coroutines, and the engine crashed while releasing the managed
        // enumerator wrapper of exactly that shape (analysis/host-raid-coroutine).
        FikaBoundaries.Run(BoundaryUpdate + "/FikaRoutinePump.Tick", static () => FikaRoutinePump.Instance.Tick());
    }

    /// <summary>Journal label of this boundary; a constant so the journal never allocates for it.</summary>
    internal const string BoundaryUpdate = "FikaPluginBehaviour.Update (il2cpp -> managed)";

    private static void RepairLocalStateInUpdate()
    {
        // (analysis/transit-crash-unload) Between the teardown of a world and the creation of its successor
        // the game singleton still points at the raid that just ended, while its world, its local player and
        // its hands are already released. Every repair below touches exactly those objects, so the window is
        // skipped as a whole; it is short (the map load) and nothing in it can be repaired anyway.
        // The second half of the condition is the safety net: as soon as the engine reports a world again,
        // the repairs resume even if a teardown was ever recorded late - a skipped repair cannot outlive its
        // window, so this guard can never disable the hands/interaction/inventory repairs of a live raid.
        if (WorldLifecycle.WorldDown && !LoadSync.WorldReady)
        {
            return;
        }

        if (FikaSingleton<IFikaGame>.Instantiated && FikaSingleton<IFikaGame>.Instance is CoopGame coopGame)
        {
            coopGame.RepairLocalInteractionState();
            coopGame.RepairLocalInventoryState();
            coopGame.RepairLocalHands();
        }
    }

    private static void DrainMainThreadQueue()
    {
        MainThreadDispatcher.Drain(exception =>
            FikaPlugin.Instance.FikaLogger.LogError($"Fika main-thread callback failed: {exception}"));
    }

    private void OnApplicationQuit()
    {
    }

    private void OnDestroy()
    {
        FikaRoutinePump.Instance.StopAll("the plugin behaviour is being destroyed");
        FikaPlugin.Instance?.NotificationManager?.Close();
        FikaPlugin.HeadlessRequesterWebSocket?.Close();
    }
}
