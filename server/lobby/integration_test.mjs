// Drives the live lobby registry with byte-for-byte the payloads Unity's JsonUtility produces,
// so an integration bug shows up here rather than in a player log.
const BASE = process.argv[2] ?? 'http://166.1.144.235:8791';

// JsonUtility emits every field, so the unused ones are empty string and zero.
const bodyOf = (clientId, nickname, lobbyName = '', maxPlayers = 0) =>
  JSON.stringify({ clientId, nickname, lobbyName, maxPlayers });

// A sync body is one flat pose plus a flat shot feed: JsonUtility cannot serialize an array of arrays.
const syncOf = (clientId, nickname, pose, shots = [], since = -1, orb = '') =>
  JSON.stringify({ clientId, nickname, since, pose, shots, orb });

async function call(method, path, body) {
  const res = await fetch(BASE + path, {
    method,
    body,
    headers: body ? { 'Content-Type': 'application/json' } : {},
  });
  const text = await res.text();
  return { code: res.status, json: text.startsWith('{') || text.startsWith('[') ? JSON.parse(text) : text };
}

function assert(condition, label) {
  console.log(`${condition ? 'PASS' : 'FAIL'}  ${label}`);
  if (!condition) process.exitCode = 1;
}

const host = 'a1b2c3d4e5f60718293a4b5c6d7e8f01';
const guest = '0f1e2d3c4b5a69780706050403020112';
const stranger = 'deadbeefdeadbeefdeadbeefdeadbeef00';

const list0 = await call('GET', '/lobbies');
assert(list0.code === 200 && Array.isArray(list0.json.lobbies), 'GET /lobbies returns a list');

const created = await call('POST', '/lobbies', bodyOf(host, 'NeonFox42', 'Test Arena', 0));
assert(created.code === 200 && created.json.maxPlayers === 4,
  `POST /lobbies with maxPlayers 0 defaults to 4 (got ${created.json.maxPlayers})`);
assert(created.json.connect === undefined, 'no game port is handed out anymore');
const id = created.json.id;

const list1 = await call('GET', '/lobbies');
assert(list1.json.lobbies.some((l) => l.id === id && l.name === 'Test Arena' && l.players === 1),
  'created lobby shows in everyone else list');

const joined = await call('POST', `/lobbies/${id}/join`, bodyOf(guest, 'StaticRaven17'));
assert(joined.code === 200 && joined.json.players.length === 2, 'guest joined, roster has 2');

const heartbeat = await call('GET', `/lobbies/${id}?c=${host}`);
assert(heartbeat.code === 200 && heartbeat.json.players.length === 2, 'GET ?c= is a heartbeat');

const renamed = await call('POST', `/lobbies/${id}/rename`, bodyOf(guest, 'RenamedName'));
assert(renamed.code === 200 && renamed.json.nickname === 'RenamedName', 'rename accepted');

const guestStart = await call('POST', `/lobbies/${id}/start`, bodyOf(guest, 'RenamedName'));
assert(guestStart.code === 403, `non-host start refused (${guestStart.code})`);

const late = await call('POST', `/lobbies/${id}/join`, bodyOf(stranger, 'Late'));
assert(late.code === 200, 'third player can join an open lobby');

const early = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [0, 0, 0, 0, 0, 0, 1]));
assert(early.code === 409, `sync before the match starts refused (${early.code})`);

const started = await call('POST', `/lobbies/${id}/start`, bodyOf(host, 'NeonFox42'));
assert(started.code === 200 && started.json.started === true, 'host starts the match');

const blocked = await call('POST', `/lobbies/${id}/join`, bodyOf('feedfacefeedfacefeedfacefeedface', 'TooLate'));
assert(blocked.code === 409, `join after start refused (${blocked.code})`);

// --- arena relay ---

const outsider = await call('POST', `/lobbies/${id}/sync`, syncOf('cafebabecafebabecafebabecafebabe', 'Ghost', [1, 2, 3, 4, 5, 6, 7]));
assert(outsider.code === 403, `sync from a client outside the room refused (${outsider.code})`);

const first = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [1.5, 1.1, -2.25, 90.5, -3.25, 4.2, 1]));
assert(first.code === 200 && Array.isArray(first.json.peers), 'host syncs a pose');
assert(first.json.peers.length === 0, 'the caller never sees itself in the peer list');
assert(Array.isArray(first.json.orbs), 'sync answers the authoritative orb set');

const junk = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [1, 2], [], first.json.seq, 'not-an-orb'));
assert(junk.code === 200, 'a short pose and a bad orb id are ignored, not rejected');

const second = await call('POST', `/lobbies/${id}/sync`, syncOf(guest, 'RenamedName', [0.125, 1.0, 0.5, -12, 0, 0, 1]));
assert(second.json.peers.some((p) => p.id === host && p.pose[0] === 1.5 && p.name === 'NeonFox42'),
  'the host pose is echoed to the guest, rounded to a millimetre');
assert(second.json.events.length === 0, 'a client with no cursor gets no history');

// A shot is six floats: muzzle xyz then endpoint xyz. Two rounds in one sync.
const burst = await call('POST', `/lobbies/${id}/sync`,
  syncOf(host, 'NeonFox42', [1.5, 1.1, -2.25, 90.5, -3.25, 0, 1],
    [1.5, 1.3, -2.0, 40.0, 1.1, -20.0, 1.5, 1.3, -2.0, 41.25, 1.1, -21.5], second.json.seq));
assert(burst.code === 200, 'host feeds two shots');

const heard = await call('POST', `/lobbies/${id}/sync`, syncOf(guest, 'RenamedName', [0.125, 1.0, 0.5, -12, 0, 0, 1], [], burst.json.seq - 2));
const shots = heard.json.events.filter((e) => e.k === 'shot');
assert(shots.length === 2 && shots[0].v.length === 6 && shots[0].v[3] === 40,
  `guest replays both remote tracers (${shots.length})`);
assert(heard.json.events.every((e) => e.by === host), 'events carry the shooter so the client can skip its own');

const drained = await call('POST', `/lobbies/${id}/sync`, syncOf(guest, 'RenamedName', [0.125, 1.0, 0.5, -12, 0, 0, 1], [], heard.json.seq));
assert(drained.json.events.length === 0, 'a cursor that has caught up replays nothing');

// The third player never took part; it joins the relay while shots are already in the window.
const latecomer = await call('POST', `/lobbies/${id}/sync`, syncOf(stranger, 'Late', [5.5, 1.0, 5.5, 180, 0, 0, 1]));
assert(latecomer.json.events.length === 0 && latecomer.json.seq >= 2,
  'a player with no cursor is not dumped the history but still learns where the cursor starts');
assert(latecomer.json.peers.length === 2, 'both active players are echoed to the latecomer');

const departed = await call('POST', `/lobbies/${id}/leave`, bodyOf(stranger, 'Late'));
assert(departed.code === 200, 'latecomer leaves');

const claimed = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [1.5, 1.1, -2.25, 90.5, 0, 0, 1], [], drained.json.seq, 'Collectible_3'));
assert(claimed.json.orbs.includes('Collectible_3'), 'host claim lands in the orb set');

const race = await call('POST', `/lobbies/${id}/sync`, syncOf(guest, 'RenamedName', [0.125, 1.0, 0.5, -12, 0, 0, 1], [], claimed.json.seq, 'Collectible_3'));
assert(race.json.orbs.filter((o) => o === 'Collectible_3').length === 1, 'a contested orb is claimed once, by whoever was first');

const both = await call('POST', `/lobbies/${id}/sync`, syncOf(guest, 'RenamedName', [0.125, 1.0, 0.5, -12, 0, 0, 1], [], race.json.seq, 'Collectible_7'));
assert(both.json.orbs.includes('Collectible_3') && both.json.orbs.includes('Collectible_7'),
  'the whole orb set is replayed so a dropped reply still converges');

const hostAfter = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [1.5, 1.1, -2.25, 90.5, 0, 0, 1], [], both.json.seq));
assert(hostAfter.json.peers.length === 1, 'leaving drops a player pose out of the relay');

// A host that presses START again is starting a second round, not resuming the first: a leftover
// corpse or a spent orb shelf would be replicated straight into the new arena.
const restarted = await call('POST', `/lobbies/${id}/start`, bodyOf(host, 'NeonFox42'));
assert(restarted.code === 200 && restarted.json.startedAt !== started.json.startedAt,
  'a second start hands out a new match clock');

const resetHost = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [1.5, 1.1, -2.25, 90, 0, 0, 1]));
assert(resetHost.json.seq === 0, 'the event cursor starts over with the match');
assert(resetHost.json.orbs.length === 0, 'the orb shelf comes back full for the new round');

const resetGuest = await call('POST', `/lobbies/${id}/sync`, syncOf(guest, 'RenamedName', [0.125, 1.0, 0.5, 0, 0, 0, 1], [], 999));
assert(resetGuest.json.peers.length === 1, 'poses were wiped: nobody walks into a fresh arena already standing somewhere');
assert(resetGuest.json.events.length === 0, 'a cursor from the previous round replays nothing');

const left = await call('POST', `/lobbies/${id}/leave`, bodyOf(host, 'NeonFox42'));
assert(left.code === 200, 'host leave accepted');

const orphan = await call('POST', `/lobbies/${id}/sync`, syncOf(host, 'NeonFox42', [9, 9, 9, 9, 9, 9, 9], [], hostAfter.json.seq));
assert(orphan.code === 403, `a client that left cannot keep pushing its pose (${orphan.code})`);

const gone = await call('POST', `/lobbies/${nonexistent(id)}/leave`, bodyOf(host, 'NeonFox42'));
assert(gone.code === 200, 'leaving an unknown lobby is idempotent, not an error');

const missing = await call('GET', `/lobbies/${nonexistent(id)}?c=${host}`);
assert(missing.code === 404, `reading an unknown lobby answers 404 (${missing.code})`);

function nonexistent(id) { return id.replace(/^./, (c) => (c === 'f' ? 'e' : 'f')); }

console.log(process.exitCode ? 'INTEGRATION FAIL' : 'INTEGRATION OK');
