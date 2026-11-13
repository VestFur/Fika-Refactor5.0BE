using System;
using EFT;
using EFT.Weather;
using Il2CppInterop.Runtime.Injection;

namespace Fika.Core.Main.GameMode;

/// <summary>Only the game's IBotGame contract crosses into the native object model.</summary>
public sealed class HostBotGameBridge : Il2CppSystem.Object, IBotGame
{
    private readonly HostGameController _host;
    public HostBotGameBridge(IntPtr pointer) : base(pointer) { }
    internal HostBotGameBridge(HostGameController host) : this(ClassInjector.DerivedConstructorPointer<HostBotGameBridge>())
    {
        _host = host;
        ClassInjector.DerivedConstructorBody(this);
        ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this, []);
    }

    public GameStatus Status => _host.Status;
    public GameDateTime GameDateTime => _host.GameDateTime;
    public BotsController BotsController => _host.BotsController;
    public IWeatherCurve WeatherCurve => _host.WeatherCurve;
    public BossSpawnScenario BossSpawnScenario => _host.BossSpawnScenario;
    public JsonType.LocationSettings.Location Location => _host.Location;
    public void add_UpdateByUnity(Il2CppSystem.Action handler) => _host.add_UpdateByUnity(handler);
    public void remove_UpdateByUnity(Il2CppSystem.Action handler) => _host.remove_UpdateByUnity(handler);
    public Il2CppSystem.Threading.Tasks.Task BotDespawn(BotOwner bot) => _host.BotDespawn(bot);
}
