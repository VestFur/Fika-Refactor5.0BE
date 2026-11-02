namespace Fika.Core.Networking.Packets.World;

/// <summary>转发 native 的周期性时间线校正，包括中途加入的情况。</summary>
public struct CutsceneTimePacket : INetSerializable
{
    public int ProcessId;
    public string CutsceneId;
    public float ElapsedTime;

    public void Deserialize(NetDataReader reader)
    {
        ProcessId = reader.GetInt();
        CutsceneId = reader.GetString();
        ElapsedTime = reader.GetFloat();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(ProcessId);
        writer.Put(CutsceneId);
        writer.Put(ElapsedTime);
    }
}
