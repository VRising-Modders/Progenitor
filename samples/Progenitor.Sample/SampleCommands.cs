using System;
using ProjectM;
using Progenitor.Commands;
using Progenitor.Events;
using Unity.Entities;

namespace Progenitor.Sample;

/// <summary>
/// Server chat commands that prove the cloned item can be granted and that join counts survive
/// a save. A C# static class: registered with <c>ctx.Commands.Register(typeof(SampleCommands))</c>
/// because static classes cannot be generic type arguments. Methods are invoked with a null target.
///
/// <b>Admin gating.</b> Any command that changes world state a player could not otherwise reach
/// — granting a buff, granting items, spawning a unit, spawning a networked effect entity —
/// carries <c>adminOnly: true</c>. That is the one named argument on <c>[Command]</c>; the dispatcher
/// checks <c>ICommandContext.IsAdmin</c> (the game's own <c>User.IsAdmin</c>) and replies
/// <c>This command is admin-only.</c> with <c>CommandResult.Denied</c> without ever entering the
/// method, and <c>.help</c> hides the command from non-admins. Plugin authors: copy this, do not
/// hand-roll a check inside the body. Before FZ none of the Sample's commands were gated, so any
/// player on a server running the Sample could type <c>.nodrain on</c> (the debug buff, now Core's
/// <c>.debug</c>) and hand themselves
/// invulnerability (R27 finding 3); <c>.ember</c> was left open in that pass and was an
/// unlimited server-side item write driven by chat input (R28 finding 27, fixed in FZ2).
/// </summary>
internal static class SampleCommands
{
    /// <summary>
    /// Ceiling on one <c>.ember</c> grant. The command used to pass whatever the player typed
    /// straight into <c>InventoryUtilitiesServer.TryAddItem</c>, so <c>.ember 2147483647</c> was
    /// a server-side item write sized by chat input (R28 finding 27).
    /// </summary>
    internal const int EmberMaxPerCommand = 50;

    // adminOnly: this writes items into a character's inventory server-side. Ember Shard is a
    // cloned Crystal — a real resource — so an open .ember on any server running the Sample is
    // unlimited item creation for every connected player: clamping the per-call count bounds one
    // invocation but not repetition, and the Sample is the framework's shop window, loaded by
    // operators who did not write it. The clamp below is kept as well so an admin typo cannot
    // ask for int.MaxValue. Note for the live acceptance test (docs/bridge-ops.md): the test user
    // must now be an admin, which the loop already requires for `.debug` / `.rat` / `.beam`.
    [Command("ember", adminOnly: true, usage: ".ember [count]", description: "Admin only. Give yourself Ember Shards (the sample cloned item), up to 50 per call.")]
    public static void Ember(ICommandContext ctx, int count = 1)
    {
        try
        {
            if (count <= 0)
            {
                ctx.Reply("Count must be a positive integer. Usage: .ember [count]");
                return;
            }

            if (count > EmberMaxPerCommand)
            {
                ctx.Reply($"Clamped {count} to the per-command maximum of {EmberMaxPerCommand}.");
                count = EmberMaxPerCommand;
            }

            if (ctx.CharacterEntity == Entity.Null)
            {
                ctx.Reply("No character entity on this user; cannot add items.");
                return;
            }

            var world = ctx.World;
            var em = world.EntityManager;
            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            if (gameData == null)
            {
                ctx.Reply("GameDataSystem is not available yet.");
                return;
            }

            var guid = Plugin.EmberGuid;
            var settings = AddItemSettings.Create(em, gameData.ItemHashLookupMap);
            var response = InventoryUtilitiesServer.TryAddItem(settings, ctx.CharacterEntity, guid, count);
            int max = 0;
            if (gameData.ItemHashLookupMap.TryGetValue(guid, out var item))
                max = item.MaxAmount;
            string msg =
                $"Gave {count} Ember Shard(s): result={response.Result} remaining={response.RemainingAmount} slot={response.Slot} max={max}";
            ctx.Reply(msg);
            Plugin.Logger?.LogInfo($"Sample .ember {ctx.User.CharacterName}: {msg}");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Sample .ember threw: {e}");
            ctx.Reply("Giving Ember Shards failed. See the server log.");
        }
    }

    [Command("joins", description: "Show how many times you (and others) have joined this save.")]
    public static void Joins(ICommandContext ctx)
    {
        try
        {
            var store = Plugin.Store;
            if (store == null)
            {
                ctx.Reply("Save store is not available.");
                return;
            }

            var book = SampleJoins.Load(store);
            string selfId = ctx.User.PlatformId.ToString();
            int self = 0;
            for (int i = 0; i < book.Players.Count; i++)
            {
                if (book.Players[i].PlatformId == selfId)
                {
                    self = book.Players[i].Count;
                    break;
                }
            }

            ctx.Reply($"You have joined this save {self} time(s).");
            if (book.Players.Count == 0)
            {
                ctx.Reply("No joins recorded yet.");
                return;
            }

            ctx.Reply("Recorded joins:");
            for (int i = 0; i < book.Players.Count; i++)
            {
                var row = book.Players[i];
                string label = string.IsNullOrEmpty(row.Name) ? row.PlatformId : row.Name;
                ctx.Reply($"  {label}: {row.Count}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Sample .joins threw: {e}");
            ctx.Reply("Reading join counts failed. See the server log.");
        }
    }

    [Command("ui", usage: ".ui demo|close", description: "Open or close the Sample UI toolkit demo panel on your client.")]
    public static void Ui(ICommandContext ctx, string what = "demo")
    {
        try
        {
            if (!SampleUi.TryParse(what, out var kind))
            {
                ctx.Reply("Usage: .ui demo | close");
                return;
            }

#if !PROGENITOR_TESTS
            SampleUi.HandleCommand(ctx, kind);
#else
            ctx.Reply("Sample ui " + what);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .ui threw: " + e);
            ctx.Reply("Sample ui failed. See the server log.");
        }
    }

    // adminOnly: spawns replicated NetworkedSequence entities (and a marker rat) in the world.
    [Command("beam", adminOnly: true, usage: ".beam [seconds] [red|blue|coil] [count] [steady|blast] | link [seconds] | mark [red|blue] [seconds] | off", description: "Admin only. Server-authored networked beam (game graphic) from you to the nearest rat, or 6 m ahead. .fx beam is the client placeholder.")]
    public static void Beam(ICommandContext ctx, string a = "", string b = "", string c = "")
    {
        try
        {
            SampleBeam.HandleCommand(ctx, a, b, c);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .beam threw: " + e);
            ctx.Reply("Sample beam failed. See the server log.");
        }
    }

    [Command("fx", usage: ".fx image|particle|beam|vignette|off [menu]", description: "Play a client-side Sample effect, or .fx off to clear them. .fx beam is the client LineRenderer placeholder; .beam is the server-authored game graphic.")]
    public static void Fx(ICommandContext ctx, string what = "", string where = "")
    {
        try
        {
            // ".fx beam menu" arrives as two chat tokens; SampleFx parses the joined form.
            if (!string.IsNullOrWhiteSpace(where)) what = what + " " + where;
            if (!SampleFx.TryParse(what, out var kind))
            {
                ctx.Reply("Usage: .fx image | particle | beam | vignette | off");
                return;
            }

            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }

            var packet = new SampleFx.Packet { Value = (int)kind };
            bool sent = plugin.Net.SendToUser(ctx.UserEntity, SampleFx.EventId, packet);
            ctx.Reply(sent
                ? "Sample fx " + what + " sent to your client."
                : "Could not send fx to your client (are you connected?).");
            Plugin.Logger?.LogInfo("Sample: fx server " + what + " sent=" + sent);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .fx threw: " + e);
            ctx.Reply("Sample fx failed. See the server log.");
        }
    }

    // adminOnly: entering the circle spawns a replicated unit.
    [Command("triggers", adminOnly: true, usage: ".triggers [on|off|status]", description: "Admin only. Arm a circle and a floor rectangle where you stand. Entering the circle spawns a rat. Leaving the rectangle logs it. Step out and back in; the first sight does not fire.")]
    public static void Triggers(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleTriggers.TryParse(what, out var kind))
            {
                ctx.Reply(SampleTriggers.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleTriggers.HandleCommand(ctx, kind);
#else
            ctx.Reply(kind == SampleTriggers.Kind.Off ? SampleTriggers.Off() : SampleTriggers.StatusReply());
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .triggers threw: " + e);
            ctx.Reply("Sample triggers failed. See the server log.");
        }
    }

    // adminOnly: spawns and steers replicated units.
    [Command("rat", adminOnly: true, usage: ".rat [distance]|follow|go|patrol|stop|status|off", description: "Admin only. Spawn a rat in front of you, steer the last one, play a behavior-step patrol, or report the unit events heard so far.")]
    public static void Rat(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleUnits.TryParse(what, out var kind, out var distance))
            {
                ctx.Reply("Usage: .rat [distance] | follow | go | patrol | stop | status | off");
                return;
            }

#if !PROGENITOR_TESTS
            SampleUnits.HandleCommand(ctx, kind, distance);
#else
            ctx.Reply(SampleUnits.Run(kind, distance));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .rat threw: " + e);
            ctx.Reply("Sample rat failed. See the server log.");
        }
    }

    // adminOnly: this reads nothing a player could not see by walking their own plot, but it
    // prints entity ids, the owner's platform-facing name, territory bounds and a full building
    // census — server-shaped detail that belongs to an operator, not to chat. Gating a read is
    // cheap; ungating one is a decision nobody revisits.
    [Command("castle", adminOnly: true, usage: ".castle [summary|inside|all|territory <n>|tiles|canplace <prefab>|raw]", description: "Admin only. Read castles: your plot summary, whether you stand inside it, every plot, one territory (with Refresh), your floor tiles, a placement check, or the raw reader rows.")]
    public static void Castle(ICommandContext ctx, [Remainder] string what = "")
    {
        try
        {
            if (!SampleCastle.TryParse(what, out var kind, out int territoryIndex, out string prefab))
            {
                ctx.Reply(SampleCastle.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleCastle.HandleCommand(ctx, kind, territoryIndex, prefab);
#else
            foreach (var line in SampleCastle.Run(default, kind, default, territoryIndex, 0, prefab))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .castle threw: " + e);
            ctx.Reply("Sample castle failed. See the server log.");
        }
    }

    // adminOnly: `.hooks` alone is a read of two counters, but `.hooks skip` sets
    // SystemHookContext.Skip on a live vanilla system - a frame of the server's movement
    // integration does not happen. It is harmless and self-healing (SampleHooks explains which
    // system and why), and it is still not a thing a player may do to everyone else's frame.
    [Command("hooks", adminOnly: true, usage: ".hooks [status] | .hooks skip [1-5]", description: "Admin only. Report the Before/After hook counters on ServerBootstrapSystem and MoveEntitySystem, or skip MoveEntitySystem for a few frames.")]
    public static void Hooks(ICommandContext ctx, string what = "", string frames = "")
    {
        try
        {
            if (!SampleHooks.TryParse(what, frames, out var kind, out int frameCount))
            {
                ctx.Reply(SampleHooks.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleHooks.HandleCommand(ctx, kind, frameCount);
#else
            ctx.Reply(SampleHooks.StatusReply(0, 0, 0, 0, 0));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .hooks threw: " + e);
            ctx.Reply("Sample hooks failed. See the server log.");
        }
    }

    // adminOnly: this puts the caller's text into every connected player's chat as a System
    // message - the message type players read as coming from the server itself. That is an
    // impersonation and spam surface, which is why it is gated even though it writes nothing to
    // the world.
    [Command("shout", adminOnly: true, usage: ".shout <text>", description: "Admin only. Broadcast one System chat line to every connected player through ctx.Chat.Broadcast, and report the recipient count.")]
    public static void Shout(ICommandContext ctx, [Remainder] string text = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                ctx.Reply(SampleChat.ShoutUsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleChat.HandleShout(ctx, text);
#else
            ctx.Reply(SampleChat.ShoutReply(0));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .shout threw: " + e);
            ctx.Reply("Sample shout failed. See the server log.");
        }
    }

    // adminOnly: same reason as .shout, one recipient instead of all of them.
    [Command("whisper", adminOnly: true, usage: ".whisper <name> <text>", description: "Admin only. Send one chat line to a single connected player through ctx.Chat.SendTo.")]
    public static void Whisper(ICommandContext ctx, string name = "", [Remainder] string text = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(text))
            {
                ctx.Reply(SampleChat.WhisperUsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleChat.HandleWhisper(ctx, name, text);
#else
            ctx.Reply(SampleChat.WhisperReply(name, false));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .whisper threw: " + e);
            ctx.Reply("Sample whisper failed. See the server log.");
        }
    }

    [Command("order", description: "Dump EventBus WorldReady handler order and the synced EmberStack value.")]
    public static void Order(ICommandContext ctx)
    {
        try
        {
            string dump = EventBus.DescribeOrder<WorldReady>();
            string[] lines = dump.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                ctx.Reply(line);
            }
            ctx.Reply($"EmberStack={Plugin.CurrentEmberStack()} (Sample.EmberStack, default {Plugin.EmberStackAfter})");
            Plugin.Logger?.LogInfo("Sample: " + dump.Replace("\n", " | "));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Sample .order threw: {e}");
            ctx.Reply("Dumping handler order failed. See the server log.");
        }
    }

    [Command("mods", usage: ".mods [trust [always]]", description: "Print the pending signed catalog on your client, or trust this operator.")]
    public static void Mods(ICommandContext ctx, string action = "", string extra = "")
    {
        SampleMods.Handle(ctx, action, extra);
    }

    // adminOnly: a pure read, gated for the same reason .castle is. It prints the world-load
    // epoch and a retained entity id (the castle heart stamped by the first .castle), which is
    // operator-shaped detail rather than chat.
    [Command("epoch", adminOnly: true, usage: ".epoch", description: "Admin only. Print the world-load epoch and whether the handle this plugin stamped at the first .castle is stale.")]
    public static void Epoch(ICommandContext ctx)
    {
        try
        {
#if !PROGENITOR_TESTS
            SampleEpoch.HandleCommand(ctx);
#else
            foreach (var line in SampleEpoch.Lines("world: epoch=1 loads=1", SampleEpoch.Label, SampleEpoch.StampedEpoch, false))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .epoch threw: " + e);
            ctx.Reply("Sample epoch failed. See the server log.");
        }
    }

    // adminOnly: `on` installs a Harmony prefix on a live server method and pins this plugin's
    // ALC for the process lifetime (BepInEx's IL2CPP detour cache is append-only). Even a
    // harmless void prefix is a change to how the server runs, and the pin is not reversible
    // without a restart, so this is never a thing a player may switch on.
    [Command("patchdemo", adminOnly: true, usage: ".patchdemo on|off|ping|status", description: "Admin only. Install/remove a harmless prefix through ctx.Patches, and fire it once. `on` pins this plugin's ALC until the process restarts.")]
    public static void PatchDemo(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SamplePatchDemo.TryParse(what, out var kind))
            {
                ctx.Reply(SamplePatchDemo.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SamplePatchDemo.HandleCommand(ctx, kind);
#else
            ctx.Reply(SamplePatchDemo.StatusReply(SamplePatchDemo.Armed, SamplePatchDemo.Fires));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .patchdemo threw: " + e);
            ctx.Reply("Sample patchdemo failed. See the server log.");
        }
    }

    // adminOnly: a read, gated like .castle and .epoch - it prints a runtime field offset and the
    // access layer's resolution counts, which is diagnostics for whoever runs the server.
    [Command("access", adminOnly: true, usage: ".access", description: "Admin only. Read one member through this plugin's own generated access layer and report the resolution log.")]
    public static void AccessDemo(ICommandContext ctx)
    {
        try
        {
#if !PROGENITOR_TESTS
            SampleAccessDemo.HandleCommand(ctx);
#else
            foreach (var line in SampleAccessDemo.Lines("access: resolved=0 declared=2", 1, "", 0, false))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .access threw: " + e);
            ctx.Reply("Sample access failed. See the server log.");
        }
    }

    // adminOnly: this moves or freezes the world's clock (and sun damage) for every player.
    [Command("time", adminOnly: true, usage: ".time [status|set <hour>|hold <hour> [bloodmoon]|release|resume]", description: "Admin only. Read, move or freeze the world's time of day through ctx.Time; connected clients' lighting follows. A hold avoids blood moon nights unless 'bloodmoon' is given.")]
    public static void TimeOfDay(ICommandContext ctx, string what = "", string hour = "", string option = "")
    {
        try
        {
            if (!SampleTime.TryParse(what, hour, option, out var kind, out double h, out bool bloodMoon))
            {
                ctx.Reply(SampleTime.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleTime.HandleCommand(ctx, kind, h, bloodMoon);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .time threw: " + e);
            ctx.Reply("Sample time failed. See the server log.");
        }
    }

    // adminOnly: this stops or slows the whole world for every player.
    [Command("speed", adminOnly: true, usage: ".speed [status|freeze|slow <scale>|release]", description: "Admin only. Freeze the world or run it in slow motion through ctx.Time (the game's replicated world speed); every connected client follows.")]
    public static void Speed(ICommandContext ctx, string what = "", string scale = "")
    {
        try
        {
            if (!SampleSpeed.TryParse(what, scale, out var kind, out float s))
            {
                ctx.Reply(SampleSpeed.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleSpeed.HandleCommand(ctx, kind, s);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .speed threw: " + e);
            ctx.Reply("Sample speed failed. See the server log.");
        }
    }

    // adminOnly: this puts items into an inventory.
    [Command("items", adminOnly: true, usage: ".items grant <prefabNameOrGuid> [amount] | equip <slot> | catalog [filter] | chests | slots <c> | move <from> <to> <item> <amount> | moveall <from> <to> | count|free <c> <item> | add|remove <c> <item> <n> | name <c> \"text\" | id <c> | watch <c> | heart | clan", description: "Admin only. ctx.Items grant/equip and the item catalog, and ctx.Inventory / ctx.Clans: list castle containers, read slots, move items with a total-conserving transfer, rename, stable ids.")]
    public static void Items(ICommandContext ctx, string verb = "", string token = "", string extra = "", string third = "", string fourth = "")
    {
        try
        {
            if (SampleItemCatalog.Handles(verb))
            {
#if !PROGENITOR_TESTS
                SampleItemCatalog.HandleCommand(ctx, (token + " " + extra + " " + third + " " + fourth).Trim());
#endif
                return;
            }

            if (SampleInventory.Handles(verb))
            {
#if !PROGENITOR_TESTS
                SampleInventory.Handle(ctx, verb.Trim().ToLowerInvariant(), token, extra, third, fourth);
#endif
                return;
            }

            if (!SampleItems.TryParse(verb, token, extra, out var kind, out string nameOrGuid, out int amount, out int slot))
            {
                ctx.Reply(SampleItems.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleItems.HandleCommand(ctx, kind, nameOrGuid, amount, slot);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .items threw: " + e);
            ctx.Reply("Sample items failed. See the server log.");
        }
    }

    // adminOnly: toggle changes a recipe switch on any station, including another player's.
    [Command("stations", adminOnly: true, usage: ".stations list [radius] | recipes [id] | toggle <recipeNameOrGuid> on|off [id] | status [id]", description: "Admin only. Read castle station recipes and progress, and switch refinement recipes, through ctx.Stations.")]
    public static void Stations(ICommandContext ctx, string verb = "", string a = "", string b = "", string c = "")
    {
        try
        {
            if (!SampleStations.TryParse(verb, a, b, c, out var request))
            {
                ctx.Reply(SampleStations.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleStations.HandleCommand(ctx, request);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .stations threw: " + e);
            ctx.Reply("Sample stations failed. See the server log.");
        }
    }

    // adminOnly: this applies the debug protection buff and the kill-path guard.
    [Command("protect", adminOnly: true, usage: ".protect on | alive | off", description: "Admin only. ctx.Debug Protect, KeepAlive or Release on your character. Separate from .godmode and from Core's .debug.")]
    public static void Protect(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleDebugProtect.TryParse(what, out var kind))
            {
                ctx.Reply(SampleDebugProtect.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleDebugProtect.HandleCommand(ctx, kind);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .protect threw: " + e);
            ctx.Reply("Sample protect failed. See the server log.");
        }
    }

    // adminOnly: the reply includes platform ids.
    [Command("players", adminOnly: true, usage: ".players | .players name <displayName> | .players account <platformId>", description: "Admin only. List online players or look one up through ctx.Players. Ambiguous names are not picked.")]
    public static void Players(ICommandContext ctx, string verb = "", [Remainder] string rest = "")
    {
        try
        {
            if (!SamplePlayerDirectory.TryParse(verb, rest, out var kind, out string name, out ulong platformId))
            {
                ctx.Reply(SamplePlayerDirectory.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SamplePlayerDirectory.HandleCommand(ctx, kind, name, platformId);
#else
            ctx.Reply(SamplePlayerDirectory.OnlineReply(0, System.Array.Empty<SamplePlayerDirectory.Row>()));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .players threw: " + e);
            ctx.Reply("Sample players failed. See the server log.");
        }
    }

    // adminOnly: a begin asks Bootstrap to take control of the caller, even though current
    // builds refuse every form.
    [Command("form", adminOnly: true, usage: ".form | .form begin [name] [seconds] | .form return", description: "Admin only. Experimental PlayerForms list, TryBegin and TryReturn. A begin is expected to be refused until a profile is qualified.")]
    public static void Form(ICommandContext ctx, string verb = "", [Remainder] string rest = "")
    {
        try
        {
            if (!SampleForms.TryParse(verb, rest, out var kind, out string name, out double seconds))
            {
                ctx.Reply(SampleForms.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleForms.HandleCommand(ctx, kind, name, seconds);
#else
            ctx.Reply(SampleForms.CatalogReply(false));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .form threw: " + e);
            ctx.Reply("Sample form failed. See the server log.");
        }
    }

    // adminOnly: this makes a character unkillable by the game's own kill paths. Same reason
    // Core's .debug is gated, minus the buff.
    [Command("godmode", adminOnly: true, usage: ".godmode on|off|status", description: "Admin only. Guard your character through ctx.GodMode only - no buff, no Immortal write. Separate from Core's .debug.")]
    public static void GodMode(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleGodModeDemo.TryParse(what, out var kind))
            {
                ctx.Reply(SampleGodModeDemo.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleGodModeDemo.HandleCommand(ctx, kind);
#else
            ctx.Reply(SampleGodModeDemo.StatusReply(false, "godmode: guarded=0 patched=0"));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .godmode threw: " + e);
            ctx.Reply("Sample godmode failed. See the server log.");
        }
    }

    // adminOnly: `.store reset` deletes a persisted key that holds every player's join count on
    // this save, and then flushes, so the deletion survives. The read halves (`status`, `keys`)
    // would be harmless on their own, but adminOnly is an attribute on the whole command and a
    // hand-rolled check inside one branch is exactly what the gating rule above forbids - so the
    // destructive branch decides for all three. `.joins` stays open and unchanged.
    [Command("store", adminOnly: true, usage: ".store [status] | .store keys | .store dir | .store flush | .store static | .store server | .store reset", description: "Admin only. Report the per-save store, list its keys, print the save directory, force a ModSave.SaveAll flush, compare ModSave.Store(guid) against ctx.Save, report the server-level store that survives wipes, or drop the join book.")]
    public static void Store(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleStore.TryParse(what, out var kind))
            {
                ctx.Reply(SampleStore.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleStore.HandleCommand(ctx, kind);
#else
            ctx.Reply(SampleStore.StatusReply(0, 0, null));
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .store threw: " + e);
            ctx.Reply("Sample store failed. See the server log.");
        }
    }

    // adminOnly: `.events cancel on` makes this plugin swallow other players' chat lines, and
    // `.events castle on` starts a once-a-second server poll. The status branch is a read of
    // this plugin's own counters, but adminOnly is an attribute on the whole command and a
    // hand-rolled check inside one branch is exactly what the gating rule above forbids.
    [Command("events", adminOnly: true, usage: ".events [status] | .events order | .events cancel on|off | .events castle on|off | .events clear", description: "Admin only. Report the events this plugin has heard, print the resolved handler order, arm the ICancellable demonstration, or subscribe the castle detector.")]
    public static void Events(ICommandContext ctx, string what = "", string arg = "")
    {
        try
        {
            if (!SampleEvents.TryParse(what, arg, out var kind))
            {
                ctx.Reply(SampleEvents.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleEvents.HandleCommand(ctx, kind);
#else
            foreach (var line in SampleEvents.StatusLines(0, 0))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .events threw: " + e);
            ctx.Reply("Sample events failed. See the server log.");
        }
    }

    // adminOnly: `define` appends a row to the server's ItemKeyLookup and pushes a PrfD to every
    // connected client; `drop` tombstones that row. Registering content is not a thing a player
    // may do, and the read branch shares the command's one attribute for the same reason .store
    // does.
    [Command("content", adminOnly: true, usage: ".content [status] | .content define | .content drop", description: "Admin only. Print PrefabRegistry.Describe() and Definitions, register a second cloned item, or retire it with UnregisterAll and watch the tombstone count.")]
    public static void Content(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleContent.TryParse(what, out var kind))
            {
                ctx.Reply(SampleContent.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleContent.HandleCommand(ctx, kind);
#else
            foreach (var line in SampleContent.StatusLines("", null, false))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .content threw: " + e);
            ctx.Reply("Sample content failed. See the server log.");
        }
    }

    // Not gated: `.rifts` is a read of the war-event scheduler, which is world information every
    // player already sees on the map card and in the HUD alert - the command only puts both tiers
    // in one place and says which number is scheduled and which is estimated. `.rifts send`
    // re-pushes the schedule packet to the *caller's own* client and nobody else's: an ungated
    // command must not open a broadcast, and the file's rule above forbids hand-rolling an admin
    // check inside one branch, so the branch was designed not to need one.
    [Command("rifts", usage: ".rifts [status] | .rifts send", description: "Print both Rift Incursion tiers (T1/T2) with their countdowns, the raw tick values, and which number is scheduled versus estimated; .rifts send re-pushes the schedule to your client.")]
    public static void Rifts(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleRifts.TryParse(what, out var kind))
            {
                ctx.Reply(SampleRifts.UsageReply);
                return;
            }

#if !PROGENITOR_TESTS
            SampleRifts.HandleCommand(ctx, kind);
#else
            foreach (var line in SampleRifts.StatusLines(false, default, 0, false, 0, 0))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .rifts threw: " + e);
            ctx.Reply("Sample rifts failed. See the server log.");
        }
    }

    // Not gated: it reads this plugin's own display strings and reports whether they are in the
    // game's string table. There is nothing here an operator would mind a player seeing.
    [Command("loc", usage: ".loc", description: "Report the Sample's runtime-registered localization key beside its file-loaded one, and whether each is in the game string table.")]
    public static void Loc(ICommandContext ctx)
    {
        try
        {
#if !PROGENITOR_TESTS
            SampleLocalization.HandleCommand(ctx);
#else
            foreach (var line in SampleLocalization.Lines(true, true, null, true, null))
                ctx.Reply(line);
#endif
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .loc threw: " + e);
            ctx.Reply("Sample loc failed. See the server log.");
        }
    }
}
