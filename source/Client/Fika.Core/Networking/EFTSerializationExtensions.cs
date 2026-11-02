using Il2CppInterop.Runtime.InteropTypes;
using System;
using System.Collections.Generic;
using System.IO;
using EFT;
using EFT.BinarySerialization;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.Notes;
using EFT.Prestige;
using EFT.Quests;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Serialization;
using JsonType;
using Mirror;

namespace Fika.Core.Networking;

/// <summary>
/// 用于写出/读取 EFT 类的扩展方法
/// </summary>
public static class EFTSerializationExtensions
{
    private static readonly List<string> _indexToType = NativeTypeNames();
    private const byte NullTypeId = byte.MaxValue;

    private static List<string> NativeTypeNames()
    {
        var native = BinarySerializationMirrorExtensions._types;
        if (native.Count > NullTypeId) throw new InvalidOperationException("Native descriptor table exceeds the Fika byte protocol");
        var result = new List<string>(native.Count);
        for (var i = 0; i < native.Count; i++) result.Add(native[i].FullName);
        return result;
    }

    private static T CastPayload<T>(object value) where T : class =>
        value is Il2CppObjectBase native ? native.Cast<T>() : (T)value;
    private static readonly Dictionary<string, byte> _typeToByte;
    private static readonly Dictionary<string, Action<NetDataWriter, object>> _serializers;
    private static readonly Dictionary<byte, Func<NetDataReader, object>> _deserializers;

    static EFTSerializationExtensions()
    {
        var count = _indexToType.Count;
        _typeToByte = new Dictionary<string, byte>(count);
        _serializers = new Dictionary<string, Action<NetDataWriter, object>>(count);
        _deserializers = new Dictionary<byte, Func<NetDataReader, object>>(count);

        for (var i = 0; i < count; i++)
        {
            _typeToByte[_indexToType[i]] = (byte)i;
        }

        RegisterSerializer<AggressorStats>((w, t) => w.PutAggressorStats(t));
        RegisterSerializer<ClassQuaternion>((w, t) => w.PutClassQuaternion(t));
        RegisterSerializer<ClassTransformSync>((w, t) => w.PutClassTransformSync(t));
        RegisterSerializer<ClassVector3>((w, t) => w.PutClassVector3(t));
        RegisterSerializer<AddNoteOperationDescriptor>((w, t) => w.PutEFTAddNoteOperationDescriptor(t));
        RegisterSerializer<ApplyKeyOperationDescriptor>((w, t) => w.PutEFTApplyKeyOperationDescriptor(t));
        RegisterSerializer<BindItemOperationDescriptor>((w, t) => w.PutEFTBindItemOperationDescriptor(t));
        RegisterSerializer<BodyPartDamageHistoryDescriptor>((w, t) => w.PutEFTBodyPartDamageHistoryDescriptor(t));
        RegisterSerializer<BonusDescriptor>((w, t) => w.PutEFTBonusDescriptor(t));
        RegisterSerializer<CheckMagazineOperationDescriptor>((w, t) => w.PutEFTCheckMagazineOperationDescriptor(t));
        RegisterSerializer<ContainerDescriptor>((w, t) => w.PutEFTContainerDescriptor(t));
        RegisterSerializer<CounterCollectionDescriptor>((w, t) => w.PutEFTCounterCollectionDescriptor(t));
        RegisterSerializer<CounterCollectionItemDescriptor>((w, t) => w.PutEFTCounterCollectionItemDescriptor(t));
        RegisterSerializer<CreateMapMarkerOperationDescriptor>((w, t) => w.PutEFTCreateMapMarkerOperationDescriptor(t));
        RegisterSerializer<CultistAmuletComponentDescriptor>((w, t) => w.PutEFTCultistAmuletComponentDescriptor(t));
        RegisterSerializer<DamageHistoryDescriptor>((w, t) => w.PutEFTDamageHistoryDescriptor(t));
        RegisterSerializer<DamageStatsDescriptor>((w, t) => w.PutEFTDamageStatsDescriptor(t));
        RegisterSerializer<DeathCause>((w, t) => w.PutEFTDeathCause(t));
        RegisterSerializer<DeleteMapMarkerOperationDescriptor>((w, t) => w.PutEFTDeleteMapMarkerOperationDescriptor(t));
        RegisterSerializer<DeleteNoteOperationDescriptor>((w, t) => w.PutEFTDeleteNoteOperationDescriptor(t));
        RegisterSerializer<DestroyedItem>((w, t) => w.PutEFTDestroyedItem(t));
        RegisterSerializer<DogTagComponentDescriptor>((w, t) => w.PutEFTDogTagComponentDescriptor(t));
        RegisterSerializer<DroppedItem>((w, t) => w.PutEFTDroppedItem(t));
        RegisterSerializer<EditMapMarkerOperationDescriptor>((w, t) => w.PutEFTEditMapMarkerOperationDescriptor(t));
        RegisterSerializer<EditNoteOperationDescriptor>((w, t) => w.PutEFTEditNoteOperationDescriptor(t));
        RegisterSerializer<ExamineMalfTypeOperationDescriptor>((w, t) => w.PutEFTExamineMalfTypeOperationDescriptor(t));
        RegisterSerializer<ExamineMalfunctionOperationDescriptor>((w, t) => w.PutEFTExamineMalfunctionOperationDescriptor(t));
        RegisterSerializer<ExamineOperationDescriptor>((w, t) => w.PutEFTExamineOperationDescriptor(t));
        RegisterSerializer<FaceShieldComponentDescriptor>((w, t) => w.PutEFTFaceShieldComponentDescriptor(t));
        RegisterSerializer<FaceshieldMarkOperationDescriptor>((w, t) => w.PutEFTFaceshieldMarkOperationDescriptor(t));
        RegisterSerializer<FireModeComponentDescriptor>((w, t) => w.PutEFTFireModeComponentDescriptor(t));
        RegisterSerializer<FoldableComponentDescriptor>((w, t) => w.PutEFTFoldableComponentDescriptor(t));
        RegisterSerializer<FoldOperationDescriptor>((w, t) => w.PutEFTFoldOperationDescriptor(t));
        RegisterSerializer<FoodDrinkComponentDescriptor>((w, t) => w.PutEFTFoodDrinkComponentDescriptor(t));
        RegisterSerializer<FoundInRaidItem>((w, t) => w.PutEFTFoundInRaidItem(t));
        RegisterSerializer<GridDescriptor>((w, t) => w.PutEFTGridDescriptor(t));
        RegisterSerializer<GridItemAddressDescriptor>((w, t) => w.PutEFTGridItemAddressDescriptor(t));
        RegisterSerializer<InventoryDescriptor>((w, t) => w.PutEFTInventoryDescriptor(t));
        RegisterSerializer<InventoryEquipmentDescriptor>((w, t) => w.PutEFTInventoryEquipmentDescriptor(t));
        RegisterSerializer<MapMarker>((w, t) => w.PutEFTInventoryLogicMapMarker(t));
        RegisterSerializer<AddToWishlistOperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsAddToWishlistOperationDescriptor(t));
        RegisterSerializer<ChangeItemsOperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsChangeItemsOperationDescriptor(t));
        RegisterSerializer<ChangeWishlistItemCategoryOperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsChangeWishlistItemCategoryOperationDescriptor(t));
        RegisterSerializer<PurchaseTraderServiceOperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsPurchaseTraderServiceOperationDescriptor(t));
        // 任务阅读（一条便签、一个章节的条件、单条条件或
        // 便签链接）属于阅读端玩家原生控制器所持有的档案记录，
        // 移植端在本地执行（见 QuestOperations），但其描述符
        // 属于原生多态表的一部分，因此仍需要一个
        // writer，否则任何最终到达数据包路径的操作都会抛出
        // "Serializer delegate missing for registered type"。
        RegisterSerializer<ReadChapterConditionsOperationDescriptor>((w, t) =>
            w.PutEFTInventoryLogicOperationsReadChapterConditionsOperationDescriptor(t));
        RegisterSerializer<ReadQuestConditionOperationDescriptor>((w, t) =>
            w.PutEFTInventoryLogicOperationsReadQuestConditionOperationDescriptor(t));
        RegisterSerializer<ReadQuestLinkOperationDescriptor>((w, t) =>
            w.PutEFTInventoryLogicOperationsReadQuestLinkOperationDescriptor(t));
        RegisterSerializer<ReadQuestNoteOperationDescriptor>((w, t) =>
            w.PutEFTInventoryLogicOperationsReadQuestNoteOperationDescriptor(t));
        RegisterSerializer<RemoveFromWishlistOperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsRemoveFromWishlistOperationDescriptor(t));
        RegisterSerializer<SearchContentOperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsSearchContentOperationDescriptor(t));
        RegisterSerializer<SearchSuboperationDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsSearchSuboperationDescriptor(t));
        RegisterSerializer<SplitToNowhereDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsSplitToNowhereDescriptor(t));
        // 磁带学习属于本地播放/进度记录，由与任务阅读相同的
        // 本地分支处理。
        RegisterSerializer<StudyItemOperationDescriptor>((w, t) => w.PutEFTStudyItemOperationDescriptor(t));
        RegisterSerializer<StudyItemPlayerOperationDescriptor>((w, t) => w.PutEFTStudyItemPlayerOperationDescriptor(t));
        RegisterSerializer<TransferFromNowhereDescriptor>((w, t) => w.PutEFTInventoryLogicOperationsTransferFromNowhereDescriptor(t));
        // 付费撤离点（车辆撤离）通过
        // EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperation 交付现金。其
        // 描述符属于原生多态表的一部分，因此缺少
        // 序列化器时，每次付款都会抛出 "Serializer delegate missing for
        // registered type"，操作在派发过程中即失败，主机
        // 始终没有收到这笔钱。
        RegisterSerializer<TransferExfiltrationPaymentOperationDescriptor>((w, t) =>
            w.PutEFTInventoryLogicOperationsTransferExfiltrationPaymentOperationDescriptor(t));
        RegisterSerializer<ItemDescriptor>((w, t) => w.PutEFTItemDescriptor(t));
        RegisterSerializer<ItemInfoDescriptor>((w, t) => w.PutEFTItemInfoDescriptor(t));
        RegisterSerializer<ItemInGridDescriptor>((w, t) => w.PutEFTItemInGridDescriptor(t));
        RegisterSerializer<JsonCorpseDescriptor>((w, t) => w.PutEFTJsonCorpseDescriptor(t));
        RegisterSerializer<JsonLootItemDescriptor>((w, t) => w.PutEFTJsonLootItemDescriptor(t));
        RegisterSerializer<KeyComponentDescriptor>((w, t) => w.PutEFTKeyComponentDescriptor(t));
        RegisterSerializer<LightComponentDescriptor>((w, t) => w.PutEFTLightComponentDescriptor(t));
        RegisterSerializer<LoadMagOperationDescriptor>((w, t) => w.PutEFTLoadMagOperationDescriptor(t));
        RegisterSerializer<LockableComponentDescriptor>((w, t) => w.PutEFTLockableComponentDescriptor(t));
        RegisterSerializer<LootDataDescriptor>((w, t) => w.PutEFTLootDataDescriptor(t));
        RegisterSerializer<MalfunctionDescriptor>((w, t) => w.PutEFTMalfunctionDescriptor(t));
        RegisterSerializer<MapComponentDescriptor>((w, t) => w.PutEFTMapComponentDescriptor(t));
        RegisterSerializer<MedKitComponentDescriptor>((w, t) => w.PutEFTMedKitComponentDescriptor(t));
        RegisterSerializer<MergeOperationDescriptor>((w, t) => w.PutEFTMergeOperationDescriptor(t));
        RegisterSerializer<MoveOperationDescriptor>((w, t) => w.PutEFTMoveOperationDescriptor(t));
        RegisterSerializer<NestedItemDescriptor>((w, t) => w.PutEFTNestedItemDescriptor(t));
        RegisterSerializer<Note>((w, t) => w.PutEFTNotesNote(t));
        RegisterSerializer<NotesManager.NotesDescriptor>((w, t) => w.PutEFTNotesNotesManagerNotesDescriptor(t));
        RegisterSerializer<OperateStationaryWeaponOperationDescriptor>((w, t) => w.PutEFTOperateStationaryWeaponOperationDescriptor(t));
        RegisterSerializer<OwnerItselfDescriptor>((w, t) => w.PutEFTOwnerItselfDescriptor(t));
        RegisterSerializer<PlantTripwireOperationDescriptor>((w, t) => w.PutEFTPlantTripwireOperationDescriptor(t));
        RegisterSerializer<PlayerVisualRepresentationDescriptor>((w, t) => w.PutEFTPlayerVisualRepresentationDescriptor(t));
        RegisterSerializer<PoisonComponentDescriptor>((w, t) => w.PutEFTPoisonComponentDescriptor(t));
        RegisterSerializer<PrestigeStatusData>((w, t) => w.PutEFTPrestigePrestigeStatusData(t));
        RegisterSerializer<Profile.HealthInfo>((w, t) => w.PutEFTProfileHealthInfo(t));
        RegisterSerializer<Profile.HealthInfo.BodyPartInfo>((w, t) => w.PutEFTProfileHealthInfoBodyPartInfo(t));
        RegisterSerializer<Profile.HealthInfo.EffectInfo>((w, t) => w.PutEFTProfileHealthInfoEffectInfo(t));
        RegisterSerializer<Profile.HealthInfo.ValueInfo>((w, t) => w.PutEFTProfileHealthInfoValueInfo(t));
        RegisterSerializer<Profile.MoneyTransferLimitData>((w, t) => w.PutEFTProfileMoneyTransferLimitData(t));
        RegisterSerializer<Profile.UnlockedInfo>((w, t) => w.PutEFTProfileUnlockedInfo(t));
        RegisterSerializer<ProfileBanDescriptor>((w, t) => w.PutEFTProfileBanDescriptor(t));
        RegisterSerializer<ProfileDescriptor>((w, t) => w.PutEFTProfileDescriptor(t));
        RegisterSerializer<ProfileInfoDescriptor>((w, t) => w.PutEFTProfileInfoDescriptor(t));
        RegisterSerializer<ProfileSettings>((w, t) => w.PutEFTProfileSettings(t));
        RegisterSerializer<ProfileStatsDescriptor>((w, t) => w.PutEFTProfileStatsDescriptor(t));
        RegisterSerializer<ProfileStatsSeparatorDescriptor>((w, t) => w.PutEFTProfileStatsSeparatorDescriptor(t));
        RegisterSerializer<QuestAcceptDescriptor>((w, t) => w.PutEFTQuestAcceptDescriptor(t));
        RegisterSerializer<QuestFinishDescriptor>((w, t) => w.PutEFTQuestFinishDescriptor(t));
        RegisterSerializer<QuestHandoverDescriptor>((w, t) => w.PutEFTQuestHandoverDescriptor(t));
        RegisterSerializer<QuestStatusData>((w, t) => w.PutEFTQuestsQuestStatusData(t));
        RegisterSerializer<RecodableComponentDescriptor>((w, t) => w.PutEFTRecodableComponentDescriptor(t));
        RegisterSerializer<RemoveOperationDescriptor>((w, t) => w.PutEFTRemoveOperationDescriptor(t));
        RegisterSerializer<RepairableComponentDescriptor>((w, t) => w.PutEFTRepairableComponentDescriptor(t));
        RegisterSerializer<RepairEnhancementComponentDescriptor>((w, t) => w.PutEFTRepairEnhancementComponentDescriptor(t));
        RegisterSerializer<RepairKitComponentDescriptor>((w, t) => w.PutEFTRepairKitComponentDescriptor(t));
        RegisterSerializer<ResourceItemComponentDescriptor>((w, t) => w.PutEFTResourceItemComponentDescriptor(t));
        RegisterSerializer<SceneResourceKey>((w, t) => w.PutEFTSceneResourceKey(t));
        RegisterSerializer<ResourceKey>((w, t) => w.PutEFTResourceKey(t));
        RegisterSerializer<SetDialogProgressOperationDescriptor>((w, t) => w.PutEFTSetDialogProgressOperationDescriptor(t));
        RegisterSerializer<SetupItemOperationDescriptor>((w, t) => w.PutEFTSetupItemOperationDescriptor(t));
        RegisterSerializer<ShellTemplateDescriptor>((w, t) => w.PutEFTShellTemplateDescriptor(t));
        RegisterSerializer<SightComponentDescriptor>((w, t) => w.PutEFTSightComponentDescriptor(t));
        RegisterSerializer<SkillsDescriptor>((w, t) => w.PutEFTSkillsDescriptor(t));
        RegisterSerializer<SkillsDescriptor.MasteringInfoDescriptor>((w, t) => w.PutEFTSkillsDescriptorMasteringInfoDescriptor(t));
        RegisterSerializer<SkillsDescriptor.SkillInfoDescriptor>((w, t) => w.PutEFTSkillsDescriptorSkillInfoDescriptor(t));
        RegisterSerializer<SlotDescriptor>((w, t) => w.PutEFTSlotDescriptor(t));
        RegisterSerializer<SlotItemAddressDescriptor>((w, t) => w.PutEFTSlotItemAddressDescriptor(t));
        RegisterSerializer<SplitOperationDescriptor>((w, t) => w.PutEFTSplitOperationDescriptor(t));
        RegisterSerializer<StackSlotDescriptor>((w, t) => w.PutEFTStackSlotDescriptor(t));
        RegisterSerializer<StackSlotItemAddressDescriptor>((w, t) => w.PutEFTStackSlotItemAddressDescriptor(t));
        RegisterSerializer<SwapOperationDescriptor>((w, t) => w.PutEFTSwapOperationDescriptor(t));
        RegisterSerializer<TagComponentDescriptor>((w, t) => w.PutEFTTagComponentDescriptor(t));
        RegisterSerializer<TagOperationDescriptor>((w, t) => w.PutEFTTagOperationDescriptor(t));
        RegisterSerializer<TaskConditionCounterDescriptor>((w, t) => w.PutEFTTaskConditionCounterDescriptor(t));
        RegisterSerializer<ThrowOperationDescriptor>((w, t) => w.PutEFTThrowOperationDescriptor(t));
        RegisterSerializer<TogglableComponentDescriptor>((w, t) => w.PutEFTTogglableComponentDescriptor(t));
        RegisterSerializer<ToggleOperationDescriptor>((w, t) => w.PutEFTToggleOperationDescriptor(t));
        RegisterSerializer<TraderInfoDescriptor>((w, t) => w.PutEFTTraderInfoDescriptor(t));
        RegisterSerializer<TraderServiceAvailabilityData>((w, t) => w.PutEFTTraderServiceAvailabilityData(t));
        RegisterSerializer<TransferOperationDescriptor>((w, t) => w.PutEFTTransferOperationDescriptor(t));
        RegisterSerializer<UnbindItemOperationDescriptor>((w, t) => w.PutEFTUnbindItemOperationDescriptor(t));
        RegisterSerializer<UnloadMagOperationDescriptor>((w, t) => w.PutEFTUnloadMagOperationDescriptor(t));
        RegisterSerializer<VictimStats>((w, t) => w.PutEFTVictimStats(t));
        RegisterSerializer<WeaponRechamberOperationDescriptor>((w, t) => w.PutEFTWeaponRechamberOperationDescriptor(t));
        RegisterSerializer<InsuredProfileItems>((w, t) => w.PutJsonTypeInsuredProfileItems(t));
        RegisterSerializer<PlayerInfo>((w, t) => w.PutJsonTypePlayerInfo(t));
        RegisterSerializer<WeightedLootPointSpawnPosition>((w, t) => w.PutWeightedLootPointSpawnPosition(t));

        RegisterDeserializer<AggressorStats>(r => r.GetAggressorStats());
        RegisterDeserializer<ClassQuaternion>(r => r.GetClassQuaternion());
        RegisterDeserializer<ClassTransformSync>(r => r.GetClassTransformSync());
        RegisterDeserializer<ClassVector3>(r => r.GetClassVector3());
        RegisterDeserializer<AddNoteOperationDescriptor>(r => r.GetEFTAddNoteOperationDescriptor());
        RegisterDeserializer<ApplyKeyOperationDescriptor>(r => r.GetEFTApplyKeyOperationDescriptor());
        RegisterDeserializer<BindItemOperationDescriptor>(r => r.GetEFTBindItemOperationDescriptor());
        RegisterDeserializer<BodyPartDamageHistoryDescriptor>(r => r.GetEFTBodyPartDamageHistoryDescriptor());
        RegisterDeserializer<BonusDescriptor>(r => r.GetEFTBonusDescriptor());
        RegisterDeserializer<CheckMagazineOperationDescriptor>(r => r.GetEFTCheckMagazineOperationDescriptor());
        RegisterDeserializer<ContainerDescriptor>(r => r.GetEFTContainerDescriptor());
        RegisterDeserializer<CounterCollectionDescriptor>(r => r.GetEFTCounterCollectionDescriptor());
        RegisterDeserializer<CounterCollectionItemDescriptor>(r => r.GetEFTCounterCollectionItemDescriptor());
        RegisterDeserializer<CreateMapMarkerOperationDescriptor>(r => r.GetEFTCreateMapMarkerOperationDescriptor());
        RegisterDeserializer<CultistAmuletComponentDescriptor>(r => r.GetEFTCultistAmuletComponentDescriptor());
        RegisterDeserializer<DamageHistoryDescriptor>(r => r.GetEFTDamageHistoryDescriptor());
        RegisterDeserializer<DamageStatsDescriptor>(r => r.GetEFTDamageStatsDescriptor());
        RegisterDeserializer<DeathCause>(r => r.GetEFTDeathCause());
        RegisterDeserializer<DeleteMapMarkerOperationDescriptor>(r => r.GetEFTDeleteMapMarkerOperationDescriptor());
        RegisterDeserializer<DeleteNoteOperationDescriptor>(r => r.GetEFTDeleteNoteOperationDescriptor());
        RegisterDeserializer<DestroyedItem>(r => r.GetEFTDestroyedItem());
        RegisterDeserializer<DogTagComponentDescriptor>(r => r.GetEFTDogTagComponentDescriptor());
        RegisterDeserializer<DroppedItem>(r => r.GetEFTDroppedItem());
        RegisterDeserializer<EditMapMarkerOperationDescriptor>(r => r.GetEFTEditMapMarkerOperationDescriptor());
        RegisterDeserializer<EditNoteOperationDescriptor>(r => r.GetEFTEditNoteOperationDescriptor());
        RegisterDeserializer<ExamineMalfTypeOperationDescriptor>(r => r.GetEFTExamineMalfTypeOperationDescriptor());
        RegisterDeserializer<ExamineMalfunctionOperationDescriptor>(r => r.GetEFTExamineMalfunctionOperationDescriptor());
        RegisterDeserializer<ExamineOperationDescriptor>(r => r.GetEFTExamineOperationDescriptor());
        RegisterDeserializer<FaceShieldComponentDescriptor>(r => r.GetEFTFaceShieldComponentDescriptor());
        RegisterDeserializer<FaceshieldMarkOperationDescriptor>(r => r.GetEFTFaceshieldMarkOperationDescriptor());
        RegisterDeserializer<FireModeComponentDescriptor>(r => r.GetEFTFireModeComponentDescriptor());
        RegisterDeserializer<FoldableComponentDescriptor>(r => r.GetEFTFoldableComponentDescriptor());
        RegisterDeserializer<FoldOperationDescriptor>(r => r.GetEFTFoldOperationDescriptor());
        RegisterDeserializer<FoodDrinkComponentDescriptor>(r => r.GetEFTFoodDrinkComponentDescriptor());
        RegisterDeserializer<FoundInRaidItem>(r => r.GetEFTFoundInRaidItem());
        RegisterDeserializer<GridDescriptor>(r => r.GetEFTGridDescriptor());
        RegisterDeserializer<GridItemAddressDescriptor>(r => r.GetEFTGridItemAddressDescriptor());
        RegisterDeserializer<InventoryDescriptor>(r => r.GetEFTInventoryDescriptor());
        RegisterDeserializer<InventoryEquipmentDescriptor>(r => r.GetEFTInventoryEquipmentDescriptor());
        RegisterDeserializer<MapMarker>(r => r.GetEFTInventoryLogicMapMarker());
        RegisterDeserializer<AddToWishlistOperationDescriptor>(r => r.GetEFTInventoryLogicOperationsAddToWishlistOperationDescriptor());
        RegisterDeserializer<ChangeItemsOperationDescriptor>(r => r.GetEFTInventoryLogicOperationsChangeItemsOperationDescriptor());
        RegisterDeserializer<ChangeWishlistItemCategoryOperationDescriptor>(r => r.GetEFTInventoryLogicOperationsChangeWishlistItemCategoryOperationDescriptor());
        RegisterDeserializer<PurchaseTraderServiceOperationDescriptor>(r => r.GetEFTInventoryLogicOperationsPurchaseTraderServiceOperationDescriptor());
        RegisterDeserializer<ReadChapterConditionsOperationDescriptor>(r =>
            r.GetEFTInventoryLogicOperationsReadChapterConditionsOperationDescriptor());
        RegisterDeserializer<ReadQuestConditionOperationDescriptor>(r =>
            r.GetEFTInventoryLogicOperationsReadQuestConditionOperationDescriptor());
        RegisterDeserializer<ReadQuestLinkOperationDescriptor>(r =>
            r.GetEFTInventoryLogicOperationsReadQuestLinkOperationDescriptor());
        RegisterDeserializer<ReadQuestNoteOperationDescriptor>(r =>
            r.GetEFTInventoryLogicOperationsReadQuestNoteOperationDescriptor());
        RegisterDeserializer<RemoveFromWishlistOperationDescriptor>(r => r.GetEFTInventoryLogicOperationsRemoveFromWishlistOperationDescriptor());
        RegisterDeserializer<SearchContentOperationDescriptor>(r => r.GetEFTInventoryLogicOperationsSearchContentOperationDescriptor());
        RegisterDeserializer<SearchSuboperationDescriptor>(r => r.GetEFTInventoryLogicOperationsSearchSuboperationDescriptor());
        RegisterDeserializer<SplitToNowhereDescriptor>(r => r.GetEFTInventoryLogicOperationsSplitToNowhereDescriptor());
        RegisterDeserializer<StudyItemOperationDescriptor>(r => r.GetEFTStudyItemOperationDescriptor());
        RegisterDeserializer<StudyItemPlayerOperationDescriptor>(r => r.GetEFTStudyItemPlayerOperationDescriptor());
        RegisterDeserializer<TransferFromNowhereDescriptor>(r => r.GetEFTInventoryLogicOperationsTransferFromNowhereDescriptor());
        RegisterDeserializer<TransferExfiltrationPaymentOperationDescriptor>(r =>
            r.GetEFTInventoryLogicOperationsTransferExfiltrationPaymentOperationDescriptor());
        RegisterDeserializer<ItemDescriptor>(r => r.GetEFTItemDescriptor());
        RegisterDeserializer<ItemInfoDescriptor>(r => r.GetEFTItemInfoDescriptor());
        RegisterDeserializer<ItemInGridDescriptor>(r => r.GetEFTItemInGridDescriptor());
        RegisterDeserializer<JsonCorpseDescriptor>(r => r.GetEFTJsonCorpseDescriptor());
        RegisterDeserializer<JsonLootItemDescriptor>(r => r.GetEFTJsonLootItemDescriptor());
        RegisterDeserializer<KeyComponentDescriptor>(r => r.GetEFTKeyComponentDescriptor());
        RegisterDeserializer<LightComponentDescriptor>(r => r.GetEFTLightComponentDescriptor());
        RegisterDeserializer<LoadMagOperationDescriptor>(r => r.GetEFTLoadMagOperationDescriptor());
        RegisterDeserializer<LockableComponentDescriptor>(r => r.GetEFTLockableComponentDescriptor());
        RegisterDeserializer<LootDataDescriptor>(r => r.GetEFTLootDataDescriptor());
        RegisterDeserializer<MalfunctionDescriptor>(r => r.GetEFTMalfunctionDescriptor());
        RegisterDeserializer<MapComponentDescriptor>(r => r.GetEFTMapComponentDescriptor());
        RegisterDeserializer<MedKitComponentDescriptor>(r => r.GetEFTMedKitComponentDescriptor());
        RegisterDeserializer<MergeOperationDescriptor>(r => r.GetEFTMergeOperationDescriptor());
        RegisterDeserializer<MoveOperationDescriptor>(r => r.GetEFTMoveOperationDescriptor());
        RegisterDeserializer<NestedItemDescriptor>(r => r.GetEFTNestedItemDescriptor());
        RegisterDeserializer<Note>(r => r.GetEFTNotesNote());
        RegisterDeserializer<NotesManager.NotesDescriptor>(r => r.GetEFTNotesNotesManagerNotesDescriptor());
        RegisterDeserializer<OperateStationaryWeaponOperationDescriptor>(r => r.GetEFTOperateStationaryWeaponOperationDescriptor());
        RegisterDeserializer<OwnerItselfDescriptor>(r => r.GetEFTOwnerItselfDescriptor());
        RegisterDeserializer<PlantTripwireOperationDescriptor>(r => r.GetEFTPlantTripwireOperationDescriptor());
        RegisterDeserializer<PlayerVisualRepresentationDescriptor>(r => r.GetEFTPlayerVisualRepresentationDescriptor());
        RegisterDeserializer<PoisonComponentDescriptor>(r => r.GetEFTPoisonComponentDescriptor());
        RegisterDeserializer<PrestigeStatusData>(r => r.GetEFTPrestigePrestigeStatusData());
        RegisterDeserializer<Profile.HealthInfo>(r => r.GetEFTProfileHealthInfo());
        RegisterDeserializer<Profile.HealthInfo.BodyPartInfo>(r => r.GetEFTProfileHealthInfoBodyPartInfo());
        RegisterDeserializer<Profile.HealthInfo.EffectInfo>(r => r.GetEFTProfileHealthInfoEffectInfo());
        RegisterDeserializer<Profile.HealthInfo.ValueInfo>(r => r.GetEFTProfileHealthInfoValueInfo());
        RegisterDeserializer<Profile.MoneyTransferLimitData>(r => r.GetEFTProfileMoneyTransferLimitData());
        RegisterDeserializer<Profile.UnlockedInfo>(r => r.GetEFTProfileUnlockedInfo());
        RegisterDeserializer<ProfileBanDescriptor>(r => r.GetEFTProfileBanDescriptor());
        RegisterDeserializer<ProfileDescriptor>(r => r.GetEFTProfileDescriptor());
        RegisterDeserializer<ProfileInfoDescriptor>(r => r.GetEFTProfileInfoDescriptor());
        RegisterDeserializer<ProfileSettings>(r => r.GetEFTProfileSettings());
        RegisterDeserializer<ProfileStatsDescriptor>(r => r.GetEFTProfileStatsDescriptor());
        RegisterDeserializer<ProfileStatsSeparatorDescriptor>(r => r.GetEFTProfileStatsSeparatorDescriptor());
        RegisterDeserializer<QuestAcceptDescriptor>(r => r.GetEFTQuestAcceptDescriptor());
        RegisterDeserializer<QuestFinishDescriptor>(r => r.GetEFTQuestFinishDescriptor());
        RegisterDeserializer<QuestHandoverDescriptor>(r => r.GetEFTQuestHandoverDescriptor());
        RegisterDeserializer<QuestStatusData>(r => r.GetEFTQuestsQuestStatusData());
        RegisterDeserializer<RecodableComponentDescriptor>(r => r.GetEFTRecodableComponentDescriptor());
        RegisterDeserializer<RemoveOperationDescriptor>(r => r.GetEFTRemoveOperationDescriptor());
        RegisterDeserializer<RepairableComponentDescriptor>(r => r.GetEFTRepairableComponentDescriptor());
        RegisterDeserializer<RepairEnhancementComponentDescriptor>(r => r.GetEFTRepairEnhancementComponentDescriptor());
        RegisterDeserializer<RepairKitComponentDescriptor>(r => r.GetEFTRepairKitComponentDescriptor());
        RegisterDeserializer<ResourceItemComponentDescriptor>(r => r.GetEFTResourceItemComponentDescriptor());
        RegisterDeserializer<SceneResourceKey>(r => r.GetEFTSceneResourceKey());
        RegisterDeserializer<ResourceKey>(r => r.GetEFTResourceKey());
        RegisterDeserializer<SetDialogProgressOperationDescriptor>(r => r.GetEFTSetDialogProgressOperationDescriptor());
        RegisterDeserializer<SetupItemOperationDescriptor>(r => r.GetEFTSetupItemOperationDescriptor());
        RegisterDeserializer<ShellTemplateDescriptor>(r => r.GetEFTShellTemplateDescriptor());
        RegisterDeserializer<SightComponentDescriptor>(r => r.GetEFTSightComponentDescriptor());
        RegisterDeserializer<SkillsDescriptor>(r => r.GetEFTSkillsDescriptor());
        RegisterDeserializer<SkillsDescriptor.MasteringInfoDescriptor>(r => r.GetEFTSkillsDescriptorMasteringInfoDescriptor());
        RegisterDeserializer<SkillsDescriptor.SkillInfoDescriptor>(r => r.GetEFTSkillsDescriptorSkillInfoDescriptor());
        RegisterDeserializer<SlotDescriptor>(r => r.GetEFTSlotDescriptor());
        RegisterDeserializer<SlotItemAddressDescriptor>(r => r.GetEFTSlotItemAddressDescriptor());
        RegisterDeserializer<SplitOperationDescriptor>(r => r.GetEFTSplitOperationDescriptor());
        RegisterDeserializer<StackSlotDescriptor>(r => r.GetEFTStackSlotDescriptor());
        RegisterDeserializer<StackSlotItemAddressDescriptor>(r => r.GetEFTStackSlotItemAddressDescriptor());
        RegisterDeserializer<SwapOperationDescriptor>(r => r.GetEFTSwapOperationDescriptor());
        RegisterDeserializer<TagComponentDescriptor>(r => r.GetEFTTagComponentDescriptor());
        RegisterDeserializer<TagOperationDescriptor>(r => r.GetEFTTagOperationDescriptor());
        RegisterDeserializer<TaskConditionCounterDescriptor>(r => r.GetEFTTaskConditionCounterDescriptor());
        RegisterDeserializer<ThrowOperationDescriptor>(r => r.GetEFTThrowOperationDescriptor());
        RegisterDeserializer<TogglableComponentDescriptor>(r => r.GetEFTTogglableComponentDescriptor());
        RegisterDeserializer<ToggleOperationDescriptor>(r => r.GetEFTToggleOperationDescriptor());
        RegisterDeserializer<TraderInfoDescriptor>(r => r.GetEFTTraderInfoDescriptor());
        RegisterDeserializer<TraderServiceAvailabilityData>(r => r.GetEFTTraderServiceAvailabilityData());
        RegisterDeserializer<TransferOperationDescriptor>(r => r.GetEFTTransferOperationDescriptor());
        RegisterDeserializer<UnbindItemOperationDescriptor>(r => r.GetEFTUnbindItemOperationDescriptor());
        RegisterDeserializer<UnloadMagOperationDescriptor>(r => r.GetEFTUnloadMagOperationDescriptor());
        RegisterDeserializer<VictimStats>(r => r.GetEFTVictimStats());
        RegisterDeserializer<WeaponRechamberOperationDescriptor>(r => r.GetEFTWeaponRechamberOperationDescriptor());
        RegisterDeserializer<InsuredProfileItems>(r => r.GetJsonTypeInsuredProfileItems());
        RegisterDeserializer<PlayerInfo>(r => r.GetJsonTypePlayerInfo());
        RegisterDeserializer<WeightedLootPointSpawnPosition>(r => r.GetWeightedLootPointSpawnPosition());
    }

    /// <summary>
    /// 用于在不使用反射的情况下注册新的多态类型。<br/>
    /// 处理已绑定在 <see cref="_indexToType"/> 中的既有类型，或为自定义类型追加动态分配的 ID。
    /// </summary>
    /// <remarks>
    /// 示例参见 <see href="https://github.com/Lacyway/MergeConsumables/blob/master/fika/MergeConsumablesSerialization.cs">MergeConsumables 参考实现</see>。
    /// </remarks>
    public static void RegisterPolymorphicType<T>(
        Action<NetDataWriter, T> serializer,
        Func<NetDataReader, T> deserializer) where T : class
    {
        var type = typeof(T).FullName;

        // 检查该类型是否已存在于基础列表中
        var index = _indexToType.IndexOf(type);
        byte id;

        if (index != -1)
        {
            id = (byte)index;
        }
        else
        {
            if (_indexToType.Count >= NullTypeId)
                throw new OverflowException("Polymorphic type limit exceeded; 255 is reserved for null");
            _indexToType.Add(type);
            id = (byte)(_indexToType.Count - 1);

        }

        _typeToByte[type] = id;
        _serializers[type] = (writer, target) => serializer(writer, CastPayload<T>(target));
        _deserializers[id] = deserializer;
    }

    private static void RegisterSerializer<T>(Action<NetDataWriter, T> exactSerializer) where T : class
    {
        _serializers[typeof(T).FullName] = (writer, target) => exactSerializer(writer, CastPayload<T>(target));
    }

    private static void RegisterDeserializer<T>(Func<NetDataReader, object> exactDeserializer) where T : class
    {
        var index = _indexToType.IndexOf(typeof(T).FullName);
        if (index != -1)
        {
            _deserializers[(byte)index] = exactDeserializer;
        }
    }

    public static void PutPolymorph<T>(this NetDataWriter writer, T target) where T : class
    {
        if (target == null)
        {
            writer.Put(NullTypeId);
            return;
        }

        var targetType = target is Il2CppSystem.Object native ? native.GetIl2CppType().FullName : target.GetType().FullName;

        if (!_typeToByte.TryGetValue(targetType, out var typeIndex))
        {
            throw new InvalidOperationException($"Type for serialization not found: {targetType}");
        }

        writer.Put(typeIndex);

        if (_serializers.TryGetValue(targetType, out var serializeAction))
        {
            serializeAction(writer, target);
        }
        else
        {
            throw new InvalidOperationException($"Serializer delegate missing for registered type: {targetType}");
        }
    }

    public static T GetPolymorph<T>(this NetDataReader reader) where T : class
    {
        var typeIndex = reader.GetByte();

        if (typeIndex == NullTypeId)
        {
            return null;
        }

        if (_deserializers.TryGetValue(typeIndex, out var deserializeFunc))
        {
            return deserializeFunc(reader) as T;
        }

        throw new InvalidOperationException($"Type index {typeIndex} for deserialization not found.");
    }

    /// <summary>
    /// 将来自 AggressorStats 对象的攻击者统计数据序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的统计数据的 AggressorStats 实例。</param>
    public static void PutAggressorStats(this NetDataWriter writer, AggressorStats target)
    {
        writer.Put(target.AccountId);
        writer.Put(target.ProfileId);
        writer.Put(target.MainProfileNickname);
        writer.Put(target.Name);
        writer.PutEnum(target.Side);
            // 47888 移除了 ProfileInfoDescriptor.PrestigeLevel：保持 writer 与 reader 对称
        writer.PutEnum(target.ColliderType);
        writer.Put(target.WeaponName);
        writer.PutEnum(target.Category);
        writer.PutEnum(target.Role);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 AggressorStats 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 AggressorStats 实例。</returns>
    public static AggressorStats GetAggressorStats(this NetDataReader reader)
    {
        return new AggressorStats
        {
            AccountId = reader.GetString(),
            ProfileId = reader.GetString(),
            MainProfileNickname = reader.GetString(),
            Name = reader.GetString(),
            Side = reader.GetEnum<EPlayerSide>(),
            // PrestigeLevel：不存在于 47888 描述符中（参见对应的 writer 改动）
            ColliderType = reader.GetEnum<EBodyPartColliderType>(),
            WeaponName = reader.GetString(),
            Category = reader.GetEnum<EMemberCategory>(),
            Role = reader.GetEnum<WildSpawnType>()
        };
    }

    /// <summary>
    /// 将添加便签操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 AddNoteOperationDescriptor 实例。</param>
    public static void PutEFTAddNoteOperationDescriptor(this NetDataWriter writer, AddNoteOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutEFTNotesNote(target.Note);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 AddNoteOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 AddNoteOperationDescriptor 实例。</returns>
    public static AddNoteOperationDescriptor GetEFTAddNoteOperationDescriptor(this NetDataReader reader)
    {
        return new AddNoteOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            Note = reader.GetEFTNotesNote()
        };
    }

    /// <summary>
    /// 将Note 便签对象序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的便签数据的 Note 实例。</param>
    public static void PutEFTNotesNote(this NetDataWriter writer, Note target)
    {
        writer.Put(target.Time);
        writer.Put(target.Text);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Note note 对象。
    /// </summary>
    /// <param name="reader">reader 实例。</param>
    /// <returns>使用流数据填充的新 Note 实例。</returns>
    public static Note GetEFTNotesNote(this NetDataReader reader)
    {
        return new Note
        {
            Time = reader.GetFloat(),
            Text = reader.GetString()
        };
    }

    /// <summary>
    /// 将使用钥匙操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 ApplyKeyOperationDescriptor 实例。</param>
    public static void PutEFTApplyKeyOperationDescriptor(this NetDataWriter writer, ApplyKeyOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.TargetItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ApplyKeyOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 ApplyKeyOperationDescriptor 实例。</returns>
    public static ApplyKeyOperationDescriptor GetEFTApplyKeyOperationDescriptor(this NetDataReader reader)
    {
        return new ApplyKeyOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            TargetItemId = reader.GetString()
        };
    }

    /// <summary>
    /// 将物品绑定操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 BindItemOperationDescriptor 实例。</param>
    public static void PutEFTBindItemOperationDescriptor(this NetDataWriter writer, BindItemOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.PutEnum(target.Index);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 BindItemOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 BindItemOperationDescriptor 实例。</returns>
    public static BindItemOperationDescriptor GetEFTBindItemOperationDescriptor(this NetDataReader reader)
    {
        return new BindItemOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            Index = reader.GetEnum<EBoundItem>()
        };
    }

    /// <summary>
    /// 将身体部位伤害历史描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的伤害历史数据的 BodyPartDamageHistoryDescriptor 实例。</param>
    public static void PutEFTBodyPartDamageHistoryDescriptor(this NetDataWriter writer, BodyPartDamageHistoryDescriptor target)
    {
        writer.Put(target.DamageList.Count);
        for (var i = 0; i < target.DamageList.Count; i++)
        {
            writer.PutEFTDamageStatsDescriptor(target.DamageList[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 BodyPartDamageHistoryDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 BodyPartDamageHistoryDescriptor 实例。</returns>
    public static BodyPartDamageHistoryDescriptor GetEFTBodyPartDamageHistoryDescriptor(this NetDataReader reader)
    {
        var gclass = new BodyPartDamageHistoryDescriptor();
        var num = reader.GetInt();
        gclass.DamageList = new Il2CppSystem.Collections.Generic.List<DamageStatsDescriptor>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.DamageList.Add(reader.GetEFTDamageStatsDescriptor());
        }
        return gclass;
    }

    /// <summary>
    /// 将伤害统计描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的伤害统计数据的 DamageStatsDescriptor 实例。</param>
    public static void PutEFTDamageStatsDescriptor(this NetDataWriter writer, DamageStatsDescriptor target)
    {
        writer.Put(target.Amount);
        writer.PutEnum(target.Type);
        writer.Put(target.SourceId);
        if (NativeNullable<EBodyPart>.Read(target, "OverDamageFrom").HasValue)
        {
            writer.Put(true);
            writer.PutEnum(NativeNullable<EBodyPart>.Read(target, "OverDamageFrom").Value);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.Blunt);
        writer.Put(target.ImpactsCount);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DamageStatsDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DamageStatsDescriptor 实例。</returns>
    public static DamageStatsDescriptor GetEFTDamageStatsDescriptor(this NetDataReader reader)
    {
        var gclass = new DamageStatsDescriptor
        {
            Amount = reader.GetFloat(),
            Type = reader.GetEnum<EDamageType>(),
            SourceId = reader.GetString()
        };
        if (reader.GetBool())
        {
            NativeNullable<EBodyPart>.Write(gclass, "OverDamageFrom", new Il2CppSystem.Nullable<EBodyPart>(reader.GetEnum<EBodyPart>()));
        }
        gclass.Blunt = reader.GetBool();
        gclass.ImpactsCount = reader.GetFloat();
        return gclass;
    }

    /// <summary>
    /// 将档案加成描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的加成数据的 BonusDescriptor 实例。</param>
    public static void PutEFTBonusDescriptor(this NetDataWriter writer, BonusDescriptor target)
    {
        writer.PutEnum(target.BonusType);
        writer.PutMongoID(target.Id);
        writer.Put(target.Value);
        writer.Put(target.IsVisible);
        writer.Put(target.Passive);
        writer.Put(target.Production);
        writer.Put(target.Icon);
        if (target.Filters != null)
        {
            writer.Put(true);
            writer.Put(target.Filters.Count);
            for (var i = 0; i < target.Filters.Count; i++)
            {
                writer.PutMongoID(target.Filters[i]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (NativeNullable<ESkillClass>.Read(target, "SkillType").HasValue)
        {
            writer.Put(true);
            writer.PutEnum(NativeNullable<ESkillClass>.Read(target, "SkillType").Value);
        }
        else
        {
            writer.Put(false);
        }
        if (NativeNullable<ESkillId>.Read(target, "SkillName").HasValue)
        {
            writer.Put(true);
            writer.PutEnum(NativeNullable<ESkillId>.Read(target, "SkillName").Value);
        }
        else
        {
            writer.Put(false);
        }
        if (NativeNullable<MongoID>.Read(target, "TemplateId").HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(NativeNullable<MongoID>.Read(target, "TemplateId").Value);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 BonusDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 BonusDescriptor 实例。</returns>
    public static BonusDescriptor GetEFTBonusDescriptor(this NetDataReader reader)
    {
        var profileBonusesClass = new BonusDescriptor
        {
            BonusType = reader.GetEnum<EBonusType>(),
            Id = reader.GetMongoID(),
            Value = reader.GetDouble(),
            IsVisible = reader.GetBool(),
            Passive = reader.GetBool(),
            Production = reader.GetBool(),
            Icon = reader.GetString()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            profileBonusesClass.Filters = new Il2CppSystem.Collections.Generic.List<MongoID>(num);
            for (var i = 0; i < num; i++)
            {
                profileBonusesClass.Filters.Add(reader.GetMongoID());
            }
        }
        if (reader.GetBool())
        {
            NativeNullable<ESkillClass>.Write(profileBonusesClass, "SkillType", new Il2CppSystem.Nullable<ESkillClass>(reader.GetEnum<ESkillClass>()));
        }
        if (reader.GetBool())
        {
            NativeNullable<ESkillId>.Write(profileBonusesClass, "SkillName", new Il2CppSystem.Nullable<ESkillId>(reader.GetEnum<ESkillId>()));
        }
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(profileBonusesClass, "TemplateId", new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        return profileBonusesClass;
    }

    /// <summary>
    /// 将弹匣检查操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 CheckMagazineOperationDescriptor 实例。</param>
    public static void PutEFTCheckMagazineOperationDescriptor(this NetDataWriter writer, CheckMagazineOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
        writer.Put(target.CheckStatus);
        writer.Put(target.SkillLevel);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 CheckMagazineOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 CheckMagazineOperationDescriptor 实例。</returns>
    public static CheckMagazineOperationDescriptor GetEFTCheckMagazineOperationDescriptor(this NetDataReader reader)
    {
        return new CheckMagazineOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID(),
            CheckStatus = reader.GetBool(),
            SkillLevel = reader.GetByte()
        };
    }

    /// <summary>
    /// 将容器描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的容器数据的 ContainerDescriptor 实例。</param>
    public static void PutEFTContainerDescriptor(this NetDataWriter writer, ContainerDescriptor target)
    {
        if (NativeNullable<MongoID>.Read(target, "ParentId").HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(NativeNullable<MongoID>.Read(target, "ParentId").Value);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.ContainerId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ContainerDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 ContainerDescriptor 实例。</returns>
    public static ContainerDescriptor GetEFTContainerDescriptor(this NetDataReader reader)
    {
        var gclass = new ContainerDescriptor();
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(gclass, "ParentId", new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        gclass.ContainerId = reader.GetString();
        return gclass;
    }

    /// <summary>
    /// 将计数器集合描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的集合项的 CounterCollectionDescriptor 实例。</param>
    public static void PutEFTCounterCollectionDescriptor(this NetDataWriter writer, CounterCollectionDescriptor target)
    {
        writer.Put(target.Items.Count);
        for (var i = 0; i < target.Items.Count; i++)
        {
            writer.PutEFTCounterCollectionItemDescriptor(target.Items[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 CounterCollectionDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 CounterCollectionDescriptor 实例。</returns>
    public static CounterCollectionDescriptor GetEFTCounterCollectionDescriptor(this NetDataReader reader)
    {
        var gclass = new CounterCollectionDescriptor();
        var num = reader.GetInt();
        gclass.Items = new Il2CppSystem.Collections.Generic.List<CounterCollectionItemDescriptor>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.Items.Add(reader.GetEFTCounterCollectionItemDescriptor());
        }
        return gclass;
    }

    /// <summary>
    /// 将计数器集合项描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的物品数据的 CounterCollectionItemDescriptor 实例。</param>
    public static void PutEFTCounterCollectionItemDescriptor(this NetDataWriter writer, CounterCollectionItemDescriptor target)
    {
        writer.Put(target.Key.Count);
        for (var i = 0; i < target.Key.Count; i++)
        {
            writer.Put(target.Key[i]);
        }
        writer.Put(target.Value);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 CounterCollectionItemDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 CounterCollectionItemDescriptor 实例。</returns>
    public static CounterCollectionItemDescriptor GetEFTCounterCollectionItemDescriptor(this NetDataReader reader)
    {
        var gclass = new CounterCollectionItemDescriptor();
        var num = reader.GetInt();
        gclass.Key = new Il2CppSystem.Collections.Generic.List<string>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.Key.Add(reader.GetString());
        }
        gclass.Value = reader.GetLong();
        return gclass;
    }

    /// <summary>
    /// 将地图标记创建操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 CreateMapMarkerOperationDescriptor 实例。</param>
    public static void PutEFTCreateMapMarkerOperationDescriptor(this NetDataWriter writer, CreateMapMarkerOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.MapItemId);
        writer.PutEFTInventoryLogicMapMarker(target.MapMarker);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 CreateMapMarkerOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 CreateMapMarkerOperationDescriptor 实例。</returns>
    public static CreateMapMarkerOperationDescriptor GetEFTCreateMapMarkerOperationDescriptor(this NetDataReader reader)
    {
        return new CreateMapMarkerOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            MapItemId = reader.GetString(),
            MapMarker = reader.GetEFTInventoryLogicMapMarker()
        };
    }

    /// <summary>
    /// 将地图标记对象序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的地图标记数据的 MapMarker 实例。</param>
    public static void PutEFTInventoryLogicMapMarker(this NetDataWriter writer, MapMarker target)
    {
        writer.PutEnum(target.Type);
        writer.Put(target.X);
        writer.Put(target.Y);
        writer.Put(target.Note);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 MapMarker 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 MapMarker 实例。</returns>
    public static MapMarker GetEFTInventoryLogicMapMarker(this NetDataReader reader)
    {
        return new MapMarker
        {
            Type = reader.GetEnum<MapMarkerType>(),
            X = reader.GetInt(),
            Y = reader.GetInt(),
            Note = reader.GetString()
        };
    }

    /// <summary>
    /// 将邪教徒护身符组件描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的组件数据的 CultistAmuletComponentDescriptor 实例。</param>
    public static void PutEFTCultistAmuletComponentDescriptor(this NetDataWriter writer, CultistAmuletComponentDescriptor target)
    {
        writer.Put(target.NumberOfUsages);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 CultistAmuletComponentDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 CultistAmuletComponentDescriptor 实例。</returns>
    public static CultistAmuletComponentDescriptor GetEFTCultistAmuletComponentDescriptor(this NetDataReader reader)
    {
        return new CultistAmuletComponentDescriptor
        {
            NumberOfUsages = reader.GetInt()
        };
    }

    /// <summary>
    /// 将完整的伤害历史描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的完整的伤害历史数据的 DamageHistoryDescriptor 实例。</param>
    public static void PutEFTDamageHistoryDescriptor(this NetDataWriter writer, DamageHistoryDescriptor target)
    {
        writer.PutEnum(target.LethalDamagePart);
        if (target.LethalDamage != null)
        {
            writer.Put(true);
            writer.PutEFTDamageStatsDescriptor(target.LethalDamage);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.BodyParts.Count);
        foreach (var keyValuePair in target.BodyParts)
        {
            writer.PutEnum(keyValuePair.Key);
            writer.PutEFTBodyPartDamageHistoryDescriptor(keyValuePair.Value);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DamageHistoryDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DamageHistoryDescriptor 实例。</returns>
    public static DamageHistoryDescriptor GetEFTDamageHistoryDescriptor(this NetDataReader reader)
    {
        var gclass = new DamageHistoryDescriptor
        {
            LethalDamagePart = reader.GetEnum<EBodyPart>()
        };
        if (reader.GetBool())
        {
            gclass.LethalDamage = reader.GetEFTDamageStatsDescriptor();
        }
        var num = reader.GetInt();
        gclass.BodyParts = new Il2CppSystem.Collections.Generic.Dictionary<EBodyPart, BodyPartDamageHistoryDescriptor>();
        for (var i = 0; i < num; i++)
        {
            gclass.BodyParts[reader.GetEnum<EBodyPart>()] = reader.GetEFTBodyPartDamageHistoryDescriptor();
        }
        return gclass;
    }

    /// <summary>
    /// 将战斗生命值消耗日志与致伤武器属性序列化写入 writer 流。
    /// </summary>
    public static void PutEFTDeathCause(this NetDataWriter writer, DeathCause target)
    {
        writer.PutEnum(target.DamageType);
        writer.PutEnum(target.Side);
        writer.PutEnum(target.Role);
        writer.Put(target.WeaponId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DeathCause 对象。
    /// </summary>
    public static DeathCause GetEFTDeathCause(this NetDataReader reader)
    {
        return new DeathCause
        {
            DamageType = reader.GetEnum<EDamageType>(),
            Side = reader.GetEnum<EPlayerSide>(),
            Role = reader.GetEnum<WildSpawnType>(),
            WeaponId = reader.GetString()
        };
    }

    /// <summary>
    /// 将地图标记删除操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 DeleteMapMarkerOperationDescriptor 实例。</param>
    public static void PutEFTDeleteMapMarkerOperationDescriptor(this NetDataWriter writer, DeleteMapMarkerOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.MapItemId);
        writer.Put(target.X);
        writer.Put(target.Y);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DeleteMapMarkerOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DeleteMapMarkerOperationDescriptor 实例。</returns>
    public static DeleteMapMarkerOperationDescriptor GetEFTDeleteMapMarkerOperationDescriptor(this NetDataReader reader)
    {
        return new DeleteMapMarkerOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            MapItemId = reader.GetString(),
            X = reader.GetInt(),
            Y = reader.GetInt()
        };
    }

    /// <summary>
    /// 将便签删除操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 DeleteNoteOperationDescriptor 实例。</param>
    public static void PutEFTDeleteNoteOperationDescriptor(this NetDataWriter writer, DeleteNoteOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.Index);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DeleteNoteOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DeleteNoteOperationDescriptor 实例。</returns>
    public static DeleteNoteOperationDescriptor GetEFTDeleteNoteOperationDescriptor(this NetDataReader reader)
    {
        return new DeleteNoteOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            Index = reader.GetInt()
        };
    }

    /// <summary>
    /// 将已销毁物品详情序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的物品销毁数据的 DestroyedItem 实例。</param>
    public static void PutEFTDestroyedItem(this NetDataWriter writer, DestroyedItem target)
    {
        writer.PutMongoID(target.ItemId);
        writer.Put(target.NumberToDestroy);
        writer.Put(target.NumberToPreserve);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DestroyedItem 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DestroyedItem 实例。</returns>
    public static DestroyedItem GetEFTDestroyedItem(this NetDataReader reader)
    {
        return new DestroyedItem
        {
            ItemId = reader.GetMongoID(),
            NumberToDestroy = reader.GetInt(),
            NumberToPreserve = reader.GetInt()
        };
    }

    /// <summary>
    /// 将狗牌组件描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的狗牌组件数据的 DogTagComponentDescriptor 实例。</param>
    public static void PutEFTDogTagComponentDescriptor(this NetDataWriter writer, DogTagComponentDescriptor target)
    {
        writer.Put(target.AccountId);
        writer.PutMongoID(target.ProfileId);
        writer.Put(target.Nickname);
        writer.PutEnum(target.Side);
        writer.Put(target.Level);
        writer.Put(target.Time);
        writer.PutEnum(target.Status);
        writer.Put(NativeNullable<uint>.Read(target, "KillerAccountId").HasValue);
        if (NativeNullable<uint>.Read(target, "KillerAccountId").HasValue) writer.Put(NativeNullable<uint>.Read(target, "KillerAccountId").Value);
        writer.Put(NativeNullable<MongoID>.Read(target, "KillerProfileId").HasValue);
        if (NativeNullable<MongoID>.Read(target, "KillerProfileId").HasValue) writer.PutMongoID(NativeNullable<MongoID>.Read(target, "KillerProfileId").Value);
        writer.Put(target.KillerName);
        writer.Put(target.WeaponName);
        writer.Put(target.CarriedByGroupMember);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DogTagComponentDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DogTagComponentDescriptor 实例。</returns>
    public static DogTagComponentDescriptor GetEFTDogTagComponentDescriptor(this NetDataReader reader)
    {
        return new DogTagComponentDescriptor
        {
            AccountId = reader.GetUInt(),
            ProfileId = reader.GetMongoID(),
            Nickname = reader.GetString(),
            Side = reader.GetEnum<EPlayerSide>(),
            Level = reader.GetByte(),
            Time = reader.GetDouble(),
            Status = reader.GetEnum<EDogtagStatus>(),
            KillerAccountId = reader.GetBool() ? new Il2CppSystem.Nullable<uint>(reader.GetUInt()) : new(),
            KillerProfileId = reader.GetBool() ? new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()) : new(),
            KillerName = reader.GetString(),
            WeaponName = reader.GetString(),
            CarriedByGroupMember = reader.GetBool()
        };
    }

    /// <summary>
    /// 将掉落物品信息序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的掉落物品数据的 DroppedItem 实例。</param>
    public static void PutEFTDroppedItem(this NetDataWriter writer, DroppedItem target)
    {
        writer.PutMongoID(target.QuestId);
        writer.PutMongoID(target.ItemId);
        writer.Put(target.ZoneId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 DroppedItem 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 DroppedItem 实例。</returns>
    public static DroppedItem GetEFTDroppedItem(this NetDataReader reader)
    {
        return new DroppedItem
        {
            QuestId = reader.GetMongoID(),
            ItemId = reader.GetMongoID(),
            ZoneId = reader.GetString()
        };
    }

    /// <summary>
    /// 将地图标记编辑操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 EditMapMarkerOperationDescriptor 实例。</param>
    public static void PutEFTEditMapMarkerOperationDescriptor(this NetDataWriter writer, EditMapMarkerOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.MapItemId);
        writer.Put(target.X);
        writer.Put(target.Y);
        writer.PutEFTInventoryLogicMapMarker(target.MapMarker);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 EditMapMarkerOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 EditMapMarkerOperationDescriptor 实例。</returns>
    public static EditMapMarkerOperationDescriptor GetEFTEditMapMarkerOperationDescriptor(this NetDataReader reader)
    {
        return new EditMapMarkerOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            MapItemId = reader.GetString(),
            X = reader.GetInt(),
            Y = reader.GetInt(),
            MapMarker = reader.GetEFTInventoryLogicMapMarker()
        };
    }

    /// <summary>
    /// 将便签编辑操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 EditNoteOperationDescriptor 实例。</param>
    public static void PutEFTEditNoteOperationDescriptor(this NetDataWriter writer, EditNoteOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.Index);
        writer.PutEFTNotesNote(target.Note);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 EditNoteOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 EditNoteOperationDescriptor 实例。</returns>
    public static EditNoteOperationDescriptor GetEFTEditNoteOperationDescriptor(this NetDataReader reader)
    {
        return new EditNoteOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            Index = reader.GetInt(),
            Note = reader.GetEFTNotesNote()
        };
    }

    /// <summary>
    /// 将故障类型检查操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 ExamineMalfTypeOperationDescriptor 实例。</param>
    public static void PutEFTExamineMalfTypeOperationDescriptor(this NetDataWriter writer, ExamineMalfTypeOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ExamineMalfTypeOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 ExamineMalfTypeOperationDescriptor 实例。</returns>
    public static ExamineMalfTypeOperationDescriptor GetEFTExamineMalfTypeOperationDescriptor(this NetDataReader reader)
    {
        return new ExamineMalfTypeOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将故障检查操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 ExamineMalfunctionOperationDescriptor 实例。</param>
    public static void PutEFTExamineMalfunctionOperationDescriptor(this NetDataWriter writer, ExamineMalfunctionOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ExamineMalfunctionOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 ExamineMalfunctionOperationDescriptor 实例。</returns>
    public static ExamineMalfunctionOperationDescriptor GetEFTExamineMalfunctionOperationDescriptor(this NetDataReader reader)
    {
        return new ExamineMalfunctionOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将物品检查操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 ExamineOperationDescriptor 实例。</param>
    public static void PutEFTExamineOperationDescriptor(this NetDataWriter writer, ExamineOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ExamineOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 ExamineOperationDescriptor 实例。</returns>
    public static ExamineOperationDescriptor GetEFTExamineOperationDescriptor(this NetDataReader reader)
    {
        return new ExamineOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString()
        };
    }

    /// <summary>
    /// 将面罩组件描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的面罩状态数据的 FaceShieldComponentDescriptor 实例。</param>
    public static void PutEFTFaceShieldComponentDescriptor(this NetDataWriter writer, FaceShieldComponentDescriptor target)
    {
        writer.Put(target.Hits);
        writer.Put(target.HitSeed);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FaceShieldComponentDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FaceShieldComponentDescriptor 实例。</returns>
    public static FaceShieldComponentDescriptor GetEFTFaceShieldComponentDescriptor(this NetDataReader reader)
    {
        return new FaceShieldComponentDescriptor
        {
            Hits = reader.GetByte(),
            HitSeed = reader.GetByte()
        };
    }

    /// <summary>
    /// 将面罩标记操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 FaceshieldMarkOperationDescriptor 实例。</param>
    public static void PutEFTFaceshieldMarkOperationDescriptor(this NetDataWriter writer, FaceshieldMarkOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FaceshieldMarkOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FaceshieldMarkOperationDescriptor 实例。</returns>
    public static FaceshieldMarkOperationDescriptor GetEFTFaceshieldMarkOperationDescriptor(this NetDataReader reader)
    {
        return new FaceshieldMarkOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将射击模式组件描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的射击模式状态数据的 FireModeComponentDescriptor 实例。</param>
    public static void PutEFTFireModeComponentDescriptor(this NetDataWriter writer, FireModeComponentDescriptor target)
    {
        writer.PutEnum(target.FireMode);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FireModeComponentDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FireModeComponentDescriptor 实例。</returns>
    public static FireModeComponentDescriptor GetEFTFireModeComponentDescriptor(this NetDataReader reader)
    {
        return new FireModeComponentDescriptor
        {
            FireMode = reader.GetEnum<Weapon.EFireMode>()
        };
    }

    /// <summary>
    /// 将可折叠组件描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的折叠状态数据的 FoldableComponentDescriptor 实例。</param>
    public static void PutEFTFoldableComponentDescriptor(this NetDataWriter writer, FoldableComponentDescriptor target)
    {
        writer.Put(target.Folded);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FoldableComponentDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FoldableComponentDescriptor 实例。</returns>
    public static FoldableComponentDescriptor GetEFTFoldableComponentDescriptor(this NetDataReader reader)
    {
        return new FoldableComponentDescriptor
        {
            Folded = reader.GetBool()
        };
    }

    /// <summary>
    /// 将物品折叠操作描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的描述符数据的 FoldOperationDescriptor 实例。</param>
    public static void PutEFTFoldOperationDescriptor(this NetDataWriter writer, FoldOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.Value);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FoldOperationDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FoldOperationDescriptor 实例。</returns>
    public static FoldOperationDescriptor GetEFTFoldOperationDescriptor(this NetDataReader reader)
    {
        return new FoldOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            Value = reader.GetBool()
        };
    }

    /// <summary>
    /// 将食物与饮料组件描述符序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的食物/饮料状态数据的 FoodDrinkComponentDescriptor 实例。</param>
    public static void PutEFTFoodDrinkComponentDescriptor(this NetDataWriter writer, FoodDrinkComponentDescriptor target)
    {
        writer.Put(target.HpPercent);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FoodDrinkComponentDescriptor 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FoodDrinkComponentDescriptor 实例。</returns>
    public static FoodDrinkComponentDescriptor GetEFTFoodDrinkComponentDescriptor(this NetDataReader reader)
    {
        return new FoodDrinkComponentDescriptor
        {
            HpPercent = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将found in raid 物品详情序列化写入 writer 流。
    /// </summary>
    /// <param name="target">包含要写入的物品战局数据的 FoundInRaidItem 实例。</param>
    public static void PutEFTFoundInRaidItem(this NetDataWriter writer, FoundInRaidItem target)
    {
        writer.PutMongoID(target.QuestId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 FoundInRaidItem 对象。
    /// </summary>
    /// <returns>使用流数据填充的新 FoundInRaidItem 实例。</returns>
    public static FoundInRaidItem GetEFTFoundInRaidItem(this NetDataReader reader)
    {
        return new FoundInRaidItem
        {
            QuestId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将网格描述符对象序列化写入 writer 流。
    /// </summary>
    public static void PutEFTGridDescriptor(this NetDataWriter writer, GridDescriptor target)
    {
        writer.Put(target.GridNumber);
        writer.Put(target.ContainedItems.Count);
        for (var i = 0; i < target.ContainedItems.Count; i++)
        {
            writer.PutEFTItemInGridDescriptor(target.ContainedItems[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 GridDescriptor 对象。
    /// </summary>
    public static GridDescriptor GetEFTGridDescriptor(this NetDataReader reader)
    {
        var gclass = new GridDescriptor
        {
            GridNumber = reader.GetByte()
        };
        var num = reader.GetInt();
        gclass.ContainedItems = new Il2CppSystem.Collections.Generic.List<ItemInGridDescriptor>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.ContainedItems.Add(reader.GetEFTItemInGridDescriptor());
        }
        return gclass;
    }

    /// <summary>
    /// 将网格物品地址描述符对象序列化写入 writer 流。
    /// </summary>
    public static void PutEFTGridItemAddressDescriptor(this NetDataWriter writer, GridItemAddressDescriptor target)
    {
        writer.Put(target.X);
        writer.Put(target.Y);
        writer.Put(target.Horizontal);
        writer.PutEFTContainerDescriptor(target.Container);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 GridItemAddressDescriptor 对象。
    /// </summary>
    public static GridItemAddressDescriptor GetEFTGridItemAddressDescriptor(this NetDataReader reader)
    {
        return new GridItemAddressDescriptor
        {
            X = reader.GetByte(),
            Y = reader.GetByte(),
            Horizontal = reader.GetBool(),
            Container = reader.GetEFTContainerDescriptor()
        };
    }

    /// <summary>
    /// 将库存描述符状态序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryDescriptor(this NetDataWriter writer, InventoryDescriptor target)
    {
        writer.PutEFTItemDescriptor(target.Equipment);
        if (target.Stash != null)
        {
            writer.Put(true);
            writer.PutEFTItemDescriptor(target.Stash);
        }
        else
        {
            writer.Put(false);
        }
        if (target.QuestRaidItems != null)
        {
            writer.Put(true);
            writer.PutEFTItemDescriptor(target.QuestRaidItems);
        }
        else
        {
            writer.Put(false);
        }
        if (target.QuestStashItems != null)
        {
            writer.Put(true);
            writer.PutEFTItemDescriptor(target.QuestStashItems);
        }
        else
        {
            writer.Put(false);
        }
        if (target.SortingTable != null)
        {
            writer.Put(true);
            writer.PutEFTItemDescriptor(target.SortingTable);
        }
        else
        {
            writer.Put(false);
        }
        if (target.HideoutCustomizationStash != null)
        {
            writer.Put(true);
            writer.PutEFTItemDescriptor(target.HideoutCustomizationStash);
        }
        else
        {
            writer.Put(false);
        }
        if (target.HideoutAreaStashes != null)
        {
            writer.Put(true);
            writer.Put(target.HideoutAreaStashes.Count);
            foreach (var keyValuePair in target.HideoutAreaStashes)
            {
                writer.PutEnum(keyValuePair.Key);
                writer.PutEFTItemDescriptor(keyValuePair.Value);
            }
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.FastAccess.Count);
        foreach (var keyValuePair2 in target.FastAccess)
        {
            writer.PutEnum(keyValuePair2.Key);
            writer.PutMongoID(keyValuePair2.Value);
        }
        writer.Put(target.FavoriteItemsStorage.Count);
        for (var i = 0; i < target.FavoriteItemsStorage.Count; i++)
        {
            writer.PutMongoID(target.FavoriteItemsStorage[i]);
        }
        writer.Put(target.CheckInventoryHash);
        writer.Put(target.DiscardLimits.Count);
        foreach (var keyValuePair3 in target.DiscardLimits)
        {
            writer.PutMongoID(keyValuePair3.Key);
            writer.Put(keyValuePair3.Value);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 InventoryDescriptor 对象。
    /// </summary>
    public static InventoryDescriptor GetEFTInventoryDescriptor(this NetDataReader reader)
    {
        var eftinventoryClass = new InventoryDescriptor
        {
            Equipment = reader.GetEFTItemDescriptor()
        };
        if (reader.GetBool())
        {
            eftinventoryClass.Stash = reader.GetEFTItemDescriptor();
        }
        if (reader.GetBool())
        {
            eftinventoryClass.QuestRaidItems = reader.GetEFTItemDescriptor();
        }
        if (reader.GetBool())
        {
            eftinventoryClass.QuestStashItems = reader.GetEFTItemDescriptor();
        }
        if (reader.GetBool())
        {
            eftinventoryClass.SortingTable = reader.GetEFTItemDescriptor();
        }
        if (reader.GetBool())
        {
            eftinventoryClass.HideoutCustomizationStash = reader.GetEFTItemDescriptor();
        }
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            eftinventoryClass.HideoutAreaStashes = new Il2CppSystem.Collections.Generic.Dictionary<EAreaType, ItemDescriptor>();
            for (var i = 0; i < num; i++)
            {
                eftinventoryClass.HideoutAreaStashes[reader.GetEnum<EAreaType>()] = reader.GetEFTItemDescriptor();
            }
        }
        var num2 = reader.GetInt();
        eftinventoryClass.FastAccess = new Il2CppSystem.Collections.Generic.Dictionary<EBoundItem, MongoID>();
        for (var j = 0; j < num2; j++)
        {
            eftinventoryClass.FastAccess[reader.GetEnum<EBoundItem>()] = reader.GetMongoID();
        }
        var num3 = reader.GetInt();
        eftinventoryClass.FavoriteItemsStorage = new Il2CppSystem.Collections.Generic.List<MongoID>(num3);
        for (var k = 0; k < num3; k++)
        {
            eftinventoryClass.FavoriteItemsStorage.Add(reader.GetMongoID());
        }
        eftinventoryClass.CheckInventoryHash = reader.GetBool();
        var num4 = reader.GetInt();
        eftinventoryClass.DiscardLimits = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, int>();
        for (var l = 0; l < num4; l++)
        {
            eftinventoryClass.DiscardLimits[reader.GetMongoID()] = reader.GetInt();
        }
        return eftinventoryClass;
    }

    /// <summary>
    /// 将库存装备描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryEquipmentDescriptor(this NetDataWriter writer, InventoryEquipmentDescriptor target)
    {
        writer.PutEFTItemDescriptor(target.Items);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 InventoryEquipmentDescriptor 对象。
    /// </summary>
    public static InventoryEquipmentDescriptor GetEFTInventoryEquipmentDescriptor(this NetDataReader reader)
    {
        return new InventoryEquipmentDescriptor
        {
            Items = reader.GetEFTItemDescriptor()
        };
    }

    /// <summary>
    /// 将愿望单添加操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsAddToWishlistOperationDescriptor(this NetDataWriter writer, AddToWishlistOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.TemplateId);
        writer.PutEnum(target.GroupType);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 AddToWishlistOperationDescriptor 对象。
    /// </summary>
    public static AddToWishlistOperationDescriptor GetEFTInventoryLogicOperationsAddToWishlistOperationDescriptor(this NetDataReader reader)
    {
        return new AddToWishlistOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            TemplateId = reader.GetMongoID(),
            GroupType = reader.GetEnum<EWishlistGroup>()
        };
    }

    /// <summary>
    /// 将物品修改与变更操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsChangeItemsOperationDescriptor(this NetDataWriter writer, ChangeItemsOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        if (target.ChangedItems != null)
        {
            writer.Put(true);
            writer.Put(target.ChangedItems.Count);
            for (var i = 0; i < target.ChangedItems.Count; i++)
            {
                writer.PutEFTItemInfoDescriptor(target.ChangedItems[i]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.RemovedItems != null)
        {
            writer.Put(true);
            writer.Put(target.RemovedItems.Count);
            for (var j = 0; j < target.RemovedItems.Count; j++)
            {
                writer.PutMongoID(target.RemovedItems[j]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.MovedItems != null)
        {
            writer.Put(true);
            writer.Put(target.MovedItems.Count);
            foreach (var keyValuePair in target.MovedItems)
            {
                writer.PutMongoID(keyValuePair.Key);
                writer.PutPolymorph(keyValuePair.Value);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.NewItems != null)
        {
            writer.Put(true);
            writer.Put(target.NewItems.Count);
            for (var k = 0; k < target.NewItems.Count; k++)
            {
                writer.PutEFTNestedItemDescriptor(target.NewItems[k]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.InternalOperationDescriptor != null)
        {
            writer.Put(true);
            writer.PutPolymorph(target.InternalOperationDescriptor);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ChangeItemsOperationDescriptor 对象。
    /// </summary>
    public static ChangeItemsOperationDescriptor GetEFTInventoryLogicOperationsChangeItemsOperationDescriptor(this NetDataReader reader)
    {
        var gclass = new ChangeItemsOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            gclass.ChangedItems = new Il2CppSystem.Collections.Generic.List<ItemInfoDescriptor>(num);
            for (var i = 0; i < num; i++)
            {
                gclass.ChangedItems.Add(reader.GetEFTItemInfoDescriptor());
            }
        }
        if (reader.GetBool())
        {
            var num2 = reader.GetInt();
            gclass.RemovedItems = new Il2CppSystem.Collections.Generic.List<MongoID>(num2);
            for (var j = 0; j < num2; j++)
            {
                gclass.RemovedItems.Add(reader.GetMongoID());
            }
        }
        if (reader.GetBool())
        {
            var num3 = reader.GetInt();
            gclass.MovedItems = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, ItemAddressDescriptor>();
            for (var k = 0; k < num3; k++)
            {
                gclass.MovedItems[reader.GetMongoID()] = reader.GetPolymorph<ItemAddressDescriptor>();
            }
        }
        if (reader.GetBool())
        {
            var num4 = reader.GetInt();
            gclass.NewItems = new Il2CppSystem.Collections.Generic.List<NestedItemDescriptor>(num4);
            for (var l = 0; l < num4; l++)
            {
                gclass.NewItems.Add(reader.GetEFTNestedItemDescriptor());
            }
        }
        if (reader.GetBool())
        {
            gclass.InternalOperationDescriptor = reader.GetPolymorph<InventoryOperationDescriptor>();
        }
        return gclass;
    }

    /// <summary>
    /// 将愿望单物品分类更改操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsChangeWishlistItemCategoryOperationDescriptor(this NetDataWriter writer, ChangeWishlistItemCategoryOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.TemplateId);
        writer.PutEnum(target.GroupType);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ChangeWishlistItemCategoryOperationDescriptor 对象。
    /// </summary>
    public static ChangeWishlistItemCategoryOperationDescriptor GetEFTInventoryLogicOperationsChangeWishlistItemCategoryOperationDescriptor(this NetDataReader reader)
    {
        return new ChangeWishlistItemCategoryOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            TemplateId = reader.GetMongoID(),
            GroupType = reader.GetEnum<EWishlistGroup>()
        };
    }

    /// <summary>
    /// 将商人服务购买操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsPurchaseTraderServiceOperationDescriptor(this NetDataWriter writer, PurchaseTraderServiceOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutEnum(target.ServiceType);
        if (target.SubServiceId != null)
        {
            writer.Put(true);
            writer.Put(target.SubServiceId);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 PurchaseTraderServiceOperationDescriptor 对象。
    /// </summary>
    public static PurchaseTraderServiceOperationDescriptor GetEFTInventoryLogicOperationsPurchaseTraderServiceOperationDescriptor(this NetDataReader reader)
    {
        var gclass = new PurchaseTraderServiceOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ServiceType = reader.GetEnum<ETraderServiceType>()
        };
        if (reader.GetBool())
        {
            gclass.SubServiceId = reader.GetString();
        }
        return gclass;
    }

    /// <summary>
    /// 将章节条件阅读操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsReadChapterConditionsOperationDescriptor(this NetDataWriter writer, ReadChapterConditionsOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ChapterId);
        writer.PutEnum(target.Type);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ReadChapterConditionsOperationDescriptor 对象。
    /// </summary>
    public static ReadChapterConditionsOperationDescriptor GetEFTInventoryLogicOperationsReadChapterConditionsOperationDescriptor(this NetDataReader reader)
    {
        return new ReadChapterConditionsOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ChapterId = reader.GetMongoID(),
            Type = reader.GetEnum<ReadChapterConditionsOperation.EChapterConditionType>()
        };
    }

    /// <summary>
    /// 将单条任务条件阅读操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsReadQuestConditionOperationDescriptor(this NetDataWriter writer, ReadQuestConditionOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ConditionId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ReadQuestConditionOperationDescriptor 对象。
    /// </summary>
    public static ReadQuestConditionOperationDescriptor GetEFTInventoryLogicOperationsReadQuestConditionOperationDescriptor(this NetDataReader reader)
    {
        return new ReadQuestConditionOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ConditionId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将任务便签链接阅读操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsReadQuestLinkOperationDescriptor(this NetDataWriter writer, ReadQuestLinkOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.LinkId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ReadQuestLinkOperationDescriptor 对象。
    /// </summary>
    public static ReadQuestLinkOperationDescriptor GetEFTInventoryLogicOperationsReadQuestLinkOperationDescriptor(this NetDataReader reader)
    {
        return new ReadQuestLinkOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            LinkId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将任务便签阅读操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsReadQuestNoteOperationDescriptor(this NetDataWriter writer, ReadQuestNoteOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.NoteId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ReadQuestNoteOperationDescriptor 对象。
    /// </summary>
    public static ReadQuestNoteOperationDescriptor GetEFTInventoryLogicOperationsReadQuestNoteOperationDescriptor(this NetDataReader reader)
    {
        return new ReadQuestNoteOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            NoteId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将愿望单移除操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsRemoveFromWishlistOperationDescriptor(this NetDataWriter writer, RemoveFromWishlistOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.TemplateId);
        writer.PutEnum(target.GroupType);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 RemoveFromWishlistOperationDescriptor 对象。
    /// </summary>
    public static RemoveFromWishlistOperationDescriptor GetEFTInventoryLogicOperationsRemoveFromWishlistOperationDescriptor(this NetDataReader reader)
    {
        return new RemoveFromWishlistOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            TemplateId = reader.GetMongoID(),
            GroupType = reader.GetEnum<EWishlistGroup>()
        };
    }

    /// <summary>
    /// 将容器内容搜索操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsSearchContentOperationDescriptor(this NetDataWriter writer, SearchContentOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SearchContentOperationDescriptor 对象。
    /// </summary>
    public static SearchContentOperationDescriptor GetEFTInventoryLogicOperationsSearchContentOperationDescriptor(this NetDataReader reader)
    {
        return new SearchContentOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将容器内容搜索子操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsSearchSuboperationDescriptor(this NetDataWriter writer, SearchSuboperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.SearchedItem);
        if (target.Content != null)
        {
            writer.Put(true);
            writer.Put(target.Content.Count);
            for (var i = 0; i < target.Content.Count; i++)
            {
                writer.PutEFTNestedItemDescriptor(target.Content[i]);
            }
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.Instant);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SearchSuboperationDescriptor 对象。
    /// </summary>
    public static SearchSuboperationDescriptor GetEFTInventoryLogicOperationsSearchSuboperationDescriptor(this NetDataReader reader)
    {
        var gclass = new SearchSuboperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            SearchedItem = reader.GetMongoID()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            gclass.Content = new Il2CppSystem.Collections.Generic.List<NestedItemDescriptor>(num);
            for (var i = 0; i < num; i++)
            {
                gclass.Content.Add(reader.GetEFTNestedItemDescriptor());
            }
        }
        gclass.Instant = reader.GetBool();
        return gclass;
    }

    /// <summary>
    /// 将item split to nowhere 操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsSplitToNowhereDescriptor(this NetDataWriter writer, SplitToNowhereDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
        writer.Put(target.Count);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SplitToNowhereDescriptor 对象。
    /// </summary>
    public static SplitToNowhereDescriptor GetEFTInventoryLogicOperationsSplitToNowhereDescriptor(this NetDataReader reader)
    {
        return new SplitToNowhereDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID(),
            Count = reader.GetUInt()
        };
    }

    /// <summary>
    /// 将item transfer from nowhere 操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsTransferFromNowhereDescriptor(this NetDataWriter writer, TransferFromNowhereDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
        writer.Put(target.Count);
        writer.Put(target.SpawnedInSession);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TransferFromNowhereDescriptor 对象。
    /// </summary>
    public static TransferFromNowhereDescriptor GetEFTInventoryLogicOperationsTransferFromNowhereDescriptor(this NetDataReader reader)
    {
        return new TransferFromNowhereDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID(),
            Count = reader.GetUInt(),
            SpawnedInSession = reader.GetBool()
        };
    }

    /// <summary>
    /// 将库存物品描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTItemDescriptor(this NetDataWriter writer, ItemDescriptor target)
    {
        writer.PutMongoID(target.Id);
        writer.PutMongoID(target.TemplateId);
        writer.Put(target.StackCount);
        writer.Put(target.SpawnedInSession);
        writer.Put(target.ActiveCamora);
        writer.Put(target.IsUnderBarrelDeviceActive);
        if (target.Malfunction != null)
        {
            writer.Put(true);
            writer.PutEFTMalfunctionDescriptor(target.Malfunction);
        }
        else
        {
            writer.Put(false);
        }
        if (target.UnsearchedInfo != null)
        {
            writer.Put(true);
            writer.PutEFTItemInfoDescriptor(target.UnsearchedInfo);
        }
        else
        {
            writer.Put(false);
        }
        if (target.Components != null)
        {
            writer.Put(true);
            writer.Put(target.Components.Count);
            for (var i = 0; i < target.Components.Count; i++)
            {
                writer.PutPolymorph(target.Components[i]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.Slots != null)
        {
            writer.Put(true);
            writer.Put(target.Slots.Count);
            for (var j = 0; j < target.Slots.Count; j++)
            {
                writer.PutEFTSlotDescriptor(target.Slots[j]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.ShellsInWeapon != null)
        {
            writer.Put(true);
            writer.Put(target.ShellsInWeapon.Count);
            for (var k = 0; k < target.ShellsInWeapon.Count; k++)
            {
                writer.PutEFTShellTemplateDescriptor(target.ShellsInWeapon[k]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.Grids != null)
        {
            writer.Put(true);
            writer.Put(target.Grids.Count);
            for (var l = 0; l < target.Grids.Count; l++)
            {
                writer.PutEFTGridDescriptor(target.Grids[l]);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.StackSlots != null)
        {
            writer.Put(true);
            writer.Put(target.StackSlots.Count);
            for (var m = 0; m < target.StackSlots.Count; m++)
            {
                writer.PutEFTStackSlotDescriptor(target.StackSlots[m]);
            }
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ItemDescriptor 对象。
    /// </summary>
    public static ItemDescriptor GetEFTItemDescriptor(this NetDataReader reader)
    {
        var inventoryDescriptorClass = new ItemDescriptor
        {
            Id = reader.GetMongoID(),
            TemplateId = reader.GetMongoID(),
            StackCount = reader.GetInt(),
            SpawnedInSession = reader.GetBool(),
            ActiveCamora = reader.GetByte(),
            IsUnderBarrelDeviceActive = reader.GetBool()
        };
        if (reader.GetBool())
        {
            inventoryDescriptorClass.Malfunction = reader.GetEFTMalfunctionDescriptor();
        }
        if (reader.GetBool())
        {
            inventoryDescriptorClass.UnsearchedInfo = reader.GetEFTItemInfoDescriptor();
        }
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            inventoryDescriptorClass.Components = new Il2CppSystem.Collections.Generic.List<ItemComponentDescriptor>(num);
            for (var i = 0; i < num; i++)
            {
                inventoryDescriptorClass.Components.Add(reader.GetPolymorph<ItemComponentDescriptor>());
            }
        }
        if (reader.GetBool())
        {
            var num2 = reader.GetInt();
            inventoryDescriptorClass.Slots = new Il2CppSystem.Collections.Generic.List<SlotDescriptor>(num2);
            for (var j = 0; j < num2; j++)
            {
                inventoryDescriptorClass.Slots.Add(reader.GetEFTSlotDescriptor());
            }
        }
        if (reader.GetBool())
        {
            var num3 = reader.GetInt();
            inventoryDescriptorClass.ShellsInWeapon = new Il2CppSystem.Collections.Generic.List<ShellTemplateDescriptor>(num3);
            for (var k = 0; k < num3; k++)
            {
                inventoryDescriptorClass.ShellsInWeapon.Add(reader.GetEFTShellTemplateDescriptor());
            }
        }
        if (reader.GetBool())
        {
            var num4 = reader.GetInt();
            inventoryDescriptorClass.Grids = new Il2CppSystem.Collections.Generic.List<GridDescriptor>(num4);
            for (var l = 0; l < num4; l++)
            {
                inventoryDescriptorClass.Grids.Add(reader.GetEFTGridDescriptor());
            }
        }
        if (reader.GetBool())
        {
            var num5 = reader.GetInt();
            inventoryDescriptorClass.StackSlots = new Il2CppSystem.Collections.Generic.List<StackSlotDescriptor>(num5);
            for (var m = 0; m < num5; m++)
            {
                inventoryDescriptorClass.StackSlots.Add(reader.GetEFTStackSlotDescriptor());
            }
        }
        return inventoryDescriptorClass;
    }

    /// <summary>
    /// 将基础物品信息参数序列化写入 writer 流。
    /// </summary>
    public static void PutEFTItemInfoDescriptor(this NetDataWriter writer, ItemInfoDescriptor target)
    {
        writer.PutMongoID(target.Id);
        writer.Put(target.Hash);
        writer.Put(target.Width);
        writer.Put(target.Height);
        writer.Put(target.TotalWeight);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ItemInfoDescriptor 对象。
    /// </summary>
    public static ItemInfoDescriptor GetEFTItemInfoDescriptor(this NetDataReader reader)
    {
        return new ItemInfoDescriptor
        {
            Id = reader.GetMongoID(),
            Hash = reader.GetInt(),
            Width = reader.GetShort(),
            Height = reader.GetShort(),
            TotalWeight = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将描述父级网格布局内物品映射关系序列化写入 writer 流。
    /// </summary>
    public static void PutEFTItemInGridDescriptor(this NetDataWriter writer, ItemInGridDescriptor target)
    {
        writer.PutEFTItemDescriptor(target.Item);
        writer.Put(target.X);
        writer.Put(target.Y);
        writer.Put(target.Horizontal);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ItemInGridDescriptor 对象。
    /// </summary>
    public static ItemInGridDescriptor GetEFTItemInGridDescriptor(this NetDataReader reader)
    {
        return new ItemInGridDescriptor
        {
            Item = reader.GetEFTItemDescriptor(),
            X = reader.GetByte(),
            Y = reader.GetByte(),
            Horizontal = reader.GetBool()
        };
    }

    /// <summary>
    /// 将详细的尸体物品状态与可视变换元数据映射序列化写入 writer 流。
    /// </summary>
    public static void PutEFTJsonCorpseDescriptor(this NetDataWriter writer, JsonCorpseDescriptor target)
    {
        writer.Put(target.Customization.Count);
        foreach (var keyValuePair in target.Customization)
        {
            writer.Put(keyValuePair.Key);
            writer.PutMongoID(keyValuePair.Value);
        }
        writer.PutEnum(target.Side);
        writer.Put(target.Bones.Length);
        for (var i = 0; i < target.Bones.Length; i++)
        {
            writer.PutClassTransformSync(target.Bones[i]);
        }
        writer.Put(target.PlayerProfileID);
        writer.Put(target.IsZombieCorpse);
        writer.Put(target.Id);
        writer.PutClassVector3(target.Position);
        writer.PutClassVector3(target.Rotation);
        writer.PutEFTItemDescriptor(target.Item);
        if (target.ValidProfiles != null)
        {
            writer.Put(true);
            writer.Put(target.ValidProfiles.Length);
            for (var j = 0; j < target.ValidProfiles.Length; j++)
            {
                writer.PutMongoID(target.ValidProfiles[j]);
            }
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.IsContainer);
        writer.Put(target.UseGravity);
        writer.Put(target.RandomRotation);
        writer.PutClassVector3(target.Shift);
        writer.Put(target.PlatformId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 JsonCorpseDescriptor 对象。
    /// </summary>
    public static JsonCorpseDescriptor GetEFTJsonCorpseDescriptor(this NetDataReader reader)
    {
        var gclass = new JsonCorpseDescriptor();
        var num = reader.GetInt();
        gclass.Customization = new Il2CppSystem.Collections.Generic.Dictionary<int, MongoID>();
        for (var i = 0; i < num; i++)
        {
            gclass.Customization[reader.GetInt()] = reader.GetMongoID();
        }
        gclass.Side = reader.GetEnum<EPlayerSide>();
        var num2 = reader.GetInt();
        gclass.Bones = new ClassTransformSync[num2];
        for (var j = 0; j < num2; j++)
        {
            gclass.Bones[j] = reader.GetClassTransformSync();
        }
        gclass.PlayerProfileID = reader.GetString();
        gclass.IsZombieCorpse = reader.GetBool();
        gclass.Id = reader.GetString();
        gclass.Position = reader.GetClassVector3();
        gclass.Rotation = reader.GetClassVector3();
        gclass.Item = reader.GetEFTItemDescriptor();
        if (reader.GetBool())
        {
            var num3 = reader.GetInt();
            gclass.ValidProfiles = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MongoID>(num3);
            for (var k = 0; k < num3; k++)
            {
                gclass.ValidProfiles[k] = reader.GetMongoID();
            }
        }
        gclass.IsContainer = reader.GetBool();
        gclass.UseGravity = reader.GetBool();
        gclass.RandomRotation = reader.GetBool();
        gclass.Shift = reader.GetClassVector3();
        gclass.PlatformId = reader.GetShort();
        return gclass;
    }

    /// <summary>
    /// 将散落的世界战利品物品实例描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTJsonLootItemDescriptor(this NetDataWriter writer, JsonLootItemDescriptor target)
    {
        writer.Put(target.Id);
        writer.PutClassVector3(target.Position);
        writer.PutClassVector3(target.Rotation);
        writer.PutEFTItemDescriptor(target.Item);
        if (target.ValidProfiles != null)
        {
            writer.Put(true);
            writer.Put(target.ValidProfiles.Length);
            for (var i = 0; i < target.ValidProfiles.Length; i++)
            {
                writer.PutMongoID(target.ValidProfiles[i]);
            }
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.IsContainer);
        writer.Put(target.UseGravity);
        writer.Put(target.RandomRotation);
        writer.PutClassVector3(target.Shift);
        writer.Put(target.PlatformId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 JsonLootItemDescriptor 对象。
    /// </summary>
    public static JsonLootItemDescriptor GetEFTJsonLootItemDescriptor(this NetDataReader reader)
    {
        var gclass = new JsonLootItemDescriptor
        {
            Id = reader.GetString(),
            Position = reader.GetClassVector3(),
            Rotation = reader.GetClassVector3(),
            Item = reader.GetEFTItemDescriptor()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            // MongoID 是以引用包装形式暴露的原生值类型。
            // 转换未填充的 CLR 数组时会尝试对其空条目进行拆箱。
            gclass.ValidProfiles = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MongoID>(num);
            for (var i = 0; i < num; i++)
            {
                gclass.ValidProfiles[i] = reader.GetMongoID();
            }
        }
        gclass.IsContainer = reader.GetBool();
        gclass.UseGravity = reader.GetBool();
        gclass.RandomRotation = reader.GetBool();
        gclass.Shift = reader.GetClassVector3();
        gclass.PlatformId = reader.GetShort();
        return gclass;
    }

    /// <summary>
    /// 将钥匙组件描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTKeyComponentDescriptor(this NetDataWriter writer, KeyComponentDescriptor target)
    {
        writer.Put(target.NumberOfUsages);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 KeyComponentDescriptor 对象。
    /// </summary>
    public static KeyComponentDescriptor GetEFTKeyComponentDescriptor(this NetDataReader reader)
    {
        return new KeyComponentDescriptor
        {
            NumberOfUsages = reader.GetInt()
        };
    }

    /// <summary>
    /// 将照明/战术设备组件描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTLightComponentDescriptor(this NetDataWriter writer, LightComponentDescriptor target)
    {
        writer.Put(target.IsActive);
        writer.Put(target.SelectedMode);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 LightComponentDescriptor 对象。
    /// </summary>
    public static LightComponentDescriptor GetEFTLightComponentDescriptor(this NetDataReader reader)
    {
        return new LightComponentDescriptor
        {
            IsActive = reader.GetBool(),
            SelectedMode = reader.GetInt()
        };
    }

    /// <summary>
    /// 将弹匣装填操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTLoadMagOperationDescriptor(this NetDataWriter writer, LoadMagOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutPolymorph(target.InternalOperationDescriptor);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 LoadMagOperationDescriptor 对象。
    /// </summary>
    public static LoadMagOperationDescriptor GetEFTLoadMagOperationDescriptor(this NetDataReader reader)
    {
        return new LoadMagOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            InternalOperationDescriptor = reader.GetPolymorph<InventoryOperationDescriptor>()
        };
    }

    /// <summary>
    /// 将可上锁组件描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTLockableComponentDescriptor(this NetDataWriter writer, LockableComponentDescriptor target)
    {
        writer.Put(target.Locked);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 LockableComponentDescriptor 对象。
    /// </summary>
    public static LockableComponentDescriptor GetEFTLockableComponentDescriptor(this NetDataReader reader)
    {
        return new LockableComponentDescriptor
        {
            Locked = reader.GetBool()
        };
    }

    /// <summary>
    /// 将全局战利品数据描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTLootDataDescriptor(this NetDataWriter writer, LootDataDescriptor target)
    {
        writer.Put(target.Items.Count);
        for (var i = 0; i < target.Items.Count; i++)
        {
            writer.PutPolymorph(target.Items[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 LootDataDescriptor 对象。
    /// </summary>
    public static LootDataDescriptor GetEFTLootDataDescriptor(this NetDataReader reader)
    {
        var gclass = new LootDataDescriptor();
        var num = reader.GetInt();
        gclass.Items = new Il2CppSystem.Collections.Generic.List<JsonLootItemDescriptor>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.Items.Add(reader.GetPolymorph<JsonLootItemDescriptor>());
        }
        return gclass;
    }

    /// <summary>
    /// 将详细的武器故障描述符结构序列化写入 writer 流。
    /// </summary>
    public static void PutEFTMalfunctionDescriptor(this NetDataWriter writer, MalfunctionDescriptor target)
    {
        writer.Put(target.Malfunction);
        writer.Put(target.LastShotOverheat);
        writer.Put(target.LastShotTime);
        writer.Put(target.SlideOnOverheatReached);
        writer.Put(target.PlayersWhoKnowAboutMalfunction.Count);
        for (var i = 0; i < target.PlayersWhoKnowAboutMalfunction.Count; i++)
        {
            writer.Put(target.PlayersWhoKnowAboutMalfunction[i]);
        }
        writer.Put(target.PlayersWhoKnowMalfType.Count);
        for (var j = 0; j < target.PlayersWhoKnowMalfType.Count; j++)
        {
            writer.Put(target.PlayersWhoKnowMalfType[j]);
        }
        writer.Put(target.PlayersReducedMalfChances.Count);
        foreach (var keyValuePair in target.PlayersReducedMalfChances)
        {
            writer.Put(keyValuePair.Key);
            writer.Put(keyValuePair.Value);
        }
        var ammoToFire = NativeNullable<MongoID>.Read(target, nameof(MalfunctionDescriptor.AmmoToFireTemplateId));
        var ammoToLoad = NativeNullable<MongoID>.Read(target, nameof(MalfunctionDescriptor.AmmoWillBeLoadedToChamberTemplateId));
        var ammoMalfunctioned = NativeNullable<MongoID>.Read(target, nameof(MalfunctionDescriptor.AmmoMalfunctionedTemplateId));
        if (ammoToFire.HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(ammoToFire.Value);
        }
        else
        {
            writer.Put(false);
        }
        if (ammoToLoad.HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(ammoToLoad.Value);
        }
        else
        {
            writer.Put(false);
        }
        if (ammoMalfunctioned.HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(ammoMalfunctioned.Value);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 MalfunctionDescriptor 对象。
    /// </summary>
    public static MalfunctionDescriptor GetEFTMalfunctionDescriptor(this NetDataReader reader)
    {
        var gclass = new MalfunctionDescriptor
        {
            Malfunction = reader.GetByte(),
            LastShotOverheat = reader.GetFloat(),
            LastShotTime = reader.GetFloat(),
            SlideOnOverheatReached = reader.GetBool()
        };
        var num = reader.GetInt();
        gclass.PlayersWhoKnowAboutMalfunction = new Il2CppSystem.Collections.Generic.List<int>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.PlayersWhoKnowAboutMalfunction.Add(reader.GetInt());
        }
        var num2 = reader.GetInt();
        gclass.PlayersWhoKnowMalfType = new Il2CppSystem.Collections.Generic.List<int>(num2);
        for (var j = 0; j < num2; j++)
        {
            gclass.PlayersWhoKnowMalfType.Add(reader.GetInt());
        }
        var num3 = reader.GetInt();
        gclass.PlayersReducedMalfChances = new Il2CppSystem.Collections.Generic.Dictionary<int, byte>();
        for (var k = 0; k < num3; k++)
        {
            gclass.PlayersReducedMalfChances[reader.GetInt()] = reader.GetByte();
        }
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(gclass, nameof(MalfunctionDescriptor.AmmoToFireTemplateId), new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(gclass, nameof(MalfunctionDescriptor.AmmoWillBeLoadedToChamberTemplateId), new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(gclass, nameof(MalfunctionDescriptor.AmmoMalfunctionedTemplateId), new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        return gclass;
    }

    /// <summary>
    /// 将地图物品组件的标记序列化写入 writer 流。
    /// </summary>
    public static void PutEFTMapComponentDescriptor(this NetDataWriter writer, MapComponentDescriptor target)
    {
        writer.Put(target.Markers.Count);
        for (var i = 0; i < target.Markers.Count; i++)
        {
            writer.PutEFTInventoryLogicMapMarker(target.Markers[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 MapComponentDescriptor 对象。
    /// </summary>
    public static MapComponentDescriptor GetEFTMapComponentDescriptor(this NetDataReader reader)
    {
        var gclass = new MapComponentDescriptor();
        var num = reader.GetInt();
        gclass.Markers = new Il2CppSystem.Collections.Generic.List<MapMarker>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.Markers.Add(reader.GetEFTInventoryLogicMapMarker());
        }
        return gclass;
    }

    /// <summary>
    /// 将医疗包组件资源状态序列化写入 writer 流。
    /// </summary>
    public static void PutEFTMedKitComponentDescriptor(this NetDataWriter writer, MedKitComponentDescriptor target)
    {
        writer.Put(target.HpResource);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 MedKitComponentDescriptor 对象。
    /// </summary>
    public static MedKitComponentDescriptor GetEFTMedKitComponentDescriptor(this NetDataReader reader)
    {
        return new MedKitComponentDescriptor
        {
            HpResource = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将物品堆叠合并操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTMergeOperationDescriptor(this NetDataWriter writer, MergeOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.Item1Id);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 MergeOperationDescriptor 对象。
    /// </summary>
    public static MergeOperationDescriptor GetEFTMergeOperationDescriptor(this NetDataReader reader)
    {
        return new MergeOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            Item1Id = reader.GetString()
        };
    }

    /// <summary>
    /// 将物品库存结构移动操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTMoveOperationDescriptor(this NetDataWriter writer, MoveOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.PutPolymorph(target.From);
        writer.PutPolymorph(target.To);
        if (target.DestroyedItems != null)
        {
            writer.Put(true);
            writer.Put(target.DestroyedItems.Count);
            for (var i = 0; i < target.DestroyedItems.Count; i++)
            {
                writer.PutEFTDestroyedItem(target.DestroyedItems[i]);
            }
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 MoveOperationDescriptor 对象。
    /// </summary>
    public static MoveOperationDescriptor GetEFTMoveOperationDescriptor(this NetDataReader reader)
    {
        var moveDescriptorClass = new MoveOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            From = reader.GetPolymorph<ItemAddressDescriptor>(),
            To = reader.GetPolymorph<ItemAddressDescriptor>()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            moveDescriptorClass.DestroyedItems = new Il2CppSystem.Collections.Generic.List<DestroyedItem>(num);
            for (var i = 0; i < num; i++)
            {
                moveDescriptorClass.DestroyedItems.Add(reader.GetEFTDestroyedItem());
            }
        }
        return moveDescriptorClass;
    }

    /// <summary>
    /// 将嵌套物品层级引用及其父级地址上下文的映射序列化写入 writer 流。
    /// </summary>
    public static void PutEFTNestedItemDescriptor(this NetDataWriter writer, NestedItemDescriptor target)
    {
        writer.PutPolymorph(target.Address);
        writer.PutEFTItemDescriptor(target.Item);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 NestedItemDescriptor 对象。
    /// </summary>
    public static NestedItemDescriptor GetEFTNestedItemDescriptor(this NetDataReader reader)
    {
        return new NestedItemDescriptor
        {
            Address = reader.GetPolymorph<ItemAddressDescriptor>(),
            Item = reader.GetEFTItemDescriptor()
        };
    }

    /// <summary>
    /// 将玩家档案上下文便签管理器数据数组序列化写入 writer 流。
    /// </summary>
    public static void PutEFTNotesNotesManagerNotesDescriptor(this NetDataWriter writer, NotesManager.NotesDescriptor target)
    {
        writer.Put(target.Notes.Length);
        for (var i = 0; i < target.Notes.Length; i++)
        {
            writer.PutEFTNotesNote(target.Notes[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 NotesManager.NotesDescriptor 对象。
    /// </summary>
    public static NotesManager.NotesDescriptor GetEFTNotesNotesManagerNotesDescriptor(this NetDataReader reader)
    {
        var gclass = new NotesManager.NotesDescriptor();
        var num = reader.GetInt();
        gclass.Notes = new Note[num];
        for (var i = 0; i < num; i++)
        {
            gclass.Notes[i] = reader.GetEFTNotesNote();
        }
        return gclass;
    }

    /// <summary>
    /// 将固定武器交互/操作上下文描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTOperateStationaryWeaponOperationDescriptor(this NetDataWriter writer, OperateStationaryWeaponOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.WeaponId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 OperateStationaryWeaponOperationDescriptor 对象。
    /// </summary>
    public static OperateStationaryWeaponOperationDescriptor GetEFTOperateStationaryWeaponOperationDescriptor(this NetDataReader reader)
    {
        return new OperateStationaryWeaponOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            WeaponId = reader.GetString()
        };
    }

    /// <summary>
    /// 将库存基础根容器自身归属追踪描述符引用序列化写入 writer 流。
    /// </summary>
    public static void PutEFTOwnerItselfDescriptor(this NetDataWriter writer, OwnerItselfDescriptor target)
    {
        writer.PutEFTContainerDescriptor(target.Container);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 OwnerItselfDescriptor 对象。
    /// </summary>
    public static OwnerItselfDescriptor GetEFTOwnerItselfDescriptor(this NetDataReader reader)
    {
        return new OwnerItselfDescriptor
        {
            Container = reader.GetEFTContainerDescriptor()
        };
    }

    /// <summary>
    /// 将本地化的交互式绊雷部署操作序列化写入 writer 流。
    /// </summary>
    public static void PutEFTPlantTripwireOperationDescriptor(this NetDataWriter writer, PlantTripwireOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.TripwireId);
        if (NativeNullable<MongoID>.Read(target, "PlantingKitId").HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(NativeNullable<MongoID>.Read(target, "PlantingKitId").Value);
        }
        else
        {
            writer.Put(false);
        }
        writer.PutClassVector3(target.FromPosition);
        writer.PutClassVector3(target.ToPosition);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 PlantTripwireOperationDescriptor 对象。
    /// </summary>
    public static PlantTripwireOperationDescriptor GetEFTPlantTripwireOperationDescriptor(this NetDataReader reader)
    {
        var gclass = new PlantTripwireOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            TripwireId = reader.GetMongoID()
        };
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(gclass, "PlantingKitId", new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        gclass.FromPosition = reader.GetClassVector3();
        gclass.ToPosition = reader.GetClassVector3();
        return gclass;
    }

    /// <summary>
    /// 将完整的本地化玩家档案动态外观装备布局表示序列化写入 writer 流。
    /// </summary>
    public static void PutEFTPlayerVisualRepresentationDescriptor(this NetDataWriter writer, PlayerVisualRepresentationDescriptor target)
    {
        writer.PutJsonTypePlayerInfo(target.Info);
        if (target.Customization != null)
        {
            writer.Put(true);
            writer.Put(target.Customization.Count);
            foreach (var keyValuePair in target.Customization)
            {
                writer.PutEnum(keyValuePair.Key);
                writer.PutMongoID(keyValuePair.Value);
            }
        }
        else
        {
            writer.Put(false);
        }
        if (target.Equipment != null)
        {
            writer.Put(true);
            writer.PutEFTInventoryEquipmentDescriptor(target.Equipment);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 PlayerVisualRepresentationDescriptor 对象。
    /// </summary>
    public static PlayerVisualRepresentationDescriptor GetEFTPlayerVisualRepresentationDescriptor(this NetDataReader reader)
    {
        var gclass = new PlayerVisualRepresentationDescriptor
        {
            Info = reader.GetJsonTypePlayerInfo()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            gclass.Customization = new Il2CppSystem.Collections.Generic.Dictionary<EBodyModelPart, MongoID>();
            for (var i = 0; i < num; i++)
            {
                gclass.Customization[reader.GetEnum<EBodyModelPart>()] = reader.GetMongoID();
            }
        }
        if (reader.GetBool())
        {
            gclass.Equipment = reader.GetEFTInventoryEquipmentDescriptor();
        }
        return gclass;
    }

    /// <summary>
    /// 将中毒组件描述符状态序列化写入 writer 流。
    /// </summary>
    public static void PutEFTPoisonComponentDescriptor(this NetDataWriter writer, PoisonComponentDescriptor target)
    {
        writer.Put(target.Resource);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 PoisonComponentDescriptor 对象。
    /// </summary>
    public static PoisonComponentDescriptor GetEFTPoisonComponentDescriptor(this NetDataReader reader)
    {
        return new PoisonComponentDescriptor
        {
            Resource = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将声望状态校验数据点序列化写入 writer 流。
    /// </summary>
    public static void PutEFTPrestigePrestigeStatusData(this NetDataWriter writer, PrestigeStatusData target)
    {
        writer.PutMongoID(target.TemplateId);
        writer.Put(target.Timestamp);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 PrestigeStatusData 对象。
    /// </summary>
    public static PrestigeStatusData GetEFTPrestigePrestigeStatusData(this NetDataReader reader)
    {
        return new PrestigeStatusData
        {
            TemplateId = reader.GetMongoID(),
            Timestamp = reader.GetInt()
        };
    }

    /// <summary>
    /// 将完整的多部位档案健康数据矩阵序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileHealthInfo(this NetDataWriter writer, Profile.HealthInfo target)
    {
        writer.Put(target.BodyParts.Count);
        foreach (var keyValuePair in target.BodyParts)
        {
            writer.PutEnum(keyValuePair.Key);
            writer.PutEFTProfileHealthInfoBodyPartInfo(keyValuePair.Value);
        }
        writer.PutEFTProfileHealthInfoValueInfo(target.Energy);
        writer.PutEFTProfileHealthInfoValueInfo(target.Hydration);
        writer.PutEFTProfileHealthInfoValueInfo(target.Temperature);
        writer.PutEFTProfileHealthInfoValueInfo(target.Poison);
        if (NativeNullable<int>.Read(target, "UpdateTime").HasValue)
        {
            writer.Put(true);
            writer.Put(NativeNullable<int>.Read(target, "UpdateTime").Value);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Profile.HealthInfo 对象。
    /// </summary>
    public static Profile.HealthInfo GetEFTProfileHealthInfo(this NetDataReader reader)
    {
        var profileHealthClass = new Profile.HealthInfo();
        var num = reader.GetInt();
        profileHealthClass.BodyParts = new Il2CppSystem.Collections.Generic.Dictionary<EBodyPart, Profile.HealthInfo.BodyPartInfo>();
        for (var i = 0; i < num; i++)
        {
            profileHealthClass.BodyParts[reader.GetEnum<EBodyPart>()] = reader.GetEFTProfileHealthInfoBodyPartInfo();
        }
        profileHealthClass.Energy = reader.GetEFTProfileHealthInfoValueInfo();
        profileHealthClass.Hydration = reader.GetEFTProfileHealthInfoValueInfo();
        profileHealthClass.Temperature = reader.GetEFTProfileHealthInfoValueInfo();
        profileHealthClass.Poison = reader.GetEFTProfileHealthInfoValueInfo();
        if (reader.GetBool())
        {
            NativeNullable<int>.Write(profileHealthClass, "UpdateTime", new Il2CppSystem.Nullable<int>(reader.GetInt()));
        }
        return profileHealthClass;
    }

    /// <summary>
    /// 将目标身体部位的结构化生命值及生效状态副作用序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileHealthInfoBodyPartInfo(this NetDataWriter writer, Profile.HealthInfo.BodyPartInfo target)
    {
        writer.PutEFTProfileHealthInfoValueInfo(target.Health);
        writer.Put(target.Effects.Count);
        foreach (var keyValuePair in target.Effects)
        {
            writer.Put(keyValuePair.Key);
            writer.PutEFTProfileHealthInfoEffectInfo(keyValuePair.Value);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Profile.HealthInfo.BodyPartInfo 对象。
    /// </summary>
    public static Profile.HealthInfo.BodyPartInfo GetEFTProfileHealthInfoBodyPartInfo(this NetDataReader reader)
    {
        var profileBodyPartHealthClass = new Profile.HealthInfo.BodyPartInfo
        {
            Health = reader.GetEFTProfileHealthInfoValueInfo()
        };
        var num = reader.GetInt();
        profileBodyPartHealthClass.Effects = new Il2CppSystem.Collections.Generic.Dictionary<string, Profile.HealthInfo.EffectInfo>();
        for (var i = 0; i < num; i++)
        {
            profileBodyPartHealthClass.Effects[reader.GetString()] = reader.GetEFTProfileHealthInfoEffectInfo();
        }
        return profileBodyPartHealthClass;
    }

    /// <summary>
    /// 将目标健康异常或增益效果上下文持续时长元素序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileHealthInfoEffectInfo(this NetDataWriter writer, Profile.HealthInfo.EffectInfo target)
    {
        writer.Put(target.Time);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Profile.HealthInfo.EffectInfo 对象。
    /// </summary>
    public static Profile.HealthInfo.EffectInfo GetEFTProfileHealthInfoEffectInfo(this NetDataReader reader)
    {
        return new Profile.HealthInfo.EffectInfo
        {
            Time = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将精确的结构属性参数，包括最大阈值与外部伤害缩放系数序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileHealthInfoValueInfo(this NetDataWriter writer, Profile.HealthInfo.ValueInfo target)
    {
        writer.Put(target.Current);
        writer.Put(target.Minimum);
        writer.Put(target.Maximum);
        writer.Put(target.OverDamageReceivedMultiplier);
        writer.Put(target.EnvironmentDamageMultiplier);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Profile.HealthInfo.ValueInfo 对象。
    /// </summary>
    public static Profile.HealthInfo.ValueInfo GetEFTProfileHealthInfoValueInfo(this NetDataReader reader)
    {
        return new Profile.HealthInfo.ValueInfo
        {
            Current = reader.GetFloat(),
            Minimum = reader.GetFloat(),
            Maximum = reader.GetFloat(),
            OverDamageReceivedMultiplier = reader.GetFloat(),
            EnvironmentDamageMultiplier = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将安全交易档案经济转账上限与剩余间隔序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileMoneyTransferLimitData(this NetDataWriter writer, Profile.MoneyTransferLimitData target)
    {
        writer.Put(target.nextResetTime);
        writer.Put(target.remainingLimit);
        writer.Put(target.totalLimit);
        writer.Put(target.resetInterval);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Profile.MoneyTransferLimitData 对象。
    /// </summary>
    public static Profile.MoneyTransferLimitData GetEFTProfileMoneyTransferLimitData(this NetDataReader reader)
    {
        return new Profile.MoneyTransferLimitData
        {
            nextResetTime = reader.GetInt(),
            remainingLimit = reader.GetInt(),
            totalLimit = reader.GetInt(),
            resetInterval = reader.GetInt()
        };
    }

    /// <summary>
    /// 将档案配方解锁结构序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileUnlockedInfo(this NetDataWriter writer, Profile.UnlockedInfo target)
    {
        writer.Put(target.unlockedSchemeList.Count);
        for (var i = 0; i < target.unlockedSchemeList.Count; i++)
        {
            writer.PutMongoID(target.unlockedSchemeList[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 Profile.UnlockedInfo 对象。
    /// </summary>
    public static Profile.UnlockedInfo GetEFTProfileUnlockedInfo(this NetDataReader reader)
    {
        var gclass = new Profile.UnlockedInfo();
        var num = reader.GetInt();
        gclass.unlockedSchemeList = new Il2CppSystem.Collections.Generic.List<MongoID>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.unlockedSchemeList.Add(reader.GetMongoID());
        }
        return gclass;
    }

    /// <summary>
    /// 将档案账号审核封禁条目状态序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileBanDescriptor(this NetDataWriter writer, ProfileBanDescriptor target)
    {
        writer.PutEnum(target.Type);
        writer.Put(target.ExpirationTime);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ProfileBanDescriptor 对象。
    /// </summary>
    public static ProfileBanDescriptor GetEFTProfileBanDescriptor(this NetDataReader reader)
    {
        return new ProfileBanDescriptor
        {
            Type = reader.GetEnum<EBanType>(),
            ExpirationTime = reader.GetLong()
        };
    }

    /// <summary>
    /// 将包含所有子系统的完整服务器档案上下文描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileDescriptor(this NetDataWriter writer, ProfileDescriptor target)
    {
        writer.PutMongoID(target.Id);
        writer.Put(target.AccountId);
        var petId = NativeNullable<MongoID>.Read(target, nameof(ProfileDescriptor.PetId));
        if (petId.HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(petId.Value);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.KarmaValue);
        writer.PutPolymorph(target.Info);
        writer.Put(target.Customization.Count);
        foreach (var keyValuePair in target.Customization)
        {
            writer.PutEnum(keyValuePair.Key);
            writer.PutMongoID(keyValuePair.Value);
        }
        if (target.Encyclopedia != null)
        {
            writer.Put(true);
            writer.Put(target.Encyclopedia.Count);
            foreach (var keyValuePair2 in target.Encyclopedia)
            {
                writer.PutMongoID(keyValuePair2.Key);
                writer.Put(keyValuePair2.Value);
            }
        }
        else writer.Put(false);
        if (target.Health != null)
        {
            writer.Put(true);
            writer.PutEFTProfileHealthInfo(target.Health);
        }
        else writer.Put(false);
        writer.PutEFTInventoryDescriptor(target.Inventory);
        writer.Put(target.InsuredItems.Length);
        for (var i = 0; i < target.InsuredItems.Length; i++) writer.PutJsonTypeInsuredProfileItems(target.InsuredItems[i]);
        writer.PutEFTSkillsDescriptor(target.Skills);
        writer.PutEFTNotesNotesManagerNotesDescriptor(target.Notes);
        writer.Put(target.TaskConditionCounters.Count);
        foreach (var keyValuePair3 in target.TaskConditionCounters)
        {
            writer.PutMongoID(keyValuePair3.Key);
            writer.PutEFTTaskConditionCounterDescriptor(keyValuePair3.Value);
        }
        writer.Put(target.QuestsData.Count);
        for (var j = 0; j < target.QuestsData.Count; j++) writer.PutEFTQuestsQuestStatusData(target.QuestsData[j]);
        writer.Put(target.AchievementsData.Count);
        foreach (var keyValuePair4 in target.AchievementsData)
        {
            writer.PutMongoID(keyValuePair4.Key);
            writer.Put(keyValuePair4.Value);
        }
        writer.Put(target.PrestigeData.Count);
        foreach (var keyValuePair5 in target.PrestigeData)
        {
            writer.PutMongoID(keyValuePair5.Key);
            writer.Put(keyValuePair5.Value);
        }
        writer.Put(target.VariableData.Count);
        foreach (var keyValuePair6 in target.VariableData)
        {
            writer.PutMongoID(keyValuePair6.Key);
            writer.Put(keyValuePair6.Value);
        }
        PutMongoIdSet(writer, target.PerkIds);
        PutOptionalNativeJson(writer, target.PerkEffectParameters);
        PutSeasonalRewards(writer, target.SeasonalRewards);
        writer.PutEFTProfileMoneyTransferLimitData(target.TransferLimitData);
        PutBattlePassDocumentLimits(writer, target.BattlePassDocumentLimitData);
        writer.Put(target.Bonuses?.Length ?? 0);
        if (target.Bonuses != null)
            for (var k = 0; k < target.Bonuses.Length; k++) writer.PutEFTBonusDescriptor(target.Bonuses[k]);
        PutOptionalNativeJson(writer, target.Hideout);
        PutOptionalNativeJson(writer, target.RagfairInfo);
        writer.PutEFTProfileUnlockedInfo(target.UnlockedRecipeInfo);
        PutMongoIdByteDictionary(writer, target.WishList);
        writer.PutEFTProfileStatsSeparatorDescriptor(target.Stats);
        PutMongoIdIntDictionary(writer, target.CheckedMagazines);
        PutMongoIdList(writer, target.CheckedChambers);
        PutTraderInfoDictionary(writer, target.TradersInfo);
        PutMongoIdBoolDictionary(writer, target.CompletableItems);
        PutMongoIdBoolDictionary(writer, target.QuestNotes);
        PutMongoIdSet(writer, target.ReadQuestData);
        PutOptionalNativeJson(writer, target.Ending);
        PutStringSet(writer, target.UnlockedLocations);
        writer.Put(target.UniversalDocuments);
        PutBattlePassProgress(writer, target.BattlePassProgress);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ProfileDescriptor 对象。
    /// </summary>
    public static ProfileDescriptor GetEFTProfileDescriptor(this NetDataReader reader)
    {
#pragma warning disable CS0162
        var completeProfileDescriptorClass = new ProfileDescriptor
        {
            Id = reader.GetMongoID(),
            AccountId = reader.GetString()
        };
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(completeProfileDescriptorClass, nameof(ProfileDescriptor.PetId), new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        completeProfileDescriptorClass.KarmaValue = reader.GetFloat();
        completeProfileDescriptorClass.Info = reader.GetPolymorph<ProfileInfoDescriptor>();
        var num = reader.GetInt();
        completeProfileDescriptorClass.Customization = new Il2CppSystem.Collections.Generic.Dictionary<EBodyModelPart, MongoID>();
        for (var i = 0; i < num; i++)
        {
            completeProfileDescriptorClass.Customization[reader.GetEnum<EBodyModelPart>()] = reader.GetMongoID();
        }
        if (reader.GetBool())
        {
            var num2 = reader.GetInt();
            completeProfileDescriptorClass.Encyclopedia = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, bool>();
            for (var j = 0; j < num2; j++)
            {
                completeProfileDescriptorClass.Encyclopedia[reader.GetMongoID()] = reader.GetBool();
            }
        }
        if (reader.GetBool())
        {
            completeProfileDescriptorClass.Health = reader.GetEFTProfileHealthInfo();
        }
        completeProfileDescriptorClass.Inventory = reader.GetEFTInventoryDescriptor();
        var num3 = reader.GetInt();
        completeProfileDescriptorClass.InsuredItems = new InsuredProfileItems[num3];
        for (var k = 0; k < num3; k++)
        {
            completeProfileDescriptorClass.InsuredItems[k] = reader.GetJsonTypeInsuredProfileItems();
        }
        completeProfileDescriptorClass.Skills = reader.GetEFTSkillsDescriptor();
        completeProfileDescriptorClass.Notes = reader.GetEFTNotesNotesManagerNotesDescriptor();
        var num4 = reader.GetInt();
        completeProfileDescriptorClass.TaskConditionCounters = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, TaskConditionCounterDescriptor>();
        for (var l = 0; l < num4; l++)
        {
            completeProfileDescriptorClass.TaskConditionCounters[reader.GetMongoID()] = reader.GetEFTTaskConditionCounterDescriptor();
        }
        var num5 = reader.GetInt();
        completeProfileDescriptorClass.QuestsData = new Il2CppSystem.Collections.Generic.List<QuestStatusData>(num5);
        for (var m = 0; m < num5; m++)
        {
            completeProfileDescriptorClass.QuestsData.Add(reader.GetEFTQuestsQuestStatusData());
        }
        var num6 = reader.GetInt();
        completeProfileDescriptorClass.AchievementsData = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, int>();
        for (var n = 0; n < num6; n++)
        {
            completeProfileDescriptorClass.AchievementsData[reader.GetMongoID()] = reader.GetInt();
        }
        var num7 = reader.GetInt();
        completeProfileDescriptorClass.PrestigeData = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, int>();
        for (var num8 = 0; num8 < num7; num8++)
        {
            completeProfileDescriptorClass.PrestigeData[reader.GetMongoID()] = reader.GetInt();
        }
        var num9 = reader.GetInt();
        completeProfileDescriptorClass.VariableData = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, int>();
        for (var num10 = 0; num10 < num9; num10++)
        {
            completeProfileDescriptorClass.VariableData[reader.GetMongoID()] = reader.GetInt();
        }
        completeProfileDescriptorClass.PerkIds = GetMongoIdSet(reader);
        completeProfileDescriptorClass.PerkEffectParameters = GetOptionalNativeJson<SeasonalPerkEffectParametersData>(reader);
        completeProfileDescriptorClass.SeasonalRewards = GetSeasonalRewards(reader);
        completeProfileDescriptorClass.TransferLimitData = reader.GetEFTProfileMoneyTransferLimitData();
        completeProfileDescriptorClass.BattlePassDocumentLimitData = GetBattlePassDocumentLimits(reader);
        var num11 = reader.GetInt();
        completeProfileDescriptorClass.Bonuses = new BonusDescriptor[num11];
        for (var num12 = 0; num12 < num11; num12++) completeProfileDescriptorClass.Bonuses[num12] = reader.GetEFTBonusDescriptor();
        completeProfileDescriptorClass.Hideout = GetOptionalNativeJson<HideoutInfo>(reader);
        completeProfileDescriptorClass.RagfairInfo = GetOptionalNativeJson<ProfileRagfairInfo>(reader);
        completeProfileDescriptorClass.UnlockedRecipeInfo = reader.GetEFTProfileUnlockedInfo();
        completeProfileDescriptorClass.WishList = GetMongoIdByteDictionary(reader);
        completeProfileDescriptorClass.Stats = reader.GetEFTProfileStatsSeparatorDescriptor();
        completeProfileDescriptorClass.CheckedMagazines = GetMongoIdIntDictionary(reader);
        completeProfileDescriptorClass.CheckedChambers = GetMongoIdList(reader);
        completeProfileDescriptorClass.TradersInfo = GetTraderInfoDictionary(reader);
        completeProfileDescriptorClass.CompletableItems = GetMongoIdBoolDictionary(reader);
        completeProfileDescriptorClass.QuestNotes = GetMongoIdBoolDictionary(reader);
        completeProfileDescriptorClass.ReadQuestData = GetMongoIdSet(reader);
        completeProfileDescriptorClass.Ending = GetOptionalNativeJson<EndingInfo>(reader);
        completeProfileDescriptorClass.UnlockedLocations = GetStringSet(reader);
        completeProfileDescriptorClass.UniversalDocuments = reader.GetInt();
        completeProfileDescriptorClass.BattlePassProgress = GetBattlePassProgress(reader);
        return completeProfileDescriptorClass;
#pragma warning restore CS0162
    }

    private static void PutOptionalNativeJson<T>(NetDataWriter writer, T value) where T : Il2CppObjectBase
    {
        writer.Put(value != null);
        if (value != null) writer.PutLargeString(FikaJson.Serialize(value));
    }

    private static T GetOptionalNativeJson<T>(NetDataReader reader) where T : Il2CppObjectBase
    {
        if (!reader.GetBool()) return null;
        var json = reader.GetLargeString();
        return string.IsNullOrEmpty(json) ? null : FikaJson.Deserialize<T>(json);
    }

    private static void PutMongoIdSet(NetDataWriter writer, Il2CppSystem.Collections.Generic.HashSet<MongoID> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var value in values) writer.PutMongoID(value);
    }

    private static Il2CppSystem.Collections.Generic.HashSet<MongoID> GetMongoIdSet(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.HashSet<MongoID>();
        for (var i = 0; i < count; i++) result.Add(reader.GetMongoID());
        return result;
    }

    private static void PutStringSet(NetDataWriter writer, Il2CppSystem.Collections.Generic.HashSet<string> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var value in values) writer.Put(value);
    }

    private static Il2CppSystem.Collections.Generic.HashSet<string> GetStringSet(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.HashSet<string>();
        for (var i = 0; i < count; i++) result.Add(reader.GetString());
        return result;
    }

    private static void PutMongoIdBoolDictionary(NetDataWriter writer, Il2CppSystem.Collections.Generic.Dictionary<MongoID, bool> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var pair in values) { writer.PutMongoID(pair.Key); writer.Put(pair.Value); }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<MongoID, bool> GetMongoIdBoolDictionary(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, bool>();
        for (var i = 0; i < count; i++) result[reader.GetMongoID()] = reader.GetBool();
        return result;
    }

    private static void PutMongoIdIntDictionary(NetDataWriter writer, Il2CppSystem.Collections.Generic.Dictionary<MongoID, int> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var pair in values) { writer.PutMongoID(pair.Key); writer.Put(pair.Value); }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<MongoID, int> GetMongoIdIntDictionary(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, int>();
        for (var i = 0; i < count; i++) result[reader.GetMongoID()] = reader.GetInt();
        return result;
    }

    private static void PutMongoIdByteDictionary(NetDataWriter writer, Il2CppSystem.Collections.Generic.Dictionary<MongoID, byte> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var pair in values) { writer.PutMongoID(pair.Key); writer.Put(pair.Value); }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<MongoID, byte> GetMongoIdByteDictionary(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, byte>();
        for (var i = 0; i < count; i++) result[reader.GetMongoID()] = reader.GetByte();
        return result;
    }

    private static void PutMongoIdList(NetDataWriter writer, Il2CppSystem.Collections.Generic.List<MongoID> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        for (var i = 0; i < values.Count; i++) writer.PutMongoID(values[i]);
    }

    private static Il2CppSystem.Collections.Generic.List<MongoID> GetMongoIdList(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.List<MongoID>(count);
        for (var i = 0; i < count; i++) result.Add(reader.GetMongoID());
        return result;
    }

    private static void PutBattlePassDocumentLimits(NetDataWriter writer, Il2CppSystem.Collections.Generic.Dictionary<MongoID, Profile.BattlePassDocumentLimitData> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var pair in values)
        {
            writer.PutMongoID(pair.Key);
            var value = pair.Value;
            writer.Put(value != null);
            if (value == null) continue;
            writer.Put(value.NextResetTime);
            writer.Put(value.RemainingLimit);
            writer.Put(value.TotalLimit);
            writer.Put(value.ResetInterval);
        }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<MongoID, Profile.BattlePassDocumentLimitData> GetBattlePassDocumentLimits(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, Profile.BattlePassDocumentLimitData>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetMongoID();
            if (!reader.GetBool()) { result[id] = null; continue; }
            result[id] = new Profile.BattlePassDocumentLimitData
            {
                NextResetTime = reader.GetInt(),
                RemainingLimit = reader.GetInt(),
                TotalLimit = reader.GetInt(),
                ResetInterval = reader.GetInt()
            };
        }
        return result;
    }

    private static void PutSeasonalRewards(NetDataWriter writer, Il2CppSystem.Collections.Generic.Dictionary<MongoID, SeasonalRewardData> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var pair in values)
        {
            writer.PutMongoID(pair.Key);
            PutMongoIdSet(writer, pair.Value?.Data);
        }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<MongoID, SeasonalRewardData> GetSeasonalRewards(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, SeasonalRewardData>();
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetMongoID();
            result[id] = new SeasonalRewardData { Data = GetMongoIdSet(reader) };
        }
        return result;
    }

    private static void PutTraderInfoDictionary(NetDataWriter writer, Il2CppSystem.Collections.Generic.Dictionary<MongoID, TraderInfoDescriptor> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        foreach (var pair in values) { writer.PutMongoID(pair.Key); writer.PutEFTTraderInfoDescriptor(pair.Value); }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<MongoID, TraderInfoDescriptor> GetTraderInfoDictionary(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, TraderInfoDescriptor>();
        for (var i = 0; i < count; i++) result[reader.GetMongoID()] = reader.GetEFTTraderInfoDescriptor();
        return result;
    }

    private static void PutBattlePassProgress(NetDataWriter writer, Il2CppSystem.Collections.Generic.List<BattlePassProgressData> values)
    {
        writer.Put(values?.Count ?? 0);
        if (values == null) return;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            writer.PutMongoID(value.BattlePassId);
            PutMongoIdList(writer, value.ObtainedRewards);
        }
    }

    private static Il2CppSystem.Collections.Generic.List<BattlePassProgressData> GetBattlePassProgress(NetDataReader reader)
    {
        var count = reader.GetInt();
        var result = new Il2CppSystem.Collections.Generic.List<BattlePassProgressData>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(new BattlePassProgressData
            {
                BattlePassId = reader.GetMongoID(),
                ObtainedRewards = GetMongoIdList(reader)
            });
        }
        return result;
    }

    /// <summary>
    /// 将档案角色条目的基础元数据与设置参数序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileInfoDescriptor(this NetDataWriter writer, ProfileInfoDescriptor target)
    {
        writer.Put(target.Nickname);
        writer.Put(target.MainProfileNickname);
        writer.PutEnum(target.Side);
            // 47888 移除了 ProfileInfoDescriptor.PrestigeLevel：保持 writer 与 reader 对称
        writer.Put(target.RegistrationDate);
        writer.Put(target.SavageLockTime);
        writer.Put(target.GroupId);
        writer.Put(target.TeamId);
        writer.Put(target.EntryPoint);
        writer.Put(target.NicknameChangeDate);
        writer.Put(target.GameVersion);
        writer.PutEnum(target.Type);
        writer.Put(target.HasCoopExtension);
        writer.Put(target.HasPveGame);
        writer.Put(target.IsStreamerModeAvailable);
        writer.Put(target.GroupInviteRestriction);
        writer.Put(target.Bans.Count);
        for (var i = 0; i < target.Bans.Count; i++)
        {
            writer.PutEFTProfileBanDescriptor(target.Bans[i]);
        }
        writer.PutEFTProfileSettings(target.Settings);
        writer.PutEnum(target.MemberCategory);
        writer.PutEnum(target.SelectedMemberCategory);
        writer.Put(target.Experience);
        writer.Put(target.Level);
        writer.Put(target.LockedMoveCommands);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ProfileInfoDescriptor 对象。
    /// </summary>
    public static ProfileInfoDescriptor GetEFTProfileInfoDescriptor(this NetDataReader reader)
    {
        var profileInfoClass = new ProfileInfoDescriptor
        {
            Nickname = reader.GetString(),
            MainProfileNickname = reader.GetString(),
            Side = reader.GetEnum<EPlayerSide>(),
            // PrestigeLevel：不存在于 47888 描述符中（参见对应的 writer 改动）
            RegistrationDate = reader.GetInt(),
            SavageLockTime = reader.GetDouble(),
            GroupId = reader.GetString(),
            TeamId = reader.GetString(),
            EntryPoint = reader.GetString(),
            NicknameChangeDate = reader.GetLong(),
            GameVersion = reader.GetString(),
            Type = reader.GetEnum<EProfileType>(),
            HasCoopExtension = reader.GetBool(),
            HasPveGame = reader.GetBool(),
            IsStreamerModeAvailable = reader.GetBool(),
            GroupInviteRestriction = reader.GetBool()
        };
        var num = reader.GetInt();
        profileInfoClass.Bans = new Il2CppSystem.Collections.Generic.List<ProfileBanDescriptor>(num);
        for (var i = 0; i < num; i++)
        {
            profileInfoClass.Bans.Add(reader.GetEFTProfileBanDescriptor());
        }
        profileInfoClass.Settings = reader.GetEFTProfileSettings();
        profileInfoClass.MemberCategory = reader.GetEnum<EMemberCategory>();
        profileInfoClass.SelectedMemberCategory = reader.GetEnum<EMemberCategory>();
        profileInfoClass.Experience = reader.GetInt();
        profileInfoClass.Level = reader.GetInt();
        profileInfoClass.LockedMoveCommands = reader.GetBool();
        return profileInfoClass;
    }

    /// <summary>
    /// 将上下文化的内部 AI bot 匹配结构配置字段序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileSettings(this NetDataWriter writer, ProfileSettings target)
    {
        writer.PutEnum(target.Role);
        writer.PutEnum(target.BotDifficulty);
        writer.Put(target.Experience);
        writer.Put(target.StandingForKill);
        writer.Put(target.AggressorBonus);
        writer.Put(target.UseSimpleAnimator);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ProfileSettings 对象。
    /// </summary>
    public static ProfileSettings GetEFTProfileSettings(this NetDataReader reader)
    {
        return new ProfileSettings
        {
            Role = reader.GetEnum<WildSpawnType>(),
            BotDifficulty = reader.GetEnum<BotDifficulty>(),
            Experience = reader.GetInt(),
            StandingForKill = reader.GetDouble(),
            AggressorBonus = reader.GetDouble(),
            UseSimpleAnimator = reader.GetBool()
        };
    }

    /// <summary>
    /// 将会话持久统计指标、战斗操作历史记录与战局日志序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileStatsDescriptor(this NetDataWriter writer, ProfileStatsDescriptor target)
    {
        writer.PutEFTCounterCollectionDescriptor(target.SessionCounters);
        writer.PutEFTCounterCollectionDescriptor(target.OverallCounters);
        writer.Put(target.SessionExperienceMult);
        writer.Put(target.ExperienceBonusMult);
        // SPT 5.0 在通用倍率与会话总计之间插入了
        // 版本专属经验加成。请在托管
        // codec 中保留该顺序，使档案快照与原生 5.0
        // 序列化器保持一致。
        writer.Put(target.GameEditionExperienceBonus != null);
        if (target.GameEditionExperienceBonus != null)
        {
            writer.Put(target.GameEditionExperienceBonus.GameEdition);
            writer.Put(target.GameEditionExperienceBonus.ExperienceBonus);
        }
        writer.Put(target.TotalSessionExperience);
        writer.Put(target.LastSessionDate);
        if (target.Aggressor != null)
        {
            writer.Put(true);
            writer.PutAggressorStats(target.Aggressor);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.DroppedItems.Count);
        for (var i = 0; i < target.DroppedItems.Count; i++)
        {
            writer.PutEFTDroppedItem(target.DroppedItems[i]);
        }
        writer.Put(target.FoundInRaidItems.Count);
        for (var j = 0; j < target.FoundInRaidItems.Count; j++)
        {
            writer.PutEFTFoundInRaidItem(target.FoundInRaidItems[j]);
        }
        writer.Put(target.Victims.Count);
        for (var k = 0; k < target.Victims.Count; k++)
        {
            writer.PutEFTVictimStats(target.Victims[k]);
        }
        writer.Put(target.CarriedQuestItems.Count);
        for (var l = 0; l < target.CarriedQuestItems.Count; l++)
        {
            writer.PutMongoID(target.CarriedQuestItems[l]);
        }
        if (target.DamageHistory != null)
        {
            writer.Put(true);
            writer.PutEFTDamageHistoryDescriptor(target.DamageHistory);
        }
        else
        {
            writer.Put(false);
        }
        if (target.DeathCause != null)
        {
            writer.Put(true);
            writer.PutEFTDeathCause(target.DeathCause);
        }
        else
        {
            writer.Put(false);
        }
        if (target.LastPlayerState != null)
        {
            writer.Put(true);
            writer.PutEFTPlayerVisualRepresentationDescriptor(target.LastPlayerState);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.TotalInGameTime);
        writer.PutEnum(target.SurvivorClass);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ProfileStatsDescriptor 对象。
    /// </summary>
    public static ProfileStatsDescriptor GetEFTProfileStatsDescriptor(this NetDataReader reader)
    {
        var profileEftStatsClass = new ProfileStatsDescriptor
        {
            SessionCounters = reader.GetEFTCounterCollectionDescriptor(),
            OverallCounters = reader.GetEFTCounterCollectionDescriptor(),
            SessionExperienceMult = reader.GetFloat(),
            ExperienceBonusMult = reader.GetFloat()
        };
        if (reader.GetBool())
        {
            profileEftStatsClass.GameEditionExperienceBonus = new GameEditionExperienceBonus
            {
                GameEdition = reader.GetString(),
                ExperienceBonus = reader.GetFloat()
            };
        }
        profileEftStatsClass.TotalSessionExperience = reader.GetInt();
        profileEftStatsClass.LastSessionDate = reader.GetInt();
        if (reader.GetBool())
        {
            profileEftStatsClass.Aggressor = reader.GetAggressorStats();
        }
        var num = reader.GetInt();
        profileEftStatsClass.DroppedItems = new Il2CppSystem.Collections.Generic.List<DroppedItem>(num);
        for (var i = 0; i < num; i++)
        {
            profileEftStatsClass.DroppedItems.Add(reader.GetEFTDroppedItem());
        }
        var num2 = reader.GetInt();
        profileEftStatsClass.FoundInRaidItems = new Il2CppSystem.Collections.Generic.List<FoundInRaidItem>(num2);
        for (var j = 0; j < num2; j++)
        {
            profileEftStatsClass.FoundInRaidItems.Add(reader.GetEFTFoundInRaidItem());
        }
        var num3 = reader.GetInt();
        profileEftStatsClass.Victims = new Il2CppSystem.Collections.Generic.List<VictimStats>(num3);
        for (var k = 0; k < num3; k++)
        {
            profileEftStatsClass.Victims.Add(reader.GetEFTVictimStats());
        }
        var num4 = reader.GetInt();
        profileEftStatsClass.CarriedQuestItems = new Il2CppSystem.Collections.Generic.List<MongoID>(num4);
        for (var l = 0; l < num4; l++)
        {
            profileEftStatsClass.CarriedQuestItems.Add(reader.GetMongoID());
        }
        if (reader.GetBool())
        {
            profileEftStatsClass.DamageHistory = reader.GetEFTDamageHistoryDescriptor();
        }
        if (reader.GetBool())
        {
            profileEftStatsClass.DeathCause = reader.GetEFTDeathCause();
        }
        if (reader.GetBool())
        {
            profileEftStatsClass.LastPlayerState = reader.GetEFTPlayerVisualRepresentationDescriptor();
        }
        profileEftStatsClass.TotalInGameTime = reader.GetLong();
        profileEftStatsClass.SurvivorClass = reader.GetEnum<ProfileStats.ESurvivorClass>();
        return profileEftStatsClass;
    }

    /// <summary>
    /// 将按游戏环境划分的统计档案序列化写入 writer 流。
    /// </summary>
    public static void PutEFTProfileStatsSeparatorDescriptor(this NetDataWriter writer, ProfileStatsSeparatorDescriptor target)
    {
        if (target.Eft != null)
        {
            writer.Put(true);
            writer.PutEFTProfileStatsDescriptor(target.Eft);
        }
        else
        {
            writer.Put(false);
        }
        if (target.Arena != null)
        {
            writer.Put(true);
            writer.PutEFTProfileStatsDescriptor(target.Arena);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ProfileStatsSeparatorDescriptor 对象。
    /// </summary>
    public static ProfileStatsSeparatorDescriptor GetEFTProfileStatsSeparatorDescriptor(this NetDataReader reader)
    {
        var profileStatsClass = new ProfileStatsSeparatorDescriptor();
        if (reader.GetBool())
        {
            profileStatsClass.Eft = reader.GetEFTProfileStatsDescriptor();
        }
        if (reader.GetBool())
        {
            profileStatsClass.Arena = reader.GetEFTProfileStatsDescriptor();
        }
        return profileStatsClass;
    }

    /// <summary>
    /// 将任务接受操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTQuestAcceptDescriptor(this NetDataWriter writer, QuestAcceptDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.QuestId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 QuestAcceptDescriptor 对象。
    /// </summary>
    public static QuestAcceptDescriptor GetEFTQuestAcceptDescriptor(this NetDataReader reader)
    {
        return new QuestAcceptDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            QuestId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将任务结算/完成操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTQuestFinishDescriptor(this NetDataWriter writer, QuestFinishDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.QuestId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 QuestFinishDescriptor 对象。
    /// </summary>
    public static QuestFinishDescriptor GetEFTQuestFinishDescriptor(this NetDataReader reader)
    {
        return new QuestFinishDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            QuestId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将任务条件物品交付操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTQuestHandoverDescriptor(this NetDataWriter writer, QuestHandoverDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.Items.Length);
        for (var i = 0; i < target.Items.Length; i++)
        {
            writer.PutEFTDestroyedItem(target.Items[i]);
        }
        writer.PutMongoID(target.ConditionId);
        writer.PutMongoID(target.QuestId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 QuestHandoverDescriptor 对象。
    /// </summary>
    public static QuestHandoverDescriptor GetEFTQuestHandoverDescriptor(this NetDataReader reader)
    {
        var gclass = new QuestHandoverDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID()
        };
        var num = reader.GetInt();
        gclass.Items = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DestroyedItem>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.Items[i] = reader.GetEFTDestroyedItem();
        }
        gclass.ConditionId = reader.GetMongoID();
        gclass.QuestId = reader.GetMongoID();
        return gclass;
    }

    /// <summary>
    /// 将历史任务状态时间线日志与目标进度映射序列化写入 writer 流。
    /// </summary>
    public static void PutEFTQuestsQuestStatusData(this NetDataWriter writer, QuestStatusData target)
    {
        writer.Put(target.Id);
        writer.Put(target.StartTime);
        writer.PutEnum(target.Status);
        writer.Put(target.StatusStartTimestamps.Count);
        foreach (var keyValuePair in target.StatusStartTimestamps)
        {
            writer.PutEnum(keyValuePair.Key);
            writer.Put(keyValuePair.Value);
        }
        writer.Put(target.CompletedConditions.Count);
        foreach (var mongoID in target.CompletedConditions)
        {
            writer.PutMongoID(mongoID);
        }
        writer.Put(target.AvailableAfter);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 QuestStatusData 对象。
    /// </summary>
    public static QuestStatusData GetEFTQuestsQuestStatusData(this NetDataReader reader)
    {
        var questDataClass = new QuestStatusData
        {
            Id = reader.GetString(),
            StartTime = reader.GetInt(),
            Status = reader.GetEnum<EQuestStatus>()
        };
        var num = reader.GetInt();
        questDataClass.StatusStartTimestamps = new Il2CppSystem.Collections.Generic.Dictionary<EQuestStatus, double>();
        for (var i = 0; i < num; i++)
        {
            questDataClass.StatusStartTimestamps[reader.GetEnum<EQuestStatus>()] = reader.GetDouble();
        }
        var num2 = reader.GetInt();
        questDataClass.CompletedConditions = new Il2CppSystem.Collections.Generic.HashSet<MongoID>();
        for (var j = 0; j < num2; j++)
        {
            questDataClass.CompletedConditions.Add(reader.GetMongoID());
        }
        questDataClass.AvailableAfter = reader.GetInt();
        return questDataClass;
    }

    /// <summary>
    /// 将可重编码/已编码物品状态组件序列化写入 writer 流。
    /// </summary>
    public static void PutEFTRecodableComponentDescriptor(this NetDataWriter writer, RecodableComponentDescriptor target)
    {
        writer.Put(target.IsEncoded);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 RecodableComponentDescriptor 对象。
    /// </summary>
    public static RecodableComponentDescriptor GetEFTRecodableComponentDescriptor(this NetDataReader reader)
    {
        return new RecodableComponentDescriptor
        {
            IsEncoded = reader.GetBool()
        };
    }

    /// <summary>
    /// 将库存物品移除操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTRemoveOperationDescriptor(this NetDataWriter writer, RemoveOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 RemoveOperationDescriptor 对象。
    /// </summary>
    public static RemoveOperationDescriptor GetEFTRemoveOperationDescriptor(this NetDataReader reader)
    {
        return new RemoveOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将可修理物品状态参数序列化写入 writer 流。
    /// </summary>
    public static void PutEFTRepairableComponentDescriptor(this NetDataWriter writer, RepairableComponentDescriptor target)
    {
        writer.Put(target.Durability);
        writer.Put(target.MaxDurability);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 RepairableComponentDescriptor 对象。
    /// </summary>
    public static RepairableComponentDescriptor GetEFTRepairableComponentDescriptor(this NetDataReader reader)
    {
        return new RepairableComponentDescriptor
        {
            Durability = reader.GetFloat(),
            MaxDurability = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将修理增强状态属性与修正值序列化写入 writer 流。
    /// </summary>
    public static void PutEFTRepairEnhancementComponentDescriptor(this NetDataWriter writer, RepairEnhancementComponentDescriptor target)
    {
        if (NativeNullable<ERepairBuffType>.Read(target, "BuffType").HasValue)
        {
            writer.Put(true);
            writer.PutEnum(NativeNullable<ERepairBuffType>.Read(target, "BuffType").Value);
        }
        else
        {
            writer.Put(false);
        }
        if (NativeNullable<EBuffRarity>.Read(target, "BuffRarity").HasValue)
        {
            writer.Put(true);
            writer.PutEnum(NativeNullable<EBuffRarity>.Read(target, "BuffRarity").Value);
        }
        else
        {
            writer.Put(false);
        }
        writer.Put(target.Value);
        writer.Put(target.ThresholdDurability);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 RepairEnhancementComponentDescriptor 对象。
    /// </summary>
    public static RepairEnhancementComponentDescriptor GetEFTRepairEnhancementComponentDescriptor(this NetDataReader reader)
    {
        var gclass = new RepairEnhancementComponentDescriptor();
        if (reader.GetBool())
        {
            NativeNullable<ERepairBuffType>.Write(gclass, "BuffType", new Il2CppSystem.Nullable<ERepairBuffType>(reader.GetEnum<ERepairBuffType>()));
        }
        if (reader.GetBool())
        {
            NativeNullable<EBuffRarity>.Write(gclass, "BuffRarity", new Il2CppSystem.Nullable<EBuffRarity>(reader.GetEnum<EBuffRarity>()));
        }
        gclass.Value = reader.GetFloat();
        gclass.ThresholdDurability = reader.GetFloat();
        return gclass;
    }

    /// <summary>
    /// 将修理工具包资源点数组件数据序列化写入 writer 流。
    /// </summary>
    public static void PutEFTRepairKitComponentDescriptor(this NetDataWriter writer, RepairKitComponentDescriptor target)
    {
        writer.Put(target.Resource);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 RepairKitComponentDescriptor 对象。
    /// </summary>
    public static RepairKitComponentDescriptor GetEFTRepairKitComponentDescriptor(this NetDataReader reader)
    {
        return new RepairKitComponentDescriptor
        {
            Resource = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将物品通用资源使用量序列化写入 writer 流。
    /// </summary>
    public static void PutEFTResourceItemComponentDescriptor(this NetDataWriter writer, ResourceItemComponentDescriptor target)
    {
        writer.Put(target.Resource);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ResourceItemComponentDescriptor 对象。
    /// </summary>
    public static ResourceItemComponentDescriptor GetEFTResourceItemComponentDescriptor(this NetDataReader reader)
    {
        return new ResourceItemComponentDescriptor
        {
            Resource = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将场景上下文的资源引用清单索引序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSceneResourceKey(this NetDataWriter writer, SceneResourceKey target)
    {
        if (target.path != null)
        {
            writer.Put(true);
            writer.Put(target.path);
        }
        else
        {
            writer.Put(false);
        }
        if (target.rcid != null)
        {
            writer.Put(true);
            writer.Put(target.rcid);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SceneResourceKey 对象。
    /// </summary>
    public static SceneResourceKey GetEFTSceneResourceKey(this NetDataReader reader)
    {
        var sceneResourceKey = new SceneResourceKey();
        if (reader.GetBool())
        {
            sceneResourceKey.path = reader.GetString();
        }
        if (reader.GetBool())
        {
            sceneResourceKey.rcid = reader.GetString();
        }
        return sceneResourceKey;
    }

    /// <summary>
    /// 将标准系统资源依赖查找键序列化写入 writer 流。
    /// </summary>
    public static void PutEFTResourceKey(this NetDataWriter writer, ResourceKey target)
    {
        if (target.path != null)
        {
            writer.Put(true);
            writer.Put(target.path);
        }
        else
        {
            writer.Put(false);
        }
        if (target.rcid != null)
        {
            writer.Put(true);
            writer.Put(target.rcid);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ResourceKey 对象。
    /// </summary>
    public static ResourceKey GetEFTResourceKey(this NetDataReader reader)
    {
        var resourceKey = new ResourceKey();
        if (reader.GetBool())
        {
            resourceKey.path = reader.GetString();
        }
        if (reader.GetBool())
        {
            resourceKey.rcid = reader.GetString();
        }
        return resourceKey;
    }

    /// <summary>
    /// 将本地化的剧情对话叙事推进事件序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSetDialogProgressOperationDescriptor(this NetDataWriter writer, SetDialogProgressOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.TraderId);
        writer.PutMongoID(target.DialogId);
        writer.PutMongoID(target.LineId);
        writer.PutPolymorph(target.SubOperation);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SetDialogProgressOperationDescriptor 对象。
    /// </summary>
    public static SetDialogProgressOperationDescriptor GetEFTSetDialogProgressOperationDescriptor(this NetDataReader reader)
    {
        return new SetDialogProgressOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            TraderId = reader.GetMongoID(),
            DialogId = reader.GetMongoID(),
            LineId = reader.GetMongoID(),
            SubOperation = reader.GetPolymorph<InventoryOperationDescriptor>()
        };
    }

    /// <summary>
    /// 将物品放置/设置操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSetupItemOperationDescriptor(this NetDataWriter writer, SetupItemOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.ZoneId);
        writer.PutClassVector3(target.Position);
        writer.PutClassQuaternion(target.Rotation);
        writer.Put(target.SetupTime);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SetupItemOperationDescriptor 对象。
    /// </summary>
    public static SetupItemOperationDescriptor GetEFTSetupItemOperationDescriptor(this NetDataReader reader)
    {
        return new SetupItemOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            ZoneId = reader.GetString(),
            Position = reader.GetClassVector3(),
            Rotation = reader.GetClassQuaternion(),
            SetupTime = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将武器弹壳模板引用映射序列化写入 writer 流。
    /// </summary>
    public static void PutEFTShellTemplateDescriptor(this NetDataWriter writer, ShellTemplateDescriptor target)
    {
        writer.PutMongoID(target.AmmoTemplateId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ShellTemplateDescriptor 对象。
    /// </summary>
    public static ShellTemplateDescriptor GetEFTShellTemplateDescriptor(this NetDataReader reader)
    {
        return new ShellTemplateDescriptor
        {
            AmmoTemplateId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将复杂的枪械瞄具校准与倍镜多模式数组序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSightComponentDescriptor(this NetDataWriter writer, SightComponentDescriptor target)
    {
        writer.Put(target.SelectedSightScope);
        writer.Put(target.ScopesSelectedModes.Length);
        for (var i = 0; i < target.ScopesSelectedModes.Length; i++)
        {
            writer.Put(target.ScopesSelectedModes[i]);
        }
        writer.Put(target.ScopesSelectedCalibPoints.Length);
        for (var j = 0; j < target.ScopesSelectedCalibPoints.Length; j++)
        {
            writer.Put(target.ScopesSelectedCalibPoints[j]);
        }
        writer.Put(target.ScopeZoomValue);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SightComponentDescriptor 对象。
    /// </summary>
    public static SightComponentDescriptor GetEFTSightComponentDescriptor(this NetDataReader reader)
    {
        var gclass = new SightComponentDescriptor
        {
            SelectedSightScope = reader.GetInt()
        };
        var num = reader.GetInt();
        gclass.ScopesSelectedModes = new int[num];
        for (var i = 0; i < num; i++)
        {
            gclass.ScopesSelectedModes[i] = reader.GetInt();
        }
        var num2 = reader.GetInt();
        gclass.ScopesSelectedCalibPoints = new int[num2];
        for (var j = 0; j < num2; j++)
        {
            gclass.ScopesSelectedCalibPoints[j] = reader.GetInt();
        }
        gclass.ScopeZoomValue = reader.GetFloat();
        return gclass;
    }

    /// <summary>
    /// 将分别涵盖通用属性与专精统计的独立进度块序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSkillsDescriptor(this NetDataWriter writer, SkillsDescriptor target)
    {
        writer.Put(target.Common.Length);
        for (var i = 0; i < target.Common.Length; i++)
        {
            writer.PutEFTSkillsDescriptorSkillInfoDescriptor(target.Common[i]);
        }
        writer.Put(target.Mastering.Length);
        for (var j = 0; j < target.Mastering.Length; j++)
        {
            writer.PutEFTSkillsDescriptorMasteringInfoDescriptor(target.Mastering[j]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SkillsDescriptor 对象。
    /// </summary>
    public static SkillsDescriptor GetEFTSkillsDescriptor(this NetDataReader reader)
    {
        var skillsDescriptorClass = new SkillsDescriptor();
        var num = reader.GetInt();
        skillsDescriptorClass.Common = new SkillsDescriptor.SkillInfoDescriptor[num];
        for (var i = 0; i < num; i++)
        {
            skillsDescriptorClass.Common[i] = reader.GetEFTSkillsDescriptorSkillInfoDescriptor();
        }
        var num2 = reader.GetInt();
        skillsDescriptorClass.Mastering = new SkillsDescriptor.MasteringInfoDescriptor[num2];
        for (var j = 0; j < num2; j++)
        {
            skillsDescriptorClass.Mastering[j] = reader.GetEFTSkillsDescriptorMasteringInfoDescriptor();
        }
        return skillsDescriptorClass;
    }

    /// <summary>
    /// 将指定的武器专精标识及其进度指标序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSkillsDescriptorMasteringInfoDescriptor(this NetDataWriter writer, SkillsDescriptor.MasteringInfoDescriptor target)
    {
        writer.Put(target.Id);
        writer.Put(target.Progress);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SkillsDescriptor.MasteringInfoDescriptor 对象。
    /// </summary>
    public static SkillsDescriptor.MasteringInfoDescriptor GetEFTSkillsDescriptorMasteringInfoDescriptor(this NetDataReader reader)
    {
        return new SkillsDescriptor.MasteringInfoDescriptor
        {
            Id = reader.GetString(),
            Progress = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将包含会话增益记录与时间标记的单个角色技能项序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSkillsDescriptorSkillInfoDescriptor(this NetDataWriter writer, SkillsDescriptor.SkillInfoDescriptor target)
    {
        writer.PutEnum(target.Id);
        writer.Put(target.Progress);
        writer.Put(target.PointsEarnedDuringSession);
        writer.Put(target.LastAccess);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SkillsDescriptor.SkillInfoDescriptor 对象。
    /// </summary>
    public static SkillsDescriptor.SkillInfoDescriptor GetEFTSkillsDescriptorSkillInfoDescriptor(this NetDataReader reader)
    {
        return new SkillsDescriptor.SkillInfoDescriptor
        {
            Id = reader.GetEnum<ESkillId>(),
            Progress = reader.GetFloat(),
            PointsEarnedDuringSession = reader.GetFloat(),
            LastAccess = reader.GetInt()
        };
    }

    /// <summary>
    /// 将单个库存槽位描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSlotDescriptor(this NetDataWriter writer, SlotDescriptor target)
    {
        writer.Put(target.SlotNumber);
        writer.PutEFTItemDescriptor(target.ContainedItem);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SlotDescriptor 对象。
    /// </summary>
    public static SlotDescriptor GetEFTSlotDescriptor(this NetDataReader reader)
    {
        return new SlotDescriptor
        {
            SlotNumber = reader.GetByte(),
            ContainedItem = reader.GetEFTItemDescriptor()
        };
    }

    /// <summary>
    /// 将槽位物品地址容器引用映射序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSlotItemAddressDescriptor(this NetDataWriter writer, SlotItemAddressDescriptor target)
    {
        writer.PutEFTContainerDescriptor(target.Container);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SlotItemAddressDescriptor 对象。
    /// </summary>
    public static SlotItemAddressDescriptor GetEFTSlotItemAddressDescriptor(this NetDataReader reader)
    {
        return new SlotItemAddressDescriptor
        {
            Container = reader.GetEFTContainerDescriptor()
        };
    }

    /// <summary>
    /// 将物品堆叠拆分操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSplitOperationDescriptor(this NetDataWriter writer, SplitOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.CloneId);
        writer.PutPolymorph(target.From);
        writer.PutPolymorph(target.To);
        writer.Put(target.Count);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SplitOperationDescriptor 对象。
    /// </summary>
    public static SplitOperationDescriptor GetEFTSplitOperationDescriptor(this NetDataReader reader)
    {
        return new SplitOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            CloneId = reader.GetString(),
            From = reader.GetPolymorph<ItemAddressDescriptor>(),
            To = reader.GetPolymorph<ItemAddressDescriptor>(),
            Count = reader.GetInt()
        };
    }

    /// <summary>
    /// 将库存多物品堆叠槽位布局块序列化写入 writer 流。
    /// </summary>
    public static void PutEFTStackSlotDescriptor(this NetDataWriter writer, StackSlotDescriptor target)
    {
        writer.Put(target.SlotNumber);
        writer.Put(target.ContainedItems.Count);
        for (var i = 0; i < target.ContainedItems.Count; i++)
        {
            writer.PutEFTItemDescriptor(target.ContainedItems[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 StackSlotDescriptor 对象。
    /// </summary>
    public static StackSlotDescriptor GetEFTStackSlotDescriptor(this NetDataReader reader)
    {
        var gclass = new StackSlotDescriptor
        {
            SlotNumber = reader.GetByte()
        };
        var num = reader.GetInt();
        gclass.ContainedItems = new Il2CppSystem.Collections.Generic.List<ItemDescriptor>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.ContainedItems.Add(reader.GetEFTItemDescriptor());
        }
        return gclass;
    }

    /// <summary>
    /// 将堆叠槽位结构布局物品地址映射描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTStackSlotItemAddressDescriptor(this NetDataWriter writer, StackSlotItemAddressDescriptor target)
    {
        writer.PutEFTContainerDescriptor(target.Container);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 StackSlotItemAddressDescriptor 对象。
    /// </summary>
    public static StackSlotItemAddressDescriptor GetEFTStackSlotItemAddressDescriptor(this NetDataReader reader)
    {
        return new StackSlotItemAddressDescriptor
        {
            Container = reader.GetEFTContainerDescriptor()
        };
    }

    /// <summary>
    /// 将复杂物品布局位置交换操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTSwapOperationDescriptor(this NetDataWriter writer, SwapOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.PutPolymorph(target.To);
        writer.Put(target.Item1Id);
        writer.PutPolymorph(target.To1);
        if (target.DestroyedItems != null)
        {
            writer.Put(true);
            writer.Put(target.DestroyedItems.Count);
            for (var i = 0; i < target.DestroyedItems.Count; i++)
            {
                writer.PutEFTDestroyedItem(target.DestroyedItems[i]);
            }
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 SwapOperationDescriptor 对象。
    /// </summary>
    public static SwapOperationDescriptor GetEFTSwapOperationDescriptor(this NetDataReader reader)
    {
        var gclass = new SwapOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            To = reader.GetPolymorph<ItemAddressDescriptor>(),
            Item1Id = reader.GetString(),
            To1 = reader.GetPolymorph<ItemAddressDescriptor>()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            gclass.DestroyedItems = new Il2CppSystem.Collections.Generic.List<DestroyedItem>(num);
            for (var i = 0; i < num; i++)
            {
                gclass.DestroyedItems.Add(reader.GetEFTDestroyedItem());
            }
        }
        return gclass;
    }

    /// <summary>
    /// 将交互式物品状态标签组件条目序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTagComponentDescriptor(this NetDataWriter writer, TagComponentDescriptor target)
    {
        writer.Put(target.Name);
        writer.Put(target.Color);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TagComponentDescriptor 对象。
    /// </summary>
    public static TagComponentDescriptor GetEFTTagComponentDescriptor(this NetDataReader reader)
    {
        return new TagComponentDescriptor
        {
            Name = reader.GetString(),
            Color = reader.GetInt()
        };
    }

    /// <summary>
    /// 将单个物品标签变更操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTagOperationDescriptor(this NetDataWriter writer, TagOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.TagName);
        writer.Put(target.TagColor);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TagOperationDescriptor 对象。
    /// </summary>
    public static TagOperationDescriptor GetEFTTagOperationDescriptor(this NetDataReader reader)
    {
        return new TagOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            TagName = reader.GetString(),
            TagColor = reader.GetInt()
        };
    }

    /// <summary>
    /// 将目标档案进度任务条件监控追踪元素序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTaskConditionCounterDescriptor(this NetDataWriter writer, TaskConditionCounterDescriptor target)
    {
        writer.PutMongoID(target.Id);
        writer.Put(target.Value);
        writer.Put(target.SourceId);
        writer.Put(target.Type);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TaskConditionCounterDescriptor 对象。
    /// </summary>
    public static TaskConditionCounterDescriptor GetEFTTaskConditionCounterDescriptor(this NetDataReader reader)
    {
        return new TaskConditionCounterDescriptor
        {
            Id = reader.GetMongoID(),
            Value = reader.GetInt(),
            SourceId = reader.GetString(),
            Type = reader.GetString()
        };
    }

    /// <summary>
    /// 将环境物品丢弃/投掷物理操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTThrowOperationDescriptor(this NetDataWriter writer, ThrowOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.DownDirection);
        if (target.DestroyedItems != null)
        {
            writer.Put(true);
            writer.Put(target.DestroyedItems.Count);
            for (var i = 0; i < target.DestroyedItems.Count; i++)
            {
                writer.PutEFTDestroyedItem(target.DestroyedItems[i]);
            }
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ThrowOperationDescriptor 对象。
    /// </summary>
    public static ThrowOperationDescriptor GetEFTThrowOperationDescriptor(this NetDataReader reader)
    {
        var throwDescriptorClass = new ThrowOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            DownDirection = reader.GetBool()
        };
        if (reader.GetBool())
        {
            var num = reader.GetInt();
            throwDescriptorClass.DestroyedItems = new Il2CppSystem.Collections.Generic.List<DestroyedItem>(num);
            for (var i = 0; i < num; i++)
            {
                throwDescriptorClass.DestroyedItems.Add(reader.GetEFTDestroyedItem());
            }
        }
        return throwDescriptorClass;
    }

    /// <summary>
    /// 将可切换组件描述符状态序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTogglableComponentDescriptor(this NetDataWriter writer, TogglableComponentDescriptor target)
    {
        writer.Put(target.IsOn);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TogglableComponentDescriptor 对象。
    /// </summary>
    public static TogglableComponentDescriptor GetEFTTogglableComponentDescriptor(this NetDataReader reader)
    {
        return new TogglableComponentDescriptor
        {
            IsOn = reader.GetBool()
        };
    }

    /// <summary>
    /// 将库存物品开关操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTToggleOperationDescriptor(this NetDataWriter writer, ToggleOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.Value);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ToggleOperationDescriptor 对象。
    /// </summary>
    public static ToggleOperationDescriptor GetEFTToggleOperationDescriptor(this NetDataReader reader)
    {
        return new ToggleOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            Value = reader.GetBool()
        };
    }

    /// <summary>
    /// 将商人实例的忠诚等级、声望与阈值元数据序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTraderInfoDescriptor(this NetDataWriter writer, TraderInfoDescriptor target)
    {
        writer.Put(target.Unlocked);
        writer.Put(target.LoyaltyLevel);
        writer.Put(target.SalesSum);
        writer.Put(target.Standing);
        writer.Put(target.NextResupply);
        writer.Put(target.Disabled);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TraderInfoDescriptor 对象。
    /// </summary>
    public static TraderInfoDescriptor GetEFTTraderInfoDescriptor(this NetDataReader reader)
    {
        return new TraderInfoDescriptor
        {
            Unlocked = reader.GetBool(),
            LoyaltyLevel = reader.GetInt(),
            SalesSum = reader.GetLong(),
            Standing = reader.GetDouble(),
            NextResupply = reader.GetInt(),
            Disabled = reader.GetBool()
        };
    }

    /// <summary>
    /// 将商人服务商业费用列表与资格校验状态序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTraderServiceAvailabilityData(this NetDataWriter writer, TraderServiceAvailabilityData target)
    {
        writer.PutMongoID(target.TraderId);
        writer.PutEnum(target.ServiceType);
        writer.Put(target.CanAfford);
        writer.Put(target.WasPurchasedInThisRaid);
        writer.Put(target.ItemsToPay.Count);
        foreach (var keyValuePair in target.ItemsToPay)
        {
            writer.PutMongoID(keyValuePair.Key);
            writer.Put(keyValuePair.Value);
        }
        writer.Put(target.UniqueItems.Length);
        for (var i = 0; i < target.UniqueItems.Length; i++)
        {
            writer.PutMongoID(target.UniqueItems[i]);
        }
        writer.Put(target.SubServices.Count);
        foreach (var keyValuePair2 in target.SubServices)
        {
            writer.Put(keyValuePair2.Key);
            writer.Put(keyValuePair2.Value);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TraderServiceAvailabilityData 对象。
    /// </summary>
    public static TraderServiceAvailabilityData GetEFTTraderServiceAvailabilityData(this NetDataReader reader)
    {
        var traderServicesClass = new TraderServiceAvailabilityData
        {
            TraderId = reader.GetMongoID(),
            ServiceType = reader.GetEnum<ETraderServiceType>(),
            CanAfford = reader.GetBool(),
            WasPurchasedInThisRaid = reader.GetBool()
        };
        var num = reader.GetInt();
        traderServicesClass.ItemsToPay = new Il2CppSystem.Collections.Generic.Dictionary<MongoID, int>();
        for (var i = 0; i < num; i++)
        {
            traderServicesClass.ItemsToPay[reader.GetMongoID()] = reader.GetInt();
        }
        var num2 = reader.GetInt();
        traderServicesClass.UniqueItems = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<MongoID>(num2);
        for (var j = 0; j < num2; j++)
        {
            traderServicesClass.UniqueItems[j] = reader.GetMongoID();
        }
        var num3 = reader.GetInt();
        traderServicesClass.SubServices = new Il2CppSystem.Collections.Generic.Dictionary<string, int>();
        for (var k = 0; k < num3; k++)
        {
            traderServicesClass.SubServices[reader.GetString()] = reader.GetInt();
        }
        return traderServicesClass;
    }

    /// <summary>
    /// 将付费撤离点（车辆撤离）现金交付操作
    /// 描述符序列化写入 writer 流。该撤离点通过其触发
    /// 名称标识，每件待交付物品通过其 id 标识，主机因此可以
    /// 基于自身的权威物品图重建该操作。
    /// </summary>
    public static void PutEFTInventoryLogicOperationsTransferExfiltrationPaymentOperationDescriptor(
        this NetDataWriter writer, TransferExfiltrationPaymentOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.PointName);
        var items = target.Items;
        var count = items?.Length ?? 0;
        writer.Put(count);
        for (var i = 0; i < count; i++)
        {
            writer.PutEFTDestroyedItem(items[i]);
        }
    }

    /// <summary>
    /// 从 reader 流反序列化并重建付费撤离点现金交付
    /// 操作描述符。
    /// </summary>
    public static TransferExfiltrationPaymentOperationDescriptor GetEFTInventoryLogicOperationsTransferExfiltrationPaymentOperationDescriptor(
        this NetDataReader reader)
    {
        var gclass = new TransferExfiltrationPaymentOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            PointName = reader.GetString()
        };
        var num = reader.GetInt();
        gclass.Items = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DestroyedItem>(num);
        for (var i = 0; i < num; i++)
        {
            gclass.Items[i] = reader.GetEFTDestroyedItem();
        }
        return gclass;
    }

    /// <summary>
    /// 将磁带学习（tape study）操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTStudyItemOperationDescriptor(this NetDataWriter writer, StudyItemOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 StudyItemOperationDescriptor 对象。
    /// </summary>
    public static StudyItemOperationDescriptor GetEFTStudyItemOperationDescriptor(this NetDataReader reader)
    {
        return new StudyItemOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
    }

    /// <summary>
    /// 将磁带学习（通过录像机回放）操作描述符
    /// 序列化写入 writer 流。录像机是可选的，因此以一个标志
    /// 后接其 id 的形式写入。
    /// </summary>
    public static void PutEFTStudyItemPlayerOperationDescriptor(this NetDataWriter writer, StudyItemPlayerOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutMongoID(target.ItemId);
        // 生成的 getter 会装箱内联的 Nullable 字段，因此需通过
        // 该字段读取，方式与 ProfileDescriptor.PetId 完全一致。
        var recorderId = NativeNullable<MongoID>.Read(target, nameof(StudyItemPlayerOperationDescriptor.RecorderId));
        if (recorderId.HasValue)
        {
            writer.Put(true);
            writer.PutMongoID(recorderId.Value);
            return;
        }
        writer.Put(false);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 StudyItemPlayerOperationDescriptor 对象。
    /// </summary>
    public static StudyItemPlayerOperationDescriptor GetEFTStudyItemPlayerOperationDescriptor(this NetDataReader reader)
    {
        var gclass = new StudyItemPlayerOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetMongoID()
        };
        if (reader.GetBool())
        {
            NativeNullable<MongoID>.Write(gclass, nameof(StudyItemPlayerOperationDescriptor.RecorderId),
                new Il2CppSystem.Nullable<MongoID>(reader.GetMongoID()));
        }
        return gclass;
    }

    /// <summary>
    /// 将库存物品数值元素转移操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTTransferOperationDescriptor(this NetDataWriter writer, TransferOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.Put(target.Item1Id);
        writer.Put(target.Count);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 TransferOperationDescriptor 对象。
    /// </summary>
    public static TransferOperationDescriptor GetEFTTransferOperationDescriptor(this NetDataReader reader)
    {
        return new TransferOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            Item1Id = reader.GetString(),
            Count = reader.GetInt()
        };
    }

    /// <summary>
    /// 将快捷栏热键物品解绑操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTUnbindItemOperationDescriptor(this NetDataWriter writer, UnbindItemOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.ItemId);
        writer.PutEnum(target.Index);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 UnbindItemOperationDescriptor 对象。
    /// </summary>
    public static UnbindItemOperationDescriptor GetEFTUnbindItemOperationDescriptor(this NetDataReader reader)
    {
        return new UnbindItemOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            ItemId = reader.GetString(),
            Index = reader.GetEnum<EBoundItem>()
        };
    }

    /// <summary>
    /// 将武器弹匣清空/卸载操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTUnloadMagOperationDescriptor(this NetDataWriter writer, UnloadMagOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.PutPolymorph(target.InternalOperationDescriptor);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 UnloadMagOperationDescriptor 对象。
    /// </summary>
    public static UnloadMagOperationDescriptor GetEFTUnloadMagOperationDescriptor(this NetDataReader reader)
    {
        return new UnloadMagOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            InternalOperationDescriptor = reader.GetPolymorph<InventoryOperationDescriptor>()
        };
    }

    /// <summary>
    /// 将击杀追踪受害者的档案上下文属性与距离指标日志序列化写入 writer 流。
    /// </summary>
    public static void PutEFTVictimStats(this NetDataWriter writer, VictimStats target)
    {
        writer.Put(target.AccountId);
        writer.Put(target.ProfileId);
        writer.Put(target.Name);
        writer.PutEnum(target.Side);
        writer.PutTimeSpan(new System.TimeSpan(target.Time.Ticks));
        writer.Put(target.Level);
            // 47888 移除了 ProfileInfoDescriptor.PrestigeLevel：保持 writer 与 reader 对称
        writer.PutEnum(target.BodyPart);
        writer.Put(target.Weapon);
        writer.Put(target.Distance);
        writer.PutEnum(target.Role);
        writer.Put(target.Location);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 VictimStats 对象。
    /// </summary>
    public static VictimStats GetEFTVictimStats(this NetDataReader reader)
    {
        return new VictimStats
        {
            AccountId = reader.GetString(),
            ProfileId = reader.GetString(),
            Name = reader.GetString(),
            Side = reader.GetEnum<EPlayerSide>(),
            Time = new Il2CppSystem.TimeSpan(reader.GetTimeSpan().Ticks),
            Level = reader.GetInt(),
            // PrestigeLevel：不存在于 47888 描述符中（参见对应的 writer 改动）
            BodyPart = reader.GetEnum<EBodyPart>(),
            Weapon = reader.GetString(),
            Distance = reader.GetFloat(),
            Role = reader.GetEnum<WildSpawnType>(),
            Location = reader.GetString()
        };
    }

    /// <summary>
    /// 将枪械枪机/上膛交互动作操作描述符序列化写入 writer 流。
    /// </summary>
    public static void PutEFTWeaponRechamberOperationDescriptor(this NetDataWriter writer, WeaponRechamberOperationDescriptor target)
    {
        writer.Put(target.OperationId);
        writer.PutMongoID(target.OwnerId);
        writer.Put(target.WeaponId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 WeaponRechamberOperationDescriptor 对象。
    /// </summary>
    public static WeaponRechamberOperationDescriptor GetEFTWeaponRechamberOperationDescriptor(this NetDataReader reader)
    {
        return new WeaponRechamberOperationDescriptor
        {
            OperationId = reader.GetUShort(),
            OwnerId = reader.GetMongoID(),
            WeaponId = reader.GetString()
        };
    }

    /// <summary>
    /// 将保险追踪记录中的物品标识字符串关联序列化写入 writer 流。
    /// </summary>
    public static void PutJsonTypeInsuredProfileItems(this NetDataWriter writer, InsuredProfileItems target)
    {
        writer.Put(target.TraderId);
        writer.Put(target.ItemId);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 InsuredProfileItems 对象。
    /// </summary>
    public static InsuredProfileItems GetJsonTypeInsuredProfileItems(this NetDataReader reader)
    {
        return new InsuredProfileItems
        {
            TraderId = reader.GetString(),
            ItemId = reader.GetString()
        };
    }

    /// <summary>
    /// 将玩家档案模型的外观标识参数与完整健康状态值序列化写入 writer 流。
    /// </summary>
    public static void PutJsonTypePlayerInfo(this NetDataWriter writer, PlayerInfo target)
    {
        writer.Put(target.Nickname);
        writer.PutEnum(target.Side);
        writer.Put(target.Level);
            // 47888 移除了 ProfileInfoDescriptor.PrestigeLevel：保持 writer 与 reader 对称
        writer.PutEnum(target.MemberCategory);
        writer.PutEnum(target.SelectedMemberCategory);
        writer.Put(target.SavageLockTime);
        writer.Put(target.SavageNickname);
        writer.Put(target.GameVersion);
        writer.Put(target.HasCoopExtension);
        writer.PutEFTProfileHealthInfo(target.Health);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 PlayerInfo 对象。
    /// </summary>
    public static PlayerInfo GetJsonTypePlayerInfo(this NetDataReader reader)
    {
        return new PlayerInfo
        {
            Nickname = reader.GetString(),
            Side = reader.GetEnum<EPlayerSide>(),
            Level = reader.GetInt(),
            // PrestigeLevel：不存在于 47888 描述符中（参见对应的 writer 改动）
            MemberCategory = reader.GetEnum<EMemberCategory>(),
            SelectedMemberCategory = reader.GetEnum<EMemberCategory>(),
            SavageLockTime = reader.GetDouble(),
            SavageNickname = reader.GetString(),
            GameVersion = reader.GetString(),
            HasCoopExtension = reader.GetBool(),
            Health = reader.GetEFTProfileHealthInfo()
        };
    }

    /// <summary>
    /// 将出生点配置位置与生成概率权重系数序列化写入 writer 流。
    /// </summary>
    public static void PutWeightedLootPointSpawnPosition(this NetDataWriter writer, WeightedLootPointSpawnPosition target)
    {
        writer.Put(target.Name);
        writer.Put(target.Weight);
        writer.PutClassVector3(target.Position);
        writer.PutClassVector3(target.Rotation);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 WeightedLootPointSpawnPosition 对象。
    /// </summary>
    public static WeightedLootPointSpawnPosition GetWeightedLootPointSpawnPosition(this NetDataReader reader)
    {
        return new WeightedLootPointSpawnPosition
        {
            Name = reader.GetString(),
            Weight = reader.GetFloat(),
            Position = reader.GetClassVector3(),
            Rotation = reader.GetClassVector3()
        };
    }

    /// <summary>
    /// 将四分量数学四元数旋转结构序列化写入 writer 流。
    /// </summary>
    public static void PutClassQuaternion(this NetDataWriter writer, ClassQuaternion target)
    {
        writer.Put(target.x);
        writer.Put(target.y);
        writer.Put(target.z);
        writer.Put(target.w);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ClassQuaternion 对象。
    /// </summary>
    public static ClassQuaternion GetClassQuaternion(this NetDataReader reader)
    {
        return new ClassQuaternion
        {
            x = reader.GetFloat(),
            y = reader.GetFloat(),
            z = reader.GetFloat(),
            w = reader.GetFloat()
        };
    }

    /// <summary>
    /// 将同步的物理对象坐标与原始旋转状态范围序列化写入 writer 流。
    /// </summary>
    public static void PutClassTransformSync(this NetDataWriter writer, ClassTransformSync target)
    {
        writer.PutClassVector3(target.Position);
        writer.PutClassQuaternion(target.Rotation);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ClassTransformSync 对象。
    /// </summary>
    public static ClassTransformSync GetClassTransformSync(this NetDataReader reader)
    {
        return new ClassTransformSync
        {
            Position = reader.GetClassVector3(),
            Rotation = reader.GetClassQuaternion()
        };
    }

    /// <summary>
    /// 将标准三维三元素向量坐标位置映射序列化写入 writer 流。
    /// </summary>
    public static void PutClassVector3(this NetDataWriter writer, ClassVector3 target)
    {
        writer.Put(target.x);
        writer.Put(target.y);
        writer.Put(target.z);
    }

    /// <summary>
    /// 从 reader 流反序列化并重建 ClassVector3 对象。
    /// </summary>
    public static ClassVector3 GetClassVector3(this NetDataReader reader)
    {
        return new ClassVector3
        {
            x = reader.GetFloat(),
            y = reader.GetFloat(),
            z = reader.GetFloat()
        };
    }
}


