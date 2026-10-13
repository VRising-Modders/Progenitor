#!/usr/bin/env node
// Live-test lifecycle rig. Offline by default; `run` and `cleanup` talk to the Dev bridge through lib/bridge.mjs.
//
//   node tools/live-rig/rig.mjs list
//   node tools/live-rig/rig.mjs stamps [--game <dir>]
//   node tools/live-rig/rig.mjs run <case.json> [--out <dir>] [--side server] [--dll <path>] [--force-teardown] [--attach-readback|--no-attach-readback] [--site main-menu|top-tab|shortcut]
//   node tools/live-rig/rig.mjs cleanup <runDir> [--out <dir>] [--attach-readback|--no-attach-readback]
//   node tools/live-rig/rig.mjs cleanup --entity <index:version> [--prefab <name>] [--out <dir>] [--attach-readback|--no-attach-readback]

import { loadCase, listCases, runCase, cleanupLeftovers, cleanupEntity, DEFAULT_OUT_DIR, CASE_SEARCH_DIRS, CASE_SITES, exitCodeForStatus } from './lib/run.mjs';
import { hashContentCatalog, DEFAULT_GAME_INSTALL } from './lib/stamps.mjs';

function usage() {
  console.log(`usage: node rig.mjs <command> [args]

  list
  stamps [--game <dir>]
  run <case.json> [--out <dir>] [--side server|client] [--dll <path>] [--force-teardown]
                    [--attach-readback|--no-attach-readback]
                    [--site main-menu|top-tab|shortcut]
  cleanup <runDir> [--out <dir>] [--attach-readback|--no-attach-readback]
  cleanup --entity <index:version> [--prefab <name>] [--out <dir>]
                    [--attach-readback|--no-attach-readback]

  --out               run directory parent (default ${DEFAULT_OUT_DIR})
  --side              reload folder/image and default snippet side (default server).
                      Admin grant/restore and admin-only chat always use the server user.
  --site              mods_ui open site; substitutes @site on open steps
                      (main-menu, top-tab, or shortcut). Omitted: main-menu when the
                      client has no Client_0, top-tab when connected. Recorded on
                      startState.site.
  --dll               replacement Sample image for a swap step
  --force-teardown    after status is decided, destroy THIS run's skipCleanup entities
                      (recorded as teardown). Does not adopt leftovers from a previous
                      run; use cleanup <runDir> or cleanup --entity for those.
  --attach-readback   inspect AttachedBuffer once before destroy and once at tagged;
                      stored on cleanup.entities[].attachedBuffer {before, atTagged}.
                      Default on for thief/DIAG cases.
  --no-attach-readback  disable the AttachedBuffer inspect even for thief/DIAG cases.
  --entity            adopt one leftover id and run cleanup (not a previous report)
  --prefab            optional prefab name for --entity (exists readback must match)
  --game              V Rising install root for stamps (default client install).
                      Offline: prints the contentCatalogSha256 the run would record.

Exit 0 only when status is complete. Incomplete or aborted is 1. Usage errors are 2.
stamps is offline (no bridge) and always exits 0; stdout is the sha256 or null.
`);
}

function parseRunArgs(argv) {
  const a = { positional: [], out: DEFAULT_OUT_DIR, side: 'server', dll: undefined, forceTeardown: false, attachReadback: undefined, site: undefined };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === '--out') a.out = argv[++i];
    else if (k === '--side') a.side = argv[++i];
    else if (k === '--dll') a.dll = argv[++i];
    else if (k === '--site') a.site = argv[++i];
    else if (k === '--force-teardown') a.forceTeardown = true;
    else if (k === '--attach-readback') a.attachReadback = true;
    else if (k === '--no-attach-readback') a.attachReadback = false;
    else if (k === '-h' || k === '--help') { usage(); process.exit(0); }
    else if (k.startsWith('--')) {
      console.error(`unknown option ${k}`);
      usage();
      process.exit(2);
    } else a.positional.push(k);
  }
  return a;
}

function parseCleanupArgs(argv) {
  const a = { positional: [], out: DEFAULT_OUT_DIR, entity: undefined, prefab: undefined, attachReadback: undefined };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === '--out') a.out = argv[++i];
    else if (k === '--entity') a.entity = argv[++i];
    else if (k === '--prefab') a.prefab = argv[++i];
    else if (k === '--attach-readback') a.attachReadback = true;
    else if (k === '--no-attach-readback') a.attachReadback = false;
    else if (k === '-h' || k === '--help') { usage(); process.exit(0); }
    else if (k.startsWith('--')) {
      console.error(`unknown option ${k}`);
      usage();
      process.exit(2);
    } else a.positional.push(k);
  }
  return a;
}

async function main(argv) {
  const command = argv[2];
  if (command === 'list') {
    const cases = CASE_SEARCH_DIRS.flatMap((dir) => listCases(dir));
    for (const c of cases) {
      console.log(`${c.name}\t${c.steps} steps\t${c.summary}`);
    }
    if (!cases.length) console.error('no cases found');
    return 0;
  }
  if (command === 'stamps') {
    let game = DEFAULT_GAME_INSTALL;
    for (let i = 3; i < argv.length; i++) {
      const k = argv[i];
      if (k === '--game') game = argv[++i];
      else if (k === '-h' || k === '--help') { usage(); return 0; }
      else if (k != null && k.startsWith('--')) {
        console.error(`unknown option ${k}`);
        usage();
        return 2;
      } else {
        console.error(`unexpected argument ${k}`);
        usage();
        return 2;
      }
    }
    const got = hashContentCatalog(game);
    console.log(got.sha256 ?? 'null');
    return 0;
  }
  if (command === 'run') {
    const a = parseRunArgs(argv.slice(3));
    const file = a.positional[0];
    if (!file) {
      usage();
      return 2;
    }
    if (a.side !== 'server' && a.side !== 'client') {
      console.error('--side must be server or client');
      return 2;
    }
    if (a.site != null && !CASE_SITES.includes(a.site)) {
      console.error('--site must be main-menu, top-tab, or shortcut');
      return 2;
    }
    const { doc } = loadCase(file);
    let stop = null;
    const onSignal = () => {
      if (typeof stop === 'function') stop();
    };
    process.on('SIGINT', onSignal);
    process.on('SIGTERM', onSignal);
    try {
      const { report } = await runCase(doc, {
        outDir: a.out,
        side: a.side,
        dll: a.dll,
        forceTeardown: a.forceTeardown,
        attachReadback: a.attachReadback,
        site: a.site,
        registerStop(fn) { stop = fn; },
      });
      return exitCodeForStatus(report.status);
    } finally {
      process.off('SIGINT', onSignal);
      process.off('SIGTERM', onSignal);
    }
  }
  if (command === 'cleanup') {
    const a = parseCleanupArgs(argv.slice(3));
    if (a.entity != null && a.positional.length) {
      console.error('cleanup takes a runDir or --entity, not both');
      usage();
      return 2;
    }
    if (a.prefab != null && a.entity == null) {
      console.error('--prefab requires --entity');
      usage();
      return 2;
    }
    if (a.entity != null) {
      if (!a.entity || a.entity.startsWith('--') || !/^\d+:\d+$/.test(a.entity)) {
        console.error('--entity must be index:version');
        usage();
        return 2;
      }
      const { report } = await cleanupEntity(a.entity, { outDir: a.out, prefab: a.prefab, attachReadback: a.attachReadback });
      return exitCodeForStatus(report.status);
    }
    const runDir = a.positional[0];
    if (!runDir) {
      usage();
      return 2;
    }
    const { report } = await cleanupLeftovers(runDir, { outDir: a.out, attachReadback: a.attachReadback });
    return exitCodeForStatus(report.status);
  }
  usage();
  return 2;
}

main(process.argv).then(
  (code) => process.exit(code),
  (err) => {
    console.error(err.message ?? err);
    process.exit(2);
  },
);
