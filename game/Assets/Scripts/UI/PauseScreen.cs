using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class PauseScreen : ScreenBase
    {
        public override string Id => UIScreenIds.Pause;
        public override ScreenKind Kind => ScreenKind.Overlay;

        [SerializeField] private Button resumeButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button menuButton;

        private GameFlowService _flow;
        private UIService _ui;

        public void Bind(GameFlowService flow, UIService ui)
        {
            _flow = flow;
            _ui = ui;
            if (resumeButton != null) resumeButton.onClick.AddListener(() => _flow?.RequestResume());
            if (settingsButton != null) settingsButton.onClick.AddListener(() => _ui?.Open(UIScreenIds.Settings));
            if (menuButton != null) menuButton.onClick.AddListener(() => _flow?.RequestMenu());
        }
    }
}
