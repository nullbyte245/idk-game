using System;
using Game.Core;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Feeds the character Animator from movement state only, so the animation layer never has to
    /// know how locomotion is calculated. Root motion stays off: the CharacterController owns the transform.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class PlayerAnimatorDriver : MonoBehaviour
    {
        [SerializeField] private FirstPersonController movement;
        [SerializeField] private PlayerHealth health;

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int VerticalId = Animator.StringToHash("Vertical");
        private static readonly int GroundedId = Animator.StringToHash("Grounded");
        private static readonly int DashId = Animator.StringToHash("Dash");
        private static readonly int DeathId = Animator.StringToHash("Death");

        private Animator _animator;
        private IDisposable _phaseSubscription;
        private GameFlowService _flow;
        private bool _dashSent;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // The menu holds Time.timeScale at 0 while the showcase idle still has to breathe.
            _animator.updateMode = AnimatorUpdateMode.UnscaledTime;

            if (movement == null) movement = GetComponentInParent<FirstPersonController>();
            if (health == null) health = GetComponentInParent<PlayerHealth>();
        }

        private void OnEnable()
        {
            if (health != null) health.Died += OnDied;
            if (GameServices.TryGet(out GameFlowService flow)) _flow = flow;
            if (_flow != null)
            {
                _phaseSubscription = GameServices.Get<EventBus>().Subscribe<PhaseChangedEvent>(OnPhaseChanged);
                ResetRunState(_flow.Phase);
            }
        }

        private void OnDisable()
        {
            if (health != null) health.Died -= OnDied;
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
        }

        private void Update()
        {
            if (_animator == null || movement == null) return;

            var planar = _flow != null && _flow.IsPlaying ? movement.PlanarVelocity : Vector3.zero;
            var runSpeed = Mathf.Max(0.01f, movement.Config.runSpeed);

            // Walk sits exactly on 0.5 and run on 1.0, which are the blend tree thresholds.
            _animator.SetFloat(SpeedId, planar.magnitude / runSpeed);
            _animator.SetFloat(VerticalId, movement.VerticalVelocity);
            _animator.SetBool(GroundedId, movement.IsGrounded);

            if (movement.IsDashing && !_dashSent)
            {
                _animator.SetTrigger(DashId);
                _dashSent = true;
            }
            else if (!movement.IsDashing) _dashSent = false;
        }

        private void OnDied()
        {
            if (_animator != null) _animator.SetTrigger(DeathId);
        }

        private void OnPhaseChanged(PhaseChangedEvent args) => ResetRunState(args.Current);

        private void ResetRunState(GamePhase phase)
        {
            if (phase != GamePhase.Playing && phase != GamePhase.Menu) return;

            _animator.ResetTrigger(DeathId);
            _animator.ResetTrigger(DashId);
            _animator.SetFloat(SpeedId, 0f);
            _animator.SetFloat(VerticalId, 0f);
            _dashSent = false;
        }
    }
}
