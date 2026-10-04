using System;
using System.Collections.Generic;
using System.Threading;
using MultiKerbal.Common.Hosting;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Ayuda para que otros entren al servidor desde fuera de casa. El servidor se abre aparte; aquí el jugador
    /// elige una de las cuatro formas (abrir el puerto, red virtual, túnel o nube) y el juego hace lo que puede solo.
    /// </summary>
    internal sealed class HostingWindow
    {
        private const int WindowId = 0x4D4B0008;
        private const float Width = 500f;
        private const float CopiedSeconds = 2f;

        private const string PlayitUrl = "https://playit.gg/download";
        private const string TailscaleUrl = "https://tailscale.com/download";
        private const string ZeroTierUrl = "https://www.zerotier.com/download/";
        private const string RadminUrl = "https://www.radmin-vpn.com/";
        private const string GuideUrlEs = "https://github.com/alonso49noob/MultiKerbal/blob/main/docs/HOSTING.es.md#4-servidor-en-la-nube-vps";
        private const string GuideUrlEn = "https://github.com/alonso49noob/MultiKerbal/blob/main/docs/HOSTING.md#4-cloud-server-vps";

        private enum Tab
        {
            Router,
            VirtualNetwork,
            Tunnel,
            Cloud,
        }

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(-1f, 90f, Width, 0f);
        private Tab _tab;
        private string _port;
        private string _homeAddress;
        private bool _showManual;
        private string _copied;
        private float _copiedAt;

        // Lo escriben los hilos de fondo y lo lee la ventana.
        private volatile bool _routerBusy;
        private volatile PortForwardResult _routerResult;
        private volatile bool _scanning;
        private volatile List<LocalNetworkAddress> _virtualNetworks = new List<LocalNetworkAddress>();

        public HostingWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
        }

        public bool Visible { get; private set; }

        public void Toggle()
        {
            Visible = !Visible;
            if (!Visible)
                return;

            _port = _core.Settings.Port.ToString();
            _homeAddress = LocalNetworks.HomeAddress()?.ToString();
            ScanVirtualNetworks();
        }

        public void Draw()
        {
            if (!Visible)
                return;

            UiStyles.Apply();
            if (_rect.x < 0f)
                _rect.x = Mathf.Max(10f, Screen.width - Width - 460f);

            _rect = GUILayout.Window(WindowId, _rect, _drawContents, Loc.T("Hospedar: que entren tus amigos", "Host: let your friends in"), GUILayout.Width(Width));
        }

        private int Port => int.TryParse(_port?.Trim(), out int port) && port >= 1 && port <= 65535 ? port : -1;

        private void DrawContents(int id)
        {
            GUILayout.Label(Loc.T(
                "Abre primero el servidor en este PC. Tú te conectas a él con 127.0.0.1; aquí eliges cómo llegan los demás.",
                "Start the server on this PC first. You join it at 127.0.0.1; here you choose how the others reach it."), UiStyles.Muted);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Puerto del servidor", "Server port"), UiStyles.Label, GUILayout.Width(150f));
            _port = GUILayout.TextField(_port ?? string.Empty, 5, GUILayout.Width(70f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (Port < 0)
                UiStyles.ColoredLabel(Loc.T("Puerto inválido", "Invalid port"), UiStyles.Bad);
            else if (_homeAddress != null)
                AddressRow(Loc.T("Misma red de casa:", "Same home network:"), $"{_homeAddress}:{Port}");

            GUILayout.Space(6f);
            _tab = (Tab)GUILayout.Toolbar((int)_tab, new[]
            {
                Loc.T("Abrir puerto", "Open port"),
                Loc.T("Red virtual", "Virtual LAN"),
                Loc.T("Túnel", "Tunnel"),
                Loc.T("Nube", "Cloud"),
            });
            GUILayout.Space(4f);

            switch (_tab)
            {
                case Tab.Router:
                    DrawRouter();
                    break;
                case Tab.VirtualNetwork:
                    DrawVirtualNetwork();
                    break;
                case Tab.Tunnel:
                    DrawTunnel();
                    break;
                case Tab.Cloud:
                    DrawCloud();
                    break;
            }

            GUILayout.Space(6f);
            if (GUILayout.Button(Loc.T("Cerrar", "Close")))
                Visible = false;

            GUI.DragWindow();
        }

        private void DrawRouter()
        {
            GUILayout.Label(Loc.T(
                $"El juego le pide a tu router que abra el puerto {Port} por TCP y UDP (UPnP). Tus amigos entran con tu IP pública y no instalan nada. Es la opción más rápida, pero no todos los routers lo permiten.",
                $"The game asks your router to open port {Port} for TCP and UDP (UPnP). Your friends join with your public IP and install nothing. It's the fastest option, but not every router allows it."), UiStyles.Label);

            GUI.enabled = !_routerBusy && Port > 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("Abrir puerto", "Open port")))
                RunRouter(open: true);
            if (GUILayout.Button(Loc.T("Cerrar puerto", "Close port"), GUILayout.Width(140f)))
                RunRouter(open: false);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            if (_routerBusy)
                GUILayout.Label(Loc.T("Buscando el router... (unos segundos)", "Looking for the router... (a few seconds)"), UiStyles.Muted);
            else if (_routerResult != null)
                DrawRouterResult(_routerResult);

            DrawFirewall();

            _showManual = GUILayout.Toggle(_showManual, (_showManual ? UiStyles.Open : UiStyles.Closed) + " " + Loc.T("Hacerlo a mano en el router", "Do it by hand on the router"), UiStyles.Label);
            if (_showManual)
            {
                string local = _homeAddress ?? Loc.T("la IP de este PC", "this PC's IP");
                GUILayout.Label(Loc.T(
                    "1. Entra en tu router desde el navegador (suele ser 192.168.1.1 o 192.168.0.1; el usuario y la contraseña suelen venir en una pegatina).\n" +
                    $"2. Busca \"Redirección de puertos\", \"Port forwarding\", \"NAT\" o \"Servidores virtuales\".\n" +
                    $"3. Crea una regla: puerto {Port} externo e interno, protocolo TCP y UDP (o \"Ambos\"), hacia {local}.\n" +
                    "4. Si existe, activa también \"UPnP\": así el botón de arriba funcionará la próxima vez.",
                    "1. Open your router in the browser (usually 192.168.1.1 or 192.168.0.1; the username and password are often on a sticker).\n" +
                    $"2. Look for \"Port forwarding\", \"NAT\" or \"Virtual servers\".\n" +
                    $"3. Add a rule: port {Port} external and internal, protocol TCP and UDP (or \"Both\"), to {local}.\n" +
                    "4. If there's a \"UPnP\" setting, turn it on too: the button above will work next time."), UiStyles.Muted);
            }
        }

        private void DrawRouterResult(PortForwardResult result)
        {
            switch (result.Outcome)
            {
                case PortForwardOutcome.Opened:
                    UiStyles.ColoredLabel(UiStyles.Tick + " " + Loc.T(
                        $"Puerto {result.Port} abierto en el router hacia este PC ({result.LocalAddress}), TCP y UDP. Se queda abierto hasta que pulses Cerrar puerto.",
                        $"Port {result.Port} open on the router to this PC ({result.LocalAddress}), TCP and UDP. It stays open until you press Close port."), UiStyles.Good);
                    break;
                case PortForwardOutcome.Closed:
                    UiStyles.ColoredLabel(UiStyles.Tick + " " + Loc.T($"Puerto {result.Port} cerrado en el router.", $"Port {result.Port} closed on the router."), UiStyles.Good);
                    return;
                case PortForwardOutcome.NoRouter:
                    UiStyles.ColoredLabel(Loc.T(
                        "Tu router no contesta por UPnP: seguramente lo tiene desactivado. Actívalo en su configuración y vuelve a probar, ábrelo a mano (abajo) o usa Red virtual o Túnel.",
                        "Your router doesn't answer over UPnP: it's probably turned off. Turn it on in the router settings and try again, open the port by hand (below), or use Virtual LAN or Tunnel."), UiStyles.Warning);
                    break;
                case PortForwardOutcome.Conflict:
                    UiStyles.ColoredLabel(Loc.T(
                        $"El puerto {result.Port} ya está abierto hacia otro PC de tu casa ({result.Detail}). Ciérralo allí o usa otro puerto.",
                        $"Port {result.Port} is already open to another PC in your home ({result.Detail}). Close it there or use another port."), UiStyles.Bad);
                    break;
                case PortForwardOutcome.Refused:
                    UiStyles.ColoredLabel(Loc.T(
                        $"El router no ha querido abrir el puerto ({result.Detail}). Ábrelo a mano o usa Red virtual o Túnel.",
                        $"The router refused to open the port ({result.Detail}). Open it by hand or use Virtual LAN or Tunnel."), UiStyles.Bad);
                    break;
            }

            if (result.Nat == NatVerdict.Blocked)
            {
                UiStyles.ColoredLabel(Loc.T(
                    "Tu conexión no admite entradas desde Internet (CGNAT o dos routers seguidos): aunque el puerto esté abierto, tus amigos no llegarán. Usa Red virtual o Túnel, o pide a tu operador una IP pública.",
                    "Your connection doesn't accept incoming connections (CGNAT or two routers in a row): even with the port open, your friends won't get through. Use Virtual LAN or Tunnel, or ask your ISP for a public IP."), UiStyles.Bad);
                return;
            }

            if (result.PublicAddress == null)
            {
                GUILayout.Label(Loc.T("No he podido averiguar tu IP pública.", "Couldn't find out your public IP."), UiStyles.Muted);
                return;
            }

            AddressRow(
                result.Outcome == PortForwardOutcome.Opened
                    ? Loc.T("Pasa a tus amigos:", "Give your friends:")
                    : Loc.T("Con el puerto abierto, sería:", "With the port open, it'd be:"),
                $"{result.PublicAddress}:{result.Port}");
            GUILayout.Label(Loc.T(
                $"Tu IP pública se consulta en {PortForwarding.PublicIpService}. Desde tu propia casa puede que no funcione: pide a un amigo que pruebe.",
                $"Your public IP comes from {PortForwarding.PublicIpService}. It may not work from inside your own home: ask a friend to try."), UiStyles.Muted);
        }

        private void DrawVirtualNetwork()
        {
            GUILayout.Label(Loc.T(
                "Tú y tus amigos instaláis el mismo programa y entráis en la misma red, como si estuvierais en la misma casa. No hay que tocar el router. Para jugar con unos pocos amigos, es lo más fácil.",
                "You and your friends install the same program and join the same network, as if you were in the same house. No router changes. For a few friends, it's the easiest."), UiStyles.Label);

            List<LocalNetworkAddress> networks = _virtualNetworks;
            if (_scanning)
            {
                GUILayout.Label(Loc.T("Buscando...", "Searching..."), UiStyles.Muted);
            }
            else if (networks.Count == 0)
            {
                UiStyles.ColoredLabel(Loc.T(
                    "No he encontrado Tailscale, ZeroTier, Radmin VPN ni Hamachi funcionando en este PC.",
                    "Couldn't find Tailscale, ZeroTier, Radmin VPN or Hamachi running on this PC."), UiStyles.Warning);
            }
            else
            {
                foreach (LocalNetworkAddress network in networks)
                    AddressRow(network.Network + ":", $"{network.Address}:{Port}");
            }

            GUI.enabled = !_scanning;
            if (GUILayout.Button(Loc.T("Buscar otra vez", "Search again")))
                ScanVirtualNetworks();
            GUI.enabled = true;

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Descargar:", "Download:"), UiStyles.Label, GUILayout.Width(90f));
            if (GUILayout.Button("Tailscale"))
                Application.OpenURL(TailscaleUrl);
            if (GUILayout.Button("ZeroTier"))
                Application.OpenURL(ZeroTierUrl);
            if (GUILayout.Button("Radmin VPN"))
                Application.OpenURL(RadminUrl);
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T(
                "1. Tú y cada amigo instaláis el mismo programa. Tailscale es el más sencillo.\n" +
                "2. Entrad en la misma red: en Tailscale, invita a tus amigos desde su web; en ZeroTier o Radmin, pásales el nombre o el ID de tu red.\n" +
                "3. Pulsa Buscar otra vez y pasa a tus amigos la dirección que salga.",
                "1. You and each friend install the same program. Tailscale is the simplest.\n" +
                "2. Join the same network: in Tailscale, invite your friends from its website; in ZeroTier or Radmin, give them your network name or ID.\n" +
                "3. Press Search again and give your friends the address that shows up."), UiStyles.Muted);

            DrawFirewall();
        }

        private void DrawTunnel()
        {
            GUILayout.Label(Loc.T(
                "playit.gg le da a tu servidor una dirección pública gratis. Tus amigos no instalan nada y funciona aunque tu router no deje abrir puertos. Va algo más lento que las otras opciones.",
                "playit.gg gives your server a free public address. Your friends install nothing and it works even if your router won't open ports. It's a bit slower than the other options."), UiStyles.Label);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("1. Instala el programa de playit y enlázalo con una cuenta.", "1. Install the playit program and link it to an account."), UiStyles.Label);
            if (GUILayout.Button(Loc.T("Abrir playit.gg", "Open playit.gg"), GUILayout.Width(130f)))
                Application.OpenURL(PlayitUrl);
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T(
                $"2. En su web, crea un túnel de tipo \"TCP+UDP\" (uno solo, no uno de cada) hacia la dirección local 127.0.0.1, puerto {Port}.",
                $"2. On its website, create one tunnel of type \"TCP+UDP\" (a single one, not one of each) to local address 127.0.0.1, port {Port}."), UiStyles.Label);
            GUILayout.Label(Loc.T("3. Pega aquí la dirección que te da playit, con su puerto:", "3. Paste the address playit gives you here, with its port:"), UiStyles.Label);

            GUILayout.BeginHorizontal();
            string tunnel = GUILayout.TextField(_core.Settings.TunnelAddress ?? string.Empty, 128);
            if (tunnel != _core.Settings.TunnelAddress)
            {
                _core.Settings.TunnelAddress = tunnel;
                _core.Settings.Save();
            }

            GUI.enabled = tunnel.Trim().Length > 0;
            CopyButton(tunnel.Trim());
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T(
                "4. Pasa esa dirección a tus amigos y deja playit abierto mientras jugáis.",
                "4. Give that address to your friends and keep playit running while you play."), UiStyles.Label);
            UiStyles.ColoredLabel(Loc.T(
                $"El puerto que usan tus amigos es el de playit, no el {Port}. Si el túnel es solo TCP, se puede jugar, pero las naves de los demás irán a saltos.",
                $"Your friends use playit's port, not {Port}. With a TCP-only tunnel you can still play, but other players' vessels will move in jumps."), UiStyles.Warning);
        }

        private void DrawCloud()
        {
            GUILayout.Label(Loc.T(
                "Alquilas un ordenador en Internet y abres allí el servidor. Queda encendido aunque apagues tu PC y no depende de tu conexión. Se prepara fuera del juego.",
                "You rent a computer on the Internet and run the server there. It stays up when your PC is off and doesn't depend on your connection. It's set up outside the game."), UiStyles.Label);
            GUILayout.Label(Loc.T(
                "Gratis: Oracle Cloud. De pago: Hetzner, DigitalOcean o Vultr, unos 4–6 € al mes. Con 1 núcleo y 1 GB de RAM sobra.",
                "Free: Oracle Cloud. Paid: Hetzner, DigitalOcean or Vultr, around 4–6 € a month. 1 core and 1 GB of RAM is plenty."), UiStyles.Muted);

            if (GUILayout.Button(Loc.T("Abrir la guía paso a paso", "Open the step-by-step guide")))
                Application.OpenURL(Loc.English ? GuideUrlEn : GuideUrlEs);

            GUILayout.Label(Loc.T(
                "Cuando esté listo, todos (tú también) os conectáis con la IP de ese servidor.",
                "Once it's ready, everyone (you too) connects with that server's IP."), UiStyles.Muted);
        }

        /// <summary>Solo en Windows: el servidor necesita permiso del firewall para que entren desde fuera.</summary>
        private void DrawFirewall()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || Port < 0)
                return;

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(
                "Firewall de Windows: si al abrir el servidor te preguntó, dale a Permitir. Si no, pega esto en PowerShell abierto como administrador.",
                "Windows Firewall: if it asked when you started the server, choose Allow. If not, paste this into PowerShell opened as administrator."), UiStyles.Muted);
            if (GUILayout.Button(_copied == FirewallCommands && Time.realtimeSinceStartup - _copiedAt < CopiedSeconds
                    ? UiStyles.Tick
                    : Loc.T("Copiar", "Copy"), GUILayout.Width(80f)))
                Copy(FirewallCommands);
            GUILayout.EndHorizontal();
        }

        private string FirewallCommands =>
            $"New-NetFirewallRule -DisplayName \"MultiKerbal TCP\" -Direction Inbound -Protocol TCP -LocalPort {Port} -Action Allow\n" +
            $"New-NetFirewallRule -DisplayName \"MultiKerbal UDP\" -Direction Inbound -Protocol UDP -LocalPort {Port} -Action Allow";

        private void AddressRow(string label, string address)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UiStyles.Label, GUILayout.Width(170f));
            GUILayout.Label(address, UiStyles.Bold);
            CopyButton(address);
            GUILayout.EndHorizontal();
        }

        private void CopyButton(string text)
        {
            bool justCopied = _copied == text && Time.realtimeSinceStartup - _copiedAt < CopiedSeconds;
            if (GUILayout.Button(justCopied ? UiStyles.Tick : Loc.T("Copiar", "Copy"), GUILayout.Width(80f)))
                Copy(text);
        }

        private void Copy(string text)
        {
            GUIUtility.systemCopyBuffer = text;
            _copied = text;
            _copiedAt = Time.realtimeSinceStartup;
        }

        private void RunRouter(bool open)
        {
            int port = Port;
            _routerBusy = true;
            _routerResult = null;
            RunInBackground("UPnP", () =>
            {
                PortForwardResult result = open ? PortForwarding.Open(port) : PortForwarding.Close(port);
                ClientLog.Info($"UPnP {(open ? "abrir" : "cerrar")} puerto {port}: {result.Outcome}, NAT {result.Nat}" +
                               (result.Detail.Length > 0 ? $" ({result.Detail})" : string.Empty));
                _routerResult = result;
            }, () => _routerBusy = false);
        }

        private void ScanVirtualNetworks()
        {
            if (_scanning)
                return;

            _scanning = true;
            RunInBackground("redes virtuales", () =>
            {
                List<LocalNetworkAddress> found = LocalNetworks.FindVirtualNetworks();
                ClientLog.Info($"Redes virtuales encontradas: {found.Count}");
                _virtualNetworks = found;
            }, () => _scanning = false);
        }

        /// <summary>La red bloquea varios segundos: nunca en el hilo de Unity.</summary>
        private static void RunInBackground(string what, Action work, Action done)
        {
            new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    ClientLog.Warn($"Fallo en {what}: {ex.Message}");
                }
                finally
                {
                    done();
                }
            })
            {
                IsBackground = true,
                Name = "MultiKerbal " + what,
            }.Start();
        }
    }
}
