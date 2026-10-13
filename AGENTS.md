# Progenitor: agent rules

These rules apply to every AI coding agent working on Progenitor. Read [CLAUDE.md](CLAUDE.md) next for the engineering constraints; it applies to all agents, not only Claude.

- Follow the relevant [specification](docs/spec.html) and keep current contracts consistent with code. Track delivered work in [roadmap.md](docs/roadmap.md).
- Write original code. Do not copy from other modding frameworks, regardless of license. BepInEx is the only runtime dependency; propose additional dependencies in the specification first.
- Only operate game processes you have been asked to operate, on machines and servers the person you work for controls. Never touch a public or shared server. Match processes by resolved executable path, never by name.
- Respect client availability. Do not launch, quit, connect, deploy to, or send bridge operations to a client someone is using to play. Earlier test permission does not override a later restriction.
- When several agents share a machine, hold the side in the [bridge queue](docs/bridge.md#bridge-queue) before changing the client or test server, and leave the queue as soon as your live work is done. After sending a queue message, keep watching for the reply; if you cannot, retract it.
- Reloadable plugins and compatible Core builds can swap in process. Bootstrap changes require restarting the affected process. Follow [hot-reload.md](docs/hot-reload.md), including completion checks.
- Server code must not branch on dedicated versus client-hosted deployment.
- Do not commit, push, open or merge pull requests, publish releases or change repository settings unless the person you work for asks. Preserve other contributors' edits.
- Follow [documentation rules](docs/documentation.md): concise current guidance, buildable examples, exact steps and honest verification labels. Research, evidence, logs, native output, spike reports and review or history narratives stay outside this repository.

Before starting work in an area, search the [knowledge notes](knowledge/README.md) (`node tools/kb/kb.mjs search <words>` or `progenitor_kb_search`). When work hits a trap that cost real time and the fix was observed in the game, add a note in the same pull request; the bar and format are in that README. When delegating, put the same instruction in the worker's prompt.

For spell, NPC, effect or catalog work, start with [asset-research-workflow.md](docs/asset-research-workflow.md). Do not treat a catalog entry, inferred mechanism or accepted cast request as proof of observed behavior.

Build from the repository root with `dotnet build`. Game references come from `$(VRisingInteropDir)`; override local paths in the ignored `Directory.Build.local.props`. The game-free tests run with `dotnet test tests/Progenitor.Tests` and `dotnet test tests/Progenitor.Testing.Tests`.
