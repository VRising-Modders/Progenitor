using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Sample;

/// <summary>
/// Job FSAMP, feature 4 (second half). A second keybind on a <b>modifier chord</b> —
/// <c>Ctrl+F9</c>, cfg value <c>F9 + Ctrl</c> under <c>[Keybinds]</c> in
/// <c>progenitor.sample.cfg</c> — that toggles a small panel showing the last
/// <see cref="SampleLog.PanelLines"/> <c>Sample …</c> lines this process wrote.
///
/// <b>What it demonstrates, beyond the chord.</b> The panel is an ordinary
/// <c>Progenitor.Ui.Ui.Panel</c>, so it takes gameplay input capture the moment it is shown:
/// movement and aim stop while it is open, camera rotation keeps working, and Escape closes it.
/// That is <c>docs/modules.md</c> "Input capture" with nothing else in the way — the <c>.ui demo</c>
/// panel captures too, but it is opened by a server packet, so a live check of the capture rule
/// through it also exercises the network path. This one is a keypress and a panel.
///
/// The chord's bind is registered <c>allowWhileCaptured: true</c> so <c>Ctrl+F9</c> can close the
/// panel it opened; without that the capture rule would suppress the second press and the panel
/// could only be closed with Escape.
///
/// The whole file is game-free — it builds the panel through <c>Progenitor.Ui</c> and never
/// touches Unity or ProjectM — so the test project compiles all of it, panel included (job FUI29).
/// </summary>
internal static class SampleLogPanel
{
    /// <summary>Log/grep area for this feature: <c>Sample logpanel: …</c>.</summary>
    internal const string Area = "logpanel";

    /// <summary>Keybind id; the cfg key under <c>[Keybinds]</c>.</summary>
    internal const string KeybindId = "ToggleLogPanel";

    /// <summary>Panel title.</summary>
    internal const string Title = "Sample log";

    /// <summary>Button that closes the panel.</summary>
    internal const string CloseButtonText = "Close";

    /// <summary>Shown instead of a line when nothing has been recorded in this process yet.</summary>
    internal const string EmptyRow =
        "(no Sample lines in this process yet - run .epoch, .access or press F9)";

    /// <summary>
    /// The label rows: a header naming how many of how many lines are shown, then one row per
    /// line, oldest first, or <see cref="EmptyRow"/> when the ring is empty.
    /// </summary>
    internal static IReadOnlyList<string> Rows(IReadOnlyList<string>? recent)
    {
        var rows = new List<string>(SampleLog.PanelLines + 1);
        int count = recent?.Count ?? 0;
        rows.Add("Last " + count.ToString(CultureInfo.InvariantCulture)
            + " of " + SampleLog.PanelLines.ToString(CultureInfo.InvariantCulture)
            + " Sample line(s) in this process:");
        if (count == 0)
        {
            rows.Add(EmptyRow);
            return rows;
        }

        for (int i = 0; i < count; i++)
            rows.Add(recent![i]);
        return rows;
    }

    // Job FUI29: the panel half used to sit behind `#if !PROGENITOR_TESTS` with a `LiveCount => 0`
    // stub in its place, and there was never a reason for it — nothing here touches Unity or
    // ProjectM, only Progenitor.Ui, which the test project compiles and fakes. The fence is why
    // this file could carry the same open-state defect as the two networking panels with a green
    // suite: the only member a test could reach answered a constant.
    private static readonly OwnedPanel Owned = new();
    private static object? _owner;
    private static string _pluginId = "progenitor.sample";

    /// <summary>
    /// 1 while the panel is on screen. Derived from the handle (job FUI29): asking whether a
    /// handle exists and is undisposed is still true after Escape hid the panel, so
    /// <see cref="Toggle"/> spent the next chord closing something the player had already
    /// dismissed — the two-press bug R34 fixed for the settings panel and nowhere else.
    /// </summary>
    internal static int LiveCount => Owned.IsOpen ? 1 : 0;

    /// <summary>Remember the owner so <c>Ui.Panel</c> is tagged with this plugin.</summary>
    internal static void Attach(object owner, string pluginId)
    {
        _owner = owner;
        if (!string.IsNullOrWhiteSpace(pluginId)) _pluginId = pluginId;
    }

    /// <summary>The keybind handler: open when closed, close when open.</summary>
    internal static void Toggle()
    {
        try
        {
            if (LiveCount > 0)
            {
                Close();
                return;
            }

            Open();
        }
        catch (Exception e)
        {
            SampleLog.Info(Area, "toggle threw: " + e.Message);
        }
    }

    private static void Open()
    {
        Close();
        var recent = SampleLog.Recent(SampleLog.PanelLines);
        var rows = Rows(recent);

        var panel = UiApi.Panel(Title, _owner, _pluginId);
        if (!panel.Enabled)
        {
            SampleLog.Info(Area, "panel unavailable (server process, or the toolkit is not up)");
            return;
        }

        for (int i = 0; i < rows.Count; i++)
            panel.Label(rows[i]);
        panel.Button(CloseButtonText).OnClick(Close);
        UiApi.Show(panel);
        var stale = Owned.Adopt(panel);
        if (stale != null)
        {
            try { stale.Dispose(); }
            catch (Exception e) { SampleLog.Info(Area, "stale dispose threw: " + e.Message); }
        }

        // Logged after the rows were snapshotted, so this line is never one of the rows it counts.
        SampleLog.Info(Area, "opened rows=" + rows.Count + " (capture is on: movement stops until it closes)");
    }

    /// <summary>Close and dispose the panel if it is up.</summary>
    internal static void Close()
    {
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Hide(); } catch { /* teardown */ }
        try { panel.Dispose(); } catch { /* teardown */ }
        SampleLog.Info(Area, "closed");
    }

    /// <summary>Unload: close the panel and forget the owner. Core sweeps the keybind itself.</summary>
    internal static void Detach()
    {
        Close();
        _owner = null;
    }
}
