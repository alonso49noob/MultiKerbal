using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Net;
using MultiKerbal.Common.Time;
using MultiKerbal.Common.Vessels;
using MultiKerbal.Server.Persistence;

namespace MultiKerbal.Server;

/// <summary>
/// Sincronización de naves. Reglas de autoridad (ver <see cref="VesselPermissions"/>):
/// <list type="bullet">
/// <item>Cada nave tiene como mucho un piloto en cada momento, el único que puede enviar su estado.</item>
/// <item>Quien publica una nave nueva pasa a ser su dueño y su piloto. El dueño se guarda en disco; el piloto no.</item>
/// <item>Para pilotar una nave que nadie pilota hace falta que el acceso lo permita (las privadas, solo su dueño).</item>
/// <item>Recuperarla o borrarla puede quien la pilota o, si nadie la pilota, quien tenga permiso.</item>
/// <item>Al desconectarse un jugador, deja de pilotar sus naves, pero sigue siendo su dueño.</item>
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

    /// <summary>Pilotarla ya, o tomar el control si nadie la pilota y el acceso lo permite.</summary>
    private static bool CanPilot(StoredVessel vessel, Player player) =>
        vessel.ControllerId == player.Info.Id
        || (vessel.ControllerId == 0 && VesselPermissions.CanPilot(vessel.OwnerName, vessel.Access, player.Name));

    private static bool CanRemove(StoredVessel vessel, Player player) =>
        vessel.ControllerId == player.Info.Id
        || (vessel.ControllerId == 0 && VesselPermissions.CanRemove(vessel.OwnerName, vessel.Access, player.Name));

    private static VesselProtoMessage ToProtoMessage(StoredVessel vessel) => new()
    {
        VesselId = vessel.Id,
        ControllerId = vessel.ControllerId,
        OwnerName = vessel.OwnerName,
        Access = vessel.Access,
        VesselName = vessel.Name,
        StructureVersion = vessel.StructureVersion,
        Data = vessel.Data,
    };

    private static VesselOwnerMessage ToOwnerMessage(StoredVessel vessel) => new()
    {
        VesselId = vessel.Id,
        OwnerName = vessel.OwnerName,
        Access = vessel.Access,
    };

    private static string DescribeOwner(StoredVessel vessel) =>
        VesselPermissions.HasOwner(vessel.OwnerName)
            ? $"de {vessel.OwnerName} ({VesselPermissions.Describe(vessel.Access)})"
            : "sin dueño";

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
        if (vessel != null && !CanPilot(vessel, player))
        {
            // La pilota otro jugador o no tiene permiso: se devuelve la versión autoritativa a quien intentó cambiarla.
            SendVessel(player, vessel);
            return;
        }

        bool created = vessel == null;
        vessel ??= _vessels.GetOrCreate(message.VesselId);
        if (created)
        {
            vessel.OwnerName = player.Name;
            vessel.Access = VesselPermissions.IsValid(message.Access) ? message.Access : VesselAccess.Shared;
        }

        bool controllerChanged = vessel.ControllerId != player.Info.Id;
        vessel.ControllerId = player.Info.Id;
        vessel.Name = SanitizeText(message.VesselName, 64);
        vessel.StructureVersion = message.StructureVersion;
        vessel.Data = message.Data;
        vessel.Dirty = true;

        BroadcastReliable(ToProtoMessage(vessel), except: player);
        if (controllerChanged)
            _network.Send(player.Connection, new VesselControlMessage { VesselId = vessel.Id, ControllerId = vessel.ControllerId });
        if (created)
        {
            _network.Send(player.Connection, ToOwnerMessage(vessel));
            Log.Info($"{player.Name} ha publicado la nave \"{vessel.Name}\" ({VesselPermissions.Describe(vessel.Access)})");
        }
    }

    private void OnVesselUpdate(Player player, VesselUpdateMessage message)
    {
        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel == null || vessel.ControllerId != player.Info.Id || !IsValid(message))
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

        if (!CanRemove(vessel, player))
        {
            SendVessel(player, vessel);
            return;
        }

        _vessels.Remove(vessel.Id);
        BroadcastReliable(new VesselRemoveMessage { VesselId = vessel.Id }, except: player);
        Log.Info($"{player.Name} ha eliminado la nave \"{vessel.Name}\"");
    }

    private void OnVesselControlRequest(Player player, VesselControlRequestMessage request)
    {
        StoredVessel? vessel = _vessels.Get(request.VesselId);
        if (vessel == null)
            return;

        int previous = vessel.ControllerId;
        if (request.Acquire && CanPilot(vessel, player))
            vessel.ControllerId = player.Info.Id;
        else if (!request.Acquire && vessel.ControllerId == player.Info.Id)
            vessel.ControllerId = 0;

        var control = new VesselControlMessage { VesselId = vessel.Id, ControllerId = vessel.ControllerId };
        if (vessel.ControllerId != previous)
            BroadcastReliable(control);
        else
            _network.Send(player.Connection, control); // Denegada o sin cambios: solo se responde al solicitante.
    }

    private void OnVesselOwnerRequest(Player player, VesselOwnerRequestMessage request)
    {
        StoredVessel? vessel = _vessels.Get(request.VesselId);
        if (vessel == null)
            return;

        string? newOwner = ResolveNewOwner(vessel, player, request.OwnerName);
        if (newOwner == null || !VesselPermissions.IsValid(request.Access))
        {
            _network.Send(player.Connection, ToOwnerMessage(vessel));
            return;
        }

        if (newOwner == vessel.OwnerName && request.Access == vessel.Access)
            return;

        string previous = DescribeOwner(vessel);
        bool gifted = newOwner.Length > 0 && !VesselPermissions.IsOwner(newOwner, player.Name);
        vessel.OwnerName = newOwner;
        vessel.Access = request.Access;
        vessel.Dirty = true;
        BroadcastReliable(ToOwnerMessage(vessel));
        Log.Info($"{player.Name} cambió la nave \"{vessel.Name}\": {previous} → {DescribeOwner(vessel)}");
        if (gifted)
            SendSystemChat($"{player.Name} le ha regalado la nave \"{vessel.Name}\" a {newOwner}");
        RevokeControlIfNotAllowed(vessel);
    }

    /// <summary>
    /// Null si no se permite. El dueño puede quedársela, dejarla sin dueño o regalarla a un jugador conectado
    /// (así no se regala a un nombre mal escrito); una nave sin dueño solo se puede reclamar para uno mismo.
    /// </summary>
    private string? ResolveNewOwner(StoredVessel vessel, Player player, string? requested)
    {
        if (!VesselPermissions.CanChangeOwnership(vessel.OwnerName, player.Name))
            return null;

        requested = requested?.Trim() ?? string.Empty;
        if (requested.Length == 0)
            return string.Empty;
        if (VesselPermissions.IsOwner(requested, player.Name))
            return player.Name;
        if (!VesselPermissions.IsOwner(vessel.OwnerName, player.Name))
            return null;

        return AuthenticatedPlayers().FirstOrDefault(p => VesselPermissions.IsOwner(requested, p.Name))?.Name;
    }

    /// <summary>Si la nave pasa a ser privada de otro, quien la pilotaba deja de hacerlo (su cliente sale de ella).</summary>
    private void RevokeControlIfNotAllowed(StoredVessel vessel)
    {
        if (vessel.ControllerId == 0)
            return;

        Player? controller = AuthenticatedPlayers().FirstOrDefault(p => p.Info.Id == vessel.ControllerId);
        if (controller != null && VesselPermissions.CanPilot(vessel.OwnerName, vessel.Access, controller.Name))
            return;

        vessel.ControllerId = 0;
        BroadcastReliable(new VesselControlMessage { VesselId = vessel.Id, ControllerId = 0 });
    }

    private void ReleaseVesselsOf(Player player)
    {
        foreach (StoredVessel vessel in _vessels.All)
        {
            if (vessel.ControllerId != player.Info.Id)
                continue;

            vessel.ControllerId = 0;
            BroadcastReliable(new VesselControlMessage { VesselId = vessel.Id, ControllerId = 0 });
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
            string pilot = vessel.ControllerId == 0
                ? string.Empty
                : $", la pilota {AuthenticatedPlayers().FirstOrDefault(p => p.Info.Id == vessel.ControllerId)?.Name ?? $"jugador {vessel.ControllerId}"}";
            Log.Info($"  {vessel.Name} [{ShortId(vessel.Id)}] — {DescribeOwner(vessel)}{pilot}, {vessel.Data.Length / 1024.0:0.0} KB");
        }
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>
    /// Comando de administrador: <c>owner &lt;nave&gt; &lt;jugador|nadie&gt; [privada|compartida|publica]</c>.
    /// La nave se indica por el principio de su identificador (ver <c>vessels</c>) o por su nombre.
    /// A diferencia de los jugadores, el administrador puede asignarla a alguien que no esté conectado.
    /// </summary>
    private void SetOwnerCommand(string arguments)
    {
        const string usage = "Uso: owner <nave o id> <jugador|nadie> [privada|compartida|publica]";
        List<string> words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count < 2)
        {
            Log.Info(usage);
            return;
        }

        VesselAccess? access = ParseAccess(words[^1]);
        if (access != null)
            words.RemoveAt(words.Count - 1);

        // El nombre de la nave es el prefijo más largo que coincide; el resto, el nuevo dueño.
        StoredVessel? vessel = null;
        int ownerStart = 0;
        for (int count = words.Count - 1; count >= 1 && vessel == null; count--)
        {
            string candidate = string.Join(' ', words.Take(count));
            List<StoredVessel> matches = FindVessels(candidate);
            if (matches.Count > 1)
            {
                Log.Info($"Hay {matches.Count} naves llamadas \"{candidate}\": usa su id ({string.Join(", ", matches.Select(v => ShortId(v.Id)))})");
                return;
            }

            if (matches.Count == 1)
            {
                vessel = matches[0];
                ownerStart = count;
            }
        }

        if (vessel == null)
        {
            Log.Info($"No encuentro la nave. {usage}");
            return;
        }

        string owner = string.Join(' ', words.Skip(ownerStart));
        if (owner.Length == 0)
        {
            Log.Info(usage);
            return;
        }

        string previous = DescribeOwner(vessel);
        vessel.OwnerName = owner.Equals("nadie", StringComparison.OrdinalIgnoreCase) || owner.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : AuthenticatedPlayers().FirstOrDefault(p => VesselPermissions.IsOwner(owner, p.Name))?.Name ?? SanitizeText(owner, ProtocolInfo.MaxPlayerNameLength);
        vessel.Access = access ?? vessel.Access;
        vessel.Dirty = true;
        BroadcastReliable(ToOwnerMessage(vessel));
        RevokeControlIfNotAllowed(vessel);
        Log.Info($"\"{vessel.Name}\": {previous} → {DescribeOwner(vessel)}");
    }

    private List<StoredVessel> FindVessels(string text)
    {
        List<StoredVessel> byName = _vessels.All.Where(v => v.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0 || text.Length < 4)
            return byName;

        return _vessels.All.Where(v => v.Id.ToString("N").StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static VesselAccess? ParseAccess(string word) => word.ToLowerInvariant() switch
    {
        "privada" or "private" => VesselAccess.Private,
        "compartida" or "shared" => VesselAccess.Shared,
        "publica" or "pública" or "public" => VesselAccess.Public,
        _ => null,
    };
}
