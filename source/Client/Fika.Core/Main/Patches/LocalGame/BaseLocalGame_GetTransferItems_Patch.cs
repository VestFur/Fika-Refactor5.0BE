using System.Collections.Generic;
using System.Reflection;
using EFT;
using Fika.Core.Main.GameMode;
using SPTushonka.Reflection.Patching;
using JsonType;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core.Main.Patches.LocalGame;

/// <summary>
/// 用于防止玩家拿到其他人的 BTR 物品
/// </summary>
public class BaseLocalGame_GetTransferItems_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BaseLocalGame<EftGamePlayerOwner>)
            .GetMethod(nameof(BaseLocalGame<EftGamePlayerOwner>.GetTransferItems));
    }

    [PatchPrefix]
    public static bool Prefix(BaseLocalGame<EftGamePlayerOwner> __instance, ref Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppReferenceArray<FlatItem>> __result)
    {
        // 原生调用可能只带着一个 BaseLocalGame 包装对象就进入这个补丁。
        // 查询 IL2CPP 类型而不是包装对象的托管类型，这样每个
        // 玩家的 BTR/过境物品都会在 Fika 战局路径上被过滤。
        if (__instance?.TryCast<CoopGame>() is { } coopGame)
        {
            __result = coopGame.GetOwnSentItems(coopGame.ProfileId)
                .Select(pair => new KeyValuePair<string, Il2CppReferenceArray<FlatItem>>(pair.Key, pair.Value)).ToNativeDictionary();
            return false;
        }
        return true;
    }
}
