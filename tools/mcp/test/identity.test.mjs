import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  detectIdentity, createIdentity, wireWho, sharingNote, formatBridgeUsers, lastJsonLine, aiFromClientName, REFRESH_MS,
} from '../identity.mjs';

function fakeHome() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-identity-'));
  const env = { USERPROFILE: root, APPDATA: path.join(root, 'AppData', 'Roaming') };
  const write = (rel, content) => {
    const f = path.join(root, rel);
    fs.mkdirSync(path.dirname(f), { recursive: true });
    fs.writeFileSync(f, typeof content === 'string' ? content : JSON.stringify(content));
    return f;
  };
  return { root, env, write, done: () => fs.rmSync(root, { recursive: true, force: true }) };
}

test('Claude desktop session: title and model from the host session file', () => {
  const h = fakeHome();
  try {
    h.write('AppData/Roaming/Claude/claude-code-sessions/acct/org/local_host1.json', { title: 'Example session', model: 'example-model', cliSessionId: 'sess-1' });
    const who = detectIdentity({
      env: { ...h.env, CLAUDECODE: '1', CLAUDE_CODE_SESSION_ID: 'sess-1', CLAUDE_CODE_HOST_SESSION_ID: 'local_host1', AI_AGENT: 'claude-code_test_agent' },
      tool: 'mcp', pid: 10, ppid: 9,
    });
    assert.equal(who.ai, 'claude');
    assert.equal(who.name, 'Example session');
    assert.equal(who.id, 'sess-1');
    assert.equal(who.model, 'example-model');
    assert.equal(who.client, 'claude-code_test_agent');
    assert.equal(who.tool, 'mcp');
    assert.equal(who.pid, 10);
  } finally { h.done(); }
});

test('Claude CLI: falls back to the last custom-title in the transcript tail', () => {
  const h = fakeHome();
  try {
    const filler = '{"type":"assistant","message":"' + 'x'.repeat(1000) + '"}\n';
    h.write('.claude/projects/C--example/sess-2.jsonl',
      '{"type":"custom-title","customTitle":"Old name","sessionId":"sess-2"}\n' + filler.repeat(400)
      + '{"type":"custom-title","customTitle":"New name","sessionId":"sess-2"}\n' + filler.repeat(400));
    const who = detectIdentity({ env: { ...h.env, CLAUDECODE: '1', CLAUDE_CODE_SESSION_ID: 'sess-2' }, tool: 'call.mjs' });
    assert.equal(who.ai, 'claude');
    assert.equal(who.name, 'New name');
  } finally { h.done(); }
});

test('Claude markers win over GROK_CC_* plugin variables', () => {
  const h = fakeHome();
  try {
    h.write('.grok/active_sessions.json', [{ session_id: 'grok-1', pid: 77 }]);
    const who = detectIdentity({
      env: { ...h.env, CLAUDECODE: '1', CLAUDE_CODE_SESSION_ID: 'sess-3', GROK_CC_SESSION_ID: 'sess-3', GROK_CC_TRANSCRIPT_PATH: 'x' },
      ppid: 12,
    });
    assert.equal(who.ai, 'claude');
    assert.equal(who.id, 'sess-3');
  } finally { h.done(); }
});

test('Codex or Grok started from a Claude session is the innermost host, not Claude', () => {
  const h = fakeHome();
  try {
    h.write('.grok/active_sessions.json', [{ session_id: 'grok-1', pid: 77 }]);
    const inherited = { ...h.env, CLAUDECODE: '1', CLAUDE_CODE_SESSION_ID: 'sess-3' };
    const codex = detectIdentity({ env: { ...inherited, CODEX_THREAD_ID: 'thread-9' }, ppid: 12 });
    assert.equal(codex.ai, 'codex');
    assert.equal(codex.id, 'thread-9');
    const grok = detectIdentity({ env: inherited, ppid: 77 });
    assert.equal(grok.ai, 'grok');
    assert.equal(grok.id, 'grok-1');
  } finally { h.done(); }
});

test('Codex: thread id from env, newest name from session_index.jsonl', () => {
  const h = fakeHome();
  try {
    h.write('.codex/session_index.jsonl', [
      { id: 't-1', thread_name: 'First name' },
      { id: 't-2', thread_name: 'Other thread' },
      { id: 't-1', thread_name: 'Example session' },
    ].map((o) => JSON.stringify(o)).join('\n') + '\n');
    const who = detectIdentity({ env: { ...h.env, CODEX_THREAD_ID: 't-1' }, ppid: 1 });
    assert.equal(who.ai, 'codex');
    assert.equal(who.id, 't-1');
    assert.equal(who.name, 'Example session');
  } finally { h.done(); }
});

test('Grok: session found by parent pid, title and model from summary.json', () => {
  const h = fakeHome();
  try {
    h.write('.grok/active_sessions.json', [{ session_id: 'g-1', pid: 41 }, { session_id: 'g-2', pid: 42 }]);
    h.write('.grok/sessions/C%3A%5Cexample/g-2/summary.json', { generated_title: 'Example session', session_summary: 'summary', current_model_id: 'grok-x' });
    const who = detectIdentity({ env: { ...h.env }, ppid: 42 });
    assert.equal(who.ai, 'grok');
    assert.equal(who.id, 'g-2');
    assert.equal(who.name, 'Example session');
    assert.equal(who.model, 'grok-x');

    const byEnv = detectIdentity({ env: { ...h.env, GROK_SESSION_ID: 'g-1' }, ppid: 1 });
    assert.equal(byEnv.ai, 'grok');
    assert.equal(byEnv.id, 'g-1');
    assert.equal(byEnv.name, '');
  } finally { h.done(); }
});

test('MCP client name is the fallback; overrides always win', () => {
  const h = fakeHome();
  try {
    const hinted = detectIdentity({ env: h.env, ppid: 1, clientInfo: { name: 'codex-mcp-client', version: '1.2' } });
    assert.equal(hinted.ai, 'codex');
    assert.equal(hinted.client, 'codex-mcp-client 1.2');

    const none = detectIdentity({ env: h.env, ppid: 1 });
    assert.equal(none.ai, 'unknown');

    const forced = detectIdentity({
      env: { ...h.env, CLAUDECODE: '1', CLAUDE_CODE_SESSION_ID: 's', PROGENITOR_AGENT: 'other', PROGENITOR_SESSION_NAME: 'Env name', PROGENITOR_SESSION_ID: 'env-id' },
      ppid: 1,
    });
    assert.deepEqual([forced.ai, forced.name, forced.id], ['other', 'Env name', 'env-id']);

    const tool = detectIdentity({ env: { ...h.env, PROGENITOR_SESSION_NAME: 'Env name' }, ppid: 1, overrides: { name: 'Tool name' } });
    assert.equal(tool.name, 'Tool name');
  } finally { h.done(); }
});

test('detection never throws on unreadable or malformed files', () => {
  const h = fakeHome();
  try {
    h.write('.grok/active_sessions.json', '{ not json');
    h.write('.codex/session_index.jsonl', 'garbage\n{"id":');
    h.write('AppData/Roaming/Claude/claude-code-sessions/a/b/local_x.json', '{');
    assert.equal(detectIdentity({ env: h.env, ppid: 5 }).ai, 'unknown');
    assert.equal(detectIdentity({ env: { ...h.env, CODEX_THREAD_ID: 'z' } }).name, '');
    assert.equal(detectIdentity({ env: { ...h.env, CLAUDECODE: '1', CLAUDE_CODE_HOST_SESSION_ID: 'local_x' } }).name, '');
    assert.equal(lastJsonLine(path.join(h.root, 'missing.jsonl'), 'x'), null);
  } finally { h.done(); }
});

test('fields are clipped and control characters removed', () => {
  const who = detectIdentity({ env: { PROGENITOR_SESSION_NAME: 'a\nb' + 'x'.repeat(500) }, ppid: 1 });
  assert.equal(who.name.length, 120);
  assert.ok(!who.name.includes('\n'));
});

test('createIdentity caches for REFRESH_MS and applies whoami overrides', () => {
  let t = 0;
  const env = { PROGENITOR_SESSION_NAME: 'first' };
  const id = createIdentity({ tool: 'mcp', env, now: () => t });
  assert.equal(id.get().name, 'first');
  env.PROGENITOR_SESSION_NAME = 'second';
  t = REFRESH_MS - 1;
  assert.equal(id.get().name, 'first');
  t = REFRESH_MS;
  assert.equal(id.get().name, 'second');

  assert.equal(id.override({ ai: 'grok', name: 'Mine' }).ai, 'grok');
  assert.equal(id.get().name, 'Mine');
  assert.deepEqual(id.overrides(), { ai: 'grok', name: 'Mine' });
  assert.equal(id.override({ name: '' }).name, 'second');
});

test('wireWho drops empty fields', () => {
  assert.deepEqual(wireWho({ ai: 'claude', name: '', id: 'x', client: '', tool: 'mcp', pid: 3, model: '' }), { ai: 'claude', id: 'x', tool: 'mcp', pid: 3 });
});

test('sharing note: other recent session only', () => {
  const prev = { ai: 'codex', name: 'Example', id: 'c-1', ageSeconds: 180, lastOp: 'connect' };
  assert.equal(sharingNote('client', prev, { id: 'me' }), 'Note: Codex session "Example" (id c-1) used the client bridge 3 min ago (last op: connect).');
  assert.equal(sharingNote('client', { ...prev, ageSeconds: 601 }, { id: 'me' }), '');
  assert.equal(sharingNote('client', prev, { id: 'c-1' }), '');
  assert.equal(sharingNote('server', undefined, {}), '');
  assert.equal(sharingNote('server', { ai: 'unknown', tool: 'call.mjs', pid: 5, ageSeconds: 12 }, {}), 'Note: Unknown session (call.mjs pid 5) used the server bridge 12 s ago.');
});

test('formatBridgeUsers summarizes current and recent', () => {
  const s = formatBridgeUsers({
    current: { ai: 'claude', name: 'A', id: 'a', requests: 4, lastOp: 'ping' },
    recent: [{ ai: 'grok', name: 'B', id: 'b', ageSeconds: 120, lastOp: 'logs' }],
  });
  assert.match(s, /Bridge user now: Claude session "A" \(id a\), 4 requests, last op ping/);
  assert.match(s, /before: Grok session "B" \(id b\), 2 min ago, last op logs/);
  assert.equal(formatBridgeUsers(undefined), '');
});

test('aiFromClientName', () => {
  assert.equal(aiFromClientName('claude-code'), 'claude');
  assert.equal(aiFromClientName('grok-cli'), 'grok');
  assert.equal(aiFromClientName('something'), '');
});
