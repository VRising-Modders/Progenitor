# Mod manifest

**Servers never send mod files (removed 2026-10-05).** Mods come only from Thunderstore: a player installs them from the [join screens](join-flow.md) or the Install tab, and a server only names the packages in a signed catalog. Why: [client-mod-policy.md#security](client-mod-policy.md#security). What went and what replaced it: [Removed 2026-10-05](#removed-2026-10-05-server-to-client-mod-files). The 2026-10-05 behavior on this page (trust on first use, retired packets, the legacy cleanup, the retargeted install window and the fix row) is **source-checked and game-free tested** (`NoServerDistributionTests`, `ModManifestPhase2Tests`, `HandshakeInstallGraceTests`, `ModVersionStoreTests`, `JoinFlowTests`); **not live-verified**.

**Planned extension:** [coordinated mod deactivation](mod-deactivation.md) specifies how a server and connected clients can retire an eligible mod without intentionally disconnecting players or the Dev bridge. Catalog removal does not currently unload client DLLs. The extension requires cooperative preparation, client acknowledgments, activation policy and recovery; its APIs and acceptance tests are not implemented.

Roadmap M4.4. The phases as they stand:

| Phase | What | Status |
|---|---|---|
| 1 | Signed catalog, signature check, client log block | current |
| 2 | Handshake hook and the trust file (pinned on first use since 2026-10-05) | current; its server-to-client package transfer was removed 2026-10-05 |
| 3 | Consent panel and install from a server | removed 2026-10-05 |
| 4 | Deny-time descriptor: a refused client learns where to get what is missing | current |
| 5 | Install window: the kick is deferred while the client fixes the join itself | retargeted 2026-10-05 to Thunderstore installs, kept copies, Turn on and Pause |
| Join section | Server message, recommended mods, time zone, client mod policy | current |

The Dev `mod_manifest` op is a later job; Core exposes `ModManifest.Describe()` for it.

## Removed 2026-10-05: server-to-client mod files

Decision (security): mods come only from Thunderstore, where authors are accountable and a package can be taken down; a server must never send mod files to clients. Installing from Thunderstore in game stays (join screens and the Install tab). Status: source-checked and game-free tested. The install window, a Thunderstore fix inside it, the window lapse, TOFU pinning and key-change detection, an old trust file and the legacy cleanup were live-verified on 2026-10-05 ([live results](test-rig.md#install-window-from-thunderstore-tofu-and-legacy-cleanup)). The key-changed line on the join screens and the cleanup's ledger check were not.

**Client.** Never requests, stages, promotes or installs a server's file. Gone: the `PrmR` request and the `PrmC` chunk handler's transfer, the consent panel, the join screens' trust page (the "Download" server-copy prompt) and the "Use server's copy" row action, the download queue, Core's mirror of the start-time promote (`ModManifestPromote`), the window install, the Settings "Server mods" group (the "Kept versions" group now lives in `src/Progenitor.Core/Ui/KeptVersions.cs`), and the remembered declines (`progenitor-server-mods-declined.txt` is no longer read). Nothing a server sent is written to `BepInEx/progenitor-staging/<hostkey>/` any more (the Install tab's own downloads still use `progenitor-staging/index/`). Bootstrap's `PendingInstalls.PromoteAtStart` is **unchanged** and still runs at every start: the Install tab's restart route and remote installs use `progenitor-pending/`.

**Server.** Never offers or sends. Gone: the offer table and transfer code, the `[Networking]` transfer limits (`ModTransferMaxBytes`, `ModTransferBytesPerSecond`, `ModTransferIdleTimeoutSeconds`, `ModTransferRequestCooldownSeconds`; an old cfg keeps the lines but nothing reads them), the admins' shared list ([join-flow.md](join-flow.md#players-joining-admins-stage-3)), the offered-mod kick notes and operator warnings, and the transfer gate an install window used to widen.

**API.** `ctx.Handshake.OfferPackage(path)`, `ctx.Handshake.TryGetOfferedPackageState(...)`, `ModManifest.Offer` and `ModManifest.TryGetOfferTransferState` are `[Obsolete]` no-ops, kept so a plugin built against an older Core still loads ([modules.md](modules.md)). `Offer` returns a handle whose `Dispose` does nothing and logs one warning per mod id per Core generation:

```
ModManifest: OfferPackage(<id>) does nothing: servers no longer distribute mod files; publish the mod on Thunderstore and name its package in progenitor-manifest.json so players can install it from the join screens
```

The state query returns false with reason `"removed"`, an empty hash and size 0. To make a mod installable for joining players, publish it on Thunderstore and name its package in the catalog (`package` in [`progenitor-manifest.json`](#progenitor-manifestjson-optional-operator-overlay)).

**Retired packet ids.** Core still registers handlers for both ids, because a client dispatcher with no handler for an id hands the packet to the game's own reader. Each handler drops the packet unread and logs one line per kind per Core generation (`ModManifest.RetiredPacketLog`):

| Id | Fourcc | Was | Handler now |
|---|---|---|---|
| `0x50726D52` | `PrmR` | client → server package request | server: dropped |
| `0x50726D43` | `PrmC` | server → client package chunk | client: dropped |

```
ModManifest: dropped a PrmC packet: servers no longer send mod files (mods come only from Thunderstore); further ones are dropped silently (<n> bytes)
```

**Trust.** With the consent panel gone, the first verified catalog from an address pins its key ([trust on first use](#trust-on-first-use-2026-10-05)). `riskAck` in `progenitor-trusted-operators.json` (the retired trust page's acknowledgement) still parses and is kept when the file is rewritten; nothing sets it any more.

**Legacy cleanup.** What an older Core installed from a server is removed once per Core generation at client start ([below](#legacy-cleanup-at-core-start)).

The full pre-removal text of this page (transfer wire, staging, consent panel, install and promote) is in git history: `docs/mod-manifest.md` at `7a055b42`.

## Why

The handshake kicks on a missing `ServerMustHave` plugin (16 slots, guid + version). A join that fails that way does not tell the player *where* to get the DLL, whether it can hot-load, or whether the list is the operator's. This module publishes a signed JSON catalog after a handshake ok (and a narrowed one on a refusal, phase 4) so the join screens can name the Thunderstore package for each row.

## Developer pitfall: the exec host is not a client package

The reserved exact ids `progenitor` and `progenitor.scratch` are excluded from the loaded-mod and override catalog rows, case-insensitively. Scratch is the temporary host compiled for one `progenitor_exec` snippet; its changing assembly is not a client dependency. During the 2026-09-06 form test, the server's transient Scratch catalog row caused an installation panel to cover the client's gameplay. The filter is deliberately exact: `progenitor.scratch.extra`, `progenitor.scratchpad` and other `NoNeedForSync` mods remain eligible.

## Algorithm

BepInEx 6 is net6.0. The runtime ships `SHA256` and `ECDsa` (nistP256). It does **not** ship Ed25519 (BCL in net8) or NSec. NSec would be a second runtime dependency (spec question first). Signatures are therefore **ECDSA P-256 + SHA-256** over canonical JSON.

- Canonical JSON: UTF-8, object keys sorted by ordinal, no insignificant whitespace, mods sorted by id. The signed bytes are `Encoding.UTF8.GetBytes(canonicalBody)` — the envelope's `signature` field is not part of the signed payload.
- Signature: IEEE P1363 raw `r||s` (64 bytes) as lowercase hex (128 chars).
- Public key: SPKI DER as base64.
- Fingerprint: first 16 hex chars of `SHA256(SPKI)`.

## Config files

All four live in `BepInEx/config/` (never the reload folder: a private key copied to clients would be a stolen operator). Generated on first server run.

### `progenitor-operator.key` (private, server)

```
# progenitor-operator.key — ECDSA P-256 PKCS#8 (base64). Never share this file.
# Algorithm: ECDSA P-256 + SHA-256. Fingerprint: a1b2c3d4e5f67890
<base64 PKCS#8>
```

### `progenitor-operator.pub` (public)

```
# progenitor-operator.pub — ECDSA P-256 SPKI (base64). fp=a1b2c3d4e5f67890
<base64 SPKI>
```

The fingerprint is logged once per Core generation:

```
ModManifest: generated ECDSA P-256 operator key fp=a1b2c3d4e5f67890 (…/progenitor-operator.pub)
ModManifest: ECDSA P-256 operator key fp=a1b2c3d4e5f67890
```

### `progenitor-manifest.json` (optional operator overlay)

Comments and trailing commas are allowed. `mods` may be an array or a map keyed by plugin id. Extra ids that are not loaded still enter the catalog (a client-only package the dedicated process does not load).

```json
{
  "serverName": "Night Garden",
  "operatorName": "Ada",
  "profileCode": null,
  "mods": [
    {
      "id": "progenitor.sample",
      "version": "0.1.0",
      "namespace": "ExampleTeam",
      "package": "ExampleTeam-Progenitor_Sample",
      "sha256": "<64 lowercase hex of the Thunderstore zip>",
      "size": 123456,
      "url": null,
      "hotLoadable": true,
      "level": "ServerMustHave",
      "versionRule": "Exact"
    }
  ]
}
```

Omitted fields leave the auto-built row alone. `package` is Thunderstore `{ns}-{name}` (not the versioned dependency string); it is the only source of a row's package (a loaded plugin's auto-built row has none), and it is what the join screens install and what the [install window](#phase-5--the-deferred-kick-and-the-install-window-job-fman18-server-half) looks up. `url` is an optional HTTPS link the refusal report shows; nothing downloads from it. `sha256` / `size` are optional: the refusal report prints them, and the client's compare checks a loaded copy against `sha256` ([trust rules](#trust-rules-eq3)). Nothing is downloaded from the server.

### `progenitor-trusted-operators.json` (client)

```json
{
  "operators": [
    {
      "host": "127.0.0.1:9976",
      "spki": "<base64 SPKI>",
      "fp": "a1b2c3d4e5f67890",
      "always": true,
      "seen": "2026-09-03T18:00:00Z"
    }
  ]
}
```

BCL strings and bools only (a Core-typed entry would pin a collectible generation). A row may also carry `issued` (the last accepted catalog stamp) and, in a file written before 2026-10-05, `riskAck` (parsed and kept, never set). The file's top-level `thunderstoreAcks` holds the join screens' Thunderstore confirmations ([join-flow.md](join-flow.md#5-install-from-thunderstore-stage-2)).

First contact: an address with no row is [pinned on first use](#trust-on-first-use-2026-10-05) by its first verified catalog. When no connect address could be captured, nothing is pinned and the client logs `unverified operator` and still classifies mods. A bad signature is not first contact; that is a reject (the unsigned body is **not** stored as `LastEnvelope` / `Pending`, and Classify does not walk mods). A fingerprint change for a **known host** logs `operator key changed for host … (not accepted)` and is never auto-accepted or re-pinned, even if the previous row had `always: true`.

### Trust rules (EQ3)

- **Embedded key.** `Fingerprint(SPKI) == body.Operator.Fp` is required. Fail closed with `Manifest: fingerprint does not match the embedded key; rejected`. Verify still uses the embedded SPKI; the claimed fp is not a substitute for the key.
- **Pin lookup.** A trust row matches only on the **computed** fingerprint **and** the exact SPKI. A row with no `spki` is refused (`ModManifest: trust row for fp=… has no spki; refused`) and is never matched on fp alone.
- **Pins are per host + SPKI.** Every pin writes both. Host comes from the connect-address seam (`ModManifest.SetConnectHost` / `CaptureConnectHostFromClient`; Handshake `OnResult` after an accepted join). Since HOSTKEY1 (2026-10-05) it is the server's `address:port` (`ConnectHostKey.Choose`): `ClientBootstrapSystem.LastConnectionData.ConnectAddress.Address`+`Port`, else Bootstrap's `LaunchGame` record of the dialed address (`ProcessHost.LastConnectAddress`/`LastConnectPort`; second because it is never cleared, so a join that skipped `LaunchGame` would read the previous server), and only when neither is known `ClientBootstrapSystem.ConnectionString`. An address that looks like a relay string is skipped. That string is a Steam relay string for a SteamIPv4 join and changes on every connection (live 2026-10-05: `Aqf27THwTQgI4Hk6FpSBAvJNEAA`, `Aqf27THwTQgIuB3hF5SBAvJNEAA`, … for one server at 127.0.0.1:9976), so a key built from it re-pinned the server at every join. See [Rows written under relay strings](#rows-written-under-relay-strings-hostkey1). Tests inject `TestConnectHost` or call `SetConnectHost`. The captured host is logged once per join: `ModManifest: connect host 127.0.0.1:9976`.
- **Replay.** `issued` is parsed (`yyyy-MM-ddTHH:mm:ssZ`). Once a pin exists, an envelope whose `issued` is older than the last seen stamp for that host/key is rejected: `ModManifest: replayed envelope issued {issued} is older than last seen {last}; rejected`. Equal `issued` (the same catalog resent) is allowed. Last-seen `issued` is stored on the pin and advanced when a newer trusted envelope is accepted.
- **Catalog `sha256`.** Classify marks a loaded id `present` only when the catalog's 64-hex `sha256` matches the hash of the loaded assembly bytes (reload-folder file, or the `LoadedModInfo.Sha256` tests inject). No sha in the catalog (or no local hash) → `present (hash not checked)`. A different hash → `mismatch (hash)`. `NormalizeSha` rejects anything that is not 64 hex.

Revoke = delete the row: the next verified catalog from that address is first use again and pins whatever key it carries. `ModManifest.TrustOperator(fingerprint, always, TrustOrigin.LocalUser)` remains the explicit pin (the Sample's client `.mods trust`): `always: true` writes atomically (temp + move); `always: false` is **session-only**, an in-memory pin for this process, never written. `PersistAcceptedIssued` advances the **on-disk** file only (Always rows); a newer catalog does not persist a session pin. `CanonicalTrust` / `TryParseTrust` drop `always:false` rows. `IsTrusted` honors `Always`: a session row is trusted for this process and logged `(trusted, session)`. **EQ8: `IsTrusted` is host-aware** (R28 finding 25): a row pinned for a specific host matches **only** that host; a **host-less** row (written only by an explicit `TrustOperator` while no host was captured) matches everywhere. The host-less overload of `IsTrusted` matches host-less rows only, and `Classify` passes the connect host. The file is read once per join. Client `Incoming` / `_envelope` / `_lastDiff` / `Pending` are cleared on client `WorldReady` (and on `Uninstall`) so a later server cannot freeze the previous catalog. `Plugin.Uninstall` calls `ModManifest.Uninstall`.

### Rows written under relay strings (HOSTKEY1)

Before HOSTKEY1 the host of a SteamIPv4 join was the per-connection relay string, so `progenitor-trusted-operators.json` holds one pin (and Thunderstore confirmation) per past connection, and `progenitor-server-profiles.json` one profile per connection. They move to the dialed address once, without any player action:

- **Trust pins and Thunderstore confirmations** (`ModManifestTrust.AdoptRelayRows`, called from `ModManifest.AdoptRelayTrust` after a catalog's fingerprint, signature and replay checks, so a replayed catalog moves nothing). The rows whose host is a relay string (`ConnectHostKey.IsRelayString`: base64 characters only, no `.` or `:`, 20 or more characters, not all hex, so an EOS product user id is not one) and whose SPKI is the catalog's become one pin for the address when it has none yet: `always` if any was, the earliest `seen` and the newest `riskAck`. No `issued` is carried, because one key may sign several servers' catalogs and another server's newer stamp would reject this one's as a replay; the accepted catalog's `issued` is stored, as for a first use. Their confirmations merge into the address's confirmation row. The relay rows are dropped either way. Logged `ModManifest: moved operator fp=… trust rows from per-connection relay keys to host=…`.
- **Server profiles** (`ServerProfiles.AdoptRelayProfiles`, when a verified catalog arrives): the relay-string profiles recording the catalog's operator fingerprint merge into the address's profile (created when missing, for example by the first join's pause before the catalog) and are dropped: auto-pause stays off if any of them opted out, and the server name, last paused list and remembered client mod policy fill only what the address's profile lacks, newest first. Relay-string profiles without a fingerprint can never be matched and are dropped at the same time. Logged `ModManifest: moved operator fp=… server profiles from per-connection relay keys to host=…`.

The rows move only to a stable key (an address, not a relay string or `(unknown)`), only for the key that signed the catalog, and an unreadable file is left alone. Moving rows across addresses on key alone is deliberate: a relay-string row named no address at all, and only the holder of that private key can sign the catalog that claims them. The automatic pause before the first join report after the update has no profile under the address yet (it runs before any catalog), so that one join goes to the install window if a mod needs pausing; from the next join the remembered policy is found. Game-free tested (`ConnectHostKeyTests`); not live-verified.

### Trust on first use (2026-10-05)

With the consent panel gone, `ModManifest.PinOnFirstUse` (called from `AcceptEnvelope`, after the fingerprint, signature and replay gates pass) pins the key of the first verified catalog from an address that has **no row at all** in the trust file or in this join's in-memory copy: an `always: true` row with the catalog's `issued`, so the replay gate and key-change detection work from the next catalog on. Rules:

- It never replaces a row: a later catalog signed by another key for that address is flagged as a key change (the log block above, and on the join screens' "Install from Thunderstore?" confirmation the plain line `This server's key changed since <date>; it was <old fingerprint>.`) and is not re-pinned. That catalog still drives the join screens, and any Thunderstore install from it asks again (the confirmation is per key).
- **Re-trusting after a legitimate key rotation** (the admin regenerated or moved the server's key): at the title screen, delete that address's row from `BepInEx/config/progenitor-trusted-operators.json` (or the whole file); the next verified catalog pins the new key on first use. With the catalog pending in a join, an explicit `ModManifest.TrustOperator(fingerprint, always: true, TrustOrigin.LocalUser)` (the Sample's client `.mods trust always`) pins it too. Ask the admin out of band first: a key change you did not expect is what a server-impersonation attempt looks like.
- No captured connect address, no pin.
- A pin moved from relay-string rows ([below](#rows-written-under-relay-strings-hostkey1)) counts as the address's row: it is not a first use.
- A trust file that exists but cannot be read is never rewritten (`ModManifest: operator fp=… not pinned on first use: progenitor-trusted-operators.json could not be read (…)`).
- It is the one pin written from the catalog's receive path, and it can only create the first row for an address. Every other trust write (`TrustOperator`, the Thunderstore confirmation) is still refused from a receive handler.

```
ModManifest: pinned operator fp=a1b2c3d4e5f67890 host=127.0.0.1:9976 on first use (trust on first use; a later key change for this address is flagged)
```

A pin decides only which key this address is expected to sign with. It installs nothing: every Thunderstore install still needs the player's per-server confirmation ([join-flow.md](join-flow.md#5-install-from-thunderstore-stage-2)).

### Client-origin trust (EQ4)

An explicit trust decision originates on the client: the server can only publish its catalog.

- `TrustOperator` takes `TrustOrigin.LocalUser` only. There is no network origin. A call while a `CustomEvents` receive handler is on the stack is refused (`ModManifest: TrustOperator refused: called from a receive handler`) and does not write the trust file.
- Sample `.mods` (server chat) still forwards **report** over `SmMd`. `.mods trust [always]` on the server replies `Run .mods trust on your client. The server cannot pin an operator for you.` A local client `.mods trust` calls `TrustOperator(..., TrustOrigin.LocalUser)` and logs `Sample: .mods trust fp=… always=… ok=…`.

## Catalog body

`id` is the BepInEx GUID. `level` / `versionRule` are `NetworkCompatibilityLevel` / `NetworkVersionRule`. `hotLoadable` is true only for a `ProgenitorPlugin<>` loaded from `BepInEx/progenitor-reload/`; a raw BepInEx plugin is false. Operator `hotLoadable` overrides that.

Bootstrap/Core (`progenitor`) is never listed. Handshake-reserved guids are skipped. Handshake `Require`/`Provide` rows that are not a live plugin still appear (that is the client-must-fetch case).

### Server-only plugins

A loaded plugin whose type declares `[ProgenitorSide(Side.Server)]` stays in the catalog with `"serverOnly": true`. The client lists it as a server mod and never as something to install: a client copy would load dormant. The flag is read from the attribute by name, including inherited attributes. Missing, `Client` and `Both` declarations stay installable.

- **Client comparison.** `ModManifestCompare.Classify` puts a flagged row in `ManifestDiff.ServerOnly`, never in `Missing`, `Mismatches`, `Present` or `RestartRequired`. The client log block adds `server-only (N): <id> <version>` when N > 0.
- **Enforcement is unchanged.** The flag does not relax enforcement. The handshake enforces required mods from its own `Require`/`DeclareSelf` rows and never reads the catalog, so a server can still kick a client that lacks a required mod.
- **Wire compatibility.** The key is written only when true, so a catalog with no server-only plugin is byte-identical to earlier Cores. A client Core older than this change re-canonicalizes without the key, so it rejects a catalog that carries one as a bad signature (`Manifest signature does not match key …`).

Source-checked and game-free tested (`ServerOnlyCatalogTests`: the signed flag, the client comparison, the log line, a catalog with the row accepted; the consent-panel tests went with the panel on 2026-10-05). Not verified live.

### Two signed forms: current and extended (mod compatibility C3)

An older client rebuilds the canonical body from the keys it knows before it checks the signature, so any new row key breaks its check (the `serverOnly` hazard above). The server therefore signs **two forms** of every catalog with the same operator key, the same `issued` and the same join section:

| Form | Rows | Sent to |
|---|---|---|
| Current | Exactly the keys above; byte-identical to the pre-C3 catalog | Clients whose handshake answer did not declare `Capability.VersionRanges` (older Cores) |
| Extended | Each row may also carry `peers` and `match`; the body also carries `matching` | Clients that declared `Capability.VersionRanges` |
| Progenitor (mod compatibility C4) | The extended form; the body also carries `progenitor` and `progenitorSha` (between `profileCode` and `schema`): the server's own Progenitor release and Core sha256, only while **Exact version for all** is enforced for Progenitor. Without them it is the extended envelope itself | Clients that declared `Capability.VersionRanges` and `Capability.ProgenitorExact` |

- `peers`: the server build's effective range for that mod ([mod-compatibility.md](mod-compatibility.md#2-declared-compatibility-range)): its declared `Peers`, else its explicit rule, else the same minor version. Omitted when the version cannot be carried.
- `match`: present only when the server does not judge that mod by ranges: the admins' per-mod rule (`exact`, `minor`, `at-least`, `any`), else `exact` or `minor` under **Exact version for all** or **Same minor for all**.
- `matching` (body, between `issued` and `mods`): the server's **Version matching** when it signed (`range`, `minor` or `exact`), always present in the extended form. A C3 client reads it into `ManifestBody.Matching` and uses it in place of the mode its join's request bits announced, while connected and in the requirements it remembers for the next pre-join prompt; an unknown word is ignored.
- Both are written only when set and take their sorted places in the row (`level`, `match`, `namespace`, `package`, `peers`, `serverOnly`, …). A C3 client reads them into `ManifestMod.Peers` / `Match` and rebuilds the canonical body with them, so the extended form verifies, and a current-form catalog from an older server still verifies.
- **Choosing the form.** Every place a catalog goes to a client picks by the answer's capability (`ModManifest.CatalogFor(userIndex)`, from `Handshake.CapsOf`): the post-join catalog, the resend after a re-sign, and the deny and install-window descriptors (narrowed from that user's form).
- **Re-signing.** Both forms are rebuilt at every re-sign. `matching`, `peers` and `match` are part of the catalog's change stamp, so a changed range, **Version matching** (also with no catalog rows) or per-mod rule re-signs; an admin's edit and a change of `[ClientPolicy] VersionMatching` re-sign at once (`ModManifest.RebuildCatalog`), and the next frame's scan resends to every handshake-ok client. A per-mod rule for a mod without a catalog row changes nothing in the catalog; it reaches clients through the rewritten handshake request rows at their next join.
- **The join screens' mods-changed check** hashes the required versions, recommended rows and client mod policy only, not `peers` or `match`, so a range or matching change alone does not show the mods page again. The Progenitor row (C4) is not part of it either: a server that starts holding players to its Progenitor build mid-session re-signs and resends the catalog, and open screens redraw with the row, but the page is not shown again by itself; the next join is judged by it.
- **C4.** `progenitor` and `progenitorSha` are part of the change stamp too, and a refusal over Progenitor keeps them in its narrowed descriptor even with no failing mod row ([mod-compatibility.md, C4 as built](mod-compatibility.md#c4-as-built)).

Source-checked and game-free tested (`CatalogDualSignTests`, `CatalogMatchingClientTests`; C4: `ProgenitorExactTests`). Not verified live.

Envelope on disk/wire:

```json
{ "body": { … }, "signature": "<128 hex r||s>" }
```

## Wire

Custom event id `PrmM` (`0x50726D4D`). Chunked UTF-8 envelope, 15 KiB max per packet (same budget as config sync). Layout: `[version u8][flags u8][seq u16le][count u16le][nbytes u16le][bytes]`. Flag bit 0 = more chunks. Reassembly cap 256 KiB; a `seq == 0` after a complete envelope starts a new one, and a duplicate chunk never re-emits. Receive slots stay with Bootstrap; Core only registers the handler. The two retired ids are in [the removal note](#removed-2026-10-05-server-to-client-mod-files).

The server sends once after an accepted handshake answer: `Handshake.OnResponse` composes `ConfigSync.OnHandshakeOk` then `ModManifest.OnHandshakeOk(userIndex)` and hands the pair to `Handshake.RunPostOkEffects`, so the wire order is handshake result → config snapshot → catalog. A **re-answer from a user that is already `ok`** inside the 5 s `HandshakeReAnswer.MinSecondsBetween` floor sends the `Result` packet and **nothing else** — snapshot, catalog and edit-layer resend are skipped together (job FMAN22); the catalog half is picked back up within a second by `ModManifest.OnFrame`'s stamp scan. Plugin load/unload rebuilds the catalog (1 s scan) and resends to handshake-ok users. Each user gets the [form](#two-signed-forms-current-and-extended-mod-compatibility-c3) its capability reads.

## Client log

One block, greppable as `ModManifest:`:

```
ModManifest: operator Ada fp=a1b2c3d4e5f67890 (trusted)
ModManifest: server 'Night Garden' issued 2026-09-03T18:00:00Z
  present (1): progenitor.sample 0.1.0
  missing (1): other.mod 2.0.0 (Thunderstore ExampleTeam-Other)
  mismatch (1): foo.mod have 1.0.0 want 1.1.0 (Exact)
  restart-required (1): other.mod
```

Other operator lines:

```
ModManifest: operator Ada fp=a1b2c3d4e5f67890 (trusted, session)
ModManifest: unverified operator Ada fp=a1b2c3d4e5f67890 (first contact; not in progenitor-trusted-operators.json)
```

The `unverified operator` line now appears only when the catalog could not be pinned (no connect address, or an unreadable trust file); a first contact with an address is pinned and then logged `(trusted)`.

Bad signature:

```
ModManifest: Manifest signature does not match key a1b2c3d4e5f67890
```

Fingerprint change for a known host:

```
ModManifest: operator key changed for host 127.0.0.1:9976: was a1b2c3d4e5f67890 now c3d4e5f6a1b2c3d4 (not accepted)
```

`ClientOnly` missing rows are listed with `(ClientOnly)` and never treated as join-blocking (the handshake already decided the join). `restart-required` is every missing or mismatched row with `hotLoadable: false`. When the catalog has [server-only rows](#server-only-plugins), the block ends with a `server-only (N): <id> <version>` line. Those rows are in none of the other lists.

## `Describe()`

`Progenitor.Networking.ModManifest.Describe()` on both sides. Server: current envelope (ids, versions, level/rule, hotLoadable, fingerprint). Client: last received envelope plus the compare block. A later Dev op prints this string; do not add that op here.

## Phase 2 — the trust API

The package-transfer half of this phase was removed 2026-10-05 (see [above](#removed-2026-10-05-server-to-client-mod-files)).

- `ModManifest.Pending` — last received catalog this join: operator fp, host, first-contact flag, fingerprint-changed flag, missing/mismatched lists. Null on the server and before the first valid envelope.
- `ModManifest.TrustOperator(fingerprint, always, TrustOrigin.LocalUser)` — the explicit pin. Refuses a fingerprint that does not match `Pending`. Refuses a receive-handler call. A host key change never auto-accepts.
- EQ6: the two files are kept apart. The **disk** write is built from the file on disk (a session pin can never leak onto it) and the same row is upserted into the **in-memory** file. A session request for a host+key that is already pinned `always: true` is a no-op that keeps the persisted row (`ModManifest: operator fp=… is already trusted always=true host=…; session pin not written`).
- Sample `.mods` (server chat) forwards **report** to the calling client over custom event `SmMd` (`0x536D4D64`). `.mods trust` is client-origin only (see above).

Trust log lines:

```
ModManifest: pinned operator fp=a1b2c3d4e5f67890 host=127.0.0.1:9976 on first use (trust on first use; a later key change for this address is flagged)
ModManifest: trusted operator fp=a1b2c3d4e5f67890 always=false (session) host=127.0.0.1:9976
ModManifest: operator Ada fp=a1b2c3d4e5f67890 (trusted, session)
Sample: .mods trust fp=a1b2c3d4e5f67890 always=False ok=True
ModManifest: connect host 127.0.0.1:9976
```

### Legacy cleanup at Core start

An older Core could install a server's DLL into `progenitor-reload/`, where it keeps loading on every start until something deletes it. A read-only Remove list would leave untrusted code running for every player who never opens that page, so the client cleans up once per Core generation instead (`LegacyServerMods.CleanUpAtStart`, called from `ModManifest.Install` before the join screens install the version store):

1. Read `BepInEx/config/progenitor-server-mods.txt` (the record only the server-copy install ever wrote). No record, or no readable row: nothing happens and nothing is logged.
2. For each row, delete the package in `progenitor-reload/` and in `progenitor-pending/<host>/` **only when its bytes still hash to the row's sha256 and the install ledger (`BepInEx/progenitor-install.json`) does not record that path with the same sha256**. Other bytes under the same name (the player's own build) are left and the line says so. A Thunderstore install the ledger tracks is left too, even with identical bytes (`left … (the install ledger records it with the same bytes: a tracked install, not the server's copy)`), and so is its allowlist row.
3. Delete the `.progenitor-session` markers beside both paths.
4. For an `always` row whose file was not a tracked install, remove its line from `progenitor-allow.txt` when that file exists.
5. Drop the row. A row whose file could not be deleted is kept for the next start. A row **without a sha256** deletes nothing and is kept, with a warning (`… the record has no sha256, so nothing was deleted; row kept. Remove the file … yourself if a server sent it.`). The record file is deleted once it is empty.

Then `ModVersionService.ForgetServerCopies` deletes the version store's kept copies whose source is Server (not the player's own), before `RepairMissingActive` puts a kept copy back where a server's copy was active. When a version swap is still being finished (a journal left by a crash, or a swap waiting for its plugin to unload) the pass waits and runs before the next join's swap plan (`ForgetServerCopiesIfDue`). Independently, every pick skips such a copy (`ParkedVersion.ServerSent`: the join's swap plan, the join screens' Use kept, Restore, the leave restore and the start repair), so server-sent bytes are never swapped back in even before they are deleted; a direct swap answers `The kept copy of <id> <version> came from a server; servers no longer send mod files, so it is not used.` The ledger and the player's own copies are never written here.

```
ModManifest: legacy server mods: <id> <version> (<file>, sha256 <prefix>, sent by <host>): deleted …/progenitor-reload/<file>; its allowlist row was removed
ModManifest: legacy server mods: <id> <version> (…): could not delete <path> (<why>); kept for the next start
ModManifest: legacy server mods: servers no longer send mod files (mods come only from Thunderstore); removed N file(s) an older Progenitor installed from a server, M record row(s) dropped
JoinFlow: versions: <one line per kept server copy deleted or left>
```

Bootstrap's start-time promote runs before Core, so a pending `always` install an older Core accepted is first moved into `progenitor-reload/` and then deleted there by this cleanup. Whether the reloader loads it in between was not checked.

## Phase 3 — consent and install (removed 2026-10-05)

The consent panel, the once/always/decline choices, the install into `progenitor-pending/<host>/`, the window install into `progenitor-reload/`, the "Server mods" Settings group, the decline memory and the consent demo package (`samples/Progenitor.Sample.ConsentDemo`, `.consentdemo`) are gone. What they wrote is removed by the [legacy cleanup](#legacy-cleanup-at-core-start). The allowlist writer `ReloadAllowStore.TryWriteAllowRow` remains (the cleanup uses it to remove rows) and still never creates `progenitor-allow.txt`; `ReloadAllowStore.TryCreateAllowList` still refuses content that omits Core's own current image. Bootstrap's `PendingInstalls` (session-marker sweep, promote, allowlist gate) is unchanged. History: git, `docs/mod-manifest.md` at `7a055b42`.

## Phase 4 — the deny-time descriptor (job FMAN17)

Phases 1–2 begin *after* the handshake passes. A player the handshake **refuses** got a
479-character sentence and a disconnect: it knew what was missing and had no way to learn where to
get it. A "click for the homepage" link that resolves only for mods you already have fails the same
way: the one row that needs a link cannot get one.

Phase 4 is **manifest, not payload**, the model every stack job FMAN16 surveyed converges on
(research `mod-manager.md`, "Deny-time offers" and "FMAN17"). Not one byte of a package moves on the
deny path.

### The send

`Handshake.OnResponse`'s failure branch, after `SendResultAndKick`, calls
`ModManifest.OnHandshakeDenied(user.Index, Compare.DenyDescriptorIds(failedIds, server, answered))`
— one line, synchronous, in the same frame the `Result` custom event went out. Reliable events keep
order, so the wire order is handshake `Result` → deny descriptor. Both are handed to the transport
synchronously, before `SendResultAndKick`'s `PostDelayed` schedules `PlayerControl.Disconnect` at all;
the disconnect is where the game's own network layer starts refusing sends (`ServerNetworkLayer.SendMessage`
sends only at `ConnectState == 5`; `Disconnect` writes `6` first). The `HandshakeAnswerWatch.KickFlushSeconds`
(2 s) hop is **not** what gets the descriptor out — the next paragraph is the measurement.

**The 2 s window is not what carries it, and evidence 185 measured that.** With
`KickFlushSeconds = 0.0` in a scratch server build, hot-swapped into a live server on Lidgren --
the transport that resets its send channels at `Disconnect` -- the client still logged
`(signed catalogue received)`. The send is synchronous and already handed to the transport before
`PostDelayed` schedules the disconnect at all, so what protects it is **reliable-channel and frame
ordering, not wall-clock delay**. That makes this more robust than a timing constant, but do not
read it as "no gap is needed": `PostDelayed(0.0)` still yields to the next main-thread pump, and a
disconnect issued synchronously in the same statement was never tested. The smallest safe gap is
unmeasured, and only Lidgren was exercised.

Same custom event as the catalog (`PrmM`), same chunker, same client reassembly, same signature
and replay gates. No new wire.

### What is in it

The signed catalog **narrowed to the rows this client actually failed**:
`Compare.DenyDescriptorIds` is `ServerEnforcedFailures` reduced to ids — the rows *this server's
own `Check`* rejected this client's copy over. `Evaluate`'s reverse walk (rows the **client**
declared `ServerMustHave` that this server lacks) is dropped by construction: this server has no
catalog row for a mod it does not run, and telling a player to fetch something from a machine that
does not have it would be a claim the code has not established.

`ModManifestDenyPolicy.Narrow` rebuilds the body with those rows and re-signs it. Operator identity,
`serverName`, `profileCode` and — deliberately — the `issued` stamp are carried through unchanged:
equal `issued` is what the client's replay gate allows, so a deny descriptor can never poison the
full catalog of the same generation on a later successful join.

### What it does NOT grant

`ModManifest.OnHandshakeDenied` **never adds the user to `HandshakeOkUsers`**, the set that marks a
peer as joined for the module (catalog resends). There is no second authorization set and no change
to the kick's timing. (Until 2026-10-05 that set also gated the package transfer; nothing is
transferred any more.)

The send also does not touch `SentUsers`: a deny descriptor is not the join catalog and must not
make the module think this user has been served one.

Both facts are pinned by tests, the second by a *negative* source pin over `OnHandshakeDenied`'s
body.

### The operator switch

`Networking.DenyTimeModDescriptor`, BCL `bool`, **default `true`**.

What it discloses is narrower than what the server already sent this peer unprompted:
`Handshake.OnRequest` hands every connecting client the whole `Required` list — id, version, level,
rule — before the client has proven anything, and `Compare.MismatchKickReason` then puts the failing
ids and expected versions inside the kick string. Narrowed to the failing rows, the descriptor adds
exactly four fields the peer did not already have — `package`, `url`, `sha256`, `size` — and for
anything published on Thunderstore or another mod site those are public by construction.

Turn it off if the *existence* of a required package is itself sensitive (a private build): that is
the one case where `package`/`url` is not already public and where a `sha256` lets someone confirm a
build they hold. When it is off, one line per Core generation says so:

```
ModManifest: deny descriptor is off (Networking.DenyTimeModDescriptor = false); a refused client is told what is missing but not where to get it.
```

### The panel

**JOINFLOW1:** the [join screens](join-flow.md) now take the refusal report in this panel's place and keep it as their fallback; the rules below still describe the report they read.

`ModManifestDenyPanel` (`src/Progenitor.Core/Networking/ModManifestDenyPanel.cs`) is for a join that
did not succeed; `ModManifest.AcceptEnvelope` routes on `ModManifestDeny.JoinWasDenied`. (The consent
panel that took a succeeded join's catalog was removed 2026-10-05.)

**A deny panel belongs to the join attempt that produced it.** It is disposed — not hidden — when
the client leaves to the main menu (`Handshake.NotifyClientReturnedToMenu`: Idle after the
disconnect screen, or `Client_0` ended), when a new join starts (`OnJoinAttempt`), when a new
handshake result arrives (an accepted or deferred result ends the refusal; a second refusal
replaces, never stacks), and on every Core unload. The Mods-menu Escape contract ("hidden handles
linger until the next `Take()`") does not apply: a panel logged as closed must not keep rendering.
`Close()` always `Dispose`s the handle. Escape still hides through `Ui.OnBootstrapKey` without
telling the owner; the deny panel's frame pump `DismissHidden` disposes that leftover the same
frame. `PlacePanel(None)` deactivates the overlay so a `placed rank=-1/0` line cannot leave a
centered graphic over the next world.

**It draws on the disconnect screen, not in the pre-kick window.** After a handshake kick the client
sits on `ProjectM.UI.ConnectingView` (`ClientState = 7` Error, `_ForceShowBackButton`) with
`Client_0` still alive. That is a stable state with no deadline in it; two seconds is not a place to
ask a person to read anything. The game may then return to the main menu on its own (Idle, or
`Client_0` destroyed). That return dismisses the panel. A kick itself (`Error`) must not.

**It opens once per handshake result.** The local diagnosis is on screen in the frame of the
refusal, from `Handshake.CopyServerAsked()` plus this client's `CopyProvided()`, through
`Compare.Evaluate` / `ServerEnforcedFailures` / `EnforcedKickReason`. When the signed descriptor
lands, the same overlay is rebuilt in place (no close/open pair, no second canvas). The catalog
may add `package` / `url` / `sha256` / `size` to a row and may **not** add a row: a catalog row
for an id this client did not fail is not a refusal reason, and listing it would be the server
deciding what the panel says.

Bounded at `ModManifestDenyPolicy.MaxPanelsPerJoin` (3), because the catalog arrives on the server's
schedule.

**Font.** Every label uses the same TMP font asset the rest of Progenitor's UI uses (`UiFontLookup`,
the game serif). The lookup holds a live font asset across a world-epoch change: dropping it on
connect is what left this panel on TMP's default sans while ConnectingView had no donor yet. A
still-dead asset is swept again. Show and settle retry the stamp, so a first open that misses
picks the game font one tick later.

**It is read-only, and it says why.** There is no accept button: no server sends files, and
this one has refused the join. (The join screens, which normally take this report, offer Thunderstore
installs instead.) Every string is a constant on
`ModDenyText` (game-free, unit-tested):

> **'Night Garden' refused this connection: 1 mod does not match.**
> The server's reason: `Mod mismatch: other.mod: the server needs it (2.0.0), but your game doesn't have it. Install it from the join screens`
> Sources below come from a catalog signed by operator 'Ada', key `a1b2c3d4e5f67890`, host `127.0.0.1:9976`. The signature was checked on this machine; the packages themselves were not.
>
> Nothing was downloaded and nothing was installed. This server does not send files to a client it refused, so there is no button here that would fetch them — install the mods yourself from the sources below, restart V Rising, and connect again.
>
> Progenitor loads plugins at process start, so a mod you install now takes effect the next time you start the game — not in this session.
>
> `other.mod  server requires 2.0.0  you have nothing  1.18 MiB  sha256 a1b2c3d4e5f678a9`
> `  join check: other.mod: the server needs it (2.0.0), but your game doesn't have it. Install it from the join screens`
> `  get it from Thunderstore: ExampleTeam-Other`
> [Close]

With no descriptor (the switch off, or a server that predates this) the third line becomes
"This server sent no mod catalog with the refusal, so the rows below carry no download source.
Everything shown comes from the handshake this client answered.", and the row's last line becomes
"the server named no source for this mod." — the honest form, rather than a link that is not there.

A row that fails because *this client* declared a `ServerMustHave` the server lacks is labeled
`you require <version>` and its last line is "this server does not run this mod, so it cannot supply
it; the requirement is on your machine, not theirs." The server is never told to hand over what it
does not have.

**When every row is this client's own requirement, the client stopped the join, and the panel says so
(JOINWORD1, source-checked and game-free tested, not live-verified).** Live 2026-09-27 the server asked
about 0 mods, the client's `Sample` and `NecroArmyExample` rows (`ServerMustHave`) failed, and the panel
still opened "'&lt;server&gt;' refused this connection" and "The server's reason". Now
`ModDenyText.RefusedByThisClient` (every row `ModDenyOrigin.YouRequire`) switches the headline to "Your
client stopped this join: 2 of your mods need &lt;server&gt; to run them too, and it does not.", the reason
line to "Your client's join check: …", and replaces the catalog, nothing-downloaded and restart lines
(which are about installing what a server requires) with `ModDenyText.YourModsFix`: nothing is missing on
this side; turn the mods off (uninstall, or move a hot-loaded mod out of `progenitor-reload`) to join.
A refusal with any server-required row keeps the server wording. The panel and `Compose` read one
line list, `ModDenyText.Lines`.

**The panel opens on a plain summary; the text above is behind "Show details" (PLAINDENY1,
source-checked and game-free tested, not live-verified).** Feedback 2026-09-28 on the full-text panel:
"This message is still not clear enough. It need to be easier to read, less technical garbage." The title
is now `Can't join <server>` (`ModDenyText.PanelTitle`), and `ModDenyText.Summary` leads with a sentence,
a bullet per mod and what to do: "This server needs 2 mods you don't have:", "• Necroarmy (0.1.0)",
"• Sample (0.1.0)", "The server didn't say where to get them.", "Install them, restart the game, and join
again." A version mismatch says both versions; a source from the catalog goes on the bullet ("on
Thunderstore as Team-Alpha"); this client's own rows read "2 of your mods only work on servers that run
them too, and this one doesn't:" then "Turn them off to join this server." Names come from the loaded
mods inventory (`ModDenyText.NameLookup`, set by the panel), else the package's name part, else the id's
last segment capitalized. **Show details** rebuilds the panel in place with `ModDenyText.Details` (every
line described above: headline, reason, catalog signature, per-row join check and source) under the
summary, and **Hide details** collapses it; a new refusal always opens collapsed. `Compose` is the
summary followed by the details.

Every consequence quoted per row is `Handshake.Compare.EnforcedKickReason` — the same function the
kick path itself ran — so the panel and the game's disconnect box cannot drift by rewording. A row
the join check said nothing about gets no consequence line at all.

### Phase 4 log lines

Server:

```
ModManifest: deny descriptor for user 7: 1 row signed fp=a1b2c3d4e5f67890 (no transfer granted; this peer is not handshake-ok)
ModManifest: no deny descriptor for user 7: the catalogue has no row for any failing mod.
ModManifest: deny descriptor is off (Networking.DenyTimeModDescriptor = false); a refused client is told what is missing but not where to get it.
```

Client:

```
ModManifest: join refused; 1 mod row(s), 1 with a download source (signed catalogue received)
ModManifest: join refused; 1 mod row(s), 0 with a download source (no catalogue received)
ModManifest: deny panel already opened 3 time(s) this join; not reopening.
Ui: deny panel opened host=127.0.0.1:9976 rows=1 sourced=1
Ui: deny panel closed.
```

### Not verified in-game

Phase 4 has not been live-checked. Owed, in order: that the descriptor arrives before the kick on
the LAN test server (the pessimistic transport — Lidgren drops queued sends at `Disconnect`, Steam
lingers) -- **done, evidence 185, and it did arrive: the window is not the mechanism**; that
analyze into a fact about this machine; that the panel draws above `ConnectingView` and its Close
button is clickable (the game's metadata says no ProjectM input context can swallow a uGUI raycast, but
`ConnectingView.Start` does a `LoadScene(1, Single)` and which `EventSystem` survives that is
serialized scene data the metadata cannot see); and that `ClientWorld.Ended` is *not* published at the
kick. The full list is in the internal research notes (FMAN17).

## Phase 5 — the deferred kick and the install window (job FMAN18, server half)

**Retargeted 2026-10-05; source-checked and game-free tested (`NoServerDistributionTests`, `HandshakeInstallGraceTests`); not live-verified.** The window used to hold a refused join open while the server sent its own copy. Servers no longer send mod files, so the window now opens only when every failing row can be fixed **on the client** during the join, from the [join screens](join-flow.md): a Thunderstore install, a kept copy, Turn on, or (client mod policy) Pause for this server. The client then answers the handshake again and the server evaluates that answer from scratch. Two facts make a same-session fix safe: the reloader loads an allowlisted plugin dropped into `BepInEx/progenitor-reload/` within about two seconds, and **prefab sync is Core's, not the plugin's**, so a plugin that loads late in a join does not disturb the key-table validation the join is doing.

### What counts as a fix (`MissFix`)

`src/Progenitor.Core/Networking/InstallWindowFixes.cs`:

| Fix | Meaning | Who decides |
|---|---|---|
| `Thunderstore` | The row's signed catalog entry (or the admins' required client row) names a Thunderstore `package` that serves a version this server's row accepts under its rule (`ThunderstoreVersions.Availability`) | the server, from its own read of the Thunderstore index |
| `Local` | The client declared it can fix this row itself: a kept copy that loads now, a turned-off copy that hot-loads, or (SELFFIX1, only with the client's `[Install] LocalIndexPath` set) a hot-loadable package of its local test source at a version this server's check accepts | the client, in the `progenitor.hs.fix` row |
| `Pause` | Client mod policy only: a not-allowed mod that the client's report shows as a hot-loaded Progenitor mod (the per-server pause can unload it) | the server, from the client's mod report |
| `TurnOn` | Client mod policy only (SELFFIX1): the client's mod report claims a turned-off copy of a required mod that turns on now, at a version the row's rule accepts. Untrusted: it earns only the window | the client, in its report's `fixes` (`turn-on`) |
| `LocalTestSource` | Client mod policy only (SELFFIX1): the report claims a hot-loadable package of the client's local test source at an accepted version. Untrusted: it earns only the window | the client, in its report's `fixes` (`local-test`) |
| `None` | Anything else; the kick is not deferred | |

**The server's index read.** `ServerPackageAvailability` reads the Thunderstore index in the background for every package the catalog and the admins' required client mods name, through the same `JoinPackageIndex` the join screens use, with `[Install] LocalIndexPath` in front of the fetched index and the Install tab's cached copy as the fallback. It reads when the catalog is re-signed and on a refusal, reuses a read for 30 minutes (TSTRAFFIC1; was 10), checks past that with a conditional, compressed request over the server's own `progenitor-cache/index.json` (a 304 while the index is unchanged; [Thunderstore traffic](mods-menu.md#thunderstore-traffic-tstraffic1)), and never makes the handshake wait. A package not read yet answers `Unknown`, which is **not** a fix, so a server that cannot reach the index kicks exactly as before. Log: `ModManifest: read Thunderstore versions of N package(s) for the install window[ (K not listed)][; index: <error>]`.

**The fix row.** `progenitor.hs.fix` is a reserved answer row, like `progenitor.hs.cap`: blob `'1'` followed by a lowercase hex bit mask over **answer-row positions** (bit i = row i of the answer as sent, reserved rows counted), only for rows the server asked about. It is `ClientOnly`, never compared as a plugin, and ignored by older servers. The client appends it after the capability row, only when a slot is free; no asked row is ever dropped for it. A malformed blob reads as nothing declared. Client log: `Handshake: declared local fixes for answer rows 0x<mask>`.

### The flow

1. The client answers the handshake and fails. `Handshake.Compare.Evaluate` produces the problems and the failing ids.
2. `Handshake.Compare.DecideMiss` defers only when all of these hold; any no, null or throw kicks exactly as before:
   - `[Networking] InstallGraceSeconds` > 0 (default 60, 0 = off; the admins' panel sets the same value) and this user has not spent its window this join;
   - the client declared `Handshake.Capability.InstallWindow` (job FMAN20) **and** `Handshake.Capability.ClientFix` (256, 2026-10-05: it fixes the rows on its own side and re-answers) in its `progenitor.hs.cap` row (`Compare.CanSpendInstallWindow`). A Core from before 2026-10-05 declares only the first: it only re-answered after a server-sent download, so it gets the immediate kick rather than a window it would wait out;
   - every `problems` entry maps to a failing id (the 16-mod overflow string does not, and nothing fixes a protocol failure);
   - every failing id is one this server's own `Check` rejected (`ServerEnforcedFailures`);
   - every one of those has a fix other than `None` (`Handshake.ServerFixFor`: the client's declared `Local` first, else `Thunderstore`).
3. **Client mod policy** ([client-mod-policy.md](client-mod-policy.md) rule 4): `Handshake.ClientPolicy.cs` `TryDeferPolicy` uses `InstallWindowFixes.PolicyFixable`. The window opens when every problem is a required client mod whose admins' row names a Thunderstore package serving an accepted version (or, SELFFIX1, one the client's report claims it can fix itself: `TurnOn`, `LocalTestSource`), or a not-allowed mod the client can pause. A verdict that could not be checked never defers.
4. **The descriptor is built first, before anything is sent** (job FMAN20): the same narrowed, signed catalog the deny path sends (the failing rows with their packages, plus the join section); a client policy problem with no catalog row gets the join section alone. If nothing can be built (not server-side, `DenyTimeModDescriptor` off), no window opens and the client is kicked now.
5. `HandshakeInstallGrace.Begin` opens a window for that user — **one per join, never extended**. The answer watch moves to `AwaitingInstall`, which cancels the 20 s answer deadline.
6. The server sends the `Result` packet with `Ok = Handshake.ResultDeferred` (2) and `Handshake.Compare.DeferredResultMessage` (ASCII only; the packet is `Encoding.ASCII`):

   ```
   Mods needed: <names>. Install from the join screens within 60 s, or this join is refused.
   ```

   Then the descriptor. Exactly one `Result` leaves the server on this path. The user is **not** added to `HandshakeOkUsers`.
7. On the client, the join screens open in install-window mode. Every fix re-answers the handshake once it is in place:
   - a Thunderstore batch that loads everything arms a re-answer that fires once the installed handshake mods are declared, or after the 8 s turn-on wait (`JoinFlowClient.Stage2.cs` `ArmWindowReAnswerLocked` / `TickWindowReAnswer`; log `JoinFlow: re-answered the handshake after the Thunderstore install of <names> inside the install window; sent=true`);
   - Turn on and Use kept re-answer from their own paths (`JoinFlow: re-answered the handshake after turning on …`, `JoinFlow: versions: re-answered the handshake after the kept <id> <version> loaded; …`);
   - Pause for this server re-answers when the policy problems are resolved (`ClientPolicy: re-answered the handshake after pausing … inside the install window; …`).
8. The second answer closes the window (`InstallGrace.Settle`) and is evaluated **from scratch by the same code path as the first**. Pass: the ordinary ok path runs once. Fail: the ordinary mismatch kick. The grace stays spent, so a third answer cannot open a second window.
9. No passing re-answer: at the window's end the server kicks with `Handshake.Compare.NotInstalledReason`:

   ```
   Mod mismatch: <names> was not confirmed within 60 s (this client did not answer again in time). If it is installed now, join again; if not, install it from the join screens first.
   ```

   (`were` for more than one mod), down the same Result-then-`Disconnect` path every other handshake kick uses.

Disconnect drops the window **and** the spent-grace mark, so a delayed post cannot kick a new occupant of the same `User.Index` and a genuine reconnect gets its own window. Core `Uninstall` clears the whole table, and the next `Install` puts the spent marks back for every user that is **still connected** (`Handshake.MarkCarriedOverInstallGrace`, job FMAN20), so an in-process Core reload cannot hand a connected player a second window.

### The wire, and why an older client is safe

The `Result` packet is unchanged: same event id (`PrhQ`), same 481 bytes, same `Handshake.ProtocolVersion` (2). Only the `Ok` byte carries a third value. A new event id would be handed to the game's own reader by a client with no handler for it, and a new field would make an older client's size-checked `TryRead<Result>` drop every Result. A client older than FMAN19 reads `r.Ok == 1` and treats 2 as a refusal; it never declares the capability, so it is never deferred. A reply with all sixteen slots used sends no capability row and gets the immediate kick.

### Client half

`ModInstallWindow` (`src/Progenitor.Core/Networking/ModInstallWindow.cs`) only arms and remembers the window: `Handshake.OnResult` classifies the byte once (ok / deferred / denied); the deferred branch logs the server's message, captures the connect host and arms the window, and does **not** open the read-only deny report. A second deferred `Result` in one join is ignored with a warning. The window is reset by a new handshake request, the end of the join, and Core `Uninstall`. It installs nothing itself: the join screens do. There is no countdown: the deferred `Result` carries only the server's sentence, so the screens show it verbatim (`ClientInstallWindow.Headline`) with `ClientInstallWindow.WaitingNote` where a clock would be.

### Log lines

| Line | Side | When |
|---|---|---|
| `Handshake: {name} deferred for 60 s to fix {ids} on the client ({fixes}) ({problems})` | server | the window opened (warning); `fixes` is `thunderstore`/`local` per row, or `client mod policy` |
| `ModManifest: install window descriptor to user {n}: {k} mod(s), operator {fp}` | server | the descriptor went out |
| `Handshake: {name} not deferred: {why}` | server | `DecideMiss` said no; `why` names the condition, e.g. `'<id>' cannot be fixed during the join (no Thunderstore package serving that version, and the client declared no kept or turned-off copy)` |
| `ClientPolicy: {name} not deferred: {why}` | server | `PolicyFixable` said no |
| `Handshake: {name} not deferred: no descriptor could be sent for {ids}; kicking` | server | step 4 |
| `Handshake: {name} not deferred: install window AlreadyUsed` | server | the grace was already spent this join |
| `Handshake: {name} re-answered inside the install window` | server | the second answer arrived |
| `Handshake: {name} failed: Mod mismatch: {ids} was not confirmed within 60 s (…` | server | the window lapsed |
| `ModManifest: install window opened: {server message}` | client | `Ok == 2` armed the window |
| `ModManifest: install window: a second deferred result arrived this join; ignored (…)` | client | step 7's guard |

The `users` bridge op's `handshake` field shows `awaiting-install` while a window is open.

### Owed live

[test-rig.md](test-rig.md#install-window-from-thunderstore-tofu-and-legacy-cleanup) has the acceptance procedure (Thunderstore fix from a local index, trust on first use, an old trust file, the legacy cleanup), with the 2026-10-05 live results.

## The join section (JOINFLOW1)

**Status: source-checked and game-free tested (`JoinSectionSchemaTests`, `JoinFlowTests`, `JoinFlowPanelTests`), not live-verified.** What the join screens draw ([join-flow.md](join-flow.md)) — the server message, the reset notification, the recommended mods and the server's time zone — rides the catalog envelope as a separately signed section:

```json
{
  "body": { … },
  "join": {
    "catalog": { "fp": "a1b2c3d4e5f67890", "issued": "2026-10-04T12:00:00Z" },
    "message": "Welcome!\nWipe in {reset}.",
    "names": { "progenitor.sample": "Progenitor Sample" },
    "versions": { "progenitor.sample": "2026.10.52" },
    "recommended": [ { "id": "maps.mod", "package": "Owner-BroaderMap", "reason": "…", "version": "1.2.0" } ],
    "reset": { "every": 7, "next": "2026-10-10T18:00:00Z", "passed": "now", "precision": "Hour", "repeat": "Days" },
    "schema": 1,
    "showMessage": "Always",
    "zone": { "id": "Eastern Standard Time", "offset": -240 }
  },
  "joinSignature": "<128 hex r||s>",
  "signature": "<128 hex r||s>"
}
```

- **Why not a body field.** A client re-canonicalizes the body from the fields it knows and verifies the signature over that, so a new body key makes every older client reject the whole catalog (the `serverOnly` rule above). Every client's envelope parser reads only `body` and `signature`, so keys beside them cost an older client nothing; a newer client handles their absence (no section, nothing changes). An envelope with no section is byte-identical to before.
- **Signing.** The operator key signs `progenitor.join.v1\n` + the canonical section (keys sorted, no whitespace, the same writer as the body). The prefix keeps a join signature from ever verifying as a body signature: a canonical body starts with `{`.
- **Binding.** `catalog.issued` and `catalog.fp` must equal the body's `issued` and operator fingerprint, so a section cannot be lifted onto another catalog, and the body's replay gate covers it.
- **Verification.** On the client, only after the body passed its fingerprint, signature and replay gates (`JoinSectionJson.Verified`). A section that fails, is over a cap or has an unknown `schema` is dropped with `ModManifest: join section dropped: <reason>`; the catalog is still used. Unknown keys inside the section are ignored when reading, but the signature is checked over the canonical form the client rebuilds, so a key or value the client does not know fails verification and drops the section: a later field must be written only when it differs from the default older clients assume, or come with a schema bump.
- **Caps.** Message 2,000 characters; 16 recommended rows (`id` 63, `package` 128, `version` 64, `reason` 200 characters); passed text 120; zone id 64; the whole section 64 KiB. A section at every cap, in three-byte characters throughout, is about 30 KiB, far under the 256 KiB reassembly cap, and chunked at 15 KiB like the rest of the envelope.
- **Names and versions.** `names` maps each loaded `ServerMustHave` mod's lowercase id to the server's readable plugin name (at most 64 entries, ids 63 and names 64 characters), so a client that lacks a mod can still name it; `versions` maps the same ids to the version the server's handshake requires now (64 characters), which the in-world mods page judges against (a catalog row's version can be an overlay pin). Each is written only when not empty. The catalog body is unchanged: a body field would break older clients. Clients without join-flow support never read the section; a join-flow client built before these keys existed rebuilds the canonical form without them and drops the section (no released client is affected: the section is unreleased).
- **The server's name.** When the overlay sets no `serverName`, a JOINFLOW1 server signs its own (as the game resolves it at start: `-serverName`, then `VR_NAME` / `VR_SERVERNAME` / `VR_SERVER_NAME`, then the save folder's, the persistent-data and the install's `ServerHostSettings.json`; SERVERDESC1, [server-list-description.md](server-list-description.md#as-built)) instead of the default `V Rising`; a client still treats a `V Rising` catalog as unnamed.
- **Page frequencies.** `showMessage` (`WhenChanged`, `Always`, `FirstJoin`) and `showMods` (`WhenNeeded`, `Always`) are the admins' choices of [when each page shows](join-flow.md#when-each-page-shows). Each is written only when it is not the default (`WhenChanged`, `WhenNeeded`), so a section without them verifies on clients that predate them; a missing key reads as the default.
- **Building it.** A JOINFLOW1 server always builds a section, even with no message (the zone and the frequencies still travel), from Progenitor's server message rows ([join-flow.md](join-flow.md#server-message-settings-admins)) and the recommended rows — the admin panel's (`progenitor-join-admin.json`) first, then the overlay's `recommended` rows for other mods, 16 at most ([precedence](join-flow.md#recommended-rows-the-panel-and-the-overlay)) — and signs it into the catalog. Its canonical form is part of the catalog stamp, so an admin's edit re-signs the catalog and resends it to every handshake-ok client within a second, as a plugin load does.

### On the deny path

**The narrowing keeps the section.** `ModManifestDenyPolicy.Narrow` keeps `issued` and the operator, so the full catalog's signed section rides the narrowed descriptor unchanged.

**The rule that a descriptor may add package/url/sha256/size to a row and may not add a row still holds.** The recommended list and the message are not refusal rows: the deny report merges catalog rows by id and the section has none. The join screens draw them in their own sections ("Recommended by this server", the message page), never as reasons the join was refused, and the required list is still built from this client's own record of the server's handshake request.

**A message alone.** When no failing row has a catalog row, phase 4 sends nothing. A client that declared `Handshake.Capability.JoinFlow` (bit 4 in the `progenitor.hs.cap` row) is sent a **zero-row descriptor** carrying the section instead (`ModManifestDenyPolicy.JoinOnly`, re-signed with the same `issued`), so it can show the server's message on the refusal. An older client never declares the bit and is never sent one: its read-only report would describe a catalog with no sources. The descriptor still grants nothing: the user never enters `HandshakeOkUsers`. The server log adds ` with the join section` to `deny descriptor for user N: …`.

### On the client

- **The deny panel's place.** The join screens subscribe to the refusal report (`ModManifestDeny.PanelRequested`) in place of `ModManifestDenyPanel`, which stays as their fallback when they cannot take it (`JoinFlow: the screens could not take the refusal (…); showing the deny panel`).
- **Installs.** The mods page installs only from Thunderstore (per-server confirmation, [join-flow.md](join-flow.md#5-install-from-thunderstore-stage-2)), a kept copy or Turn on. The consent panel, the "Use server's copy" action and its trust page were removed 2026-10-05.
- **The retired trust acknowledgement.** `riskAck` (`yyyy-MM-ddTHH:mm:ssZ`) on a `progenitor-trusted-operators.json` row was written by the removed trust page. It still parses, and `Upsert` of the same key keeps it with `seen`; nothing sets it any more.

### Log lines

| Line | When |
|---|---|
| `ModManifest: join section dropped: <reason>` | client; the section failed verification, a cap or the schema |
| `ModManifest: deny descriptor for user N: … with the join section` | server; a descriptor carried the section (zero rows for the message alone) |
| `JoinFlow: screens opened (refused\|install-window\|in-world) page=… required=N ready=N recommended=N` | client |
| `JoinFlow: the server message opens in the world in 2 s` / `JoinFlow: nothing to show on this join (message <reason>, mods <reason>)` / `JoinFlow: the screens already opened in this world (a Core swap is not a join); not shown` | client, successful join |
| `JoinFlow: the server message is dismissed until it changes (server <fp@host>)` | client; Leave or Continue with the switch on |
| `JoinFlow: screens closed` (`(join complete)`) | client |
| `ModManifest: Thunderstore batch confirmed for operator fp=… host=… (N package(s))` | client; JOINFLOW2, the Thunderstore confirmation recorded in `thunderstoreAcks` ([join-flow.md](join-flow.md#5-install-from-thunderstore-stage-2)) |
| `JoinFlow: installing from Thunderstore: <Owner-Name@version>, … (confirmed\|confirmed earlier)` / `JoinFlow: Thunderstore batch done: <join-now\|restart-needed\|incomplete\|nothing>; <summary>` | client; JOINFLOW2 |
| `JoinFlow: restart and rejoin[ (dry run)]: record written to …; relaunch automatic\|manual (…)` / `JoinFlow: relauncher started (pid N)…` / `JoinFlow: quitting V Rising for the restart in N s` | client; JOINFLOW2 |
| `JoinFlow: rejoin record at start\|bridge: rejoin\|expired\|already-used\|invalid\|own-process\|none` / `JoinFlow: rejoining\|joining <address> (<transport>)` / `JoinFlow: reconnect: back to the main menu\|leaving the server (ExitGame view)\|LaunchGame …` / `JoinFlow: reconnect joined\|failed\|canceled (…)` | client; JOINFLOW2 |

## Not this phase

Copy-links clipboard, `profileCode` import, per-server profiles beyond the [per-server mod pause](mods-menu.md#per-server-mod-pause), and writing into `plugins/`. **Shipping package bytes from a server to a client is out of scope for good** (removed 2026-10-05; [client-mod-policy.md#security](client-mod-policy.md#security)): a server names Thunderstore packages, and the player installs them.

## Package inspection: `compatibility.peers` (mod compatibility C3)

A package's `progenitor.json` may declare the peer versions of itself the mod works with
([mod-compatibility.md](mod-compatibility.md#declaration)):

```json
{ "id": "example.trade", "version": "1.4.0", "side": "both", "sha256": "…",
  "compatibility": { "peers": ">=1.2.0 <2.0.0" } }
```

- **Optional.** Absent, `null` or empty means no range is declared.
- **Must be a valid range.** Otherwise inspection refuses the package with
  `progenitor.json compatibility.peers '<text>' is not a valid compatibility range: <why>; refused`. A `compatibility`
  that is not an object refuses with the `InspectContradictory` text.
- **Must agree with the image.** The plugin image's `[NetworkCompatibility(..., Peers = "...")]` is read from the bytes
  without loading them (`PluginCompatReader`, the entry point whose GUID is the `id`). The two must both be absent, or
  carry the same text once whitespace is ignored. Otherwise inspection refuses with
  `progenitor.json is present but its metadata is contradictory: compatibility.peers is '>=1.2.0' but the plugin's [NetworkCompatibility] Peers is '1.4.x'`.
  The same refusal names `not set` for the side that has no range. The check runs when the plugin image is among the DLLs
  inspection reads, which is every package within the identity scan's limits.
- **Kept with the version.** The inspection carries `DeclaredPeers` and `EffectivePeers`: the declared range, else the
  image's explicit rule, else the same minor version. The verified-inspection cache
  (`progenitor-cache/verified/<namespace>/<name>/<version>.json`) stores them as `peers` and `effectivePeers`. Files
  written before C3 still load. The pre-join prompt reads a version's range from there
  ([auto-update.md](auto-update.md#pre-join-prompt)).
- **Written for you.** `tools/make-dummy-package.ps1`, the tool that generates `progenitor.json`, copies `Peers` from
  the built DLL's attribute bytes, so the two cannot disagree. `tools/package.ps1` packages only Progenitor itself and
  writes no `progenitor.json`.

Source-checked and game-free tested (`PackageCompatRangeTests`), not live-verified.

## Qualification records: authoring the only route to apply-now (job HOT2)

**Policy change, 2026-10-03 (decision: "trust the hotLoadable flag", job HOTDEMO1).**
A record is no longer required for apply-now. A package whose `progenitor.json` declares
`hotLoadable: true`, which passes inspection (the declared `sha256` matches the installable DLL,
and id, version, side and `compatibility.peers` agree with the image), and whose image passes the preflight checks
below, is admitted with `AdmissionReasonCode.TrustedHotLoadable`, record or not. The preflight
checks are:

- exactly one `[BepInPlugin]` type deriving from `ProgenitorPlugin<T>`;
- a resolved base chain and references;
- no UnsafeHarmony, HarmonyLib, ClassInjector, native-import or reflection-escape finding.

A record that is missing or does not match is still listed among the reasons as a fact. Everything
else stays on the restart route and says why:

- a hash or manifest refusal (inspection fails, nothing is planned);
- `hotLoadable: false` or absent (`HotLoadNotDeclared`);
- any refused preflight finding;
- a plugin that is not a `ProgenitorPlugin` and has no matching record (`NotProgenitorPlugin`);
- an unavailable runtime snapshot, a busy loader, or a plugin already loaded.

The same trust covers a hot **unload** at rest. A promoted `progenitor-reload/` copy is deleted (and the reloader drops it)
when an inspection of that listing version declared `hotLoadable` (`progenitor-cache/verified`),
the file's bytes are exactly that declaration's `sha256`, and the image passes the same checks
(`HotLoadTrust.CanUnloadHot`). The rest of this section describes records, which still admit a
plugin the flag alone does not (for example a plain BepInEx plugin someone reviewed). The text
below is the pre-2026-10-03 rule for those records.

An apply-now install of a plugin the flag does not cover needs a **reviewed exact-image qualification record** in
`BepInEx/config/progenitor-qualification.json`. No record means
`AdmissionReasonCode.NoQualificationRecord`, and the install is the restart route. Records live in
local reviewed policy under the process's own BepInEx root; a record a package ships inside itself is
ignored and reported (`EmbeddedQualificationIgnored`) — **a package cannot self-certify.**

### What a record is

A record says: *a person looked at these exact bytes, on this exact game build, against this exact
Bootstrap/Core pairing, for this side, operation and target, and accepted the consequences of loading
them into a live process.* It is a review claim, not a certificate the package earned. The offline
artifact preflight proves structural facts and finds known forbidden references; it cannot prove that
arbitrary code uses only tracked facades or releases every registration, and a collectible probe does
not fill that gap. That is why `evidence` is a required key: the file has to name where the review
lives.

A record never covers "all future versions" (since 2026-10-03 an expired record no longer forces
the restart route for a trusted hotLoadable ProgenitorPlugin; see above). `QualificationRecord.Mismatches` compares **every** key,
and a blank on either side counts as a mismatch, so an unresolved identity cannot quietly satisfy a
record that also left the field blank.

### Shape (schema 1)

```json
{
  "schema": 1,
  "records": [
    {
      "id": "progenitor.hotdemo/initialinstall/client",
      "pluginId": "progenitor.hotdemo",
      "artifactSha256": "<64 lowercase hex of the ORIGINAL image bytes>",
      "artifactSize": 12345,
      "dependencyClosure": { "<bundled assembly name>": "<sha256>" },
      "gameBuild": "client-247473480",
      "bootstrapContract": "protocol/5+host/4",
      "coreContract": "core/0.1.0+admission/1",
      "side": "both",
      "operation": "InitialInstall",
      "target": "Client",
      "qualifiedUtc": "2026-09-12T00:00:00Z",
      "evidence": "research/hot-load/hotdemo-initial.md"
    }
  ]
}
```

Parsing **fails closed**: a record missing any key, or whose hash is not 64 hex characters, is dropped
and named in the policy's errors rather than admitted with a partial key. `side` is `client`, `server`
or `both`; `operation` is `InitialInstall` only (`Replace` and `Remove` need their own acceptance);
`target` is `Client`, `LocalServer` or `Both`.

### Author one with the bridge, not by hand

```
install_local {"action":"qualify","zip":"<package>.zip","target":"Client","evidence":"<where the review lives>"}
```

It inspects the zip with the ordinary non-executing pipeline and fills `artifactSha256`/`artifactSize`
from the installable image, `dependencyClosure` from every other DLL in the zip, and the three
compatibility identities from this process. The id is keyed by plugin id, operation and target, so a
second target is a second row and re-running for the same target replaces its own row instead of
accumulating duplicates. The write is atomic (temp file plus move), and an existing file the parser
cannot read is refused rather than overwritten. Op details: [bridge-ops-client.md](bridge-ops-client.md#install_local).

Hand-editing works — the file is plain JSON — but the hashes have to be the hashes of the *original*
image bytes, before any compatibility rewrite, and the three identities have to be spelled exactly as
this process resolves them. The op exists so neither has to be got right by hand.

### The three identities, and why a record expires

- `gameBuild` — the Resilience pass's build key. A game update changes it, and every record keyed to
  the old build stops matching. That is correct: the bytes were reviewed against different native code.
- `bootstrapContract` — the Core/Bootstrap install protocol pair.
- `coreContract` — Core's version plus the admission policy version. A change to the admission state
  machine changes what a record *means*, so an old record must not carry over silently.

Any of them that cannot be resolved is the literal `unknown`, never blank.

### A record (or the trusted flag) alone is not an apply-now install

Three more things have to hold, each a refusal by design when it does not: an available Bootstrap
admission snapshot (`ProcessHost.AdmissionSnapshotCapability >= 1`), a quiet loader at plan time (not
busy, nothing pinned or retiring, the plugin not already loaded, the allowlist admitting these bytes
under this file name), and the same loader state still standing when the executor rechecks immediately
before its write. See [hot-reload.md](hot-reload.md).

**Local-server half from a client (HOTDEMO1, 2026-10-03).** A client cannot read the server's loader. So the server half of a
`LocalServer` or `Both` install is admitted against what the client can see of the configured
folder (`HotLoadTrust.ProbeLocalServer`):

- a `VRisingServer.exe` running from that folder, matched by full path and never signaled;
- Progenitor Core installed there, so its plugin reloader watches `progenitor-reload/`;
- no `BepInEx/config/progenitor-allow.txt` that could refuse the image.

With all three, that half is written straight into the server's `progenitor-reload/` (ledgered
`mod`, no `hot` flag) and its reloader loads it within a second. Its Uninstall deletes the file
(the reloader unloads it), and a stale `.uninstall` marker beside a deleted file is removed. A copy of
the plugin already in the server's reload folder makes the install a replacement, which stays on
the restart route. The loader's busy and pinned state is not observable from here, so there is no
pre-write recheck for that half. Otherwise the half is the pending file and the marker, as before.
