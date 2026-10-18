using Game.Core;
using UnityEngine;

namespace Game.Services
{
    /// <summary>Player-prefs-backed options. Applied once on register and on every change.</summary>
    public sealed class SettingsService : IService
    {
        private const string KeyVolume = "settings.master_volume";
        private const string KeySensitivity = "settings.mouse_sensitivity";
        private const string KeyInvertY = "settings.invert_y";
        private const string KeyFullscreen = "settings.fullscreen";

        public const float MinVolume = 0f;
        public const float MaxVolume = 1f;
        public const float MinSensitivity = 0.2f;
        public const float MaxSensitivity = 4f;

        private EventBus _events;
        private bool _applying;

        public float MasterVolume { get; private set; } = 0.8f;
        public float MouseSensitivity { get; private set; } = 1f;
        public bool InvertY { get; private set; }
        public bool Fullscreen { get; private set; } = true;

        public void OnRegister(IServiceRegistry registry)
        {
            _events = registry.Get<EventBus>();
            Load();
            Apply();
        }

        public void OnDispose() { }

        public void SetVolume(float value)
        {
            MasterVolume = Mathf.Clamp(value, MinVolume, MaxVolume);
            SaveAndNotify();
        }

        public void SetSensitivity(float value)
        {
            MouseSensitivity = Mathf.Clamp(value, MinSensitivity, MaxSensitivity);
            SaveAndNotify();
        }

        public void SetInvertY(bool value)
        {
            InvertY = value;
            SaveAndNotify();
        }

        public void SetFullscreen(bool value)
        {
            Fullscreen = value;
            SaveAndNotify();
        }

        public void ResetToDefaults()
        {
            MasterVolume = 0.8f;
            MouseSensitivity = 1f;
            InvertY = false;
            Fullscreen = true;
            SaveAndNotify();
        }

        private void Load()
        {
            MasterVolume = PlayerPrefs.GetFloat(KeyVolume, MasterVolume);
            MouseSensitivity = PlayerPrefs.GetFloat(KeySensitivity, MouseSensitivity);
            InvertY = PlayerPrefs.GetInt(KeyInvertY, 0) == 1;
            Fullscreen = PlayerPrefs.GetInt(KeyFullscreen, 1) == 1;
        }

        private void SaveAndNotify()
        {
            PlayerPrefs.SetFloat(KeyVolume, MasterVolume);
            PlayerPrefs.SetFloat(KeySensitivity, MouseSensitivity);
            PlayerPrefs.SetInt(KeyInvertY, InvertY ? 1 : 0);
            PlayerPrefs.SetInt(KeyFullscreen, Fullscreen ? 1 : 0);
            PlayerPrefs.Save();

            Apply();
            _events?.Publish(new SettingsChangedEvent());
        }

        private void Apply()
        {
            if (_applying) return;
            _applying = true;
            try
            {
                AudioListener.volume = MasterVolume;
                AudioListener.pause = false;
                if (Screen.fullScreen != Fullscreen)
                    Screen.fullScreen = Fullscreen;
            }
            finally
            {
                _applying = false;
            }
        }
    }
}
