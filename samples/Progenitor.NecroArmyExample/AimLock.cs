using System;
using Progenitor.Plugins;
using Unity.Mathematics;

namespace Progenitor.NecroArmyExample;

/// <summary>
/// Keeps the necromancer facing Dracula, whatever the camera or Dracula does, without touching the
/// mouse. A player faces, and aims spells at, the aim point in her <c>EntityInput</c>, which the game's
/// <c>GameplayInputSystem</c> fills from the cursor every frame. While locked, the client overwrites
/// that aim (<c>AimPosition</c>, <c>AimPositionPlane</c>, <c>AimDirection</c>, <c>AimLength</c>) just
/// after the game fills it, with a point at Dracula's feet, so the camera can cut anywhere and the
/// operating-system cursor stays with the user. When he comes within <see cref="DashRange"/>, it aims
/// 5 m directly away from him and asks the server to cast her dash, then locks on again. When he
/// can no longer be found (he has fallen), the last aim is kept, so she bows toward where he fell.
/// </summary>
internal sealed class AimLock : IDisposable
{
    /// <summary>Client-to-server event asking for the dash ('NCR2', outside the reserved range).</summary>
    public const int DashEventId = 0x4E435232;

    private const float DashRange = 3.5f;
    private const double DashCooldown = 4.0, DashAimSeconds = 0.5;

    private readonly PluginContext _ctx;
    private double _lastDash = -10, _now;

    internal AimLock(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Track(this);
    }

    /// <summary>Unload: hand the player back (cursor shown, nothing written to the input).</summary>
    public void Dispose() => Locked = false;

    private bool _locked;

    /// <summary>Also move her: keep spell range and strafe (see <see cref="Steer"/>). Off during the point, which movement would cancel.</summary>
    internal bool Moving { get; set; }

    // Range she keeps from Dracula (meters): well inside her unholy spells' reach (at 10-12 m, take 15's
    // spells fell short). Closer than BackOff she retreats, and the dash fires when he rushes her.
    private const float Near = 5.5f, Ideal = 6.5f, Far = 8f, BackOff = 4.5f;

    // The throne hall floor (x, z), kept inside with a margin.
    private const float MinX = 701f, MaxX = 737f, MinZ = -2831f, MaxZ = -2818f;

    private float _side = 1f;
    private double _nextSwitch, _pauseUntil;
    private readonly System.Random _rng = new(7);

    /// <summary>
    /// Where to walk this frame (world x, z; zero to stand). Outside the range band she closes in or
    /// backs off; inside it she strafes across his line, changing side every 2-3 s with the odd short
    /// pause, while drifting toward the ideal range. Near a wall of the hall she is steered back inside.
    /// </summary>
    private float2 Steer(float3 necro, float3 dir, float dist, bool dashing)
    {
        if (dashing) return new float2(-dir.x, -dir.z);
        float3 want;
        if (dist > Far) want = dir;
        else if (dist < BackOff) want = -dir;
        else
        {
            if (_now >= _nextSwitch)
            {
                _side = -_side;
                _nextSwitch = _now + 2.0 + _rng.NextDouble();
                if (_rng.NextDouble() < 0.25) _pauseUntil = _now + 0.8;
            }
            if (_now < _pauseUntil) return float2.zero;
            float3 across = new(dir.z * _side, 0f, -dir.x * _side);
            float drift = math.clamp((dist - Ideal) / 3f, -0.5f, 0.5f);
            want = across + dir * drift;
        }
        // Stay on the hall floor: turn back from a wall.
        float3 next = necro + want * 1.5f;
        if (next.x < MinX || next.x > MaxX) { want.x = -want.x; _side = -_side; }
        if (next.z < MinZ || next.z > MaxZ) { want.z = -want.z; _side = -_side; }
        want.y = 0f;
        return math.lengthsq(want) > 1e-4f ? math.normalize(want).xz : float2.zero;
    }

    internal bool Locked
    {
        get => _locked;
        set
        {
            _locked = value;
            if (!value) { UnityEngine.Cursor.visible = true; Moving = false; Move = float2.zero; _aimValid = false; }
        }
    }

    /// <summary>One frame. <paramref name="dracula"/> and <paramref name="necro"/> are client positions.</summary>
    internal void Tick(double now, float3 dracula, float3 necro)
    {
        _now = now;
        if (!Locked) return;
        // The cursor no longer steers her, so it is only in the way of the shot.
        if (UnityEngine.Cursor.visible) UnityEngine.Cursor.visible = false;

        float3 toDracula = dracula - necro; toDracula.y = 0f;
        if (math.lengthsq(toDracula) < 0.01f) { _aimValid = false; return; }
        float3 dir = math.normalize(toDracula);

        bool dashing = _now - _lastDash < DashAimSeconds;
        if (!dashing && math.length(toDracula) < DashRange && _now - _lastDash > DashCooldown)
        {
            _lastDash = _now;
            dashing = true;
            _ctx.Net.SendToServer(DashEventId, 1);
        }

        Move = Moving ? Steer(necro, dir, math.length(toDracula), dashing) : float2.zero;

        // The aim is a point on the floor (the game turns the cursor into one): at his feet, or 5 m
        // straight away from him while dashing.
        _aim = dashing
            ? necro - dir * 5f
            : new float3(dracula.x, necro.y, dracula.z);
        _aimFrom = necro;
        _aimValid = true;
        _aimAtTarget = !dashing;
    }

    private float3 _aim, _aimFrom;
    private bool _aimValid, _aimAtTarget;

    /// <summary>Dracula's client entity: also written as the hovered unit, which targeted spells lock onto.</summary>
    internal Unity.Entities.Entity Target;

    // ---- movement --------------------------------------------------------------------------------

    /// <summary>A world-space (x, z) direction to walk this frame, or zero. Written into the local player's
    /// <c>EntityInput.Movement</c> just after the game's <c>GameplayInputSystem</c> fills it from the keys.</summary>
    internal float2 Move;

    internal unsafe void WriteMovement(Unity.Entities.EntityManager em, Unity.Entities.Entity player)
    {
        if (!Locked && math.lengthsq(Move) == 0f) return;
        if (player == Unity.Entities.Entity.Null || !em.Exists(player)) return;
        var type = Progenitor.Ecs.ComponentIndex.IndexOf<ProjectM.EntityInput>();
        if (!em.HasComponent(player, Unity.Entities.ComponentType.FromTypeIndex(type))) return;
        byte* raw = (byte*)em.GetComponentDataRawRW(player, type);
        if (raw == null) return;
        // Movement only while moving: an aim-only lock leaves the player's own keys alone.
        if (Moving) *(float2*)(raw + 0x0) = Move; // EntityInput.Movement @0x0 (client metadata)
        if (!Locked || !_aimValid) return;
        // EntityInput aim fields (client metadata): AimPosition @0x8, AimPositionPlane @0x14,
        // AimDirection @0x20, AimLength @0x2C. Read live 2026-09-25 from a cursor-driven player: both
        // positions are the floor point, the direction is flat and normalized, the length is the distance.
        float3 flat = _aim - _aimFrom; flat.y = 0f;
        float len = math.length(flat);
        if (len < 1e-3f) return;
        *(float3*)(raw + 0x8) = _aim;
        *(float3*)(raw + 0x14) = _aim;
        *(float3*)(raw + 0x20) = flat / len;
        *(float*)(raw + 0x2C) = len;
        // HoveredEntity @0x30 and HoveredEntityNetworkId @0x38 (12 bytes; client metadata):
        // the unit under the cursor, which targeted spells lock onto. Left to the game, it is whatever the
        // idle mouse happens to be over (read live 2026-09-25 as the necromancer herself). Dracula while
        // aiming at him; nothing while dashing away.
        var netType = Progenitor.Ecs.ComponentIndex.IndexOf<ProjectM.Network.NetworkId>();
        if (_aimAtTarget && Target != Unity.Entities.Entity.Null && em.Exists(Target)
            && em.HasComponent(Target, Unity.Entities.ComponentType.FromTypeIndex(netType)))
        {
            byte* net = (byte*)em.GetComponentDataRawRO(Target, netType);
            if (net == null) return;
            *(Unity.Entities.Entity*)(raw + 0x30) = Target;
            Buffer.MemoryCopy(net, raw + 0x38, 12, 12);
        }
        else
        {
            *(Unity.Entities.Entity*)(raw + 0x30) = Unity.Entities.Entity.Null;
            for (int i = 0; i < 12; i++) raw[0x38 + i] = 0;
        }
    }
}
