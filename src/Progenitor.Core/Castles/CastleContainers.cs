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

    /// <summary>A chest's inventory, a station's input (instance 0).</summary>
    public InventoryRef Main { get; init; }

    /// <summary>A refinement station's output (instance 1); <see cref="InventoryRef.None"/> otherwise.</summary>
    public InventoryRef Output { get; init; }

    /// <summary>True for a crafting or refinement station (<c>CastleWorkstation</c> or <c>Refinementstation</c>).</summary>
    public bool IsStation { get; init; }

    /// <summary>True for a refinement station: separate input and output inventories.</summary>
    public bool IsRefinement { get; init; }

    /// <summary>Slots of <see cref="Main"/> (0 when unreadable).</summary>
    public int SlotCount { get; init; }

    /// <summary>Entity of the placed model (same as <see cref="Building"/>.Entity).</summary>
    public CastleId Entity => Building.Entity;

    /// <inheritdoc />
    public override string ToString() =>
        (Nameplate.Length > 0 ? "\"" + Nameplate + "\" " : "") + Building.Label + " " + Building.Entity
        + " id=" + Id + " slots=" + SlotCount + (IsRefinement ? " (input+output)" : "");
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

            list.Add(new CastleContainerInfo
            {
                Building = r.Building,
                Id = id,
                Nameplate = r.Nameplate ?? "",
                Nameable = r.Nameable,
                Main = new InventoryRef(e.Index, e.Version, 0, epoch),
                Output = r.IsRefinement ? new InventoryRef(e.Index, e.Version, 1, epoch) : InventoryRef.None,
                IsStation = r.IsStation,
                IsRefinement = r.IsRefinement,
                SlotCount = r.SlotCount,
            });
        }

        return list;
    }
}
