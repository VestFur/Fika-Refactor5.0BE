using EFT;
using System.Collections;

namespace Fika.Core.Main.ClientClasses;

public class FikaClientGrenade : ObservedGrenade
{
    public FikaClientGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ObservedGrenade>(this);
    }


    public FikaClientGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private const float _smoothSpeed = 10f;

    private GrenadeSyncPacket _packet;
    private bool _hasPacket;

    private Coroutine _interpolationRoutine;
    private Coroutine _fuseRoutine;
    private int _previousCollision;

    private System.Collections.IEnumerator FuseFallbackCoroutine()
    {
        var delay = WeaponSource != null ? WeaponSource.GetExplDelay : 4f;
        var start = UnityEngine.Time.time;
        while (UnityEngine.Time.time - start < delay)
        {
            if (_networkDone) yield break;
            yield return null;
        }
        if (_networkDone) yield break;
        Fika.Core.Main.Utils.FikaGlobals.LogWarning($"[FikaGrenade] no Done from the host within the fuse ({delay:F2}s) - exploding locally id={Id}");
        _networkDone = true;
        OnDoneFromNet();
        if (this != null)
        {
            Fika.Core.Main.Utils.FikaGrenadeExplosion.TryExplode(this, "fuse-fallback");
        }
    }

    public override void ApplyNetPacket(GrenadeSyncPacket packet, float remoteTime)
    {
        if (_networkDone) return;
        _packet = packet;
        _previousCollision = CollisionNumber;
        CollisionNumber = packet.CollisionNumber;

        if (_packet.Done)
        {
            _hasPacket = false;
            if (_interpolationRoutine != null) StopCoroutine(_interpolationRoutine);
            _interpolationRoutine = null;
            transform.SetPositionAndRotation(_packet.Position, _packet.Rotation);
            if (Rigidbody != null)
            {
                Rigidbody.velocity = _packet.Velocity;
                Rigidbody.angularVelocity = _packet.AngularVelocity;
            }

            // 先执行原生的 ObservedGrenade 完成流程。正是它播放本地
            // 爆炸（ObservedGrenade.Expire -> Effects.EmitGrenade 以及爆炸音效）。
            // 预先设置 _networkDone 会让该原生路径提前返回，这正是投掷方
            // 客户端以及其他所有客户端都看不到也听不到爆炸、而主机端
            // 却能的原因。该闩锁随后仍会被设置，供 Fika 自己的重复包
            // 防护使用（原生代码通常会自行设置它）。
            Fika.Core.Main.Utils.FikaGlobals.LogInfo($"[FikaGrenade] client got Done id={Id} pos={_packet.Position} nativeDone={_networkDone}");
            OnDoneFromNet();
            if (!_networkDone)
            {
                _networkDone = true;
            }
            // 被观察的手雷从不运行真正的引信：可见且可听的爆炸只
            // 来自原生完成流程，因此这里也强制执行一次，以防该流程被短路。
            if (this != null)
            {
                Fika.Core.Main.Utils.FikaGrenadeExplosion.TryExplode(this, "net-done");
            }
            return;
        }

        _hasPacket = true;
        _interpolationRoutine ??= Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, SmoothingCoroutine());
        // 如果主机的 Done 包始终不到达，手雷只会因 Expire() 消亡（没有效果）。
        if (_fuseRoutine == null)
        {
            _fuseRoutine = Fika.Core.Il2CppCoroutineExtensions.StartCoroutine(this, FuseFallbackCoroutine());
        }
    }

    private IEnumerator SmoothingCoroutine()
    {
        var wait = new WaitForFixedUpdate();
        while (_hasPacket)
        {
            yield return wait;
            if (!_hasPacket) yield break;

            var t = 1f - Mathf.Exp(-_smoothSpeed * Time.fixedDeltaTime);

            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, _packet.Position, t),
                Quaternion.Lerp(transform.rotation, _packet.Rotation, t));

            if (Rigidbody != null)
            {
                if (_previousCollision == _packet.CollisionNumber)
                {
                    Rigidbody.velocity = Vector3.Lerp(Rigidbody.velocity, _packet.Velocity, t);

                    Rigidbody.angularVelocity = Vector3.Lerp(Rigidbody.angularVelocity, _packet.AngularVelocity, t);
                }
                else
                {
                    Rigidbody.velocity = _packet.Velocity;
                    Rigidbody.angularVelocity = _packet.AngularVelocity;
                }
            }
        }
    }
}
