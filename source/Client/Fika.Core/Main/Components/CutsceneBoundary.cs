using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using EFT;
using UnityEngine;

using CommonAssets.Scripts.Cutscenes;

namespace Fika.Core.Main.Components;

/// <summary>
/// (2026-10-18 refactor W3-cutscene, 方案 B) 最小化的过场桥接。
/// Fika 战局在这里需要从 Fika 得到三个事实：两个原生控制器都存在、两者都知道地图的
/// MapTimelinesBank（没有它原生 StartCutscene 会抛异常）、且都不在离线模式下运行。
/// 由哪个进程播放、谁可以跳过、场景如何结束，仍交由游戏决定。
/// </summary>
internal static class CutsceneBoundary
{
    internal readonly struct SceneSample
    {
        public readonly bool Director;
        public readonly bool Playing;
        public readonly string Scene;
        public readonly float Time;
        public readonly bool Visible;

        public SceneSample(bool director, bool playing, string scene, float time, bool visible)
        {
            Director = director;
            Playing = playing;
            Scene = scene;
            Time = time;
            Visible = visible;
        }
    }

    private static nint _preparedWorld = -1;
    private static MapTimelinesBank _bank;
    // (2026-10-19 hotfix) bank 查找是全场景扫描（FindObjectOfType）。它不能每帧运行：
    // 重置构建在每次 Tick 都调用它，直到两个控制器都拿到 bank，这就是用户报告的
    // 战局内掉帧。改用次数有限、间隔递增的重试方式。
    private static float _nextBankAttempt;
    private static int _bankAttempts;
    private const int MaxBankAttempts = 6;

    internal static bool JoinSharedCutscene => true;

    internal static void Reset()
    {
        _preparedWorld = -1;
        _startupDispatched = false;
        _bank = null;
    }

    internal static void InitCutsceneControllers(GameWorld world) => CreateCutsceneControllers(world, "InitCutsceneControllers", false);

    internal static void CreateCutsceneControllers(GameWorld world, string source, bool viaNativePosition)
    {
        _ = source;
        _ = viaNativePosition;
        if (world == null)
        {
            return;
        }

        try
        {
            if (world.Pointer == _preparedWorld)
            {
                return;
            }

            _preparedWorld = world.Pointer;

            if (world.CutscenesClientController == null)
            {
                world.CutscenesClientController = new CutscenesClientController();
            }

            if (world.CutscenesServerController == null)
            {
                world.CutscenesServerController = new CutscenesServerController();
            }

            try { world.CutscenesClientController._offlineMode = false; } catch (Exception) { }
            try { world.CutscenesServerController._offlineMode = false; } catch (Exception) { }

            // (2026-10-19) 控制器会自行连接：原生 FindMapTimelinesBank 才是将
            // 时间线 bank 和进程所需事件订阅接线的东西。调用它就是手写 bank 赋值
            // 一直在模仿的全部「setup」。
            try { world.CutscenesClientController.FindMapTimelinesBank(); }
            catch (Exception exception) { FikaGlobals.LogWarning("[CutsceneBoundary] client FindMapTimelinesBank failed: " + exception.Message); }
            try { world.CutscenesServerController.FindMapTimelinesBank(); }
            catch (Exception exception) { FikaGlobals.LogWarning("[CutsceneBoundary] server FindMapTimelinesBank failed: " + exception.Message); }

            LinkBank(world);
        }
        catch (Exception)
        {
        }
    }

    internal static void Tick(GameWorld world)
    {
        if (world == null)
        {
            return;
        }

        if (_bank != null || _bankAttempts >= MaxBankAttempts)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextBankAttempt)
        {
            return;
        }

        _nextBankAttempt = now + 1f + _bankAttempts;
        _bankAttempts++;

        LinkBank(world);
    }

    private static int _pumpFrame = -1;
    private static int _pumpProcess;
    private static float _nextPump;
    private static bool _startupDispatched;
    private static float _lastElapsed = -1f;

    /// <summary>
    /// (2026-10-19) 最小化的播放泵。在此运行时上原生控制器没有 OnUpdate 的托管调用者
    /// （interop 报告 CallerCount 为 0），因此 Fika 启动的进程永远不会被引擎推进：
    /// 字幕／UI 状态被切换，但时间线从不播放，这就是开场 Terminal 场景
    /// 在出生点视图上显示文字的原因。当有启动挂起时，该泵推进客户端控制器，
    /// 一旦引擎自行推进该进程就停止触碰它。
    /// </summary>
    internal static void TickPlayback(GameWorld world)
    {
        try
        {
            if (world == null)
            {
                return;
            }

            var client = world.CutscenesClientController;
            if (client == null || client.Pointer == IntPtr.Zero || client._timelinesMapBank == null)
            {
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            var pid = client._cutsceneProcessId;

            if (pid != 0 && pid != _pumpProcess)
            {
                _pumpProcess = pid;
                _lastElapsed = -1f;
                PumpClientOnce(client);
                return;
            }

            if (pid == 0)
            {
                _pumpProcess = 0;
                if (!client._needStartTimeline && client._waitFramesLeft <= 0)
                {
                    return;
                }

                if (now < _nextPump)
                {
                    return;
                }

                _nextPump = now + 0.25f;
                PumpClientOnce(client);
                return;
            }

            if (now < _nextPump)
            {
                return;
            }

            _nextPump = now + 0.25f;

            var server = world.CutscenesServerController;
            if (server?._cutsceneProcesses != null && server._cutsceneProcesses.TryGetValue(pid, out var process) && process != null)
            {
                var elapsed = process.afterStartTimeElapsed;
                if (_lastElapsed >= 0f && System.Math.Abs(elapsed - _lastElapsed) > 0.0001f)
                {
                    _lastElapsed = elapsed;
                    return;
                }

                _lastElapsed = elapsed;
            }

            PumpClientOnce(client);
        }
        catch (Exception)
        {
        }
    }

    private static void PumpClientOnce(CutscenesClientController client)
    {
        if (UnityEngine.Time.frameCount == _pumpFrame)
        {
            return;
        }

        _pumpFrame = UnityEngine.Time.frameCount;
        try { client.OnUpdate(UnityEngine.Time.deltaTime); } catch (Exception) { }
    }

    private static void LinkBank(GameWorld world)
    {
        try
        {
            if (_bank == null)
            {
                _bank = UnityEngine.Object.FindObjectOfType<MapTimelinesBank>();
            }

            if (_bank == null)
            {
                return;
            }

            var client = world.CutscenesClientController;
            if (client != null && client._timelinesMapBank == null)
            {
                client._timelinesMapBank = _bank;
            }

            var server = world.CutscenesServerController;
            if (server != null && server._timelinesMapBank == null)
            {
                server._timelinesMapBank = _bank;
            }
        }
        catch (Exception)
        {
        }
    }

    internal static SceneSample SampleSceneOf(string cutscene) => new(true, true, cutscene, 0f, true);

    internal static float TickSceneOf() => 0f;

    internal static string StopSceneOf(string cutscene) => "native";

    internal static string RescueSceneOf(string cutscene) => "native";
}
