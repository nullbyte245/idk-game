using Game.Core;
using Game.Services;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// Repaints the arena's realtime reflection probe on a timer.
    ///
    /// The probe is set to ViaScripting, so without this it renders once at start-up and the floor
    /// keeps reflecting whatever the player was doing at that instant. Redrawing it a few times a
    /// second is enough for a moving body to read as a reflection instead of a smear, and a lot
    /// cheaper than every update.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ReflectionProbe))]
    public sealed class ReflectionProbeRefresher : MonoBehaviour
    {
        [SerializeField] private float interval = 0.22f;
        [SerializeField] private float idleInterval = 1.2f;

        private GameFlowService _flow;
        private ReflectionProbe _probe;
        private float _elapsed;

        private void Start()
        {
            _probe = GetComponent<ReflectionProbe>();
            GameServices.TryGet(out _flow);
        }

        private void LateUpdate()
        {
            if (_probe == null) return;

            var playing = _flow != null && !_flow.IsShowcase;
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < (playing ? interval : idleInterval)) return;

            _elapsed = 0f;
            // Unity 6 only exposes the overload that takes a target texture; null means the probe's
            // own cubemap.
            _probe.RenderProbe(null);
        }
    }
}
