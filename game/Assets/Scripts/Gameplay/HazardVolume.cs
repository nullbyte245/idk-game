using Game.Data;
using Game.Player;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>Area that drains health while the player stands inside it.</summary>
    public sealed class HazardVolume : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private GameConfig config;
        [SerializeField] private PlayerHealth targetHealth;

        private Collider _collider;
        private float _nextDamageTime;

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            if (config == null) config = GameConfig.CreateDefault();
            if (targetHealth == null && target != null)
                targetHealth = target.GetComponentInParent<PlayerHealth>();
        }

        public void Bind(Transform player, PlayerHealth health)
        {
            target = player;
            targetHealth = health != null ? health : (target != null ? target.GetComponentInParent<PlayerHealth>() : null);
        }

        private void Update()
        {
            if (target == null || targetHealth == null || _collider == null) return;
            if (!_collider.bounds.Contains(target.position)) return;
            if (Time.time < _nextDamageTime) return;

            _nextDamageTime = Time.time + config.hazardDamageInterval;
            targetHealth.Damage(config.hazardDamage);
        }

        private void OnDrawGizmosSelected()
        {
            var c = GetComponent<Collider>();
            if (c == null) return;
            Gizmos.color = new Color(0.9f, 0.25f, 0.2f, 0.5f);
            Gizmos.DrawWireCube(c.bounds.center, c.bounds.size);
        }
    }
}
