using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using Progenitor.Abilities;
using Progenitor.Commands;
using Progenitor.Items;
using Progenitor.Plugins;
using Progenitor.Units;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.NecroArmyExample;

/// <summary>
/// Demo: a necromancer (the admin who runs the commands) raises a skeleton army out of the ground
/// and, with a point, sends it at Dracula. Filmed as separate shots, each its own command, so any
/// shot can be retaken without restaging the scene. See README.md for the shot list.
///
/// <para>Server: <c>.arena setup|reset</c>, <c>.army summon|attack|status</c>, <c>.cast</c>, <c>.barrage</c>,
/// and camera cues (<c>.camera &lt;shot&gt;</c>, <c>.hud on|off</c>, <c>.lighting film|default</c>) sent to the commanding user. Client: turns
/// each cue into a <c>ctx.Camera</c> shot. Everything spawned, marked or scheduled is owned by the
/// context and released on every unload reason. Her blood (<c>SetBlood</c>, 100 % Draculin) is
/// the exception: like blood a player drinks, it is saved with the character and stays.</para>
/// </summary>
[BepInPlugin("progenitor.necroarmy", "Progenitor Necro Army Example", "0.1.0")]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
// Both sides apply the same prefab edits (SkullReach), so the handshake requires a matching build.
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
public sealed class Plugin : ProgenitorPlugin<NecroContext>
{
    protected override unsafe void OnLoad(NecroContext ctx)
    {
        if (ctx.IsServer)
        {
            var scene = new Scene(ctx);
            ctx.Commands.Register(new ArenaCommands(scene));
            ctx.Commands.Register(new ArmyCommands(scene));
            ctx.Commands.Register(new SceneCommands(scene));
            SkullReach.Apply(ctx);
            // The client's aim lock asks for the dash when Dracula gets close (AimLock).
            ctx.Net.OnReceiveFromClient(AimLock.DashEventId, (id, userEntity, user, payload, length) => scene.DashFor(user.LocalCharacter._Entity));
        }
        else
        {
            var camera = new NecroCamera(ctx);
            ctx.Net.OnReceive(CameraCue.EventId, (id, payload, length) =>
            {
                if (length < sizeof(CameraCue)) return;
                CameraCue cue = *(CameraCue*)payload;
                try { camera.Apply(cue); }
                catch (Exception e) { ctx.Log.LogWarning("Necro army: camera cue " + cue.Kind + " refused: " + e.Message); }
            });
            camera.Start();
        }
        ctx.Log.LogInfo("Necro army: loaded on " + ctx.Side);
    }

    protected override void OnUnload(NecroContext ctx, PluginUnloadReason reason) =>
        ctx.Log.LogInfo("Necro army: unloading, reason=" + reason);
}

public sealed class NecroContext : PluginContext
{
    internal readonly List<IUnitHandle> Army = new();
    internal IUnitHandle? Dracula;
    internal Entity Necro;
    internal Entity NecroUser;
    internal float3 Origin;
    internal float3 Facing = new(1f, 0f, 0f);
    internal float3 DraculaSpot;
    internal DateTime PointedAt = DateTime.MinValue;
    internal IDisposable? NoCooldowns;
    internal readonly List<IDisposable> Slots = new();
    internal IDisposable? Night;
    internal int Take;
}

/// <summary>
/// A camera instruction from the server. Kind 0 resets to top-down; 1 is a free shot at P looking at
/// L; 2 orbits Dracula (<see cref="NecroCamera"/>): start angle Px (degrees), speed Py (degrees a
/// second), radius Pz easing to Lx over Lz seconds, height Ly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CameraCue
{
    /// <summary>'NCR1': outside Progenitor's reserved 0x5072____ range.</summary>
    public const int EventId = 0x4E435231;

    public int Kind;
    public float Px, Py, Pz, Lx, Ly, Lz;
    public int Instant;

    public static CameraCue Reset() => new() { Kind = 0 };

    public static CameraCue Look(float3 from, float3 at, bool instant) => new()
    {
        Kind = 1, Px = from.x, Py = from.y, Pz = from.z, Lx = at.x, Ly = at.y, Lz = at.z, Instant = instant ? 1 : 0,
    };

    public static CameraCue Director() => new() { Kind = 3 };

    public static CameraCue Hud(bool visible) => new() { Kind = 4, Instant = visible ? 1 : 0 };

    /// <summary>Aim lock on Dracula (<see cref="AimLock"/>) on or off.</summary>
    public static CameraCue Aim(int mode) => new() { Kind = 7, Instant = mode };

    /// <summary>Hide (or show) the arena pillars on the client (<see cref="ArenaPillars"/>).</summary>
    public static CameraCue Pillars(bool hidden) => new() { Kind = 9, Instant = hidden ? 1 : 0 };

    /// <summary>Film lighting on (a darker, higher-contrast grade and thinner, darker fog) or back to the game's own.</summary>
    public static CameraCue Lighting(bool film) => new() { Kind = 5, Instant = film ? 1 : 0 };

    public static CameraCue Orbit(float startDegrees, float degreesPerSecond, float radiusFrom, float radiusTo, float height, float pushSeconds) => new()
    {
        Kind = 2, Px = startDegrees, Py = degreesPerSecond, Pz = radiusFrom, Lx = radiusTo, Ly = height, Lz = pushSeconds,
    };
}

/// <summary>
/// The scene's logic. The chat commands (<see cref="ArenaCommands"/>, <see cref="ArmyCommands"/>,
/// <see cref="SceneCommands"/>) are thin, generically named wrappers over it.
/// </summary>
internal sealed class Scene
{
    // Outfit E: Razer hood, Unholy Shroud cloak, Dracula T09 armor.
    private static readonly int[] Outfit = { -1797796642, 1863126275, 1055898174, 125611165, -204401621, 1400688919 };

    private const string Melee = "CHAR_Undead_SkeletonSoldier_Armored_Dunley";
    private const string Crossbow = "CHAR_Undead_ArmoredSkeletonCrossbow_Dunley";
    private const string Mage = "CHAR_Undead_SkeletonMage";
    private const string DeathKnight = "CHAR_Unholy_DeathKnight";
    private const string DraculaPrefab = "CHAR_Vampire_Dracula_VBlood";

    private const int ArmyOfTheDeadBuffSequence = -295480877; // green wisps around the caster
    private const int RaiseDeadCastSequence = 888380782;      // dark ripple on the floor
    private const int PointEmote = -452406649;                // AB_Emote_Vampire_Point_AbilityGroup
    private const double PointSeconds = 3.5;                  // how long the point holds the frame
    private const float NightHour = 23f;                      // held for the length of the scene

    // A gear-level-91 player, measured live: physical 45.885, spell 44.03.
    private const float PlayerPhysical = 45.885f, PlayerSpell = 44.03f;

    private readonly NecroContext _ctx;
    internal Scene(NecroContext ctx) => _ctx = ctx;

    public void Setup(ICommandContext cmd, float distance, float draculaHealth, float heading)
    {
        Clear();
        _ctx.Necro = cmd.CharacterEntity;
        _ctx.NecroUser = cmd.UserEntity;
        _ctx.Origin = cmd.World.EntityManager.GetComponentData<LocalToWorld>(cmd.CharacterEntity).Position;
        // A fixed direction, not the necromancer's facing: a player's rotation belongs to their client
        // (a server write is overwritten the same frame), and an idle character can face a wall. In
        // Dracula's throne hall the default, +X, runs down the long axis toward the throne.
        float rad = math.radians(heading);
        _ctx.Facing = new float3(math.cos(rad), 0f, math.sin(rad));

        int worn = 0;
        foreach (int item in Outfit)
        {
            var r = _ctx.Items.Grant(cmd.CharacterEntity, item, 1, new ItemGrantOptions { Equip = true, WhenPresent = WhenAlreadyPresent.EquipExisting });
            if (r.Ok) worn++;
        }

        // The necromancer stands out: 1.5x her size (Dracula stays taller) and a faint green glow.
        try
        {
            _ctx.Units.SetLook(cmd.CharacterEntity, new UnitLook
            {
                VisualScale = 1.5f,
                Tint = new Progenitor.Effects.EffectColor(0.8f, 1f, 0.8f),
                GlowIntensity = 2f,
            });
        }
        catch (Exception e) { _ctx.Log.LogWarning("Necro army: look refused: " + e.Message); }
        // A vampire's blood: 100% Draculin.
        _ctx.Units.SetBlood(cmd.CharacterEntity, "Draculin", 100f);
        // Her spells without cooldowns; cast times and animations are untouched.
        _ctx.NoCooldowns = _ctx.Abilities.NoCooldowns(cmd.CharacterEntity);
        // Her bar shows the spells she casts: R, C and T (bar slots 5, 6, 7 on a player).
        foreach (var (slot, spell) in BarSpells)
            _ctx.Slots.Add(_ctx.Abilities.SetSlot(cmd.CharacterEntity, slot, Spells[spell]));
        // Every take at the same hour: sunlight through the hall's windows changes its lighting.
        var night = _ctx.Time.HoldTimeOfDay(NightHour);
        if (night.IsLive) _ctx.Night = night;
        else _ctx.Log.LogWarning("Necro army: time hold refused: " + night.RefusedReason);

        float3 at = _ctx.Origin + _ctx.Facing * distance;
        var dracula = _ctx.Units.Spawn(DraculaPrefab, new UnitVec3(at.x, at.y, at.z), new UnitSpawnOptions
        {
            Stats = new UnitStatOverrides { MaxHealth = draculaHealth },
            Permanent = true,
            DropLoot = false,
        });
        if (!dracula.Enabled) { cmd.Reply("Dracula did not spawn."); return; }
        _ctx.Dracula = dracula;
        _ctx.DraculaSpot = at;
        _ctx.Units.HoldAt(dracula, new UnitVec3(at.x, at.y, at.z));

        // Nothing breakable on the hall floor mid-take. The arena pillars stay: Dracula's chain puts
        // them back within seconds and his AI uses them; the client hides them (ArenaPillars).
        int cleared = _ctx.Units.ClearBreakables(HallCenter, HallRadius, guid => guid == ArenaPillarGuid);
        _ctx.Log.LogInfo("Necro army: removed " + cleared + " breakable props from the hall floor");
        Cue(CameraCue.Pillars(true));
        Cue(Shot("wide"));
        cmd.Reply("Necro army: outfit " + worn + "/" + Outfit.Length + ", Dracula held at " + Fmt(at) + ", " + cleared + " breakable props removed.");
    }

    public void Summon(ICommandContext cmd, float interval, float armyHealth, float damage)
    {
        if (_ctx.Necro == Entity.Null) { cmd.Reply("Run .arena setup first."); return; }
        foreach (var h in _ctx.Army) if (!h.IsDisposed) h.Dispose();
        _ctx.Army.Clear();
        int take = ++_ctx.Take;

        _ctx.Effects.ServerBeamMark(_ctx.Necro, 4f + Formation.Build().Count * interval, ArmyOfTheDeadBuffSequence);
        _ctx.Effects.ServerBeamMark(_ctx.Necro, 3f, RaiseDeadCastSequence);

        var stats = new UnitStatOverrides
        {
            Level = 91, MaxHealth = armyHealth, PhysicalPower = PlayerPhysical * damage, SpellPower = PlayerSpell * damage,
        };
        foreach (var slot in Formation.Build())
        {
            var s = slot;
            Progenitor.Dev.MainThread.PostDelayed(1.0 + s.Order * interval, () =>
            {
                if (take != _ctx.Take) return; // a later summon or a reset superseded this one
                Raise(s, stats);
            }, _ctx.Owner);
        }
        cmd.Reply("Necro army: rising, " + Formation.Build().Count + " units over " + (Formation.Build().Count * interval).ToString("0.0") + " s.");
    }

    private void Raise(FormationSlot slot, UnitStatOverrides stats)
    {
        var (x, z) = Formation.ToWorld(slot, _ctx.Origin.x, _ctx.Origin.z, _ctx.Facing.x, _ctx.Facing.z);
        var at = new UnitVec3(x, _ctx.Origin.y, z);
        var h = _ctx.Units.Spawn(Prefab(slot.Rank), at, new UnitSpawnOptions
        {
            Faction = "Faction_Players",
            // On her team, so her projectiles pass through them (units spawn on the generic unit team).
            TeamOf = _ctx.Necro,
            HideHealthBar = true,
            Stats = stats,
            Permanent = true,
            DropLoot = false,
            Entrance = UnitEntrance.RiseFromGround,
        });
        if (!h.Enabled) { _ctx.Log.LogWarning("Necro army: " + slot.Rank + " #" + slot.Order + " did not spawn"); return; }
        _ctx.Army.Add(h);
        _ctx.Units.SetPet(h, _ctx.Necro, PetStance.Hold);
    }

    public void Attack(ICommandContext cmd, float delay, bool direct)
    {
        var dracula = _ctx.Dracula;
        if (dracula == null || dracula.IsDisposed) { cmd.Reply("No Dracula. Run .arena setup."); return; }
        if (_ctx.NecroUser == Entity.Null) _ctx.NecroUser = cmd.UserEntity;
        var world = cmd.World;
        int take = _ctx.Take;
        // Cut to the point shot first. A player faces where the mouse points in the world, and the
        // same screen position means a different world point under a new camera, so a cut made during
        // the gesture turns her away mid-point. Cutting first lets the operator's aim settle on
        // Dracula under the shot the point is seen in.
        Cue(Shot("point"));
        // Then wait until she faces him before pointing; give up waiting after FacingWaitSeconds.
        // The check starts only once the cut has reached the client, and she must hold the facing for
        // FacingHoldSeconds: a check made in the same frame as the cue passed on her old aim (take 7).
        var started = DateTime.UtcNow;
        DateTime? facingSince = null;
        void WaitForFacing()
        {
            if (take != _ctx.Take || dracula.IsDisposed) return;
            float off = FacingError();
            var now = DateTime.UtcNow;
            if (off <= FacingToleranceDegrees) facingSince ??= now; else facingSince = null;
            bool held = facingSince != null && (now - facingSince.Value).TotalSeconds >= FacingHoldSeconds;
            if (held || (now - started).TotalSeconds >= FacingWaitSeconds)
            {
                if (off > FacingToleranceDegrees)
                    _ctx.Log.LogWarning("Necro army: pointing while " + off.ToString("0") + " degrees off Dracula (she did not turn in " + FacingWaitSeconds + " s)");
                Point(world, dracula, delay, direct, take);
                return;
            }
            Progenitor.Dev.MainThread.PostDelayed(0.1, WaitForFacing, _ctx.Owner);
        }
        Progenitor.Dev.MainThread.PostDelayed(CutSettleSeconds, WaitForFacing, _ctx.Owner);
        cmd.Reply("Necro army: pointing once she faces Dracula; charge " + delay + " s after.");
    }

    private const float FacingToleranceDegrees = 15f;
    private const double FacingWaitSeconds = 4.0;
    private const double FacingHoldSeconds = 0.5;
    /// <summary>Time for a camera cue to reach the client before her facing is checked.</summary>
    private const double CutSettleSeconds = 0.5;
    private const int ArenaPillarGuid = 1934867279; // TM_Destructible_DraculasCastle_Pillar01
    // The throne-hall floor (x 698-742, z -2850..-2806, y 15), props along the walls included.
    private static readonly UnitVec3 HallCenter = new(720f, 15f, -2828f);
    private const float HallRadius = 25f;

    /// <summary>Degrees between where the necromancer faces and the direction to Dracula, on the ground plane.</summary>
    private float FacingError()
    {
        var em = _ctx.World!.EntityManager;
        if (!em.Exists(_ctx.Necro)) return 180f;
        var ltw = em.GetComponentData<LocalToWorld>(_ctx.Necro);
        float3 f = ltw.Forward; f.y = 0f;
        float3 to = DraculaPosition() - ltw.Position; to.y = 0f;
        if (math.lengthsq(f) < 1e-4f || math.lengthsq(to) < 1e-4f) return 0f;
        return math.degrees(math.acos(math.clamp(math.dot(math.normalize(f), math.normalize(to)), -1f, 1f)));
    }

    private void Point(World world, IUnitHandle dracula, float delay, bool direct, int take)
    {
        // The point is the beat; the camera is already on her. Afterwards the director takes over.
        bool pointed = AbilityCast.Cast(world, _ctx.Necro, new PrefabGUID(PointEmote), out string why);
        if (!pointed) _ctx.Log.LogWarning("Necro army: point refused: " + why);
        _ctx.PointedAt = DateTime.UtcNow;
        if (direct)
            Progenitor.Dev.MainThread.PostDelayed(PointSeconds, () => { if (take == _ctx.Take) Cue(CameraCue.Director()); }, _ctx.Owner);
        Progenitor.Dev.MainThread.PostDelayed(delay, () =>
        {
            if (take != _ctx.Take || dracula.IsDisposed) return;
            Entity target = dracula.Entity.ToEntity();
            int sent = 0;
            foreach (var h in _ctx.Army)
            {
                if (h.IsDisposed || !h.Enabled) continue;
                try { _ctx.Units.PetAttack(h, target); sent++; }
                catch (Exception e) { _ctx.Log.LogWarning("Necro army: PetAttack " + h.Entity + ": " + e.Message); }
            }
            _ctx.Units.Hold(dracula, false);
            var first = _ctx.Army.Find(h => !h.IsDisposed && h.Enabled);
            if (first != null) _ctx.Units.Engage(dracula, first.Entity.ToEntity());
            _ctx.Log.LogInfo("Necro army: charge, " + sent + " units sent at Dracula");
        }, _ctx.Owner);
    }

    // The necromancer's own unholy spells, cast through the game's forced-cast call. Aimed spells
    // go where her client aims (EntityInput.AimPosition comes from the player's mouse).
    /// <summary>What her bar shows during the scene, and the barrage casts: bar slot, spell.</summary>
    private static readonly (int Slot, string Spell)[] BarSpells = { (5, "skull"), (6, "chains"), (7, "spider") };

    private static readonly Dictionary<string, int> Spells = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skull"] = -1204819086,     // AB_Unholy_CorruptedSkull_AbilityGroup (aimed projectile)
        ["chains"] = -1845982676,    // AB_Unholy_ChainsOfDeath_AbilityGroup
        ["soulburn"] = 2138402840,   // AB_Unholy_Soulburn_AbilityGroup
        ["spider"] = -859352730,     // AB_Unholy_UnstableArachnid_AbilityGroup
        ["ward"] = -1136860480,      // AB_Unholy_WardOfTheDamned_AbilityGroup
        ["angel"] = 1297311521,      // AB_Unholy_SummonFallenAngel_AbilityGroup
        ["dash"] = -433204738,       // AB_Vampire_VeilOfShadow_Group (dashes toward her aim)
    };

    public void Spell(ICommandContext cmd, string name)
    {
        if (!Spells.TryGetValue(name, out int group)) { cmd.Reply("Unknown spell '" + name + "'."); return; }
        cmd.Reply(CastSpell(cmd.World, cmd.CharacterEntity, group) ? "Cast " + name + "." : "Cast of " + name + " refused.");
    }

    public void Barrage(ICommandContext cmd, float seconds, string spells)
    {
        var list = new List<int>();
        foreach (var n in spells.Split(','))
            if (Spells.TryGetValue(n.Trim(), out int g)) list.Add(g);
        if (list.Count == 0) { cmd.Reply("No known spells in '" + spells + "'."); return; }
        var world = cmd.World;
        var necro = cmd.CharacterEntity;
        int take = _ctx.Take, i = 0;
        double wait = Math.Max(0.0, PointSeconds - (DateTime.UtcNow - _ctx.PointedAt).TotalSeconds);
        void Step()
        {
            var d = _ctx.Dracula;
            if (take != _ctx.Take || d == null || d.IsDisposed) return;
            if (!DraculaStanding()) { Victory(world, necro, take); return; }
            CastSpell(world, necro, list[i++ % list.Count]);
            // Her spells hit harder than any one skeleton, so Dracula's aggro would turn to her and he
            // would pull her around. Keep him on the army: a death knight first, else anyone standing.
            var tank = _ctx.Army.Find(h => !h.IsDisposed && h.Enabled && h.Prefab == DeathKnight)
                ?? _ctx.Army.Find(h => !h.IsDisposed && h.Enabled);
            if (tank != null) _ctx.Units.Engage(d, tank.Entity.ToEntity());
            Progenitor.Dev.MainThread.PostDelayed(seconds, Step, _ctx.Owner);
        }
        // Let the point play out first: a cast would cut the emote short.
        if (wait > 0) Progenitor.Dev.MainThread.PostDelayed(wait, Step, _ctx.Owner); else Step();
        cmd.Reply("Barrage: " + list.Count + " spells, every " + seconds + " s, until Dracula falls.");
    }

    private const int BowEmote = -1462274656; // AB_Emote_Vampire_Bow_AbilityGroup

    /// <summary>
    /// Dracula is down: the necromancer stops (aim and movement released), turns to face him, bows and
    /// is done. The camera cuts to her for it.
    /// </summary>
    private void Victory(World world, Entity necro, int take)
    {
        Cue(CameraCue.Aim(1));                 // stop moving, keep facing him for the bow
        Cue(BowShot(necro));
        Progenitor.Dev.MainThread.PostDelayed(1.0, () =>
        {
            if (take != _ctx.Take) return;
            _ctx.Abilities.ForceCast(necro, BowEmote);
            _ctx.Log.LogInfo("Necro army: Dracula is down; bow");
        }, _ctx.Owner);
        // The aim stays locked on where he fell: released, she turns to the idle mouse mid-shot. The
        // operator (or .aim free) releases it after the take.
    }

    /// <summary>
    /// The bow, framed like the point but around where she stands now (she has strafed around the hall
    /// since the start), from the side she faces: toward where Dracula fell.
    /// </summary>
    private CameraCue? BowShot(Entity necro)
    {
        var em = _ctx.World!.EntityManager;
        if (!em.Exists(necro)) return Shot("point");
        return SideShot(em.GetComponentData<LocalToWorld>(necro).Position, DraculaPosition());
    }

    // The throne-hall floor the client's camera is clamped to (NecroCamera.HallMinX...).
    private const float CamMinX = 704f, CamMaxX = 737f, CamMinZ = -2842f, CamMaxZ = -2812f;

    /// <summary>
    /// Both of them from the side, square to the line between them, far enough back to hold the pair
    /// (she is nearer frame left or right, he on the other side). Of the two sides, the one with more
    /// floor, so the client's clamp to the hall does not push the camera in.
    /// </summary>
    private CameraCue SideShot(float3 her, float3 him)
    {
        float3 f = him - her; f.y = 0f;
        float span = math.length(f);
        f = span > 0.1f ? f / span : _ctx.Facing;
        float3 r = new(f.z, 0f, -f.x), up = new(0f, 1f, 0f);
        // Camera square to a point a third of the way to him, so she is the nearer, larger figure, and
        // high enough (about 5 m) to see her over the ranks standing around her (take 15 hid her).
        float near = math.min(span, 14f);
        float3 m = her + f * near * 0.35f;
        float back = math.max(7f, near * 0.8f + 2f);
        float3 a = m + r * back, b = m - r * back;
        float3 side = Room(a) >= Room(b) ? r : -r;
        return CameraCue.Look(m + side * back + up * (3.2f + back * 0.18f), her + f * near * 0.45f + up * 1f, true);
    }

    /// <summary>How far a camera spot is inside the hall floor (negative when outside).</summary>
    private static float Room(float3 p) =>
        math.min(math.min(p.x - CamMinX, CamMaxX - p.x), math.min(p.z - CamMinZ, CamMaxZ - p.z));

    private bool DraculaStanding()
    {
        try { return _ctx.Units.GetHealth(_ctx.Dracula!).IsStanding; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// <c>ctx.Abilities.ForceCast</c>: starts the spell even when it is not on her bar
    /// (<c>AbilityCast.Cast</c> only requests a cast, and an off-bar group never starts).
    /// </summary>
    private bool CastSpell(World world, Entity caster, int group) => _ctx.Abilities.ForceCast(caster, group);

    public void Cam(ICommandContext cmd, string shot)
    {
        if (_ctx.Necro == Entity.Null) { _ctx.Necro = cmd.CharacterEntity; _ctx.NecroUser = cmd.UserEntity; }
        var cue = Shot(shot);
        if (cue == null) { cmd.Reply("Unknown shot '" + shot + "'. Try wide, summon, point, charge, fight, flank, orbit, orbitlow, battle, director, top."); return; }
        cmd.Reply(Cue(cue.Value) ? "Camera: " + shot : "Camera cue not sent (is the client running this plugin?).");
    }

    public void Look(ICommandContext cmd, float3 from, float3 at)
    {
        if (_ctx.NecroUser == Entity.Null) _ctx.NecroUser = cmd.UserEntity;
        cmd.Reply(Cue(CameraCue.Look(from, at, true)) ? "Camera: look." : "Camera cue not sent.");
    }

    public void AimMode(ICommandContext cmd, string mode)
    {
        if (_ctx.NecroUser == Entity.Null) _ctx.NecroUser = cmd.UserEntity;
        int m = mode.Equals("free", StringComparison.OrdinalIgnoreCase) ? 0 : mode.Equals("move", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        cmd.Reply(Cue(CameraCue.Aim(m)) ? "Aim " + (m == 0 ? "free" : m == 1 ? "locked on Dracula" : "locked, keeping range and strafing") + "." : "Aim cue not sent.");
    }

    /// <summary>The client's aim lock asked for a dash away from Dracula (its cursor already points away).</summary>
    internal void DashFor(Entity character)
    {
        if (character == Entity.Null || character != _ctx.Necro) return;
        _ctx.Abilities.ForceCast(character, Spells["dash"]);
    }

    public void Lighting(ICommandContext cmd, string mode)
    {
        if (_ctx.NecroUser == Entity.Null) _ctx.NecroUser = cmd.UserEntity;
        bool film = mode.Equals("film", StringComparison.OrdinalIgnoreCase);
        cmd.Reply(Cue(CameraCue.Lighting(film)) ? "Lighting: " + (film ? "film" : "default") + "." : "Lighting cue not sent.");
    }

    public void Hud(ICommandContext cmd, string state)
    {
        if (_ctx.NecroUser == Entity.Null) _ctx.NecroUser = cmd.UserEntity;
        bool visible = state.Equals("on", StringComparison.OrdinalIgnoreCase);
        cmd.Reply(Cue(CameraCue.Hud(visible)) ? "HUD " + (visible ? "on" : "off") + "." : "HUD cue not sent.");
    }

    public void Status(ICommandContext cmd)
    {
        int alive = 0, attacking = 0, held = 0;
        foreach (var h in _ctx.Army)
        {
            if (h.IsDisposed || !h.Enabled) continue;
            alive++;
            var stance = _ctx.Units.GetPetStance(h);
            if (stance == PetStance.Attack) attacking++;
            else if (stance == PetStance.Hold) held++;
        }
        string drac = "none";
        if (_ctx.Dracula != null && !_ctx.Dracula.IsDisposed)
        {
            var s = _ctx.Units.GetStats(_ctx.Dracula);
            drac = s.MaxHealth + " max, held=" + _ctx.Units.IsHeld(_ctx.Dracula);
        }
        string line = "Necro army: " + alive + " standing (" + attacking + " attacking, " + held + " holding); Dracula " + drac;
        _ctx.Log.LogInfo(line);
        cmd.Reply(line);
    }

    public void Reset(ICommandContext cmd)
    {
        Clear();
        Cue(CameraCue.Pillars(false));
        Cue(CameraCue.Reset());
        cmd.Reply("Necro army: cleared.");
    }

    private void Clear()
    {
        _ctx.Take++;
        _ctx.NoCooldowns?.Dispose();
        _ctx.NoCooldowns = null;
        foreach (var slot in _ctx.Slots) slot.Dispose();
        _ctx.Slots.Clear();
        _ctx.Night?.Dispose();
        _ctx.Night = null;
        if (_ctx.Necro != Entity.Null)
        {
            try { _ctx.Units.ClearLook(_ctx.Necro); } catch (Exception) { /* no look, or she has gone */ }
        }
        foreach (var h in _ctx.Army) if (!h.IsDisposed) h.Dispose();
        _ctx.Army.Clear();
        if (_ctx.Dracula != null && !_ctx.Dracula.IsDisposed) _ctx.Dracula.Dispose();
        _ctx.Dracula = null;
    }

    /// <summary>Shots, placed relative to the necromancer (origin) and Dracula along the facing.</summary>
    private CameraCue? Shot(string name)
    {
        float3 o = _ctx.Origin, f = _ctx.Facing, r = new(f.z, 0f, -f.x), up = new(0f, 1f, 0f);
        float3 d = _ctx.Dracula != null && !_ctx.Dracula.IsDisposed ? DraculaPosition() : o + f * 11f;
        return name switch
        {
            "wide" => CameraCue.Look(o + f * 5.5f + r * 10f + up * 7f, (o + d) * 0.5f, true),
            "summon" => CameraCue.Look(o + f * 4f + r * 7f + up * 6f, o - f * 1f, true),
            // The point: from the side, her raised arm and Dracula in one frame.
            "point" => SideShot(o, d),
            "charge" => CameraCue.Look(o + f * 3f + r * 9f + up * 3f, o + f * 6f, false),
            "fight" => CameraCue.Look(d - f * 2f + r * 8f + up * 5f, d, false),
            "flank" => CameraCue.Look(d + f * 4f - r * 7f + up * 3f, d, false),
            // The fight: a slow orbit around Dracula, easing in from 11 m to 6.5 m over 25 s, kept low
            // and inside the hall so the lighting matches the room.
            "orbit" => CameraCue.Orbit(ToDegrees(-f) + 30f, 6f, 11f, 6.5f, 4f, 25f),
            "orbitlow" => CameraCue.Orbit(ToDegrees(r), -5f, 9f, 7f, 2.5f, 20f),
            // Cycles over-the-shoulder, orbit, hero, side-tracking and crane shots (NecroCamera).
            "director" => CameraCue.Director(),
            // The whole battle from high on the side: both of them in frame.
            "battle" => CameraCue.Look((o + d) * 0.5f + r * math.max(12f, math.distance(o.xz, d.xz) * 1.1f + 7f) + up * 5.5f, (o + d) * 0.5f + up * 0.5f, true),
            "top" => CameraCue.Reset(),
            _ => null,
        };
    }

    /// <summary>
    /// Where Dracula is now. On the tick he spawns his <c>LocalToWorld</c> still reads the origin
    /// (the game fills it a frame later), so anything near (0, 0, 0) falls back to his hold spot.
    /// </summary>
    /// <summary>The orbit angle (0 = +X, 90 = +Z) of a horizontal direction.</summary>
    private static float ToDegrees(float3 dir) => math.degrees(math.atan2(dir.z, dir.x));

    private float3 DraculaPosition()
    {
        var em = _ctx.World!.EntityManager;
        var e = _ctx.Dracula!.Entity.ToEntity();
        if (!em.Exists(e)) return _ctx.DraculaSpot;
        float3 p = em.GetComponentData<LocalToWorld>(e).Position;
        return math.lengthsq(p) < 1f ? _ctx.DraculaSpot : p;
    }

    private bool Cue(CameraCue? cue)
    {
        if (cue == null || _ctx.NecroUser == Entity.Null || _ctx.World == null) return false;
        return _ctx.Net.SendToUser(_ctx.World, _ctx.NecroUser, CameraCue.EventId, cue.Value);
    }

    private static string Prefab(ArmyRank rank) => rank switch
    {
        ArmyRank.Melee => Melee,
        ArmyRank.Crossbow => Crossbow,
        ArmyRank.Mage => Mage,
        _ => DeathKnight,
    };

    private static string Fmt(float3 p) => "(" + p.x.ToString("0.0") + ", " + p.z.ToString("0.0") + ")";
}

/// <summary><c>.arena setup</c> and <c>.arena reset</c>: stage and clear the scene.</summary>
[CommandGroup("arena")]
internal sealed class ArenaCommands
{
    private readonly Scene _scene;
    internal ArenaCommands(Scene scene) => _scene = scene;

    [Command("setup", adminOnly: true, description: "Dress as the necromancer and hold Dracula <distance> m away along <heading> degrees (0 = +X, 90 = +Z).")]
    public void Setup(ICommandContext cmd, float distance = 11f, float bossHealth = 12000f, float heading = 0f) => _scene.Setup(cmd, distance, bossHealth, heading);

    [Command("reset", adminOnly: true, description: "Despawn the army and Dracula and return the camera to top-down.")]
    public void Reset(ICommandContext cmd) => _scene.Reset(cmd);
}

/// <summary><c>.army summon|attack|status</c>: raise the army, send it in, report.</summary>
[CommandGroup("army")]
internal sealed class ArmyCommands
{
    private readonly Scene _scene;
    internal ArmyCommands(Scene scene) => _scene = scene;

    [Command("summon", adminOnly: true, description: "Raise the army out of the ground around you, back rank first.")]
    public void Summon(ICommandContext cmd, float interval = 0.15f, float health = 4000f, float damage = 0.5f) => _scene.Summon(cmd, interval, health, damage);

    [Command("attack", adminOnly: true, description: "Point (the camera holds on it); after <delay> s the army charges. <direct> hands the camera to the fight director after the point.")]
    public void Attack(ICommandContext cmd, float delay = 1.2f, bool direct = true) => _scene.Attack(cmd, delay, direct);

    [Command("status", adminOnly: true, description: "Army count, stances and Dracula's health.")]
    public void Status(ICommandContext cmd) => _scene.Status(cmd);
}

/// <summary><c>.cast</c>, <c>.barrage</c>, <c>.camera</c> and <c>.hud</c>.</summary>
internal sealed class SceneCommands
{
    private readonly Scene _scene;
    internal SceneCommands(Scene scene) => _scene = scene;

    [Command("cast", adminOnly: true, description: "Cast a spell: skull, chains, soulburn, spider, ward, angel, dash.")]
    public void Cast(ICommandContext cmd, string spell = "skull") => _scene.Spell(cmd, spell);

    [Command("barrage", adminOnly: true, description: "While Dracula stands, cast <spells> (comma list) in turn every <seconds>.")]
    public void Barrage(ICommandContext cmd, float seconds = 3f, string spells = "skull,skull,chains,skull,spider") => _scene.Barrage(cmd, seconds, spells);

    [Command("camera", adminOnly: true, description: "Camera shot: wide, summon, point, charge, fight, flank, orbit, orbitlow, battle, director, top.")]
    public void Camera(ICommandContext cmd, string shot = "wide") => _scene.Cam(cmd, shot);

    [Command("look", adminOnly: true, description: "Put your camera at x y z looking at lx ly lz (world coordinates); .camera top returns it.")]
    public void Look(ICommandContext cmd, float x, float y, float z, float lx, float ly, float lz) => _scene.Look(cmd, new float3(x, y, z), new float3(lx, ly, lz));

    [Command("hud", adminOnly: true, description: "Hide (off) or show (on) the game HUD on your client, for clean footage.")]
    public void Hud(ICommandContext cmd, string state = "off") => _scene.Hud(cmd, state);

    [Command("aim", adminOnly: true, description: "lock: face and aim at Dracula every frame (dash away when he closes in); move: also keep spell range and strafe; free: your mouse and keys again.")]
    public void Aim(ICommandContext cmd, string mode = "lock") => _scene.AimMode(cmd, mode);

    [Command("lighting", adminOnly: true, description: "Lighting on your client: film (darker grade, thinner fog) or default.")]
    public void Lighting(ICommandContext cmd, string mode = "film") => _scene.Lighting(cmd, mode);
}

/// <summary>
/// Corrupted Skull, her main spell, reaches 20 m instead of 12 while this plugin is loaded, so it
/// still lands when Dracula backs away. A projectile travels speed x lifetime and is destroyed when
/// its <c>LifeTime</c> runs out (read live 2026-09-25: the skull 22 m/s for 0.545 s, the two wave
/// skulls 18 m/s for 0.667 s, all <c>Projectile.Range</c> 12). Only <c>LifeTime.Duration</c> is
/// edited, through <c>ctx.Content.Edit</c>, which restores it on unload. Server side.
/// </summary>
internal static class SkullReach
{
    private const float Reach = 20f;

    private static readonly (int Guid, float Speed)[] Projectiles =
    {
        (917769627, 22f),   // AB_Unholy_CorruptedSkull_Projectile
        (608176448, 18f),   // AB_Unholy_CorruptedSkull_Projectile_Wave01
        (422968059, 18f),   // AB_Unholy_CorruptedSkull_Projectile_Wave02
    };

    internal static void Apply(PluginContext ctx)
    {
        foreach (var (guid, speed) in Projectiles)
        {
            float duration = Reach / speed;
            ctx.Content.Edit(new PrefabGUID(guid), prefab =>
            {
                if (!prefab.Has<ProjectM.LifeTime>()) return;
                var life = prefab.Read<ProjectM.LifeTime>();
                life.Duration = duration;
                prefab.Write(life);
            });
        }
    }
}
