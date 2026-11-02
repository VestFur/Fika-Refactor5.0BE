using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Settings;
using EFT.UI;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.UI;
using HarmonyLib;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using TMPro;

namespace Fika.Core.Main.FreeCamera;

/// <summary>
/// 本实现极大程度上基于 Terkoiz 在此处的工作。感谢你的工作，Terkoiz！<br/>
/// <see href="https://dev.sp-tarkov.com/Terkoiz/Freecam/raw/branch/master/project/Terkoiz.Freecam/FreecamController.cs"/>
/// </summary>

public class FreeCameraController : MonoBehaviour
{
    public FreeCameraController(System.IntPtr pointer) : base(pointer) { }


    public FreeCameraController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FreeCameraController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private FikaPlayer Player
    {
        get
        {
            return Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
        }
    }

    public bool IsScriptActive
    {
        get
        {
            if (_freeCamScript != null)
            {
                return _freeCamScript.IsActive;
            }
            return false;
        }
    }

    public Camera CameraMain { get; private set; }

    private EftBattleUIScreen BattleUI
    {
        get
        {
            if (_playerUI == null)
            {
                var gameObject = GameObject.Find("BattleUIScreen");
                if (gameObject == null)
                {
                    return null;
                }

                _playerUI = gameObject.GetComponent<EftBattleUIScreen>();
                if (_playerUI == null)
                {
                    return null;
                }
            }

            return _playerUI;
        }
        set
        {
            _playerUI = value;
        }
    }

    private EftBattleUIScreen _playerUI;
    private GameObject _cameraParent;
    private bool _isSpectator;
    private FreeCamera _freeCamScript;
    private bool _uiHidden;
    private bool _effectsCleared;
    private GamePlayerOwner _gamePlayerOwner;
    private Vector3 _lastKnownPosition;
    private CoopHandler _coopHandler;
    private TextMeshProUGUI _extractText;
    private bool _extracted;
    private DeathFade _deathFade;
    private bool _deathFadeEnabled;
    private DisablerCullingObjectBase[] _allCullingObjects;
    private List<PerfectCullingBakeGroup> _previouslyActiveBakeGroups;
    private bool _hasEnabledCulling;
    private System.Action<EDamageType> _diedHandler;

    protected void Awake()
    {
        _cameraParent = new GameObject("CameraParent");
        var FCamera = _cameraParent.GetOrAddComponent<Camera>();
        FCamera.enabled = false;
        _isSpectator = FikaBackendUtils.IsSpectator;
    }

    protected void Start()
    {
        // 查找主相机
        CameraMain = CameraManager.Instance.Camera;
        if (CameraMain == null)
        {
            return;
        }

        // 将 Freecam 脚本添加到场景中的主相机
        _freeCamScript = CameraMain.gameObject.AddComponent<FreeCamera>();
        if (_freeCamScript == null)
        {
            return;
        }

        // 获取 GamePlayerOwner 组件
        _gamePlayerOwner = GetLocalPlayerFromWorld().GetComponentInChildren<GamePlayerOwner>();
        if (_gamePlayerOwner == null)
        {
            return;
        }

        _deathFade = CameraManager.Instance.Camera.GetComponent<DeathFade>();
        // DeathFade 是一个相机后处理效果。它必须在
        // 正常 raid 启动期间保持禁用；在此处启用它会让倒计时
        // 在 Fika 跳过 LocalGame 的原生部署流程之后仍让第一人称视角
        // 保持模糊。DeathMainPlayer_DiedEvent 只在玩家真正死亡时
        // 才启用它。
        if (_deathFade != null)
        {
            _deathFade.DisableEffectInstant();
            _deathFade.enabled = false;
        }

        _allCullingObjects = FindObjectsOfType<DisablerCullingObjectBase>();
        _previouslyActiveBakeGroups = [];

        _diedHandler = (System.Action<EDamageType>)MainPlayer_DiedEvent;
        Player.ActiveHealthController.DiedEvent += _diedHandler;

        if (CoopHandler.TryGetCoopHandler(out var cHandler))
        {
            _coopHandler = cHandler;
        }

        CoopGame.MainPlayerExtracted += MainPlayerExtracted;
    }

    private void MainPlayerExtracted(FikaPlayer player)
    {
        if (_extracted)
        {
            return;
        }

#if DEBUG
        FikaGlobals.LogInfo("Freecam: player has extracted");
#endif
        var fikaGame = _coopHandler.LocalGameInstance;
        if (fikaGame.ExtractedPlayers.Contains(Player.NetId))
        {
            Player.ActiveHealthController.DiedEvent -= _diedHandler;
            _extracted = true;
            _freeCamScript.Extracted = true;
            ShowExtractMessage();
        }

        if (!_freeCamScript.IsActive)
        {
            _freeCamScript.RecalculateOnExtract();
            ToggleUI();
            if (FikaPlugin.Instance.Settings.AllowSpectateFreeCam || _isSpectator)
            {
                _freeCamScript.transform.position = _lastKnownPosition;
                ToggleCamera();
            }
            else
            {
                ToggleSpectateCamera();
            }
        }

        if (!_effectsCleared)
        {
            if (Player != null)
            {
                Player.Muffled = false;
                Player.HeavyBreath = false;
            }

            if (CameraManager.Exist)
            {
                ClearEffects();
            }
            _effectsCleared = true;
        }
    }

    private void MainPlayer_DiedEvent(EDamageType obj)
    {
        Player.ActiveHealthController.DiedEvent -= _diedHandler;

        if (!_deathFadeEnabled)
        {
            _deathFade.EnableEffect();
            _deathFadeEnabled = true;
        }

        Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, DeathRoutine());
    }

    protected void Update()
    {
        if (_gamePlayerOwner == null)
        {
            return;
        }

        if (Player == null)
        {
            return;
        }

        if (Player.ActiveHealthController == null)
        {
            return;
        }

        var quitState = _coopHandler.QuitState;
        if (quitState != CoopHandler.EQuitState.Extracted)
        {
            _lastKnownPosition = Player.PlayerBones.Neck.position;
        }

        if (_extracted && !_freeCamScript.IsActive)
        {
            ToggleUI();
            if (FikaPlugin.Instance.Settings.AllowSpectateFreeCam || _isSpectator)
            {
                ToggleCamera();
            }
            else
            {
                ToggleSpectateCamera();
            }
        }

        if (FikaPlugin.Instance.Settings.FreeCamButton.Value.IsDown())
        {
            if (!FikaPlugin.Instance.Settings.AllowFreeCam)
            {
                return;
            }

            if (quitState == CoopHandler.EQuitState.None)
            {
                ToggleCamera();
                ToggleUI();
                return;
            }
        }

        if (quitState == CoopHandler.EQuitState.Extracted && !_extracted)
        {

        }
    }

    private IEnumerator DeathRoutine()
    {
        if (!_isSpectator)
        {
            yield return new WaitSeconds(5f);
        }

        var cameraClassInstance = CameraManager.Instance;
        if (cameraClassInstance == null)
        {
            yield break;
        }

        if (cameraClassInstance.EffectsController == null)
        {
            yield break;
        }

        if (cameraClassInstance.Camera != null)
        {
            cameraClassInstance.Camera.fieldOfView = Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView;
        }

        // 禁用 DeathFade 效果并切换相机
        _deathFade.DisableEffect();
        if (!_freeCamScript.IsActive)
        {
            ToggleUI();
            if (FikaPlugin.Instance.Settings.AllowSpectateFreeCam || _isSpectator)
            {
                ToggleCamera();

                if (_isSpectator)
                {
                    // 把相机循环切换到任意存活玩家
                    _freeCamScript.CycleSpectatePlayers();
                }
            }
            else
            {
                ToggleSpectateCamera();
            }
        }
        ShowExtractMessage();

        if (!_effectsCleared)
        {
            if (Player != null)
            {
                Player.Muffled = false;
                Player.HeavyBreath = false;
            }

            if (CameraManager.Exist)
            {
                ClearEffects();
            }
            _effectsCleared = true;
        }
    }

    private void ClearEffects()
    {
        var cameraClass = CameraManager.Instance;

        cameraClass.EffectsController.OnGlassesChanged(null, false);

        var bloodOnScreen = cameraClass.EffectsController._bloodOnScreen;
        if (bloodOnScreen != null)
        {
            Destroy(bloodOnScreen);
        }

        var effectsManagerList = cameraClass.EffectsController._effectAccumulators;
        if (effectsManagerList != null)
        {
            for (var i = 0; i < effectsManagerList.Count; i++)
            {
                var effectsManager = effectsManagerList[i];
                while (effectsManager.ActiveEffects.Count > 0)
                {
                    var effect = effectsManager.ActiveEffects[0];
                    effectsManager.DeleteEffect(effect);
                }
            }
            effectsManagerList.Clear();
        }

        var wiggleEffect = cameraClass.Camera.gameObject.GetComponent<CC_Wiggle>();
        if (wiggleEffect != null)
        {
            wiggleEffect.enabled = false;
        }

        var blendEffects = cameraClass.Camera.gameObject.GetComponents<CC_Blend>();
        if (blendEffects.Length > 0)
        {
            foreach (var blendEffect in blendEffects)
            {
                blendEffect.enabled = false;
            }
        }

        Destroy(cameraClass.EffectsController);
        cameraClass.VisorEffect.Clear();
        Destroy(cameraClass.VisorEffect);
        cameraClass.VisorSwitcher.Deinit();
        Destroy(cameraClass.VisorSwitcher);
        if (cameraClass.NightVision.On)
        {
            cameraClass.NightVision.Switch(false);
        }
        if (cameraClass.ThermalVision.On)
        {
            cameraClass.ThermalVision.Switch(false);
        }
    }

    private void ShowExtractMessage()
    {
        if (FikaPlugin.Instance.Settings.ShowExtractMessage.Value)
        {
            var text = FikaPlugin.Instance.Settings.ExtractKey.Value.MainKey.ToString();
            if (FikaPlugin.Instance.Settings.ExtractKey.Value.Modifiers.Any())
            {
                var modifiers = string.Join(" + ", FikaPlugin.Instance.Settings.ExtractKey.Value.Modifiers);
                text = modifiers + " + " + text;
            }
            _extractText = FikaUIGlobals.CreateOverlayText(string.Format(LocaleUtils.UI_EXTRACT_MESSAGE.Localized(), $"'{text}'"));
        }
    }

    /// <summary>
    /// 切换 Freecam 模式
    /// </summary>
    public void ToggleCamera()
    {
        // 获取我们自己的 Player 实例。为 null 表示我们不在 raid 中
        if (Player == null)
        {
            return;
        }

        if (!_freeCamScript.IsActive)
        {
            SetPlayerToFreecamMode(Player);
        }
        else
        {
            SetPlayerToFirstPersonMode(Player);
        }
    }

    /// <summary>
    /// 在 raid 部署后恢复正常的本地玩家相机。它被
    /// SPT 5 的 Fika 倒计时路径使用，该路径不经过
    /// 原版 LocalGame 的相机拆除流程，可能使自由相机所有者
    /// 即使在玩家存活时也处于禁用状态。
    /// </summary>
    public void ForceFirstPersonView()
    {
        if (Player == null)
        {
            return;
        }

        if (_freeCamScript != null && _freeCamScript.IsActive)
        {
            SetPlayerToFirstPersonMode(Player);
            return;
        }

        if (_gamePlayerOwner != null)
        {
            _gamePlayerOwner.enabled = true;
        }

        if (_freeCamScript != null)
        {
            _freeCamScript.IsActive = false;
            _freeCamScript.transform.SetParent(null);
        }

        SetExternalCameraControl(Player, false);
        Player.PointOfView = EPointOfView.FirstPerson;
        if (Player.PlayerBody != null)
        {
            Player.PlayerBody.PointOfView.Value = EPointOfView.FirstPerson;
        }
        Player.GetComponent<PlayerCameraController>()?.UpdatePointOfView();
    }

    public void ToggleSpectateCamera()
    {
        if (Player == null)
        {
            return;
        }

        if (!_freeCamScript.IsActive)
        {
            var aliveTargets = _freeCamScript.RecalculateAndGetPlayers();

            if (aliveTargets.Count == 0)
            {
#if DEBUG
                FikaGlobals.LogInfo("FreecamController: No players found");
#endif
                _freeCamScript.transform.position = _lastKnownPosition;
                ToggleCamera();
                return;
            }

            var fikaPlayer = aliveTargets[0];
            _freeCamScript.SetCurrentPlayer(fikaPlayer);
#if DEBUG
            FikaGlobals.LogInfo("FreecamController: Spectating new target: " + fikaPlayer.Profile.Info.MainProfileNickname);
#endif

            Player.PointOfView = EPointOfView.ThirdPerson;
            if (Player.PlayerBody != null)
            {
                Player.PlayerBody.PointOfView.Value = EPointOfView.FreeCamera;
                Player.GetComponent<PlayerCameraController>().UpdatePointOfView();
            }
            SetExternalCameraControl(Player, true);
            _gamePlayerOwner.enabled = false;
            _freeCamScript.SetActive(true, _extracted);

            _freeCamScript.Attach3rdPerson();
        }
    }

    /// <summary>
    /// 隐藏主界面（生命、耐力、姿态、快捷栏等）
    /// </summary>
    private void ToggleUI()
    {
        // 检查我们当前是否在 raid 中
        if (Player == null)
        {
            return;
        }

        if (BattleUI == null || BattleUI.gameObject == null)
        {
            return;
        }

        BattleUI.gameObject.SetActive(_uiHidden);
        _uiHidden = !_uiHidden;
    }

    /// <summary>
    /// 一个把 Player 置于 Freecam 模式的辅助方法
    /// </summary>
    /// <param name="localPlayer"></param>
    private void SetPlayerToFreecamMode(Player localPlayer)
    {
        // 我们把玩家设置为第三人称模式
        // 这意味着我们的角色将完全可见，同时让相机自由移动
        localPlayer.PointOfView = EPointOfView.ThirdPerson;

        if (localPlayer.PlayerBody != null)
        {
            localPlayer.PlayerBody.PointOfView.Value = EPointOfView.FreeCamera;
            localPlayer.GetComponent<PlayerCameraController>().UpdatePointOfView();
        }

        SetExternalCameraControl(localPlayer, true);
        _gamePlayerOwner.enabled = false;
        _freeCamScript.SetActive(true, _extracted);
    }

    // 否则 BetterCamController 会在每次 LateUpdate 时把相机吸附到玩家身上。
    private static void SetExternalCameraControl(Player localPlayer, bool external)
    {
        var controller = localPlayer.GetComponent<PlayerCameraController>();
        if (controller != null)
        {
            controller.ExternalControl = external;
        }
    }

    /// <summary>
    /// 一个把玩家视图重置回第一人称的辅助方法
    /// </summary>
    /// <param name="localPlayer"></param>
    private void SetPlayerToFirstPersonMode(Player localPlayer)
    {
        // 重新启用 _gamePlayerOwner
        _gamePlayerOwner.enabled = true;
        _freeCamScript.SetActive(false, _extracted);
        SetExternalCameraControl(localPlayer, false);

        localPlayer.PointOfView = EPointOfView.FirstPerson;
        CameraManager.Instance.SetOcclusionCullingEnabled(new Il2CppSystem.Nullable<bool>(true));

        if (_hasEnabledCulling)
        {
            EnableAllCullingObjects();
        }
    }

    public void DisableAllCullingObjects()
    {
        var count = 0;
        foreach (var cullingObject in _allCullingObjects)
        {
            if (cullingObject.HasEntered)
            {
                continue;
            }
            count++;
            cullingObject.SetComponentsEnabled(true);
        }
#if DEBUG
        FikaGlobals.LogWarning($"Enabled {count} Culling Triggers.");
#endif

        var perfectCullingAdaptiveGrid = FindObjectOfType<PerfectCullingAdaptiveGrid>();
        if (perfectCullingAdaptiveGrid != null)
        {
            if (perfectCullingAdaptiveGrid.RuntimeGroupMapping.Count > 0)
            {
                foreach (var sceneGroup in perfectCullingAdaptiveGrid.RuntimeGroupMapping)
                {
                    foreach (var bakeGroup in sceneGroup.bakeGroups)
                    {
                        if (!bakeGroup.IsEnabled)
                        {
                            bakeGroup.IsEnabled = true;
                            continue;
                        }

                        _previouslyActiveBakeGroups.Add(bakeGroup);
                    }

                    sceneGroup.enabled = false;
                }
            }
        }

        _hasEnabledCulling = true;
    }

    public void EnableAllCullingObjects()
    {
        var count = 0;
        foreach (var cullingObject in _allCullingObjects)
        {
            if (cullingObject.HasEntered)
            {
                continue;
            }
            count++;
            cullingObject.SetComponentsEnabled(false);
        }
#if DEBUG
        FikaGlobals.LogWarning($"Disabled {count} Culling Triggers.");
#endif

        var perfectCullingAdaptiveGrid = FindObjectOfType<PerfectCullingAdaptiveGrid>();
        if (perfectCullingAdaptiveGrid != null)
        {
            if (perfectCullingAdaptiveGrid.RuntimeGroupMapping.Count > 0)
            {
                foreach (var sceneGroup in perfectCullingAdaptiveGrid.RuntimeGroupMapping)
                {
                    sceneGroup.enabled = true;

                    foreach (var bakeGroup in sceneGroup.bakeGroups)
                    {
                        if (bakeGroup.IsEnabled && !_previouslyActiveBakeGroups.Contains(bakeGroup))
                        {
                            bakeGroup.IsEnabled = false;
                            continue;
                        }

                        _previouslyActiveBakeGroups.Remove(bakeGroup);
                    }

                    _previouslyActiveBakeGroups.Clear();
                }
            }
        }

        _hasEnabledCulling = false;
    }

    /// <summary>
    /// 若可用，获取当前的 <see cref="EFT.Player"/> 实例
    /// </summary>
    /// <returns>本地 <see cref="EFT.Player"/> 实例；若游戏不在 raid 中则返回 null</returns>
    private Player GetLocalPlayerFromWorld()
    {
        // 如果 GameWorld 实例为 null 或没有 RegisteredPlayers，最可能意味着我们不在 raid 中
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null || gameWorld.MainPlayer == null)
        {
            return null;
        }

        // RegisteredPlayers 中的一个会带有 IsYourPlayer 标志，那就是我们自己的 Player 实例
        return gameWorld.MainPlayer;
    }

    public void OnDestroy()
    {
        CoopGame.MainPlayerExtracted -= MainPlayerExtracted;
        if (!FikaSingleton<FreeCameraController>.TryRelease(this))
        {
            FikaGlobals.LogWarning("Unable to release FreeCameraController singleton");
        }
        Destroy(_cameraParent);

        // 如果存在，先销毁 FreeCamScript 再销毁 FreeCamController
        Destroy(_freeCamScript);
        if (_extractText != null)
        {
            Destroy(_extractText);
        }
        Destroy(this);
    }
}


