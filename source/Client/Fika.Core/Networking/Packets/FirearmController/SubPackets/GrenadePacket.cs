using EFT.InventoryLogic;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.FirearmController.SubPackets;

public sealed class GrenadePacket : IPoolSubPacket
{
    private GrenadePacket()
    {

    }

    public static GrenadePacket FromValue(Quaternion grenadeRotation, Vector3 grenadePosition, Vector3 throwForce,
        EGrenadePacketType type, bool hasGrenade, bool lowThrow, bool plantTripwire, bool changeToIdle, bool changeToPlant)
    {
        var packet = FirearmSubPacketPoolManager.Instance.GetPacket<GrenadePacket>(EFirearmSubPacketType.Grenade);
        packet.GrenadeRotation = grenadeRotation;
        packet.GrenadePosition = grenadePosition;
        packet.ThrowForce = throwForce;
        packet.Type = type;
        packet.HasGrenade = hasGrenade;
        packet.LowThrow = lowThrow;
        packet.PlantTripwire = plantTripwire;
        packet.ChangeToIdle = changeToIdle;
        packet.ChangeToPlant = changeToPlant;
        return packet;
    }

    public static GrenadePacket CreateInstance()
    {
        return new();
    }

    public Quaternion GrenadeRotation;
    public Vector3 GrenadePosition;
    public Vector3 ThrowForce;
    public EGrenadePacketType Type;
    public bool HasGrenade;
    public bool LowThrow;
    public bool PlantTripwire;
    public bool ChangeToIdle;
    public bool ChangeToPlant;

    public void Execute(FikaPlayer player)
    {
        // HandsController 是一个 IL2CPP 包装器。CLR 模式匹配能看到的
        // 是生成的基类包装器，即使本机对象其实是我们的
        // 注入子类型，所以做原生类型判断时务必使用 TryCast。
        if (player.HandsController?.TryCast<ObservedGrenadeController>() is { } controller)
        {
            switch (Type)
            {
                case EGrenadePacketType.ExamineWeapon:
                    {
                        controller.ExamineWeapon();
                        break;
                    }
                case EGrenadePacketType.HighThrow:
                    {
                        controller.HighThrow();
                        break;
                    }
                case EGrenadePacketType.LowThrow:
                    {
                        controller.LowThrow();
                        break;
                    }
                case EGrenadePacketType.PullRingForHighThrow:
                    {
                        controller.PullRingForHighThrow();
                        break;
                    }
                case EGrenadePacketType.PullRingForLowThrow:
                    {
                        controller.PullRingForLowThrow();
                        break;
                    }
            }
            if (HasGrenade)
            {
                controller.SpawnGrenade(0f, GrenadePosition, GrenadeRotation, ThrowForce, LowThrow);
            }

            if (PlantTripwire)
            {
                controller.PlantTripwire();
            }

            if (ChangeToIdle)
            {
                controller.ChangeFireMode(Weapon.EFireMode.grenadeThrowing);
            }

            if (ChangeToPlant)
            {
                controller.ChangeFireMode(Weapon.EFireMode.greanadePlanting);
            }
        }
        else if (player.HandsController?.TryCast<ObservedQuickGrenadeController>() is { } quickGrenadeController)
        {
            if (HasGrenade)
            {
                quickGrenadeController.SpawnGrenade(0f, GrenadePosition, GrenadeRotation, ThrowForce, LowThrow);
            }
        }
        else
        {
            FikaGlobals.LogError($"GrenadePacket: HandsController was not an observed grenade controller. Was {player.HandsController?.GetType().Name ?? "null"}");
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.PutEnum(Type);
        writer.Put(HasGrenade);
        if (HasGrenade)
        {
            writer.PutUnmanaged(GrenadeRotation);
            writer.PutUnmanaged(GrenadePosition);
            writer.PutUnmanaged(ThrowForce);
            writer.Put(LowThrow);
        }
        writer.Put(PlantTripwire);
        writer.Put(ChangeToIdle);
        writer.Put(ChangeToPlant);
    }

    public void Deserialize(NetDataReader reader)
    {
        Type = reader.GetEnum<EGrenadePacketType>();
        HasGrenade = reader.GetBool();
        if (HasGrenade)
        {
            GrenadeRotation = reader.GetUnmanaged<Quaternion>();
            GrenadePosition = reader.GetUnmanaged<Vector3>();
            ThrowForce = reader.GetUnmanaged<Vector3>();
            LowThrow = reader.GetBool();
        }
        PlantTripwire = reader.GetBool();
        ChangeToIdle = reader.GetBool();
        ChangeToPlant = reader.GetBool();
    }

    public void Dispose()
    {
        GrenadeRotation = default;
        GrenadePosition = default;
        ThrowForce = default;
        Type = default;
        HasGrenade = false;
        LowThrow = false;
        PlantTripwire = false;
        ChangeToIdle = false;
        ChangeToPlant = false;
    }
}

