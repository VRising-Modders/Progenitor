using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// AUTOUPDATE3 (docs/auto-update.md, Server settings): the Server tab's <b>Updates</b> group, shown only to an admin of the
/// joined server: the mode, major versions, Restart when empty with its quiet period, the pre-update world backup with its
/// keep count and its "skip if newer than" hours, the status line with <b>Check now</b>, <b>Restart now</b> (while updates
/// wait for a restart) and <b>Cancel restart</b> (during its warning). The values are the server's <c>[AutoUpdate]</c> section,
/// edited over the admin channel (<c>'PrJa'</c>) with its authorization, rate limit, lock list (group <c>updates</c>) and audit
/// line. Game-free over <see cref="JoinAdminRows"/>' seams.
/// </summary>
internal static class ServerUpdatesRows
{
    internal const string ModeLabel = "Update server mods";
    internal const string ModeHelp =
        "Automatic: the server applies updates of the mods Progenitor installed on it: a server-only mod that hot-loads updates at "
        + "once, one that players also need waits until the server is empty, one that needs a restart waits for the next start. "
        + "Notify: admins are told; nothing is applied. Off: no checks. Old versions are kept; one that fails to load is rolled back. "
        + "Automatic installs the way a remote install does, so with remote installs off on the server ([RemoteInstall] Enabled) it acts as Notify.";

    internal const string MajorsLabel = "Major versions";
    internal const string MajorsHelp = "Automatic also applies a new major version of a mod (2.0.0 over 1.4.2); Notify only reports it. Mods only: Progenitor itself uses date versions and follows [AutoUpdate] Progenitor.";

    internal const string RestartLabel = "Restart when empty";
    internal const string RestartHelp =
        "With updates waiting for a restart, the server saves and quits the first time nobody is online for the quiet period. "
        + "Progenitor never starts it again: whatever restarts it after it quits (a service, a hosting panel, a script) must be named "
        + "in the server's progenitor.cfg, [AutoUpdate] HostRestarts, or this stays off.";

    internal const string EmptyLabel = "Empty for";
    internal const string EmptyHelp = "How long nobody may be online before Restart when empty acts, and before an update players need is applied.";

    internal const string BackupLabel = "Back up the world before updating";
    internal const string BackupHelp =
        "One world backup per update batch, before it is applied, listed under Backups made before an update. Skipped when a backup "
        + "of the world is newer than the hours below.";

    internal const string KeepLabel = "Keep backups made before an update";
    internal const string KeepHelp = "Counted only among those backups: manual and scheduled backups are never deleted to make room.";

    internal const string SkipLabel = "Skip when a backup is newer than";
    internal const string SkipHelp = "No pre-update backup is made when a manual, scheduled or pre-update backup is newer than this. 0 always makes one.";

    internal const string StatusLabel = "Status";
    internal const string CheckCaption = "Check now";
    internal const string CheckLabel = "Look for updates now";
    internal const string RestartNowCaption = "Restart now";
    internal const string RestartNowLabel = "Apply the updates waiting for a restart";
    internal const string CancelCaption = "Cancel restart";
    internal const string CancelLabel = "Restart countdown";
    internal const string WaitingLabel = "Waiting";
    internal const string RecentLabel = "Recent";
    internal const string OlderServerNote = "This server's Progenitor does not update its mods by itself (it predates server updates).";

    /// <summary>The server's update status from the admin channel's state, or null (not an admin, an older server).</summary>
    internal static JoinAdminUpdates? State => JoinAdminRows.State?.Updates;

    /// <summary>The last answer to Check now, Restart now or Cancel restart (the readback).</summary>
    internal static string LastAnswer { get; private set; } = "";

    private static AdminSettingSpec Spec(string op, AdminSettingKind kind, string label, string help, int min = 0, int max = 0, string units = "") => new()
    {
        SettingId = SettingPlaces.JoinAdminId(op),
        Key = op,
        Label = label,
        Help = help,
        Kind = kind,
        Min = min,
        Max = max,
        Units = units,
        Read = () => JoinAdminRows.Value(op),
        Send = (v, done) => JoinAdminRows.Send(op, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(op)),
    };

    internal static AdminSettingSpec Mode() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.UpdateMods),
        Key = ServerOptionOps.UpdateMods,
        Label = ModeLabel,
        Help = ModeHelp,
        Kind = AdminSettingKind.Choice,
        Choices = UpdateSettings.ModeWords,
        Read = () => JoinAdminRows.Value(ServerOptionOps.UpdateMods),
        Send = (v, done) => JoinAdminRows.Send(ServerOptionOps.UpdateMods, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.UpdateMods)),
    };

    internal static AdminSettingSpec Majors() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.UpdateMajors),
        Key = ServerOptionOps.UpdateMajors,
        Label = MajorsLabel,
        Help = MajorsHelp,
        Kind = AdminSettingKind.Choice,
        Choices = UpdateSettings.MajorWords,
        Read = () => JoinAdminRows.Value(ServerOptionOps.UpdateMajors),
        Send = (v, done) => JoinAdminRows.Send(ServerOptionOps.UpdateMajors, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.UpdateMajors)),
    };

    internal static AdminSettingSpec RestartWhenEmpty() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.UpdateRestartWhenEmpty),
        Key = ServerOptionOps.UpdateRestartWhenEmpty,
        Label = RestartLabel,
        Help = RestartHelp,
        Kind = AdminSettingKind.Toggle,
        Read = () => JoinAdminRows.Value(ServerOptionOps.UpdateRestartWhenEmpty),
        Send = (v, done) => JoinAdminRows.Send(ServerOptionOps.UpdateRestartWhenEmpty, v, done),
        LockedBy = () => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.UpdateRestartWhenEmpty)),
        // The server refuses it without HostRestarts; the row says why before anyone flips it.
        Note = () => State is { ComesBack: false } ? "This server can't start again by itself ([AutoUpdate] HostRestarts is empty), so this stays off."
            : State is { HostRestarts.Length: > 0 } s ? "Restarted by: " + s.HostRestarts + "." : null,
    };

    internal static AdminSettingSpec EmptyMinutes() =>
        Spec(ServerOptionOps.UpdateEmptyMinutes, AdminSettingKind.Number, EmptyLabel, EmptyHelp,
            ServerUpdateSettings.MinEmptyMinutes, ServerUpdateSettings.MaxEmptyMinutes, "minutes");

    internal static AdminSettingSpec Backup() =>
        Spec(ServerOptionOps.UpdateBackup, AdminSettingKind.Toggle, BackupLabel, BackupHelp);

    internal static AdminSettingSpec BackupKeep() =>
        Spec(ServerOptionOps.UpdateBackupKeep, AdminSettingKind.Number, KeepLabel, KeepHelp,
            Progenitor.WorldRestore.WorldBackupRetention.PreUpdateMinKeep, Progenitor.WorldRestore.WorldBackupRetention.PreUpdateMaxKeep, "backups");

    internal static AdminSettingSpec BackupSkipHours() =>
        Spec(ServerOptionOps.UpdateBackupSkipHours, AdminSettingKind.Number, SkipLabel, SkipHelp, 0, ServerUpdateSettings.MaxSkipHours, "hours");

    /// <summary>Every setting row of the group, in page order.</summary>
    internal static IEnumerable<AdminSettingSpec> Settings()
    {
        yield return Mode();
        yield return Majors();
        yield return RestartWhenEmpty();
        yield return EmptyMinutes();
        yield return Backup();
        yield return BackupKeep();
        yield return BackupSkipHours();
    }

    /// <summary>The rows' show-if: an admin of the joined server whose state carries the update status.</summary>
    internal static bool Shown() => JoinAdminRows.IsAdmin() && State != null;

    /// <summary>The status line, or the reason there is none.</summary>
    internal static string StatusLine() => State is { } s ? (s.Line.Length > 0 ? s.Line : "…") : "…";

    internal static bool RestartOffered() => Shown() && State is { RestartPending.Count: > 0, Countdown: < 0 };

    internal static bool CountdownRunning() => Shown() && State is { Countdown: >= 0 };

    /// <summary>Restart now's confirmation (the kit's Yes / No).</summary>
    internal const string RestartConfirmText =
        "Restart the server now to apply the updates waiting for a restart? Players online are warned first and you can cancel "
        + "during the warning. Progenitor does not start the server again: whatever restarts it after it quits (named on the "
        + "Restart when empty row) must, or it stays off until someone starts it.";

    /// <summary>Check now, Restart now, Cancel restart: one admin request. Null when sent, else why not.</summary>
    internal static string? Act(string op)
    {
        var requester = JoinAdminRows.Requester;
        if (requester == null) return "Not connected to a server with Progenitor.";
        string? refusal;
        try
        {
            refusal = requester(new JoinAdminRequest { Op = op }, response =>
                LastAnswer = response == null ? JoinAdminRows.NoAnswerError : response.Ok ? (response.Note.Length > 0 ? response.Note : "Done.") : response.Error);
        }
        catch (Exception e) { refusal = e.Message; }

        if (refusal != null) LastAnswer = refusal;
        return refusal;
    }

    private static string Lines(List<string>? lines) => lines == null || lines.Count == 0 ? "" : string.Join("\n", lines);

    /// <summary>The group's rows (<paramref name="admin"/> draws an admin-channel setting row).</summary>
    internal static void Build(SettingsPageBuilder page, Action<AdminSettingSpec> admin)
    {
        // An admin of a server whose Progenitor predates the auto-updater still sees why the group is empty.
        page.Info(StatusLabel, () => OlderServerNote, refreshSeconds: 0,
                help: "The joined server's Progenitor does not report its own updates.",
                showIf: () => JoinAdminRows.IsAdmin() && State == null)
            .InScope(SettingScope.ServerWide)
            .Uncounted();
        page.Info(StatusLabel, StatusLine, refreshSeconds: 1,
                help: "The last check, what was applied, what waits for the server to be empty or for a restart, and what failed. "
                    + "Every step is in the server's BepInEx/progenitor-auto-update.log.", showIf: Shown)
            .InScope(SettingScope.ServerWide)
            .StatusTone(() => State is { RestartPending.Count: > 0 } || State is { Countdown: >= 0 } ? CellTone.Gold : CellTone.Muted)
            .StatusWraps()
            .Readback("serverUpdates", Readback);
        page.Info(WaitingLabel, () => Lines(State?.Waiting), refreshSeconds: 1,
                help: "Updates found that are not applied automatically (Notify, a major version, a new mod they need) or are held "
                    + "until the server is empty.", showIf: () => Shown() && (State?.Waiting.Count ?? 0) > 0)
            .InScope(SettingScope.ServerWide)
            .StatusTone(() => CellTone.Muted)
            .StatusWraps();
        page.Info(RecentLabel, () => Lines(State?.Recent), refreshSeconds: 1,
                help: "The last results, newest last.", showIf: () => Shown() && (State?.Recent.Count ?? 0) > 0)
            .InScope(SettingScope.ServerWide)
            .StatusTone(() => CellTone.Muted)
            .StatusWraps();
        page.Button(CheckCaption, () => RunOrThrow(Act(JoinAdminOps.UpdateCheck)), label: CheckLabel,
                help: "One conditional request to Thunderstore now (normally answered \"not modified\"); otherwise the server checks "
                    + "every few hours and whenever another part of it reads the index.", showIf: Shown)
            .InScope(SettingScope.ServerWide);
        page.Button(RestartNowCaption, () => RunOrThrow(Act(JoinAdminOps.UpdateRestart)), confirm: RestartConfirmText, label: RestartNowLabel,
                help: "Warns the players online in chat and on screen, then saves the world and quits so the next start applies the "
                    + "staged updates. Nobody online: no wait.", showIf: RestartOffered)
            .InScope(SettingScope.ServerWide);
        page.Button(CancelCaption, () => RunOrThrow(Act(JoinAdminOps.UpdateCancelRestart)), label: CancelLabel,
                help: "Stops the restart countdown; the updates keep waiting for the next start.", showIf: CountdownRunning)
            .InScope(SettingScope.ServerWide);
        foreach (var spec in Settings()) admin(spec);
    }

    private static void RunOrThrow(string? refusal)
    {
        if (refusal != null) throw new InvalidOperationException(refusal);
    }

    internal static Dictionary<string, object?> Readback()
    {
        var s = State;
        return new Dictionary<string, object?>
        {
            ["line"] = s?.Line,
            ["restartPending"] = s == null ? null : new List<string>(s.RestartPending),
            ["waiting"] = s == null ? null : new List<string>(s.Waiting),
            ["recent"] = s == null ? null : new List<string>(s.Recent),
            ["countdown"] = s?.Countdown,
            ["comesBack"] = s?.ComesBack,
            ["hostRestarts"] = s?.HostRestarts,
            ["lastAnswer"] = LastAnswer,
        };
    }
}
