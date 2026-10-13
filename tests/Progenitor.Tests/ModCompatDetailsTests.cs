using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C5 (docs/mod-compatibility.md, docs/mods-menu.md): the Works across versions marker on the Mods list
/// entry and the mod page, and the page's For developers section. Game-free: the fake UI runtime and the plugin registry;
/// the network half of the developer details comes from a test seam. Nothing here has been observed in the game.
/// </summary>
[Collection("PluginLogger")]
public sealed class ModCompatDetailsTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();

    public ModCompatDetailsTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModSettingsPages.ResetForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModRegistry.ResetForTests();
        ModProblems.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.ResetForTests();
        ModsMenu.ResetStage3ForTests();
        ModsMenu.ResetCompatDetailsForTests();
        UiDialog.ForgetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-c5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModEnableState.TestDirectory = _dir;
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        ModsMenu.Close();
        ModsMenu.ResetStage3ForTests();
        ModsMenu.ResetCompatDetailsForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModSettingsPages.ResetForTests();
        ModRegistry.ResetForTests();
        ModProblems.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.TestDirectory = null;
        ModEnableState.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- the decision ----------------------------------------------------------------------------

    /// <summary>
    /// Only a declared range that admits a release other than the mod's own version earns the marker.
    /// What this would catch: an exact range (or one that holds only the own version) shown as "works across versions", an
    /// undeclared mod marked by its default same minor, or a wide range missed.
    /// </summary>
    [Theory]
    [InlineData("*", "1.2.0", true)]
    [InlineData(">=1.2.0 <2.0.0", "1.4.0", true)]
    [InlineData("1.2.x", "1.2.0", true)]
    [InlineData("1.x", "1.2.0", true)]
    [InlineData("1.2.x || 1.4.x", "1.4.0", true)]
    [InlineData(">1.2.0 <1.2.1", "1.2.0", true)]
    [InlineData(">=1.2.0", "1.2.0", true)]
    [InlineData("<=1.2.0", "1.2.0", true)]
    [InlineData(">=2.0.0", "1.2.0", true)]
    [InlineData("=1.2.0", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData(">=1.2.0 <=1.2.0", "1.2.0", false)]
    [InlineData("=1.2.0 || =1.2.0", "1.2.0", false)]
    [InlineData(">=1.2.0-0 <=1.2.0", "1.2.0", false)]
    [InlineData("", "1.2.0", false)]
    [InlineData("  ", "1.2.0", false)]
    [InlineData("not a range", "1.2.0", false)]
    [InlineData(null, "1.2.0", false)]
    public void The_marker_needs_a_declared_range_that_admits_another_version(string? peers, string version, bool expected)
    {
        Assert.Equal(expected, ModAcrossVersions.Applies(peers, version));
    }

    /// <summary>A package version with no inspection yet is asked again later; a found inspection is remembered.</summary>
    [Fact]
    public void A_missing_inspection_is_not_remembered()
    {
        int asked = 0;
        (bool, string) None() { asked++; return (false, ""); }
        (bool, string) Found() { asked++; return (true, ">=1.0.0 <2.0.0"); }
        Assert.Equal("", ModsMenu.InspectedPeersCached("p|pkg|1.0.0", None));
        Assert.Equal(">=1.0.0 <2.0.0", ModsMenu.InspectedPeersCached("p|pkg|1.0.0", Found));
        Assert.Equal(">=1.0.0 <2.0.0", ModsMenu.InspectedPeersCached("p|pkg|1.0.0", None));
        Assert.Equal(2, asked);
    }

    /// <summary>The hover text (the list icon's and the page row's) is the approved wording: the author's claim, and what decides.</summary>
    [Fact]
    public void The_marker_help_does_not_overstate()
    {
        Assert.Equal(
            "The author says this mod works with its other versions in a declared range, so a server and a player don't need "
            + "the exact same version. The server's version matching decides at the join.",
            ModAcrossVersions.Help);
        Assert.Equal("Works across versions", ModAcrossVersions.Words);
    }

    /// <summary>
    /// An undeclared mod (its effective range is the default same minor, which admits other patches) gets no icon; a
    /// declared wide range gets the loop-arrows icon right after the version, and the line's text is the plain line.
    /// </summary>
    [Fact]
    public void An_undeclared_mod_gets_no_marker_and_a_declared_one_does()
    {
        var plain = ModsListLayout.Decide(Facts("1.4.0", ""));
        Assert.False(plain.AcrossVersions);
        Assert.False(plain.AcrossIcon);
        Assert.Equal("", plain.AcrossRange);
        Assert.True(plain.PlainSecondLine);
        Assert.Equal("1.4.0 · Client", ModsListLayout.DrawnSecondLine(plain, 800f, out string shown, out string after));
        Assert.Equal(ModAcrossVersions.ShownNone, shown);
        Assert.Equal("", after);
        Assert.Null(ModsListLayout.EntryRow(plain, 800f).Find(ModsListLayout.CellAcross));

        var declared = ModsListLayout.Decide(Facts("1.4.0", ">=1.2.0 <2.0.0"));
        Assert.True(declared.AcrossVersions);
        Assert.True(declared.AcrossIcon);
        Assert.Equal(">=1.2.0 <2.0.0", declared.AcrossRange);
        Assert.Equal("1.4.0", declared.LineVersion);
        Assert.Equal("1.4.0 · Client", declared.SecondLine);
        Assert.Equal("1.4.0 · Client", ModsListLayout.DrawnSecondLine(declared, 800f, out shown, out after));
        Assert.Equal(ModAcrossVersions.ShownIcon, shown);
        Assert.Equal("1.4.0", after);

        var row = ModsListLayout.EntryRow(declared, 800f);
        Assert.Equal(declared.Name + "\n" + declared.SecondLine, row.Find(ModsListLayout.CellName)!.Text);
        var icon = row.Find(ModsListLayout.CellAcross)!;
        Assert.Equal(CellKind.Image, icon.Kind);
        Assert.Equal(CellSkin.LoopGlyph, icon.Skin);
        Assert.Equal(CellTone.Muted, icon.Tone);
        Assert.Equal(ModsListLayout.CellName, icon.OverlayOf);
        Assert.Equal("1.4.0".Length, icon.InlineAfter);
        Assert.True(icon.Hover);
        Assert.Equal(ModsListLayout.AcrossIconWidth, icon.Width.Value);
        Assert.Equal(CellLayout.LoopGlyphHeight, 11f);
        Assert.True(ModsListLayout.AcrossIconHeight >= CellLayout.LoopGlyphHeight);
    }

    // ---- the list entry --------------------------------------------------------------------------

    /// <summary>
    /// The icon sits right after the version at every list width where the version fits whole with the icon's room taken
    /// out: its cell starts at the name cell's left, the caption inset and the version's width, inside the name cell, and
    /// never over the Update tag. The line's text keeps that room (the sides end in an ellipsis that much sooner, as before
    /// the icon only later); where even the version would be cut, there is no icon and the line is clipped as it always was.
    /// What this would catch: an icon over the Update tag, one drawn past the name cell, an icon beside a cut version, or
    /// the sides' text running under the icon.
    /// </summary>
    [Fact]
    public void The_icon_sits_right_after_the_version_at_every_list_width()
    {
        foreach (bool update in new[] { false, true })
        {
            var entry = ModsListLayout.Decide(Facts("1.0.19", "*", update: update));
            Assert.True(entry.AcrossIcon);
            var seen = new List<string>();
            bool ellipsizedWithIcon = false;
            for (float row = 80f; row <= 800f; row += 2f)
            {
                string drawn = ModsListLayout.DrawnSecondLine(entry, row, out string shown, out string after);
                float note = Math.Max(24f, ModsListLayout.NameWidth(entry, row) - 4f);
                var spec = ModsListLayout.EntryRow(entry, row);
                var placed = CellLayout.Resolve(row, spec.Cells, spec.RightInset);
                var name = placed.Single(p => p.Key == ModsListLayout.CellName);
                var icon = placed.Single(p => p.Key == ModsListLayout.CellAcross);
                if (shown == ModAcrossVersions.ShownIcon)
                {
                    Assert.Equal("1.0.19", after);
                    Assert.Equal(MenuPanelLayout.ClipLabel(entry.SecondLine, note - ModsListLayout.AcrossIconWidth, CellLayout.TwoLineNoteFontSize), drawn);
                    Assert.StartsWith("1.0.19", drawn, StringComparison.Ordinal);
                    if (drawn.EndsWith("…", StringComparison.Ordinal)) ellipsizedWithIcon = true;
                    Assert.True(icon.Visible, "the icon is placed at row width " + row);
                    float expected = name.Left + CellLayout.TextInset + CellLayout.EstimateTextWidth("1.0.19", CellLayout.TwoLineNoteFontSize);
                    Assert.Equal(Math.Min(expected, name.Right - icon.Width), icon.Left, 2);
                    Assert.True(icon.Left >= name.Left && icon.Right <= name.Right + 0.01f, "inside the name cell at " + row);
                    Assert.Equal(ModsListLayout.AcrossIconWidth, icon.Width, 2);
                }
                else
                {
                    Assert.Equal(ModAcrossVersions.ShownNone, shown);
                    Assert.Equal("", after);
                    Assert.Equal(MenuPanelLayout.ClipLabel(entry.SecondLine, note, CellLayout.TwoLineNoteFontSize), drawn);
                    Assert.False(icon.Visible, "no icon beside a cut version at row width " + row);
                }

                if (update)
                {
                    var tag = placed.Single(p => p.Key == ModsListLayout.CellUpdate);
                    Assert.True(tag.Visible);
                    Assert.False(CellLayout.Overlap(icon, tag), "the icon is clear of the Update tag at row width " + row);
                }

                if (seen.Count == 0 || seen[^1] != shown) seen.Add(shown);
            }

            // Narrow to wide: no icon while the version itself would be cut, then the icon for good.
            Assert.Equal(new[] { ModAcrossVersions.ShownNone, ModAcrossVersions.ShownIcon }, seen);
            // At some width the sides are cut while the icon is drawn: the sides still ellipsize.
            Assert.True(ellipsizedWithIcon);
        }
    }

    /// <summary>
    /// The Update tag keeps its width and place: the name cell is sized before the icon is placed, and the icon is an
    /// overlay that takes no slot, so a marked entry's cells sit where the unmarked entry's do.
    /// What this would catch: an icon that widens the name cell or takes a slot and pushes the Update tag out of the row.
    /// </summary>
    [Fact]
    public void The_marker_never_displaces_the_Update_tag()
    {
        var marked = ModsListLayout.Decide(Facts("1.0.19", "*", update: true));
        var plain = ModsListLayout.Decide(Facts("1.0.19", "", update: true));
        foreach (float row in new[] { 160f, 260f, 320f, 420f, 600f })
        {
            var a = ModsListLayout.EntryRow(marked, row);
            var b = ModsListLayout.EntryRow(plain, row);
            Assert.Equal(b.Find(ModsListLayout.CellUpdate)!.Width.Value, a.Find(ModsListLayout.CellUpdate)!.Width.Value);
            Assert.Equal(b.Find(ModsListLayout.CellName)!.Width.Value, a.Find(ModsListLayout.CellName)!.Width.Value);
            var placedA = CellLayout.Resolve(row, a.Cells, a.RightInset);
            var placedB = CellLayout.Resolve(row, b.Cells, b.RightInset);
            foreach (string key in new[] { ModsListLayout.CellUpdate, ModsListLayout.CellName, ModsListLayout.CellDiamond })
            {
                var x = placedA.Single(p => p.Key == key);
                var y = placedB.Single(p => p.Key == key);
                Assert.Equal(y.X, x.X, 2);
                Assert.Equal(y.Width, x.Width, 2);
                Assert.True(x.Visible);
            }
        }
    }

    /// <summary>
    /// The kit's inline rule: room kept after the text before it when that text stays whole, the toned lead counted, and
    /// the ordinary clip when it does not fit or the split is outside the note.
    /// </summary>
    [Fact]
    public void The_inline_note_rule_keeps_room_only_after_whole_text()
    {
        string line = CellLayout.InlineNoteLine("", "1.3.0 · Client and server", 5, 400f, 15f, out int at);
        Assert.Equal(5, at);
        Assert.Equal("1.3.0 · Client and server", line);

        // Narrow: the sides are cut, the version kept, the room still there.
        float narrow = CellLayout.EstimateTextWidth("1.3.0 · Cli", CellLayout.TwoLineNoteFontSize) + 15f;
        line = CellLayout.InlineNoteLine("", "1.3.0 · Client and server", 5, narrow, 15f, out at);
        Assert.Equal(5, at);
        Assert.StartsWith("1.3.0", line, StringComparison.Ordinal);
        Assert.EndsWith("…", line, StringComparison.Ordinal);

        // Too narrow for the version with the room: no room, the plain clip.
        float tiny = CellLayout.EstimateTextWidth("1.3.", CellLayout.TwoLineNoteFontSize) + 15f;
        line = CellLayout.InlineNoteLine("", "1.3.0 · Client and server", 5, tiny, 15f, out at);
        Assert.Equal(-1, at);
        Assert.Equal(MenuPanelLayout.ClipLabel("1.3.0 · Client and server", tiny, CellLayout.TwoLineNoteFontSize), line);

        // A lead before the note moves the split; a split outside the note keeps no room.
        line = CellLayout.InlineNoteLine("Update · ", "1.3.0 · Client", 5, 400f, 15f, out at);
        Assert.Equal("Update · ".Length + 5, at);
        CellLayout.InlineNoteLine("", "1.3.0", 9, 400f, 15f, out at);
        Assert.Equal(-1, at);
        CellLayout.InlineNoteLine("", "1.3.0", 0, 400f, 15f, out at);
        Assert.Equal(-1, at);
    }

    /// <summary>
    /// A problem or state line is never marked, whatever the range.
    /// What this would catch: "Failed to load · Works across versions", or the marker over "Disabled".
    /// </summary>
    [Fact]
    public void The_marker_is_never_added_to_a_problem_or_state_line()
    {
        var failed = ModsListLayout.Decide(new ModsListEntryFacts
        {
            Id = "test.c5.failed",
            Name = "Failed",
            Version = "1.0.0",
            Sides = ModSides.Client,
            Line = ModFrameworkLine.Compose(new ModFrameworkFacts { PluginId = "test.c5.failed", Version = "1.0.0", Sides = ModSides.Client }),
            Problems = new[] { ModProblems.LoadFailed("test.c5.failed", "", false) },
            DeclaredPeers = "*",
        });
        Assert.True(failed.AcrossVersions);
        Assert.False(failed.PlainSecondLine);
        Assert.False(failed.AcrossIcon);
        Assert.Equal(failed.SecondLine, ModsListLayout.DrawnSecondLine(failed, 800f, out string shown));
        Assert.Equal(ModAcrossVersions.ShownNone, shown);
        Assert.Null(ModsListLayout.EntryRow(failed, 800f).Find(ModsListLayout.CellAcross));

        var missing = ModsListLayout.Decide(Facts("1.0.0", "*", serverHalfMissing: true));
        Assert.Equal(ModsListLayout.ServerHalfMissingLine, missing.SecondLine);
        Assert.Equal(ModsListLayout.ServerHalfMissingLine, ModsListLayout.DrawnSecondLine(missing, 800f, out shown));
        Assert.Equal(ModAcrossVersions.ShownNone, shown);
        Assert.False(missing.AcrossIcon);
        Assert.Null(ModsListLayout.EntryRow(missing, 800f).Find(ModsListLayout.CellAcross));
    }

    /// <summary>
    /// In the workspace: a registered plugin whose loaded build declares a wide range reads <c>acrossVersions</c>,
    /// <c>range</c>, <c>acrossVersionsShown: icon</c>, <c>acrossIconAfter</c> (its version) and <c>secondLineDrawn</c> on
    /// its entry, and its row carries the icon cell; an undeclared one reads false and <c>none</c> and has no icon cell.
    /// </summary>
    [Fact]
    public void The_list_readback_reports_the_marker()
    {
        Known("test.c5.wide", "Wide", "1.0.0", Declaration("1.0.0", ">=1.0.0 <2.0.0"));
        Known("test.c5.plain", "Plain", "1.0.0", null);
        using var wide = ModsMenu.Register(_owner, "test.c5.wide", "Wide");
        using var plain = ModsMenu.Register(_owner, "test.c5.plain", "Plain");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.c5.wide");

        using var list = Widgets("list");
        var w = Extra(list, "modsListEntry", e => e.GetProperty("id").GetString() == "test.c5.wide")!.Value;
        Assert.True(w.GetProperty("acrossVersions").GetBoolean(), w.ToString());
        Assert.Equal(">=1.0.0 <2.0.0", w.GetProperty("range").GetString());
        string drawn = w.GetProperty("secondLineDrawn").GetString()!;
        Assert.Equal(ModAcrossVersions.ShownIcon, w.GetProperty("acrossVersionsShown").GetString());
        Assert.Equal("1.0.0", w.GetProperty("acrossIconAfter").GetString());
        Assert.StartsWith("1.0.0", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain(ModAcrossVersions.Words, drawn, StringComparison.Ordinal);
        var wideRow = ListRow(list, "test.c5.wide");
        var icon = Cell(wideRow, ModsListLayout.CellAcross)!.Value;
        Assert.Equal("image", icon.GetProperty("kind").GetString());
        Assert.Equal("loop-glyph", icon.GetProperty("skin").GetString());
        Assert.Equal(ModsListLayout.CellName, icon.GetProperty("overlayOf").GetString());
        Assert.Equal("1.0.0".Length, icon.GetProperty("inlineAfter").GetInt32());
        Assert.True(icon.GetProperty("hover").GetBoolean());

        var p = Extra(list, "modsListEntry", e => e.GetProperty("id").GetString() == "test.c5.plain")!.Value;
        Assert.False(p.GetProperty("acrossVersions").GetBoolean());
        Assert.Equal("", p.GetProperty("range").GetString());
        Assert.Equal(ModAcrossVersions.ShownNone, p.GetProperty("acrossVersionsShown").GetString());
        Assert.Equal("", p.GetProperty("acrossIconAfter").GetString());
        Assert.StartsWith("1.0.0", p.GetProperty("secondLineDrawn").GetString(), StringComparison.Ordinal);
        Assert.Null(Cell(ListRow(list, "test.c5.plain"), ModsListLayout.CellAcross));
    }

    /// <summary>
    /// Pointing at the icon puts its hover text in the open page's description column (the workspace's hover-text place),
    /// and leaving it puts the column back; the icon cell holds a pointer slot but no click of its own.
    /// What this would catch: an icon whose hover is never wired (no slot for a hover-only image cell), hover text that
    /// differs from the page row's, or a column left on the icon's text after the pointer leaves.
    /// </summary>
    [Fact]
    public void Pointing_at_the_icon_describes_it_in_the_column()
    {
        Known("test.c5.wide", "Wide", "1.0.0", Declaration("1.0.0", ">=1.0.0 <2.0.0"));
        using var wide = ModsMenu.Register(_owner, "test.c5.wide", "Wide");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.c5.wide");
        string rowKey = ModsListLayout.RowKey("test.c5.wide");
        (string Heading, string Body) Column()
        {
            using var page = Widgets("content");
            var column = Extra(page, ModPagePane.RowDetails)!.Value.GetProperty("column");
            return (column.GetProperty("heading").GetString()!, column.GetProperty("body").GetString()!);
        }

        var rest = Column();
        Assert.NotEqual(ModAcrossVersions.Help, rest.Body);
        Assert.True(UiCellRowHandle.TryHoverCell(rowKey, ModsListLayout.CellAcross, entered: true));
        Assert.Equal((ModAcrossVersions.Words, ModAcrossVersions.Help), Column());
        Assert.True(UiCellRowHandle.TryHoverCell(rowKey, ModsListLayout.CellAcross, entered: false));
        Assert.Equal(rest, Column());

        // The bridge: mods_ui hover {label: <row key>, cell: across} (and leave: true) takes the same slot.
        using (var args = JsonDocument.Parse("{\"action\":\"hover\",\"pane\":\"list\",\"label\":\"mod:test.c5.wide\",\"cell\":\"across\"}"))
        {
            var request = Progenitor.Dev.Ops.ModsUiArgs.Parse(args.RootElement);
            Assert.Equal("mod:test.c5.wide", request.Label);
            Assert.Equal(ModsListLayout.CellAcross, request.Cell);
            Assert.False(request.Leave);
        }

        using (var on = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.HoverCell(rowKey, ModsListLayout.CellAcross, leave: false))))
        {
            Assert.True(on.RootElement.GetProperty("entered").GetBoolean());
            Assert.Equal(ModAcrossVersions.Help, on.RootElement.GetProperty("column").GetProperty("body").GetString());
        }

        ModsMenu.HoverCell(rowKey, ModsListLayout.CellAcross, leave: true);
        Assert.Equal(rest, Column());
        Assert.Throws<ArgumentException>(() => ModsMenu.HoverCell(rowKey, "nothing", leave: false));

        // Hover only: the bridge's pointer reaches it, a controller's press list does not.
        Assert.False(UiCellRowHandle.IsClickable(ModsListLayout.AcrossIconCell(5), header: false));
        Assert.True(UiCellRowHandle.IsHoverOnly(ModsListLayout.AcrossIconCell(5), header: false));
        Assert.True(UiCellRowHandle.HasWidget(ModsListLayout.AcrossIconCell(5), header: false));
    }

    /// <summary>
    /// The glyph: a texture the size of its own bounding box, transparent at the corners and in the loop's middle, solid
    /// on the top arc, open in the gaps between the two arcs, and computed once (the same pixels every call).
    /// What this would catch: a glyph drawn off its texture, a filled disc instead of a loop, or a texture rebuilt per row.
    /// </summary>
    [Fact]
    public void The_loop_glyph_is_drawn_once_at_its_own_aspect()
    {
        var pixels = LoopGlyph.Pixels();
        Assert.Same(pixels, LoopGlyph.Pixels());
        Assert.Equal(LoopGlyph.TextureWidth * LoopGlyph.TextureHeight, pixels.Length);
        Assert.Equal(13.2f, CellLayout.LoopGlyphWidth, 2);
        Assert.Equal(0f, LoopGlyph.Alpha(0, 0));
        Assert.Equal(0f, LoopGlyph.Alpha(LoopGlyph.TextureWidth - 1, LoopGlyph.TextureHeight - 1));
        Assert.Equal(0f, LoopGlyph.Alpha(LoopGlyph.TextureWidth / 2, LoopGlyph.TextureHeight / 2));
        Assert.False(LoopGlyph.Inside(0.5f, 0.5f));
        Assert.True(LoopGlyph.Inside(0.5f, 0.076f));   // the top of the first arc (270 degrees)
        Assert.True(LoopGlyph.Inside(0.5f, 0.924f));   // the bottom of the second arc (90 degrees)
        Assert.False(LoopGlyph.Inside(0.851f, 0.559f)); // the gap at 3 o'clock, past the first arrowhead
        Assert.False(LoopGlyph.Inside(0.149f, 0.441f)); // the gap at 9 o'clock, past the second arrowhead
        int solid = pixels.Count(a => a > 0.5f);
        Assert.InRange(solid, pixels.Length / 8, pixels.Length / 2);
        // Two arcs half a turn apart: the glyph is the same turned 180 degrees.
        int differ = 0;
        for (int y = 0; y < LoopGlyph.TextureHeight; y++)
            for (int x = 0; x < LoopGlyph.TextureWidth; x++)
                if (Math.Abs(LoopGlyph.Alpha(x, y) - LoopGlyph.Alpha(LoopGlyph.TextureWidth - 1 - x, LoopGlyph.TextureHeight - 1 - y)) > 0.25f)
                    differ++;
        Assert.True(differ < pixels.Length / 100, differ + " pixels differ from the half turn");
    }

    // ---- the page ---------------------------------------------------------------------------------

    /// <summary>
    /// The page row is a Details grid row: its name at the margin, its value (the range in words) at x1 plus the caption
    /// inset, ending inside the grid's bar edge.
    /// </summary>
    [Fact]
    public void The_page_row_sits_in_the_details_grid()
    {
        const float content = 1288f;
        float facts = ModPageSpacing.FactsWidth(content);
        string words = ModAcrossVersions.RangeWords(">=1.2.0 <2.0.0");
        Assert.Equal("Works with 1.2.0 or newer, below 2.0.0", words);
        Assert.Equal("Works with any version", ModAcrossVersions.RangeWords("*"));
        Assert.Equal("Works with any 1.2.x version", ModAcrossVersions.RangeWords("1.2.x"));
        var spec = ModPagePane.FactRow("across", ModAcrossVersions.Words,
            new[] { ModPagePane.FactText(ModPagePane.CellFactValue, words, maxWidth: ModPageSpacing.TextValueWidth(facts)) }, facts);
        float rowLeft = -facts * 0.5f;
        var name = CellLayout.Resolve(facts, spec.Cells, spec.RightInset).Single(c => c.Key == ModPagePane.CellFactName);
        var value = CellLayout.Resolve(facts, spec.Cells, spec.RightInset).Single(c => c.Key == ModPagePane.CellFactValue);
        Assert.Equal(ModPageSpacing.Margin - ModPageSpacing.CellTextOffset, name.X - name.Width * 0.5f - rowLeft, 2);
        Assert.Equal(ModPageSpacing.X1(facts) + ModPageSpacing.ValueInset - ModPageSpacing.CellTextOffset, value.X - value.Width * 0.5f - rowLeft, 2);
        Assert.True(value.X + value.Width * 0.5f - rowLeft <= ModPageSpacing.ValueRight(facts) + 0.01f);
        Assert.Equal(ModPageSpacing.FactRowHeight, spec.ExplicitHeight);

        // A long developer value (several messages) is capped at the bar edge and ends in an ellipsis there.
        string longValue = string.Join("; ", Enumerable.Range(0, 8).Select(i => "message" + i + " reads v2 · sent v2 (min reader 1)"));
        var dev = ModPagePane.FactRow("dev-messages", ModDevDetails.NameMessages,
            new[] { ModPagePane.FactText(ModPagePane.CellFactValue, longValue, maxWidth: ModPageSpacing.TextValueWidth(facts)) }, facts);
        var devValue = CellLayout.Resolve(facts, dev.Cells, dev.RightInset).Single(c => c.Key == ModPagePane.CellFactValue);
        Assert.Equal(ModPageSpacing.X1(facts) + ModPageSpacing.ValueInset - ModPageSpacing.CellTextOffset, devValue.X - devValue.Width * 0.5f - rowLeft, 2);
        Assert.True(devValue.X + devValue.Width * 0.5f - rowLeft <= ModPageSpacing.ValueRight(facts) + 0.01f);
        Assert.Equal(CellWrap.Ellipsis, dev.Find(ModPagePane.CellFactValue)!.Wrap);
    }

    /// <summary>
    /// A declared wide range draws the Works across versions row with its readback and column text; an undeclared mod
    /// draws none.
    /// </summary>
    [Fact]
    public void The_page_shows_the_row_only_for_a_declared_wide_range()
    {
        Known("test.c5.wide", "Wide", "1.0.0", Declaration("1.0.0", ">=1.0.0 <2.0.0"));
        Known("test.c5.exact", "Exact", "1.0.0", Declaration("1.0.0", "=1.0.0"));
        using var wide = ModsMenu.Register(_owner, "test.c5.wide", "Wide");
        using var exact = ModsMenu.Register(_owner, "test.c5.exact", "Exact");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.c5.wide");
        using (var page = Widgets("content"))
        {
            var compat = Extra(page, ModPagePane.RowCompat)!.Value;
            Assert.True(compat.GetProperty("acrossVersions").GetBoolean());
            Assert.Equal(">=1.0.0 <2.0.0", compat.GetProperty("range").GetString());
            Assert.Equal("Works with 1.0.0 or newer, below 2.0.0", compat.GetProperty("words").GetString());
            var fact = Extra(page, ModPagePane.RowFact, f => f.GetProperty("name").GetString() == ModAcrossVersions.Words)!.Value;
            Assert.Equal(ModAcrossVersions.Help, fact.GetProperty("description").GetString());
        }

        ModsMenu.OpenMod("test.c5.exact");
        using (var page = Widgets("content"))
        {
            Assert.Null(Extra(page, ModPagePane.RowCompat));
            Assert.Null(Extra(page, ModPagePane.RowFact, f => f.GetProperty("name").GetString() == ModAcrossVersions.Words));
        }
    }

    /// <summary>
    /// For developers: collapsed by default (the heading only, its readback listing the rows); a click on the heading
    /// expands it in place with Details grid rows; the state is kept per mod while the workspace is open and forgotten when
    /// it closes. A mod with no registry entry has no section.
    /// What this would catch: a section open by default, a toggle that forgets on the next redraw, one mod's state leaking
    /// to another, or a section on a mod Progenitor did not load.
    /// </summary>
    [Fact]
    public void The_developer_section_folds_per_mod_while_the_workspace_is_open()
    {
        Known("test.c5.dev", "Dev", "1.4.0", Declaration("1.4.0", ">=1.2.0 <2.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, ruleDeclared: true));
        Known("test.c5.other", "Other", "1.0.0", null);
        ModsMenu.DevNetProbe = id => id == "test.c5.dev"
            ? (new Dictionary<string, int> { ["notes"] = 1, ["hello"] = 2 },
                new[] { new ModMessageSummary("hello", 2, 2, 1) },
                new ModDevPeer { Version = "1.2.0", Features = new Dictionary<string, int> { ["hello"] = 1 } })
            : (new Dictionary<string, int>(), Array.Empty<ModMessageSummary>(), null);
        using var dev = ModsMenu.Register(_owner, "test.c5.dev", "Dev");
        using var other = ModsMenu.Register(_owner, "test.c5.other", "Other");
        using var loose = ModsMenu.Register(_owner, "test.c5.unloaded", "Unloaded");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.c5.dev");

        using (var page = Widgets("content"))
        {
            var section = Extra(page, ModPagePane.RowDev)!.Value;
            Assert.Equal(ModDevDetails.Heading, section.GetProperty("heading").GetString());
            Assert.False(section.GetProperty("expanded").GetBoolean());
            Assert.Equal(5, section.GetProperty("rows").GetArrayLength());
            Assert.Null(Extra(page, ModPagePane.RowDevRow));
        }

        ModsMenu.ClickVisibleWidget("content", ModDevDetails.HeadingKey, null, null);
        Assert.True(ModsMenu.DevDetailsExpanded("test.c5.dev"));
        using (var page = Widgets("content"))
        {
            Assert.True(Extra(page, ModPagePane.RowDev)!.Value.GetProperty("expanded").GetBoolean());
            string Value(string key) => Extra(page, ModPagePane.RowDevRow, r => r.GetProperty("key").GetString() == key)!.Value.GetProperty("value").GetString()!;
            Assert.Equal(">=1.2.0 <2.0.0 · 1.2.0 or newer, below 2.0.0", Value("dev-range"));
            Assert.Equal("ServerMustHave · Exact", Value("dev-network"));
            Assert.Equal("hello 2 · notes 1", Value("dev-features"));
            Assert.Equal("hello reads v2 · sent v2 (min reader 1)", Value("dev-messages"));
            Assert.Equal("1.2.0 · hello 1", Value("dev-peer"));
            // Each row is also an ordinary Details fact, described in the column.
            Assert.NotNull(Extra(page, ModPagePane.RowFact, f => f.GetProperty("name").GetString() == ModDevDetails.NameMessages));
        }

        // Another mod starts collapsed; coming back keeps the first one open.
        ModsMenu.OpenMod("test.c5.other");
        using (var page = Widgets("content"))
        {
            var section = Extra(page, ModPagePane.RowDev)!.Value;
            Assert.False(section.GetProperty("expanded").GetBoolean());
            Assert.Equal(4, section.GetProperty("rows").GetArrayLength());   // no server row while not connected
        }

        ModsMenu.OpenMod("test.c5.dev");
        using (var page = Widgets("content"))
            Assert.True(Extra(page, ModPagePane.RowDev)!.Value.GetProperty("expanded").GetBoolean());

        // A registered mod that Progenitor did not load here has no section.
        ModsMenu.OpenMod("test.c5.unloaded");
        using (var page = Widgets("content"))
            Assert.Null(Extra(page, ModPagePane.RowDev));

        // Closing the workspace folds them again.
        ModsMenu.Close();
        Assert.False(ModsMenu.DevDetailsExpanded("test.c5.dev"));
    }

    /// <summary>The heading: at the page margin like Details and About, the fold marker right after the title, clickable.</summary>
    [Fact]
    public void The_developer_heading_is_a_page_section_heading_with_the_fold_marker()
    {
        const float content = 1288f;
        var spec = ModDevDetails.HeadingRow();
        Assert.Equal(ModDevDetails.HeadingKey, spec.Key);
        Assert.Equal("page-section-dev", spec.Key);
        Assert.Equal(ModPageSpacing.LeadKey, spec.Cells[0].Key);
        Assert.Equal(CellRowSkin.CategoryHeading, spec.Skin);
        Assert.Equal(ModPageSpacing.HeadingGapAbove, spec.GapAbove);
        Assert.False(spec.Plain);
        var placed = CellLayout.Resolve(content, spec.Cells, spec.RightInset);
        var title = placed.Single(p => p.Key == ModDevDetails.CellTitle);
        var chevron = placed.Single(p => p.Key == ModDevDetails.CellChevron);
        Assert.Equal(ModPageSpacing.Margin - ModPageSpacing.CellTextOffset, title.X - title.Width * 0.5f + content * 0.5f, 2);
        Assert.Equal(CellLayout.Gap, chevron.X - chevron.Width * 0.5f - (title.X + title.Width * 0.5f), 2);
        Assert.Equal(CellKind.Chevron, spec.Find(ModDevDetails.CellChevron)!.Kind);
    }

    // ---- the words --------------------------------------------------------------------------------

    [Fact]
    public void The_developer_rows_say_the_declaration_in_words()
    {
        Assert.Equal("None declared (works with 1.4.x by default)", ModDevDetails.RangeText(null, "1.4.0"));
        Assert.Equal("None declared (works with 1.4.x by default)", ModDevDetails.RangeText(Declaration("1.4.0", null), "1.4.0"));
        Assert.Equal("None declared (exactly 1.4.0, by its Exact rule)",
            ModDevDetails.RangeText(Declaration("1.4.0", null, rule: NetworkVersionRule.Exact, ruleDeclared: true), "1.4.0"));
        Assert.Equal("None declared (any version, by its None rule)",
            ModDevDetails.RangeText(Declaration("1.4.0", null, rule: NetworkVersionRule.None, ruleDeclared: true), "1.4.0"));
        Assert.Equal("\"1.2.0 ||\" is not a valid range and is ignored (works with 1.4.x by default)",
            ModDevDetails.RangeText(Declaration("1.4.0", "1.2.0 || "), "1.4.0"));
        Assert.Equal("1.2.x || 1.4.x · any 1.2.x version or any 1.4.x version",
            ModDevDetails.RangeText(Declaration("1.4.0", "1.2.x || 1.4.x"), "1.4.0"));

        Assert.Equal(ModDevDetails.NotDeclared, ModDevDetails.NetworkText(null));
        Assert.Equal("ServerMustHave · Exact", ModDevDetails.NetworkText(Declaration("1.0.0", null, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true)));
        Assert.Equal("NoNeedForSync · rule not declared", ModDevDetails.NetworkText(Declaration("1.0.0", null, NetworkCompatibilityLevel.NoNeedForSync)));

        Assert.Equal(ModDevDetails.NoneOffered, ModDevDetails.FeaturesText(new Dictionary<string, int>()));
        Assert.Equal("hello 2 · notes 1", ModDevDetails.FeaturesText(new Dictionary<string, int> { ["notes"] = 1, ["hello"] = 2 }));

        Assert.Equal(ModDevDetails.NoneRegistered, ModDevDetails.MessagesText(Array.Empty<ModMessageSummary>()));
        Assert.Equal("hello reads v2 · sent v2 (min reader 1); ping no handler · sent v1 (min reader 1); stats reads v3 · not sent",
            ModDevDetails.MessagesText(new[]
            {
                new ModMessageSummary("hello", 2, 2, 1),
                new ModMessageSummary("ping", 0, 1, 1),
                new ModMessageSummary("stats", 3, 0, 0),
            }));

        Assert.Equal(ModDevDetails.NotOnServer, ModDevDetails.PeerText(new ModDevPeer()));
        Assert.Equal("1.2.0 · no features", ModDevDetails.PeerText(new ModDevPeer { Version = "1.2.0" }));

        // The column repeats a long value whole; an empty one adds nothing.
        var rows = ModDevDetails.Rows(new ModDevFacts { Version = "1.0.0", Messages = new[] { new ModMessageSummary("hello", 1, 0, 0) } });
        Assert.Equal(new[] { "dev-range", "dev-network", "dev-features", "dev-messages" }, rows.Select(r => r.Key).ToArray());
        Assert.EndsWith("hello reads v1 · not sent", rows[3].Help, StringComparison.Ordinal);
        Assert.Equal(ModDevDetails.FeaturesHelp, rows[2].Help);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static ModsListEntryFacts Facts(string version, string peers, bool update = false, bool serverHalfMissing = false) => new()
    {
        Id = "test.c5.entry",
        Name = "Entry",
        Version = version,
        Sides = ModSides.Client,
        Line = ModFrameworkLine.Compose(new ModFrameworkFacts { PluginId = "test.c5.entry", Version = version, Sides = ModSides.Client }),
        UpdateAvailable = update,
        ServerHalfMissing = serverHalfMissing,
        DeclaredPeers = peers,
    };

    private static CompatDeclaration Declaration(
        string version, string? peers, NetworkCompatibilityLevel level = NetworkCompatibilityLevel.ServerMustHave,
        NetworkVersionRule rule = NetworkVersionRule.Exact, bool ruleDeclared = false) =>
        new(version, level, rule, ruleDeclared, peers);

    private static void Known(string id, string name, string version, CompatDeclaration? compat)
    {
        ModRegistry.NoteLoading(id, name, version, Array.Empty<string>(), null, compat);
        ModRegistry.NoteLoaded(id);
        // The inventory's version of the plugin (the chainloader's, live).
        Handshake.Provide(id, version);
    }

    /// <summary>The list's cell row for <paramref name="id"/> as the readback lists it.</summary>
    private static JsonElement ListRow(JsonDocument doc, string id)
    {
        string key = ModsListLayout.RowKey(id);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("key", out var k) && k.GetString() == key) return row;
        }

        throw new InvalidOperationException("no list row " + key);
    }

    private static JsonElement? Cell(JsonElement row, string key)
    {
        if (!row.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) return null;
        foreach (var cell in cells.EnumerateArray())
            if (cell.TryGetProperty("key", out var k) && k.GetString() == key) return cell;
        return null;
    }

    private static JsonDocument Widgets(string pane) =>
        JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets(pane)));

    private static JsonElement? Extra(JsonDocument doc, string key, Func<JsonElement, bool>? where = null)
    {
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value)) continue;
            if (where == null || where(value)) return value;
        }

        return null;
    }
}
