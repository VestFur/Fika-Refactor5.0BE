// © 2026 Lacyway All Rights Reserved

using System;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Utils;

/// <summary>
/// One "[CorpseSync]" line per death that reached this machine, so a single raid proves where a
/// victim's death was finalised, whether the corpse exists there and whether that corpse is a
/// lootable container (the corpse itself is the loot container in EFT: <see cref="LootItem.ItemOwner"/>
/// is built by Corpse.method_17 and is what the search interaction needs).
///
/// A client never finalises a death of its own motion: the host is authoritative for AI, so the
/// line for a bot has to appear on the host first and on every client afterwards.
/// </summary>
internal static class CorpseSyncLog
{
    internal const string Prefix = "[CorpseSync]";

    /// <summary>
    /// Logs the state of a victim after its death has been processed on this machine.
    /// </summary>
    /// <param name="victim">The player (or observed puppet) that died.</param>
    /// <param name="side">Which machine logged this: host or client.</param>
    /// <param name="finalisedBy">Who completed the death: the native path or Fika's recovery.</param>
    /// <param name="damageType">The lethal damage type.</param>
    internal static void LogDeath(Player victim, string side, string finalisedBy, EDamageType damageType)
    {
        try
        {
            var fikaPlayer = victim != null ? victim.TryCast<FikaPlayer>() : null;
            var corpse = victim != null ? victim.Corpse : null;
            var info = victim?.Profile?.Info;
            var role = info?.Settings != null ? info.Settings.Role.ToString() : "none";

            Log($"{Prefix} finalisedBy={finalisedBy}, side={side}, victim={victim?.ProfileId}, role={role}, " +
                $"isAI={(victim != null && victim.IsAI)}, isObservedAI={(fikaPlayer != null && fikaPlayer.IsObservedAI)}, " +
                $"corpse={(corpse != null)}, lootContainer={HasLootContainer(corpse)}, cause={damageType}");
        }
        catch (Exception exception)
        {
            Log($"{Prefix} could not inspect the corpse of a victim: {exception}");
        }
    }

    /// <summary>Logs one line with the [CorpseSync] prefix.</summary>
    internal static void Log(string message)
    {
        var logger = FikaPlugin.Instance?.FikaLogger;
        if (logger != null)
        {
            logger.LogInfo(message);
            return;
        }

        FikaGlobals.LogInfo(message);
    }

    /// <summary>
    /// True when the corpse carries the loot container (the search interaction) the game builds
    /// in Corpse.method_17. A corpse without it can never be looted.
    /// </summary>
    private static bool HasLootContainer(Corpse corpse)
    {
        try
        {
            return corpse != null && corpse.ItemOwner != null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
