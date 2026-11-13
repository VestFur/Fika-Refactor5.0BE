using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Backend;

public class LoadingScreenUI : MonoBehaviour
{
    public LoadingScreenUI(System.IntPtr pointer) : base(pointer) { }


    public LoadingScreenUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<LoadingScreenUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public static LoadingScreenUI Instance { get; internal set; }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> LoadingPlayerPrefab;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Transform> Stack;

    private Dictionary<int, LoadingScreenPlayer> _loadingPlayers;

    /// <summary>
    /// Nickname per net id, learned from every row this screen ever created. Not cleared by
    /// <see cref="ClearData"/> on purpose: a row that has to be created again later (a re-arm, or a peer
    /// that re-announces itself) must come back with the nickname the player really has, not a placeholder.
    /// </summary>
    private readonly Dictionary<int, string> _knownNicknames = [];

    // ------------------------------------------------------------------ peer state heartbeat
    //
    // Why (analysis/loading-screen-peer-state, round10 2026-10-11): ReInitAfterTransit drops *every* row,
    // including the rows of peers that are still connected, and a machine announces its own row exactly
    // once - at its own re-arm (ReInitAfterTransit -> players packet) and at connect. The two machines of a
    // transit re-arm at different times (round10: the client at 20:16:06, the host at 20:17:30), so the
    // later re-arm wipes the row the earlier one had already announced. From that moment on every
    // LoadingScreenPacket of that peer reached a screen without its row and was dropped inside SetProgress
    // without a trace: the host's log holds no "progress netId=2" line at all after its re-arm, while the
    // client's own row climbed to 100 % and the client kept showing the host's row.
    //
    // The heartbeat closes that window without a new channel: while this screen is up it re-publishes the
    // local row (players packet) and the local progress on the existing packets, so a peer that re-armed
    // later re-creates the row within a second or two and then receives the current value again. It is
    // bounded (one progress line per second, one row announcement per two seconds, at most 600 s) and it
    // stops by itself when the screen is hidden, when the raid is running or when the network manager is
    // gone - i.e. exactly when the loading phase of this machine is over.

    /// <summary>How often the heartbeat re-publishes the local progress while the screen is up.</summary>
    private const float HeartbeatProgressInterval = 1f;
    /// <summary>How often the heartbeat re-publishes the local row (players packet).</summary>
    private const float HeartbeatRowInterval = 2f;
    /// <summary>Hard bound of the heartbeat: it never runs longer than this after it was armed.</summary>
    private const float HeartbeatMaxSeconds = 600f;

    private bool _heartbeatArmed;
    private float _heartbeatArmedAt;
    private float _lastHeartbeatProgressAt;
    private float _lastHeartbeatRowAt;
    private float _lastHeartbeatValue = -1f;
    private string _heartbeatSite = "none";

    protected void Awake()
    {
        _loadingPlayers = [];
    }

    public void ReInitAfterTransit()
    {
        FikaGlobals.LogInfo("Restarting LoadingScreenUI");
        var netId = FikaSingleton<IFikaNetworkManager>.Instance.NetId;
        var pmcName = FikaBackendUtils.IsHeadless ? "Headless" : FikaBackendUtils.PMCName;

        // (analysis/transit-reload-oom) This screen survives the transit, so its rows did too: the rows of
        // the raid that was just replaced were kept (and their stale progress with them). They are dropped
        // here and re-created when a peer announces itself again after the swap - which is exactly what the
        // [LoadSync] line at the end of this method describes.
        var replacedRows = _loadingPlayers.Count;
        ClearData();

        AddPlayer(netId, pmcName);

        var loadingPacket = new LoadingScreenPlayersPacket
        {
            NetIds = [netId],
            Nicknames = [pmcName]
        };

        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref loadingPacket, DeliveryMethod.ReliableUnordered, true);

        // Judgement line (analysis/loading-screen-sync): after a transit the screen is re-armed with the
        // local player only, and the peers are expected to re-announce themselves. Without this line a
        // reader cannot tell whether the peer's row survived the world swap or was never re-created.
        LoadSync.Report("LoadingScreenUI.ReInitAfterTransit",
            "the screen was re-armed after a transit: local netId=" + netId + " nickname='" + pmcName
            + "', the players packet was broadcast (" + LoadSync.DescribeScreen()
            + "), rows of the replaced raid dropped=" + replacedRows
            + " => a peer row only exists once that peer announces itself again after the swap");

        // (analysis/loading-screen-peer-state) The re-arm of the other machine happens at its own time and
        // takes every row with it, so this machine has to keep announcing until the loading phase is over.
        ArmPeerStateHeartbeat("LoadingScreenUI.ReInitAfterTransit");
    }

    public LoadingScreenPlayersPacket GetPlayersPacket()
    {
        var count = _loadingPlayers.Count;
        var netIds = new int[count];
        var nicknames = new string[count];

        var index = 0;
        foreach ((var netId, var loadingPlayer) in _loadingPlayers)
        {
            netIds[index] = netId;
            nicknames[index] = loadingPlayer.Nickname.Value.text;
            index++;
        }

        return new LoadingScreenPlayersPacket
        {
            NetIds = netIds,
            Nicknames = nicknames
        };
    }

    public LoadingScreenPacket[] GetLatestStates()
    {
        var packets = new LoadingScreenPacket[_loadingPlayers.Count];

        var index = 0;
        foreach ((var netId, var loadingPlayer) in _loadingPlayers)
        {
            packets[index] = new LoadingScreenPacket
            {
                NetId = netId,
                Progress = loadingPlayer.Progress
            };
            index++;
        }

        return packets;
    }

    public void UpdateAndBroadcast(float progress)
    {
        var netId = FikaSingleton<IFikaNetworkManager>.Instance.NetId;
        var loadingPacket = new LoadingScreenPacket
        {
            NetId = netId,
            Progress = progress
        };
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref loadingPacket, DeliveryMethod.Unreliable, true);
        SetProgress(netId, progress);

        // The local value is the one the heartbeat keeps re-publishing, so remember it here: the heartbeat
        // must not send an older value than the engine progress already reported.
        _lastHeartbeatValue = progress;
    }

    public void SetProgress(int netId, float progress)
    {
        SetProgress(netId, progress, "local");
    }

    /// <summary>
    /// Applies one progress value to a row.
    /// </summary>
    /// <param name="netId">The row's net id.</param>
    /// <param name="progress">The value in percent (0-100), as the progress packets carry it.</param>
    /// <param name="source">Where the value came from ("local", "peer#0", "host"), for the diagnostics.</param>
    public void SetProgress(int netId, float progress, string source)
    {
        if (_loadingPlayers.TryGetValue(netId, out var player))
        {
            player.SetProgress(progress);
            ReportProgress(netId, progress, source);
            return;
        }

        // Never drop a value silently (the round10 gap): reaching this line means the screen lost a row it
        // should hold, and a reader must see which row, which value and from whom.
        Diag("progress for unknown netId=" + netId + " from " + source + " (" + Fmt(progress)
            + "%) was dropped, this screen holds " + DescribeRows());
    }

    /// <summary>
    /// Applies the progress another machine reported, creating its row when this screen does not hold one.
    ///
    /// Why (analysis/loading-screen-peer-state): after a transit the two screens are re-armed at different
    /// times, so a peer's progress packets legitimately arrive while its row is gone. Re-creating the row
    /// from the packet is what makes the peer's loading state visible again instead of losing every value
    /// to <see cref="SetProgress"/>'s row lookup.
    /// </summary>
    /// <param name="netId">The peer's net id, as the packet carries it.</param>
    /// <param name="progress">The reported value in percent.</param>
    /// <param name="source">Who sent it, for the diagnostics ("peer#0", "host").</param>
    /// <returns><see langword="true"/> when the row had to be created for this packet.</returns>
    public bool ApplyPeerProgress(int netId, float progress, string source)
    {
        var created = EnsureRow(netId);
        if (created)
        {
            Diag("progress from " + source + " for netId=" + netId + ": this screen had no row for it yet, "
                + "the row was created again (" + DescribeRows() + ") => the peer was loading while this "
                + "machine's re-arm had already dropped its row");
        }

        SetProgress(netId, progress, source);
        return created;
    }

    /// <summary>
    /// Creates the row of a net id this screen does not know yet, reusing the nickname of an earlier row for
    /// that id. Returns true when a row was created here.
    /// </summary>
    /// <param name="nickname">
    /// The nickname the caller knows for this net id (a peer's own announcement). When it is empty or null
    /// the nickname of an earlier row for that id is reused.
    /// </param>
    public bool EnsureRow(int netId, string nickname = null)
    {
        if (_loadingPlayers.ContainsKey(netId))
        {
            return false;
        }

        if (string.IsNullOrEmpty(nickname) &&
            (!_knownNicknames.TryGetValue(netId, out nickname) || string.IsNullOrEmpty(nickname)))
        {
            nickname = "player " + netId;
        }

        AddPlayer(netId, nickname);
        return true;
    }

    // ------------------------------------------------------------------ heartbeat implementation

    /// <summary>
    /// Starts the bounded re-announcement of this machine's row and progress. Called after a transit re-arm
    /// on both roles; a second call only moves the window forward.
    /// </summary>
    public void ArmPeerStateHeartbeat(string site)
    {
        _heartbeatArmed = true;
        _heartbeatArmedAt = UnityEngine.Time.realtimeSinceStartup;
        _lastHeartbeatProgressAt = 0f;
        _lastHeartbeatRowAt = 0f;
        _heartbeatSite = site;
        Diag("heartbeat armed (site=" + site + "): this machine re-announces its row every "
            + HeartbeatRowInterval + "s and its progress every " + HeartbeatProgressInterval
            + "s while the loading screen is up (bound " + HeartbeatMaxSeconds + "s) => a peer that re-armed "
            + "later re-creates the row instead of losing every progress value");
    }

    private void StopHeartbeat(string reason)
    {
        _heartbeatArmed = false;
        Diag("heartbeat stopped (" + reason + ", site=" + _heartbeatSite + ", " + DescribeRows() + ")");
    }

    /// <summary>
    /// The per-frame pump of the heartbeat. Driven by the network managers' Update
    /// (FikaServer.Update / FikaClient.Update), because the engine only enters this injected component's
    /// Awake/OnDestroy - a MonoBehaviour.Update on this type is not guaranteed to run.
    /// </summary>
    public static void TickPeerStateHeartbeat()
    {
        var screen = Instance;
        if (screen == null)
        {
            return;
        }

        screen.TickHeartbeat();
    }

    private void TickHeartbeat()
    {
        if (!_heartbeatArmed)
        {
            return;
        }

        bool screenIsUp;
        try
        {
            screenIsUp = gameObject != null && gameObject.activeInHierarchy;
        }
        catch (System.Exception)
        {
            screenIsUp = false;
        }

        if (!screenIsUp)
        {
            StopHeartbeat("the loading screen is hidden, the loading phase of this machine is over");
            return;
        }

        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _heartbeatArmedAt >= HeartbeatMaxSeconds)
        {
            StopHeartbeat("the bounded window of " + HeartbeatMaxSeconds + "s is over");
            return;
        }

        IFikaNetworkManager networkManager;
        try
        {
            networkManager = FikaSingleton<IFikaNetworkManager>.Instance;
        }
        catch (System.Exception)
        {
            networkManager = null;
        }

        if (networkManager == null)
        {
            StopHeartbeat("no network manager on this machine");
            return;
        }

        var netId = networkManager.NetId;
        var pmcName = FikaBackendUtils.IsHeadless ? "Headless" : FikaBackendUtils.PMCName;

        try
        {
            if (_lastHeartbeatRowAt <= 0f || now - _lastHeartbeatRowAt >= HeartbeatRowInterval)
            {
                _lastHeartbeatRowAt = now;
                AddPlayer(netId, pmcName);

                var rows = new LoadingScreenPlayersPacket
                {
                    NetIds = [netId],
                    Nicknames = [pmcName]
                };
                networkManager.SendData(ref rows, DeliveryMethod.ReliableUnordered, true);
                Diag("heartbeat row announced: netId=" + netId + " nickname='" + pmcName + "' ("
                    + DescribeRows() + ")");
            }

            if (_lastHeartbeatProgressAt > 0f && now - _lastHeartbeatProgressAt < HeartbeatProgressInterval)
            {
                return;
            }

            _lastHeartbeatProgressAt = now;

            var progress = 0f;
            if (_loadingPlayers.TryGetValue(netId, out var me) && me != null)
            {
                progress = me.Progress;
            }

            var state = new LoadingScreenPacket
            {
                NetId = netId,
                Progress = progress
            };
            networkManager.SendData(ref state, DeliveryMethod.Unreliable, true);

            if (System.Math.Abs(progress - _lastHeartbeatValue) >= 1f)
            {
                _lastHeartbeatValue = progress;
                Diag("heartbeat progress sent: netId=" + netId + " " + Fmt(progress) + "% ("
                    + DescribeRows() + ")");
            }
        }
        catch (System.Exception exception)
        {
            StopHeartbeat("sending failed (" + exception.GetType().Name + ": " + exception.Message + ")");
        }
    }

    /// <summary>
    /// The last progress value written to the log per row, so the per-value trace stays at one line
    /// per 10 percent step per row instead of one line per packet.
    /// </summary>
    private readonly Dictionary<int, float> _reportedProgress = [];

    /// <summary>
    /// Writes the progress of one row into the diagnostics when it crosses a 10 percent step. Used for
    /// both the local row (UpdateAndBroadcast) and the rows of the peers (SetProgress), which is what
    /// makes "the bars are not in sync" a readable claim in the next run: the line names the row, the
    /// value and whether it was applied locally or received from the network.
    /// </summary>
    private void ReportProgress(int netId, float progress, string source)
    {
        if (_reportedProgress.TryGetValue(netId, out var previous) && (int)(previous / 10f) == (int)(progress / 10f))
        {
            return;
        }

        _reportedProgress[netId] = progress;
        var line = "progress netId=" + netId + " " + Fmt(progress)
            + "% (" + DescribeRows() + ") source=" + source;
        LoadSync.Trace(line);

        // (analysis/loading-screen-peer-state) The same line in the game log, one per 10 percent step and
        // per row, so both machines can be compared without opening the diagnostics file.
        Diag(line);
    }

    /// <summary>
    /// Writes one diagnostic line that survives the quiet diagnostics mode (the scope is on the
    /// "LoadingScreen" prefix allow list) and is greppable in both logs.
    /// </summary>
    public static void Diag(string message)
    {
        try
        {
            FikaGlobals.LogInfo("[LoadScreenDiag] " + message, "LoadScreenDiag");
            
        }
        catch (System.Exception)
        {
        }
    }

    /// <summary>Last write per throttle key (see <see cref="DiagThrottled"/>).</summary>
    private static readonly Dictionary<string, float> _diagThrottle = [];

    /// <summary>
    /// Writes a diagnostic line at most once per <paramref name="seconds"/> for one key. For the high
    /// frequency paths only: a rejected or unrecognised packet can arrive several times per second and
    /// must still leave one readable line without flooding the game log.
    /// </summary>
    public static void DiagThrottled(string key, float seconds, string message)
    {
        try
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_diagThrottle.TryGetValue(key, out var last) && now - last < seconds)
            {
                return;
            }

            _diagThrottle[key] = now;
        }
        catch (System.Exception)
        {
        }

        Diag(message);
    }

    /// <summary>The percent value as it appears in the logs ("42.5" instead of a locale dependent form).</summary>
    private static string Fmt(float value)
    {
        return value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The rows this screen holds as one line: "1 row(s) [2(harbin)=92.5]". Used by every
    /// [LoadSync] judgement line, so a tester can see which players the screen knows and how far
    /// each of them is - locally driven rows and rows received from the host in the same list.
    /// Never throws: a screen without Awake (or a destroyed one) is reported as such.
    /// </summary>
    /// <summary>
    /// The progress this machine has published for itself, as "local=92.5" (or "local=absent" when the
    /// screen holds no row for the local net id). Every client-side [LoadSync] line carries it, so a
    /// reader can compare the local progress against the peer's from one line.
    /// </summary>
    public string DescribeLocalRow()
    {
        var players = _loadingPlayers;
        if (players == null || players.Count == 0)
        {
            return "local=absent (no rows)";
        }

        int localNetId;
        try
        {
            localNetId = FikaSingleton<IFikaNetworkManager>.Instance.NetId;
        }
        catch (System.Exception)
        {
            return "local=absent (no net manager)";
        }

        if (!players.TryGetValue(localNetId, out var me) || me == null)
        {
            return "local=absent (no row for netId " + localNetId + ")";
        }

        return "local=" + Fmt(me.Progress);
    }

    public string DescribeRows()
    {
        var players = _loadingPlayers;
        if (players == null)
        {
            return "no rows yet (Awake has not run)";
        }

        if (players.Count == 0)
        {
            return "0 row(s)";
        }

        var builder = new System.Text.StringBuilder(players.Count * 24);
        builder.Append(players.Count).Append(" row(s) [");
        var first = true;
        foreach ((var netId, var player) in players)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append(netId).Append('(');
            try
            {
                builder.Append(player == null ? "?" : player.Nickname.Value.text);
            }
            catch (System.Exception)
            {
                builder.Append('?');
            }

            builder.Append(")=");
            builder.Append(player == null ? "?" : Fmt(player.Progress));
        }

        builder.Append(']');
        return builder.ToString();
    }

    public void AddPlayer(int netId, string nickname)
    {
        if (_loadingPlayers.ContainsKey(netId))
        {
            return;
        }

        // Remembered before the row exists, so a row that has to be created again later cannot lose the
        // nickname (ClearData does not touch this map).
        _knownNicknames[netId] = nickname;

        var go = GameObject.Instantiate(LoadingPlayerPrefab.Value, Stack.Value);
        var lsp = go.GetComponent<LoadingScreenPlayer>();
        lsp.SetNickname(nickname);
        _loadingPlayers.Add(netId, lsp);
        go.SetActive(true);

        // One line per row: this is the only evidence that the prefab's LoadingScreenPlayer row was
        // really created for that player (a missing row is a peer whose progress can never be shown).
        LoadSync.Report("LoadingScreenUI.AddPlayer",
            "row added for netId=" + netId + " nickname='" + nickname + "' (" + DescribeRows() + ")");
    }

    public void DeletePlayer(int netId)
    {
        if (_loadingPlayers.TryGetValue(netId, out var lsp))
        {
            _loadingPlayers.Remove(netId);
            GameObject.Destroy(lsp.gameObject);
        }
    }

    protected void OnDestroy()
    {
        _heartbeatArmed = false;
        ClearData();
    }

    public void ClearData()
    {
        foreach (var item in _loadingPlayers)
        {
            GameObject.Destroy(item.Value.gameObject);
        }

        _loadingPlayers.Clear();
    }
}
