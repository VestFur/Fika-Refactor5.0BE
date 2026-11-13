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

    public void SetActivity(string nickname, int level, EFikaPlayerPresence presence)
    {
        PlayerName.Value.SetText(nickname);
        PlayerLevel.Value.SetFormattedText("({0})", level);
        var status = presence switch
        {
            EFikaPlayerPresence.IN_MENU => LocaleUtils.UI_MMUI_IN_MENU.Localized(),
            EFikaPlayerPresence.IN_RAID => LocaleUtils.UI_MMUI_IN_RAID.Localized(),
            EFikaPlayerPresence.IN_STASH => LocaleUtils.UI_MMUI_IN_STASH.Localized(),
            EFikaPlayerPresence.IN_HIDEOUT => LocaleUtils.UI_MMUI_IN_HIDEOUT.Localized(),
            EFikaPlayerPresence.IN_FLEA => LocaleUtils.UI_MMUI_IN_FLEA.Localized(),
            _ => LocaleUtils.UI_MMUI_IN_MENU.Localized(),
        };
        PlayerStatus.Value.SetText(status);
        SetImageColor(presence);
    }

    private void SetImageColor(EFikaPlayerPresence presence)
    {
        StatusImage.Value.color = presence switch
        {
            EFikaPlayerPresence.IN_MENU => Color.green,
            EFikaPlayerPresence.IN_RAID => Color.red + (Color.black * 0.3f),
            EFikaPlayerPresence.IN_STASH or EFikaPlayerPresence.IN_HIDEOUT or EFikaPlayerPresence.IN_FLEA => Color.blue + (Color.white * 0.3f),
            _ => Color.green,
        };
    }
}
