using System;
using Game.Core;
using UnityEngine;

namespace Game.Services
{
    /// <summary>
    /// Owns the game phase. Everything else (UI, gameplay, camera, input) reacts to
    /// PhaseChangedEvent instead of asking for permission directly.
    /// </summary>
    public sealed class GameFlowService : IService
    {
        private EventBus _events;
        private GamePhase _phase = GamePhase.Boot;

        public GamePhase Phase => _phase;
        public bool IsPlaying => _phase == GamePhase.Playing;
        public bool IsPaused => _phase == GamePhase.Paused;
        public bool IsInRun => _phase == GamePhase.Playing || _phase == GamePhase.Paused;

        /// <summary>Phases that show the character from outside: the menu, the lobby browser, boot.</summary>
        public bool IsShowcase => _phase == GamePhase.Boot || _phase == GamePhase.Menu
                                  || _phase == GamePhase.Lobbies || _phase == GamePhase.Room;

        public void OnRegister(IServiceRegistry registry) => _events = registry.Get<EventBus>();
        public void OnDispose() { }

        public bool Transition(GamePhase next)
        {
            if (next == _phase) return false;
            if (!IsAllowed(_phase, next))
            {
                Debug.LogWarning($"Illegal phase transition {_phase} -> {next}, ignored.");
                return false;
            }

            var previous = _phase;
            _phase = next;
            ApplyTimeScale(next);
            _events.Publish(new PhaseChangedEvent(previous, next));
            return true;
        }

        public void RequestPlay() => Transition(GamePhase.Playing);
        public void RequestPause() => Transition(GamePhase.Paused);
        public void RequestResume() => Transition(GamePhase.Playing);
        public void RequestGameOver()
        {
            Transition(GamePhase.GameOver);
        }

        public void RequestLobbies() => Transition(GamePhase.Lobbies);
        public void RequestRoom() => Transition(GamePhase.Room);
        public void RequestMenu() => Transition(GamePhase.Menu);

        private static bool IsAllowed(GamePhase from, GamePhase to)
        {
            switch (from)
            {
                case GamePhase.Boot: return to == GamePhase.Menu;
                case GamePhase.Menu: return to == GamePhase.Lobbies;
                case GamePhase.Lobbies: return to == GamePhase.Menu || to == GamePhase.Room;
                case GamePhase.Room: return to == GamePhase.Lobbies || to == GamePhase.Playing || to == GamePhase.Menu;
                case GamePhase.Playing: return to == GamePhase.Paused || to == GamePhase.GameOver || to == GamePhase.Menu;
                case GamePhase.Paused: return to == GamePhase.Playing || to == GamePhase.Menu;
                case GamePhase.GameOver: return to == GamePhase.Playing || to == GamePhase.Lobbies || to == GamePhase.Menu;
                default: return false;
            }
        }

        private static void ApplyTimeScale(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Playing:
                    Time.timeScale = 1f;
                    break;
                case GamePhase.GameOver:
                    Time.timeScale = 0.35f;
                    break;
                default:
                    Time.timeScale = 0f;
                    break;
            }
        }
    }
}
