using System;
using Progenitor.Plugins;
using Progenitor.Units;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.NecroArmyExample;

/// <summary>
/// Client half of the camera. Plays the server's cues through <c>ctx.Camera</c>:
/// <list type="bullet">
/// <item>fixed shots and a reset;</item>
/// <item>an orbit around Dracula that follows his live position;</item>
/// <item>the fight director, which cycles over-the-shoulder, orbit, hero, side-tracking and crane
/// shots, cutting at each change and moving continuously within a shot;</item>
/// <item>HUD off/on and film lighting for clean footage.</item>
/// </list>
/// It also logs Dracula's screen position twice a second (<c>aim-target x y</c>), plus, while he is
/// within 6 m of the necromancer, a point 5 m directly away from him (<c>aim-away x y</c>), so an
/// operator steering her aim can find him and dash away under any shot. Everything runs on posted
/// main-thread steps owned by the plugin, so unload stops it; unload also turns the HUD back on.
/// </summary>
internal sealed class NecroCamera
{
    private const int DraculaGuid = -327335305; // CHAR_Vampire_Dracula_VBlood
    private const double Step = 0.016; // every frame at 60 fps: the aim lock follows the camera

    // The floor of Dracula's throne hall (x 700-740, z -2808..-2848; mapped from above, 2026-09-25),
    // kept clear of the corner structures (x < 703) and the east statue (x > 740). A
    // camera past a railing leaves the hall's lighting volume and the shot turns flat and bright, so
    // moving shots are clamped inside it.
    private const float HallMinX = 704f, HallMaxX = 737f, HallMinZ = -2842f, HallMaxZ = -2812f;

    private enum Mode { Idle, Orbit, Director }

    private readonly PluginContext _ctx;
    private readonly NecroHud _hud;
    private readonly FilmLighting _lighting;
    private readonly AimLock _aim;
    private readonly ArenaPillars _pillars;
    private readonly NearCameraCutaway _cutaway;
    private CameraCue _orbit;
    private Mode _mode;
    private double _modeStart, _now, _nextAim, _nextLookup;
    private int _shot = -1;
    private Entity _dracula, _necro;


    internal NecroCamera(PluginContext ctx)
    {
        _ctx = ctx;
        _hud = new NecroHud(ctx);
        _lighting = new FilmLighting(ctx);
        _aim = new AimLock(ctx);
        _pillars = new ArenaPillars(ctx);
        _cutaway = new NearCameraCutaway(ctx);
    }

    internal void Start()
    {
        Progenitor.Dev.MainThread.PostDelayed(Step, Tick, _ctx.Owner);
        // Movement and aim go into two EntityInputs: the character's, and the one on the local input
        // controller entity (the entity holding the InputCommandBufferElement buffer, not the character;
        // found live 2026-09-25). The game fills the controller's from the keys and mouse, and
        // CreateInputCommandsSystem copies that one into the command sent to the server
        // (InputCommandBufferElement.RawInput in the client metadata). Writing only the character's
        // turned her on this screen while the server cast where the mouse pointed.
        // Written after GameplayInputSystem fills them and again just before the command is built.
        _ctx.Hooks.After<ProjectM.GameplayInputSystem>(_ => WriteInput());
        _ctx.Hooks.Before(typeof(ProjectM.CreateInputCommandsSystem), _ => WriteInput());
    }

    private Entity _controller;

    private void WriteInput()
    {
        var world = _ctx.World;
        if (world == null) return;
        var em = world.EntityManager;
        _aim.WriteMovement(em, _necro);
        var controller = InputController(em);
        if (controller != Entity.Null && controller != _necro) _aim.WriteMovement(em, controller);
    }

    /// <summary>The local entity whose EntityInput becomes the outgoing input command (it holds the command buffer).</summary>
    private Entity InputController(EntityManager em)
    {
        if (_controller != Entity.Null && em.Exists(_controller)) return _controller;
        _controller = Entity.Null;
        var type = ComponentType.FromTypeIndex(Progenitor.Ecs.ComponentIndex.IndexOf<ProjectM.Network.InputCommandBufferElement>());
        var q = em.CreateEntityQuery(type);
        var all = q.ToEntityArray(Unity.Collections.Allocator.Temp);
        try { if (all.Length == 1) _controller = all[0]; }
        finally { all.Dispose(); q.Dispose(); }
        return _controller;
    }

    internal void Apply(CameraCue cue)
    {
        // A cut changes where Dracula is on screen: report his new position at once.
        _nextAim = 0;
        switch (cue.Kind)
        {
            case 0: _mode = Mode.Idle; _ctx.Camera.Reset(); break;
            case 1: _mode = Mode.Idle; Frame(new float3(cue.Px, cue.Py, cue.Pz), new float3(cue.Lx, cue.Ly, cue.Lz), cue.Instant != 0); break;
            case 2: _orbit = cue; _mode = Mode.Orbit; _modeStart = _now; break;
            case 3: _mode = Mode.Director; _modeStart = _now; _shot = -1; break;
            case 4: _hud.SetVisible(cue.Instant != 0); break;
            case 5: _lighting.Set(cue.Instant != 0); break;
            case 9: _pillars.SetHidden(cue.Instant != 0); _cutaway.Enabled = cue.Instant != 0; break;
            case 7:
                // Instant: 0 free, 1 lock (aim only), 2 lock and move.
                _aim.Locked = cue.Instant != 0;
                _aim.Moving = cue.Instant == 2;
                _ctx.Log.LogInfo("Necro army: aim " + (cue.Instant == 0 ? "free" : cue.Instant == 1 ? "locked on Dracula" : "locked and moving"));
                break;
        }
    }

    private void Tick()
    {
        _now += Step;
        _hud.Tick();
        _cutaway.Tick(_now);
        _pillars.Tick(_now);
        try
        {
            if (TryFind(out float3 d, out float3 c))
            {
                if (_mode == Mode.Orbit) Orbit(d, (float)(_now - _modeStart), _orbit.Px, _orbit.Py, _orbit.Pz, _orbit.Lx, _orbit.Ly, _orbit.Lz, cut: false);
                else if (_mode == Mode.Director) Direct(d, c);
                _aim.Target = _dracula;
                _aim.Tick(_now, d, c);
                if (_now >= _nextAim) { Report(d, c); _nextAim = _now + 0.25; }
            }
        }
        catch (Exception e)
        {
            _mode = Mode.Idle;
            _ctx.Log.LogWarning("Necro army: camera step threw: " + e.Message);
        }
        Progenitor.Dev.MainThread.PostDelayed(Step, Tick, _ctx.Owner);
    }

    // ---- shots ----------------------------------------------------------------------------------

    // Shots in order, cut like a sports broadcast: wide, over-the-shoulder, sideline, side-tracking,
    // orbit, end zone, sideline. None looks at her head-on: she is seen in profile or from behind,
    // always with Dracula in the same frame.
    private static readonly int[] ShotOrder = { 5, 0, 2, 3, 1, 4, 2 };
    private static readonly float[] ShotSeconds = { 8f, 7f, 8f, 7f, 7f, 7f, 8f };

    /// <summary>The fight director: which shot is up, and a cut at each change.</summary>
    private void Direct(float3 d, float3 c)
    {
        double total = 0;
        foreach (var s in ShotSeconds) total += s;
        double t = (_now - _modeStart) % total;
        int slot = 0;
        while (t >= ShotSeconds[slot]) { t -= ShotSeconds[slot]; slot++; }
        bool cut = slot != _shot;
        _shot = slot;
        float u = (float)(t / ShotSeconds[slot]); // 0..1 through the shot
        int shot = ShotOrder[slot];

        // The line of action: necromancer to Dracula, flattened.
        float3 toD = d - c; toD.y = 0f;
        float3 f = math.lengthsq(toD) > 0.01f ? math.normalize(toD) : new float3(1f, 0f, 0f);
        float3 r = new(f.z, 0f, -f.x), up = new(0f, 1f, 0f);

        switch (shot)
        {
            case 0: // Over the shoulder, dollying in: her back to us, the fight beyond her.
                Frame(c - f * math.lerp(4.2f, 3.2f, u) + r * 1.3f + up * 2.6f, d + up * 1.2f, cut);
                break;
            case 1: // Orbit Dracula, pushing in.
                Orbit(d, (float)t, math.degrees(math.atan2(-f.z, -f.x)) + 40f, 7f, 9f, 7f, 3.5f, 9f, cut);
                break;
            case 2: // Sideline: a broadcast camera high on the long side of the hall. It stays where it
                    // is and pans to follow the action, drifting a little along the rail.
            {
                float3 m = (c + d) * 0.5f;
                float sideZ = math.abs(m.z - HallMinZ) > math.abs(m.z - HallMaxZ) ? HallMinZ : HallMaxZ;
                float x = math.clamp(m.x + math.lerp(-1.5f, 1.5f, u), HallMinX + 2f, HallMaxX - 2f);
                Frame(new float3(x, 15f + 7.5f, sideZ), m + up * 0.8f, cut);
                break;
            }
            case 3: // Side tracking: the army in profile, dollying along the line of action.
            {
                float3 m = (c + d) * 0.5f;
                float slide = math.lerp(-3f, 3f, u);
                Frame(m + r * 7.5f + f * slide + up * 3.5f, m + f * slide + up * 1f, cut);
                break;
            }
            case 5: // Wide: the whole battle, both of them in frame, from high on the open side of the hall.
            {
                float3 m = (c + d) * 0.5f;
                float span = math.distance(c.xz, d.xz);
                float back = math.max(12f, span * 1.1f + 7f);
                // Of the two sides of the line of action, take the one with more floor behind the camera.
                float3 side = (m + r * back).z < HallMinZ || (m + r * back).z > HallMaxZ ? -r : r;
                float3 from = m + side * back - f * math.lerp(2f, -2f, u) + up * math.lerp(5.5f, 5f, u);
                Frame(from, m + up * 0.5f, cut);
                break;
            }
            default: // End zone: high behind her, looking down the line of action at Dracula, rising slowly.
                Frame(c - f * 9f + r * 2f + up * math.lerp(5.5f, 6.5f, u), d + up * 0.5f, cut);
                break;
        }
    }

    private void Orbit(float3 d, float t, float startDegrees, float degreesPerSecond, float radiusFrom, float radiusTo, float height, float pushSeconds, bool cut)
    {
        float push = pushSeconds > 0f ? math.saturate(t / pushSeconds) : 1f;
        // Ease the push-in so it starts and ends gently.
        float eased = push * push * (3f - 2f * push);
        float radius = math.lerp(radiusFrom, radiusTo, eased);
        float angle = math.radians(startDegrees + degreesPerSecond * t);
        // A slow rise and fall so the circle doesn't read as a turntable.
        float h = height + 0.8f * math.sin(t * 0.35f);
        Frame(new float3(d.x + math.cos(angle) * radius, d.y + h, d.z + math.sin(angle) * radius), d + new float3(0f, 1.5f, 0f), cut);
    }

    /// <summary>Place the camera, clamped to the hall floor; a cut jumps, otherwise it glides.</summary>
    private void Frame(float3 from, float3 at, bool cut)
    {
        from = Clamp(from);
        _ctx.Camera.FreeShotLookAt(new UnitVec3(from.x, from.y, from.z), new UnitVec3(at.x, at.y, at.z), instant: cut);
    }

    // The hall's chandeliers hang at y 21-25 over these spots (measured live, 2026-09-25). They are
    // baked static-scene geometry, which ignores DisableRendering, so the camera keeps out of them:
    // within 4 m of one it stays below y 20.
    private static readonly float2[] Chandeliers =
    {
        new(705f, -2822f), new(705f, -2833f), new(715f, -2818f), new(715f, -2838f),
        new(725f, -2818f), new(725f, -2838f), new(735f, -2822f), new(735f, -2833f),
    };

    private static float3 Clamp(float3 p)
    {
        p = new float3(math.clamp(p.x, HallMinX, HallMaxX), p.y, math.clamp(p.z, HallMinZ, HallMaxZ));
        if (p.y > 20f)
            foreach (var c in Chandeliers)
                if (math.distance(p.xz, c) < 4f) { p.y = 20f; break; }
        return p;
    }

    // ---- operator aid ---------------------------------------------------------------------------

    private void Report(float3 d, float3 c)
    {
        var cam = UnityEngine.Camera.main;
        if (cam == null) return;
        var s = cam.WorldToScreenPoint(new UnityEngine.Vector3(d.x, d.y + 1f, d.z));
        if (s.z > 0f) _ctx.Log.LogInfo("aim-target " + (int)s.x + " " + (UnityEngine.Screen.height - (int)s.y));
        float3 flat = d - c; flat.y = 0f;
        if (math.length(flat) < 6f && math.lengthsq(flat) > 0.01f)
        {
            // 5 m directly away from Dracula: where a dash should aim.
            float3 away = c - math.normalize(flat) * 5f;
            var a = cam.WorldToScreenPoint(new UnityEngine.Vector3(away.x, away.y, away.z));
            if (a.z > 0f) _ctx.Log.LogInfo("aim-away " + (int)a.x + " " + (UnityEngine.Screen.height - (int)a.y));
        }
    }

    // ---- entities -------------------------------------------------------------------------------

    /// <summary>
    /// Dracula's and the necromancer's client positions. Entities are looked up once, and again when
    /// they go away; the scan walks every prefab-bearing entity, so it runs at most once a second.
    /// </summary>
    private bool TryFind(out float3 dracula, out float3 necro)
    {
        dracula = necro = default;
        var world = _ctx.World;
        if (world == null) return false;
        var em = world.EntityManager;
        if (_dracula == Entity.Null || !em.Exists(_dracula) || _necro == Entity.Null || !em.Exists(_necro))
        {
            if (_now < _nextLookup) return false;
            _nextLookup = _now + 1.0;
            _dracula = _necro = Entity.Null;
            var q = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabGUID>(), ComponentType.ReadOnly<LocalToWorld>());
            var all = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (var e in all)
                {
                    if (_dracula == Entity.Null && em.GetComponentData<PrefabGUID>(e).GuidHash == DraculaGuid) _dracula = e;
                    else if (_necro == Entity.Null && em.HasComponent<ProjectM.PlayerCharacter>(e)) _necro = e;
                }
            }
            finally { all.Dispose(); }
            if (_dracula == Entity.Null || _necro == Entity.Null) return false;
        }
        dracula = em.GetComponentData<LocalToWorld>(_dracula).Position;
        necro = em.GetComponentData<LocalToWorld>(_necro).Position;
        return math.lengthsq(dracula) > 1f && math.lengthsq(necro) > 1f;
    }
}

/// <summary>
/// Clean footage: hides the HUD pieces a clip doesn't need and leaves the ability bar and every menu
/// working. Hidden: the quest tracker (<c>JournalCanvas</c>), clock and minimap (<c>HUDClockCanvas</c>),
/// chat (<c>HUDChatParent</c> inside the main HUD canvas), the build/version text
/// (<c>CanvasUnscaledMainMenu</c>), nameplates and health bars (<c>CharacterHUDsCanvas</c>), the target
/// panel (<c>TargetInfoPanelCanvas</c>) and floating combat text (<c>SCTCanvas</c>). The menus
/// (<c>MenuParent</c>, including Options) and the ability bar (<c>BottomBarCanvas</c>) stay. Names read
/// live from the client's root canvases, 2026-09-25. Only what this plugin hid is shown again, and
/// unload always shows it again.
/// </summary>
internal sealed class NecroHud : IDisposable
{
    private static readonly System.Collections.Generic.HashSet<string> Canvases = new()
    {
        "JournalCanvas", "HUDClockCanvas", "CanvasUnscaledMainMenu", "CharacterHUDsCanvas", "TargetInfoPanelCanvas", "SCTCanvas",
    };

    /// <summary>Objects hidden inside a canvas that otherwise stays (the main HUD canvas also holds popups and fades).</summary>
    private static readonly System.Collections.Generic.HashSet<string> Children = new() { "HUDChatParent", "TooltipParent", "TooltipManager" };

    private readonly PluginContext _ctx;
    private readonly System.Collections.Generic.List<UnityEngine.Canvas> _canvases = new();
    private readonly System.Collections.Generic.List<UnityEngine.GameObject> _objects = new();

    internal NecroHud(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Track(this);
    }

    internal void SetVisible(bool visible)
    {
        Dispose();
        if (visible) return;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<UnityEngine.Canvas>())
        {
            if (c == null || !c.isRootCanvas) continue;
            if (Canvases.Contains(c.gameObject.name) && c.enabled)
            {
                c.enabled = false;
                _canvases.Add(c);
                continue;
            }
            var t = c.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i).gameObject;
                if (!Children.Contains(child.name) || !child.activeSelf) continue;
                child.SetActive(false);
                _objects.Add(child);
            }
        }
        // The operator's mouse steers her aim during a take; the cursor itself shouldn't be on screen.
        UnityEngine.Cursor.visible = false;
        _cursorHidden = true;
        _ctx.Log.LogInfo("Necro army: HUD hidden (" + _canvases.Count + " canvases, " + _objects.Count + " panels)");
    }

    private bool _cursorHidden;

    /// <summary>The game shows the cursor again when it likes; re-hide it every frame while hidden.</summary>
    internal void Tick()
    {
        if (_cursorHidden && UnityEngine.Cursor.visible) UnityEngine.Cursor.visible = false;
    }

    public void Dispose()
    {
        if (_cursorHidden) { UnityEngine.Cursor.visible = true; _cursorHidden = false; }
        foreach (var c in _canvases) if (c != null) c.enabled = true;
        foreach (var o in _objects) if (o != null) o.SetActive(true);
        if (_canvases.Count + _objects.Count > 0) _ctx.Log.LogInfo("Necro army: HUD restored");
        _canvases.Clear();
        _objects.Clear();
    }
}
/// <summary>
/// Film lighting for the throne hall. The scene's post-process profile ("Scene PostProcess Profile")
/// has a fixed exposure (5.5 EV), white fog at density 0.03, and no color-adjustment exposure or
/// contrast (values read live, 2026-09-25). Low, level free-camera shots look down the length of the
/// pale-floored hall and read far brighter than the game's top-down view, so for filming this lowers
/// post-exposure, adds contrast, and thins and darkens the fog. The original values are saved and put
/// back on <c>Set(false)</c> and on unload.
/// </summary>
internal sealed class FilmLighting : IDisposable
{
    private const string ProfileName = "Scene PostProcess Profile";
    private const float PostExposure = -0.7f, Contrast = 20f, FogDensity = 0.015f;
    private static readonly UnityEngine.Color FogColor = new(0.22f, 0.25f, 0.33f, 1f);

    private readonly PluginContext _ctx;
    private readonly System.Collections.Generic.List<Action> _restore = new();

    internal FilmLighting(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Track(this);
    }

    internal void Set(bool film)
    {
        Dispose();
        if (!film) return;
        foreach (var v in UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.Volume>())
        {
            var profile = v.sharedProfile;
            if (profile == null || profile.name != ProfileName) continue;
            foreach (var c in profile.components)
            {
                string type = c.GetIl2CppType().Name;
                var ps = c.parameters;
                if (type == "ColorAdjustments" && ps.Count > 3)
                {
                    SetFloat(ps[1], PostExposure); // postExposure
                    SetFloat(ps[3], Contrast);     // contrast
                }
                else if (type == "StunlockFogVolumeComponent" && ps.Count > 7)
                {
                    SetFloat(ps[6], FogDensity);   // density
                    SetColor(ps[7], FogColor);     // fog color
                }
            }
        }
        _ctx.Log.LogInfo("Necro army: film lighting on (" + _restore.Count + " values changed)");
    }

    private void SetFloat(UnityEngine.Rendering.VolumeParameter p, float value)
    {
        var f = p.TryCast<UnityEngine.Rendering.VolumeParameter<float>>();
        if (f == null) return;
        float was = f.value; bool wasOverride = p.overrideState;
        _restore.Add(() => { f.value = was; p.overrideState = wasOverride; });
        p.overrideState = true;
        f.value = value;
    }

    private void SetColor(UnityEngine.Rendering.VolumeParameter p, UnityEngine.Color value)
    {
        var c = p.TryCast<UnityEngine.Rendering.VolumeParameter<UnityEngine.Color>>();
        if (c == null) return;
        var was = c.value; bool wasOverride = p.overrideState;
        _restore.Add(() => { c.value = was; p.overrideState = wasOverride; });
        p.overrideState = true;
        c.value = value;
    }

    public void Dispose()
    {
        if (_restore.Count == 0) return;
        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            try { _restore[i](); } catch (Exception e) { _ctx.Log.LogWarning("Necro army: lighting restore threw: " + e.Message); }
        }
        _ctx.Log.LogInfo("Necro army: film lighting off (" + _restore.Count + " values restored)");
        _restore.Clear();
    }
}

/// <summary>
/// Hides the four arena pillars around Dracula's fight (<c>TM_Destructible_DraculasCastle_Pillar01</c>
/// and the broken <c>TM_Dracula_DestroyedPillar01</c>, at
/// x 710/730, z -2819.5/-2835.5; found live 2026-09-25) on this client only, by switching off the
/// rendering of every entity in each pillar's <c>LinkedEntityGroup</c>. Nothing changes on the server:
/// the pillars' collision stays, and the world save is untouched. Shown again on <c>SetHidden(false)</c>
/// and on unload.
/// </summary>
internal sealed class ArenaPillars : IDisposable
{
    // The standing columns and their broken versions, which share the four spots around the fight.
    private static readonly int[] PillarGuids =
    {
        1934867279,  // TM_Destructible_DraculasCastle_Pillar01 (the fluted columns, 12 render parts each)
        -798366867,  // TM_Dracula_DestroyedPillar01
    };

    private static readonly float2[] Chandeliers =
    {
        new(705f, -2822f), new(705f, -2833f), new(715f, -2818f), new(715f, -2838f),
        new(725f, -2818f), new(725f, -2838f), new(735f, -2822f), new(735f, -2833f),
    };

    private readonly PluginContext _ctx;
    private readonly System.Collections.Generic.List<Entity> _hidden = new();

    internal ArenaPillars(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Track(this);
    }

    private bool _active;
    private double _nextRescan;

    /// <summary>
    /// Hides pillars that appeared since the last pass. Dracula's spawn chain restores a pillar within
    /// seconds of it being destroyed (seen live 2026-09-25), so while hidden this rescans once a second.
    /// </summary>
    internal void Tick(double now)
    {
        if (!_active || now < _nextRescan) return;
        _nextRescan = now + 1.0;
        var world = _ctx.World;
        if (world == null) return;
        var em = world.EntityManager;
        var disable = ComponentType.FromTypeIndex(Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.DisableRendering>());
        HidePillars(em, disable);
    }

    internal void SetHidden(bool hidden)
    {
        Dispose();
        _active = hidden;
        if (!hidden) return;
        var world = _ctx.World;
        if (world == null) return;
        var em = world.EntityManager;
        var disable = ComponentType.FromTypeIndex(Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.DisableRendering>());
        int count = HidePillars(em, disable);


        // The eight chandeliers hang between the columns at camera height (x 705/715/725/735,
        // z -2818/-2822/-2833/-2838, y 21-25; measured live): every render part inside a 2.5 m box
        // around each.
        int before = _hidden.Count;
        var bounds = Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.WorldRenderBounds>();
        var rq = em.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.FromTypeIndex(bounds) },
            None = new[] { ComponentType.ReadOnly<ProjectM.Health>() },
        });
        var parts = rq.ToEntityArray(Unity.Collections.Allocator.Temp);
        try
        {
            unsafe
            {
                foreach (var e in parts)
                {
                    float* b = (float*)em.GetComponentDataRawRO(e, bounds);
                    if (b == null || b[1] - b[4] < 19f || b[1] + b[4] > 28f) continue;
                    bool near = false;
                    foreach (var c in Chandeliers)
                        if (math.abs(b[0] - c.x) < 2.5f && math.abs(b[2] - c.y) < 2.5f) { near = true; break; }
                    if (!near || em.HasComponent(e, disable)) continue;
                    em.AddComponent(e, disable);
                    _hidden.Add(e);
                }
            }
        }
        finally { parts.Dispose(); rq.Dispose(); }
        _ctx.Log.LogInfo("Necro army: arena pillars hidden (" + count + " pillars, " + before + " render entities) and chandeliers (" + (_hidden.Count - before) + " render entities)");
    }


    /// <summary>Hides every render part of each pillar (standing or broken) not hidden yet. Returns the pillars seen.</summary>
    private int HidePillars(EntityManager em, ComponentType disable)
    {
        var q = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabGUID>(), ComponentType.ReadOnly<LinkedEntityGroup>());
        var pillars = q.ToEntityArray(Unity.Collections.Allocator.Temp);
        int count = 0;
        try
        {
            foreach (var pillar in pillars)
            {
                if (Array.IndexOf(PillarGuids, em.GetComponentData<PrefabGUID>(pillar).GuidHash) < 0) continue;
                count++;
                var group = em.GetBuffer<LinkedEntityGroup>(pillar);
                var members = new System.Collections.Generic.List<Entity>();
                for (int i = 0; i < group.Length; i++) members.Add(group[i].Value);
                foreach (var m in members)
                {
                    if (!em.Exists(m) || em.HasComponent(m, disable)) continue;
                    em.AddComponent(m, disable);
                    _hidden.Add(m);
                }
            }
        }
        finally { pillars.Dispose(); q.Dispose(); }
        return count;
    }

    public void Dispose()
    {
        if (_hidden.Count == 0) return;
        var world = _ctx.World;
        if (world != null)
        {
            var em = world.EntityManager;
            var disable = ComponentType.FromTypeIndex(Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.DisableRendering>());
            foreach (var m in _hidden)
                if (em.Exists(m) && em.HasComponent(m, disable)) em.RemoveComponent(m, disable);
        }
        _ctx.Log.LogInfo("Necro army: arena pillars shown again");
        _hidden.Clear();
    }
}

/// <summary>
/// Scenery that comes within <see cref="Radius"/> of the camera is not drawn (banners, chandeliers,
/// braziers the free camera passes through or close to), and is drawn again once the camera moves
/// away. Candidates are the rendered pieces around the throne hall that are not units (no
/// <c>Health</c>) and are smaller than 12 m (floors and walls stay); their bounds are cached and
/// refreshed every 10 s. Baked static-scene pieces (<c>StaticSceneTag</c>, e.g. the chandeliers) ignore
/// <c>DisableRendering</c> and stay drawn. Client only; everything hidden is shown again when disabled or unloaded.
/// </summary>
internal sealed class NearCameraCutaway : IDisposable
{
    private const float Radius = 4f;
    private const float MinX = 655f, MaxX = 760f, MinZ = -2875f, MaxZ = -2775f;

    private readonly PluginContext _ctx;
    private readonly System.Collections.Generic.List<(Entity E, float3 C, float3 X)> _candidates = new();
    private readonly System.Collections.Generic.HashSet<Entity> _hidden = new();
    private double _nextRefresh;
    private bool _enabled;

    internal NearCameraCutaway(PluginContext ctx)
    {
        _ctx = ctx;
        ctx.Track(this);
    }

    internal bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) Dispose(); else _nextRefresh = 0; }
    }

    internal unsafe void Tick(double now)
    {
        if (!_enabled) return;
        var world = _ctx.World;
        var cam = UnityEngine.Camera.main;
        if (world == null || cam == null) return;
        var em = world.EntityManager;
        var bounds = Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.WorldRenderBounds>();
        if (now >= _nextRefresh)
        {
            _nextRefresh = now + 10.0;
            _candidates.Clear();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.FromTypeIndex(bounds) },
                None = new[] { ComponentType.ReadOnly<ProjectM.Health>() },
            });
            var all = q.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (var e in all)
                {
                    float* b = (float*)em.GetComponentDataRawRO(e, bounds);
                    if (b == null) continue;
                    var c = new float3(b[0], b[1], b[2]);
                    var x = new float3(b[3], b[4], b[5]);
                    if (c.x < MinX || c.x > MaxX || c.z < MinZ || c.z > MaxZ) continue;
                    if (x.x * 2f > 12f || x.z * 2f > 12f || x.y * 2f > 12f) continue;
                    if (c.y + x.y < 15.5f) continue; // flush with the floor
                    _candidates.Add((e, c, x));
                }
            }
            finally { all.Dispose(); q.Dispose(); }
        }

        var disable = ComponentType.FromTypeIndex(Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.DisableRendering>());
        float3 p = (UnityEngine.Vector3)cam.transform.position;
        foreach (var (e, c, x) in _candidates)
        {
            // Distance from the camera to the box around the piece.
            float3 d = math.max(math.abs(p - c) - x, 0f);
            bool near = math.lengthsq(d) < Radius * Radius;
            bool hidden = _hidden.Contains(e);
            if (near == hidden) continue;
            if (!em.Exists(e)) { _hidden.Remove(e); continue; }
            if (near)
            {
                if (em.HasComponent(e, disable)) continue; // already hidden (e.g. by the pillar hide)
                em.AddComponent(e, disable);
                _hidden.Add(e);
            }
            else
            {
                if (em.HasComponent(e, disable)) em.RemoveComponent(e, disable);
                _hidden.Remove(e);
            }
        }
    }

    public void Dispose()
    {
        if (_hidden.Count == 0) return;
        var world = _ctx.World;
        if (world != null)
        {
            var em = world.EntityManager;
            var disable = ComponentType.FromTypeIndex(Progenitor.Ecs.ComponentIndex.IndexOf<Unity.Rendering.DisableRendering>());
            foreach (var e in _hidden)
                if (em.Exists(e) && em.HasComponent(e, disable)) em.RemoveComponent(e, disable);
        }
        _hidden.Clear();
    }
}
