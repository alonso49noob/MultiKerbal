using MultiKerbal.Common;
using MultiKerbal.Common.Relay;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Ventana del menú principal para conectarse a un servidor.</summary>
    internal sealed class ConnectWindow
    {
        private const int WindowId = 0x4D4B0001;
        private const float Width = 420f;
        private const float LabelWidth = 140f;
        private const float ButtonWidth = 90f;

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(-1f, 90f, Width, 0f);
        private string _name;
        private string _host;
        private string _port;
        private string _password = string.Empty;
        private string _accountPassword;

        public ConnectWindow(ClientCore core)
        {
            _core = core;
            _drawContents = DrawContents;
        }

        public bool Visible { get; set; } = true;

        public void Draw()
        {
            UiStyles.Apply();
            if (!Visible)
            {
                if (GUI.Button(new Rect(Screen.width - 160f, 10f, 150f, 30f), "MultiKerbal"))
                    Visible = true;
                return;
            }

            EnsureLoaded();

            if (_rect.x < 0f)
                _rect.x = Screen.width - Width - 40f;

            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "MultiKerbal " + ClientCore.ModVersion, GUILayout.Width(Width));
        }

        private void EnsureLoaded()
        {
            if (_name != null)
                return;

            _name = _core.Settings.PlayerName;
            _host = _core.Settings.Host;
            _port = _core.Settings.Port.ToString();
            _accountPassword = _core.Settings.AccountPassword;
        }

        /// <summary>
        /// El jugador eligió un servidor de la lista pública. Sin clave, entra; con clave, rellena el campo y espera a que la
        /// escriba y pulse Conectar.
        /// </summary>
        public void JoinFromList(string code, bool hasPassword)
        {
            EnsureLoaded();
            _host = ServerCode.Format(code);
            _password = string.Empty;
            if (hasPassword)
                _core.SetStatus(Loc.T("Escribe la contraseña y pulsa Conectar", "Type the password and press Connect"));
            else
                TryConnect();
        }

        private static string Field(string label, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UiStyles.Label, GUILayout.Width(LabelWidth));
            value = GUILayout.TextField(value ?? string.Empty, maxLength);
            GUILayout.EndHorizontal();
            return value;
        }

        private static string SecretField(string label, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UiStyles.Label, GUILayout.Width(LabelWidth));
            value = GUILayout.PasswordField(value ?? string.Empty, '*', maxLength);
            GUILayout.EndHorizontal();
            return value;
        }

        private void DrawContents(int id)
        {
            bool idle = _core.State == SessionState.Disconnected;

            GUI.enabled = idle;
            _name = Field(Loc.T("Nombre", "Name"), _name, ProtocolInfo.MaxPlayerNameLength);
            _accountPassword = SecretField(Loc.T("Tu contraseña", "Your password"), _accountPassword, ProtocolInfo.MaxAccountPasswordLength);
            GUILayout.BeginHorizontal();
            GUILayout.Space(LabelWidth + 4f);
            bool remember = GUILayout.Toggle(_core.Settings.RememberAccountPassword, Loc.T("Recordarla en este ordenador", "Remember it on this computer"));
            GUILayout.EndHorizontal();
            if (remember != _core.Settings.RememberAccountPassword)
            {
                _core.Settings.RememberAccountPassword = remember;
                _core.Settings.Save();
            }

            GUILayout.Label(
                Loc.T("La primera vez que entras con un nombre, esta contraseña queda como la de tu cuenta: nadie más podrá usar tu nombre ni tus naves.",
                      "The first time you join with a name, this becomes your account's password: nobody else can use your name or your vessels."),
                UiStyles.Muted);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Servidor (IP o código)", "Server (IP or code)"), UiStyles.Label, GUILayout.Width(LabelWidth));
            _host = GUILayout.TextField(_host ?? string.Empty, 128);
            GUI.enabled = true;
            if (GUILayout.Button(Loc.T("Lista", "List"), GUILayout.Width(60f)))
                _core.PublicServersWindow.Toggle();
            GUI.enabled = idle;
            GUILayout.EndHorizontal();

            // Con un código no hay puerto: lo resuelve el relay.
            GUI.enabled = idle && !ServerCode.LooksLikeCode(_host);
            _port = Field(Loc.T("Puerto", "Port"), _port, 5);
            GUI.enabled = idle;
            _password = SecretField(Loc.T("Clave servidor", "Server key"), _password, 64);
            GUI.enabled = true;

            DrawLanguage();

            if (!string.IsNullOrEmpty(_core.StatusText))
                GUILayout.Label(_core.StatusText, UiStyles.Label);

            GUILayout.BeginHorizontal();
            if (idle)
            {
                if (GUILayout.Button(Loc.T("Conectar y jugar", "Connect and play")))
                    TryConnect();
            }
            else if (GUILayout.Button(Loc.T("Cancelar", "Cancel")))
            {
                _core.Disconnect(Loc.T("Conexión cancelada", "Connection cancelled"));
            }

            if (GUILayout.Button("Mods", GUILayout.Width(70f)))
                _core.ModsWindow.Visible = !_core.ModsWindow.Visible;
            if (GUILayout.Button(Loc.T("Hospedar", "Host"), GUILayout.Width(ButtonWidth)))
                _core.HostingWindow.Toggle();
            if (GUILayout.Button(Loc.T("Ocultar", "Hide"), GUILayout.Width(ButtonWidth)))
                Visible = false;
            GUILayout.EndHorizontal();

            if (_core.ModDifferences.Count > 0)
                GUILayout.Label(_core.ModSummary, UiStyles.Muted);

            GUI.DragWindow();
        }

        /// <summary>Idioma del mod, aparte del de KSP: útil si juegas con gente que no habla tu idioma.</summary>
        private void DrawLanguage()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Idioma", "Language"), UiStyles.Label, GUILayout.Width(LabelWidth));
            LanguageButton("auto", Loc.T("El de KSP", "KSP's"));
            LanguageButton("es", "Español");
            LanguageButton("en", "English");
            GUILayout.EndHorizontal();
        }

        private void LanguageButton(string language, string text)
        {
            bool selected = _core.Settings.Language == language;
            if (GUILayout.Toggle(selected, text, GUI.skin.button) == selected)
                return;

            _core.Settings.Language = language;
            _core.Settings.Save();
            Loc.Initialize(language);
        }

        private void TryConnect()
        {
            string name = _name.Trim();
            string host = _host.Trim();
            if (name.Length < ProtocolInfo.MinPlayerNameLength)
            {
                _core.SetStatus(Loc.T(
                    $"El nombre debe tener al menos {ProtocolInfo.MinPlayerNameLength} caracteres",
                    $"The name needs at least {ProtocolInfo.MinPlayerNameLength} characters"));
                return;
            }

            if (host.Length == 0)
            {
                _core.SetStatus(Loc.T("Indica la IP o el código del servidor", "Enter the server's IP or code"));
                return;
            }

            if ((_accountPassword ?? string.Empty).Length < ProtocolInfo.MinAccountPasswordLength)
            {
                _core.SetStatus(Loc.T(
                    $"Tu contraseña debe tener al menos {ProtocolInfo.MinAccountPasswordLength} caracteres",
                    $"Your password needs at least {ProtocolInfo.MinAccountPasswordLength} characters"));
                return;
            }

            // Un código (K7M-Q2P) no lleva puerto: ClientCore lo valida y lo busca en el relay.
            if (ServerCode.LooksLikeCode(host))
            {
                _core.Connect(host, _core.Settings.Port, name, _password, _accountPassword);
                return;
            }

            // "dirección:puerto" pegado de golpe (como lo copia la ventana Hospedar o lo da playit.gg).
            // Con más de un ':' es una IPv6 y se deja tal cual.
            int colon = host.IndexOf(':');
            if (colon > 0 && colon == host.LastIndexOf(':') && int.TryParse(host.Substring(colon + 1), out _))
            {
                _port = host.Substring(colon + 1);
                host = host.Substring(0, colon);
                _host = host;
            }

            if (!int.TryParse(_port.Trim(), out int port) || port < 1 || port > 65535)
            {
                _core.SetStatus(Loc.T("Puerto inválido", "Invalid port"));
                return;
            }

            _core.Connect(host, port, name, _password, _accountPassword);
        }
    }
}
