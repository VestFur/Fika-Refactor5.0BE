using System;
using System.Collections.Generic;
using EFT;
using EFT.Passcodes;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core.Main.Components;

/// <summary>
/// BU（analysis/icebreaker-keypad-3）：突袭的通行码集合，由游戏生成，也由游戏校验。
///
/// 键盘锁修复的第三轮。前两轮把判定权留在了主机侧（客户端的
/// 密码被交给主机、由主机作答），而客户端的世界里带的却是一个完全没有
/// 密码表的控制器，因此同一场突袭的两台机器从未持有同一套码：客户端输入的是它自己
/// 墙面提示所显示的内容，主机却拿自己的码去比对，于是每次尝试都返回 'wrong'
/// （r33: stage=result success=no x2，实时诊断日志 27220/27221）。
///
/// 一场突袭的原生调用链如下（成员来自 47888 的 interop，file:line 见报告）：
///   * DATA 是该地点的 <c>PasscodeLocationSettings.Entries</c>（id -> 密码列表 id）；
///   * CODES 由 <c>PasscodeGenerator.GeneratePasscodes(entries)</c> 每场突袭抽取一次，它
///     会把每个列表 id 解析到 <c>GlobalConfiguration.PasscodeSettings</c>，并取该列表中的一个随机
///     条目（可选的破冰船键盘锁从一个 60000 条的列表里抽取，任务用的那个
///     只有一个码）——这就是用户所说的『码是在突袭内部生成的』；
///   * HINTS 由 <c>PasscodeController.GenerateHints(codes)</c> 生成，再由
///     <c>ClientPasscodeController.SetupHints(hints)</c> 应用到场景的提示对象上；
///   * CHECK 在 <c>ClientLocalPasscodeController.HandleSubmitPasscode</c> 内部、针对该
///     机器自己的 <c>_generatedPasscodes</c> 进行，而正是这次调用把门打开。
///
/// 因此一台机器只有持有完全相同的那张表，才可能对某个码判断正确。这个类给整场突袭
/// 只提供一张表：由主机用游戏自己的生成器生成（或保留原生
/// 流水线已经做好的那张），再由 Fika 把这张表送到各个客户端——它从不判定任何码。
/// 客户端围绕收到的表构建原生的 LOCAL 控制器，于是此后该客户端整条
/// 提交 -> 校验 -> 开锁 -> 面板 的路径就是单人模式的那一条，没有任何网络往返，
/// 也不使用中继（中继仅作为「尚未收到表」那段时间的兜底而保留）。
/// 当这个构建的原生流水线让它自己的世界里没有控制器时，主机也会构建同一个控制器，
/// 这正是主机自己的键盘锁会回答 'Tried to enter code on location without passcodes' 的原因。
/// </summary>
internal static class KeypadCodeSync
{
    /// <summary>两次尝试构建控制器之间的秒数（地点/场景可能还没就绪）。</summary>
    private const float RetrySeconds = 2f;

    /// <summary>状态一直异常期间，失败日志行的重复频率。</summary>
    private const float FailureLineSeconds = 10f;

    private static bool _hostCodesReady;
    private static int _hostCodeCount;
    private static bool _clientHasHostCodes;
    private static bool _clientApplied;
    private static bool _loggedWaiting;
    private static float _nextAttempt;
    private static float _nextFailureLine;
    private static int _failures;
    private static int _terminalPopulations;

    private static Dictionary<string, string> _hostEntries;
    private static Dictionary<string, string> _hostCodes;
    private static Dictionary<string, string[]> _hostHints;

    /// <summary>这个移植版本安装在客户端上的本地控制器，好让 RaidInteractionSync 能把它与
    /// 需要切换成网络形态的原生控制器区分开。</summary>
    private static IntPtr _clientLocalController;

    /// <summary>本机持有本场突袭的密码表（主机：自己的；客户端：主机的）。</summary>
    internal static bool Ready => FikaBackendUtils.IsServer ? _hostCodesReady : _clientApplied;

    /// <summary>尚未收到主机密码表的客户端会持续索取快照。</summary>
    internal static bool NeedsHostCodes => FikaBackendUtils.IsClient && !_clientHasHostCodes;

    /// <summary>主机的表作为线上载荷，尚未存在时为 null。</summary>
    internal static Dictionary<string, string> HostCodes() => _hostCodesReady ? _hostCodes : null;

    internal static void Reset()
    {
        _hostCodesReady = false;
        _hostCodeCount = 0;
        _clientHasHostCodes = false;
        _clientApplied = false;
        _loggedWaiting = false;
        _nextAttempt = 0f;
        _nextFailureLine = 0f;
        _failures = 0;
        _terminalPopulations = 0;
        _hostEntries = null;
        _hostCodes = null;
        _hostHints = null;
        _clientLocalController = IntPtr.Zero;
    }

    /// <summary>当该客户端的世界持有围绕主机密码表构建的本地控制器时为 true。</summary>
    internal static bool OwnsLocalController(ClientLocalPasscodeController controller)
    {
        try
        {
            return _clientLocalController != IntPtr.Zero && controller != null
                && controller.Pointer == _clientLocalController;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 主机的答复已到达本机。它的密码表就是本场突袭的表；条目 id 与
    /// 提示集合随之一同传输，因此客户端无需自有数据即可构建原生控制器。
    /// </summary>
    internal static void NoteHostSnapshot(Dictionary<string, string> codes, Dictionary<string, string> entries,
        Dictionary<string, string[]> hints)
    {
        if (FikaBackendUtils.IsServer) return;
        if (entries != null && entries.Count > 0) _hostEntries = entries;
        if (hints != null && hints.Count > 0) _hostHints = hints;
        if (codes == null || codes.Count == 0)
        {
            // 在主机尚未抽取密码之前，空快照是合法的；RaidInteractionSync 的请求循环
            // 会继续索取，而这次等待只被记录一次。
            if (!_loggedWaiting && !_clientApplied)
            {
                _loggedWaiting = true;
                
            }

            return;
        }

        var changed = !_clientHasHostCodes || _hostCodes == null || _hostCodes.Count != codes.Count;
        _hostCodes = codes;
        _clientHasHostCodes = true;
        if (changed)
        {
            
        }
    }

    /// <summary>
    /// 本场突袭键盘锁状态每帧收敛的一步。在主机和每个客户端上都会运行，
    /// 且只在突袭内部运行（RaidInteractionSync 的闸门已使我们不会进入别人的突袭）。
    /// </summary>
    internal static void Tick(GameWorld world)
    {
        if (world == null) return;
        try
        {
            if (FikaBackendUtils.IsServer) EnsureHostTable(world);
            else if (FikaBackendUtils.IsClient) EnsureClientTable(world);
        }
        catch (Exception exception)
        {
            Report("tick", -1, -1, "failed", "building the raid's passcode state threw " + exception.GetType().Name);
        }
    }

    // ---- 主机：本场突袭的密码表在此处抽取 ----------------------------------------------------------

    private static void EnsureHostTable(GameWorld world)
    {
        // 原生流水线可能已经把控制器做好了（那是主机离线突袭的单人
        // 路径）。它的表就是本场突袭的表，绝不会被重新抽取——重新抽取会让
        // 码在玩家脚下发生变化。
        if (world.PasscodeController?.TryCast<ClientLocalPasscodeController>() is { } native)
        {
            Populate(native);
            if (HasCodes(native))
            {
                if (!_hostCodesReady)
                {
                    _hostCodesReady = true;
                    _hostCodeCount = native._generatedPasscodes.Count;
                    _hostCodes = CodesOf(native);
                    Report("host-native", native._passcodeLocationSettings?.Entries?.Length ?? 0,
                        native._passcodeTerminals?.Count ?? 0, "kept-native",
                        "this host's world already carries the game's own local passcode controller, so its table is the raid's table");
                    RaidInteractionSync.PushPasscodeSnapshot();
                }

                return;
            }

            // 一个没有表的原生控制器（r33 主机日志里显示为
            // 'Tried to enter code on location without passcodes' 的那种状态）：用游戏自己的生成器抽取。
            if (TryGenerate(native, world, "host-native-repaired")) return;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextAttempt) return;
        _nextAttempt = now + RetrySeconds;

        var settings = LocationSettings();
        var entries = settings?.Entries;
        var listIds = ListIds(entries);
        if (entries == null || entries.Length == 0)
        {
            // 本机的地点不携带任何条目：退回使用本场景的键盘锁，并给一个
            // 空的列表 id（此时生成器没有列表可抽，门保持原有
            // 的行为）。日志行会点明这一点，以便下一轮能从日志判断。
            var scene = new PasscodeLocationSettings { Entries = FromSceneIds() };
            settings = scene;
            entries = scene.Entries;
        }

        var controller = new ClientLocalPasscodeController(settings);
        var previous = world.PasscodeController;
        world.PasscodeController = controller;
        Populate(controller);
        if (!TryGenerate(controller, world, "host-created"))
        {
            // 无法抽出任何表（本机的地点根本不携带条目）。世界保持
            // 它原有的那个控制器，因此主机的状态在日志里仍然可读。
            world.PasscodeController = previous;
            controller.Dispose();
            return;
        }

        previous?.Dispose();
        
    }

    /// <summary>用游戏自己的生成器抽取密码表，并应用游戏自己的提示。</summary>
    private static bool TryGenerate(ClientLocalPasscodeController controller, GameWorld world, string source)
    {
        var settings = controller._passcodeLocationSettings;
        var entries = settings?.Entries;
        if (entries == null || entries.Length == 0)
        {
            Report(source, 0, controller._passcodeTerminals?.Count ?? 0, "no-entries",
                "this machine's location carries no passcode entry at all, so there is nothing to draw a code from"
                + " (the keypads of this map stay unusable here)");
            return false;
        }

        var codes = PasscodeGenerator.GeneratePasscodes(entries);
        if (codes == null || codes.Count == 0)
        {
            Report(source, entries.Length, controller._passcodeTerminals?.Count ?? 0, "no-codes",
                "the game's own generator returned no code for this location's entries");
            return false;
        }

        controller._generatedPasscodes = codes;
        var hints = controller.GenerateHints(codes);
        controller._generatedHints = hints;
        if (hints != null && hints.Count > 0) controller.SetupHints(hints);

        _hostCodesReady = true;
        _hostCodeCount = codes.Count;
        _hostCodes = CodesOf(controller);
        Report(source, entries.Length, controller._passcodeTerminals?.Count ?? 0, "generated",
            "this host drew the raid's codes with the game's own PasscodeGenerator and applied the game's own hints"
            + " (hints=" + NativeCount(hints) + ")");
        // 本场突袭的表现在已存在：每个对等端都会立刻拿到它，而不必等到下一次重试。
        RaidInteractionSync.PushPasscodeSnapshot();
        return true;
    }

    // ---- 客户端：围绕主机密码表的原生本地控制器 ------------------------------------

    private static void EnsureClientTable(GameWorld world)
    {
        if (_clientApplied)
        {
            // 客户端的场景在其世界之后加载：键盘锁与提示组都是延迟绑定的。
            if (world.PasscodeController is { } applied) Populate(applied);
            return;
        }

        if (!_clientHasHostCodes || _hostCodes == null || _hostCodes.Count == 0) return;

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextAttempt) return;
        _nextAttempt = now + RetrySeconds;

        var settings = ClientSettings();
        if (settings?.Entries == null || settings.Entries.Length == 0)
        {
            Report("client", 0, 0, "no-entries",
                "this client could not name a single keypad of its location (neither its own location data nor the"
                + " host's snapshot carried an entry), so no native controller is built here");
            return;
        }

        var controller = new ClientLocalPasscodeController(settings);
        Populate(controller);
        controller._generatedPasscodes = ToNativeCodes(_hostCodes);
        var hints = _hostHints != null && _hostHints.Count > 0
            ? ToNativeHints(_hostHints)
            : controller.GenerateHints(controller._generatedPasscodes);
        controller._generatedHints = hints;
        if (hints != null && hints.Count > 0) controller.SetupHints(hints);

        var previous = world.PasscodeController;
        world.PasscodeController = controller;
        previous?.Dispose();
        _clientLocalController = controller.Pointer;
        _clientApplied = true;
        Report("client", settings.Entries.Length, controller._passcodeTerminals?.Count ?? 0, "local-native",
            "this client now checks codes with the game's own local controller on the host's table"
            + " (controller=" + ControllerKind(previous) + " -> local, hints=" + NativeCount(hints)
            + "): no relay and no host answer is needed for a code any more");
    }

    /// <summary>
    /// 客户端用来构建其控制器的条目来源：首先是它自己的地点数据（与主机
    /// 读取的是同一个文件），其次是主机的快照，最后是本场景的键盘锁。
    /// </summary>
    private static PasscodeLocationSettings ClientSettings()
    {
        var own = LocationSettings();
        if (own?.Entries != null && own.Entries.Length > 0) return own;
        if (_hostEntries != null && _hostEntries.Count > 0) return FromEntries(_hostEntries);
        return new PasscodeLocationSettings { Entries = FromSceneIds() };
    }

    // ---- 两种角色共用的原生部件 ---------------------------------------------------------

    /// <summary>
    /// 本机场景中的键盘锁与提示组，通过控制器的原生填充调用取得。
    /// 客户端的场景在其世界之后加载，因此这里会一直重试，直到地图真正拥有了
    /// 它们——r33 日志里的 'terminals=0' 正是一个从未绑定到其场景的控制器。
    /// </summary>
    private static void Populate(PasscodeController controller)
    {
        if (controller == null) return;
        if ((controller._passcodeTerminals?.Count ?? 0) > 0) return;
        try
        {
            controller.GetAllTerminals();
            controller.GetAllHintGroups();
            if ((controller._passcodeTerminals?.Count ?? 0) == 0)
            {
                var map = SceneTerminalMap();
                if (map.Count > 0) controller._passcodeTerminals = map;
            }

            if ((controller._passcodeTerminals?.Count ?? 0) > 0 && _terminalPopulations++ < 4)
            {
                
            }
        }
        catch (Exception exception)
        {
            Report("keys", -1, -1, "failed", "binding the scene's keypads threw " + exception.GetType().Name);
        }
    }

    private static bool HasCodes(ClientLocalPasscodeController controller)
    {
        try
        {
            return controller._generatedPasscodes != null && controller._generatedPasscodes.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, string> CodesOf(ClientLocalPasscodeController controller)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (controller._generatedPasscodes == null) return codes;
            foreach (var pair in controller._generatedPasscodes)
            {
                if (pair.Key == null) continue;
                codes[pair.Key] = pair.Value ?? string.Empty;
            }
        }
        catch (Exception)
        {
        }

        return codes;
    }

    /// <summary>
    /// 本机自行处理的某次提交所得到的原生判定：本场突袭的表是否为这个
    /// 键盘锁保留了正是这个码。它只是被读取（而不是被判定），并只决定是否需要把这次
    /// 尝试告知主机——本机的门是由游戏自己的控制器打开的，而主机会
    /// 在改动任何东西之前用自己那份表执行同样的检查。
    /// </summary>
    internal static bool MatchesLocal(ClientPasscodeController controller, string passcodeId, string input)
    {
        try
        {
            if (controller?.TryCast<ClientLocalPasscodeController>() is not { } local) return false;
            if (string.IsNullOrEmpty(passcodeId) || string.IsNullOrEmpty(input)) return false;
            var codes = local._generatedPasscodes;
            return codes != null && codes.TryGetValue(passcodeId, out var expected)
                && string.Equals(expected, input, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>当本机的世界自行校验密码、而不是去问主机时为 true（这是期望的最终状态）。</summary>
    internal static bool IsLocal(ClientPasscodeController controller)
    {
        try
        {
            return controller?.TryCast<ClientLocalPasscodeController>() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>本机的突袭模型，也就是游戏用来构建世界的那个。</summary>
    private static PasscodeLocationSettings LocationSettings()
    {
        try
        {
            var game = FikaSingleton<IFikaGame>.Instance;
            var location = game?.GameController?.Location;
            return location?.PasscodeLocationSettings;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PasscodeLocationSettings FromEntries(Dictionary<string, string> entries)
    {
        var list = new List<PasscodeEntry>(entries.Count);
        foreach (var pair in entries)
        {
            if (string.IsNullOrEmpty(pair.Key)) continue;
            list.Add(new PasscodeEntry { PasscodeId = pair.Key, PasscodeListId = pair.Value ?? string.Empty });
        }

        return new PasscodeLocationSettings { Entries = new Il2CppReferenceArray<PasscodeEntry>(list.ToArray()) };
    }

    private static Il2CppReferenceArray<PasscodeEntry> FromSceneIds()
    {
        var ids = new List<string>();
        try
        {
            foreach (var terminal in LocationScene.GetAllObjects<PasscodeTerminal>(true).AsManaged())
            {
                if (terminal == null) continue;
                var id = terminal.PasscodeId;
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
            }
        }
        catch (Exception)
        {
        }

        var entries = new List<PasscodeEntry>(ids.Count);
        foreach (var id in ids) entries.Add(new PasscodeEntry { PasscodeId = id, PasscodeListId = string.Empty });
        return new Il2CppReferenceArray<PasscodeEntry>(entries.ToArray());
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<PasscodeTerminal>>
        SceneTerminalMap()
    {
        var map = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<PasscodeTerminal>>();
        try
        {
            foreach (var terminal in LocationScene.GetAllObjects<PasscodeTerminal>(true).AsManaged())
            {
                if (terminal == null || string.IsNullOrEmpty(terminal.PasscodeId)) continue;
                if (!map.ContainsKey(terminal.PasscodeId))
                {
                    map.Add(terminal.PasscodeId, new Il2CppSystem.Collections.Generic.List<PasscodeTerminal>());
                }

                map[terminal.PasscodeId].Add(terminal);
            }
        }
        catch (Exception)
        {
        }

        return map;
    }

    private static string ListIds(Il2CppReferenceArray<PasscodeEntry> entries)
    {
        if (entries == null || entries.Length == 0) return "0";
        var text = new System.Text.StringBuilder();
        var named = 0;
        for (var i = 0; i < entries.Length && i < 8; i++)
        {
            var entry = entries[i];
            if (entry == null) continue;
            if (named++ > 0) text.Append(',');
            text.Append(entry.PasscodeId).Append("->").Append(string.IsNullOrEmpty(entry.PasscodeListId) ? "none" : entry.PasscodeListId);
        }

        return entries.Length + (named > 0 ? "[" + text + "]" : string.Empty);
    }

    private static int Count<TValue>(Dictionary<string, TValue> map) => map?.Count ?? 0;

    /// <summary>Il2Cpp 字典的元素个数（原生提示与密码所用的形态）。</summary>
    private static int NativeCount<TValue>(Il2CppSystem.Collections.Generic.Dictionary<string, TValue> map)
        => map?.Count ?? 0;

    private static string ControllerKind(PasscodeController controller)
    {
        try
        {
            if (controller == null) return "none";
            if (controller.TryCast<ClientNetworkPasscodeController>() != null) return "network";
            if (controller.TryCast<ClientLocalPasscodeController>() != null) return "local";
            return "other";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<string, string> ToNativeCodes(Dictionary<string, string> codes)
    {
        var native = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
        foreach (var pair in codes)
        {
            if (string.IsNullOrEmpty(pair.Key)) continue;
            native[pair.Key] = pair.Value ?? string.Empty;
        }

        return native;
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>
        ToNativeHints(Dictionary<string, string[]> hints)
    {
        var native = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>();
        foreach (var pair in hints)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
            var values = new Il2CppSystem.Collections.Generic.List<string>();
            for (var i = 0; i < pair.Value.Length; i++)
            {
                if (pair.Value[i] == null) continue;
                values.Add(pair.Value[i]);
            }

            native[pair.Key] = values;
        }

        return native;
    }

    /// <summary>键盘锁密码层每个不同状态各一行，且 10 秒内绝不超过一行。</summary>
    private static void Report(string source, int entries, int terminals, string result, string reason)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (result != "generated" && result != "kept-native" && result != "local-native" && now < _nextFailureLine) return;
        _nextFailureLine = now + FailureLineSeconds;
        _failures++;
        
    }
}
