using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;
using System;

namespace Fika.Core.Main.Utils;

internal static class NativeConstructor
{
    internal static unsafe void InvokeBase<T>(Il2CppObjectBase instance, params object[] arguments) where T : Il2CppObjectBase
    {
        // 所安装的注入器会固定（pin）CLR 值类型参数。CLR Boolean 不能
        // 作为 blittable 数据被固定；原生 Boolean 保留其精确类型和
        // 单字节 ABI，注入器可以安全地拆箱它的原生存储。
        for (var i = 0; i < arguments.Length; i++)
            if (arguments[i] is bool value)
                arguments[i] = new Il2CppSystem.Object(IL2CPP.il2cpp_value_box(
                    Il2CppClassPointerStore<bool>.NativeClassPtr, (IntPtr)(&value)));
        ClassInjector.InvokeBaseConstructor<T>(instance, arguments);
        GC.KeepAlive(arguments);
    }
}
