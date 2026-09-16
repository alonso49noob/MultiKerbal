using System;
using System.Globalization;
using System.IO;
using MultiKerbal.Common;
using MultiKerbal.Common.Time;

namespace MultiKerbal.Client
{
    internal sealed class ClientSettings
    {
        public string PlayerName = "Kerbal" + new Random().Next(100, 1000);
        public string Host = "127.0.0.1";
        public int Port = ProtocolInfo.DefaultPort;
        public WarpPolicy Warp = new WarpPolicy();

        // En la raíz de KSP y no en GameData: dos instancias que comparten GameData tienen así ajustes distintos.
        private static string FilePath => Path.Combine(KSPUtil.ApplicationRootPath, "PluginData/MultiKerbal/settings.cfg");

        public static ClientSettings Load()
        {
            var settings = new ClientSettings();
            try
            {
                if (!File.Exists(FilePath))
                    return settings;

                ConfigNode node = ConfigNode.Load(FilePath)?.GetNode("MULTIKERBAL");
                if (node == null)
                    return settings;

                string name = node.GetValue("playerName");
                if (!string.IsNullOrEmpty(name))
                    settings.PlayerName = name;

                string host = node.GetValue("host");
                if (!string.IsNullOrEmpty(host))
                    settings.Host = host;

                if (int.TryParse(node.GetValue("port"), out int port) && port > 0 && port <= 65535)
                    settings.Port = port;

                ConfigNode warp = node.GetNode("WARP_POLICY");
                if (warp != null)
                    LoadWarp(warp, settings.Warp);
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo leer {FilePath}: {ex.Message}");
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var root = new ConfigNode();
                ConfigNode node = root.AddNode("MULTIKERBAL");
                node.AddValue("playerName", PlayerName);
                node.AddValue("host", Host);
                node.AddValue("port", Port.ToString());
                SaveWarp(node.AddNode("WARP_POLICY"), Warp);
                root.Save(FilePath);
            }
            catch (Exception ex)
            {
                ClientLog.Warn($"No se pudo guardar {FilePath}: {ex.Message}");
            }
        }

        private static void LoadWarp(ConfigNode node, WarpPolicy policy)
        {
            policy.AutoAccept = ReadBool(node, "autoAccept", policy.AutoAccept);
            policy.AcceptMaxRate = ReadRate(node, "acceptMaxRate", policy.AcceptMaxRate);
            policy.AcceptOnlyOutsideFlight = ReadBool(node, "acceptOnlyOutsideFlight", policy.AcceptOnlyOutsideFlight);
            policy.AcceptOnlyStable = ReadBool(node, "acceptOnlyStable", policy.AcceptOnlyStable);
            policy.AcceptOnlyEnginesOff = ReadBool(node, "acceptOnlyEnginesOff", policy.AcceptOnlyEnginesOff);
            policy.AcceptOnlyAlone = ReadBool(node, "acceptOnlyAlone", policy.AcceptOnlyAlone);
            policy.AcceptWhenIdle = ReadBool(node, "acceptWhenIdle", policy.AcceptWhenIdle);
            policy.IdleSeconds = ReadRate(node, "idleSeconds", policy.IdleSeconds);
            policy.AutoDeny = ReadBool(node, "autoDeny", policy.AutoDeny);
            policy.DenyWhileFlying = ReadBool(node, "denyWhileFlying", policy.DenyWhileFlying);
            policy.DenyAtSpaceCenter = ReadBool(node, "denyAtSpaceCenter", policy.DenyAtSpaceCenter);
            policy.DenyInAtmosphere = ReadBool(node, "denyInAtmosphere", policy.DenyInAtmosphere);
            policy.DenyNearVessels = ReadBool(node, "denyNearVessels", policy.DenyNearVessels);
        }

        private static void SaveWarp(ConfigNode node, WarpPolicy policy)
        {
            node.AddValue("autoAccept", policy.AutoAccept.ToString());
            node.AddValue("acceptMaxRate", policy.AcceptMaxRate.ToString(CultureInfo.InvariantCulture));
            node.AddValue("acceptOnlyOutsideFlight", policy.AcceptOnlyOutsideFlight.ToString());
            node.AddValue("acceptOnlyStable", policy.AcceptOnlyStable.ToString());
            node.AddValue("acceptOnlyEnginesOff", policy.AcceptOnlyEnginesOff.ToString());
            node.AddValue("acceptOnlyAlone", policy.AcceptOnlyAlone.ToString());
            node.AddValue("acceptWhenIdle", policy.AcceptWhenIdle.ToString());
            node.AddValue("idleSeconds", policy.IdleSeconds.ToString(CultureInfo.InvariantCulture));
            node.AddValue("autoDeny", policy.AutoDeny.ToString());
            node.AddValue("denyWhileFlying", policy.DenyWhileFlying.ToString());
            node.AddValue("denyAtSpaceCenter", policy.DenyAtSpaceCenter.ToString());
            node.AddValue("denyInAtmosphere", policy.DenyInAtmosphere.ToString());
            node.AddValue("denyNearVessels", policy.DenyNearVessels.ToString());
        }

        private static bool ReadBool(ConfigNode node, string name, bool fallback) =>
            bool.TryParse(node.GetValue(name), out bool value) ? value : fallback;

        private static double ReadRate(ConfigNode node, string name, double fallback) =>
            double.TryParse(node.GetValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 1.0
                ? value
                : fallback;
    }
}
