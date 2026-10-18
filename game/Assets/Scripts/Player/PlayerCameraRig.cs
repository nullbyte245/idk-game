using System;
using Game.Core;
using Game.Data;
using Game.Input;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// First person view: the camera sits at the player's eyes and only rotates with the mouse, so the
    /// yaw it exposes is the direction the character turns and walks toward. In the menu it glides to a
    /// spot in front of the character instead, which is what shows off the idle animation.
    /// Deliberately not Cinemachine - no package/version coupling, and the math stays readable.
    /// </summary>
    public sealed class PlayerCameraRig : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private GameConfig config;
        [SerializeField] private Transform cameraHolder;

        private InputReader _input;
        private GameFlowService _flow;
        private SettingsService _settings;
        private IDisposable _phaseSubscription;

        private Camera _camera;
        private float _yaw;
        private float _pitch;

        public float Yaw => _yaw;
        public float Pitch => _pitch;

        /// <summary>Horizontal look direction; movement is built from this vector.</summary>
        public Vector3 ViewForward => Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;

        /// <summary>
        /// Yaw the body should point at. While playing that is the look direction; in the menu showcase
        /// the camera stands in front, so the character has to turn around to face it.
        /// </summary>
        public float DesiredBodyYaw => IsShowcase ? config.menuCameraYaw + 180f : _yaw;

        /// <summary>The menu, the lobby browser and the boot frame show the character, not the arena.</summary>
        private bool IsShowcase => _flow == null || _flow.IsShowcase;

        private void Awake()
        {
            config = config != null ? config : GameConfig.CreateDefault();
            _yaw = config.initialYaw;
            if (cameraHolder == null) cameraHolder = transform;
            if (_camera == null) _camera = cameraHolder.GetComponent<Camera>();
        }

        private void Start()
        {
            GameServices.TryGet(out _input);
            GameServices.TryGet(out _settings);
            GameServices.TryGet(out _flow);

            if (target == null)
            {
                var player = FindFirstObjectByType<FirstPersonController>();
                if (player != null) target = player.transform;
                else Debug.LogWarning("PlayerCameraRig has no target assigned.");
            }

            if (_flow == null) return;
            _phaseSubscription = GameServices.Get<EventBus>().Subscribe<PhaseChangedEvent>(OnPhaseChanged);
            ApplyCursor(_flow.Phase);
        }

        private void OnDestroy()
        {
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
        }

        private void LateUpdate()
        {
            if (target == null || config == null) return;

            // Menu and pause freeze Time.timeScale, but the camera still has to frame the character.
            var dt = Time.unscaledDeltaTime;

            ReassertCursorLock();
            ApplyLook();

            if (IsShowcase) ApplyShowcase(dt);
            else ApplyEyes();
        }

        private void ApplyLook()
        {
            // The Player action map is disabled outside a live run, so look stays neutral in menus.
            if (_input == null || IsShowcase) return;

            var sensitivity = _settings != null ? _settings.MouseSensitivity : 1f;
            var look = _input.Look * sensitivity * 0.12f;

            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - (_settings != null && _settings.InvertY ? -look.y : look.y),
                config.minPitch, config.maxPitch);
        }

        /// <summary>
        /// The eyes sit just in front of the head bones, and FirstPersonHeadHider collapses those bones
        /// while playing, so the inside of the helmet never fills the screen.
        /// </summary>
        private void ApplyEyes()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            cameraHolder.position = target.position + Vector3.up * config.eyeHeight
                                    + rotation * Vector3.forward * config.eyeForwardOffset;
            cameraHolder.rotation = rotation;
            ApplyFieldOfView(config.cameraFieldOfView, 1f);
        }

        private void ApplyShowcase(float dt)
        {
            var pivot = target.position + Vector3.up * config.menuCameraHeight;
            var rotation = Quaternion.Euler(config.menuCameraPitch, config.menuCameraYaw, 0f);
            var desired = pivot + rotation * new Vector3(0f, 0f, -config.menuCameraDistance);

            var follow = 1f - Mathf.Exp(-config.cameraFollowSpeed * dt);
            cameraHolder.position = Vector3.Lerp(cameraHolder.position, desired, follow);
            // Aim at the character rather than along the authored angle: that keeps it dead centre.
            cameraHolder.rotation = Quaternion.LookRotation(pivot - cameraHolder.position, Vector3.up);
            ApplyFieldOfView(config.menuFieldOfView, follow);
        }

        private void ApplyFieldOfView(float targetFieldOfView, float weight)
        {
            if (_camera == null) return;
            _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFieldOfView, weight);
        }

        private void OnPhaseChanged(PhaseChangedEvent args)
        {
            ApplyCursor(args.Current);
            // Resuming a paused run keeps the facing; entering a fresh one adopts the showcase facing.
            if (args.Current != GamePhase.Playing || args.Previous == GamePhase.Paused) return;

            // Leave the showcase without spinning the body: adopt its facing as the new look yaw.
            _yaw = target != null ? target.eulerAngles.y : config.initialYaw;
            _pitch = 0f;
        }

        private void ApplyCursor(GamePhase phase)
        {
            var locked = phase == GamePhase.Playing;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        /// <summary>Windows drops the lock when the player alt-tabs; grab it back on refocus.</summary>
        private void ReassertCursorLock()
        {
            if (_flow == null || !_flow.IsPlaying) return;
            if (Cursor.lockState == CursorLockMode.Locked && !Cursor.visible) return;
            ApplyCursor(_flow.Phase);
        }

        private void OnValidate()
        {
            if (config == null) return;
            _pitch = Mathf.Clamp(_pitch, config.minPitch, config.maxPitch);
        }
    }
}
