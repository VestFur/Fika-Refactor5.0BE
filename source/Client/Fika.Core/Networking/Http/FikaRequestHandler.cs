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

    public static async Task<IPAddress> GetPublicIP()
    {
        var client = _httpClient.HttpClient;
        string[] ipv4Urls = [
            "https://api.ipify.org/",
            "https://checkip.amazonaws.com/",
            "https://ipv4.icanhazip.com/"
        ];

        // An IPv6 only network has no IPv4 egress at all, so a second pass over
        // providers that answer over IPv6 keeps the raid host announceable there
        // instead of burning three timeouts and failing.
        string[] ipv6Urls = [
            "https://api64.ipify.org/",
            "https://ipv6.icanhazip.com/",
            "https://ifconfig.co/ip"
        ];

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
                    FikaGlobals.LogWarning($"Request to {url} timed out after 5 seconds.");
                }
                catch (Exception ex)
                {
                    FikaGlobals.LogWarning($"Could not get public IP address from [{url}]. Error: {ex.Message}");
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

        throw new Exception("Could not retrieve or parse the external IP address from any provider.");
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
        // Only transport runs off-thread. Game objects must be decoded on the
        // caller's thread, especially while Plugin.Load blocks the Unity loop.
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
        // The SPT 5.0 server intentionally returns an empty body for raid
        // creation. Treat a completed request as success and let transport
        // exceptions propagate to the caller.
        await PutJsonAsync("/fika/raid/create", data);
    }

    public static GetHostResponse GetHost(GetHostRequest data)
    {
        return PostJson<GetHostRequest, GetHostResponse>("/fika/raid/gethost", data);
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
