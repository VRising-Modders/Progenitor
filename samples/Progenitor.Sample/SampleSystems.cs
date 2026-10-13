using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Commands;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Progenitor.Systems;
using Unity.Entities;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job J18 (RD6). The virtual-system surface the Sample used one corner of and never showed the
/// rest of: <c>ctx.Systems.AddAfter&lt;T&gt;</c> / <c>AddBefore&lt;T&gt;</c>, an explicit
/// <see cref="Progenitor.Systems.VirtualSystemAnchor"/>, <c>ctx.Systems.RemoveAll()</c> and
/// <c>ctx.Systems.Describe()</c> — plus the two <c>SystemHooks</c> members nothing called:
/// <c>WhenWorldExists</c> and <c>RemoveAll(owner)</c>. One admin command, <c>.sys</c>, drives all
/// of it.
///
/// <b>What a virtual system is, and what it is not.</b> <c>ctx.Systems.Add</c> does not inject a
/// <c>SystemBase</c>, does not add a TypeManager row and does not touch the game's system list. It
/// is a named managed callback hung off one <c>SystemHooks</c> Before/After per anchor
/// (<c>Systems/VirtualSystems.cs</c>), which is why <b>reload is dispose</b>: unload drops the
/// callback and the game's schedule is exactly what it was. That is the whole reason the framework
/// offers this instead of an injected system — an injected type cannot be taken back out of a
/// running world, and it pins the assembly that declared it.
///
/// <b>Why three of them.</b> The three registrations here are the three placements a mod actually
/// has to choose between:
/// <list type="bullet">
/// <item><description><see cref="SimSystemName"/> at
/// <c>VirtualSystemAnchor.AfterSimulation</c> — the default, and the one to reach for. It runs
/// after <c>SimulationSystemGroup</c>, i.e. after every child system of the sim group has updated,
/// so the world is in a settled state. <c>Plugin.cs</c>'s <c>sample.users</c> and
/// <c>SampleHooks</c>'s heartbeat both sit here.</description></item>
/// <item><description><see cref="AfterSystemName"/> at
/// <c>AddAfter&lt;ServerBootstrapSystem&gt;</c> — an <em>intra-group</em> seam. Group-After is
/// after every child; naming a system puts the callback between two children, which is what you
/// need when work must land after one particular system and before the rest of the frame.</description></item>
/// <item><description><see cref="BeforeSystemName"/> at
/// <c>AddBefore&lt;ServerBootstrapSystem&gt;</c> — the same seam on the other side, for work that
/// must see the state a system is about to consume.</description></item>
/// </list>
///
/// <b>Why <c>ServerBootstrapSystem</c> is the named anchor.</b> It is the managed system CLAUDE.md
/// records as live-verified for <c>SystemHooks</c> (61 calls in two seconds), it ticks on an empty
/// server with nobody connected, and it is the same system Core itself falls back to when
/// <c>SimulationSystemGroup</c> turns out not to go through <c>SystemBase.Update</c>
/// (<c>VirtualSystems.DefaultAnchorFallbackTicks</c>). A demo anchor has to be one that fires
/// whether or not a player is online, or "the counter is 0" means nothing.
///
/// <b>The three orders of undo, and why <c>.sys</c> shows all three.</b>
/// <c>.sys off</c> disposes the three handles — the normal, surgical undo, and the only one that
/// leaves the rest of the plugin alone. <c>.sys sweep</c> calls <c>ctx.Systems.RemoveAll()</c>,
/// which is <b>owner-scoped, not caller-scoped</b>: it drops every virtual system this
/// <em>plugin</em> registered, <c>sample.users</c> and <c>sample.hooks</c> included, and the reply
/// says so, because that is the fact a reader copying the call needs and cannot see from its name.
/// <see cref="Detach"/> then shows the third: <c>SystemHooks.RemoveAll(owner)</c>, which matches by
/// the owner's <em>assembly</em> and therefore sweeps the whole Sample's hooks. That is what it is
/// for — Core's reload sweep calls it so a leftover hook cannot pin a collectible plugin ALC after
/// an <c>Unload</c> threw — and unload is the only place it is safe to call.
///
/// <b><c>WhenWorldExists</c>.</b> Attach runs on the BepInEx chainloader's thread, long before the
/// server world exists on a cold boot — but a hot reload attaches into a world that is already
/// running. <c>SystemHooks.WhenWorldExists</c> is the one call that covers both: it fires
/// immediately when the world is already there and from Core's per-frame tick when it is not, once
/// either way. This file uses the static form (rather than <c>ctx.Hooks.WhenWorldExists</c>)
/// precisely so <see cref="Detach"/> has something for <c>SystemHooks.RemoveAll</c> to count; the
/// handle is still tracked on the context, so unload would drop it regardless.
///
/// <b>Live-check split: server only.</b> Every anchor here is a server system and the command
/// replies in chat.
///
/// Parsing, the counters and every reply line are game-free, so the test project compiles this
/// file and pins the exact text.
/// </summary>
internal static class SampleSystems
{
    /// <summary>Log/grep area: <c>Sample sys: …</c>.</summary>
    internal const string Area = "sys";

    /// <summary>Virtual system at <c>VirtualSystemAnchor.AfterSimulation</c> (the default anchor).</summary>
    internal const string SimSystemName = "sample.sys.sim";

    /// <summary>Virtual system at <c>AddAfter&lt;ServerBootstrapSystem&gt;</c>.</summary>
    internal const string AfterSystemName = "sample.sys.after";

    /// <summary>Virtual system at <c>AddBefore&lt;ServerBootstrapSystem&gt;</c>.</summary>
    internal const string BeforeSystemName = "sample.sys.before";

    /// <summary>The named anchor. Managed, server-side, and it ticks with nobody connected.</summary>
    internal const string AnchorTarget = "ServerBootstrapSystem";

    /// <summary>World the <c>WhenWorldExists</c> waiter names. Checked against <c>Worlds.ServerName</c> at attach.</summary>
    internal const string WorldName = "Server";

    /// <summary>Frames between heartbeat lines, matching <c>sample.users</c>.</summary>
    internal const int HeartbeatFrames = 300;

    /// <summary>Order of the default-anchor system. Lower runs first among slots on the same anchor.</summary>
    internal const int SimOrder = 0;

    /// <summary>Order of the two explicitly anchored systems, to show the parameter is real.</summary>
    internal const int ExplicitOrder = 10;

    /// <summary>How many virtual systems <c>.sys on</c> adds.</summary>
    internal const int DemoSystemCount = 3;

    private static long _simFrames;
    private static long _afterFrames;
    private static long _beforeFrames;

    /// <summary>What the caller asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument, or <c>status</c>: counters plus <c>ctx.Systems.Describe()</c>.</summary>
        Status = 0,

        /// <summary>Register the three virtual systems.</summary>
        On = 1,

        /// <summary>Dispose the three handles this demo owns.</summary>
        Off = 2,

        /// <summary>Call <c>ctx.Systems.RemoveAll()</c> — every virtual system this plugin owns.</summary>
        Sweep = 3,

        /// <summary>Print <c>SystemHooks.DescribeOrder&lt;ServerBootstrapSystem&gt;()</c>.</summary>
        Order = 4,
    }

    /// <summary>Updates counted at the default anchor this Core generation.</summary>
    internal static long SimFrames => Interlocked.Read(ref _simFrames);

    /// <summary>Updates counted at <c>AddAfter&lt;ServerBootstrapSystem&gt;</c>.</summary>
    internal static long AfterFrames => Interlocked.Read(ref _afterFrames);

    /// <summary>Updates counted at <c>AddBefore&lt;ServerBootstrapSystem&gt;</c>.</summary>
    internal static long BeforeFrames => Interlocked.Read(ref _beforeFrames);

    /// <summary>Label for the default anchor in replies and heartbeat lines.</summary>
    internal const string SimAnchorLabel = "VirtualSystemAnchor.AfterSimulation";

    /// <summary>Label for the named After anchor.</summary>
    internal static string AfterAnchorLabel => "AddAfter<" + AnchorTarget + ">";

    /// <summary>Label for the named Before anchor.</summary>
    internal static string BeforeAnchorLabel => "AddBefore<" + AnchorTarget + ">";

    /// <summary>
    /// Parse <c>.sys</c> / <c>.sys status|on|off|sweep|order</c>. An unknown word is refused
    /// rather than treated as <c>status</c>, so a typo cannot look like it worked.
    /// </summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;

        switch (token!.Trim().ToLowerInvariant())
        {
            case "status": kind = Kind.Status; return true;
            case "on": kind = Kind.On; return true;
            case "off": kind = Kind.Off; return true;
            case "sweep": kind = Kind.Sweep; return true;
            case "order": kind = Kind.Order; return true;
            default: return false;
        }
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal const string UsageReply =
        "Usage: .sys [status] | .sys on | .sys off | .sys sweep | .sys order";

    /// <summary>The line written when the command and the world waiter are registered.</summary>
    internal static string AttachedLine() =>
        "registered .sys (ctx.Commands.Register<T>()) and a SystemHooks.WhenWorldExists(\""
        + WorldName + "\") waiter; .sys on adds the " + N(DemoSystemCount) + " virtual systems.";

    /// <summary>
    /// What the <c>WhenWorldExists</c> callback writes. It says which of the two ways it fired,
    /// because that is the whole point of the call: on a cold boot the world does not exist yet
    /// and Core's per-frame tick delivers it, on a hot reload it is already there and the callback
    /// runs inside <c>WhenWorldExists</c> itself.
    /// </summary>
    internal static string WorldExistsLine(string worldName, bool immediate) =>
        "SystemHooks.WhenWorldExists(\"" + WorldName + "\") fired for world '" + Clean(worldName)
        + "' " + (immediate ? "immediately (the world already existed: this is a hot reload)"
                            : "from Core's per-frame tick (the world was created after attach)") + ".";

    /// <summary>Reply for <c>.sys on</c>, naming each system with its anchor and order.</summary>
    internal static string OnReply() =>
        "Added " + N(DemoSystemCount) + " virtual systems: " + SimSystemName + " (" + SimAnchorLabel
        + ", order " + N(SimOrder) + "), " + AfterSystemName + " (" + AfterAnchorLabel + ", order "
        + N(ExplicitOrder) + "), " + BeforeSystemName + " (" + BeforeAnchorLabel + ", order "
        + N(ExplicitOrder) + "). Each logs one line every " + N(HeartbeatFrames)
        + " of its own updates. None of them injects a SystemBase: dispose is the undo.";

    /// <summary>Reply when <c>.sys on</c> is repeated. Adding again would replace by name, silently.</summary>
    internal static string AlreadyOnReply =>
        "The .sys demo is already on. Adding the same names again would replace them in place and "
        + "reset nothing visible, so this call did nothing. Use .sys status, or .sys off first.";

    /// <summary>Reply for <c>.sys off</c>: the surgical undo, and it says what it did not touch.</summary>
    internal static string OffReply(int disposed) =>
        "Disposed " + N(disposed) + " virtual system handle(s). This is the per-registration undo: "
        + "the plugin's other virtual systems (sample.users, sample.hooks) are untouched.";

    /// <summary>Reply for <c>.sys off</c> when nothing is registered.</summary>
    internal static string OffWhenOffReply =>
        "The .sys demo is not on, so there is nothing to dispose. Use .sys on first.";

    /// <summary>
    /// Reply for <c>.sys sweep</c>. The number is <c>ctx.Systems.RemoveAll()</c>'s return value,
    /// and the sentence after it is the part a reader cannot get from the method name: the scope
    /// is the plugin, not this file.
    /// </summary>
    internal static string SweepReply(int removed) =>
        "ctx.Systems.RemoveAll() returned " + N(removed) + ". That is owner-scoped, not caller-scoped: "
        + "every virtual system this plugin registered is now gone, sample.users and sample.hooks "
        + "included, not only the .sys ones. Reload the plugin to get them back.";

    /// <summary>Reply lines for <c>.sys status</c>: the counters, then the process-wide diagnostic.</summary>
    internal static IReadOnlyList<string> StatusLines(bool on, long sim, long after, long before, string describe) =>
        new[]
        {
            "Demo is " + (on ? "on" : "off") + ". " + SimSystemName + " (" + SimAnchorLabel + ") updates="
                + N(sim) + "; " + AfterSystemName + " (" + AfterAnchorLabel + ") updates=" + N(after)
                + "; " + BeforeSystemName + " (" + BeforeAnchorLabel + ") updates=" + N(before) + ".",
            "ctx.Systems.Describe(): " + Clean(describe),
        };

    /// <summary>
    /// Reply for <c>.sys order</c>. <c>DescribeOrder</c> is a multi-line dump and chat is one
    /// line at a time, so the newlines become <c> | </c> — the same shape <c>Plugin.cs</c> uses
    /// for <c>EventBus.DescribeOrder</c>.
    /// </summary>
    internal static string OrderReply(string dump) =>
        "SystemHooks.DescribeOrder<" + AnchorTarget + ">(): " + Clean(dump);

    /// <summary>The heartbeat line each virtual system writes every <see cref="HeartbeatFrames"/> updates.</summary>
    internal static string HeartbeatLine(string name, string anchorLabel, long updates) =>
        name + ": " + N(updates) + " update(s) at " + anchorLabel + ".";

    /// <summary>What <see cref="Detach"/> logs after the assembly-wide hook sweep.</summary>
    internal static string DetachLine(int hooksRemoved) =>
        "detached; SystemHooks.RemoveAll(owner) dropped " + N(hooksRemoved)
        + " hook(s)/waiter(s) declared by this assembly. That is assembly-scoped, which is why it "
        + "is only ever called from unload.";

    /// <summary>Whether this update is a heartbeat one. Update 0 never is.</summary>
    internal static bool IsHeartbeatFrame(long updates) => updates > 0 && updates % HeartbeatFrames == 0;

    /// <summary>Count one update at the default anchor. Returns the new total.</summary>
    internal static long CountSim() => Interlocked.Increment(ref _simFrames);

    /// <summary>Count one update at the named After anchor. Returns the new total.</summary>
    internal static long CountAfter() => Interlocked.Increment(ref _afterFrames);

    /// <summary>Count one update at the named Before anchor. Returns the new total.</summary>
    internal static long CountBefore() => Interlocked.Increment(ref _beforeFrames);

    /// <summary>Forget the counters.</summary>
    internal static void Reset()
    {
        Interlocked.Exchange(ref _simFrames, 0);
        Interlocked.Exchange(ref _afterFrames, 0);
        Interlocked.Exchange(ref _beforeFrames, 0);
    }

    /// <summary>
    /// One-line sanitizer for anything that reaches chat or the log from somewhere else — a world
    /// name, a <c>Describe</c> dump. Newlines become <c> | </c> so a multi-line dump cannot become
    /// several log lines, and control characters are dropped.
    /// </summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text!.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r') continue;
            if (c == '\n')
            {
                sb.Append(" | ");
                continue;
            }

            if (char.IsControl(c)) continue;
            sb.Append(c);
        }

        // Trim the separator as well as the whitespace: a dump that ends in a newline would
        // otherwise leave a dangling " | " on the end of the reply.
        return sb.ToString().Trim(' ', '|');
    }

#if !PROGENITOR_TESTS
    private static PluginContext? _ctx;
    private static object? _owner;
    private static readonly List<IDisposable> Handles = new();

    /// <summary>
    /// Server-side registration. The command goes on through <c>ctx.Commands.Register&lt;T&gt;()</c>
    /// — the generic form, which asks Core to construct the class; the sibling
    /// <see cref="SampleInstanceCommands"/> shows the instance form beside it. The world waiter
    /// goes on through the static <c>SystemHooks</c> API and is tracked on the context.
    /// The three virtual systems are deliberately <b>not</b> added here: <c>.sys on</c> adds them,
    /// so the log shows registration and removal happening at a moment an operator chose.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Detach();
        Reset();
        _ctx = ctx;
        _owner = ctx.Owner;

        try
        {
            if (!string.Equals(WorldName, Worlds.ServerName, StringComparison.Ordinal))
            {
                Plugin.Logger?.LogWarning(
                    "Sample sys: WorldName '" + WorldName + "' no longer matches Worlds.ServerName '"
                    + Worlds.ServerName + "'; the reply text is stale.");
            }

            ctx.Commands.Register<SampleSystemCommands>();

            // Static SystemHooks + ctx.Track rather than ctx.Hooks.WhenWorldExists, so Detach's
            // SystemHooks.RemoveAll(owner) has a registration of this file's own to count. Both
            // routes are tracked; only this one is visible to the owner-scoped sweep as ours.
            bool insideTheCall = true;
            var waiter = SystemHooks.WhenWorldExists(Worlds.ServerName, world => OnServerWorld(world, insideTheCall));
            insideTheCall = false;
            ctx.Track(waiter);

            SampleLog.Info(Area, AttachedLine());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample sys attach threw: " + e);
        }
    }

    /// <summary>
    /// The world waiter's callback. <paramref name="immediate"/> is true when
    /// <c>WhenWorldExists</c> delivered inside its own call (the world was already created), which
    /// is the hot-reload path.
    /// </summary>
    private static void OnServerWorld(World world, bool immediate)
    {
        try
        {
            SampleLog.Info(Area, WorldExistsLine(world?.Name ?? "?", immediate));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample sys WhenWorldExists callback threw: " + e);
        }
    }

    private static void OnSim(VirtualSystemContext _)
    {
        long n = CountSim();
        if (IsHeartbeatFrame(n)) SampleLog.Info(Area, HeartbeatLine(SimSystemName, SimAnchorLabel, n));
    }

    private static void OnAfter(VirtualSystemContext _)
    {
        long n = CountAfter();
        if (IsHeartbeatFrame(n)) SampleLog.Info(Area, HeartbeatLine(AfterSystemName, AfterAnchorLabel, n));
    }

    private static void OnBefore(VirtualSystemContext _)
    {
        long n = CountBefore();
        if (IsHeartbeatFrame(n)) SampleLog.Info(Area, HeartbeatLine(BeforeSystemName, BeforeAnchorLabel, n));
    }

    /// <summary>Server entry point for <c>.sys</c>.</summary>
    internal static void HandleCommand(ICommandContext cmd, Kind kind)
    {
        var ctx = _ctx;
        if (ctx == null)
        {
            cmd.Reply("The Sample plugin context is gone; reload the plugin.");
            return;
        }

        switch (kind)
        {
            case Kind.On:
                Reply(cmd, TurnOn(ctx));
                return;

            case Kind.Off:
                Reply(cmd, TurnOff());
                return;

            case Kind.Sweep:
            {
                int removed = ctx.Systems.RemoveAll();
                Handles.Clear();
                Reply(cmd, SweepReply(removed));
                return;
            }

            case Kind.Order:
                Reply(cmd, OrderReply(SystemHooks.DescribeOrder<ServerBootstrapSystem>()));
                return;

            default:
            {
                var lines = StatusLines(Handles.Count > 0, SimFrames, AfterFrames, BeforeFrames, ctx.Systems.Describe());
                for (int i = 0; i < lines.Count; i++) Reply(cmd, lines[i]);
                return;
            }
        }
    }

    private static string TurnOn(PluginContext ctx)
    {
        if (Handles.Count > 0) return AlreadyOnReply;

        Reset();
        // The explicit-anchor overload, then the two typed wrappers. All three are tracked on the
        // context as well as held here, so unload removes them even if .sys off is never typed.
        Handles.Add(ctx.Systems.Add(SimSystemName, VirtualSystemAnchor.AfterSimulation, OnSim, SimOrder));
        Handles.Add(ctx.Systems.AddAfter<ServerBootstrapSystem>(AfterSystemName, OnAfter, ExplicitOrder));
        Handles.Add(ctx.Systems.AddBefore<ServerBootstrapSystem>(BeforeSystemName, OnBefore, ExplicitOrder));
        return OnReply();
    }

    private static string TurnOff()
    {
        if (Handles.Count == 0) return OffWhenOffReply;

        int n = 0;
        for (int i = Handles.Count - 1; i >= 0; i--)
        {
            try
            {
                Handles[i].Dispose();
                n++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Sample sys: disposing a virtual system threw: " + e);
            }
        }

        Handles.Clear();
        return OffReply(n);
    }

    private static void Reply(ICommandContext cmd, string line)
    {
        cmd.Reply(line);
        SampleLog.Info(Area, line);
    }
#endif

    /// <summary>
    /// Unload. Core disposes everything tracked on the context on every reason; the extra call
    /// here is <c>SystemHooks.RemoveAll(owner)</c>, which is what Core's own reload sweep does and
    /// the only member of the hooks API that has no other demonstration. It matches by the owner's
    /// <b>assembly</b>, so it also drops <c>SampleHooks</c>'s four hooks a moment before Core would
    /// have — which is fine here and nowhere else: this runs during unload, when every one of those
    /// registrations is on its way out anyway. Never call it from a command.
    /// </summary>
    internal static void Detach()
    {
#if !PROGENITOR_TESTS
        var owner = _owner;
        Handles.Clear();
        _ctx = null;
        _owner = null;
        if (owner == null) return;

        try
        {
            int removed = SystemHooks.RemoveAll(owner);
            SampleLog.Info(Area, DetachLine(removed));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample sys detach threw: " + e);
        }
#endif
        Reset();
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}

#if !PROGENITOR_TESTS
/// <summary>
/// The <c>.sys</c> command class (job J18). An ordinary class rather than a C# <c>static</c> one,
/// registered with <c>ctx.Commands.Register&lt;SampleSystemCommands&gt;()</c> — the generic
/// overload, where <b>Core</b> constructs the object through its parameterless constructor and
/// shares one instance across the class's instance methods. That is the difference from
/// <see cref="SampleInstanceCommands"/>, which hands Core an object the plugin built and holds
/// state on it: this class deliberately holds none, because a type Core constructs cannot be given
/// anything.
/// </summary>
internal sealed class SampleSystemCommands
{
    [Command(
        "sys",
        adminOnly: true,
        usage: ".sys [status] | .sys on | .sys off | .sys sweep | .sys order",
        description: "Admin only. Add or drop three virtual systems on three different anchors, print ctx.Systems.Describe(), or dump the hook order on ServerBootstrapSystem.")]
    public void Sys(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleSystems.TryParse(what, out var kind))
            {
                ctx.Reply(SampleSystems.UsageReply);
                return;
            }

            SampleSystems.HandleCommand(ctx, kind);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .sys threw: " + e);
            ctx.Reply("Sample .sys failed. See the server log.");
        }
    }
}
#endif
