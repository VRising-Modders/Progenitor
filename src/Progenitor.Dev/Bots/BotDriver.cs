// EXPERIMENTAL, EARLY DEVELOPMENT, development-only: part of the headless bot experiment (tools/bots/README.md), not a
// supported framework feature. Ships only in the Dev bridge, never in the Release package.
using System;
using System.Text.Json;
using ProjectM.Network;
using ProjectM;
using ProjectM.Shared;
using ProjectM.UI;
using Progenitor.Ecs;
using Progenitor.Inputs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Client op <c>bot</c>: the "hands" of a headless player. It drives the local character the way a
/// keyboard and mouse would, through Core's input lease API (<see cref="InputLeases"/>, see
/// <c>docs/input.md</c>): one <see cref="InputChannels.All"/> lease with
/// <see cref="UserPolicy.Override"/> that preempts any other holder. Core writes the lease's intent
/// into the game's input and the outgoing commands, so there is one input injector per process.
/// The server then sees ordinary player input and applies its own movement, cooldown and ability
/// rules; nothing here is server authority.
///
/// <para>The lease is acquired lazily by any action other than <c>status</c> and re-acquired when
/// it has ended (disconnect, world change, Core swap, the user's release key, preemption).
/// <c>stop</c> disposes it, handing the client back.</para>
///
/// <para>Actions: <c>move</c> {x,z,[stop],[timeout]} walk to a point; <c>walk</c> {dx,dz,seconds}
/// walk in a direction; <c>aim</c> {x,[y],z}; <c>hold</c> {buttons,[seconds]} / <c>release</c>
/// {buttons}; <c>tap</c> {buttons}; <c>hover</c> {entity "index:version"} / <c>unhover</c>;
/// <c>stop</c> (release everything, stand still, end the lease); <c>status</c>. Buttons are names
/// of <see cref="SyncedButtons"/> (Primary, Interact, TravelSkill, WeaponSkill1, WeaponSkill2,
/// OffensiveSpell, DefensiveSpell, UltimateSpell, Ability4, CliffJump, Respawn, Interrupt) or a
/// number.</para>
/// </summary>
internal static class BotDriver
{
    private const string Holder = "progenitor.dev/bot";

    private enum MoveMode { None, Target, Direction }

    // Main thread only: the bridge dispatches "bot" on the main thread and OnFrame runs there.
    private static InputLease? _lease;
    private static InputLeaseEnd? _lastEnd;

    // Intent kept for status and steering.
    private static MoveMode _mode;
    private static float3 _target;
    private static float _stop = 1.5f;
    private static float2 _dir;
    private static double _moveUntil;
    private static bool _hasAim;
    private static float3 _aim;
    private static bool _hasHover;
    private static string? _hoverText;
    // Mirror of the lease's held buttons with per-bit expiry, for status only (Core expires the real ones).
    private static ushort _held;
    private static readonly double[] _holdUntil = new double[16];

    // Readback for status.
    private static float3 _lastPos;
    private static bool _arrived;
    private static string _lastIssue = "";
    private static Entity _interactTarget;
    private static double _interactDeadline;
    private static int _interactFrames;
    private static string _interaction = "none";

    public static object Run(World world, JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot runs on a client (a headless extra instance)");
        string action = (Str(args, "action") ?? "status").Trim().ToLowerInvariant();
        if (action == "status") return Status();
        if (action == "stop")
        {
            Stop();
            return Status();
        }

        if (action is not ("move" or "walk" or "aim" or "hold" or "release" or "tap" or "hover" or "unhover" or "interact"))
            throw new ArgumentException("args.action must be move, walk, aim, hold, release, tap, hover, unhover, interact, stop or status");

        // Arguments are parsed before the lease is taken: a malformed request must not preempt
        // another holder and then throw.
        double now = Time.realtimeSinceStartupAsDouble;
        switch (action)
        {
            case "interact":
            {
                var text = Str(args, "entity") ?? throw new ArgumentException("args.entity (client index:version) is required");
                var parts = text.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int index) || !int.TryParse(parts[1], out int version))
                    throw new ArgumentException("args.entity must be index:version");
                var entity = new Entity { Index = index, Version = version };
                var em = world.EntityManager;
                if (!entity.Exists(em) || !entity.Has<NetworkId>(em) || !entity.Has<Interactable>(em))
                    throw new ArgumentException("Target must be a live client interactable with a NetworkId");
                Stop();
                HUDMenuManager.CloseMenu(world);
                var lease = EnsureLease();
                try { lease.Hover(entity); lease.AimAt(entity); }
                catch { Stop(); _interaction = "failed to initialize interaction"; throw; }
                _hasHover = true;
                _hoverText = text;
                _interactTarget = entity;
                _interactFrames = 0;
                _interactDeadline = now + 3;
                _interaction = "pending: ordinary interaction input; server validation still applies";
                break;
            }
            case "move":
            {
                var target = new float3(Num(args, "x"), 0, Num(args, "z"));
                float stop = NumOr(args, "stop", 1.5f);
                double until = now + NumOr(args, "timeout", 60f);
                EnsureLease();
                _target = target;
                _stop = stop;
                _moveUntil = until;
                _arrived = false;
                _lastIssue = "";
                _mode = MoveMode.Target;
                break;
            }
            case "walk":
            {
                var dir = math.normalizesafe(new float2(Num(args, "dx"), Num(args, "dz")));
                double until = now + NumOr(args, "seconds", 1f);
                var lease = EnsureLease();
                _dir = dir;
                _moveUntil = until;
                _mode = MoveMode.Direction;
                lease.Move(_dir);
                break;
            }
            case "aim":
            {
                var aim = new float3(Num(args, "x"), NumOr(args, "y", _lastPos.y), Num(args, "z"));
                var lease = EnsureLease();
                _aim = aim;
                _hasAim = true;
                lease.AimAt(_aim);
                break;
            }
            case "hold":
            {
                var mask = Buttons(args);
                float? seconds = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("seconds", out _) ? NumOr(args, "seconds", 1f) : null;
                if (seconds is { } secs && !(secs > 0f && float.IsFinite(secs)))
                    throw new ArgumentException("args.seconds must be positive");
                EnsureLease().Press(mask, seconds);
                double until = seconds is { } s ? now + s : double.MaxValue;
                _held |= (ushort)mask;
                for (int bit = 0; bit < 16; bit++) if (((ushort)mask & (1 << bit)) != 0) _holdUntil[bit] = until;
                break;
            }
            case "release":
            {
                var mask = Buttons(args);
                EnsureLease().Release(mask);
                _held &= (ushort)~(ushort)mask;
                break;
            }
            case "tap":
            {
                var mask = Buttons(args);
                EnsureLease().Tap(mask);
                break;
            }
            case "hover":
            {
                var text = Str(args, "entity") ?? throw new ArgumentException("args.entity (index:version) is required");
                var parts = text.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int index) || !int.TryParse(parts[1], out int version))
                    throw new ArgumentException("args.entity must be index:version");
                // Throws ArgumentException when the entity is not in the client world or has no NetworkId.
                EnsureLease().Hover(new Entity { Index = index, Version = version });
                _hoverText = text;
                _hasHover = true;
                break;
            }
            case "unhover":
                EnsureLease().ClearHover();
                _hasHover = false;
                _hoverText = null;
                break;
        }
        return Status();
    }

    public static object Status()
    {
        var lease = _lease;
        bool active = lease is { Active: true };
        return new
        {
            position = new { x = _lastPos.x, y = _lastPos.y, z = _lastPos.z },
            mode = _mode.ToString(),
            target = _mode == MoveMode.Target ? new { x = _target.x, z = _target.z } : null,
            distance = _mode == MoveMode.Target ? math.distance(_lastPos.xz, _target.xz) : (float?)null,
            arrived = _arrived,
            held = active ? CurrentHeld() : (ushort)0,
            aim = _hasAim ? new { x = _aim.x, y = _aim.y, z = _aim.z } : null,
            hover = _hasHover ? _hoverText : null,
            lease = new
            {
                active,
                endReason = lease?.EndReason?.ToString() ?? _lastEnd?.ToString(),
                suspended = active && lease!.Suspended,
                targetLost = active && lease!.TargetLost,
            },
            lastSent = lease?.LastSent.ToString() ?? InputLeases.Status.LastSent.ToString(),
            input = InputLeases.Status.ToString(),
            issue = _lastIssue,
            interaction = _interaction,
        };
    }

    public static void Shutdown()
    {
        var lease = _lease;
        _lease = null;
        try { lease?.Dispose(); } catch { /* best-effort */ }
        ClearIntent();
    }

    /// <summary>Stand still, hand aim, hover and buttons back, and end the lease.</summary>
    private static void Stop()
    {
        var lease = _lease;
        _lease = null;
        if (lease is { Active: true })
        {
            lease.Stop();
            lease.ClearAim();
            lease.ClearHover();
            lease.Release((SyncedButtons)ushort.MaxValue);
            lease.Dispose();
        }
        ClearIntent();
    }

    private static void ClearIntent()
    {
        _mode = MoveMode.None;
        _hasAim = false;
        _hasHover = false;
        _hoverText = null;
        _held = 0;
        _interactTarget = Entity.Null;
    }

    private static InputLease EnsureLease()
    {
        if (_lease is { Active: true } live) return live;
        _lease = null;
        var result = InputLeases.TryAcquire(Holder,
            new InputLeaseOptions(InputChannels.All, UserPolicy.Override, preempt: true, reason: "bridge bot op"),
            out var lease);
        if (result != InputAcquireResult.Acquired || lease == null)
        {
            var issue = InputLeases.Status.Issue;
            throw new InvalidOperationException("bot: input lease not acquired: " + result + (issue == null ? "" : " (" + issue + ")"));
        }

        // A new lease starts with no intent: drop what the previous (ended) one carried.
        ClearIntent();
        _lastEnd = null;
        _lastIssue = "";
        lease.Ended += reason =>
        {
            _lastEnd = reason;
            if (ReferenceEquals(_lease, lease)) ClearIntent();
        };
        lease.OnFrame = OnFrame;
        _lease = lease;
        return lease;
    }

    /// <summary>Per frame, before Core writes input: refresh the position and steer.</summary>
    private static void OnFrame(InputLease lease)
    {
        if (_interactTarget != Entity.Null && Time.realtimeSinceStartupAsDouble >= _interactDeadline)
        {
            _interaction = "not confirmed: target unavailable, out of range, blocked, or interaction refused";
            Stop();
            return;
        }
        var world = Progenitor.Worlds.Client;
        if (world == null || !world.IsCreated) { _lastIssue = "no client world"; return; }
        var em = world.EntityManager;
        var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (locals.Count == 0) { _lastIssue = "no local character"; return; }
        if (!locals[0].TryRead<Translation>(em, out var tr)) { _lastIssue = "no Translation"; return; }
        _lastPos = tr.Value;
        double now = Time.realtimeSinceStartupAsDouble;

        if (_interactTarget != Entity.Null)
        {
            if (_interactFrames >= 3 && now >= _interactDeadline - 2.25
                && locals[0].TryRead<Interactor>(em, out var interaction) && interaction.Target == _interactTarget
                && interaction.InteractAbilityGuid.GuidHash != 0)
            {
                _interaction = "input completed; client target=" + _hoverText + "; server confirmation required";
                Stop();
                return;
            }
            if (!em.Exists(_interactTarget) || now >= _interactDeadline)
            {
                _interaction = "not confirmed: target unavailable, out of range, blocked, or interaction refused";
                Stop();
                return;
            }
            // Let the hover reach the outgoing command before the interaction edge.
            if (++_interactFrames == 3) lease.Press(SyncedButtons.Interact, 0.4f);
        }

        if (_mode == MoveMode.Target)
        {
            float2 delta = _target.xz - _lastPos.xz;
            float dist = math.length(delta);
            if (dist <= _stop || now > _moveUntil)
            {
                _arrived = dist <= _stop;
                _lastIssue = _arrived ? "" : "move timed out";
                _mode = MoveMode.None;
                lease.Stop();
            }
            else lease.Move(delta / dist);
        }
        else if (_mode == MoveMode.Direction)
        {
            if (now > _moveUntil)
            {
                _mode = MoveMode.None;
                lease.Stop();
            }
            else lease.Move(_dir);
        }
    }

    private static ushort CurrentHeld()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        for (int bit = 0; bit < 16; bit++)
            if ((_held & (1 << bit)) != 0 && now > _holdUntil[bit]) _held &= (ushort)~(1 << bit);
        return _held;
    }

    internal static ushort ParseButtons(string text)
    {
        ushort mask = 0;
        foreach (var part in text.Split(new[] { ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ushort.TryParse(part, out var n)) { mask |= n; continue; }
            if (!Enum.TryParse<SyncedButtons>(part, ignoreCase: true, out var b))
                throw new ArgumentException($"unknown button '{part}'");
            mask |= (ushort)b;
        }
        return mask;
    }

    private static SyncedButtons Buttons(JsonElement args) =>
        (SyncedButtons)ParseButtons(Str(args, "buttons") ?? throw new ArgumentException("args.buttons is required (e.g. \"Primary\")"));

    private static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float Num(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetSingle()
            : throw new ArgumentException($"args.{k} (number) is required");

    private static float NumOr(JsonElement a, string k, float d) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : d;
}
