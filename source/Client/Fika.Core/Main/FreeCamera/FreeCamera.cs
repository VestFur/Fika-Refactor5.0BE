using System;
using System.Collections.Generic;
using BSG.CameraEffects;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.FreeCamera;

/// <summary>
/// <para>为 Fika 修改的 FreeCamera</para>
/// <para>
/// 基于 Terkoiz 的原始 freecam <br/>
/// <see href="https://github.com/acidphantasm/SPT-Freecam"/>
/// </para>
/// </summary>
public partial class FreeCamera : MonoBehaviour
{
    public FreeCamera(System.IntPtr pointer) : base(pointer) { }


    public FreeCamera() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FreeCamera>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public bool IsActive { get; set; }
    public bool Extracted { get; set; }

    private const float _lookSensitivity = 3f;
    private const float _minFov = 10f;

    private bool _isSpectator;
    private bool _allowSpectateBots;
    //private bool _refreshedAfterLastPlayerLeft;
    private FikaPlayer _currentPlayer;
    private int _lastSpectatedIndex = -1;
    private Vector3 _lastKnownPlayerPosition;
    private bool _isFollowing;
    private bool _leftMode;
    private bool _disableInput;
    private bool _showOverlay;
    private NightVision _nightVision;
    private ThermalVision _thermalVision;
    private FreeCameraController _freeCameraController;
    private float _yaw;
    private float _pitch;
    private float _originalFov;
    private bool _nightVisionActive;
    private bool _thermalVisionActive;
    private FreecamUI _freecamUI;
    private CoopHandler _coopHandler;
    private List<FikaPlayer> _players;

    private KeyCode _forwardKey;
    private KeyCode _backKey;
    private KeyCode _leftKey;
    private KeyCode _rightKey;
    private KeyCode _relUpKey;
    private KeyCode _relDownKey;
    private KeyCode _detachKey;
    private KeyCode _upKey;
    private KeyCode _downKey;

    protected void Awake()
    {
        _isSpectator = FikaBackendUtils.IsSpectator;
        _allowSpectateBots = FikaPlugin.Instance.Settings.AllowSpectateBots;
        _yaw = 0f;
        _pitch = 0f;
        _forwardKey = KeyCode.W;
        _backKey = KeyCode.S;
        _leftKey = KeyCode.A;
        _rightKey = KeyCode.D;
        _relUpKey = KeyCode.E;
        _relDownKey = KeyCode.Q;
        _detachKey = KeyCode.G;
        _upKey = KeyCode.R;
        _downKey = KeyCode.F;

        _playersTracker = [];
        _players = [];

        if (!CoopHandler.TryGetCoopHandler(out var coopHandler))
        {
            FikaGlobals.LogError("Could not find CoopHandler when creating FreeCamera");
            return;
        }

        _coopHandler = coopHandler;

        FikaPlayer.OnPlayerSpawned += OnPlayerSpawned;
        FikaPlayer.OnPlayerDestroyed += OnPlayerDestroyed;
        FikaPlayer.OnPlayerDeath += OnPlayerDeath;
    }

    protected void Start()
    {
        if (FikaPlugin.Instance.Settings.AZERTYMode.Value)
        {
            _forwardKey = KeyCode.Z;
            _backKey = KeyCode.S;
            _leftKey = KeyCode.Q;
            _rightKey = KeyCode.D;

            _relUpKey = KeyCode.E;
            _relDownKey = KeyCode.A;
        }

        _showOverlay = FikaPlugin.Instance.Settings.KeybindOverlay.Value;
        FikaPlugin.Instance.Settings.KeybindOverlay.SettingChanged += KeybindOverlay_SettingChanged;

        _nightVision = CameraManager.Instance.NightVision;
        _thermalVision = CameraManager.Instance.ThermalVision;

        _freeCameraController = Singleton<GameWorld>.Instance.gameObject.GetComponent<FreeCameraController>();
        _originalFov = CameraManager.Instance.Fov;

        var asset = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.FreecamUI);
        var freecamObject = Instantiate(asset);
        freecamObject.transform.SetParent(transform);
        _freecamUI = freecamObject.GetComponent<FreecamUI>();
        if (_freecamUI == null)
        {
            Destroy(freecamObject);
            throw new NullReferenceException("Could not assign FreecamUI");
        }

        _listPlayerPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.ListPlayer);
        if (_listPlayerPrefab == null)
        {
            Destroy(freecamObject);
            throw new NullReferenceException("Could not load ListPlayer prefab");
        }
        freecamObject.SetActive(false);
        _hidePlayerList = false;

        ForceAddPlayers();
    }

    /// <summary>
    /// 在撤离时强制重新计算
    /// </summary>
    public void RecalculateOnExtract()
    {
        ForceAddPlayers();
    }

    private void ForceAddPlayers()
    {
        foreach (var player in _coopHandler.Players.Values)
        {
            if (!player.IsYourPlayer && player.HealthController.IsAlive)
            {
                OnPlayerSpawned(player);
            }
        }
    }

    private void KeybindOverlay_SettingChanged(object sender, EventArgs e)
    {
        _showOverlay = FikaPlugin.Instance.Settings.KeybindOverlay.Value;
        if (IsActive)
        {
            _freecamUI.OverlayGroup.Value.SetActive(_showOverlay);
        }
    }

    public void SetCurrentPlayer(FikaPlayer player)
    {
        if (_currentPlayer != null && _playersTracker.TryGetValue(_currentPlayer.NetId, out var listPlayer))
        {
            listPlayer.ToggleBackground(false);
        }

        _currentPlayer = player;

        if (_currentPlayer == null)
        {
            _lastSpectatedIndex = -1;
            return;
        }

        _lastSpectatedIndex = _players.IndexOf(_currentPlayer);

        if (_currentPlayer != null && _playersTracker.TryGetValue(_currentPlayer.NetId, out listPlayer))
        {
            listPlayer.ToggleBackground(true);
        }
#if DEBUG
        FikaGlobals.LogInfo($"Freecam: Setting player to {_currentPlayer.Profile.GetCorrectedNickname()}");
#endif
    }

    public void DetachCamera()
    {
        if (_currentPlayer != null)
        {
            _lastSpectatingPlayer = _currentPlayer;
        }

        SetCurrentPlayer(null);
        if (_isFollowing)
        {
            _isFollowing = false;
            transform.parent = null;
        }
    }

    public void SwitchSpectateMode()
    {
        if (!_isFollowing)
        {
            if (CheckAndAssignPlayer())
            {
                JumpToPlayer();
            }

            return;
        }

        switch (_cameraState)
        {
            case ECameraState.FollowHeadcam:
                AttachToPlayer();
                break;
            case ECameraState.Follow3rdPerson:
                Attach3rdPerson();
                break;
        }
    }

    /// <summary>
    /// 用于循环切换观战玩家的辅助方法
    /// </summary>
    /// <param name="reverse">
    /// 若为 true,则按相反方向循环玩家
    /// </param>
    public void CycleSpectatePlayers(bool reverse = false)
    {
        var count = _players.Count;
        if (count == 0)
        {
            _currentPlayer = null;
            DetachCamera();
            return;
        }

        var step = reverse ? -1 : 1;
        var currentIndex = _players.IndexOf(_currentPlayer);

        if (currentIndex != -1)
        {
            _lastSpectatedIndex = currentIndex;
            var nextIndex = (currentIndex + step + count) % count;
            SetCurrentPlayer(_players[nextIndex]);
        }
        else
        {
            var fallbackIndex = _lastSpectatingPlayer != null ? _players.IndexOf(_lastSpectatingPlayer) : -1;

            if (fallbackIndex != -1)
            {
                _lastSpectatedIndex = fallbackIndex;
                var nextIndex = (fallbackIndex + step + count) % count;
                SetCurrentPlayer(_players[nextIndex]);
            }
            else
            {
                var baseIndex = Mathf.Clamp(_lastSpectatedIndex, 0, count - 1);

                int nextIndex;
                if (reverse)
                {
                    nextIndex = (baseIndex + step + count) % count;
                }
                else
                {
                    nextIndex = baseIndex % count;
                }

                SetCurrentPlayer(_players[nextIndex]);
            }
        }

        _lastSpectatedIndex = _players.IndexOf(_currentPlayer);
        SwitchSpectateMode();
    }

    /// <summary>
    /// 清空并重新计算所有存活玩家以供自由相机跟踪(人类与 AI)
    /// </summary>
    private void RecalculatePlayerList()
    {
        _players.Clear();

        var listTransform = _freecamUI.ListOfPlayers.Value.transform;
        for (var i = 0; i < listTransform.childCount; i++)
        {
            var listPlayer = listTransform.GetChild(i)
                .GetComponent<ListPlayer>();
            if (listPlayer != null && listPlayer.Player != null
                && listPlayer.Player.HealthController != null && listPlayer.Player.HealthController.IsAlive)
            {
                _players.Add(listPlayer.Player);
            }
        }
    }

    /// <summary>
    /// 清空并重新计算所有存活玩家以供自由相机跟踪(人类与 AI),并返回一个列表
    /// </summary>
    /// <returns>所有有效玩家的列表</returns>
    /// <remarks>零分配</remarks>
    public List<FikaPlayer> RecalculateAndGetPlayers()
    {
        RecalculatePlayerList();
        return _players;
    }

    protected void Update()
    {
        if (!IsActive)
        {
            return;
        }

        // 切换输入开关
        if (Input.GetKeyDown(KeyCode.Home))
        {
            _disableInput = !_disableInput;
            _freecamUI.InputText.Value.SetText($"HOME: {(_disableInput ? "Enable Input" : "Disable Input")}");
            FikaNotifications.DisplayMessageNotification(_disableInput ? LocaleUtils.FREECAM_DISABLED.Localized() : LocaleUtils.FREECAM_ENABLED.Localized());
        }

        if (_disableInput)
        {
            return;
        }

        if (MonoBehaviourSingleton<PreloaderUI>.Instance.Console.IsConsoleVisible)
        {
            return;
        }

        // 观战下一位玩家
        if (Input.GetKeyDown(KeyCode.Mouse0))
        {
            CycleSpectatePlayers(false);
            return;
        }

        // 观战上一位玩家
        if (Input.GetKeyDown(KeyCode.Mouse1))
        {
            CycleSpectatePlayers(true);
            return;
        }

        // 切换视野
        if (Input.GetKeyDown(KeyCode.N))
        {
            ToggleVision();
            return;
        }

        // 禁用剔除
        if (Input.GetKeyDown(KeyCode.M) && _freeCameraController != null)
        {
            _freeCameraController.DisableAllCullingObjects();
            return;
        }

        if (Input.GetKeyDown(KeyCode.L))
        {
            TogglePlayerList();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            switch (_cameraState)
            {
                case ECameraState.FollowHeadcam:
                    _cameraState = ECameraState.Follow3rdPerson;
                    break;
                case ECameraState.Follow3rdPerson:
                    _cameraState = ECameraState.FollowHeadcam;
                    break;
            }
            SwitchSpectateMode();
            return;
        }

        if (!_hidePlayerList)
        {
            UpdatePlayerList();
        }

        if (Input.GetKeyDown(_detachKey))
        {
            RecalculatePlayerList();

            if (_isFollowing && FikaPlugin.Instance.Settings.AllowSpectateFreeCam)
            {
                DetachCamera();
            }
            else
            {
                _isFollowing = true;
                SwitchSpectateMode();
            }
        }

        if (_isFollowing)
        {
            if (_currentPlayer != null)
            {
                _lastKnownPlayerPosition = _currentPlayer.PlayerBones.Neck.position;
                if (_currentPlayer.MovementContext.LeftStanceEnabled && !_leftMode)
                {
#if DEBUG
                    FikaGlobals.LogInfo("Setting left shoulder mode");
#endif
                    SetLeftShoulderMode(true);
                }
                else if (!_currentPlayer.MovementContext.LeftStanceEnabled && _leftMode)
                {
#if DEBUG
                    FikaGlobals.LogInfo("Unsetting left shoulder mode");
#endif
                    SetLeftShoulderMode(false);
                }
            }
            else
            {
#if DEBUG
                FikaGlobals.LogInfo("Freecam: currentPlayer vanished while we were following, finding next player to attach to");
#endif
                CycleSpectatePlayers();
                if (_currentPlayer == null)
                {
                    // 仍然没有玩家,那就前往地图
                    AttachToMap();
                }
            }
            return;
        }

        var fastMode = Input.GetKey(KeyCode.LeftShift);
        var movementSpeed = fastMode ? 20f : 2f;
        var deltaTime = Time.deltaTime;

        if (Input.GetKey(KeyCode.LeftControl))
        {
            movementSpeed *= 12;
        }

        if (Input.GetKey(_leftKey))
        {
            transform.position += -transform.right * (movementSpeed * deltaTime);
        }

        if (Input.GetKey(_rightKey))
        {
            transform.position += transform.right * (movementSpeed * deltaTime);
        }

        if (FikaPlugin.Instance.Settings.DroneMode.Value)
        {
            if (Input.GetKey(_forwardKey))
            {
                transform.position += GetNormalizedVector3(transform) * (movementSpeed * deltaTime);
            }

            if (Input.GetKey(_backKey))
            {
                transform.position += -GetNormalizedVector3(transform) * (movementSpeed * deltaTime);
            }
        }
        else
        {
            if (Input.GetKey(_forwardKey))
            {
                transform.position += transform.forward * (movementSpeed * deltaTime);
            }

            if (Input.GetKey(_backKey))
            {
                transform.position += -transform.forward * (movementSpeed * deltaTime);
            }
        }

        if (Input.GetKey(_relUpKey))
        {
            transform.position += transform.up * (movementSpeed * deltaTime);
        }

        if (Input.GetKey(_relDownKey))
        {
            transform.position += -transform.up * (movementSpeed * deltaTime);
        }

        if (Input.GetKey(_upKey))
        {
            transform.position += Vector3.up * (movementSpeed * deltaTime);
        }

        if (Input.GetKey(_downKey))
        {
            transform.position += -Vector3.up * (movementSpeed * deltaTime);
        }

        // 传送
        if (Input.GetKeyDown(KeyCode.T))
        {
            var player = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();

            if (player != null && !_coopHandler.ExtractedPlayers.Contains(player.NetId) && player.HealthController.IsAlive)
            {
                player.Teleport(transform.position);
            }
        }

        // 缩放
        const string mouseScrollAxis = "Mouse ScrollWheel";
        var scrollValue = Input.GetAxisRaw(mouseScrollAxis);
        if (scrollValue != 0)
        {
            var currentFov = CameraManager.Instance.Fov;
            if (currentFov >= _minFov && currentFov <= _originalFov)
            {
                var newFov = Mathf.Clamp(currentFov -= (scrollValue * 100), _minFov, _originalFov);
                CameraManager.Instance.SetFov(newFov, 1f);
            }
        }

        const string mouseAxisX = "Mouse X";
        const string mouseAxisY = "Mouse Y";
        var mouseX = Input.GetAxis(mouseAxisX);
        var mouseY = Input.GetAxis(mouseAxisY);

        // 先更新偏航角
        _yaw += mouseX * _lookSensitivity;

        // 更新并钳制俯仰角
        _pitch -= mouseY * _lookSensitivity;
        _pitch = Mathf.Clamp(_pitch, -89f, 89f);

        // 应用旋转
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    private void TogglePlayerList()
    {
        _hidePlayerList = !_hidePlayerList;
        _freecamUI.ListOfPlayers.Value.gameObject.SetActive(!_hidePlayerList);
    }

    private Vector3 GetNormalizedVector3(Transform transform)
    {
        var newForward = transform.forward;
        newForward.y = 0f;
        return newForward.normalized;
    }

    private void SetLeftShoulderMode(bool enabled)
    {
        if (enabled)
        {
            // 头戴相机使用不同的坐标
            if (transform.localPosition.z == -0.17f)
            {
                transform.localPosition = new(transform.localPosition.x, transform.localPosition.y, -transform.localPosition.z);
            }
            else
            {
                transform.localPosition = new(-transform.localPosition.x, transform.localPosition.y, transform.localPosition.z);
            }
            _leftMode = true;

            return;
        }

        // 头戴相机使用不同的坐标
        if (transform.localPosition.z == 0.17f)
        {
            transform.localPosition = new(transform.localPosition.x, transform.localPosition.y, -transform.localPosition.z);
        }
        else
        {
            transform.localPosition = new(-transform.localPosition.x, transform.localPosition.y, transform.localPosition.z);
        }
        _leftMode = false;
    }

    private void ToggleVision()
    {
        if (_nightVision != null && _thermalVision != null)
        {
            if (!_nightVision.On && !_thermalVision.On)
            {
                _nightVision.Switch(true);
                _freecamUI.VisionText.Value.SetText("N: Enable thermals");
            }
            else if (_nightVision.On && !_thermalVision.On)
            {
                _nightVision.Switch(false);
                _thermalVision.Switch(true);
                _freecamUI.VisionText.Value.SetText("N: Disable thermals");
            }
            else if (_thermalVision.On)
            {
                _thermalVision.Switch(false);
                _freecamUI.VisionText.Value.SetText("N: Enable nightvision");
            }
        }
    }

    public void JumpToPlayer()
    {
        SetCameraPosition(_currentPlayer);

        if (_isFollowing)
        {
            _isFollowing = false;
            _leftMode = false;
            transform.parent = null;
        }
    }

    public void AttachToPlayer()
    {
        if (!CheckAndAssignPlayer())
        {
            return;
        }

        CheckAndResetFov();
#if DEBUG
        FikaGlobals.LogInfo($"Freecam: Attaching to helmet cam current player {_currentPlayer.Profile.GetCorrectedNickname()}");
#endif
        transform.SetParent(_currentPlayer.PlayerBones.Head.Original);
        transform.localPosition = new Vector3(-0.1f, -0.07f, -0.17f);
        transform.localEulerAngles = new Vector3(260, 80, 0);
        _isFollowing = true;
        _cameraState = ECameraState.FollowHeadcam;
    }

    /// <summary>
    /// 检查是否存在活跃玩家,若不存在则指派最后观战的那一位
    /// </summary>
    /// <returns><see langword="true"/> 表示已指派了玩家</returns>
    private bool CheckAndAssignPlayer()
    {
        if (_currentPlayer == null && _lastSpectatingPlayer != null)
        {
            SetCurrentPlayer(_lastSpectatingPlayer);
        }

        return _currentPlayer != null;
    }

    public void AttachToMap()
    {
        if (_lastKnownPlayerPosition != default)
        {
#if DEBUG
            FikaGlobals.LogInfo($"Freecam: Attaching to last tracked player position {_lastKnownPlayerPosition}");
#endif
            transform.position = _lastKnownPlayerPosition;
        }
    }

    public void Attach3rdPerson()
    {
        if (!CheckAndAssignPlayer())
        {
            return;
        }

        CheckAndResetFov();
#if DEBUG
        FikaGlobals.LogInfo($"Freecam: Attaching to 3rd person current player {_currentPlayer.Profile.GetCorrectedNickname()}");
#endif
        transform.SetParent(_currentPlayer.SpectateTransform);
        transform.localPosition = new Vector3(0.3f, 0.2f, -0.65f);
        transform.localEulerAngles = new Vector3(4.3f, 5.9f, 0f);
        _isFollowing = true;
        _cameraState = ECameraState.Follow3rdPerson;
    }

    public void SetActive(bool active, bool extracted = false)
    {
        if (!active)
        {
            _freecamUI.gameObject.SetActive(false);
            if (_nightVision != null && _nightVision.On)
            {
                _nightVision.Switch(false);
            }

            if (_thermalVision != null && _thermalVision.On)
            {
                _thermalVision.Switch(false);
            }

            var player = Singleton<GameWorld>.Instance.MainPlayer;
            if (player != null && player.HealthController.IsAlive)
            {
                if (_nightVisionActive)
                {
                    player.NightVisionObserver.Component.Togglable.ForceToggle(true);
                }

                if (_thermalVisionActive)
                {
                    player.ThermalVisionObserver.Component.Togglable.ForceToggle(true);
                }
            }

            CheckAndResetFov();
        }

        if (active)
        {
            _freecamUI.gameObject.SetActive(true);
            if (_showOverlay)
            {
                _freecamUI.OverlayGroup.Value.SetActive(true);
                _freecamUI.VisionText.Value.SetText("N: Enable nightvision");
            }
            _nightVisionActive = false;
            _thermalVisionActive = false;

            var player = Singleton<GameWorld>.Instance.MainPlayer;
            if (player != null)
            {
                if (player.HealthController.IsAlive)
                {
                    if (!extracted)
                    {
                        SetCameraPosition(player);
                    }

                    if (player.NightVisionObserver.Component != null && player.NightVisionObserver.Component.Togglable.On)
                    {
                        player.NightVisionObserver.Component.Togglable.ForceToggle(false);
                        _nightVisionActive = true;
                    }

                    if (player.ThermalVisionObserver.Component != null && player.ThermalVisionObserver.Component.Togglable.On)
                    {
                        player.ThermalVisionObserver.Component.Togglable.ForceToggle(false);
                        _thermalVisionActive = true;
                    }
                }
                else
                {
                    if (_nightVision != null && _nightVision.On)
                    {
                        _nightVision.Switch(false);
                    }

                    if (_thermalVision != null && _thermalVision.On)
                    {
                        _thermalVision.Switch(false);
                    }

                    ForceAddPlayers();
                }
            }

            if (extracted)
            {
                ForceAddPlayers();
            }
        }

        IsActive = active;
        _isFollowing = false;
        _leftMode = false;
        transform.parent = null;
    }

    /// <summary>
    /// 设置相机相对于玩家的位置
    /// </summary>
    /// <param name="player">包含 transform 与骨骼引用的 <see cref="Player"/> 对象</param>
    private void SetCameraPosition(Player player)
    {
        // 相对玩家偏移相机
        transform.position = player.Transform.position - (player.Transform.forward * 1.5f) + (player.Transform.up * 2f);
        // 朝向头部
        transform.LookAt(player.PlayerBones.Head.Original.position, Vector3.up);

        // 从旋转中提取俯仰角与偏航角
        var euler = transform.eulerAngles;
        _pitch = NormalizeAngle(euler.x);
        _yaw = NormalizeAngle(euler.y);
    }

    /// <summary>
    /// 将角度归一化到 [-180, 180] 度区间
    /// </summary>
    /// <param name="angle">要归一化的角度(度)</param>
    /// <returns>位于 [-180, 180] 区间内的归一化角度</returns>
    private float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
        {
            angle -= 360f;
        }
        return angle;
    }

    private void CheckAndResetFov()
    {
        if (CameraManager.Instance.Fov != _originalFov)
        {
            CameraManager.Instance.SetFov(_originalFov, 0.1f);
        }
    }

    protected void OnDestroy()
    {
        FikaPlayer.OnPlayerSpawned -= OnPlayerSpawned;
        FikaPlayer.OnPlayerDestroyed -= OnPlayerDestroyed;
        FikaPlayer.OnPlayerDeath -= OnPlayerDeath;

        FikaPlugin.Instance.Settings.KeybindOverlay.SettingChanged -= KeybindOverlay_SettingChanged;

        _playersTracker.Clear();
        _players.Clear();

        Destroy(_freecamUI.gameObject);
        Destroy(this);
    }

    private enum ECameraState
    {
        Follow3rdPerson,
        FollowHeadcam
    };
}
