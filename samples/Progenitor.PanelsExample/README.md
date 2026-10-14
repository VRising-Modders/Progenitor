# Progenitor Panels Example

A client-only example of the vanilla panel sites (`Ui.AttachToVanilla` with a `VanillaPanelAnchor`, spec P6). It adds one button to every site and a line to the item tooltip, and logs every open, close, target change and click. It changes nothing in the world and sends nothing to the server.

| Window | Anchor | Button |
|---|---|---|
| Storage container | left edge (with a status row), right edge, beside rename | `Deposit`, `Output`, `Options` |
| Workstation and refinement station | left edge, right edge | `Inputs`, `Outputs` |
| Player inventory | beside sort | `Stash` |
| Castle heart (also `ThronePanel`) | left edge, right edge | `Castles`, `Fuel` |
| Item tooltip | tooltip body | `Example: item <guid> x<amount>` |

Log lines start with `Panels example:`; the framework's own lines start with `Ui: panel`, `Ui: attached` and `Ui: site`. The contract, the placement rules and the full live acceptance procedure are in [docs/ui.md](../../docs/ui.md#vanilla-panel-sites-p6).

## Status

Source-checked and built; the framework half is game-free tested (`VanillaPanelTests`). **Not run on a client yet.** Remove it from the reload folder after a live test: it is a test sample, not a mod for play.
