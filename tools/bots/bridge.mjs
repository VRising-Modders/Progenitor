// Minimal in-process Dev-bridge client for the bot brain (same wire protocol as tools/mcp/call.mjs:
// one JSON line {id, token, op, args} in, one line {id, ok, result|error} out). The token file is
// re-read on every call because the Dev plugin regenerates it on each start.
import net from "node:net";
import fs from "node:fs";

let nextId = 1;

// The Dev bridge runs one main-thread op at a time per process; several bots share the server's.
// Retry "already running" refusals with a short jittered backoff.
export async function call(side, op, args = {}, timeoutMs = 15000) {
  for (let attempt = 0; ; attempt++) {
    try { return await callOnce(side, op, args, timeoutMs); }
    catch (e) {
      if (attempt >= 40 || !/already running/i.test(e.message)) throw e;
      await new Promise((r) => setTimeout(r, 40 + Math.random() * 80));
    }
  }
}

function callOnce({ port, tokenFile }, op, args, timeoutMs) {
  if (String(tokenFile).toLowerCase().startsWith("c:\\vrisingserver")) {
    return Promise.reject(new Error(`refusing a token file under the live dedicated server: ${tokenFile}`));
  }
  const token = fs.readFileSync(tokenFile, "utf8").trim();
  const id = nextId++;
  return new Promise((resolve, reject) => {
    let buf = "";
    const sock = net.createConnection({ host: "127.0.0.1", port }, () => {
      sock.write(JSON.stringify({ id, token, op, args }) + "\n");
    });
    const timer = setTimeout(() => { sock.destroy(); reject(new Error(`${op} on :${port} timed out after ${timeoutMs} ms`)); }, timeoutMs);
    sock.setEncoding("utf8");
    sock.on("data", (chunk) => {
      buf += chunk;
      const nl = buf.indexOf("\n");
      if (nl < 0) return;
      clearTimeout(timer);
      sock.end();
      let msg;
      try { msg = JSON.parse(buf.slice(0, nl)); } catch (e) { reject(e); return; }
      if (msg.ok) resolve(msg.result);
      else reject(new Error(`${op}: ${msg.error ?? "unknown bridge error"}`));
    });
    sock.on("error", (e) => { clearTimeout(timer); reject(new Error(`${op} on :${port}: ${e.message}`)); });
  });
}
