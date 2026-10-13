using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Per-user <b>awaiting-install</b> window (job FMAN18). Game-free, so the unit tests drive the
/// real state machine with a fake clock exactly as they do <see cref="HandshakeAnswerWatch"/>;
/// live code posts the expiry through <c>MainThread.PostDelayed</c> and calls
/// <see cref="TryExpire"/> from that callback.
///
/// <para><b>What it is for.</b> Until FMAN18 a client that lacked a <c>ServerMustHave</c> mod was
/// kicked at the first failing answer, even when the server had the very package on offer — the
/// contradiction evidence 172 found and job FMAN12 could only describe in the kick text. The
/// reloader loads any allowlisted plugin dropped into the reload folder within about two seconds
/// and prefab sync is Core's, not the plugin's, so a late plugin load disturbs nothing. The kick
/// is therefore <em>deferred</em>: the client is told what it is missing, given this window to
/// fetch and hot-load it, and its second <c>DeclareSelf</c> is re-evaluated from scratch by the
/// same code path as the first.</para>
///
/// <para><b>One grace per join, never extended.</b> A user index that has consumed its window
/// cannot be granted another one until it disconnects. That is the whole budget: a client that
/// keeps re-answering badly cannot hold a slot open, and the window's length is fixed at the
/// moment it opens so nothing inside the join can push it forward. This is deliberately unlike
/// <see cref="HandshakeAnswerWatch"/>, which re-arms on a Core-generation re-ask.</para>
///
/// <para><b>And a Core swap does not reset that budget</b> (job FMAN20, R50 finding 11). This
/// object is a Core static, so a collectible Core reload — a first-class, live-verified feature
/// here — hands the incoming generation an empty table while the player is still connected, and
/// the paragraph above would be false: the operator swaps Core, the new generation re-asks, the
/// same client answers badly again and gets a second sixty seconds. The mark is <b>re-derived
/// from the connection</b> rather than stored, because the only state that crosses a collectible
/// ALC unload is Bootstrap's content memory and extending that is a Bootstrap change: a user that
/// was already connected when this generation installed brought its join with it, so
/// <see cref="NoteCarriedOverJoin"/> spends that join's one window up front
/// (<c>Handshake.MarkCarriedOverInstallGrace</c>, called from <c>Install</c>). Fail-closed in the
/// direction that matters — at worst a player who never used a window loses the chance at one for
/// the rest of this join and is refused the way every client was before FMAN18.</para>
///
/// <para><b>Retargeted 2026-10-05.</b> Servers no longer send mod files, so the window no longer
/// widens any transfer gate: it holds a join open while the client fixes what failed on its own
/// side (a Thunderstore install, a kept copy, Turn on, Pause for this server) and re-answers. An
/// awaiting user is never added to <c>ModManifest.HandshakeOkUsers</c>.</para>
/// </summary>
internal sealed class HandshakeInstallGrace
{
    /// <summary>Default <c>Networking.InstallGraceSeconds</c>. 0 disables the whole feature.</summary>
    public const int DefaultGraceSeconds = 60;

    /// <summary>Why <see cref="Begin"/> did or did not open a window.</summary>
    public enum Grant : byte
    {
        /// <summary>The window is open; the caller must send the deferred Result and arm the expiry.</summary>
        Opened = 0,

        /// <summary>This user already spent its one window on this join. Kick as before.</summary>
        AlreadyUsed = 1,

        /// <summary>Configured off (<c>InstallGraceSeconds</c> 0 or negative). Kick as before.</summary>
        Disabled = 2,

        /// <summary>No id to wait for. Kick as before.</summary>
        NothingToWaitFor = 3,
    }

    public readonly struct GrantResult
    {
        public GrantResult(Grant grant, int generation, int graceSeconds)
        {
            Verdict = grant;
            Generation = generation;
            GraceSeconds = graceSeconds;
        }

        public Grant Verdict { get; }

        /// <summary>Guards the delayed expiry post; a stale post is a no-op.</summary>
        public int Generation { get; }

        public int GraceSeconds { get; }

        public bool Opened => Verdict == Grant.Opened;
    }

    public readonly struct DueResult
    {
        public DueResult(int userIndex, string name, string reason, IReadOnlyList<string> ids, int graceSeconds)
        {
            UserIndex = userIndex;
            Name = name;
            Reason = reason;
            Ids = ids;
            GraceSeconds = graceSeconds;
        }

        public int UserIndex { get; }
        public string Name { get; }
        public string Reason { get; }
        public IReadOnlyList<string> Ids { get; }
        public int GraceSeconds { get; }
    }

    private sealed class Row
    {
        public Row(int generation, string name, string[] ids, double dueSeconds, int graceSeconds)
        {
            Generation = generation;
            Name = name;
            Ids = ids;
            DueSeconds = dueSeconds;
            GraceSeconds = graceSeconds;
        }

        public int Generation { get; }
        public string Name { get; }
        public string[] Ids { get; }
        public double DueSeconds { get; }
        public int GraceSeconds { get; }
    }

    private readonly Func<double> _clock;
    private readonly object _gate = new();

    /// <summary>Open windows, by user index.</summary>
    private readonly Dictionary<int, Row> _awaiting = new();

    /// <summary>
    /// User indices whose one window has been opened on this join, whether it is still open or
    /// already settled. Cleared only by <see cref="OnDisconnect"/> and <see cref="Clear"/> — a
    /// settled window must not be re-openable, which is what "one grace per join" means.
    /// </summary>
    private readonly HashSet<int> _used = new();

    private int _nextGeneration = 1;

    public HandshakeInstallGrace(Func<double>? clock = null) => _clock = clock ?? (() => 0);

    public double Now() => _clock();

    /// <summary>
    /// Open the window for <paramref name="userIndex"/>. <paramref name="fixableIds"/> are the rows
    /// this client failed and can fix during the join — the caller has already decided that every
    /// failing row is one of them (<see cref="Handshake.Compare.DecideMiss"/>).
    /// </summary>
    public GrantResult Begin(int userIndex, string? name, IReadOnlyList<string>? fixableIds, int graceSeconds)
    {
        var ids = Copy(fixableIds);
        lock (_gate)
        {
            if (graceSeconds <= 0) return new GrantResult(Grant.Disabled, 0, graceSeconds);
            if (ids.Length == 0) return new GrantResult(Grant.NothingToWaitFor, 0, graceSeconds);
            if (_used.Contains(userIndex)) return new GrantResult(Grant.AlreadyUsed, 0, graceSeconds);

            int generation = _nextGeneration++;
            _used.Add(userIndex);
            _awaiting[userIndex] = new Row(generation, name ?? "", ids, _clock() + graceSeconds, graceSeconds);
            return new GrantResult(Grant.Opened, generation, graceSeconds);
        }
    }

    /// <summary>
    /// Job FMAN20 (R50 finding 11). Spend this user's one window without opening one: it belongs
    /// to a join that began in an earlier Core generation, whose table did not survive the swap.
    /// True when this call is what spent it, so the caller can count and log.
    /// </summary>
    public bool NoteCarriedOverJoin(int userIndex)
    {
        lock (_gate) return _used.Add(userIndex);
    }

    public bool IsAwaiting(int userIndex)
    {
        lock (_gate) return _awaiting.ContainsKey(userIndex);
    }

    /// <summary>
    /// True once this user has opened its one window on this join, whether or not it is still
    /// open. <c>OnResponse</c> passes the negation as <c>graceAvailable</c> so the deferral
    /// decision and the state machine agree without asking twice.
    /// </summary>
    public bool HasSpentGrace(int userIndex)
    {
        lock (_gate) return _used.Contains(userIndex);
    }

    /// <summary>Ids this user's open window covers, or empty.</summary>
    public IReadOnlyList<string> IdsOf(int userIndex)
    {
        lock (_gate)
            return _awaiting.TryGetValue(userIndex, out var row) ? (string[])row.Ids.Clone() : Array.Empty<string>();
    }

    /// <summary>
    /// A second answer arrived: close the window (the outcome — admit or kick — is the caller's).
    /// The grace stays spent, so a third answer cannot reopen it.
    /// </summary>
    public bool Settle(int userIndex)
    {
        lock (_gate) return _awaiting.Remove(userIndex);
    }

    /// <summary>
    /// Live delayed-post callback. Fires only when <paramref name="generation"/> is still this
    /// user's open window and the clock has reached the due time. A settled window, a disconnect
    /// and a stale post all return false.
    /// </summary>
    public bool TryExpire(int userIndex, int generation, out DueResult due)
    {
        due = default;
        lock (_gate)
        {
            if (!_awaiting.TryGetValue(userIndex, out var row)) return false;
            if (row.Generation != generation) return false;
            if (_clock() < row.DueSeconds) return false;
            _awaiting.Remove(userIndex);
            due = new DueResult(
                userIndex,
                row.Name,
                Handshake.Compare.NotInstalledReason(row.Ids, row.GraceSeconds),
                (string[])row.Ids.Clone(),
                row.GraceSeconds);
            return true;
        }
    }

    /// <summary>
    /// Drop every trace of this user, window and spent-grace mark alike. A later delayed post
    /// cannot then kick a new occupant of the same <c>User.Index</c>, and a genuine reconnect
    /// gets its own window.
    /// </summary>
    public void OnDisconnect(int userIndex)
    {
        lock (_gate)
        {
            _awaiting.Remove(userIndex);
            _used.Remove(userIndex);
        }
    }

    public int AwaitingCount
    {
        get { lock (_gate) return _awaiting.Count; }
    }

    /// <summary>
    /// Drop the whole table. Core <c>Uninstall</c> and the test reset call it; a still-connected
    /// user's spent mark is put back by <see cref="NoteCarriedOverJoin"/> at the next
    /// <c>Install</c>, which is what keeps "one grace per join" true across a Core swap.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _awaiting.Clear();
            _used.Clear();
        }
    }

    private static string[] Copy(IReadOnlyList<string>? ids)
    {
        if (ids == null || ids.Count == 0) return Array.Empty<string>();
        var list = new List<string>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            if (string.IsNullOrEmpty(id)) continue;
            bool dup = false;
            for (int j = 0; j < list.Count; j++)
                if (string.Equals(list[j], id, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup) list.Add(id);
        }
        return list.ToArray();
    }
}

/// <summary>
/// Per-user rate bound on <b>re-answers</b> of the handshake (job FMAN21, R52 finding 3).
///
/// <para>A second <c>DeclareSelf</c> from a user the server has already marked ok is an event, not
/// a duplicate: the only thing that makes a client re-answer on a live connection is that its own
/// state changed — an install window's package loaded, or a client-side Core swap rebuilt the
/// tables that hold what the server sent it. So the server re-runs the post-ok side effects that
/// are safe to re-run, which is how a client gets its prefab edit layers back after a swap the
/// server cannot see.</para>
///
/// <para><b>Which is exactly why it needs a bound.</b> Re-answering is free for the client, and
/// what it asks for is not: a full serialization of the server's synced config, a catalog
/// rebuild, and a layer walk plus a packet per prefab — all on the server's main thread, on the
/// receive path. Without a floor between them a client could ask for that as fast as it can write
/// to a socket. One per <see cref="MinSecondsBetween"/> seconds per user index: far more often
/// than any real cause of a re-answer, far too seldom to be a lever.</para>
///
/// <para><b>The bound covers the whole post-ok set, not the resend alone</b> (job FMAN22, R53
/// finding 3): <see cref="Handshake.RunPostOkEffects"/> takes one admission for all three effects,
/// and the resend — the only one already deduped per user per payload hash — was the cheapest of
/// them, so bounding it by itself left the amplifier in place. What is <em>not</em> bounded is the
/// reply: an accepted answer still gets its <c>Result</c> packet every time, because refusing to
/// answer a client at all is a worse failure than answering it twice. Nor is a <b>first</b>
/// answer, which never consults this object and never spends a slot in it.</para>
/// </summary>
internal sealed class HandshakeReAnswer
{
    /// <summary>Seconds a user index must leave between two re-answer side-effect runs.</summary>
    public const double MinSecondsBetween = 5.0;

    private readonly Func<double> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<int, double> _last = new();

    public HandshakeReAnswer(Func<double>? clock = null) => _clock = clock ?? (() => 0);

    /// <summary>
    /// True when this user's re-answer may run the side effects. The clock is only read here, so a
    /// user that is refused does not push its own next chance forward.
    /// </summary>
    public bool TryAdmit(int userIndex)
    {
        double now = _clock();
        lock (_gate)
        {
            if (_last.TryGetValue(userIndex, out double last)
                && now >= last
                && now - last < MinSecondsBetween)
            {
                return false;
            }

            _last[userIndex] = now;
            return true;
        }
    }

    /// <summary>The index is free for whoever occupies it next.</summary>
    public void OnDisconnect(int userIndex)
    {
        lock (_gate) _last.Remove(userIndex);
    }

    public void Clear()
    {
        lock (_gate) _last.Clear();
    }
}
