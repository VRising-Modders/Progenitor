# Guide robes

Guide Robes, Guide Hood, Guide Hands and Guide Feet. Original authored chest, gloves and boots are embedded in Progenitor Core, together with an original black face insert. The hood borrows the equipped Razer hood mesh at runtime with owned palette-aware materials; no native geometry is packaged. Stable `robes.*` IDs preserve existing items across rebuilds.

See [wearables.md](../../docs/wearables.md) for the API contract and [authoring](authoring/README.md) for the local Blender workflow.

## Verification scope

The native hood shape, fixed black face and chest palette inheritance were accepted on the female test character. The user reports upper-front/back breakthrough addressed by the current chest resculpt. The low shoes passed sampled female sole checks and the user's live visual check. Male authored binding and broad combat coverage remain unqualified. The late-frame equip visibility fix builds and passes wearable tests; live first-equip acceptance is pending.

A report of a headless male wearer with a closed hood (2026-10-08) traced to a dead wearer, not to the male binding: the game had disabled that character's body while the owned overlays stayed visible. Overlays now hide with the wearer's body ([wearables.md](../../docs/wearables.md#model-metadata-and-donor-visibility)). On 2026-10-09 a capture from another client showed the robe, gloves, shoes and hood drawn on a living male wearer, at the captures' small scale only; a close fit check of the male binding (and the face insert) is still owed. The overlay-concealment fix was observed live the same day: nothing was drawn while the wearer was dead, and the outfit returned on respawn.

## Install

The Guide outfit is built into `Progenitor.dll` on both sides; it has no separate mod entry. This folder retains the garment assets and Blender authoring inputs used by Core. Build from the repository root with `dotnet build`.

When upgrading an installation that has the old sample, retire `Progenitor.RobeSet.dll` from each side's `BepInEx/progenitor-reload` folder before loading the new Core. Keep a backup outside the reload folder. A legacy copy must not reload: it registers the same item IDs and can replace Core's definitions. Existing `robes.*` item IDs, gear level 1, no set bonus, palettes and appearance are unchanged. No inventory replacement is needed.

Use the [reload procedure](../../docs/hot-reload.md) with current authorization for each target. Confirm `Guide outfit: registered built-in chest, gloves, hood and shoes.` on both processes and that the old sample is absent from the loaded mod list. Core owns the command and registrations; Core unload releases them before the content registry shuts down. Icons and garment JSON are embedded, so the old sidecar assets are no longer required.

**Verified live on the isolated client/server (2026-10-04):** both sides loaded the same Core image with no RobeSet plugin, the existing four items retained their worn appearance, all four embedded icons applied, and the fresh handshake passed. Other-body fit remains unqualified.

## Grant

In game, as an admin: `.robes`.

Alternatively, grant each item through the bridge from the repository root. Write the request as UTF-8 without a BOM:

```powershell
$bridgeArgs = Join-Path $env:TEMP 'progenitor-robe-grant.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"user":"NAME","prefab":"robes.chest","amount":1,"equip":true,"whenPresent":"equipExisting","durability":"none"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server give "@$bridgeArgs"
```

Replace `NAME` with the test player. Repeat for `robes.gloves`, `robes.hood`, and `robes.shoes`. The built-in outfit registers and grants clothing only.

Every Guide item is blood-bound and indestructible by definition, so a piece obtained any other way (another grant tool, a drop, an older save) behaves the same as one from `.robes`. The grant's `"durability":"none"` and `.robes`' indestructible option only repair the granted copy at once.

### Blood-bound and indestructible

Status: source-checked, built, game-free tested (`ItemKeepRulesTests`) and live-verified on 2026-10-09 on the isolated test server. A male test character wore all four pieces, the hood granted without the grant's durability option. The robe, gloves and shoes read full durability with `LossType` 0 and take-damage factor 0; the hood has no `Durability` component. After a kill through the game's death path, all four stayed equipped (nothing dropped) at full durability, and after the respawn all four were still worn. Not live-verified: a server with the blood-bound equipment setting off.

All four items register with `.BloodBound().Indestructible()`:

- **Blood-bound.** `ItemData.ItemCategory` gets `BloodBound` (8) on the clone and on its item lookup row. The death drop reads the row of each bag and equipment slot and keeps a slot whose row is `BloodBound` or `SoulBound`, so the Guide items stay with the player on death. With the server's blood-bound equipment setting off, the game strips `BloodBound` from Equippable items only while it registers its own prefabs, before the clones exist (traced, not live-verified). The server re-checks each clone and its row on every player connect and re-sets the bit, with one warning, if something cleared it. The hood's donor, a cosmetic headgear, is not blood-bound on its own.
- **Indestructible.** The clone's `Durability` gets `LossType` None, `TakeDamageDurabilityLossFactor` 0, `DestroyItemWhenBroken` false and full durability, and its category loses `LoseDurabilityOnDeath` (2048), which gates the death durability drain.
- **Existing items.** The server rearms every existing Guide item, in bags, containers or worn, when the definitions register (Core load or swap) and again at the first player connect of each world load, because the server registers clones before the save file loads its items. Items created later are copies of the clone and need no rearm. Each rearm adds the category bits to the item's own `ItemData` and writes the indestructible durability.

Live recipe, on the isolated test server only, with current authorization:

1. Confirm `PrefabRegistry: applied 'robes.hood'` with `keep=blood-bound+indestructible` in the server log (diagnostic level).
2. Read the clone: `server prefab {"prefab":"robes.hood","component":"ProjectM.ItemData"}`. Expect `ItemCategory` to include `BloodBound` and not `LoseDurabilityOnDeath`. Repeat with `"component":"ProjectM.Shared.Durability"`: `LossType` None, `TakeDamageDurabilityLossFactor` 0, `Value` equal to `MaxDurability`.
3. Grant without the durability option: `server give {"user":"NAME","prefab":"robes.hood","amount":1,"equip":true,"whenPresent":"equipExisting"}`. Note `itemEntity`.
4. `server get {"entity":"<itemEntity>","component":"ProjectM.Shared.Durability"}` and `"component":"ProjectM.ItemData"`: the same values as step 2, on the item itself.
5. Death test, only when the user calls for it: let the test character die wearing all four pieces. Expect all four still equipped after respawn, absent from the death container, and `Durability.Value` still at maximum.

Write each argument object to a UTF-8 file without a BOM and pass it as `"@$bridgeArgs"`, as in the grant example.

| Id | Clone source |
|---|---|
| `robes.chest` | `Item_Chest_T09_Dracula_Scholar` |
| `robes.gloves` | `Item_Gloves_T09_Dracula_Scholar` |
| `robes.hood` | `Item_Headgear_RazerHood` |
| `robes.shoes` | `Item_Boots_T09_Dracula_Scholar` |

A rejected clone source prevents that item from being created. `Models` records diagnostic names only; model references remain unchanged pending a verified native layout contract.

## Visual acceptance procedure — incomplete

An earlier female build received scoped combat acceptance. The current measured waist/hip contour and finger-fitted glove changes need renewed visual acceptance. Continue qualification on the male body, additional standing/walking/seated/weapon poses, and equipping while animated. The following checklist defines the broader acceptance target; the scoped confirmation does not establish every item in every pose:

- Chest: close torso fit without clipping, covered shoulders and elbows through combat poses, matching waist seams, a single draped skirt, and an attached gold hem and tassels.
- Hood: an angular pointed silhouette, covered scalp and a narrow opening around a recessed black interior, with no face or hair protrusion.
- Gloves: covered palms and all five closed fingertips through finger movement.
- Shoes: toes follow each foot's direction and closed soles cover the underside through raised-foot poses. The lower leg above the low shoe is intentionally exposed.

Inspect `Wearables: attached` diagnostics for vertex/material counts, `missing=`, `hidden=` and `fitPoints=`. A missing compatible authored bind reference defers attachment. Missing joints omit affected shapes. `hidden=0` can indicate an unmatched donor renderer name; inspect the visible donor before widening hide prefixes. Body and face renderers are protected.

## Cleanup

Unequip or unload to remove overlays and restore donor renderers. Verify the result visually. Item clones remain resolvable, as with other `DefineItem` registrations. Follow the isolated-test cleanup procedure after authorized live checks.
