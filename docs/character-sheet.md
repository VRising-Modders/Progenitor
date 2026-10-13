# Character sheet presentation

**Presentation, reload and section-spacing checks passed; performance qualification pending.** Matching Core builds on the server
and client can refresh Character > Attributes from the requesting player's effective
server stats. This is part of Core; content packs do not implement a separate UI.
Gameplay components are read, never rewritten by the sheet.

The sheet retains native rows, art, selection, scrolling and hover tooltips. Useful
missing rows are added to existing categories only when a value is available, without
duplicating rows elsewhere in the sheet. Existing localization and formats take
precedence; fallback descriptions distinguish recovery rates, damage multipliers,
resistance percentages, regeneration ticks and blood-consumption costs.

Native cap bars and bonus totals remain when they agree with the effective value.
An inconsistent cap breakdown is hidden rather than fabricated; the effective value
and description still use the native row and tooltip. This does not change gameplay caps.

Snapshots are requested at most four times per second while the Attributes page is
visible. They are tied to the connection, character and equipped item instances when replicated;
unreplicated item entities use a server-checked definition/broken-state match. Late replies for previous equipment are discarded. Closing the page, disconnecting or
losing fresh snapshots restores native presentation. Servers without the endpoint
retain native behavior. There are no loading notices or extra player controls.

Ten headless equip/donor-restoration cycles verified actual native row text, including movement, resource yield, three resistances, Undead Damage and four cooldown rates. Removing the equipped movement weapon also restored zero bonus. Reopening retained 58 rows without duplicates, including 18 additions. Blood Drain is excluded because the native value reader rejects it. Three client Core reloads, one server Core reload and reconnect passed on the same candidate image; old generations were released and row counts remained stable. Rendered section spacing now passes after a client reload and close/reopen. All nine section layouts were read back; Physical, Defense, Health & Healing, Utility and Cooldown were visually checked with native selected-row tooltips. Removing added rows restores native counts and positions. Exhaustive tooltip/localization coverage and performance acceptance remain pending.

Presentation is a separate qualification column: a correct row or tooltip does not
prove combat, cooldown timing, removal, persistence or multiplayer behavior. Item stat authoring coverage is independent of sheet presentation.
