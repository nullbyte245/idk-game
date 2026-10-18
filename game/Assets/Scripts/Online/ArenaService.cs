using System;
using System.Collections.Generic;
using System.Text;
using Game.Core;
using Game.Services;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Online
{
    /// <summary>
    /// One round trip of the arena, ten times a second: this client's pose plus whatever it did, and
    /// everybody else's poses plus what they did. Both directions share the request because neither
    /// player can be reached from outside their own NAT.
    ///
    /// It runs next to <see cref="LobbyService"/> rather than through it on purpose: the lobby poll can
    /// afford to be late, the arena cannot, so they need separate in-flight slots and separate cadences.
    /// </summary>
    public sealed class ArenaService : IService, ITickable
    {
        public const int PoseFloats = 7;
        public const int ShotFloats = 6;

        private const double SyncEverySeconds = 0.1;
        private const int TimeoutSeconds = 3;
        private const int MaxShotsPerSync = 4;

        private readonly HashSet<UnityWebRequest> _inflight = new HashSet<UnityWebRequest>();
        private readonly List<ArenaPeerState> _peers = new List<ArenaPeerState>();
        private readonly Queue<float[]> _shots = new Queue<float[]>();
        private readonly HashSet<string> _orbs = new HashSet<string>();
        private readonly float[] _pose = new float[PoseFloats];

        private LobbyService _lobby;
        private GameFlowService _flow;
        private double _nextSyncAt;
        private int _lastSeq = -1;
        private long _startedAt;
        private string _pendingOrb;
        private int _consecutiveFailures;
        private bool _disposed;

        /// <summary>A tracer somebody else pulled the trigger on. Raised once per remote round.</summary>
        public event Action<ArenaShot> RemoteShot;

        /// <summary>The relay reports a different match stamp, which only happens when the host presses
        /// PLAY AGAIN: the orb set in the same reply is already empty, so a run that was still holding
        /// the old arena has to be rebuilt rather than resumed.</summary>
        public event Action MatchRestarted;

        /// <summary>Raised once per relay reply, after the peer list has been rebuilt. It exists because
        /// a pose is only new when a reply brings it: reading the same list every frame and re-stamping
        /// it with the local clock moves each sample forwards without moving the body, which makes the
        /// interpolator bracket a pair of identical positions and then snap when the real reply lands.</summary>
        public event Action PeersSynced;

        public bool IsLive { get; private set; }
        public IReadOnlyList<ArenaPeerState> Peers => _peers;

        public bool IsOrbTaken(string orbId) => !string.IsNullOrEmpty(orbId) && _orbs.Contains(orbId);

        public void OnRegister(IServiceRegistry registry)
        {
            _lobby = registry.Get<LobbyService>();
            _flow = registry.Get<GameFlowService>();
        }

        public void OnDispose()
        {
            _disposed = true;
            RemoteShot = null;
            MatchRestarted = null;
            PeersSynced = null;
            CancelAll();
        }

        /// <summary>Where the local player is, in the layout the relay echoes back untouched.</summary>
        public void SetPose(Vector3 position, float yawDegrees, float pitchDegrees, float planarSpeed, bool grounded)
        {
            _pose[0] = position.x;
            _pose[1] = position.y;
            _pose[2] = position.z;
            _pose[3] = yawDegrees;
            _pose[4] = pitchDegrees;
            _pose[5] = planarSpeed;
            _pose[6] = grounded ? 1f : 0f;
        }

        /// <summary>Hands a fired round to the next sync. Older rounds are dropped rather than queued.</summary>
        public void ReportShot(Vector3 muzzle, Vector3 endPoint)
        {
            if (!IsLive) return;
            _shots.Enqueue(new[]
            {
                muzzle.x, muzzle.y, muzzle.z,
                endPoint.x, endPoint.y, endPoint.z,
            });
            while (_shots.Count > MaxShotsPerSync) _shots.Dequeue();
        }

        /// <summary>
        /// Proposes an orb as taken. Nothing is spent here: the claim only counts once the relay puts it
        /// in the authoritative set, which is what every client, this one included, then applies.
        /// </summary>
        public void ClaimOrb(string orbId)
        {
            if (!IsLive || string.IsNullOrEmpty(orbId)) return;
            if (_orbs.Contains(orbId)) return;
            _pendingOrb = orbId;
        }

        public void OnTick(float deltaTime)
        {
            if (_disposed) return;

            var room = _lobby != null ? _lobby.Room : null;
            // A room that started is a match this client is inside; anywhere the showcase is on screen
            // there is no arena to feed.
            var shouldRun = room != null && room.started && _flow != null && !_flow.IsShowcase;
            if (!shouldRun)
            {
                if (IsLive) Stop();
                return;
            }

            if (_inflight.Count > 0) return;
            var now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextSyncAt) return;
            _nextSyncAt = now + SyncEverySeconds;

            Sync(room.id);
        }

        /// <summary>Drops the arena picture: a match we cannot talk about must leave no bodies behind.</summary>
        private void Stop()
        {
            IsLive = false;
            _peers.Clear();
            _orbs.Clear();
            _shots.Clear();
            _pendingOrb = null;
            _consecutiveFailures = 0;
            // The next room may be a different match entirely, so its first stamp is only a baseline.
            _startedAt = 0;
        }

        // ------------------------------------------------------------------ transport

        private void Sync(string roomId)
        {
            var body = new ArenaSyncRequest
            {
                clientId = _lobby.ClientId,
                nickname = _lobby.Nickname,
                since = IsLive ? _lastSeq : -1,
                pose = _pose,
                orb = _pendingOrb ?? string.Empty,
            };
            body.shots = TakeShots();

            var request = new UnityWebRequest(_lobby.BaseUrl + "/lobbies/" + roomId + "/sync", "POST");
            request.timeout = TimeoutSeconds;
            request.downloadHandler = new DownloadHandlerBuffer();
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
            request.SetRequestHeader("Content-Type", "application/json");
            _inflight.Add(request);

            try
            {
                request.SendWebRequest().completed += _ =>
                {
                    _inflight.Remove(request);
                    var text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                    var code = (int)request.responseCode;
                    var ok = request.result == UnityWebRequest.Result.Success;
                    request.Dispose();
                    if (_disposed) return;
                    if (ok) OnReply(code, text);
                    else OnFailure(code, text, request.error);
                };
            }
            catch (Exception e)
            {
                _inflight.Remove(request);
                request.Dispose();
                OnFailure(0, string.Empty, e.Message);
            }
        }

        private float[] TakeShots()
        {
            if (_shots.Count == 0) return Array.Empty<float>();
            var flat = new List<float>(_shots.Count * ShotFloats);
            while (_shots.Count > 0) flat.AddRange(_shots.Dequeue());
            return flat.ToArray();
        }

        private void OnReply(int code, string text)
        {
            var reply = JsonUtility.FromJson<ArenaSyncResponse>(text);
            if (reply == null || reply.peers == null)
            {
                OnFailure(code, text, "bad reply");
                return;
            }

            _consecutiveFailures = 0;
            _lastSeq = reply.seq;
            var restarted = _startedAt != 0 && reply.startedAt != _startedAt;
            _startedAt = reply.startedAt;
            if (!IsLive) Debug.Log("[Arena] relay live");
            IsLive = true;

            _peers.Clear();
            foreach (var peer in reply.peers)
                if (peer != null && peer.pose != null && peer.pose.Length >= PoseFloats) _peers.Add(peer);

            // The set is authoritative and complete on every reply, so a lost reply cannot desync it.
            _orbs.Clear();
            if (reply.orbs != null)
                foreach (var orb in reply.orbs)
                    if (!string.IsNullOrEmpty(orb)) _orbs.Add(orb);

            if (_pendingOrb != null && _orbs.Contains(_pendingOrb)) _pendingOrb = null;

            // Before the events: a body has to be placed even in a reply that carries nothing else.
            PeersSynced?.Invoke();

            if (reply.events != null)
                foreach (var eventDto in reply.events)
                {
                    if (eventDto == null || eventDto.v == null || eventDto.v.Length < ShotFloats) continue;
                    if (eventDto.k == "shot")
                        RemoteShot?.Invoke(new ArenaShot(eventDto.by, eventDto.v));
                }

            // Last, so anything a listener rebuilds sees the orb set and the peer list this reply
            // already settled.
            if (restarted) MatchRestarted?.Invoke();
        }

        private void CancelAll()
        {
            foreach (var request in new List<UnityWebRequest>(_inflight)) request.Abort();
            _inflight.Clear();
        }

        private void OnFailure(int code, string text, string transportError)
        {
            // 404 = the lobby was pruned, 403 = this client is no longer in it. Both mean there is no
            // match to keep talking about, so stop rather than retry forever.
            if (code == 404 || code == 403)
            {
                Stop();
                Debug.LogWarning($"[Arena] {(code == 404 ? "the lobby closed under us" : "we are no longer in the match")}");
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures < 4) return;

            IsLive = false;
            _peers.Clear();
            var reason = string.IsNullOrEmpty(transportError) ? "no answer" : transportError;
            Debug.LogWarning($"[Arena] relay lost: {reason} (HTTP {code})");
        }

        /// <summary>A remote round: who fired it and the six floats of muzzle then endpoint.</summary>
        public readonly struct ArenaShot
        {
            public readonly string ShooterId;
            public readonly Vector3 Muzzle;
            public readonly Vector3 EndPoint;

            public ArenaShot(string shooterId, float[] values)
            {
                ShooterId = shooterId;
                Muzzle = new Vector3(values[0], values[1], values[2]);
                EndPoint = new Vector3(values[3], values[4], values[5]);
            }
        }
    }
}
