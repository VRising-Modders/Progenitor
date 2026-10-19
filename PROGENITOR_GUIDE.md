# Progenitor guide for mod authors and AI assistants

Build V Rising mods from Progenitor's standard pieces. Use the API and sample in the table before writing anything custom. Links are relative to the [Progenitor repository](https://github.com/VRising-Modders/Progenitor).

Starter prompt ([docs/ai-starter-prompt.md](docs/ai-starter-prompt.md)):

> We are making a V Rising mod with Progenitor. Follow PROGENITOR_GUIDE.md while building a mod that does the following: <your mod description>. Start from `dotnet new progenitor-mod`, use Progenitor's standard settings, keybinds, window presets, text styles, notices and samples before writing anything custom (docs/ui-controls.md lists every control), and keep the game's look. Fonts, colors and text alignment come only from the named text styles: never set them yourself.

## Need X → use this → copy this

| Need | Use | Docs | Copy from |
|---|---|---|---|
| A new mod | `dotnet new progenitor-mod -n MyMod` | [Start a mod](#start-a-mod) | [templates/progenitor-mod](templates/progenitor-mod) |
| A player setting | `ctx.Bind(section, key, default, description)` + `ctx.Settings.Page` | [mod-settings](docs/mod-settings.md#the-settings-page-builder-stage-2) | [ControlsSample `ControlsSettings`](samples/Progenitor.ControlsSample/Plugin.cs) |
| A server setting (admins edit, locked for players) | `ctx.Bind(..., synced: true)`, page row with `scope: SettingScope.ServerWide` | [settings scopes](docs/mod-settings.md#settings-scopes-on-the-page-step-4) | [template `ModSettings`](templates/progenitor-mod/ProgenitorMod/Plugin.cs), [SampleServerLed](samples/Progenitor.SampleServerLed/ServerLedSettings.cs) |
| A key | `ctx.Keybinds.Register`; a key that opens UI: `RegisterOpener` | [keybinds](docs/modules.md#keybinds-fui5) | [ControlsSample](samples/Progenitor.ControlsSample/Plugin.cs) |
| Any UI control | The control catalog; each control live in the controls sample's **Gallery** | [ui-controls](docs/ui-controls.md), [ui-style §3](docs/ui-style.md#3-pick-the-control-for-the-value) | [Gallery](samples/Progenitor.ControlsSample/GalleryDemo.cs) |
| A window | `ctx.Windows.Create(UiWindowPreset.Basic \| Header \| Tabbed, title, options)`: Basic for a small tool, Header for one thing the player works on (toolbar, tabs), Tabbed for a large screen | [ui-controls: windows](docs/ui-controls.md#layout-and-windows), [ui.md](docs/ui.md#standard-windows) | [template `ModWindow`](templates/progenitor-mod/ProgenitorMod/ModWindow.cs) (Basic), [Gallery windows](samples/Progenitor.ControlsSample/GalleryWindows.cs) |
| Text | `panel.Text(text, UiTextStyle.Body \| Title \| Section \| Muted \| Number \| Warning \| Error \| Success)`; a figure: `panel.Text(name, value, UiTextStyle.Number)` | [text styles](docs/ui-controls.md#text-styles) | [template `ModWindow`](templates/progenitor-mod/ProgenitorMod/ModWindow.cs) |
| A page in the Mods menu | `ctx.Menu.Register(name, description).Page(title, build)` | [ui-style §1](docs/ui-style.md#1-pick-where-the-ui-lives) | [ControlsSample](samples/Progenitor.ControlsSample/Plugin.cs) |
| A question or confirmation | `ctx.Dialogs.Confirm` / `Notice` / `Choose` / `Prompt`, `panel.ConfirmButton` | [dialogs](docs/ui.md#dialogs) | [ControlsSample `ControlsDialogs`](samples/Progenitor.ControlsSample/Plugin.cs) |
| A player notice | `ctx.Hud.Notify(message, kind: …)`; Progenitor adds `<Mod Name>: ` | [notifications](docs/ui.md#notifications), [message prefix](docs/modules.md#message-prefix) | [template](templates/progenitor-mod/ProgenitorMod/ModWindow.cs) |
| A button or line on a chest, station, inventory, heart or tooltip | `Ui.AttachToVanilla(site, anchor, ctx.Owner, ctx.Guid)` | [vanilla panel sites](docs/ui.md#vanilla-panel-sites-p6) | [PanelsExample](samples/Progenitor.PanelsExample/Plugin.cs) |
| Client asks server, server answers | `ctx.Requests.Handle` / `Send` | [requests](docs/modules.md#requests-large-messages-and-subscriptions) | [template](templates/progenitor-mod/ProgenitorMod/Plugin.cs), [SampleExchange](samples/Progenitor.Sample/SampleExchange.cs) |
| Live server state in an open panel | `ctx.Subscriptions.Topic` / `Subscribe` | [subscriptions](docs/modules.md#requests-large-messages-and-subscriptions) | [SampleExchange](samples/Progenitor.Sample/SampleExchange.cs) |
| One-way versioned messages | `ctx.Messages` + `ctx.Features` | [versioned messages](docs/modules.md#feature-negotiation-and-versioned-messages-mod-compatibility-c1c2) | [SampleServerLed](samples/Progenitor.SampleServerLed/Plugin.cs) |
| An admin-only action | `[Command(..., adminOnly: true)]`, or a server-wide setting | [commands](docs/commands.md), [modules: Commands](docs/modules.md#commands) | [SampleCommands](samples/Progenitor.Sample/SampleCommands.cs) |
| Castles, plots, buildings | `ctx.Castles` | [castles](docs/modules.md#castles) | [SampleCastle](samples/Progenitor.Sample/SampleCastle.cs) |
| Containers and items | `ctx.Inventory` | [inventory](docs/modules.md#inventory) | [SampleInventory](samples/Progenitor.Sample/SampleInventory.cs) |
| Stations and braziers | `ctx.Stations` | [stations](docs/modules.md#stations) | [SampleStations](samples/Progenitor.Sample/SampleStations.cs) |
| Which containers pay for building and crafting | `ctx.MaterialSource.Register` (experimental, opt-in seams) | [material sources](docs/modules.md#material-sources) | [SharedResourcesTest](samples/Progenitor.SharedResourcesTest/README.md) |
| Servant missions | `ctx.Servants` | [servants](docs/modules.md#servants) | [SampleServants](samples/Progenitor.Sample/SampleServants.cs) |
| Items, buffs, recipes | `ctx.Content.DefineItem` / `DefineBuff` / `Edit` | [content](docs/modules.md#content), [recipes](docs/recipes.md) | [SampleContent](samples/Progenitor.Sample/SampleContent.cs) |
| Per-save data | `ctx.Save` (with the `ctx.WorldState` epoch) | [persistence](docs/modules.md#persistence), [where data goes](docs/modules.md#where-should-my-data-go) | [SampleStore](samples/Progenitor.Sample/SampleStore.cs) |
| Mods page About text | Embedded `README.md` (`LogicalName="README.md"`), fallback `[ModAbout]` | [About text](docs/modules.md#about-text-on-the-mod-page) | [AboutExample](samples/Progenitor.AboutExample/README.md) |
| Mod help | Edit the content block of `help/index.html`; everything under `help/` is embedded (`LogicalName="help/%(RecursiveDir)%(Filename)%(Extension)"`) and the mod page's Help button opens it | [template DEVELOPING.md](templates/progenitor-mod/DEVELOPING.md) | [template help](templates/progenitor-mod/help/index.html) |
| Client and server parts that must agree | `[NetworkCompatibility]` + `[Counterpart]` | [counterparts](docs/mod-settings.md#counterparts), [ranges](docs/modules.md#compatibility-ranges-and-version-matching-mod-compatibility-c3) | [SampleServerLed](samples/Progenitor.SampleServerLed/Plugin.cs), [SampleClientLed](samples/Progenitor.SampleClientLed) |
| Hot reload | `ProgenitorPlugin<TContext>`, register through `ctx`, `ctx.Track` | [hot reload](docs/hot-reload.md), [unload rules](docs/modules.md#ownership-and-pin-constraints) | [ReloadExample](samples/Progenitor.ReloadExample/README.md) |
| Work every frame or tick | `ctx.Systems.Add` / `ctx.Hooks` (cached, change-driven) | [systems](docs/modules.md#systems), [refresh cost](docs/modules.md#refresh-cost) | [SampleSystems](samples/Progenitor.Sample/SampleSystems.cs) |
| Game-free tests | `Progenitor.Testing`: `ExchangeCompat`, `MessageCompat` | [testing across versions](docs/modules.md#testing-your-mod-across-versions-mod-compatibility-c5), [tutorial 6c](docs/tutorial.md#6c-make-your-mod-work-across-versions) | [template tests](templates/progenitor-mod/ProgenitorMod.Tests/ModRulesTests.cs), [samples' tests](tests/Progenitor.Testing.Tests/Samples) |
| Live checks without taking focus | Dev bridge and MCP tools, `keybind_press`, captures | [bridge](docs/bridge.md), [MCP](docs/mcp.md), [capture](docs/observe-capture.md) | [tutorial 8](docs/tutorial.md#8-testing-with-the-dev-bridge) |
| Controller and keyboard | Kit controls only; nothing extra | [navigation](docs/ui.md#controller-and-keyboard-navigation-nav1), [ui-style §6](docs/ui-style.md#6-input-keys-and-controllers) | [ControlsSample](samples/Progenitor.ControlsSample/README.md) |
| Publish on Thunderstore | Package layout, `progenitor.json`, `tools/publish-check.ps1` | [publishing](docs/publishing.md) | [template `thunderstore/`](templates/progenitor-mod/thunderstore/manifest.json) |
| Versions and changelog | `<Version>` = manifest `version_number`; `CHANGELOG.md` | [publishing §6](docs/publishing.md#6-versions-and-compatibility) | [template](templates/progenitor-mod/CHANGELOG.md) |
| License and credits | SPDX header; a `LICENSE`; credit forks | [publishing §9](docs/publishing.md#9-license), [NOTICE](NOTICE) | template headers |

## UI control categories

All public kit controls, by category. Full catalog, with each control's text style and where the **Gallery** (controls sample, Ctrl+F7, then **Gallery**) shows it live: [docs/ui-controls.md](docs/ui-controls.md).

| Category | Controls |
|---|---|
| Layout and windows | `ctx.Windows.Create` (Basic, Header, Tabbed), `panel.Text` + `UiTextStyle`, `ctx.Settings.Page`, `ctx.Menu.Register(...).Page`, `Ui.Panel` + `GameWindow` (only when no preset fits), `Heading`, `Note`, `Divider`, `BeginInline`/`EndInline`, `Tabs`, `MasterDetail` |
| Input | `Button`, `ConfirmButton`, `Toggle`, `Slider`, `SliderField`, `Number`, `Radio`, `Dropdown`, `MultiSelect`, `Input`, `TextArea`, `ColorField`, `DateTimeField`, `KeyField`, `CodeArea` |
| Lists and data | `DataTable`, `OrderedList`, `Picker`, `Chips` |
| Feedback and messages | `Label(text, UiTextStyles.ToneOf(style))`, `Fact`, `Info`, `Tooltip`, `Term` + `UiTerms`, `ctx.Hud.Tip`, `ctx.Dialogs`, `ctx.Hud.Notify`, `ctx.Chat`, `ICommandContext.Reply` |
| HUD and world | `ctx.Hud.Notify`, `ctx.Hud.Locate`, `ctx.Hud.PickInWorld` |
| Game-panel attachments | `Ui.AttachToVanilla` (container, station, inventory, castle heart, item tooltip) |

## Start a mod

```
dotnet new install templates/progenitor-mod            # from a Progenitor checkout
dotnet new progenitor-mod -n MyMod --team YourTeam --display-name "My Mod"
cd MyMod
dotnet build
dotnet test MyMod.Tests
```

Or install the pack: `dotnet pack templates/Progenitor.Templates.csproj -o out`, then `dotnet new install out/Progenitor.Templates.<version>.nupkg`. Options: `--team`, `--display-name`, `--license` (SPDX id, default `MIT`), `--progenitor-repo` (path to the checkout; found automatically when the mod is inside it or next to it). Needs the .NET 8 SDK and V Rising started once with BepInEx. Template check: `node tools/template-check.mjs`.

Install the template from the same checkout the mod builds against (`--progenitor-repo`, `ProgenitorRepo` or `PROGENITOR_REPO`) and update the two together. The template records the Progenitor version it came from (`ProgenitorMinimumVersion` in the mod's `Directory.Build.props`); before compiling, the build reads the checkout's version from git and stops with one error when the checkout is older ([template DEVELOPING.md](templates/progenitor-mod/DEVELOPING.md#keep-the-template-and-progenitor-together)). In this repository, a template change that starts using new API raises `ProgenitorMinimumVersion` in `templates/progenitor-mod/Directory.Build.props` to the current main release plus `.1` (main at `2026.10.139` gives `2026.10.139.1`; on main, `dotnet msbuild tools/version/ProgenitorVersion.targets -nologo -t:ProgenitorComputeVersion -getProperty:ProgenitorVersion` prints the release): that means "newer than release 139", which the merge satisfies. A test fails if it is newer than the checkout.

The template gives you: `ProgenitorPlugin<TContext>` with `OnLoad`/`OnUnload`, a player and a server setting, a `Ctrl+F8` opener, a Basic window, a notice, a request with quiet retries, an embedded README and help page, `[ModAbout]`, `[NetworkCompatibility]`, `[Counterpart]`, SPDX headers, Thunderstore files and a game-free test project.

## Rules

### Plugin and lifecycle

- Subclass `ProgenitorPlugin<TContext>`; keep the context class empty.
- Declare `[BepInDependency("progenitor")]` and `[ProgenitorSide]`.
- Register everything through `ctx`; wrap anything else in `ctx.Track`.
- Release everything on every unload reason; nothing you drew may stay on screen.
- Never persist a raw `Entity` or entity index; stamp kept entities with the `ctx.WorldState` epoch.
- Never use `ctx.UnsafeHarmony`; use `ctx.Patches` (raw Harmony disables hot reload).
- Don't write generic helpers that get instantiated with Progenitor types (e.g. `T On<T>(T h) where T : IUiHandle`); the runtime keeps the mod loaded until Core unloads, so hot reloads leak and Core updates are refused. Use non-generic helpers taking the base interface ([why](docs/modules.md#generic-helpers-instantiated-with-progenitor-types-pin-the-plugin)).

### Settings

- Bind every setting with `ctx.Bind`, a description and a default that suits most players.
- Server-decided values: `synced: true`, plus `scope: SettingScope.ServerWide` on the page row so admins edit them in game.
- Call `ctx.Settings.Page` on both sides.
- Never use BepInEx `Config.Bind` or your own `ConfigFile`.
- Mark rows that need the server part `needsServerPart: true` with a `fallback`.

### Keys

- Register every key with `ctx.Keybinds`. Never poll `UnityEngine.Input`, `Keyboard.current`, `Mouse.current`, `Gamepad.current` or game input.
- Keys that open or close UI use `RegisterOpener`; F-keys or Ctrl/Alt chords only.
- Test keys with the bridge's `keybind_press`, not real key presses.

### UI

- Use kit controls only; no custom colors, fonts or sprites ([ui-style §5](docs/ui-style.md#5-color-and-art)). Find the control in [ui-controls](docs/ui-controls.md) and look at it in the Gallery first.
- **Fonts, colors and alignment need special attention.** Every line of text takes a named style: `panel.Text(text, UiTextStyle.X)` (Title, Section, Body, Muted, Number, Warning, Error, Success) or a control that fixes its own. The style sets the game font, size, color and alignment; never set any of them yourself.
- Alignment: text left, numbers right (`UiTextStyle.Number`), button and tab captions centered, window titles centered. The presets and the kit do this; do not position text yourself.
- Before calling UI done: check font, color and alignment on every new line against its control's **Text style** column in [ui-controls](docs/ui-controls.md), and take a live screenshot at 1080p and 1440p.
- Windows: a standard preset, `ctx.Windows.Create(UiWindowPreset.Basic | Header | Tabbed, title, options)`; it draws the frame, the centered title and the version stamp. `Ui.Panel` + `GameWindow()` only when no preset fits.
- US English, sentence case, two-to-four-word control names, verb captions.
- Status in words, never color alone: `panel.Label(text, UiTextStyles.ToneOf(style))`, then `SetText(text, tone)`.
- Irreversible actions use `ConfirmButton` or `ctx.Dialogs.Confirm(..., danger: true)`.
- Check at 1920x1080, 2560x1440 and 3840x2160; nothing cut off or overlapping.

### Notices and messages

- Write the message alone. `ctx.Hud.Notify`, `ctx.Chat` and `ICommandContext.Reply` show it as `<Mod Name>: <message>` ([message prefix](docs/modules.md#message-prefix)); never type the mod's name in front. `prefix: false` only when the text already names the mod.
- Kinds: `Info`, `Success`, `Warning`, `Error`.
- Notices are for events; a problem the player must act on is a dialog.
- One sentence: what happened, what to do. No exception text.

### "Just works" UI

- Retry transient failures quietly (`Timeout`, `Busy`, `NotNegotiated`, `RateLimited`, `Unloaded`).
- Keep the last good data on screen; fall back silently.
- No retry buttons, no error walls, no stack traces.

### Networking and permissions

- Use `ctx.Requests`, `ctx.Subscriptions` and `ctx.Messages`; avoid raw `ctx.Net`.
- Every request is untrusted: check ownership and limits in the server handler; refuse with `RequestRefusedException(reason)`.
- Set `RequestOptions` limits (`MaxRequestBytes`, `Burst`, `PerSecond`).
- Admin-only actions: `adminOnly: true` commands or server-wide settings. All admins are equal.

### Compatibility

- Client+server mod: `[NetworkCompatibility(ServerMustHave, Minor, Peers = ">=1.0.0 <2.0.0")]`.
- Client part useful without the server part: `[Counterpart(CounterpartNeed.Limited, Features = …)]`; server part only adds extras: `Optional`; nothing works without it: `Required`.
- Client-only UI mod: `NetworkCompatibilityLevel.ClientOnly`.
- New wire field: minor version. Removed or retyped field: major version. Never reuse a field number.

### Performance

- No per-frame work that scales with buildings, containers or players.
- Cache; recompute on change (events, deltas), not on a timer over everything.
- Look things up once per reply or open, not every frame.

### Testing

- Put rules and messages in a game-free file; link it into a `Progenitor.Testing` test project.
- Test request pairs with `ExchangeCompat`, unknown fields with `MessageCompat`, old versions with recorded fixtures.
- Live checks: dev bridge and MCP tools, focus-free captures; never steal the player's focus.
- After live tests, remove test plugins from `BepInEx/progenitor-reload`.

### Versions, changelog, license

- One `major.minor.patch` in `<Version>`, `thunderstore/manifest.json` and `progenitor.json`.
- Add a `CHANGELOG.md` section per release, newest first.
- SPDX header in every source file; ship a `LICENSE`; credit forked work in the README.

### Publishing

- Description: `<Side> · Requires Progenitor. <pitch>`; README starts with the requirement block ([publishing §3](docs/publishing.md#3-the-standard-progenitor-message)).
- Depend on the oldest Progenitor release that has your APIs.
- Generate `progenitor.json` from the built DLL; run `tools/publish-check.ps1` until nothing fails.
- Required mods go in `manifest.json` dependencies. Recommended mods go in the README and the server join list's `recommended` rows ([join flow](docs/join-flow.md#recommended-rows-the-panel-and-the-overlay)).
- The Progenitor stamp on the icon is optional ([publishing §4](docs/publishing.md#4-the-progenitor-stamp-optional)).
- Never write "official", "verified" or "approved" next to Progenitor.
- Help AI assistants find your docs: set `website_url` in `manifest.json` to your repository, and ship an `llms.txt` at its root (or a README section) with what the mod does, its settings, keys and commands, and where to report bugs. Players' log zips list that link in `mods.md`.

## More

- [Tutorial](docs/tutorial.md), [module reference](docs/modules.md), [API status](docs/api.md), [knowledge notes](knowledge/README.md) (`node tools/kb/kb.mjs search <words>`).
- Interactive Help Guide for players, server admins and mod developers: `docs/guide/progenitor-guide.html` (open it in a browser).
- Status labels: only features marked *verified live* in [api.md](docs/api.md) have been seen working in the game.
