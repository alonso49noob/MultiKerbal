using System;
using System.Collections.Generic;
using System.IO;
using MultiKerbal.Common;
using MultiKerbal.Common.Messages;
using MultiKerbal.Common.Relay;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>
    /// Lista de servidores públicos del relay. Se carga en un hilo aparte y se entra con un clic: sin clave conecta
    /// directamente; con clave rellena el campo de la ventana de conexión y espera a que el jugador la escriba.
    /// </summary>
    internal sealed class PublicServersWindow
    {
        private const int WindowId = 0x4D4B0009;
        private const float Width = 660f;
        private const float ListHeight = 260f;
        private const int LoadTimeoutMs = 8000;
        private const int MaxVersionLength = 20;

        private const float NameWidth = 230f;
        private const float PlayersWidth = 55f;
        private const float PasswordWidth = 60f;
        private const float VersionWidth = 70f;
        private const float ConnectionWidth = 60f;
        private const float JoinWidth = 70f;

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(-1f, 130f, Width, 0f);
        private Vector2 _scroll;
        private bool _hidePassworded;
        private string _search = string.Empty;

        // Lo escribe el hilo de carga. La ventana lo copia a los campos de abajo al empezar a dibujar cada cuadro
        // (evento Layout): si cambiara entre el Layout y el Repaint, Unity se queja de controles que no cuadran.
        private volatile LoadResult _incoming;
        private volatile bool _loadRunning;

        private PublicServerEntry[] _servers;
        private string _error;
        private bool _loading;

        public PublicServersWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
        }

        public bool Visible { get; set; }

        public void Toggle()
        {
            Visible = !Visible;
            if (Visible)
                Refresh();
        }

        public void Draw()
        {
            if (!Visible)
                return;

            UiStyles.Apply();
            if (Event.current.type == EventType.Layout)
                Adopt();

            if (_rect.x < 0f)
                _rect.x = Mathf.Max(10f, Screen.width - Width - 460f);

            _rect = GUILayout.Window(WindowId, _rect, _drawContents, Loc.T("Servidores públicos", "Public servers"), GUILayout.Width(Width));
        }

        private void Adopt()
        {
            LoadResult result = _incoming;
            if (result != null)
            {
                _incoming = null;
                _servers = result.Servers;
                _error = result.Error;
            }

            _loading = _loadRunning;
        }

        private void Refresh()
        {
            if (_loadRunning)
                return;

            _loadRunning = true;
            string relay = _core.Settings.RelayAddress;
            BackgroundWork.Run("lista pública", () =>
            {
                try
                {
                    PublicServerEntry[] servers = Clean(RelayDirectory.List(relay, LoadTimeoutMs));
                    ClientLog.Info($"Lista pública: {servers.Length} servidores");
                    _incoming = new LoadResult { Servers = servers };
                }
                catch (IOException ex)
                {
                    // El mensaje ya está pensado para el jugador (relay caído, sin respuesta, otra versión...).
                    ClientLog.Warn($"Lista pública: {ex.Message}");
                    _incoming = new LoadResult { Error = ex.Message };
                }
                catch (Exception ex)
                {
                    ClientLog.Warn($"Lista pública: {ex}");
                    _incoming = new LoadResult { Error = Loc.T("No se pudo cargar la lista", "Could not load the list") };
                }
            }, () => _loadRunning = false);
        }

        /// <summary>La lista viene de un relay que podría no ser el nuestro: se descartan los códigos que no lo son y se limpian los textos.</summary>
        private static PublicServerEntry[] Clean(PublicServerEntry[] servers)
        {
            var clean = new List<PublicServerEntry>(servers.Length);
            foreach (PublicServerEntry server in servers)
            {
                if (!ServerCode.TryNormalize(server.Code, out string code))
                    continue;

                server.Code = code;
                server.ModVersion = ListedName.Sanitize(server.ModVersion, MaxVersionLength);
                clean.Add(server);
            }

            return clean.ToArray();
        }

        private void DrawContents(int id)
        {
            DrawFilters();

            if (_error != null)
                UiStyles.ColoredLabel(_error, UiStyles.Bad);
            else if (_loading && _servers == null)
                GUILayout.Label(Loc.T("Cargando la lista...", "Loading the list..."), UiStyles.Muted);

            if (_servers != null)
                DrawList(_servers);

            GUILayout.BeginHorizontal();
            GUI.enabled = !_loading;
            if (GUILayout.Button(Loc.T("Actualizar", "Refresh")))
                Refresh();
            GUI.enabled = true;
            if (GUILayout.Button(Loc.T("Cerrar", "Close"), GUILayout.Width(100f)))
                Visible = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private void DrawFilters()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Buscar", "Search"), UiStyles.Label, GUILayout.Width(60f));
            _search = GUILayout.TextField(_search ?? string.Empty, 40);
            GUILayout.EndHorizontal();
            _hidePassworded = GUILayout.Toggle(_hidePassworded, Loc.T("Ocultar los que tienen contraseña", "Hide the ones with a password"));
        }

        private void DrawList(PublicServerEntry[] servers)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Nombre", "Name"), UiStyles.Bold, GUILayout.Width(NameWidth));
            GUILayout.Label(Loc.T("Jugad.", "Players"), UiStyles.Bold, GUILayout.Width(PlayersWidth));
            GUILayout.Label(Loc.T("Clave", "Key"), UiStyles.Bold, GUILayout.Width(PasswordWidth));
            GUILayout.Label(Loc.T("Versión", "Version"), UiStyles.Bold, GUILayout.Width(VersionWidth));
            GUILayout.Label(Loc.T("Vía", "Via"), UiStyles.Bold, GUILayout.Width(ConnectionWidth));
            GUILayout.EndHorizontal();

            string search = (_search ?? string.Empty).Trim();
            bool idle = _core.State == SessionState.Disconnected;
            int shown = 0;

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(ListHeight));
            foreach (PublicServerEntry server in servers)
            {
                if (_hidePassworded && server.HasPassword)
                    continue;
                if (search.Length > 0 && server.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                shown++;
                DrawRow(server, idle);
            }

            if (shown == 0)
            {
                GUILayout.Label(
                    servers.Length == 0
                        ? Loc.T("No hay servidores públicos ahora mismo.", "There are no public servers right now.")
                        : Loc.T("Ningún servidor coincide con el filtro.", "No server matches the filter."),
                    UiStyles.Muted);
            }

            GUILayout.EndScrollView();
        }

        private void DrawRow(PublicServerEntry server, bool idle)
        {
            string unavailable = Unavailable(server);
            GUIStyle style = unavailable == null ? UiStyles.Label : UiStyles.Muted;

            GUILayout.BeginHorizontal();
            GUILayout.Label(server.Name, style, GUILayout.Width(NameWidth));
            GUILayout.Label($"{server.Players}/{server.MaxPlayers}", style, GUILayout.Width(PlayersWidth));

            // Marca en texto: la fuente de KSP no trae el candado.
            if (server.HasPassword)
                UiStyles.ColoredLabel(Loc.T("Clave", "Key"), unavailable == null ? UiStyles.Warning : Color.gray, GUILayout.Width(PasswordWidth));
            else
                GUILayout.Label(string.Empty, style, GUILayout.Width(PasswordWidth));

            GUILayout.Label(server.ModVersion, style, GUILayout.Width(VersionWidth));
            GUILayout.Label(server.Direct ? Loc.T("Directa", "Direct") : "Relay", style, GUILayout.Width(ConnectionWidth));

            GUI.enabled = unavailable == null && idle;
            if (GUILayout.Button(Loc.T("Unirse", "Join"), GUILayout.Width(JoinWidth)))
                Join(server);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (unavailable != null)
                GUILayout.Label(unavailable, UiStyles.Muted);
        }

        /// <summary>Por qué no se puede entrar, o null si se puede. La versión pesa más que estar lleno: lleno cambia, la versión no.</summary>
        private static string Unavailable(PublicServerEntry server)
        {
            if (server.GameProtocol != ProtocolInfo.Version)
            {
                string version = string.IsNullOrEmpty(server.ModVersion) ? server.GameProtocol.ToString() : server.ModVersion;
                return Loc.T($"Otra versión: {version}", $"Other version: {version}");
            }

            return server.Players >= server.MaxPlayers ? Loc.T("Lleno", "Full") : null;
        }

        private void Join(PublicServerEntry server)
        {
            Visible = false;
            _core.ConnectWindow.JoinFromList(server.Code, server.HasPassword);
        }

        private sealed class LoadResult
        {
            public PublicServerEntry[] Servers;
            public string Error;
        }
    }
}
