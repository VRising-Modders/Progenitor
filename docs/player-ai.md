# Player AI companion and MCP

**Planned — not implemented or live-verified.** Roadmap item M3.11. Progenitor should let a player use their chosen AI to discover and operate supported mods without generating code, compiling plugins or enabling development tools. Voice or text interaction belongs to the player's AI application; Progenitor supplies discoverable game capabilities, not an embedded model or voice service.

## Player experience

**Planned mod discovery and installation:** the companion also searches available mods and returns descriptions through the same catalog as the Mods workspace's Install mods tab. It can offer a matching mod, present the install/restart plan and install through the shared approved handler without writing code. See [package capabilities and player AI flow](mod-package-capabilities.md). This is separate from discovering capabilities of already installed mods and does not expose arbitrary assembly loading.

A player installs the Progenitor client and opts into a separate local MCP companion. Their AI connects to that companion. They can ask questions such as “Which of my accessible boxes contains iron?” or “Help me use this mod's sorting feature.” The AI discovers the installed mod's supported queries/actions, obtains current permitted state, explains the feature and invokes an available action with typed arguments. Missing support returns an explicit unsupported result; it does not trigger code generation or fall back to the Dev bridge.

Container search is a first acceptance example, not a claim that all inventory information is currently available. Results identify the accessible container, relevant item quantities, permitted location information and freshness. Partial or unavailable searches say so; an empty partial search is not proof that the player owns none of the item. Item lookup does not implicitly move items.

## Planned architecture

- **Local player companion:** a separately configured MCP endpoint on the player's machine, usable without Progenitor.Dev, a compiler, repository checkout or server debug token. Package an easy setup path with the client; do not automatically grant AI access on installation.
- **Shared capability registry:** mods explicitly register versioned queries and actions with stable identifiers, plain descriptions, typed inputs/results, execution side, permission requirements and whether they change state. The Mods UI and MCP use the same application handlers where applicable. Arbitrary custom UI callbacks do not automatically become AI tools.
- **Discovery and help:** report currently available capabilities and constraints for installed/active mods, including concise usage guidance and unavailable reasons. Mod authors implement the integration once; players and their AI do not write code for supported operations.
- **Server authority:** personal options execute locally. Server reads and actions travel through the authenticated player's game connection. The server derives the actor from that connection, checks current permissions and mod/server policy on every request, and returns only authorized data. A caller-supplied player ID or admin flag is never authority.
- **Separate development surface:** the player companion exposes no arbitrary execution, raw memory/entity writes, assembly loading, development-token forwarding or automatic admin impersonation. Administrator capabilities are separately granted by the server to the current administrator and can be revoked. They are not the Dev MCP.

The first increment is the registry, discovery/help, one personal setting, one permission-aware inventory query and one registered mod action. The default map capabilities below add a first-party player feature and depend on the map work in M3.1. Neither increment depends on completing the asset browser, installer or coordinated deactivation. Existing menu and server-setting services are foundations to reuse, not proof that this general registry exists. The current UI assignment remains separate.

## Default player capabilities: map and pins

### Server-authorized location queries

**Planned permission model, not an existing general service.** A request such as “Show me Lord Styx” resolves a named target through a registered location provider and asks the server using the connected player's authenticated identity. The server evaluates an explicit location-query permission and its discovery/privacy policy at request time. Installing the client companion grants no additional knowledge or authority. Existing setting-specific admin checks are a foundation, not implementation of this general permission system.

The server can return an authorized current position, a known area or patrol region, or a structured result such as not discovered, not currently located, ambiguous, denied or unsupported. Results distinguish current observations from usual/spawn locations, include freshness and permitted precision, and expose only the information allowed by policy. A denied query must not leak a hidden target's existence or location through explanatory details. Vanilla integrations and mods register providers through the same capability system; their implementations and supporting permissions must be built and qualified.

The AI passes an authorized location result to the map capability to show or mark it. No manually maintained common-pin list or model memory is required to answer current-world queries. Saved pins remain optional player bookmarks, not an authoritative location source. A server snapshot does not authorize continuous tracking; subsequent queries must recheck access.

Acceptance must include a named boss query with permitted exact and reduced-precision results, a discovery/permission refusal, a missing or ambiguous target, stale-position reporting and permission revocation between requests. Verify that a player cannot impersonate another player or obtain a hidden location by calling the provider directly. These cases are not yet run.

Progenitor should supply useful built-in capabilities even before a content mod registers its own actions. The first map workflow is “Show me where this is”: resolve an unambiguous, permitted location from a query result or the player's selection, place a personal pin, and show/focus that location on the map when requested. Ambiguous targets require selection; unsupported or unavailable locations must not be guessed.

| Planned capability | Contract |
|---|---|
| Locate and show | Resolve only player-known or explicitly server-authorized locations; open/focus the map on request. A local pin is not teleportation, pathfinding or discovery of hidden world state. |
| Create a personal pin | Add a labeled marker at the resolved location using a qualified native marker path where supported, or the owned Progenitor layer. Return its identity and readback status; do not claim it is visible until attached to the current map view. |
| Progenitor pin types | Provide a distinct default style and a mod-extensible registration for owned icons, labels, descriptions and categories. Reuse the framework's asset and UI ownership rules. Players can distinguish the source and hide categories. |
| List, update and remove pins | Operate on authorized Progenitor-owned markers. Retries do not duplicate pins. Do not modify native or another mod's pins through this surface without an explicit supported ownership/access contract. |

Pins are personal by default; clan/server sharing is a separate explicit action requiring server policy and authorization, not an automatic consequence of creating a marker. Temporary query markers expire or clear with their owning session. Explicitly saved player pins persist as plain data scoped to the correct player and server/world; never persist a raw entity ID as their identity. Saved markers use a supported fallback style if an optional mod icon is unavailable. Live subscriptions, UI objects and mod-owned temporary markers release on unload. Reattach valid saved pins without duplication when the map or Core is rebuilt.

A position snapshot must say when it was observed; following a moving target is an explicit capability requiring continuing permission, not an assumed property of a pin. Losing access ends tracking and removes restricted transient information. Query results and permitted map location data should compose directly so the AI can find an item and mark its container without generating code or reconstructing coordinates from screenshots.

### Map acceptance — not run

Find an item in an accessible test container, create a labeled pin, view its actual placement on the native map, and read its identity back. Update/remove it, retry creation with the same request, close/reopen the map and hot-swap the owning mod/Core; verify no duplicates, stale callbacks or stuck input. Qualify a Progenitor-specific style and a mod-registered style. Verify temporary cleanup, saved-pin restoration in the correct world, unavailable-icon fallback, personal visibility and refusal to reveal an inaccessible container. An offline marker count does not prove on-map visibility. General map integration remains planned under M3.1.

## Native and mod command coverage

**Planned — not implemented or live-verified.** M3.11.3 lets AI discover and use supported game and mod operations directly through MCP. Runtime discovery supplies available commands, native names/aliases, typed inputs/results, concise help, execution side and transport, current permission requirements and effect/completion semantics. The AI adapts to the connected game and active mods without generating code or consulting a manually maintained coverage matrix. Use current game registries and mod capability registrations as the source; where native metadata is insufficient, a qualified adapter supplies the typed contract. Missing support returns a structured unavailable result rather than guessed arguments.

Refresh discovery when the game/session, active mods or permissions change, and invalidate stale capabilities on unload or swap. Discovery describes permitted operations; execution still checks current server authority. Verification records remain development evidence, not a catalog players or their AI must maintain.

| MCP audience | Intended scope |
|---|---|
| Player | Permitted personal controls, help, map/pins and registered gameplay actions. Ownership of the client grants no server-admin authority. |
| Administrator | Explicitly granted moderation, player inspection, item/character changes, teleports, world events and castle operations. Recheck current server authority for each action. |
| Server operator | Separately authenticated server-management operations, including announcements, save/shutdown controls and server metadata where supported. RCON credentials remain with the operator endpoint. |
| Developer | Test instrumentation and debug operations on an authorized test instance. Existing Dev chat injection is not native-console or RCON parity. |

The [GameServerKings command guide](https://www.gameserverkings.com/knowledge-base/vrising/how-to-add-admins-and-commands/) is an external discovery checklist, not a verified API contract. Qualify the installed game's command registry and official server documentation before implementing adapters. Keep the detailed inventory, source findings and raw evidence outside the repository. Console, RCON and mod chat are distinct execution surfaces; never assume a command accepted by one works through another. Record missing client requirements and commands unavailable in retail builds explicitly.

Expose discover/help/invoke/status through typed, allowlisted capabilities. Reuse the same registered mod handlers as the UI and chat where applicable. Do not offer unrestricted console strings, chained commands or aliases that evade capability checks. Resolve cursor-relative operations to explicit validated targets for focus-free use. Administrative authentication must use the supported authorization path; never write an admin flag or alter server debug policy to make a request succeed. Report effective authority separately from eligibility to authenticate. Keep secret arguments out of discovery, logs and responses.

For disruptive operations, enforce an operation-specific confirmation bound to the resolved targets and arguments. Do not automatically retry spawning, teleporting, moderation, castle changes or shutdown after an uncertain response. Return authoritative completion or unknown status and a reconciliation path. Native operations without a reliable completion signal remain partial. Commands reported to loop, crash or corrupt state stay excluded until independently qualified; a generic executor must not bypass that exclusion.

### Command acceptance — not run

Test runtime discovery and representative player, admin and operator actions through their actual MCPs on the isolated pair without generated code or UI typing. Change available mod commands and permissions, then verify the AI rediscovers the current contract and adapts without a maintained command list. Verify non-admin denial, authentication/deauthentication, revocation after discovery, disabled debug-event policy, malformed arguments, ambiguous/stale targets and unsupported transport. Check real effects and response-loss/retry behavior, restore test state, and repeat relevant disconnect/hot-swap cases. Destructive cases require disposable fixtures and recovery checks. Discovery alone is not proof of successful execution.

## Access and lifecycle contract

Local connections require explicit pairing/consent with revocable credentials scoped to the player endpoint. Server operators can disable or restrict AI operations. Define scopes for permitted queries and mutations; enforce mutation approval requirements in Progenitor where the operation requires them, rather than relying on an AI prompt. Read-only lookups and ordinary permitted personal settings should not introduce repetitive confirmation dialogs.

Container access follows current server/game/mod rules, including ownership, clan sharing and any applicable distance or visibility constraints. Neither a client cache nor an entity ID grants permission. Never return other players' private inventories or undiscovered locations merely because the server can inspect them. Explain that returned game data is made available to the player's selected AI application; do not expose credentials, private logs or unrelated state.

Bound requests, results, pagination and execution time. Recheck authorization and object identity at execution. Mutations carry request identity and authoritative completion state so retries do not duplicate effects; a timeout may mean unknown completion, not rollback. Provide status/reconciliation before retrying an uncertain mutation. Record scoped action outcomes without unnecessary private data.

Disconnect, permission revocation, mod unload and hot swap invalidate affected sessions/capabilities and release subscriptions. Stale requests cannot invoke a replacement mod's callbacks. Re-discovery restores only capabilities still permitted in the new session. Queries/help returned by mods or game content are data, not authority to issue additional actions.

## Acceptance procedure — not run

1. Install the player companion with the client and connect an MCP-capable AI using the documented opt-in flow. Progenitor.Dev is absent. No compiler or source checkout is available.
2. Discover a Sample mod's help, setting, query and action. Change the personal setting and read it back through both MCP and the Mods UI; restore its original value.
3. Ask for an item in authorized test containers. Return correct counts/locations within permitted scope. Exclude an inaccessible player's container. Change access between discovery and execution and verify refusal. Report partial/unavailable state honestly.
4. Invoke a Sample mod's registered function with typed arguments and verify the actual game outcome. No generated code, UI scraping, raw debug command or manual programmer intervention is required. Invalid arguments and unsupported capabilities return useful errors.
5. Verify non-admin denial of an admin-only capability, permitted admin execution and immediate denial after revocation. Verify server opt-out. Do not infer permission merely from a hidden UI control.
6. Retry a mutation, lose its response, disconnect, unload the owning mod and perform a compatible hot swap. Verify no duplicate effect, cross-session authorization, stale callback or stuck input; document uncertain outcomes and recovery.
7. Revoke local AI access and verify the old connection/credentials cannot continue. Re-pair explicitly. Record exact tested builds/sides and original responses externally before marking any scope verified.

Deliver one buildable author example, a player setup guide requiring no coding, and reproducible acceptance cases. Public API/tool names, transport details and packaging remain implementation decisions; examples above are intended behavior, not available commands.
