# AI starter prompt

Paste this into your AI assistant, with your mod's description in place of the angle brackets:

```text
We are making a V Rising mod with Progenitor. Follow PROGENITOR_GUIDE.md while building a mod that does the following: <your mod description>. Start from `dotnet new progenitor-mod`, use Progenitor's standard settings, keybinds, windows, notices and samples before writing anything custom, and keep the game's look.
```

The assistant needs the Progenitor repository (or [llms.txt](../llms.txt)) in reach. [PROGENITOR_GUIDE.md](../PROGENITOR_GUIDE.md) maps each need to its API and sample; the template is [templates/progenitor-mod](../templates/progenitor-mod).
