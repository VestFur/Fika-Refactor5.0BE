using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 发送一个后端操作（游戏 POST 到
/// /client/game/profile/items/moving 的载荷），且在托管元数据中不指名任何
/// Newtonsoft.Json 类型。
/// <para>
/// 直接调用会构建一个 <c>JObject</c> 并交给
/// <c>ClientBackendSession.SendOperationRightNow</c>。当 BepInEx/plugins 中存在
/// 托管的 Newtonsoft.Json 时，该构造函数会产出原生方法
/// 无法接受的托管对象。改为通过 IL2CPP 用游戏自身的
/// JToken.Parse 解析 JSON，并用原始指针调用
/// 同一个方法。
/// </para>
/// </summary>
internal static unsafe class NativeBackendSession
{
    private static IntPtr _parse;
    private static bool _parseResolved;

    /// <summary>把 Fika 构建的 JSON 解析为原生 Newtonsoft JToken。</summary>
    internal static IntPtr ParsePayload(string json)
    {
        if (!_parseResolved)
        {
            _parseResolved = true;
            var jToken = NativeIl2Cpp.FindClass("Newtonsoft.Json", "Newtonsoft.Json.Linq", "JToken");
            _parse = NativeIl2Cpp.FindMethod(jToken, "Parse", 1);
            if (_parse == IntPtr.Zero)
            {
                FikaGlobals.LogError("Could not locate Newtonsoft.Json JToken.Parse through IL2CPP: " +
                    NativeIl2Cpp.Describe("Newtonsoft.Json", "Parse"));
            }
        }

        if (_parse == IntPtr.Zero) return IntPtr.Zero;
        return NativeIl2Cpp.Invoke(_parse, IntPtr.Zero, IL2CPP.il2cpp_string_new(json));
    }

    /// <summary>
    /// 通过 IL2CPP 发送该操作。当无法到达游戏的解析器或
    /// 后端方法时返回 false，以便调用方回退。
    /// </summary>
    internal static bool TrySend(Il2CppObjectBase session, string payloadJson, Il2CppObjectBase callback)
    {
        if (session == null) return false;

        try
        {
            var payload = ParsePayload(payloadJson);
            if (payload == IntPtr.Zero) return false;

            var runtimeClass = IL2CPP.il2cpp_object_get_class(session.Pointer);
            var method = NativeIl2Cpp.FindMethod(runtimeClass, "SendOperationRightNow", 2);
            if (method == IntPtr.Zero)
            {
                // 运行时类型可能是一个未从 ClientBackendSession 继承该方法的
                // 包装器，因此在放弃原生路径之前
                // 也要搜索其声明类。
                var declaring = NativeIl2Cpp.FindClass("Assembly-CSharp", "EFT", "ClientBackendSession");
                method = NativeIl2Cpp.FindMethod(declaring, "SendOperationRightNow", 2);
            }

            if (method == IntPtr.Zero)
            {
                FikaGlobals.LogError("Could not resolve ClientBackendSession.SendOperationRightNow through IL2CPP. " +
                    NativeIl2Cpp.Describe("Assembly-CSharp", "SendOperationRightNow"));
                return false;
            }

            NativeIl2Cpp.Invoke(method, session.Pointer, payload,
                callback == null ? IntPtr.Zero : callback.Pointer);
            return true;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"Native backend send failed, falling back to the interop call: {exception.Message}");
            return false;
        }
    }
}
