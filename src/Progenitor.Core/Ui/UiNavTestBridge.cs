using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// NAV1: the dev bridge's <c>nav</c> op reaches the navigation through this class (by reflection, so it stays out of
/// the published API): the focus readback and one step by button name.
/// </summary>
internal static class UiNavTestBridge
{
    /// <summary>The focus readback (<see cref="UiNav.Describe"/>).</summary>
    public static Dictionary<string, object?> State() => UiNav.Describe();

    /// <summary>One step for <paramref name="button"/> (<c>up</c>, <c>down</c>, <c>left</c>, <c>right</c>, <c>a</c>, <c>b</c>, <c>x</c>, <c>lt</c>, <c>rt</c>).</summary>
    public static Dictionary<string, object?> Press(string button)
    {
        var command = Command(button);
        if (command == NavCommand.None) throw new ArgumentException("unknown nav button '" + button + "'");
        bool taken = UiNav.Execute(command);
        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["taken"] = taken, ["nav"] = UiNav.Describe() };
    }

    internal static NavCommand Command(string? button) => (button ?? "").Trim().ToLowerInvariant() switch
    {
        "up" => NavCommand.Up,
        "down" => NavCommand.Down,
        "left" => NavCommand.Left,
        "right" => NavCommand.Right,
        "a" => NavCommand.Submit,
        "b" => NavCommand.Cancel,
        "x" => NavCommand.Reset,
        "lt" => NavCommand.TabPrev,
        "rt" => NavCommand.TabNext,
        _ => NavCommand.None,
    };
}
