using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Networking;

/// <summary>JOINFLOW2: what one Thunderstore install from the join screens does.</summary>
internal enum JoinInstallKind
{
    /// <summary>A required mod this client lacks.</summary>
    Install = 0,

    /// <summary>A required mod this client has at an older version.</summary>
    Update = 1,

    /// <summary>This client is ahead: the server's exact older version is installed and the newer copy is kept (JOINFLOW4).</summary>
    UseVersion = 2,

    /// <summary>A checked recommended mod.</summary>
    Recommended = 3,
}

/// <summary>JOINFLOW2: where one row's install is.</summary>
internal enum JoinInstallPhase
{
    Queued = 0,
    Resolving = 1,
    Downloading = 2,
    Installing = 3,

    /// <summary>Written to <c>progenitor-reload</c> and loaded in this session.</summary>
    Loaded = 4,

    /// <summary>Written to <c>progenitor-pending</c>: loads at the next start of the game.</summary>
    AfterRestart = 5,

    /// <summary>Something went wrong on the way (a download, a write).</summary>
    Failed = 6,

    /// <summary>An install rule refused it (size, side, mod manager, version gone, …).</summary>
    Refused = 7,
}

/// <summary>JOINFLOW2: one row of a batch, as the screens asked for it.</summary>
internal sealed class JoinInstallRequest
{
    public JoinInstallRequest(string key, string name, string package, string version, JoinInstallKind kind, bool required,
        string? replaces = null, string? modId = null, Plugins.NetworkVersionRule versionRule = Plugins.NetworkVersionRule.Exact)
    {
        VersionRule = versionRule;
        Key = key ?? "";
        Name = name ?? "";
        Package = (package ?? "").Trim();
        Version = (version ?? "").Trim();
        Kind = kind;
        Required = required;
        Replaces = replaces ?? "";
        ModId = modId ?? "";
    }

    /// <summary>The row's key: a required row's mod id, or a recommended row's key.</summary>
    public string Key { get; }

    public string Name { get; }

    /// <summary>Thunderstore <c>Owner-Name</c>.</summary>
    public string Package { get; }

    /// <summary>The exact version the server named.</summary>
    public string Version { get; }

    public JoinInstallKind Kind { get; }

    public bool Required { get; }

    /// <summary>For <see cref="JoinInstallKind.UseVersion"/>: the newer version this replaces.</summary>
    public string Replaces { get; }

    /// <summary>The server's version rule for the mod: the lookup honors it as the handshake does.</summary>
    public Plugins.NetworkVersionRule VersionRule { get; }

    /// <summary>The plugin id the server named (empty for a recommended row without one).</summary>
    public string ModId { get; }

    /// <summary>
    /// JOINFLOW4: for <see cref="JoinInstallKind.UseVersion"/>, the newer copy is kept (parked) for other servers rather
    /// than replaced. False with <see cref="KeepReason"/> when it cannot be kept (another tool's copy, a plugins copy).
    /// </summary>
    public bool Keeps { get; init; } = true;

    /// <summary>JOINFLOW4: the kept copy leaves now (the reloader can unload it); false: it stays loaded until the next start.</summary>
    public bool KeepsNow { get; init; } = true;

    /// <summary>JOINFLOW4: why the newer copy cannot be kept, or empty.</summary>
    public string KeepReason { get; init; } = "";

    /// <summary>
    /// LOCALJOIN1 (debug): the package is the configured local test index's (<c>[Install] LocalIndexPath</c>), not one the
    /// server named: no Thunderstore trust is asked or recorded for it, and its words name the local test source.
    /// </summary>
    public bool LocalTest { get; init; }

    /// <summary><c>Owner-Name@1.2.0</c>, the form the trust store records.</summary>
    public string PackageVersion => Package + "@" + Version;
}

/// <summary>JOINFLOW2: how one row ended (or where it is).</summary>
internal sealed class JoinInstallOutcome
{
    public JoinInstallOutcome(JoinInstallPhase phase, string reason = "", string pluginId = "", string path = "")
    {
        Phase = phase;
        Reason = reason ?? "";
        PluginId = pluginId ?? "";
        Path = path ?? "";
    }

    public JoinInstallPhase Phase { get; }
    public string Reason { get; }
    public string PluginId { get; }
    public string Path { get; }
}

/// <summary>JOINFLOW2: one row's state while a batch runs and after, for the screens and the readback.</summary>
internal sealed class JoinInstallRowState
{
    internal JoinInstallRowState(JoinInstallRequest request)
    {
        Request = request;
    }

    public JoinInstallRequest Request { get; }
    public JoinInstallPhase Phase { get; internal set; } = JoinInstallPhase.Queued;

    /// <summary>Download progress 0–100, or -1 when the size is not known.</summary>
    public int Percent { get; internal set; } = -1;

    public string Reason { get; internal set; } = "";
    public string PluginId { get; internal set; } = "";
    public string Path { get; internal set; } = "";

    public bool Terminal => Phase is JoinInstallPhase.Loaded or JoinInstallPhase.AfterRestart or JoinInstallPhase.Failed or JoinInstallPhase.Refused;

    public bool Succeeded => Phase is JoinInstallPhase.Loaded or JoinInstallPhase.AfterRestart;

    internal JoinInstallRowState Copy() => new(Request)
    {
        Phase = Phase,
        Percent = Percent,
        Reason = Reason,
        PluginId = PluginId,
        Path = Path,
    };
}

/// <summary>JOINFLOW2: the seam that installs one package (live: the Install tab's catalog service).</summary>
internal interface IJoinPackageInstaller
{
    /// <summary>
    /// Install <paramref name="request"/>; report each phase (and the download percent, -1 when unknown)
    /// through <paramref name="progress"/>. Never throws for an ordinary failure: the outcome names it.
    /// </summary>
    Task<JoinInstallOutcome> InstallAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress, CancellationToken cancellationToken);
}

/// <summary>
/// JOINFLOW2: one "Install N mods" press — every row at once, at most <see cref="Concurrency"/> at a time. A
/// failed row never stops the others. The batch belongs to the join, not to the screens: closing them does
/// not stop it. Thread-safe; <see cref="Changed"/> is raised on worker threads.
/// </summary>
internal sealed class JoinInstallBatch
{
    /// <summary>How many packages download and install at once.</summary>
    public const int DefaultConcurrency = 2;

    private readonly object _gate = new();
    private readonly List<JoinInstallRowState> _rows = new();
    private readonly IJoinPackageInstaller _installer;
    private readonly CancellationTokenSource _cancel = new();
    private int _running;
    private int _maxRunning;
    private Task? _all;

    public JoinInstallBatch(IReadOnlyList<JoinInstallRequest> requests, IJoinPackageInstaller installer, int concurrency = DefaultConcurrency)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        Concurrency = Math.Max(1, concurrency);
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in requests ?? Array.Empty<JoinInstallRequest>())
        {
            // One package once: a recommended row naming a required row's package rides on that row (the
            // required row's version and rule win whatever the order).
            if (r == null || r.Package.Length == 0) continue;
            if (at.TryGetValue(r.Package, out int i))
            {
                if (r.Required && !_rows[i].Request.Required) _rows[i] = new JoinInstallRowState(r);
                continue;
            }

            at[r.Package] = _rows.Count;
            _rows.Add(new JoinInstallRowState(r));
        }
    }

    public int Concurrency { get; }

    /// <summary>The most rows that ran at the same time (tests).</summary>
    public int MaxRunning
    {
        get { lock (_gate) return _maxRunning; }
    }

    /// <summary>A row changed phase or progress, or the batch finished (raised on a worker thread).</summary>
    public event Action? Changed;

    /// <summary>A copy of every row's state.</summary>
    public IReadOnlyList<JoinInstallRowState> Rows
    {
        get
        {
            lock (_gate)
            {
                var list = new List<JoinInstallRowState>(_rows.Count);
                foreach (var r in _rows) list.Add(r.Copy());
                return list;
            }
        }
    }

    public JoinInstallRowState? Row(string key)
    {
        lock (_gate)
        {
            foreach (var r in _rows)
                if (string.Equals(r.Request.Key, key, StringComparison.OrdinalIgnoreCase)) return r.Copy();
        }

        return null;
    }

    public bool Done
    {
        get
        {
            lock (_gate)
            {
                foreach (var r in _rows) if (!r.Terminal) return false;
                return true;
            }
        }
    }

    public bool Started => _all != null;

    /// <summary>Start every row (idempotent). The returned task ends when every row ended.</summary>
    public Task Start()
    {
        lock (_gate)
        {
            if (_all != null) return _all;
            var gate = new SemaphoreSlim(Concurrency, Concurrency);
            var tasks = new List<Task>(_rows.Count);
            // Every row runs on the thread pool: the installer waits on the game's main thread for its writes,
            // so it must never run on that thread itself.
            foreach (var row in _rows)
            {
                var r = row;
                tasks.Add(Task.Run(() => RunOne(r, gate)));
            }
            _all = Task.WhenAll(tasks).ContinueWith(_ => Raise(), TaskScheduler.Default);
            return _all;
        }
    }

    /// <summary>Stop rows that have not started writing (Core going down). A row mid-write still ends.</summary>
    public void Cancel()
    {
        try { _cancel.Cancel(); }
        catch { /* disposed */ }
    }

    private async Task RunOne(JoinInstallRowState row, SemaphoreSlim gate)
    {
        try { await gate.WaitAsync(_cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Finish(row, new JoinInstallOutcome(JoinInstallPhase.Failed, JoinText.InstallCanceled));
            return;
        }

        lock (_gate)
        {
            _running++;
            if (_running > _maxRunning) _maxRunning = _running;
        }

        JoinInstallOutcome outcome;
        try
        {
            Update(row, JoinInstallPhase.Resolving, -1);
            outcome = await _installer.InstallAsync(row.Request, (phase, pct) => Update(row, phase, pct), _cancel.Token).ConfigureAwait(false)
                ?? new JoinInstallOutcome(JoinInstallPhase.Failed, "no result");
        }
        catch (OperationCanceledException)
        {
            outcome = new JoinInstallOutcome(JoinInstallPhase.Failed, JoinText.InstallCanceled);
        }
        catch (Exception e)
        {
            outcome = new JoinInstallOutcome(JoinInstallPhase.Failed, e.Message);
        }
        finally
        {
            lock (_gate) _running--;
            gate.Release();
        }

        Finish(row, outcome);
    }

    private void Update(JoinInstallRowState row, JoinInstallPhase phase, int percent)
    {
        lock (_gate)
        {
            if (row.Terminal) return;
            if (phase is JoinInstallPhase.Loaded or JoinInstallPhase.AfterRestart or JoinInstallPhase.Failed or JoinInstallPhase.Refused) return;
            row.Phase = phase;
            row.Percent = phase == JoinInstallPhase.Downloading ? Math.Max(-1, Math.Min(100, percent)) : -1;
        }

        Raise();
    }

    private void Finish(JoinInstallRowState row, JoinInstallOutcome outcome)
    {
        lock (_gate)
        {
            var phase = outcome.Phase;
            if (phase is not (JoinInstallPhase.Loaded or JoinInstallPhase.AfterRestart or JoinInstallPhase.Failed or JoinInstallPhase.Refused))
                phase = JoinInstallPhase.Failed;
            row.Phase = phase;
            row.Percent = -1;
            row.Reason = outcome.Reason;
            row.PluginId = outcome.PluginId;
            row.Path = outcome.Path;
        }

        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch { /* a listener must not break a worker */ }
    }
}

/// <summary>JOINFLOW2: what the screens offer once a batch ended.</summary>
internal enum JoinAfterInstall
{
    /// <summary>Nothing was installed by this batch.</summary>
    Nothing = 0,

    /// <summary>A required mod is still not satisfied (a row failed or was refused, or another row still needs the admins).</summary>
    Incomplete = 1,

    /// <summary>Every required mod is satisfied with mods loaded now: reconnect at once.</summary>
    JoinNow = 2,

    /// <summary>A required or checked mod loads only at the next start: restart and rejoin.</summary>
    RestartNeeded = 3,
}

/// <summary>JOINFLOW2: the Join now / restart decision, game-free.</summary>
internal static class JoinAfterInstallPolicy
{
    /// <summary>
    /// After a batch: <see cref="JoinAfterInstall.RestartNeeded"/> when any required row or checked
    /// recommended row was written for the next start; <see cref="JoinAfterInstall.Incomplete"/> when a
    /// required row that needs attention is neither loaded nor waiting for the restart; else
    /// <see cref="JoinAfterInstall.JoinNow"/>. A required row this batch loaded counts even before the page
    /// model sees the new plugin.
    /// </summary>
    public static JoinAfterInstall Decide(JoinPageModel model, IReadOnlyList<JoinInstallRowState>? rows)
    {
        if (rows == null || rows.Count == 0) return JoinAfterInstall.Nothing;
        var byKey = new Dictionary<string, JoinInstallRowState>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            byKey[r.Request.Key] = r;
            if (r.Request.ModId.Length > 0) byKey[r.Request.ModId] = r;
        }

        bool restart = false, incomplete = false, any = false;
        foreach (var r in rows) if (r.Succeeded) any = true;
        foreach (var r in rows) if (r.Phase == JoinInstallPhase.AfterRestart) restart = true;
        foreach (var req in model?.Required ?? Array.Empty<JoinRequiredRow>())
        {
            if (req.Ready) continue;
            // JOINTURNON: a turned-off copy switched on for the next start is satisfied by the restart.
            if (req.TurnOnPending)
            {
                restart = true;
                continue;
            }

            if (!byKey.TryGetValue(req.Id, out var state) || !state.Succeeded)
            {
                incomplete = true;
                continue;
            }
        }

        // CLIENTPOLICY2: a mod the server does not allow that only a restart removes keeps the join from succeeding until
        // it is switched off (then the restart does it); a pausable one is paused by the next join's automatic pause.
        foreach (var na in model?.NotAllowed ?? Array.Empty<JoinNotAllowedRow>())
        {
            if (na.State == JoinNotAllowedState.TurnsOffAtRestart) restart = true;
            else if (na.Action == JoinNotAllowedAction.TurnOffRestart) incomplete = true;
        }

        if (!any) return incomplete ? JoinAfterInstall.Incomplete : JoinAfterInstall.Nothing;
        if (incomplete) return JoinAfterInstall.Incomplete;
        return restart ? JoinAfterInstall.RestartNeeded : JoinAfterInstall.JoinNow;
    }
}

/// <summary>
/// JOINFLOW2: the trust step for Thunderstore installs a server named. The packages are public, but the server
/// chose them, so one confirmation per server per batch: asked unless this host and key already confirmed every
/// package of the batch (a retry, or the rest after a restart). Recorded with the trust store
/// (<see cref="TrustedOperatorsFile.ThunderstoreAcks"/>), separate from the server-copy acknowledgement.
/// </summary>
internal static class JoinThunderstoreTrust
{
    public const int MaxPackagesPerAck = 64;
    public const int MaxAcks = 200;

    /// <summary>True unless this host and key confirmed every <c>Owner-Name@version</c> in <paramref name="packages"/>.</summary>
    public static bool NeedsPrompt(TrustedOperatorsFile? file, string host, string spki, IReadOnlyCollection<string> packages)
    {
        if (string.IsNullOrEmpty(spki) || packages == null || packages.Count == 0) return true;
        ThunderstoreAck? ack = null;
        foreach (var row in file?.ThunderstoreAcks ?? Array.Empty<ThunderstoreAck>())
            if (row != null && string.Equals(row.Spki, spki, StringComparison.Ordinal)
                && string.Equals(row.Host, host ?? "", StringComparison.OrdinalIgnoreCase)) ack = row;
        if (ack == null) return true;
        var have = new HashSet<string>(ack.Packages ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (string p in packages) if (!have.Contains(p)) return true;
        return false;
    }

    /// <summary>When this host and key last confirmed a batch (<c>Oct 4, 2026</c>), or empty.</summary>
    public static string LastConfirmed(TrustedOperatorsFile? file, string host, string spki)
    {
        foreach (var row in file?.ThunderstoreAcks ?? Array.Empty<ThunderstoreAck>())
            if (row != null && string.Equals(row.Spki, spki ?? "", StringComparison.Ordinal)
                && string.Equals(row.Host, host ?? "", StringComparison.OrdinalIgnoreCase)
                && ModManifestJson.TryParseIssued(row.At, out var at))
                return at.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
        return "";
    }
}

/// <summary>JOINFLOW2: what the Thunderstore confirmation shows.</summary>
internal sealed class JoinThunderstoreFacts
{
    public string ServerName { get; set; } = "";
    public string Host { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    /// <summary>The operator key the prompt was shown for; Install records the confirmation only for this key.</summary>
    public string Spki { get; set; } = "";

    public string FirstSeen { get; set; } = "";

    /// <summary>The fingerprint pinned for this address under another key (the key changed), or empty.</summary>
    public string PreviousFingerprint { get; set; } = "";

    /// <summary>When that previous key was first seen (<c>Oct 4, 2026</c>), or empty.</summary>
    public string PreviousSeen { get; set; } = "";

    public IReadOnlyList<JoinInstallRequest> Requests { get; set; } = Array.Empty<JoinInstallRequest>();

    /// <summary>
    /// JOINTURNON: the installed-but-off mods the same press turns on (checked recommended rows). Listed on the one
    /// confirmation; they need none of their own and are turned on only when Install is pressed.
    /// </summary>
    public IReadOnlyList<JoinTurnOnTarget> TurnOns { get; set; } = Array.Empty<JoinTurnOnTarget>();
}
