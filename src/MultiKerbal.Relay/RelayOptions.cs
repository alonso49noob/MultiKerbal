namespace MultiKerbal.Relay;

/// <summary>Ajustes del relay. Los tiempos se pueden acortar en las pruebas.</summary>
internal sealed class RelayOptions
{
    /// <summary>Puerto TCP y UDP del relay. Con 0 el sistema elige uno libre.</summary>
    public int Port { get; set; } = 6760;

    /// <summary>Puerto UDP desde el que salen las comprobaciones de acceso directo.</summary>
    public int ProbePort { get; set; } = 6761;

    public string DataDirectory { get; set; } = "relay-data";

    /// <summary>Un servidor que no manda nada por su control en este tiempo se da por caído.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Plazo para que una conexión nueva mande su primer mensaje.</summary>
    public TimeSpan FirstMessageTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Plazo para que el servidor abra la conexión de enganche de una sesión.</summary>
    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Cuando un lado de una sesión termina limpio, se pasa su FIN al otro y se le da este margen para cerrar por su
    /// cuenta. Así lee todo lo que quedaba (por ejemplo el motivo de una expulsión) en vez de recibir un corte.
    /// </summary>
    public TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Cada cuánto se repite la comprobación de acceso directo de cada servidor.</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Plazo de cada comprobación de acceso directo.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Tiempo mínimo entre dos comprobaciones cuando las pide el servidor (con <c>RelayRecheck</c>).</summary>
    public TimeSpan MinRecheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxServersPerIp { get; set; } = 5;

    /// <summary>Claves nuevas (cada una gasta un código para siempre) que se aceptan de una misma IP en <see cref="NewCodeWindow"/>.</summary>
    public int MaxNewCodesPerIp { get; set; } = 3;

    public TimeSpan NewCodeWindow { get; set; } = TimeSpan.FromHours(24);

    public int MaxSessionsPerServer { get; set; } = 64;

    /// <summary>Sesiones por el relay de una misma IP de jugador hacia un mismo servidor (esperando o pasando bytes).</summary>
    public int MaxSessionsPerPlayerIp { get; set; } = 4;

    /// <summary>
    /// Conexiones abiertas a la vez desde una IP, sin contar los controles y enganches de los servidores (que dejan de
    /// contar en cuanto dicen qué son).
    /// </summary>
    public int MaxConnectionsPerIp { get; set; } = 20;

    /// <summary>Conexiones nuevas por segundo que se aceptan de una IP, con una ráfaga de <see cref="NewConnectionBurstPerIp"/>.</summary>
    public double NewConnectionsPerSecondPerIp { get; set; } = 5;

    public int NewConnectionBurstPerIp { get; set; } = 20;

    /// <summary>Tamaño máximo del primer mensaje de una conexión (todos son pequeños). El control registrado admite hasta 64 KB.</summary>
    public int MaxFirstMessageBytes { get; set; } = 4 * 1024;

    /// <summary>Cada aviso repetido de una misma IP (conexiones rechazadas, mensajes raros...) se escribe como mucho una vez en este tiempo.</summary>
    public TimeSpan WarningInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Pausa tras un error al aceptar conexiones, para no dar vueltas sin parar si el error se repite.</summary>
    public TimeSpan AcceptErrorDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Tope de tráfico de cada sesión, en cada sentido.</summary>
    public long BytesPerSecond { get; set; } = 1_000_000;

    public RelayOptions Clone() => (RelayOptions)MemberwiseClone();
}
