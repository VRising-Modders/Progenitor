#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.Plugins;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Buffs;

/// <summary>
/// Writes the supported stat modifiers onto a buff clone <b>this plugin registered</b>, through the
/// ordered prefab-edit pass Core already owns (<c>ctx.Content.Edit</c>), which captures a baseline,
/// refuses identity writes and reports conflicts.
///
/// <para><b>Only your own clone.</b> A vanilla GUID, or a clone another plugin registered, is
/// refused. That is what makes "cleanup removes only what this application contributed" true by
/// construction rather than by care: the definition is this plugin's, unregistering it takes the
/// whole clone away, and the per-wearer contribution is registered and reversed by the game's own
/// modification systems, one id per instance.</para>
///
/// <para><b>Adding the component is a structural edit</b> and Core logs it as unrestorable. On a
/// clone this plugin owns that is accurate and harmless — the clone itself is removed on unregister
/// — so this path adds one line saying so rather than leaving the warning to be misread.</para>
/// </summary>
internal static class BuffStats
{
    // The ModificationType constants that used to live here are gone: BuffDrainWrite owns the
    // op -> ModificationType mapping now (job BUFFP7), and it is on the test compile list. Keeping a
    // second private copy of the same three magic numbers here would be two sources of truth with no
    // compiler warning when they drift, since C# does not flag an unused private const.

    /// <summary>
    /// Put <paramref name="stats"/> on the clone registered under <paramref name="buffId"/>.
    /// Returns a tracked handle: disposing it (or unloading the plugin) drops the edit and Core
    /// restores the baseline it captured. Unsupported or unusable modifiers are refused
    /// individually with their reason; the usable ones still apply.
    /// </summary>
    internal static IDisposable Apply(PluginContext ctx, string buffId, BuffStat[]? stats)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (string.IsNullOrWhiteSpace(buffId))
            throw new ArgumentException("A DefineBuff id is required.", nameof(buffId));

        var usable = new List<BuffStat>();
        if (stats != null)
        {
            for (int i = 0; i < stats.Length; i++)
            {
                var stat = stats[i];
                if (stat == null) continue;
                if (!stat.IsUsable(out string reason))
                {
                    Plugin.Logger?.LogWarning(
                        "buff stat refused id='" + buffId + "' stat=" + stat.Kind + ": " + reason);
                    continue;
                }

                usable.Add(stat);
            }
        }

        if (!TryOwnedBuff(ctx, buffId, out var guid, out string why))
        {
            Plugin.Logger?.LogWarning(BuffStatSupport.StatsRefusedLine(buffId, why));
            return NopDisposable.Instance;
        }

        if (usable.Count == 0)
        {
            Plugin.Logger?.LogInfo(BuffStatSupport.NothingToWriteLine(buffId));
            return NopDisposable.Instance;
        }

        var list = usable.ToArray();
        return ctx.Content.Edit(guid, handle => Write(handle, buffId, list));
    }

    /// <summary>
    /// Find the definition and hand the three facts to
    /// <see cref="BuffStatSupport.MayCarryStats"/>, which owns the rule and the wording. This half
    /// only reads the registry: the decision is game-free and tested there.
    /// </summary>
    private static bool TryOwnedBuff(PluginContext ctx, string buffId, out PrefabGUID guid, out string reason)
    {
        guid = default;
        bool found = false;
        bool isBuff = false;
        bool owned = false;
        string kindName = "";

        var definitions = PrefabRegistry.Definitions;
        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (!string.Equals(definition.Id, buffId, StringComparison.Ordinal)) continue;
            found = true;
            kindName = definition.Kind.ToString();
            isBuff = definition.Kind == PrefabKind.Buff;
            owned = ReferenceEquals(definition.Owner, ctx.Owner);
            guid = definition.Guid;
            break;
        }

        if (!BuffStatSupport.MayCarryStats(buffId, found, isBuff, owned, kindName, out reason))
        {
            guid = default;
            return false;
        }

        return true;
    }

    private static void Write(PrefabEditHandle handle, string buffId, BuffStat[] stats)
    {
        for (int i = 0; i < stats.Length; i++)
        {
            var stat = stats[i];
            try
            {
                if (stat.IsSuppression)
                {
                    SuppressStat(handle, buffId, stat);
                    continue;
                }

                switch (stat.Kind)
                {
                    case BuffStatKind.BloodDrain:
                        WriteBloodDrain(handle, buffId, stat);
                        break;
                    default:
                        // Unreachable: Apply filters on IsUsable. Kept so a future kind added to
                        // the enum without a writer refuses out loud instead of doing nothing.
                        Plugin.Logger?.LogWarning(BuffStatSupport.RefusedLine(buffId, stat.Kind));
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError(
                    "buff stat id='" + buffId + "' " + stat + " threw: " + e);
            }
        }
    }

    /// <summary>
    /// <c>ProjectM.ModifyBloodDrainBuff</c>. The three
    /// <c>ModificationId</c> fields are left exactly as read — on a prefab they are zero and the
    /// buff's spawn system fills them on the <em>instance</em>, which is how the destroy system
    /// knows which contribution to reverse. Writing them here would hand the game an id it never
    /// issued.
    /// </summary>
    private static unsafe void WriteBloodDrain(PrefabEditHandle handle, string buffId, BuffStat stat)
    {
        bool added = false;
        if (!handle.Has<ModifyBloodDrainBuff>())
        {
            // Structural, so Core warns that it is not restorable. On a clone this plugin owns it
            // is: unregistering the definition removes the clone entirely.
            handle.EntityManager.AddComponent(
                handle.Entity, ComponentIndex.Of<ModifyBloodDrainBuff>(ComponentType.AccessMode.ReadWrite));
            added = true;
            Plugin.Logger?.LogInfo(
                "buff stat id='" + buffId + "' added " + BuffStatSupport.ComponentName(BuffStatKind.BloodDrain)
                + " to this plugin's own clone; the 'unrestorable structural edit' warning above is "
                + "expected here - unregistering the definition removes the clone itself");
        }

        // The guarded typed seam (handle.Write / EntityManager.SetComponentData) CANNOT write this
        // component, and finding that out needed a live server (job A1 live acceptance,
        // 2026-09-13). It throws:
        //
        //   SetComponentData can not be called with a zero sized component
        //   (ProjectM.ModifyBloodDrainBuff) and must have same size as sizeof(T).
        //
        // This is the TypeManager zero/garbage-size trap CLAUDE.md warns about: the IL2CPP type
        // reports size 0 to the managed write path even though the component is really 36 bytes
        // (the Dev bridge's own read path reports size 36 and every field correctly). The offline
        // unit tests drive a fake IBuffRuntime, so nothing above this seam could catch it, and the
        // throw was swallowed by Write()'s per-stat catch — the component was added, no value was
        // ever written, and the buff applied, showed its icon and reported Active while doing
        // absolutely nothing. Both worked examples shipped that way.
        //
        // So this uses the repo's own proven mechanism for this exact component:
        // GetComponentDataRawRW plus explicit field offsets, which is what
        // SampleBuffs.WriteDrainModifier has been doing on `.nodrain` all along. The OFFSETS are
        // confirmed twice over — against that Sample and against the live `get` readback, which
        // lists the fields in this order with total size 36.
        //
        // Precision, because the safety argument leans on that sentence: it is the offsets and the
        // access mechanism that are borrowed and proven, NOT the value set. `.nodrain` also writes
        // ptr[34] = 1 (IgnoreIdleDrainWhileActive) and explicitly zeroes the three ModificationId
        // fields; BuffDrainWrite deliberately writes none of those four. So this is the same recipe
        // for HOW to write, and a deliberately narrower set of WHAT to write.
        //
        // WHICH fields get written is decided in BuffDrainWrite, which is game-free and therefore
        // tested (job BUFFP7). This method does not choose any more: it applies the set and nothing
        // else. That split is the direct remedy for the BUFFP5 defect — a two-field write shipped
        // and doubled every non-Set modifier precisely because the decision lived down here behind
        // #if !PROGENITOR_TESTS where no test could see it.
        //
        // The documented policy is enforced by that set, not by care here: the three ModificationId
        // fields (8, 12, 16) and IgnoreIdleDrainWhileActive (34) are in BuffDrainWrite.NeverWritten
        // and a test asserts no scope/op combination ever emits them. The spawn system fills the ids
        // on the instance so the destroy system can reverse this contribution; writing them here
        // would hand the game an id it never issued.
        var typeIndex = ComponentIndex.IndexOf<ModifyBloodDrainBuff>();
        byte* ptr = (byte*)handle.EntityManager.GetComponentDataRawRW(handle.Entity, typeIndex);
        if (ptr == null)
        {
            Plugin.Logger?.LogWarning(
                "buff stat id='" + buffId + "' could not write "
                + BuffStatSupport.ComponentName(BuffStatKind.BloodDrain)
                + ": GetComponentDataRawRW returned null (add=" + added + ")");
            return;
        }

        var writes = BuffDrainWrite.For(stat);
        for (int i = 0; i < writes.Count; i++)
        {
            var w = writes[i];
            if (w.Width == 1) ptr[w.Offset] = w.ByteValue;
            else if (w.IsFloat) *(float*)(ptr + w.Offset) = w.FloatValue;
            else *(int*)(ptr + w.Offset) = w.IntValue;
        }

        Plugin.Logger?.LogInfo(
            "buff stat id='" + buffId + "' " + BuffStatSupport.ComponentName(BuffStatKind.BloodDrain)
            + " " + stat + " ("
            + (stat.Scope == BloodDrainScope.ActiveAndIdle
                ? "active and idle drain both affected - TWO modifications against the same value"
                : "active drain only - one modification")
            + "; ModificationIds left to the game)");
    }

    /// <summary>
    /// BUFFP8: remove what the cloned base carries for this kind, so the buff does not grant an
    /// effect the author never asked for.
    ///
    /// <para><b>Prefab-gated</b>, the same way <c>SampleBuffs.StripSourceVisuals</c> is
    /// (<c>SampleBuffs.cs:3598</c>): on a live instance these components' <c>ModificationId</c>s
    /// belong to the game and removing them would strand a registered modification. Ownership is the
    /// same <see cref="TryOwnedBuff"/> check every other stat uses, so this can only ever touch a
    /// clone this plugin registered.</para>
    /// </summary>
    private static void SuppressStat(PrefabEditHandle handle, string buffId, BuffStat stat)
    {
        if (!handle.Entity.Has<Prefab>(handle.EntityManager))
        {
            Plugin.Logger?.LogWarning(
                "buff stat id='" + buffId + "' " + stat + " skipped: the target is a live instance, "
                + "not the prefab. On an instance the modification ids belong to the game and "
                + "removing these components would strand a registered modification.");
            return;
        }

        var names = BuffStatSupport.SuppressionComponents(stat.Kind);
        int removed = 0;
        for (int i = 0; i < names.Count; i++)
        {
            try
            {
                if (RemoveByName(handle, names[i])) removed++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "buff stat id='" + buffId + "' suppress " + names[i] + " threw: " + e.Message);
            }
        }

        // Structural, like the AddComponent above, so Core's "unrestorable structural edit" warning
        // is expected here too and means the same harmless thing on a plugin-owned clone.
        Plugin.Logger?.LogInfo(
            "buff stat id='" + buffId + "' " + stat + ": removed " + removed + " of " + names.Count
            + " component(s) [" + string.Join(", ", names) + "] from this plugin's own clone, so the "
            + "buff does not inherit that effect from its base; the 'unrestorable structural edit' "
            + "warning is expected - unregistering the definition removes the clone itself");
    }

    private static bool RemoveByName(PrefabEditHandle handle, string name) => name switch
    {
        "ModifyMovementSpeedBuff" => RemoveIfPresent<ModifyMovementSpeedBuff>(handle),
        "ModifyMovementSpeedBuffModification" => RemoveIfPresent<ModifyMovementSpeedBuffModification>(handle),
        _ => false,
    };

    private static bool RemoveIfPresent<T>(PrefabEditHandle handle) where T : struct
    {
        var em = handle.EntityManager;
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(handle.Entity, type)) return false;
        em.RemoveComponent(handle.Entity, type);
        return true;
    }
}
#endif
