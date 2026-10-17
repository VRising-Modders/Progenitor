using System;
using System.Globalization;
using Progenitor.Cameras;

#if !PROGENITOR_TESTS
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Units;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
#endif

namespace Progenitor.Sample;

/// <summary>
/// <c>.shot</c> — <c>ctx.Camera</c> on the caller's client. Chat runs on the server and the camera
/// facade throws there, so the server parses and sends <see cref="Packet"/> (<c>SmSh</c>) and the
/// client calls <c>SetMode</c>, <c>Orbit</c> (the local character, or a world point), <c>FreeShot</c>,
/// <c>FreeShotLookAt</c> and <c>Reset</c>. Results are <c>Sample camera:</c> lines on the client,
/// including the Ctrl+F9 panel.
///
/// <para><c>.shot preview [hold]</c> pairs the server's <c>Observe</c> lease (on the caller's own character, so
/// it needs no authorization beyond being that player) with the client's <c>Preview</c>; <c>.shot hold</c>
/// keeps both open (<c>CameraObservation.Hold</c>, <c>CameraPreview.Hold</c>) and <c>.shot end</c> ends both.
/// When the client's preview ends for any reason it tells the server (<c>SmSe</c>) with the preview's sequence
/// number, and the server ends the lease only if that number is still the running one: a re-run preview ends the
/// old one with <c>Replaced</c>, and that late report must not end the new lease.
/// The sample does not block gameplay input, so walking ends the preview (<c>Moved</c>).</para>
///
/// <para>Named <c>.shot</c> so it does not share a command with NecroArmyExample's <c>.camera</c>
/// cue list. Parsing is game-free. Source-checked and game-free tested; not live-verified.
/// The Necro sample separately calls <c>FreeShotLookAt</c> and <c>Reset</c> from its own cues.</para>
/// </summary>
internal static class SampleCamera
{
    /// <summary>Server→client event id, ASCII <c>SmSh</c>. Outside the reserved <c>0x5072____</c> range.</summary>
    internal const int EventId = 0x536D5368;

    /// <summary>Client→server "my preview ended" event id, ASCII <c>SmSe</c>.</summary>
    internal const int EndEventId = 0x536D5365;

    internal const string Area = "camera";

    internal const string UsageReply =
        "Usage: .shot | .shot reset | .shot mode <topdown|orbit|free|static> | .shot orbit [distance] [yaw] [pitch] | .shot point <x> <y> <z> [distance] [yaw] [pitch] | .shot free | .shot shot <yaw> <pitch> [focus] | .shot preview [hold] | .shot hold | .shot end";

    internal const float DefaultDistance = 12f;
    internal const float DefaultYaw = 40f;
    internal const float DefaultPitch = 25f;
    internal const float DefaultFreeX = 0f;
    internal const float DefaultFreeY = 6f;
    internal const float DefaultFreeZ = -10f;

    internal enum Kind
    {
        Status = 1,
        Reset = 2,
        Mode = 3,
        OrbitSelf = 4,
        OrbitPoint = 5,
        FreeLook = 6,
        FreeShot = 7,
        Preview = 8,
        Hold = 9,
        End = 10,
    }

    /// <summary>Wire form of one <c>.shot</c> request. Plain numbers; the client adds the local character.</summary>
    internal struct Packet
    {
        public int Kind;
        public int Mode;
        public float Distance;
        public float Yaw;
        public float Pitch;
        public float X, Y, Z;
        public int Focus;
        public int Hold;
        /// <summary>The server's number for this preview and its lease (<see cref="Kind.Preview"/> only).</summary>
        public int Sequence;
    }

    /// <summary>
    /// Whether a client's "preview ended" report is about the lease that is running now. The server numbers each preview;
    /// a report for an older number (the preview a re-run replaced) is stale and leaves the new lease alone.
    /// </summary>
    internal static bool ReportMatches(int runningSequence, int reportedSequence) =>
        runningSequence != 0 && runningSequence == reportedSequence;

#if !PROGENITOR_TESTS
    /// <summary>Wire form of the client's "preview ended" report: the <see cref="CameraPreviewEndReason"/>.</summary>
    internal struct EndReport
    {
        public int Reason;
        public int Sequence;
    }
#endif

    internal static bool TryParse(string? verb, string? a, string? b, string? c, string? d, string? e, string? f, out Packet packet)
    {
        packet = default;
        packet.Distance = DefaultDistance;
        packet.Yaw = DefaultYaw;
        packet.Pitch = DefaultPitch;
        switch ((verb ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "status":
                packet.Kind = (int)Kind.Status;
                return string.IsNullOrWhiteSpace(a);
            case "reset":
                packet.Kind = (int)Kind.Reset;
                return string.IsNullOrWhiteSpace(a);
            case "mode":
                packet.Kind = (int)Kind.Mode;
                return TryMode(a, out packet.Mode) && string.IsNullOrWhiteSpace(b);
            case "orbit":
                packet.Kind = (int)Kind.OrbitSelf;
                return TryOrbitTail(a, b, c, ref packet) && string.IsNullOrWhiteSpace(d);
            case "point":
                packet.Kind = (int)Kind.OrbitPoint;
                return TryFloat(a, out packet.X) && TryFloat(b, out packet.Y) && TryFloat(c, out packet.Z)
                    && TryOrbitTail(d, e, f, ref packet);
            case "free":
                packet.Kind = (int)Kind.FreeLook;
                packet.X = DefaultFreeX;
                packet.Y = DefaultFreeY;
                packet.Z = DefaultFreeZ;
                return string.IsNullOrWhiteSpace(a);
            case "shot":
                packet.Kind = (int)Kind.FreeShot;
                packet.X = DefaultFreeX;
                packet.Y = DefaultFreeY;
                packet.Z = DefaultFreeZ;
                if (!TryFloat(a, out packet.Yaw) || !TryFloat(b, out packet.Pitch)) return false;
                if (string.IsNullOrWhiteSpace(c)) return true;
                if (!string.Equals(c.Trim(), "focus", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(d))
                    return false;
                packet.Focus = 1;
                return true;
            case "preview":
                packet.Kind = (int)Kind.Preview;
                if (string.IsNullOrWhiteSpace(a)) return true;
                packet.Hold = 1;
                return string.Equals(a.Trim(), "hold", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(b);
            case "hold":
                packet.Kind = (int)Kind.Hold;
                return string.IsNullOrWhiteSpace(a);
            case "end":
                packet.Kind = (int)Kind.End;
                return string.IsNullOrWhiteSpace(a);
            default:
                return false;
        }
    }

    internal static string SentReply(string verb) =>
        "Sent .shot " + (string.IsNullOrEmpty(verb) ? "status" : verb)
        + " to your client. Results are in the client log (Sample camera: ...).";

#if !PROGENITOR_TESTS
    private static PluginContext? _ctx;

    /// <summary>Client: the running preview, if any.</summary>
    private static PluginCamera.CameraPreview? _preview;

    /// <summary>Server: each user's observation lease and its preview number. Leases end themselves; this only finds them again.</summary>
    private static readonly System.Collections.Generic.Dictionary<Entity, (PluginCamera.CameraObservation Lease, int Sequence)> Leases = new();

    /// <summary>Server: the last preview number handed out.</summary>
    private static int _sequence;

    internal static void Attach(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        if (ctx.IsServer)
        {
            ctx.Commands.Register(typeof(SampleCameraCommands));
            BindEndReport(ctx);
        }
        if (ctx.IsClient) BindReceive(ctx);
    }

    private static unsafe void BindReceive(PluginContext ctx) =>
        ctx.Net.OnReceive(EventId, OnReceive);

    private static unsafe void BindEndReport(PluginContext ctx) =>
        ctx.Net.OnReceiveFromClient(EndEventId, OnEndReport);

    /// <summary>Unload ends the preview and the leases (ctx.Camera does it too); the handles are dropped here.</summary>
    internal static void Detach()
    {
        _ctx = null;
        _preview = null;
        Leases.Clear();
    }

    /// <summary>Server, <c>.shot preview</c>: lease the caller's streaming origin at their own character.</summary>
    internal static string StartLease(PluginContext ctx, Entity user, bool hold, out int sequence)
    {
        sequence = 0;
        var em = Progenitor.Worlds.Server!.EntityManager;
        var character = em.GetComponentData<User>(user).LocalCharacter._Entity;
        if (character == Entity.Null || !em.Exists(character)) return "no character";
        var lease = ctx.Camera.Observe(user, character, new CameraObserveOptions
        {
            Hold = hold,
            Ended = reason => SampleLog.Info(Area, "server lease ended: " + reason),
        });
        sequence = ++_sequence;
        Leases[user] = (lease, sequence);
        return "lease " + (hold ? "held" : "timed") + ", " + lease.SecondsLeft.ToString("0", CultureInfo.InvariantCulture) + " s left";
    }

    /// <summary>Server, <c>.shot hold</c>: keep the lease for its full cap.</summary>
    internal static string HoldLease(Entity user) =>
        Leases.TryGetValue(user, out var running) && running.Lease.Hold() ? "lease held" : "no running lease";

    /// <summary>Server, <c>.shot end</c>: end the user's lease, whichever preview it belongs to.</summary>
    internal static string EndLease(Entity user)
    {
        if (!Leases.Remove(user, out var running) || !running.Lease.Active) return "no running lease";
        running.Lease.Dispose();
        return "lease ended";
    }

    /// <summary>Server, the client's report: end the lease only if the report is about it.</summary>
    internal static string EndReportedLease(Entity user, int sequence)
    {
        if (!Leases.TryGetValue(user, out var running) || !ReportMatches(running.Sequence, sequence))
            return "stale report for preview " + sequence + "; the running lease stays";
        return EndLease(user);
    }

    private static unsafe void OnEndReport(int eventId, Entity userEntity, User user, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<EndReport>(payload, length, out var report)) return;
            SampleLog.Info(Area, "client preview " + report.Sequence + " ended (" + (CameraPreviewEndReason)report.Reason + "): "
                + EndReportedLease(userEntity, report.Sequence));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample camera end report threw: " + e);
        }
    }

    private static unsafe void OnReceive(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<Packet>(payload, length, out var packet) || _ctx is not { } ctx)
                return;
            Apply(ctx, packet);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample camera receive threw: " + e);
        }
    }

    private static void Apply(PluginContext ctx, Packet packet)
    {
        var camera = ctx.Camera;
        switch ((Kind)packet.Kind)
        {
            case Kind.Status:
                SampleLog.Info(Area, "mode=" + camera.Mode + " shot=" + camera.IsShotActive);
                return;
            case Kind.Reset:
                camera.Reset();
                SampleLog.Info(Area, "reset; mode=" + camera.Mode + " shot=" + camera.IsShotActive);
                return;
            case Kind.Mode:
                var mode = (CameraMode)packet.Mode;
                camera.SetMode(mode);
                SampleLog.Info(Area, "SetMode " + mode + "; mode now " + camera.Mode + " (the game shows the switch on the next LocalUserSystem update)");
                return;
            case Kind.OrbitSelf:
                if (!TryLocalCharacter(out var self, out _))
                {
                    SampleLog.Info(Area, "orbit: no local character in the client world");
                    return;
                }
                camera.Orbit(self, packet.Distance, packet.Yaw, packet.Pitch);
                SampleLog.Info(Area, "Orbit entity " + self.Index + ":" + self.Version
                    + " distance=" + packet.Distance + " yaw=" + packet.Yaw + " pitch=" + packet.Pitch
                    + " shot=" + camera.IsShotActive);
                return;
            case Kind.OrbitPoint:
                var point = new UnitVec3(packet.X, packet.Y, packet.Z);
                camera.Orbit(point, packet.Distance, packet.Yaw, packet.Pitch);
                SampleLog.Info(Area, "Orbit point " + point + " distance=" + packet.Distance + " shot=" + camera.IsShotActive);
                return;
            case Kind.FreeLook:
                if (!TryLocalCharacter(out _, out float3 at))
                {
                    SampleLog.Info(Area, "free: no local character in the client world");
                    return;
                }
                var from = new UnitVec3(at.x + packet.X, at.y + packet.Y, at.z + packet.Z);
                var look = new UnitVec3(at.x, at.y + 1.6f, at.z);
                camera.FreeShotLookAt(from, look);
                SampleLog.Info(Area, "FreeShotLookAt from " + from + " at " + look + " shot=" + camera.IsShotActive);
                return;
            case Kind.FreeShot:
                if (!TryLocalCharacter(out _, out float3 stand))
                {
                    SampleLog.Info(Area, "shot: no local character in the client world");
                    return;
                }
                var position = new UnitVec3(stand.x + packet.X, stand.y + packet.Y, stand.z + packet.Z);
                if (packet.Focus != 0)
                {
                    var focus = new UnitVec3(stand.x, stand.y + 1.6f, stand.z);
                    camera.FreeShot(position, packet.Yaw, packet.Pitch, focus);
                    SampleLog.Info(Area, "FreeShot " + position + " yaw=" + packet.Yaw + " pitch=" + packet.Pitch + " focus=" + focus);
                }
                else
                {
                    camera.FreeShot(position, packet.Yaw, packet.Pitch);
                    SampleLog.Info(Area, "FreeShot " + position + " yaw=" + packet.Yaw + " pitch=" + packet.Pitch);
                }
                return;
            case Kind.Preview:
                if (!TryLocalCharacter(out _, out float3 here))
                {
                    SampleLog.Info(Area, "preview: no local character in the client world");
                    return;
                }
                int sequence = packet.Sequence;
                _preview = camera.Preview(new UnitVec3(here.x, here.y, here.z), packet.Yaw, new CameraPreviewOptions
                {
                    Hold = packet.Hold != 0,
                    Ended = reason =>
                    {
                        SampleLog.Info(Area, "preview ended: " + reason);
                        ctx.Net.SendToServer(EndEventId, new EndReport { Reason = (int)reason, Sequence = sequence });
                    },
                });
                SampleLog.Info(Area, "Preview at " + here + " held=" + _preview.IsHeld);
                return;
            case Kind.Hold:
                SampleLog.Info(Area, _preview is { } held && held.Hold() ? "preview held" : "hold: no running preview");
                return;
            case Kind.End:
                if (_preview is { Active: true } running) running.Dispose();
                else SampleLog.Info(Area, "end: no running preview");
                return;
            default:
                SampleLog.Info(Area, "ignored kind " + packet.Kind);
                return;
        }
    }

    private static bool TryLocalCharacter(out Entity character, out float3 position)
    {
        character = Entity.Null;
        position = default;
        var world = Progenitor.Worlds.Client;
        if (world == null || !world.IsCreated) return false;
        var em = world.EntityManager;
        var users = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            var user = em.GetComponentData<User>(users[i]);
            var ch = user.LocalCharacter._Entity;
            if (ch == Entity.Null || !ch.Has<Translation>(em)) continue;
            character = ch;
            position = em.GetComponentData<Translation>(ch).Value;
            return true;
        }

        return false;
    }
#endif

    private static bool TryMode(string? text, out int mode)
    {
        mode = 0;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "topdown": mode = (int)CameraMode.TopDown; return true;
            case "orbit": mode = (int)CameraMode.Orbit; return true;
            case "free": mode = (int)CameraMode.Free; return true;
            case "static": mode = (int)CameraMode.Static; return true;
            default: return false;
        }
    }

    private static bool TryOrbitTail(string? distance, string? yaw, string? pitch, ref Packet packet)
    {
        if (!string.IsNullOrWhiteSpace(distance) && !TryFloat(distance, out packet.Distance)) return false;
        if (!string.IsNullOrWhiteSpace(yaw) && !TryFloat(yaw, out packet.Yaw)) return false;
        if (!string.IsNullOrWhiteSpace(pitch) && !TryFloat(pitch, out packet.Pitch)) return false;
        return IsUsable(packet.Distance) && packet.Distance >= 0.1f
            && IsUsable(packet.Yaw) && IsUsable(packet.Pitch);
    }

    private static bool TryFloat(string? text, out float value) =>
        float.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && IsUsable(value);

    private static bool IsUsable(float value) => float.IsFinite(value) && Math.Abs(value) < 100000f;
}

#if !PROGENITOR_TESTS
internal static class SampleCameraCommands
{
    [Command("shot", usage: SampleCamera.UsageReply,
        description: "Client cinematic camera through ctx.Camera: mode, orbit, a free shot, or reset. Results are in the client log.")]
    public static void Run(ICommandContext ctx, string verb = "", string a = "", string b = "", string c = "", string d = "", string e = "", string f = "")
    {
        try
        {
            if (!SampleCamera.TryParse(verb, a, b, c, d, e, f, out var packet))
            {
                ctx.Reply(SampleCamera.UsageReply);
                return;
            }

            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }

            string lease = "";
            if (packet.Kind == (int)SampleCamera.Kind.Preview)
                lease = SampleCamera.StartLease(plugin, ctx.UserEntity, packet.Hold != 0, out packet.Sequence) + ". ";
            else if (packet.Kind == (int)SampleCamera.Kind.Hold) lease = SampleCamera.HoldLease(ctx.UserEntity) + ". ";
            else if (packet.Kind == (int)SampleCamera.Kind.End) lease = SampleCamera.EndLease(ctx.UserEntity) + ". ";
            bool sent = plugin.Net.SendToUser(ctx.UserEntity, SampleCamera.EventId, packet);
            ctx.Reply(sent
                ? lease + SampleCamera.SentReply(verb)
                : "Could not send to your client (are you connected?).");
            SampleLog.Info(SampleCamera.Area, "server sent " + (verb.Length == 0 ? "status" : verb) + " sent=" + sent);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("Sample .shot threw: " + ex);
            ctx.Reply("Sample shot failed. See the server log.");
        }
    }
}
#endif
