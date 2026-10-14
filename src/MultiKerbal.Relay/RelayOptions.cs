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

    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Plazo de cada comprobación de acceso directo.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxServersPerIp { get; set; } = 5;

    public int MaxSessionsPerServer { get; set; } = 64;

    /// <summary>Tope de tráfico de cada sesión, en cada sentido.</summary>
    public long BytesPerSecond { get; set; } = 1_000_000;

    public RelayOptions Clone() => (RelayOptions)MemberwiseClone();
}
