using System;
using Game.Core;
using Game.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class SettingsScreen : ScreenBase
    {
        public override string Id => UIScreenIds.Settings;
        public override ScreenKind Kind => ScreenKind.Overlay;

        [SerializeField] private Slider volumeSlider;
        [SerializeField] private Slider sensitivitySlider;
        [SerializeField] private Toggle invertYToggle;
        [SerializeField] private Toggle fullscreenToggle;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button resetButton;
        [SerializeField] private Text volumeValue;
        [SerializeField] private Text sensitivityValue;

        private SettingsService _settings;
        private GameFlowService _flow;
        private IDisposable _settingsSubscription;
        private bool _syncing;

        public void Bind(SettingsService settings, GameFlowService flow)
        {
            _settings = settings;
            _flow = flow;

            if (volumeSlider != null)
                volumeSlider.onValueChanged.AddListener(v => { if (_syncing) return; _settings.SetVolume(v); RefreshLabels(); });
            if (sensitivitySlider != null)
                sensitivitySlider.onValueChanged.AddListener(v => { if (_syncing) return; _settings.SetSensitivity(v); RefreshLabels(); });
            if (invertYToggle != null)
                invertYToggle.onValueChanged.AddListener(v => { if (!_syncing) _settings.SetInvertY(v); });
            if (fullscreenToggle != null)
                fullscreenToggle.onValueChanged.AddListener(v => { if (!_syncing) _settings.SetFullscreen(v); });
            if (resetButton != null)
                resetButton.onClick.AddListener(() => { _settings.ResetToDefaults(); SyncFromService(); });
            if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        }

        protected override void OnOpen()
        {
            base.OnOpen();
            SyncFromService();

            if (_settingsSubscription == null)
                _settingsSubscription = GameServices.Get<EventBus>()
                    .Subscribe<SettingsChangedEvent>(_ => SyncFromService());
        }

        private void OnDestroy()
        {
            _settingsSubscription?.Dispose();
            _settingsSubscription = null;
        }

        private void OnCloseClicked()
        {
            if (GameServices.TryGet(out UIService ui)) ui.Close(UIScreenIds.Settings);
        }

        private void SyncFromService()
        {
            if (_settings == null) return;
            _syncing = true;

            if (volumeSlider != null) volumeSlider.value = _settings.MasterVolume;
            if (sensitivitySlider != null) sensitivitySlider.value = _settings.MouseSensitivity;
            if (invertYToggle != null) invertYToggle.isOn = _settings.InvertY;
            if (fullscreenToggle != null) fullscreenToggle.isOn = _settings.Fullscreen;

            _syncing = false;
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (_settings == null) return;
            if (volumeValue != null) volumeValue.text = $"Volume  {_settings.MasterVolume * 100f:0}%";
            if (sensitivityValue != null) sensitivityValue.text = $"Look  {_settings.MouseSensitivity:0.00}";
        }
    }
}
