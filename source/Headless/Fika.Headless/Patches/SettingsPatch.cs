using Comfort.Common;
using EFT;
using EFT.Settings;
using EFT.Settings.Graphics;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Fika.Headless.Patches;

public class SettingsPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ClientApplicationInitOperation)
            .GetMethod(nameof(ClientApplicationInitOperation.LoadUserSettings));
    }

    [PatchPrefix]
    public static bool Prefix(ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        __result = NativeTask.FromManaged(MainThreadDispatcher.RunAsync(FikaHeadlessSettingsManager.Initalize));
        return false;
    }
}

public static class FikaHeadlessSettingsManager
{
    private static bool _hasSet;

    internal static bool HasSet => _hasSet;

    /// <summary>
    /// Applies the headless settings when the mode was decided by the selected
    /// profile: that happens after <c>ClientApplicationInitOperation.LoadUserSettings</c>
    /// ran, so the seam this patch replaces was missed. Never called in normal
    /// play - an unarmed plugin does not reach it.
    /// </summary>
    internal static void ApplyDeferred() =>
        MainThreadDispatcher.Post(() => _ = ApplyDeferredAsync());

    private static async Task ApplyDeferredAsync()
    {
        try
        {
            if (_hasSet)
            {
                return;
            }

            SettingsManager gameSettings = Singleton<SettingsManager>.Instantiated ? Singleton<SettingsManager>.Instance : null;
            if (gameSettings == null)
            {
                FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo("[Headless] Settings: the game settings singleton is not up yet, creating it for this headless host");
                gameSettings = await SettingsManager.InstantiateSingleton();
            }

            await SetSettings(gameSettings);
            FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"[Headless] Settings: headless graphics/volume settings applied after the mode gate (applied={_hasSet})");
        }
        catch (Exception exception)
        {
            // Cosmetic and memory-management only: never fail the host for it.
            FikaHeadlessPlugin.FikaHeadlessLogger.LogError($"[Headless] Settings: applying the headless settings after the mode gate failed: {exception}");
        }
    }

    internal static async Task Initalize()
    {
        await EFTDisplayHelper.AwaitScreenReady("LoadUserSettings", false);
        await SetSettings(await SettingsManager.InstantiateSingleton());
        await PlayerVoiceLoader.InstantiateSingleton();
        await Task.Yield();
    }

    internal static async Task SetSettings(SettingsManager gameSettings)
    {
        if (_hasSet)
        {
            return;
        }

        if (gameSettings == null)
        {
            return;
        }

        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo("Setting graphics and volume");

        await gameSettings.Sound.Settings.OverallVolume.SetValue(0);
        await gameSettings.Sound.Settings.VoipEnabled.SetValue(false);

        GraphicsSettingsController.MinFramerateLimit = 1;

        await gameSettings.Graphics.Settings.VSync.SetValue(false);
        await gameSettings.Graphics.Settings.ShadowsQuality.SetValue(0);
        await gameSettings.Graphics.Settings.TextureQuality.SetValue(0);
        await gameSettings.Graphics.Settings.SuperSampling.SetValue(ESamplingMode.DownX05);
        await gameSettings.Graphics.Settings.AnisotropicFiltering.SetValue(AnisotropicFiltering.Disable);
        await gameSettings.Graphics.Settings.OverallVisibility.SetValue(400f);
        await gameSettings.Graphics.Settings.LodBias.SetValue(2);
        await gameSettings.Graphics.Settings.Ssao.SetValue(ESSAOMode.Off);
        await gameSettings.Graphics.Settings.SSR.SetValue(ESSRMode.Off);
        await gameSettings.Graphics.Settings.AntiAliasing.SetValue(EAntialiasingMode.None);
        await gameSettings.Graphics.Settings.NVidiaReflex.SetValue(ENvidiaReflexMode.Off);
        await gameSettings.Graphics.Settings.GrassShadow.SetValue(false);
        await gameSettings.Graphics.Settings.ChromaticAberrations.SetValue(false);
        await gameSettings.Graphics.Settings.Noise.SetValue(false);
        await gameSettings.Graphics.Settings.ZBlur.SetValue(false);
        await gameSettings.Graphics.Settings.HighQualityColor.SetValue(false);
        await gameSettings.Graphics.Settings.SdTarkovStreets.SetValue(true);
        await gameSettings.Graphics.Settings.DLSSMode.SetValue(EDLSSMode.Off);
        await gameSettings.Graphics.Settings.DLSSPreset.SetValue(EDLSSPreset.Default);
        await gameSettings.Graphics.Settings.FSR2Mode.SetValue(EFSR2Mode.Off);
        await gameSettings.Graphics.Settings.FSR3Mode.SetValue(EFSR3Mode.Off);
        await gameSettings.Graphics.Settings.CloudsQuality.SetValue(CloudsMode.Low);
        await gameSettings.Graphics.Settings.VolumetricLight.SetValue(ESSRMode.Off);

        await gameSettings.Graphics.Settings.LobbyFramerate.SetValue(30);
        await gameSettings.Graphics.Settings.GameFramerate.SetValue(FikaHeadlessPlugin.UpdateRate.Value);

        await gameSettings.Game.Settings.EnableHideoutPreload.SetValue(false);
        await gameSettings.Game.Settings.Language.SetValue("en");

        EftResolution resolution = new(1024, 768);
        await gameSettings.Graphics.Settings.DisplaySettings.SetValue(new()
        {
            AspectRatio = EFTAspectHelper.SuitableAspectRatio(ref resolution),
            Display = 0,
            FullScreenMode = FullScreenMode.Windowed,
            Resolution = new(1024, 768)
        });

        await gameSettings.Sound.Save();
        await gameSettings.Graphics.Save();
        await gameSettings.Game.Save();

        var hardSettings = EFTHardSettings.Instance;
        hardSettings.CULL_GROUNDER = 2000f;
        hardSettings.AnimatorCullDistance = 2000f;
        hardSettings.DRAW_DEFERRED_DECALS = false;

        hardSettings.PLAYER_HIT_DECALS_ENEBLED = false;
        hardSettings.HIT_EFFECTS_ENABLED = false;
        hardSettings.HEAT_EMITTER_ENABLED = false;
        hardSettings.SHOT_EFFECTS_ENABLED = false;
        hardSettings.DEFERRED_DECALS_ENABLED = false;
        hardSettings.STATIC_DEFERRED_DECALS_ENABLED = false;

        hardSettings.TriggersCastLayerMask = hardSettings.ServerTriggersCastLayerMask;
        hardSettings.WEAPON_OCCLUSION_LAYERS = hardSettings.WEAPON_OCCLUSION_SERVER_LAYERS;
        hardSettings.LootVolumeForHighQuallityPhysicsClient = hardSettings.LootVolumeForHighQuallityPhysicsServer;

        _hasSet = true;
    }
}
