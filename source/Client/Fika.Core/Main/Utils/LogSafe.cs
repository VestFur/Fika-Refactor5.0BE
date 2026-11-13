using Il2CppInterop.Runtime.InteropTypes;
using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 针对可能为 IL2CPP 引用的值进行日志格式化。
/// <para>
/// 在 IL2CPP 包装器上调用 <c>ToString()</c> 会在原生代码中运行
/// （<c>IL2CPP.Il2CppStringToManaged(pointer)</c>）；对原生对象已经
/// 消失的包装器而言，那就是一次任何托管 catch 都拦不住的访问违规——它曾
/// 杀死过 47888 的无头主机一次，出现在一行插值了宠物 id 的日志里
/// （Fika.Headless FikaHeadlessPlugin.BeginFikaStartRaid，2026-10-10）。
/// </para>
/// 这里列出的每个成员都只从托管代理读取：原生指针，
/// 或者一次空检查。这里任何地方都不会解引用原生对象。
/// </summary>
internal static class LogSafe
{
    /// <summary>
    /// 为日志行描述一个 IL2CPP 引用：它的原生指针，或者 "null"。
    /// 有意不读取原生对象的值。
    /// </summary>
    public static string Ref(object value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is Il2CppObjectBase proxy)
        {
            return proxy.Pointer == IntPtr.Zero ? "null" : "0x" + proxy.Pointer.ToInt64().ToString("X");
        }

        return value.ToString();
    }

    /// <summary>
    /// IL2CPP 引用的空值状态，用于仅凭指针说明不了太多的情况。
    /// </summary>
    public static string IsNull(object value)
    {
        if (value is null)
        {
            return "true";
        }

        if (value is Il2CppObjectBase proxy)
        {
            return (proxy.Pointer == IntPtr.Zero).ToString();
        }

        return "false";
    }
}
