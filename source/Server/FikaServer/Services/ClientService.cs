using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Routes.Client;
using FikaServer.Models.Fika.Routes.Client.Check;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class ClientService(ISptLogger<ClientService> logger, SaveServer saveServer,
    ClientModHashesService fikaClientModHashesService, FikaServerConfig fikaServerConfig)
{
    private readonly List<string> _requiredMods = ["com.fika.core", "com.sptushonka.custom", "com.sptushonka.singleplayer", "com.sptushonka.core", "com.sptushonka.debugging"];
    private readonly HashSet<string> _allowedMods = ["com.bepis.bepinex.configurationmanager", "com.fika.headless"];
    private bool _hasRequiredOrOptionalMods;

    public void OnPreLoad()
    {
        var sanitizedRequiredMods = FilterEmptyMods(fikaServerConfig.Client.Mods.Required);
        var sanitizedOptionalMods = FilterEmptyMods(fikaServerConfig.Client.Mods.Optional);

        if (sanitizedRequiredMods.Count > 0 || sanitizedOptionalMods.Count > 0)
        {
            _hasRequiredOrOptionalMods = true;
        }

        foreach (var mod in sanitizedRequiredMods)
        {
            _requiredMods.Add(mod);
            _allowedMods.Add(mod);
        }

        // 当可选/必需列表启用强制校验时，内置的必需项也必须被允许，
        // 否则合法的 SPT 插件会被禁止。
        _allowedMods.UnionWith(_requiredMods);
        _allowedMods.UnionWith(sanitizedOptionalMods);
    }

    protected List<string> FilterEmptyMods(List<string> list)
    {
        return [.. list.Where(str => !string.IsNullOrWhiteSpace(str))];
    }

    public FikaConfigClient GetClientConfig()
    {
        return fikaServerConfig.Client;
    }

    public bool GetIsItemSendingAllowed()
    {
        return fikaServerConfig.Server.AllowItemSending;
    }

    public FikaConfigNatPunchServer GetNatPunchServerConfig()
    {
        return fikaServerConfig.NatPunchServer;
    }

    public VersionCheckResponse GetVersion()
    {
        return new VersionCheckResponse
        {
            Version = FikaModMetadata.FikaVersion
        };
    }

    public FikaCheckModResponse GetCheckModsResponse(FikaCheckModRequestData request, MongoId sessionId)
    {
        FikaCheckModResponse mismatchedMods = new()
        {
            Forbidden = [],
            MissingRequired = [],
            HashMismatch = []
        };

        if (fikaServerConfig.Server.LogClientModsInConsole)
        {
            var username = saveServer.GetProfile(sessionId).ProfileInfo?.Username;

            logger.Info($"{username} ({sessionId}) connected with {request.Count} client mods: {string.Join(", ", request.Keys)}");
        }

        // 若未做任何配置，则允许所有 mod
        if (!_hasRequiredOrOptionalMods)
        {
            return mismatchedMods;
        }

        // 先检查缺失的必需 mod
        foreach (var pluginId in _requiredMods)
        {
            if (!request.ContainsKey(pluginId))
            {
                mismatchedMods.MissingRequired.Add(pluginId);
            }
        }

        // 已缺失必需 mod，无需再检查其他内容
        if (mismatchedMods.MissingRequired.Count > 0)
        {
            return mismatchedMods;
        }

        foreach (var pluginId in request.Keys)
        {
            var hash = request[pluginId];

            // 检查该 mod 是否不被允许
            if (!_allowedMods.Contains(pluginId))
            {
                mismatchedMods.Forbidden.Add(pluginId);
                continue;
            }

            // 首个请求至少会填入所有必需 mod 的哈希；随后来自不同客户端的请求会补充首个请求未添加的可选 mod，否则将基于首个请求的数据进行校验
            if (!fikaClientModHashesService.MatchesOrAdd(pluginId, hash))
            {
                mismatchedMods.HashMismatch.Add(pluginId);
            }
        }

        return mismatchedMods;
    }

    public DownloadProfileResponse? GetProfileBySessionID(MongoId sessionId)
    {
        var profile = saveServer.GetProfile(sessionId);

        if (profile == null)
        {
            logger.Info($"{sessionId} wants to download their profile but we don't have it");
            return null;

        }

        var path = Path.Combine("user/profileData/", sessionId);
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path);
            Dictionary<string, string> allData = new(files.Length);
            foreach (var fullFile in files)
            {
                var modData = File.ReadAllText(fullFile);
                var name = Path.GetFileNameWithoutExtension(fullFile);
                if (modData != null)
                {
                    allData.Add(name, modData);
                }
            }

            logger.Success($"{sessionId} has downloaded their profile with {allData.Count} custom mod data");
            return new()
            {
                Profile = profile,
                ModData = allData
            };
        }

        logger.Success($"{sessionId} has downloaded their profile");
        return new()
        {
            Profile = profile
        };
    }
}
