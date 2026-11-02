using EFT;
using EFT.Dialogs;
using EFT.InventoryLogic;
using EFT.Quests;

namespace Fika.Core.Main.ObservedClasses;

/// <summary>
/// 供 observed player 使用的对话控制器。将其与本地玩家的
/// ClientDialogController 分开，可防止远程任务/对话状态
/// 写入本地后端控制器。
/// </summary>
public sealed class ObservedDialogController : BaseTraderDialogController
{
    public ObservedDialogController(System.IntPtr pointer) : base(pointer)
    {
    }

    public ObservedDialogController(Profile profile, QuestController questController, InventoryController inventoryController)
        : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedDialogController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<BaseTraderDialogController>(this, profile, questController, inventoryController);
    }
}

