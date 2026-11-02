using EFT;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.DebugPatches;

/// <summary>
/// 在调试构建中取消对实验室门禁卡的需求
/// </summary>
[DebugPatch]
internal class LabsKeycardDebugPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MatchmakerOperation)
            .GetMethod(nameof(MatchmakerOperation.LocationKeyCheck));
    }

    [PatchPrefix]
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
