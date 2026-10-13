using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Per-user handshake answer deadline. Game-free so unit tests can drive it with a
/// fake clock; live code posts the due callback through <c>MainThread.PostDelayed</c>
/// (never <see cref="System.Threading.Timer"/>) and calls <see cref="TryExpire"/> from
/// that callback. A client that never answers must not stay connected when the server
/// actually requires a match — that was the Networking-disabled join (evidence 63).
/// </summary>
internal sealed class HandshakeAnswerWatch
{
    /// <summary>Default <c>Networking.HandshakeAnswerTimeoutSeconds</c>. 0 disables.</summary>
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>
    /// Same 2 s flush the mismatch kick uses so the result packet lands before
    /// <c>Disconnect</c>. Could-not-ask waits this long before sending Result, Arms
    /// the 20 s answer deadline, then waits it again: disconnect only when a mismatch
    /// owns the kick (<see cref="Handshake.Compare.DisconnectReasonAfterCouldNotAskResult"/>).
    /// Live code, not this watch, posts those delays.
    /// </summary>
    public const double KickFlushSeconds = 2.0;

    /// <summary>
    /// Delay for a one-shot ask retry when <c>SendToUser</c> fails. 0 is the next
    /// main-thread drain (the next frame).
    /// </summary>
    public const double AskRetryDelaySeconds = 0;

    public enum Status
    {
        None,
        Pending,
        Ok,
        Mismatch,
        Timeout,
        Unverified,

        /// <summary>
        /// Job FMAN18. The client answered, the answer failed, and <em>every</em> failing row is
        /// one this server has on offer — so instead of kicking, the server opened an install
        /// window (<see cref="HandshakeInstallGrace"/>) and is waiting for a second
        /// <c>DeclareSelf</c>. It is not an outcome: <see cref="HasAnswered"/> stays false,
        /// because the answer that matters has not arrived yet. It <em>is</em> terminal enough to
        /// stop <see cref="Arm"/> replacing it with a fresh 20 s answer deadline, which would kick
        /// the client a third of the way into its own 60 s window.
        /// </summary>
        AwaitingInstall,

        /// <summary>
        /// CLIENTPOLICY1. The answer passed, and the server waits (inside the answer deadline) for the client's mod
        /// report before the join is decided. Not an outcome, like <see cref="AwaitingInstall"/>: it only keeps a
        /// re-ask from replacing the wait with a fresh answer deadline.
        /// </summary>
        AwaitingReport,
    }

    private readonly struct Entry
    {
        public Entry(
            int generation,
            string name,
            Status status,
            double dueSeconds,
            int timeoutSeconds,
            string requiredList,
            string mismatchReason = "")
        {
            Generation = generation;
            Name = name;
            Status = status;
            DueSeconds = dueSeconds;
            TimeoutSeconds = timeoutSeconds;
            RequiredList = requiredList;
            MismatchReason = mismatchReason ?? "";
        }

        public int Generation { get; }
        public string Name { get; }
        public Status Status { get; }
        public double DueSeconds { get; }
        public int TimeoutSeconds { get; }
        public string RequiredList { get; }
        public string MismatchReason { get; }

        public Entry With(Status status) =>
            new(
                Generation,
                Name,
                status,
                DueSeconds,
                TimeoutSeconds,
                RequiredList,
                status == Status.Mismatch ? MismatchReason : "");

        public Entry WithMismatch(string reason) =>
            new(Generation, Name, Status.Mismatch, DueSeconds, TimeoutSeconds, RequiredList, reason ?? "");

        public Entry WithRequiredList(string requiredList) =>
            new(Generation, Name, Status, DueSeconds, TimeoutSeconds, requiredList ?? "", MismatchReason);
    }

    private readonly Func<double> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _byUser = new();
    private int _nextGeneration = 1;

    /// <param name="clock">
    /// Seconds. Tests pass a mutable fake; live code uses a monotonic process clock.
    /// </param>
    public HandshakeAnswerWatch(Func<double>? clock = null) =>
        _clock = clock ?? (() => 0);

    /// <summary>Current clock reading, for the live delayed callback to pass into <see cref="TryExpire"/>.</summary>
    public double Now() => _clock();

    /// <summary>
    /// Start or replace the deadline for <paramref name="userIndex"/>. Re-arm (join
    /// re-ask after a Core generation change) bumps the generation so an in-flight
    /// delayed post from the previous ask is a no-op. Ghosts and users with no
    /// network peer pass <paramref name="skipUser"/> and are never pending.
    /// </summary>
    public ArmResult Arm(
        int userIndex,
        string name,
        int timeoutSeconds,
        bool skipUser,
        bool serverRequiresAnswer,
        string requiredList)
    {
        name ??= "";
        requiredList ??= "";
        if (timeoutSeconds < 0) timeoutSeconds = 0;

        lock (_gate)
        {
            // A successful answer (or mismatch) must not be overwritten by a retry
            // SendToUser that later reports success, or by skip/unverified re-entry.
            // Re-arm after Uninstall Clear() has no row, so a real re-ask still schedules.
            // Job FMAN18: AwaitingInstall joins the two outcomes here. It is not an outcome, but
            // its own 60 s window already owns this user's fate, and a re-ask that replaced it
            // with a 20 s Pending row would kick the client mid-download with "no handshake
            // answer" — for a client that had already answered.
            if (_byUser.TryGetValue(userIndex, out var existing)
                && (existing.Status == Status.Ok
                    || existing.Status == Status.Mismatch
                    || existing.Status == Status.AwaitingInstall
                    || existing.Status == Status.AwaitingReport))
            {
                return new ArmResult(
                    Scheduled: false,
                    Skipped: false,
                    Unverified: false,
                    Disabled: false,
                    existing.Generation);
            }

            int generation = _nextGeneration++;
            if (skipUser)
            {
                _byUser.Remove(userIndex);
                return new ArmResult(Scheduled: false, Skipped: true, Unverified: false, Disabled: false, generation);
            }

            if (!serverRequiresAnswer)
            {
                _byUser[userIndex] = new Entry(generation, name, Status.Unverified, 0, timeoutSeconds, requiredList);
                return new ArmResult(Scheduled: false, Skipped: false, Unverified: true, Disabled: false, generation);
            }

            if (timeoutSeconds == 0)
            {
                _byUser.Remove(userIndex);
                return new ArmResult(Scheduled: false, Skipped: false, Unverified: false, Disabled: true, generation);
            }

            double due = _clock() + timeoutSeconds;
            _byUser[userIndex] = new Entry(generation, name, Status.Pending, due, timeoutSeconds, requiredList);
            return new ArmResult(Scheduled: true, Skipped: false, Unverified: false, Disabled: false, generation);
        }
    }

    /// <summary>The client answered and matched. Cancels a pending deadline; a late answer after timeout still becomes <see cref="Status.Ok"/> so the 2 s kick can back off.</summary>
    public void OnAnswer(int userIndex)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userIndex, out var entry))
                _byUser[userIndex] = entry.With(Status.Ok);
            else
                _byUser[userIndex] = new Entry(0, "", Status.Ok, 0, 0, "");
        }
    }

    /// <summary>
    /// The client answered and failed the required list. A timeout kick backs off; the
    /// mismatch kick proceeds. <paramref name="reason"/> is the live kick text so a
    /// could-not-ask disconnect hop that loses the race still uses
    /// <see cref="Handshake.Compare.DisconnectReasonAfterCouldNotAskResult"/>.
    /// </summary>
    public void OnMismatch(int userIndex, string? reason = null)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userIndex, out var entry))
                _byUser[userIndex] = entry.WithMismatch(reason ?? "");
            else
                _byUser[userIndex] = new Entry(0, "", Status.Mismatch, 0, 0, "", reason ?? "");
        }
    }

    /// <summary>
    /// Job FMAN18. The answer failed but the kick is deferred: this user is inside its install
    /// window. Cancels the pending answer deadline (<see cref="TryExpire"/> only fires on a
    /// <see cref="Status.Pending"/> row) without recording an outcome, so the second
    /// <c>DeclareSelf</c> is evaluated by exactly the same code path as the first.
    /// </summary>
    public void OnAwaitingInstall(int userIndex)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userIndex, out var entry))
                _byUser[userIndex] = entry.With(Status.AwaitingInstall);
            else
                _byUser[userIndex] = new Entry(0, "", Status.AwaitingInstall, 0, 0, "");
        }
    }

    /// <summary>CLIENTPOLICY1: the answer passed and the join waits for the client's mod report (cancels the answer deadline).</summary>
    public void OnAwaitingReport(int userIndex)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userIndex, out var entry))
                _byUser[userIndex] = entry.With(Status.AwaitingReport);
            else
                _byUser[userIndex] = new Entry(0, "", Status.AwaitingReport, 0, 0, "");
        }
    }

    /// <summary>
    /// Mod compatibility C3: the answer arrived and its judgment is held for the client's feature table. A
    /// <see cref="Status.Pending"/> row becomes <see cref="Status.AwaitingReport"/> (not an outcome; it stops the answer
    /// deadline from timing out a client that answered); any other row (an already-ok user's re-answer) is left alone.
    /// The held judgment then records the outcome as an unheld one would.
    /// </summary>
    public void OnAnswerHeld(int userIndex)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userIndex, out var entry) && entry.Status == Status.Pending)
                _byUser[userIndex] = entry.With(Status.AwaitingReport);
        }
    }

    /// <summary>Kick text captured by <see cref="OnMismatch"/>, or empty.</summary>
    public string MismatchReasonOf(int userIndex)
    {
        lock (_gate)
            return _byUser.TryGetValue(userIndex, out var entry) ? entry.MismatchReason : "";
    }

    /// <summary>Drop state on disconnect so a later delayed post cannot kick a new occupant of the same <c>User.Index</c>.</summary>
    public void OnDisconnect(int userIndex)
    {
        lock (_gate) _byUser.Remove(userIndex);
    }

    /// <summary>
    /// True when this user already produced a handshake outcome (match or mismatch).
    /// Same-ALC Install re-asks everyone else.
    /// </summary>
    public bool HasAnswered(int userIndex)
    {
        var status = GetStatus(userIndex);
        return status == Status.Ok || status == Status.Mismatch;
    }

    /// <summary>
    /// After <c>Unrequire</c> / <c>Require</c> during a wait-miss: refresh the kick
    /// reason on pending rows. Does not cancel a pending deadline here — an in-flight
    /// plugin reload may Require again before the due time. <see cref="TryExpire"/>
    /// re-checks <paramref name="serverRequiresAnswer"/> and cancels to
    /// <see cref="Status.Unverified"/> when nothing is left to enforce.
    /// </summary>
    public void ReevaluateRequired(bool serverRequiresAnswer, string requiredList)
    {
        requiredList ??= "";
        lock (_gate)
        {
            if (_byUser.Count == 0) return;
            var keys = new int[_byUser.Count];
            _byUser.Keys.CopyTo(keys, 0);
            for (int i = 0; i < keys.Length; i++)
            {
                var entry = _byUser[keys[i]];
                if (entry.Status != Status.Pending) continue;
                _byUser[keys[i]] = entry.WithRequiredList(
                    serverRequiresAnswer ? requiredList : "none");
            }
        }
    }

    /// <summary>
    /// Live delayed-post callback. Fires only when <paramref name="generation"/> is
    /// still the pending ask for this user and the clock has reached the due time.
    /// Re-arm, answer, disconnect, and a wait-miss <c>Unrequire</c> that left nothing
    /// to enforce all make this return false.
    /// </summary>
    public bool TryExpire(int userIndex, int generation, out DueResult due) =>
        TryExpire(userIndex, generation, serverStillRequiresAnswer: true, out due);

    /// <inheritdoc cref="TryExpire(int,int,out DueResult)"/>
    public bool TryExpire(int userIndex, int generation, bool serverStillRequiresAnswer, out DueResult due)
    {
        due = default;
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userIndex, out var entry)) return false;
            if (entry.Generation != generation) return false;
            if (entry.Status != Status.Pending) return false;
            if (!serverStillRequiresAnswer)
            {
                _byUser[userIndex] = entry.With(Status.Unverified);
                return false;
            }
            if (_clock() < entry.DueSeconds) return false;
            _byUser[userIndex] = entry.With(Status.Timeout);
            due = new DueResult(
                userIndex,
                entry.Name,
                Handshake.Compare.NoAnswerReason(entry.TimeoutSeconds, entry.RequiredList));
            return true;
        }
    }

    public Status GetStatus(int userIndex)
    {
        lock (_gate)
            return _byUser.TryGetValue(userIndex, out var entry) ? entry.Status : Status.None;
    }

    /// <summary>Current required-list snapshot for a pending row, or empty.</summary>
    public string RequiredListOf(int userIndex)
    {
        lock (_gate)
            return _byUser.TryGetValue(userIndex, out var entry) ? entry.RequiredList : "";
    }

    /// <summary>
    /// Bridge <c>users.handshake</c> token. Seven values:
    /// <c>none</c> (never asked / ghost / timeout disabled / disconnected — not waiting),
    /// <c>asked</c> (waiting for an answer; was <c>pending</c> before the rename),
    /// <c>ok</c>, <c>mismatch</c>, <c>timeout</c>, <c>unverified</c>
    /// (connected, and nothing on the server requires an answer), or
    /// <c>awaiting-install</c> (job FMAN18: answered, failed, and inside the install window).
    /// </summary>
    public string Token(int userIndex) => Token(GetStatus(userIndex));

    public static string Token(Status status) => status switch
    {
        Status.Pending => "asked",
        Status.Ok => "ok",
        Status.Mismatch => "mismatch",
        Status.Timeout => "timeout",
        Status.Unverified => "unverified",
        Status.AwaitingInstall => "awaiting-install",
        Status.AwaitingReport => "awaiting-report",
        _ => "none",
    };

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                int n = 0;
                foreach (var entry in _byUser.Values)
                    if (entry.Status == Status.Pending) n++;
                return n;
            }
        }
    }

    public void Clear()
    {
        lock (_gate) _byUser.Clear();
    }

    public readonly struct ArmResult
    {
        public ArmResult(bool Scheduled, bool Skipped, bool Unverified, bool Disabled, int Generation)
        {
            this.Scheduled = Scheduled;
            this.Skipped = Skipped;
            this.Unverified = Unverified;
            this.Disabled = Disabled;
            this.Generation = Generation;
        }

        public bool Scheduled { get; }
        public bool Skipped { get; }
        public bool Unverified { get; }
        public bool Disabled { get; }
        public int Generation { get; }
    }

    public readonly struct DueResult
    {
        public DueResult(int UserIndex, string Name, string Reason)
        {
            this.UserIndex = UserIndex;
            this.Name = Name;
            this.Reason = Reason;
        }

        public int UserIndex { get; }
        public string Name { get; }
        public string Reason { get; }
    }
}
