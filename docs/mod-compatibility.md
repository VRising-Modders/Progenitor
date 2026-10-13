# Mod compatibility across versions (design)

**Status (2026-10-09): C1 (feature negotiation) and C2 (versioned messages) are built, game-free tested and verified live** on the Steam client and the isolated test server with a test plugin at two versions, each side newer in turn; the API reference is [modules.md](modules.md#feature-negotiation-and-versioned-messages-mod-compatibility-c1c2). Of the send results only `Sent` and `PeerLacksMod` were observed live. Not observed live: `PeerTooOld` (a peer on an older Core), `PeerNotReady`, `NotConnected`, `TooLarge`, `Invalid`, `TransportFailed`, a `MinReaderVersion` drop, the `more` truncation, a server Core swap with players connected, a headless client, and the post-review hardening (the 256-mod cap on a merged table, tables held until a user's handshake is ok, the bounded drop-log set, full tables in place of cut deltas), which is game-free tested only. **C3 (declared ranges and Version matching) is built and game-free tested, not verified live (2026-10-10)**; what it covers, where it deviates and what remains are in [C3 as built](#c3-as-built). **C4 (Exact for Progenitor itself, and the C3 wording fix) is built, game-free tested and verified live in part (2026-10-10)** on the Steam client and the isolated test server with test Progenitor releases from the local Thunderstore stand-in; see [C4 as built](#c4-as-built) and its [live test](#c4-live-test-2026-10-10). **C5 (the compat test helper, the `messages_record` bridge op, the samples, the Works across versions marker and the developer details) is built, game-free tested and verified live in part (2026-10-10)**; see [C5 as built](#c5-as-built) and its [live test](#c5-live-test-2026-10-10). Requested 2026-10-09: API features that help mod authors keep their mods working when the server or the client is a version ahead. Decisions of the same day on server-wide version matching are folded in ([Server rules](#server-rules-version-matching)). It is milestone M7 in the [roadmap](roadmap.md#m7-mod-compatibility-across-versions), scheduled after auto-update phase 2. Every API name below is a proposal. Statements about today's code are **source-checked** against the files named; nothing here is game-free tested or live-verified.

## The problem in one paragraph

Today a two-sided mod is either the same version on both sides or it is refused. The handshake compares version strings (`NetworkVersionRule` `Exact`, the default, `Minor` or `None`); a mod cannot say "1.4 still talks to 1.2". Its messages are fixed-size structs read with `CustomEvents.TryRead<T>`, which drops any payload whose length is not exactly `sizeof(T)`, so adding one field breaks every older peer. And a mod cannot ask what the other side can do, so it cannot send a new message only to peers that understand it. Progenitor solved all three for itself with capability bits; this design gives mods the same tools.

## What exists today

| Piece | Today (source-checked) | Gap this design fills |
|---|---|---|
| Progenitor's own feature detection | `Handshake.Capability` (`Networking/Handshake.cs`): a `[Flags]` word in the reserved row `progenitor.hs.cap`, hex in the version slot. An unknown bit is ignored. Every new Core channel is sent only to a peer that declared its bit (`RemoteInstall`, `JoinAdmin`, `PolicyRecheck`, `CommandHints`, …). | Mods have no equivalent. |
| Mod version rule | `[NetworkCompatibility(level, rule)]` (`Plugins/NetworkCompatibility.cs`): levels `ServerMustHave` / `NoNeedForSync` / `ClientOnly`, rules `Exact` (default, case-insensitive string equality) / `Minor` / `None`. Travels in version-slot bytes 22–23 of a handshake row. | No range, no way for a newer build to vouch for an older peer. |
| Handshake capacity | 16 rows of a 64-byte guid and a 24-byte version (`Handshake.MaxMods`). | Too small for mod feature lists. |
| Admin rules for required client mods | `ClientModRule` `AtLeast` (default, "or newer") / `Exact` / `SameMinor` / `Any` (`Networking/ClientModPolicy.cs`), per row, at most 16 rows ([client-mod-policy.md](client-mod-policy.md#required-client-mods)). Applies only to client mods the admin requires; mods the server runs keep their author's attribute rule. | No server-wide strictness, no override for mods the server runs. |
| Client mod report | `'PrCm'` kind 0: chunked UTF-8 JSON of every client mod (guid, name, version, source, package), sent only to a server whose request set `Capability.ClientPolicy` (`Networking/ClientModReport.cs`). | Carries versions, not features or ranges. |
| Kept versions | `ModVersionStore` keeps several copies of a client mod and swaps in the one a server needs at the handshake ([join-flow.md](join-flow.md#9-keeping-both-versions-stage-4)). Progenitor's own files are not kept ([auto-update.md](auto-update.md#progenitor-itself)). | Progenitor itself cannot follow an Exact server. |
| Catalog | Operator-signed JSON, `'PrmM'`, rows with `id`, `version`, `level`, `versionRule`, `hotLoadable`, package fields ([mod-manifest.md](mod-manifest.md#catalog-body)). An older client re-canonicalizes without keys it does not know, so a **new key breaks its signature check** ([server-only plugins](mod-manifest.md#server-only-plugins)). | A range cannot simply be added as a key. |
| Package manifest | `progenitor.json` in the package zip: `id`, `version`, `side`, `hotLoadable`, `dependencies`, … (`Install/PackageInspector.cs` `TryParseProgenitorJson`). Unknown keys are ignored. | No compatibility declaration. |
| Mod messages | `ctx.Net` (`Plugins/PluginContext.cs` `PluginNet`): unmanaged structs on a mod-chosen 4-byte event id, at most 15 KiB (`CustomEvents.MaxPayloadBytes`). The `0x5072____` range is Core's. Reads are exact-size. | No version, no tolerance. |
| Unknown event ids | A packet whose id has no handler in this process is **passed to the game's own reliable-events reader** (`CustomEvents.TryDispatchClient` returns false; `ReceiveSlotPolicy.Decide` drops only ids in its owned set: Core's protocol ids plus ids registered through Core in this process). | Sending a mod message to a peer without that mod's handler feeds a foreign packet to the game. This is the strongest reason for gated sends. |
| Synced settings | `ctx.Bind(..., synced: true)` ([config-sync.md](config-sync.md)): a plugin's whole snapshot is skipped when the versions fail its `NetworkVersionRule`; an unknown plugin or key on the client is ignored with one line; a type mismatch is one `schema-drift` line and a skip; a key the server does not send keeps the client's **local** value, unlocked. | A missing key should mean "the server's mod has no such setting", which behaves like the default. |
| Content | `ctx.Content.DefineItem(id)` / `DefineBuff(id)` hash a stable string id to a GUID; the server sends each definition (`'PrfD'`) and the join-time order (`'PrfI'`), so a client **without the mod** still gets a working clone with the server's name, description and stack size, but not the icon (client-local). A client-defined item the server does not list is refused and unusable ([key-table.md](key-table.md)). Unregister is a soft unapply with a tombstone (`'PrfK'`) so key-table indices never move. | No author rules for adding and retiring ids; no fallback for an id a peer cannot resolve in a message. |
| Counterparts | `[Counterpart(Required / Limited / Optional, Features = …)]` and `ctx.Handshake.ServerPart` (`Plugins/Counterpart.cs`): whether the other side runs at all, with human-readable feature names for the mod page. | Presence only, no machine-checkable features. |
| Auto-update cap | The pre-join prompt offers an update only when the server's remembered rule accepts its version ([auto-update.md](auto-update.md#pre-join-prompt)). | It reasons about version numbers, not about what actually works together. |

## Design principles

1. **Ask what the peer can do, not what version it is** (the rule Progenitor already follows, [versioning.md](versioning.md#how-the-numbers-are-computed)). Versions decide who may join; features decide what is sent.
2. **The newer side knows more.** A build cannot know what its successors will do, but a successor knows what its predecessors did. Where two declarations disagree, the newer build's declaration about the older peer is the one that counts, unless an admin says otherwise.
3. **Fail cautious, never silent.** Unknown means "not supported": `Peer.Has` is false until the peer's table arrives; a mod that declares nothing gets "Same minor". Every refusal or downgrade logs one line naming the mod, both versions and the rule.
4. **Ride on what exists.** One new capability bit gates everything; mod traffic travels inside Core-owned event ids, so a peer that lacks a mod's handler drops the packet in Core instead of handing it to the game.
5. **Admins have the last word.** A per-mod admin rule beats the server-wide setting, which beats the authors' declarations.

## 1. Feature negotiation

### API

```csharp
// Both sides, in OnLoad. Names are scoped to this mod's GUID automatically.
ctx.Features.Offer("trade");            // level 1
ctx.Features.Offer("trade", level: 2);  // a later revision of the same feature
ctx.Features.Offer("map-pins");

// Client: the joined server.
if (ctx.Peer.Has("trade", minLevel: 2)) SendTradeV2(); else SendTradeV1();
bool other = ctx.Peer.Has("othermod.guid", "pins");   // another mod's feature
string? v = ctx.Peer.ModVersion("othermod.guid");      // null when the peer lacks it
ctx.Peer.Changed += OnPeerChanged;                     // join, leave, peer hot reload

// Server: one view per connection.
IPeerView p = ctx.Peers.Of(userEntity);
if (p.Has("trade")) ...
ctx.Peers.Changed += (Entity user) => ...;             // that user's table arrived or changed
```

```csharp
public sealed class PluginFeatures
{
    /// Adds or raises a feature of this mod. Tracked: unload withdraws it.
    public IDisposable Offer(string name, int level = 1);
    public IReadOnlyDictionary<string, int> Offered { get; }
}

public interface IPeerView
{
    /// False until the peer's table has arrived (and always false for a peer whose
    /// Core predates this feature). Has() is false while Known is false.
    bool Known { get; }
    bool Has(string feature, int minLevel = 1);                 // this mod's feature
    bool Has(string modGuid, string feature, int minLevel = 1); // any mod's feature
    string? ModVersion(string modGuid);                         // from the table, else the handshake rows
    string? CoreVersion { get; }
}
```

`ctx.Peer` on the server is a view that always answers `Known = false`, so shared code paths do not crash; server code uses `ctx.Peers.Of(user)`. A feature is identified by `(mod guid, name)`; the level is an integer that only grows. Renaming a feature is a removal plus an addition.

### Wire

- **Capability.** A new bit `Handshake.Capability.ModFeatures` (32768, the next free bit; 8 to 32 stay reserved for join-flow stages). The server sets it on its request row; a client that supports it declares it in its answer.
- **Feature table.** A new Core id `'PrFt'` (`0x50724674`), chunked like the catalog (`[version u8][kind u8][seq u16le][count u16le][nbytes u16le][bytes]`, 15 KiB per packet, reassembly cap 32 KiB). Kind 1 is a full table, kind 2 a delta. Body (UTF-8 JSON, parsed with the `JsonDocument` limits the report uses):

  ```json
  { "v": 1,
    "core": "2026.10.12", "coreSha": "<sha256 of Progenitor.dll>", "bootstrapProtocol": 5,
    "mods": [ { "id": "example.trade", "ver": "1.4.0", "peers": ">=1.2.0 <2.0.0",
                "f": { "trade": 2, "map-pins": 1 } } ] }
  ```

  Every loaded Progenitor plugin is listed (so the table also lifts the 16-row limit on knowing the peer's mod versions), with `peers` only when declared ([section 2](#2-declared-compatibility-range)) and `f` only when non-empty.
- **When.** The client sends its full table right after its handshake answer, only when the request carried `ModFeatures`; the server waits for it inside the existing answer deadline, the way it waits for the client mod report. The server sends its full table right after an `ok` result. A client that did not declare the bit is judged on the handshake rows alone, exactly as today.
- **Hot reload and Core swaps.** Offers are tracked by the plugin's context, so unload withdraws them and `OnLoad` offers them again. Core collects changes for one second (the catalog's existing scan) and sends one delta to every handshake-`ok` peer that declared the bit; the receiver raises `Changed`. After a Core swap the new generation sends its full table again once its handshake state is restored. A version change that takes a peer out of range mid-session is handled like a required mod changing mid-session today (the join notice and the install window), not by a kick from this channel.

### Limits

| Limit | Value | When exceeded |
|---|---|---|
| Feature name | 1–48 characters, `[a-z0-9][a-z0-9._-]*` | `Offer` throws `ArgumentException` |
| Level | 1–65535 | `Offer` throws |
| Features per mod | 32 | `Offer` throws; the message names the limit |
| Mods in one table | 256 | The rest are left out, flagged `"more": true`, and logged once; the peer treats a missing mod's features as absent |
| Encoded table | 32 KiB (at most three packets) | Same as above |
| Deltas | At most one per second per peer | Coalesced |

## 2. Declared compatibility range

### Declaration

A mod declares which **peer versions of itself** it works with, in code and in its package:

```csharp
[BepInPlugin("example.trade", "Trade", "1.4.0")]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = ">=1.2.0 <2.0.0")]
public sealed class Plugin : ProgenitorPlugin<PluginContext> { ... }
```

```json
// progenitor.json in the package
{ "id": "example.trade", "version": "1.4.0", "side": "both",
  "compatibility": { "peers": ">=1.2.0 <2.0.0" } }
```

- **Syntax.** Space-separated comparators (`>=`, `>`, `<=`, `<`, `=`) on `major.minor.patch`, joined with `||` for alternatives; shorthands `1.2.x` (same minor) and `*` (any). At most 64 characters. Versions compare as numbers with missing parts as zero, a prerelease below its release (the auto-update comparison, [Phase 1 as built](auto-update.md#phase-1-as-built)).
- **The two must agree.** Package inspection compares `compatibility.peers` with the attribute in the image, as it already does for `side`; a contradiction refuses the package with `InstallReasons.InspectContradictory`. `tools/package.ps1` writes the field from the attribute so authors write it once.
- **Default when nothing is declared: Same minor** (`1.4.x` for a 1.4.0 build). `Peers` overrides the attribute's `NetworkVersionRule` for peers that understand ranges; the rule byte still goes on the handshake row for older peers, so an older Progenitor sees exactly today's behavior.

### Evaluation at the join

For each mod both sides run, the server decides with both tables in hand:

1. A per-mod **admin rule** for that mod, when set, decides alone ([Server rules](#server-rules-version-matching)).
2. Otherwise the server-wide **Version matching** setting applies. Under **Use each mod's declared range**:
   - when the versions differ, the **newer** side's range must include the older version (principle 2);
   - when the newer side declared nothing, its default Same minor applies;
   - equal versions always pass.
3. A client whose Core predates `ModFeatures` sent no ranges: the server evaluates its rows with the server's own range for each mod, then the attribute rule, which is today's behavior.

A passed mod writes `range` as the reason in the server log (`Handshake: example.trade client 1.2.3 accepted by server 1.4.0's range >=1.2.0 <2.0.0`). A refusal names the side whose range decided: `Mods needed: Trade (this server's 1.4.0 works with 1.2.0 or newer, below 2.0.0; you have 1.1.0).`

### Where else the range is used

- **Catalog.** Signed catalog rows gain `peers`. Because an older client rejects a signature over a key it does not know, the server signs two forms: the current one for clients without `ModFeatures`, and the extended one for clients that declared it. Both are signed with the same operator key.
- **Config sync** sends a plugin's snapshot when the range evaluation passes, instead of the attribute rule alone ([section 4](#4-tolerant-synced-settings)).
- **Version store and join screens.** "Use server's version" is offered only when the range evaluation fails; a kept copy is chosen by the same evaluation (`ModVersionSelect` gains a range input).
- **Auto-update** ([below](#tie-in-with-auto-update)).

### Server rules: version matching

Decided 2026-10-09: servers can require version matching. A server-wide setting, **Version matching**, on top of the per-mod admin rules:

| Choice | Meaning |
|---|---|
| **Use each mod's declared range** (default once ranges exist) | The evaluation above. |
| **Same minor for all** | Every mod the server checks must have the server's major and minor. Declared ranges are ignored. |
| **Exact version for all** | Every mod the server checks must be the server's version exactly, and **Progenitor itself** too (decided 2026-10-09; [below](#exact-applies-to-progenitor-itself)). |

- **Which mods.** "Every mod the server checks" means the server's `ServerMustHave` mods, the admins' required client mods, and a `NoNeedForSync` mod that both sides run. A both-sides optional mod at another version is **paused for this server** when it can be paused (the client policy's existing pause), else the join is refused; a peer that simply lacks an optional mod is not affected.
- **Per-mod rule wins.** An admin rule on a required client mod (`Exact`, `or newer`, `Same minor`, `Any version`, unchanged) overrides the server-wide setting for that mod. The Rule button is extended to the mods the server runs, so an admin can, for example, choose Exact for all but Any version for a cosmetic pack.
- **Home.** The Server tab, group **Mods players need** (lock word `mods-players-need`), as a radio list above the required client mods, mirrored in the Players joining panel. [Placement rule 3](settings-organization.md#for-progenitor-itself-where-does-a-new-setting-go): it affects everyone on the server. Stored in `progenitor-join-admin.json` with the other policy values; `[ClientPolicy] VersionMatching = range | minor | exact` in the cfg for an operator who locks it. Under Exact the row's help reads `Players must have exactly the versions this server runs, including Progenitor.`
- **Reasons players see.** The existing refusal sentence gains the exact form: `This server requires exactly ChatSkin 1.2.0 (you have 1.3.0).`, `This server requires Progenitor 2026.10.12 exactly (you have 2026.10.14).` The join screens' row reads `Needs exactly 1.2.0 · You have 1.3.0` with **Use 1.2.0**.
- **Pre-join prompt and auto-update under Exact.** The prompt offers exactly the server's version and never anything newer: it swaps in the kept copy from the version store when there is one, else installs that version from Thunderstore, and leaving restores the player's own version. An update is never offered for a mod an Exact server checks; the prompt lists it as `Not offered: ChatSkin (this server requires exactly 1.2.0)`.
- **Protocol and capability checks still apply underneath.** Matching versions do not skip `CoreLayout.ProtocolVersion`, the Bootstrap protocol check at Core load, or any capability bit. Exact is an additional refusal, never a replacement.

### Exact applies to Progenitor itself

Decided 2026-10-09. Under **Exact version for all**, a client whose Progenitor differs from the server's is refused, and the fix happens where the player is.

- **What is compared: Core only (recommended).** The client's `core` version and `coreSha` from its feature table against the server's own. Builds are deterministic, so one release has one sha256; the version string alone would let two different unmerged builds of the same release pass ([versioning.md](versioning.md#how-the-numbers-are-computed)). Bootstrap is **not** compared by version: it carries no wire protocol of its own, and its compatibility with the server's Core is already enforced by the Core/Bootstrap protocol check when the client loads that Core. Comparing Bootstrap too would force a restart for every Bootstrap-only release with no effect on what crosses the wire.
- **An older client** (a Core without `ModFeatures`) sends no table, so it cannot prove its Core: it is refused with `This server requires Progenitor 2026.10.12 exactly. Update Progenitor to join.` Its existing deny report shows the sentence.
- **The fix, in place.**
  1. Progenitor's own Core joins the version store (`progenitor-versions/progenitor/<version>/`: `Progenitor.dll` and its address tables). This reverses the auto-update rule "Progenitor's own versions are not kept in the version store" for this one purpose: the server's Core is **placed for that server** and the player's own Core is restored on leaving, the way a mod is today. It is never a change of the player's preferred version.
  2. The refusal (or, on a later join, the pre-join prompt, which remembers the server's Core in `progenitor-server-requirements.json`) offers **Use Progenitor 2026.10.12 for this server**. The client takes the kept copy, or downloads that Progenitor release from Thunderstore through the self-update path ([auto-update phase 5](auto-update.md#progenitor-itself)), swaps Core at the title screen (Core hot-swaps there; never in a world or mid-join), confirms the swap as [hot-reload.md](hot-reload.md#swap-core-and-confirm-completion) requires, and rejoins.
  3. When the server's Core needs a Bootstrap protocol this client's Bootstrap does not accept, the swap is refused by Bootstrap. The dialog then offers **Restart and join**: Bootstrap and Core are staged as `.next`, the game restarts, and the rejoin record joins the server.
  4. A development build on the client (a sub-release version or the Dev bridge loaded) is never swapped; the refusal says `This server requires Progenitor 2026.10.12 exactly; this is a development build.`
- **No client is ever stuck.** The server enforces Exact for Progenitor only while its own Core is obtainable: a Progenitor release (no sub-release), whose package version Progenitor recorded when it installed itself (the release-hash ledger of auto-update phase 5), listed active in the index the server reads. When it is not (a development build, a hand build, a release withdrawn from Thunderstore), the server does **not** refuse on Progenitor's version, logs `VersionMatching: Exact is not enforced for Progenitor: 2026.10.12.3 is not published on Thunderstore`, and the Server tab row says so under the radio list. Mods are still held to Exact. The server re-checks availability on each index read it already makes. If availability is lost between that check and a join, the refusal says it in words: `… 2026.10.12 exactly, which is no longer on Thunderstore. Ask the server's admin.`
- **Range and Same minor do not apply to Progenitor** (decided 2026-10-09). Progenitor's versions are dates, `<year>.<month>.<build>`, so "same minor" would mean "same month" and a range over dates says nothing about compatibility. Only **Exact version for all** applies to Progenitor: the same Core build by sha256 (C4, [as built](#c4-as-built)). Under **Use each mod's declared range** and **Same minor for all**, Progenitor's protocol and capability checks decide, as today: the Bootstrap protocol at Core load, `Handshake.ProtocolVersion` on the wire, and the capability bits. The same rule decides what a breaking Progenitor update is ([auto-update.md](auto-update.md#progenitor-itself)).

## 3. Versioned, tolerant messages

### API

```csharp
[ModMessage("trade.offer", Version = 2)]
public sealed class TradeOffer
{
    [Field(1)] public int ItemId;
    [Field(2)] public int Count;
    [Field(3, Since = 2)] public string Note = "";   // default when an older sender omits it
}

// Receive (both sides). Tracked: unload removes the handler.
ctx.Messages.On<TradeOffer>((from, msg, info) =>
{
    // info.SenderVersion: the message version the sender wrote (1 or 2 here)
    // info.SenderModVersion: the sender's mod version, from its feature table
});

// Send. Returns a result instead of a bool so a skip is visible.
SendResult r = ctx.Messages.SendToServer(new TradeOffer { ItemId = 7, Count = 3 });
ctx.Messages.SendTo(userEntity, offer);
ctx.Messages.SendToAll(offer);       // only to peers that run this mod
```

- **Field rules.** Fields are numbered; a number is never reused or retyped. Types: `bool`, integers, `float`, `double`, `string` (UTF-8, at most 4 KiB), `byte[]`, enums (sent as their integer; an unknown value reads as the field's default and logs once), `PrefabGUID` and stable content ids (as strings, [section 5](#5-content-by-stable-ids)), lists of these, and nested `[ModMessage]`-less classes with their own `[Field]` numbers.
- **Unknown fields are skipped; missing fields keep the field initializer.** A field marked `Since = n` is documentation and a test input; the reader does not need it.
- **Breaking changes** raise `MinReaderVersion`: `[ModMessage("trade.offer", Version = 3, MinReaderVersion = 3)]`. A reader below it drops the message with one line per message name per generation (`Messages: dropped trade.offer v3 from <user>: needs reader 3, this mod reads 2`). The sender should check `Peer.Has` first; the compat contract says a breaking message change is a major version.
- **A hand-written codec** is available for hot paths: `ctx.Messages.On("trade.tick", (from, MessageReader r, info) => ...)` with `r.Int(1, default)`, `r.String(3, "")`, and `MessageWriter` for sending.

### Wire

- **Serialization: tagged binary**, written in Core with no new dependency (BepInEx stays the only runtime dependency). Each field is `[tag varint = number << 3 | wire type][value]`, wire types varint, 32-bit, 64-bit and length-delimited, so a reader can skip any field it does not know. This is the property JSON would also give, at a fraction of the size, and it needs no field names on the wire.
- **Envelope.** A new Core id `'PrMs'` (`0x50724D73`): `[envelope version u8][flags u8][mod hash u32][message hash u32][message version u16][min reader version u16][payload length u16][payload]`. Hashes are FNV-1a of the mod guid and the message name, as other Core hashes are; Core keeps the name table to detect a collision at registration (refused with the two names).
- **Gating.** Core sends only to a peer whose answer declared `Handshake.Capability.ModMessages` (65536) **and** whose feature table lists the mod. Otherwise the send returns `SendResult.PeerLacksMod` or `PeerTooOld` and nothing goes out. Because `'PrMs'` is a Core id, a peer that has the envelope but not this mod's handler drops the packet in Core (one line per mod per generation), never in the game.
- **Size.** The payload is at most 15 KiB minus the header (16 bytes as built; the field list above adds up to 16, not 15), the transport's single-packet limit. Larger messages are refused at send with `SendResult.TooLarge`; chunking is a later addition if a real mod needs it. Received messages pass the existing per-connection `ReceiveGate` (burst and rate limits) before any parsing.
- **Main thread.** Handlers run where `ctx.Net` handlers run today: on the game's main thread, inside the receive dispatch.

### Relation to `ctx.Net`

`ctx.Net` stays for raw and Core-adjacent use. It cannot be made tolerant: its ids are the mod's own, so a peer without the handler hands them to the game. The [unversioned-message warning](#6-guidance-and-tooling) points authors from `ctx.Net` to `ctx.Messages`.

## 4. Tolerant synced settings

What already holds ([config-sync.md](config-sync.md), `Networking/ConfigSync.cs`):

- An unknown plugin or section/key on the client is ignored with one line (`ConfigSync: ignored … no such plugin or entry`).
- A type mismatch is one `schema-drift` line and a skip; under `Minor` a packet without a type name is refused rather than applied as the wrong type.
- Values are BepInEx Toml strings; an entry whose value would not fit one packet is refused at Register.

What changes:

| Case | Today | Proposed |
|---|---|---|
| Plugin versions differ | Snapshot skipped unless the attribute rule allows (`Minor`, `None`) | Snapshot sent when the range evaluation of [section 2](#2-declared-compatibility-range) passes |
| Server sends a key the client lacks | Ignored, one line | Unchanged |
| Client has a synced key the server's snapshot lacks (the server's mod is older) | Keeps the client's local value, unlocked | Locked to the entry's **declared default** for the session, one line (`ConfigSync: example.trade/Trade/Fee not sent by the server (its version predates it); using the default 5`). The server's older mod behaves as if the setting were at its default, so the client should too. The local value comes back on disconnect, as for every locked entry. |
| Type mismatch | Skip | Skip and lock to the declared default, same line |
| An enum name the client does not know | Toml parse failure | Declared default, `schema-drift` line |
| Out of the client's declared bounds (`SyncedBounds`) | Clamped or refused per the bounds rules | Unchanged |

Authors keep one rule: renaming a key or changing its type or meaning is a new key ([config-sync.md](config-sync.md#join) already says this).

## 5. Content by stable IDs

What already holds: a definition's id is hashed to the same GUID on every peer; the server sends its definitions, so a client without the mod still sees a working item or buff with the server's name, description and stack size (the source item's icon, since icons are client-local); a client-only definition the server does not list stays unusable; key-table rows never move ([key-table.md](key-table.md)).

Rules this design adds:

- **Additive within a major.** A minor release may add ids. It must not remove, rename or change the `CloneOf` source kind of an id; a removal is a major release.
- **Retire instead of remove.** `ctx.Content.DefineItem("trade.coin").Retired()` keeps the definition registered and resolvable (inventories and saves keep working) but marks it not granted: `ctx.Content.IsRetired(id)` lets the mod stop handing it out. A retired id costs a key row, which it already does as a tombstone.
- **Fallback for peers that cannot resolve an id.** `.FallbackTo("Item_Ingredient_Coin_Silver")` names a vanilla prefab. `ctx.Content.Resolve(id, peer)` returns the definition's GUID when the peer's table lists the defining mod at a version that has the id (from the feature table, which carries the mod version), else the fallback, else `null`. Message fields that carry content are strings, so a receiver that cannot resolve an id gets `null` and the field's default rather than a wrong GUID.
- **No new wire for content.** `'PrfD'`, `'PrfI'` and `'PrfK'` are unchanged.
- **Saves are out of scope.** What the game does with a saved prefab GUID whose definition no longer exists after a restart is not established; this design does not claim removal is safe in a save. It is a research item, recorded under [what this cannot solve](#what-this-cannot-solve).

## 6. Guidance and tooling

### The compatibility contract

Published in [modules.md](modules.md) and the tutorial, and the basis of the default range:

| Change | Version part | On the wire |
|---|---|---|
| Removes or changes a message, a field's meaning, a feature, a synced key or a content id; raises `MinReaderVersion` | **major** | Breaking. The default range stops at the next major. |
| Adds a message, a field, a feature level, a synced key or a content id | **minor** | Additive. Older peers ignore what they do not know. |
| Changes nothing on the wire | **patch** | None. |

The cautious default (Same minor) assumes a mod has not been checked against the contract. A mod that follows it declares `>=X.Y.0 <(X+1).0.0`. Progenitor's own components use date versions and are covered by capability bits, not by this contract.

### Compat test helper

A game-free test library, `Progenitor.Testing` (shipped as a source folder for mod test projects, the way `tests/Progenitor.Tests` links Core sources today):

- **Record.** The Dev bridge op `messages_record on|off [mod]` writes every `'PrMs'` payload a mod sends or receives to `BepInEx/progenitor-dev/messages/<mod>/<mod version>/<message>@v<n>/<n>.bin` with a JSON sidecar (direction, sender version). The files go into the mod's repository as fixtures.
- **Replay.** `MessageCompat.ReplayAll<TPlugin>(fixturesDir)` decodes every recorded payload of every earlier version with the current message types, runs the registered handler against a fake context, and fails on an exception, a dropped message that the contract says must be read, or a field that came back other than its default when the fixture lacks it.
- **Forward check.** `MessageCompat.WithUnknownFields(payload)` appends random fields with unused numbers; the decoded message must equal the original's.
- **Range check.** `MessageCompat.AssertRangeCovers("1.2.0", fixturesDir)` fails when a recorded version inside the declared range has no fixtures.

### Unversioned-message warning

Core logs one line per mod and event id per generation when a plugin sends through `ctx.Net` while it is loaded on both sides and declares a range wider than one exact version: `Messages: example.trade sends raw ctx.Net messages (id TrOf); peers on another version may misread them. Use ctx.Messages.` The mod's page shows the same fact in its developer details. It never blocks a send.

### Samples adopt it

- `Progenitor.SampleServerLed` and `Progenitor.SampleClientLed` move their packets to `ctx.Messages` (today `ServerLedPolicy` rejects any packet whose `Version` is not exactly `PrefsVersion`), offer a feature each, and declare `Peers`.
- `Progenitor.Sample` offers a feature for its camera message and gains a `.compat` command that prints the peer's table.
- Each sample's test project carries recorded fixtures and a replay test.
- A new two-version demo for the live recipe: `Progenitor.HotDemo` built as 0.1.0 and republished as 0.2.0 with one added field and one added feature.

### Install and Mods pages

- **Works across versions.** A package row on the Install tab and a mod's page show `Works across versions` with the range in its tooltip (`Works with 1.2.0 or newer, below 2.0.0`) when the range is known: from the signed catalog of the joined server, or from the inspection facts already cached in `progenitor-cache/verified/<namespace>/<name>/<version>.json` (the Thunderstore index carries no `progenitor.json`, so a listing that was never inspected shows nothing rather than a guess).
- **Mod page developer details** list offered features, the declared range and the peer's table for this mod while connected.

## Tie-in with auto-update

- **The pre-join prompt reasons about compatibility.** For each update it asks: with this server's remembered version, rule and range, would the updated version pass the join evaluation? A newer version whose own declared range includes the server's version is offered even when the server's remembered rule was `Exact` from a server-side attribute, because the newer build vouches for the older peer; an admin rule or an Exact **Version matching** setting is remembered too and still caps it. The new version's range comes from its inspected `progenitor.json` (the prompt already downloads and inspects before writing); before inspection the prompt falls back to today's version cap.
- **The server's own updates** (auto-update phases 3 and 4) use the same evaluation to predict whether connected players would still pass: an update that stays inside every connected player's range applies as a hot update while they play; one that would refuse them waits for the server's apply window, as a both-sided update does today.
- **Exact servers** pin both directions: no update past the server's version, and the version store swaps the exact copy in ([Server rules](#server-rules-version-matching)).

## What this cannot solve

- **Raw packets and Harmony patches that bypass the API.** A mod that sends its own ids through `ctx.Net` or `CustomEvents`, patches game networking, or writes components the game replicates on its own is outside this design. Feature bits can still gate such code, but the framework cannot make those bytes tolerant.
- **Game updates.** A new game build can change layouts, prefabs and systems on both sides at once. That is handled by the game-identity checks that exist (the `'PrfE'` build identity, the catalog's game-update tags, the Resilience fingerprint), not by mod version ranges.
- **Semantics.** A range is the author's claim. The framework checks that a message parses, not that two versions agree on what it means; the replay helper only catches what fixtures exercise.
- **Lying clients.** Feature tables, like the client mod report, are the client's own word. This is compatibility, not anti-cheat.
- **Removed content in saves** (above): not established, not claimed.

## Implementation plan

Each phase is game-free tested before the next depends on it, and live-verified on the isolated test server and test client.

| Phase | Scope | Game-free tests | Bootstrap change |
|---|---|---|---|
| C1 | `ModFeatures` bit, `'PrFt'` table (full and delta), `ctx.Features`, `ctx.Peer`, `ctx.Peers`, limits, hot-reload deltas, Core-swap resend | `FeatureTableTests`: encode/decode, limits, `more` truncation, unknown keys ignored, delta coalescing, `Known` false before arrival and for a client without the bit, withdraw on unload | None: confirmed. The new ids register through `CustomEvents.OnReceive` / `OnReceiveFromClient`, which already add them to Bootstrap's process-lifetime owned set (`ReceiveSlotHandoff.RegisterOwnedEventId`), as `'PrCm'` and `'PrCh'` do. **Built 2026-10-09**, live-verified (see Status). One deviation: the server records the client's table whenever it arrives and does not hold the join for it; the wait belongs with C3, whose join evaluation is the first thing that needs the table at the join. |
| C2 | `ctx.Messages` on `'PrMs'`, tagged binary codec, `MinReaderVersion`, gated sends, the unversioned warning | `ModMessageCodecTests` (every type, unknown fields skipped, missing defaulted, nested, size limit), `ModMessageGateTests` (no send without bit or mod, drop for an unknown mod in Core, hash collision refused); `ModCompatEndToEndTests` (two fake peers on different versions, both directions) | None. **Built 2026-10-09**, live-verified (see Status). The send result adds `PeerNotReady`, `NotConnected`, `Invalid` and `TransportFailed` to the designed values; the raw-`ctx.Net` warning fires when a peer's table lists the mod and the versions differ or the mod declares no rule or a rule other than `Exact` (ranges arrive with C3). |
| C3 | `Peers` on the attribute and in `progenitor.json`, inspection agreement, `tools/package.ps1`, join evaluation (newer side decides), dual-signed catalog, config sync on ranges and the default lock, **Version matching** with per-mod rules for server-run mods and the Exact refusal sentences for mods | `CompatRangeTests` (syntax, compare, newer-side rule, defaults), `VersionMatchingTests` (three modes, per-mod override, optional mod paused, older client fallback), `CatalogDualSignTests`, `ConfigSyncRangeTests` | Yes, partly. **Built and live-verified 2026-10-10** (Steam client and the isolated test server: ranges, the default refusal, the hold with a client mod policy, Exact and an "or newer" rule switched while connected, switching back, config sync); not tested with an older-Core client or a headless client; see [C3 as built](#c3-as-built). |
| C4 | Exact for Progenitor: Core in the version store, placed-for-server swap at the title screen and restore on leaving, Restart and join for a Bootstrap protocol gap, server-side availability check; pre-join prompt and auto-update on ranges | `ProgenitorExactTests` (Core sha compare, older client refusal text, dev build refusal, not enforced when unobtainable, Restart and join route), `PreJoinRangeTests` | None: confirmed (the placement file and route are Core only; `SelfUpdateFiles.cs`, which Bootstrap compiles, is unchanged). **Built 2026-10-10**, game-free tested, not live-verified; the pre-join prompt and update cap on ranges were built with C3. Deviations: [C4 as built](#c4-as-built) |
| C5 | `Progenitor.Testing` (record, replay, unknown-field and range checks), `messages_record` bridge op, samples adopt everything, Works across versions marker, mod page developer details, docs (modules, tutorial, api) | The samples' own replay tests; UI placement tests for the marker | No. **Built 2026-10-10**, game-free tested, live-verified in part; see [C5 as built](#c5-as-built) |

### C3 as built

**Built 2026-10-10. Source-checked, built (`dotnet build`) and game-free tested (`dotnet test`, the whole suite passing). Not verified live:** nothing below has run in the game, on the test server or with a headless client yet. The API reference is [modules.md](modules.md#compatibility-ranges-and-version-matching-mod-compatibility-c3).

| Scope item | State | Game-free tests |
|---|---|---|
| `Peers` on `[NetworkCompatibility]` (and the one-argument constructor that leaves the rule undeclared); `CompatRange` syntax and comparison | Built | `CompatRangeTests` |
| `compatibility.peers` in `progenitor.json`, and inspection agreement with the image (`InstallReasons.InspectContradictory`) | Built. The image's `Peers` is read from its attribute blob without loading it (`Install/Admission/PluginCompatReader.cs`) | `PackageCompatRangeTests` |
| The packager writes `compatibility.peers` from the attribute | Built in `tools/make-dummy-package.ps1`, the mod packager. `tools/package.ps1` packages Progenitor itself, which takes no range, so the design's file name was wrong | none (script) |
| Join evaluation: the newer side's range decides, equal versions pass, nothing declared means the same minor; an older client is judged on the server's own `Peers`, else the row's rule | Built. The server holds a `VersionRanges` client's answer up to 3 seconds for its feature table (`FeatureTableHold`), the wait C1 deferred | `VersionMatchingTests` |
| Dual-signed catalog: the current form byte-identical to the pre-C3 one, the extended form with `peers` and `match` per row and the server's `matching` | Built. Each user gets the form its capability reads, for the full catalog, the deny and install descriptors and every resend | `CatalogDualSignTests` |
| Re-signing on a change of Version matching or a per-mod rule | Built. `matching` and each row's `match` are signed and part of the change stamp, so an admin's change, a cfg change or a rule re-signs at once and the next frame's scan resends; a C3 client takes the catalog's `matching` over its join's request bits, connected and in the remembered requirements | `CatalogDualSignTests`, `CatalogMatchingClientTests` |
| Config sync on ranges, and the default lock for synced keys the server did not send | Built (the `progenitor:snapshot-sent` marker per plugin, only to `VersionRanges` clients) | `ConfigSyncRangeTests` |
| **Version matching** (range, Same minor for all, Exact version for all) and per-mod rules for server-run mods; the cfg override `[ClientPolicy] VersionMatching`; the Server tab row and the Players joining panel | Built | `VersionMatchingSettingTests`, `ThreeHomesTests` |
| Exact and Same minor refusal sentences for mods | Built (the handshake's `Mod mismatch:` reasons) | `VersionMatchingTests` |
| A both-sides optional mod at another version under a strict mode is paused for that server, else refused | Built on the client, from the request's `MatchExact` / `MatchSameMinor` bits | `StrictMatchPauseTests` |
| Kept-copy choice and the pre-join prompt and update cap reason with ranges (designed under C4 in the plan above; built here because the client half needed only C3 data) | Built | `PreJoinRangeTests`, `PackageCompatRangeTests` |
| The client judges versions as the server's join check did: the join screens' required rows, the refusal (deny) rows, the in-world re-answer check, the catalog comparison report and the Install tab's per-side status | Fixed 2026-10-10 after the first live test (below). `VersionMatching.ClientContextFor` rebuilds the server's judge from the catalog's `matching`, each row's `match` (a per-mod rule) and `peers`, and this client's own effective ranges, and `VersionMatchJudge.Check` (the server's own function) decides. Without a catalog that carries `matching` (an older server) the request rows' rules decide, as before | `RangeAwareClientJudgeTests` |

**First live test (2026-10-10), two defects, fixed and re-verified live the same day.** Server Range mod 1.2.0 with `Peers=">=1.2.0 <2.0.0"`, client 1.3.0; server Default mod 1.4.0 with no range, client 1.5.0; Version matching = range. The server refused the join for the Default mod only and logged the Range mod as accepted by the client's range, as designed. Two client-side views disagreed with it:

- The join screen counted the Range mod as needing attention (`Needs 1.2.0 · You have 1.3.0 (newer)`, `you're ahead`, Tell the admin), because its rows were judged by the request row's attribute rule. Now a version the server accepts is ready; under Exact version for all, Same minor for all or an admin's per-mod rule the same pair still needs attention. A refusal's catalog is narrowed to the refused rows but keeps `matching`, so a row it left out is judged with this client's own range when the client is newer, else with the server's request row's effective range (a server-newer row whose wider declared range only the full catalog carried can still show as needing attention on a refusal).
- After a successful join the catalog report listed both mods under `mismatch (2): ... (Exact)`, the catalog row's rule. Now an extended catalog is judged by the server's judge: an accepted version is listed as present with what accepted it (`progenitor.compattest.default 1.4.1 (server 1.4.0, accepted by the client's range ...)`, `ManifestDiff.Accepted`), and a mismatch is only a version the server would refuse, with the server's own reason in place of the rule name. Its consumers: the mismatch list is read only by the log block and the Describe text; the offers that could nag about an accepted version were elsewhere and are fixed in the same change: the join screens' Use version / Tell the admin (the rows above), the refusal rows (rebuilt with the server's judgment when the descriptor arrives), the in-world re-answer preflight and the panels' "join check" reason (`Handshake.ClientJudge`), and the Install tab's **Version mismatch** / Update server / Update client status for a declared both-sided package (`SideInputs.VersionsAccepted` from the connected server's requirement through `ServerVersionCheck`). The pre-join prompt, the update cap, the kept-copy choice and the update badges already reasoned with ranges (`ServerVersionCheck`).

**Review fixes after the live re-check (2026-10-10; game-free tested, not live-verified).** The client's own range is looked up by mod id **and version** (`VersionMatching.OwnRangeOf(id, version)`, `VersionJudgeContext.ClientRangeOf`): the loaded copy's declared range applies only to the loaded version. A turned-off copy, a pending install or a kept copy at another version is judged with the default for its own version (join screens' **Turn on**), or with no range so the request rule's cap applies (the Install tab's per-side status), never with the loaded copy's range, which had produced false "OK"s. The refusal rows keep the rows already on screen when the server's request was already forgotten by the time the catalog finished (`RangeAwareClientJudgeTests`).

**Known limitation (client prediction vs the server).** The server judges with the client's ranges only when it has the client's feature table. When the 3-second hold expires without it (`FeatureTableHold`), the server judges as for an older client (its own declared range, else the row's rule), and a mod beyond the table's row cap (`ModFeatureLimits.MaxModsPerTable`, 256, or its byte cap) gets the default range on the server; in both cases a row the client shows as ready can still be refused. The refusal's kick reason is then the authority.

**Live results (2026-10-10, Steam client and the isolated test server, Core and Dev from this branch on both sides).** Test plugins: `Progenitor.CompatTest` built as `-p:CompatTestVariant=Range` (1.2.0 on the server; 1.3.0 with `-p:CompatTestNewKey=true` on the client), `-p:CompatTestVariant=Default` (1.4.0 on the server; 1.4.1 and 1.5.0 on the client) and the default build (optional, `NoNeedForSync`: 1.0.0 on the server, 1.1.0 on the client). The server's client mod policy was on.

| Step | Observed |
|---|---|
| Ranges | Joined. Server: `progenitor.compattest.range client 1.3.0 accepted by the client's range >=1.2.0 <2.0.0 (server 1.2.0)` and `progenitor.compattest.default client 1.4.1 accepted by the client's range 1.4.x (server 1.4.0)`. |
| Config sync | The server's `Limit = 7` applied and locked; the client-only key fell back to its default for the session: `ConfigSync: progenitor.compattest.range/Sync/NewLimit not sent by the server (its version predates it); using the default 5` (local value 9), and the plugin read `Limit=7 (locked) NewLimit=5 (locked)`. |
| Default rule | Client 1.5.0 against server 1.4.0 refused: `your 1.5.0 works with any 1.5.x version; this server has 1.4.0`. After the fix the refusal screen read `1 needs attention`, with the Range row `ready`. |
| Hold with a client mod policy | `received report ... kept until its answer's judgment, which waits for the feature table`, then `answered ok; judging the join on the report it sent during the feature table hold`; no 20 s wait. |
| Exact, switched while connected | The Players joining panel's Apply logged `VersionMatching: now exact after version-matching; applies from the next join` and `ModManifest: catalog re-signed (join-admin version-matching); resent to 1 client(s)`; the client refreshed its open screens. The next join was refused with `this server requires exactly 1.4.0 (you have 1.4.1)` for each mismatched mod; nothing was paused because pausing the optional mod alone could not make the join succeed (`ServerModPause: the join is predicted to fail and nothing was paused`). With the required mods at the exact versions, the optional mod was paused and the join succeeded (notice: `Paused 1 mod for this server: Progenitor Compat Test (Another version than this server's (exact match required))`). |
| "Or newer" rule | A per-mod `at-least` rule re-signed the catalog; on the next join `progenitor.compattest client 1.1.0 accepted by the admins' rule for it (1.0.0 or newer)` and nothing was paused. |
| Back to ranges | Switching back and clearing the rule re-signed and resent twice; the server's store read `stored range, forced null, effective range`. |
| Catalog report (after the fix) | `present (5): progenitor.compattest 1.1.0 (server 1.0.0, accepted by the client's range *); progenitor.compattest.default 1.4.1 (server 1.4.0, accepted by the client's range 1.4.x); progenitor.compattest.range 1.3.0 (server 1.2.0, accepted by the client's range >=1.2.0 <2.0.0); ...`, `mismatch (0)`. |

Not observed live: the Install tab's per-side status for an accepted version, an older-Core client receiving the current catalog form, and a headless client.

**Remaining (not built in C3):**

- The Exact wording on the join screens is fixed in C4: `Needs exactly 1.2.0`, `Needs 1.2.x`, `Needs 1.2.0 or newer` ([C4 as built](#c4-as-built)). The button keeps its existing caption (**Use version 1.2.0**, **Use kept 1.2.0**).
- The server's own updates (auto-update phases 3 and 4) do not yet predict with ranges whether connected players would still pass; they keep today's rule.
- Exact for Progenitor itself is built in C4.
- The Works across versions marker, the mod page's developer details and the compat test helper are C5.
- A per-mod rule mid-session re-judges nobody: it applies from each player's next join, as the panel says.
- Known gaps from the review, game-free reasoning only: a client reply deferred across a client Core swap is answered by the new generation as if under declared ranges (the carried request does not record the strict mode), so an optional mod paused for Exact or Same minor can be resumed and the join refused; and for a package with several plugins, the inspected range belongs to the plugin `progenitor.json` names, so an update of another plugin in it is predicted with that range.
### C4 as built

**Built 2026-10-10. Source-checked, built (`dotnet build` of the solution) and game-free tested (`dotnet test`, the whole suite passing; new: `ProgenitorExactTests`, `ProgenitorPlacementTests`). Verified live in part the same day** ([live test](#c4-live-test-2026-10-10)): the Exact judgment by sha256, the refusal, the Progenitor row, **Use Progenitor X** from Thunderstore and from a kept copy, the accepted rejoin, the restore after leaving, the held update, the panel and Server tab lines, and the not-enforced and Same minor negatives. Not live: the restart route (another Bootstrap protocol), older clients (C1/C2/C3 or no table), the mid-session re-judgment, the 3-day and repeated-restore paths, a damaged placement file, the on-demand index read, a headless client. Bootstrap is unchanged; `CoreLayout.ProtocolVersion` stays 5 and `Handshake.ProtocolVersion` stays 2.

| Scope item | State | Where |
|---|---|---|
| What is compared | The client's Core **sha256** (from its feature table) against the server's loaded Core, as decided (decision 6). The version string is only for the words: one sha256 is one build, and two builds of one release number differ (`you have another build of 2026.10.159`). Bootstrap is not compared. | `Networking/ProgenitorExact.cs` `Check` |
| Only Exact applies | Under declared ranges and Same minor for all nothing changes for Progenitor: its protocols and capability bits decide. | `ProgenitorExactStatus.Enforced` |
| No client is ever stuck (decision 7) | Enforced only while the server's own Core is obtainable: not a sub-release, named by the self-install record Progenitor wrote when it installed itself (or the update on trial), and listed active on the index the server reads (the one auto-update phase 3 caches). Otherwise `VersionMatching: Exact is not enforced for Progenitor: <why>` is logged once per change and the Server tab and the Players joining panel say so under the radio list. The status is cached 2 s and read by the catalog scan every second, so a change of availability re-signs the catalog at once. For 20 s after a Core load (`ProgenitorExactSource.PendingSeconds`) a Core image not read yet is **pending**, not a reason: nothing is logged or cached, a join under Exact waits for the client's feature table as under an enforced Exact, and its judgment waits (asked again every 0.5 s, `ProgenitorExact.JudgeWhenKnown`) until the status is known (a wait ends unjudged when the user answers again or disconnects); after the 20 s an unreadable image is final (`its Core image could not be read`, not enforced). | `ProgenitorExactSource`, `JoinAdminServer.Install`, `Handshake.OnResponse` |
| The refusal | `Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.158)`, one of the handshake's problems (first in the kick reason after `Mod mismatch: `) (the C3 sentence style; the design's `This server requires Progenitor … exactly` was reworded to match). The failing id is `progenitor`, never a row this server enforces, so the install window never opens for it: a Core cannot be swapped mid-join. | `Handshake.JudgeAnswer` |
| Older clients (decided) | **No feature table** (a Core before C1): refused, `(your Progenitor is too old to say which build it runs; update Progenitor to join)`; its existing deny report or join screens show the server's text. **C1/C2** (`ModFeatures` without `VersionRanges`): the join now waits for the table (at most 3 s, `FeatureTableHold`) while Exact applies to Progenitor, and the client is judged on its sha256 like any other; it draws no Progenitor row. **C3** (`VersionRanges` without `ProgenitorExact`): judged the same way; it gets the extended catalog form without the Progenitor keys (it could not verify them), so it shows the refusal text, not a row. A table that misses the hold: `(your Progenitor did not say which build it runs in time; join again)`. | `ProgenitorExact.HoldsForTable`, `Check` |
| The catalog | A third signed form, the **Progenitor form**: the extended form plus the body keys `progenitor` (the server's release) and `progenitorSha`, written only while Exact is enforced for Progenitor, for clients that declared the new capability bit `ProgenitorExact` (67108864) as well as `VersionRanges`. Without a requirement it is the extended envelope itself. The keys are in the change stamp, so turning Exact on or off, or the build becoming obtainable or not, re-signs and resends to every handshake-ok client, as a mode change does for mods; a refusal's narrowed descriptor keeps them (even with no failing mod row). | `ModCatalogForms.WithProgenitor`, `ModManifest.BuildForms`, `ModManifestDenyPolicy.Narrow` |
| The join screens | A Progenitor row first in the red band: `Needs exactly 2026.10.159 · You have …` (the readback; the drawn line is `needs exactly 2026.10.159`, then the player's version and the status while they fit, and the button's cell is as wide as its caption: live fix 2026-10-10); **Use Progenitor 2026.10.159** when the build can come here (a kept copy with that sha256, else the release on the index this client reads), else **Tell the admin** with the reason (a development build, a mod-manager install, changed files, not on Thunderstore here). | `JoinPageModel.ProgenitorRow`, [join-flow.md](join-flow.md#2-mods-page) |
| Use Progenitor X (decision 8) | The server's build is **placed for that server** through the self-update path: from the kept copy (no download), else the Thunderstore release, refused unless its Core is the server's sha256. At the title screen a Core whose protocol the running Bootstrap accepts is swapped in place and the running Bootstrap stays; another protocol stages the build's own Bootstrap and starts at the next launch. On a refused join the screens close so the swap runs now; in the world it waits until the player leaves. `BepInEx/config/progenitor-self-placed.json` (Core only) records the copy that ran before and the self-install record; automatic Progenitor updates wait while a build is placed, and a click on **Update** is refused. Once a join was accepted with it and the player is back at the title screen, the previous copy and record come back; a placement never used for a join comes back after 3 days. A build placed for one server and then another keeps the player's own copy as the one to give back, and the first placement comes back if the second never loads. A restore is tried at most once per title-screen visit (a visit ends with a join or a world); one whose swap did not run is tried again at the next visit, up to 3 times, then the placed build stays and Update Progenitor applies as usual. A restore that fails (or that Bootstrap rolls back) never lists the player's own version as a failed release. The placement is also written beside the placed build (`progenitor-versions/progenitor/<version>-<sha8>/placed.json`): a damaged `progenitor-self-placed.json` is moved aside (`.bad-<utc>`, logged) and the placement of the build that runs is recovered from that copy, so the player's own build is still the one given back. Both directions pass the hash allowlist (`progenitor-allow.txt`) like any update. Automatic mod updates are not held while a build is placed. | `Install/SelfUpdates.Place.cs`, `Install/SelfPlaced.cs`, `SelfRoute.Placement` |
| While connected | A click on **Update** for another Progenitor than the joined server requires is refused (`not now: the server you are on requires exactly Progenitor X`). Automatic Progenitor updates never apply in the world or during a join (the apply window), and the pre-join prompt never offers Progenitor (auto-update lists it on its own). | `SelfUpdates.ConnectedExactReason` |
| Wording fix (C3 leftover) | The join screens' version line and short line say how the server matches a mod it runs: `Needs exactly 1.2.0`, `Needs 1.2.x`, `Needs 1.2.0 or newer`, `Needs any version` (from the catalog's mode and per-mod rules; plain under declared ranges). Required client mods use the same words (they read `Exact 1.2.0`, `Same minor as 1.2.0` before). The Install tab's and pre-join prompt's cap under an Exact rule byte reads `needs exactly 1.0.0`. | `JoinText.NeedsWords`, `ServerVersionCheck.LegacyReason` |
| Server tab and panel | Exact's help: `Players must have exactly the versions this server runs, including Progenitor.`; under the radio list while Exact is chosen: `Players also need exactly Progenitor 2026.10.159, this server's build. Players already on the server are checked again when …` or the amber not-enforced line. | `VersionMatchingText.HelpExact`, `ProgenitorExact.AdminLine` |

**Review fixes (2026-10-10, game-free tested):**

- **One reading per answer.** Exact for Progenitor is read once when an answer arrives; the feature-table hold, the judgment and the refusal's descriptor all use that reading. A refusal over Progenitor signs the requirement it was judged with into a C4 client's descriptor even when the catalog was not re-signed yet, so the row always comes with the text.
- **Index read on demand.** When the join screens need the server's build and the index this client read does not list it (old, or never read), the client reads the index once (a conditional request with the update check's backoff) and the screens redraw when it lands.
- **Mid-session re-answers are judged again (kept, documented).** A connected player's game answers again after a client mod hot reload, and when this server re-asks after its own Core swap with players online. That answer runs the same judgment, so a player whose Progenitor is not this server's build is disconnected with the same sentence (`Mod mismatch: Progenitor: this server requires exactly Progenitor …`). The Server tab's line under Exact says so: `Players already on the server are checked again when their game answers again (a mod reload, or a re-ask after this server's Core is swapped).`

**Deviations from the design above, and what remains:**

- **The pre-join prompt does not offer Use Progenitor X.** The held join belongs to the running Core generation, which a Core swap ends, and there is no automatic rejoin after a swap. The refusal screen offers it instead, so a player whose placement was given back after leaving is refused once on the next join, then presses **Use Progenitor X** again (a kept copy: no download). The server's Progenitor is not stored in `progenitor-server-requirements.json`.
- **Restart and join** for a Bootstrap protocol gap stages the server's Core and Bootstrap for the next launch and says `Restart V Rising, then join <server> again.`; it does not restart and rejoin by itself.
- **No refusal "which is no longer on Thunderstore"**: when the server's build stops being obtainable, Exact simply stops being enforced for Progenitor (and the catalog is re-signed without the requirement).
- **The admins' refused-join record** lists failing mod rows only; a Progenitor refusal is in the server log, not in that record.
- A development build or a client with the Dev bridge loaded is never swapped (`[AutoUpdate] SelfUpdateDevOverride = true` lifts this for a live test, as for self-update).

#### C4 live test (2026-10-10)

Steam client (bridge 27341) and the isolated test server (bridge 27342), Bootstrap `7f66bc74…` on both, unchanged throughout. Test releases built from this branch with `-p:ProgenitorVersion=<v>` (Release), packed as Progenitor package zips with the running Bootstrap's exact bytes and the live tree's address tables, and published to the local Thunderstore stand-in on port 27450: **A** = `2026.10.900` (Core `41ac240e…`), **B** = `2026.10.901` (Core `0d9d7188…`, from 496dd653d), **B'** = `2026.10.902` (Core `ed0467b4…`, with the row fix below). Both sides first ran the branch's Debug Core (`2026.10.162.2`). Cfg changed for the test, all put back afterwards: both sides `[Install] DevThunderstoreUrl` and `[AutoUpdate] SelfUpdateDevOverride = true`, the server's `[RemoteInstall] ThunderstoreBaseUrl`, the client's `[AutoUpdate] Progenitor = Notify` (so it would not take A by itself). The server read the new index URL only after a Core reload (`core_reload`; fixed afterwards, see [Fixed after the C4 live test](#fixed-after-the-c4-live-test)).

| Step | Result | Log lines observed |
|---|---|---|
| Server on A (`self_apply`) | Pass. The first click was deferred by the 5-minute empty window and not applied when the window opened; a second `self_apply` applied it. `self_status.record.version` 2026.10.900, loaded Core `41ac240e…` | `op=route version=2026.10.900 route=Hot why="Core only, protocol 5: swapped in place"`, `op=confirm … result=loaded core=41ac240ee410 outcome=ok`, `op=healthy` |
| Client on B (`self_apply`) | Pass. `ping`: client Core `0d9d7188…`, server `41ac240e…` | `op=route version=2026.10.901 route=Hot`, `op=healthy version=2026.10.901` |
| Join under **Use each mod's declared range** | Pass: accepted, no Progenitor check | `Handshake: … ok (0 mod(s) match)` |
| Players joining panel: `version_matching exact`, apply, while connected | Pass. Panel and Server tab: `Players also need exactly Progenitor 2026.10.900, this server's build. Players already on the server are checked again when …` | `VersionMatching: Exact applies to Progenitor: players need exactly 2026.10.900 (41ac240ee410)`, `catalog re-signed (join-admin version-matching); resent to 1 client(s)` |
| Rejoin | Pass: refused. `join_ui read`: `required[0]` `id progenitor`, `origin progenitor`, `status newer`, `action use-progenitor`, `actionCaption Use Progenitor 2026.10.900`, `versionLine Needs exactly 2026.10.900 · You have 2026.10.901`. The screens opened on the server message page; **Continue** showed the mods page | `Handshake: … failed: Mod mismatch: Progenitor: this server requires exactly Progenitor 2026.10.900 (you have 2026.10.901)` |
| The drawn row (capture at 5120x1440) | **Failed, fixed on the branch:** the 170 px cell cut the button to `Use Progenitor 2…` and the line to `Newer than the server's · you have 2026.10.90…`. Fix: a **Use Progenitor** row's action cell is as wide as its caption (`JoinFlowLayout.RequiredActionWidth`), and its line leads with what the server needs, adding the player's version and the status while they fit (`ProgenitorLine`; test `The_progenitor_row_fits_its_caption_and_says_which_build_the_server_needs`). Re-checked live on B': the button reads `Use Progenitor 2026.10.900` in full, the line `needs exactly 2026.10.900` | |
| **Use Progenitor A** (`join_ui row progenitor`), from Thunderstore | Pass. The screens closed; the game's own `You were kicked from the server` box stayed until **Leave Game** (bridge `disconnect`); at the title screen the build was swapped in. `ping`: client Core `41ac240e…`. `self_apply` while placed: `held: Progenitor 2026.10.900 is in place for … until you leave it` (`self_status.offer.clickable` still reads true) | `op=place-download version=2026.10.900`, `op=place … result=ready from=thunderstore (waits for the title screen)`, `op=place-route … route=Hot why="Core only, protocol 5: swapped in place, the running Bootstrap stays"`, Core reload `reason=self-update 2026.10.900 (placed for …)`, `op=confirm … outcome=ok`, `op=placed version=2026.10.900` |
| Rejoin | Pass: accepted | server `Handshake: … runs this server's Progenitor 2026.10.900 (Exact version for all)`; client `op=placed-used version=2026.10.900 … (your own Progenitor comes back at the title screen after you leave)` |
| Leave to the title screen | Pass. Record back to 2026.10.901 (its own install time kept), `placement null`, `failed []`, `progenitor-self-placed.json` gone | `op=restore version=2026.10.901 … result="swap scheduled …"`, Core reload `reason=self-update 2026.10.901 (yours again after leaving …)`, `op=confirm`, `op=healthy`, `op=restored version=2026.10.901` |
| The same from B', with A kept | Pass, no download | `op=place version=2026.10.900 result=ready from=kept 2026.10.900-41ac240e`, then `placed`, `placed-used`, `restore`, `restored version=2026.10.902` |
| Same minor (server cfg `VersionMatching = minor`) | Pass: B' against A accepted, no Progenitor line | `catalog re-signed (version matching cfg)`, `Handshake: … ok` |
| Server on a development Core (`2026.10.162.3`; Exact still stored) | Pass: B' accepted | `VersionMatching: Exact is not enforced for Progenitor: 2026.10.162.3 is a development build`, `Handshake: … ok` |

Observed in passing: right after a server Core load the first status read logged `Exact is not enforced for Progenitor: its Core image could not be read` (the running Core's sha256 not known yet); the next read, within the 2 s cache, gave the real status. A join inside that window would not be held to Progenitor's build (fixed afterwards, below).

#### Fixed after the C4 live test

Built and game-free tested after the live test (2026-10-10); **not yet verified live**.

| Finding | Fix | Test |
|---|---|---|
| The server's first `self_apply`, deferred by the empty window, was not applied when the window opened | Only an automatic offer was retried, and a server's offer is Notify. A click the window defers is now kept for the session and installed as a click when the window opens ([auto-update.md](auto-update.md), Window) | `SelfUpdateTests.A_click_the_window_deferred_installs_when_the_window_opens`, `A_deferred_click_is_dropped_when_the_offer_moved_on_and_an_automatic_deferral_keeps_no_click` |
| A join right after a server Core load was not held to the server's build | The status is pending, not "not enforced", for 20 s after a Core load; the join waits until it is known (No client is ever stuck, above) | `ProgenitorExactTests.Right_after_a_core_load_an_unread_image_is_pending_and_a_join_waits_until_the_status_is_known` |
| `[RemoteInstall] ThunderstoreBaseUrl` applied only after a Core reload | `[RemoteInstall]` was bound once into a snapshot. The snapshot is now rebuilt whenever the bound values differ (`RemoteInstallSettingsCache`; the cfg watch already reloads the entries on an edit), and the remote installer's catalog is rebuilt when the index URL or `TotalTimeoutSeconds` changes. `ConnectTimeoutSeconds` still applies only to the HTTP handler built at the first request after a Core load. `[Install] DevThunderstoreUrl` was already read on every use; on the server the index follows `ThunderstoreBaseUrl` | `RemoteInstallServiceTests.A_changed_base_url_reaches_the_catalog_without_a_reload`, `The_settings_snapshot_follows_the_cfg_values` |
| `self_status.offer.clickable` read true while a build was placed | The offer is held while placed: `clickable` and `auto` false, `held` gives the click's answer; recomputed when the placement starts or ends | `ProgenitorPlacementTests` (the placed and restored flow, mod passes) |

Clean-up: Version matching back to **Use each mod's declared range** (panel, as admin); every cfg key above back to its original value; the stand-in stopped; `progenitor-self-install.json` and `progenitor-versions/progenitor/` removed from both trees (no placement file left); origin/main (044c9dc74) Core and Dev built in a scratch detached worktree and deployed to both sides (`ping`: Core `165ba57a…` on both, clean Core reloads); the client at the title screen. This test deployed no sample; the other plugins in the reload folders belong to other sessions.

### C5 as built

**Built 2026-10-10. Source-checked, built (`dotnet build` of the solution) and game-free tested (`dotnet test` of `tests/Progenitor.Tests` and `tests/Progenitor.Testing.Tests`, both passing). Verified live in part (2026-10-10):** see [C5 live test](#c5-live-test-2026-10-10). The samples' messages, `messages_record`, the page row and For developers passed; the list marker's readback passed but neither form was drawn at the test client's list width; `.compat` was not run. Bootstrap is unchanged.

**List icon (2026-10-10, after the live test).** The text marker was replaced by a loop-arrows icon right after the version (`1.3.0 ⟲ · Client and server`), the choice from the marker mockup (option A); hovering it writes the help text into the page's column, which now uses the same text as the page row. Readback `acrossVersionsShown` is `icon` or `none` (was `full` / `short` / `none`), plus `acrossIconAfter`. Source-checked, built and game-free tested (`ModCompatDetailsTests`); **not live-verified**: the icon's drawn position, size and color, its hover and its click pass-through have not been seen in the game. Rules and the acceptance procedure: [mods-menu.md](mods-menu.md#works-across-versions-and-for-developers-c5).

| Scope item | State | Where / tests |
|---|---|---|
| `Progenitor.Testing`: fixtures, replay, unknown-field and range checks | Built as a separate game-free project, `src/Progenitor.Testing` (no game, BepInEx or IL2CPP reference). It compiles Core's codec, envelope, range and fixture sources, so encoding is byte-identical; a mod's message file is linked into its test project as source. API: `MessageFixtures` (`Load`, `SaveRelease`, `Save`), `MessageFixture`, `MessageCompat` (`Replay`, `ReplayAll`, `CheckRange`, `AssertSkipsUnknownFields`, `WithUnknownFields`, `Encode`, `Decode`), `MessageTypes.In`, `ReplayResult`, `MessageCompatException` ([modules.md](modules.md#testing-your-mod-across-versions-mod-compatibility-c5)) | `MessageCompatTests` (`tests/Progenitor.Testing.Tests`) |
| Fixture format | `<mod version>/<message>@v<n>/<sha12>.bin` plus a JSON sidecar (`format` 1) with the writer's field schema when known; shared by the bridge op and the library (`Networking/ModMessageFixtures.cs`); a damaged fixture fails the load; a released version's folder is never rewritten (`SaveRelease` refuses) | `ModMessageRecordTests`, `MessageCompatTests` |
| `messages_record` bridge op | Built: `on` (an explicit mod GUID or `*`) / `off` / `status`, bounded by time (at most 1800 s), fixtures (5000), bytes (16 MiB) and 64 MiB for the folder across sessions, written by a background thread from a bounded queue (drops counted), deduplicated by SHA-256, both directions on each side, keyed by the sender's mod version ([bridge-ops-join.md](bridge-ops-join.md#messages_record)). A Core swap ends a session | `ModMessageRecordTests` |
| Samples adopt it | `Progenitor.SampleServerLed` (messages `prefs`, `announce`; features `prefs` on the server part, `popups` on the client part; `Peers = ">=1.0.0 <2.0.0"` at a literal 1.0.0) and `Progenitor.SampleClientLed` (`time.request`, `time.answer`; feature `time`; the same range and version) moved from raw `ctx.Net` to `ctx.Messages`, sends gated on the peer's features. `Progenitor.CompatTest`'s messages moved to a game-free file, unchanged. `Progenitor.Sample` offers `camera` and gained `.compat` (with a `compat.report` message); its other packets stay raw `ctx.Net` under `Exact` on purpose. Not migrated: `Progenitor.NecroArmyExample` (raw `ctx.Net` under `Exact`, which keeps fixed-size packets safe) | Replay tests in `tests/Progenitor.Testing.Tests/Samples/` (`ServerLedReplayTests`, `ClientLedReplayTests`, `CompatTestReplayTests`), `SampleCompatTests`, `CounterpartSamplesTests` |
| Works across versions marker | Built as a loop-arrows icon after the version on the Mods list's second line (with hover text in the page's column) and as a mod page Details row ([mods-menu.md](mods-menu.md#works-across-versions-and-for-developers-c5)). The icon is not live-verified | `ModCompatDetailsTests` |
| Mod page developer details | Built: **For developers**, the page's last section, collapsed by default: declared range, network level and rule, features with levels, messages read and sent this Core generation, and the server's copy while connected | `ModCompatDetailsTests` |
| Router summary for the page | `ModMessageRouter.NoteSent` / `Describe` (what this generation handles and sent, with versions) | `ModMessageRecordTests` |

**Deviations from the design above:**

- The design's names became `MessageCompat.ReplayAll(fixtures, types)` (not `ReplayAll<TPlugin>(dir)`: a plugin type cannot load without the game, so the message types are passed or found with `MessageTypes.In`) and `MessageCompat.CheckRange(peers, currentVersion, fixtures, types, allowSelfOnly)` (not `AssertRangeCovers`). Inside the range, a field an older version sends or lists in its recorded schema that today's type lacks (removed or renumbered) fails, a field whose recorded type differs fails (recursively, also with the same wire type), and each of today's types must stay readable by that version. The range is backed only by fixtures of other versions (`OtherVersions`): only the build's own fixtures fail as "not proven" unless `allowSelfOnly` (a first release). A range with no recorded version inside it fails.
- Replay runs a mod's game-free handler logic passed as a delegate, not the registered `ctx.Messages` handler against a fake context.
- The bridge op takes `action: on | off | status` with bounds, and files are named by payload hash (`<sha12>.bin`), not by sequence number.
- The samples' checked-in fixtures were written by tests (`MessageFixtures.SaveRelease`, `source: "test"`, with field schemas), not recorded live. SampleServerLed and SampleClientLed have only their first release (1.0.0), so their range checks pass as "not proven" (`allowSelfOnly`); CompatTest's 1.0.0 and 1.1.0 back each other. A live recording should give the same bytes for the same values; that comparison is in the op's acceptance procedure.
- The marker is on the Mods menu (list and page), not on the Install tab's package rows. The design's two-version HotDemo (0.1.0 and 0.2.0) was not built: HotDemo exchanges no messages.
- The unversioned-message warning on the mod page (the design's "developer details show the same fact") was not added.

### C5 live test (2026-10-10)

On the Steam client (bridge 27341) and the isolated test server (game 9976), both running Core `62ec2fa3` built from branch `feat/compat-c5` at `6657fffac`, with test plugins hot-loaded from `BepInEx/progenitor-reload`: client `Progenitor.CompatTest` Range 1.3.0 and Default 1.4.0; server Range 1.2.0 and Default 1.4.0; both sides `Progenitor.SampleServerLed` and `Progenitor.SampleClientLed` 1.0.0 built from the branch. All readbacks by bridge (`mods_ui`, `messages_record`); captures focus-free. No code change was needed.

| Check | Result |
|---|---|
| List marker readback | **Pass for the readback, marker not drawn at this window size.** Range: `acrossVersions: true`, `range: ">=1.2.0 <2.0.0"`; Default: `acrossVersions: false`, `range: ""`. Both read `acrossVersionsShown: "none"`, `secondLineDrawn: "1.3.0 · Client and server"`: at the client's resolution the list's name cell (about 150 px) fits neither `· Works across versions` nor `· Across versions` after `1.3.0 · Client and server`, so by the rule nothing is drawn. The capture agrees (no marker, nothing cut). The full and short forms were not seen drawn live; at the default list width a mod for both sides will not show the marker. |
| Page row | Pass. `page-compat {acrossVersions: true, range: ">=1.2.0 <2.0.0", words: "Works with 1.2.0 or newer, below 2.0.0"}`; the **Works across versions** row sits after Installs on (and Server, while joined) and before Managed by, name at the margin and value in the value column like its neighbors. Its column text is in `page-fact.description`; the pointer hover itself was not exercised (no focus-free hover for Details rows). Default has no `page-compat`. |
| For developers | Pass. Collapsed (`expanded: false`) on first open; `widget_click label=page-section-dev` expands it; the fold marker turns; rows Declared range `>=1.2.0 <2.0.0 · 1.2.0 or newer, below 2.0.0`, Network `ServerMustHave · rule not declared`, Features `None offered`, Messages `None registered`; Default reads `None declared (works with 1.4.x by default)`. Opening Default and coming back keeps Range expanded; closing and reopening the workspace collapses it. Layout: no overlap or clipping; the section heading and rows match Details. |
| Server's copy (joined) | Pass. Range's row reads `1.2.0 · no features`; client log `CompatTest.Range 1.3.0 (Client) peer changed: known=True server has 1.2.0`. SampleServerLed's page reads Features `popups 1`, Messages `announce reads v1 · not sent; prefs no handler · sent v1 (min reader 1)`, Server's copy `1.0.0 · prefs 1`. |
| Samples on messages | Pass. Server-led: server `preferences from <player> receive=on delivery=Popup.`; with `AnnounceMinutes = 1`, `announcement 1 sent: chat=0 popup=1` and the client showed the `Server announcement` popup; after the client part was removed (`unloaded Server-led sample: clean`), server `<player>'s client part unloaded; using the server defaults.` and `announcement 3 sent: chat=1 popup=0` (the chat fallback). Client-led: `time.request` and `time.answer` went both ways (recorded on both sides). `.serverled announce` was not exercised: the test player is not an administrator. |
| `messages_record` | Pass. `{"action":"on"}` without `mod` is refused (bridge error `args.mod is required for on`). Sessions for `progenitor.sampleserverled` then `progenitor.sampleclientled` on both sides: `written` 2 then 8 in total per side, `dropped` 0, `queued` 0, `errors` empty; files under `BepInEx/progenitor-dev/messages/<mod>/1.0.0/<message>@v1/` with sidecars; the side that sent a message wrote its field `schema`, the receiving side none; both sides wrote the same hashes. The live `prefs` (`47d0fe42aa09`) and `time.request` (`52b0030b43d9`) payloads are byte-identical to the checked-in `source: "test"` fixtures, with equal `schema`. A copy of each side's recordings replays with `MessageCompat.Replay` (each type, with the samples' value checks), `ReplayAll` and `CheckRange(">=1.0.0 <2.0.0", "1.0.0", …, allowSelfOnly: true)` through a temporary test (not kept). No hitch summary on either side names messages or recording during the sessions. |
| `.compat` (`Progenitor.Sample`) | Not run: the Sample uses `ctx.UnsafeHarmony`, so it cannot be hot-unloaded cleanly. |

Cleanup: recording off; both `progenitor-dev` folders deleted; the CompatTest and sample DLLs and their cfg files removed from both sides (each `unloaded …: clean`); origin/main Core and Dev redeployed to both sides; client at the title screen.

### Live recipe (acceptance procedure, run in part 2026-10-10)

On the isolated test server and the test client, with the local Thunderstore stand-in ([test-rig.md](test-rig.md#local-thunderstore-dev-only)): `node tools/local-thunderstore/lts.mjs serve --data <data> --port 27450`, both sides' `[Install] DevThunderstoreUrl` and the server's `[RemoteInstall] ThunderstoreBaseUrl` pointed at it.

1. **Features.** Publish `Progenitor-HotDemo-0.1.0` and, with `lts.mjs publish --as Progenitor-HotDemo-0.2.0`, the build that adds a field and a feature. Server on 0.2.0, client on 0.1.0, both declaring `>=0.1.0 <1.0.0`. Join: expected `ok`, server log `accepted by server 0.2.0's range`, client `.compat` shows the server offers the feature at level 2, server `Peers.Of(user).Has(...)` false for the new feature. The 0.2.0 server's message reaches the 0.1.0 client, which ignores the new field.
2. **Hot reload.** Swap the client to 0.2.0 through the version store at the title screen, rejoin, then hot-reload the server's HotDemo; expect one delta and `Changed` on the client.
3. **Cautious default.** Republish without a range: 0.1.0 against 0.2.0 refuses with the Same minor sentence; the join screens offer the kept copy.
4. **Version matching Exact.** Set **Exact version for all**. Join with a client on 0.2.0 against a 0.1.0 server: refused with `This server requires exactly HotDemo 0.1.0`; the pre-join prompt on the next join swaps in the kept 0.1.0 and offers no update; leaving restores 0.2.0.
5. **Exact for Progenitor.** Publish two Progenitor releases to the stand-in and run each side on a different one: the refusal names the server's Progenitor, **Use Progenitor X for this server** swaps Core at the title screen and rejoins; leaving restores the player's Core. Repeat with a server on a development build: the server logs `Exact is not enforced for Progenitor` and the join passes.
6. **Clean up.** Remove HotDemo and every test package from both reload folders and the stand-in, restore **Version matching** and the cfg keys, confirm clean unloads in both logs and that the client's own Core is back.

## Decisions (2026-10-09)

**Every recommendation below was accepted (2026-10-09).** Each item is decided as its "Recommended" text says; the alternatives are kept for the record.

1. **Who decides when ranges disagree.** Recommended: the newer side's range about the older peer (principle 2). The alternative, both ranges must accept, is stricter and makes an older build's guess about the future binding.
2. **Range syntax.** Recommended: the small comparator syntax above. The alternative is a fixed pair (`OldestPeer`, `NewestPeer`).
3. **Default for undeclared mods.** You asked for Same minor. Confirm that this also replaces today's `Exact` default for mods that declare neither a range nor a rule once C3 lands (this loosens undeclared mods from Exact to Same minor), or keep `Exact` for those and use Same minor only when a range system is present on both sides.
4. **Optional mods under Exact or Same minor for all.** Recommended: pause a both-sides optional mod at another version for this server when it can be paused, else refuse.
5. **Per-mod rules for mods the server runs.** Recommended: yes, the existing Rule button extended to them, so an admin can relax or tighten one mod under the server-wide setting.
6. **Exact for Progenitor: what is compared.** Recommended: Core version and sha256 only; Bootstrap through the protocol check. The alternative, Bootstrap version too, means a restart for every Bootstrap-only release.
7. **Exact for Progenitor when the server's build is not on Thunderstore.** Recommended: not enforced for Progenitor, with a log line and a note on the Server tab row. The alternative is to refuse to save the setting.
8. **Progenitor in the version store.** Exact for Progenitor needs the client to run the server's Core for that session, which reverses auto-update's "Progenitor never downgrades itself to match a server" for placed-for-server copies only. Confirm.
9. **Synced keys the server does not send.** Recommended: lock to the declared default for the session (a behavior change; today the local value stays).
10. **Serialization.** Recommended: Core's own tagged binary. The alternative is JSON through `System.Text.Json`, larger on the wire and with field names in every message.
11. **Scheduling.** M7 follows M6.2, but C4 needs auto-update phase 5's self-install record and Core download. Either run C1 to C3 and C5 after M6.2 and C4 after M6.5, or bring that part of M6.5 forward.
12. **Server tab home** for **Version matching**: recommended **Mods players need**, mirrored in the Players joining panel.
