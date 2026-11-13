using CommonAssets.Scripts.Game;
using EFT;
using EFT.Weather;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;

namespace Fika.Core.Networking.Packets.World;

public static class RequestSubPackets
{
    public class SpawnPointRequest : IRequestPacket
    {
        public string Infiltration;
        public Vector3 Position;
        public Quaternion Rotation;

        public SpawnPointRequest(string name, Vector3 position, Quaternion rotation)
        {
            Infiltration = name;
            Position = position;
            Rotation = rotation;
        }

        public SpawnPointRequest()
        {

        }

        public SpawnPointRequest(NetDataReader reader)
        {
            Infiltration = reader.GetString();
            Position = reader.GetUnmanaged<Vector3>();
            Rotation = reader.GetUnmanaged<Quaternion>();
        }

        public void HandleRequest(NetPeer peer, FikaServer server)
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame != null)
            {
                if (FikaBackendUtils.IsServer && !string.IsNullOrEmpty(fikaGame.GameController.InfiltrationPoint) && fikaGame.GameController.SpawnPoint != null)
                {
                    RequestPacket response = new()
                    {
                        Type = ERequestSubPacketType.SpawnPoint,
                        RequestSubPacket = new SpawnPointRequest(fikaGame.GameController.InfiltrationPoint,
                        fikaGame.GameController.SpawnPoint.Position, fikaGame.GameController.SpawnPoint.Rotation)
                    };

                    server.SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
                    return;
                }
            }

            FikaGlobals.LogError("SpawnPointRequest::HandleRequest: CoopGame was null upon receiving packet!");
        }

        public void HandleResponse()
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame != null)
            {
                if (!string.IsNullOrEmpty(Infiltration))
                {
                    fikaGame.GameController.InfiltrationPoint = Infiltration;
                    fikaGame.GameController.ClientSpawnPosition = Position;
                    fikaGame.GameController.ClientSpawnRotation = Rotation;
                    FikaGlobals.LogInfo($"Received spawn position from host: {Position:F2}, rotation: {Rotation:F2}");
                    return;
                }
                FikaGlobals.LogError("SpawnPointRequest::HandleResponse: Infiltration was empty!");
                return;
            }

            FikaGlobals.LogError("SpawnPointRequest::HandleResponse: CoopGame was null upon receiving packet!");
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Infiltration);
            writer.PutUnmanaged(Position);
            writer.PutUnmanaged(Rotation);
        }
    }

    public class WeatherRequest : IRequestPacket
    {
        public ESeason Season;
        public Vector3 SpringSnowFactor;
        public WeatherNode[] WeatherClasses;

        public WeatherRequest()
        {

        }

        public WeatherRequest(NetDataReader reader)
        {
            Season = reader.GetEnum<ESeason>();
            SpringSnowFactor = reader.GetUnmanaged<Vector3>();
            var amount = reader.GetInt();
            WeatherClasses = new WeatherNode[amount];
            for (var i = 0; i < amount; i++)
            {
                WeatherClasses[i] = reader.GetWeatherClass();
            }
        }

        public void HandleRequest(NetPeer peer, FikaServer server)
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame != null && fikaGame.GameController.WeatherClasses != null && fikaGame.GameController.WeatherClasses.Length > 0)
            {
                RequestPacket response = new()
                {
                    Type = ERequestSubPacketType.Weather,
                    RequestSubPacket = new WeatherRequest()
                    {
                        Season = fikaGame.Season,
                        SpringSnowFactor = fikaGame.SeasonsSettings != null ? fikaGame.SeasonsSettings.SpringSnowFactor : Vector3.zero,
                        WeatherClasses = fikaGame.GameController.WeatherClasses
                    }
                };

                server.SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            }
        }

        public void HandleResponse()
        {
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame != null)
            {
                fikaGame.Season = Season;
                if (SpringSnowFactor != Vector3.zero)
                {
                    fikaGame.SeasonsSettings = new()
                    {
                        SpringSnowFactor = SpringSnowFactor
                    };
                }
                fikaGame.GameController.WeatherClasses = WeatherClasses;
                return;
            }

            FikaGlobals.LogError("WeatherRequest::HandleResponse: CoopGame was null upon receiving packet!");
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.PutEnum(Season);
            writer.PutUnmanaged(SpringSnowFactor);
            var amount = WeatherClasses.Length;
            writer.Put(amount);
            for (var i = 0; i < amount; i++)
            {
                writer.PutWeatherClass(WeatherClasses[i]);
            }
        }
    }

    public class ExfiltrationRequest : IRequestPacket
    {
        public byte[] Data;

        public ExfiltrationRequest()
        {

        }

        public ExfiltrationRequest(NetDataReader reader)
        {
            Data = reader.GetByteArray();
        }

        public void HandleRequest(NetPeer peer, FikaServer server)
        {
            if (ExfiltrationController.Instance == null)
            {
                FikaGlobals.LogError("ExfiltrationRequest::HandleRequest: ExfiltrationController was null!");
                return;
            }

            var exfilController = ExfiltrationController.Instance;
            FikaGlobals.LogInfo($"ExfiltrationRequest: serving {exfilController.ExfiltrationPoints?.Length ?? 0} " +
                $"exits and {exfilController.SecretExfiltrationPoints?.Length ?? 0} secret exits to peer {peer.Id}");
            RequestPacket response = new()
            {
                Type = ERequestSubPacketType.Exfiltration,
                RequestSubPacket = new ExfiltrationRequest()
                {
                    // Secret exits live in a separate native array in SPT 5.0.
                    // Include both arrays so late joiners and reconnecting peers
                    // receive the same state as the host.
                    Data = CaptureSnapshot(exfilController.ExfiltrationPoints,
                        exfilController.SecretExfiltrationPoints)
                }
            };
            server.SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
        }

        public void HandleResponse()
        {
            if (Data == null || Data.Length == 0)
            {
                FikaGlobals.LogError("ExfiltrationRequest::HandleRequest: Data was null or empty!");
                return;
            }

            if (ExfiltrationController.Instance == null)
            {
                FikaGlobals.LogError("ExfiltrationRequest::HandleRequest: ExfiltrationController was null!");
                return;
            }

            var amount = ApplySnapshot(Data,
                ExfiltrationController.Instance.ExfiltrationPoints,
                ExfiltrationController.Instance.SecretExfiltrationPoints);
            FikaGlobals.LogInfo($"Received {amount} exfils from the server");
            var fikaGame = FikaSingleton<IFikaGame>.Instance;
            if (fikaGame != null)
            {
                if (fikaGame.GameController is ClientGameController clientGameController)
                    clientGameController.ExfiltrationReceived = true;
            }
        }

        internal static byte[] CaptureSnapshot(ExfiltrationPoint[] points)
        {
            NetDataWriter writer = new();
            writer.Put(points.Length);
            WriteRegularPoints(writer, points);
            return writer.CopyData();
        }

        // Version marker for the extended snapshot. A legacy snapshot starts
        // with the regular-exit count, which is always non-negative.
        private const int SnapshotMagic = unchecked((int)0x464B5035);

        internal static byte[] CaptureSnapshot(ExfiltrationPoint[] points, SecretExfiltrationPoint[] secretPoints)
        {
            NetDataWriter writer = new();
            writer.Put(SnapshotMagic);
            writer.Put(points?.Length ?? 0);
            WriteRegularPoints(writer, points);

            writer.Put(secretPoints?.Length ?? 0);
            if (secretPoints != null)
            {
                foreach (var point in secretPoints)
                {
                    writer.Put(point.Settings.Name);
                    writer.PutEnum(point.Status);
                    writer.Put(point.Settings.StartTime);
                    if (point.Status == EExfiltrationStatus.Countdown)
                        writer.Put(point.ExfiltrationStartTime);

                    // These values are private-set in the game source but are
                    // exposed by the SPT 5.0 IL2CPP interop wrappers.
                    writer.Put(point.EligibleForPmc);
                    writer.Put(point.EligibleForScav);
                    writer.Put(point.ItemTransferred);

                    // Hidden is the native representation for a secret exit
                    // that has not been discovered yet. Preserve that bit so a
                    // reconnect can restore discovery without a live event.
                    writer.Put(point.Status != EExfiltrationStatus.Hidden);

                    FikaFlareExfilManager.TryGetSnapshotScope(point, out var secretScopeOwner, out var secretScopeSquadWide);
                    writer.Put(secretScopeOwner ?? string.Empty);
                    writer.Put(secretScopeSquadWide);
                }
            }
            return writer.CopyData();
        }

        private static void WriteRegularPoints(NetDataWriter writer, ExfiltrationPoint[] points)
        {
            if (points == null)
                return;

            foreach (var point in points)
            {
                writer.Put(point.Settings.Name);
                writer.PutEnum(point.Status);
                writer.Put(point.Settings.StartTime);
                if (point.Status == EExfiltrationStatus.Countdown)
                    writer.Put(point.ExfiltrationStartTime);

                // A flare gated countdown belongs to the player that unlocked the
                // exit, so the snapshot carries the owner. An empty owner keeps
                // the historical point wide behaviour.
                FikaFlareExfilManager.TryGetSnapshotScope(point, out var scopeOwner, out var scopeSquadWide);
                writer.Put(scopeOwner ?? string.Empty);
                writer.Put(scopeSquadWide);
            }
        }

        internal static int ApplySnapshot(byte[] data, ExfiltrationPoint[] points)
        {
            return ApplySnapshot(data, points, null);
        }

        internal static int ApplySnapshot(byte[] data, ExfiltrationPoint[] points, SecretExfiltrationPoint[] secretPoints)
        {
            NetDataReader reader = new(data);
            var first = reader.GetInt();
            var extended = first == SnapshotMagic;
            var amount = extended ? reader.GetInt() : first;
            var applied = ApplyRegularSnapshot(reader, amount, points);

            if (extended)
            {
                var secretAmount = reader.GetInt();
                for (var i = 0; i < secretAmount; i++)
                {
                    var name = reader.GetString();
                    var status = reader.GetEnum<EExfiltrationStatus>();
                    var startTime = reader.GetInt();
                    var exfilStartTime = status == EExfiltrationStatus.Countdown ? reader.GetFloat() : -1f;
                    var eligibleForPmc = reader.GetBool();
                    var eligibleForScav = reader.GetBool();
                    var itemTransferred = reader.GetBool();
                    var discovered = reader.GetBool();
                    var scopeOwner = reader.GetString();
                    var scopeSquadWide = reader.GetBool();

                    var exfilPoint = secretPoints?.FirstOrDefault(x => x.Settings.Name == name);
                    if (exfilPoint != null && !FikaFlareExfilManager.ShouldApplyScope(scopeOwner, scopeSquadWide))
                    {
                        FikaGlobals.LogInfo($"[FlareExfil] ignored (report profile=<{scopeOwner}> != me)");
                        continue;
                    }
                    if (exfilPoint != null)
                    {
                        exfilPoint.Settings.StartTime = startTime;
                        if (status == EExfiltrationStatus.Countdown)
                            exfilPoint.ExfiltrationStartTime = exfilStartTime;
                        exfilPoint.EligibleForPmc = eligibleForPmc;
                        exfilPoint.EligibleForScav = eligibleForScav;
                        exfilPoint.ItemTransferred = itemTransferred;

                        if (discovered && exfilPoint.Status == EExfiltrationStatus.Hidden)
                            exfilPoint.ExternalDiscoverOfPoint(false);

                        // Set the timestamp and all secret flags before the
                        // status setter so native subscribers observe a
                        // coherent state, matching regular exits.
                        exfilPoint.Status = status;
                    }
                }
                applied += secretAmount;
            }
            return applied;
        }

        private static int ApplyRegularSnapshot(NetDataReader reader, int amount, ExfiltrationPoint[] points)
        {
            for (var i = 0; i < amount; i++)
            {
                var name = reader.GetString();
                var status = reader.GetEnum<EExfiltrationStatus>();
                var startTime = reader.GetInt();
                var exfilStartTime = status == EExfiltrationStatus.Countdown ? reader.GetFloat() : -1f;
                var scopeOwner = reader.GetString();
                var scopeSquadWide = reader.GetBool();

                if (!FikaFlareExfilManager.ShouldApplyScope(scopeOwner, scopeSquadWide))
                {
                    FikaGlobals.LogInfo($"[FlareExfil] ignored (report profile=<{scopeOwner}> != me)");
                    continue;
                }

                var exfilPoint = points?.FirstOrDefault(x => x.Settings.Name == name);
                if (exfilPoint != null)
                {
                    exfilPoint.Settings.StartTime = startTime;
                    if (status == EExfiltrationStatus.Countdown)
                        exfilPoint.ExfiltrationStartTime = exfilStartTime;
                    // The native setter raises OnStatusChanged synchronously.
                    // Its subscribers must see the received start time.
                    exfilPoint.Status = status;
                }
            }
            return amount;
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.PutByteArray(Data);
        }
    }

    public class TraderServicesRequest : IRequestPacket
    {
        public int NetId;
        public string TraderId;
        public List<TraderServiceAvailabilityData> Services;

        public TraderServicesRequest()
        {

        }

        public TraderServicesRequest(NetDataReader reader)
        {
            NetId = reader.GetInt();
            var isRequest = reader.GetBool();
            if (isRequest)
            {
                TraderId = reader.GetString();
                return;
            }

            Services = [];
            var amount = reader.GetInt();
            if (amount > 0)
            {
                for (var i = 0; i < amount; i++)
                {
                    Services.Add(reader.GetTraderService());
                }
            }
        }

        public void HandleRequest(NetPeer peer, FikaServer server)
        {
            if (peer.Player == null || NetId != peer.Player.NetId || string.IsNullOrEmpty(TraderId)) return;
            if (FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.Players.TryGetValue(NetId, out var playerToApply))
            {
                var services = playerToApply.GetAvailableTraderServices(TraderId).AsManaged().ToList();
                RequestPacket response = new()
                {
                    Type = ERequestSubPacketType.TraderServices,
                    RequestSubPacket = new TraderServicesRequest()
                    {
                        NetId = NetId,
                        Services = services
                    }
                };

                server.SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            }
        }

        public void HandleResponse()
        {
            if (Services == null || Services.Count < 1)
            {
                FikaGlobals.LogWarning("OnTraderServicesPacketReceived: Services was 0, but might be intentional. Skipping...");
                return;
            }

            if (FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.Players.TryGetValue(NetId, out var playerToApply))
            {
                playerToApply.UpdateTraderServiceData(Services.ToNativeList());
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(NetId);
            var isRequest = !string.IsNullOrEmpty(TraderId);
            writer.Put(isRequest);
            if (isRequest)
            {
                writer.Put(TraderId);
                return;
            }

            var amount = Services.Count;
            writer.Put(amount);
            if (amount > 0)
            {
                for (var i = 0; i < Services.Count; i++)
                {
                    writer.PutTraderService(Services[i]);
                }
            }
        }
    }

    public class RequestCharactersPacket : IRequestPacket
    {
        public List<int> MissingIds;

        public RequestCharactersPacket(List<int> missingIds)
        {
#if DEBUG
            FikaGlobals.LogWarning($"Requesting {missingIds.Count} missing ids");
#endif
            MissingIds = [.. missingIds];
        }

        public RequestCharactersPacket(NetDataReader reader)
        {
            int amount = reader.GetUShort();
#if DEBUG
            FikaGlobals.LogWarning($"A client has requested {amount} missing players");
#endif
            if (amount > 0)
            {
                MissingIds = new(amount);
                for (var i = 0; i < amount; i++)
                {
                    MissingIds.Add(reader.GetInt());
                }
            }
        }

        public void HandleRequest(NetPeer peer, FikaServer server)
        {
            if (MissingIds != null && server.CoopHandler != null)
            {
                foreach (var netId in MissingIds)
                {
#if DEBUG
                    FikaGlobals.LogWarning($"Looking for missing netId {netId}");
#endif
                    if (server.CoopHandler.Players.TryGetValue(netId, out var fikaPlayer))
                    {
#if DEBUG
                        FikaGlobals.LogWarning($"Found {fikaPlayer.Profile.Nickname} that was missing from client, sending...");
#endif
                        var packet = SendCharacterPacket.FromValue(new()
                        {
                            Profile = fikaPlayer.Profile,
                            ControllerId = fikaPlayer.InventoryController.CurrentId,
                            FirstOperationId = fikaPlayer.InventoryController.NextOperationId
                        }, fikaPlayer.HealthController.IsAlive, fikaPlayer.IsAI, fikaPlayer.Transform.position, fikaPlayer.NetId, fikaPlayer.CaptureRevivalState());

                        if (fikaPlayer.ActiveHealthController != null)
                        {
                            packet.PlayerInfoPacket.HealthByteArray = fikaPlayer.ActiveHealthController.SerializeState();
                        }
                        else if (fikaPlayer.TryCast<ObservedPlayer>() is { } observedPlayer)
                        {
                            packet.PlayerInfoPacket.HealthByteArray = observedPlayer.NetworkHealthController.Store().SerializeHealthInfo();
                        }
                        else
                        {
                            packet.PlayerInfoPacket.HealthByteArray = fikaPlayer.Profile.Health.SerializeHealthInfo();
                        }

                        if (fikaPlayer.HandsController != null)
                        {
                            packet.PlayerInfoPacket.ControllerType = HandsControllerTypeConvert.FromController(fikaPlayer.HandsController);
                            packet.PlayerInfoPacket.ItemId = fikaPlayer.HandsController.Item?.Id;
                            packet.PlayerInfoPacket.IsStationary = fikaPlayer.MovementContext.IsStationaryWeaponInHands;
                        }

                        server.SendGenericPacketToPeer(EGenericSubPacketType.SendCharacter, packet, peer);
                    }
                }
            }
        }

        /// <summary>
        /// Handled in <see cref="SendCharacterPacket.Execute(FikaPlayer)"/>
        /// </summary>
        public void HandleResponse()
        {
            // Do nothing
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((ushort)MissingIds.Count);
            if (MissingIds.Count > 0)
            {
                foreach (var netId in MissingIds)
                {
                    writer.Put(netId);
                }
            }
        }
    }
}
