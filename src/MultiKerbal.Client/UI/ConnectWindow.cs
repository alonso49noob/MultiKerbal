using MultiKerbal.Common;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Ventana del menú principal para conectarse a un servidor.</summary>
    internal sealed class ConnectWindow
    {
        private const int WindowId = 0x4D4B0001;
        private const float Width = 380f;
        private const float LabelWidth = 90f;

        private readonly ClientCore _core;
        private readonly GUI.WindowFunction _drawContents;
        private Rect _rect = new Rect(-1f, 90f, Width, 0f);
        private string _name;
        private string _host;
        private string _port;
        private string _password = string.Empty;

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

            if (_name == null)
            {
                _name = _core.Settings.PlayerName;
                _host = _core.Settings.Host;
                _port = _core.Settings.Port.ToString();
            }

            if (_rect.x < 0f)
                _rect.x = Screen.width - Width - 40f;

            _rect = GUILayout.Window(WindowId, _rect, _drawContents, "MultiKerbal " + ClientCore.ModVersion, GUILayout.Width(Width));
        }

        private static string Field(string label, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UiStyles.Label, GUILayout.Width(LabelWidth));
            value = GUILayout.TextField(value ?? string.Empty, maxLength);
            GUILayout.EndHorizontal();
            return value;
        }

        private void DrawContents(int id)
        {
            bool idle = _core.State == SessionState.Disconnected;

            GUI.enabled = idle;
            _name = Field(Loc.T("Nombre", "Name"), _name, ProtocolInfo.MaxPlayerNameLength);
            _host = Field(Loc.T("Servidor", "Server"), _host, 128);
            _port = Field(Loc.T("Puerto", "Port"), _port, 5);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Contraseña", "Password"), UiStyles.Label, GUILayout.Width(LabelWidth));
            _password = GUILayout.PasswordField(_password, '*', 64);
            GUILayout.EndHorizontal();
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
            if (GUILayout.Button(Loc.T("Ocultar", "Hide"), GUILayout.Width(LabelWidth)))
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
                _core.SetStatus(Loc.T("Indica la dirección del servidor", "Enter the server address"));
                return;
            }

            if (!int.TryParse(_port.Trim(), out int port) || port < 1 || port > 65535)
            {
                _core.SetStatus(Loc.T("Puerto inválido", "Invalid port"));
                return;
            }

            _core.Connect(host, port, name, _password);
        }
    }
}
