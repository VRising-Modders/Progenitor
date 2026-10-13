# Content files — JSON item definitions

Roadmap **M4.6**, phase 1 (items only). `docs/spec.html` "Data files": *"Every manager can also
read its definitions from JSON in the plugin's folder, so a server admin can add an item or tune a
drop table without a compiler."* This is the first half of that: a plugin's `content/*.json` files
define cloned **items**, and nothing else, without a build.

The point is reach, not power. A content file cannot express anything
`ctx.Content.DefineItem(...)` cannot — it goes through the very same fluent builder, in the same
order, under the same owner — so there is exactly one registration path in the framework and a
data file is a way of *calling* it rather than a second implementation of it.

For server-owned definitions without a plugin DLL, use [data-only item packs](content-packs.md).

## Where the files go

Next to `localization/` and `assets/`, with the same probe order
(`LocalizationParser.ResolvePluginFolder(..., "content")`):

```
<reload>/<AssemblyName>/content/*.json     # what tools/deploy.ps1 writes
<plugin dll folder>/content/*.json         # output-dir layout a zip can ship
```

Every `*.json` directly inside that folder is read, sorted by name (ordinal, case-insensitive).
Subfolders are ignored. A plugin with no `content` folder logs nothing — that is the normal case.

`tools/deploy.ps1` copies a plugin's built `content/` folder into the reload layout exactly as it
already copies `localization/` and `assets/`. To ship one from your own project, mirror the
`Progenitor.Sample.csproj` item group:

```xml
<None Remove="content\**\*.json" />
<Content Include="content\**\*.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <Link>content\%(RecursiveDir)%(Filename)%(Extension)</Link>
</Content>
```

## The file

```json
{
  "schema": 1,
  "items": [
    {
      "id": "sample.ash",
      "cloneOf": "Item_Ingredient_Crystal",
      "name": "Ash Shard",
      "description": "Cold ash, left where an ember burned out.",
      "stack": 50
    }
  ]
}
```

| Field | Required | Maps to | Notes |
|---|---|---|---|
| `schema` | yes | — | Must be the number `1`. A file for a schema this build does not read is refused whole. |
| `items` | yes | — | Array of item objects. May be empty. |
| `id` | yes | `DefineItem(id)` | 1–63 ASCII bytes, validated by the registry's own rule. Also the spawnable name; hashed with `PrefabGUID.CalculateGuidHash`, so it must be identical on every peer. |
| `cloneOf` | yes | `CloneOf(name)` / `CloneOf(guid)` | A **string** is a spawnable name resolved at apply time (`"Item_Ingredient_Crystal"`); a **number** is a raw non-zero `PrefabGUID` hash (`-257494203`). The source must have `ItemData`. |
| `name` | no | `Named(text)` | 1–63 ASCII. Registered under `<id>`. Omit to keep the source prefab's name. |
| `description` | no | `Described(text)` | 1–63 ASCII. Registered under `<id>.desc`. Omit to keep the source's, or to let `<id>.desc` from the plugin's `localization/en.json` supply it. |
| `stack` | no | `MaxAmount(n)` | Positive integer. Omit to keep the source's `ItemData.MaxAmount`. |
| `statBonuses` | no | `StatBonuses(new ItemStatBonuses(...))` | Object with optional `maxHealth`, `physicalPower`, `spellPower` numbers. Each is additive, finite and between -10000 and 10000 inclusive; omitted members mean zero. Omit the object to keep source stats unchanged. |

For an equipment clone, `"statBonuses": { "maxHealth": 100, "physicalPower": 100,
"spellPower": 100 }` adds 100 to each of those three stats while retaining the source's
other stats. The clone uses native local equipment stat data. On compatible Core builds,
the server sends these bonuses in a versioned `PrfS` packet after the `PrfD` definition;
both peers must support that packet. The standalone data-only pack is installed on the server.
Unknown stat names, duplicate fields and invalid stat values refuse the whole file.
This field is offline-tested; its changed equipment behavior has not been verified live.

This plugin-folder loader reads files belonging to a loaded plugin; many items can share one
plugin's content folder. The separate [data-only pack format](content-packs.md) is under
development for definitions that do not require a plugin assembly.

`//` comments and trailing commas are accepted, because these files are edited by hand.

There is **no `icon` field**. Item clones keep their source prefab's sprite; only `DefineBuff`
takes an icon, and buffs are not in phase 1. Writing `"icon"` gets a refusal that says so.

## When they load, and what owns them

`ProgenitorPlugin.Load` reads the folder **after** `OnLoad`, and registers everything under the
plugin as owner, tracked in the plugin's context bag. Two consequences:

- Unload, hot reload and `UnregisterAll(owner)` sweep file-defined items exactly like C# ones.
- The ids the plugin registered in C# already exist when the files are read, which is what makes
  the collision rule below possible.

Applying, `PrfD` sync to clients, `ItemKeyLookup` rows, tombstones on unload and everything else
downstream are unchanged: to the rest of Core a file-defined item is a `DefineItem` registration.

## Refusals

**A file error refuses that file and only that file.** The other files in the folder still load.

**Within a file it is all or nothing.** Every problem is collected and reported, then nothing in
the file is registered. Half a file is the worst state available here: an admin who mistypes the
fourth item would otherwise get the first three live with no obvious way to tell which state the
server is in.

Every line names the file, the item index and the field, in that order — the whole of what is
needed to find the character to change:

```
Content files: Progenitor.Sample/content/items.json refused; nothing in it was registered (1 problem(s)):
Content files:   Progenitor.Sample/content/items.json: item 0 ('sample.ash'): unknown field 'ikon'; schema 1 items know id, cloneOf, name, description, stack, statBonuses.
```

Refused for: a wrong or missing `schema`; an unknown field at the top level or on an item; a
missing `id` or `cloneOf`; a field of the wrong JSON type; a `cloneOf` of `0` (that is
`PrefabGUID.Empty`, which the builder cannot tell from no `cloneOf` at all); a `cloneOf` or
`stack` that is a number outside the 32-bit range, is fractional, or is written with a decimal
point or an exponent rather than as an integer literal; a non-positive `stack`; the same `id`
twice in one file; text that is not ASCII or does not fit 63 bytes.

A **`cloneOf` too large for a signed 32-bit integer** is nearly always the unsigned spelling of a
real hash — several community GUID tables publish them that way — so the refusal does the
arithmetic for you:

```
… item 0 ('sample.ash'): field 'cloneOf' is 4037473093, which is outside the 32-bit range
-2147483648 to 2147483647; PrefabGUID hashes are signed 32-bit values, so 4037473093 is written
-257494203.
```

Three different edits, three different sentences, and none of them reported as "not a number",
which is what the admin just wrote: **out of range** (above), **fractional** (`50.5` — "is 50.5,
which is a fractional value"), and **the wrong spelling of a whole number**. The last one is what
`json.dump` and a spreadsheet export produce from a perfectly correct hash, so it is refused for
what is actually wrong with it — the literal, not the value — and handed the edit:

```
… item 0 ('sample.ash'): field 'cloneOf' is -257494203.0, which is written with a decimal point;
this field takes a spawnable name or an integer literal, so write it as -257494203.
```

`1e2` gets the same sentence with "written with an exponent". These fields read the literal as
written: `1.0` and `1e3` are integers in arithmetic, but neither is a spelling they take.

A **length** refusal carries the measured UTF-8 byte count, so a pasted sentence says how much to
cut:

```
… item 0 ('sample.ash'): description must be 1–63 ASCII bytes. (this one is 71 bytes, limit 63).
```

A **non-ASCII** refusal carries no count and quotes the string instead, because the count would be
a length complaint about something whose length is fine — forty em dashes are forty characters and
120 UTF-8 bytes, and would still be refused at twenty:

```
… item 0 ('sample.ash'): name 'Ember — Shard' is not ASCII; the wire format is a 64-byte ASCII field.
```

**An id the plugin already defines in C# (or in an earlier file of the same load) refuses the
file.** A data file never replaces a compiled definition; a mod author who ships both a
`DefineItem("x")` and a file defining `x` has a bug, and silently letting one win is how an
operator ends up debugging a value that is nowhere in the source they are reading.

A refusal the *registry* makes (a `cloneOf` with no `ItemData`, say) is reported against that item
and the other items in the file still load — the file was well-formed, the game disagreed. That is
the only way a file can be partly registered, so anything the parser can see for itself — a
`cloneOf` the builder is going to reject out of hand, for instance — is refused at parse time
instead, where it takes the whole file with it.

## Success

One line per accepted file:

```
Content files: Progenitor.Sample/content/items.json: 1 item(s) defined
```

Every line this feature writes starts `Content files: `, so one grep finds the whole load.

## What is in the code

| File | Role |
|---|---|
| `src/Progenitor.Core/Content/Json/ContentFileModel.cs` | `ContentFileSchema` (version, folder name, known fields), `ContentItemSpec`, `ContentFileDocument`, `ContentFileSource`, `ContentFileLoadResult`. Game-free. |
| `…/ContentFileParser.cs` | Parse and validate one file; every refusal message. Game-free. |
| `…/IContentFileSink.cs` | The single seam to the registry: `IsDefined(id)` and `DefineItem(spec)`. |
| `…/ContentFileLoad.cs` | Folder-level decisions: per-file refusal, the collision pass, the log lines. Game-free. |
| `…/ContentFiles.cs` | The only game-touching part: probe the folder, read the files, drive the real `PrefabRegistry.DefineItem` builder, own the handles. |

`tests/Progenitor.Tests/ContentJsonTests.cs` drives the real parser and loader and fakes only
`IContentFileSink`, so the decision table — which file is refused, which item and field the
message names, what actually reaches the builder — is covered without a world.

## Phase 2 and later (not built)

Buffs, prefab edits (`Content.Edit` values from a file), and the M3.7 content managers' own
definitions (recipes, drops) once those settle. The schema number exists so those can be added
without guessing what an old file meant. C# `Content.Edit` already has the layer contract JSON
would have to call: declared priority order, a server `PrfE` layer as last writer, refusal of
unauthorized (client-originated or off-allowlist) edits with no state change, and owned removal
restoring the baseline — see `docs/modules.md` Ordered edits.
