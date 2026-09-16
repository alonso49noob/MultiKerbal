using System.Collections.Generic;
using System.Linq;
using MultiKerbal.Common.Messages;
using UnityEngine;

namespace MultiKerbal.Client.Systems
{
    internal sealed class PlayerRegistry
    {
        private readonly Dictionary<int, PlayerInfo> _players = new Dictionary<int, PlayerInfo>();

        public int LocalPlayerId { get; set; }

        public int Count => _players.Count;

        public IEnumerable<PlayerInfo> All => _players.Values.OrderBy(p => p.Id);

        public PlayerInfo Get(int id) => _players.TryGetValue(id, out PlayerInfo player) ? player : null;

        public static Color ColorOf(PlayerInfo player) =>
            player == null ? Color.white : Color.HSVToRGB(player.ColorHue, 0.5f, 1f);

        public void Clear()
        {
            _players.Clear();
            LocalPlayerId = 0;
        }

        public void Handle(IMessage message)
        {
            switch (message)
            {
                case PlayerListMessage list:
                    _players.Clear();
                    foreach (PlayerInfo player in list.Players)
                        _players[player.Id] = player;
                    break;

                case PlayerJoinedMessage joined:
                    _players[joined.Player.Id] = joined.Player;
                    break;

                case PlayerLeftMessage left:
                    _players.Remove(left.PlayerId);
                    break;

                case PlayerStatusMessage status:
                    if (_players.TryGetValue(status.PlayerId, out PlayerInfo existing))
                    {
                        existing.Activity = status.Activity;
                        existing.Detail = status.Detail;
                    }

                    break;
            }
        }
    }
}
