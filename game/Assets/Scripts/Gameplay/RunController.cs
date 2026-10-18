using System;
using Game.Core;
using Game.Data;
using Game.Input;
using Game.Online;
using Game.Player;
using Game.Services;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>Owns a single run: arms pickups, starts the clock, publishes the final numbers.</summary>
    public sealed class RunController : MonoBehaviour
    {
        [SerializeField] private GameConfig config;
        [SerializeField] private Transform player;
        [SerializeField] private PlayerHealth playerHealth;
        [SerializeField] private RespawnAnchor spawnPoint;
        [SerializeField] private Collectible[] collectibles;
        [SerializeField] private HazardVolume[] hazards;

        private ScoreService _score;
        private TimerService _timer;
        private RunResultService _result;
        private GameFlowService _flow;
        private InputReader _input;
        private LobbyService _lobby;
        private ArenaService _arena;
        private IDisposable _phaseSubscription;
        private IDisposable _scoreSubscription;

        private void Awake()
        {
            config = config != null ? config : GameConfig.CreateDefault();
            if (collectibles == null || collectibles.Length == 0)
                collectibles = FindObjectsByType<Collectible>(FindObjectsSortMode.None);
            if (hazards == null || hazards.Length == 0)
                hazards = FindObjectsByType<HazardVolume>(FindObjectsSortMode.None);
            if (player == null)
            {
                var controller = FindFirstObjectByType<FirstPersonController>();
                if (controller != null) player = controller.transform;
            }
            if (playerHealth == null && player != null)
                playerHealth = player.GetComponent<PlayerHealth>();
            if (spawnPoint == null) spawnPoint = FindFirstObjectByType<RespawnAnchor>();
        }

        private void OnEnable()
        {
            GameServices.TryGet(out _score);
            GameServices.TryGet(out _timer);
            GameServices.TryGet(out _result);
            GameServices.TryGet(out _flow);
            GameServices.TryGet(out _input);
            GameServices.TryGet(out _lobby);
            GameServices.TryGet(out _arena);

            if (_arena != null) _arena.MatchRestarted += OnMatchRestarted;

            if (_flow != null)
            {
                var events = GameServices.Get<EventBus>();
                _phaseSubscription = events.Subscribe<PhaseChangedEvent>(OnPhaseChanged);
                _scoreSubscription = events.Subscribe<ScoreChangedEvent>(OnScoreChanged);
            }
        }

        private void OnDisable()
        {
            if (_arena != null) _arena.MatchRestarted -= OnMatchRestarted;
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
            _scoreSubscription?.Dispose();
            _scoreSubscription = null;
        }

        private void Start()
        {
            WireDependencies();
            if (_flow != null) _flow.RequestMenu();
        }

        public void WireDependencies()
        {
            for (int i = 0; i < collectibles.Length; i++)
                if (collectibles[i] != null) collectibles[i].Bind(player, config);

            for (int i = 0; i < hazards.Length; i++)
                if (hazards[i] != null) hazards[i].Bind(player, playerHealth);
        }

        private void OnPhaseChanged(PhaseChangedEvent args)
        {
            switch (args.Current)
            {
                case GamePhase.Playing:
                    if (args.Previous == GamePhase.Paused)
                    {
                        _input?.SetGameplayEnabled(true);
                        return;
                    }
                    // Retrying out of the game over screen reopens the arena for the whole room, not
                    // just this client: the orbs are spent on the relay, so a restart that left them
                    // there would drop the player into an empty map.
                    if (args.Previous == GamePhase.GameOver) _lobby?.RestartMatch();
                    BeginRun();
                    break;
                case GamePhase.Paused:
                    _input?.SetGameplayEnabled(false);
                    break;
                case GamePhase.Menu:
                case GamePhase.Lobbies:
                case GamePhase.Room:
                    _input?.SetGameplayEnabled(false);
                    _timer?.Stop();
                    break;
                case GamePhase.GameOver:
                    _input?.SetGameplayEnabled(false);
                    _timer?.Stop();
                    PublishFinished();
                    break;
            }
        }

        private void BeginRun()
        {
            _score?.BeginRun(config);
            _timer?.Start();

            if (playerHealth != null) playerHealth.ResetHealth();
            for (int i = 0; i < collectibles.Length; i++)
                if (collectibles[i] != null) collectibles[i].Revive();

            if (player != null && spawnPoint != null)
            {
                var cc = player.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                player.position = spawnPoint.Position;
                if (cc != null) cc.enabled = true;
            }

            GameServices.Get<EventBus>().Publish(new RunStartedEvent(config.collectibleCount));
            _input?.SetGameplayEnabled(true);
        }

        /// <summary>
        /// The host reopened the match, and everybody - the host included - learns that from the relay
        /// rather than from the button. A client that was still mid-round has to rebuild its arena the
        /// same way, or it keeps playing against an orb set that no longer exists.
        /// </summary>
        private void OnMatchRestarted()
        {
            if (_flow == null) return;
            if (_flow.IsPlaying) BeginRun();
            else _flow.RequestPlay();
        }

        private void PublishFinished()
        {
            var victory = _score != null && _score.AllCollected;
            var finalScore = _score != null ? _score.Current : 0f;
            var elapsed = _timer != null ? _timer.Elapsed : 0f;

            _result?.Record(finalScore, elapsed, victory);
            GameServices.Get<EventBus>().Publish(new RunFinishedEvent(finalScore, elapsed, victory));
        }

        /// <summary>Collecting the last orb ends the run immediately as a win.</summary>
        private void OnScoreChanged(ScoreChangedEvent args)
        {
            if (_score == null || !_score.AllCollected) return;
            _flow?.RequestGameOver();
        }
    }
}
