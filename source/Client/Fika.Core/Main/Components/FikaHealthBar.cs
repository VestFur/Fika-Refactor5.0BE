// © 2026 Lacyway All Rights Reserved

using EFT.Ballistics;
using EFT.CameraControl;
using System;
using System.Collections.Generic;
using Comfort.Common;
using DG.Tweening;
using EFT;
using EFT.HealthSystem;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using TMPro;
using UnityEngine.UI;
using Il2CppInterop.Runtime;

namespace Fika.Core.Main.Components;

/// <summary>
/// 在其他玩家上方显示生命条 <br/>
/// 创建者：ssh_
/// </summary>
public sealed class FikaHealthBar : MonoBehaviour
{
    public FikaHealthBar(System.IntPtr pointer) : base(pointer) { }


    public FikaHealthBar() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHealthBar>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    /// <summary>
    /// 检查 GClass 增量，可在 <see cref="StaticIcons.EffectSprites"/> 方法 <see cref="ISerializationCallbackReceiver.OnAfterDeserialize"/> 中检查 <br/><br/>
    /// <see cref="ActiveHealthController.Wound"/>、<see cref="ActiveHealthController.Encumbered"/>、<see cref="ActiveHealthController.OverEncumbered"/>， <br/>
    /// <see cref="ActiveHealthController.MildMusclePain"/>、<see cref="ActiveHealthController.SevereMusclePain"/>
    /// </summary>
    private static readonly Il2CppSystem.Type[] _ignoredTypes = [Il2CppType.Of<IWound>(), Il2CppType.Of<IEncumbered>(), Il2CppType.Of<IOverEncumbered>(), Il2CppType.Of<IMildMusclePain>(), Il2CppType.Of<ISevereMusclePain>()];

    private const float _tweenLength = 0.25f;

    private ObservedPlayer _currentPlayer;
    private Camera _camera;
    private FikaPlayer _mainPlayer;
    private PlayerPlateUI _playerPlate;
    private Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, Sprite> _effectIcons;
    private List<HealthBarEffect> _effects;
    private float _counter;
    private bool _updatePos = true;
    private RectTransform _canvasRect;
    private float _lastFinalAlpha = -1f;
    private RectTransform _plateRectTransform;
    private CanvasGroup _alphaGroup;
    private Transform _neckBone;
    private Dictionary<EBodyPart, GameObject> _bodyParts;
    private int _destroyedLimbs;
    private bool _showLimbs;
    private IHealthController _boundHealthController;
    private bool _healthEventsBound;
    private bool _effectEventsBound;
    // 原生生命值通知无法绑定时设置（其签名携带按值传递的
    // 结构体）。此时面板改由 Update 自行刷新，而不再依赖这些事件。
    private bool _pollHealth;
    private float _pollTimer;
    private System.Action<EBodyPart, float, DamageInfo> _healthChanged;
    private System.Action<EBodyPart, EDamageType> _bodyPartDestroyed;
    private System.Action<EBodyPart, ValueStruct> _bodyPartRestored;
    private System.Action<EDamageType> _died;
    private System.Action<IHealthEffect> _effectAdded;
    private System.Action<IHealthEffect> _effectRemoved;

    private bool _downed;
    private bool _updateDowned;
    private float _downedTimer;

    private static readonly int _checkLayers = LayerMask.GetMask(["HighPolyCollider", "Terrain", "Player"]);
    private static readonly int _playerLayer = LayerMask.NameToLayer("Player");

    public static FikaHealthBar Create(ObservedPlayer player)
    {
        if (player == null)
        {
            FikaGlobals.LogError("FikaHealthBar::Create: Player was null!");
            return null;
        }

        var healthBar = player.gameObject.AddComponent<FikaHealthBar>();
        healthBar._currentPlayer = player;
        healthBar._mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        healthBar._effectIcons = EFTHardSettings.Instance.StaticIcons.EffectIcons.EffectIcons;
        healthBar._effects = [];
        healthBar._neckBone = player.PlayerBones.Neck;

        healthBar.CreateHealthBar();
        return healthBar;
    }

    private void Update()
    {
        var deltaTime = Time.deltaTime;
        UpdateScreenSpacePosition();
        if (_pollHealth && _currentPlayer != null)
        {
            _pollTimer += deltaTime;
            if (_pollTimer >= 0.5f)
            {
                _pollTimer = 0f;
                UpdateHealth();
            }
        }
        if (_updateDowned && _downed)
        {
            UpdateDowned(deltaTime);
        }
        if (FikaPlugin.Instance.Settings.UseOcclusion.Value)
        {
            _counter += deltaTime;
            if (_counter > 1f)
            {
                _counter = 0f;
                CheckForOcclusion();
            }
        }
    }

    private void UpdateDowned(float deltaTime)
    {
        _downedTimer -= deltaTime;
        _playerPlate.downedStateTimerScreen.Value.SetFormattedText("{0:0.0}", _downedTimer);

        if (_downedTimer <= 0f)
        {
            _updateDowned = false;
            _playerPlate.downedStateBackgroundScreen.Value.gameObject.SetActive(false);
            _playerPlate.downedStateScreen.Value.gameObject.SetActive(false);
            _playerPlate.downedStateTimerScreen.Value.gameObject.SetActive(false);
        }
    }

    public void ToggleRevive(bool reviving, string nickname)
    {
        if (reviving)
        {
            _playerPlate.downedStateTimerScreen.Value.gameObject.SetActive(false);
            _playerPlate.downedStateScreen.Value.SetText(string.Format(LocaleUtils.UI_REVIVING_BEING_REVIVED_BY.Localized(), nickname));
            _updateDowned = false;
            return;
        }

        _playerPlate.downedStateTimerScreen.Value.gameObject.SetActive(true);
        _playerPlate.downedStateScreen.Value.SetText(LocaleUtils.UI_REVIVING_DOWNED.Localized());
        _updateDowned = true;
    }

    private void CheckForOcclusion()
    {
        var camPos = _camera.transform.position;
        var targetPos = _currentPlayer.PlayerBones.Neck.position;

        if (Physics.Raycast(camPos, targetPos - camPos, out var hitinfo, 800f, _checkLayers))
        {
            if (hitinfo.collider.gameObject.layer != _playerLayer)
            {
                _playerPlate.ScalarObjectScreen.Value.SetActive(false);
                _updatePos = false;
            }
            else
            {
                if (!_playerPlate.ScalarObjectScreen.Value.activeSelf)
                {
                    _playerPlate.ScalarObjectScreen.Value.SetActive(true);
                    _updatePos = true;
                    UpdateScreenSpacePosition();
                }
            }
        }
    }

    private void UpdateScreenSpacePosition()
    {
        if (!_updatePos)
        {
            return;
        }

        var settings = FikaPlugin.Instance.Settings;
        var proceduralAnimation = _mainPlayer.ProceduralWeaponAnimation;

        var opacityMultiplier = 1f;
        if (_mainPlayer.HealthController.IsAlive && proceduralAnimation.IsAiming)
        {
            if (proceduralAnimation.CurrentScope.IsOptic && settings.HideNamePlateInOptic.Value)
            {
                if (_playerPlate.ScalarObjectScreen.Value.activeSelf)
                {
                    _playerPlate.ScalarObjectScreen.Value.SetActive(false);
                }

                return;
            }
            opacityMultiplier = settings.OpacityInADS.Value;
        }

        var cameraPos = _camera.transform.position;
        var offset = _currentPlayer.Position - cameraPos;
        var sqrDist = offset.sqrMagnitude;
        var maxDist = settings.MaxDistanceToShow.Value;

        if (sqrDist > maxDist * maxDist)
        {
            if (_playerPlate.ScalarObjectScreen.Value.activeSelf)
            {
                _playerPlate.ScalarObjectScreen.Value.SetActive(false);
            }

            return;
        }

        if (!_playerPlate.ScalarObjectScreen.Value.activeSelf)
        {
            _playerPlate.ScalarObjectScreen.Value.SetActive(true);
        }

        var targetPosition = _neckBone.position + Vector3.up;
        if (!WorldToScreen.ProjectToCanvas(targetPosition, _mainPlayer,
            _canvasRect, out var canvasPos, settings.NamePlateUseOpticZoom.Value, false))
        {
            _alphaGroup.alpha = 0f;
            return;
        }

        _plateRectTransform.anchoredPosition = canvasPos;

        var distance = Mathf.Sqrt(sqrDist);
        var t = Mathf.InverseLerp(2f, maxDist, distance);

        var distFromCenterMult = 1f;
        if (settings.DecreaseOpacityNotLookingAt.Value)
        {
            var sqrDistFromCenter = canvasPos.sqrMagnitude;
            var maxSqrDist = Mathf.Pow(Mathf.Min(_canvasRect.sizeDelta.x, _canvasRect.sizeDelta.y) * 0.5f, 2f);
            distFromCenterMult = Mathf.Clamp01(1f - (sqrDistFromCenter / maxSqrDist));
        }

        var distanceAlpha = Mathf.Lerp(1f, 0.3f, t);

        var finalAlpha = Mathf.Max(settings.MinimumOpacity.Value, distanceAlpha * opacityMultiplier * distFromCenterMult);

        //var healthVisibleMultiplier = settings.HideHealthBar.Value ? 0f : 1f;
        //var healthAlpha = Mathf.Max(finalAlpha * healthVisibleMultiplier, 0.15f * healthVisibleMultiplier);

        if (!Mathf.Approximately(_lastFinalAlpha, finalAlpha))
        {
            _lastFinalAlpha = finalAlpha;
            _alphaGroup.alpha = finalAlpha;

            var scaleMultiplier = Mathf.Lerp(0.48f, 0.075f, t * Mathf.Sqrt(t)) * settings.NamePlateScale.Value;
            _plateRectTransform.localScale = new Vector3(scaleMultiplier, scaleMultiplier, 1f);

            foreach (var effect in _effects)
            {
                var showAmount = effect.Amount > 1;
                if (effect.TMPText.gameObject.activeSelf != showAmount)
                {
                    effect.TMPText.gameObject.SetActive(showAmount);
                }
            }
        }
    }

    private void CreateHealthBar()
    {
        var uiPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.PlayerUI);
        var uiGameObj = Instantiate(uiPrefab);
        _playerPlate = uiGameObj.GetComponent<PlayerPlateUI>();
        _alphaGroup = _playerPlate.AlphaGroup.Value;
        _plateRectTransform = _playerPlate.ScalarObjectScreen.Value.GetComponent<RectTransform>();
        // 面板是队友在屏幕上的身份标识。FikaGlobals.GetDisplayName 是唯一
        // 解析它的地方：当游戏／公告携带了人类玩家的主档案名时就用它，
        // 否则用昵称——这也是 AI 档案唯一拥有的名字，因为
        // 游戏对 bot 的 MainProfileNickname 留空。
        var shownName = _currentPlayer.Profile.GetDisplayName();

        _playerPlate.SetNameText(shownName);
        
        _camera = CameraManager.Instance.Camera;
        if (FikaPlugin.DevelopersList.ContainsKey(_currentPlayer.Profile.Nickname.ToLower()))
        {
            _playerPlate.playerNameScreen.Value.color = new Color(0, 6f, 1, 1);
            var specialIcons = Resources.Load<ChatSpecialIconSettings>("ChatSpecialIconSettings");
            _playerPlate.bearPlateScreen.Value.GetComponent<Image>().sprite = specialIcons.IconsSettings[1].IconSprite;
            _playerPlate.bearPlateScreen.Value.transform.localPosition = new Vector3(0f, 24.9f, 0);
            _playerPlate.usecPlateScreen.Value.GetComponent<Image>().sprite = specialIcons.IconsSettings[1].IconSprite;
            _playerPlate.usecPlateScreen.Value.transform.localPosition = new Vector3(0f, 24.9f, 0);
        }
        else if (FikaPlugin.RespectedPlayersList.ContainsKey(_currentPlayer.Profile.Nickname.ToLower()))
        {
            _playerPlate.playerNameScreen.Value.color = new Color(1, 0.6f, 0, 1);
            var specialIcons = Resources.Load<ChatSpecialIconSettings>("ChatSpecialIconSettings");
            _playerPlate.bearPlateScreen.Value.GetComponent<Image>().sprite = specialIcons.IconsSettings[2].IconSprite;
            _playerPlate.bearPlateScreen.Value.transform.localPosition = new Vector3(0f, 24.9f, 0);
            _playerPlate.usecPlateScreen.Value.GetComponent<Image>().sprite = specialIcons.IconsSettings[2].IconSprite;
            _playerPlate.usecPlateScreen.Value.transform.localPosition = new Vector3(0f, 24.9f, 0);
        }
        else
        {
            _playerPlate.playerNameScreen.Value.color = FikaPlugin.Instance.Settings.NamePlateTextColor.Value;
        }
        // 启动时两个面板都禁用，可见性将在更新循环中设置
        _playerPlate.usecPlateScreen.Value.gameObject.SetActive(false);
        _playerPlate.bearPlateScreen.Value.gameObject.SetActive(false);

        SetPlayerPlateFactionVisibility(FikaPlugin.Instance.Settings.UsePlateFactionSide.Value);
        SetPlayerPlateHealthVisibility(FikaPlugin.Instance.Settings.HideHealthBar.Value);

        FikaPlugin.Instance.Settings.UsePlateFactionSide.SettingChanged += UsePlateFactionSide_SettingChanged;
        FikaPlugin.Instance.Settings.HideHealthBar.SettingChanged += HideHealthBar_SettingChanged;
        FikaPlugin.Instance.Settings.UseNamePlates.SettingChanged += UseNamePlates_SettingChanged;
        FikaPlugin.Instance.Settings.UseHealthNumber.SettingChanged += UseHealthNumber_SettingChanged;
        FikaPlugin.Instance.Settings.ShowEffects.SettingChanged += ShowEffects_SettingChanged;
        FikaPlugin.Instance.Settings.ShowBrokenLimbs.SettingChanged += ShowBrokenLimbs_SettingChanged;

        ToggleHealthControllerEvents(!FikaPlugin.Instance.Settings.HideHealthBar.Value);

        _playerPlate.SetHealthNumberText(100);

        _canvasRect = _playerPlate.ScalarObjectScreen.Value.transform.parent.RectTransform();

        _showLimbs = FikaPlugin.Instance.Settings.ShowBrokenLimbs.Value;
        _bodyParts = new Dictionary<EBodyPart, GameObject>()
        {
            [EBodyPart.Common] = _playerPlate.Skeleton.Value,
            [EBodyPart.Head] = _playerPlate.Head.Value,
            [EBodyPart.LeftArm] = _playerPlate.LeftArm.Value,
            [EBodyPart.RightArm] = _playerPlate.RightArm.Value,
            [EBodyPart.LeftLeg] = _playerPlate.LeftLeg.Value,
            [EBodyPart.RightLeg] = _playerPlate.RightLeg.Value,
            [EBodyPart.Chest] = _playerPlate.Chest.Value,
            [EBodyPart.Stomach] = _playerPlate.Stomach.Value
        };
        _bodyParts[EBodyPart.Common].SetActive(false);

        if (!FikaPlugin.Instance.Settings.HideHealthBar.Value)
        {
            UpdateHealth();
        }
        ToggleNamePlate();
    }

    /// <summary>
    /// 绑定一个原生生命值通知。被拒绝的转换（DamageInfo 与 ValueStruct
    /// 按值传递）只会禁用该单个通知，而不会在创建名牌
    /// 期间抛异常，而创建名牌发生在战局开始时。
    /// </summary>
    private T BindHealthEvent<T>(string method) where T : System.Delegate
    {
        NativeDelegates.TryBindManaged<T>(this, method, "FikaHealthBar." + method, out var handler);
        return handler;
    }

    private void ToggleHealthControllerEvents(bool enabled)
    {
        _boundHealthController ??= _currentPlayer?.HealthController;
        if (_boundHealthController == null) return;
        if (_healthChanged == null)
        {
            _healthChanged = BindHealthEvent<System.Action<EBodyPart, float, DamageInfo>>(nameof(HealthController_HealthChangedEvent));
            _bodyPartDestroyed = BindHealthEvent<System.Action<EBodyPart, EDamageType>>(nameof(HealthController_BodyPartDestroyedEvent));
            _bodyPartRestored = BindHealthEvent<System.Action<EBodyPart, ValueStruct>>(nameof(HealthController_BodyPartRestoredEvent));
            _died = BindHealthEvent<System.Action<EDamageType>>(nameof(HealthController_DiedEvent));
            _effectAdded = BindHealthEvent<System.Action<IHealthEffect>>(nameof(HealthController_EffectAddedEvent));
            _effectRemoved = BindHealthEvent<System.Action<IHealthEffect>>(nameof(HealthController_EffectRemovedEvent));
        }

        if (enabled != _healthEventsBound)
        {
            if (enabled)
            {
                if (_healthChanged != null) _boundHealthController.add_HealthChangedEvent(_healthChanged);
                if (_bodyPartDestroyed != null) _boundHealthController.add_BodyPartDestroyedEvent(_bodyPartDestroyed);
                if (_bodyPartRestored != null) _boundHealthController.add_BodyPartRestoredEvent(_bodyPartRestored);
                if (_died != null) _boundHealthController.add_DiedEvent(_died);
            }
            else
            {
                if (_healthChanged != null) _boundHealthController.remove_HealthChangedEvent(_healthChanged);
                if (_bodyPartDestroyed != null) _boundHealthController.remove_BodyPartDestroyedEvent(_bodyPartDestroyed);
                if (_bodyPartRestored != null) _boundHealthController.remove_BodyPartRestoredEvent(_bodyPartRestored);
                if (_died != null) _boundHealthController.remove_DiedEvent(_died);
            }

            // 没有绑定任何必要项：通过拉取式路径保持面板最新。
            _pollHealth = _healthChanged == null || _died == null;
            _healthEventsBound = enabled;
        }
        ToggleEffectEvents(enabled && FikaPlugin.Instance.Settings.ShowEffects.Value);
    }

    private void ToggleEffectEvents(bool enabled)
    {
        if (enabled == _effectEventsBound || _boundHealthController == null) return;
        if (enabled)
        {
            if (_effectAdded != null) _boundHealthController.add_EffectAddedEvent(_effectAdded);
            if (_effectRemoved != null) _boundHealthController.add_EffectRemovedEvent(_effectRemoved);
            RemoveAllActiveEffects();
            AddAllActiveEffects();
        }
        else
        {
            _boundHealthController.remove_EffectAddedEvent(_effectAdded);
            _boundHealthController.remove_EffectRemovedEvent(_effectRemoved);
            RemoveAllActiveEffects();
        }
        _effectEventsBound = enabled;
    }

    private void ToggleNamePlate()
    {
        var useNamePlates = FikaPlugin.Instance.Settings.UseNamePlates.Value;
        _playerPlate.gameObject.SetActive(useNamePlates);
        enabled = useNamePlates;
    }

    #region events
    private void ShowBrokenLimbs_SettingChanged(object sender, EventArgs e)
    {
        _showLimbs = FikaPlugin.Instance.Settings.ShowBrokenLimbs.Value;
        RefreshLimbs(_showLimbs);
    }

    private void UseHealthNumber_SettingChanged(object sender, EventArgs e)
    {
        UpdateHealth();
    }

    private void HealthController_EffectRemovedEvent(IHealthEffect effect)
    {
        for (var i = 0; i < _effects.Count; i++)
        {
            var currentEffect = _effects[i];
            if (currentEffect.EffectType == effect.Type)
            {
                currentEffect.DecreaseAmount();
                if (currentEffect.Amount == 0)
                {
                    currentEffect.Remove();
                    _effects.Remove(currentEffect);
                }
                break;
            }
        }
    }

    private void HealthController_EffectAddedEvent(IHealthEffect effect)
    {
        AddEffect(effect);
    }

    private void ShowEffects_SettingChanged(object sender, EventArgs e)
    {
        ToggleEffectEvents(_healthEventsBound && FikaPlugin.Instance.Settings.ShowEffects.Value);
    }

    private void HealthController_DiedEvent(EDamageType obj)
    {
        Destroy(this);
    }

    private void HealthController_BodyPartRestoredEvent(EBodyPart bodyPart, ValueStruct arg2)
    {
        if (_showLimbs)
        {
            HandleLimb(bodyPart, false);
        }
        UpdateHealth();
    }

    private void HealthController_BodyPartDestroyedEvent(EBodyPart bodyPart, EDamageType arg2)
    {
        if (_showLimbs)
        {
            HandleLimb(bodyPart, true);
        }
        UpdateHealth();
    }

    private void HealthController_HealthChangedEvent(EBodyPart bodyPart, float arg2, DamageInfo arg3)
    {
        UpdateHealth();
    }

    private void UsePlateFactionSide_SettingChanged(object sender, EventArgs e)
    {
        SetPlayerPlateFactionVisibility(FikaPlugin.Instance.Settings.UsePlateFactionSide.Value);
    }

    private void HideHealthBar_SettingChanged(object sender, EventArgs e)
    {
        var value = FikaPlugin.Instance.Settings.HideHealthBar.Value;
        SetPlayerPlateHealthVisibility(value);
        ToggleHealthControllerEvents(!value);
    }

    private void UseNamePlates_SettingChanged(object sender, EventArgs e)
    {
        ToggleNamePlate();
    }
    #endregion

    private void AddEffect(IHealthEffect effect)
    {
        for (var i = 0; i < _ignoredTypes.Length; i++)
        {
            if (_ignoredTypes[i] == effect.Type)
            {
                return;
            }
        }

        var found = false;
        foreach (var currentEffect in _effects)
        {
            if (currentEffect.EffectType == effect.Type)
            {
                currentEffect.IncreaseAmount();
                found = true;
            }
        }

        if (found)
        {
            return;
        }

        if (_effectIcons.TryGetValue(effect.Type, out var effectSprite))
        {
            var newEffect = Instantiate(_playerPlate.EffectImageTemplate.Value, _playerPlate.EffectsBackground.Value.transform);
            HealthBarEffect healthBarEffect = new();
            healthBarEffect.Init(newEffect, effect, effectSprite);
            _effects.Add(healthBarEffect);
        }
    }

    private void RefreshLimbs(bool active)
    {
        if (!active)
        {
            foreach (var bodyPart in _bodyParts.Values)
            {
                bodyPart.SetActive(false);
            }
        }
        else
        {
            var shouldActivate = false;
            foreach (var item in _bodyParts.Keys)
            {
                if (item is EBodyPart.Common)
                {
                    continue;
                }

                var destroyed = _currentPlayer.HealthController.IsBodyPartDestroyed(item);
                if (destroyed)
                {
                    shouldActivate = true;
                }

                _bodyParts[item].SetActive(destroyed);
            }

            _bodyParts[EBodyPart.Common].SetActive(shouldActivate);
        }
    }

    private void HandleLimb(EBodyPart bodyPart, bool destroyed)
    {
        _destroyedLimbs += destroyed ? 1 : -1;

        _bodyParts[EBodyPart.Common].SetActive(_destroyedLimbs > 0);
        _bodyParts[bodyPart].SetActive(destroyed);
    }

    internal void AddAllActiveEffects()
    {
        foreach (var effect in _currentPlayer.HealthController.GetAllActiveEffects())
        {
            AddEffect(effect);
        }
    }

    internal void RemoveAllActiveEffects()
    {
        for (var i = _effects.Count - 1; i >= 0; i--)
        {
            _effects[i].Remove();
        }
        _effects.Clear();
    }

    /// <summary>
    /// 更新 HealthBar 上的生命值，由 healthcontroller 上的事件调用
    /// </summary>
    private void UpdateHealth()
    {
        var health = _currentPlayer.HealthController.GetBodyPartHealth(EBodyPart.Common, true);
        var currentHealth = health.Current;
        var maxHealth = health.Maximum;
        if (FikaPlugin.Instance.Settings.UseHealthNumber.Value)
        {
            if (!_playerPlate.healthNumberBackgroundScreen.Value.gameObject.activeSelf)
            {
                SetPlayerPlateHealthVisibility(false);
            }
            var healthNumberPercentage = (int)Math.Round(currentHealth / maxHealth * 100);
            _playerPlate.SetHealthNumberText(healthNumberPercentage);
        }
        else
        {
            if (!_playerPlate.healthBarBackgroundScreen.Value.gameObject.activeSelf)
            {
                SetPlayerPlateHealthVisibility(false);
            }

            var normalizedHealth = Mathf.Clamp01(currentHealth / maxHealth);
            _playerPlate.healthBarScreen.Value.DOFillAmount(normalizedHealth, _tweenLength);
            UpdateHealthBarColor(normalizedHealth);
        }
    }

    private void UpdateHealthBarColor(float normalizedHealth)
    {
        var color = Color.Lerp(FikaPlugin.Instance.Settings.LowHealthColor.Value,
            FikaPlugin.Instance.Settings.FullHealthColor.Value, normalizedHealth);
        color.a = _playerPlate.healthBarScreen.Value.color.a; // 保持 alpha 值不变
        _playerPlate.healthBarScreen.Value.color = color;
    }

    private void UpdateColorImage(Image screenObject, float alpha)
    {
        if (screenObject.gameObject.activeInHierarchy)
        {
            var color = screenObject.color;
            color.a = alpha;
            screenObject.color = color;
        }
    }

    private void UpdateColorTextMeshProUGUI(TextMeshProUGUI screenObject, float alpha)
    {
        if (screenObject.gameObject.activeInHierarchy)
        {
            var color = screenObject.color;
            color.a = alpha;
            screenObject.color = color;
        }
    }

    public void ToggleDowned(bool downed)
    {
        if (!FikaPlugin.Instance.Settings.HideHealthBar.Value)
        {
            SetPlayerPlateHealthVisibility(downed);
        }
        _playerPlate.downedStateBackgroundScreen.Value.gameObject.SetActive(downed);
        _playerPlate.downedStateScreen.Value.gameObject.SetActive(downed);
        _playerPlate.downedStateTimerScreen.Value.gameObject.SetActive(downed);

        if (downed)
        {
            _playerPlate.downedStateScreen.Value.SetText(LocaleUtils.UI_REVIVING_DOWNED.Localized()); // 强制显示倒地消息，以防玩家此前正处于被救起过程中
        }

        _downed = downed;
        var bleedoutTime = FikaPlugin.Instance.Settings.ReviveConfig.BleedoutTime;
        _updateDowned = !Mathf.Approximately(bleedoutTime, 0f);
        if (_updateDowned)
        {
            _downedTimer = bleedoutTime;
        }
    }

    private void SetPlayerPlateHealthVisibility(bool hidden)
    {
        _playerPlate.healthNumberScreen.Value.gameObject.SetActive(!hidden && FikaPlugin.Instance.Settings.UseHealthNumber.Value);
        _playerPlate.healthNumberBackgroundScreen.Value.gameObject.SetActive(!hidden && FikaPlugin.Instance.Settings.UseHealthNumber.Value);
        _playerPlate.healthBarScreen.Value.gameObject.SetActive(!hidden && !FikaPlugin.Instance.Settings.UseHealthNumber.Value);
        _playerPlate.healthBarBackgroundScreen.Value.gameObject.SetActive(!hidden && !FikaPlugin.Instance.Settings.UseHealthNumber.Value);
    }


    private void SetPlayerPlateFactionVisibility(bool visible)
    {
        if (_currentPlayer.Profile.Side == EPlayerSide.Usec)
        {
            _playerPlate.usecPlateScreen.Value.gameObject.SetActive(visible);
        }
        else if (_currentPlayer.Profile.Side == EPlayerSide.Bear)
        {
            _playerPlate.bearPlateScreen.Value.gameObject.SetActive(visible);
        }
    }

    private void OnDestroy()
    {
        FikaPlugin.Instance.Settings.UsePlateFactionSide.SettingChanged -= UsePlateFactionSide_SettingChanged;
        FikaPlugin.Instance.Settings.HideHealthBar.SettingChanged -= HideHealthBar_SettingChanged;
        FikaPlugin.Instance.Settings.UseNamePlates.SettingChanged -= UseNamePlates_SettingChanged;
        FikaPlugin.Instance.Settings.UseHealthNumber.SettingChanged -= UseHealthNumber_SettingChanged;
        FikaPlugin.Instance.Settings.ShowEffects.SettingChanged -= ShowEffects_SettingChanged;
        FikaPlugin.Instance.Settings.ShowBrokenLimbs.SettingChanged -= ShowBrokenLimbs_SettingChanged;
        ToggleHealthControllerEvents(false);
        if (_playerPlate != null) Destroy(_playerPlate.gameObject);
        _effects?.Clear();
    }

    private sealed class HealthBarEffect
    {
        public int Amount { get; private set; }

        public Il2CppSystem.Type EffectType;
        public Image EffectImage;
        public TextMeshProUGUI TMPText;

        private GameObject _effectObject;

        public void Init(GameObject initObject, IHealthEffect effect, Sprite effectSprite)
        {
            _effectObject = initObject;
            _effectObject.SetActive(true);
            EffectImage = _effectObject.transform.GetChild(0).GetComponent<Image>();
            EffectImage.sprite = effectSprite;
            TMPText = _effectObject.transform.GetChild(1).GetComponent<TextMeshProUGUI>();
            Amount = 1;
            TMPText.SetFormattedText("{0}", Amount);
            TMPText.enabled = false;
            EffectType = effect.Type;
        }

        public void Remove()
        {
            Destroy(EffectImage);
            Destroy(TMPText);
            Destroy(_effectObject);
        }

        public void IncreaseAmount()
        {
            Amount++;
            TMPText.SetFormattedText("{0}", Amount);

            if (Amount > 1)
            {
                TMPText.enabled = true;
            }
        }

        public void DecreaseAmount()
        {
            var newValue = Amount - 1;
            Amount = Math.Max(0, newValue);

            if (Amount == 0)
            {
                Remove();
                return;
            }

            if (Amount == 1)
            {
                TMPText.enabled = false;
            }

            TMPText.SetFormattedText("{0}", Amount);
        }
    }
}
