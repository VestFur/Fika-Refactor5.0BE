using EFT.Ballistics;
using System.Collections.Generic;
using Comfort.Common;
using EFT.UI;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using TMPro;

public class DebugUI : MonoBehaviour
{
    public DebugUI(System.IntPtr pointer) : base(pointer) { }


    public DebugUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<DebugUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> AlivePlayersText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> AliveBotsText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> ClientsText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> PingText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> RTTText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> ServerFPSText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<RectTransform> Border;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<RectTransform> Frame;

    private const float _serverHeight = 90f;
    private const float _clientHeight = 130f;

    private FikaServer _server;
    private CoopHandler _coopHandler;
    private float _frameCounter;
    private bool _isServer;
    private List<FikaPlayer> _alivePlayers;
    private List<FikaPlayer> _aliveBots;
    private readonly Dictionary<FikaPlayer, EFT.OnPlayerDead> _playerDeathHandlers = new();
    private readonly Dictionary<FikaPlayer, EFT.OnPlayerDead> _botDeathHandlers = new();

    private int Ping
    {
        get
        {
            return FikaSingleton<FikaClient>.Instance.Ping;
        }
    }

    private int ServerFPS
    {
        get
        {
            return FikaSingleton<FikaClient>.Instance.ServerFPS;
        }
    }

    private int RTT
    {
        get
        {
            return FikaSingleton<FikaClient>.Instance.NetClient.FirstPeer.RoundTripTime;
        }
    }

    private Color DefaultColor;

    protected void Awake()
    {
        _isServer = FikaBackendUtils.IsServer;
        if (_isServer)
        {
            _server = FikaSingleton<FikaServer>.Instance;
        }

        if (CoopHandler.TryGetCoopHandler(out var coopHandler))
        {
            _coopHandler = coopHandler;

            _alivePlayers = [];
            _aliveBots = [];
        }
        else
        {
            FikaGlobals.LogError("FikaDebug: CoopHandler was null!");
            Destroy(gameObject);
        }

        DefaultColor = AlivePlayersText.Value.color;

        var sizeDelta = Frame.Value.sizeDelta;
        var borderSizeDelta = Border.Value.sizeDelta;
        if (_isServer)
        {
            sizeDelta.y = _serverHeight;
            borderSizeDelta.y = _serverHeight + 10f;
            PingText.Value.gameObject.SetActive(false);
            RTTText.Value.gameObject.SetActive(false);
            ServerFPSText.Value.gameObject.SetActive(false);
        }
        else
        {
            sizeDelta.y = _clientHeight;
            borderSizeDelta.y = _clientHeight + 10f;
            ClientsText.Value.gameObject.SetActive(false);
        }
        Frame.Value.sizeDelta = sizeDelta;
        Border.Value.sizeDelta = borderSizeDelta;

        Border.Value.gameObject.AddComponent<UIDragComponent>()
            .Init(Border.Value, true);
    }

    protected void Update()
    {
        _frameCounter += Time.unscaledDeltaTime;
        if (_frameCounter >= 5f)
        {
            _frameCounter = 0f;
            CheckAndAdd();
        }

        AlivePlayersText.Value.SetFormattedText("Alive Players: {0}", _alivePlayers.Count);
        AliveBotsText.Value.SetFormattedText("Alive Bots: {0}", _aliveBots.Count);
        if (_isServer)
        {
            ClientsText.Value.SetFormattedText("Clients: {0}", _server.NetServer.ConnectedPeersCount);
        }
        else
        {
            var ping = Ping;
            PingText.Value.SetFormattedText("Ping: {0}", ping);
            PingText.Value.color = GetPingColor(ping);
            var rtt = RTT;
            RTTText.Value.SetFormattedText("RTT: {0}", rtt);
            RTTText.Value.color = GetRTTColor(rtt);
            var serverFps = ServerFPS;
            ServerFPSText.Value.SetFormattedText("Server FPS: {0}", serverFps);
            ServerFPSText.Value.color = GetServerFPSColor(serverFps);
        }
    }

    private Color GetServerFPSColor(int serverFps)
    {
        if (serverFps < 30)
        {
            return Color.red;
        }

        if (serverFps < 50)
        {
            return Color.yellow;
        }

        return DefaultColor;
    }

    private Color GetRTTColor(int rtt)
    {
        if (rtt < 0.0 || rtt > 120.0)
        {
            return Color.red;
        }

        if (rtt > 60.0)
        {
            return Color.yellow;
        }

        return DefaultColor;
    }

    private Color GetPingColor(int ping)
    {
        if (ping <= 75)
        {
            return DefaultColor;
        }

        if (ping <= 125)
        {
            return Color.yellow;
        }

        return Color.red;
    }

    private void CheckAndAdd()
    {
        foreach (var player in _coopHandler.HumanPlayers)
        {
            if (!_alivePlayers.Contains(player) && player.HealthController.IsAlive)
            {
                AddPlayer(player);
            }
        }

        foreach (var player in _coopHandler.Players.Values)
        {
            if ((player.IsObservedAI || player.IsAI) && !_aliveBots.Contains(player) && player.HealthController.IsAlive)
            {
                AddBot(player);
            }
        }
    }

    protected void OnEnable()
    {
        CheckAndAdd();
    }

    protected void OnDisable()
    {
        foreach (var player in _alivePlayers)
        {
            if (_playerDeathHandlers.Remove(player, out var handler)) player.OnPlayerDead -= handler;
        }
        _alivePlayers.Clear();

        foreach (var bot in _aliveBots)
        {
            if (_botDeathHandlers.Remove(bot, out var handler)) bot.OnPlayerDead -= handler;
        }
        _aliveBots.Clear();
    }

    private void AddPlayer(FikaPlayer player)
    {
        var handler = Fika.Core.NativeDelegates.Bind<EFT.OnPlayerDead>(this, nameof(PlayerDied));
        _playerDeathHandlers[player] = handler;
        player.OnPlayerDead += handler;
        _alivePlayers.Add(player);
    }

    private void PlayerDied(EFT.Player player, EFT.IPlayer lastAggressor, DamageInfo damageInfo, EBodyPart part)
    {
        var fikaPlayer = player.Cast<FikaPlayer>();
        if (_playerDeathHandlers.Remove(fikaPlayer, out var handler)) player.OnPlayerDead -= handler;
        _alivePlayers.Remove(fikaPlayer);
    }

    private void AddBot(FikaPlayer bot)
    {
        var handler = Fika.Core.NativeDelegates.Bind<EFT.OnPlayerDead>(this, nameof(BotDied));
        _botDeathHandlers[bot] = handler;
        bot.OnPlayerDead += handler;
        _aliveBots.Add(bot);
    }

    private void BotDied(EFT.Player player, EFT.IPlayer lastAggressor, DamageInfo damageInfo, EBodyPart part)
    {
        var fikaPlayer = player.Cast<FikaPlayer>();
        if (_botDeathHandlers.Remove(fikaPlayer, out var handler)) player.OnPlayerDead -= handler;
        _aliveBots.Remove(fikaPlayer);
    }
}
