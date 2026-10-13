using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Units;

/// <summary>Which NPCs <c>ctx.Units.ClearArea</c> may remove.</summary>
public enum ClearAreaScope
{
    /// <summary>
    /// Default. Only units this plugin spawned through <c>ctx.Units</c>, their summons, and drops
    /// that can be attributed to them (or every drop in the area, when
    /// <see cref="ClearAreaOptions.IncludeUnattributedDrops"/> is set).
    /// </summary>
    Owned = 0,

    /// <summary>
    /// Every non-player NPC unit, summon and drop inside the area, whoever spawned it: the world's
    /// own NPCs and other plugins' units included. Player characters, users, anything a player
    /// owns, servants, mounts and castle structures are still never touched.
    /// </summary>
    All = 1,
}

/// <summary>
/// Settings for <c>ctx.Units.ClearArea</c>. A null options object means these defaults.
/// <para>
/// <b>Why the default scope is <see cref="ClearAreaScope.Owned"/>.</b> An area clear is a
/// destructive sweep over entities the caller did not list. With <see cref="ClearAreaScope.All"/>
/// a test-arena command would also delete the world's own NPCs and units other plugins spawned and
/// still track. A plugin should never delete what it does not own unless its caller asked for it
/// explicitly, so the wide sweep is opt-in.
/// </para>
/// </summary>
public sealed class ClearAreaOptions
{
    /// <summary>Default <see cref="MaxPasses"/>.</summary>
    public const int DefaultMaxPasses = 4;

    /// <summary>Largest accepted <see cref="MaxPasses"/>.</summary>
    public const int MaxPassesCap = 16;

    /// <summary>Which NPCs may be removed. Default <see cref="ClearAreaScope.Owned"/>.</summary>
    public ClearAreaScope Scope { get; set; } = ClearAreaScope.Owned;

    /// <summary>Remove NPC units (entities with a behavior tree) that are not summons. Default true.</summary>
    public bool IncludeUnits { get; set; } = true;

    /// <summary>
    /// Remove summons: units with <c>Minion</c> or <c>IsMinion.Value</c> whose <c>EntityOwner</c> chain
    /// reaches one of this plugin's units (<see cref="ClearAreaScope.Owned"/>) or any non-player unit
    /// (<see cref="ClearAreaScope.All"/>). Default true.
    /// </summary>
    public bool IncludeSummons { get; set; } = true;

    /// <summary>
    /// Remove loot drops (<c>ItemPickup</c> entities). Default true. In
    /// <see cref="ClearAreaScope.Owned"/> this covers only drops whose <c>EntityOwner</c> chain reaches
    /// one of this plugin's units; see <see cref="IncludeUnattributedDrops"/>.
    /// </summary>
    public bool IncludeDrops { get; set; } = true;

    /// <summary>
    /// In <see cref="ClearAreaScope.Owned"/>, also remove drops that cannot be attributed to anyone.
    /// Default false. <c>ItemPickup</c> records the item and amount but not who dropped it, and a
    /// drop is only attributable when it carries an <c>EntityOwner</c> pointing (directly or through
    /// owners) at one of this plugin's units — which loot from a killed boss may not. Set this when
    /// the area holds only your own arena, since it also removes loose items the world or a player
    /// put down there (a drop whose owner chain reaches a player is still never removed). Ignored in
    /// <see cref="ClearAreaScope.All"/>, which removes every non-player drop in the area.
    /// </summary>
    public bool IncludeUnattributedDrops { get; set; }

    /// <summary>
    /// Removal passes, one per <see cref="ClearAreaPolicy.PassIntervalSeconds"/>. Bosses keep summoning
    /// while they are being removed, so the call surveys again until a pass finds nothing new and
    /// nothing it scheduled is still alive, or this many passes have run. 1 to
    /// <see cref="MaxPassesCap"/>; default <see cref="DefaultMaxPasses"/>.
    /// </summary>
    public int MaxPasses { get; set; } = DefaultMaxPasses;

    /// <summary>
    /// Called once on the main thread when the clear finishes (also when it is refused or the world
    /// reloads mid-clear). Dropped with the rest of the work if the plugin unloads first.
    /// </summary>
    public Action<ClearAreaResult>? OnComplete { get; set; }
}

/// <summary>
/// Live counts of one <c>ctx.Units.ClearArea</c> call. The call returns after the first pass; later
/// passes update this object over the next seconds. Read it from the main thread, or wait for
/// <see cref="ClearAreaOptions.OnComplete"/>.
/// </summary>
public sealed class ClearAreaResult
{
    /// <summary>False when the call did nothing: client side, module skipped, or no server world.</summary>
    public bool Enabled { get; internal set; }

    /// <summary>Why nothing (more) was done, or null.</summary>
    public string? Refusal { get; internal set; }

    /// <summary>Non-summon units whose removal was started.</summary>
    public int UnitsRemoved { get; internal set; }

    /// <summary>Summons whose removal was started.</summary>
    public int SummonsRemoved { get; internal set; }

    /// <summary>Loot drops whose removal was started.</summary>
    public int DropsRemoved { get; internal set; }

    /// <summary>Removal passes run so far.</summary>
    public int Passes { get; internal set; }

    /// <summary>
    /// Targets still present at the end: 0 when the last pass found nothing new and nothing it had
    /// scheduled was still alive; otherwise the count from a final survey
    /// <see cref="ClearAreaPolicy.VerifyDelaySeconds"/> after the last pass, which includes any
    /// removal still in flight.
    /// </summary>
    public int Remaining { get; internal set; }

    /// <summary>True once the clear has finished (or was refused).</summary>
    public bool IsComplete { get; internal set; }

    /// <summary>Everything whose removal was started.</summary>
    public int Total => UnitsRemoved + SummonsRemoved + DropsRemoved;

    internal void Count(ClearTargetKind kind)
    {
        switch (kind)
        {
            case ClearTargetKind.Unit: UnitsRemoved++; break;
            case ClearTargetKind.Summon: SummonsRemoved++; break;
            case ClearTargetKind.Drop: DropsRemoved++; break;
        }
    }

    public override string ToString() =>
        "units=" + UnitsRemoved.ToString(CultureInfo.InvariantCulture)
        + " summons=" + SummonsRemoved.ToString(CultureInfo.InvariantCulture)
        + " drops=" + DropsRemoved.ToString(CultureInfo.InvariantCulture)
        + " passes=" + Passes.ToString(CultureInfo.InvariantCulture)
        + " remaining=" + Remaining.ToString(CultureInfo.InvariantCulture)
        + (IsComplete ? " complete" : " running")
        + (Refusal != null ? " refusal='" + Refusal + "'" : "");
}

/// <summary>What one surveyed entity is to a clear.</summary>
internal enum ClearTargetKind
{
    None = 0,
    Unit,
    Summon,
    Drop,
}

/// <summary>What happens after a removal pass.</summary>
internal enum ClearAreaStep
{
    /// <summary>Survey and remove again after <see cref="ClearAreaPolicy.PassIntervalSeconds"/>.</summary>
    AnotherPass,

    /// <summary>Pass budget spent with work still visible: count what is left, then finish.</summary>
    Verify,

    /// <summary>Nothing new and nothing scheduled is still alive: finished, Remaining 0.</summary>
    Done,
}

/// <summary>
/// Facts the live survey reads off one entity. Game-free so the classification is unit-tested.
/// </summary>
internal struct ClearCandidate
{
    /// <summary>Inside the clear area (horizontal distance ≤ radius).</summary>
    public bool InArea;

    /// <summary>This clear (or the runtime) already started removing it.</summary>
    public bool AlreadyScheduled;

    /// <summary>Already carries <c>DestroyTag</c>.</summary>
    public bool Dying;

    public bool IsPlayerCharacter;
    public bool IsUser;

    /// <summary><c>CastleHeartConnection</c> (not <c>TileModel</c>: NPC units carry it too).</summary>
    public bool IsCastleStructure;

    /// <summary><c>ServantConnectedCoffin</c> or <c>ServantTypeData</c>.</summary>
    public bool IsServant;

    /// <summary><c>Mountable</c>: horses and other mounts, a player's or not.</summary>
    public bool IsMountable;

    /// <summary><c>EntityOwner</c> chain reaches a <c>PlayerCharacter</c> or <c>User</c>.</summary>
    public bool PlayerOwned;

    /// <summary>The owner chain ran past <see cref="ClearAreaPolicy.MaxOwnerHops"/> or looped: owner unknown.</summary>
    public bool OwnerUnknown;

    /// <summary>Has <c>BehaviourTreeInstance</c>: an NPC unit.</summary>
    public bool IsUnit;

    /// <summary>Has <c>ItemPickup</c>: a loot drop.</summary>
    public bool IsDrop;

    /// <summary>Has <c>Minion</c>, or <c>IsMinion.Value</c> is true.</summary>
    public bool IsMinion;

    /// <summary>The entity of one of this plugin's live unit handles.</summary>
    public bool IsOurUnit;

    /// <summary><c>EntityOwner</c> chain reaches one of this plugin's units (live or already removed) or their summons.</summary>
    public bool OwnedByOurs;
}

/// <summary>Result of following <c>EntityOwner</c> links from one entity.</summary>
internal readonly struct OwnerChain
{
    public OwnerChain(bool playerOwned, bool oursOwned, bool unknown)
    {
        PlayerOwned = playerOwned;
        OursOwned = oursOwned;
        Unknown = unknown;
    }

    public bool PlayerOwned { get; }
    public bool OursOwned { get; }
    public bool Unknown { get; }
}

/// <summary>
/// Pure decisions behind <c>ctx.Units.ClearArea</c> and the summon sweep that runs after every unit
/// despawn. The live survey (UnityClearArea.cs) only reads facts and acts on these answers.
/// </summary>
internal static class ClearAreaPolicy
{
    /// <summary>Seconds between removal passes. Summons spawned while the boss was being removed show up here.</summary>
    public const double PassIntervalSeconds = 1.0;

    /// <summary>
    /// Seconds between the last pass and the final count. Longer than a pass interval because a
    /// replicated removal takes up to about 2 s (buff sweep, then <c>DestroyUtility.Destroy</c>).
    /// </summary>
    public const double VerifyDelaySeconds = 2.5;

    /// <summary>Owner links followed before the owner is treated as unknown (and the entity left alone).</summary>
    public const int MaxOwnerHops = 4;

    /// <summary>Largest accepted clear radius, in meters.</summary>
    public const float MaxRadius = 200f;

    /// <summary>
    /// Validate a caller's radius and options; returns the effective pass count. Throws on a bad
    /// radius or pass count rather than clamping: a typo in a radius is a world-wide delete.
    /// </summary>
    public static int Validate(float radius, ClearAreaOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "radius must be a positive number of meters.");
        if (radius > MaxRadius)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "radius is capped at " + MaxRadius.ToString(CultureInfo.InvariantCulture) + " m.");
        if (options.MaxPasses < 1 || options.MaxPasses > ClearAreaOptions.MaxPassesCap)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxPasses, "MaxPasses must be 1 to " + ClearAreaOptions.MaxPassesCap + ".");
        if (!Enum.IsDefined(typeof(ClearAreaScope), options.Scope))
            throw new ArgumentOutOfRangeException(nameof(options), options.Scope, "unknown scope.");
        return options.MaxPasses;
    }

    /// <summary>Horizontal (XZ) distance test: the area is a vertical cylinder, any height.</summary>
    public static bool InArea(float centerX, float centerZ, float radius, float x, float z)
    {
        float dx = x - centerX, dz = z - centerZ;
        if (float.IsNaN(dx) || float.IsNaN(dz)) return false;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>
    /// What a clear does with one entity. Order matters: area and scheduling first, then the
    /// protections that hold in every scope, then drops, then units by scope.
    /// </summary>
    public static ClearTargetKind Classify(in ClearCandidate c, ClearAreaOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!c.InArea || c.AlreadyScheduled || c.Dying) return ClearTargetKind.None;
        if (IsProtected(c)) return ClearTargetKind.None;

        if (c.IsDrop && !c.IsUnit)
        {
            if (!options.IncludeDrops) return ClearTargetKind.None;
            if (options.Scope == ClearAreaScope.All) return ClearTargetKind.Drop;
            return c.OwnedByOurs || options.IncludeUnattributedDrops ? ClearTargetKind.Drop : ClearTargetKind.None;
        }

        if (!c.IsUnit) return ClearTargetKind.None;

        if (c.IsOurUnit)
        {
            if (c.IsMinion && c.OwnedByOurs) return options.IncludeSummons ? ClearTargetKind.Summon : ClearTargetKind.None;
            return options.IncludeUnits ? ClearTargetKind.Unit : ClearTargetKind.None;
        }

        // Mounts are protected unless this plugin spawned them (a wild horse and a player's horse
        // cannot be told apart reliably from a component read).
        if (c.IsMountable) return ClearTargetKind.None;

        if (c.IsMinion && c.OwnedByOurs)
            return options.IncludeSummons ? ClearTargetKind.Summon : ClearTargetKind.None;

        if (options.Scope != ClearAreaScope.All) return ClearTargetKind.None;

        if (c.IsMinion) return options.IncludeSummons ? ClearTargetKind.Summon : ClearTargetKind.None;
        return options.IncludeUnits ? ClearTargetKind.Unit : ClearTargetKind.None;
    }

    /// <summary>Never removed in any scope.</summary>
    public static bool IsProtected(in ClearCandidate c) =>
        c.IsPlayerCharacter || c.IsUser || c.PlayerOwned || c.OwnerUnknown || c.IsCastleStructure || c.IsServant;

    /// <summary>
    /// Follow <c>EntityOwner</c> from <paramref name="start"/> (not counting itself) for at most
    /// <paramref name="maxHops"/> links. A player or user anywhere in the chain wins. An entity that
    /// names itself as owner ends the chain. A chain that loops through two or more entities, or is
    /// longer than the cap, is <see cref="OwnerChain.Unknown"/>, which the classification treats as
    /// protected.
    /// </summary>
    public static OwnerChain WalkOwners(
        UnitId start,
        Func<UnitId, UnitId> ownerOf,
        Func<UnitId, bool> isPlayer,
        Func<UnitId, bool> isOurs,
        int maxHops = MaxOwnerHops)
    {
        if (ownerOf == null) throw new ArgumentNullException(nameof(ownerOf));
        if (isPlayer == null) throw new ArgumentNullException(nameof(isPlayer));
        if (isOurs == null) throw new ArgumentNullException(nameof(isOurs));

        bool ours = false;
        var seen = new HashSet<UnitId> { start };
        var previous = start;
        var current = ownerOf(start);
        for (int hop = 0; ; hop++)
        {
            // No owner, or an entity that owns itself: the chain ends here.
            if (current.IsNone || current == previous) return new OwnerChain(false, ours, false);
            if (hop >= maxHops || !seen.Add(current)) return new OwnerChain(false, ours, true);
            if (isPlayer(current)) return new OwnerChain(true, ours, false);
            if (isOurs(current)) ours = true;
            previous = current;
            current = ownerOf(current);
        }
    }

    /// <summary>
    /// After pass <paramref name="passesRun"/> (1-based) started <paramref name="foundThisPass"/>
    /// new removals while <paramref name="earlierStillAlive"/> removals from earlier passes had not
    /// finished yet.
    /// </summary>
    public static ClearAreaStep After(int passesRun, int maxPasses, int foundThisPass, int earlierStillAlive)
    {
        if (foundThisPass <= 0 && earlierStillAlive <= 0) return ClearAreaStep.Done;
        if (passesRun >= maxPasses) return ClearAreaStep.Verify;
        return ClearAreaStep.AnotherPass;
    }
}
