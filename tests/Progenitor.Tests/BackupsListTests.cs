using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LISTROWS1 (live 2026-10-07, Server rules → Backups → Show): the list's entries were 360-wide buttons that cut
/// "Before restore · Wed, Oct 7, 2026 1:39 A…", and its header was a centered wall of sentences. Pinned here: the row
/// model (kind and date over a muted world age and size), the grouping (Progenitor backups, then wipe archives, each newest
/// first) and its headings, the header's label/value lines, and that no entry line is clipped at the narrowest frame.
/// </summary>
public sealed class BackupsListTests
{
    private static ServerBackupView Entry(string kind, string created, int? age = 34, long size = 82L * 1024 * 1024, bool restorable = true)
    {
        bool archive = kind == WorldBackupEntry.KindArchive;
        return new ServerBackupView
        {
            Id = kind + ":" + created,
            Kind = kind,
            KindLabel = archive ? "Wipe archive" : "Before restore",
            Name = (archive ? "Wipe archive from " : "Before restore of the wipe archive from x, ") + created,
            Created = created,
            AgeDays = age,
            Size = size,
            Restorable = restorable,
            Reason = restorable ? "" : "the save is damaged",
        };
    }

    private static ServerBackupsState State(params ServerBackupView[] entries)
    {
        var s = new ServerBackupsState
        {
            Count = entries.Length,
            ArchiveBytes = 150L * 1024 * 1024,
            BackupBytes = 36L * 1024 * 1024,
            CurrentBytes = 22L * 1024 * 1024,
            FreeBytes = 360L * 1024 * 1024 * 1024,
            Keep = 2,
            RestoreAllowed = true,
            ServerNow = "2026-10-07T09:30:00",
        };
        s.Entries.AddRange(entries);
        return s;
    }

    private const string Archive = WorldBackupEntry.KindArchive;
    private const string Backup = WorldBackupEntry.KindBackup;

    // ---- the rows ---------------------------------------------------------------------------------------

    [Fact]
    public void An_entry_is_the_kind_and_date_over_a_muted_world_age_and_size()
    {
        var e = Entry(Backup, "2026-10-07T01:39:00", age: 34, size: 82L * 1024 * 1024);
        Assert.Equal("Before restore · Wed, Oct 7, 2026 1:39 AM", ServerBackups.EntryTitle(e));
        Assert.Equal("World age: day 34 · 82 MB", ServerBackups.EntryNote(e));

        var odd = Entry(Archive, "not a date", age: null, size: 512, restorable: false);
        Assert.Equal("Wipe archive · date unknown", ServerBackups.EntryTitle(odd));
        Assert.Equal("World age unknown · 512 B · cannot be restored", ServerBackups.EntryNote(odd));
    }

    [Fact]
    public void Progenitor_backups_come_first_then_wipe_archives_each_newest_first_under_headings()
    {
        var s = State(
            Entry(Archive, "2026-10-06T23:37:00"),
            Entry(Archive, "2026-09-30T04:00:00"),
            Entry(Backup, "2026-10-05T10:00:00"),
            Entry(Archive, "unknown"),
            Entry(Backup, "2026-10-07T01:39:00"));
        var shown = ServerBackupsRows.ListedEntries(s);
        Assert.Equal(new[]
        {
            "backup:2026-10-07T01:39:00", "backup:2026-10-05T10:00:00",
            "archive:2026-10-06T23:37:00", "archive:2026-09-30T04:00:00", "archive:unknown",
        }, shown.Select(e => e.Id).ToArray());

        var list = ServerBackupsRows.ListContent(s);
        Assert.Equal(UiDialogShape.Choose, list.Shape);
        Assert.True(list.OptionsAsRows);
        Assert.False(list.OptionsAsButtons);
        Assert.Equal(shown.Select(ServerBackups.EntryTitle).ToArray(), list.Options.ToArray());
        Assert.Equal(shown.Select(ServerBackups.EntryNote).ToArray(), list.OptionNotes.ToArray());
        Assert.Equal(new[] { (0, ServerBackupsRows.BackupsHeading), (2, ServerBackupsRows.ArchivesHeading) },
            list.OptionHeadings.Select(h => (h.Before, h.Text)).ToArray());
        Assert.Equal("Close", list.CancelCaption);

        // Only one kind: one heading.
        var archivesOnly = ServerBackupsRows.ListContent(State(Entry(Archive, "2026-09-30T04:00:00")));
        Assert.Equal(ServerBackupsRows.ArchivesHeading, Assert.Single(archivesOnly.OptionHeadings).Text);
    }

    [Fact]
    public void The_list_keeps_the_newest_ten_and_says_how_many_it_lists()
    {
        var entries = new List<ServerBackupView>();
        for (int i = 1; i <= 12; i++)
            entries.Add(Entry(i % 3 == 0 ? Backup : Archive, new DateTime(2026, 9, i, 12, 0, 0).ToString(ServerBackups.LocalFormat, CultureInfo.InvariantCulture)));
        var s = State(entries.ToArray());
        var shown = ServerBackupsRows.ListedEntries(s);
        Assert.Equal(DialogLayout.MaxListOptions, shown.Count);
        // The two oldest (Sep 1 and Sep 2) are the ones left out.
        Assert.DoesNotContain(shown, e => e.Created.StartsWith("2026-09-01") || e.Created.StartsWith("2026-09-02"));
        var listed = Assert.Single(ServerBackupsRows.ListContent(s).Facts, f => f.Label == ServerBackupsRows.ListedLabel);
        Assert.Equal("the newest 10 of 12 restore points", listed.Value);
    }

    // ---- the header -------------------------------------------------------------------------------------

    [Fact]
    public void The_header_is_label_value_lines_with_the_deletion_in_amber_only_when_something_goes()
    {
        var s = State(Entry(Backup, "2026-10-07T01:39:00"), Entry(Archive, "2026-10-06T23:37:00"));
        var facts = ServerBackupsRows.ListContent(s).Facts;
        Assert.Equal(new[] { ServerBackupsRows.DiskLabel, ServerBackupsRows.KeptLabel }, facts.Select(f => f.Label).ToArray());
        Assert.Equal("Restore points 186 MB · current world 22 MB · free 360 GB", facts[0].Value);
        Assert.Equal("newest 2 backups made before a restore; wipe archives are never deleted", facts[1].Value);
        Assert.All(facts, f => Assert.Equal(DialogFactTone.Normal, f.Tone));

        s.PruneNext.Add(new ServerBackupView { Id = "backup:old", Name = "Before restore of the wipe archive from x, Wed, Oct 1, 2026 9:00 AM" });
        s.Keep = 0;
        s.FreeBytes = -1;
        facts = ServerBackupsRows.ListContent(s).Facts;
        var deletes = Assert.Single(facts, f => f.Label == ServerBackupsRows.DeletesLabel);
        Assert.Equal("Before restore of the wipe archive from x, Wed, Oct 1, 2026 9:00 AM", deletes.Value);
        Assert.Equal(DialogFactTone.Warn, deletes.Tone);
        Assert.Equal("Restore points 186 MB · current world 22 MB", facts[0].Value);
        Assert.Equal("every backup made before a restore; wipe archives are never deleted", facts[1].Value);

        s.RestoreAllowed = false;
        s.RestoreRefusal = "this is a hosted private game.";
        var refused = Assert.Single(ServerBackupsRows.ListContent(s).Facts, f => f.Label == ServerBackupsRows.RefusedLabel);
        Assert.Equal("not available here: this is a hosted private game.", refused.Value);
        Assert.Equal(DialogFactTone.Warn, refused.Tone);
    }

    [Fact]
    public void An_empty_list_is_a_notice_with_the_same_header()
    {
        var s = State();
        var c = ServerBackupsRows.ListContent(s);
        Assert.Equal(UiDialogShape.Notice, c.Shape);
        Assert.StartsWith("No restore points yet.", c.Body);
        Assert.Equal(new[] { ServerBackupsRows.DiskLabel, ServerBackupsRows.KeptLabel }, c.Facts.Select(f => f.Label).ToArray());
    }

    // ---- the layout ---------------------------------------------------------------------------------------

    /// <summary>The live failures and the longest lines the model can make, at the narrowest frame a wide dialog gets.</summary>
    [Fact]
    public void No_entry_line_is_truncated_at_the_minimum_dialog_width()
    {
        // The runtime never draws a dialog narrower than the ordinary frame (DialogLayout.FrameWidthFor).
        float minRow = DialogLayout.ContentRowWidth(DialogLayout.FrameWidthFor(DialogLayout.WideFrameWidth, 1f));
        Assert.Equal(DialogLayout.ContentRowWidth(DialogLayout.FrameWidth), minRow);

        var s = State(
            Entry(Backup, "2026-10-07T01:39:00"),
            Entry(Archive, "2026-10-06T23:37:00"),
            // The widest date: a two-digit day and hour, PM.
            Entry(Backup, "2026-09-30T12:59:00", age: 9999, size: 999L * 1024 * 1024 + 900 * 1024, restorable: false),
            Entry(Archive, "2026-12-31T23:59:00", age: null, size: 1023L * 1024 * 1024 * 1024, restorable: false));
        var list = ServerBackupsRows.ListContent(s);
        Assert.Contains("Before restore · Wed, Oct 7, 2026 1:39 AM", list.Options);
        Assert.Contains("Wipe archive · Tue, Oct 6, 2026 11:37 PM", list.Options);
        for (int i = 0; i < list.Options.Count; i++)
        {
            Assert.True(DialogLayout.OptionRowFits(list.Options[i], list.OptionNote(i), minRow),
                "clipped at " + minRow + ": " + list.Options[i] + " / " + list.OptionNote(i));
            // The same check the runtime's text writer makes: the clip changes nothing.
            float width = DialogLayout.OptionTextWidth(minRow) - 4f;
            Assert.Equal(list.Options[i], MenuPanelLayout.ClipLabel(list.Options[i], width, CellLayout.TwoLineTitleFontSize));
            Assert.Equal(list.OptionNote(i), MenuPanelLayout.ClipLabel(list.OptionNote(i), width, CellLayout.TwoLineNoteFontSize));
        }

        // The old 360-wide option button cut the first live line.
        string old = "Before restore · Wed, Oct 7, 2026 1:39 AM · day 34 · 82 MB";
        Assert.True(DialogLayout.CaptionWidth(old) > DialogLayout.ListButtonWidth);
        // And the header's longest value fits its column without the four-line cap.
        var facts = DialogFacts.Clean(list.Facts);
        float valueWidth = DialogFacts.ValueWidth(DialogFacts.LabelWidth(facts, minRow), minRow);
        Assert.All(facts, f => Assert.True(DialogFacts.ValueLines(f.Value, valueWidth) < DialogLayout.MaxFactLines, f.ToString()));
    }

    [Fact]
    public void A_row_option_is_a_full_width_clickable_two_line_list_row()
    {
        float row = DialogLayout.ContentRowWidth(DialogLayout.FrameWidth);
        var spec = DialogLayout.OptionRowSpec(UiDialogs.OptionRow(3), UiDialogs.OptionKey(3), "Wipe archive · Tue, Oct 6, 2026 11:37 PM", "World age: day 6 · 22 MB", row);
        Assert.Equal("dialog-option-3", spec.Key);
        Assert.True(spec.TwoLine);
        Assert.Equal(CellLayout.TwoLineRowHeight, spec.Height);
        Assert.False(spec.Plain);
        // A list row has its whole-row click target from birth, so click_at {row} reaches it.
        Assert.True(CellRowSpec.RowClickAtBuild(spec, header: false));
        var text = spec.Find(UiDialogs.OptionKey(3))!;
        Assert.Equal(CellWrap.TwoLine, text.Wrap);
        Assert.Equal("Wipe archive · Tue, Oct 6, 2026 11:37 PM\nWorld age: day 6 · 22 MB", text.Text);
        // The text takes the whole row but the two insets.
        float textWidth = DialogLayout.OptionTextWidth(row);
        Assert.InRange(textWidth, row - 2 * DialogLayout.OptionRowInset - 2 * CellLayout.Gap - 0.5f, row - 2 * DialogLayout.OptionRowInset + 0.5f);
        // Wider frames give wider text.
        Assert.True(DialogLayout.OptionTextWidth(DialogLayout.ContentRowWidth(DialogLayout.WideFrameWidth)) > DialogLayout.OptionTextWidth(row));
    }
}
