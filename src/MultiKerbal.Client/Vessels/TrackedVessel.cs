using System;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Client.Vessels
{
    /// <summary>Lo que el cliente sabe de una nave del universo, exista o no en la escena actual.</summary>
    internal sealed class TrackedVessel
    {
        private int _sentPartCount = -1;
        private int _sentCrewCount = -1;
        private string _sentName;
        private VesselType _sentType;

        public TrackedVessel(Guid id)
        {
            Id = id;
        }

        public Guid Id { get; }

        /// <summary>Jugador que la pilota ahora (0 = nadie). Solo él envía su estado.</summary>
        public int ControllerId { get; set; }

        /// <summary>Se pidió el control y aún no hay respuesta: si llega sin concederse, hay que salir de la nave.</summary>
        public bool ControlPending { get; set; }

        /// <summary>Nombre del dueño (vacío = sin dueño).</summary>
        public string OwnerName { get; set; } = string.Empty;

        public VesselAccess Access { get; set; } = VesselAccess.Shared;

        /// <summary>Jugador al que el dueño se la ha prestado (vacío = a nadie).</summary>
        public string LentTo { get; set; } = string.Empty;

        /// <summary>Kerbals a bordo según el servidor (de las naves ajenas; las nuestras las sabe KSP).</summary>
        public string[] Crew { get; set; } = new string[0];

        /// <summary>Su dueño puede devolverla a como la dejó (alguien sin permiso para destruirla la ha usado).</summary>
        public bool Restorable { get; set; }

        /// <summary>Solo al publicarla por primera vez: la nave de la que se separó (vacío si ninguna).</summary>
        public Guid ParentId { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Última definición conocida (comprimida).</summary>
        public byte[] Proto { get; set; }

        /// <summary>Aumenta cuando llega una definición remota con otra estructura: indica que hay que recargarla.</summary>
        public int ProtoVersion { get; set; }

        /// <summary>Versión de estructura de la última definición recibida de otro jugador.</summary>
        public int RemoteStructureVersion { get; set; }

        /// <summary>Versión cargada en la escena actual (-1 = no cargada).</summary>
        public int SpawnedVersion { get; set; } = -1;

        /// <summary>Versión que había cargada al guardarse la partida al salir de la escena anterior.</summary>
        public int SavedVersion { get; set; } = -1;

        /// <summary>Versión que no se pudo cargar (para no reintentarla en bucle).</summary>
        public int FailedVersion { get; set; } = -1;

        public double NextSpawnAttempt { get; set; }

        public VesselUpdateMessage LastUpdate { get; set; }

        /// <summary>Movimiento suavizado entre estados recibidos (solo marionetas).</summary>
        public PuppetMotion Motion { get; } = new PuppetMotion();

        /// <summary>Referencia en la escena actual (puede estar destruida: comprobar antes de usar).</summary>
        public Vessel Vessel { get; set; }

        // Solo para naves propias.

        /// <summary>Aumenta con cada cambio de estructura publicado.</summary>
        public int StructureVersion { get; set; }

        public double NextUpdate { get; set; }

        public double NextProtoRefresh { get; set; }

        /// <summary>El primer estado tras publicar va por TCP para que no llegue antes que la definición.</summary>
        public bool FirstUpdatePending { get; set; } = true;

        public bool StructureChanged(Vessel vessel) =>
            vessel.parts.Count != _sentPartCount
            || vessel.GetCrewCount() != _sentCrewCount
            || vessel.vesselName != _sentName
            || vessel.vesselType != _sentType;

        public void RememberPublished(Vessel vessel)
        {
            _sentPartCount = vessel.parts.Count;
            _sentCrewCount = vessel.GetCrewCount();
            _sentName = vessel.vesselName;
            _sentType = vessel.vesselType;
            FirstUpdatePending = true;
        }

        /// <summary>Al tomar el control de una nave ajena: se continúa desde su versión de estructura y se republica.</summary>
        public void ForgetPublished()
        {
            _sentPartCount = -1;
            StructureVersion = RemoteStructureVersion;
        }
    }
}
