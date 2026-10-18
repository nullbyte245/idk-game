using Game.Core;
using Game.Input;
using Game.Services;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>Esc / Start button routing: only the phase machine decides what pause means.</summary>
    public sealed class GameFlowDriver : MonoBehaviour
    {
        private InputReader _input;
        private GameFlowService _flow;

        private void Start()
        {
            GameServices.TryGet(out _input);
            GameServices.TryGet(out _flow);
        }

        private void OnEnable()
        {
            if (_input != null) _input.PauseToggled += OnPauseToggled;
        }

        private void OnDisable()
        {
            if (_input != null) _input.PauseToggled -= OnPauseToggled;
        }

        private void OnPauseToggled()
        {
            if (_flow == null) return;

            // Screen visibility is derived from the phase inside UIService, so the driver
            // only moves the phase machine.
            switch (_flow.Phase)
            {
                case GamePhase.Playing:
                    _flow.RequestPause();
                    break;
                case GamePhase.Paused:
                    _flow.RequestResume();
                    break;
                case GamePhase.GameOver:
                    _flow.RequestMenu();
                    break;
            }
        }
    }
}
