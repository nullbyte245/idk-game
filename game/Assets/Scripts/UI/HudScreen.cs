using System;
using Game.Core;
using Game.Data;
using Game.Player;
using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class HudScreen : ScreenBase
    {
        public override string Id => UIScreenIds.Hud;
        public override ScreenKind Kind => ScreenKind.Overlay;

        [SerializeField] private Image portrait;
        [SerializeField] private RectTransform healthFill;
        [SerializeField] private RectTransform dashFill;
        [SerializeField] private Text scoreText;
        [SerializeField] private Text collectibleText;
        [SerializeField] private Text timerText;
        [SerializeField] private Text hintText;
        [SerializeField] private Button pauseButton;

        [Header("Crosshair")]
        [SerializeField] private RectTransform crosshairUp;
        [SerializeField] private RectTransform crosshairDown;
        [SerializeField] private RectTransform crosshairLeft;
        [SerializeField] private RectTransform crosshairRight;
        [SerializeField] private CanvasGroup hitMarker;
        [SerializeField] private Text ammoText;

        [SerializeField] private HitscanWeapon weapon;

        private const float CrosshairIdleGap = 5f;
        private const float CrosshairPerPixel = 9f;
        private const float HitMarkerSeconds = 0.16f;

        private static readonly Color AmmoColor = new Color(0.92f, 0.95f, 1f);
        private static readonly Color ReloadingColor = new Color(0.95f, 0.78f, 0.25f);

        private PlayerHealth _health;
        private FirstPersonController _controller;
        private GameConfig _config;
        private ScoreService _score;
        private TimerService _timer;
        private GameFlowService _flow;
        private IDisposable _scoreSubscription;

        public void Bind(PlayerHealth health, FirstPersonController controller, GameConfig config,
                         ScoreService score, TimerService timer, GameFlowService flow,
                         HitscanWeapon hitscanWeapon)
        {
            _health = health;
            _controller = controller;
            _config = config;
            _score = score;
            _timer = timer;
            _flow = flow;
            weapon = hitscanWeapon;

            if (_health != null) _health.Changed += RefreshHealth;
            if (pauseButton != null) pauseButton.onClick.AddListener(OnPauseClicked);

            _scoreSubscription = GameServices.Get<EventBus>().Subscribe<ScoreChangedEvent>(OnScoreChanged);
            RefreshHealth();
            OnScoreChanged(new ScoreChangedEvent(0f, 0, _config != null ? _config.collectibleCount : 0));
            UpdateCrosshair();
        }

        private void OnDisable()
        {
            if (_health != null) _health.Changed -= RefreshHealth;
            _scoreSubscription?.Dispose();
            _scoreSubscription = null;
        }

        private void Update()
        {
            if (timerText != null && _timer != null) timerText.text = _timer.Format();
            if (dashFill != null && _controller != null)
            {
                dashFill.anchorMax = new Vector2(
                    _controller.IsDashing ? 0f : _controller.DashCooldownProgress, dashFill.anchorMax.y);
            }

            UpdateCrosshair();
            UpdateHitMarker();
            UpdateAmmo();
        }

        /// <summary>
        /// The gap between the ticks is the weapon's current spread, so the space between them is the
        /// circle a round can land in. Hiding that number would make the recoil feel random.
        /// </summary>
        private void UpdateCrosshair()
        {
            if (crosshairUp == null || crosshairDown == null ||
                crosshairLeft == null || crosshairRight == null) return;

            var gap = CrosshairIdleGap + (weapon != null ? weapon.SpreadDegrees : 0f) * CrosshairPerPixel;

            var half = crosshairUp.sizeDelta.y * 0.5f;
            crosshairUp.anchoredPosition = new Vector2(0f, gap + half);
            crosshairDown.anchoredPosition = new Vector2(0f, -(gap + half));

            half = crosshairRight.sizeDelta.x * 0.5f;
            crosshairRight.anchoredPosition = new Vector2(gap + half, 0f);
            crosshairLeft.anchoredPosition = new Vector2(-(gap + half), 0f);
        }

        private void UpdateHitMarker()
        {
            if (hitMarker == null) return;

            var age = weapon != null ? Time.time - weapon.LastOrbHitTime : float.PositiveInfinity;
            var show = age >= 0f && age < HitMarkerSeconds;
            if (hitMarker.gameObject.activeSelf != show) hitMarker.gameObject.SetActive(show);
            if (show) hitMarker.alpha = 1f - age / HitMarkerSeconds;
        }

        private void UpdateAmmo()
        {
            if (ammoText == null || weapon == null) return;

            if (weapon.IsReloading)
            {
                ammoText.text = "RELOADING";
                ammoText.color = ReloadingColor;
                return;
            }

            ammoText.text = $"{weapon.AmmoInMagazine} / {weapon.MagazineSize}";
            ammoText.color = AmmoColor;
        }

        private void RefreshHealth()
        {
            if (healthFill == null || _health == null) return;
            healthFill.anchorMax = new Vector2(_health.Ratio, healthFill.anchorMax.y);
            if (portrait != null) portrait.color = Color.Lerp(Color.red, Color.white, _health.Ratio);
        }

        private void OnScoreChanged(ScoreChangedEvent args)
        {
            if (scoreText != null) scoreText.text = $"Score  {args.Score:0}";
            if (collectibleText != null)
                collectibleText.text = $"Orbs  {args.CollectiblesPicked}/{args.CollectibleTotal}";
        }

        private void OnPauseClicked() => _flow?.RequestPause();
    }
}
