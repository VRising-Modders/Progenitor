# Chat box improvements

Progenitor improves the game's own chat box on the client. It keeps the game's input line, sending, channel cycling, whispers, mute and profanity filter, and adds behavior around them. It is built into Core. Its one plugin API is [chat links for mods](#chat-links-for-mods); server-side chat sending for plugins is a separate contract: `ctx.Chat` in [api.md](api.md#chat).

Design decisions were made 2026-10-07. The game facts below come from the client's chat code.

## The game's chat box (defaults Progenitor builds on)

| Behavior | Game default |
|---|---|
| Fade | While open and not focused, the box starts fading **14 seconds** after the last message (a constant in the game's code, not a setting), fades over about a second, then closes. |
| Lines shown | At most **50** lines, pooled across all channels. The full message list is kept separately. |
| Channel filter | One channel at a time (`ChatMode` All, Global, Allies, Whispers, Local); no multi-channel filter. |
| Player text | Wrapped in `<noparse>`, so players cannot add formatting; nothing in the game makes a clickable link. |
| Hovering the box | Takes only the camera zoom axis. Mouse buttons reach gameplay: clicking the box attacks or turns the camera. |
| Position | The game never sets the box's position; it only moves it between the HUD and the clan menu. |
| Sender admin flag | Every client knows whether a sender is an admin (it colors admin names). |
| System messages | Server notices, admin announcements and mod command replies all arrive as one type (System). |

## Stage 1: as built

Status: source-checked, built against the installed game, and game-free tested (`ChatWindowLogicTests`, `ProgenitorPageTests`, `ThreeHomesTests`, `UiInputMaskTests`).

**Live (2026-10-07, Steam client on the isolated test server, Core 2026.10.107.1 from commit 00f4e7a2):**
- **Visibility verified live.** The click guard registered with the mask live on join. A server chat line was still fully shown 24 s after it arrived (the game alone fades at 14 s), and the box was closed 31 s after an earlier line, which matches the 30 s setting.
- **Click guard verified live** (same day, with the Dev [`mouse`](bridge-ops-client.md#mouse) op and the server's cast counter for the player): three left clicks on the visible chat log started no attack (+0), three control clicks in the middle of the screen started three (+3), and with **Clicking chat doesn't attack** off three clicks on the chat log started three (+3). The guard also registered again on a second join.
- **Keep chat on screen verified live:** a line was still fully shown 55 s after it arrived.
- **Not yet live-verified:** right click (camera turn) on the box, a drag that starts outside and crosses it, hover keep-up and an in-world Core swap.

Settings: `[Chat]` in `BepInEx/config/progenitor.ui.cfg`, also on Progenitor's page (**Mods → Progenitor → Your settings → Chat**). Each takes effect immediately.

| Setting | Key | Default | What it does |
|---|---|---|---|
| Seconds chat stays on screen | `VisibleSeconds` | 30 | How long the box stays up after the last message before it fades. 14–600; 14 is the game's own. |
| Keep chat on screen | `KeepVisible` | off | Never fade. Escape still closes the box. |
| Keep chat up while the mouse is over it | `StayWhileHovered` | on | The countdown restarts while the pointer rests on the box. |
| Clicking chat doesn't attack | `ClickSafe` | on | A click on the box doesn't attack, turn the camera or use another mouse action. |

**How it works.** `Progenitor.Chat.ChatWindow` runs just before the game's `ClientChatSystem` update and moves the game's `_MessageTime` forward, never backward and never past now (`ChatVisibility`). The click guard is a third Progenitor input context at order 97 (`UiInputCapture.ChatClickGuardOrder`). It is registered once per client world, and it shares the input mask the mod panels use. It takes only gameplay actions bound to a mouse button: attack, camera turn, build place and pick up, plus any eligible action bound to the middle, back or forward button. Keyboard keys, push-to-talk, the map and menus are never taken. A press that starts on the box stays blocked until release; a press that starts elsewhere, such as a camera drag, is never cut off as it crosses the box. If the chat update stops, a frame watchdog drops the guard.

**The wheel over the box scrolls only the chat** (2026-10-17, source-checked and game-free tested, not yet live-verified). The same order-97 context takes the camera zoom axis, and nothing else, while the pointer is over the visible box (log, scroll bar, tab line, grip, input line), whatever **Clicking chat doesn't attack** says; keys, movement and the mouse buttons are unchanged (`ChatMaskPlan`).

**The wheel scrolls the box while it lingers** (2026-10-17, source-checked and game-free tested, not yet live-verified). After the input line closes, the box stays up for reading, and in that state the game's UI does not deliver the wheel to the log. Progenitor then moves the log itself, two rows a notch, the same step as its panel lists. While the input line is open the game scrolls the log and Progenitor writes nothing. A notch over the shown box also restarts the fade countdown, whether or not **Keep chat up while the mouse is over it** is on (`ChatWheelScroll`).

**Where clicks are taken.** Over the message log (the same area the game's own hover test uses), over its scroll bar, which sits just outside the log's left edge, and over the input line while it shows, and only while the box is on screen: not after it fades, and not while the HUD is hidden. The area stays for as long as the box is shown, so a longer visibility time also keeps it longer. The guard is armed only when Core's UI module is installed, because that module's teardown releases the input mask it shares.

**Limitations.**
- The left and right buttons are taken by action, because the game reports their bindings only as display text. A player who moved attack to a keyboard key loses that key while the pointer rests on the chat box.
- A mouse binding with a modifier (Shift+Mouse 4) is not taken.
- Any new message restarts the countdown, including one the game's channel filter hides.

### Live acceptance (steps 1, 3 left click and 5 passed; the rest not run)

Steam client in a world on the isolated test server, with the client free for a short foreground test:

1. Receive a message. The box stays up for about 30 s instead of 14, then fades. With **Keep chat on screen** on, it stays until Escape.
2. Rest the pointer on the box past the timeout: it stays up, and fades `VisibleSeconds` after the pointer leaves.
3. Left-click and right-click the box while it shows, both focused and unfocused: no attack and no camera turn. Scrolling still scrolls; typing and sending still work.
4. Start a right-drag outside the box and cross it: the camera keeps turning. Hold attack and cross the box: the attack continues.
5. Turn **Clicking chat doesn't attack** off: clicks reach the game again. Leave the world and rejoin: the guard registers again (one `Chat: click guard registered` line per world).
6. Hot-swap Core while in a world: no stuck input, and the log shows the guard removed and registered again.
7. Scroll the wheel with the pointer over the log, the scroll bar and the tab line: the chat scrolls and the camera does not zoom, with **Clicking chat doesn't attack** on and off. Move the pointer off the box (or let it fade) and scroll: the camera zooms. While over the box, WASD still walks.
8. Send a line so the input closes and the box lingers. With the pointer over the log, scroll up and down: the log moves two rows a notch and the box stays up while you keep scrolling. Open the input line and scroll: the log moves by the game's own step, not twice.

## Chat commands: as built (CHAT2)

Status: source-checked, built against the installed game, and game-free tested (`ChatCommandLogicTests`, `KeybindTabTests` chat-scope cases, `ProgenitorPageTests`). **Verified live 2026-10-07** on the Steam client in the test server, with chat opened by the Dev `key` op and the line set in the field: `/r hello` became `/reply hello`; `/w` plus the start of the test character's name and Tab completed the name with the channel unchanged; `/w zz` plus Tab (no match) left the line and cycled the channel as the game does. Not live-verified: completing another player's name (only one player was online), and remapping the key off Tab.

**What the game already has.** `/w name message` and `/whisper name message` (the name must match exactly, otherwise "No vampire with that name found"), `/reply message`, the channel switches `/local`, `/global`, `/allies`, `/a`, `/team`, `/clan` and `/system`, and the clan, kill, stats and pvp commands. It has no `/r` and no name completion.

| Addition | How it behaves | Setting |
|---|---|---|
| `/r` | Typing `/r ` at the start of a chat line continues your latest whisper conversation, in either direction: it becomes `/w <partner> `, where the partner is the sender of the last whisper you received or the player you last whispered (yourself included). With no whisper yet it becomes the game's `/reply `, which says "There are no whispers to reply!". The game's own `/reply` answers only whispers received, and it keeps no record of whom you whispered (its whisper target is cleared after sending, and a sent whisper's entry names you), so Progenitor remembers the target you typed (`/w name`) or the whisper mode's target when your sent whisper appears. | **/r replies to the last whisper** (`ReplyShortcut`, on) |
| Name completion | While the line is `/w ` or `/whisper ` followed by part of a name and nothing after it, **Tab** fills in the first online player whose name starts with it (ignoring case), then a space. Pressing Tab again on that line cycles through the other matches. Typing anything else starts over. While typing the name, up to three matching online players show above the line (your own name included, so a whisper to yourself completes too), the first marked with the key; with no match it says no player online has that name. For testing alone, `[Chat] TestNames` (cfg only, empty by default) adds names the suggestions offer as if online; they are not players. | **Tab completes names after /w** (`NameCompletion`, on) |

**The completion key** is the Core keybind `ChatCompleteName` (default Tab), listed under Keys and in the Keybinds tab, where it can be remapped. It is a chat-scope bind: it works only while typing in chat and is the only kind of bind that runs then. The game also uses Tab in chat to cycle the channel, and it reads the Tab key itself rather than through a remappable action, so nothing can stop the cycle before it happens. While a Tab press completes a name, Progenitor puts the channel (and its placeholder) back right after the game's chat update, so one press does one thing. Elsewhere Tab cycles the channel as before; with the completion key moved off Tab, Tab only cycles.

**Names** come from the client world's connected `User` entities. Your own name is not filtered out: finding the local user would need a new generic instantiation over a game type, and whispering yourself only shows the game's own answer. Whether the client holds `User` entities for players outside its view is not yet checked live.

## Command hints (data)

Status: source-checked, built against the installed game, and game-free tested (`ChatCommandHintsTests`). **Not live-verified.** This is the data behind the hint line that lists the server's `.` commands while a player types one; drawing that line is separate (`ChatWindow`).

**What a player gets.** The commands that player can run on the joined server, at most 200, sorted by text:

| Field | Content |
|---|---|
| Command | The exact text to type, prefix included. The root shortcut when the routing table publishes one (`.help`, `.army summon`, a renamed shortcut under its new name); otherwise the qualified form (`.p <owner> [group] <name>`), as for a shortcut an administrator disabled or one two mods both claim ([command routing](roadmap.md)). |
| Usage | The usage of every form this player may run, joined with ` \| `: the author's `usage` when set, else the command and its parameters (`<required>`, `[optional]`, `<rest...>`). At most 160 characters. |
| Help | The command's description, on one line, at most 120 characters (a clipped line ends in `...`). |

A command whose every form is `adminOnly` is not listed for a player who is not an admin, and an admin-only form of a mixed command adds neither its usage nor its help. The admin test is the one an `adminOnly` command applies (`User.IsAdmin`, or the player hosting the game).

**Channel.** Core-reserved event id **`'PrCh'`** (`CustomEventPolicy.ChatCommandHintsEventId` = `0x50724368`), both directions, with a version byte and a kind byte (`Chat/ChatCommandHintsWire.cs`):

1. The client declares `Handshake.Capability.CommandHints` (1024) in its handshake answer.
2. After an ok handshake the server sends that client a **hello** carrying a stamp (the command registry's generation). Only a client that declared the bit gets it, and a client never sends on `'PrCh'` before a hello, so neither side sends the id to a peer without a handler. A server without Progenitor, or with an older Progenitor, never sends a hello: the client has no hints.
3. The first time the chat line needs hints (`ChatCommandHints.RequestIfNeeded`), the client sends a **request** with a nonce. Requests are at least 5 s apart, time out after 10 s, and stop after 3 unanswered tries until the next hello.
4. The server re-reads the sender's `User`, requires its handshake to be ok, answers at most one request per player every 2 s, and sends the catalog to that player only as **pages** of at most 12 KiB each (the transport limit is 15 KiB). The client keeps a catalog only when every page of its own request has arrived.
5. When the server's command list changes (a plugin loads or unloads, routing settings are applied), the server waits until it has been stable for 1 s and sends a new hello to every capable client; the client's next `RequestIfNeeded` asks again and keeps the old catalog until the new one is complete. A server Core reload forgets which clients were sent a hello, so those clients learn of later changes on their next connection.

The client forgets the catalog when the connection ends (`Handshake.ServerContextCleared`) and when Core unloads.

**Client API** (internal, `Chat/ChatCommandHints.cs`): `RequestIfNeeded()`, cheap enough to call every frame while the line starts with the prefix; `Available`, true once a catalog is held; `Match(typed, max)`, which matches the typed line (leading spaces dropped, repeated spaces read as one) case-insensitively against each command's text. A command matches when its text starts with the line, or when the line is that command followed by a space and arguments. Commands the line already names come first (the longest first), then the rest, shortest first, then alphabetically.

**Acceptance procedure — not run** (isolated test server, Steam client):
1. Join as a player who is not an admin. Type `.` in chat: the client sends one `'PrCh'` request and holds a catalog with no admin-only commands. Typing more does not send another request.
2. Join as an admin: admin-only commands such as `.stop-server` are in the catalog.
3. Hot-load a sample with commands on the server: within a few seconds the client is sent a hello, and its next request lists the sample's commands. Remove the sample: they disappear the same way.
4. Join a server without Progenitor: no `'PrCh'` packet is sent and there are no hints.

## Settings on both sides

Every chat feature ships with its settings in the same change (decided 2026-10-07). Each feature has a player setting, and a server setting where the server has a say.

- **Player (client):** Progenitor's page, **Your settings → Chat**, stored in `[Chat]` of `progenitor.ui.cfg`. Personal look and behavior that affects only this player.
- **Server:** a **Chat** group in the **Server** tab, server-wide and edited by admins through the existing admin edit path. It holds what the server decides for everyone: whether a feature is allowed, limits, and moderation. A server setting can turn a feature off for everyone; it never changes a player's personal look. When a server has no Progenitor, the player settings still apply to features that work client-only, and server-dependent features show why they are unavailable.

| Feature | Player setting (Your settings → Chat) | Server setting (Server tab → Chat) | Stage |
|---|---|---|---|
| Visibility | Seconds on screen, keep on screen, keep up while hovered | none | 1 (built) |
| Click-safe chat box | Clicking chat doesn't attack | none | 1 (built) |
| `/r` and name completion | `/r` replies to the last whisper; Tab completes names after `/w` (the key is under Keys) | none | CHAT2 (built) |
| Chat view (tabs, history, links, mentions) | Progenitor's chat lines (on); text size | none | CHAT5 (built) |
| Tabs and window | Tabs (from the tab line), lock position, background opacity; position and size by dragging | none | CHAT6 (built) |
| Web links | Confirm before opening (always) | **Allow clickable links** | CHAT5 (built) |
| Mod links | Share / Copy link on mod rows and pages | **Allow mod links** | CHAT4 (built) |
| Mentions | Highlight mentions, play a sound, watched words | **Allow @all from admins** | CHAT5 (built) |
| History | Keep chat between sessions, lines kept per server | none | CHAT5 (built) |
| Repeat counts, timestamps | One toggle each | none | CHAT5 (built) |
| Recall, copy, mute | Up and Down bring back sent lines; muted players (right-click a line) | none | CHAT7 (built) |
| Location and item links | none (`/loc`, `/item`) | none | CHAT8 (built) |
| Long messages | Send long lines in parts | **Allow long messages**, **Longest long message** | CHAT9 (built) |
| Command hints | Show command hints after "." | none | CHAT10 (built) |
| Mod suggestions | Suggest mods after "[mod:" | **Allow mod links** | CHAT11 (built) |
| Word filter, Discord relay | none | **Word filter**, **Filtered words**, **When a word matches**; **Discord relay**, **Discord webhook address**, one switch per relayed channel | built (server) |

### Server settings (as built)

Status: source-checked, built against the installed game, and game-free tested (`ChatWordFilterTests`, `ChatRelayTests`, `ServerChatPolicyTests`, `ServerChatOptionsTests`, and the Server tab cases in `ThreeHomesTests` and `ServerSettingsStage5Tests`). **Not live-verified**: nothing below has run in the game yet.

The **Chat** group of the **Server** tab follows **Server rules**: like the rules, it is what the server decides for every player, ahead of the two admin-only groups (Players without Progenitor, Operator). Its heading is **Chat on this server** and the left list names it **Chat**: Progenitor's page and the Server tab are split by group title, and **Chat** is already Your settings' group.

| Row | Default | Stored in | Who sees it |
|---|---|---|---|
| Allow clickable links | on | `progenitor.serverchat.cfg` `[ServerChat] AllowLinks` | every player (synced) |
| Allow mod links | on | `[ServerChat] AllowModLinks` | every player (synced) |
| Allow @all from admins | on | `[ServerChat] AllowAtAll` | every player (synced) |
| Allow long messages | on | `[ServerChat] AllowLongMessages` | every player (synced) |
| Longest long message | 1000 characters (200 to 4000; a value outside is clamped) | `[ServerChat] MaxLongMessageChars` | every player (synced) |
| Word filter | off | the server's `progenitor.cfg` `[ChatFilter] Enabled` | admins only |
| Filtered words | empty | `[ChatFilter] Words` (comma separated, at most 2000 characters, 200 words of up to 64 characters) | admins only |
| When a word matches | Replace with \*\*\* | `[ChatFilter] Action` (`replace` or `block`) | admins only |
| Discord relay | off | `[ChatRelay] Enabled` | admins only |
| Discord webhook address | empty | `[ChatRelay] WebhookUrl` | nobody: the row reads **Set** or **Not set** |
| Relay Global chat, Relay System lines | on | `[ChatRelay] Global`, `System` | admins only |
| Relay Local chat, Relay clan chat, Relay whispers | off | `[ChatRelay] Local`, `Clan`, `Whispers` | admins only |

**How the policy reaches players.** The five synced rows are ConfigSync rows of Progenitor (`progenitor/ServerChat/<key>`), sent to every client that answered the handshake and edited by admins through the server-wide edit path (authorization, rate limit, audit, lock list). A client fills `ChatPolicy` from them twice a second while ConfigSync holds the server's values, and goes back to `ChatPolicy.Defaults` (everything allowed, 1000 characters) when it leaves the server, when the server has no Progenitor or an older one, and when Core unloads. The filter and relay rows travel only on the admin channel to admins of the joined server; the word list is never sent to other players, and the webhook address is never sent at all.

**Word filter.** It runs on the server in Core's chat hook, after every `ChatMessage` subscriber (a plugin sees the line as typed, and a line a command cancelled is not filtered). Matching ignores case and takes whole words or phrases only: a match may not touch a letter, digit or `_` on either side, so `ass` does not match `class`.
- **Replace with \*\*\*** writes the masked text into the game's own chat event before the game picks the recipients, so Global, Local (48 m), clan, whisper and the game's mute rules stay the game's own. For Global, Local, clan and whisper lines the game never sends a line back to its speaker, so the speaker's own chat still shows what they typed (an admin's System line does come back, masked). The write is the same typed component write the Dev bridge's `command` op makes; it is not yet verified live in the chat hook.
- **Block the message** drops the line, and only its sender gets the System line `Your message was not sent: it contains a word this server filters.`
- The game's own profanity filter on each player's client is separate and unchanged.

**Discord relay.** With the relay on and a valid address, each line that goes out on a ticked channel is posted as `Name: message` (Global) or `[Local] Name: message`, `[Clan] …`, `[Whisper] …`, `[System] …`; a server-wide System notice from Progenitor or a mod (`Chat.Broadcast` without a speaker) is posted as `[System] message`. Lines starting with the command prefix, blocked lines and replies sent to one player are never posted. A filtered line is posted masked.
- Every `@` gets a zero-width space after it and the body sets `allowed_mentions` to none, so nothing pings `@everyone`, `@here`, a user or a role. A line is cut to Discord's 2000 characters.
- At most one request is in flight and at most one starts per second; waiting lines go out together, up to 2000 characters per post. The queue holds 50 lines and drops the oldest. A `429` answer puts its lines back and waits its retry time (at most 60 s); any other failure drops them.
- Posting runs on a background task over `HttpClient` (10 s timeout, no redirects), never on the game thread. Turning the relay off, clearing the address or unloading Core cancels it and empties the queue.
- The relay sees a line before the game's own checks, so a line the game then drops (a muted sender, Global chat turned off, a line over the game's 509-byte limit) is still posted.
- Failures and dropped lines are logged at most once a minute each, without the address. The address must be `https` on `discord.com` or `discordapp.com` (or their `ptb.`/`canary.` hosts) under `/api/webhooks/`; the admin channel, its audit and every log carry only `set` or empty.

**Locking.** The lock list (`[ServerSettings] LockedInGame`) takes the group word `chat` for the whole group, or a row's id as `settings_places` lists it.

**Acceptance procedure — not run** (isolated test server, Steam client as an admin, a second player or a ghost user):
1. Server tab → Chat: an admin sees all fifteen rows; a player who is not an admin sees the five synced rows as `Admin only · <value>` and none of the others.
2. Turn **Allow clickable links** off: the client log shows `Chat: policy from the server … links off`. Leave the server: `Chat: policy defaults (left the server)`.
3. Word filter on, words `darn`, Replace: a line `darn it` from one player reaches the other as `*** it`. Switch to Block: the line reaches nobody and its sender gets the notice.
4. Relay on with a test webhook: Global lines arrive in Discord as `Name: message`, `@everyone` does not ping, a burst of 60 lines arrives in at most one post a second with the oldest dropped, and the server log never shows the address.

## Chat view: as built (CHAT4 to CHAT9)

Status: source-checked, built against the installed game, and game-free tested (`ChatLogTests`, `ChatRecallTests`, `ChatTokenTests`, `ModLinkTests`, `ProgenitorPageTests`). Live results are listed per feature; anything not listed there is **not live-verified**.

**How it works.** The game draws at most 50 pooled lines across all channels. Progenitor keeps its own log per server (`progenitor-cache/chat/<server>.jsonl`, `[Chat] HistoryLines`, default 500) and draws the active tab's newest 120 lines. It clones the game's content node (layout and all) under the chat log's scroll view, fills it with lines made from the game's own line prefab, so font, size and wrapping match, points the scroll view at it and hides the game's node; the game keeps writing its own lines, unchanged and unseen. Turning **Progenitor's chat lines** off, Core unloading or leaving the world puts the game's view back. The window's fade and placement stay the game's.

**New lines.** The game's message list (`ClientChatSystem._ChatMessages`) holds at most 50 entries and drops the oldest for each new one, so once it is full its count no longer changes. Each frame the view lines its last read up with the current list and takes every entry after the part already seen (`ChatNewEntries`), so a burst of lines in one frame arrives whole and in order. Until 2026-10-17 a full list made the view take only the newest entry: a mod that sent a header and nine lines to one player from one event handler (all arriving in the same frame) showed only the last line, while the same lines sent before the list filled all showed. Mods never need to spread their sends over frames. Server side, `Chat.SendTo` and `Chat.Broadcast` create the event entities at once with the `EntityManager` (no command buffer, no per-frame merging), so several sends in one handler all go out. Status: game-free tested (`ChatNewEntriesTests`); not live-verified. Live check: fill a Progenitor client's chat past 50 lines, then send `broadcast {text, count: 10}` on the server bridge; all ten lines must show.

| Feature | How it behaves | Live (2026-10-07) |
|---|---|---|
| Lines | Each line keeps the game's channel tag and colored name; the message is redrawn with every character escaped, so players still cannot add formatting. | Verified: lines match the game's look. |
| Tabs | A row above the log in the look of the game's menu tabs ([tab row](#tab-row-look-and-reordering-chat12)): **All**, the player's tabs (default **Clan**), **+** and **≡**. Left-click a tab to show it (single-channel tabs also switch the send channel); drag a tab sideways to move it; right-click a tab or click **≡** for the quick menu. A tab with unread lines gets the game's new-unlock star. Six tabs at most. | Verified (CHAT6 text row): clicking **Clan** and **All** switched the view. The CHAT12 row is not live-verified. |
| Quick menu | Edit this tab (one dialog: the name and a "Show messages from" check box per channel, Save and Cancel; [tab editor](#tab-editor-formdialog1)), Move left, Move right (CHAT12), Close this tab, New tab (presets Whispers, System and mods, Mentions, Clan, Local, or Custom), Colors (a panel directly above the chat box, design approved 2026-10-07: one row per channel, Global, Local, Clan, Whispers, System and mods, each with a sample line in its color now and swatches, the game's color then ten colors; a click recolors the sample, the chat and the line you type in that channel at once and is saved at once; the chosen swatch is outlined and the hovered one is named in the title; Use the game's colors resets all; Done or a click outside closes it; the chat stays shown while it is open. `[Chat] Colors`, e.g. `Global=#7AB8F5;Clan=#8ED86E`. Earlier builds used two dialogs, which never opened, then a line that cycled colors on click, which was rejected), Background opacity, Lock / Unlock position, Reset position and size, All chat settings (opens the Mods menu through the game's menu, waits for it, then Progenitor's page at Chat; opening it directly left the player on the game's spellbook page, user report 2026-10-07). | Not checked. |
| Move and resize | Drag the tab row (not a tab, + or ≡) to move the box, the ◥ corner (top right) to resize it, up to the top and right edges of the screen; a move keeps the whole box, from the tab line to the game's channel hint under the input, on screen; Lock stops both. Resizing changes the messages area (the game's fixed 512 x 212 `TextArea`) and grows the window by the same amounts, so the input line keeps its width. Stored in `[Chat] OffsetX`, `OffsetY`, and `Width`, `Height` (the messages area; 0 is the game's size). | First build resized only the window (user report 2026-10-07); fixed, not checked live. |
| History | Each server's log comes back when you rejoin, dimmed, with `— Joined <server> · 13:23 —` and `— Left the server · 13:28 —` dividers. | Verified: 8 lines restored after a rejoin, both dividers drawn. |
| Repeat counts | The same line again within a minute raises a `×3` count. | Verified. |
| Timestamps | `[16:05]` before each line; skipped when the game's own timestamp option is on. | Verified with the game's option on (no double time). |
| Web links | http(s), `www.` and `host.tld/path` become links (the join message's rules, `UiLinks.Find`). A click asks **Open this link?** (Open in browser, Copy link, Cancel); nothing opens without it. Off when the server turns clickable links off. | Verified: a click opened the dialog. |
| Mod links | `[mod:Namespace-Name]` or `[mod:Namespace-Name@1.2.3]` becomes a link; a click opens the mod's installed page or its Install Mods listing and never installs anything. **Share** on Mods and Install Mods rows (on hover) and **Copy link** on the mod page and the Install drawer copy the token to the clipboard and say so in a notice; the Mods menu stays open (2026-10-07: closing it to fill the chat input left the player on the game's System menu). | Drawn as a link: verified. Click and copy: not checked. |
| Mentions | A line with your name (learned from your own lines, or the only connected player), a watched word, or `@all` from an admin (the sender's replicated admin flag; when the server allows it) is highlighted, and a short chirp (the game's positive UI sound, `UI_Generic_Feedback_Positive`) plays once. Your own line that matches is highlighted and chirps too, so the sender sees and hears the mention work (requested 2026-10-07). | Own-line highlight: fixed after a user report (2026-10-07), not checked live. Sound: needs a second player. |
| Copy, whisper, mute | Right-click a player's line: Copy line, Whisper them (puts `/w name ` in the input), Mute or Unmute them (their lines stay out of Progenitor's view; `[Chat] MutedPlayers`). Right-click a system line copies it. A copy shows "Copied the line" in the input line's placeholder for 2 s and adds nothing to the log (the first build added a log line per copy; user report 2026-10-07). | Not checked. |
| Recall | While typing, Up brings back the lines you sent (commands too) and Down goes forward again (`ChatRecallPrevious` / `ChatRecallNext`, chat-scope keys). Kept in `progenitor-cache/chat/sent.txt`. A line counts as sent when the chat closes with its input emptied. | Not checked: the Dev `key` op cannot submit the game's input. |
| Location links | `/loc ` at the start of a line becomes `[loc:x,z]` at your position; it draws as `[x, z]` and a click says how far and in which direction it is. | Not checked. |
| Item links | `[item:id]` draws as the item's name. Resting the pointer on it shows the item's name and description from the game (`ManagedItemData`) in Progenitor's tooltip box; a click opens a notice with the same text (name as the title, description as the body; a description's `{0}` placeholders, which the game fills from an item in a slot, show as `…`). For an id the game does not know, the line shows the token as written and the tooltip and notice read "Unknown item" (the raw id is not shown there). A name the game cannot give yet is asked for again (after 1 s, the wait doubling to 30 s) and the lines redraw when it arrives: history is restored as the chat box appears, which can be before the game's item data loads, and until 2026-10-17 a first miss was kept for the whole session, so restored lines showed `[item:id]` while new lines showed names. The history file keeps the token as written, never the drawn name. The game's own item tooltip is not used: Progenitor shows no game item tooltip anywhere yet, and that one is built from an item in an inventory slot. A mod can take the click first ([chat links for mods](#chat-links-for-mods)). `/item <prefab name> ` at the start of a line becomes the token (there is no completion over all items: listing them would need an unproven native enumeration). | Not checked (hover and the new dialog not live-verified). |
| Long messages | A line too long for one chat line (480 bytes) goes out in parts on Enter (`ChatSendLong`, a chat-scope key beside the game's Enter): `… ` at the end of a part, `↪ ` at the start of the next. Progenitor clients join the parts into one line; other players read the parts. The input takes up to the server's limit (default 1000 characters). If the game handles Enter first, the line goes out cut as before; it is never sent twice. | Not checked. |
| Channel color | The line you type takes its channel's color (the game's Global, Clan, Whisper, Local and System colors), so the channel shows while typing, not only in the empty line's placeholder (`[Chat] ChannelColor`). | Not checked. |
| Command hints | While a line starts with `.`, up to three of the server's matching commands show above the input (command, usage, help), from the [command hints data](#command-hints-data); Tab completes the first and cycles on the next press, undoing the game's Tab channel cycle as for names. Only commands this player can run; none on a server without Progenitor (`[Chat] CommandHints`). | Not checked. |
| Mod suggestions | Type `[mod:` and part of a mod's name (or its Thunderstore package): up to three matching mods show above the line, name and package, installed ones marked; names that start with the text first, then installed mods. **Tab** replaces the unfinished token with a finished link (`[mod:Namespace-Name] `) and cycles on the next press, undoing the game's Tab channel cycle as for names. Installed mods with a Thunderstore package always; Thunderstore listings once the Mods menu has loaded its list in this session (chat never fetches; with no match the panel says so and, when the list is not loaded, to open the Mods menu once). Off when the server turns mod links off (`[Chat] ModSuggestions`, page row **Suggest mods after "[mod:"**). | Not checked. |
| Text size, opacity | `[Chat] FontSize` (0 keeps the game's) and `[Chat] BackgroundOpacity` (-1 keeps the game's). The game's line prefab resets a new line's size when it first shows, so a chosen size is re-applied each frame. | Size on new lines: fixed after a user report (2026-10-07), not checked live. |

**Restored lines** are drawn by the same formatter as new ones, from the saved text with its tokens. What they need from the game or from mods is looked at again on every redraw: item names (above), mod-owned links (a handler registering or leaving redraws the lines, `ChatLinkRegistry.Version`), web and mod links (the server's policy is part of the redraw key). Location tokens need nothing from the game. Status of the item-name retry: game-free tested (`ChatRestoredLineTests`), built; not live-verified.

### Tab row: look and reordering (CHAT12)

Status: source-checked, built against the installed game, and game-free tested (`ChatTabRowTests`, `ChatLogTests`). **Not live-verified.**

**Look.** The row borrows the game's full-screen menu top bar (EMOTES, VAMPIRE POWERS, SPELLBOOK...), whose entries draw a caption, a soft glow behind it (`Glow04`) and an underline (`OuterTab_UnderLine`), tinted per state. The row reads that art and never changes it (`Chat/ChatTabArt.cs`): the open menu's top tabs first, then a top tab entry already loaded in memory, else the kit look. The three sprites are held and copied into textures Progenitor owns (the kit's owned-copy store), because a world load can release a game texture that is only referenced; the font's atlas texture is checked every 30 frames, and every chat window attach (a new world) looks for the art again. The caption uses the menu's font, style and letter spacing at the chat's line size (14 to 21; smaller once if the row is wider than the box). A glyph the menu font lacks (**≡**) falls back to the chat's own font.

| State | Caption | Glow | Underline |
|---|---|---|---|
| Normal | the game's rest gray (0.604) | none | none |
| Hover | part way to white (the game's hover tint when it is below the active look) | about half | about half |
| Active (the tab shown) | white | the game's selected glow (0.5) | full |
| Dragged | as active, slightly see-through | as active | as active |
| Unread | the game's new-unlock star (`Stunlock_Icon_NewStar`) at the tab's top right, on any tab but the shown one | | |

**+** and **≡** hover the same way and are never active. Active always reads stronger than hover, and hover than normal, whatever the donor's tints are (`ChatTabRow.Style`). Without the game's art (kit look) the states keep the same order with a flat tint, a drawn 2-unit underline and a gold dot for unread; the row switches to the game's art when it is found, for example the first time the player opens the menu.

**Reordering.** Press a tab and drag it sideways: past 6 canvas units it follows the pointer and a marker (the game's underline stood upright) shows where it will land; release to drop. A press that moves less is a click and shows the tab on release. Every tab can be moved, **All** included (it stays fixed in the sense that it cannot be edited or closed): there is no reason to pin it. Without a drag, **Move left** / **Move right** in the tab's quick menu do the same. The quick menu opens with a right-click or **≡**; the chat window has no keyboard or controller navigation, so there is no controller way to reorder yet. The shown tab and the unread marks move with their tabs. Drag uses the same pointer reads as moving the box; the Lock setting stops moving and resizing the box, not reordering.

**Saved order.** `[Chat] Tabs` keeps the tabs in row order. All is written only when it is not first, as a bare `All` entry (`Clan=4;All;Trade=1`). A config without it (saved before CHAT12) loads with All first; builds before CHAT12 skip the bare entry and also put All first. A new tab is added at the end. `[Chat] ActiveTab` follows the shown tab.

**Live check (not run).** Steam client in a world, chat shown, with the Dev bridge (`chat_tabs`, `mouse`; see [bridge-ops-client.md](bridge-ops-client.md#chat_tabs)):
1. `chat_tabs read`: `art` is `menu` or `resident` (else `kit`; open the game's menu once and read again), and each item's rect.
2. Capture the row at rest: the shown tab white with glow and underline, the others gray.
3. `mouse move` to another tab's `centerX`/`centerY`: its `state` is `Hover`; capture. Move to **+** and **≡**: both hover.
4. Send a line to a channel only another tab shows: that tab's `unread` is true and its star shows.
5. `mouse drag` from a tab's center to the right past the next tab: `saved` and the item order change, the shown tab stays shown; mid-drag captures show the tab following the pointer and the marker. A `mouse click` on a tab selects it without moving it.
6. `chat_tabs move {from,to}` and the quick menu's Move left / right do the same; rejoin: the order is kept and `art` is found again.
7. Before the chat has updated once in a session (`loaded false`), `chat_tabs move` replies `moved false` and `[Chat] Tabs` is unchanged.

**Another chat mod.** Progenitor's chat view steps aside for a mod that replaces the game's chat window; visibility, the click guard and the chat commands still work on the window the game has. Progenitor names no chat mod itself. Two signals turn the view off, either is enough (`ChatReplacement` in `Chat/ChatWindowLogic.cs`, unit-tested):

- **Configured.** `[Chat] ReplacedBy` (client, `progenitor.ui.cfg`, default empty) lists plugin GUIDs or names separated by commas or semicolons (at most 20). A loaded plugin whose GUID or name equals an entry, ignoring case, turns the view off; the setting is read again when it changes. Log: `Chat: <name> replaces the chat window ([Chat] ReplacedBy); Progenitor's chat view stays off.`
- **Observed.** When the view is about to attach and the chat window's scroll area no longer shows the game's own line node (another mod has pointed it at its own content), the view stays off for the session. Log: `Chat: another mod drives the chat window's lines; Progenitor's chat view stays off.`

Not checked live: neither signal has been exercised with a chat mod installed. A chat mod that draws its own window without touching the game's scroll area is not observed; list it in `[Chat] ReplacedBy`.

**Clicks and focus.** The chat view reads the pointer whatever the window focus (`TrySamplePointerScreenAnyFocus`): a window in the background gets no presses anyway, and the Dev `mouse` op can then test it. The click guard covers the log, its scroll bar, the tab line and the grip.

### Tab editor (FORMDIALOG1)

Status: source-checked, built against the installed game, and game-free tested (`ChatTabFormTests`, `DialogTests` `A_form_*`). **Not live-verified.**

**Edit this tab** and **New tab › Custom…** open one dialog, **Edit tab** or **New tab**: the tab's name (up to 16 characters), then **Show messages from** with one check box per channel (Global, Local, Clan, Whispers, System and mods, Mentions of me), then **Cancel** and **Save**. A click anywhere on a channel's line turns it on or off. Save keeps the tab; nothing changes before it. The name is required ("Enter a name." under the field), and at least one channel must be on: with none, Save is grayed and "Turn on at least one channel." shows under the list. Enter saves and Escape cancels; Tab and Shift+Tab move through the name, the channels and the buttons, Down leaves the name for the first channel, Space or Enter on a channel turns it on or off. **All** still cannot be edited or closed, and a new tab still needs a free place in the row (six tabs at most). Until 2026-10-17 the editor was two dialogs: the name with Next, then the channels as a list of buttons ending in Done, which the user found slow and confusing. The dialog is `ChatTabForm` drawn by the framework's form dialog ([ui.md](ui.md#form-dialog-formdialog1)).

**Live check (not run).** Steam client in a world, chat shown, Dev bridge: `chat_tabs edit {"tab":1}` opens the editor of tab 1 (`-1`: a new custom tab) and replies with the dialog readback (`dialog.checks`, `mainEnabled`). Capture it; `mods_ui widget_click label=Local` flips Local, `mods_ui dialog_type {text}` renames, `mods_ui dialog_choose {"choice":"yes"}` saves; `chat_tabs read` shows the new caption and `[Chat] Tabs` the new scope. With every channel off, `mainEnabled` is false and `checksHint` reads the hint.

## Chat links for mods

Status: source-checked, built against the installed game, and game-free tested (`ChatLinkTests`). **Not live-verified.** API rows: [api.md](api.md#chat).

**Click handlers.** On the client, `ctx.Chat.OnLinkClicked(handler)` runs for every link a player clicks in Progenitor's chat view, before Progenitor's own action. The handler gets a `ChatLinkClick`: the kind, the target, the text the line shows, who wrote the line (`Source`, `Sender`, `FromSelf`), whether the line was restored from an earlier session (`IsRestored`), the channel and the line's text. Returning true marks the click handled: later handlers and Progenitor's dialog are skipped.

| `ChatLinkKind` | Written as | Target | Progenitor's own action |
|---|---|---|---|
| `Web` | `https://…`, `www.…` | the URL | asks before opening the browser |
| `Package` | `[mod:Namespace-Name@1.2.3]`: a Thunderstore package that players share | the package, `@version` optional | opens the mod in the Mods menu |
| `Location` | `[loc:x,z]` | `x,z` | says how far and which way |
| `Item` | `[item:id]` | the prefab id (`ItemPrefab`) | the item's name and description |
| `Mod` | `[mod:owner:type:payload\|text]`: a link one mod sends and handles, with its own data | the payload (`Owner`, `LinkType`, `Payload`) | none: only its owner's handler acts |

The two `[mod:` forms never overlap: a package link has no `:` after `mod:`.

**Mod links.** A mod writes a mod link into a server line with `ChatLinks.ModLink(owner, type, payload, text)`, which escapes `\`, `|` and `]` (written `\\`, `\|`, `\]`) and checks the limits. On a Progenitor client:

| Line from | A mod on this client handles the owner (`ctx.Chat.OnModLink`) | No mod handles it |
|---|---|---|
| The server | The text, as a link; the click goes to that handler first | The text only, not a link, without the raw token |
| A player | As written, unless the handler accepts player links (`acceptPlayerLinks: true`): then a link, with `Source = Player` | As written |

A client without Progenitor shows the token as written. Example: `  5,000 [item:862477668] → ` followed by `ChatLinks.ModLink("satisvampory", "chest", guid, "Storage (Plot 1)")` reads `5,000 [<item name>] → Storage (Plot 1)`, with *Storage (Plot 1)* a link for players who have the mod. The shown text never carries control or Unicode format characters (bidirectional overrides such as U+202E included): the builder and the client both drop them, so a link cannot show reordered text.

**Order.** A mod link goes to its owner's handler first; then every `OnLinkClicked` handler, in registration order (plugin load order); the first that returns true ends it. A throwing handler counts as not handled and is logged once. A mod link nobody handled does nothing more. One plugin owns an owner name: another plugin's `OnModLink` for it is refused with a logged warning and gets a handle that does nothing; the same plugin claiming it again (a hot reload) replaces its handler. Every handler is tracked by the plugin's context and removed on unload, and Core's unload forgets them all; the chat redraws so links of an owner that went away become plain text.

### Who wrote a line

A line is the **server's** (`ChatLinkSource.Server`) only when the game's chat event is a System or lore line with an empty sender network id (`ChatEntry.From`). Everything else is a **player's**, admins and your own lines included. This never looks at the text:

- The client cannot set the type or the sender of the line other players receive: it sends `ChatMessageEvent`, and the server builds each recipient's `ChatMessageServerEvent`, filling the sender from the user the message came from.
- The game forwards a player's System line only from an admin, with that admin as its sender, so it counts as a player's.
- Server notices, admin console announcements, Progenitor's lines and mod replies (`ctx.Chat`, `ctx.Reply`) carry no sender. Lines this client's game adds itself ("No vampire with that name found") have none either.
- `ctx.Chat` sends with a speaker (`Chat.Broadcast(world, text, type, speakerUser)`) count as that player's.
- The history file keeps each line's source; lines saved before it was kept count as a player's.

**What Server means.** `Server` says another player did not type the line. It does not say your mod's server half wrote it: it is the server you joined and any mod running there, and a hostile or careless server can send any owner's links with any payload. Treat the payload as untrusted server input: parse and validate it, act only on what your own mod would send, and never open a URL, a file path or a command taken from it directly.

**Echoes.** A server line that repeats a player's words (an "unknown name 'x'" reply) would carry their tokens as the server's. Pass such text through `ChatLinks.Defuse`. Its rule: every `[` becomes `(` and every `]` becomes `)`, so the text can neither open nor close a token; and text that starts with `mod:`, `item:` or `loc:` gets a space in front, so a `[` the template puts right before it cannot complete one either. Defuse is for text placed beside a template's own brackets; player text meant to go inside a link goes in as `ModLink`'s payload or text, which escapes it. Progenitor's own command replies that echo input (unknown command, unknown command owner) do this. A token must fit in one chat line: a reply longer than a line is split by bytes and may cut it.

**Not live-verified:** that the game leaves `ChatEntry.From` empty (type `None`) for its own System lines and Progenitor's sends, the hover tooltip over a chat line, and the Sample's `.chat link`.

**Acceptance procedure — not run** (Steam client with the Sample on the isolated test server):
1. Type `.chat link hello`: a System reply (after the mod name) "Click this link: hello" with *hello* drawn as a link. Click it: a notice "Payload 1, on a line from the server." and the client log `Sample chat: sample link clicked payload=1 source=Server`.
2. Type `[mod:progenitor.sample:ping:9|fake]` in Global: the line shows the token as written, not a link.
3. Click an item link from `/item Item_Weapon_Sword_T01_Bone `: the Sample logs `link clicked kind=Item … source=Player`, then the notice shows the sword's name and description. Rest the pointer on the link: the tooltip shows the same text and goes when the pointer leaves; scroll the log under the still pointer: the tooltip follows what is under it.
4. Remove the Sample from the client's reload folder: the System reply now shows *hello* as plain text. Remove it from the server too and clean up.

### Testing chat from the bridge

The server bridge's [`command` op](bridge-ops.md#command) types a line as a connected player without OS keystrokes: `{"text":"hello","channel":"global"}`. The game never sends a player's own Global, Local or clan line back to them (their client draws it as they type), so the op also sends the line to the speaker (`echo`, on by default); without that, a line from the only connected player reaches nobody.

Live check, not run (Steam client on the isolated test server, nobody else playing):
1. `command {"text":"hello"}`: the reply has `echoed: true`, and the client's chat shows `hello` as the player's own Local line.
2. `command {"text":"got [item:862477668] at [loc:-1804,-1697]","channel":"global"}`: the line shows the item's name and `[-1804, -1697]` as links.
3. `command {"text":".joins","echo":false}`: only the command's reply shows.
4. Disconnect and rejoin (or swap Core): the restored lines are dimmed and the item link still shows the item's name, not `[item:…]`. If a restored line first shows the token, it changes to the name within a few seconds of the world loading.
