# Getting help

## 1. Collect your logs

- **In game or at the title screen:** **Mods** → **Progenitor** → **Get help** → **Collect logs**.
- **If the game crashes when you join:** the zip icon at the bottom left of the title screen.

A zip lands on your Desktop. If the game crashed, start it again first; the logs of the last three sessions are kept.

**Privacy.** SteamIDs, IP addresses, computer and user names and known player names are removed. Names typed in chat or written by other mods may remain. Look through the zip before you share it with anyone, an AI assistant included.

## 2. Ask an AI assistant

Open a chat with an AI assistant (ChatGPT, Gemini, Grok, Claude or another), paste the prompt below, and attach the zip or the files in it.

<a id="ai-starter-prompt"></a>
```text
Help me fix a problem with V Rising mods that use Progenitor
(https://github.com/VRising-Modders/Progenitor).

1. Read the repository's llms.txt, SUPPORT.md and the player pages in
   docs/guide/progenitor-guide.html, then the docs they link to.
2. Ask me for my V Rising version, my Progenitor version (Mods > Progenitor),
   the mods I use, and whether I play on a server or in a private game.
3. Read the logs I attach (BepInEx LogOutput.log, Player.log, the mod list).
4. Tell me the likely cause, then give me steps a player can follow in the
   game or the game folder. No code changes unless I ask.
5. If it looks like a bug in Progenitor or a mod, help me write a short bug report.

My problem: <describe what happened and what you expected>
```

## 3. Report a bug

Post in the [Progenitor channel](https://discord.com/channels/978094827830915092/1112888087836631090) on the V Rising Mod Community Discord. The link opens only after you have [joined that Discord server](https://vrisingmods.com/discord). Or [open a bug report](https://github.com/VRising-Modders/Progenitor/issues/new?template=bug-report.yml) on GitHub.

A good report is short:

- V Rising version, Progenitor version and your mod list
- Steps to reproduce
- What you expected and what happened
- The relevant log lines or the zip, checked for private details

## 4. Fix it yourself

Fork the repository, fix the problem, and open a pull request; it is reviewed before it is merged. [CONTRIBUTING.md](CONTRIBUTING.md) covers building against your installed game, running the tests and the coding rules.

Security problems: see [SECURITY.md](SECURITY.md), not a public post.
