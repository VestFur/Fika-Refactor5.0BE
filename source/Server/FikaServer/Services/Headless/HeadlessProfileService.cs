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

        // Stop headless from adding up to the percentage of achievements unlocked
        foreach (var headlessProfile in HeadlessProfiles)
        {
            coreConfig.Features.AchievementProfileIdBlacklist.Add(headlessProfile.ProfileInfo.ProfileId);

            // Existing headless profiles must bypass the SPT 5.0 first-launch tutorial too.
            if (headlessProfile.SptData.TutorialCompleted != true)
            {
                headlessProfile.SptData.TutorialCompleted = true;
                await saveServer.SaveProfileAsync(headlessProfile.ProfileInfo.ProfileId.Value);
            }

            // Keep the double-clickable launch helper next to HeadlessConfig.json in sync, including
            // for profiles that were created before the helper existed. Regenerating it is idempotent.
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
        // Generate a unique username
        var username = $"headless_{new MongoId()}";
        // Random edition. Doesn't matter
        const string edition = "Standard";

        // Create mini profile
        string profileId = await CreateMiniProfile(username, edition);

        // Random character configs. Doesn't matter.
        ProfileCreateRequestData newProfileData = new()
        {
            Side = "usec",
            Nickname = username, // Use the username as the nickname to ensure it is unique.
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

        var launchSettings = new HeadlessLaunchSettings()
        {
            SettingsVersion = HeadlessLaunchSettings.CurrentSettingsVersion,
            ProfileId = profileId,
            BackendUrl = uri,
            StartMinimized = startMinimized.Value
        };

        var serialized = jsonUtil.Serialize(launchSettings, true);
        var newFile = Path.Combine(newFolderPath, "HeadlessConfig.json");
        File.WriteAllText(newFile, serialized);
        logger.Info($"Created new launch settings in {newFile} for headless profile {profileId}");
        logger.Info($"[Headless] profile {profileId}: StartMinimized={startMinimized.Value.ToString().ToLowerInvariant()} " +
            $"({startMinimized.Source})");

        WriteLaunchHelper(newFolderPath, profileId, uri, startMinimized.Value);
    }

    /// <summary>
    /// Resolves whether this profile's dedicated host comes up minimised.
    /// An existing <c>HeadlessConfig.json</c> wins, so an operator who edited the value keeps it
    /// across server restarts; every other case - a new profile, a missing file, an unreadable
    /// file, or a file written by an older server build without the property - defaults to
    /// minimised, which is also what <see cref="HeadlessLaunchSettings.StartMinimized"/> declares.
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
                // Only a file this schema wrote carries a deliberate choice. Older builds wrote
                // StartMinimized=false unconditionally, so honouring it would keep every existing
                // profile restored forever.
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
    /// Writes the double-clickable <c>StartHeadless.cmd</c> next to HeadlessConfig.json. The SPT
    /// launcher never lists headless profiles, so this helper is the supported way to start a
    /// dedicated host: it runs the exact command line the launcher uses for a normal profile.
    /// </summary>
    private void WriteLaunchHelper(string folderPath, MongoId profileId, Uri backendUrl, bool startMinimized)
    {
        var newFile = Path.Combine(folderPath, HeadlessLaunchHelper.FileName);

        try
        {
            // Uri.ToString() always renders an authority-only url with a trailing slash
            // ("https://host:port/"), and the game appends its own "/singleplayer/..." to the
            // BackendUrl, so passing it through unchanged produced requests like
            // "//singleplayer/bundles" that the server answers with 404 - which is exactly the
            // error storm a fresh headless host used to produce. HeadlessConfig.json never had
            // this problem because the json serializer writes the Uri without the slash, so trim
            // it here as well to keep both files identical.
            File.WriteAllText(newFile, HeadlessLaunchHelper.Build(profileId, backendUrl.ToString().TrimEnd('/'), startMinimized));
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to create the headless launch helper for {profileId}", ex);
            return;
        }

        logger.Info($"[Headless] wrote launch helper {ToModRelativePath(newFile)} " +
            $"(StartMinimized={(startMinimized ? "true" : "false")})");
    }

    /// <summary>
    /// Renders a path relative to the mod root with forward slashes, so log lines never leak the
    /// machine specific install location.
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
