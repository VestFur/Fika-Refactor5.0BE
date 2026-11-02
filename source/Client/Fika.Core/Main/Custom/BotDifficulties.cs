using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Utils;
using System.Text.Json.Serialization;
using Fika.Core.Networking.Serialization;
using SPTushonka.Common.Http;

namespace Fika.Core.Main.Custom;

public sealed class BotDifficulties : Dictionary<string, BotDifficulties.RoleData>
{
    public BotGlobalsCoreSettings CoreSettings
    {
        get
        {
            FikaGlobals.LogInfo("Retrieving Core settings");
            return _coreSettings ?? null;
        }
    }

    [JsonIgnore]
    private readonly BotGlobalsCoreSettings _coreSettings;

    public BotDifficulties()
    {
        var coreString = RequestHandler.GetJson("/singleplayer/settings/bot/difficulty/core/core");
        _coreSettings = FikaJson.Deserialize<BotGlobalsCoreSettings>(coreString);

        // 调整波次系数，让波次设置真正起作用
        _coreSettings.WAVE_COEF_LOW = 0.5f;
        _coreSettings.WAVE_COEF_HIGH = 1.5f;
    }

    public BotSettingsComponents GetComponent(BotDifficulty botDifficulty, WildSpawnType role)
    {
#if DEBUG
        FikaGlobals.LogInfo($"Retrieving data for: {role}, difficulty: {botDifficulty}");
#endif
        if (TryGetValue(role.ToString().ToLower(), out var value))
        {
            if (value.TryGetValue(botDifficulty.ToString().ToLower(), out var botSettingsComponents))
            {
                return botSettingsComponents;
            }
        }

        FikaGlobals.LogError($"Unable to retrieve difficulty settings for: {role}, difficulty: {botDifficulty}");
        return null;
    }

    public class RoleData : Dictionary<string, BotSettingsComponents>
    {

    }
}
