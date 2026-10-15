using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Mods;
using MultiKerbal.Common.Time;
using MultiKerbal.Server.Net;

namespace MultiKerbal.Server;

/// <summary>Una conexión y, tras el saludo, el jugador asociado. Solo se usa desde el bucle principal.</summary>
internal sealed class Player
{
    public Player(ClientConnection connection)
    {
        Connection = connection;
    }

    public ClientConnection Connection { get; }

    public bool Authenticated { get; set; }

    /// <summary>Ya se anotó "Conexión #N" en el registro (con su primer mensaje, si no era una consulta de Hospedar).</summary>
    public bool Announced { get; set; }

    /// <summary>La conexión era una consulta o una orden de Hospedar, ya contestada: se cierra sin dejar rastro en el registro.</summary>
    public bool HostingRequest { get; set; }

    public PlayerInfo Info { get; set; } = new();

    public WarpVote Vote { get; set; }

    /// <summary>Mods que declaró al entrar, para el comando <c>mods</c>.</summary>
    public ModInfo[] Mods { get; set; } = [];

    public string Name => Info.Name;
}
