using System;

namespace MultiKerbal.Common.Vessels
{
    /// <summary>Qué pueden hacer con una nave los jugadores que no son su dueño.</summary>
    public enum VesselAccess : byte
    {
        /// <summary>Solo el dueño puede pilotarla, recuperarla o borrarla.</summary>
        Private = 0,

        /// <summary>Cualquiera puede pilotarla; recuperarla o borrarla, solo el dueño.</summary>
        Shared = 1,

        /// <summary>Cualquiera puede hacer de todo.</summary>
        Public = 2,
    }

    /// <summary>
    /// Reglas de propiedad de las naves, compartidas por el servidor (que las aplica) y el cliente (que las muestra).
    /// <list type="bullet">
    /// <item>El dueño es un jugador identificado por su nombre (con su cuenta) y se conserva entre sesiones. Una nave sin dueño es de todos.</item>
    /// <item>Pilotar es tener el control en este momento: lo decide el servidor, de uno en uno, y es aparte del dueño.</item>
    /// <item>El dueño puede prestarla a un jugador: puede pilotarla aunque sea privada, pero no destruirla ni recuperarla.</item>
    /// </list>
    /// </summary>
    public static class VesselPermissions
    {
        public static bool HasOwner(string owner) => !string.IsNullOrEmpty(owner);

        public static bool IsOwner(string owner, string player) =>
            HasOwner(owner) && string.Equals(owner, player, StringComparison.OrdinalIgnoreCase);

        /// <summary>Se la han prestado a este jugador.</summary>
        public static bool IsBorrower(string lentTo, string player) => IsOwner(lentTo, player);

        public static bool IsValid(VesselAccess access) =>
            access == VesselAccess.Private || access == VesselAccess.Shared || access == VesselAccess.Public;

        /// <summary>Tomar el control cuando nadie la está pilotando.</summary>
        public static bool CanPilot(string owner, VesselAccess access, string lentTo, string player) =>
            !HasOwner(owner) || access != VesselAccess.Private || IsOwner(owner, player) || IsBorrower(lentTo, player);

        /// <summary>
        /// Destruirla, recuperarla o borrarla: solo con permiso del dueño (pública o sin dueño), aunque se esté pilotando.
        /// Lo que sí puede quien la pilota es fundirla con otra (acoplamientos, kerbals que embarcan).
        /// </summary>
        public static bool CanRemove(string owner, VesselAccess access, string player) =>
            !HasOwner(owner) || access == VesselAccess.Public || IsOwner(owner, player);

        /// <summary>Cambiar el acceso, regalarla o dejarla sin dueño. Una nave sin dueño la puede reclamar cualquiera.</summary>
        public static bool CanChangeOwnership(string owner, string player) =>
            !HasOwner(owner) || IsOwner(owner, player);

        public static string Describe(VesselAccess access)
        {
            switch (access)
            {
                case VesselAccess.Private:
                    return Lang.T("privada", "private");
                case VesselAccess.Public:
                    return Lang.T("pública", "public");
                default:
                    return Lang.T("compartida", "shared");
            }
        }
    }
}
