namespace MultiKerbal.Client
{
    /// <summary>La partida local de KSP: solo una caché del universo del servidor.</summary>
    internal static class GameSession
    {
        public const string SaveFolderName = "MultiKerbal";

        public static void StartSandbox(double universalTime)
        {
            var parameters = new GameParameters();
            // Todo lo que permite viajar al pasado rompe el reloj compartido.
            parameters.Flight.CanQuickSave = false;
            parameters.Flight.CanQuickLoad = false;
            parameters.Flight.CanRestart = false;
            parameters.Flight.CanLeaveToEditor = false;

            Game game = GamePersistence.CreateNewGame(SaveFolderName, Game.Modes.SANDBOX, parameters, "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            if (game.flightState == null)
                game.flightState = new FlightState();
            game.flightState.universalTime = universalTime;
            HighLogic.CurrentGame = game;
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);

            ClientLog.Info($"Iniciando partida multijugador en UT {universalTime:0.0}");
            game.Start();
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
