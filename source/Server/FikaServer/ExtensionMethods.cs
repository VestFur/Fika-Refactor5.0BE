using FikaServer.Models.Fika.Dialog;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using static FikaShared.Enums;

namespace FikaServer;

public static class ExtensionMethods
{
    /// <summary>
    /// 从 <see cref="SptProfile"/> 生成
    /// </summary>
    /// <param name="profile"></param>
    /// <returns>新的 <see cref="FriendData"/></returns>
    public static FriendData ToFriendData(this SptProfile profile)
    {
        return new()
        {
            Aid = profile.ProfileInfo.Aid,
            Id = profile.ProfileInfo.ProfileId,
            Info = new()
            {
                Level = profile.CharacterData.PmcData.Info.Level,
                MemberCategory = profile.CharacterData.PmcData.Info.MemberCategory,
                SelectedMemberCategory = profile.CharacterData.PmcData.Info.SelectedMemberCategory,
                Nickname = profile.CharacterData.PmcData.Info.Nickname,
                Side = profile.CharacterData.PmcData.Info.Side
            }
        };
    }

    /// <summary>
    /// 从 <see cref="PmcData"/> 生成
    /// </summary>
    /// <param name="profile"></param>
    /// <returns>新的 <see cref="FriendData"/></returns>
    public static FriendData ToFriendData(this PmcData pmcData)
    {
        return new()
        {
            Aid = pmcData.Aid,
            Id = pmcData.Id,
            Info = new()
            {
                Level = pmcData.Info.Level,
                MemberCategory = pmcData.Info.MemberCategory,
                SelectedMemberCategory = pmcData.Info.SelectedMemberCategory,
                Nickname = pmcData.Info.Nickname,
                Side = pmcData.Info.Side
            }
        };
    }

    /// <summary>
    /// 检查该档案是否拥有可用于获取 <see cref="Info.ProfileId"/> 的有效数据
    /// </summary>
    /// <param name="profile"></param>
    /// <returns></returns>
    public static bool HasProfileData(this SptProfile? profile)
    {
        return profile != null && profile.ProfileInfo?.ProfileId != null && profile.CharacterData?.PmcData?.Info?.Nickname != null;
    }

    public static EFikaLocation ToFikaLocation(this string location)
    {
        return location switch
        {
            "bigmap" => EFikaLocation.Customs,
            "factory4_day" or "factory4_night" => EFikaLocation.Factory,
            "interchange" => EFikaLocation.Interchange,
            "laboratory" => EFikaLocation.Laboratory,
            "labyrinth" => EFikaLocation.Labyrinth,
            "lighthouse" => EFikaLocation.Lighthouse,
            "rezervbase" => EFikaLocation.Reserve,
            "sandbox" or "sandbox_high" => EFikaLocation.GroundZero,
            "shoreline" => EFikaLocation.Shoreline,
            "tarkovstreets" => EFikaLocation.Streets,
            "woods" => EFikaLocation.Woods,
            _ => EFikaLocation.None,
        };
    }

    /// <summary>
    /// 检查给定档案是否为 headless 档案
    /// </summary>
    /// <param name="profile">要检查的档案</param>
    /// <returns>若为 headless 档案则为 <see langword="true"/></returns>
    public static bool IsHeadlessProfile(this SptProfile profile)
    {
        return profile?.CharacterData?.PmcData?.Info?.MemberCategory is MemberCategory.UnitTest;
    }
}
