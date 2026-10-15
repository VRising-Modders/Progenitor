# Progenitor.AboutExample

The smallest mod that ships its own About text for the Mods page ([About text on the mod page](../../docs/modules.md#about-text-on-the-mod-page)).

| File | What it shows |
|---|---|
| `ABOUT.md` | The page text, embedded with `<EmbeddedResource Include="ABOUT.md" LogicalName="README.md" />`. |
| `Plugin.cs` | `[ModAbout(...)]`, which a plugin with no embedded README would show instead. |

Build offline, from the repository root:

```
dotnet build samples/Progenitor.AboutExample
```

Acceptance procedure (not run): deploy only this project to the client's `BepInEx/progenitor-reload`, open the Mods menu and select **Progenitor About Example**. Expected: the About section shows the `# About example` text, and the page readback's `page-readme.source` is `embedded`. Afterwards remove `Progenitor.AboutExample.dll` from `BepInEx/progenitor-reload`.

Status: compile-verified (the build above); not live-verified.
