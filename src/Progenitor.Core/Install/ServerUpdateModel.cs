using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.WorldRestore;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE3 (M6.3): the server's <c>[AutoUpdate]</c> section in its <c>progenitor.cfg</c> (decisions of 2026-10-09:
/// servers Notify by default; Restart when empty and the pre-update world backup off by default in every mode). Until the
/// section is bound, and whenever it cannot be read, the server runs as <see cref="UpdateMode.Off"/> (fail closed, like
/// <c>[RemoteInstall]</c>). Game-free.
/// </summary>
internal sealed class ServerUpdateSettings
{
    public const string Section = UpdateSettings.Section;
    public const string ModsKey = UpdateSettings.ModsKey;
    public const string MajorKey = UpdateSettings.MajorKey;
    public const string CheckHoursKey = "CheckHours";
    public const string RestartWhenEmptyKey = "RestartWhenEmpty";
    public const string EmptyMinutesKey = "EmptyMinutes";
    public const string BackupKey = "BackupBeforeUpdate";
    public const string BackupKeepKey = "PreUpdateBackupKeep";
    public const string BackupSkipHoursKey = "PreUpdateBackupSkipHours";
    public const string WarnMinutesKey = "WarnMinutes";
    public const string ExcludeKey = "Exclude";
    public const string PinsKey = "Pins";
    public const string HostRestartsKey = "HostRestarts";

    /// <summary>Servers: Notify (decision 1).</summary>
    public const string DefaultMode = "Notify";

    public const int DefaultCheckHours = 6;
    public const int MinCheckHours = 1;
    public const int MaxCheckHours = 168;
    public const int DefaultEmptyMinutes = 5;
    public const int MinEmptyMinutes = 1;
    public const int MaxEmptyMinutes = 120;
    public const int DefaultSkipHours = 6;
    public const int MaxSkipHours = 168;
    public const int DefaultWarnMinutes = 5;
    public const int MinWarnMinutes = 1;
    public const int MaxWarnMinutes = 60;

    public bool Bound { get; init; }
    public UpdateMode Mode { get; init; } = UpdateMode.Notify;
    public bool MajorsAutomatic { get; init; }
    public int CheckHours { get; init; } = DefaultCheckHours;
    public bool RestartWhenEmpty { get; init; }
    public int EmptyMinutes { get; init; } = DefaultEmptyMinutes;
    public bool BackupBeforeUpdate { get; init; }
    public int PreUpdateBackupKeep { get; init; } = WorldBackupRetention.PreUpdateDefaultKeep;
    public int PreUpdateBackupSkipHours { get; init; } = DefaultSkipHours;
    public int WarnMinutes { get; init; } = DefaultWarnMinutes;
    public IReadOnlyCollection<string> Exclude { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The operator's statement that something starts this server again after it quits (a service, a panel, a script), or
    /// empty. Restart when empty cannot be turned on without it: Progenitor cannot start a server again by itself.
    /// </summary>
    public string HostRestarts { get; init; } = "";

    /// <summary>
    /// <c>[RemoteInstall] Enabled</c> on this server. Automatic writes mods the way a remote install does, so with remote
    /// installs turned off the operator has said "no installs from the game": Automatic then acts as Notify.
    /// </summary>
    public bool RemoteInstallEnabled { get; init; } = true;

    public static ServerUpdateSettings Unbound { get; } = new() { Bound = false, Mode = UpdateMode.Off };

    /// <summary>The mode in force: Off until the section is bound (fail closed); Automatic acts as Notify while remote installs are off.</summary>
    public UpdateMode Effective => !Bound ? UpdateMode.Off : Mode == UpdateMode.Automatic && !RemoteInstallEnabled ? UpdateMode.Notify : Mode;

    /// <summary>Automatic is set but acts as Notify because <c>[RemoteInstall] Enabled</c> is false.</summary>
    public bool AutomaticHeldByRemoteInstall => Bound && Mode == UpdateMode.Automatic && !RemoteInstallEnabled;

    public const string RemoteInstallOffText = "Automatic acts as Notify: remote installs are off on this server ([RemoteInstall] Enabled = false).";

    /// <summary>Something is known to start the server again after it quits.</summary>
    public bool ComesBack => (HostRestarts ?? "").Trim().Length > 0;

    /// <summary>Restart when empty is on and can act (the host restarts the server).</summary>
    public bool RestartsWhenEmpty => Bound && RestartWhenEmpty && ComesBack;

    /// <summary>The server's mode word: Automatic, Notify or Off; anything else (empty, a typo) is the default, Notify.</summary>
    public static UpdateMode ParseMode(string? text) => UpdateSettings.ParsePerMod(text) ?? UpdateMode.Notify;

    public static int ClampCheckHours(int n) => n < MinCheckHours ? MinCheckHours : n > MaxCheckHours ? MaxCheckHours : n;

    public static int ClampEmptyMinutes(int n) => n < MinEmptyMinutes ? MinEmptyMinutes : n > MaxEmptyMinutes ? MaxEmptyMinutes : n;

    public static int ClampSkipHours(int n) => n < 0 ? 0 : n > MaxSkipHours ? MaxSkipHours : n;

    public static int ClampWarnMinutes(int n) => n < MinWarnMinutes ? MinWarnMinutes : n > MaxWarnMinutes ? MaxWarnMinutes : n;

    /// <summary>Why Restart when empty may not be turned on here, or null.</summary>
    public static string? RestartRefusal(string? hostRestarts) =>
        (hostRestarts ?? "").Trim().Length > 0 ? null : NoHostRestartsText;

    public const string NoHostRestartsText =
        "This server can't start again by itself: Progenitor can stop it but never starts it, and nothing is known to start it again "
        + "after it quits. The operator names what restarts it (a service, a hosting panel, a script) in the server's progenitor.cfg, "
        + "[AutoUpdate] HostRestarts, first.";

    /// <summary><c>A-B, C-D</c> (commas, semicolons or spaces) → the package keys.</summary>
    public static HashSet<string> ParseExclude(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string k = raw.Trim();
            if (k.Length > 0) set.Add(k);
        }

        return set;
    }

    /// <summary><c>A-B@1.2.0, C-D@2.0.0</c> → package → version; entries without <c>@version</c> are dropped.</summary>
    public static Dictionary<string, string> ParsePins(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int at = raw.IndexOf('@');
            if (at <= 0 || at == raw.Length - 1) continue;
            map[raw.Substring(0, at).Trim()] = raw.Substring(at + 1).Trim();
        }

        return map;
    }

    /// <summary>
    /// The server's auto-update file (per-mod modes, the failed list, the start checks) with the cfg's <c>Exclude</c> (Off for
    /// that mod) and <c>Pins</c> (kept at that version) laid over it. The file is never changed by this.
    /// </summary>
    public UpdatePins Overlay(UpdatePins? file)
    {
        var copy = UpdatePins.Parse((file ?? new UpdatePins()).ToJson());
        foreach (string key in Exclude) copy.Modes[key] = UpdateSettings.Word(UpdateMode.Off);
        foreach (var kv in Pins) copy.Pins[kv.Key] = kv.Value;
        return copy;
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["bound"] = Bound,
        ["mode"] = UpdateSettings.Word(Effective),
        ["majors"] = MajorsAutomatic ? "Automatic" : "Notify",
        ["checkHours"] = CheckHours,
        ["restartWhenEmpty"] = RestartWhenEmpty,
        ["emptyMinutes"] = EmptyMinutes,
        ["backupBeforeUpdate"] = BackupBeforeUpdate,
        ["preUpdateBackupKeep"] = PreUpdateBackupKeep,
        ["preUpdateBackupSkipHours"] = PreUpdateBackupSkipHours,
        ["warnMinutes"] = WarnMinutes,
        ["exclude"] = new List<string>(Exclude),
        ["pins"] = new Dictionary<string, string>(Pins),
        ["hostRestarts"] = HostRestarts,
        ["comesBack"] = ComesBack,
        ["remoteInstallEnabled"] = RemoteInstallEnabled,
        ["automaticHeldByRemoteInstall"] = AutomaticHeldByRemoteInstall,
    };
}

/// <summary>
/// AUTOUPDATE3: when the server looks for updates. The rule of the design: an update check is an index read that would have
/// happened anyway, or one conditional request at a moment that already exists. The server's one exception is its own
/// cadence (<c>[AutoUpdate] CheckHours</c>, default 6: nobody opens a menu on an unattended server), and a read by any other
/// owner (the install window's availability, a remote install) within the interval counts as the check: the pass then runs on
/// the cached body, with no request. Game-free.
/// </summary>
internal static class ServerUpdateCadence
{
    /// <summary>Make one conditional request (TSTRAFFIC1: normally a 304 with no body).</summary>
    public const string Request = "request";

    /// <summary>Run the pass on the cached index body another owner (or this one) read; no request.</summary>
    public const string Cache = "cache";

    /// <param name="nowUtc">Now.</param>
    /// <param name="lastPassUtc">When this side's last pass ran, or null before the first one.</param>
    /// <param name="cacheStampUtc">When the cached index body was last fetched or confirmed current by any owner, or null.</param>
    /// <param name="checkHours">The cadence.</param>
    /// <returns><see cref="Request"/>, <see cref="Cache"/>, or empty when nothing is due.</returns>
    public static string Due(DateTime nowUtc, DateTime? lastPassUtc, DateTime? cacheStampUtc, int checkHours)
    {
        var interval = TimeSpan.FromHours(ServerUpdateSettings.ClampCheckHours(checkHours));
        bool freshCache = cacheStampUtc is { } stamp && nowUtc - stamp < interval && nowUtc >= stamp;
        if (lastPassUtc is not { } last)
            // The first pass after the world loaded: the cache answers when another read is recent enough.
            return freshCache ? Cache : Request;
        // Another owner read the index since this side's last pass: run the pass on that body, no request.
        if (cacheStampUtc is { } s && s > last) return Cache;
        if (nowUtc - last < interval && nowUtc >= last) return "";
        return freshCache ? Cache : Request;
    }
}

/// <summary>
/// AUTOUPDATE3: the server's "empty" (no connected player) and its quiet period: the time since the last player left (or since
/// the world loaded, when nobody came), which a connect resets. Main thread; the clock is passed in. Game-free.
/// </summary>
internal sealed class EmptyGate
{
    private int _online = -1;
    private DateTime? _emptySince;
    private DateTime? _lastConnectUtc;

    /// <summary>The players online as last observed (-1 before the first observation).</summary>
    public int Online => _online;

    public DateTime? EmptySince => _emptySince;

    public DateTime? LastConnectUtc => _lastConnectUtc;

    /// <summary>The count of connected players now.</summary>
    public void Observe(int online, DateTime nowUtc)
    {
        if (online > 0) _emptySince = null;
        else if (_emptySince == null) _emptySince = nowUtc;
        _online = online < 0 ? 0 : online;
    }

    /// <summary>A player connected (the quiet period starts again when the server is empty next).</summary>
    public void Connected(DateTime nowUtc)
    {
        _lastConnectUtc = nowUtc;
        _emptySince = null;
        if (_online < 1) _online = 1;
    }

    /// <summary>Nobody online for at least <paramref name="minutes"/>.</summary>
    public bool Quiet(DateTime nowUtc, int minutes) =>
        _online == 0 && _emptySince is { } since && nowUtc - since >= TimeSpan.FromMinutes(Math.Max(0, minutes));

    /// <summary>How long until the quiet period is over (zero when it is), or null while someone is online.</summary>
    public TimeSpan? Remaining(DateTime nowUtc, int minutes)
    {
        if (_online != 0 || _emptySince is not { } since) return null;
        var left = since + TimeSpan.FromMinutes(Math.Max(0, minutes)) - nowUtc;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }
}

/// <summary>AUTOUPDATE3: what to do with one prepared server update now.</summary>
internal enum ServerApplyRoute
{
    /// <summary>Write it now (a hot update: the reloader swaps it within seconds).</summary>
    Now = 0,

    /// <summary>Keep it for later (a hot update clients need, while players are online or the quiet period runs).</summary>
    Hold = 1,

    /// <summary>Stage it for the next start (a restart update): written to <c>progenitor-pending</c>, nothing changes now.</summary>
    Stage = 2,
}

/// <summary>
/// AUTOUPDATE3: the split of the design's table: a server-only hot update applies now, players or not; a hot update clients
/// also need waits until the server has been empty for the quiet period (applying it re-signs the catalog and changes a
/// requirement under the players in the world); a restart update is staged now and applies at the next start. Game-free.
/// </summary>
internal static class ServerApplyPlan
{
    public const string HoldPlayers = "players are online";
    public const string HoldQuiet = "waiting for the server to stay empty";

    public static (ServerApplyRoute Route, string Why) Decide(bool hot, bool clientsNeed, int online, bool quiet)
    {
        if (!hot) return (ServerApplyRoute.Stage, "");
        if (!clientsNeed) return (ServerApplyRoute.Now, "");
        if (online != 0) return (ServerApplyRoute.Hold, HoldPlayers);
        if (!quiet) return (ServerApplyRoute.Hold, HoldQuiet);
        return (ServerApplyRoute.Now, "");
    }

    /// <summary>
    /// Do clients need this update (review 2026-10-09)? Decided from the update itself as well as the running copy: the running
    /// copy's handshake row or the client mod policy (<paramref name="runningCopyNeeds"/>), or the new image not declaring
    /// itself server-only (<c>progenitor.json</c> <c>side</c>, which admission checks against <c>[ProgenitorSide]</c>). A
    /// 1.1 that becomes required, or an old copy that failed to load (no handshake row), is never swapped live under players
    /// unless the new image says it is server-only. Unknown fails safe: clients need it.
    /// </summary>
    public static bool ClientsNeed(bool runningCopyNeeds, PackageSide newImageSide) =>
        runningCopyNeeds || newImageSide != PackageSide.Server;

    /// <summary>
    /// The re-check on the main thread in the same step as a write: a hot update clients need is written only while nobody is
    /// connected (a join that started meanwhile defers it, never a failure).
    /// </summary>
    public static string WriteRefusal(bool clientsNeed, int onlineNow) =>
        clientsNeed && onlineNow != 0 ? HoldPlayers : "";
}

/// <summary>AUTOUPDATE3: whether the server restarts itself now to apply restart updates (Restart when empty).</summary>
internal static class RestartWhenEmptyRule
{
    /// <summary>After a restart attempt that failed (the save or the backup), the next one waits for the next empty moment, or this long.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>Empty when the server restarts now; otherwise why not.</summary>
    public static string Decide(ServerUpdateSettings settings, int restartPending, EmptyGate gate, DateTime nowUtc, bool busy,
        DateTime? lastFailureUtc)
    {
        if (settings == null || !settings.Bound || !settings.RestartWhenEmpty) return "Restart when empty is off";
        if (!settings.ComesBack) return "nothing is known to start the server again ([AutoUpdate] HostRestarts is empty)";
        if (restartPending <= 0) return "no update waits for a restart";
        if (busy) return "an update, a backup or a countdown is running";
        if (gate == null || gate.Online != 0) return "players are online";
        if (!gate.Quiet(nowUtc, settings.EmptyMinutes)) return "the quiet period (" + settings.EmptyMinutes + " min empty) has not passed";
        if (lastFailureUtc is { } failed && nowUtc - failed < RetryAfterFailure && !(gate.EmptySince is { } since && since > failed))
            return "the last restart attempt failed; it is tried again at the next empty moment";
        return "";
    }
}

/// <summary>
/// AUTOUPDATE3 (review 2026-10-09): the last check of a restart, on the main thread in the same step as the stop. A restart
/// that started without a warning (Restart when empty, or Restart now on an empty server) took a backup in between (minutes);
/// a player who connected meanwhile must not be dropped by a quit nobody warned them of.
/// </summary>
internal static class RestartStopGate
{
    /// <summary>Save and quit now.</summary>
    public const string Stop = "stop";

    /// <summary>Do not quit: someone came (Restart when empty: the quiet period starts again when they leave).</summary>
    public const string Abort = "abort";

    /// <summary>Do not quit yet: someone came during Restart now; warn them first (the countdown).</summary>
    public const string Warn = "warn";

    /// <param name="origin"><c>empty</c> (Restart when empty) or <c>now</c> (Restart now).</param>
    /// <param name="warned">The players online were warned (the countdown ran to its end).</param>
    /// <param name="online">Players connected now (a failed read counts as someone).</param>
    /// <param name="lastConnectUtc">The last player connect seen, or null.</param>
    /// <param name="sinceUtc">When this restart was decided.</param>
    public static string Decide(string origin, bool warned, int online, DateTime? lastConnectUtc, DateTime sinceUtc)
    {
        if (warned) return Stop;
        bool someone = online != 0 || (lastConnectUtc is { } c && c >= sinceUtc);
        if (!someone) return Stop;
        return origin == "empty" ? Abort : Warn;
    }
}

/// <summary>AUTOUPDATE3: Restart now's warnings (seconds before the stop), from <c>[AutoUpdate] WarnMinutes</c>.</summary>
internal static class RestartCountdown
{
    /// <summary>The announcements, in seconds before the stop, latest last: the full warning, then one minute.</summary>
    public static List<int> Announcements(int warnMinutes)
    {
        int total = ServerUpdateSettings.ClampWarnMinutes(warnMinutes) * 60;
        var list = new List<int> { total };
        if (total > 60) list.Add(60);
        return list;
    }

    /// <summary><c>The server restarts in 5 minutes to update mods: ChatSkin, BroaderMap.</c></summary>
    public static string Line(int secondsLeft, IReadOnlyList<string> mods)
    {
        string when = secondsLeft >= 120 && secondsLeft % 60 == 0 ? (secondsLeft / 60).ToString(CultureInfo.InvariantCulture) + " minutes"
            : secondsLeft == 60 ? "1 minute"
            : secondsLeft.ToString(CultureInfo.InvariantCulture) + " seconds";
        string list = mods != null && mods.Count > 0 ? ": " + string.Join(", ", mods) : "";
        return "The server restarts in " + when + " to update mods" + list + ".";
    }
}

/// <summary>AUTOUPDATE3: the pre-update world backup's decision for one batch.</summary>
internal sealed class PreUpdateBackupDecision
{
    /// <summary><c>off</c>, <c>take</c>, <c>skip</c> (a recent backup stands in) or <c>done</c> (this batch already has one).</summary>
    public string Action { get; init; } = "off";

    /// <summary>The backup that stands in (<c>skip</c>), for the audit line.</summary>
    public string StandIn { get; init; } = "";

    public bool Take => Action == "take";
}

/// <summary>
/// AUTOUPDATE3 (decision 5): one pre-update backup per update batch, before the batch's first write, when <b>Back up the world
/// before updating</b> is on; skipped when a backup of the current world (manual, scheduled or pre-update) is newer than
/// <c>PreUpdateBackupSkipHours</c>. A batch is one automatic pass that writes (hot or staged), or one restart Progenitor
/// makes to apply staged updates. Game-free.
/// </summary>
internal static class PreUpdateBackupRule
{
    public static PreUpdateBackupDecision Decide(bool enabled, bool batchHasOne, IReadOnlyList<WorldBackupEntry>? entries, int skipHours, DateTime nowUtc)
    {
        if (!enabled) return new PreUpdateBackupDecision { Action = "off" };
        if (batchHasOne) return new PreUpdateBackupDecision { Action = "done" };
        var standIn = WorldBackupRetention.PreUpdateStandIn(entries ?? Array.Empty<WorldBackupEntry>(), skipHours, nowUtc);
        if (standIn != null) return new PreUpdateBackupDecision { Action = "skip", StandIn = standIn.FolderName.Length > 0 ? standIn.FolderName : standIn.DisplayName };
        return new PreUpdateBackupDecision { Action = "take" };
    }

    /// <summary>The label a pre-update backup's sidecar keeps: <c>ChatSkin 1.3.0, BroaderMap 2.0.2</c> (at most 200 characters).</summary>
    public static string Label(IEnumerable<(string Name, string Version)> updates)
    {
        var parts = new List<string>();
        foreach (var (name, version) in updates ?? Array.Empty<(string, string)>())
            if (!string.IsNullOrWhiteSpace(name)) parts.Add(name.Trim() + (string.IsNullOrWhiteSpace(version) ? "" : " " + version.Trim()));
        string text = string.Join(", ", parts);
        return text.Length <= 200 ? text : text.Substring(0, 199) + "…";
    }
}

/// <summary>AUTOUPDATE3: one in-game notice for the server's admins.</summary>
internal sealed class AdminUpdateNotice
{
    /// <summary><c>available</c>, <c>restart</c>, <c>applied</c> or <c>failed</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary><c>2 server updates available</c>, <c>ChatSkin 1.3.0 applied live</c>, …</summary>
    public string Text { get; init; } = "";

    /// <summary>Restart now is offered (restart updates are waiting).</summary>
    public bool OfferRestart { get; init; }

    /// <summary>A waiting notice's "N server updates available" part alone (empty when none), for a combined notice.</summary>
    public string AvailableText { get; init; } = "";

    /// <summary>What the notice is about: the same signature is never shown twice.</summary>
    public string Signature { get; init; } = "";

    public Dictionary<string, object?> Describe() => new()
    {
        ["kind"] = Kind, ["text"] = Text, ["offerRestart"] = OfferRestart, ["signature"] = Signature,
    };
}

/// <summary>
/// AUTOUPDATE3 (decision 7): what online admins are told. Once per batch: a notice with the same signature (the same updates
/// waiting, the same batch applied) is not sent again; an admin who joins later gets the current waiting notice once.
/// Game-free.
/// </summary>
internal static class AdminNoticeRule
{
    /// <summary>
    /// After a check or a pass: the waiting notice. <paramref name="available"/>: updates found that are not applied
    /// automatically (Notify, a major version, a new dependency) or are held; <paramref name="restartPending"/>: updates staged
    /// for the next start. Null when nothing waits.
    /// </summary>
    public static AdminUpdateNotice? Waiting(IReadOnlyList<string> available, IReadOnlyList<string> restartPending)
    {
        int a = available?.Count ?? 0;
        int r = restartPending?.Count ?? 0;
        if (a == 0 && r == 0) return null;
        var parts = new List<string>();
        if (a > 0) parts.Add(a == 1 ? "1 server update available" : a + " server updates available");
        if (r > 0) parts.Add(r == 1 ? "1 update waiting for a restart" : r + " updates waiting for a restart");
        var sig = new List<string>();
        foreach (var s in available ?? Array.Empty<string>()) sig.Add("a:" + s);
        foreach (var s in restartPending ?? Array.Empty<string>()) sig.Add("r:" + s);
        sig.Sort(StringComparer.OrdinalIgnoreCase);
        return new AdminUpdateNotice
        {
            Kind = r > 0 ? "restart" : "available",
            Text = string.Join(" · ", parts),
            AvailableText = a > 0 ? parts[0] : "",
            OfferRestart = r > 0,
            Signature = "wait|" + string.Join(",", sig),
        };
    }

    /// <summary>After an automatic pass wrote something: what was applied live, staged, held or failed. Null when nothing was done.</summary>
    public static AdminUpdateNotice? Batch(IReadOnlyList<ServerUpdateOutcome> outcomes, int restartPending)
    {
        var applied = new List<string>();
        var failed = new List<string>();
        var staged = new List<string>();
        foreach (var o in outcomes ?? Array.Empty<ServerUpdateOutcome>())
        {
            string label = o.Name + " " + o.To;
            if (o.Result == ServerUpdateOutcome.Applied) applied.Add(label);
            else if (o.Result == ServerUpdateOutcome.Staged) staged.Add(label);
            else if (o.Result is ServerUpdateOutcome.Failed or ServerUpdateOutcome.RolledBack) failed.Add(label);
        }

        if (applied.Count + failed.Count + staged.Count == 0) return null;
        var parts = new List<string>();
        if (applied.Count > 0) parts.Add(string.Join(", ", applied) + " applied live");
        if (staged.Count > 0) parts.Add(string.Join(", ", staged) + (staged.Count == 1 ? " waits" : " wait") + " for a restart");
        if (failed.Count > 0) parts.Add(string.Join(", ", failed) + " failed and " + (failed.Count == 1 ? "was" : "were") + " rolled back");
        var sig = new List<string>();
        foreach (var o in outcomes!) sig.Add(o.Package + "@" + o.To + "=" + o.Result);
        return new AdminUpdateNotice
        {
            Kind = failed.Count > 0 ? "failed" : "applied",
            Text = string.Join(" · ", parts),
            OfferRestart = restartPending > 0,
            Signature = "batch|" + string.Join(",", sig),
        };
    }

    /// <summary>
    /// One notice for a check that applied something and still has updates waiting: what was done, then what waits (two
    /// notices in a row would replace each other on screen). Either may be null.
    /// </summary>
    public static AdminUpdateNotice? Combine(AdminUpdateNotice? batch, AdminUpdateNotice? waiting)
    {
        if (batch == null) return waiting;
        if (waiting == null) return batch;
        // A batch that staged updates already says they wait for a restart (live 2026-10-09: "… waits for a restart · 1 update
        // waiting for a restart"): only what is available is added then.
        string more = batch.Text.Contains("for a restart", StringComparison.Ordinal) ? waiting.AvailableText : waiting.Text;
        return new AdminUpdateNotice
        {
            Kind = batch.Kind,
            Text = more.Length > 0 ? batch.Text + " · " + more : batch.Text,
            OfferRestart = batch.OfferRestart || waiting.OfferRestart,
            Signature = batch.Signature + "|" + waiting.Signature,
        };
    }

    /// <summary>True when an admin who has seen <paramref name="seen"/> should be shown <paramref name="notice"/>.</summary>
    public static bool ShouldSend(AdminUpdateNotice? notice, string? seen) =>
        notice != null && !string.Equals(notice.Signature, seen ?? "", StringComparison.Ordinal);
}

/// <summary>AUTOUPDATE3: what happened to one server update in a pass.</summary>
internal sealed class ServerUpdateOutcome
{
    public const string Applied = "applied";
    public const string Staged = "staged";
    public const string Held = "held";
    public const string Failed = "failed";
    public const string RolledBack = "rolledback";
    public const string Skipped = "skipped";
    public const string Deferred = "deferred";

    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    /// <summary><c>hot</c>, <c>restart</c> or <c>held</c>.</summary>
    public string Route { get; set; } = "";

    public string Result { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool ClientsNeed { get; set; }
    public bool LoadFailed { get; set; }
    public DateTime Utc { get; set; }

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Package, ["name"] = Name, ["from"] = From, ["to"] = To, ["route"] = Route, ["result"] = Result, ["detail"] = Detail,
        ["clientsNeed"] = ClientsNeed, ["loadFailed"] = LoadFailed, ["utc"] = Utc.ToString("o", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// AUTOUPDATE3: <c>BepInEx/progenitor-auto-update.log</c>, one line per step (rolled to <c>.1</c> past 1 MiB), the format of
/// <c>progenitor-remote-install.log</c>:
/// <c>AutoUpdate &lt;utc&gt; op=&lt;op&gt; package=&lt;key&gt; from=&lt;v&gt; to=&lt;v&gt; route=&lt;route&gt; result=&lt;result&gt; detail="…"</c>.
/// </summary>
internal static class ServerUpdateAudit
{
    public const string FileName = "progenitor-auto-update.log";

    public static string Line(DateTime utc, string op, string package = "", string from = "", string to = "", string route = "", string result = "ok",
        string detail = "", IReadOnlyList<string>? files = null)
    {
        string text = (detail ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
        return "AutoUpdate " + utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            + " op=" + (string.IsNullOrEmpty(op) ? "-" : op)
            + " package=" + (string.IsNullOrEmpty(package) ? "-" : package)
            + " from=" + (string.IsNullOrEmpty(from) ? "-" : from)
            + " to=" + (string.IsNullOrEmpty(to) ? "-" : to)
            + " route=" + (string.IsNullOrEmpty(route) ? "-" : route)
            + " result=" + (string.IsNullOrEmpty(result) ? "ok" : result)
            + (text.Length > 0 ? " detail=\"" + text + "\"" : "")
            + (files != null && files.Count > 0 ? " files=[" + string.Join(";", files) + "]" : "");
    }
}
