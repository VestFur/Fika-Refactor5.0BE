using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Routes.Client;
using FikaServer.Models.Fika.Routes.Client.Check;
using FikaServer.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FikaServer.Controllers;

[Injectable]
public class ClientController(ClientService fikaClientService)
{
    /// <summary>
    /// 处理 /fika/client/config
    /// </summary>
    public string HandleClientConfig()
    {
        var clientConfig = fikaClientService.GetClientConfig();

        // 此处有坑，严格来说它并不在客户端配置里，嗯，它以前是在的……但最终决定还是让这项配置
        // 与 'sentItemsLoseFIR' 放在一起，这样用户更容易找到这两个选项。
        // 把它留在这里，因为它本来就只应作为 'client' 配置，而且确实只在客户端使用。
        string config = JsonSerializer.Serialize(clientConfig);
        JsonNode configObject = JsonNode.Parse(config)!.AsObject();
        configObject["allowItemSending"] = fikaClientService.GetIsItemSendingAllowed();

        return configObject.ToJsonString();
    }

    /// <summary>
    /// 处理 /fika/natpunchserver/config
    /// </summary>
    public FikaConfigNatPunchServer HandleNatPunchServerConfig()
    {
        return fikaClientService.GetNatPunchServerConfig();
    }

    /// <summary>
    /// 处理 /fika/client/check/mods
    /// </summary>
    public FikaCheckModResponse HandleCheckMods(FikaCheckModRequestData request, MongoId sessionId)
    {
        return fikaClientService.GetCheckModsResponse(request, sessionId);
    }

    /// <summary>
    /// 处理 /fika/profile/download
    /// </summary>
    public DownloadProfileResponse? HandleProfileDownload(MongoId sessionId)
    {
        return fikaClientService.GetProfileBySessionID(sessionId);
    }

    /// <summary>
    /// 处理 /fika/client/check/version
    /// </summary>
    public VersionCheckResponse HandleVersionCheck()
    {
        return fikaClientService.GetVersion();
    }
}
