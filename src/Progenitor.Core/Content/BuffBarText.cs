using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// Game-free checks for a cloned buff's HUD name and description. <c>DefineBuff.Named</c> /
/// <c>Described</c> store display text the same way items do; the client applicator registers
/// that text under <c>id.name</c> / <c>id.desc</c> and writes those keys onto
/// <c>ShowOnBuffBarData.Entries[i]</c>. Tests compile this file and pin the log lines, the
/// key shape, and a fake-entry write without game assemblies.
/// </summary>
internal static class BuffBarText
{
    /// <summary>Suffix Localization uses for a cloned buff's HUD name key (<c>id</c> + this).</summary>
    public const string NameKeySuffix = ".name";

    /// <summary>
    /// Logged when <c>GetComponentObject</c> returned null or threw. Distinct from a missing
    /// component so the live log names the interop read, not the source prefab.
    /// </summary>
    public const string UnreadableReason =
        "ShowOnBuffBarData present but unreadable (GetComponentObject returned null or threw)";

    /// <summary>Logged when the managed component exists but <c>Entries</c> is null or empty.</summary>
    public const string NoEntriesReason = "ShowOnBuffBarData has no entries";

    /// <summary>
    /// Logged when the applicator would write a key that Localization has not
    /// <c>Register</c>ed (no <c>Named</c>/<c>Described</c> text and no plugin JSON).
    /// </summary>
    public const string UnregisteredKeyReason = "localization key unregistered";

    /// <summary>Localization key for a cloned buff's HUD title: <c>id.name</c>.</summary>
    public static string NameKey(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id is required.", nameof(id));
        return id + NameKeySuffix;
    }

    /// <summary>Localization key for a cloned buff's HUD tooltip body: <c>id.desc</c>.</summary>
    public static string DescriptionKey(string id) => PrefabWire.DescriptionKey(id);

    /// <summary>
    /// Client-only, buff-only, and only when there is a name or description to write
    /// (builder text, or a key already registered from JSON).
    /// </summary>
    public static bool ShouldApply(
        bool isServerProcess,
        PrefabKind kind,
        string? localizedName,
        string? localizedDescription,
        bool nameRegistered,
        bool descriptionRegistered) =>
        !isServerProcess
        && kind == PrefabKind.Buff
        && (!string.IsNullOrEmpty(localizedName)
            || !string.IsNullOrEmpty(localizedDescription)
            || nameRegistered
            || descriptionRegistered);

    /// <summary>
    /// Which keys to write given builder text and Localization. An unregistered key that
    /// has no builder text is reported in <paramref name="unregisteredKey"/> so the live
    /// path can log <see cref="UnregisteredKeyReason"/> without writing a blank Header.
    /// </summary>
    public static void ResolveKeys(
        string id,
        string? localizedName,
        string? localizedDescription,
        bool nameRegistered,
        bool descriptionRegistered,
        out string? headerKey,
        out string? descriptionKey,
        out string? unregisteredKey)
    {
        headerKey = null;
        descriptionKey = null;
        unregisteredKey = null;

        string nameKey = NameKey(id);
        string descKey = DescriptionKey(id);
        bool wantName = !string.IsNullOrEmpty(localizedName) || nameRegistered;
        bool wantDesc = !string.IsNullOrEmpty(localizedDescription) || descriptionRegistered;

        // Builder text is registered before this runs. A LocalizedName with no table
        // row means Register failed — log unregistered rather than writing a blank Header.
        if (wantName)
        {
            if (nameRegistered)
                headerKey = nameKey;
            else
                unregisteredKey = nameKey;
        }

        if (wantDesc)
        {
            if (descriptionRegistered)
                descriptionKey = descKey;
            else
                unregisteredKey ??= descKey;
        }
    }

    /// <summary>Live success line grepped from the client log.</summary>
    public static string FormatApplied(int entries, string id) =>
        "PrefabRegistry: name/description -> ShowOnBuffBarData entries=" + entries + " on '" + id + "'";

    /// <summary>Live failure line: unreadable, no entries, or an unregistered key.</summary>
    public static string FormatFailed(string reason, string id) =>
        "PrefabRegistry: name/description " + reason + " on '" + id + "'";

    /// <summary>
    /// Distinct unregistered-key reason, including the key so a missing JSON entry is
    /// greppable. <paramref name="key"/> empty keeps the short reason.
    /// </summary>
    public static string FormatUnregisteredReason(string? key) =>
        string.IsNullOrEmpty(key)
            ? UnregisteredKeyReason
            : UnregisteredKeyReason + " ('" + key + "')";

    /// <summary>
    /// Game-free stand-in for <c>ShowOnBuffBarData.Entry</c>. The live applicator writes
    /// <c>LocalizationKey Header</c>, <c>LocalizedStringBuilderBase.Key</c>, and
    /// <c>UseParentNameAndDescription = false</c> only when both keys are written;
    /// tests pin the same fields here.
    /// </summary>
    internal sealed class FakeEntry
    {
        public string HeaderKey = "";
        public string DescriptionKey = "";
        public int ParameterIndex;
        public int ParameterCount;
        public bool UseParentNameAndDescription;
    }

    /// <summary>
    /// Logged when <c>Named</c> ran without <c>Described</c> (and no <c>id.desc</c>).
    /// Header is written; the cloned parent description and
    /// <c>UseParentNameAndDescription</c> stay so the source tooltip does not go blank.
    /// </summary>
    public static string FormatNamedWithoutDescribed(string id) =>
        "PrefabRegistry: Named without Described on '" + (id ?? "")
        + "' (parent description kept; call Described or add id.desc)";

    /// <summary>
    /// True when the Named-only Info line should be emitted. Clone apply logs once;
    /// a live-instance HUD nudge never logs (<paramref name="logNamedOnly"/> false)
    /// so N holders of a Named-only buff do not emit N lines per swap.
    /// </summary>
    public static bool ShouldLogNamedOnly(bool wroteHeader, bool wroteDescription, bool logNamedOnly) =>
        wroteHeader && !wroteDescription && logNamedOnly;

    /// <summary>
    /// Write Header / description / <c>UseParentNameAndDescription</c> onto every fake
    /// entry. Returns how many rows were written, or 0 with a reason when the list is
    /// missing, empty, or both keys were refused as unregistered. Named-only writes
    /// Header and leaves the parent description / flag; both keys clear the flag.
    /// </summary>
    public static int WriteFakeEntries(
        IList<FakeEntry>? entries,
        string? headerKey,
        string? descriptionKey,
        out string reason)
    {
        reason = "";
        if (entries == null)
        {
            reason = UnreadableReason;
            return 0;
        }
        if (entries.Count == 0)
        {
            reason = NoEntriesReason;
            return 0;
        }
        if (string.IsNullOrEmpty(headerKey) && string.IsNullOrEmpty(descriptionKey))
        {
            reason = UnregisteredKeyReason;
            return 0;
        }

        bool wroteHeader = !string.IsNullOrEmpty(headerKey);
        bool wroteDescription = !string.IsNullOrEmpty(descriptionKey);
        bool clearParent = PrefabApplyBookkeeping.ShouldClearUseParent(wroteHeader, wroteDescription);

        int written = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry == null) continue;
            if (wroteHeader)
                entry.HeaderKey = headerKey!;
            if (wroteDescription)
            {
                entry.DescriptionKey = descriptionKey!;
                entry.ParameterIndex = 0;
                entry.ParameterCount = 0;
            }
            if (clearParent)
                entry.UseParentNameAndDescription = false;
            written++;
        }

        if (written == 0)
            reason = NoEntriesReason;
        return written;
    }
}
