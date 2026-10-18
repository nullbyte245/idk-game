namespace Game.Core
{
    public enum GamePhase
    {
        Boot,
        Menu,
        Lobbies,
        Room,
        Playing,
        Paused,
        GameOver
    }

    public readonly struct PhaseChangedEvent : IGameEvent
    {
        public readonly GamePhase Previous;
        public readonly GamePhase Current;
        public PhaseChangedEvent(GamePhase previous, GamePhase current)
        {
            Previous = previous;
            Current = current;
        }
    }

    public readonly struct RunStartedEvent : IGameEvent
    {
        public readonly int CollectibleTarget;
        public RunStartedEvent(int collectibleTarget) { CollectibleTarget = collectibleTarget; }
    }

    public readonly struct RunFinishedEvent : IGameEvent
    {
        public readonly float FinalScore;
        public readonly float ElapsedSeconds;
        public readonly bool Victory;
        public RunFinishedEvent(float finalScore, float elapsedSeconds, bool victory)
        {
            FinalScore = finalScore;
            ElapsedSeconds = elapsedSeconds;
            Victory = victory;
        }
    }

    public readonly struct HealthChangedEvent : IGameEvent
    {
        public readonly float Current;
        public readonly float Max;
        public readonly float Delta;
        public HealthChangedEvent(float current, float max, float delta)
        {
            Current = current;
            Max = max;
            Delta = delta;
        }
    }

    public readonly struct PlayerDiedEvent : IGameEvent { }

    public readonly struct ScoreChangedEvent : IGameEvent
    {
        public readonly float Score;
        public readonly int CollectiblesPicked;
        public readonly int CollectibleTotal;
        public ScoreChangedEvent(float score, int picked, int total)
        {
            Score = score;
            CollectiblesPicked = picked;
            CollectibleTotal = total;
        }
    }

    public readonly struct ScreenRequestedEvent : IGameEvent
    {
        public readonly string ScreenId;
        public readonly bool CloseOthers;
        public ScreenRequestedEvent(string screenId, bool closeOthers = true)
        {
            ScreenId = screenId;
            CloseOthers = closeOthers;
        }
    }

    public readonly struct SettingsChangedEvent : IGameEvent { }
}
