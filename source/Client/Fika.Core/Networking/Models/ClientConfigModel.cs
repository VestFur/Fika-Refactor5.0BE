using System;
using System.Runtime.Serialization;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Models;

[DataContract]
public struct ClientConfigModel
{
    [DataMember(Name = "useBtr")]
    public bool UseBTR { get; set; }

    [DataMember(Name = "friendlyFire")]
    public bool FriendlyFire { get; set; }

    [DataMember(Name = "dynamicVExfils")]
    public bool DynamicVExfils { get; set; }

    [DataMember(Name = "allowFreeCam")]
    public bool AllowFreeCam { get; set; }

    [DataMember(Name = "AllowSpectateFreeCam")]
    public bool AllowSpectateFreeCam { get; set; }

    [DataMember(Name = "allowItemSending")]
    public bool AllowItemSending { get; set; }

    [DataMember(Name = "blacklistedItems")]
    public string[] BlacklistedItems { get; set; }

    [DataMember(Name = "forceSaveOnDeath")]
    public bool ForceSaveOnDeath { get; set; }

    [DataMember(Name = "useInertia")]
    public bool UseInertia { get; set; }

    [DataMember(Name = "sharedQuestProgression")]
    public bool SharedQuestProgression { get; set; }

    [DataMember(Name = "canEditRaidSettings")]
    public bool CanEditRaidSettings { get; set; }

    [DataMember(Name = "enableTransits")]
    public bool EnableTransits { get; set; }

    [DataMember(Name = "anyoneCanStartRaid")]
    public bool AnyoneCanStartRaid { get; set; }

    [DataMember(Name = "allowNamePlates")]
    public bool AllowNamePlates { get; set; }

    [DataMember(Name = "randomLabyrinthSpawns")]
    public bool RandomLabyrinthSpawns { get; set; }

    [DataMember(Name = "pmcFoundInRaid")]
    public bool PMCFoundInRaid { get; set; }

    [DataMember(Name = "allowSpectateBots")]
    public bool AllowSpectateBots { get; set; }

    [DataMember(Name = "instantLoad")]
    public bool InstantLoad { get; set; }

    [DataMember(Name = "fastLoad")]
    public bool FastLoad { get; set; }

    [DataMember(Name = "reviveConfig")]
    public ClientReviveConfig ReviveConfig { get; set; }

    public readonly void LogValues()
    {
        FikaGlobals.LogInfo("Received config from server:");
        foreach (var property in typeof(ClientConfigModel).GetProperties())
        {
            var value = property.GetValue(this);
            if (value is Array valueArray)
            {
                var values = "";
                for (var i = 0; i < valueArray.Length; i++)
                {
                    if (i == 0)
                    {
                        values = valueArray.GetValue(i).ToString();
                        continue;
                    }
                    values = values + ", " + valueArray.GetValue(i);
                }
                FikaGlobals.LogInfo($"[Config] {property.Name}: {values}");
                continue;
            }

            if (value is ClientReviveConfig reviveConfig)
            {
                reviveConfig.LogValues();
                continue;
            }

            FikaGlobals.LogInfo($"[Config] {property.Name}: {value}");
        }
    }
}

public struct ClientReviveConfig
{

    /// <summary>
    /// (2026-10-26 W31) 显式无参构造：C# 要求带字段初始值的 struct 必须有它。
    /// </summary>
    public ClientReviveConfig()
    {
    }
    [DataMember(Name = "enabled")]
    // (2026-10-26 W33) 保持关闭：本机已装独立的「Bring Me To Life / 复活模组」（BepInEx\plugins\RevivalMod.dll +
    // user\mods\RevivalModServer，其服务端配置 enabledTeamRevival=true 且注释写明支持 Fika 队友互救）。
    // 两套系统都会拦 ActiveHealthController.Kill 并各自广播倒地状态，同时开启只会互相打断。
    // 想改用 Fika 自带复活：把 fika-server 配置里的 reviveConfig.enabled 显式设为 true，并停用 RevivalMod。
    public bool Enabled { get; set; } = false;

    [DataMember(Name = "headshotKills")]
    public bool HeadshotKills { get; set; } = true;

    [DataMember(Name = "grenadesKills")]
    public bool GrenadesKills { get; set; } = true;

    [DataMember(Name = "allowLooting")]
    public bool AllowLooting { get; set; }

    [DataMember(Name = "maxRevives")]
    public int MaxRevives { get; set; } = 2;

    [DataMember(Name = "bleedoutTime")]
    public float BleedoutTime { get; set; } = 60f;

    [DataMember(Name = "reviveTime")]
    public float ReviveTime { get; set; } = 5f;

    public readonly void LogValues()
    {
        foreach (var property in typeof(ClientReviveConfig).GetProperties())
        {
            var value = property.GetValue(this);
            if (value is Array valueArray)
            {
                var values = "";
                for (var i = 0; i < valueArray.Length; i++)
                {
                    if (i == 0)
                    {
                        values = valueArray.GetValue(i).ToString();
                        continue;
                    }
                    values = values + ", " + valueArray.GetValue(i);
                }
                FikaGlobals.LogInfo($"[ReviveConfig] {property.Name}: {values}");
                continue;
            }

            FikaGlobals.LogInfo($"[ReviveConfig] {property.Name}: {value}");
        }
    }
}
