using System;
using System.Collections.Generic;

namespace Progenitor.Install;

/// <summary>What the pre-join flow asks of the game (live: <c>PreJoinGate</c>; tests: a fake).</summary>
internal interface IPreJoinHost
{
    /// <summary>
    /// Show one dialog. <paramref name="dropped"/> runs when the dialog goes away without an answer (another dialog took
    /// the screen, its panel was hidden, Core unloaded). False when no dialog could open.
    /// </summary>
    bool Ask(PreJoinQuestion question, Action<string> answered, Action dropped);

    /// <summary>Launch the held join again, unchanged (the gate lets this one call through).</summary>
    void Launch(object connect, string why);

    /// <summary>Restart the game and join this server after it starts (the restart bar's restart and rejoin). False when it cannot.</summary>
    bool RestartAndJoin(object connect, out string error);

    /// <summary>Can <see cref="RestartAndJoin"/> join this server again after a restart (an address join without a password).</summary>
    bool CanRestartAndJoin(object connect);

    /// <summary>Start the update runner on these packages; <c>done</c> runs on the main thread.</summary>
    bool BeginUpdate(IReadOnlyList<string> packages, Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd> done, out string message);

    /// <summary>Wait until every listed hot update is the loaded image (or the window passes); the bool says whether all were.</summary>
    void WaitLoaded(IReadOnlyList<HotWrite> writes, Action<bool, string> then);

    void Log(string line);

    /// <summary>Review H1: an update is being downloaded or written (an automatic pass included); a join waits for it.</summary>
    bool UpdateWriting => false;

    /// <summary>Run <paramref name="action"/> on the main thread after <paramref name="seconds"/>.</summary>
    void After(double seconds, Action action) => action();
}

/// <summary>A hot-written plugin to wait for: the reloader must report this image (sha256) loaded for this plugin id.</summary>
internal readonly struct HotWrite
{
    public HotWrite(string pluginId, string path, string name, string sha256 = "")
    {
        PluginId = pluginId ?? "";
        Path = path ?? "";
        Name = name ?? "";
        Sha256 = sha256 ?? "";
    }

    public string PluginId { get; }
    public string Path { get; }
    public string Name { get; }

    /// <summary>AUTOUPDATE2: the image expected (known before the file is placed), or empty to read it from <see cref="Path"/>.</summary>
    public string Sha256 { get; }
}

/// <summary>How a runner batch ended for the caller.</summary>
internal enum UpdateRunEnd
{
    /// <summary>Written (each member's outcome says loaded / restart / failed).</summary>
    Applied = 0,

    /// <summary>The player cancelled the runner's own confirmation (a new dependency asked first).</summary>
    Cancelled = 1,

    /// <summary>Nothing could be prepared (every package refused).</summary>
    NothingPrepared = 2,
}

/// <summary>One dialog the flow shows. Answers are button keys: <see cref="PreJoinFlow.KeyMain"/>, <see cref="PreJoinFlow.KeyExtra"/>, <see cref="PreJoinFlow.KeyCancel"/>.</summary>
internal sealed class PreJoinQuestion
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Note { get; set; } = "";
    public string MainCaption { get; set; } = "";

    /// <summary>A third button, or empty.</summary>
    public string ExtraCaption { get; set; } = "";

    public string CancelCaption { get; set; } = "Cancel";
    public string Key { get; set; } = "";
}

/// <summary>
/// AUTOUPDATE1 (decision 2), review fixes 2026-10-09: the pre-join prompt's state machine, game-free. A join is held
/// only while one of this flow's dialogs is open or its update runs; every way a dialog can end releases or moves the
/// hold, and a new launch that finds a hold with neither replaces it (a stale hold is never left blocking joins).
///
/// <para>Paths: <b>Update first</b> runs the updater (no second confirmation, but a new dependency still asks first;
/// cancelling that returns to the prompt); hot updates are waited for until the reloader reports the new image loaded,
/// then the join is launched; an update that needs a restart asks <b>Restart and join</b> / <b>Join with the current
/// version</b> / <b>Cancel</b>; a failure asks <b>Join anyway</b> / <b>Cancel</b>. <b>Cancel</b> and a dropped dialog
/// launch nothing.</para>
/// </summary>
internal sealed class PreJoinFlow
{
    public const string KeyMain = "main";
    public const string KeyExtra = "extra";
    public const string KeyCancel = "cancel";

    public const string KindPrompt = "prejoin-updates";
    public const string KindRestart = "prejoin-restart";
    public const string KindFailed = "prejoin-failed";
    public const string KindNotLoaded = "prejoin-not-loaded";

    /// <summary>Review H1: how long a join waits for a running update before it goes on anyway (fail open).</summary>
    public const double RunnerWaitSeconds = 60;

    private const double RunnerPollSeconds = 0.25;

    private readonly IPreJoinHost _host;
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private object? _held;
    private string _heldKey = "";
    private PreJoinDecision? _decision;
    private int _token;

    public PreJoinFlow(IPreJoinHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary><c>idle</c>, <c>asking</c> (a dialog is open), <c>updating</c> (the runner or the load wait is running).</summary>
    public string State { get; private set; } = "idle";

    /// <summary>The open dialog's kind while <see cref="State"/> is <c>asking</c>.</summary>
    public string Asking { get; private set; } = "";

    public bool Holding => _held != null;
    public string HeldKey => _heldKey;
    public string LastResult { get; private set; } = "";
    public ICollection<string> Dismissed => _dismissed;
    public PreJoinDecision? LastDecision => _decision;

    /// <summary>
    /// A join is about to launch (inside <c>LaunchGame</c>). True lets it run; false holds it. A hold whose dialog is gone
    /// and whose update is not running is stale and is replaced by this launch.
    /// </summary>
    public bool OnLaunch(object connect, string key, Func<PreJoinDecision> decide)
    {
        if (_held != null)
        {
            if (State == "asking" || State == "updating" || State == "waiting")
            {
                _host.Log("AutoUpdate: pre-join: a join is already waiting for an answer; this launch is ignored.");
                return false;
            }

            _host.Log("AutoUpdate: pre-join: a stale held join was replaced by a new one.");
            Release("replaced");
        }

        // Review H1: never join while an update is being written (the DLL could change mid-handshake). The join is held
        // until the update ends (at most RunnerWaitSeconds), then decided as usual.
        if (_host.UpdateWriting)
        {
            _held = connect;
            _heldKey = key ?? "";
            State = "waiting";
            int wait = ++_token;
            _host.Log("AutoUpdate: pre-join: an update is being written; the join waits for it.");
            WaitForRunner(wait, 0, () => DecideHeld(decide));
            return false;
        }

        var decision = decide();
        _decision = decision;
        if (!decision.Show) return true;
        _held = connect;
        _heldKey = key ?? "";
        if (!AskPrompt()) return true; // could not show: the join goes on (fail open), nothing stays held
        _host.Log("AutoUpdate: pre-join prompt for " + (_heldKey.Length > 0 ? _heldKey : "the server") + ": "
            + decision.Offers.Count + " update(s) offered, " + decision.Held.Count + " held for the server's version.");
        return false;
    }

    /// <summary>Poll until no update is being written (or the wait ends), then <paramref name="then"/>; a newer hold cancels it.</summary>
    private void WaitForRunner(int token, double waited, Action then)
    {
        _host.After(RunnerPollSeconds, () =>
        {
            if (token != _token) return;
            if (_host.UpdateWriting && waited < RunnerWaitSeconds)
            {
                WaitForRunner(token, waited + RunnerPollSeconds, then);
                return;
            }

            if (_host.UpdateWriting) _host.Log("AutoUpdate: pre-join: the update is still running after " + (int)RunnerWaitSeconds + " s; going on.");
            State = "idle";
            then();
        });
    }

    /// <summary>The held join after the wait: the prompt when there is something to offer, else the join itself.</summary>
    private void DecideHeld(Func<PreJoinDecision> decide)
    {
        var held = _held;
        PreJoinDecision decision;
        try { decision = decide(); }
        catch { decision = new PreJoinDecision { Why = "error" }; }
        _decision = decision;
        if (!decision.Show)
        {
            Relaunch("the update finished");
            return;
        }

        if (!AskPrompt() && held != null) _host.Launch(held, "the prompt could not open");
    }

    private bool AskPrompt()
    {
        var d = _decision!;
        var q = new PreJoinQuestion
        {
            Kind = KindPrompt,
            Key = d.DismissKey,
            Title = d.Offers.Count == 1 ? "A mod update is available" : "Mod updates are available",
            Body = PreJoinOffers.Body(d),
            Note = "Join anyway joins with the versions you have now.",
            MainCaption = "Update first",
            ExtraCaption = "Join anyway",
            CancelCaption = "Cancel",
        };
        return AskOrRelease(q, answer =>
        {
            if (answer == KeyMain) UpdateFirst();
            else if (answer == KeyExtra)
            {
                if (_decision != null && _decision.DismissKey.Length > 0) _dismissed.Add(_decision.DismissKey);
                Relaunch("join anyway");
            }
            else Release("cancelled");
        });
    }

    /// <summary>Show a dialog; on any answer run <paramref name="answered"/>; a dropped dialog releases the hold.</summary>
    private bool AskOrRelease(PreJoinQuestion q, Action<string> answered)
    {
        int token = ++_token;
        bool opened;
        try
        {
            opened = _host.Ask(q,
                answer =>
                {
                    if (token != _token) return;
                    State = "idle";
                    Asking = "";
                    answered(answer);
                },
                () =>
                {
                    if (token != _token) return;
                    Release("the dialog closed without an answer");
                });
        }
        catch (Exception e)
        {
            _host.Log("AutoUpdate: pre-join dialog failed: " + e.Message);
            opened = false;
        }

        if (!opened)
        {
            Release("no dialog could open");
            return false;
        }

        State = "asking";
        Asking = q.Kind;
        return true;
    }

    private void UpdateFirst()
    {
        var d = _decision;
        if (d == null || _held == null) { Release("nothing held"); return; }
        var packages = new List<string>();
        foreach (var o in d.Offers) packages.Add(o.Item.Package);
        State = "updating";
        int token = ++_token;
        if (_host.UpdateWriting)
        {
            // Review H1: Update first waits for the running pass instead of failing "already running".
            WaitForRunner(token, 0, () =>
            {
                State = "updating";
                UpdateFirst();
            });
            return;
        }

        bool started = _host.BeginUpdate(packages, (prepared, end) =>
        {
            if (token != _token) return;
            AfterUpdate(prepared, end);
        }, out string message);
        if (!started)
        {
            AskFailed("The update could not start: " + message + ".");
        }
    }

    private void AfterUpdate(IReadOnlyList<PreparedUpdate> prepared, UpdateRunEnd end)
    {
        string body = ClientUpdates.ResultBody(prepared);
        LastResult = body;
        if (end == UpdateRunEnd.Cancelled)
        {
            // Review item 2: a cancel never connects. Back to the prompt.
            _host.Log("AutoUpdate: pre-join: the update was cancelled; back to the prompt.");
            State = "idle";
            if (!AskPrompt()) Release("the prompt could not open again");
            return;
        }

        bool failed = end == UpdateRunEnd.NothingPrepared;
        bool restart = false;
        var hot = new List<HotWrite>();
        foreach (var p in prepared)
        {
            if (!p.Ok || p.Outcome == "failed") failed = true;
            else if (p.Outcome == "restart") restart = true;
            foreach (var w in p.HotWrites) hot.Add(w);
        }

        if (failed)
        {
            AskFailed(body);
            return;
        }

        if (hot.Count == 0)
        {
            if (restart) AskRestart(body);
            else Relaunch("updated");
            return;
        }

        int token = ++_token;
        _host.WaitLoaded(hot, (allLoaded, detail) =>
        {
            if (token != _token) return;
            if (!allLoaded)
            {
                AskNotLoaded(body + " " + detail);
                return;
            }

            if (restart) AskRestart(body);
            else Relaunch("updated and loaded");
        });
    }

    private void AskFailed(string body)
    {
        State = "idle";
        AskOrRelease(new PreJoinQuestion
        {
            Kind = KindFailed,
            Key = _heldKey,
            Title = "Not every update was applied",
            Body = body,
            MainCaption = "Join anyway",
            CancelCaption = "Cancel",
        }, answer =>
        {
            if (answer == KeyMain) Relaunch("join after a failed update");
            else Release("cancelled after a failed update");
        });
    }

    private void AskNotLoaded(string body)
    {
        State = "idle";
        AskOrRelease(new PreJoinQuestion
        {
            Kind = KindNotLoaded,
            Key = _heldKey,
            Title = "An update has not loaded yet",
            Body = body,
            MainCaption = "Join anyway",
            CancelCaption = "Cancel",
        }, answer =>
        {
            if (answer == KeyMain) Relaunch("join before the update loaded");
            else Release("cancelled while an update loads");
        });
    }

    private void AskRestart(string body)
    {
        State = "idle";
        bool canRestart = _held != null && _host.CanRestartAndJoin(_held);
        AskOrRelease(new PreJoinQuestion
        {
            Kind = KindRestart,
            Key = _heldKey,
            Title = "This update needs a restart",
            Body = body,
            Note = canRestart ? "" : "Restart V Rising to load it; this join uses the version you have now.",
            MainCaption = canRestart ? "Restart and join" : "Join with the current version",
            ExtraCaption = canRestart ? "Join with the current version" : "",
            CancelCaption = "Cancel",
        }, answer =>
        {
            if (answer == KeyMain && canRestart)
            {
                var held = _held;
                string error = "nothing is held";
                if (held != null && _host.RestartAndJoin(held, out error))
                {
                    _host.Log("AutoUpdate: pre-join: restarting to load the update, then joining.");
                    ClearHold("restart and join");
                    return;
                }

                AskFailed("Restart and join could not start: " + error + ".");
                return;
            }

            if (answer == KeyMain || answer == KeyExtra) Relaunch("join with the current version");
            else Release("cancelled; the update loads at the next start");
        });
    }

    private void Relaunch(string why)
    {
        var held = _held;
        ClearHold(why);
        if (held == null) return;
        LastResult = why + (LastResult.Length > 0 ? " (" + LastResult + ")" : "");
        _host.Launch(held, why);
    }

    /// <summary>Drop the hold without launching (Cancel, a dropped dialog, a stale hold).</summary>
    public void Release(string why)
    {
        if (_held == null && State == "idle") return;
        ClearHold(why);
        LastResult = why;
        _host.Log("AutoUpdate: pre-join: the held join was released, nothing launched (" + why + ").");
    }

    private void ClearHold(string why)
    {
        _token++;
        _held = null;
        _heldKey = "";
        State = "idle";
        Asking = "";
    }

    /// <summary>Core unload: a held join is dropped (announced), never launched by a dead generation.</summary>
    public void Unload()
    {
        if (_held != null)
            _host.Log("AutoUpdate: pre-join: a held join was dropped because Core is reloading; join again.");
        ClearHold("unload");
    }

    public int ForgetDismissed()
    {
        int n = _dismissed.Count;
        _dismissed.Clear();
        return n;
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["state"] = State,
        ["asking"] = Asking,
        ["held"] = _held != null,
        ["heldServer"] = _heldKey,
        ["lastDecision"] = _decision?.Describe(),
        ["lastResult"] = LastResult,
        ["dismissed"] = _dismissed.Count,
    };
}
