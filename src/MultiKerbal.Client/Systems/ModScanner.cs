using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MultiKerbal.Common;
using MultiKerbal.Common.Mods;

namespace MultiKerbal.Client.Systems
{
    /// <summary>
    /// Qué mods hay instalados: una entrada por carpeta de GameData, con la versión de sus DLL si tiene.
    /// Se mira una sola vez (KSP carga los mods al arrancar y no cambian durante la partida).
    /// </summary>
    internal static class ModScanner
    {
        private static ModInfo[] _mods;

        public static ModInfo[] Installed => _mods ?? (_mods = Scan());

        private static ModInfo[] Scan()
        {
            try
            {
                Dictionary<string, List<string>> versionsByFolder = AssemblyVersionsByFolder();
                string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
                var mods = new List<ModInfo>();
                foreach (string directory in Directory.GetDirectories(gameData))
                {
                    string name = Path.GetFileName(directory);
                    if (string.IsNullOrEmpty(name))
                        continue;

                    versionsByFolder.TryGetValue(name, out List<string> versions);
                    mods.Add(new ModInfo
                    {
                        Name = Trim(name),
                        // Varias DLL en la misma carpeta: se juntan para que la comparación sea estable.
                        Version = versions == null ? string.Empty : Trim(string.Join(", ", versions.OrderBy(v => v).ToArray())),
                    });
                }

                mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                if (mods.Count > ProtocolInfo.MaxMods)
                    mods.RemoveRange(ProtocolInfo.MaxMods, mods.Count - ProtocolInfo.MaxMods);
                return mods.ToArray();
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudieron leer los mods instalados: {ex.Message}");
                return new ModInfo[0];
            }
        }

        /// <summary>Las versiones de cada DLL cargada, agrupadas por la carpeta de GameData a la que pertenece.</summary>
        private static Dictionary<string, List<string>> AssemblyVersionsByFolder()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                string url = loaded?.url ?? string.Empty;
                string folder = url.Split('/')[0];
                if (folder.Length == 0 || loaded.assembly == null)
                    continue;

                Version version = loaded.assembly.GetName().Version;
                if (version == null)
                    continue;

                if (!result.TryGetValue(folder, out List<string> versions))
                {
                    versions = new List<string>();
                    result[folder] = versions;
                }

                string text = version.ToString(3);
                if (!versions.Contains(text))
                    versions.Add(text);
            }

            return result;
        }

        private static string Trim(string text) =>
            text.Length > ProtocolInfo.MaxModNameLength ? text.Substring(0, ProtocolInfo.MaxModNameLength) : text;
    }
}
