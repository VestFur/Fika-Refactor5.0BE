using System;
using CommonAssets.Scripts.Cutscenes;
using EFT;

namespace Fika.Core.Networking.Packets.World;

public enum FinalMissionMessage : byte { State, Outcome, Complete, ExitAuthorized }

/// <summary>主机决策，以及对玩家自身结局的已认证确认。</summary>
public struct FinalMissionPacket : INetSerializable
{
    public FinalMissionMessage Message;
    public int NetId;
    public int OutcomeId;
    public int Seats;
    public int EligiblePlayers;
    public float Remaining;
    public EFinalExitStatus Eligibility;
    public string CutsceneId;
    public ExitStatus ExitStatus;

    public void Deserialize(NetDataReader reader)
    {
        Message = (FinalMissionMessage)reader.GetByte();
        NetId = reader.GetInt();
        switch (Message)
        {
            case FinalMissionMessage.State:
                Seats = reader.GetInt();
                EligiblePlayers = reader.GetInt();
                Remaining = reader.GetFloat();
                Eligibility = (EFinalExitStatus)reader.GetByte();
                if (Seats < 0 || EligiblePlayers < 0 || !float.IsFinite(Remaining) || Remaining < 0 ||
                    Eligibility > EFinalExitStatus.notInZone) throw new InvalidOperationException("Invalid final exit state");
                break;
            case FinalMissionMessage.Outcome:
                OutcomeId = reader.GetInt();
                CutsceneId = reader.GetString(128);
                ExitStatus = (ExitStatus)reader.GetByte();
                if (OutcomeId <= 0 || ExitStatus < ExitStatus.Survived || ExitStatus >= ExitStatus.Transit)
                    throw new InvalidOperationException("Invalid final mission outcome");
                break;
            case FinalMissionMessage.Complete:
            case FinalMissionMessage.ExitAuthorized:
                OutcomeId = reader.GetInt();
                break;
            default: throw new InvalidOperationException("Unknown final mission message");
        }
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Message);
        writer.Put(NetId);
        switch (Message)
        {
            case FinalMissionMessage.State:
                writer.Put(Seats);
                writer.Put(EligiblePlayers);
                writer.Put(Remaining);
                writer.Put((byte)Eligibility);
                break;
            case FinalMissionMessage.Outcome:
                writer.Put(OutcomeId);
                writer.Put(CutsceneId ?? string.Empty);
                writer.Put((byte)ExitStatus);
                break;
            case FinalMissionMessage.Complete:
            case FinalMissionMessage.ExitAuthorized:
                writer.Put(OutcomeId);
                break;
        }
    }
}
