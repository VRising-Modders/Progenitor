using System;
using System.Collections.Generic;
using Progenitor.Inventory;

namespace Progenitor.Castles;

/// <summary>How a user stands to a castle (<c>ctx.Castles.RelationTo</c>).</summary>
public enum CastleRelation
{
    /// <summary>No standing: a stranger.</summary>
    None,

    /// <summary>The user owns the heart.</summary>
    Owner,

    /// <summary>The user is in the owner's clan.</summary>
    ClanMember,

    /// <summary>
    /// Not owner and not in the owner's clan, but the game's team check
    /// (<c>Team.IsAllies</c> between the user's character and the heart) says allied.
    /// </summary>
    Allied,
}

/// <summary>
/// One placed container or station in a castle (<see cref="ICastleHandle.Containers"/> and
/// <see cref="ICastleHandle.Stations"/>). The <see cref="Id"/> is stable across restarts; the
/// inventory references are session-scoped.
/// </summary>
public sealed class CastleContainerInfo
{
    /// <summary>The placed model: entity, prefab, tile, rotation, network id.</summary>
    public CastleBuildingInfo Building { get; init; } = new();

    /// <summary>Stable id (P5). <see cref="ContainerId.None"/> when the Inventory module is not running.</summary>
    public ContainerId Id { get; init; }

    /// <summary>The player-set name (<c>NameableInteractable.Name</c>); empty when unnamed or not nameable.</summary>
    public string Nameplate { get; init; } = "";

    /// <summary>True when the model carries <c>NameableInteractable</c> (it can be renamed).</summary>
    public bool Nameable { get; init; }

    /// <summary>
    /// A chest's inventory, a station's input. Instance 0, except on a station whose component
    /// names its input inventory: then the <c>InventoryInstanceElement</c> row holding that entity.
    /// </summary>
    public InventoryRef Main { get; init; }

    /// <summary>
    /// The separate output inventory of a refinement station (furnace, sawmill, loom, blood press, ...)
    /// or a salvage station (the Devourer): the <c>InventoryInstanceElement</c> row whose entity is the
    /// station component's <c>OutputInventoryEntity</c>. <see cref="InventoryRef.None"/> for a chest, a
    /// crafting station (its crafted items land in its one inventory) and every other model.
    /// </summary>
    public InventoryRef Output { get; init; }

    /// <summary>
    /// True for a crafting, refinement or salvage station: a model with a recipe list (<c>WorkstationRecipesBuffer</c>),
    /// a <c>Refinementstation</c> or a <c>Salvagestation</c>. Storage that carries <c>CastleWorkstation</c> only for room and servant
    /// bonuses is a container, not a station.
    /// </summary>
    public bool IsStation { get; init; }

    /// <summary>
    /// True for a refinement station (<c>Refinementstation</c>). A salvage station is not one but also has
    /// an <see cref="Output"/>: test <c>Output.IsNone</c> for a separate output.
    /// </summary>
    public bool IsRefinement { get; init; }

    /// <summary>Slots of <see cref="Main"/> (0 when unreadable).</summary>
    public int SlotCount { get; init; }

    /// <summary>Entity of the placed model (same as <see cref="Building"/>.Entity).</summary>
    public CastleId Entity => Building.Entity;

    /// <inheritdoc />
    public override string ToString() =>
        (Nameplate.Length > 0 ? "\"" + Nameplate + "\" " : "") + Building.Label + " " + Building.Entity
        + " id=" + Id + " slots=" + SlotCount + (Output.IsNone ? "" : " (input+output)");
}

/// <summary>
/// What the castle runtime reports for one placed model with an inventory. Game-free so the
/// test runtime can produce it.
/// </summary>
internal sealed class CastleInventoryRow
{
    internal CastleBuildingInfo Building { get; init; } = new();
    internal bool IsStation { get; init; }
    internal bool IsRefinement { get; init; }

    /// <summary>The input (or only) inventory's instance index.</summary>
    internal int InputInstance { get; init; }

    /// <summary>
    /// The separate output's instance index, -1 for none. Null when the runtime did not say: a
    /// refinement row then keeps instance 1, any other row has no output.
    /// </summary>
    internal int? OutputInstance { get; init; }

    internal int SlotCount { get; init; }
    internal bool Nameable { get; init; }
    internal string Nameplate { get; init; } = "";
}

/// <summary>Builds <see cref="CastleContainerInfo"/> rows from runtime rows. Shared by the handle and the tests.</summary>
internal static class CastleContainerRows
{
    /// <summary>
    /// Set by the Inventory module while it runs: the stable id for a placed model on a
    /// territory. Null leaves ids as <see cref="ContainerId.None"/>.
    /// </summary>
    internal static Func<CastleBuildingInfo, int, ContainerId>? IdResolver;

    /// <summary>Set by the Inventory module while it runs: the stable id of a heart entity.</summary>
    internal static Func<CastleId, ContainerId>? HeartIdResolver;

    internal static IReadOnlyList<CastleContainerInfo> Build(IReadOnlyList<CastleInventoryRow>? rows, int territoryIndex, bool stations, int epoch)
    {
        if (rows == null || rows.Count == 0) return Array.Empty<CastleContainerInfo>();
        var resolver = IdResolver;
        var list = new List<CastleContainerInfo>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r == null || r.IsStation != stations) continue;
            var e = r.Building.Entity;
            ContainerId id = ContainerId.None;
            if (resolver != null)
            {
                try { id = resolver(r.Building, territoryIndex); }
                catch (Exception ex) { Plugin.Logger?.LogWarning("Castles: container id for " + e + " threw: " + ex.Message); }
            }

            int output = OutputOf(r);
            list.Add(new CastleContainerInfo
            {
                Building = r.Building,
                Id = id,
                Nameplate = r.Nameplate ?? "",
                Nameable = r.Nameable,
                Main = new InventoryRef(e.Index, e.Version, r.InputInstance, epoch),
                Output = output >= 0 ? new InventoryRef(e.Index, e.Version, output, epoch) : InventoryRef.None,
                IsStation = r.IsStation,
                IsRefinement = r.IsRefinement,
                SlotCount = r.SlotCount,
            });
        }

        return list;
    }

    /// <summary>The row's output instance: as reported, else instance 1 on a refinement row, else none (-1).</summary>
    internal static int OutputOf(CastleInventoryRow r) => r.OutputInstance ?? (r.IsRefinement ? 1 : -1);
}
