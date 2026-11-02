using System;
using System.Reflection;
using Dissonance.Integrations.MirrorIgnorance;
using Dissonance.Networking;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.VOIP;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using NativeClient = Dissonance.Networking.BaseClient<Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceServer, Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceClient, Dissonance.Integrations.MirrorIgnorance.MirrorConn>;
using NativeServer = Dissonance.Networking.BaseServer<Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceServer, Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceClient, Dissonance.Integrations.MirrorIgnorance.MirrorConn>;
using NativeClientInfo = Dissonance.Networking.ClientInfo<Dissonance.Integrations.MirrorIgnorance.MirrorConn>;
using NativeNetwork = Dissonance.Networking.BaseCommsNetwork<Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceServer, Dissonance.Integrations.MirrorIgnorance.MirrorIgnoranceClient, Dissonance.Integrations.MirrorIgnorance.MirrorConn, Dissonance.Unit, Dissonance.Unit>;

namespace Fika.Core.Main.Patches.VOIP;

public sealed class FikaVoiceCreateClientPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceCommsNetwork), nameof(MirrorIgnoranceCommsNetwork.CreateClient));
    [PatchPostfix]
    public static void Postfix(MirrorIgnoranceCommsNetwork __instance, MirrorIgnoranceClient __result) => FikaCommsNetwork.Find(__instance)?.SetClient(__result);
}

public sealed class FikaVoiceCreateServerPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceCommsNetwork), nameof(MirrorIgnoranceCommsNetwork.CreateServer));
    [PatchPostfix]
    public static void Postfix(MirrorIgnoranceCommsNetwork __instance, MirrorIgnoranceServer __result) => FikaCommsNetwork.Find(__instance)?.SetServer(__result);
}

public sealed class FikaVoiceNetworkInitializePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceCommsNetwork), nameof(MirrorIgnoranceCommsNetwork.Initialize), Type.EmptyTypes);
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceCommsNetwork __instance)
    {
        if (FikaCommsNetwork.Find(__instance) == null) return true;
        NativeMethod.InvokeVoid(__instance, typeof(NativeNetwork).GetMethod(nameof(NativeNetwork.Initialize), Type.EmptyTypes));
        return false;
    }
}

public sealed class FikaVoiceNetworkUpdatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceCommsNetwork), nameof(MirrorIgnoranceCommsNetwork.Update));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceCommsNetwork __instance)
    {
        var network = FikaCommsNetwork.Find(__instance);
        if (network == null) return true;
        network.Tick();
        return false;
    }
}

public sealed class FikaVoiceClientConnectPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceClient), nameof(MirrorIgnoranceClient.Connect));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceClient __instance)
    {
        if (FikaCommsNetwork.Find(__instance._network) == null) return true;
        __instance.Connected();
        return false;
    }
}

public sealed class FikaVoiceClientDisconnectPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceClient), nameof(MirrorIgnoranceClient.Disconnect));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceClient __instance)
    {
        var network = FikaCommsNetwork.Find(__instance._network);
        if (network == null) return true;
        try { NativeMethod.InvokeVoid(__instance, typeof(NativeClient).GetMethod(nameof(NativeClient.Disconnect))); }
        finally { network.ClientDisconnected(__instance); }
        return false;
    }
}

public sealed class FikaVoiceServerConnectPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceServer), nameof(MirrorIgnoranceServer.Connect));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceServer __instance)
    {
        if (FikaCommsNetwork.Find(__instance._network) == null) return true;
        NativeMethod.InvokeVoid(__instance, typeof(NativeServer).GetMethod(nameof(NativeServer.Connect)));
        return false;
    }
}

public sealed class FikaVoiceServerDisconnectPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceServer), nameof(MirrorIgnoranceServer.Disconnect));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceServer __instance)
    {
        var network = FikaCommsNetwork.Find(__instance._network);
        if (network == null) return true;
        try { NativeMethod.InvokeVoid(__instance, typeof(NativeServer).GetMethod(nameof(NativeServer.Disconnect))); }
        finally { network.ServerDisconnected(__instance); }
        return false;
    }
}

// 在这个 SPT 5 构建中，两个 ReadMessages 重写已经是原生的空操作。
// 它们与不相关的构造函数共用一个 RET 函数体。对该函数体做 detour 会
// 以 Dissonance 的 ABI 拦截那些构造函数，并破坏原生内存。
// 保留原生空操作；Fika 的 UDP 接收器负责喂入 NetworkReceivedPacket。

public sealed class FikaVoiceServerAddClientPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceServer), nameof(MirrorIgnoranceServer.AddClient));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceServer __instance, NativeClientInfo client)
    {
        if (FikaCommsNetwork.Find(__instance._network) == null) return true;
        // AddClient 是虚方法，生成的包装器会重新分派回
        // 这个 patch。直接调用封闭的 AOT 基类实现，
        // 以便 Dissonance 建立它的 client/room/handshake 状态。
        NativeMethod.InvokeVoid(__instance, typeof(NativeServer).GetMethod(nameof(NativeServer.AddClient), [typeof(NativeClientInfo)]), client);
        return false;
    }
}

public sealed class FikaVoiceClientReliablePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceClient), nameof(MirrorIgnoranceClient.SendReliable));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceClient __instance, Il2CppSystem.ArraySegment<byte> packet)
    {
        var network = FikaCommsNetwork.Find(__instance._network);
        if (network == null) return true;
        network.Client?.Send(packet, DeliveryMethod.ReliableOrdered);
        return false;
    }
}

public sealed class FikaVoiceClientUnreliablePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceClient), nameof(MirrorIgnoranceClient.SendUnreliable));
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceClient __instance, Il2CppSystem.ArraySegment<byte> packet)
    {
        var network = FikaCommsNetwork.Find(__instance._network);
        if (network == null) return true;
        network.Client?.Send(packet, DeliveryMethod.Sequenced);
        return false;
    }
}

public sealed class FikaVoiceServerReliablePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceServer), nameof(MirrorIgnoranceServer.SendReliable), [typeof(MirrorConn), typeof(Il2CppSystem.ArraySegment<byte>)]);
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceServer __instance, MirrorConn connection, Il2CppSystem.ArraySegment<byte> packet)
    {
        var network = FikaCommsNetwork.Find(__instance._network);
        if (network == null) return true;
        network.Server?.Send(connection, packet, DeliveryMethod.ReliableOrdered);
        return false;
    }
}

public sealed class FikaVoiceServerUnreliablePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(MirrorIgnoranceServer), nameof(MirrorIgnoranceServer.SendUnreliable), [typeof(MirrorConn), typeof(Il2CppSystem.ArraySegment<byte>)]);
    [PatchPrefix]
    public static bool Prefix(MirrorIgnoranceServer __instance, MirrorConn connection, Il2CppSystem.ArraySegment<byte> packet)
    {
        var network = FikaCommsNetwork.Find(__instance._network);
        if (network == null) return true;
        network.Server?.Send(connection, packet, DeliveryMethod.Sequenced);
        return false;
    }
}
