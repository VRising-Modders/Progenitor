# Getting help

## 1. Collect your logs

- **In game or at the title screen:** **Mods** → **Progenitor** → **Get help** → **Collect logs**.
- **If the game crashes when you join:** the zip icon at the bottom left of the title screen.

A zip lands on your Desktop. If the game crashed, start it again first; the logs of the last three sessions are kept.

**Privacy.** SteamIDs, IP addresses, computer and user names and known player names are removed. Names typed in chat or written by other mods may remain. Look through the zip before you share it with anyone, an AI assistant included.

## 2. Ask an AI assistant

Open a chat with an AI assistant (ChatGPT, Gemini, Grok, Claude or another), paste the prompt below and add your question. For a problem, also attach the zip or the files in it; its `mods.md` lists every installed mod with its version, author and Thunderstore, website and help links. No zip? Each mod's page in the Mods menu shows its version, and **Store page** opens its Thunderstore page: paste the names, versions and links.

<a id="ai-starter-prompt"></a>
```text
I play, run a server or make mods for V Rising with Progenitor
(https://github.com/VRising-Modders/Progenitor). Help me with my question
about Progenitor or a mod that uses it.

- First read the repository's llms.txt and the Help Guide pages for my role
  (docs/guide/progenitor-guide.html).
- For other mods, use the mod list (mods.md in the log zip, or the list
  I paste) and each mod's linked page or repository.
- Answer questions directly. Ask for my Progenitor and V Rising versions,
  mod list or logs only when you need them, e.g. for a problem or crash.
- For a problem: the likely cause, then steps I can follow in the game or
  the game folder. If it looks like a bug, help me write a short report for
  the Progenitor Discord channel or GitHub.

My question:
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
