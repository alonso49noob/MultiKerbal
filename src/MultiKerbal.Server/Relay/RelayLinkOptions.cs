namespace MultiKerbal.Server.Relay;

/// <summary>Tiempos del enlace con el relay. Los tests los acortan.</summary>
internal sealed class RelayLinkOptions
{
    /// <summary>Cada cuánto manda el latido por el control (el relay caduca a los 45 s).</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Cada cuánto manda el hola UDP que mantiene abierto el puerto. Lo usa la tarea 7.</summary>
    public TimeSpan UdpHello { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Espera antes del primer reintento; se duplica en cada fallo seguido hasta <see cref="RetryMax"/>.</summary>
    public TimeSpan RetryInitial { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryMax { get; set; } = TimeSpan.FromMinutes(5);
}
