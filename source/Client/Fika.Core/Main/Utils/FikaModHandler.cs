using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using Comfort.Common;
using Diz.Utils;
using EFT.UI;
using Fika.Core.Main.Patches.Bugfixes;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Serialization;
using SPTushonka.Common.Http;
using SPTushonka.Custom.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 用于校验并处理其他 SPT 模组的类
/// </summary>
public class FikaModHandler
{
    private readonly ManualLogSource _logger = Logger.CreateLogSource("FikaModHandler");

    public bool QuestingBotsLoaded;
    public bool SAINLoaded;
    public bool UIFixesLoaded;

    public SemanticVersioning.Version SPTCoreVersion { get; }

    public FikaModHandler()
    {
        // SPTushonka 5.0.0 用 com.sptushonka.* 形式的 GUID 注册其插件，较早的
        // 构建使用裸的 sptushonka.* 名称。两种都接受，并且绝不解引用
        // 为 null 的 PluginInfo：一次查找失败过去会从这个构造函数抛出，
        // 然后链式加载器就丢弃了整个插件。
        var plugins = IL2CPPChainloader.Instance.Plugins;
        var present = plugins.TryGetValue("com.sptushonka.core", out var pluginInfo)
            || plugins.TryGetValue("sptushonka.core", out pluginInfo);
        SPTCoreVersion = present ? pluginInfo.Metadata.Version : null;
    }

    public async Task VerifyMods(PatchManager manager)
    {
        PluginInfo[] pluginInfos = [.. IL2CPPChainloader.Instance.Plugins.Values];

        // 设置容量，避免为加载了大量模组的用户做不必要的扩容
        Dictionary<string, uint> loadedMods = new(pluginInfos.Length);

        foreach (var pluginInfo in pluginInfos)
        {
            var location = pluginInfo.Location;
            var fileBytes = File.ReadAllBytes(location);
            var crc32 = CRC32C.Compute(fileBytes, 0, fileBytes.Length);
            loadedMods.Add(pluginInfo.Metadata.GUID, crc32);
            _logger.LogInfo($"Loaded plugin: [{pluginInfo.Metadata.Name}] with GUID [{pluginInfo.Metadata.GUID}] and crc32 [{crc32}]");
            if (pluginInfo.Metadata.GUID == "com.fika.core")
            {
                FikaPlugin.Crc32 = crc32;
            }

            CheckSpecialMods(pluginInfo.Metadata.GUID);
        }

        var modValidationRequestJson = FikaJson.Serialize(loadedMods);
        _logger.LogDebug(modValidationRequestJson);

        var validationJson = RequestHandler.PostJson("/fika/client/check/mods", modValidationRequestJson);
        _logger.LogDebug(validationJson);

        var validationResult = FikaJson.Deserialize<ModValidationResponse>(validationJson);
        if (validationResult.Forbidden == null || validationResult.MissingRequired == null || validationResult.HashMismatch == null)
        {
            FikaGlobals.LogError("FikaModHandler::VerifyMods: Response was invalid!");
            MessageBoxHelper.Show("Failed to verify mods with server.\nMake sure that the server mod is installed!", "FIKA ERROR", MessageBoxHelper.MessageBoxType.OK);
            MainThreadDispatcher.Post(Application.Quit);
            return;
        }

        // 若检测到任何错误，我们会打印出发生了什么
        var installationError =
            validationResult.Forbidden.Length > 0 ||
            validationResult.MissingRequired.Length > 0 ||
            validationResult.HashMismatch.Length > 0;

        if (validationResult.Forbidden.Length > 0)
        {
            _logger.LogError($"{validationResult.Forbidden.Length} forbidden mod(s) are loaded, have the server host allow or remove the following mods: {string.Join(", ", validationResult.Forbidden)}");
        }

        if (validationResult.MissingRequired.Length > 0)
        {
            _logger.LogError($"{validationResult.MissingRequired.Length} missing required mod(s), verify the following mods are present: {string.Join(", ", validationResult.MissingRequired)}");
        }

        if (validationResult.HashMismatch.Length > 0)
        {
            _logger.LogWarning($"{validationResult.HashMismatch.Length} mismatched mod(s) are loaded, verify the following mods are up to date with the server host: {string.Join(", ", validationResult.HashMismatch)}");
        }

        if (installationError)
        {
            MainThreadDispatcher.RunAsync(InformInstallationError).Forget();
        }

        await HandleModSpecificPatches(manager);

        return;
    }

    private Task HandleModSpecificPatches(PatchManager manager)
    {
        // 只有在 UI Fixes 未加载时我们才想加载这个
        if (!UIFixesLoaded)
        {
            _logger.LogInfo("UI Fixes is not loaded, enabling PartyInfoPanel fix");
            manager.EnablePatch(new PartyInfoPanel_CG_method_3_Patch());
        }

        return Task.CompletedTask;
    }

    private async Task InformInstallationError()
    {
        while (!Singleton<PreloaderUI>.Instantiated)
        {
            await Task.Delay(250);
        }

        ShowModErrorMessage();
    }

    private void ShowModErrorMessage()
    {
        const string message = "Your client doesn't meet server requirements, check logs for more details";

        // -1f 的时间让这条消息永久显示
        var errorScreen = Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen("INSTALLATION ERROR", message,
            ErrorScreen.EButtonType.QuitButton, -1f);

        Action quitAction = Application.Quit;
        errorScreen.OnAccept += quitAction;
        errorScreen.OnDecline += quitAction;
        errorScreen.OnClose += quitAction;
        errorScreen.OnCloseSilent += quitAction;

        MainThreadDispatcher.RunAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            Application.Quit();
        }).Forget();
    }

    private void CheckSpecialMods(string key)
    {
        switch (key)
        {
            case "com.DanW.QuestingBots":
                {
                    QuestingBotsLoaded = true;
                    break;
                }
            case "me.sol.sain":
                {
                    SAINLoaded = true;
                    break;
                }
            case "com.tyfon.uifixes":
                {
                    UIFixesLoaded = true;
                    break;
                }
        }
    }
}
