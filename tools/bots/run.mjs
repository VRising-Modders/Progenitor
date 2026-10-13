#!/usr/bin/env node
// EXPERIMENTAL, EARLY DEVELOPMENT, development-only: headless test-client bots, not a supported framework feature and
// not part of the Release package. Read tools/bots/README.md first; may change or be removed.
// Bot brain runner. Each bot runs its task loop concurrently.
//   node tools/bots/run.mjs break --bots 1:HeadlessOne,2:HeadlessTwo,3:HeadlessThree --name StoneUrn --count 5
//   node tools/bots/run.mjs look  --bots 1:HeadlessOne
import { Bot, breakNearest } from "./bot.mjs";
import { feedOn, leaveCrypt, gather, fight, nearest, recover, safePrey } from "./skills.mjs";

const argv = process.argv.slice(2);
const task = argv[0];
const opt = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
const bots = (opt("bots", "1:HeadlessOne,2:HeadlessTwo,3:HeadlessThree"))
  .split(",").map((s) => { const [i, n] = s.split(":"); return new Bot(Number(i), n); });

async function taskBreak(bot) {
  const name = opt("name", "StoneUrn");
  const count = Number(opt("count", "5"));
  const skip = new Set();
  let broken = 0;
  const start = await bot.inventory();
  bot.log(`start: ${start.used}/${start.slots} slots used`);
  for (let tries = 0; broken < count && tries < count * 3; tries++) {
    const r = await breakNearest(bot, name, { skip });
    if (r.ok) { broken++; bot.log(`broke ${r.target.name} in ${r.swings} swing(s)`); }
    else { bot.log(`skip: ${r.reason}`); if (/no .* within/.test(r.reason)) break; }
  }
  const end = await bot.inventory();
  bot.log(`done: broke ${broken}; inventory`, JSON.stringify(end.items));
}

async function taskLook(bot) {
  const v = await bot.look({ radius: Number(opt("radius", "30")), limit: 20 });
  bot.log(JSON.stringify(v.self), v.nearby.map((n) => `${n.kind}:${n.name}@${n.distance}`).join(" | "));
}

async function taskGoto(bot) {
  const [x, z] = opt("to", "0,0").split(",").map(Number);
  const y = Number(opt("y", "5"));
  const t0 = Date.now();
  const r = await bot.goTo({ x, y, z }, { stop: Number(opt("stop", "2")) });
  const me = await bot.self();
  bot.log(`goto (${x},${z}) -> ${JSON.stringify(r)}; now at (${me.x.toFixed(1)},${me.y.toFixed(1)},${me.z.toFixed(1)}) after ${((Date.now() - t0) / 1000).toFixed(1)} s`);
}

async function taskFeed(bot) {
  const name = opt("name", "Critter");
  const target = await safePrey(bot, { name, safety: Number(opt("safety", "12")) });
  if (!target) { bot.log(`no ${name} nearby`); return; }
  bot.log(`feeding on ${target.name} ${target.entity} at ${target.distance} m`);
  bot.log(JSON.stringify(await feedOn(bot, target)));
}

async function taskLeave(bot) { bot.log(JSON.stringify(await leaveCrypt(bot))); }
async function taskRecover(bot) { bot.log(JSON.stringify(await recover(bot))); }
async function taskGather(bot) {
  const name = opt("name", "Rock");
  for (let i = 0; i < Number(opt("count", "1")); i++) bot.log(JSON.stringify(await gather(bot, name)));
}
async function taskFight(bot) {
  const t = await nearest(bot, { name: opt("name", "Skeleton"), kind: "unit", radius: 60 });
  if (!t) { bot.log("no target"); return; }
  bot.log(`fighting ${t.name} ${t.entity} (${t.health} hp) at ${t.distance} m`);
  bot.log(JSON.stringify(await fight(bot, t)));
}

const tasks = { break: taskBreak, look: taskLook, goto: taskGoto, feed: taskFeed, leave: taskLeave, recover: taskRecover, gather: taskGather, fight: taskFight };
if (!tasks[task]) { console.error(`usage: run.mjs <${Object.keys(tasks).join("|")}> [--bots i:Name,...]`); process.exit(2); }
const results = await Promise.allSettled(bots.map((b) => tasks[task](b)));
for (const [i, r] of results.entries()) if (r.status === "rejected") console.error(`${bots[i].name}: ${r.reason?.message ?? r.reason}`);
