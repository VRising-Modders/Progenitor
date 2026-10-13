namespace Progenitor.Combat;

/// <summary>
/// Game-free decisions for the death / damage event bus. Kept free of Unity / ProjectM so
/// <c>tests/Progenitor.Tests/CombatEventPolicyTests.cs</c> drives the same rules Core uses.
/// </summary>
public static class CombatEventPolicy
{
    /// <summary>
    /// <c>StatType.Health</c> from the server metadata (TypeDefIndex 17185). Blood is 1 and is not
    /// published as <see cref="DamageDealt"/>.
    /// </summary>
    public const int StatTypeHealth = 0;

    /// <summary>
    /// True when a stamped epoch no longer matches the live world epoch — the recorded
    /// <c>index:version</c> pairs on the event must not be used for mutation.
    /// </summary>
    public static bool IsStale(int eventEpoch, int currentEpoch) =>
        eventEpoch != currentEpoch;

    /// <summary>
    /// Classify a kill-path victim: players raise <see cref="PlayerDied"/>; everyone else raises
    /// <see cref="UnitDied"/>. Never both.
    /// </summary>
    public static bool PublishAsPlayerDied(bool hasPlayerCharacter) => hasPlayerCharacter;

    /// <summary>
    /// Whether a <c>StatChangeEvent</c> should become <see cref="DamageDealt"/>. Only Health, and
    /// only when at least one of the two deltas is non-zero (invulnerability can zero
    /// <paramref name="change"/> while leaving <paramref name="originalChange"/> intact).
    /// </summary>
    public static bool ShouldPublishDamage(int statType, float change, float originalChange) =>
        statType == StatTypeHealth
        && (change != 0f || originalChange != 0f);

    /// <summary>
    /// Prefer the richer reason string when both halves of the kill path were readable.
    /// </summary>
    public static string CombineReasons(string? statChangeReason, string? deathReason)
    {
        string a = statChangeReason ?? "";
        string b = deathReason ?? "";
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        if (string.Equals(a, b, System.StringComparison.Ordinal)) return a;
        return a + "/" + b;
    }

    /// <summary>
    /// One-line pin for Core boot / diagnostics.
    /// </summary>
    public static string FormatPins(bool installed, bool deathPatched, bool damageHooked, int deaths, int damages) =>
        "combat:"
        + (installed ? " installed" : " off")
        + (deathPatched ? " death=patched" : " death=unpatched")
        + (damageHooked ? " damage=hooked" : " damage=unhooked")
        + " deaths=" + deaths
        + " damages=" + damages;
}
