using System;
using System.Globalization;

namespace Progenitor.Stations;

/// <summary>
/// A burning structure's state (<c>ctx.Stations.Brazier</c>): the player's on/off switch, whether it
/// burns now, and its fuel. Read from the game's <c>BurnContainer</c> (the switch and progress through
/// the current fuel item) and <c>Bonfire</c> (burning, strength, fuel item, seconds per item) on the
/// server. Game-free.
/// </summary>
public sealed class BrazierState
{
    /// <summary>
    /// The player's switch (<c>BurnContainer.Enabled</c>), the state the vanilla window's toggle
    /// changes. Persistent: while it is on and fuel is inside, the game lights the brazier by itself.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The structure carries the game's <c>Bonfire</c> burn state. False for a burn container
    /// without it: then <see cref="Burning"/>, <see cref="Strength"/>, <see cref="FuelItemGuid"/>
    /// and <see cref="BurnTimeSeconds"/> are zero and the fuel cannot be counted.
    /// </summary>
    public bool HasBonfire { get; init; }

    /// <summary>
    /// Burning now (<c>Bonfire.IsActive</c>). The game burns only while <see cref="Enabled"/> is on,
    /// during the day, and while the structure's <c>EditableTileModel.CanRepair</c> flag is clear
    /// (inferred to mean "not damaged"); it takes the next fuel item from the
    /// structure's own inventory when the current one is used up.
    /// </summary>
    public bool Burning { get; init; }

    /// <summary>Effect strength 0..1 (<c>Bonfire.Strength</c>): ramps up while burning and down after.</summary>
    public float Strength { get; init; }

    /// <summary>Progress 0..1 through the fuel item being burnt (<c>BurnContainer.Progress</c>); 0 when none is started.</summary>
    public float Progress { get; init; }

    /// <summary>The fuel item's prefab GUID hash (<c>Bonfire.InputItem</c>); 0 without <see cref="HasBonfire"/>.</summary>
    public int FuelItemGuid { get; init; }

    /// <summary>Fuel items waiting in the structure's own inventory (the one being burnt is not counted).</summary>
    public int FuelAmount { get; init; }

    /// <summary>Real seconds one fuel item burns (<c>Bonfire.BurnTime</c>).</summary>
    public float BurnTimeSeconds { get; init; }

    /// <summary>
    /// Burning seconds the fuel inside lasts: the rest of the started item plus every waiting one
    /// (<see cref="BrazierLogic.FuelSecondsLeft"/>). Daytime seconds: nothing burns at night. For the
    /// one server frame after a new item is taken (progress still 0 while it already left the
    /// inventory) this is one item short.
    /// </summary>
    public float FuelSecondsLeft => BrazierLogic.FuelSecondsLeft(Progress, FuelAmount, BurnTimeSeconds);

    /// <summary><c>on, burning 80%, fuel 12x -1234567 (1440 s of burning)</c>.</summary>
    public override string ToString() => BrazierLogic.Describe(this);
}

/// <summary>The answer of <c>ctx.Stations.SetBrazierEnabled</c>. Game-free.</summary>
public sealed class BrazierToggleResult
{
    /// <summary>True when the switch is now in the requested state.</summary>
    public bool Ok { get; init; }

    /// <summary>True when the call changed the switch (false when it was already in that state).</summary>
    public bool Changed { get; init; }

    /// <summary>The switch after the call.</summary>
    public bool Enabled { get; init; }

    /// <summary>Why the call refused or what it did, in one sentence.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>Game-free half of the brazier reads and the switch: fuel arithmetic, the write plan, one-line text.</summary>
public static class BrazierLogic
{
    /// <summary>
    /// Burning seconds left: <c>((progress &gt; 0 ? 1 - progress : 0) + waiting) * burnTime</c>. The
    /// started item is already out of the inventory, so its rest is added to the waiting items.
    /// Zero for a non-positive or non-finite burn time; progress is clamped to 0..1.
    /// </summary>
    public static float FuelSecondsLeft(float progress, int waitingItems, float burnTimeSeconds)
    {
        if (!(burnTimeSeconds > 0f) || float.IsInfinity(burnTimeSeconds)) return 0f;
        float p = float.IsNaN(progress) ? 0f : Math.Clamp(progress, 0f, 1f);
        float started = p > 0f ? 1f - p : 0f;
        return (started + Math.Max(0, waitingItems)) * burnTimeSeconds;
    }

    /// <summary>
    /// The switch write a set needs: null when the switch already reads <paramref name="want"/>,
    /// otherwise the byte to write (1 on, 0 off). The game's own toggle flips the value; a set
    /// writes only on a difference, so two callers asking for the same state never undo each other.
    /// </summary>
    public static byte? PlanSet(bool current, bool want) => current == want ? null : want ? (byte)1 : (byte)0;

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> unless <paramref name="radius"/> is finite and
    /// positive (a NaN radius would otherwise pass every distance test).
    /// </summary>
    public static void RequireRadius(float radius)
    {
        if (!(radius > 0f) || float.IsInfinity(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "radius must be finite and positive");
    }

    /// <summary>The one-line form <see cref="BrazierState.ToString"/> returns.</summary>
    public static string Describe(BrazierState s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        var text = (s.Enabled ? "on" : "off") + ", ";
        if (!s.HasBonfire) return text + "no burn state";
        text += (s.Burning ? "burning " : "dark ") + ((int)Math.Round(s.Strength * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        text += ", fuel " + s.FuelAmount.ToString(CultureInfo.InvariantCulture) + "x " + s.FuelItemGuid.ToString(CultureInfo.InvariantCulture);
        float left = s.FuelSecondsLeft;
        if (left > 0f) text += " (" + ((int)Math.Round(left)).ToString(CultureInfo.InvariantCulture) + " s of burning)";
        return text;
    }
}
