using EFT.CameraControl;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.UI;
using TMPro;
using UnityEngine.UI;
using Object = System.Object;

namespace Fika.Core.Main.Factories;

public static class PingFactory
{
    public enum EPingType : byte
    {
        Point,
        Player,
        DeadBody,
        LootItem,
        LootContainer,
        Door,
        Interactable
    }

    public static void ReceivePing(Vector3 location, EPingType pingType, Color pingColor, string nickname, string localeId)
    {
        var prefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.Ping);
        var pingGameObject = UnityEngine.Object.Instantiate(prefab);
        var abstractPing = FromPingType(pingType, pingGameObject);
        if (abstractPing != null)
        {
            abstractPing.Initialize(ref location, null, pingColor);
            Singleton<GUISounds>.Instance.PlayUISound(GetPingSound());
            if (string.IsNullOrEmpty(localeId))
            {
                FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.RECEIVE_PING.Localized(), FikaUIGlobals.ColorizeText(FikaUIGlobals.EColor.GREEN, nickname)),
                            ENotificationDurationType.Default, ENotificationIconType.Friend);
            }
            else
            {
                var localizedName = localeId.Localized();
                FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.RECEIVE_PING_OBJECT.Localized(),
                    [FikaUIGlobals.ColorizeText(FikaUIGlobals.EColor.GREEN, nickname), FikaUIGlobals.ColorizeText(FikaUIGlobals.EColor.BLUE, localizedName)]),
                    ENotificationDurationType.Default, ENotificationIconType.Friend);
            }
        }
        else
        {
            FikaGlobals.LogError($"Received {pingType} from {nickname} but factory failed to handle it");
        }
    }

    public static EUISoundType GetPingSound()
    {
        return FikaPlugin.Instance.Settings.PingSound.Value switch
        {
            FikaPlugin.EPingSound.InsuranceInsured => EUISoundType.InsuranceInsured,
            FikaPlugin.EPingSound.SubQuestComplete => EUISoundType.QuestSubTrackComplete,
            FikaPlugin.EPingSound.ButtonClick => EUISoundType.ButtonClick,
            FikaPlugin.EPingSound.ButtonHover => EUISoundType.ButtonOver,
            FikaPlugin.EPingSound.InsuranceItemInsured => EUISoundType.InsuranceItemOnInsure,
            FikaPlugin.EPingSound.MenuButtonBottom => EUISoundType.ButtonBottomBarClick,
            FikaPlugin.EPingSound.ErrorMessage => EUISoundType.ErrorMessage,
            FikaPlugin.EPingSound.InspectWindow => EUISoundType.MenuInspectorWindowOpen,
            FikaPlugin.EPingSound.InspectWindowClose => EUISoundType.MenuInspectorWindowClose,
            FikaPlugin.EPingSound.MenuEscape => EUISoundType.MenuEscape,
            _ => EUISoundType.QuestSubTrackComplete,
        };
    }

    public static AbstractPing FromPingType(EPingType type, GameObject gameObject)
    {
        return type switch
        {
            EPingType.Point => gameObject.AddComponent<PointPing>(),
            EPingType.Player => gameObject.AddComponent<PlayerPing>(),
            EPingType.DeadBody => gameObject.AddComponent<DeadBodyPing>(),
            EPingType.LootItem => gameObject.AddComponent<LootItemPing>(),
            EPingType.LootContainer => gameObject.AddComponent<LootContainerPing>(),
            EPingType.Door => gameObject.AddComponent<DoorPing>(),
            EPingType.Interactable => gameObject.AddComponent<InteractablePing>(),
            _ => null
        };
    }

    public abstract class AbstractPing : MonoBehaviour
    {
        public AbstractPing(System.IntPtr pointer) : base(pointer) { }


        //internal static readonly AssetBundle pingBundle;
        private static readonly System.Lazy<Dictionary<InternalBundleLoader.EFikaSprite, Sprite>> SpriteCache =
            new(() => InternalBundleLoader.Instance.GetFikaSprites());
        internal static Dictionary<InternalBundleLoader.EFikaSprite, Sprite> sprites => SpriteCache.Value;

        protected Image _image;
        protected Vector3 _hitPoint;
        private RectTransform _canvasRect;
        private TextMeshProUGUI _rangeText;
        private bool _displayRange;
        private Color _pingColor = Color.white;
        private FikaPlayer _mainPlayer;

        protected void Awake()
        {
            _image = GetComponentInChildren<Image>();
            _image.color = Color.clear;
            _mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>();
            _canvasRect = GetComponentInChildren<Canvas>().GetComponent<RectTransform>();
            _rangeText = GetComponentInChildren<TextMeshProUGUI>(true);
            _rangeText.color = Color.clear;
            _displayRange = FikaPlugin.Instance.Settings.ShowPingRange.Value;
            _rangeText.gameObject.SetActive(_displayRange);
            if (_mainPlayer == null)
            {
                Destroy(gameObject);
                FikaGlobals.LogError("Ping::Awake: Could not find MainPlayer!");
            }
            Destroy(gameObject, FikaPlugin.Instance.Settings.PingTime.Value);
        }

        protected void Update()
        {
            if (_mainPlayer.HealthController.IsAlive && _mainPlayer.ProceduralWeaponAnimation.IsAiming
                && _mainPlayer.ProceduralWeaponAnimation.CurrentScope.IsOptic && !FikaPlugin.Instance.Settings.ShowPingDuringOptics.Value)
            {
                _image.color = Color.clear;
                if (_displayRange)
                {
                    _rangeText.color = Color.clear;
                }

                return;
            }

            const float edgePadding = 20f;
            var cam = CameraManager.Instance.Camera;
            var targetPos = _hitPoint;

            // 从摄像机指向目标的向量
            var toTarget = targetPos - cam.transform.position;
            var behindCamera = Vector3.Dot(cam.transform.forward, toTarget.normalized) <= 0f;

            // 位于前方时按正常方式投影
            Vector2 canvasPos;
            if (!behindCamera)
            {
                WorldToScreen.ProjectToCanvas(targetPos, _mainPlayer, _canvasRect, out canvasPos,
                    FikaPlugin.Instance.Settings.PingUseOpticZoom.Value, true);
            }
            else
            {
                // 位于后方时在摄像机平面内计算方向
                var local = cam.transform.InverseTransformPoint(targetPos);
                var dir = new Vector2(local.x, local.y); // X=水平，Y=垂直
                if (dir.sqrMagnitude < 0.001f)
                {
                    dir = Vector2.up; // 兜底，避免零向量
                }

                dir.Normalize();

                // 钳制到画布边缘
                var halfWidth = (_canvasRect.sizeDelta.x / 2f) - edgePadding;
                var halfHeight = (_canvasRect.sizeDelta.y / 2f) - edgePadding;

                // 缩放使标记贴住边缘
                var scaleX = halfWidth / Mathf.Abs(dir.x);
                var scaleY = halfHeight / Mathf.Abs(dir.y);
                var scale = Mathf.Min(scaleX, scaleY);

                canvasPos = dir * scale;
            }

            // 基于距离的透明度
            var distanceToCenter = canvasPos.magnitude;
            var alpha = distanceToCenter < 200f
                ? Mathf.Max(FikaPlugin.Instance.Settings.PingMinimumOpacity.Value, distanceToCenter / 200f)
                : 1f;

            _image.color = new Color(_pingColor.r, _pingColor.g, _pingColor.b, alpha);
            if (_displayRange)
            {
                _rangeText.color = Color.white.SetAlpha(alpha);
            }

            // 钳制到画布边缘（安全兜底）
            var halfW = (_canvasRect.sizeDelta.x / 2f) - edgePadding;
            var halfH = (_canvasRect.sizeDelta.y / 2f) - edgePadding;
            canvasPos.x = Mathf.Clamp(canvasPos.x, -halfW, halfW);
            canvasPos.y = Mathf.Clamp(canvasPos.y, -halfH, halfH);

            _image.rectTransform.anchoredPosition = canvasPos;

            if (_displayRange)
            {
                _rangeText.SetFormattedText("[{0:0.0}m]", CameraManager.Instance.Distance(_hitPoint));
            }
        }

        public virtual void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            _hitPoint = point;
            transform.position = point;
            _pingColor = pingColor;

            var distance = Mathf.Clamp(Vector3.Distance(CameraManager.Instance.Camera.transform.position, transform.position) / 100, 0.4f, 0.6f);
            var pingSize = FikaPlugin.Instance.Settings.PingSize.Value;
            Vector3 scaledSize = new(pingSize, pingSize, pingSize);
            if (FikaPlugin.Instance.Settings.PingScaleWithDistance.Value)
            {
                scaledSize *= distance;
            }
            else
            {
                scaledSize *= 0.5f;
            }
            _image.rectTransform.localScale = scaledSize;
        }
    }

    public class InteractablePing : AbstractPing
    {
        public InteractablePing(System.IntPtr pointer) : base(pointer) { }


        public InteractablePing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<InteractablePing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, pingColor);
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingPoint];
        }
    }

    public class PlayerPing : AbstractPing
    {
        public PlayerPing(System.IntPtr pointer) : base(pointer) { }


        public PlayerPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<PlayerPing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, pingColor);
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingPlayer];
        }
    }

    public class LootContainerPing : AbstractPing
    {
        public LootContainerPing(System.IntPtr pointer) : base(pointer) { }


        public LootContainerPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<LootContainerPing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, pingColor);
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingLootableContainer];
        }
    }

    public class DoorPing : AbstractPing
    {
        public DoorPing(System.IntPtr pointer) : base(pointer) { }


        public DoorPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<DoorPing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, pingColor);
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingDoor];
        }
    }

    public class PointPing : AbstractPing
    {
        public PointPing(System.IntPtr pointer) : base(pointer) { }


        public PointPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<PointPing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, pingColor);
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingPoint];
        }
    }

    public class DeadBodyPing : AbstractPing
    {
        public DeadBodyPing(System.IntPtr pointer) : base(pointer) { }


        public DeadBodyPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<DeadBodyPing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, Color.white); // 白色，因为该图标本身已经是红色...
            transform.localScale *= 0.5f;
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingDeadBody];
        }
    }

    public class LootItemPing : AbstractPing
    {
        public LootItemPing(System.IntPtr pointer) : base(pointer) { }


        public LootItemPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<LootItemPing>())
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        }

        public override void Initialize(ref Vector3 point, Object userObject, Color pingColor)
        {
            base.Initialize(ref point, userObject, pingColor);
            _image.sprite = sprites[InternalBundleLoader.EFikaSprite.PingLootItem];
        }
    }
}
