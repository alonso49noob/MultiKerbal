using System;
using UnityEngine;

namespace MultiKerbal.Client
{
    /// <summary>
    /// Punto de entrada: KSP lo crea al arrancar y persiste entre escenas. Solo delega en <see cref="ClientCore"/>
    /// y evita que una excepción de un frame rompa el resto del juego.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class MultiKerbalAddon : MonoBehaviour
    {
        private const int MaxLoggedErrors = 20;

        private ClientCore _core;
        private int _loggedErrors;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _core = new ClientCore();
            ClientLog.Info($"MultiKerbal {ClientCore.ModVersion} cargado");
        }

        private void Start() => Guard(_core.Start);

        private void Update() => Guard(_core.Update);

        private void OnGUI() => Guard(_core.OnGUI);

        private void OnApplicationQuit() => Guard(() => _core.Shutdown("Cerró el juego"));

        private void OnDestroy() => Guard(() => _core.Shutdown("Cerró el juego"));

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (_loggedErrors++ < MaxLoggedErrors)
                    ClientLog.Error(ex.ToString());
            }
        }
    }
}
