using EFT;
using Fika.Core.Main.Utils;
using TMPro;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

public class MainMenuUI : MonoBehaviour
{
    public MainMenuUI(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> RefreshButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> Label;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> PlayerTemplate;

    /// <summary>
    /// 引擎在它从 bundle 创建的组件上触发的回调。它们会写入稳定的
    /// 诊断文件，使引擎启动和销毁这些注入实例的顺序在运行结束后
    /// 仍然可用：本构建要追查的崩溃发生在引擎释放脚本对象的时候，
    /// 因此「引擎调用了 OnDestroy」成为最后一行，正是把那
    /// 对象识别为正在被释放的那一个的依据。
    /// </summary>
    private void Awake()
    {
        
    }

    private void OnDestroy()
    {
        
    }

    private bool _labelReported;

    /// <summary>
    /// 把在线玩家数写入 prefab 序列化出的那个标签。
    ///
    /// 这个重载接收调用方已经缓存并验证过的标签，因此面板不必在
    /// 每个刷新 tick 上重新读取序列化引用字段（并为其分配
    /// 一个包装器）。
    ///
    /// 写入被跳过时返回 false。
    /// </summary>
    public bool UpdateLabel(TextMeshProUGUI label, int amount)
    {
        if (!NativeRefGuard.IsProvablyUsable(label, "TextMeshProUGUI", out string detail))
        {
            if (!_labelReported)
            {
                _labelReported = true;
                FikaGlobals.LogWarning($"[MainMenuUI] the online player counter was not written: MainMenuUI.Label is not a provably usable native object ({detail})");
            }

            return false;
        }

        label.SetFormattedText(LocaleUtils.UI_MMUI_ONLINE_PLAYERS.Localized(), amount);
        return true;
    }

    /// <summary>
    /// 读取序列化的标签引用并向其中写入。
    ///
    /// 这是面板对它没有自己创建的对象所执行的唯一一次原生写入，
    /// 因此引用在使用之前会被证明：指针必须指向一个已提交的可读页，
    /// 对象必须携带可读的 il2cpp 类名，并且该类必须是（或派生自）
    /// TextMeshProUGUI。47888 的转储中出现过在 GameAssembly 内部对 0xA 的故障，
    /// 当时这样的写入被传入了一个根本不是对象的值，而一个仅仅
    /// *看起来*像指针的值并不够用——写入错误的对象
    /// 会破坏真正存在于那里的内容。
    ///
    /// 写入被跳过时返回 false，以便调用方记录裸指针。
    /// </summary>
    public bool UpdateLabel(int amount)
    {
        var label = Label is null ? null : Label.Value;
        return UpdateLabel(label, amount);
    }
}
