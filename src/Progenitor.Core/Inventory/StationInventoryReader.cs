using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// Reads which <c>InventoryInstanceElement</c> rows of a station are its input and its output: the
/// entities the station component names (<c>Refinementstation</c> or <c>Salvagestation</c>
/// <c>InputInventoryEntity</c> / <c>OutputInventoryEntity</c>) matched against the rows'
/// <c>ExternalInventoryEntity</c>. Server world, main thread. Mapping rules:
/// <see cref="StationInventoryMap.Resolve"/>.
/// </summary>
internal static unsafe class StationInventoryReader
{
    /// <summary>The input and output instances of <paramref name="owner"/>; instance 0 and no output for any other model.</summary>
    internal static StationInventoryInstances Read(EntityManager em, Entity owner)
    {
        try
        {
            var kind = StationInventoryKind.None;
            long input = 0, output = 0;
            if (owner.Has<Refinementstation>(em))
            {
                kind = StationInventoryKind.Refinement;
                byte* p = AiRuntime.RO<Refinementstation>(em, owner, Layout.RefinementstationOutputInventoryEntityOffset + 8);
                if (p != null)
                {
                    input = EntityKey(p + Layout.RefinementstationInputInventoryEntityOffset);
                    output = EntityKey(p + Layout.RefinementstationOutputInventoryEntityOffset);
                }
            }
            else if (owner.Has<Salvagestation>(em))
            {
                kind = StationInventoryKind.Salvage;
                byte* p = AiRuntime.RO<Salvagestation>(em, owner, Layout.SalvagestationOutputInventoryEntityOffset + 8);
                if (p != null)
                {
                    input = EntityKey(p + Layout.SalvagestationInputInventoryEntityOffset);
                    output = EntityKey(p + Layout.SalvagestationOutputInventoryEntityOffset);
                }
            }

            if (kind == StationInventoryKind.None) return StationInventoryInstances.Single;
            return StationInventoryMap.Resolve(kind, input, output, Rows(em, owner));
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("Inventory: station inventories of " + owner.Index + ":" + owner.Version + " threw: " + ex.Message);
            return owner.Has<Refinementstation>(em) ? new StationInventoryInstances(0, 1) : StationInventoryInstances.Single;
        }
    }

    /// <summary><c>NetworkedEntity._Entity</c> at <paramref name="field"/>, packed.</summary>
    private static long EntityKey(byte* field)
    {
        byte* e = field + Layout.NetworkedEntityEntityOffset;
        return StationInventoryMap.Key(*(int*)e, *(int*)(e + 4));
    }

    /// <summary>Each <c>InventoryInstanceElement</c> row's external inventory entity, packed (stride and offset as <c>Items</c> reads them).</summary>
    private static IReadOnlyList<long> Rows(EntityManager em, Entity owner)
    {
        if (!owner.Has<InventoryInstanceElement>(em)) return Array.Empty<long>();
        var typeIndex = ComponentIndex.IndexOf<InventoryInstanceElement>();
        int length = em.GetBufferLength(owner, typeIndex);
        if (length <= 0 || length > Progenitor.Items.Items.MaxInventoryInstances) return Array.Empty<long>();
        byte* raw = (byte*)em.GetBufferRawRO(owner, typeIndex);
        if (raw == null) return Array.Empty<long>();
        var rows = new long[length];
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + (i * Progenitor.Items.Items.InstanceStride) + Progenitor.Items.Items.InstanceEntityOffset;
            rows[i] = StationInventoryMap.Key(*(int*)row, *(int*)(row + 4));
        }
        return rows;
    }
}
