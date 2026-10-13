using System;
using System.Diagnostics;
using System.Globalization;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Inputs;
using Progenitor.Networking;
using Progenitor.Plugins;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Sample;

/// <summary>
/// <c>.input aim|walk &lt;x&gt; &lt;z&gt; [seconds]|tap &lt;button&gt;|release|status</c> — <c>ctx.Input</c>
/// (<c>docs/input.md</c>): steer the local player the way the keyboard and mouse would.
///
/// <para>Chat commands run on the server and <c>ctx.Input</c> is client only, so this uses the
/// Sample's usual split (<see cref="SampleFx"/>, <see cref="SampleMenu"/>): the server command parses
/// and sends <see cref="Packet"/> to the caller's client, and the client takes the lease. Results are
/// client-side, so they go to the client log as <c>Sample input: …</c> (also on the Ctrl+F9 panel).</para>
///
/// <para>The three leases hold disjoint channels, so they coexist: <c>aim</c> holds Aim + Hover,
/// <c>walk</c> holds Move, <c>tap</c> holds Buttons. All use <see cref="UserPolicy.Yield"/>: touching
/// the mouse or movement keys pauses the matching channel and it resumes 1.5 s later. Leases end on
/// plugin unload by themselves; <see cref="Detach"/> only drops this file's references.</para>
///
/// <para><b>Acceptance procedure — not run.</b> On a focused test client (a background client sends
/// no input commands; <c>.input status</c> shows <c>commands not flowing</c>): <c>.input aim</c> near
/// a unit turns the character toward it and the client log shows its <c>LastSent</c> with a non-zero
/// <c>hoverNet</c>; <c>.input walk 1 0</c> walks east for 2 s and stops; <c>.input tap Primary</c>
/// swings once; <c>.input release</c> hands everything back.</para>
/// </summary>
internal static unsafe class SampleInput
{
    /// <summary>Server→client event id, ASCII <c>SmIn</c>.</summary>
    internal const int EventId = 0x536D496E;

    internal const string Area = "input";
    internal const string UsageReply = "Usage: .input aim | walk <x> <z> [seconds] | tap <button> | release | status";
    internal const float AimRange = 15f;
    internal const float DefaultWalkSeconds = 2f;
    internal const float TapTimeoutSeconds = 3f;

    internal enum Kind { Aim = 1, Walk = 2, Tap = 3, Release = 4, Status = 5 }

    /// <summary>Wire form of one <c>.input</c> request.</summary>
    internal struct Packet
    {
        public int Kind;
        public float X, Z, Seconds;
        public int Buttons;
    }

    private static PluginContext? _ctx;
    private static InputLease? _aim, _walk, _tap;

    internal static void Attach(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        if (ctx.IsServer) ctx.Commands.Register(typeof(SampleInputCommands));
        if (ctx.IsClient) ctx.Net.OnReceive(EventId, OnReceive);
    }

    /// <summary>Unload: Core ends the leases (<see cref="InputLeaseEnd.PluginUnloaded"/>); drop the references.</summary>
    internal static void Detach()
    {
        _aim = _walk = _tap = null;
        _ctx = null;
    }

    /// <summary>Server-side parse. Buttons are <see cref="SyncedButtons"/> names, never numbers.</summary>
    internal static bool TryParse(string? what, string? a, string? b, string? c, out Packet packet)
    {
        packet = default;
        var inv = CultureInfo.InvariantCulture;
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "aim": packet.Kind = (int)Kind.Aim; return true;
            case "release": packet.Kind = (int)Kind.Release; return true;
            case "":
            case "status": packet.Kind = (int)Kind.Status; return true;
            case "walk":
                packet.Kind = (int)Kind.Walk;
                packet.Seconds = DefaultWalkSeconds;
                if (!float.TryParse(a, NumberStyles.Float, inv, out packet.X) || !float.TryParse(b, NumberStyles.Float, inv, out packet.Z)) return false;
                if (!string.IsNullOrWhiteSpace(c) && !float.TryParse(c, NumberStyles.Float, inv, out packet.Seconds)) return false;
                return float.IsFinite(packet.X) && float.IsFinite(packet.Z) && (packet.X != 0f || packet.Z != 0f)
                    && packet.Seconds > 0f && packet.Seconds <= 30f;
            case "tap":
                string name = (a ?? "").Trim();
                if (name.Length == 0 || char.IsDigit(name[0]) || name[0] == '-') return false;
                if (!Enum.TryParse(name, ignoreCase: true, out SyncedButtons button) || button == SyncedButtons.None) return false;
                packet.Kind = (int)Kind.Tap;
                packet.Buttons = (int)button;
                return true;
            default: return false;
        }
    }

    // ---- client ------------------------------------------------------------------------------------

    private static void OnReceive(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<Packet>(payload, length, out var p) || _ctx is not { } ctx) return;
            switch ((Kind)p.Kind)
            {
                case Kind.Aim: StartAim(ctx); break;
                case Kind.Walk: StartWalk(ctx, p.X, p.Z, p.Seconds); break;
                case Kind.Tap: StartTap(ctx, (SyncedButtons)p.Buttons); break;
                case Kind.Release:
                    _aim?.Dispose();
                    _walk?.Dispose();
                    _tap?.Dispose();
                    SampleLog.Info(Area, "released; held now " + ctx.Input.HeldChannels);
                    break;
                case Kind.Status:
                    var input = ctx.Input;
                    SampleLog.Info(Area, "status " + input.Status + " | aim holder " + (input.HolderOf(InputChannels.Aim) ?? "none")
                        + ", move holder " + (input.HolderOf(InputChannels.Move) ?? "none"));
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample input receive threw: " + e);
        }
    }

    private static void StartAim(PluginContext ctx)
    {
        _aim?.Dispose(); // our own previous aim; another plugin's lease is refused below, not taken
        if (!TryNearestUnit(out var target))
        {
            SampleLog.Info(Area, "aim: no unit with a NetworkId within " + AimRange + " m");
            return;
        }
        if (!Acquire(ctx, InputChannels.Aim | InputChannels.Hover, "sample aim", out var lease)) return;
        lease.AimAt(target);
        lease.Hover(target);
        // Retarget when the unit dies or despawns; release when nothing is left in range.
        lease.OnFrame = l =>
        {
            if (!l.TargetLost) return;
            if (TryNearestUnit(out var next)) { l.AimAt(next); l.Hover(next); }
            else l.Dispose();
        };
        _aim = lease;
        SampleLog.Info(Area, "aim at " + target.Index + ":" + target.Version + "; last sent " + lease.LastSent);
    }

    private static void StartWalk(PluginContext ctx, float x, float z, float seconds)
    {
        _walk?.Dispose();
        if (!Acquire(ctx, InputChannels.Move, "sample walk", out var lease)) return;
        lease.Move(x, z);
        long until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        lease.OnFrame = l =>
        {
            if (Stopwatch.GetTimestamp() < until) return;
            l.Stop();
            SampleLog.Info(Area, "walk done; last sent " + l.LastSent);
            l.Dispose(); // the release frame also sends a zero walk
        };
        _walk = lease;
        SampleLog.Info(Area, "walk (" + x + ", " + z + ") for " + seconds + " s");
    }

    private static void StartTap(PluginContext ctx, SyncedButtons button)
    {
        _tap?.Dispose();
        if (!Acquire(ctx, InputChannels.Buttons, "sample tap", out var lease)) return;
        lease.Tap(button);
        // Tap is down on one command and up on the next: keep the lease until the read-back shows
        // the press went out and then the release, or give up (an unfocused client sends nothing).
        long giveUp = Stopwatch.GetTimestamp() + (long)(TapTimeoutSeconds * Stopwatch.Frequency);
        bool pressed = false;
        lease.OnFrame = l =>
        {
            bool down = (l.LastSent.Pressed & button) != 0;
            if (down) pressed = true;
            if (pressed && !down)
            {
                SampleLog.Info(Area, "tap " + button + " sent; last sent " + l.LastSent);
                l.Dispose();
            }
            else if (Stopwatch.GetTimestamp() > giveUp)
            {
                SampleLog.Info(Area, "tap " + button + " not seen going out (" + ctx.Input.Status + ")");
                l.Dispose();
            }
        };
        _tap = lease;
    }

    /// <summary>Acquire, log the outcome, and log the end reason once the lease ends.</summary>
    private static bool Acquire(PluginContext ctx, InputChannels channels, string reason, out InputLease lease)
    {
        var result = ctx.Input.TryAcquire(new InputLeaseOptions(channels, UserPolicy.Yield, preempt: false, reason: reason), out var got);
        if (result != InputAcquireResult.Acquired || got == null)
        {
            string holder = result == InputAcquireResult.Busy ? " (held by " + (ctx.Input.HolderOf(channels) ?? "?") + ")" : "";
            SampleLog.Info(Area, reason + " refused: " + result + holder + "; " + ctx.Input.Status);
            lease = null!;
            return false;
        }
        lease = got;
        got.Ended += end =>
        {
            if (ReferenceEquals(_aim, got)) _aim = null;
            if (ReferenceEquals(_walk, got)) _walk = null;
            if (ReferenceEquals(_tap, got)) _tap = null;
            SampleLog.Info(Area, reason + " ended: " + end);
        };
        return true;
    }

    /// <summary>
    /// Nearest client-world unit (Health + NetworkId + Translation) within <see cref="AimRange"/> of
    /// the local character, excluding it. Client entity ids only; never a server id.
    /// </summary>
    private static bool TryNearestUnit(out Entity best)
    {
        best = Entity.Null;
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return false;
        var em = world.EntityManager;
        var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (locals.Count != 1 || !locals[0].TryRead<Translation>(em, out var self)) return false;
        var units = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
        float bestSq = AimRange * AimRange;
        for (int i = 0; i < units.Count; i++)
        {
            var e = units[i];
            if (e == locals[0] || !e.TryRead<Translation>(em, out var t)) continue;
            float2 d = t.Value.xz - self.Value.xz;
            float sq = math.lengthsq(d);
            if (sq >= bestSq) continue;
            bestSq = sq;
            best = e;
        }
        return best != Entity.Null;
    }
}

/// <summary>Server half of <c>.input</c>: parse, then hand the request to the caller's client.</summary>
internal static class SampleInputCommands
{
    [Command("input", usage: SampleInput.UsageReply,
        description: "Steer your own character through ctx.Input on your client: aim at the nearest unit, walk, tap a button, release, or status. Results go to the client log.")]
    public static void Run(ICommandContext ctx, string what = "", string a = "", string b = "", string c = "")
    {
        try
        {
            if (!SampleInput.TryParse(what, a, b, c, out var packet))
            {
                ctx.Reply(SampleInput.UsageReply + "  (buttons: " + string.Join(", ", Enum.GetNames(typeof(Progenitor.Inputs.SyncedButtons))) + ")");
                return;
            }
            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }
            bool sent = plugin.Net.SendToUser(ctx.UserEntity, SampleInput.EventId, packet);
            ctx.Reply(sent
                ? "Sent .input " + what + " to your client; results are in the client log (Sample input: ...)."
                : "Could not send to your client (are you connected?).");
            SampleLog.Info(SampleInput.Area, "server sent " + (what.Length == 0 ? "status" : what) + " sent=" + sent);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .input threw: " + e);
            ctx.Reply("Sample input failed. See the server log.");
        }
    }
}
