using Fika.Core.Main.BaseClasses;
using JsonType;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Weather;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

public class ClientRunddansController : FikaWorldController<NetworkRunddansController>
{
    public ClientRunddansController(RunddansGlobalSettings settings, LocationSettings.Location location) : base(new NetworkRunddansController(settings, location))
    {
        MainThreadDispatcher.RunAsync(() => HandleWeather(settings.ApplyFrozenEveryMS, _cts.Token)).Forget();
    }

    public async Task HandleWeather(int delay, CancellationToken token)
    {
        var gameTimer = Singleton<AbstractGame>.Instance.GameTimer;
        while (!token.IsCancellationRequested)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(System.Math.Max(1, delay), token);
            }
            catch (System.OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            if (token.IsCancellationRequested) break;
            if (gameTimer.PastTime.TotalSeconds >= (double)Native.settings.initialFrozenDelaySec)
            {
                var myPlayer = GamePlayerOwner.MyPlayer;
                if (myPlayer != null && myPlayer.HealthController.IsAlive && !myPlayer.IsAI
                    && myPlayer.AIData != null && !myPlayer.AIData.IsInside && !Native.CheckBonfires(myPlayer)
                    && !(WeatherController.Instance == null) && WeatherController.Instance.WeatherCurve.Rain
                    >= Native.settings.rainForFrozen &&
                    (DateTimeExtensions.UtcNow - myPlayer.AIData.DrinkTimestamp).TotalSeconds >= (double)Native.settings.drunkImmunitySec)
                {
                    var activeHealthController = myPlayer.ActiveHealthController;
                    if (activeHealthController != null)
                    {
                        activeHealthController.TryDoExternalBuff("Buffs_Frostbite");
                    }
                }
            }
        }
    }

    internal void DestroyItem(FikaPlayer player, string itemId)
    {
        // 物品栏同步可能已经移除了这个确切的实例。
        // 重复通知绝不能消耗同一模板下的另一个物品。
        if (string.IsNullOrEmpty(itemId) || !player.InventoryController.TryFindItem(itemId, out var item)) return;
        if (!Native.TryRemoveConsumableItem(item))
            FikaGlobals.LogError($"Runddans failed to remove item {itemId} from {player.ProfileId}");
    }
    private readonly CancellationTokenSource _cts = new();

    protected override void DisposeNative() => Native.Dispose();

    protected override void DisposeManaged()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

