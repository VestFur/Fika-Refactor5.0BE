using System.Collections.Generic;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Main.Utils;
using System;
using System.Threading.Tasks;
using CommonAssets.Scripts.Game;
using EFT;
using EFT.Interactive;

namespace Fika.Core.Main.Components;

/// <summary>
/// Tracks raid dialog entry points (trader/cutscene terminals) that are
/// currently occupied.  The host is authoritative; clients only apply the
/// occupancy snapshots and state changes received from it.
/// </summary>
public static class DialogEntryOccupancy
{
    private static readonly Dictionary<string, int> _occupants = new();
    private static readonly Dictionary<string, TaskCompletionSource<bool>> _claims = new();
    private static RaidDialogEntryController _native;
    private static Il2CppSystem.Action<string> _changed;
    private static bool _applying;
    private static bool _requested;

    public static IReadOnlyDictionary<string, int> Current => _occupants;

    internal static void Bind(RaidDialogEntryController controller)
    {
        Reset();
        _native = controller;
        _changed = (Action<string>)OnNativeChanged;
        _native.add_PointOccupancyChanged(_changed);
    }

    internal static void Reset()
    {
        if (_native != null && _changed != null) _native.remove_PointOccupancyChanged(_changed);
        foreach (var claim in _claims.Values) claim.TrySetResult(false);
        _claims.Clear();
        _occupants.Clear();
        _native = null;
        _changed = null;
        _requested = _applying = false;
    }

    internal static void Tick()
    {
        var manager = FikaSingleton<IFikaNetworkManager>.Instance;
        if (!_requested && _native != null && FikaBackendUtils.IsClient && manager?.CoopHandler?.MyPlayer != null)
        {
            _requested = true;
            manager.SendGenericPacket(EGenericSubPacketType.DialogEntry, DialogEntryPacket.RequestFullState());
        }
        if (FikaBackendUtils.IsServer && manager?.CoopHandler?.Players != null)
        {
            var dead = new HashSet<int>();
            foreach (var occupant in _occupants.Values)
                if (!manager.CoopHandler.Players.TryGetValue(occupant, out var player) ||
                    player.HealthController?.IsAlive != true || manager.CoopHandler.ExtractedPlayers.Contains(occupant))
                    dead.Add(occupant);
            foreach (var occupant in dead) ReleaseAllFor(occupant);
        }
    }

    private static void OnNativeChanged(string pointId)
    {
        if (_applying || _native == null) return;
        _native._occupantByPointId.TryGetValue(pointId, out var raidId);
        var occupant = RaidInteractionSync.ToNetId(raidId);
        _occupants.TryGetValue(pointId, out var previous);
        if (occupant == 0) _occupants.Remove(pointId);
        else _occupants[pointId] = occupant;
        if (FikaBackendUtils.IsServer) Broadcast(DialogEntryPacket.Occupancy(pointId, occupant));
        else if (occupant == 0 && previous == FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.MyPlayer?.NetId)
            FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.DialogEntry,
                DialogEntryPacket.Release(pointId, previous));
    }

    internal static bool HasLocalClaim(string pointId)
    {
        var player = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.MyPlayer;
        return player != null && _occupants.TryGetValue(pointId, out var occupant) && occupant == player.NetId;
    }

    internal static async Task StartDialog(RaidDialogEntryPoint point)
    {
        var player = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler?.MyPlayer;
        var controller = _native;
        if (player == null || controller == null || _claims.ContainsKey(point.PointId)) return;
        var pointId = point.PointId;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _claims.Add(pointId, completion);
        try
        {
            FikaSingleton<IFikaNetworkManager>.Instance.SendGenericPacket(EGenericSubPacketType.DialogEntry,
                DialogEntryPacket.Claim(pointId, player.NetId));
            if (await Task.WhenAny(completion.Task, Task.Delay(10000)) != completion.Task ||
                !await completion.Task || _native?.Pointer != controller.Pointer || point == null ||
                player.HealthController?.IsAlive != true || !point.DistanceCheck(player.Position))
            {
                if (_native?.Pointer == controller.Pointer)
                    FikaSingleton<IFikaNetworkManager>.Instance?.SendGenericPacket(EGenericSubPacketType.DialogEntry,
                        DialogEntryPacket.Release(pointId, player.NetId));
                return;
            }
            // Re-enter the native offline dialog only after the host has granted the claim.
            // Its close callback still calls ReleaseOffline and therefore OnNativeChanged.
            await point.StartDialogOffline().AsManaged();
        }
        catch
        {
            if (_native?.Pointer == controller.Pointer)
                FikaSingleton<IFikaNetworkManager>.Instance?.SendGenericPacket(EGenericSubPacketType.DialogEntry,
                    DialogEntryPacket.Release(pointId, player.NetId));
            throw;
        }
        finally
        {
            if (_claims.TryGetValue(pointId, out var pending) && pending == completion) _claims.Remove(pointId);
        }
    }

    public static void HandleClaimRequest(string pointId, int occupantId)
    {
        if (!FikaBackendUtils.IsServer) return;
        if (string.IsNullOrEmpty(pointId)) return;
        if (_native == null || occupantId == 0 || _native.IsOccupiedByOther(pointId, RaidInteractionSync.ToRaidId(occupantId))) return;
        _native.TryClaimOffline(pointId, RaidInteractionSync.ToRaidId(occupantId));
    }

    public static void HandleReleaseRequest(string pointId, int occupantId)
    {
        if (!FikaBackendUtils.IsServer) return;
        if (string.IsNullOrEmpty(pointId)) return;
        if (_occupants.TryGetValue(pointId, out var current) && current == occupantId && _native != null)
        {
            // The player may already have left the world; use the native stored ID.
            if (_native._occupantByPointId.TryGetValue(pointId, out var raidId)) _native.ReleaseOffline(pointId, raidId);
        }
    }

    public static void HandleFullStateRequest()
    {
        if (FikaBackendUtils.IsServer) Broadcast(DialogEntryPacket.FullState(new(_occupants)));
    }

    public static void ApplyOccupancy(string pointId, int occupantId)
    {
        if (FikaBackendUtils.IsServer) return;
        if (string.IsNullOrEmpty(pointId)) return;
        if (occupantId == 0) _occupants.Remove(pointId);
        else _occupants[pointId] = occupantId;
        try
        {
            _applying = true;
            _native?.ApplyOccupancyState(pointId, RaidInteractionSync.ToRaidId(occupantId));
        }
        finally { _applying = false; }
        if (_claims.TryGetValue(pointId, out var claim)) claim.TrySetResult(HasLocalClaim(pointId));
    }

    public static void ApplyFullState(IEnumerable<KeyValuePair<string, int>> occupants)
    {
        if (FikaBackendUtils.IsServer) return;
        // Clear absent entries in the native controller as well as the managed snapshot.
        var snapshot = new Dictionary<string, int>();
        foreach (var pair in occupants)
            if (!string.IsNullOrEmpty(pair.Key) && pair.Value != 0) snapshot[pair.Key] = pair.Value;
        foreach (var point in new List<string>(_occupants.Keys))
            if (!snapshot.ContainsKey(point)) ApplyOccupancy(point, 0);
        foreach (var pair in snapshot)
            if (!string.IsNullOrEmpty(pair.Key) && pair.Value != 0) ApplyOccupancy(pair.Key, pair.Value);
    }

    public static void ReleaseAllFor(int occupantId)
    {
        var released = new List<string>();
        foreach (var pair in _occupants)
            if (pair.Value == occupantId) released.Add(pair.Key);
        foreach (var pointId in released)
        {
            HandleReleaseRequest(pointId, occupantId);
        }
    }

    private static void Broadcast(DialogEntryPacket packet)
    {
        var manager = FikaSingleton<IFikaNetworkManager>.Instance;
        manager?.SendGenericPacket(EGenericSubPacketType.DialogEntry, packet, true);
    }
}
