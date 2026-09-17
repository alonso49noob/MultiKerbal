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
    /// <item>El dueño es un jugador identificado por su nombre y se conserva entre sesiones. Una nave sin dueño es de todos.</item>
    /// <item>Pilotar es tener el control en este momento: lo decide el servidor, de uno en uno, y es aparte del dueño.</item>
    /// </list>
    /// </summary>
    public static class VesselPermissions
    {
        public static bool HasOwner(string owner) => !string.IsNullOrEmpty(owner);

        public static bool IsOwner(string owner, string player) =>
            HasOwner(owner) && string.Equals(owner, player, StringComparison.OrdinalIgnoreCase);

        public static bool IsValid(VesselAccess access) =>
            access == VesselAccess.Private || access == VesselAccess.Shared || access == VesselAccess.Public;

        /// <summary>Tomar el control cuando nadie la está pilotando.</summary>
        public static bool CanPilot(string owner, VesselAccess access, string player) =>
            !HasOwner(owner) || access != VesselAccess.Private || IsOwner(owner, player);

        /// <summary>Recuperarla o borrarla (quien la pilota siempre puede perderla: choques, acoplamientos...).</summary>
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
