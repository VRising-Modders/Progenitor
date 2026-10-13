using System;

#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Content;
using Stunlock.Core;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Two-process ordered edit of a vanilla prefab (roadmap M2.4 remaining gate).
///
/// Two editors of <c>Item_Ingredient_Crystal</c> <c>ItemData.MaxAmount</c>: Default writes
/// <see cref="DefaultAmount"/>, Late writes <see cref="LateAmount"/>. Last writer wins, so
/// both sides derive <see cref="ExpectedEffective"/>; the server ships that as <c>PrfE</c>
/// and the client re-applies it over its own editors. Dispose / plugin unload restores the
/// captured vanilla stack. Distinct Late plugin id so Core logs the conflict (same-plugin
/// stacking is otherwise silent).
/// </summary>
public static class SampleEditLayers
{
    /// <summary>Vanilla Crystal, the clone source Sample already uses for ember.</summary>
    public const string SpawnableName = "Item_Ingredient_Crystal";

    /// <summary><c>PrefabGUID</c> hash of <see cref="SpawnableName"/>.</summary>
    public const int GuidHash = -257494203;

    /// <summary>Short name of the component both editors write.</summary>
    public const string Component = "ItemData";

    /// <summary>Wire full name; this type is on Core's <c>PrfE</c> allowlist.</summary>
    public const string ComponentFullName = "ProjectM.ItemData";

    /// <summary>Default-priority editor; loser of the intentional conflict.</summary>
    public const int DefaultAmount = 10;

    /// <summary>Late-priority editor; last writer and the value <c>PrfE</c> carries.</summary>
    public const int LateAmount = 40;

    /// <summary>What both processes must read after the ordered pass (and after <c>PrfE</c>).</summary>
    public const int ExpectedEffective = LateAmount;

    /// <summary>BepInPlugin GUID of the Sample; the Default editor registers under this id.</summary>
    public const string SampleId = "progenitor.sample";

    /// <summary>Display name paired with <see cref="SampleId"/> on the conflict Warn.</summary>
    public const string SampleName = "Progenitor Sample";

    /// <summary>
    /// Distinct plugin id for the Late editor. Core skips a conflict Warn when both writers
    /// share a plugin id; this id is owned by the Sample so unload still reverses it.
    /// </summary>
    public const string LateId = "progenitor.sample.layers.late";

    /// <summary>Display name paired with <see cref="LateId"/> on the conflict Warn.</summary>
    public const string LateName = "Sample Layers Late";

    /// <summary>
    /// Operator-facing conflict line both sides log once per Core generation. Pinned by
    /// <c>SampleEditLayersTests</c> against <c>PrefabEditLog.Conflict</c>.
    /// </summary>
    public const string ExpectedConflict =
        "PrefabRegistry: conflict on prefab guid=-257494203 component=ItemData: Progenitor Sample (progenitor.sample) vs Sample Layers Late (progenitor.sample.layers.late); winner=Sample Layers Late (progenitor.sample.layers.late) because later priority (1000 over 0)";

#if !PROGENITOR_TESTS
    /// <summary>Live GUID for <see cref="GuidHash"/>.</summary>
    public static readonly PrefabGUID Guid = new(GuidHash);

    /// <summary>Register both editors. Tracked so unload restores vanilla Crystal.</summary>
    internal static void Register(SampleContext ctx)
    {
        ctx.Content.Edit(Guid, EditDefault, PrefabEditPriority.Default);
        ctx.Track(PrefabRegistry.Edit(
            Guid,
            EditLate,
            ctx.Owner,
            PrefabEditPriority.Late,
            pluginId: LateId,
            pluginName: LateName));
        ctx.Log.LogInfo(
            "Sample: vanilla " + SpawnableName + " (" + GuidHash + ") " + Component
            + ".MaxAmount Default " + DefaultAmount + " then Late " + LateAmount
            + "; effective=" + ExpectedEffective + " on both sides (PrfE server layer).");
    }

    private static void EditDefault(PrefabEditHandle prefab) => WriteAmount(prefab, DefaultAmount);

    private static void EditLate(PrefabEditHandle prefab) => WriteAmount(prefab, LateAmount);

    private static void WriteAmount(PrefabEditHandle prefab, int maxAmount)
    {
        if (!prefab.Has<ItemData>()) return;
        var data = prefab.GetComponent<ItemData>();
        data.MaxAmount = maxAmount;
        prefab.SetComponent(data);
    }
#endif
}
