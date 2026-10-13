#!/usr/bin/env node
// TEST SETUP, trainer only (the user's instruction, 2026-09-25): the combat trainer is an arena
// combatant, so it is handed gear of a tier instead of crafting it, to scale its opponents up. The
// progression bots never use this; they craft their gear.
//   node tools/bots/arena.mjs --bot 3:HeadlessThree --tier 4
import { pathToFileURL } from "node:url";
import { call } from "./bridge.mjs";
import { Bot, SERVER, sleep } from "./bot.mjs";

// Armor sets and the ring per tier (item names from the live recipe outputs), and every weapon type
// the tier has, so the weapon rotation keeps comparing them.
export const KITS = {
  2: { armour: ["Chest_T02_BoneReinforced", "Legs_T02_BoneReinforced", "Gloves_T02_BoneReinforced", "Boots_T02_BoneReinforced"], ring: "MagicSource_General_T02_BloodBoneRing",
    weapons: ["Sword_T02_Bone_Reinforced", "Axe_T02_Bone_Reinforced", "Mace_T02_Bone_Reinforced", "Spear_T02_Bone_Reinforced", "Reaper_T02_Bone_Reinforced", "Slashers_T02_Bone_Reinforced", "Crossbow_T02_Bone_Reinforced"] },
  4: { armour: ["Chest_T04_Copper_Warrior", "Legs_T04_Copper_Warrior", "Gloves_T04_Copper_Warrior", "Boots_T04_Copper_Warrior"], ring: "MagicSource_General_T03_GravediggerRing",
    weapons: ["Sword_T03_Copper", "Axe_T03_Copper", "Mace_T03_Copper", "Spear_T03_Copper", "Reaper_T03_Copper", "Slashers_T03_Copper", "Crossbow_T03_Copper"] },
  6: { armour: ["Chest_T06_Iron_Warrior", "Legs_T06_Iron_Warrior", "Gloves_T06_Iron_Warrior", "Boots_T06_Iron_Warrior"], ring: "MagicSource_General_T05_Relic",
    weapons: ["Sword_T05_Iron", "Axe_T05_Iron", "Mace_T05_Iron", "Spear_T05_Iron", "Reaper_T05_Iron", "Slashers_T05_Iron", "Crossbow_T05_Iron", "GreatSword_T05_Iron"] },
  8: { armour: ["Chest_T08_DarkSilver_Warrior", "Legs_T08_DarkSilver_Warrior", "Gloves_T08_DarkSilver_Warrior", "Boots_T08_DarkSilver_Warrior"], ring: "MagicSource_General_T07_BloodwineAmulet",
    weapons: ["Sword_T07_DarkSilver", "Axe_T07_DarkSilver", "Mace_T07_DarkSilver", "Reaper_T07_DarkSilver", "Crossbow_T07_DarkSilver", "GreatSword_T07_DarkSilver"] },
};

// The trainer climbs the V Blood ladder with gear matched to each boss (the user: fight opponents
// equivalent to its armor, and do not skip V Bloods). Live gear levels: bone T01 kit ~9, T02 armor
// and ring with a T01 weapon 17 (Alpha Wolf 16), T02 throughout ~20 (Keely/Errol/Rufus 20), T04
// armor with T02 ring and weapons ~28 (Grayson/Goreswine 27), T04 armor, T03 ring and weapons ~30.
export const LADDER = [
  { upTo: 17, armour: 2, ring: 2, weapons: 1 },
  { upTo: 22, armour: 2, ring: 2, weapons: 2 },
  { upTo: 28, armour: 4, ring: 2, weapons: 2 },
  { upTo: 32, armour: 4, ring: 4, weapons: 4 },
  { upTo: 40, armour: 6, ring: 6, weapons: 6 },
];
export const rungFor = (level) => LADDER.find((r) => level <= r.upTo) ?? LADDER[LADDER.length - 1];
KITS[1] = { armour: ["Chest_T01_Bone", "Legs_T01_Bone", "Gloves_T01_Bone", "Boots_T01_Bone"], ring: "MagicSource_General_T01_BoneRing",
  weapons: ["Sword_T01_Bone", "Axe_T01_Bone", "Mace_T01_Bone", "Spear_T01_Bone", "Reaper_T01_Bone", "Slashers_T01_Bone", "Crossbow_T01_Bone"] };

/**
 * Give and wear a kit: armor of one tier, the ring of another, weapons of a third (defaults: all one
 * tier). Items already carried are not given again. Returns the new gear level.
 */
export async function equipKit(bot, tier, { ring = tier, weapons = tier, fresh = [] } = {}) {
  const kit = KITS[tier], ringKit = KITS[ring], weaponKit = KITS[weapons];
  if (!kit || !ringKit || !weaponKit) throw new Error(`no kit for tier ${tier}/${ring}/${weapons} (have ${Object.keys(KITS).join(", ")})`);
  const give = (name) => call(SERVER, "give", { user: bot.name, prefab: `Item_${name}`, amount: 1 }).catch((e) => ({ error: e.message.slice(0, 120) }));
  const wear = [...kit.armour, ringKit.ring].map((n) => `Item_${n}`);
  const carried = new Set([...((await bot.inventory()).occupied ?? []).map((o) => o.item), ...((await bot.inventory()).equipped ?? []).map((e) => e.item)]);
  const given = [];
  // A worn-out piece: a copy already in the bag may be fresh (bag items carry no durability; each
  // swap left the worn one in the bag, and five pairs of boots piled up while the worn pair kept being
  // put back on, live 2026-09-25). Try the copies on first; a new one only if none is fresh.
  const stillWorn = [];
  for (const item of fresh) if (!(await wearFreshest(bot, item))) stillWorn.push(item);
  for (const n of [...kit.armour, ringKit.ring, ...weaponKit.weapons.map((w) => `Weapon_${w}`)]) {
    if (!carried.has(`Item_${n}`) || stillWorn.includes(`Item_${n}`)) given.push({ item: n, ...(await give(n)) });
  }
  for (const item of stillWorn) await wearFreshest(bot, item);
  for (const item of wear) {
    const bag = await bot.inventory();
    // Already on: leave it (the first bag copy may be the worn one just swapped off, live).
    if ((bag.equipped ?? []).some((e) => e.item === item)) continue;
    const o = (bag.occupied ?? []).find((x) => x.item === item);
    if (o) await call(SERVER, "equip", { user: bot.name, slot: o.slot }).catch(() => null);
    await sleep(200);
  }
  // In hand, a weapon of the kit's tier: a copper weapon left from an earlier rung put the trainer at
  // gear 23 against the Alpha Wolf's 16 (live).
  const inv = await bot.inventory();
  const held = (inv.equipped ?? []).find((e) => e.slot === "weapon")?.item ?? "";
  const kitWeapons = weaponKit.weapons.map((w) => `Item_Weapon_${w}`);
  if (!kitWeapons.includes(held)) {
    const o = (inv.occupied ?? []).find((x) => kitWeapons.includes(x.item) && !/Crossbow/.test(x.item));
    if (o) { await call(SERVER, "equip", { user: bot.name, slot: o.slot }).catch(() => null); await sleep(200); }
  }
  const bag = await bot.inventory();
  return { tier, ring, weapons, gear: bag.gear, failed: given.filter((g) => g.error).map((g) => `${g.item}: ${g.error}`) };
}

const SLOT_OF = [["chest", /_Chest_/], ["legs", /_Legs_/], ["gloves", /_Gloves_/], ["boots", /_Boots_/], ["grimoire", /MagicSource/]];
/**
 * Put on the copy of `item` with the most durability left: each bag copy is worn in turn and its
 * durability read (bot_repair status), stopping at one above 90%. True when a copy above 50% is on.
 */
export async function wearFreshest(bot, item) {
  const slot = SLOT_OF.find(([, re]) => re.test(item))?.[0];
  if (!slot) return false;
  const worn = async () => {
    const r = await call(SERVER, "bot_repair", { user: bot.name, action: "status" }).catch(() => null);
    const d = (r?.slots ?? []).find((x) => x.slot === slot && x.item === item);
    return d?.max ? d.durability / d.max : 0;
  };
  let best = await worn();
  const tried = new Set();
  for (let i = 0; i < 8 && best < 0.9; i++) {
    const o = ((await bot.inventory()).occupied ?? []).find((x) => x.item === item && !tried.has(x.slot));
    if (!o) break;
    tried.add(o.slot); // the piece taken off lands in this slot: never tried twice
    await call(SERVER, "equip", { user: bot.name, slot: o.slot }).catch(() => null);
    await sleep(250);
    const now = await worn();
    if (now > best) best = now;
    else { await call(SERVER, "equip", { user: bot.name, slot: o.slot }).catch(() => null); await sleep(250); } // swap back
  }
  return best > 0.5;
}

/** The trainer's kit for fighting at `level` (the next V Blood's): its rung of the ladder. */
export async function kitForLevel(bot, level, { fresh = [] } = {}) {
  const r = rungFor(level);
  return { ...(await equipKit(bot, r.armour, { ring: r.ring, weapons: r.weapons, fresh })), rung: r };
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const arg = (k) => { const i = process.argv.indexOf(`--${k}`); return i > 0 ? process.argv[i + 1] : null; };
  const [i, name] = (arg("bot") ?? "3:HeadlessThree").split(":");
  const bot = new Bot(Number(i), name);
  console.log(JSON.stringify(arg("level") ? await kitForLevel(bot, Number(arg("level"))) : await equipKit(bot, Number(arg("tier") ?? 4))));
}
