using System.Collections.Generic;

namespace Fika.Core.Networking.Packets.World;

public struct PasscodeInitPacket(bool isRequest) : INetSerializable
{
    public bool IsRequest = isRequest;
    public Dictionary<string, string[]> Hints;

    /// <summary>
    /// BO (analysis/icebreaker-keypad-2)：主机所在位置的通行码 id（id -> 列表 id），当主机的
    /// 控制器还没启动时为 null。此次移植的两端发送和读取同一种结构；该字段在
    /// 网络传输中是可选的，这样旧构建的节点仍能理解该包（反之亦然）。
    /// </summary>
    public Dictionary<string, string> Entries;

    /// <summary>
    /// BU (analysis/icebreaker-keypad-3)：本战局的密码表（通行码 id -> 密码），由主机
    /// 用游戏自己的生成器抽取。它是两台机器共同校验的那一份，因此客户端可以运行
    /// 游戏自己的本地校验，而不必每次尝试都去询问主机。当主机尚未
    /// 抽出密码时为 null；该字段是一个可选的尾部块，因此旧构建的节点仍能
    /// 理解该包（反之亦然）。
    /// </summary>
    public Dictionary<string, string> Codes;

    public void Deserialize(NetDataReader reader)
    {
        IsRequest = reader.GetBool();
        if (IsRequest)
        {
            return;
        }

        var amount = reader.GetUShort();
        Hints = new(amount);
        for (var i = 0; i < amount; i++)
        {
            var passcodeId = reader.GetString();
            var hintAmount = reader.GetUShort();
            var hints = new string[hintAmount];
            for (var j = 0; j < hintAmount; j++)
            {
                hints[j] = reader.GetString();
            }

            Hints[passcodeId] = hints;
        }

        // 可选的通行码 id 块只在发送方写过时才读取。LiteNetLib 在这里不会追加
        // 自己的内容，因此写入方自己的尾部标志就是判断依据。
        if (reader.AvailableBytes <= 0)
        {
            return;
        }

        if (!reader.GetBool())
        {
            return;
        }

        var entryAmount = reader.GetUShort();
        Entries = new(entryAmount);
        for (var i = 0; i < entryAmount; i++)
        {
            var passcodeId = reader.GetString();
            Entries[passcodeId] = reader.GetString();
        }

        // BU (analysis/icebreaker-keypad-3)：本快照的可选密码表，按与上面
        // 那个块相同的规则读取——只在发送方确实写过时才读。
        if (reader.AvailableBytes <= 0)
        {
            return;
        }

        if (!reader.GetBool())
        {
            return;
        }

        var codeAmount = reader.GetUShort();
        Codes = new(codeAmount);
        for (var i = 0; i < codeAmount; i++)
        {
            var passcodeId = reader.GetString();
            Codes[passcodeId] = reader.GetString();
        }
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(IsRequest);
        if (IsRequest)
        {
            return;
        }

        writer.Put((ushort)Hints.Count);
        foreach ((var passcodeId, var hints) in Hints)
        {
            writer.Put(passcodeId);
            writer.Put((ushort)hints.Length);
            for (var i = 0; i < hints.Length; i++)
            {
                writer.Put(hints[i]);
            }
        }

        // BO (analysis/icebreaker-keypad-2)：本快照的可选通行码 id 块。
        writer.Put(Entries != null);
        if (Entries != null)
        {
            writer.Put((ushort)Entries.Count);
            foreach ((var passcodeId, var listId) in Entries)
            {
                writer.Put(passcodeId);
                writer.Put(listId ?? string.Empty);
            }
        }

        // BU (analysis/icebreaker-keypad-3)：本快照的可选密码表（本战局的密码）。
        writer.Put(Codes != null);
        if (Codes == null)
        {
            return;
        }

        writer.Put((ushort)Codes.Count);
        foreach ((var passcodeId, var code) in Codes)
        {
            writer.Put(passcodeId);
            writer.Put(code ?? string.Empty);
        }
    }
}
