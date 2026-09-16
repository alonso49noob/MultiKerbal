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

        private void DrawContents(int id)
        {
            bool idle = _core.State == SessionState.Disconnected;

            GUI.enabled = idle;
            _name = Field("Nombre", _name, ProtocolInfo.MaxPlayerNameLength);
            _host = Field("Servidor", _host, 128);
            _port = Field("Puerto", _port, 5);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Contraseña", UiStyles.Label, GUILayout.Width(LabelWidth));
            _password = GUILayout.PasswordField(_password, '*', 64);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_core.StatusText))
                GUILayout.Label(_core.StatusText, UiStyles.Label);

            GUILayout.BeginHorizontal();
            if (idle)
            {
                if (GUILayout.Button("Conectar y jugar"))
                    TryConnect();
            }
            else if (GUILayout.Button("Cancelar"))
            {
                _core.Disconnect("Conexión cancelada");
            }

            if (GUILayout.Button("Ocultar", GUILayout.Width(LabelWidth)))
                Visible = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private static string Field(string label, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UiStyles.Label, GUILayout.Width(LabelWidth));
            value = GUILayout.TextField(value ?? string.Empty, maxLength);
            GUILayout.EndHorizontal();
            return value;
        }

        private void TryConnect()
        {
            string name = _name.Trim();
            string host = _host.Trim();
            if (name.Length < ProtocolInfo.MinPlayerNameLength)
            {
                _core.SetStatus($"El nombre debe tener al menos {ProtocolInfo.MinPlayerNameLength} caracteres");
                return;
            }

            if (host.Length == 0)
            {
                _core.SetStatus("Indica la dirección del servidor");
                return;
            }

            if (!int.TryParse(_port.Trim(), out int port) || port < 1 || port > 65535)
            {
                _core.SetStatus("Puerto inválido");
                return;
            }

            _core.Connect(host, port, name, _password);
        }
    }
}
