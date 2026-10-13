namespace Progenitor.Units;

/// <summary>
/// Game (or a test fake) behind <see cref="Units"/>. Tests inject a fake so they never
/// need the game; the server uses <c>UnityUnitRuntime</c>. Every method swallows game
/// exceptions and returns a disabled/empty result rather than throw into a hook.
/// </summary>
internal interface IUnitRuntime
{
    /// <summary>True in a VRisingServer process. Client Spawn/Follow/GoTo must not touch ECS.</summary>
    bool IsServer { get; }

    /// <summary>
    /// Why the last <see cref="Spawn"/> returned none, for the one log line Units writes.
    /// Null means the generic "no prefab named X" line.
    /// </summary>
    string? LastMissReason { get; }

    /// <summary>
    /// Instantiate <paramref name="prefabName"/> (or the GUID hash in
    /// <paramref name="prefabGuid"/>) at <paramref name="position"/>. Returns
    /// <see cref="UnitId.None"/> when the prefab is missing or instantiate failed.
    /// </summary>
    UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position);

    /// <summary>
    /// Follower overlay + Follow behavior-tree request. Must clear a live GoTo first
    /// (TargettingMode None, TargetEntity_Movement Null, TargetPosition zeroed). When the
    /// unit has no Follower (CHAR_Critter_Rat), add the component then overlay; refuse
    /// only if that add fails. False when the add/overlay is refused, the target is none,
    /// or the GoTo fields were left set. Re-posts the Follow BT request only when the
    /// target changed.
    /// </summary>
    bool Follow(UnitId unit, UnitId target);

    /// <summary>
    /// Post Combat and set <c>AggroConsumer.AggroTarget</c> to <paramref name="target"/>.
    /// Does not clear <c>Immortal</c> and does not post Follow.
    /// </summary>
    UnitEngageResult Engage(UnitId unit, UnitId target);

    /// <summary>
    /// True while this runtime still owns a <c>Follower</c> it added because the prefab
    /// did not ship one. False when the prefab already had the component, Follow never
    /// added it, or GoTo / Despawn already removed it.
    /// </summary>
    /// <summary>One pet step: fight a live aggro target, otherwise follow the owner. Returns "combat", "follow" or "gone".</summary>
    string PetTick(UnitId pet, UnitId owner);

    bool AddedFollower(UnitId unit);

    /// <summary>
    /// ApproachPosition <c>AiMove_Server</c> field set. Must clear a live Follow first
    /// (Followed Null, Stationary 0, Idle BT, TargettingMode that Follow wrote) so
    /// FollowerSystem cannot rewrite the goal. Removes a Follower this runtime added
    /// (prefab had none) so the exclusive GoTo write is not fighting an extra component.
    /// False when the unit has no AiMove_Server, the overlay is refused, or Follow is
    /// still active after the clear.
    /// </summary>
    bool GoTo(UnitId unit, UnitVec3 position);

    /// <summary>Buff sweep, then replicated <c>DestroyUtility.Destroy</c> (DestroyTag only as a delayed fallback). False when the entity was already gone.</summary>
    bool Despawn(UnitId unit);

    bool Exists(UnitId unit);

    bool TryGetPosition(UnitId unit, out UnitVec3 position);

    /// <summary>Read Health max / value / IsDead. False when missing.</summary>
    bool TryGetHealth(UnitId unit, out float maxHealth, out float value, out bool isDead);

    /// <summary>True when the entity has a <c>Dead</c> component.</summary>
    bool HasDeadComponent(UnitId unit);

    /// <summary>Write MaxHealth (_Value), Value, and MaxRecoveryHealth.</summary>
    bool TrySetHealthPools(UnitId unit, float maxHealth, float value, float maxRecovery);

    /// <summary>Read PhysicalPower / SpellPower. False when UnitStats is missing.</summary>
    bool TryGetUnitPower(UnitId unit, out float physicalPower, out float spellPower);

    /// <summary>Write whichever power fields are non-null. False when UnitStats is missing.</summary>
    bool TrySetUnitPower(UnitId unit, float? physicalPower, float? spellPower);

    /// <summary>Read UnitLevel.Level. False when missing (players have none).</summary>
    bool TryGetLevel(UnitId unit, out int level);

    /// <summary>Write UnitLevel.Level. False when missing.</summary>
    bool TrySetLevel(UnitId unit, int level);

    /// <summary>
    /// Add <c>Age</c> when missing and write <c>LifeTime { Age + seconds, Destroy }</c>. False when the write is refused.
    /// </summary>
    bool TrySetLifetime(UnitId unit, float seconds);

    /// <summary>
    /// Write <c>Team.Value</c> and/or <c>Team.FactionIndex</c> when non-null. False when Team is missing.
    /// </summary>
    bool TrySetTeam(UnitId unit, int? teamValue, int? factionIndex);

    /// <summary>
    /// Clear <c>BloodConsumeSource.CanBeConsumed</c>; remove <c>VBloodUnit</c> and
    /// <c>VBloodConsumeSource</c> when present. Never removes <c>BloodConsumeSource</c>.
    /// </summary>
    UnitStripVBloodResult StripVBlood(UnitId unit);
}

/// <summary>Default when no runtime is injected: every call is a no-op, reported as client-side.</summary>
internal sealed class NullUnitRuntime : IUnitRuntime
{
    internal static readonly NullUnitRuntime Instance = new();

    public bool IsServer => false;

    public string? LastMissReason => null;

    public UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position) => UnitId.None;

    public bool Follow(UnitId unit, UnitId target) => false;

    public UnitEngageResult Engage(UnitId unit, UnitId target) => default;

    public string PetTick(UnitId pet, UnitId owner) => "gone";

    public bool AddedFollower(UnitId unit) => false;

    public bool GoTo(UnitId unit, UnitVec3 position) => false;

    public bool Despawn(UnitId unit) => false;

    public bool Exists(UnitId unit) => false;

    public bool TryGetPosition(UnitId unit, out UnitVec3 position)
    {
        position = default;
        return false;
    }

    public bool TryGetHealth(UnitId unit, out float maxHealth, out float value, out bool isDead)
    {
        maxHealth = 0f; value = 0f; isDead = false; return false;
    }

    public bool HasDeadComponent(UnitId unit) => false;

    public bool TrySetHealthPools(UnitId unit, float maxHealth, float value, float maxRecovery) => false;

    public bool TryGetUnitPower(UnitId unit, out float physicalPower, out float spellPower)
    {
        physicalPower = 0f; spellPower = 0f; return false;
    }

    public bool TrySetUnitPower(UnitId unit, float? physicalPower, float? spellPower) => false;

    public bool TryGetLevel(UnitId unit, out int level) { level = 0; return false; }

    public bool TrySetLevel(UnitId unit, int level) => false;

    public bool TrySetLifetime(UnitId unit, float seconds) => false;

    public bool TrySetTeam(UnitId unit, int? teamValue, int? factionIndex) => false;

    public UnitStripVBloodResult StripVBlood(UnitId unit) => default;
}
