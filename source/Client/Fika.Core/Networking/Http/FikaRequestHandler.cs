using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using Fika.Core.Main.Custom;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Models.Admin;
using Fika.Core.Networking.Models.Headless;
using Fika.Core.Networking.Models.Presence;
using Fika.Core.UI.Models;
using Fika.Core.Networking.Serialization;
using SPTushonka.Common.Http;

namespace Fika.Core.Networking.Http;

public static class FikaRequestHandler
{
    private static readonly Client _httpClient;

    static FikaRequestHandler()
    {
        _httpClient = RequestHandler.HttpClient;
    }

    /// <summary>
    /// 解析本机在主持 raid 时宣告的 WAN 地址。
    ///
    /// 当没有提供者应答时返回 <see langword="null"/>。这是一种降级，
    /// 而绝不是失败：该地址恰好只被一个调用方读取（FikaServer.Init，即 raid
    /// 主机），而客户端加入路径不触碰它。在此处抛异常过去会把一次
    /// 离线安装变成一行 "RunChecks" 错误，从而掩盖真正的问题。
    /// </summary>
    public static async Task<IPAddress> GetPublicIP()
    {
        var client = _httpClient.HttpClient;
        string[] ipv4Urls = [
            "https://api.ipify.org/",
            "https://checkip.amazonaws.com/",
            "https://ipv4.icanhazip.com/"
        ];

        // 纯 IPv6 网络完全没有 IPv4 出口，因此对通过 IPv6 应答的提供者
        // 再做一轮遍历，可以让 raid 主机在那里仍然可被宣告，
        // 而不是白白消耗三个超时然后失败。
        string[] ipv6Urls = [
            "https://api64.ipify.org/",
            "https://ipv6.icanhazip.com/",
            "https://ifconfig.co/ip"
        ];

        // 第一个失败的提供者就解释了整个查询，而它之后每个提供者
        // 通常重复同样的传输失败。因此按原因只报告一次：
        // 第一次失败保留完整证据（消息、堆栈、内部异常），其余各只是
        // 一行且不带堆栈。在此之前，一次被重置的 TLS 握手会为每个
        // 提供者产生一个堆栈块，再加上 HttpClient 内部每次抛出的首次机会异常条目
        // （2026-10-10，analysis/log-noise-cleanup：api.ipify.org 重置了握手，10 条首次机会
        // 条目，单个提供者失败约 54 行堆栈）。
        string firstFailure = null;

        void ReportFailure(string url, AddressFamily family, string reason, Exception exception)
        {
            if (firstFailure == null)
            {
                firstFailure = url + ": " + reason;
                if (exception == null)
                {
                    FikaGlobals.LogWarning($"Request to {url} timed out after 5 seconds.");
                    
                }
                else
                {
                    FikaGlobals.LogWarning($"Could not get public IP address from [{url}]. Error: {reason}");
                    
                }

                return;
            }

            
        }

        async Task<IPAddress> Query(string[] urls, AddressFamily family)
        {
            foreach (var url in urls)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                try
                {
                    var response = await client.GetAsync(url, cts.Token);
                    response.EnsureSuccessStatusCode();

                    var ipString = (await response.Content.ReadAsStringAsync()).Trim();
                    if (!IPAddress.TryParse(ipString, out var ipAddress))
                    {
                        continue;
                    }

                    if (ipAddress.AddressFamily == family)
                    {
                        return ipAddress;
                    }

                    FikaGlobals.LogWarning($"Provider {url} answered with a {ipAddress.AddressFamily} address ({ipAddress}), expected {family}");
                }
                catch (OperationCanceledException)
                {
                    ReportFailure(url, family, "timed out after 5 seconds", null);
                }
                catch (Exception ex)
                {
                    ReportFailure(url, family, ex.GetBaseException().Message, ex);
                }
            }

            return null;
        }

        var ipv4 = await Query(ipv4Urls, AddressFamily.InterNetwork);
        if (ipv4 != null)
        {
            return ipv4;
        }

        if (FikaPlugin.Instance.Settings.UseIPv6.Value)
        {
            var ipv6 = await Query(ipv6Urls, AddressFamily.InterNetworkV6);
            if (ipv6 != null)
            {
                FikaGlobals.LogInfo($"Announcing the public IPv6 address {ipv6} for this raid host");
                return ipv6;
            }
        }

        const string failure = "Could not retrieve or parse the external IP address from any provider.";
        // 由错误降级为警告，并在同一行给出首个原因：没有提供者应答会
        // 让 raid 主机失去其宣告的 WAN 地址（设置 'Force IP' 来宣告一个），而
        // 加入路径不受影响。原因本身才是测试者需要的，而它过去被埋在
        // 每个提供者的堆栈块之下。
        FikaGlobals.LogWarning(firstFailure == null ? failure : failure + " First failure: " + firstFailure);
        
        return null;
    }

    private static byte[] EncodeBody<T>(T o)
    {
        var serialized = FikaJson.Serialize(o);
        return Encoding.UTF8.GetBytes(serialized);
    }

    private static T DecodeBody<T>(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        return FikaJson.Deserialize<T>(json);
    }

    private static async Task<T> GetJsonAsync<T>(string path)
    {
        var response = await _httpClient.GetAsync(path);
        return DecodeBody<T>(response);
    }

    private static T GetJson<T>(string path)
    {
        // 只有传输在线程外运行。游戏对象必须在
        // 调用方的线程上解码，尤其是当 Plugin.Load 阻塞 Unity 循环时。
        var response = Task.Run(() => _httpClient.GetAsync(path))
            .GetAwaiter()
            .GetResult();
        return DecodeBody<T>(response);
    }

    private static async Task<T2> PostJsonAsync<T1, T2>(string path, T1 o)
    {
        var data = EncodeBody(o);
        var response = await _httpClient.PostAsync(path, data);
        return DecodeBody<T2>(response);
    }

    private static T2 PostJson<T1, T2>(string path, T1 o)
    {
        var data = EncodeBody(o);
        var response = Task.Run(() => _httpClient.PostAsync(path, data))
            .GetAwaiter()
            .GetResult();
        return DecodeBody<T2>(response);
    }

    private static async Task<byte[]> PutJsonAsync<T>(string path, T o)
    {
        var data = EncodeBody(o);
        return await _httpClient.PutAsync(path, data);
    }

    private static byte[] PutJson<T>(string path, T o)
    {
        return Task.Run(() => PutJsonAsync(path, o))
            .GetAwaiter()
            .GetResult();
    }

    public static BotDifficulties GetBotDifficulties()
    {
        return GetJson<BotDifficulties>("/singleplayer/settings/bot/difficulties/");
    }

    public static ClientConfigModel GetClientConfig()
    {
        return GetJson<ClientConfigModel>("/fika/client/config");
    }

    public static NatPunchServerConfigModel GetNatPunchServerConfig()
    {
        return GetJson<NatPunchServerConfigModel>("/fika/natpunchserver/config");
    }

    public static RestartAfterRaidAmountModel GetHeadlessRestartAfterRaidAmount()
    {
        return GetJson<RestartAfterRaidAmountModel>("/fika/headless/restartafterraidamount");
    }

    public static async Task UpdatePing(PingRequest data)
    {
        await PutJsonAsync("/fika/update/ping", data);
    }

    public static async Task UpdateSetStatus(SetStatusModel data)
    {
        await PutJsonAsync("/fika/update/setstatus", data);
    }

    public static async Task UpdatePlayerSpawn(PlayerSpawnRequest data)
    {
        await PutJsonAsync("/fika/update/playerspawn", data);
    }

    public static void UpdateSetHost(SetHostRequest data)
    {
        PutJson("/fika/update/sethost", data);
    }

    public static void RaidLeave(PlayerLeftRequest data)
    {
        PutJson("/fika/raid/leave", data);
    }

    public static CreateMatch RaidJoin(MatchJoinRequest data)
    {
        return PostJson<MatchJoinRequest, CreateMatch>("/fika/raid/join", data);
    }

    public static void UpdateAddPlayer(AddPlayerRequest data)
    {
        PutJson("/fika/update/addplayer", data);
    }

    public static async Task RaidCreate(CreateMatch data)
    {
        // SPT 5.0 服务器对 raid 创建刻意返回空响应体。
        // 把已完成的请求视为成功，并让传输
        // 异常传播到调用方。
        await PutJsonAsync("/fika/raid/create", data);
    }

    /// <summary>
    /// 本客户端所指向的后端地址（例如 https://127.0.0.1:6969），
    /// 仅用于诊断。该值由启动器掌管；它会打印在每一次主机查询
    /// 失败旁边，以便报告说明询问的是哪个服务器。
    /// </summary>
    public static string ServerHost
    {
        get
        {
            try
            {
                var host = RequestHandler.Host;
                return string.IsNullOrWhiteSpace(host) ? "<empty>" : host;
            }
            catch (Exception exception)
            {
                return "<unavailable: " + exception.GetBaseException().Message + ">";
            }
        }
    }

    /// <summary>
    /// 最后一次 <see cref="GetHost"/> 应答的原始 JSON，或 <see langword="null"/>（当该调用
    /// 没有产生任何应答时）。当某个主机应答却没有地址时，加入路径会打印它：
    /// 仅凭解码后的 DTO 无法看出服务器发送的是空列表、针对未知对局的 "null" 响应体，
    /// 还是完全不同的东西。
    /// </summary>
    public static string LastGetHostResponseBody { get; private set; }

    /// <summary>
    /// 最后一次 <see cref="GetHost"/> 调用的传输级失败，或 <see langword="null"/>（当它
    /// 成功返回时）。HTTP 层（SPTushonka 的 Client.SendAsync）对每个
    /// 非 2xx 应答都抛出 "Http response status code: {code}"，且从不把响应体交回，因此
    /// 状态只能在这里以这段文本的形式可见。单独读取状态将需要在
    /// 该层内部做改动；为那个真正重要的调用提供一条原始响应体通道就避免了它。
    /// </summary>
    public static string LastGetHostFailure { get; private set; }

    public static GetHostResponse GetHost(GetHostRequest data)
    {
        LastGetHostResponseBody = null;
        LastGetHostFailure = null;

        var payload = EncodeBody(data);
        byte[] response;
        try
        {
            // 与 PostJson 相同的传输路径：只有传输在线程外运行。
            response = Task.Run(() => _httpClient.PostAsync("/fika/raid/gethost", payload))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            LastGetHostFailure = exception.GetBaseException().Message;
            throw;
        }

        // 在解码之前保存，这样反序列化失败的响应体仍可读取。
        LastGetHostResponseBody = Encoding.UTF8.GetString(response);
        return FikaJson.Deserialize<GetHostResponse>(LastGetHostResponseBody);
    }

    public static LobbyEntry[] LocationRaids(RaidSettings data)
    {
        return PostJson<RaidSettings, LobbyEntry[]>("/fika/location/raids", data);
    }

    public static Dictionary<string, string> AvailableReceivers(AvailableReceiversRequest data)
    {
        return PostJson<AvailableReceiversRequest, Dictionary<string, string>>("/fika/senditem/availablereceivers", data);
    }

    public static async Task<RaidSettingsResponse> GetRaidSettings(RaidSettingsRequest data)
    {
        return await PostJsonAsync<RaidSettingsRequest, RaidSettingsResponse>("/fika/raid/getsettings", data);
    }

    public static async Task<DownloadProfileResponse> GetProfile()
    {
        return await GetJsonAsync<DownloadProfileResponse>("/fika/profile/download");
    }

    public static async Task<StartHeadlessResponse> StartHeadless(StartHeadlessRequest request)
    {
        return await PostJsonAsync<StartHeadlessRequest, StartHeadlessResponse>("/fika/raid/headless/start", request);
    }

    public static AvailableHeadlessClientsRequest[] GetAvailableHeadlesses()
    {
        return GetJson<AvailableHeadlessClientsRequest[]>("/fika/headless/available");
    }

    public static async Task RegisterPlayer(RegisterPlayerRequest request)
    {
        await PutJsonAsync("/fika/raid/registerPlayer", request);
    }

    public static async Task PlayerDied(AddPlayerRequest request)
    {
        await PutJsonAsync("/fika/update/playerdied", request);
    }

    public static CheckVersionResponse CheckServerVersion()
    {
        return GetJson<CheckVersionResponse>("/fika/client/check/version");
    }

    public static FikaPlayerPresence[] GetPlayerPresences()
    {
        return GetJson<FikaPlayerPresence[]>("/fika/presence/get");
    }

    public static void SetPresence(FikaSetPresence data)
    {
        PutJson("/fika/presence/set", data);
    }

    public static FikaPlayerPresence[] SetAndGetPresence(FikaSetPresence data)
    {
        return PostJson<FikaSetPresence, FikaPlayerPresence[]>("/fika/presence/setget", data);
    }

    public static CurrentSettingsResponse GetServerSettings()
    {
        return GetJson<CurrentSettingsResponse>("/fika/admin/get");
    }

    public static SetSettingsResponse SaveServerSettings(SetSettingsRequest request)
    {
        return PostJson<SetSettingsRequest, SetSettingsResponse>("/fika/admin/set", request);
    }
}
