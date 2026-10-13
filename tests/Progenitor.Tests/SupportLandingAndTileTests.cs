using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Progenitor.Support;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The Mods tab's landing model, the title Collect logs tile's game-free half, and the mod-agnostic bundle name.</summary>
public sealed class SupportLandingAndTileTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;

    public SupportLandingAndTileTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "progenitor-landing-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "game", "BepInEx");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SupportActions.ResetForTests();
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    // ---- landing ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("7 mods found on this client.", "Server mod list unavailable. Known requirements are shown where available.",
        "Your mods, in one place. 7 mods found on this client · server mod list unavailable (known requirements shown)")]
    [InlineData("7 mods found on this client. 1 more reported; loading is unconfirmed. The list is partial.",
        "Server mod list received (12 entries). Server activity is not reported.",
        "Your mods, in one place. 7 mods found on this client (1 more reported, loading unconfirmed; list partial) · server mod list received (12 entries; activity not reported)")]
    [InlineData("1 mod found on this client.", "", "Your mods, in one place. 1 mod found on this client")]
    [InlineData("", "", "Your mods, in one place.")]
    [InlineData("3 mods found on this client.", "MOD list from the server.", "Your mods, in one place. 3 mods found on this client · MOD list from the server")]
    public void The_subtitle_condenses_both_summaries(string local, string server, string expected) =>
        Assert.Equal(expected, ModsLanding.Subtitle(local, server));

    [Fact]
    public void The_newest_bundle_lookup_is_cached_for_ten_seconds_and_cleared_by_a_collect_or_another_folder()
    {
        string folder = Path.Combine(_root, "progenitor-support");
        string other = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        long now = 1_000_000;
        SupportActions.ClockMs = () => now;
        string chosen = folder;
        SupportActions.Folder = () => chosen;
        SupportActions.FallbackFolder = () => null;
        Assert.Equal(SupportActions.NoneYet, SupportActions.NewestLine());

        File.WriteAllText(Path.Combine(folder, "vrising-mod-logs-client-20261010-000000.zip"), "z");
        now += 5_000;
        Assert.Equal(SupportActions.NoneYet, SupportActions.NewestLine()); // cached
        now += 5_001;
        Assert.StartsWith("vrising-mod-logs-client-20261010-000000.zip", SupportActions.NewestLine(), StringComparison.Ordinal);

        // Another folder (the Save bundles in setting changed) reads at once.
        chosen = other;
        Assert.Equal(SupportActions.NoneYet, SupportActions.NewestLine());
        File.WriteAllText(Path.Combine(other, "vrising-mod-logs-client-20261010-000001.zip"), "z");
        Assert.Equal(SupportActions.NoneYet, SupportActions.NewestLine());
        // A finished collect clears it.
        SupportActions.Finish(new SupportBundleResult { Ok = false, Error = "x" });
        Assert.StartsWith("vrising-mod-logs-client-20261010-000001.zip", SupportActions.NewestLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_names_and_their_temp_files_are_recognized_by_one_rule()
    {
        Assert.True(SupportBundle.IsBundleName("vrising-mod-logs-abc.zip.tmp", ".zip.tmp"));
        Assert.True(SupportBundle.IsBundleName("progenitor-logs-abc.zip.tmp", ".zip.tmp"));
        Assert.False(SupportBundle.IsBundleName("vrising-mod-logs-abc.zip.tmp"));
        Assert.False(SupportBundle.IsBundleName("other.zip.tmp", ".zip.tmp"));
        string folder = Path.Combine(_base, "mixed");
        Directory.CreateDirectory(folder);
        foreach (string n in new[] { "vrising-mod-logs-a.zip", "progenitor-logs-b.zip", "notes.zip", "vrising-mod-logs-c.zip.tmp" })
            File.WriteAllText(Path.Combine(folder, n), "z");
        Assert.Equal(new[] { "progenitor-logs-b.zip", "vrising-mod-logs-a.zip" },
            SupportBundle.BundleFiles(folder).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "vrising-mod-logs-c.zip.tmp" }, SupportBundle.BundleFiles(folder, ".zip.tmp").Select(f => f.Name).ToArray());
    }

    [Fact]
    public void The_tile_copies_a_layout_size_only_from_an_axis_that_does_not_stretch()
    {
        Assert.False(TitleLogsTile.Stretched(0.5f, 0.5f));
        Assert.True(TitleLogsTile.Stretched(0f, 1f));
        Assert.Equal(52f, TitleLogsTile.LayoutAsk(52f, 48f, stretched: true));  // the template's own LayoutElement wins
        Assert.Equal(48f, TitleLogsTile.LayoutAsk(-1f, 48f, stretched: false)); // else its rect size
        Assert.Equal(-1f, TitleLogsTile.LayoutAsk(0f, 300f, stretched: true));  // never a stretched rect's size
    }

    private static string[] Keys(CellRowSpec row) =>
        row.Cells.Where(c => c.Text.Length > 0 || c.Key.EndsWith("-note", StringComparison.Ordinal)).Select(c => c.Key).ToArray();

    [Fact]
    public void The_tiles_are_two_lines_of_two_bordered_tiles_with_stable_keys()
    {
        var (top, topNotes) = ModsLanding.TopRow(adminHere: true);
        Assert.Equal(ModsLanding.RowTop, top.Key);
        Assert.Equal(ModsLanding.RowTopNotes, topNotes.Key);
        Assert.Equal(new[] { ModsLanding.CellYourSettings, ModsLanding.CellServerSettings }, Keys(top));
        Assert.Equal(new[] { ModsLanding.CellYourSettingsNote, ModsLanding.CellServerSettingsNote }, Keys(topNotes));
        Assert.Equal(ModsLanding.YourSettingsCaption, top.Cells.First(c => c.Key == ModsLanding.CellYourSettings).Text);
        Assert.Equal(ModsLanding.ServerSettingsAdminNote, topNotes.Cells.First(c => c.Key == ModsLanding.CellServerSettingsNote).Text);
        Assert.Equal(ModsLanding.ServerSettingsJoinNote,
            ModsLanding.TopRow(adminHere: false).Note.Cells.First(c => c.Key == ModsLanding.CellServerSettingsNote).Text);
        Assert.Equal(CellSectionEdge.First, top.TileEdge);
        Assert.Equal(CellSectionEdge.Last, topNotes.TileEdge);
        Assert.Equal(0f, topNotes.GapAbove); // the note row joins its head: one box

        var (bottom, bottomNotes) = ModsLanding.BottomRow("vrising-mod-logs-client-20261010-142233.zip · 2 min ago");
        Assert.Equal(new[] { ModsLanding.CellPreferences, ModsLanding.CellCollect }, Keys(bottom));
        Assert.Equal(new[] { ModsLanding.CellPreferencesNote, ModsLanding.CellCollectNote }, Keys(bottomNotes));
        // All four tiles are the same native-styled key-cap button, captions without the link's ».
        foreach (var (row, key) in new[] { (top, ModsLanding.CellYourSettings), (top, ModsLanding.CellServerSettings),
                     (bottom, ModsLanding.CellPreferences), (bottom, ModsLanding.CellCollect) })
        {
            var button = row.Cells.First(c => c.Key == key);
            Assert.Equal(CellKind.Button, button.Kind);
            Assert.Equal(CellSkin.KeyCap, button.Skin);
            Assert.DoesNotContain("»", button.Text, StringComparison.Ordinal);
        }
        Assert.Equal(new[] { "Your settings", "Server settings", "Preferences" },
            new[] { ModsLanding.YourSettingsCaption, ModsLanding.ServerSettingsCaption, ModsLanding.PreferencesCaption });
        Assert.Equal(ModsLanding.CollectDescription, ModsLanding.BottomRow().Note.Cells.First(c => c.Key == ModsLanding.CellCollectNote).Text);
        var collect = bottom.Cells.First(c => c.Key == ModsLanding.CellCollect);
        Assert.Equal(CellSkin.KeyCap, collect.Skin);
        Assert.Equal("Collect logs", collect.Text);
        // The Collect logs tile's second line is the Last bundle line, one line, ellipsized at the tile.
        var collectNote = bottomNotes.Cells.First(c => c.Key == ModsLanding.CellCollectNote);
        Assert.Equal("vrising-mod-logs-client-20261010-142233.zip · 2 min ago", collectNote.Text);
        Assert.Equal(CellWrap.Ellipsis, collectNote.Wrap);
        Assert.Equal(CellTone.Muted, collectNote.Tone);
        Assert.DoesNotContain(bottomNotes.Cells, c => c.Text == "Collect logs for a help post");
        Assert.True(bottom.GapAbove > 0f); // a gap between the two lines of tiles
        Assert.Equal(new[] { 1, 2 }, top.Cells.Select(c => c.Tile).Distinct().ToArray());
        Assert.Equal(new[] { 3, 4 }, bottomNotes.Cells.Select(c => c.Tile).Distinct().ToArray());
        Assert.Equal(4, ModsLanding.Points.Count);
        Assert.StartsWith("Choose a mod on the left", ModsLanding.Footer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1248f)] // the in-game detail pane
    [InlineData(1108f)]
    [InlineData(700f)]
    public void Both_tiles_of_a_line_are_equally_wide_and_the_head_and_note_rows_frame_the_same_box(float rowWidth)
    {
        foreach (var (head, note) in new[] { ModsLanding.TopRow(true), ModsLanding.BottomRow("a very long last bundle line " + new string('x', 200)) })
        {
            var headPlaced = CellLayout.Resolve(rowWidth, head.Cells, head.RightInset);
            var notePlaced = CellLayout.Resolve(rowWidth, note.Cells, note.RightInset);
            var headSpans = CellTiles.Spans(headPlaced, head.Cells);
            var noteSpans = CellTiles.Spans(notePlaced, note.Cells);
            Assert.Equal(2, headSpans.Count);
            Assert.Equal(2, noteSpans.Count);
            float expected = (rowWidth - CellLayout.Gap) / 2f;
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(expected, headSpans[i].Width, 1);
                Assert.Equal(headSpans[i].Left, noteSpans[i].Left, 1);
                Assert.Equal(headSpans[i].Right, noteSpans[i].Right, 1);
            }
            Assert.Equal(-rowWidth / 2f, headSpans[0].Left, 1);
            Assert.Equal(rowWidth / 2f, headSpans[1].Right, 1);
            Assert.Equal(CellLayout.Gap, headSpans[1].Left - headSpans[0].Right, 1);

            // Each link sits a pad inside its tile; both links take the same slot; a long note stays inside its tile.
            var links = headPlaced.Where(p => p.Visible && head.Cells[p.Index].Kind == CellKind.Button).ToArray();
            Assert.Equal(2, links.Length);
            Assert.Equal(links[0].Width, links[1].Width, 1);
            Assert.Equal(headSpans[0].Left + CellTiles.Pad + CellLayout.Gap, links[0].Left, 1);
            var notes = notePlaced.Where(p => p.Visible && note.Cells[p.Index].Key.EndsWith("-note", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, notes.Length);
            Assert.True(notes[0].Right <= noteSpans[0].Right - CellTiles.Pad + .5f);
            Assert.True(notes[1].Right <= noteSpans[1].Right - CellTiles.Pad + .5f);
            Assert.True(notes[0].Width > 0f);
        }
    }

    [Theory]
    [InlineData(1248f)]
    [InlineData(800f)]
    public void The_points_are_a_centered_block_with_their_bullets_in_one_column(float rowWidth)
    {
        float? bulletLeft = null;
        for (int n = 1; n <= ModsLanding.Points.Count; n++)
        {
            var row = ModsLanding.PointRow(n, ModsLanding.Points[n - 1]);
            Assert.Equal(ModsLanding.PointRowKey(n), row.Key);
            var placed = CellLayout.Resolve(rowWidth, row.Cells, row.RightInset);
            var lead = placed[0];
            var bullet = placed[1];
            var text = placed[2];
            var trail = placed[3];
            Assert.Equal(lead.Width, trail.Width, 1); // centered as a block
            Assert.Equal(rowWidth * ModsLanding.PointsFraction, text.Width, 1);
            bulletLeft ??= bullet.Left;
            Assert.Equal(bulletLeft.Value, bullet.Left, 1); // every bullet in one column
            Assert.Equal(ModsLanding.Bullet, row.Cells[1].Text);
            Assert.Equal(CellTone.Muted, row.Cells[2].Tone);
            Assert.Equal(CellWrap.Wrap, row.Cells[2].Wrap);
            Assert.Equal(CellAlign.Left, row.Cells[2].Align);
            Assert.True(row.Cells[2].FontSize < CellLayout.TextFontSize);
            Assert.True(row.FitWrap);
        }
    }

    [Fact]
    public void The_landing_blocks_are_spaced_and_the_bullet_is_a_round_dot_with_a_fallback()
    {
        Assert.Equal(ModsLanding.BlockGap, ModsLanding.TopRow(true).Head.GapAbove);      // points -> first tiles
        Assert.Equal(CellTiles.LineGap, ModsLanding.BottomRow().Head.GapAbove);          // between the two lines of tiles
        var footer = ModsLanding.FooterRow();
        Assert.Equal(ModsLanding.RowFooter, footer.Key);
        Assert.Equal(ModsLanding.BlockGap, footer.GapAbove);                             // last tiles -> footer
        Assert.Equal(ModsLanding.Footer, footer.Cells[0].Text);
        Assert.Equal(CellAlign.Center, footer.Cells[0].Align);
        Assert.InRange(ModsLanding.BlockGap, 12f, 16f);

        Assert.Equal("•", ModsLanding.Bullet);
        var bullet = ModsLanding.PointRow(1, ModsLanding.Points[0]).Cells[1];
        Assert.Equal(CellWrap.Glyph, bullet.Wrap); // drawn as the first form the font has
        Assert.Equal(new[] { "•", "·", "*" }, UiOptionsRules.GlyphCandidates(ModsLanding.Bullet));
    }

    [Fact]
    public void The_Collect_logs_note_is_its_description_then_progress_then_the_outcome_for_ten_seconds()
    {
        Assert.Equal("Collect the logs for a help post to assist with troubleshooting a mod.", ModsLanding.CollectDescription);
        Assert.Equal(ModsLanding.CollectDescription, ModsLanding.CollectNote(0, false, "", "", long.MinValue));
        Assert.Equal("Getting server logs... 1.0 / 2.0 MB", ModsLanding.CollectNote(0, true, "Getting server logs... 1.0 / 2.0 MB", "", 0));
        Assert.Equal(SupportActions.Collecting, ModsLanding.CollectNote(0, true, "", "", 0));
        Assert.Equal("Logs saved to your Desktop (1 KB).",
            ModsLanding.CollectNote(5_000, false, "", "Logs saved to your Desktop (1 KB).\n" + TitleLogsTile.DragLine, 10_000));
        Assert.Equal(ModsLanding.CollectDescription, ModsLanding.CollectNote(10_000, false, "", "Saved x.zip", 10_000));
        Assert.Equal(10_000L, ModsLanding.CollectResultMs);
    }

    // ---- the title tile -----------------------------------------------------------------------------------------

    [Fact]
    public void The_tiles_line_is_centered_over_the_tile_and_kept_inside_the_16_by_9_content()
    {
        // 5120x1440: the 16:9 content is 2560 wide, centered.
        var (left, right) = TitleLogsTile.ContentSpan(5120f, 1440f);
        Assert.Equal(1280f, left);
        Assert.Equal(3840f, right);
        Assert.Equal((0f, 1920f), TitleLogsTile.ContentSpan(1920f, 1080f));
        Assert.Equal((0f, 1600f), TitleLogsTile.ContentSpan(1600f, 1200f)); // narrower than 16:9: the whole width

        var row = TitleLogsTile.PlaceLine(1400f, 120f, 560f, null, 1440f / 1080f);
        // A short hover line sits centered on the tile.
        var hover = TitleLogsTile.CenterOverTile(row, tileCenterX: 2000f, textWidth: 300f, left, right);
        Assert.Equal(1850f, hover.Left);
        Assert.Equal(300f, hover.Width);
        Assert.Equal(row.Bottom, hover.Bottom);   // the height above the row (under EXIT) is kept
        Assert.Equal(row.MaxLines, hover.MaxLines);
        // Near the content's left edge it moves right to stay inside; the measured width is kept (no cut for room it has).
        var nearLeft = TitleLogsTile.CenterOverTile(row, tileCenterX: 1300f, textWidth: 1000f, left, right);
        Assert.Equal(left, nearLeft.Left);
        Assert.Equal(1000f, nearLeft.Width);
        // Never wider than the content.
        Assert.Equal(right - left, TitleLogsTile.CenterOverTile(row, 2000f, 9000f, left, right).Width);
        var nearRight = TitleLogsTile.CenterOverTile(row, tileCenterX: 3830f, textWidth: 300f, left, right);
        Assert.Equal(right - 300f, nearRight.Left);
        // Nothing to center on: the row-aligned place.
        Assert.Equal(row, TitleLogsTile.CenterOverTile(row, float.NaN, 300f, left, right));
    }

    [Fact]
    public void The_tiles_line_keeps_its_second_line_only_when_it_fits_and_cuts_only_a_line_wider_than_the_room()
    {
        string text = "Logs saved to your Desktop (1.1 MB).\n" + TitleLogsTile.DragLine;
        // Room for both: both lines, as wide as the wider one, no ellipsis.
        var both = TitleLogsTile.FitLines(text, 380f, 520f, available: 2560f, maxLines: 2);
        Assert.Equal(text, both.Text);
        Assert.Equal(2, both.Lines);
        Assert.Equal(520f, both.Width);
        Assert.False(both.Ellipsis);
        // One line of room under EXIT: the first line alone, at its own width.
        var one = TitleLogsTile.FitLines(text, 380f, 520f, 2560f, maxLines: 1);
        Assert.Equal("Logs saved to your Desktop (1.1 MB).", one.Text);
        Assert.Equal(380f, one.Width);
        Assert.False(one.Ellipsis);
        // The second line wider than the room: dropped, the first kept whole.
        var narrow = TitleLogsTile.FitLines(text, 380f, 520f, 400f, 2);
        Assert.Equal(1, narrow.Lines);
        Assert.False(narrow.Ellipsis);
        // Only a first line truly wider than the room is cut.
        var cut = TitleLogsTile.FitLines("x", 900f, 0f, 400f, 2);
        Assert.True(cut.Ellipsis);
        Assert.Equal(400f, cut.Width);
        // A one-line text (the hover line, a failure) stays one line.
        Assert.Equal(1, TitleLogsTile.FitLines(TitleLogsTile.Tooltip, 200f, 0f, 2560f, 2).Lines);
        // The line count under EXIT follows the measured line height.
        Assert.Equal(1, TitleLogsTile.PlaceLine(0f, 0f, 400f, exitBottom: 60f, pxPerUnit: 1f, lineHeight: 28f).MaxLines);
        Assert.Equal(2, TitleLogsTile.PlaceLine(0f, 0f, 400f, exitBottom: 60f, pxPerUnit: 1f, lineHeight: 20f).MaxLines);
    }

    [Fact]
    public void The_tiles_result_is_two_short_lines_after_a_save_and_one_line_otherwise()
    {
        Assert.Equal("Logs saved to your Desktop (863 KB).\nDrag the zip into your #technical-support post.",
            TitleLogsTile.ResultText(true, "vrising-mod-logs-client-20261010-124221.zip", 863 * 1024, "your Desktop", false,
                "Saved vrising-mod-logs-client-20261010-124221.zip (863 KB) and opened its folder. Drag it into your #technical-support post."));
        Assert.Equal("Logs saved to BepInEx/progenitor-support (10 B).\n" + TitleLogsTile.DragLine,
            TitleLogsTile.ResultText(true, "x.zip", 10, "your Desktop", fellBack: true, "Saved x.zip"));
        // A configured folder is named by its last segment, never a full path.
        Assert.Equal("Support bundles", TitleLogsTile.Where("[profile]\\Documents\\Support bundles\\", false));
        Assert.Equal("BepInEx/progenitor-support", TitleLogsTile.Where("BepInEx/progenitor-support", false));
        Assert.Equal("BepInEx/progenitor-support", TitleLogsTile.Where("", false));
        // A failure or refusal: the status's first line alone.
        Assert.Equal("Could not collect logs: disk full",
            TitleLogsTile.ResultText(false, null, 0, null, false, "Could not collect logs: disk full\nmore detail"));
        Assert.Equal("Already collecting logs.", TitleLogsTile.ResultText(false, "old.zip", 5, "your Desktop", false, "Already collecting logs."));
    }

    [Fact]
    public void The_tiles_line_sits_left_on_the_icon_row_just_above_it_and_fits_under_EXIT()
    {
        // 5120x1440: canvas scale 1440/1080; the native row 7 icons wide.
        float s = 1440f / 1080f;
        float iconsLeft = 1110f, iconsTop = 120f, iconsWidth = 420f * s;
        var place = TitleLogsTile.PlaceLine(iconsLeft, iconsTop, iconsWidth, exitBottom: iconsTop + 90f * s, pxPerUnit: s);
        Assert.Equal(iconsLeft, place.Left);                                  // left edge of the native row, not the tile's right
        Assert.Equal(iconsTop + TitleLogsTile.LineGap * s, place.Bottom, 2);   // a small gap above the row
        Assert.Equal(iconsWidth * 2f, place.Width, 2);                         // twice the row
        Assert.Equal(2, place.MaxLines);
        // Little room under EXIT: one line.
        Assert.Equal(1, TitleLogsTile.PlaceLine(iconsLeft, iconsTop, iconsWidth, iconsTop + 30f * s, s).MaxLines);
        // A very wide row is capped; an unreadable one takes the fallback width.
        Assert.Equal(TitleLogsTile.LineMaxWidth * s, TitleLogsTile.PlaceLine(0f, 0f, 2000f * s, null, s).Width, 2);
        Assert.Equal(TitleLogsTile.LineFallbackWidth * s, TitleLogsTile.PlaceLine(0f, 0f, 0f, null, s).Width, 2);
        Assert.Equal(2, TitleLogsTile.PlaceLine(0f, 0f, 100f, null, 1f).MaxLines);
    }

    [Fact]
    public void The_tiles_line_shows_progress_then_the_outcome_for_eight_seconds_and_the_tooltip_on_hover()
    {
        Assert.Equal("", TitleLogsTile.Line(0, hovered: false, running: false, "", "old status", shownUntil: 0));
        Assert.Equal(TitleLogsTile.Tooltip, TitleLogsTile.Line(0, hovered: true, running: false, "", "old status", shownUntil: 0));
        Assert.Equal("Getting server logs... 1.0 / 2.0 MB", TitleLogsTile.Line(0, false, true, "Getting server logs... 1.0 / 2.0 MB", "", 0));
        Assert.Equal(SupportActions.Collecting, TitleLogsTile.Line(0, false, true, "", "", 0));
        Assert.Equal("Saved x.zip", TitleLogsTile.Line(5, true, false, "", "Saved x.zip", shownUntil: 8));
        Assert.Equal(TitleLogsTile.Tooltip, TitleLogsTile.Line(9, true, false, "", "Saved x.zip", shownUntil: 8));
        Assert.Equal(8f, TitleLogsTile.MessageSeconds);
        Assert.True(TitleLogsTile.IsOwnTile(TitleLogsTile.ObjectName));
        Assert.False(TitleLogsTile.IsOwnTile("Discord"));
        Assert.Contains(TitleLogsTile.ObjectName, NativeModsEntry.EnumerateOwnedRootNames());
    }

    [Fact]
    public void The_zip_glyph_is_a_page_with_a_zipper_inside_its_inset()
    {
        int size = ZipGlyph.TextureSize;
        float covered = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float a = ZipGlyph.Alpha(x, y, size);
                Assert.InRange(a, 0f, 1f);
                covered += a;
            }
        float share = covered / (size * size);
        Assert.InRange(share, 0.08f, 0.45f); // a line drawing, not a filled square
        // Corners of the texture are empty (the page stays inside its margins); the cut-off corner is empty too.
        Assert.Equal(0f, ZipGlyph.Alpha(0, 0, size));
        Assert.Equal(0f, ZipGlyph.Alpha(size - 1, size - 1, size));
        Assert.False(ZipGlyph.Inside(0.77f, 0.11f));
        // The left edge, the bottom edge, a zipper tooth and the pull are drawn.
        Assert.True(ZipGlyph.Inside(0.23f, 0.5f));
        Assert.True(ZipGlyph.Inside(0.5f, 0.89f));
        Assert.True(ZipGlyph.Inside(0.47f, 0.12f));
        Assert.True(ZipGlyph.Inside(0.5f - 0.06f, 0.66f));
        Assert.False(ZipGlyph.Inside(0.5f, 0.66f)); // the pull's hole
    }

    [Fact]
    public void A_baked_icon_is_not_borrowed_and_a_separate_plain_tile_is()
    {
        // Every icon one sprite with its glyph baked in (the live row, 2026-10-10): nothing to borrow, the tile is drawn.
        var baked = new List<IReadOnlyList<TitleLogsTile.IconImage>>
        {
            new[] { new TitleLogsTile.IconImage("", true, 1f, 1f) },
            new[] { new TitleLogsTile.IconImage("", true, 1f, 1f) },
        };
        Assert.Null(TitleLogsTile.PickBackground(baked));
        // A raycast-only root over one baked child is still one baked image.
        var rootAndBaked = new List<IReadOnlyList<TitleLogsTile.IconImage>>
        {
            new[] { new TitleLogsTile.IconImage("", false, 1f, 1f), new TitleLogsTile.IconImage("Icon", true, 1f, 1f) },
        };
        Assert.Null(TitleLogsTile.PickBackground(rootAndBaked));
        // A tile image and a smaller glyph image: the tile one, of the first icon that has both.
        var split = new List<IReadOnlyList<TitleLogsTile.IconImage>>
        {
            new[] { new TitleLogsTile.IconImage("", true, 1f, 1f) },
            new[] { new TitleLogsTile.IconImage("Glyph", true, 0.6f, 0.6f), new TitleLogsTile.IconImage("Background", true, 1f, 1f) },
        };
        Assert.Equal((1, 1), TitleLogsTile.PickBackground(split));
    }

    [Fact]
    public void The_drawn_tile_is_a_rounded_rectangle_sliced_at_its_corner()
    {
        int size = RoundedTile.TextureSize, r = RoundedTile.Radius;
        Assert.Equal(0f, RoundedTile.Alpha(0, 0, size, r));                 // the corner is cut
        Assert.Equal(0f, RoundedTile.Alpha(size - 1, size - 1, size, r));
        Assert.Equal(1f, RoundedTile.Alpha(size / 2, 0, size, r));           // the straight edges are full
        Assert.Equal(1f, RoundedTile.Alpha(0, size / 2, size, r));
        Assert.Equal(1f, RoundedTile.Alpha(size / 2, size / 2, size, r));
        Assert.Equal(1f, RoundedTile.Alpha(r, r, size, r));                   // inside the corner's circle
        Assert.InRange(RoundedTile.Alpha(2, 2, size, r), 0f, 0.5f);           // outside the arc, near it
        // 9-sliced so the 16 px corner draws at 16 % of the tile: 7.7 units on the live 48-unit tile.
        Assert.Equal(16f / (0.16f * 48f), RoundedTile.SliceMultiplier(48f), 3);
        Assert.Equal(1f, RoundedTile.SliceMultiplier(0f));
        Assert.Equal(48f * TitleLogsTile.CornerShare, r / RoundedTile.SliceMultiplier(48f), 3);
    }

    // ---- the bundle's name and README ---------------------------------------------------------------------------

    private SupportBundleRequest Request() => new()
    {
        Side = "client",
        BepInExRoot = _root,
        NowLocal = new DateTime(2026, 10, 10, 14, 22, 33),
    };

    [Fact]
    public void Bundles_are_named_for_any_mod_and_old_names_still_count()
    {
        Assert.Equal("vrising-mod-logs-client-20261010-142233.zip", SupportBundle.FileNameFor("client", new DateTime(2026, 10, 10, 14, 22, 33)));
        Assert.True(SupportBundle.IsBundleName("progenitor-logs-server-20261001-000000.zip"));
        Assert.True(SupportBundle.IsBundleName("vrising-mod-logs-client-20261001-000000.zip"));
        Assert.False(SupportBundle.IsBundleName("notes.zip"));

        string folder = Path.Combine(_root, "progenitor-support");
        Directory.CreateDirectory(folder);
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 4; i++)
        {
            string old = Path.Combine(folder, "progenitor-logs-client-2026100" + i + "-000000.zip");
            File.WriteAllText(old, "z");
            File.SetLastWriteTimeUtc(old, start.AddHours(i));
        }
        string oldTemp = Path.Combine(folder, "progenitor-logs-abc.zip.tmp");
        File.WriteAllText(oldTemp, "t");
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddHours(-2));

        // The Last bundle line finds an old-named bundle.
        SupportActions.Folder = () => folder;
        Assert.StartsWith("progenitor-logs-client-20261003-000000.zip", SupportActions.NewestLine(), StringComparison.Ordinal);

        // Two new bundles: retention keeps five across both names, oldest old ones go; the old temp file is cleaned.
        var first = SupportBundle.Build(Request());
        var r2 = Request();
        r2.NowLocal = r2.NowLocal.AddSeconds(1);
        var second = SupportBundle.Build(r2);
        Assert.True(first.Ok && second.Ok);
        Assert.StartsWith("vrising-mod-logs-client-", second.FileName, StringComparison.Ordinal);
        var left = SupportBundle.BundleFiles(folder).Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(5, left.Length);
        Assert.DoesNotContain("progenitor-logs-client-20261000-000000.zip", left);
        Assert.Contains("progenitor-logs-client-20261003-000000.zip", left);
        Assert.False(File.Exists(oldTemp));
    }

    [Fact]
    public void The_readme_presents_a_bundle_for_any_mod()
    {
        var result = SupportBundle.Build(Request());
        string readme;
        using (var zip = ZipFile.OpenRead(result.Path))
        using (var r = new StreamReader(zip.GetEntry("README.txt")!.Open(), Encoding.UTF8))
            readme = r.ReadToEnd();
        Assert.StartsWith("V Rising mod support bundle (client)", readme, StringComparison.Ordinal);
        Assert.Contains("help the developer of whichever mod is misbehaving", readme, StringComparison.Ordinal);
        Assert.Contains(SupportBundle.CollectedBy, readme, StringComparison.Ordinal);
        Assert.Contains("HOW TO ASK FOR HELP", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Progenitor's copies", readme, StringComparison.Ordinal);
        Assert.Single(readme.Split('\n'), l => l.Contains("Progenitor (Get help"));
        Assert.DoesNotContain("Progenitor's and BepInEx's logs", SupportActions.CollectHelp, StringComparison.Ordinal);
    }

    // ---- the Logs saved dialog ----------------------------------------------------------------------------------

    private SupportBundleRequest Next(int seconds)
    {
        var r = Request();
        r.NowLocal = r.NowLocal.AddSeconds(seconds);
        return r;
    }

    [Fact]
    public void The_offer_shows_a_button_for_each_usable_link_and_Not_now()
    {
        const string invite = "https://vrisingmods.com/discord";
        const string channel = "https://discord.com/channels/1/2";
        var both = SupportActions.BuildOffer("vrising-mod-logs-client-x.zip", 1153434, "your Desktop", false, invite, channel)!;
        Assert.Equal("Logs saved", both.Title);
        Assert.Equal("vrising-mod-logs-client-x.zip (1.1 MB) is on your Desktop. Post it in the #technical-support forum of the "
            + "V Rising Mod Community Discord so the mod's developer can help.", both.Body);
        Assert.Equal(new[] { ("Join the Discord", invite), ("Open #technical-support", channel) }, both.Links.ToArray());
        Assert.Equal("Not now", both.CancelCaption);

        // The channel button only for a set Discord channel link.
        Assert.Equal(new[] { "Join the Discord" }, SupportActions.BuildOffer("a.zip", 1, "your Desktop", false, invite, "")!.Links.Select(l => l.Caption));
        Assert.Equal(new[] { "Join the Discord" },
            SupportActions.BuildOffer("a.zip", 1, "your Desktop", false, invite, "https://example.invalid/channels/1")!.Links.Select(l => l.Caption));
        // Only https links; nothing usable, no dialog.
        Assert.Equal(new[] { "Open #technical-support" },
            SupportActions.BuildOffer("a.zip", 1, "your Desktop", false, "http://vrisingmods.com/discord", channel)!.Links.Select(l => l.Caption));
        Assert.Null(SupportActions.BuildOffer("a.zip", 1, "your Desktop", false, "", ""));
        // Where it is: a folder by its name, the fallback by its path.
        Assert.Contains(") is in Support bundles. Post it", SupportActions.BuildOffer("a.zip", 1, "[profile]\\Support bundles", false, invite, "")!.Body);
        Assert.Contains(") is in BepInEx/progenitor-support. Post it", SupportActions.BuildOffer("a.zip", 1, "your Desktop", true, invite, "")!.Body);
        // The shipped links: the invite and the channel are both set, so both buttons show.
        Assert.Equal(invite, SupportBundle.DiscordInviteUrl);
        Assert.True(SupportActions.IsChannelUrl(SupportBundle.TechSupportChannelUrl));
    }

    [Fact]
    public void A_save_offers_the_Discord_in_a_dialog_and_opens_nothing_by_itself()
    {
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), "x\n");
        var opened = new List<string>();
        var shown = new List<SupportActions.DiscordOffer>();
        SupportActions.OpenUrl = url => { opened.Add(url); return true; };
        SupportActions.ShowOffer = shown.Add;
        SupportActions.Gather = () => Request();
        SupportActions.Collect();
        Assert.Single(shown);
        Assert.Equal("shown", SupportActions.LastOffer);
        Assert.Empty(opened); // no auto-open
        Assert.DoesNotContain("Opened the", SupportActions.Status, StringComparison.Ordinal);
        Assert.StartsWith("Saved vrising-mod-logs-client-", SupportActions.Status, StringComparison.Ordinal);

        // A link opens only through the dialog's answer, https only.
        Assert.True(SupportActions.OpenOfferLink(shown[0].Links[0].Url));
        Assert.False(SupportActions.OpenOfferLink("file:///C:/x"));
        Assert.Equal(new[] { SupportBundle.DiscordInviteUrl }, opened);
    }

    [Fact]
    public void The_preference_off_or_a_loading_world_or_a_failed_save_shows_no_dialog()
    {
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), "x\n");
        var shown = new List<SupportActions.DiscordOffer>();
        SupportActions.ShowOffer = shown.Add;

        SupportActions.AskToOpenDiscord = () => false;
        SupportActions.Gather = () => Next(1);
        SupportActions.Collect();
        Assert.Empty(shown);
        Assert.Equal("off", SupportActions.LastOffer);
        Assert.StartsWith("Saved ", SupportActions.Status, StringComparison.Ordinal); // the status line still says where

        SupportActions.AskToOpenDiscord = null;
        SupportActions.DialogBlocked = () => SupportActions.DialogBlockedFor("Loading");
        SupportActions.Gather = () => Next(2);
        SupportActions.Collect();
        Assert.Empty(shown);
        Assert.Equal("skipped: a world is loading or joining (Loading)", SupportActions.LastOffer);

        SupportActions.DialogBlocked = null;
        SupportActions.Builder = _ => new SupportBundleResult { Ok = false, Error = "disk full" };
        SupportActions.Gather = () => Next(3);
        SupportActions.Collect();
        Assert.Empty(shown);

        // The timing rule: the title and in game are fine; every join or load state, and the disconnect screen, are not.
        Assert.Null(SupportActions.DialogBlockedFor("Idle"));
        Assert.Null(SupportActions.DialogBlockedFor("Running"));
        Assert.Null(SupportActions.DialogBlockedFor(null));
        foreach (string state in new[] { "Loading", "PreWarmingShaders", "Reconnecting", "Connecting", "Error" })
            Assert.NotNull(SupportActions.DialogBlockedFor(state));
    }

    [Fact]
    public void The_Get_help_page_has_the_offer_preference()
    {
        Assert.Equal("AskToOpenDiscord", SupportActions.AskToOpenDiscordKey);
        Assert.Equal("Offer to open the Discord after saving", SupportActions.AskToOpenDiscordLabel);
        Assert.Equal("Support", SupportActions.SettingsSection);
    }
}
