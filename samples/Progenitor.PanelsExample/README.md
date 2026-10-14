# Progenitor Panels Example

An example of the vanilla panel sites (`Ui.AttachToVanilla` with a `VanillaPanelAnchor`, spec P6). It adds one button to every site and a line to the item tooltip, and logs every open, close, target change and click. It changes nothing in the world.

Deploy it to both sides. The client half is the panels. The server half only answers the container's `Locate` button (`Locate.cs`): the client sends the window's network id words (`VanillaPanelTarget.NetworkIdIndex`, `NetworkIdGeneration`, `NetworkId2`) with `ctx.Requests`, the server resolves them with `ctx.Inventory.TryResolveNetworkId` to the chest's stable `ContainerId`, turns the id back into words with `ctx.Inventory.TryGetNetworkId`, and the client finds its own entity with `ctx.Hud.TryFindByNetworkId` and pings it with `ctx.Hud.Locate`. Client only, the panels still work and `Locate` logs that the request was not answered.

| Window | Anchor | Button |
|---|---|---|
| Storage container | left edge (with a status row), right edge, beside rename | `Deposit`, `Output`, `Locate` |
| Workstation and refinement station | left edge, right edge | `Inputs`, `Outputs` |
| Player inventory | beside sort | `Stash` |
| Castle heart (also `ThronePanel`) | left edge, right edge | `Castles`, `Fuel` |
| Item tooltip | tooltip body | `Example: item <guid> x<amount>` |

Log lines start with `Panels example:`; the framework's own lines start with `Ui: panel`, `Ui: attached` and `Ui: site`. The contract, the placement rules and the full live acceptance procedure are in [docs/ui.md](../../docs/ui.md#vanilla-panel-sites-p6).

## Status

Source-checked and built; the framework half is game-free tested (`VanillaPanelTests`, `NetworkIdResolverTests`). **Not run on a client yet.** Remove it from the reload folder after a live test: it is a test sample, not a mod for play.
