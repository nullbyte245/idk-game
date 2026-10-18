using System;
using Game.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Input
{
    /// <summary>
    /// Owns the InputActionAsset and exposes gameplay-meaningful values. Consumers never see
    /// InputSystem types, so input can be re-authored as a .inputactions asset without touching
    /// gameplay code.
    ///
    /// Two maps: "Player" is disabled outside a live run, "System" stays enabled so Escape can
    /// always open/close the pause screen.
    /// </summary>
    public sealed class InputReader : IService, ITickable, IDisposable
    {
        private InputActionAsset _asset;
        private InputActionMap _playerMap;
        private InputActionMap _systemMap;

        private InputAction _move;
        private InputAction _look;
        private InputAction _jump;
        private InputAction _run;
        private InputAction _dash;
        private InputAction _interact;
        private InputAction _fire;
        private InputAction _reload;
        private InputAction _pause;

        private Action<InputAction.CallbackContext> _onJump;
        private Action<InputAction.CallbackContext> _onDash;
        private Action<InputAction.CallbackContext> _onInteract;
        private Action<InputAction.CallbackContext> _onReload;
        private Action<InputAction.CallbackContext> _onPause;

        public Vector2 Move { get; private set; }
        public Vector2 Look { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>Left button held: the rifle is automatic, so fire rate is the weapon's business.</summary>
        public bool IsFiring { get; private set; }

        /// <summary>Needed for variable jump height (cut the rise when the key is released).</summary>
        public bool IsJumpHeld { get; private set; }

        /// <summary>Time.time stamps consumed by coyote-time and jump-buffer logic.</summary>
        public float LastJumpRequestTime { get; private set; } = float.NegativeInfinity;
        public float LastDashRequestTime { get; private set; } = float.NegativeInfinity;

        public event Action PauseToggled;
        public event Action InteractPerformed;
        public event Action ReloadPerformed;

        public bool IsGameplayEnabled => _playerMap != null && _playerMap.enabled;

        public void OnRegister(IServiceRegistry registry)
        {
            BuildAsset();
            SubscribeCallbacks();
            _systemMap.Enable();
        }

        public void OnDispose() => Dispose();

        public void OnTick(float deltaTime)
        {
            if (!IsGameplayEnabled)
            {
                Move = Vector2.zero;
                Look = Vector2.zero;
                IsRunning = false;
                IsJumpHeld = false;
                IsFiring = false;
                return;
            }

            var move = _move.ReadValue<Vector2>();
            Move = move.sqrMagnitude > 1f ? move.normalized : move;
            Look = _look.ReadValue<Vector2>();
            IsRunning = _run.IsPressed() && Move.sqrMagnitude > 0.01f;
            IsJumpHeld = _jump.IsPressed();
            IsFiring = _fire.IsPressed();
        }

        public void SetGameplayEnabled(bool enabled)
        {
            if (_playerMap == null || _playerMap.enabled == enabled) return;

            if (enabled) _playerMap.Enable();
            else _playerMap.Disable();

            ResetTransientState();
        }

        /// <summary>Fails loudly in logs instead of silently producing a dead controller.</summary>
        public void Validate()
        {
            if (_asset == null) { Debug.LogError("InputReader asset missing."); return; }
            if (_move == null || _look == null || _jump == null || _run == null ||
                _dash == null || _interact == null || _fire == null || _reload == null ||
                _pause == null)
                Debug.LogError("InputReader: an action failed to build.");

            var moveBindings = _move.bindings.Count;
            Debug.Log($"InputReader OK: Player actions={_playerMap.actions.Count} bindings(Move)={moveBindings}, " +
                      $"System actions={_systemMap.actions.Count}");
        }

        private void ResetTransientState()
        {
            Move = Vector2.zero;
            Look = Vector2.zero;
            IsRunning = false;
            IsFiring = false;
            LastJumpRequestTime = float.NegativeInfinity;
            LastDashRequestTime = float.NegativeInfinity;
        }

        private void BuildAsset()
        {
            _asset = ScriptableObject.CreateInstance<InputActionAsset>();
            _asset.name = "GameInput";

            _playerMap = _asset.AddActionMap("Player");
            _systemMap = _asset.AddActionMap("System");

            _move = AddAction(_playerMap, "Move", InputActionType.Value, "Vector2");
            AddWasdComposite(_move, "<Keyboard>/w", "<Keyboard>/s", "<Keyboard>/a", "<Keyboard>/d");
            AddWasdComposite(_move, "<Keyboard>/upArrow", "<Keyboard>/downArrow",
                "<Keyboard>/leftArrow", "<Keyboard>/rightArrow");
            _move.AddBinding("<Gamepad>/leftStick");

            _look = AddAction(_playerMap, "Look", InputActionType.Value, "Vector2");
            _look.AddBinding("<Pointer>/delta");
            _look.AddBinding("<Gamepad>/rightStick");

            _jump = AddAction(_playerMap, "Jump", InputActionType.Button, "Button", "<Keyboard>/space");
            _jump.AddBinding("<Gamepad>/buttonSouth");

            _run = AddAction(_playerMap, "Run", InputActionType.PassThrough, "Button", "<Keyboard>/leftShift");
            _run.AddBinding("<Gamepad>/leftStickPress");

            _dash = AddAction(_playerMap, "Dash", InputActionType.Button, "Button", "<Keyboard>/leftCtrl");
            _dash.AddBinding("<Gamepad>/buttonWest");

            _interact = AddAction(_playerMap, "Interact", InputActionType.Button, "Button", "<Keyboard>/f");
            _interact.AddBinding("<Gamepad>/buttonEast");

            _fire = AddAction(_playerMap, "Fire", InputActionType.PassThrough, "Button",
                "<Mouse>/leftButton");
            _fire.AddBinding("<Gamepad>/rightTrigger");

            _reload = AddAction(_playerMap, "Reload", InputActionType.Button, "Button", "<Keyboard>/r");
            _reload.AddBinding("<Gamepad>/buttonNorth");

            _pause = AddAction(_systemMap, "Pause", InputActionType.Button, "Button", "<Keyboard>/escape");
            _pause.AddBinding("<Gamepad>/start");
        }

        /// <summary>AddAction() returns void in this package version, so the action is fetched back by name.</summary>
        private static InputAction AddAction(InputActionMap map, string name, InputActionType type,
            string expectedControlLayout, string binding = null)
        {
            map.AddAction(name, type, binding, expectedControlLayout: expectedControlLayout);
            var action = map.FindAction(name);
            if (action == null)
                throw new InvalidOperationException($"Input action '{map.name}/{name}' was not created.");
            return action;
        }

        private static void AddWasdComposite(InputAction action, string up, string down, string left, string right)
        {
            action.AddCompositeBinding("2DVector")
                .With("Up", up)
                .With("Down", down)
                .With("Left", left)
                .With("Right", right);
        }

        private void SubscribeCallbacks()
        {
            _onJump = _ => LastJumpRequestTime = Time.time;
            _onDash = _ => LastDashRequestTime = Time.time;
            _onInteract = _ => InteractPerformed?.Invoke();
            _onReload = _ => ReloadPerformed?.Invoke();
            _onPause = _ => PauseToggled?.Invoke();

            _jump.performed += _onJump;
            _dash.performed += _onDash;
            _interact.performed += _onInteract;
            _reload.performed += _onReload;
            _pause.performed += _onPause;
        }

        public void Dispose()
        {
            if (_jump != null && _onJump != null) _jump.performed -= _onJump;
            if (_dash != null && _onDash != null) _dash.performed -= _onDash;
            if (_interact != null && _onInteract != null) _interact.performed -= _onInteract;
            if (_reload != null && _onReload != null) _reload.performed -= _onReload;
            if (_pause != null && _onPause != null) _pause.performed -= _onPause;

            if (_asset != null) _asset.Disable();

            _asset = null;
            _playerMap = null;
            _systemMap = null;
        }
    }
}
