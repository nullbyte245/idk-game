using System;
using Game.Core;
using Game.Data;
using Game.Gameplay;
using Game.Input;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Hitscan rifle. A shot is a ray fired from the eye camera straight down its forward axis, so the
    /// crosshair is literally where the bullet goes; nothing travels through the world and nothing can
    /// miss because of a frame rate.
    ///
    /// What does travel is the picture of it: the ray hands its endpoint to <see cref="ShotEffects"/>,
    /// which draws the tracer, the flame and the burst. This component decides where a round goes and
    /// never touches a renderer.
    ///
    /// Aim is not free: every round widens a cone, the cone is what the crosshair gap draws, and the
    /// ray is sampled inside that cone. Short taps keep it tight, holding the button walks the group
    /// out to the configured maximum.
    ///
    /// Orbs count as a hit, which gives the rifle something to do in a level with no enemies yet.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HitscanWeapon : MonoBehaviour
    {
        [SerializeField] private GameConfig config;
        [SerializeField] private Transform aimOrigin;
        [SerializeField] private LayerMask aimMask = ~0;
        [SerializeField] private ShotEffects effects;

        private InputReader _input;
        private GameFlowService _flow;
        private IDisposable _phaseSubscription;

        private int _ammo;
        private float _nextShotTime;
        private float _reloadReadyTime;
        private bool _reloading;
        private float _spread;

        public int AmmoInMagazine => _ammo;
        public int MagazineSize => config != null ? config.magazineSize : 0;
        public bool IsReloading => _reloading;
        public float SpreadDegrees => _spread;

        /// <summary>Rises with every shot; the viewmodel and the crosshair both read it.</summary>
        public int ShotsFired { get; private set; }

        /// <summary>Time.time of the last round that hit an orb, used for the hit marker.</summary>
        public float LastOrbHitTime { get; private set; } = float.NegativeInfinity;

        /// <summary>Muzzle then endpoint of every round fired. The relay is the only listener.</summary>
        public event Action<Vector3, Vector3> ShotPlayed;

        private void Awake()
        {
            config = config != null ? config : GameConfig.CreateDefault();
            if (aimOrigin == null) aimOrigin = Camera.main != null ? Camera.main.transform : null;
            _ammo = config.magazineSize;
        }

        private void OnEnable()
        {
            if (GameServices.TryGet(out InputReader input))
            {
                _input = input;
                _input.ReloadPerformed += OnReloadRequested;
            }
            if (GameServices.TryGet(out GameFlowService flow)) _flow = flow;
            if (_flow == null) return;

            _phaseSubscription = GameServices.Get<EventBus>().Subscribe<PhaseChangedEvent>(OnPhaseChanged);
            if (_flow.Phase == GamePhase.Playing) OnPhaseChanged(new PhaseChangedEvent(_flow.Phase, _flow.Phase));
        }

        private void OnDisable()
        {
            if (_input != null) _input.ReloadPerformed -= OnReloadRequested;
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
            _spread = 0f;
        }

        private void Update()
        {
            if (_flow == null || !_flow.IsPlaying)
            {
                _spread = 0f;
                return;
            }

            var dt = Time.deltaTime;
            if (_spread > 0f) _spread = Mathf.Max(0f, _spread - config.spreadRecovery * dt);

            if (_reloading)
            {
                if (Time.time < _reloadReadyTime) return;
                _reloading = false;
                _ammo = config.magazineSize;
                return;
            }

            if (_ammo <= 0)
            {
                // An empty magazine reloads by itself; the alternative is a player staring at a rifle
                // that stopped responding without telling them why.
                StartReload();
                return;
            }

            if (_input != null && _input.IsFiring && Time.time >= _nextShotTime) Fire();
        }

        private void Fire()
        {
            if (aimOrigin == null) return;

            var forward = aimOrigin.forward;
            var direction = forward;
            if (_spread > 0.001f)
            {
                // Uniform over the disc, not over the radius: sampling the radius alone would pile
                // most of the shots into a point dead centre.
                var angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var radius = _spread * Mathf.Sqrt(UnityEngine.Random.value) * Mathf.Deg2Rad;
                var basis = Quaternion.LookRotation(forward, Vector3.up);
                direction = basis * new Vector3(
                    Mathf.Sin(angle) * Mathf.Sin(radius),
                    Mathf.Cos(angle) * Mathf.Sin(radius),
                    Mathf.Cos(radius));
            }

            _ammo--;
            ShotsFired++;
            _nextShotTime = Time.time + config.fireInterval;
            _spread = Mathf.Min(config.maxSpread, _spread + config.spreadPerShot);

            var origin = aimOrigin.position;
            if (Physics.Raycast(origin, direction, out var hit, config.aimRange, aimMask,
                    QueryTriggerInteraction.Collide))
            {
                var orb = hit.collider != null ? hit.collider.GetComponentInParent<Collectible>() : null;
                if (orb != null && !orb.IsCollected)
                {
                    orb.Collect();
                    LastOrbHitTime = Time.time;
                }
                PlayEffects(hit.point, hit.normal, true);
            }
            else
            {
                PlayEffects(origin + direction * config.aimRange, Vector3.zero, false);
            }
        }

        private void PlayEffects(Vector3 endPoint, Vector3 surfaceNormal, bool onSurface)
        {
            if (effects == null) return;
            // The muzzle comes back from the effects rather than being recalculated: it is derived from
            // the viewmodel's mesh bounds, and the relay has to see the exact point the tracer left from.
            var muzzle = effects.Play(endPoint, surfaceNormal, onSurface);
            ShotPlayed?.Invoke(muzzle, endPoint);
        }

        private void OnReloadRequested()
        {
            if (_flow == null || !_flow.IsPlaying) return;
            StartReload();
        }

        private void StartReload()
        {
            if (_reloading || _ammo >= config.magazineSize) return;
            _reloading = true;
            _reloadReadyTime = Time.time + config.reloadDuration;
            // The cone has to be gone before the next mag is fired, otherwise the extra second of
            // standing still reads as the aim refusing to settle.
            _spread = 0f;
        }

        private void OnPhaseChanged(PhaseChangedEvent args)
        {
            if (args.Current != GamePhase.Playing) return;
            _ammo = config.magazineSize;
            _reloading = false;
            _spread = 0f;
            _nextShotTime = 0f;
        }
    }
}
