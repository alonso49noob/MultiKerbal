using System;
using System.Collections.Generic;
using MultiKerbal.Common.Serialization;

namespace MultiKerbal.Common.Mods
{
    /// <summary>Un mod instalado: la carpeta de GameData y la versión de sus DLL (vacía si solo trae piezas y configuraciones).</summary>
    public sealed class ModInfo
    {
        public string Name = string.Empty;
        public string Version = string.Empty;

        public void Write(PacketWriter writer)
        {
            writer.WriteString(Name);
            writer.WriteString(Version);
        }

        public static ModInfo Read(PacketReader reader) => new ModInfo
        {
            Name = reader.ReadString() ?? string.Empty,
            Version = reader.ReadString() ?? string.Empty,
        };
    }

    public enum ModStatus
    {
        /// <summary>Instalado y con la misma versión que el servidor.</summary>
        Ok,

        /// <summary>Instalado pero con otra versión.</summary>
        OtherVersion,

        /// <summary>Lo tiene el servidor y el jugador no.</summary>
        Missing,

        /// <summary>Lo tiene el jugador y el servidor no.</summary>
        Extra,
    }

    public sealed class ModDifference
    {
        public string Name = string.Empty;
        public string ServerVersion = string.Empty;
        public string PlayerVersion = string.Empty;
        public ModStatus Status;

        public bool IsProblem => Status != ModStatus.Ok;
    }

    /// <summary>Compara la lista de mods del servidor con la de un jugador. Igual en el servidor y en el cliente.</summary>
    public static class ModCompare
    {
        public static List<ModDifference> Compare(IEnumerable<ModInfo> serverMods, IEnumerable<ModInfo> playerMods)
        {
            var result = new List<ModDifference>();
            var mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModInfo mod in playerMods ?? new ModInfo[0])
            {
                if (!string.IsNullOrEmpty(mod?.Name))
                    mine[mod.Name] = mod.Version ?? string.Empty;
            }

            foreach (ModInfo mod in serverMods ?? new ModInfo[0])
            {
                if (string.IsNullOrEmpty(mod?.Name))
                    continue;

                bool installed = mine.TryGetValue(mod.Name, out string version);
                string serverVersion = mod.Version ?? string.Empty;
                result.Add(new ModDifference
                {
                    Name = mod.Name,
                    ServerVersion = serverVersion,
                    PlayerVersion = installed ? version : string.Empty,
                    Status = !installed
                        ? ModStatus.Missing
                        : SameVersion(serverVersion, version)
                            ? ModStatus.Ok
                            : ModStatus.OtherVersion,
                });
                mine.Remove(mod.Name);
            }

            foreach (KeyValuePair<string, string> extra in mine)
            {
                result.Add(new ModDifference
                {
                    Name = extra.Key,
                    ServerVersion = string.Empty,
                    PlayerVersion = extra.Value,
                    Status = ModStatus.Extra,
                });
            }

            result.Sort(CompareForDisplay);
            return result;
        }

        /// <summary>Resumen corto para el chat, el registro o el motivo de un rechazo.</summary>
        public static string Summarize(List<ModDifference> differences)
        {
            List<string> missing = Names(differences, ModStatus.Missing);
            List<string> extra = Names(differences, ModStatus.Extra);
            List<string> other = Names(differences, ModStatus.OtherVersion);

            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add($"faltan {Join(missing)}");
            if (other.Count > 0)
                parts.Add($"con otra versión {Join(other)}");
            if (extra.Count > 0)
                parts.Add($"sobran {Join(extra)}");

            return parts.Count == 0 ? "los mismos mods que el servidor" : string.Join("; ", parts.ToArray());
        }

        /// <summary>Sin versión en uno de los dos lados (un mod sin DLL) no se compara la versión.</summary>
        private static bool SameVersion(string server, string player) =>
            string.IsNullOrEmpty(server)
            || string.IsNullOrEmpty(player)
            || string.Equals(server, player, StringComparison.OrdinalIgnoreCase);

        private static List<string> Names(List<ModDifference> differences, ModStatus status)
        {
            var names = new List<string>();
            foreach (ModDifference difference in differences)
            {
                if (difference.Status == status)
                    names.Add(difference.Name);
            }

            return names;
        }

        private static string Join(List<string> names) =>
            names.Count <= 3
                ? string.Join(", ", names.ToArray())
                : string.Join(", ", names.GetRange(0, 3).ToArray()) + $" y {names.Count - 3} más";

        /// <summary>Primero los problemas (faltan, otra versión, sobran) y dentro de cada grupo por nombre.</summary>
        private static int CompareForDisplay(ModDifference a, ModDifference b)
        {
            int byStatus = Rank(a.Status).CompareTo(Rank(b.Status));
            return byStatus != 0 ? byStatus : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int Rank(ModStatus status)
        {
            switch (status)
            {
                case ModStatus.Missing:
                    return 0;
                case ModStatus.OtherVersion:
                    return 1;
                case ModStatus.Extra:
                    return 2;
                default:
                    return 3;
            }
        }
    }
}
