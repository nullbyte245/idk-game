using System.Collections.Generic;
using Game.Online;
using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// The online entry point: every lobby the registry knows about, a create button and the player's
    /// own nickname in the corner. It owns no game state, it only mirrors the server.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LobbyListScreen : ScreenBase
    {
        public override string Id => UIScreenIds.LobbyList;

        [SerializeField] private Transform rowRoot;
        [SerializeField] private GameObject rowTemplate;
        [SerializeField] private Text statusText;
        [SerializeField] private Button createButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button backButton;

        private readonly List<LobbyRow> _rows = new List<LobbyRow>();
        private GameFlowService _flow;
        private UIService _ui;
        private LobbyService _lobby;

        public void Bind(GameFlowService flow, UIService ui, LobbyService lobby)
        {
            _flow = flow;
            _ui = ui;
            _lobby = lobby;

            if (createButton != null) createButton.onClick.AddListener(() => _ui?.Open(UIScreenIds.CreateLobby));
            if (refreshButton != null) refreshButton.onClick.AddListener(() => _lobby?.BrowseList());
            if (backButton != null) backButton.onClick.AddListener(() => _flow?.RequestMenu());
        }

        protected override void OnOpen()
        {
            if (_lobby == null) return;
            _lobby.Changed += OnLobbyChanged;
            _lobby.BrowseList();
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
            // Joining or creating lands the player in the room; the phase change closes this screen.
            if (_lobby.Room != null)
            {
                _flow?.RequestRoom();
                return;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            if (statusText != null) statusText.text = _lobby.Status;
            var lobbies = _lobby.Lobbies;

            for (var i = 0; i < lobbies.Length; i++)
            {
                var row = EnsureRow(i);
                if (row == null) break;
                var id = lobbies[i].id;
                row.Show(lobbies[i], () => _lobby.JoinLobby(id));
            }

            for (var i = lobbies.Length; i < _rows.Count; i++) _rows[i].gameObject.SetActive(false);
        }

        private LobbyRow EnsureRow(int index)
        {
            if (index < _rows.Count) return _rows[index];
            if (rowTemplate == null || rowRoot == null) return null;

            var clone = Instantiate(rowTemplate, rowRoot);
            var row = clone.GetComponent<LobbyRow>();
            if (row == null)
            {
                Destroy(clone);
                Debug.LogError("LobbyListScreen rowTemplate has no LobbyRow component.");
                return null;
            }
            _rows.Add(row);
            return row;
        }
    }
}
