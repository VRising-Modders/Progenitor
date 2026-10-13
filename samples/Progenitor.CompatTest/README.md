# Progenitor Compat Test (test plugin)

A **test plugin** for the mod compatibility C1/C2 live tests: feature negotiation (`ctx.Features`, `ctx.Peer`,
`ctx.Peers`) and versioned messages (`ctx.Messages`). API reference:
[modules.md](../../docs/modules.md#feature-negotiation-and-versioned-messages-mod-compatibility-c1c2). It is not a
sample to ship; remove it from every reload folder after a test.

Two plugins come from this one project:

| Build | File | Id | What it does |
|---|---|---|---|
| default, `-p:ProgenitorVersion=1.0.0` | `Progenitor.CompatTest.dll` | `progenitor.compattest` | Offers `hello` (level 1); `Hello` message version 1 (fields 1, 2). |
| `-p:CompatTestV2=true -p:ProgenitorVersion=1.1.0` | `Progenitor.CompatTest.dll` | `progenitor.compattest` | Offers `hello` (level 2) and `notes`; `Hello` message version 2 adds field 3 `Note`. |
| `-p:CompatTestVariant=Other` | `Progenitor.CompatTest.Other.dll` | `progenitor.compattest.other` | Offers `beacon`. Run it on the server only: the server's attempt to send it to a client is refused with `PeerLacksMod`. |

Both sides run `progenitor.compattest` (`[NetworkCompatibility(NoNeedForSync, None)]`, so different versions may join).
The client says hello whenever the server's table arrives or changes; the server answers each hello and greets each
client whose table lists the mod. Every observation is one log line starting `CompatTest:`. The client also sends one
raw `ctx.Net` message per connection (`'CtRw'`, handled on both sides) so the unversioned-message warning shows. No
keys, no Harmony: hot-loadable.

The message classes (`Hello`, `Ping`) live in [CompatTestMessages.cs](CompatTestMessages.cs), a game-free file the replay tests link as source; [Plugin.cs](Plugin.cs) holds the plugins.

## Build

From the repository root, with Core built (`dotnet build src/Progenitor.Core`):

```
dotnet build samples/Progenitor.CompatTest -p:ProgenitorVersion=1.0.0 -p:BuildProjectReferences=false -o <out>/1.0.0
dotnet build samples/Progenitor.CompatTest -p:ProgenitorVersion=1.1.0 -p:CompatTestV2=true -p:BuildProjectReferences=false -o <out>/1.1.0
dotnet build samples/Progenitor.CompatTest -p:ProgenitorVersion=1.0.0 -p:CompatTestVariant=Other -p:BuildProjectReferences=false -o <out>/other
```

## Live test (verified 2026-10-09)

On the isolated test server and the test client, both running a Core with C1/C2:

1. Server: copy `1.1.0` and `other` into `BepInEx/progenitor-reload`. Client: copy `1.0.0`. Connect.
   Expected: client `peer changed: known=True … compattest=1.1.0 hello>=2=True notes=True other.beacon=True`;
   server `received hello … note='(no note: the sender predates field 3)' (sender wrote v1 …)`; client
   `received hello … (sender wrote v2, sender mod 1.1.0)` with field 3 skipped; server
   `CompatTest.Other … ping to a client that lacks this mod: PeerLacksMod`; client
   `Messages: progenitor.compattest sends raw ctx.Net messages (id CtRw) …`.
2. Swap the server to `1.0.0` and the client to `1.1.0` in the reload folders while connected. Expected: one
   `Features: sent a delta (1 changed, 0 gone)` per swap, the other side's `peer changed`, and the same exchange
   with the versions reversed.
3. Clean up: remove `Progenitor.CompatTest.dll` and `Progenitor.CompatTest.Other.dll` from both reload folders, check
   `unloaded …: clean` in both logs, disconnect.

## Replay tests (mod compatibility C5)

`tests/Progenitor.Testing.Tests/Samples/Progenitor.CompatTest/CompatTestReplayTests.cs` links `CompatTestMessages.cs` as the 1.1.0 build (`COMPATTEST_V2` is defined in that test project) and freezes the 1.0.0 `Hello` (fields 1 and 2) as `HelloV1` in the test. The fixtures in `Fixtures/` are what the live test's builds send (the client's hello, the server's echo and greeting): `progenitor.compattest/1.0.0/hello@v1` (3), `progenitor.compattest/1.1.0/hello@v2` (3) and `progenitor.compattest.other/1.0.0/ping@v1` (1). They were written by the test (`source: "test"`), not recorded live. The tests check:

- the 1.0.0 reader reads the 1.1.0 fixtures and skips field 3; the 1.1.0 reader reads the 1.0.0 fixtures and `Note` keeps `(no note: the sender predates field 3)`, as the live log showed;
- every message skips unknown fields (`AssertSkipsUnknownFields`);
- the range `>=1.0.0 <2.0.0` holds for both builds' types (`CheckRange` with each build as the current version: 1.1.0 is backed by the 1.0.0 fixtures and 1.0.0 by the 1.1.0 ones), and a `hello` that raises `MinReaderVersion` fails it. The plugin itself declares `NetworkVersionRule.None` (any version) and the Range variant declares a range but sends no message, so this is the range the two builds would declare under the compatibility contract.

```
dotnet test tests/Progenitor.Testing.Tests --filter "FullyQualifiedName~Progenitor.SampleTests.CompatTest"
```

The fixtures carry each message's field schema. A released version's fixtures are frozen: `PROGENITOR_WRITE_FIXTURES=1` writes only a version that has no folder yet (`MessageFixtures.SaveRelease` refuses an existing one), so a changed message is a new version, recorded beside the old one, and the next version's `CheckRange` replays the old fixtures. Status: built and game-free tested; the move of the classes into their own file is not verified live (the plugins' behavior is unchanged).
