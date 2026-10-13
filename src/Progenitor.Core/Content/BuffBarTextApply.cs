using System;
using System.Collections.Generic;
using ProjectM.UI;
using Stunlock.Localization;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// Client-only: write a cloned buff's <c>Named</c> / <c>Described</c> (or file-based
/// <c>id.name</c> / <c>id.desc</c>) onto every <c>ShowOnBuffBarData.Entries[i]</c>. The HUD
/// tooltip reads <c>Entry.Header</c> as a <c>LocalizationKey</c> (AssetGuid at offset 0)
/// and <c>Entry.Description.Key</c> the same way inventory reads <c>ManagedItemData</c>;
/// our Localization table hook already serves registered keys. <c>UseParentNameAndDescription</c>
/// is cleared only when both Header and description keys are written so a
/// <c>Named</c>-only buff keeps the source tooltip. Re-applied after a Core reload
/// from the same seam as <see cref="BuffIconApply"/>. Localization keys are owned
/// by the definition's plugin (re-registered on Register, not undone on soft unapply).
/// </summary>
internal static class BuffBarTextApply
{
    private static readonly object NamedOnlyGate = new();
    private static readonly HashSet<int> LoggedNamedOnly = new();

    /// <summary>
    /// No-op on the server, for items, or when neither builder text nor a registered key
    /// exists. Failures log one line and leave the clone applied (the source tooltip stays).
    /// <paramref name="logNamedOnly"/> is true for the clone apply (once per definition)
    /// and false for a live-instance HUD nudge so N holders do not emit N Info lines.
    /// </summary>
    public static void TryApply(EntityManager em, Entity clone, PrefabDefinition def, bool logNamedOnly = true)
    {
        try
        {
            Apply(em, clone, def, logNamedOnly);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                BuffBarText.FormatFailed(BuffBarText.UnreadableReason, def.Id) + " threw: " + e.Message);
        }
    }

    /// <summary>Drop the once-per-definition Named-only log set (Core Uninstall).</summary>
    public static void Uninstall()
    {
        lock (NamedOnlyGate) LoggedNamedOnly.Clear();
    }

    private static void Apply(EntityManager em, Entity clone, PrefabDefinition def, bool logNamedOnly)
    {
        string nameKey = BuffBarText.NameKey(def.Id);
        string descKey = BuffBarText.DescriptionKey(def.Id);
        bool nameRegistered = Progenitor.Localization.Localization.IsRegistered(nameKey);
        bool descRegistered = Progenitor.Localization.Localization.IsRegistered(descKey);
        if (!BuffBarText.ShouldApply(
                Plugin.IsServerProcess,
                def.Kind,
                def.LocalizedName,
                def.LocalizedDescription,
                nameRegistered,
                descRegistered))
            return;

        BuffBarText.ResolveKeys(
            def.Id,
            def.LocalizedName,
            def.LocalizedDescription,
            nameRegistered,
            descRegistered,
            out var headerKey,
            out var descriptionKey,
            out var unregisteredKey);

        if (!string.IsNullOrEmpty(unregisteredKey))
        {
            Plugin.Logger?.LogWarning(
                BuffBarText.FormatFailed(BuffBarText.FormatUnregisteredReason(unregisteredKey), def.Id));
            if (string.IsNullOrEmpty(headerKey) && string.IsNullOrEmpty(descriptionKey))
                return;
        }

        if (!BuffBarDataAccess.TryGet(em, clone, out var data, out var missReason) || data == null)
        {
            Plugin.Logger?.LogWarning(BuffBarText.FormatFailed(missReason, def.Id));
            return;
        }

        var entries = data.Entries;
        int written = 0;
        try
        {
            int count = entries.Count;
            LocalizationKey header = default;
            if (!string.IsNullOrEmpty(headerKey))
                header = Progenitor.Localization.Localization.KeyFor(headerKey);
            LocalizationKey description = default;
            if (!string.IsNullOrEmpty(descriptionKey))
                description = Progenitor.Localization.Localization.KeyFor(descriptionKey);

            bool wroteHeader = !string.IsNullOrEmpty(headerKey);
            bool wroteDescription = !string.IsNullOrEmpty(descriptionKey);
            bool clearParent = PrefabApplyBookkeeping.ShouldClearUseParent(wroteHeader, wroteDescription);
            if (BuffBarText.ShouldLogNamedOnly(wroteHeader, wroteDescription, logNamedOnly)
                && RememberNamedOnly(def))
                Plugin.Logger?.LogInfo(BuffBarText.FormatNamedWithoutDescribed(def.Id));

            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                if (wroteHeader)
                    entry.Header = header;
                if (wroteDescription)
                {
                    LocalizedStringBuilderBase builder = entry.Description;
                    builder.Key = description;
                    builder.ParameterIndex = 0;
                    builder.ParameterCount = 0;
                    entry.Description = builder;
                }
                if (clearParent)
                    entry.UseParentNameAndDescription = false;
                written++;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                BuffBarText.FormatFailed(BuffBarText.UnreadableReason, def.Id) + " threw: " + e.Message);
            return;
        }

        if (written == 0)
        {
            Plugin.Logger?.LogWarning(BuffBarText.FormatFailed(BuffBarText.NoEntriesReason, def.Id));
            return;
        }

        Plugin.Logger?.LogInfo(BuffBarText.FormatApplied(written, def.Id));
    }

    private static bool RememberNamedOnly(PrefabDefinition def)
    {
        int hash = def.Guid.GuidHash;
        lock (NamedOnlyGate)
            return LoggedNamedOnly.Add(hash);
    }
}
