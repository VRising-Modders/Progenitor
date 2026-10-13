using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// Game-free decisions for delivering typed keys and wheel notches to shown panels.
///
/// The hover gate's <c>Plugin.Frame</c> pump is armed only while a
/// <see cref="UiCaptureMode.WhenHovered"/> panel is shown: a set of
/// <see cref="UiCaptureMode.WhileShown"/> panes has a constant block answer, so
/// that pump stays off. The Mods workspace uses WhileShown, which is why
/// piggy-backing typing on <c>ReadPointer</c> never ran on the live client. This
/// type is the separate arming rule: any shown panel is enough.
///
/// The pump is the wheel mover. It samples <c>Mouse.current.scroll</c>, writes one
/// <see cref="MenuPanelLayout.WheelRowsPerNotch"/>-row step per new notch, and
/// leaves uGUI's <c>OnScroll</c> inert (<c>scrollSensitivity = 0</c>) so a notch
/// cannot land twice. A live line names who moved the list.
///
/// Typed keys use the same sole-writer rule: armed <c>TMP_InputField</c>s set
/// <c>readOnly</c> so native processing cannot also insert, and mapping is
/// <see cref="UiTypedKeys"/> (named digits, modifier suppression). The caret is
/// owned (<see cref="UiFieldCaret"/>) because assigning <c>field.text</c> resets
/// TMP's <c>stringPosition</c> to 0, and drawn by the framework
/// (<see cref="UiOwnedCaret"/>) because a <c>readOnly</c> TMP field does not
/// paint its own bar. Held keys auto-repeat through
/// <see cref="UiKeyRepeat"/> (400 ms delay, then 50 ms). Escape is sampled here
/// as a backup and as the key-up that releases the clear latch; the hide sweep
/// still runs first because <c>UiInputContext</c> is added before
/// <c>FrameTicker</c>.
/// </summary>
internal static class UiShownInput
{
    internal const string LogPrefix = "Ui: shown-input ";

    internal const int HeartbeatMs = 1000;

    internal const string AppliedUgui = "ugui";
    internal const string AppliedPump = "pump";
    internal const string AppliedBoth = "both";
    internal const string AppliedNone = "none";

    internal const float WheelDeltaEpsilon = 0.01f;

    internal static bool ShouldArm(int shownCount) => shownCount > 0;

    /// <summary>
    /// True when <paramref name="deltaY"/> is a new notch rather than a held last
    /// reading. Input System scroll is a per-frame delta on some builds and the last
    /// WHEEL_DELTA on others; applying every non-zero sample of a held 120 would
    /// run the list away. A 0 → non-zero edge is a notch; the same reading held
    /// from the previous sample is not.
    /// </summary>
    internal static bool IsNewNotch(float deltaY, float previousDeltaY, bool previousHadDelta)
    {
        if (deltaY > -WheelDeltaEpsilon && deltaY < WheelDeltaEpsilon) return false;
        if (!previousHadDelta) return true;
        return deltaY - previousDeltaY > 1f || previousDeltaY - deltaY > 1f;
    }

    internal static bool HasWheelDelta(float deltaY) =>
        deltaY < -WheelDeltaEpsilon || deltaY > WheelDeltaEpsilon;

    /// <summary>
    /// Which mover changed the list this sample. <paramref name="posMoved"/> is versus
    /// the previous sample <b>before</b> this frame's pump write (uGUI or a scrollbar
    /// drag). <paramref name="pumpWrote"/> is this frame's shown-input apply. Both true
    /// is the double-apply. <paramref name="notch"/> with neither is a dropped notch.
    /// </summary>
    internal static string AppliedToken(bool notch, bool posMoved, bool pumpWrote)
    {
        if (pumpWrote && posMoved) return AppliedBoth;
        if (pumpWrote) return AppliedPump;
        if (posMoved) return AppliedUgui;
        if (notch) return AppliedNone;
        return AppliedNone;
    }

    /// <summary>
    /// Heartbeat once a second while panes are shown; always log a wheel notch,
    /// a typed key, or a click that armed a field.
    /// </summary>
    internal static bool ShouldLog(long nowMs, long lastLogMs, bool interesting, int rateLimitMs = HeartbeatMs)
    {
        if (interesting) return true;
        if (lastLogMs == long.MinValue) return true;
        return nowMs - lastLogMs >= rateLimitMs;
    }

    internal static string Format(
        int shown,
        bool hoverPump,
        float deltaY,
        bool notch,
        string? hit,
        float pos,
        float? nextPos,
        float content,
        float view,
        string? applied,
        string? path,
        int keys,
        char typed,
        bool focused,
        string? selected,
        bool click,
        bool activated)
    {
        var sb = new StringBuilder(LogPrefix);
        sb.Append("pump=1 shown=").Append(shown);
        sb.Append(" hoverPump=").Append(hoverPump ? "1" : "0");
        sb.Append(" deltaY=").Append(FormatFloat(deltaY));
        sb.Append(" notch=").Append(notch ? "1" : "0");
        sb.Append(" hit='").Append(string.IsNullOrEmpty(hit) ? "none" : hit).Append('\'');
        sb.Append(" pos=").Append(FormatFloat(pos));
        if (nextPos.HasValue)
            sb.Append("->").Append(FormatFloat(nextPos.Value));
        sb.Append(" applied=").Append(string.IsNullOrEmpty(applied) ? AppliedNone : applied);
        sb.Append(" path=").Append(string.IsNullOrEmpty(path) ? MenuPanelLayout.WheelMover : path);
        sb.Append(" content=").Append(((int)content).ToString(CultureInfo.InvariantCulture));
        sb.Append(" view=").Append(((int)view).ToString(CultureInfo.InvariantCulture));
        sb.Append(" keys=").Append(keys);
        if (typed != '\0')
        {
            sb.Append(" char='");
            if (typed == ' ') sb.Append(' ');
            else if (typed == '\b') sb.Append("\\b");
            else sb.Append(typed);
            sb.Append('\'');
        }

        sb.Append(" focused=").Append(focused ? "1" : "0");
        sb.Append(" selected=").Append(string.IsNullOrEmpty(selected) ? "none" : selected);
        sb.Append(" click=").Append(click ? "1" : "0");
        if (click) sb.Append(" activated=").Append(activated ? "1" : "0");
        return sb.ToString();
    }

    private static string FormatFloat(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
