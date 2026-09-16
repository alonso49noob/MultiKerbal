using MultiKerbal.Common.Messages;

namespace MultiKerbal.Client
{
    internal static class ClientScenes
    {
        /// <summary>Escenas donde el reloj universal avanza y el jugador participa en el consenso de warp.</summary>
        public static bool TimeFlows
        {
            get
            {
                if (HighLogic.CurrentGame == null)
                    return false;

                switch (HighLogic.LoadedScene)
                {
                    case GameScenes.SPACECENTER:
                    case GameScenes.TRACKSTATION:
                    case GameScenes.FLIGHT:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public static bool IsGameplay
        {
            get
            {
                switch (HighLogic.LoadedScene)
                {
                    case GameScenes.SPACECENTER:
                    case GameScenes.TRACKSTATION:
                    case GameScenes.FLIGHT:
                    case GameScenes.EDITOR:
                        return HighLogic.CurrentGame != null;
                    default:
                        return false;
                }
            }
        }

        public static PlayerActivity Activity
        {
            get
            {
                switch (HighLogic.LoadedScene)
                {
                    case GameScenes.MAINMENU:
                        return PlayerActivity.MainMenu;
                    case GameScenes.SPACECENTER:
                        return PlayerActivity.SpaceCenter;
                    case GameScenes.EDITOR:
                        return PlayerActivity.Editor;
                    case GameScenes.FLIGHT:
                        return PlayerActivity.Flight;
                    case GameScenes.TRACKSTATION:
                        return PlayerActivity.TrackingStation;
                    default:
                        return PlayerActivity.Loading;
                }
            }
        }
    }
}
