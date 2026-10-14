using MultiKerbal.Common.Messages;
using MultiKerbal.Server.Net;

namespace MultiKerbal.Server;

internal enum ServerEventKind
{
    Connected,
    Disconnected,
    Message,
    Command,
    Stop,

    /// <summary>Cambió el estado del enlace con el relay (lo encola el propio enlace).</summary>
    RelayChanged,
}

internal readonly record struct ServerEvent(
    ServerEventKind Kind,
    ClientConnection? Connection = null,
    IMessage? Message = null,
    string? Text = null);
