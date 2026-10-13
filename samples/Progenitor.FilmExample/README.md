# Film example

The set of "The Comments", a short film with a vampire heroine and Dracula, shot in the game instead of in
Blender. It grows with the film modules; today it exercises `ctx.Models` only.

## What it does now

Client only. On load it streams in Dracula's evolved model (`HYB_NPCDracula_Evolved`, section
`ddfc6a4db4009e2448b8f8b6f0965b8f:373` on the 2026-08-25 build), which the game only loads during
his boss fight. When the handle is Ready it makes an inert copy named `FilmDracula`, hides the wings
and sword, and stands it 2.5 m to the right of the local character. Unload destroys the copy and
releases the model.

## Verified

Live 2026-09-28, extra client 4 on the film test server, in Dunley Monastery: Ready in 102 ms on a
fresh client and 104 ms after a hot reload; the reload unloaded cleanly and left exactly one copy.
The render with a separate film camera still lights characters wrongly (too blue); that belongs to
the film camera work, not to model loading.

## Files

The set (`set/film_set.glb`), the take (`take/take.json`), capture requests (`capture.json`), marked
shots (`marked_shots.jsonl`) and screen frames (`screen_frames/`) are read from and written to one
folder: `[Film] Folder` in `BepInEx/config/progenitor.film.cfg`. Empty (the default) means
`BepInEx/progenitor-film`. Source-checked and built; the configurable folder was not run live.

These are raw files on purpose: a client-only working folder exchanged with tools outside the game,
not world state or server data. A mod's world state belongs in `ctx.Save` and data that survives
wipes in `ctx.ServerSave` ([Where should my data go?](../../docs/modules.md#where-should-my-data-go)).
