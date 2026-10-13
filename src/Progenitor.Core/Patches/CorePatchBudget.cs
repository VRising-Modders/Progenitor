using System.Collections.Generic;

namespace Progenitor.Patches;

/// <summary>
/// One Core-owned registration through the Bootstrap slot pool: which module asks for it, which
/// game method it patches, and which kind of patch it is.
/// </summary>
/// <param name="Module">A <see cref="CorePatchFallbackPolicy"/> module token, or <c>"world"</c> /
/// <c>"godmode"</c> for the two sites that are not fallbacks at all.</param>
/// <param name="Site">The patched method, in the words the log line uses.</param>
/// <param name="Kind">Prefix, postfix or finalizer.</param>
public readonly record struct CorePatchSite(string Module, string Site, PatchKind Kind);

/// <summary>
/// Job FHARM8: <b>every</b> patch Core registers under the <c>&lt;guid&gt;.core</c> owner id,
/// enumerated, so that <c>Progenitor.Bootstrap.PatchSlots.CoreReserve</c> is a number backed by a
/// list rather than by a comment.
///
/// <para><b>Why this type exists.</b> The reservation (FHARM7, R41 finding 2) is what stops a
/// plugin that patched everything at load from silently disarming a Core seam that arms lazily —
/// god mode on the first <c>.godmode</c>, the world-load epoch postfix on the first world load,
/// the module fallbacks only against a Bootstrap that does not own their sites. Its size was
/// argued for in prose ("seven plus one spare"), which meant that adding a Core patch — exactly
/// what FHARM8 did, four times over — could overrun it with nothing failing until a live server
/// refused a guard hours in. <c>PatchSlotBudgetTests</c> now asserts
/// <c>CoreReserve &gt;= </c><see cref="Count"/>, so a new Core patch that is not paid for is a
/// red build.</para>
///
/// <para><b>What is not here.</b> Sites that take the <see cref="CoreFallbackRoute.Direct"/> or
/// <see cref="CoreFallbackRoute.Refused"/> route only — the hosted launcher's <c>Start</c> trio
/// and <c>HarmonyFallback</c>'s <c>Update</c> pair, which carry <c>__state</c> and cannot be
/// expressed as a slot at all — cost the pool nothing and are listed in
/// <c>docs/modules.md</c>'s per-site table instead. A site that is slot-routed <em>when it is
/// reached</em> belongs here even if it is rarely reached: the reserve is a worst case.</para>
///
/// <para>Game-free by construction: nothing here names a <c>ProjectM</c> type, so the test
/// project links it.</para>
/// </summary>
public static class CorePatchBudget
{
    /// <summary>
    /// The list. Keep it in the order the sites arm: eagerly first, then lazily.
    /// </summary>
    public static readonly IReadOnlyList<CorePatchSite> Sites = new[]
    {
        // Always, on a server that reaches a world load (FHARM6).
        new CorePatchSite("world", "SaveFileWriteSystem/LoadPersistenceSystemV2 epoch", PatchKind.Postfix),

        // The god-mode kill group — an atomic group of three (FHARM6).
        new CorePatchSite("godmode", "DeathUtilities.Kill", PatchKind.Prefix),
        new CorePatchSite("godmode", "DeathUtilities.KillEntity (11 args)", PatchKind.Prefix),
        new CorePatchSite("godmode", "DeathUtilities.KillEntity (6 args)", PatchKind.Prefix),

        // The three EventHooks fallbacks, taken when Bootstrap does not own those sites (FHARM7).
        new CorePatchSite(CorePatchFallbackPolicy.ModuleEvents, "ChatMessageSystem.OnUpdate", PatchKind.Prefix),
        new CorePatchSite(CorePatchFallbackPolicy.ModuleEvents, "ServerBootstrapSystem.OnUserConnected", PatchKind.Postfix),
        new CorePatchSite(CorePatchFallbackPolicy.ModuleEvents, "ServerBootstrapSystem.OnUserDisconnected", PatchKind.Prefix),

        // FHARM8: the three fallbacks FHARM7 left unrouted. PrefabRegistry's pair is one site
        // with two patches and costs two slots.
        new CorePatchSite(CorePatchFallbackPolicy.ModuleContent, "LoadPersistenceSystemV2.DeserializeLoadFile", PatchKind.Prefix),
        new CorePatchSite(CorePatchFallbackPolicy.ModuleContent, "LoadPersistenceSystemV2.DeserializeLoadFile", PatchKind.Postfix),
        new CorePatchSite(CorePatchFallbackPolicy.ModuleLocalization, "GameLocalization.SetLanguage", PatchKind.Postfix),
        new CorePatchSite(CorePatchFallbackPolicy.ModulePersistence, "ServerWorldManager.BeginCreateServerWorld", PatchKind.Postfix),

        // NODESCCHAT: on a server, the join-chat guard's prefix (ServerListDescriptionSettings, always armed). Its
        // restore rides the OnUserConnected postfix Bootstrap (or the events fallback above) already owns.
        new CorePatchSite("serverlist", "ServerBootstrapSystem.OnUserConnected", PatchKind.Prefix),

        // ModSave's TriggerSave postfix is deliberately absent: it needs __result, so with
        // Bootstrap present it is Refused, not slotted. See docs/modules.md.
    };

    /// <summary>How many pool slots Core can want at once. The reserve must be at least this.</summary>
    public static int Count => Sites.Count;

    /// <summary>
    /// How many of the listed sites belong to <paramref name="module"/>. For a log line and for
    /// the tests that pin each module's share.
    /// </summary>
    public static int CountFor(string module)
    {
        int n = 0;
        foreach (var site in Sites)
        {
            if (string.Equals(site.Module, module, System.StringComparison.Ordinal)) n++;
        }
        return n;
    }

    /// <summary><c>core-patches: 12 (world 1, godmode 3, events 3, content 2, ...)</c>.</summary>
    public static string Describe()
    {
        var counts = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var site in Sites)
        {
            if (!seen.Add(site.Module)) continue;
            counts.Add(site.Module + " " + CountFor(site.Module).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return "core-patches: " + Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " (" + string.Join(", ", counts) + ")";
    }
}
