using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class GameOverScreen : ScreenBase
    {
        public override string Id => UIScreenIds.GameOver;

        [SerializeField] private Text resultTitle;
        [SerializeField] private Text resultScore;
        [SerializeField] private Text resultTime;
        [SerializeField] private Button retryButton;
        [SerializeField] private Button menuButton;

        private GameFlowService _flow;
        private RunResultService _result;

        public void Bind(GameFlowService flow, RunResultService result)
        {
            _flow = flow;
            _result = result;
            if (retryButton != null) retryButton.onClick.AddListener(() => _flow?.RequestPlay());
            if (menuButton != null) menuButton.onClick.AddListener(() => _flow?.RequestMenu());
        }

        /// <summary>Reads the stored result: the run may have ended before this screen was enabled.</summary>
        protected override void OnOpen()
        {
            base.OnOpen();
            if (_result == null || !_result.HasResult) return;

            if (resultTitle != null) resultTitle.text = _result.Victory ? "Victory" : "Run over";
            if (resultScore != null) resultScore.text = $"Score  {_result.FinalScore:0}";
            if (resultTime != null) resultTime.text = $"Time  {_result.FormattedTime}";
        }
    }
}
