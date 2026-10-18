using System;
using System.Collections.Generic;
using System.Text;
using Game.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Online
{
    /// <summary>
    /// The whole online surface of the game: an identity, a nickname, and the lobby registry on the
    /// VPS. It polls instead of holding a socket because the registry is plain HTTP and the only
    /// realtime requirement is that a lobby list does not look dead.
    ///
    /// Requests are never allowed to stack: one in flight at a time, so a laggy link slows the
    /// refresh rate rather than turning it into a queue.
    /// </summary>
    public sealed class LobbyService : IService, ITickable
    {
        private const double ListEverySeconds = 1.0;
        private const double RoomEverySeconds = 0.5;
        private const int TimeoutSeconds = 5;
        private const int NameLimit = 24;

        private static readonly string[] Adjectives =
        {
            "Neon", "Static", "Cobalt", "Amber", "Vector", "Hollow", "Quiet",
            "Rapid", "Crimson", "Frozen", "Golden", "Iron", "Solar", "Toxic",
        };

        private static readonly string[] Nouns =
        {
            "Fox", "Raven", "Circuit", "Nomad", "Viper", "Echo", "Drifter",
            "Falcon", "Spark", "Warden", "Pixel", "Comet", "Reaper", "Signal",
        };

        private readonly HashSet<UnityWebRequest> _inflight = new HashSet<UnityWebRequest>();
        private double _nextPollAt;
        private bool _disposed;

        public string BaseUrl { get; private set; } = "http://166.1.144.235:8791";
        public string ClientId { get; private set; }
        public string Nickname { get; private set; }
        public LobbyBrowseMode Mode { get; private set; }
        public LobbySummary[] Lobbies { get; private set; } = Array.Empty<LobbySummary>();
        public LobbyRoomState Room { get; private set; }
        public string Status { get; private set; } = "Idle";

        public event Action Changed;

        public bool IsHost => Room != null && !string.IsNullOrEmpty(ClientId) && Room.hostClientId == ClientId;

        public void OnRegister(IServiceRegistry registry)
        {
            ClientId = Guid.NewGuid().ToString("N");
            Nickname = RandomNickname();
            Debug.Log($"[Lobby] {Nickname} -> {BaseUrl}");
        }

        public void OnDispose()
        {
            _disposed = true;
            CancelAll();
            Changed = null;
        }

        public void SetBaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            BaseUrl = url.TrimEnd('/');
        }

        public void OnTick(float deltaTime)
        {
            if (_disposed || Mode == LobbyBrowseMode.None) return;
            if (Mode == LobbyBrowseMode.Room && Room == null) return;
            if (_inflight.Count > 0) return;

            var now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextPollAt) return;

            if (Mode == LobbyBrowseMode.List)
            {
                _nextPollAt = now + ListEverySeconds;
                Get("/lobbies", OnListReply);
            }
            else
            {
                _nextPollAt = now + RoomEverySeconds;
                Get($"/lobbies/{Room.id}?c={ClientId}", OnRoomReply);
            }
        }

        // ------------------------------------------------------------------ browsing

        public void BrowseList()
        {
            // Showing the list means not being in a room, so a room left behind by a previous
            // session cannot bounce the player straight back into it.
            Room = null;
            Mode = LobbyBrowseMode.List;
            _nextPollAt = 0f;
        }

        public void BrowseRoom()
        {
            if (Room == null)
            {
                BrowseList();
                return;
            }
            Mode = LobbyBrowseMode.Room;
            _nextPollAt = 0f;
        }

        public void StopBrowsing() => Mode = LobbyBrowseMode.None;

        // ------------------------------------------------------------------ actions

        public void CreateLobby(string lobbyName)
        {
            var body = LobbyRequest.For(this);
            body.lobbyName = Sanitize(lobbyName, "Untitled lobby");
            Status = "Creating...";
            Changed?.Invoke();
            Post("/lobbies", body, OnRoomReply);
        }

        public void JoinLobby(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            Status = "Joining...";
            Changed?.Invoke();
            Post($"/lobbies/{id}/join", LobbyRequest.For(this), OnRoomReply);
        }

        public void LeaveLobby()
        {
            var id = Room != null ? Room.id : null;
            Room = null;
            BrowseList();
            Changed?.Invoke();
            if (string.IsNullOrEmpty(id)) return;
            Post($"/lobbies/{id}/leave", LobbyRequest.For(this), null);
        }

        public void StartMatch()
        {
            if (Room == null || !IsHost) return;
            Status = "Starting match...";
            Changed?.Invoke();
            Post($"/lobbies/{Room.id}/start", LobbyRequest.For(this), OnRoomReply);
        }

        /// <summary>
        /// Re-opens a match that already ended, for the player who pressed PLAY AGAIN. Deliberately
        /// fire-and-forget with no reply handler: <see cref="OnRoomReply"/> would put the poll back into
        /// room mode and drag the lobby screen over a run that is already being played. The relay hands
        /// the new start stamp to every client through the arena sync instead, which is where the other
        /// players learn that the round reset.
        /// </summary>
        public void RestartMatch()
        {
            if (Room == null || !IsHost || !Room.started) return;
            Post($"/lobbies/{Room.id}/start", LobbyRequest.For(this), null);
        }

        public void SetNickname(string value)
        {
            var clean = Sanitize(value, null);
            if (clean == null || clean == Nickname) return;

            Nickname = clean;
            Changed?.Invoke();
            if (Room == null) return;
            Post($"/lobbies/{Room.id}/rename", LobbyRequest.For(this), null);
        }

        // ------------------------------------------------------------------ replies

        private void OnListReply(LobbyReply reply)
        {
            if (!reply.Ok)
            {
                Status = reply.Error;
                Changed?.Invoke();
                return;
            }

            var parsed = JsonUtility.FromJson<LobbyListResponse>(reply.Body);
            Lobbies = parsed?.lobbies ?? Array.Empty<LobbySummary>();
            Status = Lobbies.Length == 0 ? "No lobbies yet. Create one." : $"{Lobbies.Length} lobby(ies)";
            Changed?.Invoke();
        }

        private void OnRoomReply(LobbyReply reply)
        {
            if (!reply.Ok)
            {
                // A room that answers 404 was pruned or left by its host; drop back to the list
                // rather than letting the player stare at a roster that no longer exists.
                if (reply.Code == 404 && Mode == LobbyBrowseMode.Room)
                {
                    Room = null;
                    BrowseList();
                }
                Status = reply.Error;
                Changed?.Invoke();
                return;
            }

            var parsed = JsonUtility.FromJson<LobbyRoomState>(reply.Body);
            if (parsed == null || string.IsNullOrEmpty(parsed.id))
            {
                Status = "Bad reply from the server.";
                Changed?.Invoke();
                return;
            }

            Room = parsed;
            Mode = LobbyBrowseMode.Room;
            Status = parsed.name;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ transport

        private void Get(string path, Action<LobbyReply> onDone) => Send(UnityWebRequest.kHttpVerbGET, path, null, onDone);

        private void Post(string path, LobbyRequest body, Action<LobbyReply> onDone) =>
            Send(UnityWebRequest.kHttpVerbPOST, path, JsonUtility.ToJson(body), onDone);

        private void Send(string method, string path, string json, Action<LobbyReply> onDone)
        {
            var request = new UnityWebRequest(BaseUrl + path, method);
            request.timeout = TimeoutSeconds;
            request.downloadHandler = new DownloadHandlerBuffer();
            if (json != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.SetRequestHeader("Content-Type", "application/json");
            }

            _inflight.Add(request);
            try
            {
                request.SendWebRequest().completed += _ =>
                {
                    _inflight.Remove(request);

                    var body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                    var reply = request.result == UnityWebRequest.Result.Success
                        ? LobbyReply.Success((int)request.responseCode, body)
                        : LobbyReply.Failure((int)request.responseCode, ReadError(body, request.error));

                    request.Dispose();
                    if (!_disposed && onDone != null) onDone(reply);
                };
            }
            catch (Exception e)
            {
                // A refused connection (plain HTTP blocked in the player, bad URL) throws here instead
                // of coming back as a reply, so turn it into one rather than killing the click.
                _inflight.Remove(request);
                request.Dispose();
                Debug.LogWarning($"[Lobby] {method} {path}: {e.Message}");
                if (!_disposed && onDone != null) onDone(LobbyReply.Failure(0, e.Message));
            }
        }

        private void CancelAll()
        {
            foreach (var request in new List<UnityWebRequest>(_inflight)) request.Abort();
            _inflight.Clear();
        }

        private static string ReadError(string body, string transportError)
        {
            // The registry answers 4xx with a reason meant for the player; the transport error is
            // only the status line, so prefer the payload whenever it is there.
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var parsed = JsonUtility.FromJson<LobbyErrorResponse>(body);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.error)) return parsed.error;
                }
                catch (Exception)
                {
                    // Not JSON at all: fall through to the transport message below.
                }
            }
            return string.IsNullOrEmpty(transportError) ? "Server unreachable." : transportError;
        }

        // ------------------------------------------------------------------ identity

        private static string RandomNickname()
        {
            var adjective = Adjectives[UnityEngine.Random.Range(0, Adjectives.Length)];
            var noun = Nouns[UnityEngine.Random.Range(0, Nouns.Length)];
            return $"{adjective}{noun}{UnityEngine.Random.Range(10, 100)}";
        }

        private static string Sanitize(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var c in value)
            {
                if (char.IsControl(c)) continue;
                if (c == ' ')
                {
                    if (builder.Length > 0) pendingSpace = true;
                    continue;
                }
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(c);
                if (builder.Length >= NameLimit) break;
            }
            var text = builder.ToString().Trim();
            return text.Length == 0 ? fallback : text;
        }
    }
}
