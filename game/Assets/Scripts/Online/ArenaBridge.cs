using System.Collections.Generic;
using Game.Core;
using Game.Gameplay;
using Game.Player;
using Game.Services;
using UnityEngine;

namespace Game.Online
{
    /// <summary>
    /// The seam between the arena and the relay. Everything that knows about both lives here: it reads
    /// this player's transform out into the sync, turns the sync's replies into bodies, tracers and
    /// taken orbs, and nothing in the gameplay code has to know that a server exists.
    ///
    /// Lives on the Player object beside the controller, because that is the only place in the scene
    /// that already has the movement, the eye and the rifle at hand.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArenaBridge : MonoBehaviour
    {
        [SerializeField] private FirstPersonController movement;
        [SerializeField] private HitscanWeapon weapon;
        [SerializeField] private ShotEffects effects;
        [SerializeField] private Transform eye;

        private readonly Dictionary<string, RemoteAvatar> _avatars = new Dictionary<string, RemoteAvatar>();
        private readonly Dictionary<string, float> _lastSeen = new Dictionary<string, float>();
        private readonly HashSet<string> _present = new HashSet<string>();
        private readonly List<string> _removed = new List<string>();
        private readonly List<Collectible> _orbs = new List<Collectible>();

        /// <summary>
        /// How long a body stays in the arena after the relay stops listing its player. The relay
        /// purges a pose after 1.2 s of silence and a sync that times out once looks exactly like
        /// that, so without a grace window one late reply destroys the body and the next one rebuilds
        /// it somewhere else - a teleport with extra steps. A player who really left is gone from the
        /// room itself, which drops them through <see cref="ClearAvatars"/> on the way out anyway.
        /// </summary>
        private const float KeepAfterSilence = 4f;

        private ArenaService _arena;
        private LobbyService _lobby;
        private Transform _body;
        private int _layer;

        private void Awake()
        {
            if (movement == null) movement = GetComponent<FirstPersonController>();
            if (weapon == null) weapon = GetComponentInChildren<HitscanWeapon>();
            if (effects == null) effects = GetComponentInChildren<ShotEffects>();
            if (eye == null && Camera.main != null) eye = Camera.main.transform;

            _layer = gameObject.layer;
            // The body is the avatar template: it is the only part of the player another client is
            // supposed to see, and cloning it keeps the two bodies identical by construction.
            var body = transform.Find("Body");
            _body = body != null ? body : transform;
            if (body == null) Debug.LogWarning("[ArenaBridge] no Body under the player; remote avatars use the whole player root.");

            _orbs.AddRange(FindObjectsByType<Collectible>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        }

        private void OnEnable()
        {
            GameServices.TryGet(out _arena);
            // The room roster is what tells a player who left from a player whose reply was late.
            GameServices.TryGet(out _lobby);
            if (_arena == null) return;

            if (weapon != null) weapon.ShotPlayed += OnLocalShot;
            _arena.RemoteShot += OnRemoteShot;
            // Once per reply, never per frame: see ArenaService.PeersSynced.
            _arena.PeersSynced += SyncAvatars;

            foreach (var orb in _orbs)
            {
                orb.Touched += OnOrbTouched;
                orb.UsesAuthority = false;
            }
        }

        private void OnDisable()
        {
            if (weapon != null) weapon.ShotPlayed -= OnLocalShot;
            if (_arena != null)
            {
                _arena.RemoteShot -= OnRemoteShot;
                _arena.PeersSynced -= SyncAvatars;
            }

            foreach (var orb in _orbs)
            {
                orb.Touched -= OnOrbTouched;
                orb.UsesAuthority = false;
            }
            ClearAvatars();
        }

        private void Update()
        {
            if (_arena == null) return;

            var live = _arena.IsLive;
            foreach (var orb in _orbs) orb.UsesAuthority = live;

            if (!live)
            {
                // Nobody to show: an avatar left behind after the relay drops is a player standing in
                // the arena forever.
                ClearAvatars();
                return;
            }

            FeedPose();
            ApplyOrbs();
        }

        // ------------------------------------------------------------------ outgoing

        private void FeedPose()
        {
            if (movement == null) return;

            var pitch = 0f;
            if (eye != null)
                pitch = -Mathf.Asin(Mathf.Clamp(eye.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            _arena.SetPose(transform.position, transform.eulerAngles.y, pitch,
                movement.PlanarVelocity.magnitude, movement.IsGrounded);
        }

        private void OnLocalShot(Vector3 muzzle, Vector3 endPoint) => _arena.ReportShot(muzzle, endPoint);

        private void OnOrbTouched(Collectible orb) => _arena.ClaimOrb(orb.OrbId);

        // ------------------------------------------------------------------ incoming

        private void OnRemoteShot(ArenaService.ArenaShot shot)
        {
            if (effects == null) return;

            // The relay carries two points, not a surface: the arena is the same scene on every
            // machine, so the wall this round stopped on is right here and the sparks can be real.
            // Environment only, exactly like the local rifle's aim mask, so a ghost standing in the way
            // cannot cut somebody else's tracer short.
            if (Physics.Linecast(shot.Muzzle, shot.EndPoint, out var hit, LayerMask.GetMask("Environment"),
                    QueryTriggerInteraction.Ignore))
            {
                effects.PlayRemote(shot.Muzzle, hit.point, hit.normal, true);
                return;
            }
            effects.PlayRemote(shot.Muzzle, shot.EndPoint, Vector3.zero, false);
        }

        private void SyncAvatars()
        {
            _present.Clear();
            var now = Time.realtimeSinceStartupAsDouble;

            foreach (var peer in _arena.Peers)
            {
                if (peer == null || string.IsNullOrEmpty(peer.id)) continue;
                if (!_avatars.TryGetValue(peer.id, out var avatar)) avatar = Spawn(peer);
                if (avatar == null) continue;

                _present.Add(peer.id);
                _lastSeen[peer.id] = (float)now;
                avatar.Push(peer.pose, peer.age);
            }

            if (_avatars.Count == 0) return;

            // The relay lists only players whose pose is under a second old, so a body missing from one
            // reply proves nothing about the next. The room roster does say who left, so that is what
            // decides: dropping a body on a single late reply and rebuilding it on the next is a
            // teleport, and this grace window is what removes it.
            _removed.Clear();
            foreach (var pair in _avatars)
            {
                if (_present.Contains(pair.Key)) continue;
                if (LeftRoom(pair.Key))
                {
                    _removed.Add(pair.Key);
                    continue;
                }
                if (!(_lastSeen.TryGetValue(pair.Key, out var seen)
                      && now - seen > KeepAfterSilence)) continue;
                _removed.Add(pair.Key);
            }

            foreach (var id in _removed)
            {
                Destroy(_avatars[id].gameObject);
                _avatars.Remove(id);
                _lastSeen.Remove(id);
            }
        }

        /// <summary>True only when the roster says so: an unknown room is not evidence that anybody left.</summary>
        private bool LeftRoom(string peerId)
        {
            var players = _lobby != null && _lobby.Room != null ? _lobby.Room.players : null;
            if (players == null) return false;
            foreach (var player in players)
                if (player != null && player.clientId == peerId) return false;
            return true;
        }

        private void ApplyOrbs()
        {
            foreach (var orb in _orbs)
            {
                if (orb == null || orb.IsCollected) continue;
                if (_arena.IsOrbTaken(orb.OrbId)) orb.Take();
            }
        }

        private RemoteAvatar Spawn(ArenaPeerState peer)
        {
            if (_body == null) return null;

            // Built straight onto the pose that is already in hand: instantiated at this player's own
            // feet, the body spends its first frame in the wrong half of the arena, and that flash is
            // what reads as teleporting even when every later sample lands where it should.
            var pose = peer.pose;
            var start = pose != null && pose.Length >= 3
                ? new Vector3(pose[0], pose[1], pose[2])
                : transform.position;
            var yaw = pose != null && pose.Length > 3 ? pose[3] : 0f;

            var clone = Instantiate(_body.gameObject, start, Quaternion.Euler(0f, yaw, 0f));
            clone.name = "Remote " + peer.name;

            // A remote body is scenery made of a player: it must not inherit the parts that only make
            // sense for the one camera inside it, and it must not be solid, or a ghost walking past
            // would shove the local player across the arena.
            foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) Destroy(collider);
            foreach (var hider in clone.GetComponentsInChildren<FirstPersonHeadHider>(true)) Destroy(hider);
            foreach (var driver in clone.GetComponentsInChildren<PlayerAnimatorDriver>(true)) Destroy(driver);
            foreach (var child in clone.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = _layer;

            var avatar = clone.AddComponent<RemoteAvatar>();
            avatar.Initialise(peer.id, peer.name, movement != null && movement.Config != null ? movement.Config.runSpeed : 6f);
            _avatars[peer.id] = avatar;
            return avatar;
        }

        private void ClearAvatars()
        {
            foreach (var avatar in _avatars.Values) Destroy(avatar.gameObject);
            _avatars.Clear();
        }
    }
}
