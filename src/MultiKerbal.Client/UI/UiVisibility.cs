namespace MultiKerbal.Client.UI
{
    /// <summary>Respeta la tecla de ocultar interfaz de KSP (F2).</summary>
    internal static class UiVisibility
    {
        private static bool _registered;

        public static bool Visible { get; private set; } = true;

        /// <summary>Se registra la primera vez que se dibuja algo.</summary>
        public static void Ensure()
        {
            if (_registered)
                return;

            _registered = true;
            GameEvents.onHideUI.Add(OnHide);
            GameEvents.onShowUI.Add(OnShow);
        }

        private static void OnHide() => Visible = false;

        private static void OnShow() => Visible = true;
    }
}
