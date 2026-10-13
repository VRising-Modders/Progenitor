import { mkdtempSync, mkdirSync, writeFileSync, existsSync, rmSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { fakeClock } from '../lib/clock.mjs';
import { createBridge } from '../lib/bridge.mjs';
import { MAX_BUFF_CYCLES } from '../lib/entityOp.mjs';
import { canonicalRectId } from '../lib/input.mjs';

export const HERE = path.dirname(fileURLToPath(import.meta.url));
export const TOOL_DIR = path.resolve(HERE, '..');
export const REPO_ROOT = path.resolve(HERE, '..', '..', '..');

export function tempDir() {
  const dir = mkdtempSync(path.join(tmpdir(), 'live-rig-'));
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) };
}

const OK_STATUS = 'Debug: status character=1:1 buff=3:1 flags=7 immortal=True guard=True prefixes=3/3 drain=True aggro=True persists=True reconcileGaveUpGrants=0 reconcileGaveUpCharacters=(none) reconcilePassGaveUp=False reconcileThrowingPasses=0';
const OFF_STATUS = 'Debug: status character=1:1 buff=none flags=0 immortal=True guard=False prefixes=3/3 drain=False aggro=False persists=False reconcileGaveUpGrants=0 reconcileGaveUpCharacters=(none) reconcilePassGaveUp=False reconcileThrowingPasses=0';
const GUARD_DROPPED_STATUS = 'Debug: status character=1:1 buff=3:1 flags=7 immortal=True guard=False prefixes=3/3 drain=True aggro=True persists=True reconcileGaveUpGrants=0 reconcileGaveUpCharacters=(none) reconcilePassGaveUp=False reconcileThrowingPasses=0';
export const DEFAULT_SAMPLE_HASH = 'a274cbd4478a943df2e0e92e4d5942f48ae39172fce9743611b7eab806e63c8e';
export const SWAP_SAMPLE_HASH = '59a2ad1186b0dbb7c64e0af434e578712c42dca90bbd7e8c2633ee8d48180c13';

export function createFakeWorld(opts = {}) {
  let seq = 1;
  let nextIndex = 100;
  const logs = { client: [], server: [] };
  const entities = new Map();
  const sticky = new Set();
  let sampleLoaded = opts.sampleLoaded !== false;
  let protectionOn = opts.protectionOn !== false;
  let protectionGuard = opts.protectionGuard !== false;
  let rearmFails = !!opts.rearmFails;
  // Simulates a Core defect: the Core-owned debug buff's guard drops on a Sample swap.
  let dropGuardOnSwap = !!opts.dropGuardOnSwap;
  let sampleHash = opts.sampleHash ?? DEFAULT_SAMPLE_HASH;
  let clientSampleHash = opts.clientSampleHash ?? sampleHash;
  let generation = opts.generation ?? 1;
  let lastReload = {
    gen: generation,
    outcome: 'ok',
    reason: 'boot',
    timestamp: 't0',
  };
  let previousAlive = false;
  let clientWorlds = Array.isArray(opts.clientWorlds) ? opts.clientWorlds.slice() : null;
  function pingClientWorlds() {
    return clientWorlds ?? ['Client_0'];
  }
  function clientHasGameWorld() {
    return pingClientWorlds().includes('Client_0');
  }
  let clientWorldEpoch = Number.isFinite(Number(opts.clientWorldEpoch))
    ? Number(opts.clientWorldEpoch)
    : (clientHasGameWorld() ? 1 : 0);
  let leaveRequested = !!opts.leaveRequested;
  let leaveEpoch = Number.isFinite(Number(opts.leaveEpoch)) ? Number(opts.leaveEpoch) : 0;
  let leaveFailure = opts.leaveFailure != null ? String(opts.leaveFailure) : null;

  function observeLeave(statusOnly) {
    const hasWorld = clientHasGameWorld();
    const epoch = hasWorld ? clientWorldEpoch : 0;
    const menuVerified = !hasWorld;
    if (menuVerified) {
      leaveRequested = false;
      leaveFailure = null;
      return 'verified';
    }
    if (leaveRequested) {
      if (hasWorld && epoch !== leaveEpoch) {
        leaveRequested = false;
        leaveFailure = null;
        return 'superseded';
      }
      return leaveFailure != null ? 'requestFailed' : 'pending';
    }
    if (statusOnly || !hasWorld) {
      return hasWorld ? 'inGame' : 'notVerified';
    }
    leaveRequested = true;
    leaveEpoch = epoch;
    leaveFailure = null;
    return 'request';
  }

  function disconnectResult(state, hasWorldAtObserve) {
    return {
      ok: true,
      result: {
        ok: leaveFailure == null,
        state,
        verified: state === 'verified',
        requested: leaveRequested,
        clientWorldPresent: hasWorldAtObserve,
        view: hasWorldAtObserve ? 'None' : 'MainMenu',
        mainMenuVisible: !hasWorldAtObserve,
        elapsedSeconds: 0,
        error: leaveFailure,
        note: 'Poll disconnect with args.statusOnly=true; pending is not proof of leaving. No focus or OS input is used.',
      },
    };
  }

  function applyLeaveEffects() {
    clientWorlds = ['Default World'];
    for (const row of users) {
      row.connected = false;
      row.handshake = null;
    }
    for (const row of clientUsers) {
      row.connected = false;
      if ('handshake' in row) row.handshake = null;
    }
    serverListAvailable = false;
    serverListEpoch += 1;
    closeModsUi();
    if (serverContextLive) {
      serverContextLive = false;
      pushLog('client', 'Mods inventory: server context cleared reason=disconnect epoch=1');
    }
  }
  let serverListAvailable = opts.serverListAvailable !== false;
  let serverListEpoch = opts.serverListEpoch ?? 1;
  let serverContextLive = opts.serverContextLive !== false;
  let uptimeSeconds = opts.uptimeSeconds ?? 1;
  const players = opts.players ?? [{
    entity: '1:1',
    name: 'TestVampire',
    connected: true,
    userEntity: '2:1',
    position: { x: 0, y: 0, z: 0 },
  }];
  const users = opts.users ?? [{
    index: 1,
    name: 'TestVampire',
    connected: true,
    handshake: 'ok',
    admin: false,
    entity: '2:1',
    character: '1:1',
    platformId: '1',
  }];
  const clientPlayers = opts.clientPlayers ?? players;
  const clientUsers = opts.clientUsers ?? users;
  const carriersBySide = { client: new Set(), server: new Set() };
  let sampleInFolder = opts.sampleLoaded !== false;
  let samplePresentFn = opts.samplePresent ?? null;
  const throwOn = [];
  let omitDestroyPhase = !!opts.omitDestroyPhase;
  let oldDetachPhase = !!opts.oldDetachPhase || !!opts.oldTwoPhase;
  let keepAllBuffs = opts.keepBuffPresent === true;
  const keepBuffPresent = new Set(Array.isArray(opts.keepBuffPresent) ? opts.keepBuffPresent : []);
  let sweepIncompleteAll = opts.sweepIncomplete === true;
  const sweepIncomplete = new Set(Array.isArray(opts.sweepIncomplete) ? opts.sweepIncomplete : []);
  let sweepIncompleteReason = opts.sweepIncompleteReason ?? 'buff sweep incomplete';
  let newBuffCyclesDefault = Number(opts.newBuffCycles ?? 0);
  let fatalLogOnDestroy = opts.fatalLog ?? null;
  let emitExpectedDetachLogs = opts.emitExpectedDetachLogs !== false;
  let dead = !!opts.dead;
  if (players[0]?.entity) {
    entities.set(players[0].entity, { exists: true, prefab: 'CHAR_VampireMale', side: 'server' });
  }
  if (users[0]?.entity) {
    entities.set(users[0].entity, { exists: true, prefab: 'User', side: 'server' });
  }
  const reloadFolder = {
    client: opts.clientReload ?? path.join(tmpdir(), 'live-rig-reload-client'),
    server: opts.serverReload ?? path.join(tmpdir(), 'live-rig-reload-server'),
  };
  const calls = [];
  const adminWrites = [];
  const dumpCalls = [];
  let drainFrame = 1000;
  let holdBridgeFrame = false;
  let lastFreezeDump = opts.lastFreezeDump ?? null;
  let nowFn = typeof opts.now === 'function' ? opts.now : null;
  const perf = {
    running: false,
    historical: false,
    captureId: 0,
    nextId: 1,
    startedAtMs: null,
    lastSampleAtMs: null,
    seconds: 30,
    frames: 0,
    lastFrameMs: null,
    lastCoreTickMs: null,
    maxFrameMs: null,
    sampleSilenceMs: 0,
    path: null,
    summary: null,
    stopReason: null,
  };

  function nowMs() {
    if (typeof nowFn === 'function') {
      const n = Number(nowFn());
      return Number.isFinite(n) ? n : 0;
    }
    return Date.now();
  }

  function closeFakePerf(reason) {
    const elapsedSeconds = perf.startedAtMs == null ? 0 : Math.max(0, (nowMs() - perf.startedAtMs) / 1000);
    perf.running = false;
    perf.historical = true;
    perf.stopReason = reason;
    perf.path = `C:\\fake\\progenitor-perf\\${perf.captureId}.json`;
    perf.summary = {
      frames: perf.frames,
      elapsedSeconds,
      stopReason: reason,
      frameMs: {
        max: perf.maxFrameMs,
        p50: 16.6,
        p95: 18,
        p99: 20,
      },
    };
  }

  function maybeCloseFakePerfDeadline() {
    if (!perf.running || perf.startedAtMs == null) return;
    if (nowMs() - perf.startedAtMs >= perf.seconds * 1000) closeFakePerf('deadline');
  }

  function fakePerfProgress() {
    if (!perf.captureId) return null;
    return {
      frames: perf.frames,
      sampleSilenceMs: perf.sampleSilenceMs,
      lastFrameMs: perf.frames === 0 ? null : perf.lastFrameMs,
      lastCoreTickMs: perf.frames === 0 ? null : perf.lastCoreTickMs,
      maxFrameMs: perf.frames === 0 ? null : perf.maxFrameMs,
    };
  }

  function tickFakePerf() {
    maybeCloseFakePerfDeadline();
    if (!perf.running) return;
    const t = nowMs();
    if (holdBridgeFrame) {
      const origin = perf.lastSampleAtMs ?? perf.startedAtMs ?? t;
      perf.sampleSilenceMs = Math.max(0, t - origin);
      return;
    }
    const prev = perf.lastSampleAtMs ?? perf.startedAtMs ?? t;
    const dt = Math.max(0, t - prev);
    const add = Math.max(1, Math.round(dt / 16.6) || 1);
    perf.frames += add;
    perf.lastFrameMs = 16.6;
    perf.lastCoreTickMs = 0.4;
    perf.maxFrameMs = Math.max(perf.maxFrameMs ?? 0, 16.6);
    perf.sampleSilenceMs = 0;
    perf.lastSampleAtMs = t;
  }
  const modsUi = {
    open: false,
    site: 'main-menu',
    attachedSite: 'main-menu',
    plugin: null,
    page: null,
    pageId: '',
    search: '',
    entries: [],
    curtain: false,
    modsCapture: false,
    inputCapture: !!opts.modsUiHudCapture,
    hudInputCapture: !!opts.modsUiHudCapture,
    fullscreenMenu: opts.modsUiFullscreenMenu === true,
    refuseTopTabAttach: opts.modsUiRefuseTopTabAttach === true,
    width: 1920,
    height: 1080,
    mode: 'window',
    clientX: 0,
    clientY: 0,
    displayWidth: opts.displayWidth ?? 3840,
    displayHeight: opts.displayHeight ?? 1080,
    shortcut: opts.modsUiOmitShortcut === true ? undefined : (opts.modsUiShortcut ?? 'F10'),
    listScroll: 1,
    contentScroll: 1,
    panes: 0,
    observers: 0,
    scroll: null,
    nativeTab: null,
    rectOverrides: opts.modsUiRectOverrides ?? {},
    topTabVisible: opts.modsUiTopTabVisible === true,
    plugins: ['progenitor.sample'],
    values: { ShowHints: 'true', ExampleLimit: '10' },
    committedExampleLimit: '10',
    note: 'Server setting — changes require administrator permission.',
    pendingNote: null,
    pendingCommit: null,
    pendingReads: 0,
  };

  function snapshotModsUi() {
    const modsCapture = !!modsUi.modsCapture;
    const inputCapture = !!(modsUi.modsCapture || modsUi.hudInputCapture || modsUi.inputCapture);
    const snap = {
      open: modsUi.open,
      site: modsUi.site,
      plugin: modsUi.plugin,
      page: modsUi.page,
      pageId: modsUi.pageId,
      search: modsUi.search,
      entries: [
        { id: 'progenitor', status: 'Running' },
        ...modsUi.plugins.map((id) => ({ id, status: 'Loaded on this client' })),
        {
          id: 'server-list',
          status: serverListAvailable
            ? 'Server mod list received (5 entries)'
            : 'Server mod list unavailable. Known requirements are shown where available.',
        },
      ],
      curtain: modsUi.curtain,
      modsCapture,
      inputCapture,
      capture: inputCapture,
      width: modsUi.width,
      height: modsUi.height,
      mode: modsUi.mode,
      scroll: { list: modsUi.listScroll, content: modsUi.contentScroll },
      panes: modsUi.panes,
      observers: modsUi.observers,
      serverList: serverListAvailable
        ? { state: 'available', count: 5 }
        : { state: 'unavailable' },
      serverListEpoch,
      // Constants kept for bridge readers; nothing suspends the workspace any more.
      suspended: false,
      suspendReason: null,
    };
    if (modsUi.shortcut !== undefined) snap.shortcut = modsUi.shortcut;
    return snap;
  }

  function scaleRect(x, y, w, h) {
    const sx = (Number(modsUi.width) || 1920) / 1920;
    const sy = (Number(modsUi.height) || 1080) / 1080;
    return {
      x: Math.round(x * sx),
      y: Math.round(y * sy),
      width: Math.round(w * sx),
      height: Math.round(h * sy),
    };
  }

  function control(id, x, y, w, h, extra = {}) {
    const box = scaleRect(x, y, w, h);
    const row = {
      id,
      kind: extra.kind ?? 'button',
      label: extra.label ?? id,
      ...box,
      unity: extra.unity ?? { x: box.x, y: box.y, width: box.width, height: box.height },
      canvasScale: extra.canvasScale ?? 1,
      visible: extra.visible !== false,
      interactable: extra.interactable !== false,
      occludedBy: extra.occludedBy ?? null,
      controls: extra.controls ?? [],
    };
    if (extra.plugin != null) row.plugin = extra.plugin;
    if (extra.index != null) row.index = extra.index;
    return row;
  }

  function overrideMatches(row, sel) {
    if (row.id === sel) return true;
    const mapped = canonicalRectId(sel);
    if (mapped !== sel && row.id === mapped) return true;
    if (sel.startsWith('sidebarRow:') && row.id === 'sidebar-row' && row.plugin === sel.slice('sidebarRow:'.length)) return true;
    if (sel.startsWith('nativeTab:') && row.id === 'native-tab' && row.label === sel.slice('nativeTab:'.length)) return true;
    if (sel.startsWith('widget:')) {
      const rest = sel.slice('widget:'.length);
      const colon = rest.lastIndexOf(':');
      const label = colon >= 0 ? rest.slice(0, colon) : rest;
      if (row.id === 'widget' && row.label === label) return true;
      if (row.id === 'widget' && row.label === `Apply ${label}`) return true;
    }
    return false;
  }

  /** Live `mods_ui rects`: client-area pixels, `detail.rects` with id/kind/label/plugin/index/controls. */
  function snapshotRects() {
    const open = !!modsUi.open;
    const menuUp = !!modsUi.fullscreenMenu || !!modsUi.topTabVisible;
    const atMainMenu = !menuUp;
    const settings = open && modsUi.page === 'settings';
    const nativeTabs = [
      ['Emotes', 160],
      ['Vampire Powers', 280],
      ['Spellbook', 400],
      ['Map', 520],
      ['V Blood', 580],
      ['Social', 700],
      ['System', 800],
    ];
    const rows = [
      control('main-menu', 1760, 24, 140, 40, { kind: 'button', label: 'Mods', visible: atMainMenu }),
      control('top-tab', 640, 8, 120, 40, { kind: 'button', label: 'Mods', visible: menuUp }),
      ...nativeTabs.map(([label, x], index) => control('native-tab', x, 8, 120, 40, {
        kind: 'tab', label, index, visible: menuUp,
      })),
      control('search', 24, 104, 280, 32, {
        kind: 'input', label: 'Search', visible: open,
        controls: [control('input', 120, 106, 140, 28, { kind: 'input', label: 'input', visible: open })],
      }),
      control('close-back', 24, 64, 100, 32, {
        kind: 'button',
        label: open && (modsUi.page === 'settings' || modsUi.page === 'custom') ? 'Back to mod' : 'Close Mods',
        visible: open,
      }),
      control('sidebar-row', 24, 160, 280, 36, {
        kind: 'button', label: 'Progenitor Sample', plugin: 'progenitor.sample', index: 0,
        visible: open,
      }),
      control('sidebar-row', 24, 200, 280, 36, {
        kind: 'button', label: 'Progenitor', plugin: 'progenitor', index: 1,
        visible: open,
      }),
      control('list-scrollbar-track', 300, 160, 16, 400, { kind: 'scrollbar-track', label: 'list', visible: open }),
      control('list-scrollbar-handle', 300, 180, 16, 80, { kind: 'scrollbar-handle', label: 'list', visible: open }),
      control('content-scrollbar-track', 1880, 80, 16, 900, { kind: 'scrollbar-track', label: 'content', visible: open }),
      control('content-scrollbar-handle', 1880, 120, 16, 80, { kind: 'scrollbar-handle', label: 'content', visible: open }),
      control('widget', 400, 180, 860, 40, {
        kind: 'toggle', label: 'ShowHints', index: 1, visible: settings,
        controls: [control('toggle', 800, 184, 40, 28, { kind: 'toggle', label: 'toggle', visible: settings })],
      }),
      control('widget', 400, 240, 860, 40, {
        kind: 'input', label: 'ExampleLimit', index: 3, visible: settings,
        controls: [control('input', 620, 244, 180, 28, { kind: 'input', label: 'input', visible: settings })],
      }),
      control('widget', 400, 290, 860, 40, {
        kind: 'button', label: 'Apply ExampleLimit', index: 5, visible: settings,
      }),
    ];
    for (const [sel, patch] of Object.entries(modsUi.rectOverrides ?? {})) {
      let hit = false;
      for (let i = 0; i < rows.length; i++) {
        if (!overrideMatches(rows[i], sel)) continue;
        rows[i] = { ...rows[i], ...patch, id: patch.id ?? rows[i].id };
        hit = true;
      }
      if (!hit) {
        const id = canonicalRectId(sel);
        rows.push({
          id,
          kind: 'button',
          label: id,
          x: 0,
          y: 0,
          width: 0,
          height: 0,
          visible: true,
          interactable: true,
          occludedBy: null,
          controls: [],
          ...patch,
          id: patch.id ?? id,
        });
      }
    }
    return {
      origin: 'client-top-left',
      unityOrigin: 'screen-bottom-left',
      screenWidth: modsUi.width,
      screenHeight: modsUi.height,
      canvasScale: 1,
      ...(modsUi.shortcut !== undefined ? { shortcut: modsUi.shortcut } : {}),
      rects: rows,
    };
  }

  /** Live `mods_ui window` detail: nested client/screen/mode, no flat clientWidth. */
  function snapshotWindow() {
    return {
      hwnd: '0x1',
      client: {
        x: 0,
        y: 0,
        width: modsUi.width,
        height: modsUi.height,
        screenX: modsUi.clientX ?? 0,
        screenY: modsUi.clientY ?? 0,
      },
      screen: {
        width: modsUi.width,
        height: modsUi.height,
      },
      fullScreenMode: modsUi.mode,
      mode: modsUi.mode,
      primaryDisplay: {
        width: modsUi.displayWidth,
        height: modsUi.displayHeight,
      },
    };
  }

  function modsUiEnvelope(ok, action, before, after, detail) {
    return {
      ok: true,
      result: {
        ok,
        action,
        before,
        after,
        detail: detail ?? {},
        elapsedMs: 1,
      },
    };
  }

  function callerIsAdmin() {
    return !!users[0]?.admin;
  }

  /** Commit or drop an in-flight ExampleLimit apply. Live: the server still applies if admin held the request; dropping admin while pending cancels it. */
  function resolvePendingExampleLimit({ drop = false } = {}) {
    if (modsUi.pendingNote == null && modsUi.pendingCommit == null) {
      modsUi.values.ExampleLimit = modsUi.committedExampleLimit;
      return;
    }
    const isAdmin = callerIsAdmin();
    if (drop || !isAdmin) {
      modsUi.pendingCommit = null;
      modsUi.pendingNote = drop ? null : (modsUi.pendingNote && /Saved by the server/i.test(modsUi.pendingNote)
        ? 'Only server administrators can change this setting.'
        : modsUi.pendingNote);
      if (modsUi.pendingNote) {
        modsUi.note = modsUi.pendingNote;
      }
      modsUi.pendingNote = null;
      modsUi.pendingReads = 0;
      modsUi.values.ExampleLimit = modsUi.committedExampleLimit;
      return;
    }
    if (modsUi.pendingCommit != null) {
      modsUi.committedExampleLimit = modsUi.pendingCommit;
    }
    if (modsUi.pendingNote != null) {
      modsUi.note = modsUi.pendingNote;
    }
    modsUi.pendingNote = null;
    modsUi.pendingCommit = null;
    modsUi.pendingReads = 0;
    modsUi.values.ExampleLimit = modsUi.committedExampleLimit;
  }

  let scratchPresent = existsSync(path.join(reloadFolder.client, 'Progenitor.Scratch.dll'))
    || existsSync(path.join(reloadFolder.server, 'Progenitor.Scratch.dll'));

  function maybeEmitScratch(side) {
    const dest = path.join(reloadFolder[side], 'Progenitor.Scratch.dll');
    const present = existsSync(dest);
    if (!present) {
      scratchPresent = false;
      return;
    }
    if (scratchPresent) return;
    scratchPresent = true;
    let bytes;
    try { bytes = readFileSync(dest); } catch { return; }
    const hash = createHash('sha256').update(bytes).digest('hex');
    pushLog(side, `Reload: image Progenitor.Scratch.dll sha256=${hash} size=${bytes.length} asm=Progenitor.Scratch`);
    pushLog(side, 'scratch: result: fake scratch loaded');
  }

  function closeModsUi() {
    // Closing disposes the widget; an in-flight admin apply still commits server-side.
    resolvePendingExampleLimit();
    modsUi.open = false;
    modsUi.plugin = null;
    modsUi.page = null;
    modsUi.pageId = '';
    modsUi.search = '';
    modsUi.curtain = false;
    modsUi.modsCapture = false;
    modsUi.inputCapture = !!modsUi.hudInputCapture;
    modsUi.panes = 0;
    modsUi.observers = 0;
    modsUi.scroll = null;
    modsUi.listScroll = 1;
    modsUi.contentScroll = 1;
    modsUi.site = modsUi.attachedSite || 'main-menu';
    modsUi.pendingNote = null;
    modsUi.pendingCommit = null;
    modsUi.pendingReads = 0;
    modsUi.note = 'Server setting — changes require administrator permission.';
    modsUi.values.ExampleLimit = modsUi.committedExampleLimit;
  }

  function openModsUi(site) {
    const resolved = site || modsUi.attachedSite || 'main-menu';
    modsUi.open = true;
    modsUi.site = resolved;
    modsUi.attachedSite = resolved;
    modsUi.curtain = true;
    modsUi.inputCapture = true;
    modsUi.observers = 2;
    if (!modsUi.page) {
      modsUi.page = 'overview';
      modsUi.pageId = '';
    }
    modsUi.modsCapture = true;
    modsUi.panes = 2;
  }

  function defaultPushLog(side, text, extra = {}) {
    const line = {
      seq: seq++,
      time: extra.time ?? '00:00:00.000',
      level: extra.level ?? 'Info',
      source: extra.source ?? 'Progenitor',
      text,
    };
    logs[side].push(line);
    return line;
  }
  let pushLogFn = defaultPushLog;
  function pushLog(side, text, extra = {}) {
    return pushLogFn(side, text, extra);
  }

  function sideSampleHash(side) {
    return side === 'client' ? clientSampleHash : sampleHash;
  }

  function defaultBuffs(id) {
    const [idx, ver] = String(id).split(':');
    const n = Number(idx);
    const v = Number(ver) || 1;
    if (!Number.isFinite(n)) return ['900000:1', '900001:1'];
    return [`${n + 900000}:${v}`, `${n + 900001}:${v}`];
  }

  function ensureBuffs(row, id) {
    if (!Array.isArray(row.buffs)) row.buffs = defaultBuffs(id);
    if (row.attached === undefined) row.attached = true;
    return row.buffs;
  }

  function shouldKeepBuff(unitId, buffId, row) {
    if (keepAllBuffs || row.keepBuffs === true) return true;
    if (Array.isArray(row.keepBuffs) && row.keepBuffs.includes(buffId)) return true;
    if (keepBuffPresent.has(buffId) || keepBuffPresent.has(unitId)) return true;
    return false;
  }

  function vanishBuffs(id, row) {
    if (!row) return;
    row.buffs = (row.buffs ?? []).filter((b) => shouldKeepBuff(id, b, row));
  }

  function prefabInfo(row, exists) {
    if (!exists || row?.prefab == null) return null;
    return typeof row.prefab === 'object' ? row.prefab : { hash: 0, name: row.prefab };
  }

  function unitSweepIncomplete(id, row) {
    return sweepIncompleteAll || sweepIncomplete.has(id) || !!(row && row.sweepIncomplete);
  }

  function unitOmitsPhase(id, row) {
    return omitDestroyPhase || !!(row && row.omitPhase);
  }

  function unitOldDetachPhase(id, row) {
    return oldDetachPhase || !!(row && (row.oldDetachPhase || row.oldTwoPhase));
  }

  function staleRowsOf(row) {
    if (!row || row.staleRows == null) return [];
    return Array.isArray(row.staleRows) ? row.staleRows : [row.staleRows];
  }

  function emitDestroyLogs(id, row, buffs) {
    if (emitExpectedDetachLogs && buffs.length) {
      for (const buff of buffs) {
        pushLog('server', `Found entity: ${buff} that has been destroyed without being detached. Info: Buff, Attached`, { level: 'Error' });
        pushLog('server', `DetachEntity information. Entity: ${buff} EntityInBuffer: ${buff} Parent: ${id} PrefabGUID: 0 ParentPrefabGUID: 0`);
      }
    }
    if (fatalLogOnDestroy) {
      pushLog('server', String(fatalLogOnDestroy), { level: 'Error' });
    }
  }

  function takeNewBuffCycle(row) {
    const remaining = Number(row.newBuffCycles ?? 0);
    const cycle = Number(row.buffCycle ?? 0);
    if (cycle >= MAX_BUFF_CYCLES) {
      return { stuck: true, reason: row.stuckReason ?? 'buff cycle cap' };
    }
    if (remaining > 0) {
      row.newBuffCycles = remaining - 1;
      row.buffCycle = cycle + 1;
      const newId = `${nextIndex++}:1`;
      if (!Array.isArray(row.buffs)) row.buffs = [];
      row.buffs.push(newId);
      return { buffs: [newId], cycle: row.buffCycle };
    }
    return null;
  }

  function addCarrier(side, spec = {}) {
    const id = spec.entity ?? `${nextIndex++}:1`;
    const prev = entities.get(id) ?? {};
    entities.set(id, {
      ...prev,
      ...(spec.row ?? {}),
      exists: true,
      prefab: spec.prefab ?? prev.prefab ?? 'NetworkedSequence',
      side,
      buffs: spec.buffs ?? prev.buffs ?? [],
      attached: spec.attached ?? false,
      isCarrier: true,
    });
    carriersBySide[side].add(id);
    return id;
  }

  function listCarriers(side) {
    const list = [];
    for (const id of carriersBySide[side] ?? []) {
      const row = entities.get(id);
      if (!row || row.exists === false) continue;
      list.push({ entity: id, prefab: prefabInfo(row, true) });
    }
    return list;
  }

  const carrierSeed = opts.carrierEntities ?? null;
  if (carrierSeed) {
    for (const side of ['client', 'server']) {
      for (const spec of carrierSeed[side] ?? []) {
        addCarrier(side, typeof spec === 'string' ? { entity: spec } : spec);
      }
    }
  } else {
    const counts = opts.carriers ?? { client: 0, server: 0 };
    for (const side of ['client', 'server']) {
      const n = typeof counts[side] === 'number' ? counts[side] : 0;
      for (let i = 0; i < n; i++) addCarrier(side);
    }
  }

  function attachedBufferInspect(id, row, depth) {
    const buffs = row && Array.isArray(row.buffs) ? row.buffs.map(String) : [];
    const attached = !!(row && row.attached);
    const take = Number.isFinite(depth) && depth >= 0 ? Math.min(depth, buffs.length) : buffs.length;
    const elements = attached ? buffs.slice(0, take).map((b) => ({ Entity: b, PrefabGuid: 0 })) : [];
    return {
      entity: id,
      name: null,
      prefab: prefabInfo(row, !!row?.exists),
      count: 1,
      depth: depth ?? 0,
      truncated: false,
      components: [{
        component: 'ProjectM.AttachedBuffer',
        kind: 'buffer',
        length: attached ? buffs.length : 0,
        elements,
      }],
    };
  }

  async function request(side, op, args = {}) {
    calls.push({ side, op, args });
    const throwHit = throwOn.find((t) => t.op === op && (!t.side || t.side === side) && (!t.action || t.action === args.action));
    if (throwHit) {
      if (throwHit.once) throwOn.splice(throwOn.indexOf(throwHit), 1);
      throw new Error(throwHit.error);
    }
    switch (op) {
      case 'ping':
        if (dead) throw new Error('server not answering');
        maybeEmitScratch(side);
        return {
          ok: true,
          result: {
            side,
            progenitor: '0.1.0',
            uptimeSeconds,
            worlds: side === 'server' ? ['Server'] : (clientWorlds ?? ['Client_0']),
            reloadFolder: reloadFolder[side],
            reloadedPlugins: sampleLoaded ? ['Progenitor Sample'] : ['Progenitor Dev Bridge'],
            logSeq: logs[side].length ? logs[side][logs[side].length - 1].seq : 0,
            reload: { generation, alcName: `progenitor-core:${generation}` },
            instance: '',
            sampleHash: sampleLoaded ? sideSampleHash(side) : null,
            images: sampleLoaded
              ? [{ file: 'Progenitor.Sample.dll', sha256: sideSampleHash(side), size: 377856 }]
              : [],
            ...(opts.gameIdentity ? { gameIdentity: opts.gameIdentity } : {}),
            ...(opts.versionData ? { versionData: opts.versionData } : {}),
            ...(opts.coreHash ? { coreHash: opts.coreHash } : {}),
            ...(opts.devHash ? { devHash: opts.devHash } : {}),
            ...(opts.pingExtra && typeof opts.pingExtra === 'object' ? opts.pingExtra : {}),
            ...(clientWorlds && side !== 'server' ? { worlds: clientWorlds } : {}),
          },
        };
      case 'bridge_status':
        if (!holdBridgeFrame) drainFrame += 1;
        return {
          ok: true,
          result: {
            entered: false,
            op: null,
            id: 0,
            enteredAt: null,
            abandoned: false,
            elapsedMs: 0,
            queued: 0,
            delayed: 0,
            lastDrainAt: null,
            lastDrainMs: 0,
            frame: drainFrame,
            heartbeatSeconds: 5,
            uiTickTiming: false,
            lastFreezeDump,
          },
        };
      case 'dump': {
        dumpCalls.push({ side, args: args ?? {} });
        const at = typeof opts.dumpAt === 'string'
          ? opts.dumpAt
          : new Date(Number.isFinite(Number(opts.dumpAtMs)) ? Number(opts.dumpAtMs) : Date.now()).toISOString();
        return {
          ok: true,
          result: {
            ok: true,
            reason: args?.reason ?? null,
            at,
            path: opts.dumpPath ?? null,
          },
        };
      }
      case 'players': {
        const list = side === 'client' ? clientPlayers : players;
        return { ok: true, result: { count: list.length, players: list } };
      }
      case 'users':
        return { ok: true, result: { users: side === 'client' ? clientUsers : users } };
      case 'set': {
        // Only what the runner uses: the User component's IsAdmin flag on a user entity.
        const row = users.find((u) => u.entity === args.entity);
        if (!row) return { ok: false, error: `entity ${args.entity} does not exist` };
        if (args.component !== 'User' || !args.fields || typeof args.fields.IsAdmin !== 'boolean') {
          return { ok: false, error: 'fake set supports only User.IsAdmin' };
        }
        row.admin = args.fields.IsAdmin;
        adminWrites.push({ IsAdmin: args.fields.IsAdmin });
        if (args.fields.IsAdmin === false) {
          resolvePendingExampleLimit({ drop: true });
        }
        return { ok: true, result: { entity: args.entity, component: 'ProjectM.Network.User', fields: { IsAdmin: args.fields.IsAdmin } } };
      }
      case 'query': {
        if (side === 'client' && !clientHasGameWorld()) {
          return { ok: false, error: 'the game world does not exist yet (not in a game)' };
        }
        // EntityOps: stop at `limit` unless `total: true`; truncated is only the hard scan cap.
        const list = listCarriers(side);
        const limit = Number.isFinite(Number(args.limit)) ? Math.max(1, Number(args.limit)) : 50;
        const wantTotal = args.total === true;
        let total = 0;
        const rows = [];
        for (const row of list) {
          if (!wantTotal && total >= limit) break;
          total++;
          if (rows.length < limit) rows.push(row);
        }
        return { ok: true, result: { total, count: rows.length, truncated: false, entities: rows } };
      }
      case 'spawn': {
        const entity = `${nextIndex++}:1`;
        entities.set(entity, {
          exists: true,
          prefab: args.prefab,
          health: { Value: 80, IsDead: false },
          side: 'server',
          buffs: defaultBuffs(entity),
          attached: true,
          newBuffCycles: newBuffCyclesDefault,
          buffCycle: 0,
        });
        return {
          ok: true,
          result: {
            count: 1,
            prefab: { hash: 0, name: args.prefab },
            entities: [{
              entity,
              prefab: { hash: 0, name: args.prefab },
              position: { x: args.x, y: args.y, z: args.z },
              placed: true,
            }],
          },
        };
      }
      case 'inspect': {
        const id = args.entity;
        const row = id != null ? entities.get(id) : undefined;
        if (row && row.exists) ensureBuffs(row, id);
        return { ok: true, result: attachedBufferInspect(id, row, args.depth) };
      }
      case 'entity': {
        const action = args.action;
        if (action === 'finalize') {
          const buffsArg = args.buffs && typeof args.buffs === 'object' && !Array.isArray(args.buffs)
            ? args.buffs
            : {};
          const entries = (args.entities ?? []).map((id) => {
            const row = entities.get(id);
            const listed = Array.isArray(buffsArg[id]) ? buffsArg[id].map(String) : [];
            if (!row) {
              return { entity: id, phase: 'tagged', destroyTagAdded: false, staleRows: [] };
            }
            const present = () => (Array.isArray(row.buffs) ? row.buffs.map(String) : []);
            const pendingOf = () => {
              const have = new Set(present());
              return (listed.length ? listed : present()).filter((b) => have.has(b));
            };
            const staleRows = staleRowsOf(row);

            if (row.stuckOnFinalize) {
              return { entity: id, phase: 'stuck', reason: row.stuckReason ?? 'buff cycle cap', staleRows };
            }

            const pending = pendingOf();
            if (pending.length) {
              vanishBuffs(id, row);
              return { entity: id, phase: 'waiting-buffs', pending, staleRows };
            }

            const cycled = takeNewBuffCycle(row);
            if (cycled?.stuck) {
              return { entity: id, phase: 'stuck', reason: cycled.reason, staleRows };
            }
            if (cycled?.buffs) {
              return { entity: id, phase: 'buffs', buffs: cycled.buffs, cycle: cycled.cycle, staleRows };
            }

            if (!sticky.has(id)) row.exists = false;
            row.attached = false;
            return { entity: id, phase: 'tagged', destroyTagAdded: true, staleRows };
          });
          return { ok: true, result: { action, count: entries.length, entries, errors: [] } };
        }
        const entries = (args.entities ?? []).map((id) => {
          const row = entities.get(id);
          if (action === 'destroy') {
            if (!row) {
              if (omitDestroyPhase) return { entity: id, exists: false, prefab: null };
              if (oldDetachPhase) {
                return {
                  entity: id,
                  phase: 'detach',
                  buffs: [],
                  staleRows: [],
                  buffSweepComplete: true,
                  existsAfterRequest: false,
                  exists: false,
                  prefab: null,
                };
              }
              return {
                entity: id,
                phase: 'buffs',
                buffs: [],
                staleRows: [],
                buffSweepComplete: true,
                existsAfterRequest: false,
                exists: false,
                prefab: null,
              };
            }
            if (unitOmitsPhase(id, row)) {
              const exists = !!row.exists;
              return { entity: id, exists, prefab: prefabInfo(row, exists) };
            }
            const buffs = ensureBuffs(row, id).slice();
            const exists = !!row.exists;
            if (row.newBuffCycles == null) row.newBuffCycles = newBuffCyclesDefault;
            if (row.buffCycle == null) row.buffCycle = 0;
            if (unitOldDetachPhase(id, row)) {
              return {
                entity: id,
                phase: 'detach',
                buffs,
                staleRows: staleRowsOf(row),
                buffSweepComplete: true,
                existsAfterRequest: exists,
                exists,
                prefab: prefabInfo(row, exists),
              };
            }
            if (unitSweepIncomplete(id, row)) {
              return {
                entity: id,
                phase: 'buffs',
                buffs,
                staleRows: staleRowsOf(row),
                buffSweepComplete: false,
                existsAfterRequest: exists,
                exists,
                prefab: prefabInfo(row, exists),
                reason: row.sweepReason ?? sweepIncompleteReason,
              };
            }
            emitDestroyLogs(id, row, buffs);
            if (opts.dieOnDestroy) dead = true;
            return {
              entity: id,
              phase: 'buffs',
              buffs,
              staleRows: staleRowsOf(row),
              buffSweepComplete: true,
              existsAfterRequest: exists,
              exists,
              prefab: prefabInfo(row, exists),
            };
          }
          const exists = row ? !!row.exists : false;
          return { entity: id, exists, prefab: prefabInfo(row, exists) };
        });
        return { ok: true, result: { action, count: entries.length, entries, errors: [] } };
      }
      case 'get': {
        if (args.entity === players[0].entity && args.component === 'Health') {
          return {
            ok: true,
            result: { entity: args.entity, component: 'Health', fields: opts.playerHealth ?? { Value: 100, IsDead: false } },
          };
        }
        const row = entities.get(args.entity);
        if (row && args.component === 'Health') {
          return { ok: true, result: { entity: args.entity, component: 'Health', fields: row.health } };
        }
        return { ok: true, result: { entity: args.entity, component: args.component, fields: {} } };
      }
      case 'command': {
        const text = args.text ?? '';
        if (args.user) {
          const named = users.find((u) => u.name === args.user);
          if (named && named.connected === false) {
            return { ok: false, error: `no connected user named '${args.user}'` };
          }
        }
        const caller = users.find((u) => u.name === args.user) ?? users[0];
        const isAdmin = !!caller?.admin;
        if (text === '.debug status') {
          // Core debug buff, admin-only: a non-admin caller gets the chat echo and no
          // flags= line. The readback does not depend on the Sample being loaded.
          if (!isAdmin) {
            pushLog('server', `Sample chat: saw type=Local from=${args.user ?? caller?.name ?? 'unknown'} handled=True text=".debug status"`);
          } else {
            let line = OFF_STATUS;
            if (protectionOn) line = protectionGuard ? OK_STATUS : GUARD_DROPPED_STATUS;
            pushLog('server', line);
          }
        }
        if (text === '.debug on' && isAdmin && !rearmFails) {
          protectionOn = true;
          protectionGuard = true;
        }
        if (typeof text === 'string' && /^\.frostdash\b/.test(text)) {
          if (sampleLoaded && isAdmin) {
            pushLog('server', `Sample chat: saw type=Local from=${args.user ?? caller?.name ?? 'unknown'} handled=True text="${text}"`);
            if (text === '.frostdash dash') {
              const entity = `${nextIndex++}:1`;
              entities.set(entity, { exists: true, prefab: 'frost-ring', side: 'server' });
              pushLog('server', `FROSTDASH edge=Start frame=1 ring=Entity(${entity}) position=(0, 0, 0) player=1:1 castCounter=1 group=0`);
            }
            if (text === '.frostdash off') {
              const destroyed = [];
              for (const [id, row] of entities) {
                if (row.prefab === 'frost-ring' && row.exists !== false) {
                  destroyed.push(id);
                  row.exists = false;
                }
              }
              pushLog('server', `FROSTDASH cleanup alive=${destroyed.length} destroyed=[${destroyed.join(',')}]`);
            }
          }
        }
        return { ok: true, result: { injectedAs: args.user, userIndex: 1, text } };
      }
      case 'logs': {
        maybeEmitScratch(side);
        const since = args.since ?? -1;
        const filter = String(args.filter ?? '').toLowerCase();
        let lines = logs[side].filter((l) => {
          if (l.seq <= since) return false;
          if (!filter) return true;
          return `${l.text} ${l.source}`.toLowerCase().includes(filter);
        });
        if (typeof args.max === 'number' && Number.isFinite(args.max) && args.max >= 0) {
          lines = lines.slice(0, args.max);
        }
        const latest = logs[side].length ? logs[side][logs[side].length - 1].seq : 0;
        return { ok: true, result: { latestSeq: latest, count: lines.length, lines } };
      }
      case 'ops':
        return {
          ok: true,
          result: {
            ops: ['ping', 'logs', 'bridge_status', 'dump', 'reload', 'reload_status', 'core_reload', 'ops', 'mods_ui', 'command', 'query', 'get', 'set', 'spawn', 'entity', 'connect', 'disconnect', 'quit', 'perf', 'perf_status'],
          },
        };
      case 'mods_ui': {
        const action = String(args.action ?? 'status');
        const before = snapshotModsUi();
        const needsOpen = new Set(['select', 'search', 'scroll', 'widget_read', 'widget_click', 'back']);
        if (needsOpen.has(action) && !modsUi.open) {
          return modsUiEnvelope(false, action, before, snapshotModsUi(), { error: 'workspace closed' });
        }
        if (action === 'status') {
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { readback: 'same-drain' });
        }
        if (action === 'open') {
          const site = args.site ?? 'main-menu';
          if (!['main-menu', 'top-tab', 'shortcut'].includes(site)) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: `args.site must be main-menu, top-tab, or shortcut (got '${site}')`,
            });
          }
          const detail = {
            path: site,
            handler: site === 'shortcut' ? 'ModsMenu.Toggle' : `progenitor-ui-core-${site === 'main-menu' ? 'main-mods' : 'mods-tab'}.onClick`,
            readback: 'next-drain',
          };
          if (site === 'top-tab') {
            if (modsUi.open) {
              detail.alreadyOpen = true;
              detail.menuOpenedBy = 'already';
              return modsUiEnvelope(true, action, before, snapshotModsUi(), detail);
            }
            if (!modsUi.fullscreenMenu) {
              if (modsUi.refuseTopTabAttach) {
                return modsUiEnvelope(false, action, before, snapshotModsUi(), {
                  error: 'top-tab button not attached',
                  path: site,
                  readback: 'next-drain',
                });
              }
              modsUi.fullscreenMenu = true;
              detail.menuOpenedBy = 'open';
            } else {
              detail.menuOpenedBy = 'already';
            }
          }
          openModsUi(site);
          if (site === 'top-tab') modsUi.fullscreenMenu = true;
          return modsUiEnvelope(true, action, before, snapshotModsUi(), detail);
        }
        if (action === 'close') {
          closeModsUi();
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { path: 'close', readback: 'next-drain' });
        }
        if (action === 'back') {
          if (modsUi.page === 'settings' || modsUi.page === 'custom') {
            modsUi.page = 'overview';
            modsUi.pageId = '';
          }
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { path: 'back-to-mod', readback: 'same-drain' });
        }
        if (action === 'select') {
          const page = String(args.page ?? '').trim().toLowerCase();
          if (!['overview', 'settings', 'custom'].includes(page)) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.page is required: overview, settings, or custom',
            });
          }
          modsUi.plugin = args.plugin ?? modsUi.plugin;
          modsUi.page = page;
          modsUi.pageId = page === 'overview' ? '' : String(args.group ?? '');
          return modsUiEnvelope(true, action, before, snapshotModsUi(), {
            path: page,
            plugin: modsUi.plugin,
            page,
            group: args.group ?? null,
          });
        }
        if (action === 'search') {
          if (!Object.prototype.hasOwnProperty.call(args, 'text')) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.text is required (empty string clears)',
            });
          }
          modsUi.search = args.text ?? '';
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { text: modsUi.search });
        }
        if (action === 'scroll') {
          const pane = String(args.pane ?? '').trim().toLowerCase();
          if (pane !== 'list' && pane !== 'content') {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.pane is required: list or content',
            });
          }
          if (args.to == null) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.to is required: top, bottom, or a number',
            });
          }
          modsUi.scroll = { pane, to: args.to };
          const normalized = args.to === 'bottom' ? 0 : 1;
          if (pane === 'list') modsUi.listScroll = normalized;
          if (pane === 'content') modsUi.contentScroll = normalized;
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { pane, to: args.to, normalized });
        }
        if (action === 'widget_read') {
          const pane = args.pane == null || args.pane === '' ? null : String(args.pane).trim().toLowerCase();
          if (pane != null && pane !== 'list' && pane !== 'content') {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.pane is required: list or content',
            });
          }
          if (modsUi.pendingNote != null || modsUi.pendingCommit != null) {
            modsUi.pendingReads += 1;
            if (modsUi.pendingReads >= 2) {
              resolvePendingExampleLimit();
            }
          }
          const rows = [];
          if (pane == null || pane === 'list') {
            for (const id of modsUi.plugins) rows.push({ id, kind: 'plugin', pane: 'list' });
          }
          if (pane == null || pane === 'content') {
            const waiting = /Waiting for the server/i.test(String(modsUi.note ?? ''));
            rows.push({
              kind: modsUi.page ?? 'overview',
              selected: modsUi.plugin,
              label: 'mods found on this client',
              text: modsUi.page === 'overview'
                ? (serverListAvailable
                  ? 'mods found on this client. Server mod list received (5 entries).'
                  : 'mods found on this client. Server mod list unavailable. Known requirements are shown where available.')
                : `ShowHints value=${modsUi.values.ShowHints} ExampleLimit value=${modsUi.values.ExampleLimit} source=admin synced-readonly`,
              value: modsUi.pageId === 'Server' ? modsUi.values.ExampleLimit : modsUi.values.ShowHints,
              source: modsUi.page === 'settings' ? 'admin' : undefined,
            });
            if (modsUi.page === 'settings') {
              rows.push({
                kind: 'label',
                label: modsUi.note,
                text: modsUi.note,
                note: modsUi.note,
                source: 'admin',
                pending: waiting,
              });
            }
          }
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { pane, count: rows.length, rows });
        }
        if (action === 'widget_click') {
          const label = String(args.label ?? '');
          if (/set by the server/i.test(label)) {
            return modsUiEnvelope(true, action, before, snapshotModsUi(), { invoked: 0, label });
          }
          if (/ShowHints/i.test(label) && args.value != null) modsUi.values.ShowHints = String(args.value);
          if (args.index === 3 && args.value != null) modsUi.values.ExampleLimit = String(args.value);
          if (/Apply ExampleLimit/i.test(label)) {
            const typed = String(modsUi.values.ExampleLimit ?? '');
            const n = Number(typed);
            const isAdmin = callerIsAdmin();
            let resultNote;
            let pendingCommit = null;
            if (!isAdmin) resultNote = 'Only server administrators can change this setting.';
            else if (!Number.isFinite(n) || n < 1 || n > 20) resultNote = 'Outside the values this setting accepts.';
            else {
              resultNote = 'Saved by the server.';
              pendingCommit = typed;
            }
            modsUi.note = 'Waiting for the server…';
            modsUi.pendingNote = resultNote;
            modsUi.pendingCommit = pendingCommit;
            modsUi.pendingReads = 0;
          }
          if (/Try an action/i.test(label)) {
            pushLog('client', 'Sample menu: action handled.', { source: 'Progenitor Sample' });
          }
          return modsUiEnvelope(true, action, before, snapshotModsUi(), { invoked: 1, label, index: args.index ?? null });
        }
        if (action === 'native_tab') {
          const same = args.same === true || args.same === 'true';
          const tab = args.tab;
          if (!same && (tab == null || tab === '')) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.tab (name or index) is required when args.same is not true',
              readback: 'next-drain',
            });
          }
          closeModsUi();
          modsUi.nativeTab = same ? 'same' : String(tab);
          return modsUiEnvelope(true, action, before, snapshotModsUi(), {
            tab: modsUi.nativeTab,
            same,
            handler: 'OuterTabEntry.OnPointerClick+EventTrigger.OnPointerClick',
            readback: 'next-drain',
          });
        }
        if (action === 'resolution') {
          const width = Number(args.width);
          const height = Number(args.height);
          const mode = String(args.mode ?? '').trim().toLowerCase();
          if (!Number.isFinite(width) || width <= 0) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), { error: 'args.width is required' });
          }
          if (!Number.isFinite(height) || height <= 0) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), { error: 'args.height is required' });
          }
          if (!['window', 'windowed', 'fullscreenwindow', 'exclusive'].includes(mode)) {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.mode is required: window, windowed, fullscreenwindow, or exclusive',
            });
          }
          modsUi.width = width;
          modsUi.height = height;
          modsUi.mode = mode;
          return modsUiEnvelope(true, action, before, snapshotModsUi(), {
            requestedWidth: width,
            requestedHeight: height,
            requestedMode: mode,
            readback: 'next-drain',
          });
        }
        if (action === 'rects') {
          const pane = args.pane == null || args.pane === '' ? null : String(args.pane).trim().toLowerCase();
          if (pane != null && pane !== 'list' && pane !== 'content' && pane !== 'entry' && pane !== 'native') {
            return modsUiEnvelope(false, action, before, snapshotModsUi(), {
              error: 'args.pane is list, content, entry, native, or omitted',
            });
          }
          const detail = snapshotRects();
          return modsUiEnvelope(true, action, before, snapshotModsUi(), {
            ...detail,
            readback: 'same-drain',
          });
        }
        if (action === 'window') {
          return modsUiEnvelope(true, action, before, snapshotModsUi(), snapshotWindow());
        }
        return { ok: false, error: `unknown mods_ui action '${action}'` };
      }
      case 'reload': {
        const present = samplePresentFn ? !!samplePresentFn() : sampleInFolder;
        sampleInFolder = present;
        if (!present) {
          // Removal leaves the Core-owned debug buff alone.
          sampleLoaded = false;
          const destroyed = [];
          for (const [id, row] of entities) {
            if (row.prefab === 'frost-ring' && row.exists !== false) {
              destroyed.push(id);
              row.exists = false;
            }
          }
          pushLog(side, `FROSTDASH cleanup alive=${destroyed.length} destroyed=[${destroyed.join(',')}]`);
          pushLog(side, 'Reload: Progenitor.Sample.dll removed, unloading (unload reason=Removal).');
          pushLog(side, 'Reload: unloaded Progenitor Sample: clean');
        } else if (!sampleLoaded) {
          sampleLoaded = true;
          if (side === 'client') {
            if (!clientSampleHash) clientSampleHash = DEFAULT_SAMPLE_HASH;
          } else if (!sampleHash) {
            sampleHash = DEFAULT_SAMPLE_HASH;
          }
          pushLog(side, `Reload: image Progenitor.Sample.dll sha256=${sideSampleHash(side)} size=377856`);
          pushLog(side, 'Reload: loaded Progenitor Sample 0.1.0 from Progenitor.Sample.dll.');
        } else {
          const nextHash = opts.swapSampleHash ?? SWAP_SAMPLE_HASH;
          if (side === 'client') clientSampleHash = nextHash;
          else sampleHash = nextHash;
          if (dropGuardOnSwap) protectionGuard = false;
          pushLog(side, 'Reload: Progenitor.Sample.dll changed, reloading (unload reason=Swap).');
          pushLog(side, 'Reload: unloaded Progenitor Sample: clean');
          pushLog(side, `Reload: image Progenitor.Sample.dll sha256=${sideSampleHash(side)} size=377344`);
          pushLog(side, 'Reload: loaded Progenitor Sample 0.1.0 from Progenitor.Sample.dll.');
          pushLog(side, 'Sample handshake: declared 4 row(s) on the server side costing 2 of 16 slot(s); every one is weaker than this plugin\'s own ServerMustHave/Exact row, so no new join can be refused because of them. `.hs` prints them.', { source: 'Progenitor Sample' });
        }
        return { ok: true, result: { events: [], loaded: [] } };
      }
      case 'reload_status':
        return {
          ok: true,
          result: {
            ok: true,
            status: {
              generation,
              alcName: `progenitor-core:${generation}`,
              inFlight: false,
              previousAlive,
              lastReload,
            },
          },
        };
      case 'core_reload': {
        generation += 1;
        previousAlive = false;
        lastReload = {
          gen: generation,
          outcome: 'ok',
          reason: 'bridge core_reload',
          timestamp: 't1',
        };
        closeModsUi();
        return {
          ok: true,
          result: {
            ok: true,
            scheduled: true,
            result: `scheduled gen=${generation}`,
          },
        };
      }
      case 'disconnect': {
        if (side === 'server') {
          return { ok: false, error: 'disconnect runs on the client side only' };
        }
        const statusOnly = args?.statusOnly === true;
        const hasWorld = clientHasGameWorld();
        let state = observeLeave(statusOnly);
        if (state === 'request') {
          applyLeaveEffects();
          state = 'pending';
        }
        return disconnectResult(state, hasWorld);
      }
      case 'perf': {
        const action = String(args.action ?? 'status');
        if (action === 'start') {
          maybeCloseFakePerfDeadline();
          if (perf.running) {
            return { ok: false, error: 'a perf window is already running; call perf {"action":"stop"} first' };
          }
          const seconds = Math.max(1, Math.min(300, Number(args.seconds) || 30));
          perf.running = true;
          perf.historical = false;
          perf.captureId = perf.nextId++;
          perf.startedAtMs = nowMs();
          perf.lastSampleAtMs = perf.startedAtMs;
          perf.seconds = seconds;
          perf.frames = 0;
          perf.lastFrameMs = null;
          perf.lastCoreTickMs = null;
          perf.maxFrameMs = null;
          perf.sampleSilenceMs = 0;
          perf.path = null;
          perf.summary = null;
          perf.stopReason = null;
          return {
            ok: true,
            result: {
              running: true,
              side,
              seconds,
              captureId: perf.captureId,
              note: 'sampling every frame; call perf {"action":"stop"} for the report, or let the deadline close it.',
            },
          };
        }
        maybeCloseFakePerfDeadline();
        if (action === 'stop') {
          if (perf.running) closeFakePerf('stop');
          else if (!perf.path && !perf.summary) {
            return { ok: false, error: 'no perf window is running; start one first' };
          }
          return {
            ok: true,
            result: {
              running: false,
              path: perf.path,
              summary: perf.summary,
              captureId: perf.captureId || null,
              note: 'the window had already closed; this is the finished report',
            },
          };
        }
        if (!perf.running) {
          return {
            ok: true,
            result: {
              running: false,
              path: perf.path,
              summary: perf.summary,
              captureId: perf.captureId || null,
            },
          };
        }
        const elapsedSeconds = perf.startedAtMs == null ? 0 : Math.max(0, (nowMs() - perf.startedAtMs) / 1000);
        return {
          ok: true,
          result: {
            running: true,
            frames: perf.frames,
            elapsedSeconds,
            remainingSeconds: Math.max(0, perf.seconds - elapsedSeconds),
            captureId: perf.captureId,
          },
        };
      }
      case 'perf_status': {
        tickFakePerf();
        const progress = fakePerfProgress();
        return {
          ok: true,
          result: {
            running: perf.running,
            historical: !perf.running && progress != null,
            captureId: perf.captureId || null,
            progress,
            note: 'Silence is time since the last completed sample (or capture start). It is not an attributed frame duration or proof of a freeze. When running=false these are retained capture values.',
          },
        };
      }
      case 'connect': {
        clientWorlds = ['Client_0'];
        clientWorldEpoch += 1;
        for (const row of users) {
          row.connected = true;
          row.handshake = 'ok';
        }
        for (const row of clientUsers) {
          row.connected = true;
          if ('handshake' in row) row.handshake = 'ok';
        }
        serverListAvailable = true;
        serverListEpoch += 1;
        if (!serverContextLive) {
          serverContextLive = true;
          pushLog('client', 'Mods inventory: server context restored reason=handshake-ok epoch=1');
        }
        return { ok: true, result: { ok: true } };
      }
      default:
        return { ok: false, error: `unknown op '${op}'` };
    }
  }

  return {
    request,
    calls,
    dumpCalls,
    adminWrites,
    entities,
    sticky,
    addCarrier,
    carriersBySide,
    keepBuffPresent,
    sweepIncomplete,
    players,
    users,
    logs,
    get pushLog() { return pushLogFn; },
    set pushLog(fn) { if (typeof fn === 'function') pushLogFn = fn; },
    setProtection(v) { protectionOn = v; },
    setProtectionGuard(v) { protectionGuard = v; },
    setRearmFails(v) { rearmFails = !!v; },
    setSampleLoaded(v) { sampleLoaded = v; sampleInFolder = v; },
    setSampleInFolder(v) { sampleInFolder = v; },
    setSamplePresent(fn) { samplePresentFn = fn; },
    throwOn(op, error, extra = {}) { throwOn.push({ op, error, once: extra.once !== false, side: extra.side, action: extra.action }); },
    setDropGuardOnSwap(v) { dropGuardOnSwap = !!v; },
    setSampleHash(v) { sampleHash = v; clientSampleHash = v; },
    setClientSampleHash(v) { clientSampleHash = v; },
    get clientSampleHash() { return clientSampleHash; },
    setGeneration(v) { generation = v; },
    setUptimeSeconds(v) { uptimeSeconds = v; },
    setOmitDestroyPhase(v) { omitDestroyPhase = !!v; },
    setOldDetachPhase(v) { oldDetachPhase = !!v; },
    setOldTwoPhase(v) { oldDetachPhase = !!v; },
    setKeepAllBuffs(v) { keepAllBuffs = !!v; },
    setSweepIncompleteAll(v) { sweepIncompleteAll = !!v; },
    setSweepIncompleteReason(v) { sweepIncompleteReason = v; },
    setNewBuffCycles(v) { newBuffCyclesDefault = Number(v) || 0; },
    setFatalLog(v) { fatalLogOnDestroy = v; },
    setDead(v) { dead = !!v; },
    freezeBridgeFrame(on = true) { holdBridgeFrame = !!on; },
    setBridgeFrame(n) { drainFrame = Number(n) || 0; },
    setLastFreezeDump(v) { lastFreezeDump = v; },
    setNow(fn) { nowFn = typeof fn === 'function' ? fn : null; },
    nowMs,
    perf,
    get bridgeFrame() { return drainFrame; },
    get lastFreezeDump() { return lastFreezeDump; },
    get holdBridgeFrame() { return holdBridgeFrame; },
    get omitDestroyPhase() { return omitDestroyPhase; },
    get oldDetachPhase() { return oldDetachPhase; },
    get oldTwoPhase() { return oldDetachPhase; },
    get keepAllBuffs() { return keepAllBuffs; },
    get sweepIncompleteAll() { return sweepIncompleteAll; },
    get dead() { return dead; },
    get sampleLoaded() { return sampleLoaded; },
    get protectionOn() { return protectionOn; },
    get protectionGuard() { return protectionGuard; },
    get sampleHash() { return sampleHash; },
    get clientWorlds() { return pingClientWorlds(); },
    get clientWorldEpoch() { return clientWorldEpoch; },
    get leaveRequested() { return leaveRequested; },
    reloadFolder,
    modsUi,
  };
}

export function testBridge(runDir, world) {
  return createBridge({
    runDir,
    transport: { request: (side, op, args) => world.request(side, op, args) },
  });
}

export function installFakeSample(world, side = 'server') {
  const folder = world.reloadFolder[side];
  mkdirSync(folder, { recursive: true });
  const dest = path.join(folder, 'Progenitor.Sample.dll');
  writeFileSync(dest, 'dll');
  world.setSamplePresent(() => existsSync(dest));
  return dest;
}

export function runOpts(tmp, world, extra = {}) {
  const clock = extra.clock ?? fakeClock();
  const lines = extra.lines ?? [];
  return {
    bridge: extra.bridge ?? testBridge(tmp.dir, world),
    runDir: tmp.dir,
    outDir: tmp.dir,
    clock,
    log: (s) => lines.push(s),
    cleanupTimeoutMs: extra.cleanupTimeoutMs ?? 1000,
    cleanupPollMs: extra.cleanupPollMs ?? 100,
    gameInstall: extra.gameInstall ?? path.join(tmp.dir, 'no-game-install'),
    ...extra,
    lines,
  };
}

function isObserveShot(cmd, args) {
  const base = String(cmd ?? '').replace(/\\/g, '/').toLowerCase();
  const argv = (args ?? []).map(String);
  const isPs = base.endsWith('powershell.exe') || base.endsWith('powershell') || base.includes('windowspowershell');
  return isPs && argv.includes('shot');
}

const OBSERVE_INPUT_ACTIONS = new Set(['click', 'move', 'scroll', 'key', 'type']);

function isObserveInput(cmd, args) {
  const base = String(cmd ?? '').replace(/\\/g, '/').toLowerCase();
  const argv = (args ?? []).map(String);
  const isPs = base.endsWith('powershell.exe') || base.endsWith('powershell') || base.includes('windowspowershell');
  if (!isPs) return false;
  return argv.some((a) => OBSERVE_INPUT_ACTIONS.has(String(a)));
}

function argValue(args, flag) {
  const argv = args ?? [];
  const i = argv.findIndex((a) => String(a) === flag);
  if (i < 0 || i + 1 >= argv.length) return null;
  return String(argv[i + 1]);
}

/**
 * Injected spawnSync for capture tests. Writes a PNG-sized file at `-Out` and
 * never launches PowerShell or observe.ps1. Pass `inner` to forward `dotnet build`.
 */
export function fakeObserveSpawner(opts = {}) {
  const io = opts.fs ?? { mkdirSync, writeFileSync };
  const calls = [];
  function spawnSync(cmd, args, spawnOpts) {
    calls.push({ cmd, args, spawnOpts });
    if (isObserveInput(cmd, args)) {
      if (typeof opts.onInput === 'function') opts.onInput({ cmd, args, spawnOpts, calls });
      const action = (args ?? []).map(String).find((a) => OBSERVE_INPUT_ACTIONS.has(a)) ?? 'input';
      const exitCode = opts.inputExitCode == null ? (opts.exitCode == null ? 0 : opts.exitCode) : opts.inputExitCode;
      if (opts.timeout && opts.timeoutInput !== false) {
        return {
          status: null,
          signal: 'SIGTERM',
          error: { code: 'ETIMEDOUT' },
          stdout: opts.stdout ?? '',
          stderr: opts.stderr ?? '',
          pid: 0,
        };
      }
      return {
        status: exitCode,
        stdout: opts.stdout ?? JSON.stringify({ ok: exitCode === 0, action, exitCode }),
        stderr: opts.stderr ?? '',
        pid: 0,
      };
    }
    if (!isObserveShot(cmd, args)) {
      if (typeof opts.inner === 'function') return opts.inner(cmd, args, spawnOpts);
      throw new Error(`fakeObserveSpawner: unexpected command ${cmd}`);
    }
    const outPath = argValue(args, '-Out');
    if (typeof opts.onShot === 'function') opts.onShot({ cmd, args, spawnOpts, outPath, calls });
    if (opts.timeout) {
      return {
        status: null,
        signal: 'SIGTERM',
        error: { code: 'ETIMEDOUT' },
        stdout: opts.stdout ?? '',
        stderr: opts.stderr ?? '',
        pid: 0,
      };
    }
    const exitCode = opts.exitCode == null ? 0 : opts.exitCode;
    if (exitCode !== 0) {
      return {
        status: exitCode,
        stdout: opts.stdout ?? '',
        stderr: opts.stderr ?? 'observe: failed',
        pid: 0,
      };
    }
    if (outPath && !opts.missingFile) {
      io.mkdirSync(path.dirname(outPath), { recursive: true });
      if (opts.zeroByte) io.writeFileSync(outPath, Buffer.alloc(0));
      else io.writeFileSync(outPath, opts.bytes ?? Buffer.alloc(128, 1));
    }
    return {
      status: 0,
      stdout: opts.stdout ?? '',
      stderr: opts.stderr ?? '',
      pid: 0,
    };
  }
  spawnSync.calls = calls;
  return spawnSync;
}

/**
 * Injected child_process.spawn for capture tests. Completes asynchronously so a
 * 1 s PrintWindow cannot sit on the fake-clock census wait. Pass `hang: true`
 * to leave the child running until kill() or collectTimeoutMs.
 */
export function fakeObserveAsyncSpawner(opts = {}) {
  const io = opts.fs ?? { mkdirSync, writeFileSync };
  const calls = [];
  let inFlight = 0;
  let maxInFlight = 0;
  function spawn(cmd, args, spawnOpts) {
    calls.push({ cmd, args, spawnOpts });
    const isInput = isObserveInput(cmd, args);
    if (!isObserveShot(cmd, args) && !isInput) {
      if (typeof opts.inner === 'function') return opts.inner(cmd, args, spawnOpts);
      throw new Error(`fakeObserveAsyncSpawner: unexpected command ${cmd}`);
    }
    inFlight += 1;
    if (inFlight > maxInFlight) maxInFlight = inFlight;
    const outPath = argValue(args, '-Out');
    const stdout = new PassThrough();
    const stderr = new PassThrough();
    const child = new EventEmitter();
    child.stdout = stdout;
    child.stderr = stderr;
    child.pid = 1;
    child.exitCode = null;
    child.killed = false;
    let done = false;
    let timer;
    function finish(status, signal, error) {
      if (done) return;
      done = true;
      if (timer) clearTimeout(timer);
      inFlight = Math.max(0, inFlight - 1);
      child.exitCode = status;
      child.signalCode = signal ?? null;
      if (opts.stdout) stdout.write(opts.stdout);
      if (opts.stderr) stderr.write(opts.stderr);
      stdout.end();
      stderr.end();
      if (error) child.emit('error', error);
      child.emit('close', status, signal ?? null);
    }
    child.kill = (sig = 'SIGTERM') => {
      child.killed = true;
      if (opts.ignoreKill) return;
      finish(null, sig ?? 'SIGTERM', null);
    };
    if (typeof opts.onShot === 'function') opts.onShot({ cmd, args, spawnOpts, outPath, calls, child });
    if (typeof opts.onInput === 'function' && isInput) opts.onInput({ cmd, args, spawnOpts, calls, child });
    const startWork = () => {
      if (done) return;
      if (isInput) {
        if (opts.timeout && opts.timeoutInput !== false) {
          finish(null, 'SIGTERM', { code: 'ETIMEDOUT' });
          return;
        }
        const action = (args ?? []).map(String).find((a) => OBSERVE_INPUT_ACTIONS.has(a)) ?? 'input';
        const exitCode = opts.inputExitCode == null ? (opts.exitCode == null ? 0 : opts.exitCode) : opts.inputExitCode;
        if (!opts.stdout) stdout.write(JSON.stringify({ ok: exitCode === 0, action, exitCode }));
        finish(exitCode, null, null);
        return;
      }
      if (opts.timeout) {
        finish(null, 'SIGTERM', { code: 'ETIMEDOUT' });
        return;
      }
      const exitCode = opts.exitCode == null ? 0 : opts.exitCode;
      if (exitCode !== 0) {
        finish(exitCode, null, null);
        return;
      }
      if (outPath && !opts.missingFile) {
        io.mkdirSync(path.dirname(outPath), { recursive: true });
        if (opts.zeroByte) io.writeFileSync(outPath, Buffer.alloc(0));
        else io.writeFileSync(outPath, opts.bytes ?? Buffer.alloc(128, 1));
      }
      finish(0, null, null);
    };
    if (opts.hang) return child;
    const duration = Number(opts.durationMs) || 0;
    if (duration > 0) timer = setTimeout(startWork, duration);
    else queueMicrotask(startWork);
    return child;
  }
  spawn.calls = calls;
  Object.defineProperty(spawn, 'inFlight', { get: () => inFlight });
  Object.defineProperty(spawn, 'maxInFlight', { get: () => maxInFlight });
  return spawn;
}
