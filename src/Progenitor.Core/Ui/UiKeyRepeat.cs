using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Auto-repeat for the shown-input pump. The pump samples held state once per
/// framework tick and does not receive the OS key-repeat stream, so holding a
/// key would otherwise insert or delete exactly once.
///
/// <para>
/// A tap fires on the press edge only. Holding past
/// <see cref="InitialDelayMs"/> then fires at <see cref="RepeatIntervalMs"/>.
/// Release, focus loss, a panel close, a sidebar rebuild or a Core swap must
/// <see cref="Reset()"/> — a repeat that outlives its key runs the field away.
/// </para>
/// </summary>
internal sealed class UiKeyRepeat
{
    /// <summary>
    /// Milliseconds after the press edge before the first extra fire. 400 ms is
    /// long enough that a quick tap is never a repeat, and short enough that a
    /// held Backspace starts deleting promptly.
    /// </summary>
    internal const int InitialDelayMs = 400;

    /// <summary>
    /// Milliseconds between extra fires after the initial delay. 50 ms is 20 per
    /// second — fast enough to run down a search string, slower than Windows'
    /// default ~30 Hz so a hitch cannot dump a dozen characters in one burst.
    /// </summary>
    internal const int RepeatIntervalMs = 50;

    private readonly Dictionary<UiKey, long> _nextAt = new();

    /// <summary>
    /// Printable insertion, Backspace, Delete and the arrow keys repeat.
    /// Modifiers, shortcuts (Ctrl+A/C/X/V), Home/End and Escape do not.
    /// </summary>
    internal static bool IsRepeatable(UiTypedCommandKind kind) =>
        kind is UiTypedCommandKind.Insert
            or UiTypedCommandKind.Backspace
            or UiTypedCommandKind.Delete
            or UiTypedCommandKind.Left
            or UiTypedCommandKind.Right;

    /// <summary>
    /// Record this tick's held state. Returns true when a <b>repeat</b> should
    /// fire. The press edge itself is the caller's to apply — this only arms
    /// the delay, so a tap is never treated as a repeat.
    /// </summary>
    internal bool Observe(UiKey key, bool held, bool edge, long nowMs)
    {
        if (!held)
        {
            _nextAt.Remove(key);
            return false;
        }

        if (edge)
        {
            _nextAt[key] = nowMs + InitialDelayMs;
            return false;
        }

        if (!_nextAt.TryGetValue(key, out long nextAt))
            return false;
        if (nowMs < nextAt)
            return false;

        _nextAt[key] = nowMs + RepeatIntervalMs;
        return true;
    }

    internal void Reset(UiKey key) => _nextAt.Remove(key);

    /// <summary>
    /// Drop every in-flight repeat. Focus loss, panel close, sidebar rebuild
    /// (new field instance) and Core swap all go through here.
    /// </summary>
    internal void Reset() => _nextAt.Clear();
}
