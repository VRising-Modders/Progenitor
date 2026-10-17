using System;
using System.IO;

namespace Progenitor.SelfUpdate;

/// <summary>AUTOUPDATE5: what Bootstrap does with the journal at start, before Core loads.</summary>
internal enum BootAction
{
    /// <summary>No update in progress, or the previous Bootstrap still runs (its <c>.next</c> takes effect next launch).</summary>
    None,

    /// <summary>A staged release and its new Bootstrap runs now: put the release's Core in place, then count this start.</summary>
    Place,

    /// <summary>The new release runs: count this start.</summary>
    Count,

    /// <summary>The new release failed to reach the healthy mark too often: restore the previous copy.</summary>
    Restore,

    /// <summary>Another copy was deployed over the release: stop judging it.</summary>
    Superseded,
}

/// <summary>
/// AUTOUPDATE5: the start-loop guard (pure). Bootstrap reads the journal at every start, before Core loads. While a new
/// Progenitor release is on trial, each start that runs it is counted; Core clears the count when it reaches its healthy
/// mark (a client running 30 s, a server with a loaded world). When <see cref="FailedStartsBeforeRestore"/> starts in a row
/// ended without that mark, the next start restores the previous copy from the version store, marks the version failed and
/// never retries it automatically. Only a start that runs exactly the journal's images counts, so a developer deploy (other
/// bytes) is never rolled back.
/// </summary>
internal static class BootHealthPolicy
{
    /// <summary>Starts of a release that may end without the healthy mark before the next start restores the previous copy.</summary>
    public const int FailedStartsBeforeRestore = 2;

    public static BootAction Decide(SelfJournal? journal, string ownBootstrapSha, string diskCoreSha) =>
        Decide(journal, ownBootstrapSha, diskCoreSha, "", "");

    /// <summary>
    /// <paramref name="nextCoreSha"/> and <paramref name="nextBootstrapSha"/> are the pending <c>.next</c> files (empty when
    /// none): this start promotes them, so they are what runs. A pending file that is not the release's is another deploy:
    /// the trial is superseded and nothing is restored (review M1, M6).
    /// </summary>
    public static BootAction Decide(SelfJournal? journal, string ownBootstrapSha, string diskCoreSha, string nextCoreSha, string nextBootstrapSha)
    {
        if (journal == null) return BootAction.None;
        bool hot = journal.NewBootstrap.Length == 0;
        // A hot release keeps the running Bootstrap: it must still be the one that ran before (a Bootstrap-only deploy is not it).
        bool bootstrapIsNew = hot
            ? journal.PrevBootstrap.Length == 0 || Same(ownBootstrapSha, journal.PrevBootstrap)
            : Same(ownBootstrapSha, journal.NewBootstrap);
        switch (journal.State)
        {
            case SelfJournal.Staged:
                // The previous Bootstrap still runs: this start promotes its .next (or quit did), the next launch places Core.
                if (!bootstrapIsNew) return BootAction.None;
                if ((nextCoreSha ?? "").Length > 0 && !Same(nextCoreSha!, journal.NewCore)) return BootAction.Superseded;
                return BootAction.Place;
            case SelfJournal.Swapping:
            case SelfJournal.Trial:
                if (!bootstrapIsNew) return BootAction.Superseded;
                if ((nextBootstrapSha ?? "").Length > 0 && !Same(nextBootstrapSha!, hot ? journal.PrevBootstrap : journal.NewBootstrap))
                    return BootAction.Superseded;
                // A pending .next is what this start loads (a crash after a swap was requested, before it ran: review M6).
                string runs = (nextCoreSha ?? "").Length > 0 ? nextCoreSha! : diskCoreSha;
                if (!Same(runs, journal.NewCore)) return BootAction.Superseded;
                return journal.Starts >= FailedStartsBeforeRestore ? BootAction.Restore : BootAction.Count;
            default:
                return BootAction.None;
        }
    }

    private static bool Same(string a, string b) =>
        (a ?? "").Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// AUTOUPDATE5: the start-loop guard's file work, run by Bootstrap at the start of <c>Load</c> (before Core is read). Never
/// throws. The Bootstrap that runs is mapped and cannot be replaced in place: a restore stages the previous Bootstrap as
/// <c>Progenitor.Bootstrap.dll.next</c>, which Bootstrap's own promote takes at this start and the next launch runs.
/// </summary>
internal static class BootHealth
{
    /// <summary>What the last <see cref="RunAtStart"/> did (status readback).</summary>
    public static string LastResult { get; private set; } = "";

    /// <summary>The action of the last run.</summary>
    public static BootAction LastAction { get; private set; } = BootAction.None;

    /// <summary>
    /// Read the journal and act. <paramref name="ownBootstrapPath"/> is the running Bootstrap's file (its bytes are the
    /// running image at this point of <c>Load</c>, before any promote).
    /// </summary>
    public static string RunAtStart(string root, string ownBootstrapPath, Action<string>? info, Action<string>? error)
    {
        LastAction = BootAction.None;
        LastResult = "";
        try
        {
            string statePath = SelfPaths.State(root);
            if (!File.Exists(statePath)) return LastResult = "no self-update journal";
            var state = SelfFiles.ReadState(root);
            var j = state.Journal;
            if (j == null || !j.InProgress) return LastResult = "no update in progress";
            string own = SelfFiles.Sha256File(ownBootstrapPath);
            string disk = SelfFiles.Sha256File(SelfPaths.CoreDll(root));
            string nextCore = SelfFiles.Sha256File(SelfPaths.CoreNext(root));
            string nextBoot = SelfFiles.Sha256File((ownBootstrapPath ?? "") + SelfPaths.NextSuffix);
            var action = BootHealthPolicy.Decide(j, own, disk, nextCore, nextBoot);
            LastAction = action;
            switch (action)
            {
                case BootAction.Place:
                    return LastResult = Place(root, j, disk, info, error);
                case BootAction.Count:
                    return LastResult = Count(root, j, info);
                case BootAction.Restore:
                    return LastResult = Restore(root, j, info, error);
                case BootAction.Superseded:
                    SelfFiles.EditState(root, s =>
                    {
                        if (s.Journal == null) return false;
                        s.Journal.State = SelfJournal.Superseded;
                        s.Journal.Note = "another Progenitor copy was put in place; the update is not judged";
                        return true;
                    }, out _);
                    info?.Invoke(Line("Progenitor " + j.Version + " is no longer on trial: another copy of Progenitor is in place (not judged, nothing restored)."));
                    return LastResult = "superseded";
                default:
                    return LastResult = "waiting: the previous Bootstrap runs this launch; Progenitor " + j.Version + " starts at the next launch";
            }
        }
        catch (Exception e)
        {
            try { error?.Invoke(Line("the start check failed: " + e.Message)); }
            catch { /* never into Load */ }
            return LastResult = "failed: " + e.Message;
        }
    }

    internal static string Line(string text) => "AutoUpdate: progenitor " + text;

    /// <summary>
    /// False when the kept Core, or the kept Bootstrap when it would be staged, is an assembly built before the
    /// single-folder layout. A missing or unreadable kept file is not judged here; the restore reports it.
    /// </summary>
    internal static bool PreviousFitsLayout(string corePath, string? bootstrapPath)
    {
        try
        {
            if (File.Exists(corePath) && InstallLayout.PredatesSingleFolder(File.ReadAllBytes(corePath))) return false;
            return bootstrapPath == null || !File.Exists(bootstrapPath)
                || !InstallLayout.PredatesSingleFolder(File.ReadAllBytes(bootstrapPath));
        }
        catch { return true; }
    }

    private static string Place(string root, SelfJournal j, string disk, Action<string>? info, Action<string>? error)
    {
        if (!string.Equals(disk, j.NewCore, StringComparison.OrdinalIgnoreCase))
        {
            string dir = SelfPaths.Absolute(root, j.NewDir);
            string src = Path.Combine(dir, SelfPaths.CoreFile);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(src); }
            catch (Exception e)
            {
                error?.Invoke(Line(j.Version + ": the kept Core could not be read (" + e.Message + ")."));
                return "failed: kept Core unreadable; " + Restore(root, j, info, error, "its kept Core could not be read");
            }

            if (!string.Equals(SelfFiles.Sha256(bytes), j.NewCore, StringComparison.OrdinalIgnoreCase))
            {
                error?.Invoke(Line(j.Version + ": the kept Core does not match its journal."));
                return "failed: kept Core changed; " + Restore(root, j, info, error, "its kept Core changed on disk");
            }

            try
            {
                string next = SelfPaths.CoreNext(root);
                if (File.Exists(next)) File.Delete(next);
            }
            catch { /* a stale .next would be promoted over the placed copy; the trial then reads superseded */ }

            if (!SelfFiles.WriteAtomic(SelfPaths.CoreDll(root), bytes, out string werr))
            {
                error?.Invoke(Line(j.Version + ": the new Core could not be put in place (" + werr + "); tried again next start."));
                return "failed: " + werr;
            }

            SelfFiles.CopyTables(dir, SelfPaths.Tables(root));
        }

        SelfFiles.EditState(root, s =>
        {
            if (s.Journal == null) return false;
            s.Journal.State = SelfJournal.Trial;
            s.Journal.Starts = 1;
            s.Journal.Utc = DateTime.UtcNow;
            return true;
        }, out _);
        info?.Invoke(Line(j.Version + " starts now (Bootstrap and Core from the update; start 1 of its trial)."));
        return "placed";
    }

    private static string Count(string root, SelfJournal j, Action<string>? info)
    {
        int starts = j.Starts + 1;
        SelfFiles.EditState(root, s =>
        {
            if (s.Journal == null) return false;
            s.Journal.State = SelfJournal.Trial;
            s.Journal.Starts = starts;
            return true;
        }, out _);
        info?.Invoke(Line(j.Version + " is on trial: start " + starts + " (restored after " + BootHealthPolicy.FailedStartsBeforeRestore
            + " starts without reaching a healthy start)."));
        return "counted " + starts;
    }

    private static string Restore(string root, SelfJournal j, Action<string>? info, Action<string>? error, string? reason = null)
    {
        string why = reason ?? "it did not start " + j.Starts + " times in a row";
        string dir = SelfPaths.Absolute(root, j.PrevDir);
        string core = Path.Combine(dir, SelfPaths.CoreFile);
        string boot = Path.Combine(dir, SelfPaths.BootstrapFile);
        var kept = SelfFiles.ReadKept(dir);
        // PR #48 review H2: a previous copy from before the single-folder layout cannot be put back into it (its
        // Bootstrap looks for Core only in BepInEx/progenitor-core). Nothing is touched; the player is told how.
        if (!PreviousFitsLayout(core, j.NewBootstrap.Length > 0 && File.Exists(boot) ? boot : null))
        {
            string note = "the previous copy (" + j.Previous + ") was not restored: " + InstallLayout.CrossLayoutRefusal;
            error?.Invoke(Line(j.Version + ": " + why + "; " + note + "."));
            SelfFiles.EditState(root, s =>
            {
                s.AddFailed(j.Version, why, DateTime.UtcNow);
                if (s.Journal == null) return true;
                s.Journal.State = SelfJournal.Failed;
                s.Journal.Note = why + "; " + note;
                s.Journal.Utc = DateTime.UtcNow;
                return true;
            }, out _);
            return "refused: " + note;
        }

        bool coreOk = false;
        try
        {
            byte[] bytes = File.ReadAllBytes(core);
            string next = SelfPaths.CoreNext(root);
            if (File.Exists(next)) File.Delete(next);
            coreOk = SelfFiles.WriteAtomic(SelfPaths.CoreDll(root), bytes, out string werr);
            if (!coreOk) error?.Invoke(Line("restoring the previous Core failed: " + werr));
            SelfFiles.CopyTables(dir, SelfPaths.Tables(root));
        }
        catch (Exception e) { error?.Invoke(Line("restoring the previous Core failed: " + e.Message)); }

        bool bootStaged = false;
        if (j.NewBootstrap.Length > 0 && File.Exists(boot))
        {
            try
            {
                string own = BootstrapPathHint ?? SelfPaths.DefaultBootstrap(root);
                bootStaged = SelfFiles.WriteAtomic(own + SelfPaths.NextSuffix, File.ReadAllBytes(boot), out string berr);
                if (!bootStaged) error?.Invoke(Line("staging the previous Bootstrap failed: " + berr));
            }
            catch (Exception e) { error?.Invoke(Line("staging the previous Bootstrap failed: " + e.Message)); }
        }

        SelfFiles.EditState(root, s =>
        {
            s.AddFailed(j.Version, why, DateTime.UtcNow);
            if (s.Journal == null) return true;
            s.Journal.State = SelfJournal.RolledBack;
            s.Journal.Note = why + "; the previous copy (" + j.Previous + ") was restored";
            s.Journal.Utc = DateTime.UtcNow;
            return true;
        }, out _);
        if (kept != null && coreOk)
        {
            var record = SelfFiles.ReadRecord(root);
            if (record == null || !string.Equals(record.CoreSha, kept.CoreSha, StringComparison.OrdinalIgnoreCase))
            {
                // The record follows what runs again; a foreign copy that was never managed stays unrecorded.
                if (record != null || kept.Source == "thunderstore")
                    SelfFiles.WriteRecord(root, new SelfInstallRecord
                    {
                        Package = kept.Package,
                        Version = kept.Version,
                        CoreSha = kept.CoreSha,
                        BootstrapSha = kept.BootstrapSha,
                        InstalledUtc = DateTime.UtcNow,
                        Source = "rollback",
                    }, out _);
            }
        }

        error?.Invoke(Line(j.Version + " was rolled back: " + why + ". Core " + (coreOk ? "restored to " + j.Previous + " now" : "could not be restored")
            + (j.NewBootstrap.Length > 0 ? "; Bootstrap " + (bootStaged ? "restored at the next launch" : "could not be restored") : "")
            + ". It is not applied automatically again."));
        return coreOk ? "restored" : "restore failed";
    }

    /// <summary>
    /// Review M4: a clean quit of a process that ran the release (its Core loaded) is not a failed start: the count this start
    /// added is taken back, so a player who quits at the title screen twice never rolls a good release back. Never throws.
    /// </summary>
    public static string NoteCleanExit(string root, string ownBootstrapSha, string loadedCoreSha)
    {
        try
        {
            if (!File.Exists(SelfPaths.State(root))) return "no journal";
            string result = "not judged";
            SelfFiles.EditState(root, s =>
            {
                var j = s.Journal;
                if (j == null || (j.State != SelfJournal.Trial && j.State != SelfJournal.Swapping) || j.Starts <= 0) return false;
                if (!string.Equals(loadedCoreSha, j.NewCore, StringComparison.OrdinalIgnoreCase)) return false;
                if (j.NewBootstrap.Length > 0 && !string.Equals(ownBootstrapSha, j.NewBootstrap, StringComparison.OrdinalIgnoreCase)) return false;
                j.Starts--;
                result = "clean exit: start not counted (" + j.Starts + ")";
                return true;
            }, out _);
            return result;
        }
        catch (Exception e) { return "failed: " + e.Message; }
    }

    /// <summary>The running Bootstrap's file (live: set by Bootstrap before <see cref="RunAtStart"/>); the restore stages its <c>.next</c> beside it.</summary>
    public static string? BootstrapPathHint { get; set; }
}
