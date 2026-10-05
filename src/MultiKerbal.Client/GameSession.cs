using MultiKerbal.Common.Progress;

namespace MultiKerbal.Client
{
    /// <summary>La partida local de KSP: solo una caché del universo del servidor.</summary>
    internal static class GameSession
    {
        public const string SaveFolderName = "MultiKerbal";

        /// <summary>
        /// Crea la partida local del mismo tipo que la del servidor. En ciencia y carrera el progreso lo pone después
        /// el servidor (ver <see cref="Systems.ProgressSync"/>).
        /// </summary>
        public static void StartGame(double universalTime, GameMode mode)
        {
            var parameters = new GameParameters();
            // Todo lo que permite viajar al pasado rompe el reloj compartido.
            parameters.Flight.CanQuickSave = false;
            parameters.Flight.CanQuickLoad = false;
            parameters.Flight.CanRestart = false;
            parameters.Flight.CanLeaveToEditor = false;

            Game game = GamePersistence.CreateNewGame(SaveFolderName, KspMode(mode), parameters, "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            if (game.flightState == null)
                game.flightState = new FlightState();
            game.flightState.universalTime = universalTime;
            HighLogic.CurrentGame = game;
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);

            ClientLog.Info($"Iniciando partida multijugador ({mode}) en UT {universalTime:0.0}");
            game.Start();
        }

        private static Game.Modes KspMode(GameMode mode)
        {
            switch (mode)
            {
                case GameMode.Science:
                    return Game.Modes.SCIENCE_SANDBOX;
                case GameMode.Career:
                    return Game.Modes.CAREER;
                default:
                    return Game.Modes.SANDBOX;
            }
        }

        public static void ReturnToMainMenu()
        {
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
                return;

            if (TimeWarp.fetch != null)
                TimeWarp.SetRate(0, true, false);
            HighLogic.LoadScene(GameScenes.MAINMENU);
        }
    }
}
