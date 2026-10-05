using System;
using KSP.Localization;
using MultiKerbal.Common.Messages;

namespace MultiKerbal.Client.Systems
{
    /// <summary>Informa al servidor de la escena y la nave del jugador cuando cambian.</summary>
    internal sealed class StatusReporter
    {
        private const double CheckIntervalSeconds = 1.0;

        private bool _hasSent;
        private PlayerActivity _sentActivity;
        private string _sentDetail;
        private double _nextCheck;

        public void Reset()
        {
            _hasSent = false;
            _nextCheck = 0;
        }

        /// <param name="watching">Se está mirando una nave ajena: la nave activa no es la nuestra.</param>
        public void Update(Action<IMessage> send, bool watching)
        {
            double now = LocalClock.Now;
            if (now < _nextCheck)
                return;
            _nextCheck = now + CheckIntervalSeconds;

            PlayerActivity activity = ClientScenes.Activity;
            if (activity == PlayerActivity.Flight && watching)
                activity = PlayerActivity.Watching;
            Vessel vessel = activity == PlayerActivity.Flight || activity == PlayerActivity.Watching ? FlightGlobals.ActiveVessel : null;
            string detail = vessel != null ? Localizer.Format(vessel.vesselName) : string.Empty;
            if (_hasSent && activity == _sentActivity && detail == _sentDetail)
                return;

            _hasSent = true;
            _sentActivity = activity;
            _sentDetail = detail;
            send(new PlayerStatusMessage { Activity = activity, Detail = detail });
        }
    }
}
