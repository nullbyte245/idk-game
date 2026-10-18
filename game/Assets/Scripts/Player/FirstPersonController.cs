using System;
using Game.Core;
using Game.Data;
using Game.Input;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// CharacterController-based mover: locomotion follows the camera's look direction and the body
    /// turns to match it, plus coyote time, jump buffering, variable jump height, run and dash.
    /// Reads intent through InputReader only.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public sealed class FirstPersonController : MonoBehaviour
    {
        [SerializeField] private GameConfig config;
        [SerializeField] private PlayerCameraRig cameraRig;
        [SerializeField] private Transform groundedCheck;
        [SerializeField] private float groundedRadius = 0.25f;
        [SerializeField] private LayerMask groundedMask = ~0;

        private CharacterController _controller;
        private InputReader _input;
        private GameFlowService _flow;
        private IDisposable _phaseSubscription;

        private Vector3 _planarVelocity;
        private float _verticalVelocity;
        private float _lastGroundedTime = float.NegativeInfinity;
        private float _nextDashReadyTime;
        private bool _isDashing;
        private float _dashEndTime;
        private Vector3 _dashVelocity;

        public bool IsGrounded { get; private set; }
        public bool IsDashing => _isDashing;
        public Vector3 PlanarVelocity => _planarVelocity;
        public float VerticalVelocity => _verticalVelocity;
        public GameConfig Config => config;

        /// <summary>0 right after a dash, 1 when the dash is available again.</summary>
        public float DashCooldownProgress =>
            config.dashCooldown <= 0f ? 1f
            : Mathf.Clamp01((Time.time - (_nextDashReadyTime - config.dashCooldown)) / config.dashCooldown);

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            config = config != null ? config : GameConfig.CreateDefault();

            if (cameraRig == null) cameraRig = FindFirstObjectByType<PlayerCameraRig>();
            if (groundedCheck == null) groundedCheck = CreateDefaultGroundedCheck();
        }

        private void OnEnable()
        {
            if (GameServices.TryGet(out InputReader input)) _input = input;
            if (GameServices.TryGet(out GameFlowService flow)) _flow = flow;
            if (_flow == null) return;

            _phaseSubscription = GameServices.Get<EventBus>().Subscribe<PhaseChangedEvent>(OnPhaseChanged);
            ApplyPhase(_flow.Phase);
        }

        private void OnDisable()
        {
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
            _isDashing = false;
            _planarVelocity = Vector3.zero;
        }

        private void Update()
        {
            if (_input == null || _flow == null || !_flow.IsPlaying)
            {
                if (_controller != null && _controller.isGrounded) _verticalVelocity = 0f;
                // The menu frames the character from the front, so it still has to turn to face it.
                UpdateFacing(Time.unscaledDeltaTime);
                return;
            }

            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            var localMove = ReadLocalMove();
            var worldMove = ToWorldSpace(localMove);

            UpdateDash(localMove, worldMove, dt);
            UpdateGrounding();
            UpdatePlanarVelocity(worldMove, localMove.magnitude, dt);
            UpdateJumpAndGravity(localMove, dt);

            var velocity = _planarVelocity + Vector3.up * _verticalVelocity;
            if (_isDashing) velocity = _dashVelocity;

            UpdateFacing(dt);
            _controller.Move(velocity * dt);

            if (transform.position.y < config.respawnY) HandleFallenOutOfWorld();
        }

        private Vector2 ReadLocalMove()
        {
            var move = _input.Move;
            if (move.sqrMagnitude > 1f) move = move.normalized;
            return move;
        }

        /// <summary>
        /// Walk goes where the view points, on the ground plane: looking down at the floor must not
        /// shrink the step to nothing, and A/D strafe along the view's own right rather than the
        /// body's, so a strafing player keeps facing where they aim.
        /// </summary>
        private Vector3 ToWorldSpace(Vector2 localMove)
        {
            var forward = cameraRig != null ? cameraRig.ViewForward : transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = transform.forward;
            var right = Vector3.Cross(Vector3.up, forward.normalized);
            return (forward.normalized * localMove.y + right * localMove.x).normalized
                   * localMove.magnitude;
        }

        private void UpdatePlanarVelocity(Vector3 worldMove, float inputMagnitude, float dt)
        {
            var targetSpeed = (_input.IsRunning ? config.runSpeed : config.walkSpeed) * Mathf.Clamp01(inputMagnitude);
            var desired = worldMove.normalized * targetSpeed;

            var rate = 1f - Mathf.Exp(-config.acceleration * dt);
            _planarVelocity = Vector3.Lerp(_planarVelocity, desired, rate);
        }

        private void UpdateGrounding()
        {
            var probe = groundedCheck != null ? groundedCheck.position : transform.position;
            var sphereOverlaps = Physics.CheckSphere(probe, groundedRadius, groundedMask,
                QueryTriggerInteraction.Ignore);

            // controller.isGrounded alone flips off on the tiniest step-down; the sphere probe
            // keeps jumps and coyote time readable on ramps and edges.
            IsGrounded = _controller.isGrounded && (sphereOverlaps || _controller.velocity.y <= 0f);

            if (IsGrounded) _lastGroundedTime = Time.time;
        }

        private void UpdateJumpAndGravity(Vector2 localMove, float dt)
        {
            var canUseCoyote = Time.time - _lastGroundedTime <= config.coyoteTime;
            var hasBufferedJump = Time.time - _input.LastJumpRequestTime <= config.jumpBufferTime;

            if (hasBufferedJump && canUseCoyote && !_isDashing)
            {
                _verticalVelocity = Mathf.Sqrt(config.jumpHeight * -2f * config.gravity);
                IsGrounded = false;
                _lastGroundedTime = float.NegativeInfinity;
            }
            else if (!_input.IsJumpHeld && _verticalVelocity > 0f)
            {
                // Variable jump height: releasing the key cuts the rise.
                _verticalVelocity *= config.jumpCutMultiplier;
            }

            if (!IsGrounded || _verticalVelocity > 0f)
                _verticalVelocity += config.gravity * dt;
            else if (_verticalVelocity < 0f)
                _verticalVelocity = -0.5f;

            if (_verticalVelocity < config.gravity * 4f)
                _verticalVelocity = config.gravity * 4f;
        }

        private void UpdateDash(Vector2 localMove, Vector3 worldMove, float dt)
        {
            if (_isDashing)
            {
                _dashVelocity = Quaternion.RotateTowards(
                    Quaternion.LookRotation(_dashVelocity.normalized, Vector3.up),
                    Quaternion.LookRotation(worldMove == Vector3.zero ? _dashVelocity : worldMove, Vector3.up),
                    240f * dt) * _dashVelocity;

                if (Time.time >= _dashEndTime)
                {
                    _isDashing = false;
                    _planarVelocity = _dashVelocity.normalized * config.walkSpeed;
                    _verticalVelocity = 0f;
                }
                return;
            }

            if (Time.time - _input.LastDashRequestTime > config.dashDuration + 0.001f) return;
            if (Time.time < _nextDashReadyTime) return;

            var direction = worldMove.sqrMagnitude > 0.0001f
                ? worldMove.normalized
                : (cameraRig != null ? cameraRig.ViewForward : transform.forward);

            _dashVelocity = direction * config.dashSpeed;
            _isDashing = true;
            _dashEndTime = Time.time + config.dashDuration;
            _nextDashReadyTime = Time.time + config.dashCooldown;
        }

        /// <summary>
        /// The body follows the camera yaw: in third person the character has to point where it
        /// walks, and in the menu showcase it has to face the camera.
        /// </summary>
        private void UpdateFacing(float dt)
        {
            if (cameraRig == null || dt <= 0f) return;

            var desired = Quaternion.Euler(0f, cameraRig.DesiredBodyYaw, 0f);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, config.bodyTurnSpeed * dt);
        }

        private void OnPhaseChanged(PhaseChangedEvent args) => ApplyPhase(args.Current);

        private void ApplyPhase(GamePhase phase)
        {
            if (phase != GamePhase.Playing)
            {
                _isDashing = false;
                _planarVelocity = Vector3.zero;
            }
        }

        private void HandleFallenOutOfWorld()
        {
            var health = GetComponent<PlayerHealth>();
            if (health != null) health.Kill();
        }

        private Transform CreateDefaultGroundedCheck()
        {
            var probe = new GameObject("GroundedCheck");
            probe.transform.SetParent(transform, false);
            var cc = _controller;
            probe.transform.localPosition = new Vector3(0f, cc.center.y - cc.height * 0.5f - 0.02f, 0f);
            return probe.transform;
        }
    }
}
