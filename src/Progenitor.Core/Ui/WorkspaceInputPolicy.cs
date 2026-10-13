using System;

namespace Progenitor.Ui;

/// <summary>HOSTS1: which part of the workspace one Escape belongs to, highest first.</summary>
internal enum EscapeStage
{
    /// <summary>A key capture is waiting (or just ended on this press): it is canceled and nothing else happens.</summary>
    CancelCapture = 1,

    /// <summary>A framework dialog is open: it is answered with No and closed; the workspace behind it stays.</summary>
    AnswerDialog = 2,

    /// <summary>The focused search field has text: it is cleared; the workspace stays.</summary>
    ClearSearch = 3,

    /// <summary>The shown panels (the workspace) are hidden.</summary>
    Close = 4,

    /// <summary>
    /// SEARCHCLEAR1: the focused search field is empty: it gives up focus; the workspace stays (ordered
    /// with <see cref="ClearSearch"/>, before <see cref="Close"/>; numbered last to keep the values).
    /// </summary>
    LeaveSearch = 5,
}

/// <summary>
/// HOSTS1: the stage the policy chose, and whether the paired Back of the same press is swallowed. The
/// in-game-only effects are separate calls their callers make (<see cref="WorkspaceInputPolicy.HoldsGameClose"/>
/// from the per-frame pump, <see cref="WorkspaceInputPolicy.DismissesGameMenuOnClose"/> from the close).
/// </summary>
internal readonly record struct EscapeOutcome(EscapeStage Stage, bool SwallowPairedBack);

/// <summary>
/// HOSTS1: the one input policy both hosts consult (decided 2026-09-30: one input policy, so a fix
/// applies to both hosts and a test covers both). Escape's order is
/// capture &gt; dialog &gt; search (clear, then leave) &gt; close, whatever the host; the hosts differ only in how they carry
/// the result out:
/// <list type="bullet">
/// <item><b>In game</b> the game's FullscreenMenu also reads the same press (<c>Back</c>, <c>Menu_Close</c> in
/// <c>FullscreenMenuMapper</c>, order 502). While a capture or a dialog owns Escape the game's close is held
/// (<see cref="HoldsGameClose"/>, applied by the 498 guard, <see cref="MenuHotkeyGuard"/>), and a close
/// that hides the workspace also dismisses the menu, as Escape on Spellbook returns to the game.</item>
/// <item><b>At the title and in the standalone window</b> there is no game menu to hold or dismiss (no
/// InputActionSystem or input context at the title), so those two flags carry no action there.</item>
/// </list>
/// One physical Escape arrives as 27 then 17 (Bootstrap <c>UiInputContext</c>); when the 27 went to a
/// capture or a dialog, the 17 of the same press is swallowed. A search clear keeps its own two-dispatch
/// guard (<c>UiEscapeSweep</c>). Game-free.
/// </summary>
internal static class WorkspaceInputPolicy
{
    internal const int EscapeKey = 27;
    internal const int BackAction = 17;

    /// <summary>
    /// The stage for one Escape (or Back) and what it implies. <paramref name="searchFocused"/> and
    /// <paramref name="searchText"/> are the workspace search field's state; pass false/empty where the
    /// caller does not own a search (the panel sweep, which asks the workspace for the search stage).
    /// </summary>
    internal static EscapeOutcome Escape(
        WorkspaceHost host,
        bool captureOwnsEscape,
        bool dialogOpen,
        bool searchFocused,
        string? searchText,
        bool suspended = false,
        int keyCode = EscapeKey)
    {
        // The stage order is the same for every host; only the in-game host carries the extra effects,
        // through HoldsGameClose and DismissesGameMenuOnClose.
        _ = host;
        _ = suspended;
        bool pairedBack = keyCode == EscapeKey;
        if (captureOwnsEscape)
            return new EscapeOutcome(EscapeStage.CancelCapture, pairedBack);
        if (dialogOpen)
            return new EscapeOutcome(EscapeStage.AnswerDialog, pairedBack);
        var search = UiSearchEscape.Decide(searchFocused, searchText);
        if (search == UiSearchEscape.Decision.Clear)
            return new EscapeOutcome(EscapeStage.ClearSearch, false);
        // SEARCHCLEAR1: a focused empty search gives up focus first; the next Escape closes.
        if (search == UiSearchEscape.Decision.Leave)
            return new EscapeOutcome(EscapeStage.LeaveSearch, false);
        return new EscapeOutcome(EscapeStage.Close, false);
    }

    // ---- one press, one stage (review L6) ----------------------------------------------------

    /// <summary>
    /// How long a press taken by a capture or a dialog stays taken when its release is not seen (the
    /// pump that reports releases runs only while a panel is shown). Matches <c>Keybinds.EscapeGraceMs</c>.
    /// </summary>
    internal const long PressTakenGraceMs = 400;

    private static readonly object PressGate = new();
    private static bool _pressTaken;
    private static long _pressTakenAt;

    /// <summary>The clock for the grace; a seam for tests.</summary>
    internal static Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// The press was taken by a capture or a dialog (<see cref="EscapeStage.CancelCapture"/>,
    /// <see cref="EscapeStage.AnswerDialog"/>): no later stage may act on it. Without this the pump's
    /// search clear, which can run after the hook answered the dialog, cleared the search on that same
    /// press, because the dialog it would have deferred to was already gone.
    /// </summary>
    internal static void NotePressTaken(EscapeStage stage)
    {
        if (stage is not (EscapeStage.CancelCapture or EscapeStage.AnswerDialog)) return;
        lock (PressGate)
        {
            _pressTaken = true;
            _pressTakenAt = SafeNow();
        }
    }

    /// <summary>Escape is up: the next press is a new one.</summary>
    internal static void NotePressReleased()
    {
        lock (PressGate) _pressTaken = false;
    }

    /// <summary>True while the current press already went to a capture or a dialog.</summary>
    internal static bool PressTaken
    {
        get
        {
            lock (PressGate)
            {
                if (!_pressTaken) return false;
                long since = SafeNow() - _pressTakenAt;
                if (since >= 0 && since <= PressTakenGraceMs) return true;
                _pressTaken = false;
                return false;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (PressGate) _pressTaken = false;
        NowMs = () => Environment.TickCount64;
    }

    private static long SafeNow()
    {
        try { return NowMs(); }
        catch { return 0; }
    }

    /// <summary>
    /// Whether the search clear may take this press: no capture and no dialog ahead of it, and the press
    /// was not already taken by one of them (review L6).
    /// </summary>
    internal static bool SearchMayClear(bool captureOwnsEscape, bool dialogOpen) =>
        !captureOwnsEscape && !dialogOpen && !PressTaken;

    /// <summary>
    /// A close that hides the in-game workspace also dismisses the FullscreenMenu (Escape on Spellbook
    /// returns to the game). The title and the standalone window have no menu to dismiss; a search clear
    /// never closes.
    /// </summary>
    internal static bool DismissesGameMenuOnClose(WorkspaceHost host, bool searchCleared) =>
        !searchCleared && host == WorkspaceHost.InGame;

    /// <summary>
    /// The per-frame hold on the game's close actions: while a capture owns Escape or a dialog is open,
    /// and while the Escape that started a hold is still down, so neither its press nor its release reaches
    /// the game's menu. Released on the first frame none of those is true. The same answer in every host;
    /// only the in-game host has a game close to hold (the 498 guard exists only in a world).
    /// </summary>
    internal static bool HoldsGameClose(bool captureOwnsEscape, bool dialogOpen, bool escapeHeld, bool wasHeld) =>
        captureOwnsEscape || dialogOpen || (wasHeld && escapeHeld);
}
