using FikaServer.Models.Fika;
using FikaServer.Models.Fika.Config;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace FikaServer.Services.Headless;

[Injectable(InjectionType.Singleton)]
public class HeadlessProfileService(ISptLogger<HeadlessProfileService> logger, SaveServer saveServer, FikaPaths fikaPaths, FikaServerConfig fikaServerConfig,
    CoreConfig coreConfig, HashUtil hashUtil, ProfileController profileController, InventoryHelper inventoryHelper,
    JsonUtil jsonUtil)
{
    public List<SptProfile> HeadlessProfiles { get; set; } = [];

    private static readonly MongoId HEAD_USEC_4 = new("5fdb4139e4ed5b5ea251e4ed"); // _parent: 5cc085e214c02e000c6bea67
    private static readonly MongoId VOICE_USEC_4 = new("6284d6a28e4092597733b7a6"); // _parent: 5fc100cf95572123ae738483

    public async Task OnPostLoadAsync(CancellationToken cancellationToken = default)
    {
        LoadHeadlessProfiles();

        if (logger.IsLogEnabled(LogLevel.Information))
        {

            logger.Log(LogLevel.Information, $"Found {HeadlessProfiles.Count} headless profiles");
        }

        var profileAmount = fikaServerConfig.Headless.Profiles.Amount;

        if (HeadlessProfiles.Count < profileAmount)
        {
            var createdProfiles = await CreateHeadlessProfiles(profileAmount);

            if (logger.IsLogEnabled(LogLevel.Information))
            {
                logger.Log(LogLevel.Information, $"Created {createdProfiles.Count} headless client profiles!");
            }
        }

        // 避免 headless 抬高已解锁成就的百分比
        foreach (var headlessProfile in HeadlessProfiles)
        {
            coreConfig.Features.AchievementProfileIdBlacklist.Add(headlessProfile.ProfileInfo.ProfileId);

            // 已有的 headless profile 同样必须跳过 SPT 5.0 的首次启动教程。
            if (headlessProfile.SptData.TutorialCompleted != true)
            {
                headlessProfile.SptData.TutorialCompleted = true;
                await saveServer.SaveProfileAsync(headlessProfile.ProfileInfo.ProfileId.Value);
            }

            // 保持 HeadlessConfig.json 旁边那个可双击的启动辅助脚本同步更新，包括
            // 那些在辅助脚本存在之前就已创建的 profile。重新生成它是幂等的。
            GenerateLaunchScript(headlessProfile.ProfileInfo.ProfileId.Value);
        }
    }

    internal async Task<List<SptProfile>> CreateHeadlessProfiles(int amount, bool ignoreCurrent = false)
    {
        var profileCount = HeadlessProfiles.Count;
        var profileAmountToCreate = ignoreCurrent ? amount : amount - profileCount;
        List<SptProfile> createdProfiles = [];
        for (var i = 0; i < profileAmountToCreate; i++)
        {
            var profile = await CreateHeadlessProfile();
            createdProfiles.Add(profile);
            HeadlessProfiles.Add(profile);
            GenerateLaunchScript(profile.ProfileInfo.ProfileId.Value);
        }

        return createdProfiles;
    }

    private void LoadHeadlessProfiles()
    {
        HeadlessProfiles = [.. saveServer.GetProfiles().Values
            .Where(x => x.IsHeadlessProfile())];
    }

    private async Task<SptProfile> CreateHeadlessProfile()
    {
        // 生成唯一的用户名
        var username = $"headless_{new MongoId()}";
        // 随机版本。无所谓
        const string edition = "Standard";

        // 创建 mini profile
        string profileId = await CreateMiniProfile(username, edition);

        // 随机角色配置。无所谓。
        ProfileCreateRequestData newProfileData = new()
        {
            Side = "usec",
            Nickname = username, // 用用户名作为昵称，确保其唯一。
            HeadId = HEAD_USEC_4,
            VoiceId = VOICE_USEC_4
        };

        return await CreateFullProfile(newProfileData, profileId);
    }

    private void GenerateLaunchScript(MongoId profileId)
    {
        var scriptsPath = fikaPaths.ScriptsPath;
        var newFolderPath = Path.Combine(scriptsPath, profileId);

        try
        {
            Directory.CreateDirectory(newFolderPath);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to create headless launch settings for {profileId}", ex);
            return;
        }

        var backendUrl = fikaServerConfig.Headless.Scripts.ForceIp;
        backendUrl = string.IsNullOrEmpty(backendUrl) ? "https://127.0.0.1:6969" : backendUrl;

        if (!Uri.TryCreate(backendUrl, UriKind.Absolute, out var uri))
        {
            logger.Error($"Could not parse {backendUrl} as a valid URL, please delete the headless profile and try again.");
            return;
        }

        var startMinimized = ResolveStartMinimized(newFolderPath);
        var display = ResolveDisplay(newFolderPath);

        var launchSettings = new HeadlessLaunchSettings()
        {
            SettingsVersion = HeadlessLaunchSettings.CurrentSettingsVersion,
            ProfileId = profileId,
            BackendUrl = uri,
            StartMinimized = startMinimized.Value,
            Display = display.Value
        };

        var serialized = jsonUtil.Serialize(launchSettings, true);
        var newFile = Path.Combine(newFolderPath, "HeadlessConfig.json");
        File.WriteAllText(newFile, serialized);
        logger.Info($"Created new launch settings in {newFile} for headless profile {profileId}");
        logger.Info($"[Headless] profile {profileId}: StartMinimized={startMinimized.Value.ToString().ToLowerInvariant()} " +
            $"({startMinimized.Source})");
        logger.Info($"[Headless] profile {profileId}: Display={display.Value} ({display.Source})");

        WriteLaunchHelper(newFolderPath, profileId, uri, startMinimized.Value, display.Value);
    }

    /// <summary>
    /// 判定该 profile 的专用主机是否以最小化方式启动。
    /// 已存在的 <c>HeadlessConfig.json</c> 优先，因此运维人员修改过的值会在
    /// 服务器重启后继续生效；其他所有情况 —— 新建 profile、文件缺失、文件不可读，
    /// 或由缺少该属性的旧版本服务器写出的文件 —— 都默认为
    /// 最小化，这也正是 <see cref="HeadlessLaunchSettings.StartMinimized"/> 所声明的。
    /// </summary>
    private (bool Value, string Source) ResolveStartMinimized(string folderPath)
    {
        var configFile = Path.Combine(folderPath, "HeadlessConfig.json");
        if (!File.Exists(configFile))
        {
            return (true, "default for a new profile");
        }

        var relative = ToModRelativePath(configFile);
        try
        {
            var existing = jsonUtil.Deserialize<HeadlessLaunchSettings>(File.ReadAllText(configFile));
            if (existing is not null)
            {
                // 只有本 schema 写出的文件才携带明确的选择。旧版本构建会
                // 无条件写入 StartMinimized=false，若遵从它会让所有已有
                // profile 永久保持还原状态。
                if (existing.SettingsVersion >= HeadlessLaunchSettings.CurrentSettingsVersion)
                {
                    return (existing.StartMinimized, $"kept from {relative}");
                }

                return (true, $"default ({relative} was written before the window state was consumed)");
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[Headless] {relative} could not be read ({ex.Message}), regenerating it with StartMinimized=true");
        }

        return (true, $"default ({relative} carried no usable value)");
    }

    /// <summary>
    /// 判定该 profile 的主机以何种方式呈现。<c>headless.display.mode</c> 存在时优先，
    /// 因此修改 <c>fika.jsonc</c> 并重启服务器会重写每个 profile 的
    /// <c>StartHeadless.cmd</c>（本方法与创建 profile 及其辅助脚本的
    /// 位置相同）。已指定模式的配置文件仅在键缺失时才会被遵从，
    /// 因此运维人员也可以通过删除服务器级的值来固定单个 profile。
    /// </summary>
    private (string Value, string Source) ResolveDisplay(string folderPath)
    {
        var raw = fikaServerConfig.Headless.Display?.Mode;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var configured = HeadlessDisplayModes.Normalize(raw);
            if (configured == null)
            {
                logger.Warning($"[Headless] headless.display.mode '{raw}' is not Console/Hidden, using {HeadlessDisplayModes.Console}");
                configured = HeadlessDisplayModes.Console;
            }

            return (configured, $"headless.display.mode ('{raw}')");
        }

        var configFile = Path.Combine(folderPath, "HeadlessConfig.json");
        if (!File.Exists(configFile))
        {
            return (HeadlessDisplayModes.Console, "default for a new profile (headless.display.mode is not set)");
        }

        var relative = ToModRelativePath(configFile);
        try
        {
            var existing = jsonUtil.Deserialize<HeadlessLaunchSettings>(File.ReadAllText(configFile));
            var kept = HeadlessDisplayModes.Normalize(existing?.Display);
            if (kept != null)
            {
                return (kept, $"kept from {relative} (headless.display.mode is not set)");
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[Headless] {relative} could not be read ({ex.Message}), regenerating it with the default display mode");
        }

        return (HeadlessDisplayModes.Console, $"default ({relative} carried no usable value)");
    }

    /// <summary>
    /// 在 HeadlessConfig.json 旁边写入可双击运行的 <c>StartHeadless.cmd</c>。SPT
    /// 启动器从不列出 headless profile，因此该辅助脚本是启动
    /// 专用主机受支持的方式：它执行启动器为普通 profile 使用的完全相同的命令行，外加
    /// Console 显示模式下的 <c>-batchmode -nographics</c>。
    /// </summary>
    private void WriteLaunchHelper(string folderPath, MongoId profileId, Uri backendUrl, bool startMinimized, string displayMode)
    {
        var newFile = Path.Combine(folderPath, HeadlessLaunchHelper.FileName);

        try
        {
            // Uri.ToString() 总是把仅含 authority 的 url 渲染为带结尾斜杠的形式
            // （"https://host:port/"），而游戏会自行把 "/singleplayer/..." 追加到
            // BackendUrl 之后，因此原样传递会产生类似
            // "//singleplayer/bundles" 的请求，服务器以 404 响应 —— 这正是
            // 新建 headless 主机过去会产生的错误风暴。HeadlessConfig.json 从不存在
            // 此问题，因为 json 序列化器写入 Uri 时不带斜杠，所以此处
            // 也去掉它，以保持两个文件一致。
            File.WriteAllText(newFile, HeadlessLaunchHelper.Build(profileId, backendUrl.ToString().TrimEnd('/'), startMinimized, displayMode));
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to create the headless launch helper for {profileId}", ex);
            return;
        }

        logger.Info($"[Headless] wrote launch helper {ToModRelativePath(newFile)} " +
            $"(Display={displayMode}, StartMinimized={(startMinimized ? "true" : "false")})");
    }

    /// <summary>
    /// 以正斜杠形式渲染相对于 mod 根目录的路径，使日志行永不泄露
    /// 与具体机器相关的安装位置。
    /// </summary>
    private string ToModRelativePath(string path)
    {
        return Path.GetRelativePath(fikaPaths.ModPath, path).Replace('\\', '/');
    }

    private async Task<MongoId> CreateMiniProfile(string username, string edition)
    {
        MongoId profileId = new();
        MongoId scavId = new();

        Info newProfile = new()
        {
            ProfileId = profileId,
            ScavengerId = scavId,
            Aid = hashUtil.GenerateAccountId(),
            Username = username,
            IsWiped = true,
            Edition = edition
        };

        saveServer.CreateProfile(newProfile);

        await saveServer.LoadProfileAsync(profileId);
        await saveServer.SaveProfileAsync(profileId);

        return profileId;
    }

    private async Task<SptProfile> CreateFullProfile(ProfileCreateRequestData profileData, MongoId profileId)
    {
        await profileController.CreateProfile(profileData, profileId);

        var profile = saveServer.GetProfile(profileId)
            ?? throw new NullReferenceException("CreateFullProfile:: Could not find profile");

        ClearUnecessaryHeadlessItems(profile.CharacterData.PmcData, profileId);

        profile.CharacterData.PmcData.Info.MemberCategory = MemberCategory.UnitTest;
        profile.SptData.TutorialCompleted = true;

        await saveServer.SaveProfileAsync(profileId);

        return profile;
    }

    private void ClearUnecessaryHeadlessItems(PmcData pmcProfile, MongoId sessionId)
    {
        if (pmcProfile == null)
        {
            throw new NullReferenceException("ClearUnecessaryHeadlessItems:: PmcProfile was null");
        }

        foreach (var item in GetAllHeadlessItems(pmcProfile))
        {
            inventoryHelper.RemoveItem(pmcProfile, item, sessionId);
        }

        pmcProfile.Inventory.FastPanel = [];
    }

    private List<string?> GetAllHeadlessItems(PmcData pmcProfile)
    {
        var inventoryItems = pmcProfile.Inventory?.Items ?? [];
        string? equipmentRootId = pmcProfile.Inventory?.Equipment;
        string? stashRootId = pmcProfile.Inventory?.Stash;

        return [.. inventoryItems
            .Where(x => x.ParentId == equipmentRootId || x.ParentId == stashRootId)
            .Select(x => x.Id)];
    }
}
