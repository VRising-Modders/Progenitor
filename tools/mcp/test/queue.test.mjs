import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createQueue, isReadOnlyOp, normalizeMods, normalizeSides, keyOf, revisionOf, describeChange, observationFromPing, declaresRevision, coreLine, answered, MAX_HISTORY } from '../queue.mjs';

const MIN = 60_000;

function rig() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-queue-'));
  let t = Date.parse('2026-10-10T12:00:00Z');
  const alive = new Set();
  const env = {};
  const q = createQueue({ file: path.join(dir, 'q.json'), now: () => t, alive: (pid) => alive.has(pid), host: 'test-host', env });
  return { q, alive, env, advance: (m) => { t += m * MIN; }, done: () => fs.rmSync(dir, { recursive: true, force: true }) };
}

const who = (name, pid) => ({ ai: 'claude', name, id: `id-${name}`, tool: 'mcp', pid });
const job = (sides, minutes = 20) => ({ sides, estimatedMinutes: minutes, progenitor: '0.1.0+abc1234', mods: ['Example Mod@1.2.0'], purpose: 'test' });

test('read-only ops never need a slot; changing ops do', () => {
  for (const op of ['ping', 'logs', 'query', 'get', 'inspect', 'status', 'bridge_users']) assert.ok(isReadOnlyOp(op, {}), op);
  for (const op of ['set', 'spawn', 'connect', 'reload', 'quit', 'command']) assert.ok(!isReadOnlyOp(op, {}), op);
  assert.ok(isReadOnlyOp('ghost', { action: 'list' }));
  assert.ok(isReadOnlyOp('disconnect', { statusOnly: true }));
  assert.ok(isReadOnlyOp('store', { mod: 'x', key: 'k' }));
  assert.ok(!isReadOnlyOp('store', { mod: 'x', key: 'k', value: 1 }));
  assert.ok(isReadOnlyOp('time', {}), 'time defaults to status');
  assert.ok(isReadOnlyOp('photo', {}), 'photo defaults to status');
  assert.ok(!isReadOnlyOp('time', { action: 'freeze' }));
  for (const op of ['versions', 'catalog_status', 'datetime_read', 'rejoin_read']) assert.ok(isReadOnlyOp(op, {}), op);
  assert.ok(isReadOnlyOp('mods_ui', { action: 'rects' }));
  assert.ok(isReadOnlyOp('mods_ui', { action: 'keys', op: 'conflict' }));
  assert.ok(!isReadOnlyOp('mods_ui', { action: 'keys', op: 'set' }));
  assert.ok(!isReadOnlyOp('mods_ui', { action: 'open' }));
  assert.ok(isReadOnlyOp('mods_ui', { action: 'menu', op: 'status' }));
  assert.ok(!isReadOnlyOp('mods_ui', { action: 'menu', op: 'open' }));
});

test('join requires versions and an estimate; mods parse as name@version', () => {
  const r = rig();
  try {
    assert.throws(() => r.q.join(who('A', 1), { sides: ['client'], estimatedMinutes: 10, mods: [] }), /progenitor_version is required/);
    assert.throws(() => r.q.join(who('A', 1), { sides: ['client'], estimatedMinutes: 10, progenitor: '0.1.0' }), /mods is required/);
    assert.throws(() => r.q.join(who('A', 1), { sides: ['client'], progenitor: '0.1.0', mods: [] }), /estimated_minutes/);
    assert.deepEqual(normalizeMods(['Example Mod@1.2.0', { name: 'B', version: '2' }]), [{ name: 'Example Mod', version: '1.2.0' }, { name: 'B', version: '2' }]);
    assert.throws(() => normalizeMods(['NoVersion']), /needs a version/);
    assert.deepEqual(normalizeSides(['both']), ['client', 'server']);
  } finally { r.done(); }
});

test('free side is granted at once; others wait in order with ETAs and versions shown', () => {
  const r = rig();
  try {
    r.alive.add(1); r.alive.add(2); r.alive.add(3); r.alive.add(4);
    const a = r.q.join(who('A', 1), job(['client', 'server'], 30));
    assert.match(a, /You hold the client\+server bridge/);
    assert.match(a, /versions: Progenitor 0\.1\.0\+abc1234; mods: Example Mod 1\.2\.0/);
    r.q.join(who('B', 2), job(['server'], 20));
    r.q.join(who('C', 3), job(['client'], 10));
    const d = r.q.join(who('D', 4), job(['server'], 5));
    assert.match(d, /running 0 min of est\. 30 \(about 30 min left/);
    assert.match(d, /1\. server: Claude session "B".*\n.*est\. 20 min; starts ~.* \(in 30 min\).*may check on the holder/);
    assert.match(d, /2\. client: Claude session "C".*\n.*est\. 10 min.*\(in 30 min\).*may check on the holder/);
    assert.match(d, /3\. server: Claude session "D".*\(you\).*\n.*est\. 5 min.*\(in 50 min\).*do not contact the holder/);
    assert.match(d, /reach: Claude Code session id-A/);
    assert.match(d, /MCP pid 1 alive on test-host/);
  } finally { r.done(); }
});

test('a holder that leaves hands each side to the next waiter; disjoint claims run side by side', () => {
  const r = rig();
  try {
    [1, 2, 3].forEach((p) => r.alive.add(p));
    r.q.join(who('A', 1), job(['client', 'server']));
    r.q.join(who('B', 2), job(['server']));
    r.q.join(who('C', 3), job(['client']));
    r.advance(5);
    r.q.leave(who('A', 1));
    const s = r.q.snapshot();
    assert.deepEqual(s.entries.map((e) => [e.who.name, e.state]), [['B', 'active'], ['C', 'active']]);
  } finally { r.done(); }
});

test('gate: MCP needs the slot for changing ops; scripts only respect another holder', () => {
  const r = rig();
  try {
    r.alive.add(1); r.alive.add(2);
    assert.match(r.q.gate(who('A', 1), 'server', 'spawn', {}), /bridge is free, but you have not claimed it/);
    assert.equal(r.q.gate(who('A', 1), 'server', 'spawn', {}, { requireSlot: false }), null);
    assert.equal(r.q.gate(who('A', 1), 'server', 'logs', {}), null);
    r.q.join(who('A', 1), job(['server']));
    assert.equal(r.q.gate(who('A', 1), 'server', 'spawn', {}), null);
    assert.match(r.q.gate(who('B', 2), 'server', 'spawn', {}, { requireSlot: false }), /held by Claude session "A"/);
    assert.equal(r.q.gate(who('B', 2), 'server', 'ping', {}), null);
    assert.match(r.q.gate(who('B', 2), 'client', 'connect', {}), /not claimed/);
    r.env.PROGENITOR_QUEUE = 'off';
    assert.equal(r.q.gate(who('B', 2), 'server', 'spawn', {}), null);
  } finally { r.done(); }
});

test('a script started by the holder counts as the holder (PROGENITOR_QUEUE_KEY)', () => {
  const r = rig();
  try {
    const holder = { ai: 'claude', name: 'H', id: 'h-1', tool: 'mcp', pid: 7 };
    r.alive.add(7);
    r.q.join(holder, job(['server']));
    const script = { ai: 'claude', name: 'Other', id: 'o-1', tool: 'call.mjs', pid: 99 };
    assert.match(r.q.gate(script, 'server', 'reload', {}, { requireSlot: false }), /held by/);
    r.env.PROGENITOR_QUEUE_KEY = keyOf(holder);
    assert.equal(r.q.gate(script, 'server', 'reload', {}, { requireSlot: false }), null);
  } finally { r.done(); }
});

test('scripts of the same AI get the benefit of the doubt when one side has no session ID; MCP does not', () => {
  const r = rig();
  try {
    const codexMcp = { ai: 'codex', name: '', id: '', tool: 'mcp', pid: 7 };
    r.alive.add(7);
    r.q.join(codexMcp, job(['client']));
    const codexShell = { ai: 'codex', name: '', id: 'thread-1', tool: 'call.mjs', pid: 50 };
    assert.equal(r.q.gate(codexShell, 'client', 'reload', {}, { requireSlot: false }), null);
    assert.match(r.q.gate({ ...codexShell, tool: 'mcp' }, 'client', 'reload', {}), /held by/);
    assert.match(r.q.gate({ ai: 'grok', id: 'g-1', tool: 'call.mjs' }, 'client', 'reload', {}, { requireSlot: false }), /held by/);
    // An unidentified session's CLI join and its call.mjs calls share one key.
    assert.equal(keyOf({ ai: 'grok', name: 'N', tool: 'queue.mjs', pid: 1 }), keyOf({ ai: 'grok', name: 'N', tool: 'call.mjs', pid: 2 }));
    assert.notEqual(keyOf({ ai: 'grok', name: 'N', tool: 'mcp', pid: 1 }), keyOf({ ai: 'grok', name: 'N', tool: 'mcp', pid: 2 }));
  } finally { r.done(); }
});

test('a fixed key keeps the slot when the identity changes later', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-queue-'));
  try {
    let fixed = null;
    const q = createQueue({ file: path.join(dir, 'q.json'), alive: () => true, host: 'h', env: {}, key: (w) => (fixed ??= keyOf(w)) });
    q.join(who('A', 1), job(['server']));
    assert.equal(q.gate({ ...who('A', 1), id: 'renamed', name: 'Renamed' }, 'server', 'spawn', {}), null);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('exited process releases at once; idle holder released at 15 min with waiters, 30 min without', () => {
  const r = rig();
  try {
    r.alive.add(1); r.alive.add(2);
    r.q.join(who('A', 1), job(['server'], 60));
    r.q.join(who('B', 2), job(['server']));
    r.alive.delete(1);
    assert.equal(r.q.snapshot().entries[0].who.name, 'B');
    assert.equal(r.q.snapshot().entries[0].state, 'active');

    r.alive.add(3);
    r.q.join(who('C', 3), job(['client'], 120));
    r.advance(29);
    assert.ok(r.q.snapshot().entries.some((e) => e.who.name === 'C'), 'alone and idle 29 min: kept');
    r.advance(1);
    assert.ok(!r.q.snapshot().entries.some((e) => e.who.name === 'C'), 'idle 30 min: released');

    r.alive.add(4);
    r.q.join(who('D', 4), job(['client'], 120)); // active
    r.alive.add(5);
    r.q.join(who('E', 5), job(['client'])); // waiting
    r.advance(14);
    r.q.status(who('E', 5));
    assert.equal(r.q.snapshot().entries.find((e) => e.who.name === 'D')?.state, 'active');
    r.advance(1);
    const s = r.q.snapshot();
    assert.ok(!s.entries.some((e) => e.who.name === 'D'), 'idle 15 min with a waiter: released');
    assert.equal(s.entries.find((e) => e.who.name === 'E').state, 'active');
    assert.match(s.events.map((e) => e.text).join('\n'), /released the client bridge: idle 15 min with sessions waiting/);
  } finally { r.done(); }
});

test('only the next two waiters may probe or evict; evict needs idle and overrun', () => {
  const r = rig();
  try {
    [1, 2, 3, 4].forEach((p) => r.alive.add(p));
    r.q.join(who('A', 1), job(['server'], 20));
    r.q.join(who('B', 2), job(['server']));
    r.q.join(who('C', 3), job(['server']));
    r.q.join(who('D', 4), job(['server']));
    assert.throws(() => r.q.probe(who('D', 4)), /position 3; only the next 2/);
    const p = r.q.probe(who('B', 2), 'are you done?');
    assert.match(p, /Holder of server: Claude session "A"/);
    assert.match(p, /message it at: Claude Code session id-A/);
    assert.match(p, /Not evictable/);
    assert.match(r.q.status(who('D', 4)), /checked by Claude session "B" .*: are you done\?/);
    assert.throws(() => r.q.evict(who('C', 3), 'idle'), /not evictable/);
    r.advance(14); // B and C keep checking in, A is quiet; 14 min idle < 15 min auto-release
    r.q.status(who('B', 2)); r.q.status(who('C', 3)); r.q.status(who('D', 4));
    assert.throws(() => r.q.evict(who('C', 3), 'idle'), /still inside its estimate/);
    assert.throws(() => r.q.evict(who('D', 4), 'x'), /position 3/);
    r.q.update(who('A', 1), { estimatedMinutes: 1 }); // A: "1 more minute" -> also counts as activity
    r.advance(11);
    r.q.status(who('B', 2)); r.q.status(who('C', 3)); r.q.status(who('D', 4));
    const out = r.q.evict(who('C', 3), 'no activity for 11 min, past estimate');
    assert.match(out, /Evicted Claude session "A"/);
    assert.equal(r.q.snapshot().entries[0].who.name, 'B');
    assert.equal(r.q.snapshot().entries[0].state, 'active');
  } finally { r.done(); }
});

test('reminder tells a holder about overrun and waiters', () => {
  const r = rig();
  try {
    r.alive.add(1); r.alive.add(2);
    r.q.join(who('A', 1), job(['client'], 10));
    assert.equal(r.q.reminder(who('A', 1)), '');
    r.q.join(who('B', 2), job(['client']));
    r.advance(12);
    assert.match(r.q.reminder(who('A', 1)), /estimate ran out 2 min ago.*1 waiting: Claude session "B" .*needs client for 20 min.*Leave as soon as your live work is done\./);
  } finally { r.done(); }
});

const ping = (gen, rev, plugins = []) => ({
  progenitor: '2026.10.3',
  images: [
    { name: 'Core', sha256: `core${gen}`.padEnd(64, '0'), generation: gen, version: `2026.10.3+${rev}` },
    { name: 'Bootstrap', sha256: 'b'.repeat(64), version: '2026.10.1+1111111' },
    ...plugins.map(([name, version]) => ({ name, sha256: `${name}${version}`, version })),
  ],
});

test('revisionOf and describeChange read stamped versions', () => {
  assert.deepEqual(revisionOf('2026.10.3+abc1234'), { rev: 'abc1234', dirty: false });
  assert.deepEqual(revisionOf('2026.10.3+abc1234.dirty'), { rev: 'abc1234', dirty: true });
  assert.equal(revisionOf('2026.10.3'), null);
  const a = observationFromPing(ping(31, 'aaaaaaa', [['Progenitor.Dev', '2026.10.2+ddddddd']]), 0, 'x');
  const b = observationFromPing(ping(32, 'bbbbbbb', [['Progenitor.Dev', '2026.10.2+ddddddd'], ['Example Mod', '1.2.0']]), 0, 'x');
  assert.equal(describeChange(a, a), '');
  assert.equal(describeChange(null, a), 'first seen');
  assert.equal(describeChange(a, b), 'Core 2026.10.3+aaaaaaa gen 31 -> 2026.10.3+bbbbbbb gen 32; +Example Mod 1.2.0');
});

test('LOADED NOW shows the observed Core with the branch note; swaps land in the holder connection', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-queue-'));
  let t = Date.parse('2026-10-10T12:00:00Z');
  try {
    const q = createQueue({ file: path.join(dir, 'q.json'), now: () => t, alive: () => true, host: 'h', env: {}, relate: (v) => `relation of ${v}` });
    q.observe(who('X', 9), 'client', ping(31, 'aaaaaaa'));
    q.join(who('A', 1), job(['client'], 30));
    t += 5 * MIN;
    q.observe(who('A', 1), 'client', ping(32, 'bbbbbbb', [['Example Mod', '1.2.0']]));
    t += 1 * MIN;
    const st = q.status(who('B', 2));
    assert.match(st, /LOADED NOW/);
    assert.match(st, /client: Core 2026\.10\.3\+bbbbbbb gen 32 \(sha core3200\); seen 1 min ago via Claude session "A"/);
    assert.match(st, /relation of 2026\.10\.3\+bbbbbbb/);
    assert.match(st, /plugins: Example Mod 1\.2\.0/);
    assert.match(st, /note: declared Progenitor 0\.1\.0\+abc1234, but the client runs 2026\.10\.3\+bbbbbbb/);
    q.leave(who('A', 1));
    const h = q.history(who('B', 2));
    assert.match(h, /Last 2 of 2 connections/);
    assert.match(h, /client: Claude session "A".*6 min of est\. 30/);
    assert.match(h, /\(left\)/);
    assert.match(h, /declared: Progenitor 0\.1\.0\+abc1234; mods: Example Mod 1\.2\.0/);
    assert.match(h, /client: loaded at start: Core 2026\.10\.3\+aaaaaaa gen 31/);
    assert.match(h, /client: Core 2026\.10\.3\+aaaaaaa gen 31 -> 2026\.10\.3\+bbbbbbb gen 32; \+Example Mod 1\.2\.0; now Core/);
    assert.match(h, /outside the queue/, 'a change seen with no holder is its own record');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('history keeps the last 50 connections and records how each ended', () => {
  const r = rig();
  try {
    r.alive.add(1);
    for (let i = 0; i < MAX_HISTORY + 5; i++) { r.q.join(who('A', 1), job(['server'], 5)); r.advance(1); r.q.leave(who('A', 1)); }
    const s = r.q.snapshot();
    assert.equal(s.history.length, MAX_HISTORY);
    assert.ok(s.history.every((h) => h.end === 'left'));
    r.alive.add(2); r.alive.add(3);
    r.q.join(who('B', 2), job(['server'], 60));
    r.q.join(who('C', 3), job(['server']));
    r.alive.delete(2);
    assert.equal(r.q.snapshot().history.at(-2).end, 'process exited');
    assert.equal(r.q.snapshot().history.at(-1).endedAt, null, 'C now holds');
  } finally { r.done(); }
});

test('declared short shas match the stamped 10-character revision', () => {
  assert.ok(declaresRevision('0.1.0+abc1234', 'abc1234def'));
  assert.ok(declaresRevision('main at abc1234def0', 'abc1234def'));
  assert.ok(!declaresRevision('0.1.0+abc1235', 'abc1234def'));
  assert.ok(!declaresRevision('0.1.0', 'abc1234def'));
});

test('extra instances are ignored; an unchanged ping writes at most once a minute; a trimmed hold gets a record', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-queue-'));
  const file = path.join(dir, 'q.json');
  let t = Date.parse('2026-10-10T12:00:00Z');
  try {
    const q = createQueue({ file, now: () => t, alive: () => true, host: 'h', env: {} });
    q.observe(who('X', 9), 'client', { ...ping(3, 'ccccccc'), instance: 'client-1' });
    assert.equal(q.snapshot().observed.client, undefined, 'extra client not recorded as the client');
    q.observe(who('X', 9), 'client', ping(31, 'aaaaaaa'));
    const m1 = fs.statSync(file).mtimeMs;
    const before = fs.readFileSync(file, 'utf8');
    t += 10_000;
    q.observe(who('X', 9), 'client', ping(31, 'aaaaaaa'));
    assert.equal(fs.readFileSync(file, 'utf8'), before, 'unchanged within a minute: no write');
    assert.ok(m1 > 0);

    q.join(who('A', 1), job(['client'], 30));
    const s = JSON.parse(fs.readFileSync(file, 'utf8'));
    delete s.entries[0].historyId; // as if its record was trimmed
    fs.writeFileSync(file, JSON.stringify(s));
    q.observe(who('A', 1), 'client', ping(32, 'bbbbbbb'));
    const last = q.snapshot().history.at(-1);
    assert.equal(last.who.name, 'A');
    assert.equal(last.endedAt, null);
    assert.match(last.builds.at(-1).change, /gen 31 -> .* gen 32/);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('an older Dev bridge without image versions never shows ping.progenitor as the Core version', () => {
  const old = observationFromPing({ progenitor: '2026.10.167.2', images: [{ name: 'Core', sha256: 'a'.repeat(64), generation: 47 }] }, 0, 'x');
  assert.equal(coreLine(old), 'Core version not reported (Dev bridge older than 2026.10.171) gen 47 (sha aaaaaaaa)');
  assert.match(coreLine(observationFromPing({ progenitor: '1' }, 0, 'x')), /^Core unknown/);
});

test('mailbox: next-two waiters message the holder; it is delivered once; the holder replies to the asker', () => {
  const r = rig();
  try {
    [1, 2, 3, 4].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client', 'server'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    r.q.join(who('C', 3), job(['server']));
    r.q.join(who('D', 4), job(['client']));
    assert.throws(() => r.q.message(who('D', 4), 'hello'), /position 3; only the next 2 waiters/);
    assert.throws(() => r.q.message(who('Z', 9), 'hello'), /join the queue first/);
    assert.match(r.q.message(who('B', 2), 'need the client at the title for 15 min; can you release just the client?'), /Sent to Claude session "G"/);

    const mail = r.q.notes(who('G', 1));
    assert.match(mail, /Queue message from Claude session "B" .*: "need the client at the title for 15 min; can you release just the client\?"/);
    assert.match(mail, /3 waiting: .*"B" .*needs client for 15 min.*release just the side they need|Leave as soon/);
    assert.equal(r.q.notes(who('G', 1)), '', 'delivered once; reminder not due again within 5 min');
    assert.match(r.q.status(who('D', 4)), /message from Claude session "B" .*\(delivered 0 s ago\)/);

    r.q.message(who('G', 1), 'done in 5 min, then the client is yours');
    assert.match(r.q.notes(who('B', 2)), /Queue message from Claude session "G".*done in 5 min/);
    assert.equal(r.q.notes(who('C', 3)), '', 'the reply goes to the asker only');
  } finally { r.done(); }
});

test('mailbox: two undelivered messages per sender; UNANSWERED after 10 min while the holder stays active', () => {
  const r = rig();
  try {
    [1, 2].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    r.q.message(who('B', 2), 'one');
    r.q.message(who('B', 2), 'two');
    assert.throws(() => r.q.message(who('B', 2), 'three'), /Not sent to Claude session "G".*2 of your messages are still waiting to be picked up/);
    r.q.notes(who('G', 1));
    r.advance(11);
    r.q.touch(who('G', 1)); // the holder keeps working
    const s = r.q.status(who('B', 2));
    assert.match(s, /UNANSWERED for 11 min: Claude session "B".*'s message, while the holder was active/);
    r.q.message(who('G', 1), 'ok');
    assert.doesNotMatch(r.q.status(who('B', 2)), /UNANSWERED/);
  } finally { r.done(); }
});

test('release hands back one side: a client-only waiter starts while the holder keeps the server', () => {
  const r = rig();
  try {
    [1, 2].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client', 'server'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    assert.throws(() => r.q.release(who('B', 2), ['client']), /only the holder/);
    const out = r.q.release(who('G', 1), ['client']);
    assert.match(out, /Released client\./);
    const s = r.q.snapshot();
    assert.deepEqual(s.entries.map((e) => [e.who.name, e.state, e.sides.join('+')]), [['G', 'active', 'server'], ['B', 'active', 'client']]);
    assert.match(r.q.history(who('G', 1)), /released client, kept server/);
    r.q.release(who('G', 1), ['server']);
    assert.ok(!r.q.snapshot().entries.some((e) => e.who.name === 'G'));
  } finally { r.done(); }
});

test('review fixes: wait keeps mail; a Codex shell gets its MCP entry\'s mail; holder `to` must share a side', async () => {
  const r = rig();
  try {
    [1, 2, 3].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    r.q.join(who('S', 3), job(['server'], 10)); // server is free: S holds it
    r.q.message(who('G', 1), 'yours in 1 min');
    const sleeps = [];
    const out = await r.q.wait(who('B', 2), 6, async (ms) => { sleeps.push(ms); r.advance(ms / MIN); });
    assert.match(out, /^Mail arrived\.[\s\S]*Queue message from Claude session "G".*"yours in 1 min"/, 'wait returns at once with the reply');
    assert.equal(sleeps.length, 0, 'no polling when mail is already there');

    assert.throws(() => r.q.message(who('G', 1), 'hi', { to: 9 }), /no waiter for your sides at position 9/);

    const codexMcp = { ai: 'codex', name: '', id: '', tool: 'mcp', pid: 1 };
    const r2 = rig();
    try {
      r2.alive.add(1); r2.alive.add(2);
      r2.q.join(codexMcp, job(['server'], 60));
      r2.q.join(who('W', 2), job(['server']));
      r2.q.message(who('W', 2), 'need it');
      const shell = { ai: 'codex', name: '', id: 'thread-9', tool: 'call.mjs', pid: 77 };
      assert.ok(r2.q.hasNotes(shell));
      assert.match(r2.q.notes(shell), /Queue message from Claude session "W".*"need it"/);
    } finally { r2.done(); }
  } finally { r.done(); }
});

test('history shows a partial release on the holder record', () => {
  const r = rig();
  try {
    [1, 2].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client', 'server'], 60));
    r.q.join(who('B', 2), job(['client'], 15));
    r.q.release(who('G', 1), ['client']);
    assert.match(r.q.history(who('G', 1)), /client\+server \(client released .*\): Claude session "G"/);
  } finally { r.done(); }
});

test('retract: the sender is told to keep watching; retracting withdraws undelivered mail and notifies after delivery', () => {
  const r = rig();
  try {
    [1, 2, 3].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    const sent = r.q.message(who('B', 2), 'first');
    assert.match(sent, /Keep watching for the reply: progenitor_queue action=wait .*retract it: progenitor_queue action=retract/);

    // Undelivered: retracting means the holder never sees it.
    assert.match(r.q.retract(who('B', 2)), /Retracted 1 message\./);
    assert.equal(r.q.notes(who('G', 1)).includes('first'), false);
    assert.match(r.q.status(who('B', 2)), /"first" \(retracted 0 s ago: the sender stopped watching for a reply\)/);

    // Delivered: the holder gets a notice.
    r.q.message(who('B', 2), 'second');
    assert.match(r.q.notes(who('G', 1)), /"second"/);
    r.q.retract(who('B', 2), 'session ending');
    assert.match(r.q.notes(who('G', 1)), /Queue notice .*retracted its message "second" \(session ending\); no reply is expected/);
    assert.doesNotMatch(r.q.status(who('B', 2)), /UNANSWERED/);

    // Answered messages are not open any more.
    r.q.message(who('B', 2), 'third');
    r.q.message(who('G', 1), 'ok');
    assert.match(r.q.retract(who('B', 2)), /no open messages to retract/);
  } finally { r.done(); }
});

test('open messages are retracted automatically when the sender leaves as a waiter or its process exits', () => {
  const r = rig();
  try {
    [1, 2, 3].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    r.q.join(who('C', 3), job(['client'], 15));
    r.q.message(who('B', 2), 'from B');
    r.q.message(who('C', 3), 'from C');
    r.q.notes(who('G', 1)); // both delivered
    r.q.leave(who('B', 2));
    r.alive.delete(3);
    const mail = r.q.notes(who('G', 1));
    assert.match(mail, /retracted its message "from B" \(it left the queue\)/);
    assert.match(mail, /retracted its message "from C" \(its session exited\)/);
  } finally { r.done(); }
});

test('retract review fixes: a holder waits for its replies; holders retract on leave and evict; crossed messages are not answers', async () => {
  const r = rig();
  try {
    [1, 2, 3].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    // Holder asks; wait blocks for the reply instead of returning at once.
    r.q.message(who('G', 1), 'how long do you need?');
    let polls = 0;
    const out = await r.q.wait(who('G', 1), 20, async () => { polls++; if (polls === 2) { r.q.notes(who('B', 2)); r.q.message(who('B', 2), '15 min'); } r.advance(5 / 60); });
    assert.match(out, /^Mail arrived\.[\s\S]*"15 min"/);
    assert.equal(polls, 2);
    // No open messages: wait returns status at once for a holder.
    assert.doesNotMatch(await r.q.wait(who('G', 1), 20, async () => { throw new Error('should not poll'); }), /Mail arrived/);

    // A crossed message (sent before the original was read) is not an answer; one sent after is.
    const t0 = '2026-10-10T12:00:00.000Z';
    const orig = { id: 'm1', at: t0, fromKey: 'g', toKey: 'b', readAt: null };
    const crossed = { id: 'm2', at: t0, fromKey: 'b', toKey: 'g', readAt: null };
    assert.equal(answered({ messages: [orig, crossed] }, orig), false);
    assert.equal(answered({ messages: [{ ...orig, readAt: t0 }, crossed] }, { ...orig, readAt: t0 }), true);

    // A holder that leaves retracts its open messages; B gets a notice for the delivered one.
    r.q.message(who('G', 1), 'ping');
    r.q.notes(who('B', 2));                      // B reads 'ping' and does not answer
    r.q.leave(who('G', 1));
    assert.match(r.q.notes(who('B', 2)), /retracted its message "ping" \(it left the queue\)/);
  } finally { r.done(); }
});

test('evict retracts the idle holder\'s open messages; no notice to a recipient that already left', () => {
  const r = rig();
  try {
    [1, 2, 3].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 10));
    r.q.join(who('B', 2), job(['client'], 15));
    r.q.join(who('C', 3), job(['client'], 15));
    r.q.message(who('G', 1), 'to all waiters');
    r.q.notes(who('B', 2)); r.q.notes(who('C', 3));
    r.q.leave(who('C', 3));
    r.advance(12); r.q.status(who('B', 2));
    const evicted = r.q.evict(who('B', 2), 'idle and past its estimate');
    const s = r.q.snapshot();
    assert.ok(s.messages.filter((m) => m.text === 'to all waiters').every((m) => m.retractedAt));
    assert.equal(s.messages.filter((m) => m.system && m.toKey === keyOf(who('C', 3))).length, 0, 'no notice to C, it left');
    assert.match(evicted, /retracted its message "to all waiters" \(it was evicted as idle\)/, 'delivered with the evict result');
  } finally { r.done(); }
});

test('a no-reply notice survives the sender leaving and is never UNANSWERED', () => {
  const r = rig();
  try {
    [1, 2].forEach((p) => r.alive.add(p));
    r.q.join(who('G', 1), job(['client'], 240));
    r.q.join(who('B', 2), job(['client'], 15));
    const sent = r.q.message(who('B', 2), 'main moved; please rebase', { noReply: true });
    assert.match(sent, /Sent as a notice: no reply expected/);
    assert.doesNotMatch(sent, /Keep watching/);
    r.q.leave(who('B', 2));
    const mail = r.q.notes(who('G', 1));
    assert.match(mail, /Queue notice from Claude session "B" .*"main moved; please rebase" \(no reply needed\)/);
    assert.doesNotMatch(mail, /retracted|Reply with/);
  } finally { r.done(); }
});

test('notify reaches a session by ID even when it is not in the queue', () => {
  const r = rig();
  try {
    r.alive.add(1);
    r.q.join(who('G', 1), job(['client'], 30));
    r.q.leave(who('G', 1));                                   // G is gone from the queue
    const out = r.q.notify(who('Z', 9), 'id-G', 'main moved; please rebase');
    assert.match(out, /Notice queued for Claude session "G" .*\(not in the queue now\)/);
    const shell = { ai: 'claude', name: 'G', id: 'id-G', tool: 'call.mjs', pid: 5 };
    assert.match(r.q.notes(shell), /Queue notice from Claude session "Z" .*"main moved; please rebase" \(no reply needed\)/);
    assert.equal(r.q.notes(shell), '', 'delivered once');
    assert.throws(() => r.q.notify(who('Z', 9), '', 'x'), /session ID/);
  } finally { r.done(); }
});

test('an override run on a held side is recorded on the holder and noticed, at most every 5 min', () => {
  const r = rig();
  try {
    r.alive.add(1);
    r.q.join(who('G', 1), job(['client'], 60));
    r.env.PROGENITOR_QUEUE = 'off';
    assert.equal(r.q.gate(who('B', 2), 'client', 'set', {}), null, 'the override still lets it through');
    assert.equal(r.q.gate(who('B', 2), 'client', 'spawn', {}), null);
    assert.equal(r.q.gate(who('B', 2), 'client', 'ping', {}), null);
    delete r.env.PROGENITOR_QUEUE;
    const st = r.q.status(who('Z', 9));
    assert.match(st, /OVERRIDE: Claude session "B" .*last ran 'spawn' on the client under PROGENITOR_QUEUE=off/);
    const mail = r.q.notes(who('G', 1));
    assert.equal((mail.match(/under PROGENITOR_QUEUE=off/g) || []).length, 1, 'one notice within 5 min');
    assert.match(mail, /Queue notice from Claude session "B" .*ran 'set' on the client/);
    r.env.PROGENITOR_QUEUE = 'off';
    r.q.gate(who('G', 1), 'client', 'set', {});                 // the holder itself: nothing recorded
    delete r.env.PROGENITOR_QUEUE;
    assert.equal(r.q.notes(who('G', 1)), '');
  } finally { r.done(); }
});
