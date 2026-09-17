using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Client.UI
{
    /// <summary>Palabras que salen en varias ventanas, para que siempre se diga igual.</summary>
    internal static class UiStrings
    {
        /// <summary>El acceso de una nave: "privada", "compartida" o "pública".</summary>
        public static string Access(VesselAccess access)
        {
            switch (access)
            {
                case VesselAccess.Private:
                    return Loc.T("privada", "private");
                case VesselAccess.Public:
                    return Loc.T("pública", "public");
                default:
                    return Loc.T("compartida", "shared");
            }
        }

        public static string AccessPlural(VesselAccess access)
        {
            switch (access)
            {
                case VesselAccess.Private:
                    return Loc.T("privadas", "private");
                case VesselAccess.Public:
                    return Loc.T("públicas", "public");
                default:
                    return Loc.T("compartidas", "shared");
            }
        }

        /// <summary>Qué significa cada acceso, con el dueño en tercera persona.</summary>
        public static string ExplainAccess(VesselAccess access)
        {
            switch (access)
            {
                case VesselAccess.Private:
                    return Loc.T(
                        "solo el dueño puede pilotarla, recuperarla o borrarla",
                        "only the owner can fly, recover or delete it");
                case VesselAccess.Public:
                    return Loc.T(
                        "cualquiera puede pilotarla, recuperarla o borrarla",
                        "anyone can fly, recover or delete it");
                default:
                    return Loc.T(
                        "cualquiera puede pilotarla; recuperarla o borrarla, solo el dueño",
                        "anyone can fly it; only the owner can recover or delete it");
            }
        }

        public static string Capitalize(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
