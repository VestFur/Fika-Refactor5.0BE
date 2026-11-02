using EFT.Ballistics;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.BufferZone;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Lighthouse;

public static class Zyriachy_Patches
{
    /// <summary>
    /// 在程序集里搜索 'Zryachiy don't have controllable zone FIX it' 这个字符串即可找到该类
    /// </summary>
    public class BossZryachiy_Activate_Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(BossZryachiy)
                .GetMethod(nameof(BossZryachiy.Activate));
        }

        [PatchPostfix]
        public static void Postfix(BossZryachiy __instance)
        {
            var player = __instance._owner.GetPlayer;
            var listener = player.GetComponent<ZryachiyDeathListener>() ?? player.gameObject.AddComponent<ZryachiyDeathListener>();
            listener.Bind(player);
        }
    }
}

// 把原生的 DamageInfo 委托绑定到一个已注册的接收者。转换
// CLR Action 在本运行时上会生成不受支持的值包装跳板。
internal sealed class ZryachiyDeathListener : UnityEngine.MonoBehaviour
{
    public ZryachiyDeathListener(System.IntPtr pointer) : base(pointer) { }

    private Player _player;
    private System.Action<EFT.Player, EFT.IPlayer, EFT.Ballistics.DamageInfo, EBodyPart> _handler;

    internal void Bind(Player player)
    {
        Unbind();
        if (_handler == null && !NativeDelegates.TryBindManaged<System.Action<EFT.Player, EFT.IPlayer, EFT.Ballistics.DamageInfo, EBodyPart>>(
                this, nameof(OnPlayerDead), "ZryachiyDeathListener.OnPlayerDead", out _handler))
        {
            // DamageInfo 按值传递：当互操作层拒绝该委托时，原生区域的
            // 逻辑仍会继续运行，只是丢失了 Fika 这一侧的通知。
            return;
        }
        _player = player;
        _player.OnPlayerDead += _handler;
    }

    private void Unbind()
    {
        if (_player != null) _player.OnPlayerDead -= _handler;
        _player = null;
    }

    private void OnPlayerDead(Player player, IPlayer lastAggressor, DamageInfo damageInfo, EBodyPart part)
    {
        Unbind();
        Singleton<GameWorld>.Instance.BufferZoneController.SetInnerZoneAvailabilityStatus(false,
            EBufferZoneData.DisableByZryachiyDead);
    }

    private void OnDestroy() => Unbind();
}
