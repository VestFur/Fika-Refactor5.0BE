// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Utils;

/// <summary>
/// One-shot, per-raid diagnostic for the values third-party mods read to classify
/// players. "SPT Dynamic Maps" (SPT.DynamicMaps) sorts its player markers through
/// <c>Player.Profile.Side</c> (1 = Usec, 2 = Bear, 4 = Savage), <c>Player.Profile.Info.Settings.Role</c>,
/// <c>Player.Profile.Info.MemberCategory</c> and <c>Player.GroupId</c> (<c>Profile.Info.GroupId</c>),
/// so a single raid log proves exactly what Fika hands those mods for the local player, remote
/// players, PMC bots, scav bots and bosses.
/// <para>
/// The same lines carry a <c>[Dogtag]</c> report for every death: the victim, the aggressor
/// Fika's killer plumbing resolved and the values that end up on the tag.
/// </para>
/// </summary>
internal static class MapMarkerDiagnostics
{
    private const string _prefix = "[MapMarker]";
    private const string _dogtagPrefix = "[Dogtag]";
    private const string _nameplatePrefix = "[Nameplate]";

    /// <summary>
    /// The roles "SPT Dynamic Maps" treats as a tracked boss (GameUtils._trackedBosses), so the
    /// log reports the same category the mod will pick.
    /// </summary>
    private static readonly HashSet<WildSpawnType> _modTrackedBosses =
    [
        (WildSpawnType)32, (WildSpawnType)3, (WildSpawnType)11, (WildSpawnType)6, (WildSpawnType)26,
        (WildSpawnType)27, (WildSpawnType)28, (WildSpawnType)43, (WildSpawnType)7, (WildSpawnType)17,
        (WildSpawnType)22, (WildSpawnType)47, (WildSpawnType)29, (WildSpawnType)25, (WildSpawnType)35,
        (WildSpawnType)21, (WildSpawnType)65, (WildSpawnType)66, (WildSpawnType)67, (WildSpawnType)199,
        (WildSpawnType)801
    ];

    private static readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _loggedNameplates = new(StringComparer.Ordinal);

    /// <summary>
    /// A new local player means a new raid: log every participant of it again.
    /// </summary>
    internal static void ResetForRaid()
    {
        _logged.Clear();
        _loggedNameplates.Clear();
    }

    /// <summary>
    /// One line per player per raid about the nameplate: whether Fika created one and the exact
    /// text it shows. The plate reads <c>Info.MainProfileNickname</c> (with a
    /// <c>Info.Nickname</c> fallback) while map mods read <c>Info.Nickname</c>, so this proves
    /// both labels from the profile the observing machine actually built.
    /// </summary>
    /// <param name="player">The player the plate belongs to.</param>
    /// <param name="source">Why the line is written (plate / plate-not-allowed / ...).</param>
    /// <param name="created">True when a plate object exists for the player.</param>
    /// <param name="shownName">The text the plate displays, when it was created.</param>
    internal static void LogNameplate(Player player, string source, bool created, string shownName)
    {
        if (player == null)
        {
            return;
        }

        try
        {
            var profileId = player.ProfileId;
            if (!string.IsNullOrEmpty(profileId) && !_loggedNameplates.Add(profileId))
            {
                return;
            }

            var info = player.Profile?.Info;
            var fikaPlayer = player.TryCast<FikaPlayer>();
            FikaPlugin.Instance.FikaLogger.LogInfo($"{_nameplatePrefix} source={source}, profileId={profileId}, " +
                $"created={created}, shownName='{shownName}', Nickname='{info?.Nickname}', " +
                $"MainProfileNickname='{info?.MainProfileNickname}', " +
                $"isObservedAI={(fikaPlayer != null && fikaPlayer.IsObservedAI)}, " +
                $"allowNamePlates={FikaPlugin.Instance.Settings.AllowNamePlates}, " +
                $"useNamePlates={FikaPlugin.Instance.Settings.UseNamePlates.Value}, " +
                $"groupId=[{player.GroupId}], teamId=[{player.TeamId}], " +
                $"modCategory={ClassifyAsMod(player)}");
        }
        catch (Exception exception)
        {
            FikaPlugin.Instance.FikaLogger.LogWarning($"{_nameplatePrefix} could not inspect the plate ({source}): {exception}");
        }
    }

    /// <summary>
    /// Logs one line per player per raid.
    /// </summary>
    /// <param name="player">The player to describe.</param>
    /// <param name="source">Where the player came from (local/observed/bot).</param>
    internal static void Log(Player player, string source)
    {
        if (player == null)
        {
            return;
        }

        try
        {
            var profileId = player.ProfileId;
            if (!string.IsNullOrEmpty(profileId) && !_logged.Add(profileId))
            {
                return;
            }

            var profile = player.Profile;
            var info = profile?.Info;
            var settings = info?.Settings;
            var fikaPlayer = player.TryCast<FikaPlayer>();

            var side = info != null ? info.Side.ToString() : "none";
            var role = settings != null ? settings.Role.ToString() : "none";
            var memberCategory = info != null ? info.MemberCategory.ToString() : "none";
            var isObservedAi = fikaPlayer != null && fikaPlayer.IsObservedAI;

            FikaPlugin.Instance.FikaLogger.LogInfo($"{_prefix} source={source}, profileId={profileId}, " +
                $"side={side}, role={role}, isAI={player.IsAI}, isYourPlayer={player.IsYourPlayer}, " +
                $"memberCategory={memberCategory}, groupId=[{player.GroupId}], teamId=[{player.TeamId}], " +
                $"isObservedAI={isObservedAi}, playerType={player.GetIl2CppType().Name}, " +
                $"modCategory={ClassifyAsMod(player)}");
        }
        catch (Exception exception)
        {
            FikaPlugin.Instance.FikaLogger.LogWarning($"{_prefix} could not inspect player ({source}): {exception}");
        }
    }

    /// <summary>
    /// Repeats the classification of a player after its side was corrected, so one raid shows the
    /// values the mod reads at the moment it builds the marker.
    /// </summary>
    /// <param name="player">The player to describe.</param>
    /// <param name="source">Where the correction happened.</param>
    internal static void LogClassified(Player player, string source)
    {
        if (player == null)
        {
            return;
        }

        try
        {
            var info = player.Profile?.Info;
            FikaPlugin.Instance.FikaLogger.LogInfo($"{_prefix} source={source}, profileId={player.ProfileId}, " +
                $"side={(info != null ? info.Side.ToString() : "none")}, " +
                $"role={(info?.Settings != null ? info.Settings.Role.ToString() : "none")}, " +
                $"groupId=[{player.GroupId}], modCategory={ClassifyAsMod(player)}");
        }
        catch (Exception exception)
        {
            FikaPlugin.Instance.FikaLogger.LogWarning($"{_prefix} could not inspect player ({source}): {exception}");
        }
    }

    /// <summary>
    /// Mirrors the classification of "SPT Dynamic Maps"
    /// (GameUtils.IsGroupedWithMainPlayer / IsTrackedBoss / IsPMC / IsScav, first match wins, see
    /// OtherPlayersMarkerProvider.TryAddMarker) so the log states the category the mod will pick.
    /// </summary>
    /// <param name="player">The player to classify.</param>
    /// <returns>Friendly Player, Boss, Enemy Player, Scav or none.</returns>
    internal static string ClassifyAsMod(Player player)
    {
        var info = player?.Profile?.Info;
        if (info == null)
        {
            return "none";
        }

        var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
        var mainGroup = mainPlayer != null ? mainPlayer.GroupId : null;
        if (!string.IsNullOrEmpty(mainGroup) && player.GroupId == mainGroup)
        {
            return "Friendly Player";
        }

        var role = info.Settings != null ? info.Settings.Role : WildSpawnType.assault;
        if (info.Side == EPlayerSide.Savage && _modTrackedBosses.Contains(role))
        {
            return "Boss";
        }

        if (info.Side is EPlayerSide.Bear or EPlayerSide.Usec)
        {
            return "Enemy Player";
        }

        if (info.Side == EPlayerSide.Savage)
        {
            return "Scav";
        }

        return "none";
    }

    /// <summary>
    /// One line per dog tag event. It shows the victim (profile, role and the side SPT actually
    /// stored against the side the tag must carry), the aggressor Fika's killer plumbing resolved
    /// and the tag fields that were written, so one raid proves both halves of the tag.
    /// </summary>
    /// <param name="victim">The player whose dog tag is being handled.</param>
    /// <param name="outcome">What happened to the tag (generated/skipped/missing-item/...).</param>
    /// <param name="faction">The side the tag must carry for the victim's role.</param>
    /// <param name="component">The dog tag component that was written, when there is one.</param>
    /// <param name="aggressor">The aggressor Fika resolved, when there is one.</param>
    internal static void LogDogtag(Player victim, string outcome, EPlayerSide faction,
        DogtagComponent component = null, IPlayer aggressor = null)
    {
        try
        {
            var info = victim?.Profile?.Info;
            var fikaPlayer = victim?.TryCast<FikaPlayer>();
            var aggressorInfo = aggressor?.Profile?.Info;
            var victimId = victim != null ? victim.ProfileId : "none";
            var victimRole = info?.Settings != null ? info.Settings.Role.ToString() : "none";
            var victimIsAi = victim != null && victim.IsAI;
            var victimIsObservedAi = fikaPlayer != null && fikaPlayer.IsObservedAI;
            var aggressorId = aggressor != null ? aggressor.ProfileId : "none";
            var aggressorRole = aggressorInfo?.Settings != null ? aggressorInfo.Settings.Role.ToString() : "none";
            var aggressorIsAi = aggressor != null && aggressor.IsAI;
            var aggressorName = aggressor?.Profile != null ? aggressor.Profile.Nickname : "none";
            var aggressorMainName = aggressorInfo != null ? aggressorInfo.MainProfileNickname : "none";
            var aggressorIsCoop = aggressor != null && aggressor.IsCoopPlayer();

            FikaPlugin.Instance.FikaLogger.LogInfo($"{_dogtagPrefix} {outcome} victim profileId={victimId}, " +
                $"nickname='{info?.Nickname}', role={victimRole}, profileSide={info?.Side}, tagSide={faction}, " +
                $"isAI={victimIsAi}, isObservedAI={victimIsObservedAi}, aggressor profileId={aggressorId}, " +
                $"role={aggressorRole}, isAI={aggressorIsAi}, name='{aggressorName}', " +
                $"mainName='{aggressorMainName}', isCoopPlayer={aggressorIsCoop}, " +
                $"tagComponent={(component != null)}, Nickname='{component?.Nickname}', Side={component?.Side}, " +
                $"Level={component?.Level}, Status='{component?.Status}', KillerName='{component?.KillerName}', " +
                $"KillerProfileId='{component?.KillerProfileId}', WeaponName='{component?.WeaponName}', " +
                $"tagGroupId=[{component?.GroupId}]");
        }
        catch (Exception exception)
        {
            FikaPlugin.Instance.FikaLogger.LogWarning($"{_dogtagPrefix} could not inspect the dog tag ({outcome}): {exception}");
        }
    }
}
