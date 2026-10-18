// Neon Relay lobby registry and arena relay.
//
// Plain node:http on purpose: the box already runs other services, so the deploy should be one file
// and one unit rather than an npm tree. It tracks who is in which lobby, who is allowed to start the
// match, and once the match has started it carries the arena state itself.
//
// The arena rides on this same HTTP surface rather than on a game port because both players sit behind
// their own NAT: neither can be connected to. Everyone instead reaches out to a host that can be
// reached, and the box fans each player's pose out to the others. It costs one extra hop of latency
// and buys a match that works from any two ISPs.
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';

const PORT = Number(process.env.PORT ?? 8791);
const MAX_LOBBIES = Number(process.env.MAX_LOBBIES ?? 200);
const MEMBER_TIMEOUT_S = Number(process.env.MEMBER_TIMEOUT_S ?? 15);
const EMPTY_GRACE_S = Number(process.env.EMPTY_GRACE_S ?? 60);
const LOBBY_TTL_S = Number(process.env.LOBBY_TTL_S ?? 6 * 3600);
const MAX_BODY_BYTES = 8192;
const NAME_LIMIT = 24;

// Arena relay limits. A pose is seven floats, so a sync body stays far under the request cap even when
// a player fires a burst; the event window only has to be longer than a second of polling.
const POSE_FLOATS = 7;
const SHOT_FLOATS = 6;
const MAX_SHOTS_PER_SYNC = 4;
const MAX_EVENT_WINDOW = 64;
const POSE_COORD_LIMIT = 2000;
// A client syncs ten times a second, so anything older than this is a client that has stopped talking
// rather than a slow one.
const STALE_POSE_MS = Number(process.env.STALE_POSE_MS ?? 1200);

const lobbies = new Map();

function cleanName(value, fallback) {
  if (typeof value !== 'string') return fallback;
  // Control characters would let a lobby name smuggle a newline into another player's list.
  const text = value.replace(/[\u0000-\u001f\u007f]/g, '').trim().replace(/\s+/g, ' ');
  return text.length === 0 ? fallback : text.slice(0, NAME_LIMIT);
}

function idOf(value) {
  return typeof value === 'string' && /^[0-9a-f-]{8,64}$/i.test(value) ? value.slice(0, 64) : null;
}

function newMatch() {
  return {
    // clientId -> { pose, name, at }. A pose is [x, y, z, yaw, pitch, speed, grounded]; one flat array
    // because that is exactly what the client reads out of its transform and what it writes back.
    poses: new Map(),
    events: [],
    seq: 0,
    // Orb ids the match has already consumed, in claim order. Every sync carries the whole set, so a
    // player that missed a reply still converges on the same arena as everyone else.
    orbs: [],
  };
}

function cleanPose(value) {
  if (!Array.isArray(value) || value.length < POSE_FLOATS) return null;
  const pose = new Array(POSE_FLOATS);
  for (let i = 0; i < POSE_FLOATS; i++) {
    const n = Number(value[i]);
    if (!Number.isFinite(n)) return null;
    // Two kilometres is well past the arena; anything beyond that is a corrupt client, not a player.
    if (Math.abs(n) > POSE_COORD_LIMIT) return null;
    // Three decimals is a millimetre. Any finer and the payload grows for nothing.
    pose[i] = Math.round(n * 1000) / 1000;
  }
  return pose;
}

function cleanShot(value) {
  if (!Array.isArray(value) || value.length < SHOT_FLOATS) return null;
  const shot = new Array(SHOT_FLOATS);
  for (let i = 0; i < SHOT_FLOATS; i++) {
    const n = Number(value[i]);
    if (!Number.isFinite(n) || Math.abs(n) > POSE_COORD_LIMIT) return null;
    shot[i] = Math.round(n * 1000) / 1000;
  }
  return shot;
}

function cleanOrbId(value) {
  return typeof value === 'string' && /^Collectible_[0-9]{1,3}$/.test(value) ? value : null;
}

function prune() {
  const now = Date.now();
  for (const [id, lobby] of lobbies) {
    for (const [clientId, member] of lobby.members) {
      if (now - member.lastSeen > MEMBER_TIMEOUT_S * 1000) {
        lobby.members.delete(clientId);
        lobby.match?.poses.delete(clientId);
      }
    }
    // A pose older than this belongs to a player whose client stopped mid-tick: frozen legs in front
    // of everyone else is worse than them vanishing.
    if (lobby.match) {
      for (const [clientId, pose] of lobby.match.poses) {
        if (now - pose.at > STALE_POSE_MS) lobby.match.poses.delete(clientId);
      }
    }
    // A started lobby stays until the TTL so late joiners still learn the match exists; an empty one
    // is just a stale row in somebody else's list.
    const emptyTooLong = lobby.members.size === 0 && now - lobby.lastActivity > EMPTY_GRACE_S * 1000;
    if (emptyTooLong || now - lobby.createdAt > LOBBY_TTL_S * 1000) lobbies.delete(id);
  }
}

function membersOf(lobby) {
  return [...lobby.members.values()]
    .sort((a, b) => a.joinedAt - b.joinedAt)
    .map((m) => ({ clientId: m.clientId, name: m.name, isHost: m.clientId === lobby.hostId }));
}

function summary(lobby) {
  return {
    id: lobby.id,
    name: lobby.name,
    players: lobby.members.size,
    maxPlayers: lobby.maxPlayers,
    started: lobby.started,
    hostName: lobby.members.get(lobby.hostId)?.name ?? '',
  };
}

function roomState(lobby) {
  return {
    id: lobby.id,
    name: lobby.name,
    hostClientId: lobby.hostId,
    started: lobby.started,
    startedAt: lobby.startedAt,
    maxPlayers: lobby.maxPlayers,
    players: membersOf(lobby),
  };
}

function addMember(lobby, clientId, nickname, now) {
  const member = lobby.members.get(clientId);
  if (member) {
    member.lastSeen = now;
    member.name = nickname;
    return { ok: true };
  }
  if (lobby.started) return { ok: false, error: 'match already started' };
  if (lobby.members.size >= lobby.maxPlayers) return { ok: false, error: 'lobby full' };
  lobby.members.set(clientId, { clientId, name: nickname, joinedAt: now, lastSeen: now });
  return { ok: true };
}

function send(res, code, body) {
  const text = JSON.stringify(body);
  res.writeHead(code, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(text),
    'Cache-Control': 'no-store',
  });
  res.end(text);
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        req.destroy();
        resolve(null);
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      if (size > MAX_BODY_BYTES) { resolve(null); return; }
      try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}')); }
      catch { resolve(null); }
    });
    req.on('error', () => resolve(null));
  });
}

function createLobby(body, hostClient, now) {
  if (lobbies.size >= MAX_LOBBIES) return { ok: false, code: 503, error: 'too many lobbies' };

  // JSON clients send every field, so an absent one arrives as 0 rather than undefined.
  const maxRaw = Number(body?.maxPlayers);
  const maxPlayers = Number.isFinite(maxRaw) && maxRaw > 0 ? Math.max(2, Math.min(8, Math.round(maxRaw))) : 4;
  const lobby = {
    id: randomUUID(),
    name: cleanName(body?.lobbyName, 'Untitled lobby'),
    hostId: hostClient,
    maxPlayers,
    started: false,
    startedAt: null,
    createdAt: now,
    lastActivity: now,
    members: new Map(),
    // Only a started match carries arena state; a room still being filled has nothing to relay.
    match: null,
  };
  addMember(lobby, hostClient, cleanName(body?.nickname, 'Host'), now);
  lobbies.set(lobby.id, lobby);
  return { ok: true, lobby };
}

/**
 * One round trip of the arena: the caller hands over its own pose, the shots fired since the last
 * call and any orb it walked into, and takes back everybody else's poses plus the events it has not
 * seen yet. Requests and replies travel together because a player behind NAT can only ever speak
 * first.
 */
function syncMatch(lobby, clientId, body, now) {
  const match = lobby.match;
  const nickname = cleanName(body?.nickname, 'Player');

  const pose = cleanPose(body?.pose);
  if (pose) match.poses.set(clientId, { pose, name: nickname, at: now });

  // Shots arrive as one flat array, six numbers per round, because that is what a client can hand to
  // Unity's JsonUtility: it will not serialize an array of arrays.
  const shots = Array.isArray(body?.shots) ? body.shots : [];
  for (let i = 0; i + SHOT_FLOATS <= Math.min(shots.length, MAX_SHOTS_PER_SYNC * SHOT_FLOATS); i += SHOT_FLOATS) {
    const shot = cleanShot(shots.slice(i, i + SHOT_FLOATS));
    if (!shot) continue;
    match.seq += 1;
    match.events.push({ seq: match.seq, by: clientId, k: 'shot', v: shot });
  }

  const orb = cleanOrbId(body?.orb);
  // First claim wins and the whole set is replayed to everyone on every sync, so a dropped reply
  // cannot leave two players holding the same pickup.
  if (orb && !match.orbs.includes(orb)) match.orbs.push(orb);

  if (Array.isArray(match.events) && match.events.length > MAX_EVENT_WINDOW) {
    match.events.splice(0, match.events.length - MAX_EVENT_WINDOW);
  }

  // `since` is the highest event number the caller has already applied. -1 is the only value that can
  // mean "I have no cursor, do not dump history on me", because 0 is a legitimate fresh-match cursor.
  const since = Number(body?.since);
  const cursor = Number.isFinite(since) ? Math.max(-1, Math.min(since, match.seq)) : -1;
  const peers = [];
  for (const [id, entry] of match.poses) {
    if (id === clientId) continue;
    if (now - entry.at > STALE_POSE_MS) continue;
    peers.push({ id, name: entry.name, pose: entry.pose, age: Math.round((now - entry.at) / 10) / 100 });
  }

  // The caller's own shots are excluded: it already played them locally when it pulled the trigger.
  const events = cursor >= 0
    ? match.events.filter((event) => event.seq > cursor && event.by !== clientId)
    : [];

  return {
    ok: true,
    seq: match.seq,
    startedAt: lobby.startedAt,
    peers,
    events,
    orbs: match.orbs,
  };
}

const ROOM = /^\/lobbies\/([0-9a-f-]{8,64})(?:\/([a-z]+))?$/;

const server = createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host ?? 'localhost'}`);
  const path = url.pathname.replace(/\/+$/, '') || '/';
  const now = Date.now();

  if (path === '/healthz' && req.method === 'GET') {
    send(res, 200, { ok: true, lobbies: lobbies.size, time: now });
    return;
  }

  if (path === '/lobbies') {
    if (req.method === 'GET') {
      send(res, 200, { lobbies: [...lobbies.values()].map(summary).sort((a, b) => b.players - a.players) });
      return;
    }
    if (req.method === 'POST') {
      const body = await readBody(req);
      const host = idOf(body?.clientId);
      if (!host) { send(res, 400, { error: 'clientId required' }); return; }
      const result = createLobby(body, host, now);
      if (!result.ok) { send(res, result.code, { error: result.error }); return; }
      send(res, 200, roomState(result.lobby));
      return;
    }
    send(res, 405, { error: 'method not allowed' });
    return;
  }

  const room = ROOM.exec(path);
  if (!room) { send(res, 404, { error: 'not found' }); return; }
  const lobby = lobbies.get(room[1]);
  const action = room[2] ?? '';

  // Heartbeats are a GET with ?c=; every mutation posts a body, read exactly once here.
  const body = req.method === 'GET' ? {} : await readBody(req);
  if (body === null) { send(res, 400, { error: 'bad body' }); return; }
  const player = idOf(url.searchParams.get('c')) ?? idOf(body?.clientId);

  if (req.method === 'GET') {
    if (!lobby) { send(res, 404, { error: 'lobby gone' }); return; }
    const member = player ? lobby.members.get(player) : null;
    if (member) member.lastSeen = now;
    send(res, 200, roomState(lobby));
    return;
  }

  if (req.method !== 'POST') { send(res, 405, { error: 'method not allowed' }); return; }
  if (!player) { send(res, 400, { error: 'clientId required' }); return; }

  if (action === 'join') {
    if (!lobby) { send(res, 404, { error: 'lobby gone' }); return; }
    const result = addMember(lobby, player, cleanName(body.nickname, 'Player'), now);
    if (!result.ok) { send(res, 409, { error: result.error }); return; }
    lobby.lastActivity = now;
    send(res, 200, roomState(lobby));
    return;
  }

  if (action === 'leave') {
    if (lobby) {
      lobby.members.delete(player);
      lobby.match?.poses.delete(player);
      lobby.lastActivity = now;
      // The host walking away ends the room; nobody should be able to join an empty shell.
      if (lobby.hostId === player && !lobby.started) lobbies.delete(lobby.id);
    }
    send(res, 200, { ok: true });
    return;
  }

  if (action === 'rename') {
    const nickname = cleanName(body.nickname, null);
    if (!nickname) { send(res, 400, { error: 'nickname required' }); return; }
    if (lobby?.members.has(player)) {
      lobby.members.get(player).name = nickname;
      lobby.lastActivity = now;
    }
    send(res, 200, { ok: true, nickname });
    return;
  }

  if (action === 'start') {
    if (!lobby) { send(res, 404, { error: 'lobby gone' }); return; }
    if (lobby.hostId !== player) { send(res, 403, { error: 'only the host can start' }); return; }
    lobby.started = true;
    lobby.startedAt = now;
    lobby.lastActivity = now;
    // Starting again is a new match: a leftover corpse from the previous round would be replicated
    // into a fresh arena.
    lobby.match = newMatch();
    send(res, 200, roomState(lobby));
    return;
  }

  if (action === 'sync') {
    if (!lobby) { send(res, 404, { error: 'lobby gone' }); return; }
    if (!lobby.started || !lobby.match) { send(res, 409, { error: 'match not started' }); return; }
    // Only people in the room may push state; otherwise a stale client could keep a peer alive.
    if (!lobby.members.has(player)) { send(res, 403, { error: 'not in this lobby' }); return; }
    lobby.members.get(player).lastSeen = now;
    lobby.lastActivity = now;
    send(res, 200, syncMatch(lobby, player, body, now));
    return;
  }

  send(res, 404, { error: 'not found' });
});

setInterval(prune, 5000).unref();

server
  .listen(PORT, '0.0.0.0', () => {
    console.log(`lobby registry listening on :${PORT}, arena relay on the same port`);
  })
  .on('error', (error) => {
    console.error('listen failed', error.message);
    process.exit(1);
  });
