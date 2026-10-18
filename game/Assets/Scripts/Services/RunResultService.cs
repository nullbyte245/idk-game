using Game.Core;

namespace Game.Services
{
    /// <summary>
    /// Holds the last run's numbers so a screen opened after the fact can still read them.
    /// Events alone would be missed when the screen was inactive at publish time.
    /// </summary>
    public sealed class RunResultService : IService
    {
        public float FinalScore { get; private set; }
        public float FinalSeconds { get; private set; }
        public bool Victory { get; private set; }
        public bool HasResult { get; private set; }

        public void OnRegister(IServiceRegistry registry) { }
        public void OnDispose() => HasResult = false;

        public void Record(float score, float seconds, bool victory)
        {
            FinalScore = score;
            FinalSeconds = seconds;
            Victory = victory;
            HasResult = true;
        }

        public string FormattedTime
        {
            get
            {
                var total = (int)FinalSeconds;
                return $"{total / 60:00}:{total % 60:00}";
            }
        }
    }
}
