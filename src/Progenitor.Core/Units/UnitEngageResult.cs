namespace Progenitor.Units;

/// <summary>
/// Outcome of <see cref="Units.Engage"/>. Combat is requested even when the aggro write
/// cannot be made. Neither flag clears <c>Immortal</c> or posts Follow.
/// </summary>
public readonly struct UnitEngageResult
{
    public UnitEngageResult(bool combatRequested, bool aggroWritten)
    {
        CombatRequested = combatRequested;
        AggroWritten = aggroWritten;
    }

    /// <summary><c>ExternalBehaviourTreeTransitionRequest</c> with Combat was posted.</summary>
    public bool CombatRequested { get; }

    /// <summary><c>AggroConsumer.AggroTarget</c> was set to the target and Active was set.</summary>
    public bool AggroWritten { get; }

    /// <summary>Both the combat request and the aggro write landed.</summary>
    public bool Ok => CombatRequested && AggroWritten;
}
