using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>AUTOUPDATE2: the title-screen notice after an automatic batch (or a rollback).</summary>
internal sealed class UpdateNotice
{
    /// <summary>Mods updated by automatic passes since the player last opened the Mods window (loaded now or at the next start).</summary>
    public int Updated { get; set; }

    /// <summary>Updates that failed (rolled back, or could not be written) since then.</summary>
    public int Failed { get; set; }

    /// <summary>The full sentences (log, bridge, the Install tab's update list); never drawn on the title screen.</summary>
    public string Detail { get; set; } = "";

    public DateTime Utc { get; set; }

    public bool Problem => Failed > 0;

    /// <summary>
    /// The line under MODS (decided 2026-10-09: short and generic, never a mod or a version): <c>N mods updated</c>,
    /// else <c>Update failed</c> / <c>N updates failed</c>.
    /// </summary>
    public string Line =>
        Updated > 0 ? (Updated == 1 ? "1 mod updated" : Updated + " mods updated")
        : Failed > 0 ? (Failed == 1 ? "Update failed" : Failed + " updates failed")
        : "";

    public Dictionary<string, object?> Describe() => new()
    {
        ["line"] = Line,
        ["updated"] = Updated,
        ["failed"] = Failed,
        ["detail"] = Detail,
        ["utc"] = Utc.ToString("o", CultureInfo.InvariantCulture),
        ["problem"] = Problem,
    };
}

/// <summary>
/// AUTOUPDATE2 (M6.2): the client's Automatic mode. In the apply window (<see cref="ApplyWindow"/>: the title screen,
/// which is also right after leaving a server) every update the policy marks automatic (<see cref="UpdatePolicy"/>) is run
/// through the same runner as <b>Update all</b>, without a confirmation, once per version per session. Hot updates go
/// through the version store and are waited for; one that does not load is rolled back and its version goes into the
/// failed list. Restart updates are queued for the next start, which checks them (<see cref="CheckAfterStart"/>). The
/// mod page's <b>Roll back</b>, the per-mod mode and the failed list are here too. Game-free: the live probes are set by
/// <c>ModsMenu</c>.
/// </summary>
internal static class ClientAutoUpdate
{
    public const string LogPrefix = ClientUpdates.LogPrefix;

    /// <summary>The first pass waits this long after Core loads (the first detection pass and the reloader settle).</summary>
    public const double FirstPassDelaySeconds = 5;

    /// <summary>The start check (<see cref=CheckAfterStart/>) runs this long after the first title-screen tick, before any pass.</summary>
    public const double StartCheckDelaySeconds = 10;

    private static bool _startChecked;

    private static readonly object Gate = new();
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);
    private static UpdateNotice? _notice;
    private static string _lastClosed = "";
    private static string _lastPass = "";
    private static double _firstTick = -1;
    private static readonly List<string> History = new();

    internal static Func<ApplyWindowFacts>? WindowProbe { get; set; }
    internal static Func<ModVersionService?>? StoreProbe { get; set; }
    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>What can be read about a plugin's load (plugin id, file stem): the snapshot, Core's registry, the reloader's record.</summary>
    internal static Func<string, string, LoadFacts>? LoadFactsProbe { get; set; }

    internal static Action<string>? Log => ClientUpdates.Log;

    /// <summary>The last apply-window verdict (empty: open).</summary>
    public static string WindowClosedReason
    {
        get { lock (Gate) return _lastClosed; }
    }

    public static string WindowNow() => WindowNow(ignoreRunner: false);

    /// <summary>
    /// The apply window now; <paramref name="ignoreRunner"/>: as the running pass itself reads it before each write (its own
    /// run is not a reason to stop).
    /// </summary>
    public static string WindowNow(bool ignoreRunner)
    {
        ApplyWindowFacts? facts;
        try { facts = WindowProbe?.Invoke(); }
        catch (Exception e) { return "the window could not be read (" + e.Message + ")"; }
        if (facts == null) return "no window probe";
        if (ignoreRunner) facts.RunnerBusy = false;
        return ApplyWindow.Closed(facts);
    }

    private static string FactsText()
    {
        try
        {
            var f = WindowProbe?.Invoke();
            if (f == null) return "";
            return "title=" + f.AtTitle + " world=" + f.InWorld + " joining=" + f.Joining + " held=" + f.PreJoinHolding + " hosting=" + f.Hosting
                + " loaderBusy=" + f.LoaderBusy + " storeBusy=" + f.StoreBusy + " restoreDue=" + f.RestoreDue + " runner=" + f.RunnerBusy;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>The updates an automatic pass would run now (not attempted this session).</summary>
    public static List<UpdateItem> Due()
    {
        var list = new List<UpdateItem>();
        var r = ClientUpdates.Report;
        lock (Gate)
            foreach (var i in r.Items)
                if (i.Auto && !Attempted.Contains(i.Package + "@" + i.Latest)) list.Add(i);
        return list;
    }

    /// <summary>
    /// The title screen's tick (twice a second): start an automatic pass when Automatic applies something, the window is
    /// open and the first detection pass had time to run. Returns what it did, for the bridge.
    /// </summary>
    public static string Tick()
    {
        double now = Clock();
        lock (Gate)
        {
            if (_firstTick < 0) _firstTick = now;
            if (now - _firstTick < FirstPassDelaySeconds) return "waiting for the first pass";
        }

        bool check;
        lock (Gate)
        {
            check = !_startChecked && now - _firstTick >= StartCheckDelaySeconds;
            if (check) _startChecked = true;
        }

        if (check)
        {
            try { CheckAfterStart(); }
            catch (Exception e) { Remember("start check threw: " + e.Message); }
        }

        lock (Gate)
            if (!_startChecked) return "waiting for the start check";

        if (!ClientUpdates.Report.HasRun) return "not checked yet";
        // AUTOUPDATE5: Progenitor first, then mods (a mod may need the newer Progenitor).
        if (SelfUpdates.HoldsModPasses) return "waiting for Progenitor's own update";
        var due = Due();
        if (due.Count == 0) return "nothing due";
        string closed = WindowNow();
        lock (Gate) _lastClosed = closed;
        if (closed.Length > 0) return "window closed: " + closed;
        return RunPass("auto", due);
    }

    /// <summary>Run an automatic pass on <paramref name="due"/> now (the window was checked by the caller).</summary>
    public static string RunPass(string origin, List<UpdateItem> due)
    {
        var packages = new List<string>();
        lock (Gate)
            foreach (var i in due)
            {
                Attempted.Add(i.Package + "@" + i.Latest);
                packages.Add(i.Package);
            }

        if (!ClientUpdates.BeginUpdate(packages, origin, confirm: false, AfterPass, out string message))
        {
            Remember("auto pass not started: " + message);
            return "not started: " + message;
        }

        Remember("auto pass started: " + string.Join(", ", packages));
        lock (Gate) _lastPass = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        return "started: " + message;
    }

    /// <summary>The runner's completion for an automatic pass: the notice, and the failed list for a rollback.</summary>
    internal static void AfterPass(IReadOnlyList<PreparedUpdate> prepared, UpdateRunEnd end)
    {
        foreach (var p in prepared)
        {
            // Review M1: only a load that threw is a failed version; a timeout is tried again next session.
            if (p.RolledBack && p.LoadFailed) RecordFailed(p.Package, p.To, "failed to load: " + FirstSentence(p.OutcomeDetail));
            // Review H1: the window closed before the write: tried again at the next window this session.
            if (p.Outcome == "deferred") ForgetAttempt(p.Package, p.To);
        }
        var notice = NoticeFor(prepared, end);
        if (notice != null) SetNotice(notice);
        Remember("auto pass done: " + ClientUpdates.ResultBody(prepared));
    }

    private static string FirstSentence(string s)
    {
        string t = (s ?? "").Trim();
        int dot = t.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? t.Substring(0, dot) : t.TrimEnd('.');
    }

    /// <summary>The notice for a batch: how many were updated and how many failed; nothing when neither.</summary>
    public static UpdateNotice? NoticeFor(IReadOnlyList<PreparedUpdate> prepared, UpdateRunEnd end)
    {
        if (end == UpdateRunEnd.Cancelled || prepared == null || prepared.Count == 0) return null;
        int updated = 0, failed = 0;
        foreach (var p in prepared)
        {
            if (p.RolledBack || !p.Ok || p.Outcome == "failed") failed++;
            else if (p.Outcome is "loaded" or "restart") updated++;
        }

        if (updated + failed == 0) return null;
        return new UpdateNotice { Updated = updated, Failed = failed, Detail = ClientUpdates.ResultBody(prepared), Utc = DateTime.UtcNow };
    }

    /// <summary>Show (or add to) the notice: counts add up until the player opens the Mods window.</summary>
    public static void SetNotice(UpdateNotice? notice)
    {
        lock (Gate)
        {
            if (notice != null && _notice != null)
                notice = new UpdateNotice
                {
                    Updated = _notice.Updated + notice.Updated,
                    Failed = _notice.Failed + notice.Failed,
                    Detail = (_notice.Detail + " " + notice.Detail).Trim(),
                    Utc = notice.Utc,
                };
            _notice = notice;
        }
        if (notice != null)
        {
            try { Log?.Invoke(LogPrefix + "notice: " + notice.Line + " (" + notice.Detail + ")"); }
            catch { /* */ }
        }
    }

    /// <summary>The notice still to show under MODS, or null. It lasts until the Mods window opens (else the session).</summary>
    public static UpdateNotice? Notice
    {
        get
        {
            lock (Gate)
            {
                return _notice;
            }
        }
    }

    /// <summary>The Mods window opened: the notice was seen (the Install tab keeps the full result).</summary>
    public static void NoticeSeen()
    {
        UpdateNotice? n;
        lock (Gate)
        {
            n = _notice;
            _notice = null;
        }

        if (n != null) LastSeenNotice = n;
    }

    /// <summary>The last notice the player saw (the Install tab's update band repeats it).</summary>
    public static UpdateNotice? LastSeenNotice { get; private set; }

    // ---- the file: per-mod modes, the failed list, the start checks -------------------------------------------

    private static string? Root()
    {
        try { return ClientUpdates.BepInExRootProbe?.Invoke(); }
        catch { return null; }
    }

    private static bool Edit(Func<UpdatePins, bool> change, out string error) => EditFile(change, out error);

    /// <summary>Review L1: every change of the auto-update file goes through <see cref="ClientUpdateInputs.EditPins"/> (one lock, atomic write).</summary>
    internal static bool EditFile(Func<UpdatePins, bool> change, out string error)
    {
        error = "";
        string? root = Root();
        if (string.IsNullOrEmpty(root)) { error = "no BepInEx folder"; return false; }
        return ClientUpdateInputs.EditPins(root!, change, line => Remember(line), out error);
    }

    public static UpdatePins ReadFile()
    {
        string? root = Root();
        return string.IsNullOrEmpty(root) ? new UpdatePins() : ClientUpdateInputs.ReadPins(root!);
    }

    public static void RecordFailed(string package, string version, string reason)
    {
        if (!Edit(f => { f.AddFailed(package, version, reason, DateTime.UtcNow); return true; }, out string error))
            Remember("could not save the failed version " + package + " " + version + ": " + error);
        try { Log?.Invoke(LogPrefix + "op=failed package=" + package + " version=" + version + " reason=\"" + reason + "\"; it is not applied automatically again"); }
        catch { /* */ }
        ClientUpdates.Recheck("failed-version");
    }

    /// <summary>Clear the failed list of <paramref name="package"/> (the mod page's <b>Clear</b>). Returns how many were cleared.</summary>
    public static int ClearFailed(string package)
    {
        int n = 0;
        Edit(f =>
        {
            if (!f.Failed.TryGetValue(package ?? "", out var list)) return false;
            n = list.Count;
            f.Failed.Remove(package!);
            return true;
        }, out _);
        if (n > 0)
        {
            try { Log?.Invoke(LogPrefix + "cleared " + n + " failed version(s) of " + package); }
            catch { /* */ }
            lock (Gate) Attempted.RemoveWhere(a => a.StartsWith(package + "@", StringComparison.OrdinalIgnoreCase));
            ClientUpdates.Recheck("failed-cleared");
        }

        return n;
    }

    public static IReadOnlyList<FailedVersion> FailedOf(string package)
    {
        var f = ReadFile();
        return f.Failed.TryGetValue(package ?? "", out var list) ? list : Array.Empty<FailedVersion>();
    }

    /// <summary>The per-mod dropdown's words, in order (the last is the pin, captioned with the version).</summary>
    public static readonly string[] ModeChoices = { "Global", "Automatic", "Notify", "Off", "Keep" };

    /// <summary>The per-mod choice now: <c>Keep</c> (pinned), a mode word, or <c>Global</c>.</summary>
    public static string ModeChoiceOf(string package)
    {
        var f = ReadFile();
        if (f.Pins.ContainsKey(package ?? "")) return "Keep";
        return f.ModeOf(package ?? "") is { } m ? UpdateSettings.Word(m) : "Global";
    }

    /// <summary>
    /// Set the per-mod choice: <c>Global</c>, <c>Automatic</c>, <c>Notify</c>, <c>Off</c>, or <c>Keep</c> (pin at
    /// <paramref name="installedVersion"/>, the version the ledger names now).
    /// </summary>
    public static bool SetModeChoice(string package, string choice, string installedVersion, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(package)) { message = "no package"; return false; }
        string c = (choice ?? "").Trim();
        if (c.Equals("Keep", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(installedVersion))
        {
            message = "the installed version is not known";
            return false;
        }

        bool ok = Edit(f =>
        {
            f.Pins.Remove(package);
            f.Modes.Remove(package);
            if (c.Equals("Keep", StringComparison.OrdinalIgnoreCase)) f.Pins[package] = installedVersion.Trim();
            else if (UpdateSettings.ParsePerMod(c) is { } mode) f.Modes[package] = UpdateSettings.Word(mode);
            return true;
        }, out string error);
        if (!ok) { message = "could not save: " + error; return false; }
        message = c.Equals("Keep", StringComparison.OrdinalIgnoreCase) ? "kept at " + installedVersion
            : UpdateSettings.ParsePerMod(c) is { } m2 ? "updates: " + UpdateSettings.Word(m2) : "updates: the default (Preferences › Updates)";
        try { Log?.Invoke(LogPrefix + "mode " + package + " = " + c + (c.Equals("Keep", StringComparison.OrdinalIgnoreCase) ? " " + installedVersion : "")); }
        catch { /* */ }
        ClientUpdates.Recheck("mode");
        return true;
    }

    // ---- Roll back ----------------------------------------------------------------------------------------------

    /// <summary>The version <b>Roll back</b> returns to for a plugin, or empty.</summary>
    public static string RollbackVersionOf(string pluginId)
    {
        try
        {
            var svc = StoreProbe?.Invoke();
            if (svc == null) return "";
            return StoreUpdate.RollbackTarget(svc.Manifest().Find(pluginId))?.Version ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// The mod page's <b>Roll back</b>: the previous kept version comes back (now when the reloader can, else at the next
    /// start), and the mod is kept at it (pinned) until the player unpins it, so the update is not offered again at once.
    /// </summary>
    public static string RollBack(string pluginId, string package)
    {
        var svc = StoreProbe?.Invoke();
        if (svc == null) return "Keeping versions is not available here.";
        string closed = WindowNow();
        if (closed.StartsWith("in a world", StringComparison.Ordinal) || closed.StartsWith("a join", StringComparison.Ordinal))
            return "Can't roll back now: " + closed + ". Roll back at the title screen.";
        var entry = svc.Manifest().Find(pluginId);
        var target = StoreUpdate.RollbackTarget(entry);
        if (target == null) return "No earlier version of it is kept.";
        var r = StoreUpdate.RollBack(svc, pluginId, target.Version);
        string line = r.Ok ? r.Message : "Can't roll back to " + target.Version + ": " + r.Message;
        if (r.Ok)
        {
            // Review H2: a start check of the update this replaces must not judge (or undo) the player's choice.
            ForgetStartCheck(pluginId, "rolled back from the mod page");
            string pinAt = LedgerVersionOf(target) ?? target.Version;
            if (!string.IsNullOrEmpty(package))
                SetModeChoice(package, "Keep", pinAt, out _);
            line = "Rolled back to " + target.Version + ". " + r.Message + " It is kept at this version until you change Updates.";
        }

        try { Log?.Invoke(LogPrefix + "op=rollback by=menu plugin=" + pluginId + " to=" + target.Version + " result=" + (r.Ok ? r.Route : ModVersionResult.CodeName(r.Code)) + " detail=\"" + r.Message + "\""); }
        catch { /* */ }
        ClientUpdates.Recheck("rollback");
        return line;
    }

    /// <summary>The listing version a kept copy's ledger row names (<c>package:Owner-Name@1.2.0</c>), or null.</summary>
    internal static string? LedgerVersionOf(ParkedVersion p) =>
        p.Ledger != null && InstalledState.TrySplitPackageSource(p.Ledger.Source, out _, out string v) && v.Length > 0 ? v : null;

    /// <summary>
    /// After a hot update failed to load (main thread): the previous version comes back through the store and the failed
    /// copy is deleted. Returns the sentence for the result.
    /// </summary>
    public static string RollBackFailed(PreparedUpdate p, string why)
    {
        var svc = StoreProbe?.Invoke();
        string id = p.PluginIds.Count > 0 ? p.PluginIds[0] : "";
        if (svc == null || id.Length == 0 || p.Previous.Length == 0) return "The previous version could not be put back (" + (svc == null ? "no version store" : "no previous version") + ").";
        var r = StoreUpdate.RollBack(svc, id, p.Previous, forget: p.To);
        try { Log?.Invoke(LogPrefix + "op=rollback by=auto package=" + p.Package + " from=" + p.To + " to=" + p.Previous + " result=" + (r.Ok ? r.Route : ModVersionResult.CodeName(r.Code)) + " why=\"" + why + "\""); }
        catch { /* */ }
        return r.Ok ? "It was rolled back to " + p.Previous + "." : "Rolling back to " + p.Previous + " failed: " + r.Message;
    }

    // ---- the start check (restart updates) ------------------------------------------------------------------

    /// <summary>A restart update was queued (or a queued one replaced): the next start checks this exact image.</summary>
    public static void NoteRestart(PreparedUpdate p, string sha, string path)
    {
        string id = p.PluginIds.Count > 0 ? p.PluginIds[0] : "";
        if (id.Length == 0) return;
        string file = "";
        try { file = Path.GetFileName(path ?? ""); }
        catch { file = ""; }
        EditFile(f =>
        {
            f.Verify[p.Package] = new StartCheck { PluginId = id, Version = p.To, Sha256 = sha, Previous = p.Previous, File = file, Utc = DateTime.UtcNow };
            return true;
        }, out _);
    }

    /// <summary>
    /// Review H2: the queued copy of a plugin changed or went away by another path (a Roll back, a Cancel install, another
    /// install): its start check is dropped, never judged. Returns how many were dropped.
    /// </summary>
    public static int ForgetStartCheck(string pluginId, string why)
    {
        int n = 0;
        EditFile(f =>
        {
            foreach (var kv in new List<KeyValuePair<string, StartCheck>>(f.Verify))
                if (string.Equals(kv.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    f.Verify.Remove(kv.Key);
                    n++;
                }
            return n > 0;
        }, out _);
        if (n > 0) Remember("start check of " + pluginId + " dropped: " + why);
        return n;
    }

    /// <summary>
    /// After a start (main thread, once the reloader has had time to load), each queued restart update is judged only on the
    /// exact image it queued (review H2):
    /// <list type="bullet">
    /// <item>still queued with that image: wait for a later start; queued with another image, or the copy on disk is
    /// another image (Roll back, Cancel install, another install): dropped silently, nothing rolled back or listed;</item>
    /// <item>loaded (<see cref="LoadCheck"/>): done; its load threw: rolled back and entered in the failed list; not loaded
    /// with no failure seen: rolled back, tried again next session (not listed); nothing can tell (an older Bootstrap and
    /// no reloader record): dropped as unverified.</item>
    /// </list>
    /// </summary>
    public static List<string> CheckAfterStart()
    {
        var svc = StoreProbe?.Invoke();
        var file = ReadFile();
        if (file.Verify.Count == 0) return new List<string>();
        var result = UpdateStartChecks.Run(svc, file, change => EditFile(change, out _), LoadFactsProbe, line =>
        {
            try { Log?.Invoke(line); }
            catch { /* */ }
        }, LogPrefix);
        if (result.Rolled.Count > 0)
        {
            SetNotice(NoticeFor(result.Rolled, UpdateRunEnd.Applied));
            ClientUpdates.Recheck("start-check");
        }

        foreach (var l in result.Lines) Remember("start check: " + l);
        return result.Lines;
    }

    // ---- readback --------------------------------------------------------------------------------------------

    private static void Remember(string line)
    {
        lock (Gate)
        {
            History.Add(DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line);
            if (History.Count > 40) History.RemoveAt(0);
        }
    }

    public static Dictionary<string, object?> Describe()
    {
        var due = new List<object?>();
        foreach (var i in Due()) due.Add(i.Package + "@" + i.Latest);
        var file = ReadFile();
        var failed = new Dictionary<string, object?>();
        foreach (var kv in file.Failed)
        {
            var rows = new List<object?>();
            foreach (var f in kv.Value) rows.Add(f.Describe());
            failed[kv.Key] = rows;
        }

        var verify = new Dictionary<string, object?>();
        foreach (var kv in file.Verify)
            verify[kv.Key] = new Dictionary<string, object?> { ["pluginId"] = kv.Value.PluginId, ["version"] = kv.Value.Version, ["previous"] = kv.Value.Previous };
        lock (Gate)
        {
            var attempted = new List<string>(Attempted);
            return new Dictionary<string, object?>
            {
                ["window"] = WindowNow() is { Length: > 0 } w ? w : "open",
                ["windowFacts"] = FactsText(),
                ["due"] = due,
                ["attempted"] = attempted,
                ["lastPass"] = _lastPass,
                ["notice"] = _notice?.Describe(),
                ["lastSeenNotice"] = LastSeenNotice?.Describe(),
                ["modes"] = new Dictionary<string, object?>(ToObjects(file.Modes)),
                ["pins"] = new Dictionary<string, object?>(ToObjects(file.Pins)),
                ["failed"] = failed,
                ["verify"] = verify,
                ["history"] = new List<string>(History),
            };
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> ToObjects(Dictionary<string, string> d)
    {
        foreach (var kv in d) yield return new KeyValuePair<string, object?>(kv.Key, kv.Value);
    }

    /// <summary>One version may be tried again this session (its write was deferred).</summary>
    public static void ForgetAttempt(string package, string version)
    {
        lock (Gate) Attempted.Remove(package + "@" + version);
    }

    /// <summary>Bridge: forget this session's attempts, so a pass retries the same versions.</summary>
    public static int ForgetAttempts()
    {
        lock (Gate)
        {
            int n = Attempted.Count;
            Attempted.Clear();
            return n;
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            Attempted.Clear();
            _notice = null;
            _lastClosed = "";
            _lastPass = "";
            _firstTick = -1;
            _startChecked = false;
            History.Clear();
        }

        LastSeenNotice = null;
        WindowProbe = null;
        StoreProbe = null;
        LoadFactsProbe = null;
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }
}
