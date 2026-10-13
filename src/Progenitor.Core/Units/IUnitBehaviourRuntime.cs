namespace Progenitor.Units;

/// <summary>
/// The writes a behavior step needs on top of <see cref="IUnitRuntime"/>'s
/// Spawn / Follow / GoTo / Despawn. Deliberately a <b>second</b> interface rather than more
/// members on the first: a runtime that does not implement it (the null runtime, an older
/// fake) still compiles, and the sequencer reports every unsupported step as a refusal
/// instead of a crash.
/// </summary>
internal interface IUnitBehaviourRuntime
{
    /// <summary>
    /// Post <c>ExternalBehaviourTreeTransitionRequest { GenericEnemyState.Idle }</c> so the
    /// selected vanilla tree stops wandering and fighting while the module drives the unit.
    /// </summary>
    bool Idle(UnitId unit);

    /// <summary>Turn the unit toward another entity's current position.</summary>
    bool Face(UnitId unit, UnitId target);

    /// <summary>Turn the unit toward a world position.</summary>
    bool Face(UnitId unit, UnitVec3 position);

    /// <summary>
    /// Play a named animation clip. Returns false with a one-line <paramref name="refusal"/>
    /// when this build has no server-side route for it; the sequencer then reports the step
    /// <see cref="UnitStepOutcome.Skipped"/> and still honors its duration.
    /// </summary>
    bool Animate(UnitId unit, string animationName, float seconds, out string? refusal);

    /// <summary>
    /// Point <c>BehaviourTreeBinding.PrefabGUID</c> at a vanilla behavior-tree prefab.
    /// Only meaningful on the spawn tick (see the implementation for why). Returns false
    /// with a one-line <paramref name="refusal"/> when the name/GUID is unknown or its
    /// prefab carries no <c>BehaviourTree</c>.
    /// </summary>
    bool SelectBehaviourTree(UnitId unit, string treeName, int treeGuid, out string? refusal);
}
