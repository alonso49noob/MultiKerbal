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

    /// <summary>Pilotarla ya, o tomar el control si nadie la pilota y el acceso (o un préstamo) lo permite.</summary>
    private static bool CanPilot(StoredVessel vessel, Player player) =>
        vessel.ControllerId == player.Info.Id
        || (vessel.ControllerId == 0 && VesselPermissions.CanPilot(vessel.OwnerName, vessel.Access, vessel.LentTo, player.Name));

    /// <summary>
    /// Fundirla con otra (acoplamiento, kerbal que embarca) puede quien la pilota. Destruirla, recuperarla o borrarla,
    /// además, solo con permiso del dueño.
    /// </summary>
    private static bool CanRemove(StoredVessel vessel, Player player, VesselRemoveReason reason)
    {
        bool inHand = vessel.ControllerId == player.Info.Id;
        if (reason == VesselRemoveReason.Merged)
            return inHand;

        return (inHand || vessel.ControllerId == 0) && VesselPermissions.CanRemove(vessel.OwnerName, vessel.Access, player.Name);
    }

    private static VesselProtoMessage ToProtoMessage(StoredVessel vessel) => new()
    {
        VesselId = vessel.Id,
        ControllerId = vessel.ControllerId,
        OwnerName = vessel.OwnerName,
        Access = vessel.Access,
        LentTo = vessel.LentTo,
        VesselName = vessel.Name,
        Crew = vessel.Crew,
        StructureVersion = vessel.StructureVersion,
        Data = vessel.Data,
    };

    private static VesselOwnerMessage ToOwnerMessage(StoredVessel vessel) => new()
    {
        VesselId = vessel.Id,
        OwnerName = vessel.OwnerName,
        Access = vessel.Access,
        LentTo = vessel.LentTo,
    };

    private static string DescribeOwner(StoredVessel vessel)
    {
        if (!VesselPermissions.HasOwner(vessel.OwnerName))
            return "sin dueño";

        string lent = vessel.LentTo.Length > 0 ? $", prestada a {vessel.LentTo}" : string.Empty;
        return $"de {vessel.OwnerName} ({VesselPermissions.Describe(vessel.Access)}{lent})";
    }

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
        if (vessel.CopilotId != 0)
        {
            _network.Send(player.Connection, new VesselCopilotMessage
            {
                VesselId = vessel.Id,
                CopilotId = vessel.CopilotId,
                AllowActions = vessel.CopilotActions,
            });
        }
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

        string[] crew = SanitizeCrew(message.Crew);
        if (FindCrewInUse(message.VesselId, crew, player) is { } inUse)
        {
            string text = Lang.T(
                $"{inUse.Kerbal} ya va en \"{inUse.Vessel.Name}\" ({DescribeUser(inUse.Vessel)})",
                $"{inUse.Kerbal} is already aboard \"{inUse.Vessel.Name}\" ({DescribeUser(inUse.Vessel)})");
            if (vessel == null)
            {
                // Nave nueva (lanzamiento, EVA...): no se acepta y quien la publicó la quita de su partida.
                _network.Send(player.Connection, new VesselRejectMessage { VesselId = message.VesselId, Reason = text });
                Log.Info($"Rechazada la nave \"{SanitizeText(message.VesselName, 64)}\" de {player.Name}: {text}");
                return;
            }

            // Una nave que ya existe no se puede deshacer: se acepta y el kerbal queda en las dos hasta que se aclare.
            Log.Warn($"\"{vessel.Name}\" de {player.Name}: {text}");
        }

        bool created = vessel == null;
        vessel ??= _vessels.GetOrCreate(message.VesselId);
        if (created)
        {
            vessel.OwnerName = player.Name;
            vessel.Access = VesselPermissions.IsValid(message.Access) ? message.Access : VesselAccess.Shared;
        }

        bool controllerChanged = vessel.ControllerId != player.Info.Id;
        if (controllerChanged && !created)
            TakeCheckpoint(vessel, player);
        vessel.ControllerId = player.Info.Id;
        vessel.Name = SanitizeText(message.VesselName, 64);
        vessel.StructureVersion = message.StructureVersion;
        vessel.Data = message.Data;
        vessel.Crew = crew;
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

    /// <summary>Nombres válidos, sin repetir y sin pasar del máximo.</summary>
    internal static string[] SanitizeCrew(string[]? crew) =>
        (crew ?? [])
            .Select(name => SanitizeText(name, 64))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ProtocolInfo.MaxCrewPerVessel)
            .ToArray();

    /// <summary>
    /// Un kerbal de la tripulación que ya va en otra nave que no está en manos de este jugador. Entre naves que
    /// pilota el mismo jugador sí se puede (EVA, embarcar, trasvasar): durante un momento aparece en las dos.
    /// </summary>
    private (string Kerbal, StoredVessel Vessel)? FindCrewInUse(Guid vesselId, string[] crew, Player player)
    {
        if (crew.Length == 0)
            return null;

        foreach (StoredVessel other in _vessels.All)
        {
            if (other.Id == vesselId || other.ControllerId == player.Info.Id)
                continue;

            // El nombre tal como va en la otra nave (es el que conoce su jugador).
            string? kerbal = other.Crew.FirstOrDefault(name => crew.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (kerbal != null)
                return (kerbal, other);
        }

        return null;
    }

    /// <summary>Para los mensajes: quién la pilota o, si nadie, de quién es.</summary>
    private string DescribeUser(StoredVessel vessel)
    {
        Player? pilot = vessel.ControllerId != 0 ? PlayerById(vessel.ControllerId) : null;
        if (pilot != null)
            return Lang.T($"la pilota {pilot.Name}", $"flown by {pilot.Name}");

        return VesselPermissions.HasOwner(vessel.OwnerName)
            ? Lang.T($"de {vessel.OwnerName}", $"{vessel.OwnerName}'s")
            : Lang.T("sin dueño", "no owner");
    }

    /// <summary>
    /// Al tomar el control alguien que no podría destruirla, se guarda cómo estaba: si la destruye, vuelve ahí
    /// (te la prestan y, si la rompes, aparece donde la cogiste).
    /// </summary>
    private static void TakeCheckpoint(StoredVessel vessel, Player newPilot)
    {
        vessel.Checkpoint = VesselPermissions.CanRemove(vessel.OwnerName, vessel.Access, newPilot.Name) || vessel.Data.Length == 0
            ? null
            : new VesselCheckpoint(vessel.Data, vessel.Crew, vessel.LastUpdate);
        vessel.Dirty = true;
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

        VesselRemoveReason reason = Enum.IsDefined(message.Reason) ? message.Reason : VesselRemoveReason.Destroyed;
        if (!CanRemove(vessel, player, reason))
        {
            RefuseRemoval(vessel, player, reason);
            return;
        }

        _vessels.Remove(vessel.Id);
        BroadcastReliable(new VesselRemoveMessage { VesselId = vessel.Id, Reason = reason }, except: player);
        Log.Info($"{player.Name} ha eliminado la nave \"{vessel.Name}\" ({reason})");
    }

    /// <summary>
    /// Sin permiso del dueño la nave sigue existiendo. Quien la perdió deja de pilotarla y la recibe de vuelta;
    /// si la destruyó, vuelve a como estaba cuando la tomó (y todos la recargan así).
    /// </summary>
    private void RefuseRemoval(StoredVessel vessel, Player player, VesselRemoveReason reason)
    {
        bool wasPilot = vessel.ControllerId == player.Info.Id;
        bool restored = false;
        if (reason == VesselRemoveReason.Destroyed && wasPilot && vessel.Checkpoint is { Data.Length: > 0 } checkpoint)
        {
            vessel.Data = checkpoint.Data;
            vessel.Crew = checkpoint.Crew;
            vessel.LastUpdate = checkpoint.LastUpdate;
            vessel.StructureVersion++; // Así los demás la recargan en vez de seguir con la versión dañada.
            restored = true;
        }

        if (wasPilot)
        {
            vessel.ControllerId = 0;
            vessel.Checkpoint = null;
            SetCopilot(vessel, 0, false);
        }

        vessel.Dirty = true;
        if (restored)
        {
            foreach (Player other in AuthenticatedPlayers())
                SendVessel(other, vessel);
        }
        else
        {
            if (wasPilot)
                BroadcastReliable(new VesselControlMessage { VesselId = vessel.Id, ControllerId = 0 }, except: player);
            SendVessel(player, vessel);
        }

        string owner = VesselPermissions.HasOwner(vessel.OwnerName) ? vessel.OwnerName : "?";
        string text = restored
            ? Lang.T(
                $"\"{vessel.Name}\" es de {owner}: sin su permiso no se puede destruir. Ha vuelto a donde estaba cuando la tomaste.",
                $"\"{vessel.Name}\" belongs to {owner}: it cannot be destroyed without their permission. It is back where it was when you took it.")
            : Lang.T(
                $"\"{vessel.Name}\" es de {owner}: sin su permiso no puedes destruirla, recuperarla ni borrarla.",
                $"\"{vessel.Name}\" belongs to {owner}: you cannot destroy, recover or delete it without their permission.");
        _network.Send(player.Connection, new ChatMessage { SenderId = ChatMessage.SystemSenderId, Text = text });
        Log.Info($"{player.Name} no puede eliminar \"{vessel.Name}\" ({reason}){(restored ? ": restaurada" : string.Empty)}");
    }

    /// <summary>
    /// Préstamos: el dueño presta la nave a un jugador conectado o la recupera; quien la tiene prestada puede devolverla.
    /// Al recuperarla el dueño, quien la tenía deja de pilotarla aunque la nave sea compartida.
    /// </summary>
    private void OnVesselLendRequest(Player player, VesselLendRequestMessage request)
    {
        StoredVessel? vessel = _vessels.Get(request.VesselId);
        if (vessel == null)
            return;

        bool owner = VesselPermissions.IsOwner(vessel.OwnerName, player.Name);
        string target = request.PlayerName?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            string borrower = vessel.LentTo;
            if (borrower.Length == 0 || (!owner && !VesselPermissions.IsBorrower(borrower, player.Name)))
            {
                _network.Send(player.Connection, ToOwnerMessage(vessel));
                return;
            }

            vessel.LentTo = string.Empty;
            vessel.Dirty = true;
            BroadcastReliable(ToOwnerMessage(vessel));
            if (owner)
            {
                Player? pilot = vessel.ControllerId != 0 ? PlayerById(vessel.ControllerId) : null;
                if (pilot != null && VesselPermissions.IsOwner(borrower, pilot.Name))
                    RevokeControl(vessel);
                SendSystemChat(Lang.T(
                    $"{player.Name} ha recuperado \"{vessel.Name}\", que le había prestado a {borrower}",
                    $"{player.Name} took back \"{vessel.Name}\" from {borrower}"));
            }
            else
            {
                RevokeControlIfNotAllowed(vessel);
                SendSystemChat(Lang.T(
                    $"{player.Name} le ha devuelto \"{vessel.Name}\" a {vessel.OwnerName}",
                    $"{player.Name} gave \"{vessel.Name}\" back to {vessel.OwnerName}"));
            }

            Log.Info($"\"{vessel.Name}\" ya no está prestada a {borrower}");
            return;
        }

        Player? to = AuthenticatedPlayers().FirstOrDefault(p => VesselPermissions.IsOwner(target, p.Name));
        if (!owner || to == null || to == player)
        {
            _network.Send(player.Connection, ToOwnerMessage(vessel));
            return;
        }

        vessel.LentTo = to.Name;
        vessel.Dirty = true;
        BroadcastReliable(ToOwnerMessage(vessel));
        SendSystemChat(Lang.T(
            $"{player.Name} le ha prestado \"{vessel.Name}\" a {to.Name}",
            $"{player.Name} lent \"{vessel.Name}\" to {to.Name}"));
        Log.Info($"{player.Name} le presta \"{vessel.Name}\" a {to.Name}");
    }

    private void RevokeControl(StoredVessel vessel)
    {
        vessel.ControllerId = 0;
        vessel.Checkpoint = null;
        SetCopilot(vessel, 0, false);
        BroadcastReliable(new VesselControlMessage { VesselId = vessel.Id, ControllerId = 0 });
    }

    private void OnVesselControlRequest(Player player, VesselControlRequestMessage request)
    {
        StoredVessel? vessel = _vessels.Get(request.VesselId);
        if (vessel == null)
            return;

        int previous = vessel.ControllerId;
        if (request.Acquire && CanPilot(vessel, player))
        {
            if (previous != player.Info.Id)
                TakeCheckpoint(vessel, player);
            vessel.ControllerId = player.Info.Id;
        }
        else if (!request.Acquire && vessel.ControllerId == player.Info.Id)
        {
            vessel.ControllerId = 0;
            vessel.Checkpoint = null;
        }

        var control = new VesselControlMessage { VesselId = vessel.Id, ControllerId = vessel.ControllerId };
        if (vessel.ControllerId != previous)
        {
            SetCopilot(vessel, 0, false); // El copiloto lo nombra quien pilota: al cambiar de manos, se cae.
            BroadcastReliable(control);
        }
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
        if (!VesselPermissions.IsOwner(newOwner, vessel.OwnerName))
            vessel.LentTo = string.Empty; // El préstamo era cosa del dueño anterior.
        vessel.OwnerName = newOwner;
        vessel.Access = request.Access;
        vessel.Dirty = true;
        BroadcastReliable(ToOwnerMessage(vessel));
        Log.Info($"{player.Name} cambió la nave \"{vessel.Name}\": {previous} → {DescribeOwner(vessel)}");
        if (gifted)
            SendSystemChat(Lang.T(
                $"{player.Name} le ha regalado la nave \"{vessel.Name}\" a {newOwner}",
                $"{player.Name} gave the vessel \"{vessel.Name}\" to {newOwner}"));
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
        if (controller != null && VesselPermissions.CanPilot(vessel.OwnerName, vessel.Access, vessel.LentTo, controller.Name))
            return;

        RevokeControl(vessel);
    }

    private void ReleaseVesselsOf(Player player)
    {
        foreach (StoredVessel vessel in _vessels.All)
        {
            if (vessel.CopilotId == player.Info.Id)
                SetCopilot(vessel, 0, false);

            if (vessel.ControllerId != player.Info.Id)
                continue;

            RevokeControl(vessel);
        }
    }

    /// <summary>"Pídele el control a quien la pilota": el servidor solo hace de mensajero; decide quien la pilota.</summary>
    private void OnVesselHandoverRequest(Player player, VesselHandoverRequestMessage request)
    {
        StoredVessel? vessel = _vessels.Get(request.VesselId);
        if (vessel == null || vessel.ControllerId == 0 || vessel.ControllerId == player.Info.Id)
            return;

        if (!VesselPermissions.CanPilot(vessel.OwnerName, vessel.Access, vessel.LentTo, player.Name))
        {
            _network.Send(player.Connection, ToOwnerMessage(vessel));
            return;
        }

        Player? pilot = PlayerById(vessel.ControllerId);
        if (pilot == null)
            return;

        _network.Send(pilot.Connection, new VesselHandoverAskMessage { VesselId = vessel.Id, FromPlayerId = player.Info.Id });
        Log.Info($"{player.Name} le pide a {pilot.Name} el control de \"{vessel.Name}\"");
    }

    private void OnVesselHandoverGrant(Player player, VesselHandoverGrantMessage grant)
    {
        StoredVessel? vessel = _vessels.Get(grant.VesselId);
        if (vessel == null || vessel.ControllerId != player.Info.Id)
            return;

        Player? target = PlayerById(grant.ToPlayerId);
        if (target == null || !VesselPermissions.CanPilot(vessel.OwnerName, vessel.Access, vessel.LentTo, target.Name))
            return;

        TakeCheckpoint(vessel, target);
        vessel.ControllerId = target.Info.Id;
        SetCopilot(vessel, 0, false);
        BroadcastReliable(new VesselControlMessage { VesselId = vessel.Id, ControllerId = vessel.ControllerId });
        SendSystemChat(Lang.T(
            $"{player.Name} le ha dado el control de \"{vessel.Name}\" a {target.Name}",
            $"{player.Name} handed \"{vessel.Name}\" over to {target.Name}"));
        Log.Info($"{player.Name} → {target.Name}: control de \"{vessel.Name}\"");
    }

    /// <summary>Quien pilota nombra (o quita) copiloto. El copiloto también puede renunciar.</summary>
    private void OnVesselCopilot(Player player, VesselCopilotMessage message)
    {
        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel == null)
            return;

        bool resigning = message.CopilotId == 0 && vessel.CopilotId == player.Info.Id;
        if (vessel.ControllerId != player.Info.Id && !resigning)
            return;

        Player? copilot = message.CopilotId == 0 ? null : PlayerById(message.CopilotId);
        if (message.CopilotId != 0 && (copilot == null || copilot.Info.Id == vessel.ControllerId))
            return;

        SetCopilot(vessel, copilot?.Info.Id ?? 0, message.AllowActions);
        if (copilot != null)
            Log.Info($"{player.Name} nombró copiloto de \"{vessel.Name}\" a {copilot.Name}");
    }

    private void SetCopilot(StoredVessel vessel, int copilotId, bool allowActions)
    {
        if (vessel.CopilotId == copilotId && vessel.CopilotActions == allowActions)
            return;

        vessel.CopilotId = copilotId;
        vessel.CopilotActions = allowActions;
        BroadcastReliable(new VesselCopilotMessage
        {
            VesselId = vessel.Id,
            CopilotId = vessel.CopilotId,
            AllowActions = vessel.CopilotActions,
        });
    }

    /// <summary>Mandos del copiloto: solo llegan a quien pilota, que decide qué hace con ellos.</summary>
    private void OnVesselInput(Player player, VesselInputMessage message)
    {
        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel == null || vessel.CopilotId != player.Info.Id || vessel.ControllerId == 0)
            return;

        Player? pilot = PlayerById(vessel.ControllerId);
        if (pilot != null)
            _network.Send(pilot.Connection, message, Delivery.Unreliable);
    }

    private void OnVesselAction(Player player, VesselActionMessage message)
    {
        StoredVessel? vessel = _vessels.Get(message.VesselId);
        if (vessel == null || vessel.CopilotId != player.Info.Id || !vessel.CopilotActions || vessel.ControllerId == 0)
            return;

        Player? pilot = PlayerById(vessel.ControllerId);
        if (pilot != null)
            _network.Send(pilot.Connection, message);
    }

    private Player? PlayerById(int id) => AuthenticatedPlayers().FirstOrDefault(p => p.Info.Id == id);

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
        vessel.LentTo = string.Empty;
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
