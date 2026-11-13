using System;
using EFT;
using EFT.Weather;
using Il2CppInterop.Runtime.Injection;

namespace Fika.Core.Main.GameMode;

/// <summary>
/// 只有游戏的 IBotGame 契约会进入原生对象模型。每个
/// 成员都精确对应 47888 的 interop 签名，包括
/// Il2CppSystem.Action 事件访问器。
/// </summary>
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
    // 47888 用 IL2CPP 委托类型声明这些事件访问器，
    // 那正是注入器绑定的原生 vtable 槽位。System.Action
    // 重载无法实现它们。
    public void add_UpdateByUnity(Il2CppSystem.Action handler) => _host.add_UpdateByUnity(handler);
    public void remove_UpdateByUnity(Il2CppSystem.Action handler) => _host.remove_UpdateByUnity(handler);
    public Il2CppSystem.Threading.Tasks.Task BotDespawn(BotOwner bot) => _host.BotDespawn(bot);
}
