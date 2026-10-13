using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Settings organization stage 5: the control an admin-channel row is drawn with.</summary>
internal enum AdminSettingKind
{
    /// <summary>A selector over <see cref="AdminSettingSpec.Choices"/>; a pick is sent at once.</summary>
    Choice = 0,

    /// <summary>A switch (<c>true</c> / <c>false</c>); a flip is sent at once.</summary>
    Toggle = 1,

    /// <summary>Whole seconds in a field with <b>Apply</b> (the server clamps and says so).</summary>
    Number = 2,

    /// <summary>One line of text in a field with <b>Apply</b>.</summary>
    Text = 3,

    /// <summary>WIPECONFIRM1: a day, picked with the date-time picker (ISO UTC of 00:00 in the server's zone); Done sends it.</summary>
    Date = 4,
}

/// <summary>A question asked before an admin row's change is sent (<see cref="AdminSettingSpec.Confirm"/>).</summary>
internal sealed class AdminSettingConfirm
{
    internal AdminSettingConfirm(string heading, string body, string yes)
    {
        Heading = heading ?? "";
        Body = body ?? "";
        Yes = yes ?? "";
    }

    internal string Heading { get; }
    internal string Body { get; }
    internal string Yes { get; }

    /// <summary>WIPECONFIRM1: a word the admin types to enable Yes (case-insensitive); empty: a click is enough.</summary>
    internal string Word { get; init; } = "";

    /// <summary>WIPECONFIRM1: drawn as a destructive question (Enter does not answer it).</summary>
    internal bool Danger { get; init; }

    /// <summary>WIPEPOLISH1: label/value lines drawn left-aligned under the heading (the body is then empty or short).</summary>
    internal IReadOnlyList<DialogFact> Facts { get; init; } = Array.Empty<DialogFact>();

    /// <summary>WIPEPOLISH1: the whole question as text (the body, or the facts as lines), for logs and tests.</summary>
    internal string Text { get; init; } = "";
}

/// <summary>
/// Settings organization stage 5 (Core only): a row on Progenitor's page whose value is the joined server's, read
/// from and written through the admin channel (<c>'PrJa'</c>), not a local cfg entry. The server authorizes, rate
/// limits and audits every change; the row only draws the value it last answered with and sends the admin's pick.
/// Game-free: the delegates are <c>Networking.JoinAdminRows</c>' (live) or a test's.
/// </summary>
internal sealed class AdminSettingSpec
{
    /// <summary>The setting id (<see cref="SettingPlaces.JoinAdminId"/>): readback <c>settingId</c>, <c>home</c>, <c>alsoAt</c>.</summary>
    internal string SettingId { get; init; } = "";

    /// <summary>The cell-row and readback key (the operation word).</summary>
    internal string Key { get; init; } = "";

    internal string Label { get; init; } = "";

    internal string Help { get; init; } = "";

    internal AdminSettingKind Kind { get; init; }

    /// <summary>A choice row's stored values, and the words drawn for them (same length).</summary>
    internal IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    internal IReadOnlyList<string> ChoiceLabels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// RADIO1: a pick-one choice row drawn as a radio list (one option per line), not a dropdown: the "this or that"
    /// choices. <see cref="ChoiceDescriptions"/> is each option's explanation (same length; empty for none): a radio list
    /// draws it under the option; a dropdown does not, and OPTIONHOVER1's details pane shows the hovered option's.
    /// </summary>
    internal bool ChoiceRadio { get; init; }

    /// <inheritdoc cref="ChoiceRadio"/>
    internal IReadOnlyList<string> ChoiceDescriptions { get; init; } = Array.Empty<string>();

    /// <summary>A number row's bounds (the field's; the server clamps again) and units.</summary>
    internal int Min { get; init; }

    internal int Max { get; init; }

    internal string Units { get; init; } = "";

    /// <summary>A text row's longest value.</summary>
    internal int MaxLength { get; init; } = 256;

    /// <summary>The server's value now (invariant text), or null while it has not said.</summary>
    internal Func<string?> Read { get; init; } = () => null;

    /// <summary>
    /// Send <c>value</c>; <c>done(ok, message)</c> is called once with the server's answer (its note, or the refusal),
    /// or with <c>false</c> and why when nothing was sent or no answer came. Main thread.
    /// </summary>
    internal Action<string, Action<bool, string>> Send { get; init; } = (_, done) => done(false, "Not connected.");

    /// <summary>The question to ask before <c>next</c> replaces <c>current</c>, or null to send at once.</summary>
    internal Func<string, string, AdminSettingConfirm?>? Confirm { get; init; }

    /// <summary>
    /// The entry of the server's lock list (<c>[ServerSettings] LockedInGame</c>) that keeps this value out of the game, or
    /// null: the row is then read-only with <see cref="Networking.ServerSettingsLock.Note"/>.
    /// </summary>
    internal Func<string?>? LockedBy { get; init; }

    /// <summary>How a value reads to a person (null: <see cref="SettingPlaces.Display"/>, a choice its label).</summary>
    internal Func<string, string>? Display { get; init; }

    internal string DisplayOf(string? value)
    {
        string v = value ?? "";
        if (Display != null)
        {
            try { return Display(v); }
            catch { /* the plain text below */ }
        }

        for (int i = 0; i < Choices.Count && i < ChoiceLabels.Count; i++)
            if (string.Equals(Choices[i], v, StringComparison.OrdinalIgnoreCase)) return ChoiceLabels[i];
        return SettingPlaces.Display(v);
    }

    /// <summary>The lock list entry that locks this row now, or null.</summary>
    internal string? LockedByNow()
    {
        if (LockedBy == null) return null;
        try { return LockedBy(); }
        catch { return null; }
    }

    /// <summary>The read-only note while the row is locked, or null.</summary>
    internal string? LockNote() => LockedByNow() is { } entry ? Networking.ServerSettingsLock.NoteFor(entry) : null;

    internal string? SafeRead()
    {
        try { return Read(); }
        catch { return null; }
    }

    // ---- Settings organization stage 6 (the Server rules) ----------------------------------------------------

    /// <summary>Non-null while the row is read-only for another reason (not an admin, a preset): the row is grayed with it.</summary>
    internal Func<string?>? ReadOnlyReason { get; init; }

    /// <summary>A sentence the help line carries on an editable row (a start-command override), or null.</summary>
    internal Func<string?>? Note { get; init; }

    /// <summary>True while the stored value waits for a restart (the row reads <c>pending</c> and says so).</summary>
    internal Func<bool>? Pending { get; init; }

    /// <summary>Write-only (the password): the field never shows the server's value, and every Apply is sent.</summary>
    internal bool WriteOnly { get; init; }

    /// <summary>One more readback object under <c>serverRule</c>, read when the row is read.</summary>
    internal Func<object?>? ExtraReadback { get; init; }

    /// <summary>
    /// Review fix: true when the page refuses a number outside <see cref="Min"/>..<see cref="MaxNowValue"/> before sending
    /// (the Server rules; the join admin numbers keep the server's clamp-and-say).
    /// </summary>
    internal bool RangeChecked { get; init; }

    /// <summary>Review fix: the upper bound the server enforces now (max players: the state's maxPlayersMax), or null for <see cref="Max"/>.</summary>
    internal Func<int>? MaxNow { get; init; }

    internal int MaxNowValue()
    {
        if (MaxNow == null) return Max;
        try { return MaxNow(); }
        catch { return Max; }
    }

    /// <summary>
    /// Settings organization live 2026-10-06: true when this player may only read the row (not an admin there). The page
    /// then draws it in the read-only style of every other server row, <c>Admin only · value</c> (with the Server chip off the Server tab),
    /// instead of a grayed control. Decided when the page is drawn (an admin change rebuilds the page).
    /// </summary>
    internal Func<bool>? ViewOnly { get; init; }

    internal bool ViewOnlyNow()
    {
        try { return ViewOnly?.Invoke() ?? false; }
        catch { return false; }
    }

    internal string? ReadOnlyNow() => Safe(ReadOnlyReason);

    internal string? NoteNow() => Safe(Note);

    internal bool PendingNow()
    {
        try { return Pending?.Invoke() ?? false; }
        catch { return false; }
    }

    private static string? Safe(Func<string?>? f)
    {
        if (f == null) return null;
        try
        {
            string? s = f();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }
}
