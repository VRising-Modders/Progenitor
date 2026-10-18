# Progenitor Panels Example

An example of the vanilla panel sites (`Ui.AttachToVanilla` with a `VanillaPanelAnchor`, spec P6). It adds one button to every site and a line to the item tooltip and the recipe tooltip, and logs every open, close, target change and click. It changes nothing in the world.

Deploy it to both sides. The client half is the panels. The server half only answers the container's `Locate` button (`Locate.cs`): the client sends the window's network id words (`VanillaPanelTarget.NetworkIdIndex`, `NetworkIdGeneration`, `NetworkId2`) with `ctx.Requests`, the server resolves them with `ctx.Inventory.TryResolveNetworkId` to the chest's stable `ContainerId`, turns the id back into words with `ctx.Inventory.TryGetNetworkId`, and the client finds its own entity with `ctx.Hud.TryFindByNetworkId` and pings it with `ctx.Hud.Locate`. Client only, the panels still work and `Locate` logs that the request was not answered.

| Window | Anchor | Button |
|---|---|---|
| Storage container | left edge (with a status row), right edge, beside rename | `Deposit`, `Output`, `Locate`, and a log icon button (`VanillaButtonIcon.Drawn("log")`) whose hover tooltip reads **Chest log** over *Deposits and withdrawals of this chest.* (`IUiButton.Tooltip(text, title)`); a click logs `Panels example: click chest log <target>` |
| Workstation and refinement station | left edge, right edge | `Inputs`, `Outputs` |
| Player inventory | beside sort | `Stash` |
| Castle heart (also `ThronePanel`) | left edge, right edge (twice), the top-right icon row | `Castles`, `Fuel`; `Routes` under a second plugin id (`progenitor.panelsexample.routes`) on the same right edge, which stacks under `Fuel`; a settings cog (`VanillaButtonIcon.Drawn("settings")`) left of the window's own icon buttons (`VanillaPanelAnchor.BesideTitleButtons`), whose tooltip reads **Castle settings** over *Options of this castle's mods.*; a click logs `Panels example: click heart settings <target>` |
| Item tooltip | tooltip body | `Example: item <guid> x<amount>` |
| Recipe tooltip (workstation, refinement station, crafting tab) | tooltip body; under the requirement list; under the title | `Example: recipe <guid> makes <n> x <guid>, tooltip shows <guid> at station <id>` (muted); one `Example: needs <n> x <guid>` per requirement (the first green); `Example: header, <Workstation\|RefinementStation\|Character>` (muted) right under the title, above the description. Each hovered recipe also logs `Panels example: recipe <target>`. Under each requirement line, a muted `Example: line <i>, <guid>` indented one level (it starts under the line's name, past its icon). At the very bottom, the key hint `Hold [Alt] Show storage` in the game's hint style; while Alt is held, a green `Example: footer, held, <Kind>` under it. |
| Item tooltip (header and footer) | under the title; the very bottom | a muted `Example: header, item <guid>`; the same key-hint footer as the recipe tooltip. The key is the plugin's hold bind `ShowStorage` (default Alt): remap it in the Keybinds tab and the glyph follows. |

It also subscribes to `InventorySortUsed` ([docs/api.md](../../docs/api.md#inventory)): every use of the game's sort, by key or button, logs `Panels example: sort Single Own net=… seq=… since=… at day … hh:mm …` with the client's own hour of day (`ctx.Time.GetTimeOfDay()` on the client), and two sorts of the same inventory within 400 ms log `Panels example: double sort Own (… ms apart)`.

Log lines start with `Panels example:`; the framework's own lines start with `Ui: panel`, `Ui: attached` and `Ui: site`. The contract, the placement rules and the full live acceptance procedure are in [docs/ui.md](../../docs/ui.md#vanilla-panel-sites-p6).

## Status

Source-checked and built; the framework half is game-free tested (`VanillaPanelTests`, `VanillaRecipeTooltipTests`, `NetworkIdResolverTests`). **Not run on a client yet.** Remove it from the reload folder after a live test: it is a test sample, not a mod for play.
