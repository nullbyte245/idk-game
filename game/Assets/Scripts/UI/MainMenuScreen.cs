using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class MainMenuScreen : ScreenBase
    {
        public override string Id => UIScreenIds.MainMenu;

        [SerializeField] private Button playButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private Text versionLabel;

        private GameFlowService _flow;
        private UIService _ui;

        public void Bind(GameFlowService flow, UIService ui, string version)
        {
            _flow = flow;
            _ui = ui;

            if (versionLabel != null) versionLabel.text = version;
            if (playButton != null) playButton.onClick.AddListener(OnPlay);
            if (settingsButton != null) settingsButton.onClick.AddListener(() => _ui?.Open(UIScreenIds.Settings));
            if (quitButton != null) quitButton.onClick.AddListener(OnQuit);
        }

        // Play means "find a lobby", never straight into a run: an online run needs a room first.
        private void OnPlay() => _flow?.RequestLobbies();

        private void OnQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
