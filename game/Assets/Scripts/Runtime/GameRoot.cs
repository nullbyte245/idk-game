using Game.Core;
using Game.Input;
using Game.Services;
using Game.UI;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// Creates the service graph once, before any scene Awake runs, and keeps it alive across
    /// scene loads. Scene objects only look services up, they never own them.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameRoot : MonoBehaviour
    {
        private static GameRoot _instance;

        public ServiceRegistry Registry { get; private set; }
        public EventBus Events { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            var go = new GameObject("[GameRoot]");
            _instance = go.AddComponent<GameRoot>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            Registry = new ServiceRegistry();
            GameServices.Attach(Registry);

            Events = Registry.Register(new EventBus());
            Registry.Register(new SettingsService());
            Registry.Register(new Game.Online.LobbyService());
            Registry.Register(new GameFlowService());
            // After the flow: the arena only speaks while a run is not a showcase, and it needs both
            // the room and the phase to know that.
            Registry.Register(new Game.Online.ArenaService());
            Registry.Register(new ScoreService());
            Registry.Register(new TimerService());
            Registry.Register(new RunResultService());
            Registry.Register(new UIService());

            var input = Registry.Register(new InputReader());
            input.Validate();

            gameObject.AddComponent<TickDriver>().Bind(Registry);
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;

            if (Registry != null)
            {
                GameServices.Detach(Registry);
                Registry.Dispose();
                Registry = null;
            }
        }
    }
}
