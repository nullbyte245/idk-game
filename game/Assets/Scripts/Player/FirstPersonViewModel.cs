using Game.Core;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Draws the first person hands and rifle with a second camera that only sees the ViewModel
    /// layer. The main camera renders the world first, this one clears depth only and paints on top,
    /// so a viewmodel never clips into a wall no matter how close the player stands to one.
    ///
    /// It also animates them. The CS:S hands arrive without a single clip, so the only motion
    /// available is procedural: the weapon lags behind the look rotation, breathes while standing
    /// still and bobs while walking.
    ///
    /// It lives on the camera rig rather than on the viewmodel itself because it has to switch that
    /// object off: a component on a deactivated GameObject stops running.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FirstPersonViewModel : MonoBehaviour
    {
        [SerializeField] private GameObject viewModelRoot;
        [SerializeField] private Camera viewModelCamera;
        [SerializeField] private Camera mainCamera;

        [Header("Look sway")]
        [SerializeField] private float rotationLag = 0.038f;
        [SerializeField] private float positionLag = 0.00025f;
        [SerializeField] private float rollLag = 0.018f;
        [SerializeField] private float maxSwayAngle = 7f;
        [SerializeField] private Vector3 maxSwayPosition = new Vector3(0.030f, 0.028f, 0.020f);
        // Damping 14.5 against stiffness 85 is a damping ratio of ~0.79: the rifle settles in one
        // pass with a hint of overshoot instead of wobbling for half a second.
        [SerializeField] private float springStiffness = 85f;
        [SerializeField] private float springDamping = 14.5f;

        [Header("Idle breathing")]
        [SerializeField] private float breatheAmplitude = 0.004f;
        [SerializeField] private float breatheSpeed = 1.15f;
        [SerializeField] private float breatheAngle = 0.25f;

        [Header("Walking")]
        [SerializeField] private float bobAmplitude = 0.012f;
        [SerializeField] private float bobFrequency = 2.1f;
        [SerializeField] private float bobRunScale = 1.35f;
        [SerializeField] private float bobStiffness = 12f;

        [Header("Recoil")]
        // The rifle is kicked by an impulse and springs back on its own, so automatic fire reads as a
        // hammer blowing instead of a shiver.
        [SerializeField] private HitscanWeapon weapon;
        [SerializeField] private float recoilImpulse = 20f;
        [SerializeField] private float recoilStiffness = 260f;
        [SerializeField] private float recoilDamping = 22f;
        [SerializeField] private float recoilBack = 0.055f;
        [SerializeField] private float recoilUp = 0.022f;
        [SerializeField] private float recoilPitch = 4.6f;
        [SerializeField] private float recoilRoll = 1.8f;

        private GameFlowService _flow;
        private FirstPersonController _controller;
        private Transform _root;

        private Vector3 _basePosition;
        private Quaternion _baseRotation;
        private float _previousYaw;
        private float _previousPitch;
        private float _bobPhase;

        private Vector3 _swayPosition;
        private Vector3 _swayTargetPosition;
        private Vector3 _swayPositionVelocity;
        private Vector3 _swayAngles;
        private Vector3 _swayTargetAngles;
        private Vector3 _swayAngleVelocity;
        private Vector3 _bobOffset;

        private int _lastShots;
        private float _recoil;
        private float _recoilVelocity;
        private float _recoilSide;

        private void Start()
        {
            if (mainCamera == null) mainCamera = Camera.main;
            if (GameServices.TryGet(out GameFlowService flow)) _flow = flow;
            if (_controller == null) _controller = FindFirstObjectByType<FirstPersonController>();
            if (viewModelRoot == null) return;

            _root = viewModelRoot.transform;
            _basePosition = _root.localPosition;
            _baseRotation = _root.localRotation;
            ResetLook();
        }

        private void LateUpdate()
        {
            if (viewModelRoot == null) return;

            // Same phase rule as FirstPersonHeadHider: the menu shows the whole body, a run does not.
            var show = _flow != null && !_flow.IsShowcase;
            if (viewModelRoot.activeSelf != show) viewModelRoot.SetActive(show);
            // The camera is a sibling of the viewmodel now, so it has to be switched off with it.
            if (viewModelCamera != null && viewModelCamera.enabled != show) viewModelCamera.enabled = show;
            if (!show || viewModelCamera == null || mainCamera == null)
            {
                ResetLook();
                return;
            }

            // The two cameras share a viewpoint, so a perspective mismatch would put the sights
            // somewhere other than where the world camera says they point.
            viewModelCamera.fieldOfView = mainCamera.fieldOfView;

            // A tab-out or a menu hand-over produces one enormous look delta; cap it like the rig's
            // own smoothing does, otherwise the rifle snaps across the screen on the first frame.
            var dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;

            UpdateSway(dt);
            UpdateBob(dt);
            UpdateRecoil();
            ApplyPose(dt);
        }

        /// <summary>
        /// The rifle trails the camera by an amount proportional to how fast the player is turning,
        /// so a flick swings it wide and a slow track barely moves it. The rate comes from the eye
        /// camera's own orientation rather than the rig's yaw/pitch fields: that way a wiring slip in
        /// the generated scene cannot silently freeze the viewmodel.
        /// </summary>
        private void UpdateSway(float dt)
        {
            ReadLook(out var yaw, out var pitch);
            var yawRate = Mathf.DeltaAngle(_previousYaw, yaw) / dt;
            var pitchRate = (pitch - _previousPitch) / dt;
            _previousYaw = yaw;
            _previousPitch = pitch;

            _swayTargetAngles = new Vector3(
                Mathf.Clamp(-pitchRate * rotationLag, -maxSwayAngle, maxSwayAngle),
                Mathf.Clamp(-yawRate * rotationLag, -maxSwayAngle, maxSwayAngle),
                Mathf.Clamp(yawRate * rollLag, -maxSwayAngle, maxSwayAngle));
            _swayTargetPosition = new Vector3(
                Mathf.Clamp(-yawRate * positionLag, -maxSwayPosition.x, maxSwayPosition.x),
                Mathf.Clamp(-pitchRate * positionLag, -maxSwayPosition.y, maxSwayPosition.y),
                Mathf.Clamp(-yawRate * positionLag * 0.35f, -maxSwayPosition.z, maxSwayPosition.z));
        }

        private void UpdateBob(float dt)
        {
            if (_controller == null) return;
            var speed = _controller.PlanarVelocity.magnitude;
            var normalized = _controller.Config != null
                ? Mathf.Clamp01(speed / Mathf.Max(0.01f, _controller.Config.runSpeed))
                : 0f;
            _bobPhase += dt * bobFrequency * Mathf.Lerp(0.35f, bobRunScale, normalized) *
                        (1f + normalized);
        }

        /// <summary>
        /// A shot is a count change on the weapon, not an animation: the CS:S hands have no clips, so
        /// recoil is an impulse on the same spring the sway already runs on. Which side the rifle
        /// kicks to is picked per shot, otherwise automatic fire leans one way and reads as a bug.
        /// </summary>
        private void UpdateRecoil()
        {
            if (weapon == null) return;
            var shots = weapon.ShotsFired;
            if (shots <= _lastShots) return;

            _lastShots = shots;
            _recoilVelocity += recoilImpulse;
            _recoilSide = Random.Range(-1f, 1f);
        }

        private void ApplyPose(float dt)
        {
            // A spring rather than a lerp: underdamped, so the rifle swings past its resting spot
            // and settles after a flick instead of sliding back on a rail.
            StepSpring(ref _swayAngles, ref _swayAngleVelocity, _swayTargetAngles, dt);
            StepSpring(ref _swayPosition, ref _swayPositionVelocity, _swayTargetPosition, dt);

            var recoilStep = Mathf.Min(dt, 0.033f);
            _recoilVelocity += (-_recoil * recoilStiffness - _recoilVelocity * recoilDamping) * recoilStep;
            _recoil += _recoilVelocity * recoilStep;

            var breathe = Mathf.Sin(Time.time * breatheSpeed);
            var speed = _controller != null ? _controller.PlanarVelocity.magnitude : 0f;
            var amplitude = bobAmplitude * Mathf.Clamp01(speed / 3f);
            var bob = new Vector3(
                Mathf.Cos(_bobPhase * 2f) * amplitude * 0.6f,
                Mathf.Sin(_bobPhase) * amplitude,
                0f);
            _bobOffset = Vector3.Lerp(_bobOffset, bob, 1f - Mathf.Exp(-bobStiffness * dt));

            var kick = new Vector3(_recoilSide * recoilBack * 0.35f, recoilUp, -recoilBack) * _recoil;

            _root.localPosition = _basePosition + _swayPosition + _bobOffset +
                                  Vector3.up * (breathe * breatheAmplitude) + kick;
            _root.localRotation = _baseRotation * Quaternion.Euler(
                _swayAngles.x + breathe * breatheAngle * 0.5f + _recoil * recoilPitch,
                _swayAngles.y + breathe * -breatheAngle,
                _swayAngles.z + breathe * -breatheAngle * 0.35f + _recoilSide * _recoil * recoilRoll);
        }

        /// <summary>Semi-implicit Euler; stable while stiffness * dt^2 stays far below 4.</summary>
        private void StepSpring(ref Vector3 value, ref Vector3 velocity, Vector3 target, float dt)
        {
            var step = Mathf.Min(dt, 0.033f);
            var acceleration = (target - value) * springStiffness - velocity * springDamping;
            velocity += acceleration * step;
            value += velocity * step;
        }

        private void ReadLook(out float yaw, out float pitch)
        {
            var forward = mainCamera != null ? mainCamera.transform.forward : Vector3.forward;
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private void ResetLook()
        {
            ReadLook(out _previousYaw, out _previousPitch);
        }
    }
}
