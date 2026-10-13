using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Keybind picker state on top of the existing tracked rebind flow. Capture, cancel,
/// clear and reset all go through <see cref="Keybinds"/> — this type does not fork it.
/// Commit is <see cref="UiCommitPolicy.Immediate"/> when a capture lands.
/// </summary>
internal sealed class UiKeybindPickerState
{
    internal const UiCommitPolicy Commit = UiCommitPolicy.Immediate;

    internal const string CapturingLabel = "press a key…";

    public UiKeybindPickerState(string pluginId, string bindId)
    {
        PluginId = pluginId ?? "";
        BindId = bindId ?? "";
        Refresh();
    }

    public string PluginId { get; }

    public string BindId { get; }

    public KeyChord Chord { get; private set; }

    public KeyChord DefaultChord { get; private set; }

    public string ChordText { get; private set; } = "None";

    public bool Capturing { get; private set; }

    /// <summary>
    /// The row's notice: KEYS2's refusal for the key just pressed or the reset just tried (reserved by
    /// Windows or Steam, or used by something else), else the winner rule's sentence, else null.
    /// </summary>
    public string? ConflictNotice => LastRefusal ?? _conflict;

    private string? _conflict;

    /// <summary>KEYS2: the last refusal (a capture keeps waiting after one), cleared by the next bind, cancel or capture.</summary>
    public string? LastRefusal { get; private set; }

    public bool Found { get; private set; }

    /// <summary>Readable chord, or the capturing prompt. Never an unrenderable glyph.</summary>
    public string DisplayText => Capturing ? CapturingLabel : ChordText;

    public void Refresh()
    {
        Found = false;
        DefaultChord = KeyChord.None;
        Chord = KeyChord.None;
        IReadOnlyList<IKeybindInfo> snapshot;
        try { snapshot = Keybinds.Snapshot(); }
        catch
        {
            snapshot = Array.Empty<IKeybindInfo>();
        }

        IKeybindInfo? self = null;
        for (int i = 0; i < snapshot.Count; i++)
        {
            var row = snapshot[i];
            if (!string.Equals(row.PluginId, PluginId, StringComparison.Ordinal)) continue;
            if (!string.Equals(row.Id, BindId, StringComparison.Ordinal)) continue;
            self = row;
            break;
        }

        if (self != null)
        {
            Found = true;
            Chord = self.Chord;
            DefaultChord = self.DefaultChord;
        }

        ChordText = Chord.IsBound ? Chord.DisplayText : "None";
        Capturing = string.Equals(Keybinds.RebindingId, BindId, StringComparison.Ordinal);
        _conflict = FindConflict(snapshot, self);
    }

    public bool BeginCapture()
    {
        LastRefusal = null;
        bool started;
        try { started = Keybinds.BeginRebind(PluginId, BindId); }
        catch { started = false; }
        Refresh();
        return started;
    }

    public void CancelCapture()
    {
        LastRefusal = null;
        try { Keybinds.CancelRebind(); }
        catch { /* */ }
        Refresh();
    }

    public bool Clear()
    {
        bool ok;
        try { ok = Keybinds.Rebind(PluginId, BindId, KeyChord.None); }
        catch { ok = false; }
        Refresh();
        return ok;
    }

    public bool ResetToDefault()
    {
        Refresh();
        bool ok;
        string? why = null;
        // KEYS2: a default that is now reserved or used by something else is refused with the reason.
        try { ok = Keybinds.TryRebind(PluginId, BindId, DefaultChord, Keybinds.RebindCheck.Reset, out why); }
        catch { ok = false; }
        Refresh();
        LastRefusal = ok ? null : why;
        return ok;
    }

    public void NoteFinished(RebindResult result)
    {
        if (!string.Equals(result.Id, BindId, StringComparison.Ordinal)) return;
        LastRefusal = result.Outcome == RebindOutcome.Refused ? result.Reason : null;
        Refresh();
    }

    /// <summary>KEYS1: the winner rule's sentence for this bind (not active / also used by), or null.</summary>
    private string? FindConflict(IReadOnlyList<IKeybindInfo> snapshot, IKeybindInfo? self)
    {
        if (self == null || !self.Chord.IsBound) return null;
        string line = ModProblems.KeyConflictLine(PluginId, BindId);
        return line.Length == 0 ? null : line;
    }
}
