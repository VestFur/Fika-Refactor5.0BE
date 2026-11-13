using EFT.UI;
using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Revival;

[IgnoreAutoPatch]
internal sealed class InteractionContextHelper_GetAvailableActions_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InteractionContextHelper)
            .GetMethods()
            .First(x => x.Name == nameof(InteractionContextHelper.GetAvailableActions)
                && x.GetParameters()[0].ParameterType.Equals(typeof(GamePlayerOwner)));
    }

    [PatchPrefix]
    public static bool Prefix(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
    {
        // AP (analysis/nonfika-raid-inertness): this is the interaction menu of the local player, i.e. the
        // hot path a "which actions can I run on this thing" question takes. A ReviveInteractable only
        // exists in a raid of ours, but the guard keeps the port out of the native raid's own menu as well.
        if (FikaRaidGate.Skip("InteractionContextHelper.GetAvailableActions prefix"))
        {
            return true;
        }

        if (interactive is not ReviveInteractable reviveInteractable)
        {
            return true;
        }

        __result = reviveInteractable.GetActions(owner);
        return false;
    }
}
