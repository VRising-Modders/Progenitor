using System;

namespace Progenitor.Units;

/// <summary>
/// One spawned unit owned by a plugin. Core-owned so a collectible plugin never holds a
/// game entity across unload: <see cref="IDisposable.Dispose"/> / <see cref="Despawn"/>
/// runs <c>DestroyUtility.Destroy</c> (evidence §11). Double-dispose is a no-op.
/// </summary>
public interface IUnitHandle : IDisposable
{
    /// <summary>Spawnable name or GUID hash this handle was created with.</summary>
    string Prefab { get; }

    /// <summary>BepInEx plugin GUID that owns this spawn. <see cref="Units.RemoveAll"/> keys on the owner object; the plugin sweep matches this string too.</summary>
    string PluginId { get; }

    /// <summary>Entity index:version, or none on a disabled handle.</summary>
    UnitId Entity { get; }

    /// <summary>
    /// False on the client, when the module is skipped, or when the prefab was missing.
    /// Follow / GoTo become no-ops; the caller still Disposes it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/> / <see cref="Despawn"/>, including a second call.</summary>
    bool IsDisposed { get; }

    /// <summary>Spawn â†’ Follow / GoTo â†’ Disposed. Disabled handles skip Following / GoingTo.</summary>
    UnitHandleState State { get; }

    /// <summary>Current Translation, or the last written spawn/goal if the entity is gone.</summary>
    UnitVec3 Position { get; }

    /// <summary>
    /// Hookable behavior steps over this unit's vanilla tree: <c>Behaviour.Play(...)</c> /
    /// <c>Behaviour.Stop()</c>. Never a compiled behavior-tree blob. Present on a disabled
    /// handle too, where <c>Play</c> logs one line and does nothing. Stopped by
    /// <see cref="Despawn"/>, <c>Units.RemoveAll</c> and Core uninstall.
    /// </summary>
    IUnitBehaviour Behaviour { get; }

    /// <summary>
    /// Walk toward a real entity (player character or prefab instance). Verified live
    /// (evidence 33): Follower overlay + Follow behavior-tree request. Never a bare marker.
    /// Prefabs that do not ship Follower (CHAR_Critter_Rat, evidence 116) receive the
    /// component on this call; it is removed on GoTo / Despawn / RemoveAll if we added it.
    /// Clears a live GoTo first (TargettingMode None, TargetEntity_Movement Null) so the
    /// handle's Following state is what the entity actually runs. Re-posts the Follow BT
    /// request only when the target changed.
    /// </summary>
    /// <summary>
    /// Request Combat against <paramref name="target"/> and set
    /// <c>AggroConsumer.AggroTarget</c>. Does not clear <c>Immortal</c> and does not
    /// post Follow. A disabled handle returns both flags false.
    /// </summary>
    UnitEngageResult Engage(UnitId target);

    void Follow(UnitId target);

    /// <summary>
    /// Steer toward a world position. Verified 2026-09-03 (evidence 46): write
    /// <c>AiMove_Server</c> the way ApproachPosition.Tick does. Clears a live Follow first
    /// (Follower.Followed Null, Idle BT, TargettingMode that ApproachEntity wrote) then
    /// posts Idle and writes the ApproachPosition set, so GoingTo is what the entity runs.
    /// A Follower this handle added (prefab had none) is removed here so FollowerSystem
    /// does not keep iterating the unit. Never a raw PathRequest (evidence §11 / §20).
    /// </summary>
    void GoTo(UnitVec3 position);

    /// <summary>Same as <see cref="IDisposable.Dispose"/>: buff sweep, then <c>DestroyUtility.Destroy</c> alone so clients see the removal (<c>DestroyTag</c> only as a logged fallback).</summary>
    void Despawn();

    /// <summary>
    /// Instance-only Health / UnitStats / UnitLevel write. Never edits the shared prefab
    /// (<c>Content.Edit</c>). When max health still reads 0 after spawn, the request is held
    /// until the Units poll sees a computed max. Disabled / disposed handles refuse.
    /// </summary>
    UnitStatsWriteResult TrySetStats(UnitStatOverrides stats);

    /// <summary>
    /// End the unit <paramref name="seconds"/> of server time from now: adds <c>Age</c> when missing
    /// and writes <c>LifeTime { Age + seconds, Destroy }</c> on the entity, so the limit survives a
    /// Core swap, a world save and a restart (Core's lifetime sweep enforces it). Plugin unload still
    /// despawns owned units at once. Disabled / disposed refuse.
    /// </summary>
    bool TrySetLifetime(float seconds);

    /// <summary>
    /// Write <c>Team.Value</c> and/or <c>Team.FactionIndex</c> on this instance. Null args are skipped.
    /// </summary>
    bool TrySetTeam(int? teamValue = null, int? factionIndex = null);

    /// <summary>
    /// Clear feedability and strip V Blood identity components on this instance. See
    /// <see cref="UnitSpawnOptions.StripVBlood"/>.
    /// </summary>
    UnitStripVBloodResult TryStripVBlood();
}
