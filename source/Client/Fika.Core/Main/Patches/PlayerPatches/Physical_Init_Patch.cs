using System.Reflection;
using EFT;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Players;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

public sealed class Physical_Init_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(Physical)
        .GetMethod(nameof(Physical.Init), new[] { typeof(IPlayer) });

    [PatchPostfix]
    public static void Postfix(Physical __instance, IPlayer player)
    {
        // Init 会创建/重置耐力状态。只在其之后再套用战局覆盖，
        // 对象与旧子类相同：本地玩家和机器人。
        if (player?.TryCast<FikaPlayer>() != null)
            FikaClientPhysical.ApplySettings(__instance);
    }
}
