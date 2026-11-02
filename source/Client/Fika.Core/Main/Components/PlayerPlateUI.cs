// © 2026 Lacyway All Rights Reserved

using TMPro;
using UnityEngine.UI;

/// <summary>
/// 玩家名牌的控制器。 <br/>
/// 创建者：ssh_
/// </summary>
public class PlayerPlateUI : MonoBehaviour
{
    public PlayerPlateUI(System.IntPtr pointer) : base(pointer) { }


    public PlayerPlateUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<PlayerPlateUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> ScalarObjectScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> playerNameScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> healthBarBackgroundScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> healthBarScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> healthNumberBackgroundScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> healthNumberScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> downedStateBackgroundScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> downedStateScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> downedStateTimerScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> usecPlateScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> bearPlateScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> EffectsBackground;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> EffectImageTemplate;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<CanvasGroup> AlphaGroup;

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> Skeleton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> Head;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> LeftArm;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> RightArm;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> LeftLeg;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> RightLeg;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> Chest;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> Stomach;

    public void SetNameText(string text)
    {
        playerNameScreen.Value.SetText(text);
    }

    public void SetHealthNumberText(int amount)
    {
        healthNumberScreen.Value.SetFormattedText("{0}%", amount);
    }
}
