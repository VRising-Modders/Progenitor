using System.Collections.Generic;

namespace Progenitor.Inventory;

/// <summary>
/// Which station component names a station's input and output inventories. A station of one of
/// these kinds keeps two external inventory entities in its component, and its
/// <c>InventoryInstanceElement</c> rows point at the same entities.
/// </summary>
internal enum StationInventoryKind
{
    /// <summary>No separate input and output: a chest, a crafting station, a heart, a unit station.</summary>
    None,

    /// <summary><c>Refinementstation.InputInventoryEntity</c> / <c>OutputInventoryEntity</c>: sawmill, furnace, loom, blood press, ...</summary>
    Refinement,

    /// <summary><c>Salvagestation.InputInventoryEntity</c> / <c>OutputInventoryEntity</c>: the Devourer.</summary>
    Salvage,
}

/// <summary>The instance indexes of a station's input and output inventories.</summary>
internal readonly struct StationInventoryInstances
{
    internal StationInventoryInstances(int input, int output)
    {
        Input = input;
        Output = output;
    }

    /// <summary>The input (or only) inventory's instance index.</summary>
    internal int Input { get; }

    /// <summary>The output inventory's instance index; -1 when there is no separate output.</summary>
    internal int Output { get; }

    /// <summary>A model with one inventory: instance 0, no output.</summary>
    internal static StationInventoryInstances Single => new(0, -1);
}

/// <summary>
/// Maps a station component's input and output inventory entities onto the owner's
/// <c>InventoryInstanceElement</c> rows. Game-free: entities are packed with <see cref="Key"/>.
/// </summary>
internal static class StationInventoryMap
{
    /// <summary>Packs an entity index and version; 0 is <c>Entity.Null</c>.</summary>
    internal static long Key(int index, int version) => ((long)(uint)version << 32) | (uint)index;

    /// <summary>
    /// The row whose entity is <paramref name="entity"/>; -1 when the entity is null, absent, or
    /// named by more than one row.
    /// </summary>
    internal static int InstanceOf(long entity, IReadOnlyList<long>? rows)
    {
        if (entity == 0 || rows == null) return -1;
        int found = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] != entity) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }

    /// <summary>
    /// The input and output instances for a station of <paramref name="kind"/> whose component
    /// names <paramref name="input"/> and <paramref name="output"/>. The output is the row that
    /// holds the component's output entity. A refinement station whose output entity matches no
    /// row keeps instance 1, the row the game's spawn appends second.
    /// </summary>
    internal static StationInventoryInstances Resolve(StationInventoryKind kind, long input, long output, IReadOnlyList<long>? rows)
    {
        if (kind == StationInventoryKind.None) return StationInventoryInstances.Single;
        int i = InstanceOf(input, rows);
        int o = InstanceOf(output, rows);
        if (o >= 0 && o == i) o = -1;
        if (i < 0) i = o == 0 ? 1 : 0;
        if (o < 0 && kind == StationInventoryKind.Refinement) o = i == 1 ? 0 : 1;
        return new StationInventoryInstances(i, o);
    }
}
