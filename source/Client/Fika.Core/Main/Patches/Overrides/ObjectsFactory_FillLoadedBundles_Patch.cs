using System;
using System.Collections.Generic;
using System.Reflection;
using Diz.DependencyManager;
using Diz.Resources;
using EFT;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

internal sealed class ObjectsFactory_FillLoadedBundles_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(ObjectsFactory).GetMethod(
        "Method_Internal_Static_Void_List_1_PoolResourceInfo_Dictionary_2_ResourceKey_List_1_Token_IEasyBundle_TokenContainer_IEasyBundle_0");

    [PatchPrefix]
    private static void Prefix(
        ref Il2CppSystem.Collections.Generic.List<ObjectsFactory.PoolResourceInfo> bundlesList,
        Il2CppSystem.Collections.Generic.Dictionary<ResourceKey, Il2CppSystem.Collections.Generic.List<DependencyGraph<IEasyBundle>.Token>> loadedBundles,
        DependencyGraph<IEasyBundle>.TokenContainer container)
    {
        // RetainSeparate 为每个 bundle 路径持有一个 token。不同的 ResourceKeys
        // （例如同一个语音有无 rcid 的两种情况）因此可以共享那个 token。
        // 只过滤这个辅助方法自己的归属注册；调用方完整的
        // 资源列表仍会创建所有被请求的 asset/pool。
        var owned = new HashSet<IntPtr>();
        foreach (var entry in loadedBundles)
            foreach (var token in entry.Value)
                owned.Add(token.Pointer);

        var resources = new Il2CppSystem.Collections.Generic.List<ObjectsFactory.PoolResourceInfo>();
        foreach (var resource in bundlesList)
        {
            if (!container.TokenDict.TryGetValue(resource.ResourceKey.path, out var token) || owned.Add(token.Pointer))
                resources.Add(resource);
        }
        bundlesList = resources;
    }
}
