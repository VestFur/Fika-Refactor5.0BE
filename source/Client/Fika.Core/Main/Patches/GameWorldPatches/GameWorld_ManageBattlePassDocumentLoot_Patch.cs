using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

/// <summary>
/// SPT 5 会为每个玩家通过
/// <see cref="GameWorld.ManageBattlePassDocumentLoot"/> 生成战斗通行证文件，并使用新铸造的物品
/// id，因此 Fika 客户端会生成主机从未见过的一批私有文件。
/// 主机随后无法解析该客户端的拾取描述符并拒绝它，
/// 这会迫使客户端在仍处于交互状态时进行一次完整的库存重新同步。
/// Fika 客户端则改为保留主机的世界；主机仍然
/// 管理自己的文件。
/// </summary>
public sealed class GameWorld_ManageBattlePassDocumentLoot_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GameWorld)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(x => x.Name == nameof(GameWorld.ManageBattlePassDocumentLoot));
    }

    [PatchPrefix]
    public static bool Prefix() => !FikaBackendUtils.IsClient;
}
