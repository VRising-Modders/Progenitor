using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// AUTOUPDATE1 (M6.1): the Install tab's update list, Simple and Advanced alike. Pinned above the column header while a
/// mod Progenitor installed has a newer version (and <b>Update mods</b> is not Off): a band <c>Updates · Checked 14:05 ·
/// 2 updates</c> with <b>Update all</b>, then one row per mod (at most <see cref="MaxUpdateRows"/>; <b>Update all</b>
/// covers the rest) with <b>Update</b> and <b>Keep 1.2.0</b>, and kept mods with <b>Unpin</b>. The buttons run
/// <see cref="ClientUpdates"/>, which goes through the same install pipeline as a listing's own Update.
/// </summary>
internal sealed partial class InstallModsPane
{
    internal const string UpdatesBandKey = "install-updates";
    internal const string UpdateRowPrefix = "install-update:";
    internal const string CellUpdatesText = "updates-text";
    internal const string CellUpdateAll = "update-all";
    internal const string CellUpdateText = "update-text";
    internal const string CellUpdateOne = "update";
    internal const string CellUpdateKeep = "keep";
    internal const int MaxUpdateRows = 4;
    internal const string UpdateResultKey = "install-update-result";

    /// <summary>The Install tab's line for the last automatic updates: <c>Updated automatically at 14:05: …</c>.</summary>
    internal static string AutoResultLine(UpdateNotice n) =>
        "Updated automatically at " + n.Utc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + ": " + n.Detail;

    private IUiHandle? _updatesBand;
    private readonly List<(string Package, IUiHandle Row)> _updateRows = new();
    private string _updatesBuiltFor = "";

    /// <summary>What the update list last built from (the pane rebuilds when <see cref="UpdatesStamp"/> moves).</summary>
    internal string UpdatesBuiltFor => _updatesBuiltFor;

    private static int _stampVersion = -1;
    private static string _stampRun = "";
    private static long _stampSlowAt;
    private static string _stampSlow = "";
    private static string _stamp = "";

    /// <summary>
    /// The report version, the run state, the mode and what the connected server blocks: a change means the list needs a
    /// rebuild. Read every frame while the Install tab shows, so it is cached: the version and run state are compared each
    /// call, the mode and the connected server's rows at most once a second.
    /// </summary>
    internal static string UpdatesStamp()
    {
        int version = ClientUpdates.Version;
        string run = ClientUpdates.RunState;
        long now = Environment.TickCount64;
        bool slowDue = now - _stampSlowAt >= 1000 || _stampSlowAt == 0;
        if (version == _stampVersion && string.Equals(run, _stampRun, StringComparison.Ordinal) && !slowDue) return _stamp;
        if (slowDue)
        {
            _stampSlowAt = now;
            var blocked = new System.Text.StringBuilder(UpdateSettings.Word(ClientUpdates.Mode));
            foreach (var i in ClientUpdates.Report.Items)
            {
                string why = ClientUpdates.BlockedReason(i);
                if (why.Length > 0) blocked.Append('|').Append(i.Package).Append(':').Append(why);
            }
            _stampSlow = blocked.ToString();
        }

        _stampVersion = version;
        _stampRun = run;
        var last = ClientAutoUpdate.Notice ?? ClientAutoUpdate.LastSeenNotice;
        _stamp = version + "/" + run + "/" + _stampSlow + "/" + (last?.Utc.Ticks ?? 0) + "/p" + SelfUpdates.Version;
        return _stamp;
    }

    internal static string UpdateAllCaption(string runState, int count) => runState switch
    {
        "preparing" => "Checking…",
        "confirming" => "Waiting…",
        "applying" => "Updating…",
        _ => count == 1 ? "Update" : "Update all",
    };

    /// <summary>The band's cells. Game-free.</summary>
    internal static List<CellSpec> UpdatesBandCells(string header, string caption) => new()
    {
        new CellSpec(CellKind.Text, CellUpdatesText).With(text: header, width: CellWidth.Fill(160f), tone: CellTone.Pending),
        new CellSpec(CellKind.Spacer, CellGap),
        new CellSpec(CellKind.Button, CellUpdateAll).With(text: caption, align: CellAlign.Right, skin: CellSkin.KeyCap),
    };

    /// <summary>One mod's row cells. Game-free.</summary>
    internal static List<CellSpec> UpdateRowCells(UpdateItem item, string blocked = "")
    {
        blocked ??= "";
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellUpdateText).With(
                text: item.Pinned ? (item.Name + " kept at " + item.PinnedAt + " · " + item.Latest + " available")
                    : blocked.Length > 0 ? item.Line + " · not now: " + blocked : item.Line,
                width: CellWidth.Fill(160f),
                tone: item.Pinned ? CellTone.Muted : CellTone.Normal),
            new CellSpec(CellKind.Spacer, CellGap),
        };
        if (!item.Pinned && blocked.Length == 0)
        {
            cells.Add(new CellSpec(CellKind.Button, CellUpdateOne).With(text: "Update", align: CellAlign.Right, skin: CellSkin.KeyCap));
            cells.Add(new CellSpec(CellKind.Spacer, CellGap));
        }

        cells.Add(new CellSpec(CellKind.Button, CellUpdateKeep).With(
            text: item.Pinned ? "Unpin" : "Keep " + item.Installed, align: CellAlign.Right, skin: CellSkin.KeyCap));
        return cells;
    }

    /// <summary>The rows the list shows: unpinned first, then kept, at most <see cref="MaxUpdateRows"/>.</summary>
    internal static List<UpdateItem> UpdateRowsShown(UpdateReport report)
    {
        var shown = new List<UpdateItem>();
        foreach (var i in report.Items)
        {
            if (shown.Count >= MaxUpdateRows) break;
            shown.Add(i);
        }

        return shown;
    }

    /// <summary>Build the band and its rows (called by <c>Build</c> among the pinned top rows).</summary>
    private void BuildUpdateRows(IUiPanel panel)
    {
        _updatesBand = null;
        _updateRows.Clear();
        _updatesBuiltFor = UpdatesStamp();
        // AUTOUPDATE5: Progenitor's own row follows Update Progenitor, not Update mods.
        BuildProgenitorRow(panel);
        if (ClientUpdates.Mode == UpdateMode.Off) return;
        var report = ClientUpdates.Report;
        // AUTOUPDATE2: what the automatic updates did (names, versions, reasons): the title screen says only how many.
        var last = ClientAutoUpdate.Notice ?? ClientAutoUpdate.LastSeenNotice;
        if (last != null && last.Detail.Length > 0)
        {
            panel.CellRow(new CellRowSpec(UpdateResultKey, new List<CellSpec>
            {
                new CellSpec(CellKind.Text, CellUpdateText).With(text: AutoResultLine(last), width: CellWidth.Fill(160f),
                    tone: last.Problem ? CellTone.Pending : CellTone.Muted),
            }, height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset));
        }

        if (report.Items.Count == 0) return;
        string header = ClientUpdates.HeaderText();
        int more = report.Items.Count - Math.Min(report.Items.Count, MaxUpdateRows);
        if (more > 0) header += " · " + more + " more not shown";
        var band = panel.CellRow(new CellRowSpec(UpdatesBandKey,
            UpdatesBandCells(header, UpdateAllCaption(ClientUpdates.RunState, report.Count)),
            height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, band: true));
        band.OnClick(CellUpdateAll, () => PressUpdateAll());
        _updatesBand = band;

        foreach (var item in UpdateRowsShown(report))
        {
            string package = item.Package;
            string blocked = ClientUpdates.BlockedReason(item);
            var row = panel.CellRow(new CellRowSpec(UpdateRowPrefix + package, UpdateRowCells(item, blocked),
                height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset));
            if (!item.Pinned && blocked.Length == 0) row.OnClick(CellUpdateOne, () => PressUpdateOne(package));
            row.OnClick(CellUpdateKeep, () => PressKeep(package));
            _updateRows.Add((package, row));
        }
    }

    /// <summary>AUTOUPDATE5: Progenitor's own entry in the update list.</summary>
    internal const string ProgenitorRowKey = "install-update-progenitor";

    /// <summary>
    /// AUTOUPDATE5: the Progenitor row's cells. Game-free. <c>Progenitor 2026.10.12 → 2026.10.14</c>, then how it applies
    /// (automatically, or why not), with <b>Update</b> when a click may install it.
    /// </summary>
    internal static List<CellSpec> ProgenitorRowCells(SelfOffer offer, bool busy)
    {
        string how = offer.Auto ? "installs automatically at the title screen" : offer.NotAutoReason;
        if (offer.FailedBefore.Length > 0) how += (how.Length > 0 ? " · " : "") + offer.FailedBefore + " failed before";
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellUpdateText).With(text: offer.Line + (how.Length > 0 ? " · " + how : ""), width: CellWidth.Fill(160f),
                tone: offer.Kind is SelfInstallKind.ModManager or SelfInstallKind.Development or SelfInstallKind.Changed ? CellTone.Muted : CellTone.Normal),
        };
        if (offer.Clickable)
        {
            cells.Add(new CellSpec(CellKind.Spacer, CellGap));
            cells.Add(new CellSpec(CellKind.Button, CellUpdateOne).With(text: busy ? "Updating…" : "Update", align: CellAlign.Right, skin: CellSkin.KeyCap));
        }

        return cells;
    }

    private void BuildProgenitorRow(IUiPanel panel)
    {
        var offer = SelfUpdates.Offer;
        if (!offer.Available) return;
        var row = panel.CellRow(new CellRowSpec(ProgenitorRowKey, ProgenitorRowCells(offer, SelfUpdates.Busy),
            height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset));
        if (offer.Clickable) row.OnClick(CellUpdateOne, () => PressUpdateProgenitor());
    }

    /// <summary>AUTOUPDATE5: the Progenitor row's <b>Update</b> (bridge: <c>auto_update self_apply</c>).</summary>
    internal string PressUpdateProgenitor()
    {
        SelfUpdates.Begin(SelfUpdates.Offer, "install-tab", byClick: true, out string message);
        RequestDetailRebuild();
        return message;
    }

    /// <summary><b>Update all</b> (bridge: <c>widget_click label=install-updates/update-all</c>).</summary>
    internal string PressUpdateAll()
    {
        var packages = new List<string>();
        foreach (var i in ClientUpdates.Report.Items) if (!i.Pinned) packages.Add(i.Package);
        ClientUpdates.BeginUpdate(packages, "update-all", confirm: true, ClientUpdates.ShowOutcome, out string message);
        RequestDetailRebuild();
        return message;
    }

    /// <summary>One row's <b>Update</b>.</summary>
    internal string PressUpdateOne(string package)
    {
        ClientUpdates.BeginUpdate(new[] { package }, "update", confirm: true, ClientUpdates.ShowOutcome, out string message);
        RequestDetailRebuild();
        return message;
    }

    /// <summary>A row's <b>Keep x</b> / <b>Unpin</b>.</summary>
    internal string PressKeep(string package)
    {
        var item = ClientUpdates.Report.Find(package);
        bool pin = item != null && !item.Pinned;
        ClientUpdates.SetPinned(package, pin, out string message);
        return message;
    }

    /// <summary>The band's readback: <c>updates {header, count, button, rows[]}</c>.</summary>
    private Dictionary<string, object?> UpdatesBandWidget()
    {
        var report = ClientUpdates.Report;
        var rows = new List<object?>();
        foreach (var i in UpdateRowsShown(report)) rows.Add(i.Describe());
        return new Dictionary<string, object?>
        {
            ["updates"] = new Dictionary<string, object?>
            {
                ["header"] = ClientUpdates.HeaderText(),
                ["count"] = report.Count,
                ["button"] = UpdateAllCaption(ClientUpdates.RunState, report.Count),
                ["runState"] = ClientUpdates.RunState,
                ["rows"] = rows,
                ["total"] = report.Items.Count,
            },
        };
    }

    /// <summary>One update row's readback: <c>update {…}</c>.</summary>
    private Dictionary<string, object?>? UpdateRowWidget(string key)
    {
        if (!key.StartsWith(UpdateRowPrefix, StringComparison.Ordinal)) return null;
        var item = ClientUpdates.Report.Find(key.Substring(UpdateRowPrefix.Length));
        return item == null ? null : new Dictionary<string, object?> { ["update"] = item.Describe() };
    }
}
