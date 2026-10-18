using Game.Online;
using Game.Services;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>Overlay sheet that names a new lobby. Opens on top of the lobby list.</summary>
    [DisallowMultipleComponent]
    public sealed class CreateLobbyScreen : ScreenBase
    {
        public override string Id => UIScreenIds.CreateLobby;
        public override ScreenKind Kind => ScreenKind.Overlay;

        [SerializeField] private InputField lobbyNameField;
        [SerializeField] private Text statusText;
        [SerializeField] private Button createButton;
        [SerializeField] private Button cancelButton;

        private UIService _ui;
        private LobbyService _lobby;

        public void Bind(UIService ui, LobbyService lobby)
        {
            _ui = ui;
            _lobby = lobby;

            if (createButton != null) createButton.onClick.AddListener(OnCreate);
            if (cancelButton != null) cancelButton.onClick.AddListener(() => _ui?.Close(Id));
        }

        protected override void OnOpen()
        {
            if (statusText != null) statusText.text = "Your lobby is public: anyone in the list can join it.";
            if (lobbyNameField == null) return;

            lobbyNameField.text = string.Empty;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(lobbyNameField.gameObject);
        }

        private void OnCreate()
        {
            var name = lobbyNameField != null ? lobbyNameField.text : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (statusText != null) statusText.text = "Type a lobby name first.";
                return;
            }

            _lobby?.CreateLobby(name);
            _ui?.Close(Id);
        }
    }
}
