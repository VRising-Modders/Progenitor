# AI starter prompt

Paste this into your AI assistant, with your mod's description in place of the angle brackets:

```text
We are making a V Rising mod with Progenitor. Follow PROGENITOR_GUIDE.md while building a mod that does the following: <your mod description>. Start from `dotnet new progenitor-mod`, use Progenitor's standard settings, keybinds, window presets, text styles, notices and samples before writing anything custom (docs/ui-controls.md lists every control), and keep the game's look. Fonts, colors and text alignment come only from the named text styles: never set them yourself.
```

The assistant needs the Progenitor repository (or [llms.txt](../llms.txt)) in reach. [PROGENITOR_GUIDE.md](../PROGENITOR_GUIDE.md) maps each need to its API and sample; the template is [templates/progenitor-mod](../templates/progenitor-mod).

What to check in its work:

- Windows come from `ctx.Windows.Create` with a preset (Basic, Header or Tabbed), and each control is one from the [controls catalog](ui-controls.md); the controls sample's Gallery shows them live.
- Every line of text has a named style (`panel.Text(text, UiTextStyle.X)`). No hand-set colors, font sizes, fonts or alignment: these are the most common mistakes, so look at every label in the game.
- Messages are written without the mod's name: `ctx.Hud.Notify`, `ctx.Chat` and command replies add `<Mod Name>: ` themselves.
