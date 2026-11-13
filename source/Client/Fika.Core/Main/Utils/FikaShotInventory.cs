using System;
using EFT.InventoryLogic;

namespace Fika.Core.Main.Utils;

internal static class FikaShotInventory
{
    internal static Item PopAndClone(StackSlot cartridges, ItemController controller)
    {
        var result = cartridges.PopToNowhere(controller);
        if (result.Failed)
            throw new InvalidOperationException("Observed shot could not consume ammunition: " + result.Error);

        // SPT 5 的 FireOperationBase 会使用物品栏的
        // ID 生成器克隆被分离出来的 ResultItem。仅 PopToNowhere 会消耗这一叠，但
        // 不会为该次射击分配物品 ID，这一点与 PopTo 装入弹膛不同。
        return result.Value.ResultItem.CloneItem(controller.Cast<IDatabaseIdGenerator>());
    }
}
