using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Time;
using MultiKerbal.Server.Persistence;

namespace MultiKerbal.Server;

/// <summary>
/// Sincronización de naves. Reglas de autoridad:
/// <list type="bullet">
/// <item>Cada nave tiene como mucho un dueño, el único que puede enviar su estado.</item>
/// <item>Quien publica una nave nueva, o modifica una sin dueño, pasa a ser su dueño.</item>
/// <item>Al desconectarse un jugador, sus naves quedan sin dueño y cualquiera puede tomarlas.</item>
/// </list>
/// </summary>
public sealed partial class ServerHost
{
    internal static bool IsValid(VesselUpdateMessage update) =>
        update.VesselId != Guid.Empty
        && update.BodyIndex >= 0
        && double.IsFinite(update.UniversalTime)
        && double.IsFinite(update.Latitude)
        && double.IsFinite(update.Longitude)
        && double.IsFinite(update.Altitude)
        && double.IsFinite(update.Inclination)
        && double.IsFinite(update.Eccentricity)
        && double.IsFinite(update.SemiMajorAxis)
        && double.IsFinite(update.LongitudeOfAscendingNode)
        && double.IsFinite(update.ArgumentOfPeriapsis)
        && double.IsFinite(update.MeanAnomalyAtEpoch)
        && double.IsFinite(update.Epoch)
        && float.IsFinite(update.RotationX)
        && float.IsFinite(update.RotationY)
        && float.IsFinite(update.RotationZ)
        && float.IsFinite(update.RotationW);

    private static bool CanModify(StoredVessel vessel, Player player) =>
        vessel.OwnerId == 0 || vessel.OwnerId == player.Info.Id;

    private static VesselProtoMessage ToProtoMessage(StoredVessel vessel) => new()
    {
        VesselId = vessel.Id,
        OwnerId = vessel.OwnerId,
        VesselName = vessel.Name,
        StructureVersion = vessel.StructureVersion,
        Data = vessel.Data,
    };

    /// <summary>Estado inicial para un jugador que acaba de entrar.</summary>
    private void SendVesselsTo(Player player)
    {
        foreach (StoredVessel vessel in _vessels.All)
            SendVessel(player, vessel);
    }

    private void SendVessel(Player player, StoredVessel vessel)
    {
        _network.Send(player.Connection, ToProtoMessage(vessel));
        if (vessel.LastUpdate != null)
            _network.Send(player.Connection, vessel.LastUpdate);
    }

    private void OnVesselProto(Player player, VesselProtoMessage message)
    {
        if (message.VesselId == Guid.Empty || message.Data is not { Length: > 0 } || message.Data.Length > ProtocolInfo.MaxVesselDataBytes)
            return;

        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel != null && !CanModify(vessel, player))
        {
            // La controla otro jugador: se devuelve la versión autoritativa a quien intentó cambiarla.
            SendVessel(player, vessel);
            return;
        }

        bool created = vessel == null;
        vessel ??= _vessels.GetOrCreate(message.VesselId);
        bool ownerChanged = vessel.OwnerId != player.Info.Id;
        vessel.OwnerId = player.Info.Id;
        vessel.Name = SanitizeText(message.VesselName, 64);
        vessel.StructureVersion = message.StructureVersion;
        vessel.Data = message.Data;
        vessel.Dirty = true;

        BroadcastReliable(ToProtoMessage(vessel), except: player);
        if (ownerChanged)
            _network.Send(player.Connection, new VesselOwnershipMessage { VesselId = vessel.Id, OwnerId = vessel.OwnerId });
        if (created)
            Log.Info($"{player.Name} ha publicado la nave \"{vessel.Name}\"");
    }

    private void OnVesselUpdate(Player player, VesselUpdateMessage message)
    {
        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel == null || vessel.OwnerId != player.Info.Id || !IsValid(message))
            return;

        vessel.LastUpdate = message;
        vessel.Dirty = true;
        foreach (Player other in AuthenticatedPlayers())
        {
            if (other != player)
                _network.Send(other.Connection, message, Delivery.Unreliable);
        }
    }

    private void OnVesselRemove(Player player, VesselRemoveMessage message)
    {
        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel == null)
            return;

        if (!CanModify(vessel, player))
        {
            SendVessel(player, vessel);
            return;
        }

        _vessels.Remove(vessel.Id);
        BroadcastReliable(new VesselRemoveMessage { VesselId = vessel.Id }, except: player);
        Log.Info($"{player.Name} ha eliminado la nave \"{vessel.Name}\"");
    }

    private void OnVesselOwnershipRequest(Player player, VesselOwnershipRequestMessage request)
    {
        StoredVessel? vessel = _vessels.Get(request.VesselId);
        if (vessel == null)
            return;

        int previousOwner = vessel.OwnerId;
        if (request.Acquire && vessel.OwnerId == 0)
            vessel.OwnerId = player.Info.Id;
        else if (!request.Acquire && vessel.OwnerId == player.Info.Id)
            vessel.OwnerId = 0;

        var ownership = new VesselOwnershipMessage { VesselId = vessel.Id, OwnerId = vessel.OwnerId };
        if (vessel.OwnerId != previousOwner)
            BroadcastReliable(ownership);
        else
            _network.Send(player.Connection, ownership); // Denegada o sin cambios: solo se responde al solicitante.
    }

    private void ReleaseVesselsOf(Player player)
    {
        foreach (StoredVessel vessel in _vessels.All)
        {
            if (vessel.OwnerId != player.Info.Id)
                continue;

            vessel.OwnerId = 0;
            BroadcastReliable(new VesselOwnershipMessage { VesselId = vessel.Id, OwnerId = 0 });
        }
    }

    /// <summary>Qué ha pedido cada jugador: para ver por qué el warp está donde está.</summary>
    private void ListWarpVotes()
    {
        List<Player> players = AuthenticatedPlayers().ToList();
        string limited = _time.LimitedBy.Length > 0
            ? $", limitado por {_time.LimitedBy}{(_time.LimiterAutoDenies ? " (rechazo automático)" : string.Empty)}"
            : string.Empty;
        Log.Info($"Warp efectivo: x{_time.Rate:0.##} ({(_time.Mode == WarpMode.Physics ? "físico" : "sobre raíles")}){limited}");

        if (players.Count == 0)
        {
            Log.Info("  No hay jugadores conectados");
            return;
        }

        foreach (Player player in players)
        {
            WarpVote vote = player.Vote;
            string state = !vote.Participating
                ? "no participa (menú o hangar)"
                : vote.AutoDeny
                    ? "rechaza automáticamente"
                    : $"pide x{Math.Max(1.0, vote.Rate):0}, acepta hasta x{Math.Max(1.0, vote.AcceptUpTo):0}";
            Log.Info($"  {player.Name}: {state}");
        }
    }

    private void ListVessels()
    {
        if (_vessels.Count == 0)
        {
            Log.Info("No hay naves en el universo");
            return;
        }

        Log.Info($"{_vessels.Count} nave(s):");
        foreach (StoredVessel vessel in _vessels.All.OrderBy(v => v.Name))
        {
            string owner = vessel.OwnerId == 0
                ? "sin dueño"
                : AuthenticatedPlayers().FirstOrDefault(p => p.Info.Id == vessel.OwnerId)?.Name ?? $"jugador {vessel.OwnerId}";
            Log.Info($"  {vessel.Name} — {owner}, {vessel.Data.Length / 1024.0:0.0} KB");
        }
    }
}
