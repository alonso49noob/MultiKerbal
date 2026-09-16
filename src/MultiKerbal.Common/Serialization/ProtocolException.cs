using System;

namespace MultiKerbal.Common.Serialization
{
    /// <summary>Datos recibidos que no respetan el protocolo. La conexión que los envía debe cerrarse.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }
    }
}
