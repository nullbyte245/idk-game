using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// Drives ITickable services. Runs on every Update regardless of timeScale so input keeps
    /// working while the game is paused.
    /// </summary>
    public sealed class TickDriver : MonoBehaviour
    {
        private ServiceRegistry _registry;
        private readonly List<ITickable> _snapshot = new List<ITickable>();

        public void Bind(ServiceRegistry registry)
        {
            _registry = registry;
        }

        private void Update()
        {
            if (_registry == null) return;

            _snapshot.Clear();
            _snapshot.AddRange(_registry.Tickables);

            var dt = Time.deltaTime;
            for (int i = 0; i < _snapshot.Count; i++)
            {
                try
                {
                    _snapshot[i].OnTick(dt);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Tick failed for {_snapshot[i].GetType().Name}: {e}");
                }
            }
        }
    }
}
