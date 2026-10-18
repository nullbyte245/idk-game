using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Online
{
    /// <summary>
    /// Another player standing in this arena: a clone of the local body with the first person parts
    /// stripped off, driven by nothing but the pose stream.
    ///
    /// The relay answers once per round trip, so the stream arrives at whatever rate the connection
    /// allows - seven or eight poses a second here, with tens of milliseconds of jitter on every gap.
    /// A body read straight off those stamps stands still until a reply lands and then jumps, which is
    /// the teleport this class exists to remove. Two things fix it: the incoming gaps are paced to
    /// their own running average, so the timeline the body is read from is even; and a playhead walks
    /// that timeline in real time, held a couple of poses behind its newest end and corrected in speed
    /// rather than in position, so a late reply costs a slightly longer stride instead of a snap.
    /// </summary>
    public sealed class RemoteAvatar : MonoBehaviour
    {
        // What the pacing filter is allowed to believe about the send rate. Outside these bounds a
        // stall or a burst is being averaged into the cadence and the buffer would be sized wrong.
        private const float MinInterval = 0.05f;
        private const float MaxInterval = 0.35f;
        private const float InitialInterval = 0.12f;
        // How far behind the newest pose the body renders, in poses. Under two and a half leaves no
        // pair to read whenever a reply is late, and the body then has to be dragged back out of the
        // dead reckoning it slid into. Over three and the player visibly lives in the past.
        private const float BufferIntervals = 2.6f;
        private const float MinBuffer = 0.2f;
        private const float MaxBuffer = 0.6f;
        // How far outside the measured cadence one arrival may be before its gap is clipped. A spike
        // kept as measured is a run-then-drag; clipped to the average it is invisible.
        private const float GapLow = 0.65f;
        private const float GapHigh = 1.5f;
        // How much faster or slower than real time the playhead may run to stay on the buffer. Three
        // times clears a 50 ms error in a sixth of a second, which reads as one long step, not a jump.
        private const float CatchUp = 3f;
        // How long the body may keep sliding past the newest pose, in poses. One is about the distance
        // the next reply would have covered anyway; past that the body holds.
        private const float ExtrapolateIntervals = 1.2f;
        // A jump this big between two poses is not movement, it is a respawn or a lag gap: slide the
        // body across the room for it and the player looks like they are flying.
        private const float SnapDistance = 12f;
        // A pose the relay has held for this long is a retransmission from before a stall. It has no
        // business at the end of the timeline, where it would corrupt the measured cadence.
        private const float MaxPoseAge = 0.6f;

        private struct Sample
        {
            /// <summary>Where this pose sits on the paced timeline, not when it arrived.</summary>
            public double At;
            /// <summary>Local clock the reply was read, used only to measure the next gap.</summary>
            public double Wall;
            public Vector3 Position;
            public float Yaw;
            public float Pitch;
            public float Speed;
            public bool Grounded;
        }

        private readonly List<Sample> _samples = new List<Sample>(16);

        private Animator _animator;
        private Transform _head;
        private Quaternion _headRest;
        private TextMesh _plate;
        private float _runSpeed = 6f;

        private float _interval = InitialInterval;
        private float _buffer = InitialInterval * BufferIntervals;
        private double _playhead;
        private bool _playheadSet;

        public string PeerId { get; private set; }

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int GroundedId = Animator.StringToHash("Grounded");

        /// <summary>Call once, right after the clone is made and before the first pose arrives.</summary>
        public void Initialise(string peerId, string peerName, float runSpeed)
        {
            PeerId = peerId;
            _runSpeed = Mathf.Max(1f, runSpeed);

            _animator = GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                // A paused local run must not freeze everybody else: their match is still moving.
                _animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                _animator.applyRootMotion = false;
            }

            _head = FindByName(transform, "Head");
            if (_head != null) _headRest = _head.localRotation;

            BuildPlate(peerName);
        }

        /// <summary>
        /// Takes one pose from the relay. <paramref name="ageSeconds"/> is how long the relay held it,
        /// and it is not used as a clock: the reply has already crossed the network once on the way
        /// here, so any stamp the sender wrote would have to survive a round trip to mean anything. It
        /// is only a filter, so that a pose retransmitted after a stall cannot land at the end of the
        /// timeline and drag the measured cadence with it.
        /// </summary>
        public void Push(float[] pose, float ageSeconds)
        {
            if (ageSeconds > MaxPoseAge) return;

            var wall = Time.realtimeSinceStartupAsDouble;
            var at = wall;

            if (_samples.Count > 0)
            {
                var last = _samples[_samples.Count - 1];
                // Pace the gap, then average it. A measured gap is the sender's cadence plus this
                // connection's jitter, and only the cadence belongs on the timeline: a 60 ms spike kept
                // as measured makes the body run, and the reply after it drags the playhead back over
                // the ground it was just given.
                var gap = Mathf.Clamp((float)(wall - last.Wall), _interval * GapLow, _interval * GapHigh);
                _interval = Mathf.Clamp(Mathf.Lerp(_interval, gap, 0.25f), MinInterval, MaxInterval);
                _buffer = Mathf.Clamp(_interval * BufferIntervals, MinBuffer, MaxBuffer);
                at = last.At + gap;

                // A stream that stopped has to be slid back onto the clock, or the body keeps reading
                // poses from before the gap. The playhead goes with it, so the correction is one clean
                // placement rather than a run followed by a drag.
                if (at < wall - _buffer) _playheadSet = false;
            }

            var sample = new Sample
            {
                At = at,
                Wall = wall,
                Position = new Vector3(pose[0], pose[1], pose[2]),
                Yaw = pose[3],
                Pitch = pose[4],
                Speed = pose[5],
                Grounded = pose[6] > 0.5f,
            };

            if (_samples.Count == 0)
            {
                transform.position = sample.Position;
                transform.rotation = Quaternion.Euler(0f, sample.Yaw, 0f);
            }
            else if ((sample.Position - _samples[_samples.Count - 1].Position).sqrMagnitude
                     > SnapDistance * SnapDistance)
            {
                // A teleport clears the buffer: interpolating out of a stale corner of the arena is
                // exactly the flying the buffer exists to prevent.
                _samples.Clear();
                _playheadSet = false;
                transform.position = sample.Position;
            }

            _samples.Add(sample);
            while (_samples.Count > 2 && _samples[0].At < at - MaxBuffer) _samples.RemoveAt(0);
            while (_samples.Count > 16) _samples.RemoveAt(0);
        }

        private void LateUpdate()
        {
            if (_samples.Count == 0) return;

            // Unscaled, because the animator on this body is too: the stream keeps arriving while a
            // menu is open, and a playhead that stopped would have to run the buffer down at three
            // times speed the moment the menu closes.
            var dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            var newest = _samples[_samples.Count - 1];
            if (!_playheadSet)
            {
                _playhead = newest.At - _buffer;
                _playheadSet = true;
            }
            else
            {
                _playhead += dt;
                // Hold the playhead one buffer behind the newest pose by changing its rate, never its
                // position: a rate change reads as a stride that runs a touch long or short, while a
                // position change is the snap this whole class exists to remove. It may speed up to
                // catch a stream that ran ahead of it, but it never runs backwards.
                var error = (float)((newest.At - _buffer) - _playhead);
                _playhead += error > 0f
                    ? Math.Min(error, CatchUp * dt)
                    : Math.Max(error, -dt * 0.5f);
            }

            for (var i = 1; i < _samples.Count; i++)
            {
                if (_samples[i].At < _playhead) continue;
                Place(_samples[i - 1], _samples[i], SampleT(_samples[i - 1], _samples[i], _playhead));
                return;
            }

            // Nothing left to read: the stream ran out from under the playhead. Carry on along the
            // last measured step for one pose, which is about what the missing reply would have
            // covered, then hold - past that a player who stopped is wrong by more than the reply
            // would have been.
            if (_samples.Count < 2)
            {
                Place(newest, newest, 1f);
                return;
            }

            var previous = _samples[_samples.Count - 2];
            Place(previous, newest, 1f);

            var span = (float)(newest.At - previous.At);
            if (span < 0.0001f) return;
            var overshoot = (float)Math.Min(_playhead - newest.At, _interval * ExtrapolateIntervals);
            if (overshoot > 0f)
                transform.position += (newest.Position - previous.Position) * (overshoot / span);
        }

        private static float SampleT(Sample a, Sample b, double target)
        {
            var span = b.At - a.At;
            return span > 0.0001 ? Mathf.Clamp01((float)((target - a.At) / span)) : 1f;
        }

        private void Place(Sample a, Sample b, float t)
        {
            transform.position = Vector3.Lerp(a.Position, b.Position, t);
            transform.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(a.Yaw, b.Yaw, t), 0f);

            if (_head != null)
                _head.localRotation = _headRest * Quaternion.Euler(Mathf.Lerp(a.Pitch, b.Pitch, t), 0f, 0f);

            if (_animator != null && _animator.isActiveAndEnabled)
            {
                // Walk sits on 0.5 and run on 1.0, which are the blend tree thresholds the local
                // driver uses too, so both bodies read the same clip at the same speed.
                _animator.SetFloat(SpeedId, Mathf.Max(0f, Mathf.Lerp(a.Speed, b.Speed, t)) / _runSpeed);
                _animator.SetBool(GroundedId, b.Grounded);
            }

            if (_plate != null) BillboardPlate();
        }

        private void BillboardPlate()
        {
            var eye = Camera.main;
            if (eye == null) return;
            var toEye = eye.transform.position - _plate.transform.position;
            if (toEye.sqrMagnitude < 0.001f) return;
            _plate.transform.rotation = Quaternion.LookRotation(toEye, Vector3.up);
        }

        private void BuildPlate(string peerName)
        {
            var go = new GameObject("Nameplate");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            go.layer = gameObject.layer;

            _plate = go.AddComponent<TextMesh>();
            _plate.text = peerName;
            _plate.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _plate.fontSize = 44;
            _plate.characterSize = 0.09f;
            _plate.anchor = TextAnchor.MiddleCenter;
            _plate.alignment = TextAlignment.Center;
            _plate.fontStyle = FontStyle.Bold;
            _plate.color = new Color(0.94f, 0.96f, 1f, 0.92f);
        }

        private static Transform FindByName(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }
    }
}
