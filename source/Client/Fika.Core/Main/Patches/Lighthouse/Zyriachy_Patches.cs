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
    /// Search for 'Zryachiy don't have controllable zone FIX it' string in assembly to find class
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

// Bind the native DamageInfo delegate to a registered receiver. Converting a
// CLR Action creates an unsupported value-wrapper trampoline on this runtime.
internal sealed class ZryachiyDeathListener : UnityEngine.MonoBehaviour
{
    public ZryachiyDeathListener(System.IntPtr pointer) : base(pointer) { }

    private Player _player;
    private OnPlayerDead _handler;

    internal void Bind(Player player)
    {
        Unbind();
        _handler ??= NativeDelegates.Bind<OnPlayerDead>(this, nameof(OnPlayerDead));
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
