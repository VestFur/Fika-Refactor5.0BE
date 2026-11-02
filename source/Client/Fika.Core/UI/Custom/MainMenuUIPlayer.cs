using Fika.Core.Main.Utils;
using TMPro;
using EFT;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

public class MainMenuUIPlayer : MonoBehaviour
{
    public MainMenuUIPlayer(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUIPlayer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUIPlayer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerName;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerLevel;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerStatus;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> StatusImage;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> JoinButton;

    /// <summary>
    /// 由引擎在我们从资源包创建的组件上触发的回调。它们被写入
    /// 稳定的诊断文件，好让引擎启动和销毁这些注入实例的顺序
    /// 在整次运行之后仍然留存：此构建追查的崩溃发生在
    /// 引擎释放某个脚本对象的过程中，因此“引擎调用了 OnDestroy”成为最后一行，
    /// 正是把它认定为正在被释放对象的依据。
    /// </summary>
    private void Awake()
    {
        
    }

    private void OnDestroy()
    {
        
    }

    private bool _wiringReported;

    /// <summary>
    /// 填充一行名册数据。每个字段都来自包内的玩家模板，所以
    /// 每个字段在写入之前都会先被验证：可读的页面、可读的 il2cpp 类
    /// 名，以及预期的基类。否则，一个持有根本不是对象的东西
    /// （dump 显示 0x0A）的字段，或者一个类型错误的对象，都会把这
    /// 一行变成原生访问违规，或者破坏一个无关对象。
    ///
    /// 当至少有一个字段不得不被跳过时返回 false，这样调用方就能统计
    /// 只被部分接线的行。
    /// </summary>
    public bool SetActivity(string nickname, int level, EFikaPlayerPresence presence)
    {
        var complete = true;

        var playerName = PlayerName is null ? null : PlayerName.Value;
        if (NativeRefGuard.IsProvablyUsable(playerName, "TextMeshProUGUI", out _))
        {
            playerName.SetText(nickname);
        }
        else
        {
            complete = false;
        }

        

        var playerLevel = PlayerLevel is null ? null : PlayerLevel.Value;
        
        if (NativeRefGuard.IsProvablyUsable(playerLevel, "TextMeshProUGUI", out _))
        {
            playerLevel.SetFormattedText("({0})", level);
        }
        else
        {
            complete = false;
        }

        

        var status = presence switch
        {
            EFikaPlayerPresence.IN_MENU => LocaleUtils.UI_MMUI_IN_MENU.Localized(),
            EFikaPlayerPresence.IN_RAID => LocaleUtils.UI_MMUI_IN_RAID.Localized(),
            EFikaPlayerPresence.IN_STASH => LocaleUtils.UI_MMUI_IN_STASH.Localized(),
            EFikaPlayerPresence.IN_HIDEOUT => LocaleUtils.UI_MMUI_IN_HIDEOUT.Localized(),
            EFikaPlayerPresence.IN_FLEA => LocaleUtils.UI_MMUI_IN_FLEA.Localized(),
            _ => LocaleUtils.UI_MMUI_IN_MENU.Localized(),
        };

        var playerStatus = PlayerStatus is null ? null : PlayerStatus.Value;
        
        if (NativeRefGuard.IsProvablyUsable(playerStatus, "TextMeshProUGUI", out _))
        {
            playerStatus.SetText(status);
        }
        else
        {
            complete = false;
        }

        

        if (!SetImageColor(presence))
        {
            complete = false;
        }

        var joinButton = JoinButton is null ? null : JoinButton.Value;
        

        if (!complete && !_wiringReported)
        {
            _wiringReported = true;
            FikaGlobals.LogWarning("[MainMenuUIPlayer] at least one field of the player template is not a provably usable native object; the remaining fields are still filled in");
        }

        return complete;
    }

    private bool SetImageColor(EFikaPlayerPresence presence)
    {
        var statusImage = StatusImage is null ? null : StatusImage.Value;
        
        if (!NativeRefGuard.IsProvablyUsable(statusImage, "Image", out _))
        {
            return false;
        }

        statusImage.color = presence switch
        {
            EFikaPlayerPresence.IN_MENU => Color.green,
            EFikaPlayerPresence.IN_RAID => Color.red + (Color.black * 0.3f),
            EFikaPlayerPresence.IN_STASH or EFikaPlayerPresence.IN_HIDEOUT or EFikaPlayerPresence.IN_FLEA => Color.blue + (Color.white * 0.3f),
            _ => Color.green,
        };
        return true;
    }
}
