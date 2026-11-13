using EFT;
using Fika.Core.Main.Utils;
using TMPro;
using UnityEngine.UI;

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

    public void UpdateLabel(int amount)
    {
        Label.Value.SetFormattedText(LocaleUtils.UI_MMUI_ONLINE_PLAYERS.Localized(), amount);
    }
}
