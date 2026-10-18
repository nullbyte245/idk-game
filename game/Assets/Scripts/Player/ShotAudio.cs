using System.Collections.Generic;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// A rifle shot is three sounds, not one. The crack at the muzzle, the report that rolls away down
    /// the arena, and a beat later the walls handing it back. Firing one sample straight into the ear
    /// is what makes a game gunshot feel like a headphone test instead of a gun.
    ///
    /// So a single clip is split across layers with different geometry: the crack rides with the
    /// shooter, the report is placed at the barrel in world space and falls off with distance, and the
    /// reflections arrive delayed, dimmed and dulled the way concrete takes the top off a bounce.
    ///
    /// The sample peaks at 0 dBFS, so every level here sits well under unity on purpose. That headroom
    /// is what keeps three overlapping rounds from summing into a clipped buzz.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShotAudio : MonoBehaviour
    {
        /// <summary>A reflection waiting for its turn to arrive.</summary>
        private struct Bounce
        {
            public AudioSource Source;
            public float At;
            public float Level;
            public float Pitch;
        }

        [SerializeField] private AudioClip clip;

        [Header("Level")]
        // The one knob: every layer below is a proportion of it.
        [SerializeField] private float level = 0.34f;
        [SerializeField] private int voices = 3;

        [Header("Report in the arena")]
        // Close enough that the shooter keeps it loud, far enough that a round fired across the map is
        // still audible but obviously over there.
        [SerializeField] private float minDistance = 1.6f;
        [SerializeField] private float maxDistance = 120f;
        [SerializeField] private AudioRolloffMode rolloff = AudioRolloffMode.Logarithmic;

        [Header("Variation")]
        [SerializeField] private float pitchJitter = 0.035f;
        // The barrel sits right of the eyes, so the crack is not centred.
        [SerializeField] private Vector2 nearPan = new Vector2(0.04f, 0.34f);

        [Header("Reflections")]
        [SerializeField] private float firstBounceDelay = 0.055f;
        [SerializeField] private float secondBounceDelay = 0.135f;
        [SerializeField] private float firstBounceLevel = 0.30f;
        [SerializeField] private float secondBounceLevel = 0.16f;
        // A bounce off a wall arrives with its highs gone. That duller copy is most of what makes the
        // arena sound like a place rather than a void.
        [SerializeField] private float firstBounceCutoff = 7600f;
        [SerializeField] private float secondBounceCutoff = 4200f;
        [SerializeField] private Vector2 firstBouncePan = new Vector2(-0.5f, -0.3f);
        [SerializeField] private Vector2 secondBouncePan = new Vector2(0.34f, 0.56f);

        private AudioSource[] _cracks;
        private AudioSource[] _reports;
        private AudioSource _bounceNear;
        private AudioSource _bounceFar;
        private readonly Queue<Bounce> _pending = new Queue<Bounce>();

        private int _cursor;

        private void Awake()
        {
            if (clip == null)
            {
                Debug.LogWarning("ShotAudio has no clip; the rifle fires silent.");
                enabled = false;
                return;
            }

            var count = Mathf.Max(1, voices);
            _cracks = new AudioSource[count];
            _reports = new AudioSource[count];
            for (var i = 0; i < count; i++)
            {
                _cracks[i] = NewSource("Crack " + i, false);
                _reports[i] = NewSource("Report " + i, true);
            }

            _bounceNear = NewBounce("Bounce Near", firstBounceCutoff, firstBouncePan);
            _bounceFar = NewBounce("Bounce Far", secondBounceCutoff, secondBouncePan);
        }

        private AudioSource NewSource(string name, bool threeD)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = threeD ? 1f : 0f;
            source.rolloffMode = rolloff;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
            source.priority = 128;
            return source;
        }

        private AudioSource NewBounce(string name, float cutoff, Vector2 pan)
        {
            var source = NewSource(name, false);
            source.panStereo = Random.Range(pan.x, pan.y);

            var filter = source.gameObject.AddComponent<AudioLowPassFilter>();
            filter.cutoffFrequency = cutoff;
            return source;
        }

        /// <summary>
        /// Fires at a muzzle in world space. A local shooter also gets the crack layer; anyone else is
        /// heard only through the report, so the arena places the shot instead of a gun going off
        /// inside the listener's head.
        /// </summary>
        public void Play(Vector3 muzzle, bool localShooter)
        {
            if (clip == null || _cracks == null) return;

            var slot = _cursor;
            _cursor = (slot + 1) % _cracks.Length;

            // Rounds out of the same rifle are never identical, and a repeated sample reads as a loop.
            var pitch = 1f + Random.Range(-pitchJitter, pitchJitter);

            _reports[slot].transform.position = muzzle;
            _reports[slot].pitch = pitch;
            _reports[slot].PlayOneShot(clip, level * (localShooter ? 0.78f : 1f));

            if (!localShooter) return;

            _cracks[slot].pitch = pitch * 1.01f;
            _cracks[slot].panStereo = Random.Range(nearPan.x, nearPan.y);
            _cracks[slot].PlayOneShot(clip, level);

            _pending.Enqueue(new Bounce
            {
                Source = _bounceNear,
                At = Time.time + firstBounceDelay,
                Level = level * firstBounceLevel,
                Pitch = pitch * 0.985f,
            });
            _pending.Enqueue(new Bounce
            {
                Source = _bounceFar,
                At = Time.time + secondBounceDelay,
                Level = level * secondBounceLevel,
                Pitch = pitch * 0.965f,
            });
        }

        private void Update()
        {
            while (_pending.Count > 0 && _pending.Peek().At <= Time.time)
            {
                var bounce = _pending.Dequeue();
                bounce.Source.pitch = bounce.Pitch;
                bounce.Source.PlayOneShot(clip, bounce.Level);
            }
        }
    }
}
