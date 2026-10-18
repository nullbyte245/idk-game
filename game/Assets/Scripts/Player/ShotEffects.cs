using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player
{
    /// <summary>
    /// What a round leaves behind: the streak, the flame, the burst, and the report that travels the
    /// same distance a beat later. The ray is instant, the eye and the ear are not, so a tracer has to
    /// cross the room and the impact burst is scheduled for the moment the streak arrives rather than
    /// for the trigger pull. That half-beat between flash and sparks is what sells distance.
    ///
    /// The flame rides the viewmodel's muzzle so the recoil carries it away. The light it throws on the
    /// arena is a separate world object: a light on the ViewModel layer is culled by the eye camera and
    /// would light nothing the player can actually see. The audio itself lives in <see cref="ShotAudio"/>,
    /// which owns the layers a gunshot is made of.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShotEffects : MonoBehaviour
    {
        /// <summary>One tracer in flight, with the burst it owes the wall it is heading for.</summary>
        private struct Streak
        {
            public LineRenderer Beam;
            public float StartedAt;
            public float Travel;
            public float TailFraction;
            public Vector3 From;
            public Vector3 To;
            public Vector3 Normal;
            public bool OnSurface;
            public bool BurstDone;
        }

        private struct Spark
        {
            public LineRenderer Beam;
            public Vector3 Head;
            public Vector3 Velocity;
            public float Age;
            public float Life;
        }

        [SerializeField] private Transform muzzle;
        [SerializeField] private Transform aimOrigin;
        [SerializeField] private Vector3 fallbackMuzzle = new Vector3(0.14f, -0.10f, 0.35f);
        [SerializeField] private Material streakMaterial;
        [SerializeField] private Material glowMaterial;
        [SerializeField] private ShotAudio sound;

        [Header("Tracer")]
        [SerializeField] private int streakSlots = 3;
        // A 7.62 round crosses a 30 m room in 40 ms and nobody has ever seen that. Slowed to the
        // fastest speed that still reads as a streak travelling rather than a line blinking on.
        [SerializeField] private float tracerSpeed = 130f;
        [SerializeField] private float tracerTail = 3.2f;
        [SerializeField] private Color tracerColor = new Color(1f, 0.82f, 0.55f);

        [Header("Muzzle flash")]
        [SerializeField] private float flashLife = 0.05f;
        [SerializeField] private float flashSize = 0.30f;
        [SerializeField] private Color flashColor = new Color(1f, 0.66f, 0.26f);
        [SerializeField] private float lightPeak = 11f;
        [SerializeField] private float lightRange = 8f;
        [SerializeField] private float lightLife = 0.10f;

        [Header("Impact")]
        [SerializeField] private int sparkSlots = 7;
        [SerializeField] private float sparkLife = 0.30f;
        [SerializeField] private float sparkSpeed = 6.5f;
        [SerializeField] private Color sparkColor = new Color(1f, 0.72f, 0.36f);
        [SerializeField] private float puffLife = 0.16f;
        [SerializeField] private float puffSize = 0.62f;
        [SerializeField] private Color puffColor = new Color(0.52f, 0.47f, 0.43f);

        private Streak[] _streaks;
        private Spark[] _sparks;
        private int _streakCursor;

        private Transform _flash;
        private Renderer _flashRenderer;
        private float _flashStartedAt = float.NegativeInfinity;
        private float _flashRoll;

        private Transform _puff;
        private Renderer _puffRenderer;
        private float _puffStartedAt = float.NegativeInfinity;

        private Light _light;
        private float _lightStartedAt = float.NegativeInfinity;
        private float _lightSeed = 1f;

        private MaterialPropertyBlock _props;

        private void Awake()
        {
            if (aimOrigin == null && Camera.main != null) aimOrigin = Camera.main.transform;

            _props = new MaterialPropertyBlock();
            BuildStreaks();
            BuildSparks();
            BuildFlash();
            BuildPuff();
            _light = BuildLight();
        }

        /// <summary>
        /// Called by the rifle with where the round stopped. The streak starts at the barrel tip, not at
        /// the eye, so the line visibly leaves the gun even though the ray never did. Returns that point
        /// so the caller can hand the same muzzle to the relay instead of guessing at it again.
        /// </summary>
        public Vector3 Play(Vector3 endPoint, Vector3 surfaceNormal, bool onSurface)
        {
            var start = MuzzleWorld();

            // Sound goes out first: it is a fixed length and the eye can wait a frame for a line,
            // whereas a report that starts late reads as the gun lagging the trigger.
            if (sound != null) sound.Play(start, true);

            BeginStreak(start, endPoint, surfaceNormal, onSurface);
            PlayFlash(start);
            return start;
        }

        /// <summary>
        /// A round somebody else fired. Everything a shot leaves behind is world-side and replays as is,
        /// except the flame quad: it is parented to this player's viewmodel muzzle, on a layer only this
        /// player's eye camera draws, so firing it here would put the other player's flash inside our own
        /// gun. The report is the 3D one, placed at their barrel, so it arrives with their distance.
        /// </summary>
        public void PlayRemote(Vector3 start, Vector3 endPoint, Vector3 surfaceNormal, bool onSurface)
        {
            if (sound != null) sound.Play(start, false);
            BeginStreak(start, endPoint, surfaceNormal, onSurface);
            PlayLight(start);
        }

        private void BeginStreak(Vector3 start, Vector3 endPoint, Vector3 surfaceNormal, bool onSurface)
        {
            var distance = Mathf.Max(0.05f, Vector3.Distance(start, endPoint));

            var slot = _streakCursor;
            _streakCursor = (slot + 1) % _streaks.Length;

            _streaks[slot] = new Streak
            {
                Beam = _streaks[slot].Beam,
                StartedAt = Time.time,
                Travel = distance / Mathf.Max(1f, tracerSpeed),
                TailFraction = Mathf.Min(0.85f, tracerTail / distance),
                From = start,
                To = endPoint,
                Normal = surfaceNormal,
                OnSurface = onSurface,
                BurstDone = false,
            };
            _streaks[slot].Beam.enabled = true;
            _streaks[slot].Beam.SetPosition(0, start);
            _streaks[slot].Beam.SetPosition(1, start);
        }

        private void Update()
        {
            var now = Time.time;
            UpdateStreaks(now);
            UpdateFlash(now);
            UpdatePuff(now);
            UpdateLight(now);
            UpdateSparks(Time.deltaTime);
        }

        // ------------------------------------------------------------------ tracer

        private void UpdateStreaks(float now)
        {
            for (var i = 0; i < _streaks.Length; i++)
            {
                var streak = _streaks[i];
                if (streak.Beam == null || !streak.Beam.enabled) continue;

                var progress = (now - streak.StartedAt) / streak.Travel;
                if (progress >= 1f + streak.TailFraction)
                {
                    streak.Beam.enabled = false;
                    continue;
                }

                // The head parks at the wall and the tail slides in after it, so the streak collapses
                // into the impact instead of switching off in mid-air.
                var head = Mathf.Clamp01(progress);
                var tail = Mathf.Max(0f, progress - streak.TailFraction);
                streak.Beam.SetPosition(0, Vector3.Lerp(streak.From, streak.To, tail));
                streak.Beam.SetPosition(1, Vector3.Lerp(streak.From, streak.To, head));

                if (!streak.OnSurface || streak.BurstDone || progress < 1f) continue;
                _streaks[i].BurstDone = true;
                EmitImpact(streak.To, streak.Normal);
            }
        }

        private void BuildStreaks()
        {
            _streaks = new Streak[Mathf.Max(1, streakSlots)];
            for (var i = 0; i < _streaks.Length; i++)
            {
                var beam = NewBeam("Tracer " + i, 0.024f, 0.005f);
                beam.startColor = Color.black;
                beam.endColor = tracerColor;
                _streaks[i].Beam = beam;
            }
        }

        // ------------------------------------------------------------------ muzzle

        private Vector3 MuzzleWorld()
        {
            if (muzzle != null) return muzzle.position;
            return aimOrigin != null ? aimOrigin.TransformPoint(fallbackMuzzle) : transform.position;
        }

        private void PlayFlash(Vector3 world)
        {
            _flashStartedAt = Time.time;
            _flashRoll = Random.Range(0f, 360f);

            if (_flash != null)
            {
                var size = flashSize * Random.Range(0.82f, 1.24f);
                _flash.localScale = new Vector3(size * 0.78f, size, 1f);
                _flash.gameObject.SetActive(true);
            }
            PlayLight(world);
        }

        private void PlayLight(Vector3 world)
        {
            if (_light == null) return;
            _lightStartedAt = Time.time;
            _lightSeed = Random.Range(0.78f, 1.22f);
            _light.transform.position = world;
            _light.enabled = true;
        }

        private void UpdateFlash(float now)
        {
            if (_flash == null || !_flash.gameObject.activeSelf) return;

            var age = (now - _flashStartedAt) / flashLife;
            if (age >= 1f)
            {
                _flash.gameObject.SetActive(false);
                return;
            }

            // Facing the eye camera and spun to a fresh roll every shot, so three rounds in a row never
            // show the same flame.
            var forward = aimOrigin != null ? aimOrigin.forward : transform.forward;
            _flash.rotation = Quaternion.LookRotation(forward) * Quaternion.Euler(0f, 0f, _flashRoll);
            _props.SetColor("_Color", flashColor * Mathf.Pow(1f - age, 1.6f) * 2.4f);
            _flashRenderer.SetPropertyBlock(_props);
        }

        private void BuildFlash()
        {
            var quad = NewQuad("Muzzle Flash", muzzle != null ? muzzle : transform);
            if (quad == null) return;
            _flash = quad.transform;
            _flashRenderer = quad.GetComponent<Renderer>();
        }

        // ------------------------------------------------------------------ light

        private void UpdateLight(float now)
        {
            if (_light == null || !_light.enabled) return;

            var age = (now - _lightStartedAt) / lightLife;
            if (age >= 1f)
            {
                _light.enabled = false;
                return;
            }
            _light.intensity = lightPeak * (1f - age) * (1f - age) * _lightSeed;
        }

        private Light BuildLight()
        {
            var go = new GameObject("Muzzle Light");
            go.transform.SetParent(transform, false);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = flashColor;
            light.range = lightRange;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.enabled = false;
            return light;
        }

        // ------------------------------------------------------------------ impact

        private void EmitImpact(Vector3 point, Vector3 normal)
        {
            _puffStartedAt = Time.time;
            if (_puff != null)
            {
                _puff.position = point + normal * 0.02f;
                _puff.localScale = new Vector3(puffSize * 0.35f, puffSize * 0.35f, 1f);
                _puff.gameObject.SetActive(true);
            }

            for (var i = 0; i < _sparks.Length; i++)
            {
                // Half the burst leaves along the surface it skipped across, the rest sprays free;
                // every spark going the same way reads as a sprite rather than as a hit.
                var spray = Random.onUnitSphere;
                if (Vector3.Dot(spray, normal) < 0f) spray = Vector3.Reflect(spray, normal);

                var spark = _sparks[i];
                spark.Head = point + normal * 0.03f;
                spark.Velocity = (normal * 0.45f + spray) * sparkSpeed * Random.Range(0.45f, 1.25f);
                spark.Age = 0f;
                spark.Life = sparkLife * Random.Range(0.55f, 1f);
                spark.Beam.enabled = true;
                _sparks[i] = spark;
            }
        }

        private void UpdatePuff(float now)
        {
            if (_puff == null || !_puff.gameObject.activeSelf) return;

            var age = (now - _puffStartedAt) / puffLife;
            if (age >= 1f)
            {
                _puff.gameObject.SetActive(false);
                return;
            }

            var forward = aimOrigin != null ? aimOrigin.forward : transform.forward;
            _puff.rotation = Quaternion.LookRotation(forward);
            var grow = Mathf.Lerp(0.35f, 1f, age);
            _puff.localScale = new Vector3(puffSize * grow, puffSize * grow, 1f);
            _props.SetColor("_Color", puffColor * (1f - age) * 1.4f);
            _puffRenderer.SetPropertyBlock(_props);
        }

        private void BuildPuff()
        {
            var quad = NewQuad("Impact Puff", transform);
            if (quad == null) return;
            _puff = quad.transform;
            _puffRenderer = quad.GetComponent<Renderer>();
        }

        private void UpdateSparks(float dt)
        {
            if (dt <= 0f) return;

            for (var i = 0; i < _sparks.Length; i++)
            {
                var spark = _sparks[i];
                if (spark.Beam == null || !spark.Beam.enabled) continue;

                var age = spark.Age + dt;
                if (age >= spark.Life)
                {
                    spark.Beam.enabled = false;
                    continue;
                }

                spark.Age = age;
                spark.Velocity += Physics.gravity * 1.7f * dt;
                spark.Head += spark.Velocity * dt;

                // The tail sits behind the head by however far the spark travelled this frame, so a fast
                // spark is a long streak and a dying one is a dot.
                spark.Beam.SetPosition(0, spark.Head - spark.Velocity * 0.02f);
                spark.Beam.SetPosition(1, spark.Head);

                var remaining = 1f - age / spark.Life;
                spark.Beam.startColor = sparkColor * (remaining * remaining * 0.4f);
                spark.Beam.endColor = sparkColor * Mathf.Pow(remaining, 3f);
                _sparks[i] = spark;
            }
        }

        private void BuildSparks()
        {
            _sparks = new Spark[Mathf.Max(1, sparkSlots)];
            for (var i = 0; i < _sparks.Length; i++)
                _sparks[i].Beam = NewBeam("Spark " + i, 0.012f, 0.003f);
        }

        // ------------------------------------------------------------------ parts

        private LineRenderer NewBeam(string name, float startWidth, float endWidth)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;

            var beam = go.AddComponent<LineRenderer>();
            beam.useWorldSpace = true;
            beam.positionCount = 2;
            beam.startWidth = startWidth;
            beam.endWidth = endWidth;
            beam.numCapVertices = 1;
            beam.numCornerVertices = 0;
            beam.alignment = LineAlignment.View;
            beam.shadowCastingMode = ShadowCastingMode.Off;
            beam.receiveShadows = false;
            beam.lightProbeUsage = LightProbeUsage.Off;
            beam.enabled = false;
            if (streakMaterial != null) beam.material = streakMaterial;
            else Debug.LogWarning("ShotEffects has no streak material; shot lines will be magenta.");
            return beam;
        }

        private GameObject NewQuad(string name, Transform parent)
        {
            if (glowMaterial == null)
            {
                Debug.LogWarning($"ShotEffects has no glow material; {name} is off.");
                return null;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;

            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.layer = parent.gameObject.layer;

            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = glowMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            go.SetActive(false);
            return go;
        }
    }
}
