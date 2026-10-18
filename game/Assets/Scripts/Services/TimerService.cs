using Game.Core;
using UnityEngine;

namespace Game.Services
{
    /// <summary>Counts only time spent inside a live run; paused time is excluded.</summary>
    public sealed class TimerService : IService, ITickable
    {
        public float Elapsed { get; private set; }
        public bool IsRunning { get; private set; }

        public void OnRegister(IServiceRegistry registry) { }

        public void OnDispose() => Stop();

        public void Start()
        {
            Elapsed = 0f;
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;

        public void OnTick(float deltaTime)
        {
            if (IsRunning) Elapsed += deltaTime;
        }

        public string Format()
        {
            var total = (int)Mathf.Floor(Elapsed);
            var minutes = total / 60;
            var seconds = total % 60;
            var tenths = (int)Mathf.Floor((Elapsed - total) * 10f);
            return $"{minutes:00}:{seconds:00}.{tenths}";
        }
    }
}
