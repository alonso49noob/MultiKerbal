using System;
using KSP.UI.Screens;
using UnityEngine;

namespace MultiKerbal.Client.UI
{
    /// <summary>Botón en la barra de aplicaciones de KSP; solo existe mientras hay sesión multijugador.</summary>
    internal sealed class ToolbarButton
    {
        private const ApplicationLauncher.AppScenes Scenes =
            ApplicationLauncher.AppScenes.SPACECENTER
            | ApplicationLauncher.AppScenes.FLIGHT
            | ApplicationLauncher.AppScenes.MAPVIEW
            | ApplicationLauncher.AppScenes.VAB
            | ApplicationLauncher.AppScenes.SPH
            | ApplicationLauncher.AppScenes.TRACKSTATION;

        private readonly Action<bool> _onToggle;
        private ApplicationLauncherButton _button;
        private Texture2D _icon;
        private bool _available;
        private bool _on;

        public ToolbarButton(Action<bool> onToggle)
        {
            _onToggle = onToggle;
        }

        public void Register()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(Refresh);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnLauncherDestroyed);
        }

        public void Unregister()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(Refresh);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnLauncherDestroyed);
            RemoveButton();
        }

        public void SetAvailable(bool available)
        {
            if (_available == available)
                return;

            _available = available;
            if (!available)
                _on = false;
            Refresh();
        }

        public void SetOn(bool on)
        {
            _on = on;
            if (_button == null)
                return;

            if (on)
                _button.SetTrue(false);
            else
                _button.SetFalse(false);
        }

        private void Refresh()
        {
            if (!_available)
            {
                RemoveButton();
                return;
            }

            if (_button != null || !ApplicationLauncher.Ready || ApplicationLauncher.Instance == null)
                return;

            if (_icon == null)
                _icon = CreateIcon();

            _button = ApplicationLauncher.Instance.AddModApplication(
                () => _onToggle(true),
                () => _onToggle(false),
                null,
                null,
                null,
                null,
                Scenes,
                _icon);
            if (_on)
                _button.SetTrue(false);
        }

        private void OnLauncherDestroyed() => _button = null;

        private void RemoveButton()
        {
            if (_button == null)
                return;

            if (ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        /// <summary>Icono generado en código (dos círculos = dos jugadores) para no depender de texturas.</summary>
        private static Texture2D CreateIcon()
        {
            const int size = 38;
            var pixels = new Color32[size * size];
            DrawDisc(pixels, size, 14f, 19f, 9f, new Color32(110, 200, 255, 255));
            DrawDisc(pixels, size, 24f, 19f, 9f, new Color32(255, 196, 80, 255));

            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        private static void DrawDisc(Color32[] pixels, int size, float centerX, float centerY, float radius, Color32 color)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - centerX;
                    float dy = y + 0.5f - centerY;
                    float coverage = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    if (coverage <= 0f)
                        continue;

                    int index = y * size + x;
                    Color32 blended = Color32.Lerp(pixels[index], color, coverage);
                    blended.a = (byte)Mathf.Max(pixels[index].a, color.a * coverage);
                    pixels[index] = blended;
                }
            }
        }
    }
}
