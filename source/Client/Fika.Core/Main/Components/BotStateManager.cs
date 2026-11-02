using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Snapshotting;
using static Fika.Core.Networking.NetworkUtils;

namespace Fika.Core.Main.Components;

public sealed class BotStateManager : MonoBehaviour
{
    public BotStateManager(System.IntPtr pointer) : base(pointer) { }


    public BotStateManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BotStateManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private List<FikaBot> _bots;
    private HostGameController _controller;
    private BotsController _botsController;
    private FikaServer _server;
    private NetDataWriter _writer;

    private float _updateCount;
    private float _updatesPerTick;
    private uint _writtenPackets;
    private bool _headerWritten;
    private readonly byte _stateSize = PlayerStateData.PacketSize;

    private void Start()
    {
        FikaBot.OnPlayerDeath += OnPlayerDeath;
        FikaBot.OnPlayerDestroyed += OnPlayerDeath;
    }

    private void OnPlayerDeath(FikaPlayer player)
    {
        if (player?.TryCast<FikaBot>() is { } bot)
        {
            RemoveBot(bot);
        }
    }

    public void AddBot(FikaBot bot)
    {
        if (_bots.Contains(bot))
        {
            return;
        }

        _bots.Add(bot);
    }

    public bool RemoveBot(FikaBot bot)
    {
        return _bots.Remove(bot);
    }

    public static BotStateManager Create(AbstractGame game, FikaServer server, HostGameController hostGameController)
    {
        var component = game.gameObject.AddComponent<BotStateManager>();
        component._controller = hostGameController;
        component._updateCount = 0f;
        component._updatesPerTick = 1f / server.SendRate;
        component._bots = [];
        component._server = server;
        component._writer = new NetDataWriter(true, 1024);
        return component;
    }

    /// <summary>
    /// 驱动主机控制器的逐帧工作，而正是它调用了游戏自身
    /// <c>IBotGame.UpdateByUnity</c> 的订阅者。
    /// <para>
    /// (analysis/session-log-archive) 其中一个订阅者是原生的，它会解引用一个在突袭世界
    /// 仍在构建时还是 null 的字段：<c>BotGroupHostility.UpdateHostilityStatusForAllPlayers</c> ->
    /// <c>OwnedGroupCalcActionNextFrame</c> 从它自己的原生栈帧中抛出 NullReferenceException（在三次会话中
    /// 每次会话见到一次，总是出现在构建世界的窗口内；见
    /// analysis/session-log-archive/REPORT.md）。现在这次调用被隔离在
    /// <see cref="Fika.Core.Main.Utils.NativeActionEvent"/> 中：失败会以 [NativeUpdate]
    /// WARN 连同其原生栈帧一起上报，而这个 Update 保住它剩余的工作——首要是下面
    /// 的 bot 状态批次——而不是为此被中止一帧。
    /// </para>
    /// </summary>
    private void Update()
    {
        _controller.Update?.Invoke();

        _updateCount += Time.unscaledDeltaTime;
        if (_updateCount >= _updatesPerTick)
        {
            SendBatchStates();
            _updateCount -= _updatesPerTick;
        }
    }

    private void SendBatchStates()
    {
        CheckAndWriteHeader();

        var count = _bots.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            if ((_writer.Length + _stateSize) > _server.MaxMTU)
            {
                SendAndReset();
                CheckAndWriteHeader();
            }

            if (_bots[i].BotPacketSender.WriteState(_writer))
            {
                _writtenPackets++;
            }
        }

        SendAndReset();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckAndWriteHeader()
    {
        if (!_headerWritten)
        {
            _writer.PutEnum(EPacketType.PlayerState);
            _writer.Put(NetworkTimeSync.NetworkTime);
            _headerWritten = true;
        }
    }

    private void SendAndReset()
    {
        if (_writtenPackets != 0)
        {
            _server.BatchSendStates(_writer);
            _writtenPackets = 0;
            _headerWritten = false;
            _writer.Reset();
        }
    }

    private void OnDestroy()
    {
        FikaBot.OnPlayerDeath -= OnPlayerDeath;
        FikaBot.OnPlayerDestroyed -= OnPlayerDeath;
        _bots.Clear();
    }

    public void AssignBotsController(BotsController botsController)
    {
        _botsController = botsController;
    }

    public void UnassignBotsController()
    {
        _botsController = null;
    }
}
