using Game.Core;
using Game.Data;
using Game.Services;
using System;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Pickup by proximity polling rather than trigger callbacks: a CharacterController without a
    /// Rigidbody does not reliably generate OnTriggerEnter pairs, and a deterministic pickup reads
    /// better in a headless build than a silently missing callback.
    /// </summary>
    public sealed class Collectible : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float pickupRadius = 1.3f;
        [SerializeField] private Vector3 spinAxis = new Vector3(0f, 1f, 0f);
        [SerializeField] private float spinSpeed = 90f;
        [SerializeField] private float bobAmplitude = 0.18f;
        [SerializeField] private float bobFrequency = 1.4f;

        private GameConfig _config;
        private ScoreService _score;
        private Vector3 _basePosition;
        private float _phaseOffset;
        private bool _collected;

        public bool IsCollected => _collected;

        /// <summary>Stable across clients because the arena is built from the same layout: the relay
        /// arbitrates orbs by this name and nothing else.</summary>
        public string OrbId => gameObject.name;

        /// <summary>
        /// Set by the online bridge while a match is running. With it on, touching an orb only proposes
        /// the claim; the orb is spent when the relay's authoritative set says it is.
        /// </summary>
        public bool UsesAuthority { get; set; }

        /// <summary>Raised instead of taking the orb while <see cref="UsesAuthority"/> is on.</summary>
        public event Action<Collectible> Touched;

        private void Awake()
        {
            _basePosition = transform.position;
            _phaseOffset = transform.position.x * 0.7f + transform.position.z * 1.3f;
            GameServices.TryGet(out _score);
        }

        private void OnEnable() => Revive();

        public void Bind(Transform player, GameConfig config)
        {
            target = player;
            _config = config;
        }

        public void Revive()
        {
            _collected = false;
            gameObject.SetActive(true);
        }

        /// <summary>
        /// Same pickup for a shot and for walking into it: the orb leaves the run exactly once,
        /// however it was taken.
        /// </summary>
        public void Collect()
        {
            if (_collected) return;
            // Two players can close on the same orb in the same tenth of a second. Whoever the relay
            // heard first owns it, and the loser's copy simply stays on the shelf for the next run.
            if (UsesAuthority)
            {
                Touched?.Invoke(this);
                return;
            }
            Take();
        }

        /// <summary>The spend itself: called locally, or by the bridge once the relay has confirmed it.</summary>
        public void Take()
        {
            if (_collected) return;
            _collected = true;
            gameObject.SetActive(false);
            if (_score != null) _score.AddCollectible(_config ?? GameConfig.CreateDefault());
        }

        private void Update()
        {
            if (_collected) return;

            var dt = Time.deltaTime;
            transform.Rotate(spinAxis, spinSpeed * dt, Space.World);
            transform.position = _basePosition + Vector3.up *
                (Mathf.Sin(Time.time * bobFrequency + _phaseOffset) * bobAmplitude);

            if (target == null) return;
            if (Vector3.Distance(transform.position, target.position) > pickupRadius) return;

            Collect();
        }
    }
}
