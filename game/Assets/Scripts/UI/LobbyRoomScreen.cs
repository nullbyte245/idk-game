using System.Collections.Generic;
using Game.Online;
using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// The room the player is standing in: everyone's nickname, and a start button that only answers
    /// to the host. Everyone else waits for the reply that says the match began.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LobbyRoomScreen : ScreenBase
    {
        public override string Id => UIScreenIds.LobbyRoom;

        [SerializeField] private Transform rowRoot;
        [SerializeField] private GameObject rowTemplate;
        [SerializeField] private Text lobbyTitle;
        [SerializeField] private Text statusText;
        [SerializeField] private Button startButton;
        [SerializeField] private Button leaveButton;

        private readonly List<LobbyPlayerRow> _rows = new List<LobbyPlayerRow>();
        private GameFlowService _flow;
        private LobbyService _lobby;

        public void Bind(GameFlowService flow, LobbyService lobby)
        {
            _flow = flow;
            _lobby = lobby;

            if (startButton != null) startButton.onClick.AddListener(() => _lobby?.StartMatch());
            if (leaveButton != null) leaveButton.onClick.AddListener(() => _lobby?.LeaveLobby());
        }

        protected override void OnOpen()
        {
            if (_lobby == null) return;
            _lobby.Changed += OnLobbyChanged;
            _lobby.BrowseRoom();
            Rebuild();
        }

        protected override void OnClose()
        {
            if (_lobby == null) return;
            _lobby.Changed -= OnLobbyChanged;
            _lobby.StopBrowsing();
        }

        private void OnLobbyChanged()
        {
            if (_lobby.Room == null)
            {
                _flow?.RequestLobbies();
                return;
            }
            if (_lobby.Room.started)
            {
                _flow?.RequestPlay();
                return;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            var room = _lobby.Room;
            if (room == null) return;

            if (lobbyTitle != null) lobbyTitle.text = room.name;
            if (statusText != null)
            {
                statusText.text = _lobby.IsHost
                    ? $"{room.players?.Length ?? 0} / {room.maxPlayers} players. Start the match when everyone is here."
                    : $"{room.players?.Length ?? 0} / {room.maxPlayers} players. Waiting for the host to start.";
            }

            var players = room.players;
            var count = players != null ? players.Length : 0;
            for (var i = 0; i < count; i++)
            {
                var row = EnsureRow(i);
                if (row == null) break;
                row.Show(players[i], _lobby.ClientId);
            }
            for (var i = count; i < _rows.Count; i++) _rows[i].gameObject.SetActive(false);

            if (startButton == null) return;
            startButton.interactable = _lobby.IsHost;
            var label = startButton.GetComponentInChildren<Text>();
            if (label != null) label.text = _lobby.IsHost ? "START MATCH" : "WAITING FOR HOST";
        }

        private LobbyPlayerRow EnsureRow(int index)
        {
            if (index < _rows.Count) return _rows[index];
            if (rowTemplate == null || rowRoot == null) return null;

            var clone = Instantiate(rowTemplate, rowRoot);
            var row = clone.GetComponent<LobbyPlayerRow>();
            if (row == null)
            {
                Destroy(clone);
                Debug.LogError("LobbyRoomScreen rowTemplate has no LobbyPlayerRow component.");
                return null;
            }
            _rows.Add(row);
            return row;
        }
    }
}
