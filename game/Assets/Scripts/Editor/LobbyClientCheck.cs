using Game.Online;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Feeds the exact reply shapes the live registry produces through JsonUtility, which is the one
    /// part of the online client that can fail silently: field names, array mapping and number types.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath <proj> -executeMethod Game.Editor.LobbyClientCheck.VerifyMappings -quit
    /// </summary>
    public static class LobbyClientCheck
    {
        private const string ListJson =
            "{\"lobbies\":[{\"id\":\"382b6d0f-3315-41dd-ba4a-a83c975d595d\",\"name\":\"Test Arena\"," +
            "\"players\":2,\"maxPlayers\":4,\"started\":false,\"hostName\":\"Alpha\"}," +
            "{\"id\":\"b\",\"name\":\"Second\",\"players\":0,\"maxPlayers\":8,\"started\":true,\"hostName\":\"\"}]}";

        private const string RoomJson =
            "{\"id\":\"382b6d0f-3315-41dd-ba4a-a83c975d595d\",\"name\":\"Test Arena\"," +
            "\"hostClientId\":\"11111111-1111-1111-1111-111111111111\",\"started\":true,\"startedAt\":1792288879108," +
            "\"maxPlayers\":3,\"players\":[{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Alpha\",\"isHost\":true}," +
            "{\"clientId\":\"22222222-2222-2222-2222-222222222222\",\"name\":\"BetaX\",\"isHost\":false}]}";

        private const string ErrorJson = "{\"error\":\"lobby full\"}";

        /// <summary>A live arena round trip, in exactly the shape the relay answers with.</summary>
        private const string SyncJson =
            "{\"ok\":true,\"seq\":7,\"startedAt\":1792288879108," +
            "\"peers\":[{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"NeonFox42\"," +
            "\"pose\":[1.5,1.1,-2.25,90.5,-3.2,4.2,1],\"age\":0.08}," +
            "{\"id\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"name\":\"StaticRaven17\"," +
            "\"pose\":[0.125,1.0,0.5,-12.0,0.0,0.0,1],\"age\":0.02}]," +
            "\"events\":[{\"seq\":6,\"by\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"k\":\"shot\"," +
            "\"v\":[1.5,1.3,-2.0,40.0,1.1,-20.0]}]," +
            "\"orbs\":[\"Collectible_3\",\"Collectible_7\"]}";

        /// <summary>The first sync of a fresh match: nothing claimed, nobody else talking yet.</summary>
        private const string EmptySyncJson =
            "{\"ok\":true,\"seq\":0,\"startedAt\":1792288879108,\"peers\":[],\"events\":[],\"orbs\":[]}";

        /// <summary>The create/join reply of a lobby that never started: startedAt is JSON null.</summary>
        private const string FreshRoomJson =
            "{\"id\":\"cccccccc-1111-1111-1111-111111111111\",\"name\":\"ывавыва\"," +
            "\"hostClientId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"started\":false,\"startedAt\":null," +
            "\"maxPlayers\":4,\"players\":[{\"clientId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"NeonFox42\",\"isHost\":true}]}";

        public static void VerifyMappings()
        {
            var failures = 0;
            var list = JsonUtility.FromJson<LobbyListResponse>(ListJson);
            Check(list != null && list.lobbies != null && list.lobbies.Length == 2, "two lobbies parsed", ref failures);
            if (list?.lobbies != null && list.lobbies.Length == 2)
            {
                var first = list.lobbies[0];
                Check(first.name == "Test Arena", "lobby name", ref failures);
                Check(first.players == 2 && first.maxPlayers == 4, "occupancy numbers", ref failures);
                Check(!first.started, "started=false", ref failures);
                Check(list.lobbies[1].started, "started=true", ref failures);
                Check(first.id == "382b6d0f-3315-41dd-ba4a-a83c975d595d", "lobby id", ref failures);
            }

            var room = JsonUtility.FromJson<LobbyRoomState>(RoomJson);
            Check(room != null && room.players != null && room.players.Length == 2, "roster parsed", ref failures);
            if (room?.players != null && room.players.Length == 2)
            {
                Check(room.players[0].isHost && !room.players[1].isHost, "host flag per player", ref failures);
                Check(room.players[1].name == "BetaX", "renamed player name", ref failures);
                Check(room.hostClientId == room.players[0].clientId, "host id matches a roster entry", ref failures);
                Check(room.started && room.startedAt == 1792288879108L, "start state and timestamp", ref failures);
            }

            LobbyRoomState fresh = null;
            try
            {
                fresh = JsonUtility.FromJson<LobbyRoomState>(FreshRoomJson);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LobbyClientCheck] FAIL  a null startedAt threw {e.GetType().Name}: {e.Message}");
                failures++;
            }
            Check(fresh != null && fresh.id == "cccccccc-1111-1111-1111-111111111111",
                  "null startedAt still parses a room", ref failures);
            Check(fresh != null && fresh.name == "ывавыва" && !fresh.started && fresh.startedAt == 0 &&
                  fresh.players != null && fresh.players.Length == 1 && fresh.players[0].isHost,
                  "fresh room fields", ref failures);

            var failure = JsonUtility.FromJson<LobbyErrorResponse>(ErrorJson);
            Check(failure != null && failure.error == "lobby full", "server error message", ref failures);

            var request = JsonUtility.ToJson(new LobbyRequest
            {
                clientId = "a1b2",
                nickname = "NeonFox42",
                lobbyName = "",
                maxPlayers = 0,
            });
            Check(request.Contains("\"clientId\":\"a1b2\"") && request.Contains("\"maxPlayers\":0"),
                  "request body shape " + request, ref failures);

            var sync = JsonUtility.FromJson<ArenaSyncResponse>(SyncJson);
            Check(sync != null && sync.ok && sync.seq == 7 && sync.startedAt == 1792288879108L,
                  "sync header", ref failures);
            Check(sync?.peers != null && sync.peers.Length == 2, "two peers echoed", ref failures);
            if (sync?.peers != null && sync.peers.Length == 2)
            {
                var peer = sync.peers[0];
                Check(peer.id == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" && peer.name == "NeonFox42",
                      "peer identity", ref failures);
                Check(peer.pose != null && peer.pose.Length == 7 && peer.pose[0] == 1.5f && peer.pose[6] == 1f,
                      "flat pose of seven floats", ref failures);
                Check(Mathf.Abs(peer.age - 0.08f) < 0.0001f, "peer age", ref failures);
                Check(sync.peers[1].pose != null && sync.peers[1].pose[3] == -12f, "second peer yaw", ref failures);
            }
            Check(sync?.events != null && sync.events.Length == 1 && sync.events[0].k == "shot" &&
                  sync.events[0].by == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "one remote shot event", ref failures);
            Check(sync?.events != null && sync.events.Length == 1 && sync.events[0].v != null &&
                  sync.events[0].v.Length == 6 && sync.events[0].v[3] == 40f,
                  "tracer endpoints ride the event", ref failures);
            Check(sync?.orbs != null && sync.orbs.Length == 2 && sync.orbs[0] == "Collectible_3",
                  "authoritative orb set", ref failures);

            var empty = JsonUtility.FromJson<ArenaSyncResponse>(EmptySyncJson);
            Check(empty != null && empty.peers != null && empty.peers.Length == 0 &&
                  empty.events != null && empty.events.Length == 0 &&
                  empty.orbs != null && empty.orbs.Length == 0,
                  "an empty arena parses as empty, not null", ref failures);

            var syncRequest = JsonUtility.ToJson(new ArenaSyncRequest
            {
                clientId = "a1b2",
                nickname = "NeonFox42",
                since = -1,
                pose = new[] { 1.5f, 1.1f, -2.25f, 90.5f, 0f, 4.2f, 1f },
                shots = new[] { 1.5f, 1.3f, -2f, 40f, 1.1f, -20f },
                orb = "Collectible_3",
            });
            Check(syncRequest.Contains("\"since\":-1") && syncRequest.Contains("\"pose\":[")
                  && syncRequest.Contains("\"shots\":["),
                  "sync body shape " + syncRequest, ref failures);

            // Read the emitted body back rather than matching the numbers as text: how a float prints
            // belongs to JsonUtility, while what matters is that seven pose floats and six shot floats
            // travel as flat arrays and come out the same size and order.
            var roundTrip = JsonUtility.FromJson<ArenaSyncRequest>(syncRequest);
            Check(roundTrip != null && roundTrip.since == -1 && roundTrip.orb == "Collectible_3"
                  && roundTrip.pose != null && roundTrip.pose.Length == 7
                  && Mathf.Abs(roundTrip.pose[2] + 2.25f) < 0.0001f && Mathf.Abs(roundTrip.pose[5] - 4.2f) < 0.0001f
                  && roundTrip.shots != null && roundTrip.shots.Length == 6
                  && Mathf.Abs(roundTrip.shots[3] - 40f) < 0.0001f,
                  "sync body survives the round trip", ref failures);

            if (failures == 0) Debug.Log("[LobbyClientCheck] SUCCESS: all lobby JSON mappings verified.");
            else Debug.LogError($"[LobbyClientCheck] FAILED: {failures} mapping(s).");
        }

        private static void Check(bool condition, string label, ref int failures)
        {
            if (condition)
            {
                Debug.Log($"[LobbyClientCheck] ok  {label}");
                return;
            }
            failures++;
            Debug.LogError($"[LobbyClientCheck] FAIL  {label}");
        }
    }
}
