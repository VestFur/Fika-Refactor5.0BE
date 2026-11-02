using EFT;

namespace Fika.Core.Main.Utils;

internal static class ObservedGrenadeSync
{
    internal static void Apply(Throwable throwable, GrenadeSyncPacket packet, float smoothSpeed)
    {
        // 该标志由原生快照处理负责，但 Fika 提供自己的
        // 插值。静止的烟雾弹可能会重复发送最终数据包，
        // 而过期的非可靠数据包也可能在最终包之后到达。
        if (throwable._networkDone) return;
        var previousCollision = throwable.CollisionNumber;
        throwable.CollisionNumber = packet.CollisionNumber;
        var body = throwable.Rigidbody;
        if (packet.Done)
        {
            throwable.transform.SetPositionAndRotation(packet.Position, packet.Rotation);
            if (body != null)
            {
                body.velocity = packet.Velocity;
                body.angularVelocity = packet.AngularVelocity;
            }

            // 先走原生完成逻辑，使本地爆炸特效与音效真正执行；
            // 这个置位只是 Fika 重复包防护的兜底。
            throwable.OnDoneFromNet();
            if (!throwable._networkDone)
            {
                throwable._networkDone = true;
            }
            // 与观测手雷子类相同的保证：确保本地爆炸已经执行。
            if (throwable.TryCast<Grenade>() is { } grenade)
            {
                FikaGrenadeExplosion.TryExplode(grenade, "sync-net-done");
            }
            return;
        }

        var t = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        throwable.transform.SetPositionAndRotation(Vector3.Lerp(throwable.transform.position, packet.Position, t),
            Quaternion.Lerp(throwable.transform.rotation, packet.Rotation, t));
        // 烟雾弹在释放期间合法地释放其刚体。
        if (body != null)
        {
            body.velocity = previousCollision == packet.CollisionNumber
                ? Vector3.Lerp(body.velocity, packet.Velocity, t) : packet.Velocity;
            body.angularVelocity = previousCollision == packet.CollisionNumber
                ? Vector3.Lerp(body.angularVelocity, packet.AngularVelocity, t) : packet.AngularVelocity;
        }
    }
}
