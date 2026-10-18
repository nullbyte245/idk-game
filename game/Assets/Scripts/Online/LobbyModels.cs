using System;
using UnityEngine;

namespace Game.Online
{
    /// <summary>What the lobby list screen is currently watching; decides what LobbyService polls.</summary>
    public enum LobbyBrowseMode { None, List, Room }

    [Serializable]
    public sealed class LobbySummary
    {
        public string id;
        public string name;
        public int players;
        public int maxPlayers;
        public bool started;
        public string hostName;
    }

    [Serializable]
    public sealed class LobbyListResponse
    {
        public LobbySummary[] lobbies;
    }

    [Serializable]
    public sealed class LobbyPlayerInfo
    {
        public string clientId;
        public string name;
        public bool isHost;
    }

    [Serializable]
    public sealed class LobbyRoomState
    {
        public string id;
        public string name;
        public string hostClientId;
        public bool started;
        public long startedAt;
        public int maxPlayers;
        public LobbyPlayerInfo[] players;
    }

    /// <summary>One shape for every POST body: the registry ignores the fields an action does not use.</summary>
    [Serializable]
    public sealed class LobbyRequest
    {
        public string clientId;
        public string nickname;
        public string lobbyName;
        public int maxPlayers;

        public static LobbyRequest For(LobbyService lobby)
        {
            return new LobbyRequest { clientId = lobby.ClientId, nickname = lobby.Nickname };
        }
    }

    /// <summary>The registry answers every failure with {"error": "..."}; that text is what the UI shows.</summary>
    [Serializable]
    public sealed class LobbyErrorResponse
    {
        public string error;
    }

    /// <summary>Reply text plus a human-readable failure, so callers never parse HTTP themselves.</summary>
    public sealed class LobbyReply
    {
        public bool Ok;
        public int Code;
        public string Body;
        public string Error;

        public static LobbyReply Success(int code, string body) => new LobbyReply { Ok = true, Code = code, Body = body };
        public static LobbyReply Failure(int code, string error) => new LobbyReply { Ok = false, Code = code, Error = error };
    }
}
