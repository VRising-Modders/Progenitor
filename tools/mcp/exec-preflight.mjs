/** MCP progenitor_exec preflight: never retry a mutation after ping stall. */

export const EXEC_PREFLIGHT_LOG_FILTER = "tick hb|bridge gate|scratch:|MainThread";

export function isExecPreflightStall(err) {
  const msg = String(err?.message ?? err ?? "");
  if (/timed out on 'ping'/i.test(msg)) return true;
  if (/a main-thread op is already running/i.test(msg)) return true;
  return false;
}

/**
 * One bounded `bridge_status` retry, then `logs`. Never sends another main-thread op.
 * `readStatus` / `readLogs` are injected so tests do not open a socket.
 */
export async function snapshotAfterExecPreflight(readStatus, readLogs) {
  let status = null;
  let statusError = null;
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      status = await readStatus();
      statusError = null;
      break;
    } catch (e) {
      statusError = String(e?.message ?? e);
    }
  }
  let logs = null;
  let logsError = null;
  try {
    logs = await readLogs();
  } catch (e) {
    logsError = String(e?.message ?? e);
  }
  return { status, statusError, logs, logsError };
}

export function formatExecPreflightStall(err, snapshot) {
  const error = String(err?.message ?? err ?? "preflight failed");
  const body = {
    error,
    note: "preflight ping failed; not sending another main-thread op. Use bridge_status / logs as the liveness probe. Timeout is not a free slot.",
    bridge_status: snapshot?.status ?? null,
    statusError: snapshot?.statusError ?? null,
    logs: snapshot?.logs ?? null,
    logsError: snapshot?.logsError ?? null,
  };
  return JSON.stringify(body, null, 2);
}
