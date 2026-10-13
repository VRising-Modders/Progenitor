namespace Progenitor.Units;

/// <summary>
/// A unit's health as read by <c>ctx.Units.GetHealth</c>: <c>Health.Value</c>, <c>Health.MaxHealth</c>
/// and whether the unit is dead (<c>Health.IsDead</c> or a <c>Dead</c> component).
/// </summary>
public readonly struct UnitHealth
{
    public UnitHealth(float current, float max, bool isDead)
    {
        Current = current;
        Max = max;
        IsDead = isDead;
    }

    /// <summary><c>Health.Value</c>.</summary>
    public float Current { get; }

    /// <summary><c>Health.MaxHealth</c>. A unit spawned this frame reads 0 here until the game's spawn systems fill it.</summary>
    public float Max { get; }

    /// <summary><c>Health.IsDead</c> or a <c>Dead</c> component.</summary>
    public bool IsDead { get; }

    /// <summary>
    /// At or below 0 health without being dead: how a downed V Blood boss reads before it is fed on or
    /// executed. False for a unit whose health has not been filled yet (<see cref="Max"/> 0).
    /// </summary>
    public bool IsDowned => !IsDead && Max > 0f && Current <= 0f;

    /// <summary>Alive and standing: not dead, not downed, health filled.</summary>
    public bool IsStanding => !IsDead && Max > 0f && Current > 0f;

    public override string ToString() => Current.ToString("0.#") + "/" + Max.ToString("0.#") + (IsDead ? " dead" : IsDowned ? " downed" : "");
}
