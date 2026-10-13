#if !PROGENITOR_TESTS
using System.Collections.Generic;
using System;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// <see cref="PluginContext.Units"/> surface. Fills owner / plugin id on
/// <see cref="Units"/> and <see cref="PluginContext.Track"/>s every handle so unload
/// despawns what this plugin spawned.
/// </summary>
public sealed class PluginUnits
{
    private readonly PluginContext _ctx;
    private bool _capacityWarned;
    private bool _looksTracked;
    private bool _healthBarsTracked;

    internal PluginUnits(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Spawn a vanilla unit for this plugin and record the handle so unload despawns it.
    /// Server-only; the client returns a disabled handle (one log line).
    /// </summary>
    /// <param name="behaviourTree">
    /// Optional vanilla behavior-tree prefab to bind on the spawn tick. See
    /// <see cref="Units.Spawn(string, UnitVec3, object?, string?, string?)"/>.
    /// </param>
    public IUnitHandle Spawn(string prefab, UnitVec3 position, string? behaviourTree = null)
    {
        _ctx.EnsureAttached();
        var handle = Units.Spawn(prefab, position, _ctx.Owner, _ctx.Guid, behaviourTree);
        _ctx.Track(handle);
        NoteCapacity(handle);
        return handle;
    }

    /// <summary>Spawn by PrefabGUID hash. Same ownership as the name overload.</summary>
    public IUnitHandle Spawn(int prefabGuid, UnitVec3 position, string? behaviourTree = null)
    {
        _ctx.EnsureAttached();
        var handle = Units.Spawn(prefabGuid, position, _ctx.Owner, _ctx.Guid, behaviourTree);
        _ctx.Track(handle);
        NoteCapacity(handle);
        return handle;
    }

    /// <summary>
    /// Spawn with <see cref="UnitSpawnOptions"/>: an optional faction written on the spawn tick
    /// (and re-checked at 1 s and 5 s), an optional vanilla behavior tree, and the body, look, stats,
    /// aura, lifetime, drop and entrance settings described on each option. Server-only; the client
    /// returns a disabled handle. An unknown faction or an out-of-range option throws before anything
    /// is spawned.
    /// </summary>
    public IUnitHandle Spawn(string prefab, UnitVec3 position, UnitSpawnOptions options)
    {
        _ctx.EnsureAttached();
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Refusal() is { } refusal)
            throw new ArgumentOutOfRangeException(nameof(options), refusal);
        if (options.HitboxScale is float hitbox && !UnitHitboxCodec.IsValidScale(hitbox))
            throw new ArgumentOutOfRangeException(nameof(options), UnitHitboxCodec.ScaleRangeMessage(hitbox));
        if (options.MoveSpeedMultiplier is float speed && !UnitMoveSpeedMath.IsValidMultiplier(speed))
            throw new ArgumentOutOfRangeException(nameof(options), UnitMoveSpeedMath.MultiplierRangeMessage(speed));
        if (options.Look != null && UnitLookMath.Refusal(options.Look) is { } lookRefusal)
            throw new ArgumentOutOfRangeException(nameof(options), lookRefusal);
        if (options.Stats != null && options.Stats.Refusal() is { } statsRefusal)
            throw new ArgumentOutOfRangeException(nameof(options), statsRefusal);
        if (options.Aura is UnitAura spawnAura && !UnitAuraTable.IsDefined(spawnAura))
            throw new ArgumentOutOfRangeException(nameof(options), UnitAuraTable.RangeMessage(spawnAura));
        if (options.ExtrasRefusal() is { } extrasRefusal)
            throw new ArgumentOutOfRangeException(nameof(options), extrasRefusal);
        Progenitor.Ai.AiFactionInfo? faction = null;
        if (!string.IsNullOrWhiteSpace(options.Faction) && _ctx.IsServer)
            faction = _ctx.Ai.Factions.Describe(options.Faction!);
        var handle = Spawn(prefab, position, options.BehaviourTree);
        // Lifetime first: a 6 s LifeTime (the death knight) must not run out while the rest applies.
        if (handle.Enabled && options.Permanent)
            ApplyPermanent(handle);
        if (handle.Enabled && options.LifetimeSeconds is float life)
        {
            if (!handle.TrySetLifetime(life))
                Plugin.Logger?.LogWarning("Units: spawn LifetimeSeconds=" + life + " refused on " + handle.Entity);
        }
        if (handle.Enabled && (options.TeamValue != null || options.FactionIndex != null))
        {
            if (!handle.TrySetTeam(options.TeamValue, options.FactionIndex))
                Plugin.Logger?.LogWarning("Units: spawn TeamValue/FactionIndex refused on " + handle.Entity);
        }
        if (handle.Enabled && options.StripVBlood)
        {
            var strip = handle.TryStripVBlood();
            if (!strip.Ok)
                Plugin.Logger?.LogWarning("Units: spawn StripVBlood refused on " + handle.Entity);
        }
        if (handle.Enabled && !options.DropLoot)
            ApplyNoLoot(handle);
        if (handle.Enabled && options.Entrance != UnitEntrance.None)
            ApplyEntrance(handle, options.Entrance);
        if (faction != null && handle.Enabled)
            ApplyFaction(handle, faction);
        if (handle.Enabled)
            ApplyBody(handle, options.HitboxScale, options.MoveSpeedMultiplier);
        if (handle.Enabled && options.Look != null && !options.Look.IsVanilla)
        {
            try { SetLookCore(handle, options.Look); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: spawn option Look for " + handle.Entity + " refused; the unit looks vanilla: " + e.Message); }
        }
        if (handle.Enabled && options.Stats != null && !options.Stats.IsEmpty)
            ApplyStats(handle, options.Stats);
        if (handle.Enabled && options.TeamOf != Entity.Null)
        {
            try { UnitTeams.Join(handle, options.TeamOf, _ctx.Owner); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: spawn option TeamOf for " + handle.Entity + " refused; the unit keeps its team: " + e.Message); }
        }
        if (handle.Enabled && options.HideHealthBar)
        {
            // A clone not verified inert yet is one warning inside SetHealthBarHidden; the unit keeps its bar.
            try { SetHealthBarHiddenCore(handle, true); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: spawn option HideHealthBar for " + handle.Entity + " refused; the unit shows its health bar: " + e.Message); }
        }
        if (handle.Enabled && options.Aura is UnitAura aura && aura != UnitAura.None)
        {
            // A clone not verified inert yet is one warning inside SetAura; the unit spawns without it.
            try { UnitAuras.Set(handle, aura, _ctx.Owner); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: spawn option Aura for " + handle.Entity + " refused; the unit has no aura: " + e.Message); }
        }
        if (handle.Enabled && options.Configure != null)
        {
            try { options.Configure(handle); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: spawn Configure callback threw: " + e.Message);
            }
        }
        return handle;
    }

    /// <summary>Spawn by PrefabGUID hash with <see cref="UnitSpawnOptions"/>.</summary>
    public IUnitHandle Spawn(int prefabGuid, UnitVec3 position, UnitSpawnOptions options)
    {
        _ctx.EnsureAttached();
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Refusal() is { } refusal)
            throw new ArgumentOutOfRangeException(nameof(options), refusal);
        // Reuse the string path after resolving the prefab name is not available; go through Units.
        var handle = Units.Spawn(prefabGuid, position, options, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary><see cref="UnitSpawnOptions.Permanent"/>: a refusal is one warning; the unit keeps its lifetime.</summary>
    private static void ApplyPermanent(IUnitHandle handle)
    {
        if (!UnityUnitRuntime.TryMakePermanent(handle.Entity, out bool hadLifeTime))
            Plugin.Logger?.LogWarning("Units: spawn option Permanent for " + handle.Entity + " refused; the unit keeps its LifeTime.");
        else if (hadLifeTime)
            Units.LogDebug("Units: " + handle.Entity + " LifeTime set to {-1, None} (Permanent).");
    }

    /// <summary><see cref="UnitSpawnOptions.DropLoot"/> false: a refusal is one warning; the unit may drop loot.</summary>
    private static void ApplyNoLoot(IUnitHandle handle)
    {
        if (!UnityUnitRuntime.TryDisableLoot(handle.Entity, out string removed))
            Plugin.Logger?.LogWarning("Units: spawn option DropLoot=false for " + handle.Entity + " refused; the unit may drop loot.");
        else if (removed.Length > 0)
            Units.LogDebug("Units: " + handle.Entity + " drops disabled (removed " + removed + ").");
    }

    /// <summary><see cref="UnitSpawnOptions.Entrance"/>: the entrance buff through <c>DebugEventsSystem.ApplyBuff</c>, as auras are applied.</summary>
    private static void ApplyEntrance(IUnitHandle handle, UnitEntrance entrance)
    {
        if (!UnitEntranceTable.TryGetBuff(entrance, out int guid)) return;
        try
        {
            var world = Worlds.Server ?? throw new InvalidOperationException("no server world");
            Progenitor.Ai.AiRuntime.ApplyBuff(world, handle.Entity.ToEntity(), guid);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: spawn option Entrance " + entrance + " for " + handle.Entity + " refused; the unit spawns without it: " + e.Message);
        }
    }

    /// <summary>
    /// Spawn-tick stats from <see cref="UnitSpawnOptions.Stats"/>. The fresh unit reads max health 0
    /// until the game's spawn computation runs, so this records the request and the checks write it.
    /// A refusal is one warning: the unit exists by now and keeps its own stats.
    /// </summary>
    private void ApplyStats(IUnitHandle handle, UnitStatOverrides stats)
    {
        try { UnitStatsBody.SetStats(handle, stats.Clone()); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: spawn option Stats for " + handle.Entity + " refused; the unit keeps its own stats: " + e.Message);
            return;
        }
        UnitStatsBody.ScheduleChecks(handle, _ctx.Owner);
    }

    /// <summary>
    /// Set absolute level, max health and power on a unit this plugin spawned; a null field is left as
    /// it is (see <see cref="UnitStatOverrides"/> for the ranges). Setting max health also fills current
    /// health to the new maximum. Level feeds the game's level-difference damage scaling and does not
    /// recompute power or health: set those too. Returns false when the unit was spawned so recently
    /// that the game has not computed its health yet (it reads max health 0): the values are written by
    /// the checks at 0.25 s, 1 s and 5 s. Those checks also write a value again if the game resets it.
    /// A later call with new values replaces the earlier ones. Server-only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A field of <paramref name="stats"/> is outside its range.</exception>
    /// <exception cref="InvalidOperationException">
    /// The handle is disabled or disposed, or the unit is gone; <see cref="UnitStatOverrides.MaxHealth"/>
    /// is set and the unit is dead or downed (it is never revived); or the unit lacks the component a
    /// set field needs (<c>UnitStats</c>, <c>UnitLevel</c>). Nothing was changed.
    /// </exception>
    public bool SetStats(IUnitHandle handle, UnitStatOverrides stats)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (stats == null) throw new ArgumentNullException(nameof(stats));
        stats.Validate();
        bool now = UnitStatsBody.SetStats(handle, stats.Clone());
        UnitStatsBody.ScheduleChecks(handle, _ctx.Owner);
        return now;
    }

    /// <summary>
    /// Give a player character <paramref name="bloodType"/> blood at <paramref name="quality"/>
    /// percent (0-100), with a full pool, as the game's own admin "consume blood" does. The type is
    /// the prefab name with or without its prefix: <c>"Draculin"</c> or <c>"BloodType_Draculin"</c>.
    /// Blood types in this build include Brute, Corruption, Creature, Draculin, Rogue, Warrior, Worker,
    /// and None (frail). The change is saved with the character, like any blood the player drinks.
    ///
    /// <para>The game applies it on its next update (live 2026-09-25: Worker 4% to Draculin 100%);
    /// true means the event was queued. False, with <paramref name="why"/>, when the world is not
    /// ready, the entity is not a player character with blood, the quality is outside 0-100, or no
    /// blood type has that name. Server-only (throws on the client); main thread.</para>
    /// </summary>
    public bool SetBlood(Entity character, string bloodType, float quality, out string why)
    {
        RequireServer("SetBlood");
        return UnitBlood.TryResolve(_ctx.World, bloodType, out int guid, out why)
            && UnitBlood.Set(_ctx.World, character, guid, quality, out why);
    }

    /// <summary><see cref="SetBlood(Entity, string, float, out string)"/>, logging the refusal.</summary>
    public bool SetBlood(Entity character, string bloodType, float quality)
    {
        bool ok = SetBlood(character, bloodType, quality, out string why);
        if (!ok) _ctx.Log.LogWarning("Units.SetBlood: " + why);
        return ok;
    }

    /// <summary><see cref="SetBlood(Entity, string, float, out string)"/> by the blood type's prefab GUID.</summary>
    public bool SetBlood(Entity character, int bloodTypeGuid, float quality, out string why)
    {
        RequireServer("SetBlood");
        return UnitBlood.Set(_ctx.World, character, bloodTypeGuid, quality, out why);
    }

    /// <summary>
    /// Destroy the breakable props (banners, braziers, vases, pillars) within
    /// <paramref name="radius"/> of <paramref name="center"/> on the x-z plane and
    /// <see cref="UnitBreakablesPolicy.HeightBand"/> of its height, so nothing shatters during a fight
    /// or a take. Returns how many were removed.
    ///
    /// <para>These are the tile models a dungeon's or arena's spawn chains place
    /// (<c>SpawnChainChild</c>); units are never touched. They go through the game's destroy path,
    /// which replicates to clients, and the chains stay, so the game restores the props when the area
    /// resets. Some chains restore theirs within seconds: Dracula's four arena pillars came back in
    /// about 20 s (live 2026-09-25), so pass their prefab GUID to <paramref name="keep"/> and hide or
    /// ignore them instead. <paramref name="keep"/> gets each prop's prefab GUID; true leaves it.</para>
    ///
    /// <para>Server-only (throws on the client); main thread. Returns 0 and logs when the world is not
    /// ready.</para>
    /// </summary>
    public int ClearBreakables(UnitVec3 center, float radius, Func<int, bool>? keep = null)
    {
        RequireServer("ClearBreakables");
        int count = UnitBreakables.Clear(_ctx.World, center, radius, keep, out string why);
        if (why.Length > 0) _ctx.Log.LogWarning("Units.ClearBreakables: " + why);
        return count;
    }

    private void RequireServer(string op)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsServer)
            throw new InvalidOperationException("ctx.Units." + op + " is server-only.");
    }

    /// <summary>
    /// The unit's current and maximum health and whether it is dead or downed
    /// (<see cref="UnitHealth"/>). Server-only (throws on the client); main thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, the unit is gone, or it has no Health.</exception>
    public UnitHealth GetHealth(IUnitHandle handle)
    {
        RequireServer("GetHealth");
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UnitStatsBody.GetHealth(handle);
    }

    /// <summary>
    /// The unit's current level, max health and physical and spell power, read off the unit (temporary
    /// modifiers included). <see cref="UnitStatOverrides.Level"/> is null when the unit has no
    /// <c>UnitLevel</c>. Server-only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public UnitStatOverrides GetStats(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UnitStatsBody.GetStats(handle);
    }

    /// <summary>
    /// Change how a unit this plugin spawned looks on Progenitor clients: visual size relative to its
    /// natural size, tint, glow and a flat overlay (see <see cref="UnitLook"/> for the measured bounds).
    /// Replaces any earlier look; a vanilla look is the same as <see cref="ClearLook"/>. The look is
    /// sent to every connected client, to clients that join later, and survives the model being rebuilt
    /// (streaming, leaving and re-entering view). Clients without Progenitor see the vanilla unit. The
    /// server-side hitbox is not changed: that is <see cref="SetHitboxScale"/>. Despawn and plugin
    /// unload clear the look. Server-only: a disabled handle (client, skipped module) throws.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A field of <paramref name="look"/> is outside its hard range.</exception>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public void SetLook(IUnitHandle handle, UnitLook look)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (look == null) throw new ArgumentNullException(nameof(look));
        look.Validate();
        SetLookCore(handle, look);
    }

    /// <summary>A copy of the look last set on this unit through <c>ctx.Units</c>, or a vanilla one.</summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public UnitLook GetLook(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UnitLooks.Get(handle);
    }

    /// <summary>
    /// Remove the unit's look: Progenitor clients put its model back to its natural size and vanilla
    /// colors. Nothing happens when it has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public void ClearLook(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        UnitLooks.Clear(handle);
    }

    /// <summary>
    /// Put a unit this plugin spawned on <paramref name="character"/>'s team: its <c>Team.Value</c> and
    /// <c>TeamReference</c> are copied from the character (for a player, the player's own team), so the
    /// character's hits and projectiles treat it as an ally and clients draw its health bar as allied.
    /// The unit keeps its own <c>Team.FactionIndex</c>, so whom its AI fights does not change (use the
    /// <c>Faction</c> spawn option or <see cref="SetFaction"/> for that). Checked again a second and five
    /// seconds later and written again if something reset it. Not restored: the unit stays on that team
    /// until despawned or joined to another. Independent of Core's ally-safe hits, which only guard hits
    /// between API-spawned units. Server-only.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The handle is disabled or disposed, or the unit is gone; the character is <c>Entity.Null</c>, the
    /// unit itself, or missing; either lacks <c>Team</c> or <c>TeamReference</c>; or the unit wears a buff
    /// with <c>ModifyTeamBuff</c> (the game would restore the old team when it ends). Nothing was changed.
    /// </exception>
    public void JoinTeam(IUnitHandle handle, Entity character)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        UnitTeams.Join(handle, character, _ctx.Owner);
    }

    /// <summary>
    /// Change how any networked entity with a model looks on Progenitor clients, such as a player
    /// character: the same <see cref="UnitLook"/> as <see cref="SetLook(IUnitHandle, UnitLook)"/>, sent
    /// and re-applied the same way (join, model rebuilt, shapeshift). Visual scale is relative to the
    /// model's natural size (a player character is 1). Owned by this plugin: plugin unload clears it, as
    /// does the entity being destroyed, a world load, and, for a player character, its user
    /// disconnecting. A later call by any plugin replaces the look and takes it over. Server-only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A field of <paramref name="look"/> is outside its hard range.</exception>
    /// <exception cref="InvalidOperationException">
    /// Not on the server; the entity does not exist; it has no <c>NetworkId</c> (clients find a look by
    /// network id); or it is a player character whose user is not connected.
    /// </exception>
    public void SetLook(Entity entity, UnitLook look)
    {
        _ctx.EnsureAttached();
        if (look == null) throw new ArgumentNullException(nameof(look));
        look.Validate();
        UnitLooks.Set(entity, look, _ctx.Guid);
        TrackLooks();
    }

    /// <summary>A copy of the look last set on <paramref name="entity"/> through <c>ctx.Units</c>, or a vanilla one.</summary>
    /// <exception cref="InvalidOperationException">Not on the server, or the entity does not exist.</exception>
    public UnitLook GetLook(Entity entity)
    {
        _ctx.EnsureAttached();
        return UnitLooks.Get(entity);
    }

    /// <summary>Remove the look of <paramref name="entity"/>: clients restore its model. Nothing happens when it has none.</summary>
    /// <exception cref="InvalidOperationException">Not on the server, or the entity does not exist.</exception>
    public void ClearLook(Entity entity)
    {
        _ctx.EnsureAttached();
        UnitLooks.Clear(entity);
    }

    /// <summary>
    /// Hide (or show again) the health bar and name plate a unit this plugin spawned shows on Progenitor
    /// clients. A Core-owned inert buff carrying <c>HideTargetHUD</c> is put on the unit (removed to
    /// show it); the client skips the HUD of a unit that wears it. Checked again a second and five
    /// seconds later. The buff is saved with the unit. Plugin unload shows the bar again on every unit
    /// this plugin hid that still exists. What a client without Progenitor shows is not verified. Server-only.
    /// </summary>
    /// <returns>
    /// True when the request took effect. False when Core has not verified the buff's clone yet (it is
    /// registered once prefabs load): nothing changed, and one warning is logged.
    /// </returns>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public bool SetHealthBarHidden(IUnitHandle handle, bool hidden)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return SetHealthBarHiddenCore(handle, hidden);
    }

    /// <summary>True when the unit wears Core's hide-health-bar buff (also after a restore from a save).</summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public bool IsHealthBarHidden(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UnitHealthBars.IsHidden(handle);
    }

    private bool SetHealthBarHiddenCore(IUnitHandle handle, bool hidden)
    {
        bool ok = UnitHealthBars.Set(handle, hidden, _ctx.Owner, _ctx.Guid);
        if (hidden && !_healthBarsTracked)
        {
            // One tracked release per plugin generation; it holds the plugin id string only.
            _ctx.Track(new HealthBarRelease(_ctx.Guid));
            _healthBarsTracked = true;
        }
        return ok;
    }

    private sealed class HealthBarRelease : IDisposable
    {
        private string? _pluginId;

        public HealthBarRelease(string pluginId) => _pluginId = pluginId;

        public void Dispose()
        {
            var id = _pluginId;
            _pluginId = null;
            if (id != null) UnitHealthBars.ReleaseOwnedBy(id);
        }
    }

    /// <summary>
    /// Put a server-applied aura on a unit this plugin spawned (<see cref="UnitAura.Elite"/>: the
    /// elite-affix purple ring and glow). The aura is a Core-owned buff on the unit, so every client
    /// that has the aura's clone draws it natively; Progenitor clients build that clone from the
    /// registry. The clone is stripped on the server to carry no gameplay (the vanilla elite aura's
    /// +8 levels to nearby units is gone). A unit keeps at most one aura: a different one replaces it,
    /// and <see cref="UnitAura.None"/> is the same as <see cref="ClearAura"/>. The buff is saved with the
    /// unit, so a unit restored from a save keeps its aura. Checked again a second and five seconds
    /// later. Server-only: a disabled handle (client, skipped module) throws.
    /// </summary>
    /// <returns>
    /// True when the aura is on the unit (or was cleared). False when Core has not verified the aura's
    /// clone inert yet (it is registered once prefabs load): nothing was put on the unit, and one
    /// warning per aura is logged.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="aura"/> is not a defined <see cref="UnitAura"/>.</exception>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public bool SetAura(IUnitHandle handle, UnitAura aura)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        UnitAuraTable.Validate(aura, nameof(aura));
        return UnitAuras.Set(handle, aura, _ctx.Owner);
    }

    /// <summary>
    /// The aura the unit wears now, read from its buffs, or <see cref="UnitAura.None"/>. It reports an
    /// aura whoever put it there, including one restored from a save.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public UnitAura GetAura(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UnitAuras.Get(handle);
    }

    /// <summary>
    /// The aura <paramref name="unit"/> wears now, read from its buffs, or <see cref="UnitAura.None"/>.
    /// Takes any server entity, so it works on a unit restored from a save, which has no handle.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not on the server, or the entity does not exist.</exception>
    public UnitAura GetAura(Entity unit)
    {
        _ctx.EnsureAttached();
        return UnitAuras.Get(unit);
    }

    /// <summary>
    /// Remove the unit's aura: its aura buff instance is destroyed on the server, and clients drop the
    /// ring with it. Nothing happens when it has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the unit is gone.</exception>
    public void ClearAura(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        UnitAuras.Clear(handle);
    }

    private void SetLookCore(IUnitHandle handle, UnitLook look)
    {
        UnitLooks.Set(handle, look, _ctx.Guid);
        TrackLooks();
    }

    private void TrackLooks()
    {
        if (_looksTracked) return;
        // One tracked release per plugin generation: unload clears every look this plugin set, even
        // on a unit that outlives the plugin. It holds the plugin id string only.
        _ctx.Track(new LookRelease(_ctx.Guid));
        _looksTracked = true;
    }

    private sealed class LookRelease : IDisposable
    {
        private string? _pluginId;

        public LookRelease(string pluginId) => _pluginId = pluginId;

        public void Dispose()
        {
            var id = _pluginId;
            _pluginId = null;
            if (id != null) UnitLooks.ReleaseOwnedBy(id);
        }
    }

    /// <summary>
    /// Spawn-tick hitbox and speed from <see cref="UnitSpawnOptions"/>. A refusal is one warning, not
    /// an exception: the unit exists by now, and a vanilla hitbox is a usable unit.
    /// </summary>
    private void ApplyBody(IUnitHandle handle, float? hitbox, float? speed)
    {
        bool any = false;
        if (hitbox is float scale && scale != 1f)
        {
            any = true;
            try { UnitBody.SetHitboxScale(handle, scale); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: spawn option HitboxScale for " + handle.Entity + " refused; the unit keeps its vanilla hitbox: " + e.Message); }
        }
        if (speed is float multiplier && multiplier != 1f)
        {
            any = true;
            try { UnitBody.SetMoveSpeed(handle, multiplier); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: spawn option MoveSpeedMultiplier for " + handle.Entity + " refused; the unit keeps its vanilla speed: " + e.Message); }
        }
        if (any) UnitBody.ScheduleChecks(handle, _ctx.Owner);
    }

    /// <summary>
    /// Scale the server-side hitbox of a unit this plugin spawned: the unit's Hittable capsule or
    /// sphere grows (or shrinks) by <paramref name="scale"/> with its bottom kept on the ground, so hits
    /// land further from (or closer to) the unit. <paramref name="scale"/> is 0.25 to 4; 1 restores the
    /// vanilla hitbox. The visual size of the model is unchanged (it is client-side). Checked again a
    /// second and five seconds later. Server-only: a disabled handle (client, skipped module) throws.
    /// Despawn and unload need no restore.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is outside 0.25 to 4.</exception>
    /// <exception cref="InvalidOperationException">
    /// The handle is disabled or disposed, the unit is gone, or its collider cannot be scaled (not a
    /// compound collider, or its Hittable child is not a capsule or sphere). Nothing was changed.
    /// </exception>
    public void SetHitboxScale(IUnitHandle handle, float scale)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (!UnitHitboxCodec.IsValidScale(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), UnitHitboxCodec.ScaleRangeMessage(scale));
        UnitBody.SetHitboxScale(handle, scale);
        UnitBody.ScheduleChecks(handle, _ctx.Owner);
    }

    /// <summary>
    /// The hitbox scale the unit has now, read off the unit: the scale of the Progenitor collider copy
    /// it points at, or 1 when it has its vanilla collider (or one something else put there).
    /// </summary>
    public float GetHitboxScale(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        return UnitBody.GetHitboxScale(handle);
    }

    /// <summary>
    /// Set a unit this plugin spawned to <paramref name="multiplier"/> x its vanilla move speed:
    /// <c>Movement.Speed</c> (what chase speed follows) and the four <c>AiMoveSpeeds</c> values.
    /// <paramref name="multiplier"/> is 0.1 to 4; 1 restores the vanilla speed. Calls do not compound:
    /// 2 then 3 is 3x, not 6x. Temporary speed effects the game has on the unit at the time are kept;
    /// see docs/modules.md for the small error an additive one leaves. Returns false when the unit
    /// cannot move right now (its speed reads 0, a root or stun): the write is retried a second and
    /// five seconds later. Server-only: a disabled handle (client, skipped module) throws.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiplier"/> is outside 0.1 to 4.</exception>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, the unit is gone, or it has no <c>Movement</c>.</exception>
    public bool SetMoveSpeed(IUnitHandle handle, float multiplier)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (!UnitMoveSpeedMath.IsValidMultiplier(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier), UnitMoveSpeedMath.MultiplierRangeMessage(multiplier));
        bool now = UnitBody.SetMoveSpeed(handle, multiplier);
        UnitBody.ScheduleChecks(handle, _ctx.Owner);
        return now;
    }

    /// <summary>The move speed multiplier last set on this unit through <c>ctx.Units</c>, or 1.</summary>
    public float GetMoveSpeed(IUnitHandle handle)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UnitBody.GetMoveSpeed(handle);
    }

    /// <summary>
    /// Put a unit this plugin spawned on <paramref name="faction"/>: writes its <c>FactionReference</c>
    /// (the game derives <c>Team.FactionIndex</c> from it at spawn) and its <c>Team.FactionIndex</c>.
    /// Not restored on unload: the unit itself is despawned then. Returns the faction written.
    /// </summary>
    public Progenitor.Ai.AiFactionInfo SetFaction(IUnitHandle handle, string faction)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (!handle.Enabled) throw new InvalidOperationException("the unit handle is disabled.");
        var info = _ctx.Ai.Factions.Describe(faction);
        ApplyFaction(handle, info);
        return info;
    }

    /// <summary>
    /// One warning per plugin generation when this plugin's live spawned units first exceed
    /// <see cref="Progenitor.Ai.AiLimits.RecommendedActiveUnits"/>. Never refuses a spawn.
    /// </summary>
    private void NoteCapacity(IUnitHandle handle)
    {
        if (!handle.Enabled || _capacityWarned) return;
        int live = Units.LiveCountFor(_ctx.Owner);
        if (!Progenitor.Ai.AiLimits.ShouldWarn(live, _capacityWarned)) return;
        _capacityWarned = true;
        Plugin.Logger?.LogWarning(Progenitor.Ai.AiLimits.FormatWarning(_ctx.Guid, live));
    }

    private void ApplyFaction(IUnitHandle handle, Progenitor.Ai.AiFactionInfo faction)
    {
        var entity = handle.Entity.ToEntity();
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        _ctx.Ai.Factions.SetUntracked(entity, faction.Guid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The live proof wrote Team after spawn; nothing is known about a spawn system rewriting it,
        // so check twice more and log if something did.
        foreach (double delay in new[] { 1.0, 5.0 })
        {
            Progenitor.Dev.MainThread.PostDelayed(delay, () =>
            {
                try
                {
                    if (handle.IsDisposed || !handle.Enabled) return;
                    // A world reload re-numbers entities: the id may name another unit now.
                    if (Progenitor.WorldState.WorldEpoch.IsStale(epoch)) return;
                    var world = Worlds.Server;
                    if (world == null || !world.IsCreated || !world.EntityManager.Exists(entity)) return;
                    if (Progenitor.Ai.AiRuntime.HasFaction(world.EntityManager, entity, faction)) return;
                    Plugin.Logger?.LogWarning("Units: faction of " + handle.Entity + " changed after spawn; writing " + faction.Name + " again.");
                    _ctx.Ai.Factions.SetUntracked(entity, faction.Guid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Units: faction re-check threw: " + e.Message);
                }
            }, _ctx.Owner);
        }
    }

    /// <summary>
    /// Walk toward a real entity. Convenience over <see cref="IUnitHandle.Follow(UnitId)"/>
    /// so a command can pass <c>ctx.CharacterEntity</c> without constructing a <see cref="UnitId"/>.
    /// </summary>
    /// <summary>
    /// Request Combat against <paramref name="target"/> and set aggro. Does not clear
    /// <c>Immortal</c>. Client and a disabled handle report both flags false.
    /// </summary>
    public UnitEngageResult Engage(IUnitHandle handle, Entity target)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return handle.Engage(UnitId.From(target));
    }

    private PetStanceBook? _stances;
    private bool _petLoopPosted;

    private PetStanceBook Stances
    {
        get
        {
            if (_stances != null) return _stances;
            _stances = new PetStanceBook(StanceRuntime);
            // One tracked release per plugin generation: unload writes back the aggro setting of every
            // unit this plugin held (the units themselves are despawned by their own handles).
            _ctx.Track(new StanceRelease(this));
            return _stances;
        }
    }

    private static IUnitStanceRuntime StanceRuntime() =>
        Units.Runtime as IUnitStanceRuntime ?? NullStanceRuntime.Instance;

    /// <summary>
    /// Make a unit this plugin spawned behave as a pet of <paramref name="owner"/> in the
    /// <see cref="PetStance.Follow"/> stance: while it has no live aggro target it follows the owner;
    /// when it has one (from <see cref="Engage"/> or the game's own aggro) it stops following and
    /// fights, and it returns to the owner when the target is dead or gone. Checked every 0.5 s. While a
    /// pet follows, the unit's walk speed is kept equal to its run speed, so following uses its full
    /// speed (including <see cref="SetMoveSpeed"/>). Plain <see cref="Follow"/> keeps the unit in the
    /// game's Follow state, which overrides combat, so a following unit never fights; use this instead
    /// for a companion. Same as <c>SetPet(handle, owner, PetStance.Follow)</c>; kept as its own
    /// overload for plugins compiled against the two-argument form. Server-only; main thread only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the call is off the main thread.</exception>
    public void SetPet(IUnitHandle handle, Entity owner) => SetPet(handle, owner, PetStance.Follow);

    /// <summary>
    /// Make a unit this plugin spawned a pet of <paramref name="owner"/> in <paramref name="stance"/>
    /// (docs/modules.md, "Pet stances and hold"): <see cref="PetStance.Follow"/> as the two-argument
    /// overload, or <see cref="PetStance.Hold"/>, held where it stands (see <see cref="Hold"/>). For
    /// <see cref="PetStance.Attack"/> use <see cref="PetAttack"/>. Server-only; main thread only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the call is off the main thread.</exception>
    /// <exception cref="ArgumentException"><paramref name="stance"/> is Attack (it needs a target) or undefined.</exception>
    public void SetPet(IUnitHandle handle, Entity owner, PetStance stance)
    {
        Units.EnsureMainThread(nameof(SetPet));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        Stances.SetPet(handle, UnitId.From(owner), stance);
        EnsurePetLoop();
    }

    /// <summary>
    /// Change a pet's stance to <see cref="PetStance.Follow"/> or <see cref="PetStance.Hold"/> (held
    /// where it stands now). Leaving Hold may write <c>Active</c> back (see <see cref="Hold"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, the unit is not a pet, or the call is off the main thread.</exception>
    /// <exception cref="ArgumentException"><paramref name="stance"/> is Attack (use <see cref="PetAttack"/>) or undefined.</exception>
    public void SetPetStance(IUnitHandle handle, PetStance stance)
    {
        Units.EnsureMainThread(nameof(SetPetStance));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        Stances.SetStance(handle, stance);
        EnsurePetLoop();
    }

    /// <summary>The pet's current stance, or null when the unit is not a pet.</summary>
    /// <exception cref="InvalidOperationException">The call is off the main thread.</exception>
    public PetStance? GetPetStance(IUnitHandle handle)
    {
        Units.EnsureMainThread(nameof(GetPetStance));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return _stances?.StanceOf(handle);
    }

    /// <summary>
    /// Send a pet at <paramref name="target"/> (stance <see cref="PetStance.Attack"/>): every 0.5 s its
    /// aggro target is set to the target with <c>AggroConsumer.Active</c> forced to 1, Follow is
    /// cleared, and Combat is posted when it is not fighting. When the target is dead or gone the pet
    /// goes back to Follow. A held pet is released first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, the unit is not a pet, or the call is off the main thread.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is <c>Entity.Null</c>, the owner, or the pet itself.</exception>
    public void PetAttack(IUnitHandle handle, Entity target)
    {
        Units.EnsureMainThread(nameof(PetAttack));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        Stances.Attack(handle, UnitId.From(target));
        EnsurePetLoop();
    }

    /// <summary>
    /// Hold a unit this plugin spawned (pet or not) at <paramref name="position"/>: as <see cref="Hold"/>,
    /// with this spot instead of where it stands. A pet's stance becomes <see cref="PetStance.Hold"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handle is disabled or disposed, or the call is off the main thread.</exception>
    public void HoldAt(IUnitHandle handle, UnitVec3 position)
    {
        Units.EnsureMainThread(nameof(HoldAt));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        Stances.HoldAt(handle, position);
        EnsurePetLoop();
    }

    /// <summary>
    /// Hold a unit this plugin spawned passive in place (<paramref name="hold"/> true) or release it
    /// (false). Held, every 0.5 s: <c>AggroConsumer.Active</c> is written 0, its aggro and alert
    /// targets and <c>Follower.Followed</c> are cleared, Idle is posted when its tree is in Alert,
    /// Combat, Return, Follow or Relocate_CombatArea, and it is walked back (the runtime GoTo write, no
    /// handle events) when more than 2 m from the hold position. That it then acquires no aggro and
    /// does not fight back is not live-verified. A dead unit drops out of the hold. Release,
    /// <see cref="ClearPet"/> and plugin unload write <c>Active</c> back to 1 only when the hold saved 1,
    /// <c>Active</c> still reads 0 and no buff with <c>DisableAggroBuff</c> <c>TargetDontAttackOthers</c>
    /// is on the unit; otherwise the game's current value is left. Release also drops the walk-back
    /// goal. Releasing a pet puts it back in <see cref="PetStance.Follow"/>. Holding a unit that is
    /// already held keeps its hold position. Server-only; main thread only.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The call is off the main thread, or <paramref name="hold"/> is true and the handle is disabled or
    /// disposed. Releasing never throws for the handle's state.
    /// </exception>
    public void Hold(IUnitHandle handle, bool hold)
    {
        Units.EnsureMainThread(nameof(Hold));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (!hold && _stances == null) return;
        Stances.Hold(handle, hold);
        if (hold) EnsurePetLoop();
    }

    /// <summary>True while the unit is held (<see cref="Hold"/>, <see cref="HoldAt"/> or a pet in Hold).</summary>
    /// <exception cref="InvalidOperationException">The call is off the main thread.</exception>
    public bool IsHeld(IUnitHandle handle)
    {
        Units.EnsureMainThread(nameof(IsHeld));
        return handle != null && _stances != null && _stances.IsHeld(handle);
    }

    /// <summary>
    /// Stop treating the unit as a pet. A pet in Hold is released as <see cref="Hold"/> describes;
    /// otherwise it keeps whatever it is doing. Call Follow or GoTo to direct it.
    /// </summary>
    public void ClearPet(IUnitHandle handle)
    {
        Units.EnsureMainThread(nameof(ClearPet));
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        _stances?.ClearPet(handle);
    }

    /// <summary>True while <paramref name="handle"/> is a pet set by <see cref="SetPet(IUnitHandle, Entity)"/>.</summary>
    public bool IsPet(IUnitHandle handle) => handle != null && _stances != null && _stances.IsPet(handle);

    private void EnsurePetLoop()
    {
        if (_petLoopPosted || _stances == null || _stances.Count == 0) return;
        _petLoopPosted = true;
        Progenitor.Dev.MainThread.PostDelayed(0.5, PetLoop, _ctx.Owner);
    }

    private void PetLoop()
    {
        _petLoopPosted = false;
        if (_stances == null || _stances.Count == 0) return;
        _stances.Step((id, e) => Plugin.Logger?.LogWarning("Units: pet step for " + id + " threw: " + e.Message));
        EnsurePetLoop();
    }

    private sealed class StanceRelease : IDisposable
    {
        private PluginUnits? _units;

        public StanceRelease(PluginUnits units) => _units = units;

        public void Dispose()
        {
            var units = _units;
            _units = null;
            if (units?._stances == null) return;
            try { units._stances.ReleaseAll(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: stance release on unload threw: " + e.Message); }
        }
    }

    private sealed class NullStanceRuntime : IUnitStanceRuntime
    {
        internal static readonly NullStanceRuntime Instance = new();
        public string PetTick(UnitId pet, UnitId owner) => "gone";
        public bool TryGetPosition(UnitId unit, out UnitVec3 position) { position = default; return false; }
        public bool? ReadAggroActive(UnitId unit) => null;
        public string HoldTick(UnitId unit) => "gone";
        public void ReleaseHold(UnitId unit, bool? active) { }
        public string AttackTick(UnitId unit, UnitId target) => "gone";
        public bool HasAggroBlockingBuff(UnitId unit) => false;
        public bool WalkTo(UnitId unit, UnitVec3 position) => false;
    }

    public void Follow(IUnitHandle handle, Entity target)
    {
        _ctx.EnsureAttached();
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        handle.Follow(UnitId.From(target));
    }

    /// <summary>
    /// Despawn every unit this plugin spawned, and then their live summons (units whose
    /// <c>EntityOwner</c> is one of them and that carry <c>Minion</c> or <c>IsMinion</c>) over the next
    /// few seconds. Plugin unload also does this via <see cref="PluginContext.Track"/>. Every removal
    /// takes the replicated path, so clients drop the entities too.
    /// </summary>
    public void RemoveAll()
    {
        _ctx.EnsureAttached();
        Units.RemoveAll(_ctx.Owner);
    }

    /// <summary>
    /// Remove NPC units, their summons and loot drops within <paramref name="radius"/> meters
    /// (horizontal distance, any height) of <paramref name="center"/>. Server only: on the client, or
    /// with the Units module skipped, it returns a disabled, complete result and one Debug line.
    /// Throws off the game main thread.
    /// <para>
    /// <b>Scope.</b> <see cref="ClearAreaScope.Owned"/> (the default) removes only units this plugin
    /// spawned through <c>ctx.Units</c>, their summons, and attributable drops; a clear must never
    /// delete the world's or another plugin's NPCs unless the caller asks with
    /// <see cref="ClearAreaScope.All"/>. In every scope, player characters, users, anything whose
    /// <c>EntityOwner</c> chain reaches a player, servants, mounts (unless this plugin spawned them)
    /// and castle structures are left alone. Only entities with <c>BehaviourTreeInstance</c> (units) or
    /// <c>ItemPickup</c> (drops) are ever removed.
    /// </para>
    /// <para>
    /// <b>Drops.</b> <c>ItemPickup</c> does not record who dropped it. In Owned scope a drop is removed
    /// only when its <c>EntityOwner</c> chain reaches one of this plugin's units, or when
    /// <see cref="ClearAreaOptions.IncludeUnattributedDrops"/> is set.
    /// </para>
    /// <para>
    /// <b>Passes.</b> Pass 1 runs inside this call. Bosses keep summoning while they are being removed,
    /// so the call surveys again every <see cref="ClearAreaPolicy.PassIntervalSeconds"/> s until a pass
    /// finds nothing new and nothing it removed is still alive, or
    /// <see cref="ClearAreaOptions.MaxPasses"/> passes have run; then it counts what is left. The
    /// returned result updates as that happens; <see cref="ClearAreaOptions.OnComplete"/> fires at the
    /// end (inside this call when pass 1 already finds nothing more to do). Removal is the same
    /// replicated path as <see cref="IUnitHandle.Despawn"/>; this plugin's own units are disposed
    /// through their handles.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="radius"/> is not a positive number up to <see cref="ClearAreaPolicy.MaxRadius"/>
    /// m, or <see cref="ClearAreaOptions.MaxPasses"/> is outside 1 to
    /// <see cref="ClearAreaOptions.MaxPassesCap"/>.
    /// </exception>
    public ClearAreaResult ClearArea(Unity.Mathematics.float3 center, float radius, ClearAreaOptions? options = null)
    {
        _ctx.EnsureAttached();
        Units.EnsureMainThread(nameof(ClearArea));
        options ??= new ClearAreaOptions();
        int passes = ClearAreaPolicy.Validate(radius, options);
        var result = new ClearAreaResult();

        string? refusal = null;
        if (Units.IsSkipped) refusal = "the Units module is skipped";
        else if (Units.Runtime is not UnityUnitRuntime runtime) refusal = "ClearArea is server-only";
        else
        {
            result.Enabled = true;
            UnityClearArea.Start(runtime, _ctx.Owner, center, radius, options, passes, result);
            return result;
        }

        Units.LogDebug("Units: ClearArea refused: " + refusal + ".");
        result.Refusal = refusal;
        result.IsComplete = true;
        try { options.OnComplete?.Invoke(result); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Units: ClearArea OnComplete threw: " + e.Message); }
        return result;
    }
}
#endif
