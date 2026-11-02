using EFT.Communications;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Models.Presence;
using Fika.Core.Networking.Websocket;
using HarmonyLib;
using JsonType;
using TMPro;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Custom;

public class MainMenuUIScript : MonoBehaviour
{
    public MainMenuUIScript(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUIScript() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUIScript>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public static bool Exist
    {
        get
        {
            return _instance != null;
        }
    }

    public static MainMenuUIScript Instance
    {
        get
        {
            return _instance;
        }
    }

    /// <summary>
    /// 面板本身一旦存在即为 true。<see cref="Exist"/> 只表示“有一个在线状态上报者
    /// 存活”，这在 'Coop / Enable Online Players' 关闭时同样成立：那六处非 UI
    /// 调用点使用 <see cref="Exist"/> 且只上报存在状态，因此它们必须在
    /// 没有面板的情况下继续工作。除非此值为 true，否则不得向面板写入任何内容。
    /// </summary>
    public bool PanelReady
    {
        get
        {
            return _panelCreated && IsAlive(_userInterface) && IsAlive(_mainMenuUI);
        }
    }

    public bool JoinInProgress
    {
        get => _joinInProgress;
        set
        {
            lock (_joinInProgressLock)
            {
                _joinInProgress = value;
            }
        }
    }

    private static MainMenuUIScript _instance;

    // [fix] 面板泵。面板以前以托管协程方式运行；引擎在它
    // 处理正好是那个对象时崩溃了（Il2CppManagedEnumerator，即
    // StartCoroutine(IEnumerator) 交给引擎的包装器），因此面板改由 Update
    // 驱动，并且不再把枚举器交给引擎去驱动。
    private bool _pumpArmed;
    private bool _pumpAttached;
    private int _pumpStage;
    private int _pumpPolls;
    private float _pumpElapsed;
    private bool _pumpWasReady;
    private IntPtr _lastPumpInstance;
    private IntPtr _lastPumpMenuScreen;
    private float _nextRefreshTime;
    // 当名单为空、*而且* 通知 websocket 仍未连接时，此值为 true——该 socket 的
    // 连接正是把本玩家加入服务器在线名单的原因。这样下一次刷新会在
    // 一秒后而不是十秒后执行，于是本地玩家的那一行会在
    // 服务器知道它之后立刻出现，而不是晚一个 tick。
    private bool _waitingForPresenceSocket;

    // [refresh] 是谁请求了即将执行的这次刷新：引擎重新激活面板时为 "open"，
    // 刷新按钮为 "manual"，普通轮询周期为 null。
    // 只有诊断输出会读取它。
    private string _pendingRefreshSource;

    // [refresh] 面板当前显示的内容：构建这些行时所依据的名单标识
    // （RosterSignature），以及写入计数标签的数量（尚未写入任何内容时为 -1）。
    // 若某次轮询的应答具有相同标识，就保留现有行而不再重建，
    // 这正是让较短的可见周期刷新变得可承受的原因。
    private string _lastRosterSignature;
    private int _labelCount = -1;

    // [refresh] 上一行诊断输出，这样没有变化的轮询就不会重复它。
    private string _lastRefreshReport;

    private int _watchdogStartFrame = -1;
    private int _watchdogMark;
    private static readonly int[] _watchdogMarks = [1, 2, 3, 5, 10, 30, 60, 120];

    private MainMenuUI _mainMenuUI;
    private GameObject _playerTemplate;
    private GameObject _userInterface;
    private List<GameObject> _players;
    private DateTime _lastRefresh;
    private DateTime _lastSet;
    private RectTransform _transformToScale;
    private bool _labelUsable;
    private IMatchmakerSession<RaidSettings> _backendSession;
    private bool _initialized;

    private bool _joinInProgress;
    private readonly object _joinInProgressLock = new();

    // 每项只上报一次，这样损坏的 prefab 引用能被看到，同时不会在每个刷新周期
    // 反复刷屏，而下次崩溃日志也能指出是哪一个引用有问题。
    private bool _labelReported;
    private bool _templateReported;
    private bool _rowReported;
    private bool _refreshReported;
    private bool _panelReported;
    private bool _tooltipTraceReported;
    private bool _counterReported;
    private bool _presenceSocketReported;
    private int _refreshTick;
    private string _labelDetail = "not checked yet";

    // [life] 缓存的 native 包装器：从 prefab 读取一次，在退出路径上再校验一次，
    // 而不是每个 tick 都通过 Il2CppReferenceField.Value 新分配一个包装器。
    private GameObject _panelObject;
    private Button _refreshButton;
    private TextMeshProUGUI _label;
    private Transform _menuScreenTransform;
    private Transform _panelTransform;

    private bool _panelCreated;
    private bool _engineViewReported;

    // [life] 两个由引擎持有、承载 *Fika 注入* 类的对象，它们以前是
    // 局部变量：面板的拖拽组件，以及每行名单一个 MainMenuUIPlayer。引擎
    // 从 bundle 实例化它们，因此其托管侧没有自己的 GC 句柄
    // （FikaNativeTypes / NativeInjectedFinalizer），只能通过这些字段访问。
    // 引擎仍持有某行、而其托管侧却已被回收，正是本次构建所追查的
    // “引擎 -> 托管”调用进入已死对象的情形。
    private UIDragComponent _dragComponent;
    private readonly List<MainMenuUIPlayer> _rowComponents = [];

    /// <summary>
    /// 为本面板交给引擎的每个 native 对象提供强引用根。
    ///
    /// 引擎以裸 il2cpp 指针持有面板、其子对象、注入的 MainMenuUI 组件以及
    /// 已注册的监听器。native 侧的裸指针并
    /// 不能使托管对象保持存活，因此引擎仍可能调用的任何对象都必须同时
    /// 从托管代码可达——这就是“引擎调用
    /// 我的对象”与“引擎调用已释放对象”的区别（47888 号转储读到的是一个
    /// 根本不是对象指针的值，说明至少有一次这样的交接已经丢失）。
    /// 所有交接出去的对象都经过 <see cref="Root"/>，并在
    /// <see cref="DestroyPanel"/> 中再次被移除，因此该列表不会比单个面板活得更久。
    /// </summary>
    private static readonly List<RootEntry> _roots = [];

    private readonly struct RootEntry
    {
        internal readonly string Tag;
        internal readonly object Target;

        internal RootEntry(string tag, object target)
        {
            Tag = tag;
            Target = target;
        }
    }

    /// <summary>
    /// 当前注册在 *native* UnityEvent 中的托管委托。native
    /// 事件会让自己内部的跳板保持存活；其背后的 CLR 委托（以及随之而来的目标）
    /// 在此保持可达，直到它被再次移除。
    /// </summary>
    private readonly List<(Button Button, Action Listener)> _rowListeners = [];
    private Action _refreshListener;

    private const int _minSecondsToWait = 2;

    /// <summary>
    /// 在任何协程或面板工作开始之前，注册引擎会调用的注入组件
    /// （Start/OnEnable/OnDisable）。由 MenuScreen 补丁调用，该补丁是
    /// 唯一的创建者。
    /// </summary>
    internal static void Attach(MainMenuUIScript instance)
    {
        if (instance is null)
        {
            return;
        }

        if (_instance != null && !ReferenceEquals(_instance, instance))
        {
            
            
            _instance.DestroyPanel();
        }

        _instance = instance;
        Root("behaviour", instance);
        
    }

    private static void Root(string tag, object target)
    {
        if (target is null)
        {
            return;
        }

        lock (_roots)
        {
            foreach (var entry in _roots)
            {
                if (ReferenceEquals(entry.Target, target))
                {
                    return;
                }
            }

            _roots.Add(new RootEntry(tag, target));
        }

        
    }

    private void Release(params object[] targets)
    {
        lock (_roots)
        {
            foreach (var target in targets)
            {
                if (target is null)
                {
                    continue;
                }

                for (var index = _roots.Count - 1; index >= 0; index--)
                {
                    if (ReferenceEquals(_roots[index].Target, target))
                    {
                        _roots.RemoveAt(index);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 把无法启动 ping 的具体原因追加到对话框正文，这样玩家
    /// 不必打开日志就能区分“主机从未公布地址”和“我们无法
    /// 连上服务器”。没有额外细节时为空。
    /// </summary>
    private static string PingingFailureDetail(FikaPingingClient pingingClient) =>
        string.IsNullOrEmpty(pingingClient?.FailureReason) ? string.Empty : "\n\n" + pingingClient.FailureReason;


    /// <summary>
    /// 拆除本实例交给引擎的所有东西：先从 native 事件中移除
    /// 监听器（这样引擎就不会调用目标已消失的委托），
    /// 然后销毁面板层级并释放所有根。
    /// </summary>
    internal void DestroyPanel()
    {
        TeardownPanel("the menu screen released the panel", stopCoroutines: true, protecting: false);
    }

    /// <summary>
    /// 唯一的拆除路径。顺序正是关键所在：先处理 native 监听器（
    /// 引擎不得调用目标正在销毁中的委托），然后
    /// 停止协程（引擎不得继续驱动一个本脚本已不再
    /// 跟踪的枚举器），最后销毁面板层级并释放所有根。
    ///
    /// <paramref name="protecting"/> 标记自我保护式调用：面板发现
    /// 引擎已不再持有先前交给它的对象，于是它在同一帧内销毁自己。这样损坏的
    /// 面板最坏也只是消失，而不会让引擎调用进入已死对象。
    /// </summary>
    private void TeardownPanel(string reason, bool stopCoroutines, bool protecting)
    {
        RemoveRowListeners();
        DestroyRows();

        if (_refreshButton != null && _refreshListener != null && IsAlive(_refreshButton))
        {
            try
            {
                Fika.Core.UnityEventCompatExtensions.RemoveListener(_refreshButton.onClick, _refreshListener);
            }
            catch (Exception exception)
            {
                
            }
        }

        if (stopCoroutines)
        {
            StopCoroutines();
        }
        else
        {
            // 该行为本身正在被销毁：解除泵的启用状态时不会触碰
            // 引擎（已不存在引擎协程，因此无需停止任何东西）。
            StopCoroutines();
        }

        _refreshButton = null;
        _refreshListener = null;
        _label = null;
        _transformToScale = null;
        _dragComponent = null;

        if (IsAlive(_panelObject))
        {
            GameObject.Destroy(_panelObject);
        }

        _panelObject = null;
        _playerTemplate = null;
        _userInterface = null;
        _mainMenuUI = null;
        _panelTransform = null;
        _panelCreated = false;
        _engineViewReported = false;
        _pendingRefreshSource = null;
        _lastRosterSignature = null;
        _labelCount = -1;
        _players?.Clear();
        Release(_panelObject, _userInterface, _mainMenuUI, _transformToScale, _panelTransform, _refreshButton,
            _label, _playerTemplate, _dragComponent, _refreshListener);
        
        
    }

    /// <summary>
    /// 解除面板泵的启用状态。
    ///
    /// 已没有引擎协程需要停止：面板由 Update 驱动，因此
    /// 这里无需跟踪引擎持有的任何东西。这就是菜单故障的修复——
    /// 引擎在处理面板协程的包装器时崩溃，并把其
    /// 托管对象解析成了错误指针 0xA（由首次机会探测器
    /// 在引擎自身栈帧中捕获）。
    /// </summary>
    private void StopCoroutines()
    {
        _pumpArmed = false;
        _nextRefreshTime = 0f;
        _watchdogStartFrame = -1;
    }

    /// <summary>
    /// 每个刷新 tick 都会执行的廉价自我保护，无论裸指针
    /// 追踪是否开启：本面板交给引擎的对象是否仍然可用？当其中某个
    /// 不可用时，面板会在同一帧内被销毁，而不是继续被使用。
    /// </summary>
    private bool EnsurePanelUsable()
    {
        if (!_panelCreated)
        {
            // 尚未交接任何对象（或已完成拆除）：无需保护。
            return false;
        }

        string reason = null;
        if (!NativeRefGuard.IsUsable(_panelObject))
        {
            reason = "the panel root is no longer a usable native object";
        }
        else if (!NativeRefGuard.IsUsable(_userInterface))
        {
            reason = "the panel content object is no longer a usable native object";
        }
        else if (!NativeRefGuard.IsUsable(_mainMenuUI))
        {
            reason = "the panel component is no longer a usable native object";
        }

        if (reason is null)
        {
            return true;
        }

        FikaGlobals.LogWarning("[MainMenuUI] " + reason + "; the panel is destroyed in the same frame instead of being used");
        TeardownPanel(reason, stopCoroutines: true, protecting: true);
        return false;
    }

    /// <summary>
    /// 放弃构建面板。此时引擎已持有这些已实例化的对象，
    /// 因此它们会被再次销毁，而不是被留在菜单屏幕下且没有
    /// 归属者（引擎仍持有的孤儿对象，正是本面板绝不允许留下的
    /// 状态）。
    /// </summary>
    private void AbortPanelCreation(string reason)
    {
        FikaGlobals.LogError(reason);
        
        DestroyPanel();
    }

    private void RemoveRowListeners()
    {
        foreach ((var button, var listener) in _rowListeners)
        {
            try
            {
                if (button != null && listener != null && IsAlive(button))
                {
                    Fika.Core.UnityEventCompatExtensions.RemoveListener(button.onClick, listener);
                }
            }
            catch (Exception exception)
            {
                
            }

            // native 监听器已移除，因此该 CLR 委托——以及随之捕获了
            // 本行为和一个 native location 对象的闭包——不必再
            // 保持为根。否则根列表每刷新一个 tick 就会多出一个委托。
            Release(listener);
        }

        _rowListeners.Clear();
    }

    /// <summary>
    /// 销毁名单行并释放属于它们的每个根，其中包括
    /// 每行中 Fika 注入的 <see cref="MainMenuUIPlayer"/>：该实例由引擎
    /// 从 bundle 创建，因此只要引擎持有该行，其托管侧就必须
    /// 保持可达。
    /// </summary>
    private void DestroyRows()
    {
        if (_players != null)
        {
            foreach (var item in _players)
            {
                Release(item);
                if (IsAlive(item))
                {
                    GameObject.Destroy(item);
                }
            }

            _players.Clear();
        }

        foreach (var component in _rowComponents)
        {
            Release(component);
        }

        _rowComponents.Clear();
    }

    /// <summary>
    /// 面板自身的 transform、其父级及其组件，每个 tick 都从
    /// 引擎重新读取。如果引擎已不再持有本面板交给它的对象，这一点必须
    /// 在引擎调用它 *之前* 就出现在日志里——正是这项检查区分了
    /// “Fika 写入了坏数据”与“引擎丢失了引用”。
    /// </summary>
    private void VerifyEngineView()
    {
        if (!IsAlive(_panelObject))
        {
            return;
        }

        try
        {
            var parent = _panelObject.transform.parent;
            var parentMatches = IsAlive(parent) && IsAlive(_menuScreenTransform)
                && parent.Pointer == _menuScreenTransform.Pointer;

            var component = _panelObject.GetComponent<MainMenuUI>();
            var componentMatches = IsAlive(component) && IsAlive(_mainMenuUI)
                && component.Pointer == _mainMenuUI.Pointer;

            var active = _panelObject.activeInHierarchy;

            if (parentMatches && componentMatches && active)
            {
                if (!_engineViewReported)
                {
                    _engineViewReported = true;
                    
                }

                return;
            }

            // 自我保护：引擎丢弃了本脚本交给它的一个引用。面板
            // 会在同一帧内被销毁——在引擎再次有机会调用
            // 它之前——并且该行会准确说明发生了什么变化。
            FikaGlobals.LogWarning("[MainMenuUI] the engine no longer holds the panel this script handed it "
                + $"(parentMatches={parentMatches}, componentMatches={componentMatches}, active={active}); "
                + "the panel is destroyed in this frame instead of being used");
            
            TeardownPanel("engine view changed: parentMatches=" + parentMatches
                + ", componentMatches=" + componentMatches + ", active=" + active,
                stopCoroutines: true, protecting: true);
        }
        catch (Exception exception)
        {
            
        }
    }

    // SPT 5 的菜单 canvas 应用的缩放系数比原始 Fika prefab 使用的
    // Mono 菜单更大。把用户设置保留为相对
    // 比例，但应用修正后的 prefab 基准值，这样包含旧默认值 1.0 的
    // 现有配置文件会被自动迁移。
    private static float GetOnlinePlayersScale()
    {
        var configured = FikaPlugin.Instance.Settings.OnlinePlayersScale.Value;
        return Math.Clamp(configured * 0.75f, 0.4f, 1.0f);
    }

    private DateTime BackendTime
    {
        get
        {
            if (_backendSession == null)
            {
                return DateTime.MinValue;
            }
            return _backendSession.GetCurrentLocationTime.ToManaged();
        }
    }

    private DateTime StaticTime
    {
        get
        {
            return new DateTime(2016, 8, 4, 15, 28, 0);
        }
    }

    protected void Start()
    {
        
        PumpStart();
    }

    /// <summary>
    /// 引擎回调：引擎对新添加行为的首次触碰。该步骤会写入
    /// 稳定文件（而不只是 BepInEx 的日志，后者下次启动就会被清空），这样
    /// “engine Awake -> patch Attach -> engine Start -> Initialize”的顺序在故障后仍可证明。
    /// </summary>
    private void Awake()
    {
        
    }

    private int _engineTicks;

    /// <summary>
    /// 引擎心跳（有界：第一帧，之后每 300 帧一次），写入稳定文件。
    /// 引擎每帧都会调用这个注入行为；如果心跳是故障前的
    /// 最后一行，说明引擎仍在驱动该行为，那么面板对象就是
    /// 剩下的嫌疑对象；而如果面板对象变坏时心跳仍在继续，那么问题的关键就是
    /// 这些对象的交接过程。
    /// </summary>
    private void Update()
    {
        _engineTicks++;
        if (_engineTicks == 1 || _engineTicks % 300 == 0)
        {
            
        }

        // 面板本身在这里运行（已不再涉及引擎协程）。
        try
        {
            Pump();
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// 无法解引用的 native il2cpp 对象。Il2CppInterop 会为
    /// 未实现的 Unity 对象，以及由 bundle 或 prefab 连好的引用返回这类对象，而对它的每次
    /// 成员访问都会在 GameAssembly 内部触发故障（0xC0000005），而不是抛出
    /// 托管异常。47888 系列转储在 GameAssembly+0x2BE60（"mov rax,[rcx]"）处故障，
    /// 读取地址 0x0A——这是一个小整数而非 null，而此处本应是对象指针——
    /// 因此该守卫同时拒绝 null 和这类非指针值（NativeRefGuard）。
    /// </summary>
    private static bool IsAlive(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase instance)
    {
        return NativeRefGuard.IsUsable(instance);
    }

    /// <summary>
    /// 同时写入稳定诊断文件和 BepInEx 日志的面板步骤。
    ///
    /// BepInEx/LogOutput.log 每次游戏启动都会被清空，因此只写在那里的步骤，在
    /// 崩溃被上报时早已消失——所以面板的进度也必须
    /// 写入 Fika-diagnostics.log：故障前的最后一行才指明了出错的
    /// 步骤。
    /// </summary>
    private static void PanelStep(string message)
    {
        FikaGlobals.LogInfo(message);
        
    }

    // 纯装饰性面板不应每十秒就用同一条抱怨把日志刷满。
    private static void ReportOnce(ref bool alreadyReported, string message)
    {
        if (alreadyReported)
        {
            return;
        }

        alreadyReported = true;
        FikaGlobals.LogWarning(message);
    }

    /// <summary>
    /// 对菜单 canvas 的一次轮询，附带它读取的每个 native 对象的裸指针。
    ///
    /// 本面板追查的故障是携带错误对象指针的 engine -> il2cpp 调用，因此
    /// 这些对象在被使用 *之前* 就写入稳定诊断文件：故障前的
    /// 最后一行随即指明了引擎当时持有的对象。轮询之间不缓存
    /// 任何东西——单例、实例和菜单屏幕每轮都会重新读取并重新校验，
    /// 因此绝不会顺着引擎重建过的 canvas 走：在新链路
    /// 自证可用之前，轮询会回答“尚未就绪”并说明原因。
    /// </summary>
    private static bool PollMenuCanvas(out string verdict, out IntPtr commonUiPointer, out IntPtr menuScreenPointer)
    {
        commonUiPointer = IntPtr.Zero;
        menuScreenPointer = IntPtr.Zero;
        CommonUI commonUi;

        try
        {
            if (!Singleton<CommonUI>.Instantiated)
            {
                verdict = "CommonUI is not instantiated yet";
                return false;
            }

            commonUi = Singleton<CommonUI>.Instance;
        }
        catch (Exception exception)
        {
            verdict = "reading the CommonUI singleton failed: " + exception.Message;
            return false;
        }

        if (commonUi is not null)
        {
            commonUiPointer = commonUi.Pointer;
        }

        // 只做 null 检查是不够的：“已销毁但非 null”正是引擎侧故障
        // 所产生的那种状态，因此在该对象的任何成员被触碰之前，
        // 该引用必须是预期类型的、可读的 il2cpp 对象。
        if (!NativeRefGuard.IsProvablyUsable(commonUi, "CommonUI", out string commonUiDetail))
        {
            verdict = "CommonUI instance is not provably usable: " + commonUiDetail;
            return false;
        }

        MenuScreen menuScreen;
        try
        {
            menuScreen = commonUi.MenuScreen;
        }
        catch (Exception exception)
        {
            verdict = "reading CommonUI.MenuScreen failed: " + exception.Message;
            return false;
        }

        if (menuScreen is not null)
        {
            menuScreenPointer = menuScreen.Pointer;
        }

        if (!NativeRefGuard.IsProvablyUsable(menuScreen, "MenuScreen", out string menuScreenDetail))
        {
            verdict = "MenuScreen is not provably usable: " + menuScreenDetail;
            return false;
        }

        verdict = "commonUi=" + commonUiDetail + ", menuScreen=" + menuScreenDetail;
        return true;
    }

    /// <summary>
    /// 启用面板泵，并写出协程过去写的那条相同的第一步记录。
    ///
    /// 面板刻意不再以托管协程方式运行。引擎在它
    /// 处理正好是那个对象时崩溃了：<c>Il2CppManagedEnumerator</c>——即
    /// <c>StartCoroutine(IEnumerator)</c> 为托管协程交给引擎的包装器——而
    /// 首次机会探测器在引擎自身栈帧中捕获到它：当时引擎正在为它解引用
    /// 那个错误对象 0xA。面板所做的任何事都不需要引擎的协程
    /// 机制，因此整个面板改由本行为的 Update 驱动。
    /// </summary>
    private void PumpStart()
    {
        _pumpArmed = true;
        _pumpAttached = false;
        _pumpStage = 0;
        _pumpPolls = 0;
        _pumpElapsed = 0f;
        _pumpWasReady = false;
        _lastPumpInstance = IntPtr.Zero;
        _lastPumpMenuScreen = IntPtr.Zero;
        _nextRefreshTime = 0f;
        _watchdogStartFrame = -1;
        _watchdogMark = 0;
        _players = [];
        _lastRefresh = DateTime.Now;
        _lastSet = DateTime.Now;
        _pendingRefreshSource = null;
        _lastRosterSignature = null;
        _labelCount = -1;
        _lastRefreshReport = null;
        
    }

    /// <summary>
    /// 每个引擎帧执行一次泵步。阶段 0 等待本地化数据和菜单 canvas，然后
    /// 构建面板；阶段 1 按面板自身的周期重复刷新（参见
    /// <see cref="NextRefreshDelay"/>：面板可见时为几秒，而不可见时沿用
    /// 一直使用的十秒），并写入看门狗标记。这与协程运行的是
    /// 同一份代码——只是“等待下一帧”变成了“返回并再次被
    /// Update 调用”。
    /// </summary>
    private void Pump()
    {
        if (!_pumpArmed)
        {
            return;
        }

        if (!_pumpAttached)
        {
            _pumpAttached = true;
            // 在任何等待或分配发生之前先把注入行为设为根：从此刻起引擎随时可能
            // 对它调用 OnEnable/OnDisable。
            Attach(this);
        }

        if (_pumpStage == 0)
        {
            if (!FikaPlugin.Instance.LocalesLoaded)
            {
                if (FikaPlugin.Instance.InitializationError != null)
                {
                    _pumpArmed = false;
                }

                return;
            }

            _pumpPolls++;
            _pumpElapsed += Time.unscaledDeltaTime;
            var ready = PollMenuCanvas(out string verdict, out var commonUiPointer, out var menuScreenPointer);
            var instanceChanged = _lastPumpInstance != IntPtr.Zero && commonUiPointer != IntPtr.Zero && _lastPumpInstance != commonUiPointer;
            var menuScreenChanged = _lastPumpMenuScreen != IntPtr.Zero && menuScreenPointer != IntPtr.Zero && _lastPumpMenuScreen != menuScreenPointer;

            if (_pumpPolls <= 5 || ready != _pumpWasReady || instanceChanged || menuScreenChanged || _pumpPolls % 60 == 0)
            {
                
            }

            _lastPumpInstance = commonUiPointer != IntPtr.Zero ? commonUiPointer : _lastPumpInstance;
            _lastPumpMenuScreen = menuScreenPointer != IntPtr.Zero ? menuScreenPointer : _lastPumpMenuScreen;
            _pumpWasReady = ready;

            if (!ready)
            {
                if (_pumpElapsed > 30f)
                {
                    FikaGlobals.LogError("[MainMenuUI] CommonUI/MenuScreen never became available; the online players panel is disabled");
                    
                    _pumpArmed = false;
                }

                return;
            }

            PanelStep("[MainMenuUI] menu canvas is ready, building the online players panel");

            // 引用守卫所读取的类布局，会先在一个类型已知的引用上确认，
            // 之后才会用它去信任任何序列化的 prefab 引用。
            if (Singleton<CommonUI>.Instantiated)
            {
                NativeRefGuard.VerifyTypeProbe(Singleton<CommonUI>.Instance, "CommonUI");
            }

            // Coop / 'Enable Online Players' 是面板自己的总开关：关闭时不会实例化
            // prefab 的任何内容，也完全不会触碰任何 bundle 引用。
            if (!(FikaPlugin.Instance.Settings.EnableOnlinePlayers?.Value ?? true))
            {
                FikaGlobals.LogWarning("[MainMenuUI] the online players panel is disabled in the config (Coop / 'Enable Online Players' = false); the panel prefab is not created");
                _pumpArmed = false;
                return;
            }

            if (TarkovApplication.Exist(out var tarkovApplication) && tarkovApplication.Session != null)
            {
                _backendSession = tarkovApplication.Session;
            }

            CreateMainMenuUI();
            _initialized = true;
            OnEnable();
            _pumpStage = 1;
            _nextRefreshTime = 0f;
            return;
        }

        // 阶段 1：刷新计时器和刷新后的看门狗标记（即旧看门狗
        // 协程）——两者现在都只是普通的帧计数器。
        if (_watchdogStartFrame >= 0 && _watchdogMark < _watchdogMarks.Length)
        {
            var elapsedFrames = Time.frameCount - _watchdogStartFrame;
            if (elapsedFrames >= _watchdogMarks[_watchdogMark])
            {
                
                _watchdogMark++;
                if (_watchdogMark >= _watchdogMarks.Length)
                {
                    _watchdogStartFrame = -1;
                }
            }
        }

        if (Time.unscaledTime < _nextRefreshTime)
        {
            return;
        }

        // [refresh] 周期现在归面板所有：可见时为几秒（Coop /
        // 'Online Players Refresh Seconds'，默认 3），而不可见时沿用一直使用的
        // 十秒（Coop / 'Online Players Idle Refresh Seconds'）。计时器在发送请求 *之前* 就向前推进，
        // 这样耗时较长的应答——实测该漏斗耗时在 12 ms 到
        // 1.6 s 之间——无法在同一帧内启动第二次请求。
        var visible = IsPanelVisible();
        var delay = NextRefreshDelay(visible);
        var source = _pendingRefreshSource ?? "poll";
        _pendingRefreshSource = null;
        _nextRefreshTime = Time.unscaledTime + delay;
        _waitingForPresenceSocket = false;
        _refreshTick++;
        _watchdogStartFrame = Time.frameCount;
        _watchdogMark = 0;

        try
        {
            ClearAndQueryPlayers(_refreshTick, source, delay, false);
        }
        catch (Exception exception)
        {
            // 这里的托管失败（服务器宕机、缺少存档）绝不能杀死泵。
            ReportOnce(ref _refreshReported, $"[MainMenuUI] the player list could not be refreshed: {exception}");
        }
    }

    /// <summary>
    /// 在线状态列表就是服务器的在线名单，而玩家只有在
    /// <c>/fika/notification/</c> websocket 已连接时才会进入该名单（其连接处理器会
    /// 注册该玩家）——因此菜单刚打开时若该 socket 仍在连接中，
    /// 返回空应答是正常的，面板绝不能在这个“0 名玩家”的结果上停留
    /// 整整一个 tick。只记录一次：每次重试的原因都相同。
    /// </summary>
    private void ScheduleFasterRefreshWhenEmpty(int received)
    {
        _waitingForPresenceSocket = received == 0
            && FikaNotificationManager.Exists
            && !FikaNotificationManager.IsConnected;
        if (!_waitingForPresenceSocket)
        {
            return;
        }

        ReportOnce(ref _presenceSocketReported, "[MainMenuUI] no one is listed yet and the notification websocket is still connecting; "
            + "the list is asked again in a second (the server registers a player when that socket connects)");
    }

    /// <summary>
    /// 本行为构建的面板在屏幕上时此值为 true。
    ///
    /// Update 只在引擎启用并激活本行为时运行，因此这里剩下的一种情况，是引擎在
    /// <see cref="OnEnable"/> 中重新激活的内容对象：当战局或加载界面
    /// 存在时，整个菜单屏幕处于非激活状态，面板完全不会轮询——
    /// 这就是“看不见时不发请求”那一半的由来。
    /// </summary>
    private bool IsPanelVisible()
    {
        if (!_panelCreated || !IsAlive(_userInterface))
        {
            return false;
        }

        try
        {
            return _userInterface.activeInHierarchy;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 距离下一次轮询的延迟。
    ///
    /// 'Coop / Online Players Refresh Seconds'（默认 3）是面板可见时的周期：
    /// 该列表是实时名单，因此玩家加入或离开都必须在几秒内
    /// 体现出来。未发生变化的周期只花费一次 HTTP GET——只有当名单确实变化时才重建行
    /// （参见 <see cref="ClearAndQueryPlayers"/>）——这正是让该周期对服务器和帧开销
    /// 都很低的原因。'Coop / Online Players Idle Refresh Seconds'
    /// （默认 10）是面板不可见时的周期，并保留面板一直使用的
    /// 十秒。
    ///
    /// 关闭 'Coop / Online Players Fast Refresh' 会回到旧的固定十秒周期：一次开关
    /// 就能把面板恢复到改动前的节奏。
    /// </summary>
    private float NextRefreshDelay(bool visible)
    {
        // 未改动：当在线状态 websocket——其连接正是把本玩家加入
        // 服务器在线名单的那个 socket——尚未连接时的空名单，会在一秒后
        // 再次查询，而不是等一整轮。
        if (_waitingForPresenceSocket)
        {
            return 1f;
        }

        if (!FastRefreshEnabled())
        {
            return 10f;
        }

        var settings = SettingsOrNull();
        return visible
            ? Math.Clamp(SettingInt(settings is null ? null : settings.OnlinePlayersRefreshSeconds, 3), 1, 60)
            : Math.Clamp(SettingInt(settings is null ? null : settings.OnlinePlayersIdleRefreshSeconds, 10), 1, 300);
    }

    /// <summary>
    /// 'Coop / Online Players Fast Refresh'：这个开关把面板恢复为
    /// 改动前的路径（固定十秒周期，且每轮都完整重建）。缺少该
    /// 条目时——旧配置文件、尚未绑定的设置——保持随附的默认值。
    /// </summary>
    private static bool FastRefreshEnabled()
    {
        var settings = SettingsOrNull();
        return settings is null || (settings.OnlinePlayersFastRefresh?.Value ?? true);
    }

    private static FikaConfig SettingsOrNull()
    {
        var plugin = FikaPlugin.Instance;
        return plugin is null ? null : plugin.Settings;
    }

    private static int SettingInt(BepInEx.Configuration.ConfigEntry<int> entry, int fallback)
    {
        return entry is null ? fallback : entry.Value;
    }

    /// <summary>
    /// 服务器刚返回的名单的稳定标识：面板中每一行所显示的
    /// 全部内容（谁在线、其等级、正在做什么，以及——对处于战局中的人——
    /// 哪一场战局、哪一方、哪个地点，以及是否已经开始）。
    ///
    /// 该应答是服务器上的字典转储（"/fika/presence/get" 返回
    /// PresenceService.AllPlayersPresence），因此其顺序不稳定；条目在哈希之前会先排序，
    /// 否则一个仅顺序不同的应答会被视为变化，从而白白重建
    /// 每一行——并重新注册每个 join 监听器。
    /// </summary>
    private static string RosterSignature(FikaPlayerPresence[] responses)
    {
        if (responses is null || responses.Length == 0)
        {
            return "0";
        }

        var entries = new List<string>(responses.Length);
        foreach (var presence in responses)
        {
            entries.Add(presence.RaidInformation is { } raid
                ? $"{presence.Nickname}|{presence.Level}|{presence.Activity}|{raid.MatchId}|{raid.Started}|{raid.Location}|{raid.Side}"
                : $"{presence.Nickname}|{presence.Level}|{presence.Activity}|-");
        }

        entries.Sort(StringComparer.Ordinal);

        var hash = 2166136261u;
        foreach (var entry in entries)
        {
            foreach (var character in entry)
            {
                hash = (hash ^ (uint)character) * 16777619u;
            }

            hash = (hash ^ 0x1Fu) * 16777619u;
        }

        return responses.Length.ToString() + ":" + hash.ToString("X8");
    }

    /// <summary>
    /// 这些行是否必须重建。行就是面板所显示的内容，因此只有当它们可证明 *就是*
    /// 该应答时才保留：名单标识相同、面板
    /// 为每个列出的玩家各持有一行，且这些行中每一个仍是可用的 native
    /// 对象。引擎已销毁的行、无法完成绑定的行，或从未绘制过的
    /// 名单，都会让本轮走完整路径。
    /// </summary>
    private bool RebuildNeeded(bool changed, int players)
    {
        // 总开关背后的改动前路径：每轮都完整重建所有内容。
        if (!FastRefreshEnabled())
        {
            return true;
        }

        if (changed || _players is null || _players.Count != players)
        {
            return true;
        }

        foreach (var player in _players)
        {
            if (!IsAlive(player))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 每次刷新状态变化输出一行便于 grep 的记录：
    /// <c>[OnlinePlayers] refresh source=open|poll|change|manual players=N took=..ms next=..s render=..</c>.
    ///
    /// 它回答了下一轮必须确定的问题——列表是否及时、什么代价由什么造成——
    /// 给出触发器、名单大小、刷新占用主线程的时间，以及为下一次轮询
    /// 安排的延迟。仅当它与上一行不同时才会写出（
    /// 比较时排除时间数据），因此三秒周期下稳定的名单只会
    /// 每分钟写一次，而不是二十次。
    /// </summary>
    private void ReportRefresh(string source, int players, long tookMs, float nextSeconds, string detail)
    {
        var fields = "source=" + source + " players=" + players + " next=" + nextSeconds.ToString("0.#") + "s " + detail;
        if (fields == _lastRefreshReport)
        {
            return;
        }

        _lastRefreshReport = fields;
        var line = "[OnlinePlayers] refresh " + fields + " took=" + tookMs + "ms";
        FikaGlobals.LogInfo(line);
        
    }

    protected void OnEnable()
    {
        // Unity 会在 Start 之前调用 OnEnable，此时配置/UI 可能尚未就绪。
        if (!_initialized) return;
        // 面板创建可能失败（参见 CreateMainMenuUI）；那时没有任何可切换的东西。
        if (_userInterface is null) return;
        if (!IsAlive(_userInterface))
        {
            ReportOnce(ref _panelReported, "[MainMenuUI] the panel content object is no longer a usable native object; the online players panel stays off");
            return;
        }

        if (!FikaPlugin.Instance.Settings.EnableOnlinePlayers.Value)
        {
            _userInterface.SetActive(false);
            return;
        }

        _userInterface.SetActive(true);
        

        // 现在归引擎所有的全部对象：面板根、引擎激活的内容、
        // 注入的组件，以及应用缩放的那个 transform。
        Root("panel", _panelObject);
        Root("content", _userInterface);
        Root("panel component", _mainMenuUI);
        Root("scale target", _transformToScale);

        if (IsAlive(_transformToScale))
        {
            var scale = GetOnlinePlayersScale();
            _transformToScale.localScale = new(scale, scale, scale);
        }
        else
        {
            ReportOnce(ref _panelReported, "[MainMenuUI] the panel content transform is no longer usable; the configured scale is not applied");
        }

        if (!IsAlive(_mainMenuUI))
        {
            ReportOnce(ref _panelReported, "[MainMenuUI] the panel component is no longer usable; the player list is not refreshed");
            return;
        }

        // 启用刷新泵：它由本行为的 Update 运行，因此面板不会
        // 把枚举器交给引擎去驱动（那个包装器正是引擎
        // 崩溃时的对象）。
        //
        // [fix] 每当本行为所在的菜单屏幕离开屏幕（进入战局、加载界面、
        // 屏幕切换）时，引擎就会禁用本行为，并在回到菜单时
        // 重新启用它，而 OnDisable 过去会解除泵的启用：那时这里只恢复了阶段，因此
        // 面板轮询到第一次禁用后，就在余下的整个会话里停在
        // 已有的那些行上——即被上报的“在线玩家列表不刷新”。Update 只在
        // 本行为被启用且激活时运行，因此在这里重新启用它，正是引擎
        // 所要求的状态。
        _pumpArmed = true;
        _pumpStage = 1;
        // [refresh] 面板刚刚被（重新）打开：下一个泵 tick 会立即刷新它，
        // 而不是先把当前周期走完，诊断输出也会说明这一点（source=open）。
        _pendingRefreshSource = "open";
        _nextRefreshTime = 0f;

        PanelStep("[MainMenuUI] refresh pump armed (Update driven, no engine coroutine)");
    }

    protected void OnDisable()
    {
        // 解除泵的启用；已没有引擎协程需要停止。
        StopCoroutines();
    }

    protected void OnDestroy()
    {
        // 每次屏幕切换都会销毁菜单屏幕（以及本行为）；
        // 该行为正在被销毁：它的协程也随之销毁，此时调用 StopCoroutine
        // 会触碰一个正在消亡的 native 对象。
        TeardownPanel("the menu screen behaviour was destroyed", stopCoroutines: false, protecting: false);
    }

    // 刷新后的看门狗标记现在由 Pump（阶段 1）依据帧计数器写入；
    // 过去负责写它们的协程已随其余引擎驱动的协程一并移除。

    private void CreateMainMenuUI()
    {
        // 下面每一处访问以前都是无条件的。缺失的资源或未实现的
        // Unity 对象，会在 il2cpp 内部变成 native 访问违例，而不是
        // 托管异常，因此每一步都要校验、上报，并允许其失败而
        // 不把客户端搞崩：这个面板只是装饰性的。
        try
        {
            PanelStep("[MainMenuUI] 1/8 loading the prefab");
            var mainMenuUIPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.MainMenuUI);
            
            if (!IsAlive(mainMenuUIPrefab))
            {
                FikaGlobals.LogError("[MainMenuUI] MainMenuUI.prefab is unavailable; the online players panel is disabled");
                return;
            }

            PanelStep("[MainMenuUI] 2/8 instantiating the panel");
            var mainMenuUI = GameObject.Instantiate(mainMenuUIPrefab);
            // 从引擎拥有的面板及其内容存在的那一刻起，就把它们保持为强根：
            // 从此之后，引擎可能是唯一的另一个持有者。
            _panelObject = IsAlive(mainMenuUI) ? mainMenuUI : null;
            Root("panel", _panelObject);
            
            if (!IsAlive(mainMenuUI))
            {
                FikaGlobals.LogError("[MainMenuUI] the panel prefab could not be instantiated; the online players panel is disabled");
                return;
            }

            PanelStep("[MainMenuUI] 3/8 binding the MainMenuUI component");
            _mainMenuUI = mainMenuUI.GetComponent<MainMenuUI>();
            
            if (!IsAlive(_mainMenuUI))
            {
                AbortPanelCreation("[MainMenuUI] the panel prefab has no bound MainMenuUI component; the online players panel is disabled");
                return;
            }

            Root("panel component", _mainMenuUI);

            PanelStep("[MainMenuUI] 4/8 reading the serialized references");
            var playerTemplateField = _mainMenuUI.PlayerTemplate;
            if (playerTemplateField is null)
            {
                AbortPanelCreation("[MainMenuUI] MainMenuUI.PlayerTemplate was not injected; the online players panel is disabled");
                return;
            }

            var playerTemplate = playerTemplateField.Value;
            
            _playerTemplate = IsAlive(playerTemplate) ? playerTemplate : null;
            Root("player template", _playerTemplate);
            if (_playerTemplate is null)
            {
                FikaGlobals.LogWarning("[MainMenuUI] MainMenuUI.PlayerTemplate is empty in the prefab; the player list stays empty");
            }

            PanelStep("[MainMenuUI] 5/8 checking the panel label");
            // MainMenuUI.UpdateLabel 会直接写入这个 TextMeshProUGUI。某个字段若
            // 持有的不是对象指针（转储中显示为 0x0A），就会使这次
            // 写入变成一次 native 解引用，因此这里要校验它——并且每次
            // 刷新时再校验一次——而不是直接信任它。
            var labelField = _mainMenuUI.Label;
            var label = labelField is null ? null : labelField.Value;
            // 仅凭“看起来合理”是不够的：该值必须是预期类型的、可读的
            // il2cpp 对象，否则这次写入会破坏它真正指向的任何东西。
            _labelUsable = NativeRefGuard.IsProvablyUsable(label, "TextMeshProUGUI", out _labelDetail);
            // 缓存写入计数的那个包装器，这样任何 tick 都不必再读取序列化的
            // 引用字段（并再分配一个包装器）。
            _label = _labelUsable ? label : null;
            Root("label", _label);
            
            if (!_labelUsable)
            {
                FikaGlobals.LogWarning($"[MainMenuUI] MainMenuUI.Label is not a provably usable native object ({_labelDetail}); the online player counter is skipped instead of being written");
            }

            PanelStep("[MainMenuUI] 6/8 attaching the panel to the menu screen");
            var commonUiForScreen = Singleton<CommonUI>.Instance;
            if (!NativeRefGuard.IsProvablyUsable(commonUiForScreen, "CommonUI", out var screenDetail))
            {
                AbortPanelCreation("[MainMenuUI] the CommonUI instance is not provably usable (" + screenDetail + "); the online players panel is disabled");
                return;
            }

            var menuScreen = commonUiForScreen.MenuScreen;
            
            if (!IsAlive(menuScreen))
            {
                AbortPanelCreation("[MainMenuUI] MenuScreen disappeared before the panel could be attached; the online players panel is disabled");
                return;
            }

            _menuScreenTransform = menuScreen.gameObject.transform;
            Root("menu screen transform", _menuScreenTransform);

            // 挂载目标和 prefab 层级都逐个对象地遍历：
            // 上一版把 menuScreen.gameObject.transform 直接传给
            // SetParent，随后在未检查任何一个的情况下索引了两个子对象。
            var menuScreenTransform = menuScreen.gameObject.transform;
            
            if (!IsAlive(menuScreenTransform))
            {
                AbortPanelCreation("[MainMenuUI] the menu screen has no usable transform; the online players panel is disabled");
                return;
            }

            mainMenuUI.transform.SetParent(menuScreenTransform, false);

            var mainMenuUITransform = _mainMenuUI.gameObject.transform;
            
            _panelTransform = mainMenuUITransform;
            Root("panel transform", _panelTransform);
            var panelChildCount = IsAlive(mainMenuUITransform) ? mainMenuUITransform.childCount : -1;
            
            var contentParent = IsAlive(mainMenuUITransform) && mainMenuUITransform.childCount > 0
                ? mainMenuUITransform.GetChild(0)
                : null;
            if (!IsAlive(contentParent) || contentParent.childCount < 1)
            {
                AbortPanelCreation("[MainMenuUI] the panel prefab hierarchy no longer matches (expected two nested children); the online players panel is disabled");
                return;
            }

            var contentChild = contentParent.GetChild(0);
            var objectToAttach = IsAlive(contentChild) ? contentChild.gameObject : null;
            
            if (!IsAlive(objectToAttach))
            {
                AbortPanelCreation("[MainMenuUI] the panel content object is missing; the online players panel is disabled");
                return;
            }

            _userInterface = objectToAttach;
            Root("content", _userInterface);

            PanelStep("[MainMenuUI] 7/8 preparing the draggable content");
            _transformToScale = objectToAttach.RectTransform();
            
            if (!IsAlive(_transformToScale))
            {
                AbortPanelCreation("[MainMenuUI] the panel content has no RectTransform; the online players panel is disabled");
                return;
            }

            Root("scale target", _transformToScale);

            var scale = GetOnlinePlayersScale();
            _transformToScale.localScale = new(scale, scale, scale);

            var dragComponent = objectToAttach.AddComponent<UIDragComponent>();
            
            // 组件归引擎所有；它创建的实例的托管侧通过该字段
            // 保持可达（参见字段声明）。
            _dragComponent = dragComponent;
            Root("drag component", _dragComponent);
            if (IsAlive(dragComponent))
            {
                dragComponent.Init(_transformToScale, true);
            }
            else
            {
                FikaGlobals.LogWarning("[MainMenuUI] UIDragComponent could not be added; the panel stays fixed");
            }

            PanelStep("[MainMenuUI] 8/8 wiring the refresh button");
            var refreshButtonField = _mainMenuUI.RefreshButton;
            if (refreshButtonField is null)
            {
                AbortPanelCreation("[MainMenuUI] MainMenuUI.RefreshButton was not injected; the panel is read-only");
                return;
            }

            var refreshButton = refreshButtonField.Value;
            
            if (!IsAlive(refreshButton))
            {
                AbortPanelCreation("[MainMenuUI] MainMenuUI.RefreshButton is empty in the prefab; the panel cannot be refreshed manually");
                return;
            }

            // 引擎在 native UnityEvent 中持有该监听器：让 CLR 委托（因而
            // 其目标）保持为根，直到 DestroyPanel 再次移除它。
            _refreshButton = refreshButton;
            _refreshListener = ManualRefresh;
            Fika.Core.UnityEventCompatExtensions.AddListener(refreshButton.onClick, _refreshListener);
            Root("refresh button", _refreshButton);
            Root("refresh listener", _refreshListener);
            _panelCreated = true;
            PanelStep("[MainMenuUI] online players panel created");
            
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[MainMenuUI] the online players panel could not be created: {exception}");
        }
    }

    private void ManualRefresh()
    {
        if ((DateTime.Now - _lastRefresh).TotalSeconds < 5)
        {
            return;
        }

        _lastRefresh = DateTime.Now;
        FikaGlobals.LogInfo("[MainMenuUI] refresh: manual refresh requested");
        // [refresh] 这次点击与其他刷新并无不同：它不会排入第二个请求，
        // 周期从它重新开始计算，而不是稍后再触发一次。该按钮一贯的
        // 完整重建被保留——即使应答正是面板已经显示的内容，
        // 手动刷新也会重建这些行。
        _pendingRefreshSource = null;
        var delay = NextRefreshDelay(IsPanelVisible());
        _nextRefreshTime = Time.unscaledTime + delay;
        try
        {
            ClearAndQueryPlayers(0, "manual", delay, true);
        }
        catch (Exception exception)
        {
            ReportOnce(ref _refreshReported, $"[MainMenuUI] the player list could not be refreshed: {exception}");
        }
    }

    /// <summary>
    /// 一次刷新：向服务器查询在线名单并把它显示到屏幕上。
    ///
    /// 一次刷新恰好对应一个请求——<see cref="Pump"/> 每个周期触发一次，
    /// <see cref="ManualRefresh"/> 则是玩家自己的点击——而泵在请求发出前
    /// 就把计时器向前推进，因此较慢的应答无法启动第二个请求。
    ///
    /// 应答首先被归纳为一个名单标识（<see cref="RosterSignature"/>）。构建
    /// 这些行是刷新中开销较大的一半（2026-10-12 实测：行占用 579-704 ms，
    /// 计数写入占用 137-172 ms，而请求本身为 11-1614 ms，
    /// 见 analysis/online-players-refresh），因此只有当面板显示的名单与服务器刚返回的
    /// 名单不一致时才重建行。于是未发生变化的周期只花费一次
    /// HTTP GET 并保留现有行——不会为一份没有变动的列表重新注册任何人的 join 按钮或 tooltip——
    /// 而变化会在被观察到的那个 tick 就渲染出来，绝不延后
    /// 一个周期。
    /// </summary>
    private void ClearAndQueryPlayers(int tick, string source, float nextSeconds, bool rebuildAlways)
    {
        // 每个 tick 都做自我保护，与追踪开关无关：若面板的对象被引擎
        // 丢弃，就销毁该面板而不是继续使用它。
        if (!EnsurePanelUsable())
        {
            return;
        }

        var clock = Stopwatch.StartNew();

        _refreshTick = tick;
        
        if (tick <= 2)
        {
            PanelStep($"[MainMenuUI] refresh: requesting the online player presences (tick {tick}, frame {Time.frameCount})");
        }

        var response = FikaRequestHandler.GetPlayerPresences();
        if (response is null)
        {
            ReportOnce(ref _refreshReported, "[MainMenuUI] the server returned no player presence list; the panel keeps the previous content");
            
            // 应答到达之前不再改动这些行，因此这里实际发生的是“面板保留
            // 原有内容”。
            ReportRefresh(source, -1, clock.ElapsedMilliseconds, nextSeconds, "render=none note=no-response");
            return;
        }

        
        if (tick <= 2)
        {
            PanelStep($"[MainMenuUI] refresh: {response.Length} presence(s) received (tick {tick}, frame {Time.frameCount})");
        }
        ScheduleFasterRefreshWhenEmpty(response.Length);

        var signature = RosterSignature(response);
        var changed = signature != _lastRosterSignature;
        var rebuild = rebuildAlways || RebuildNeeded(changed, response.Length);
        // 发现变化的周期按其变化本身上报；而 "open" 和 "manual" 会保留在前面，
        // 因为它们说明最初是谁请求了这次刷新。
        var reportedSource = changed && source == "poll" ? "change" : source;

        if (rebuild)
        {
            _lastRosterSignature = signature;

            // 先移除 native 监听器：这些行即将被销毁，而仍持有指向已销毁行的委托的
            // native 事件，正是本次构建所追查的“引擎调用一个
            // 托管侧已消失的对象”状态。
            RemoveRowListeners();
            DestroyRows();
        }

        // 计数会在行被重建时（即原行为）写入，也会在它显示的数字与服务器刚返回的
        // 数字不一致时写入——比如一次失败的写入，或一份没有变化但
        // 从未被计数的名单。
        if (rebuild || _labelCount != response.Length)
        {
            _labelCount = UpdateLabel(response.Length) ? response.Length : -1;
            
        }

        if (rebuild)
        {
            SetupPlayers(ref response);
            
        }
        else
        {
            
        }

        if (tick <= 2)
        {
            PanelStep($"[MainMenuUI] refresh: the refresh funnel returned (tick {tick}, frame {Time.frameCount}); the coroutine is about to yield");
        }
        // 看门狗标记从此由 Pump 驱动；这仍是该漏斗的最后一行：
        // 即 *引擎* 仍为本面板持有什么。
        VerifyEngineView();

        ReportRefresh(reportedSource, response.Length, clock.ElapsedMilliseconds, nextSeconds,
            (rebuild ? "render=rows rows=" : "render=kept rows=") + (_players?.Count ?? -1));
    }

    /// <summary>
    /// 把在线玩家数量写入面板标签。当什么都没写入时返回 false
    /// （组件或标签不可用，或计数器被关闭），这样调用方就能
    /// 区分“计数器显示的就是这个数字”和“计数器状态未知”，并在下一轮
    /// 再次写入，而不是假设已经写过了。
    /// </summary>
    private bool UpdateLabel(int amount)
    {
        // 两道屏障：组件本身，然后是写入目标标签。该标签
        // 每个 tick 都会重新校验——它是一个序列化的 prefab 引用，也是面板在
        // prefab 实例化之后所执行的唯一一次 native 写入。
        if (!IsAlive(_mainMenuUI))
        {
            return false;
        }

        if (!(FikaPlugin.Instance.Settings.WriteOnlinePlayerCounter?.Value ?? true))
        {
            ReportOnce(ref _counterReported, "[MainMenuUI] the online player counter write is disabled in the config (Coop / 'Write Online Player Counter' = false)");
            return false;
        }

        // 标签包装器已缓存（创建时从 prefab 读取一次）：若在此处再次读取
        // 序列化的引用字段，就会每个 tick 多分配一个包装器。
        var label = _label;
        _labelUsable = NativeRefGuard.IsProvablyUsable(label, "TextMeshProUGUI", out _labelDetail);
        if (!_labelUsable)
        {
            ReportOnce(ref _labelReported, $"[MainMenuUI] the online player counter is not updated: MainMenuUI.Label is not provably usable ({_labelDetail})");
            
            return false;
        }

        
        var written = _mainMenuUI.UpdateLabel(label, amount);
        
        return written;
    }

    private string ConvertToTime(EDateTime dateTime, bool staticTime)
    {
        if (staticTime)
        {
            var staticDate = StaticTime;
            return dateTime == EDateTime.CURR ? staticDate.ToString("HH:mm:ss") : staticDate.AddHours(-12).ToString("HH:mm:ss");
        }

        var backendTime = BackendTime;
        if (backendTime == DateTime.MinValue)
        {
            return "ERROR";
        }
        return dateTime == EDateTime.CURR ? backendTime.ToString("HH:mm:ss") : backendTime.AddHours(-12).ToString("HH:mm:ss");
    }

    private bool IsStaticTimeLocation(string location)
    {
        return location is "factory4_day" or "factory4_night" or "laboratory";
    }

    private void SetupPlayers(ref FikaPlayerPresence[] responses)
    {
        if (responses is null || responses.Length == 0)
        {
            FikaGlobals.LogInfo("[MainMenuUI] refresh: no players online, the panel shows the counter only");
            
            return;
        }

        // 当 prefab 不携带玩家模板时，只用标签就够了。
        if (!IsAlive(_playerTemplate))
        {
            ReportOnce(ref _templateReported, $"[MainMenuUI] the player template is not a usable native object ({NativeRefGuard.Describe(_playerTemplate)}); the panel shows the counter only");
            return;
        }

        var playerParent = ResolveTemplateParent();
        var created = 0;
        var partial = 0;
        var skipped = 0;

        foreach (var presence in responses)
        {
            GameObject newPlayer;
            try
            {
                // 把旧的 prefab 父级交给 Instantiate，只有在它已被证明是
                // 真正的对象指针之后才安全。若父引用不是（转储中显示
                // 0x0A），引擎就会为新层级存下垃圾值并在之后解引用它，
                // 这正是 47888 的崩溃模式，因此损坏的父级会被丢弃，
                // 而不是被继续传递。
                newPlayer = playerParent is null
                    ? GameObject.Instantiate(_playerTemplate)
                    : GameObject.Instantiate(_playerTemplate, playerParent);
            }
            catch (Exception exception)
            {
                skipped++;
                ReportOnce(ref _rowReported, $"[MainMenuUI] a player row could not be instantiated: {exception}");
                continue;
            }

            if (!IsAlive(newPlayer))
            {
                skipped++;
                ReportOnce(ref _rowReported, "[MainMenuUI] a player row was created but is not a usable native object; it is not shown");
                continue;
            }

            var mainMenuUIPlayer = newPlayer.GetComponent<MainMenuUIPlayer>();
            if (!IsAlive(mainMenuUIPlayer))
            {
                skipped++;
                GameObject.Destroy(newPlayer);
                ReportOnce(ref _rowReported, "[MainMenuUI] a player row has no bound MainMenuUIPlayer component; it is not shown");
                continue;
            }

            // 该组件由引擎从 bundle 创建，因此它是一个 Fika 注入类的
            // 实例，自身没有 GC 句柄：若没有这个根，其托管侧可能在引擎
            // 仍持有该行时就被回收。
            _rowComponents.Add(mainMenuUIPlayer);
            Root("row component", mainMenuUIPlayer);

            if (!mainMenuUIPlayer.SetActivity(presence.Nickname, presence.Level, presence.Activity))
            {
                partial++;
                ReportOnce(ref _rowReported, "[MainMenuUI] a player row has unwired references; only the usable fields are filled in");
            }

            if (presence.Activity is EFikaPlayerPresence.IN_RAID && presence.RaidInformation.HasValue)
            {
                var information = presence.RaidInformation.Value;
#if DEBUG
                FikaGlobals.LogInfo($"Got presense: {information.Started}, {information.MatchId}");
#endif
                var side = information.Side == ESideType.Pmc ? "RaidSidePmc".Localized() : "RaidSideScav".Localized();
                var time = ConvertToTime(information.Time, IsStaticTimeLocation(information.Location));
                AttachTooltip(newPlayer, string.Format(LocaleUtils.UI_MMUI_RAID_DETAILS.Localized(), side,
                    ColorizeText(EColor.BLUE, information.Location.Localized()),
                    BoldText(time)));

                if (!information.Started)
                {
                    var joinButton = mainMenuUIPlayer.JoinButton is null ? null : mainMenuUIPlayer.JoinButton.Value;
                    if (!IsAlive(joinButton))
                    {
                        partial++;
                        ReportOnce(ref _rowReported, $"[MainMenuUI] the join button of a player row is not a usable native object ({NativeRefGuard.Describe(joinButton)}); joining from the panel is skipped");
                        newPlayer.SetActive(true);
                        _players.Add(newPlayer);
                        created++;
                        continue;
                    }

                    var buttonGo = joinButton.transform.parent.gameObject;
                    if (!IsAlive(buttonGo))
                    {
                        partial++;
                        ReportOnce(ref _rowReported, "[MainMenuUI] the join button has no usable parent object; joining from the panel is skipped");
                        newPlayer.SetActive(true);
                        _players.Add(newPlayer);
                        created++;
                        continue;
                    }

                    buttonGo.SetActive(true);
                    AttachTooltip(buttonGo, LocaleUtils.UI_JOIN_RAID.Localized());
                    System.Action joinListener = async () =>
                    {
                        if (JoinInProgress)
                        {
                            return;
                        }

                        JoinInProgress = true;

                        // 新的尝试从干净状态开始；上次尝试的失败会在此处（并写入
                        // 日志）上报，而不是在战局开始时用一个过期的原因中断这次尝试。
                        FikaBackendUtils.ClearLeftoverJoinFailure();

                        if (!TarkovApplication.Exist(out var tarkovApplication))
                        {
                            FikaGlobals.LogError("OnFikaStartRaid: Could not find TarkovApplication");
                            JoinInProgress = false;
                            return;
                        }

                        var session = tarkovApplication.Session;
                        if (session == null)
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                "Session was null when starting the raid");
                            FikaGlobals.LogError("Session was null when starting the raid");
                            JoinInProgress = false;
                            return;
                        }

                        FikaBackendUtils.Profile = session.Profile;

                        var location = session.LocationSettings.locations.Values.AsManaged()
                            .FirstOrDefault(l => l.Id == information.Location);

                        if (location == null)
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                $"Failed to find location {information.Location}");
                            FikaGlobals.LogError($"Failed to find location {information.Location}");
                            JoinInProgress = false;
                            return;
                        }
                        var mmc = tarkovApplication._menuOperation;

#if !DEBUG
                        var access = location.GetAccessRequirements(EGameMode.Pve, JsonType.LocationSettings.Location.EAccessUsageType.Menu);
                        if (access != null && access.Count > 0 && information.Side is ESideType.Pmc)
                        {
                            var playerLevel = session.Profile.Info.Level;
                            if (playerLevel >= location.MinPlayerLvlAccessKeys)
                            {
                                var playerItems = mmc.InventoryController.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                                if (!LocationAccessRequirementsUtility.HasAnyValidAccessRequirement(playerItems, access,
                                    (Il2CppSystem.Func<EFT.InventoryLogic.Item, bool>)(item => mmc.InventoryController.Examined(item))))
                                {
                                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.MISSING_KEY_FOR_LOCATION.Localized(),
                                        string.Join(", ", access.Keys.AsManaged().Select(key => $"{key} Name".Localized()))),
                                        iconType: EFT.Communications.ENotificationIconType.Alert);
                                    JoinInProgress = false;
                                    return;
                                }
                            }
                        }
#endif

                        FikaNotifications.DisplayMessageNotification(LocaleUtils.CONNECTING_TO_SESSION.Localized(),
                            iconType: EFT.Communications.ENotificationIconType.EntryPoint);

                        FikaBackendUtils.HostLocationId = information.Location;

                        using var pingingClient = await NetManagerUtils.CreatePingingClient();

                        if (pingingClient.Init(information.MatchId))
                        {
                            FikaGlobals.LogInfo("Attempting to connect to host session...");
                            var knockMessage = FikaBackendUtils.ServerGuid.ToString();
                            var success = await pingingClient.AttemptToPingHost(knockMessage);

                            if (!success)
                            {
                                Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                                LocaleUtils.UI_UNABLE_TO_CONNECT.Localized() + PingingFailureDetail(pingingClient),
                                ErrorScreen.EButtonType.OkButton, 10f);
                                JoinInProgress = false;
                                return;
                            }
                        }
                        else
                        {
                            // 返回结果中不含 host 记录的服务器属于主机那边的问题（他们的战局
                            // 从未被发布）；而传输失败则属于我们这边。
                            Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                                (pingingClient.HostAddressMissing
                                    ? LocaleUtils.UI_ERROR_CONNECTING_TO_RAID.Localized()
                                    : LocaleUtils.UI_UNABLE_TO_CONNECT.Localized()) + PingingFailureDetail(pingingClient),
                                ErrorScreen.EButtonType.OkButton, 10f);
                            JoinInProgress = false;
                            return;
                        }

                        if (FikaBackendUtils.JoinMatch(FikaBackendUtils.Profile.ProfileId, information.MatchId, out var result, out var errorMessage))
                        {
                            FikaBackendUtils.GroupId = result.ServerId;
                            FikaBackendUtils.ClientType = EClientType.Client;

                            AddPlayerRequest data = new(FikaBackendUtils.GroupId, FikaBackendUtils.Profile.ProfileId, FikaBackendUtils.IsSpectator);
                            FikaRequestHandler.UpdateAddPlayer(data);

                            // -1 表示保留世界时钟；此处若用默认结构体，
                            // 就意味着 0 点（午夜），并会强制每个加入的战局都
                            // 变成夜间。主机自己的选择会在片刻后从服务器
                            // 到达并覆盖此值。
                            var hostTimeAndWeather = new TimeAndWeatherSettings
                            {
                                HourOfDay = -1,
                                TimeFlowType = ETimeFlowType.x1
                            };

                            RaidSettings raidSettings = new()
                            {
                                Side = information.Side,
                                PlayersSpawnPlace = default,
                                MetabolismDisabled = default,
                                BotSettings = default,
                                WavesSettings = default,
                                TimeAndWeatherSettings = hostTimeAndWeather,
                                SelectedDateTime = information.Time,
                                SelectedLocation = location,
                                isInTransition = false,
                                RaidMode = ERaidMode.Local,
                                IsPveOffline = true,
                                OnlinePveRaid = false
                            };

                            FikaBackendUtils.IsScav = raidSettings.IsScav;

                            var controller = mmc.Matchmaker.MatchmakerPlayersController;

                            if (raidSettings.Side is ESideType.Savage && ScavBlocked(session, out var minutesLeft))
                            {
                                Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                    string.Format(LocaleUtils.UI_SCAV_NOT_READY.Localized(), minutesLeft.ToString("F0")));
                                JoinInProgress = false;
                                return;
                            }

                            tarkovApplication._raidSettings = raidSettings;
                            controller.MatchingProgress.SetStartTime(DateTimeExtensions.Now);

                            try
                            {
                                await tarkovApplication.LocalGameMatching(raidSettings.TimeAndWeatherSettings).AsManaged();
                            }
                            catch (Exception ex)
                            {
                                Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                    $"Exception caught during raid init: {ex.Message}", (System.Action)Application.Quit);
                                JoinInProgress = false;
                            }
                        }
                        else
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING", errorMessage, null);
                        }

                        JoinInProgress = false;
                    };

                    // 引擎在按钮的 native UnityEvent 中持有该监听器。让该
                    // CLR 委托保持为根并记住它，这样就能在该行被销毁之前
                    // 再次移除它。
                    _rowListeners.Add((joinButton, joinListener));
                    Root("row join listener", joinListener);
                    Fika.Core.UnityEventCompatExtensions.AddListener(joinButton.onClick, joinListener);
                }
            }
            newPlayer.SetActive(true);
            Root("row", newPlayer);
            _players.Add(newPlayer);
            created++;
        }

        FikaGlobals.LogInfo($"[MainMenuUI] refresh complete: {created} row(s) shown, {partial} partially wired, {skipped} skipped");
    }

    /// <summary>
    /// 复用 prefab 自身的父级，好让各行保持模板的布局，但该
    /// 引用来自 bundle：在 Instantiate 看到它之前会先做校验。
    /// </summary>
    private Transform ResolveTemplateParent()
    {
        try
        {
            var templateTransform = _playerTemplate.transform;
            if (!IsAlive(templateTransform))
            {
                ReportOnce(ref _templateReported, $"[MainMenuUI] the player template transform is not a usable native object ({NativeRefGuard.Describe(templateTransform)}); the rows are created without a parent");
                return null;
            }

            var parent = templateTransform.parent;
            if (!IsAlive(parent))
            {
                ReportOnce(ref _templateReported, "[MainMenuUI] the player template parent is not a usable native object; the rows are created without a parent");
                return null;
            }

            return parent;
        }
        catch (Exception exception)
        {
            ReportOnce(ref _templateReported, $"[MainMenuUI] the player template parent could not be read: {exception}");
            return null;
        }
    }

    /// <summary>
    /// tooltip 组件是在运行时添加的，因此它自身的引用按设计就是未连线的。
    /// 只有被确认是真正 native 对象的组件才会被配置和使用。
    /// </summary>
    private void AttachTooltip(GameObject target, string message)
    {
        if (!IsAlive(target))
        {
            return;
        }

        if (!_tooltipTraceReported)
        {
            _tooltipTraceReported = true;
            FikaGlobals.LogInfo("[MainMenuUI] player row: attaching a HoverTooltipArea");
        }

        var tooltip = target.AddComponent<HoverTooltipArea>();
        if (!IsAlive(tooltip))
        {
            ReportOnce(ref _rowReported, $"[MainMenuUI] a HoverTooltipArea could not be added ({NativeRefGuard.Describe(tooltip)}); the row is shown without its tooltip");
            return;
        }

        tooltip.enabled = true;
        tooltip.SetMessageText(message);
    }

    private bool ScavBlocked(IEftSession session, out double remainingMinutes)
    {
        var utcNowUnixInt = DateTimeExtensions.UtcNowUnixInt;
        var diffSeconds = session.ProfileOfPet.Info.SavageLockTime - (double)utcNowUnixInt;

        if (diffSeconds > 0.0d)
        {
            remainingMinutes = diffSeconds / 60.0d;
            return true;
        }

        remainingMinutes = 0.0d;
        return false;
    }

    public void UpdatePresence(EFikaPlayerPresence presence)
    {
        // 防止反复进出主菜单导致无谓的服务器卡顿
        if ((DateTime.Now - _lastSet).TotalSeconds < _minSecondsToWait)
        {
            return;
        }

        _lastSet = DateTime.Now;
        FikaSetPresence data = new(presence);
        FikaRequestHandler.SetPresence(data);
    }
}


