using System;
using Game.Core;
using Game.Data;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    public sealed class PlayerHealth : MonoBehaviour
    {
        [SerializeField] private GameConfig config;
        [SerializeField] private int respawnPoints = 3;

        private IDisposable _phaseSubscription;
        private GameFlowService _flow;

        public float Current { get; private set; }
        public float Max { get; private set; }
        public int RespawnPointsLeft { get; private set; }
        public bool IsDead => Current <= 0f;
        public float Ratio => Max > 0f ? Current / Max : 0f;

        public event Action Changed;
        public event Action Died;

        private void Awake()
        {
            config = config != null ? config : GameConfig.CreateDefault();
            Max = config.maxHealth;
        }

        private void OnEnable()
        {
            if (GameServices.TryGet(out GameFlowService flow)) _flow = flow;
            if (_flow != null)
                _phaseSubscription = GameServices.Get<EventBus>()
                    .Subscribe<PhaseChangedEvent>(OnPhaseChanged);

            if (IsDead) ResetHealth();
        }

        private void OnDisable()
        {
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
        }

        public void ResetHealth()
        {
            RespawnPointsLeft = respawnPoints;
            Current = Max;
            Changed?.Invoke();
        }

        public void Damage(float amount)
        {
            if (IsDead || amount <= 0f) return;
            if (_flow != null && !_flow.IsPlaying) return;

            Current = Mathf.Max(0f, Current - amount);
            Changed?.Invoke();
            if (Current > 0f) return;

            HandleDepleted();
        }

        public void Heal(float amount)
        {
            if (IsDead || amount <= 0f) return;
            Current = Mathf.Min(Max, Current + amount);
            Changed?.Invoke();
        }

        public void Kill()
        {
            if (IsDead) return;
            Current = 0f;
            Changed?.Invoke();
            HandleDepleted();
        }

        private void HandleDepleted()
        {
            RespawnPointsLeft = Mathf.Max(0, RespawnPointsLeft - 1);

            if (RespawnPointsLeft > 0)
            {
                Debug.Log($"Player down, {RespawnPointsLeft} respawn(s) left.");
                Current = Max;
                Changed?.Invoke();
                TeleportToSpawn();
                return;
            }

            Current = 0f;
            Died?.Invoke();
            if (GameServices.TryGet(out GameFlowService flow)) flow.RequestGameOver();
        }

        private void TeleportToSpawn()
        {
            var controller = GetComponent<FirstPersonController>();
            if (controller != null) controller.GetComponent<CharacterController>().enabled = false;

            var spawn = FindFirstObjectByType<Game.Gameplay.RespawnAnchor>();
            if (spawn != null) transform.position = spawn.Position;
            else transform.position = Vector3.up * 1f;

            if (controller != null) controller.GetComponent<CharacterController>().enabled = true;
        }

        private void OnPhaseChanged(PhaseChangedEvent args)
        {
            if (args.Current == GamePhase.Playing && args.Previous != GamePhase.Paused)
                ResetHealth();
        }
    }
}
