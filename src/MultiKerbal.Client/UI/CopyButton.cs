using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Botón "Copiar" que deja el texto en el portapapeles y muestra una marca un par de segundos.</summary>
    internal static class CopyButton
    {
        private const float CopiedSeconds = 2f;

        private static string _copied;
        private static float _copiedAt;

        public static void Draw(string text, float width = 80f)
        {
            bool justCopied = _copied == text && Time.realtimeSinceStartup - _copiedAt < CopiedSeconds;
            if (!GUILayout.Button(justCopied ? UiStyles.Tick : Loc.T("Copiar", "Copy"), GUILayout.Width(width)))
                return;

            GUIUtility.systemCopyBuffer = text;
            _copied = text;
            _copiedAt = Time.realtimeSinceStartup;
        }
    }
}
