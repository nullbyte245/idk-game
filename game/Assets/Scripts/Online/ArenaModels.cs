using System;
using UnityEngine;

namespace Game.Online
{
    /// <summary>
    /// Shapes of the arena relay. Every payload is flat float arrays because JsonUtility cannot
    /// serialize an array of arrays, and a pose or a tracer is exactly a handful of numbers anyway.
    ///
    /// Pose is [x, y, z, yaw, pitch, speed, grounded]; a shot is [muzzle xyz, endpoint xyz].
    /// </summary>
    [Serializable]
    public sealed class ArenaSyncRequest
    {
        public string clientId;
        public string nickname;

        /// <summary>Highest event number already applied. -1 means "no cursor, do not send history".</summary>
        public int since = -1;

        public float[] pose;
        public float[] shots;
        public string orb;
    }

    [Serializable]
    public sealed class ArenaPeerState
    {
        public string id;
        public string name;
        public float[] pose;
        public float age;
    }

    [Serializable]
    public sealed class ArenaEventDto
    {
        public int seq;
        public string by;
        public string k;
        public float[] v;
    }

    [Serializable]
    public sealed class ArenaSyncResponse
    {
        public bool ok;
        public int seq;
        public long startedAt;
        public ArenaPeerState[] peers;
        public ArenaEventDto[] events;
        public string[] orbs;
    }
}
