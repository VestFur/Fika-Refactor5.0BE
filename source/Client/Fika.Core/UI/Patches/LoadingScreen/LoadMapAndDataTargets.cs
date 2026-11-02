using System;
using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.UI.Patches.LoadingScreen;

/// <summary>
/// 解析加载界面补丁所需的 <c>TarkovApplication._LoadMapAndData</c> 进度回调。
/// </summary>
/// <remarks>
/// 两个回调都位于 <c>LoadMapAndData</c> 状态机的编译器生成显示类上，而
/// 该类的编号来自元数据顺序：在本移植版起步的那个构建上是 <c>__c__DisplayClass216_0</c>，
/// 在 EFT 1.2.0.47888 上是 <c>__c__DisplayClass237_0</c>（<c>_LoadMapAndData_d__237</c> 就是该状态机）。
/// 只有方法名在各个构建之间是稳定的，所以显示类是搜索出来的，而不是硬编码；
/// 解析出的名字会写入诊断日志，这样之后的构建仅凭该日志就能检查。
/// 缓存回调（<c>b__0</c>）与加载回调（<c>b__1</c>）都接收补丁按名称绑定的
/// 那一个 <c>float</c> 进度参数。
/// </remarks>
internal static class LoadMapAndDataTargets
{
    internal static MethodBase Resolve(string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var nested in typeof(TarkovApplication).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            var method = nested.GetMethod(methodName, flags, null, new[] { typeof(float) }, null);
            if (method == null) continue;
            
            return method;
        }
        throw new MissingMethodException(
            $"TarkovApplication has no nested type declaring {methodName}(float) in this game build.");
    }
}
