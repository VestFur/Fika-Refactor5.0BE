using DG.Tweening;
using EFT;
using Fika.Core;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using TMPro;
using UnityEngine.UI;

public class ListPlayer : MonoBehaviour
{
    public ListPlayer(System.IntPtr pointer) : base(pointer) { }


    public ListPlayer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ListPlayer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

#pragma warning disable CS0649
    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> BackgroundBorder;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> Background;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> HPText;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> NameText;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> FactionText;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> HPBackground;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> NameBackground;

    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> FactionBackground;
#pragma warning restore CS0649

    public const float _tweenLength = 0.25f;

    public FikaPlayer Player { get; private set; }
    public int _lastHealth;

    /// <summary>
    /// 更新各项数值
    /// </summary>
    /// <returns>若该条目应被移除则为 <see langword="true"/>，否则为 <see langword="false"/></returns>
    public void ManualUpdate()
    {
        var health = Player.HealthController.GetBodyPartHealth(EBodyPart.Common, true);
        var current = Mathf.RoundToInt(health.Current);
        if (_lastHealth != current)
        {
            _lastHealth = current;
            UpdateHealth(current,
                Mathf.RoundToInt(health.Maximum));
        }
    }

    public void Init(FikaPlayer player)
    {
        NameText.Value.text = player.Profile.GetCorrectedNickname();
        NameText.Value.ForceMeshUpdate(true);
        LayoutRebuilder.ForceRebuildLayoutImmediate(NameBackground.Value.rectTransform);
        LayoutRebuilder.ForceRebuildLayoutImmediate(NameBackground.Value.rectTransform.parent.RectTransform());

        FactionText.Value.text = GetPlayerRole(player);
        FactionText.Value.ForceMeshUpdate(true);
        LayoutRebuilder.ForceRebuildLayoutImmediate(FactionBackground.Value.rectTransform);
        LayoutRebuilder.ForceRebuildLayoutImmediate(FactionBackground.Value.rectTransform.parent.RectTransform());

        Player = player;

        _lastHealth = -1;
        ManualUpdate();
        FactionBackground.Value.color = GetRoleColor(player);

        _lastHealth = Mathf.RoundToInt(player.HealthController.GetBodyPartHealth(EBodyPart.Common, true).Current);
    }

    public static string GetPlayerRole(FikaPlayer player)
    {
        if ((!FikaBackendUtils.IsServer && !player.IsObservedAI) || (FikaBackendUtils.IsServer && !player.IsAI))
        {
            return $"[P] ({player.Profile.Info.Side})";
        }

        var role = player.Profile.Info.Settings.Role;
        var playerRole = role switch
        {
            WildSpawnType.pmcUSEC => "USEC",
            WildSpawnType.pmcBEAR => "BEAR",
            WildSpawnType.assault or WildSpawnType.assaultGroup => "Scav",
            WildSpawnType.pmcBot => "Raider",
            WildSpawnType.exUsec => "Rogue",
            _ when role.IsBoss() => $"Boss ({role})",
            _ when role.IsFollower() => $"Follower ({role})",
            _ => player.Profile.Side.ToString()
        };

        if (role is WildSpawnType.assault or WildSpawnType.assaultGroup)
        {
            playerRole = role switch
            {
                WildSpawnType.marksman => "Sniper Scav",
                WildSpawnType.shooterBTR => "BTR Gunner",
                WildSpawnType.pmcUSEC => "AI USEC",
                WildSpawnType.pmcBEAR => "AI BEAR",
                _ => playerRole
            };
        }

        return playerRole;
    }

    public static Color GetRoleColor(FikaPlayer player)
    {
        if ((!FikaBackendUtils.IsServer && !player.IsObservedAI) || !player.IsAI)
        {
            switch (player.Profile.Info.Side)
            {
                case EPlayerSide.Usec:
                    return new Color(0.290f, 0.565f, 0.886f, 1f);
                case EPlayerSide.Bear:
                    return new Color(0.886f, 0.290f, 0.290f, 1f);
                case EPlayerSide.Savage:
                    return new Color(0.886f, 0.722f, 0.290f, 1f);
            }
        }

        var role = player.Profile.Info.Settings.Role;
        var isPmc = role.IsPmcBot();

        if (!isPmc && role.IsBoss())
        {
            return new Color(0.80f, 0.60f, 0.80f);
        }

        if (!isPmc && role.IsFollower())
        {
            return new Color(0.85f, 0.70f, 0.55f);
        }

        switch (role)
        {
            case WildSpawnType.pmcUSEC:
                return new Color(0.290f, 0.565f, 0.886f, 1f);
            case WildSpawnType.pmcBEAR:
                return new Color(0.886f, 0.290f, 0.290f, 1f);
            case WildSpawnType.assault:
            case WildSpawnType.assaultGroup:
                return new Color(0.886f, 0.722f, 0.290f, 1f);
            case WildSpawnType.pmcBot:
                return new Color(0.75f, 0.65f, 0.45f);
            case WildSpawnType.exUsec:
                return new Color(0.65f, 0.55f, 0.75f);
            case WildSpawnType.marksman:
                return new Color(0.60f, 0.75f, 0.85f);
            case WildSpawnType.shooterBTR:
                return new Color(0.80f, 0.70f, 0.50f);
            default:
                return new Color(0.55f, 0.55f, 0.55f);
        }
    }

    public void ToggleBackground(bool enabled)
    {
        Background.Value.color = enabled ? Color.yellow : Color.clear;
        BackgroundBorder.Value.color = enabled ? Color.white : Color.clear;
    }

    public void UpdateHealth(int currentHealth, int maxHealth)
    {
        HPText.Value.SetFormattedText("{0}/{1}",
            currentHealth, maxHealth);

        var normalizedHealth = Mathf.Clamp01((float)currentHealth / maxHealth);
        HPBackground.Value.DOFillAmount(normalizedHealth, _tweenLength);
        HPBackground.Value.color = Color.Lerp(FikaPlugin.Instance.Settings.LowHealthColor.Value,
            FikaPlugin.Instance.Settings.FullHealthColor.Value, normalizedHealth);
    }
}




