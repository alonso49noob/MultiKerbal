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
}

internal readonly record struct ServerEvent(
    ServerEventKind Kind,
    ClientConnection? Connection = null,
    IMessage? Message = null,
    string? Text = null);
