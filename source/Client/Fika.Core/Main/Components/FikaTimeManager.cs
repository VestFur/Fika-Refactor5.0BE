using Comfort.Common;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Components;

public class FikaTimeManager : MonoBehaviour
{
    public FikaTimeManager(System.IntPtr pointer) : base(pointer) { }


    public FikaTimeManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaTimeManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public CoopGame CoopGame;
    public GameTimer GameTimer;

    public static FikaTimeManager Create(CoopGame game)
    {
        var timeManager = game.gameObject.AddComponent<FikaTimeManager>();
        timeManager.CoopGame = game;
        timeManager.GameTimer = game.GameTimer;
        return timeManager;
    }

    protected void Update()
    {
        if (CoopGame.Status != GameStatus.Started || GameTimer == null)
            return;
        if (FikaFinalMissionDirector.DeferStop) return;

        var sessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(GameTimer, "<SessionTime>k__BackingField");
        if (sessionTime.HasValue && GameTimer.PastTime >= sessionTime.Value)
        {
            CoopGame.ExitStatus = ExitStatus.MissingInAction;
            if (Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>() is { } fikaPlayer)
                CoopGame.Extract(fikaPlayer, null);
            enabled = false;
        }
    }
}
