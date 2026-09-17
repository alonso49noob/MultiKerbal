namespace MultiKerbal.Client.UI
{
    /// <summary>Respeta la tecla de ocultar interfaz de KSP (F2).</summary>
    internal static class UiVisibility
    {
        private static Listener _listener;

        public static bool Visible { get; private set; } = true;

        /// <summary>Se registra la primera vez que se dibuja algo.</summary>
        public static void Ensure()
        {
            if (_listener != null)
                return;

            // GameEvents solo acepta métodos de instancia: con uno estático lanza NullReferenceException al registrarlo.
            _listener = new Listener();
            GameEvents.onHideUI.Add(_listener.OnHide);
            GameEvents.onShowUI.Add(_listener.OnShow);
        }

        private sealed class Listener
        {
            public void OnHide() => Visible = false;

            public void OnShow() => Visible = true;
        }
    }
}
