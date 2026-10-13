/** Bounded post-run scan of server logs for DIAG5 detach signals. */
export const LOG_SCAN_MAX_LINES = 400;
export const LOG_SCAN_FILTER = '';

export const EXPECTED_DETACH_SNIPPETS = Object.freeze({
  destroyedWithoutBeingDetached: 'destroyed without being detached',
  detachEntityInformation: 'DetachEntity information',
});

export const FATAL_LOG_SNIPPETS = Object.freeze({
  triedToDetachAttachedBuffer: Object.freeze(['Tried to detach', 'AttachedBuffer']),
  pointsAtBuffTarget: 'points at buff.Target',
  burstException: 'This Exception was thrown from a job compiled with Burst',
});

const EMPTY_EXPECTED = () => ({
  destroyedWithoutBeingDetached: 0,
  detachEntityInformation: 0,
});

const EMPTY_FATAL = () => ({
  triedToDetachAttachedBuffer: 0,
  pointsAtBuffTarget: 0,
  burstException: 0,
  bridgeUnanswered: 0,
});

function lineText(line) {
  if (line == null) return '';
  if (typeof line === 'string') return line;
  return String(line.text ?? '');
}

function firstFatalReason(fatal, fatalLine) {
  if (fatal.triedToDetachAttachedBuffer) return fatalLine ?? 'Tried to detach';
  if (fatal.pointsAtBuffTarget) return fatalLine ?? 'points at buff.Target';
  if (fatal.burstException) return fatalLine ?? 'This Exception was thrown from a job compiled with Burst';
  if (fatal.bridgeUnanswered) return 'server bridge not answering';
  return null;
}

export function emptyLogSignals(extra = {}) {
  return {
    scanned: extra.scanned ?? false,
    sinceSeq: extra.sinceSeq ?? -1,
    lineCount: extra.lineCount ?? 0,
    expected: EMPTY_EXPECTED(),
    fatal: EMPTY_FATAL(),
    reason: extra.reason ?? null,
    error: extra.error ?? undefined,
  };
}

/** Count expected vs fatal DIAG5 log lines. Expected lines never fail a run. */
export function classifyLogSignals(lines) {
  const expected = EMPTY_EXPECTED();
  const fatal = EMPTY_FATAL();
  let fatalLine = null;
  for (const line of lines ?? []) {
    const text = lineText(line);
    if (text.includes(FATAL_LOG_SNIPPETS.triedToDetachAttachedBuffer[0])
      && text.includes(FATAL_LOG_SNIPPETS.triedToDetachAttachedBuffer[1])) {
      fatal.triedToDetachAttachedBuffer += 1;
      if (!fatalLine) fatalLine = text;
    }
    if (text.includes(FATAL_LOG_SNIPPETS.pointsAtBuffTarget)) {
      fatal.pointsAtBuffTarget += 1;
      if (!fatalLine) fatalLine = text;
    }
    if (text.includes(FATAL_LOG_SNIPPETS.burstException)) {
      fatal.burstException += 1;
      if (!fatalLine) fatalLine = text;
    }
    if (text.includes(EXPECTED_DETACH_SNIPPETS.destroyedWithoutBeingDetached)) {
      expected.destroyedWithoutBeingDetached += 1;
    }
    if (text.includes(EXPECTED_DETACH_SNIPPETS.detachEntityInformation)) {
      expected.detachEntityInformation += 1;
    }
  }
  return { expected, fatal, fatalLine };
}

export function hasFatalLogSignals(logSignals) {
  const fatal = logSignals?.fatal;
  if (!fatal) return false;
  return (fatal.triedToDetachAttachedBuffer ?? 0) > 0
    || (fatal.pointsAtBuffTarget ?? 0) > 0
    || (fatal.burstException ?? 0) > 0
    || (fatal.bridgeUnanswered ?? 0) > 0;
}

function errorText(err) {
  if (err == null) return 'unknown';
  if (typeof err === 'string') return err;
  if (typeof err === 'object') return err.error ?? err.message ?? JSON.stringify(err);
  return String(err);
}

/**
 * Post-run server log scan: lines since `since` (max 400, filter ""), then a
 * server ping. Fatal counts make the run incomplete; expected detach lines do not.
 */
export async function scanServerLogSignals(bridge, opts = {}) {
  const since = opts.since ?? -1;
  const max = opts.max ?? LOG_SCAN_MAX_LINES;
  let lines = [];
  let scanned = true;
  let scanError = null;
  try {
    const r = await bridge.call('server', 'logs', { since, max, filter: LOG_SCAN_FILTER });
    const raw = Array.isArray(r?.lines) ? r.lines : [];
    lines = raw.slice(0, max);
  } catch (e) {
    scanned = false;
    scanError = errorText(e && e.message ? e.message : e);
  }

  let bridgeUnanswered = 0;
  let pingError = null;
  try {
    await bridge.call('server', 'ping');
  } catch (e) {
    bridgeUnanswered = 1;
    pingError = errorText(e && e.message ? e.message : e);
  }

  const classified = classifyLogSignals(lines);
  classified.fatal.bridgeUnanswered = bridgeUnanswered;
  const reason = firstFatalReason(classified.fatal, classified.fatalLine)
    ?? (bridgeUnanswered ? (pingError ?? 'server bridge not answering') : null);

  const logSignals = {
    scanned,
    sinceSeq: since,
    lineCount: lines.length,
    expected: classified.expected,
    fatal: classified.fatal,
    reason,
  };
  if (scanError) logSignals.error = scanError;
  if (pingError) logSignals.bridgeError = pingError;

  return {
    expectedDetachLogs: { ...classified.expected },
    logSignals,
    fatalReason: reason,
    hasFatal: hasFatalLogSignals(logSignals),
  };
}

/** Poll the logs op until matchers hit or the clock timeout elapses. */

export async function readLatestSeq(bridge, side) {
  const ping = await bridge.call(side, 'ping');
  return ping?.logSeq ?? -1;
}

/**
 * @param {object} opts
 * @param {number} [opts.since]
 * @param {string} [opts.filter]
 * @param {((text: string, line: object) => boolean) | Array<(text: string, line: object) => boolean>} opts.match
 * @param {number} [opts.timeoutMs]
 * @param {number} [opts.pollMs]
 * @param {{now: Function, sleep: Function}} opts.clock
 */
export async function waitForLogs(bridge, side, opts) {
  const clock = opts.clock;
  const timeoutMs = opts.timeoutMs ?? 20000;
  const pollMs = opts.pollMs ?? 250;
  const filter = opts.filter;
  const matchers = Array.isArray(opts.match) ? opts.match : [opts.match];
  const start = clock.now();
  const deadline = start + timeoutMs;
  let cursor = opts.since ?? -1;
  const hit = new Array(matchers.length).fill(null);
  const collected = [];
  let spins = 0;
  while (spins++ < 10000) {
    const r = await bridge.call(side, 'logs', { since: cursor, max: 200, filter });
    if (r?.latestSeq != null) cursor = r.latestSeq;
    for (const line of r?.lines ?? []) {
      collected.push(line);
      const text = String(line.text ?? '');
      matchers.forEach((m, i) => {
        if (!hit[i] && m(text, line)) hit[i] = line;
      });
    }
    if (hit.every(Boolean)) {
      return { ok: true, lines: hit, latestSeq: cursor, collected, elapsedMs: clock.now() - start };
    }
    if (clock.now() >= deadline) break;
    await clock.sleep(pollMs);
  }
  return { ok: false, lines: hit, latestSeq: cursor, collected, elapsedMs: clock.now() - start };
}

export function lineContains(snippet) {
  const needle = String(snippet).toLowerCase();
  return (text) => String(text).toLowerCase().includes(needle);
}

export function lineMatches(regex) {
  const re = regex instanceof RegExp ? regex : new RegExp(regex);
  return (text) => re.test(String(text));
}

function asRegex(regex) {
  return regex instanceof RegExp ? regex : new RegExp(regex);
}

/**
 * Wait until at least one line matches `regex`, drain one more logs poll, and
 * return the latest matching line plus its capture groups.
 */
export async function waitForLatestLog(bridge, side, opts) {
  const re = asRegex(opts.regex);
  const waited = await waitForLogs(bridge, side, {
    since: opts.since,
    filter: opts.filter,
    match: (text) => re.test(text),
    timeoutMs: opts.timeoutMs,
    pollMs: opts.pollMs,
    clock: opts.clock,
  });
  if (!waited.ok) return { ...waited, line: null, match: null };
  let extraLines = [];
  try {
    const extra = await bridge.call(side, 'logs', { since: waited.latestSeq, max: 200, filter: opts.filter });
    extraLines = extra?.lines ?? [];
  } catch {
    extraLines = [];
  }
  const all = [...(waited.collected ?? []), ...extraLines];
  let line = null;
  let match = null;
  for (const l of all) {
    const text = String(l.text ?? '');
    const m = re.exec(text);
    if (m) {
      line = l;
      match = m;
    }
  }
  return { ok: !!line, line, match, collected: all, latestSeq: waited.latestSeq, elapsedMs: waited.elapsedMs };
}
