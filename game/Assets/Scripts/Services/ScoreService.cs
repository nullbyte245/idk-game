using Game.Core;
using Game.Data;

namespace Game.Services
{
    public sealed class ScoreService : IService
    {
        private EventBus _events;
        private int _picked;
        private int _total;

        public float Current { get; private set; }
        public int Picked => _picked;
        public int Total => _total;
        public bool AllCollected => _total > 0 && _picked >= _total;

        public void OnRegister(IServiceRegistry registry) => _events = registry.Get<EventBus>();
        public void OnDispose() { }

        public void BeginRun(GameConfig config)
        {
            Current = 0f;
            _picked = 0;
            _total = config.collectibleCount;
            Publish();
        }

        public void AddCollectible(GameConfig config)
        {
            if (AllCollected) return;
            _picked++;
            Current += config.collectibleScore;
            Publish();
        }

        public void AddScore(float amount)
        {
            Current += amount;
            Publish();
        }

        private void Publish() => _events.Publish(new ScoreChangedEvent(Current, _picked, _total));
    }
}
