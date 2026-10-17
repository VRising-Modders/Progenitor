# Security policy

## Reporting a vulnerability

Report security problems privately through GitHub's **private vulnerability reporting**: open the repository's **Security** tab and choose **Report a vulnerability**. Please do not open a public issue, pull request or Discord post for a security problem.

Include what you found, the Progenitor version or commit, the side (client, server or both), steps to reproduce, and what an attacker could do with it. A maintainer will acknowledge the report, keep you informed while it is investigated, and credit you in the advisory unless you prefer not to be named. Please give us a reasonable time to release a fix before you disclose the problem publicly.

## Supported versions

Only the **latest release** receives security fixes. Progenitor is pre-release software: until the first release is published, fixes land on `main`.

## Scope

In scope:

- **Bootstrap** (`Progenitor.Bootstrap.dll`), **Core** (`Progenitor.dll`, shipped as `Progenitor.core`) and the **preloader** (`Progenitor.Preloader.dll`, shipped as `Progenitor.Preloader.patcher`): the code in a release package that runs on players' clients and on servers. Examples: a client that can make a server run code or write files it should not, a server that can do the same to a connecting client, a way around admin checks for server-wide settings, unsafe handling of downloaded mod packages, or private data left in a log bundle.
- The packaging and install tooling under `tools/` and `packaging/`, where a flaw could put something unsafe into a release.

Out of scope, or by design:

- **The development bridge (`Progenitor.Dev.dll`) and the MCP server (`tools/mcp`) are development-only.** The bridge is a loopback socket with a token and an opt-in marker, meant for a developer's own machine. It must never run on a public server, and release packages do not include it. Reports that it is powerful when installed and enabled are expected behavior; a way to reach it from another machine, or to enable it without the opt-in, is in scope.
- BepInEx plugins run with full process authority. A malicious mod a player or operator chose to install is outside what Progenitor can sandbox.
- Problems in BepInEx, Il2CppInterop, HarmonyX or V Rising itself: report them to those projects.
- Cheats that use the game's own behavior without a Progenitor flaw.
