using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Inventory;

namespace Progenitor.Materials;

/// <summary>
/// The kind of game operation that is asking for materials. A provider governs a subset
/// (<see cref="IMaterialSourceProvider.Operations"/>); Progenitor wires only the operations in
/// <see cref="MaterialOperations.Supported"/>. Every other operation keeps the game's own rules.
/// </summary>
public enum MaterialOperation
{
    /// <summary>Building placement from the build menu in the game's shared-inventory mode.</summary>
    Build = 0,

    /// <summary>Starting a craft at a castle workstation.</summary>
    Craft = 1,

    /// <summary>Repairing an item (planned: not wired yet).</summary>
    Repair = 2,

    /// <summary>Castle heart and other upgrade costs (planned: not wired yet).</summary>
    Upgrade = 3,

    /// <summary>Research costs (stays vanilla).</summary>
    Research = 4,

    /// <summary>Anything else that pays with materials (stays vanilla).</summary>
    Other = 5,
}

/// <summary>Which operations Progenitor can currently route through a provider.</summary>
public static class MaterialOperations
{
    /// <summary>
    /// The operations whose availability and payment seams are wired: <see cref="MaterialOperation.Build"/>
    /// and <see cref="MaterialOperation.Craft"/>. A provider that names others is told so once at
    /// registration and those operations stay vanilla.
    /// </summary>
    public static IReadOnlyCollection<MaterialOperation> Supported { get; } =
        new[] { MaterialOperation.Build, MaterialOperation.Craft };

    /// <summary>True when <paramref name="operation"/> is wired.</summary>
    public static bool IsSupported(MaterialOperation operation) =>
        operation == MaterialOperation.Build || operation == MaterialOperation.Craft;
}

/// <summary>
/// Who is paying, for what, and where. Built by Progenitor for each availability view and each
/// payment; the provider reads it and must not keep it beyond the call.
/// </summary>
public readonly struct MaterialContext : IEquatable<MaterialContext>
{
    /// <summary>The acting player's user entity (session id; valid for this world load only).</summary>
    public CastleId User { get; }

    /// <summary>The acting player's character entity (session id).</summary>
    public CastleId Character { get; }

    /// <summary>World-load epoch the ids above belong to.</summary>
    public int WorldEpoch { get; }

    /// <summary>The operation asking for materials.</summary>
    public MaterialOperation Operation { get; }

    /// <summary>
    /// Territory index of the plot the player stands on (the game's own standing-position rule),
    /// or -1 when the player stands on no plot.
    /// </summary>
    public int StandingTerritory { get; }

    /// <summary>The workstation for <see cref="MaterialOperation.Craft"/>; <see cref="ContainerId.None"/> otherwise.</summary>
    public ContainerId Station { get; }

    /// <summary>Construct a context. Progenitor builds these; tests and providers may too.</summary>
    public MaterialContext(CastleId user, CastleId character, int worldEpoch, MaterialOperation operation,
        int standingTerritory, ContainerId station)
    {
        User = user;
        Character = character;
        WorldEpoch = worldEpoch;
        Operation = operation;
        StandingTerritory = standingTerritory;
        Station = station;
    }

    /// <inheritdoc />
    public bool Equals(MaterialContext other) =>
        User.Equals(other.User) && Character.Equals(other.Character) && WorldEpoch == other.WorldEpoch
        && Operation == other.Operation && StandingTerritory == other.StandingTerritory && Station.Equals(other.Station);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MaterialContext other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(User, Character, WorldEpoch, (int)Operation, StandingTerritory, Station);

    /// <inheritdoc />
    public override string ToString() =>
        Operation + " user=" + User + " territory=" + StandingTerritory
        + (Station.IsNone ? "" : " station=" + Station);
}

/// <summary>
/// For one item: the physical containers that may pay for it, in spend order (the first is
/// drained first), and the amount that must stay behind across the whole pool.
/// </summary>
public sealed class ItemSources
{
    /// <summary>Eligible containers, first spent first. Duplicates and unknown ids are ignored.</summary>
    public IReadOnlyList<ContainerId> SpendOrder { get; }

    /// <summary>
    /// Amount of the item that must remain across the pool after any payment; subtracted once from
    /// the pool total, never once per container. 0 for none; negative values count as 0.
    /// </summary>
    public int PoolReserve { get; }

    /// <summary>Construct one item's sources.</summary>
    public ItemSources(IReadOnlyList<ContainerId> spendOrder, int poolReserve = 0)
    {
        SpendOrder = spendOrder ?? throw new ArgumentNullException(nameof(spendOrder));
        PoolReserve = poolReserve < 0 ? 0 : poolReserve;
    }
}

/// <summary>
/// A mod's rule for where materials come from. Register one with <c>ctx.MaterialSource.Register</c>;
/// only one provider may be registered at a time.
///
/// <para>Every member is called on the server main thread, inside the game's own availability
/// check (a synchronous call; nothing spans a frame) or while the per-player totals are refreshed.
/// Members must be fast, must not move items, must not call back into <c>ctx.MaterialSource</c>, and
/// must not throw: an exception is logged, counted in <see cref="MaterialSourceStatus.Faults"/>, and
/// treated as "no remote sources" for that context or item (never as vanilla storage rules, which
/// could be less restrictive than the provider's).</para>
///
/// <para>One game operation (one craft start, one placement) asks the provider once per item, at the
/// game's own affordability check, and pays every cost line from that same answer: a provider that
/// changes its mind or starts throwing between the check and the payment cannot change what that
/// operation pays from.</para>
/// </summary>
public interface IMaterialSourceProvider
{
    /// <summary>
    /// Operations this provider governs. Read at registration and again whenever the provider is
    /// invalidated. Operations outside <see cref="MaterialOperations.Supported"/> stay vanilla.
    /// </summary>
    IReadOnlyCollection<MaterialOperation> Operations { get; }

    /// <summary>
    /// The provider's policy revision for this context. Any change (membership, raid state,
    /// exclusions, settings, container lists) must change it; Progenitor uses it to decide when a
    /// player's displayed totals are stale. Payments always re-resolve regardless.
    /// </summary>
    long Revision(in MaterialContext context);

    /// <summary>
    /// Every container that may pay for any item in this context, in no particular order. Used to
    /// discover which items exist for the displayed totals. Return an empty list for "this context
    /// is not governed": the game's own shared storage then applies as if no provider were registered.
    /// </summary>
    IReadOnlyList<ContainerId> Candidates(in MaterialContext context);

    /// <summary>
    /// The sources for one item (a PrefabGUID hash) in this context. Return null when the provider
    /// does not govern this item here: the game's own shared storage then pays for it.
    /// </summary>
    ItemSources? Sources(in MaterialContext context, int item);
}

/// <summary>
/// Published on the server (<c>ctx.Events.On&lt;MaterialPaid&gt;</c>) after Progenitor paid part
/// of a governed cost line from the provider's containers. One event per line the pool paid any part
/// of, published on the main thread right after the game system that ran the operation finished its
/// update (same frame). <see cref="Paid"/> is less than <see cref="Requested"/> after a shortfall.
/// </summary>
public sealed class MaterialPaid
{
    /// <summary>The operation, player, plot and station.</summary>
    public MaterialContext Context { get; }

    /// <summary>
    /// Identifies the game operation (one craft start, one placement) this line belongs to; every line
    /// of one operation carries the same value. Increasing and unique within a server process, Core swaps
    /// included (seeded from the clock); also printed as <c>tx=</c> in the <c>materials:</c> log lines.
    /// </summary>
    public long Transaction { get; }

    /// <summary>The item (PrefabGUID hash).</summary>
    public int Item { get; }

    /// <summary>How much the game asked the pool for (after the player's bag and the station paid their part).</summary>
    public int Requested { get; }

    /// <summary>How much the pool paid. Equal to <see cref="Requested"/> unless the line was short at payment.</summary>
    public int Paid { get; }

    /// <summary>Per container: how much was taken, in the order taken.</summary>
    public IReadOnlyList<MaterialDebit> Debits { get; }

    internal MaterialPaid(MaterialContext context, long transaction, int item, int requested, int paid, IReadOnlyList<MaterialDebit> debits)
    {
        Context = context;
        Transaction = transaction;
        Item = item;
        Requested = requested;
        Paid = paid;
        Debits = debits;
    }
}

/// <summary>One container's part of a payment.</summary>
public readonly struct MaterialDebit
{
    /// <summary>The container's stable id.</summary>
    public ContainerId Container { get; }

    /// <summary>Amount taken from it.</summary>
    public int Amount { get; }

    /// <summary>Construct a debit row.</summary>
    public MaterialDebit(ContainerId container, int amount)
    {
        Container = container;
        Amount = amount;
    }

    /// <inheritdoc />
    public override string ToString() => Container + "x" + Amount;
}
