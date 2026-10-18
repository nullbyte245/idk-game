using Game.Core;
using Game.Data;
using Game.Player;
using Game.Services;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// The only place UI screens are registered and handed their dependencies.
    /// Awake of every screen runs before any Start, so the first phase change already
    /// has a fully wired UI to route to.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class UiSceneBinder : MonoBehaviour
    {
        [SerializeField] private MainMenuScreen mainMenuScreen;
        [SerializeField] private HudScreen hudScreen;
        [SerializeField] private PauseScreen pauseScreen;
        [SerializeField] private GameOverScreen gameOverScreen;
        [SerializeField] private SettingsScreen settingsScreen;
        [SerializeField] private LobbyListScreen lobbyListScreen;
        [SerializeField] private CreateLobbyScreen createLobbyScreen;
        [SerializeField] private LobbyRoomScreen lobbyRoomScreen;

        [SerializeField] private GameConfig config;
        [SerializeField] private PlayerHealth playerHealth;
        [SerializeField] private FirstPersonController playerController;
        [SerializeField] private HitscanWeapon weapon;

        public string VersionLabel = "vertical slice 0.1";

        private void Awake()
        {
            if (!GameServices.IsReady)
            {
                Debug.LogError("UiSceneBinder ran before GameRoot registered services.");
                return;
            }

            var ui = GameServices.Get<UIService>();
            var flow = GameServices.Get<GameFlowService>();
            var score = GameServices.Get<ScoreService>();
            var timer = GameServices.Get<TimerService>();
            var result = GameServices.Get<RunResultService>();
            var settings = GameServices.Get<SettingsService>();
            var lobby = GameServices.Get<Game.Online.LobbyService>();
            if (config != null) lobby.SetBaseUrl(config.lobbyServerUrl);

            ui.Register(mainMenuScreen);
            ui.Register(hudScreen);
            ui.Register(pauseScreen);
            ui.Register(gameOverScreen);
            ui.Register(settingsScreen);
            ui.Register(lobbyListScreen);
            ui.Register(createLobbyScreen);
            ui.Register(lobbyRoomScreen);

            mainMenuScreen?.Bind(flow, ui, VersionLabel);
            hudScreen?.Bind(playerHealth, playerController, config, score, timer, flow, weapon);
            pauseScreen?.Bind(flow, ui);
            gameOverScreen?.Bind(flow, result);
            settingsScreen?.Bind(settings, flow);
            lobbyListScreen?.Bind(flow, ui, lobby);
            createLobbyScreen?.Bind(ui, lobby);
            lobbyRoomScreen?.Bind(flow, lobby);
        }
    }
}
