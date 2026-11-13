using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using EFT.UI.Insurance;
using EFT.Utilities;
using Fika.Core.Main.GameMode;
using Il2CppInterop.Runtime;
using Il2CppObjectPool = Il2CppInterop.Runtime.Runtime.Il2CppObjectPool;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using JsonType;

namespace Fika.Core.Main.Utils;

/// <summary>使用游戏自带的工厂，同时避开生成的 CLR 泛型仓库约束缺陷。</summary>
internal static class NativeCoopGameFactory
{
    private static IntPtr _method;

    internal static IntPtr Resolve()
    {
        if (_method != IntPtr.Zero) return _method;
        var ownerType = typeof(BaseLocalGame<EftGamePlayerOwner>);
        var field = ownerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(f => f.Name.StartsWith("NativeMethodInfoPtr_Create_", StringComparison.Ordinal));
        var definition = (IntPtr)field.GetValue(null);
        var reflected = new Il2CppSystem.Reflection.MethodInfo(IL2CPP.il2cpp_method_get_object(
            definition, Il2CppClassPointerStore<BaseLocalGame<EftGamePlayerOwner>>.NativeClassPtr));
        var closed = reflected.MakeGenericMethod(new Il2CppReferenceArray<Il2CppSystem.Type>(
            new[] { Il2CppType.Of<CoopGame>() }));
        _method = IL2CPP.il2cpp_method_get_from_reflection(closed.Pointer);
        if (_method == IntPtr.Zero) throw new MissingMethodException("Native BaseLocalGame<EftGamePlayerOwner>.Create<CoopGame> is unavailable.");
        return _method;
    }

    internal static unsafe CoopGame Create(IInputTree inputTree, Profile profile, GameWorld gameWorld,
        GameDateTime backendDateTime, InsuranceCompany insurance, GameUI gameUI, LocationSettings.Location location,
        TimeAndWeatherSettings timeAndWeather, WavesSettings wavesSettings, EDateTime dateTime,
        Callback<ExitStatus, Il2CppSystem.TimeSpan, ClientMetrics> callback, float fixedDeltaTime,
        EUpdateQueue updateQueue, IEftSession backEndSession, Il2CppSystem.Nullable<Il2CppSystem.TimeSpan> sessionTime,
        ClientMetricsEvents metricsEvents, ClientMetricsCollector metricsCollector, LocalRaidSettings raidSettings)
    {
        // 与所安装的 <T> 包装器使用相同的原生参数布局。只有
        // 解析方式变了：TPlayerOwner 与 TGameType 仍然保持各自不同。
        var keepAlive = new object[] { inputTree, profile, gameWorld, backendDateTime, insurance, gameUI, location,
            callback, backEndSession, sessionTime, metricsEvents, metricsCollector, raidSettings };
        var args = stackalloc IntPtr[18];
        args[0] = IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)inputTree);
        args[1] = IL2CPP.Il2CppObjectBaseToPtr(profile);
        args[2] = IL2CPP.Il2CppObjectBaseToPtr(gameWorld);
        args[3] = IL2CPP.Il2CppObjectBaseToPtr(backendDateTime);
        args[4] = IL2CPP.Il2CppObjectBaseToPtr(insurance);
        args[5] = IL2CPP.Il2CppObjectBaseToPtr(gameUI);
        args[6] = IL2CPP.Il2CppObjectBaseToPtr(location);
        args[7] = (IntPtr)(&timeAndWeather);
        args[8] = (IntPtr)(&wavesSettings);
        args[9] = (IntPtr)(&dateTime);
        args[10] = IL2CPP.Il2CppObjectBaseToPtr(callback);
        args[11] = (IntPtr)(&fixedDeltaTime);
        args[12] = (IntPtr)(&updateQueue);
        args[13] = IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)backEndSession);
        args[14] = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(sessionTime));
        args[15] = IL2CPP.Il2CppObjectBaseToPtr(metricsEvents);
        args[16] = IL2CPP.Il2CppObjectBaseToPtr(metricsCollector);
        args[17] = IL2CPP.Il2CppObjectBaseToPtr(raidSettings);
        var exception = IntPtr.Zero;
        try
        {
            var result = IL2CPP.il2cpp_runtime_invoke(Resolve(), IntPtr.Zero, (void**)args, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
            if (result == IntPtr.Zero) throw new InvalidOperationException("Native cooperation factory returned null.");
            return Il2CppObjectPool.Get<CoopGame>(result);
        }
        finally { GC.KeepAlive(keepAlive); }
    }
}
