using System;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// WIDEDIALOG1 (user report 2026-10-06, "seems like this window might be too small for the info"): the Players joining
/// panel is a wide dialog, and its mod rows are two lines whose every cell stays inside the content width.
/// </summary>
public sealed class JoinAdminWideTests
{
    /// <summary>The narrowest content the panel can get: the ordinary 640 px frame (a small screen caps the wide one there).</summary>
    private static readonly float MinContent = DialogLayout.ContentRowWidth(DialogLayout.FrameWidth);

    [Fact]
    public void The_dialog_asks_for_a_wide_frame_capped_to_the_screen()
    {
        Assert.Equal(560f, MinContent);
        Assert.Equal(800f, DialogLayout.ContentRowWidth(DialogLayout.WideFrameWidth));
        Assert.Equal(880f, DialogLayout.FrameWidthFor(DialogLayout.WideFrameWidth, 1920f));
        Assert.Equal(0.9f * 800f, DialogLayout.FrameWidthFor(DialogLayout.WideFrameWidth, 800f), 2);
        Assert.Equal(DialogLayout.FrameWidth, DialogLayout.FrameWidthFor(DialogLayout.WideFrameWidth, 600f));
        Assert.Equal(DialogLayout.FrameWidth, DialogLayout.FrameWidthFor(0f, 1920f));
        // The ordinary dialog is unchanged.
        Assert.Equal(DialogLayout.RowWidth, DialogLayout.ContentRowWidth());

        string panel = Source("Networking", "JoinAdminPanel.cs");
        Assert.Contains("FrameWidth = DialogLayout.WideFrameWidth,", panel);
        string dialog = Source("Ui", "UiDialog.cs");
        Assert.Contains("if (content.FrameWidth > DialogLayout.FrameWidth) Ui.SetDialogWidth(panel, content.FrameWidth);", dialog);
        string runtime = Source("Ui", "UnityUiRuntime.cs");
        Assert.Contains("panelRt.sizeDelta = new Vector2(FrameWidth, panelHeight);", runtime);
        Assert.Contains("(_dialog || (_gameWindow && _dialogFrameWidth > PanelWidth)) ? DialogLayout.ContentRowWidth(FrameWidth) : IsStandardWindow ? StandardRowWidth : RowWidth;", runtime);
    }

    [Theory]
    [InlineData(560f)]
    [InlineData(720f)]
    [InlineData(800f)]
    public void Required_approved_and_recommended_rows_stay_inside_the_content_width(float width)
    {
        string longName = "BasinHoverTipsWithAVeryLongModNameIndeed";
        var rows = new[]
        {
            JoinAdminRowLayout.Head("ja-req-a", longName, "author." + longName + " · Thunderstore · Or newer", "Or newer", removable: true, locked: false),
            JoinAdminRowLayout.Reason("ja-req-a", ""),
            JoinAdminRowLayout.Head("ja-app-a", longName, "any version", null, removable: true, locked: false),
            JoinAdminRowLayout.Head("ja-rec-a", longName, "1.0.0", null, removable: true, locked: false),
            JoinAdminRowLayout.FileReason("ja-rec-a", "From the operator's file"),
        };
        foreach (var row in rows)
        {
            foreach (var p in MenuPanelLayout.CellColumns(width, row).Where(p => p.Visible))
            {
                float left = p.Left + width * .5f, right = p.Right + width * .5f;
                Assert.True(left >= -0.01f, row.Key + "/" + p.Key + " starts at " + left);
                Assert.True(right <= width + 0.01f, row.Key + "/" + p.Key + " ends at " + right + " > " + width);
            }
        }

        // Line 1: the name takes the room left by the rule and Remove; Remove is right-aligned.
        var head = MenuPanelLayout.CellColumns(width, rows[0]);
        var remove = head.Single(p => p.Key == JoinAdminRowLayout.CellRemove);
        Assert.Equal(width, remove.Right + width * .5f, 1);
        Assert.True(head.Single(p => p.Key == JoinAdminRowLayout.CellName).Width >= 120f);
        Assert.True(head.Single(p => p.Key == JoinAdminRowLayout.CellRule).Visible);

        // Line 2: the reason field at the full width, its placeholder whole (JOINDRAFT1: no Save; the reason is staged).
        var reason = MenuPanelLayout.CellColumns(width, rows[1]).Single(p => p.Key == JoinAdminRowLayout.CellReason);
        Assert.True(reason.Visible);
        Assert.Equal(0f, reason.Left + width * .5f, 1);
        Assert.True(reason.Width >= CellLayout.EstimateTextWidth(JoinAdminLimits.PlaceholderReason, CellLayout.InputFontSize) + 16f,
            "placeholder fits: " + reason.Width);
    }

    [Fact]
    public void The_reason_line_sits_under_its_name_line_and_sections_have_room_above_their_headings()
    {
        var reason = JoinAdminRowLayout.Reason("ja-req-a", "");
        Assert.Equal("ja-req-a" + JoinAdminRowLayout.ReasonSuffix, reason.Key);
        Assert.True(reason.Compact);
        Assert.Equal(JoinAdminRowLayout.ReasonGap, reason.GapAbove);
        Assert.Equal(JoinAdminRowLayout.SectionGap, JoinAdminRowLayout.SectionHeading("h", "Required client mods", "0 required").GapAbove);
        Assert.InRange(JoinAdminRowLayout.SectionGap, 16f, 32f);
        // Every heading in the panel uses it.
        Assert.DoesNotContain("panel.CellRow(JoinFlowLayout.Heading(", Source("Networking", "JoinAdminPanel.cs"));
    }

    [Fact]
    public void The_install_window_help_cannot_be_read_as_the_rule_being_off()
    {
        Assert.DoesNotContain("turns it off", JoinAdminText.WindowHelp, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("How long a player who is missing a required mod may stay on the join screens", JoinAdminText.WindowHelp, StringComparison.Ordinal);
        Assert.Contains("0: no extra time; they are refused at once", JoinAdminText.WindowHelp, StringComparison.Ordinal);
        Assert.Contains("Thunderstore", JoinAdminText.WindowFixes, StringComparison.Ordinal);
    }

    /// <summary>
    /// TEXTEXPAND2 ("the ... does nothing"): every settings host has the dialog source the expanded editor opens with,
    /// because the Server tab and Preferences build through <c>EnsureBindings</c>, which now provides it.
    /// </summary>
    [Fact]
    public void Every_settings_host_has_the_expanded_editors_dialog_source()
    {
        string menu = Source("Ui", "ModsMenu.cs");
        int at = menu.IndexOf("private static void EnsureBindings()", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = menu.Substring(at, 600);
        Assert.Contains("EnsureModPageDialogFactory();", body);
        Assert.True(body.IndexOf("EnsureModPageDialogFactory();", StringComparison.Ordinal) < body.IndexOf("if (_bindings != null) return;", StringComparison.Ordinal));
        Assert.Contains("EnsureBindings();", Source("Ui", "ModsMenu.SectionNav.cs"));
        Assert.Contains("has no dialog source; nothing opened.", Source("Ui", "ModSettingsView.cs"));
    }

    private static string Source(string dir, string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Progenitor.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "Progenitor.Core", dir, file));
    }
}
