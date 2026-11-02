using System;

namespace Fika.Core.Main.Utils;

/// <summary>战局内的复活状态。通过网络传输的是时长，绝不是进程内时钟的读数。</summary>
public struct RevivalState : INetSerializable
{
    public bool IsAlive;
    public bool Downed;
    public bool BledOut;
    public int Revives;
    // 负数表示定时流血死亡被禁用；零表示已经到期。
    public float BleedoutRemaining;
    public float RevivePauseRemaining;
    public string ReviverNickname;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(IsAlive);
        writer.Put(Downed);
        writer.Put(BledOut);
        writer.Put(Revives);
        writer.Put(BleedoutRemaining);
        writer.Put(RevivePauseRemaining);
        writer.Put(ReviverNickname ?? string.Empty);
    }

    public void Deserialize(NetDataReader reader)
    {
        IsAlive = reader.GetBool();
        Downed = reader.GetBool();
        BledOut = reader.GetBool();
        Revives = reader.GetInt();
        BleedoutRemaining = reader.GetFloat();
        RevivePauseRemaining = reader.GetFloat();
        ReviverNickname = reader.GetString();
        if (Revives < 0 || !float.IsFinite(BleedoutRemaining) || !float.IsFinite(RevivePauseRemaining) ||
            RevivePauseRemaining < 0 || (Downed && (!IsAlive || BledOut)))
            throw new InvalidOperationException("Invalid revival snapshot");
    }
}

/// <summary>当玩家进程断开或正在加载时，它也会在主机上推进。</summary>
internal sealed class RevivalStateClock
{
    private RevivalState _state = new() { IsAlive = true, BleedoutRemaining = -1f };
    private double _receivedAt;

    internal RevivalState Capture()
    {
        var state = _state;
        var elapsed = (float)Math.Max(0, Time.unscaledTimeAsDouble - _receivedAt);
        if (state.Downed)
        {
            if (state.BleedoutRemaining >= 0)
                state.BleedoutRemaining = Math.Max(0, state.BleedoutRemaining - Math.Max(0, elapsed - state.RevivePauseRemaining));
            state.RevivePauseRemaining = Math.Max(0, state.RevivePauseRemaining - elapsed);
            if (state.RevivePauseRemaining == 0) state.ReviverNickname = string.Empty;
        }
        return state;
    }

    internal void Restore(RevivalState state)
    {
        _state = state;
        _receivedAt = Time.unscaledTimeAsDouble;
    }

    internal void SetDowned(bool downed, float duration)
    {
        var state = Capture();
        if (state.Downed == downed) return;
        state.IsAlive = true;
        state.Downed = downed;
        if (!downed) state.Revives++;
        state.BleedoutRemaining = downed && duration > 0 ? duration : -1f;
        state.RevivePauseRemaining = 0;
        state.ReviverNickname = string.Empty;
        Restore(state);
    }

    internal void SetReviving(bool reviving, string nickname, float duration)
    {
        var state = Capture();
        if (!state.Downed) return;
        // 断线的施救者不能把这个计时器无限期地留在暂停状态。
        state.RevivePauseRemaining = reviving ? Math.Max(0, duration) : 0;
        state.ReviverNickname = reviving ? nickname : string.Empty;
        Restore(state);
    }

    internal void MarkDead()
    {
        var state = Capture();
        state.BledOut |= state.Downed;
        state.IsAlive = false;
        state.Downed = false;
        state.RevivePauseRemaining = 0;
        state.ReviverNickname = string.Empty;
        Restore(state);
    }
}
