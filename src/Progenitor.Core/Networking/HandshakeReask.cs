using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// REASK1 (live 2026-10-07): the users who answered the handshake successfully on this server this session, kept across
/// Core generations in a plain process slot (an AppDomain data string: user index and SteamID pairs, no entity). A Core
/// swap empties the handshake tables; without this, the re-ask after the swap treated a player who had joined fine as a
/// new join and, when their client was swapping its own Core at the same moment, kicked them with the
/// "install the Progenitor mod" message. Forgotten on disconnect and on a join (a join is always asked in full). Game-free.
/// </summary>
internal sealed class HandshakeSessionMemory
{
    internal const string ProcessKey = "progenitor.handshake.answered.v1";

    private readonly Func<string?> _read;
    private readonly Action<string> _write;

    internal HandshakeSessionMemory(Func<string?>? read = null, Action<string>? write = null)
    {
        _read = read ?? ReadProcess;
        _write = write ?? WriteProcess;
    }

    private static string? ReadProcess()
    {
        try { return AppDomain.CurrentDomain.GetData(ProcessKey) as string; }
        catch { return null; }
    }

    private static void WriteProcess(string value)
    {
        try { AppDomain.CurrentDomain.SetData(ProcessKey, value); }
        catch { /* best effort: without the slot a re-ask falls back to a join's ask */ }
    }

    /// <summary>The remembered pairs (user index → SteamID).</summary>
    internal Dictionary<int, ulong> Load() => Decode(_read());

    internal bool Contains(int userIndex, ulong platformId) =>
        userIndex >= 0 && platformId != 0 && Load().TryGetValue(userIndex, out ulong id) && id == platformId;

    internal void Remember(int userIndex, ulong platformId)
    {
        if (userIndex < 0 || platformId == 0) return;
        var all = Load();
        if (all.TryGetValue(userIndex, out ulong id) && id == platformId) return;
        all[userIndex] = platformId;
        _write(Encode(all));
    }

    internal void Forget(int userIndex)
    {
        var all = Load();
        if (all.Remove(userIndex)) _write(Encode(all));
    }

    internal void Clear() => _write("");

    internal static string Encode(IReadOnlyDictionary<int, ulong> pairs)
    {
        var sb = new StringBuilder();
        foreach (var pair in pairs)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(':').Append(pair.Value.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Malformed parts are skipped; at most 512 pairs.</summary>
    internal static Dictionary<int, ulong> Decode(string? text)
    {
        var all = new Dictionary<int, ulong>();
        if (string.IsNullOrEmpty(text)) return all;
        foreach (string part in text!.Split(';'))
        {
            if (all.Count >= 512) break;
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            if (!int.TryParse(part.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0) continue;
            if (!ulong.TryParse(part.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id) || id == 0) continue;
            all[index] = id;
        }

        return all;
    }
}

/// <summary>
/// RESEND1 (live 2026-10-14): what the server's handshake request said <b>to each user</b>, so a mid-session change of the
/// required table — a server plugin hot-loaded or unloaded from <c>progenitor-reload</c> — is re-sent to every connected
/// client whose handshake is ok. Before, the request went out only at a join or a Core generation change, so a client
/// never learned that a server part had loaded (its needs-server-part rows stayed gray) or gone (nothing paused).
/// <para>The stamp is kept per user (review M1): a join, soft re-ask or refresh inside the settle window updates only
/// that user, never hiding the change from the others. A user mid-join when the table changed is compared again when
/// its answer is accepted (M2). Each successful re-send counts one outstanding answer (L1), so back-to-back re-sends
/// cannot leave a later answer judged as a join. A swap (unload, then load) settles for <see cref="SettleSeconds"/>
/// and re-sends nothing when the rows end up as they were. Game-free.</para>
/// </summary>
internal sealed class HandshakeRequirementWatch
{
    /// <summary>How long a change settles before it is compared and re-sent (an unload and the load after it are one change).</summary>
    internal const double SettleSeconds = 1.0;

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _sent = new();
    private readonly Dictionary<int, int> _resends = new();
    private bool _scheduled;

    /// <summary>The rows as an order-free text: guid, version, level, rule and declared, one row per guid.</summary>
    internal static string Stamp(IReadOnlyList<Handshake.ModRecord>? rows)
    {
        if (rows == null || rows.Count == 0) return "";
        var parts = new List<string>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (string.IsNullOrEmpty(r.Guid)) continue;
            parts.Add(r.Guid.ToLowerInvariant() + "|" + r.Version + "|" + ((int)r.Level).ToString(CultureInfo.InvariantCulture)
                + "|" + ((int)r.VersionRule).ToString(CultureInfo.InvariantCulture) + "|" + (r.Declared ? "d" : "u"));
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    /// <summary>A request with these rows reached <paramref name="userIndex"/> (a join's ask, a re-ask, a refresh, a re-send).</summary>
    internal void NoteSent(int userIndex, string stamp)
    {
        if (userIndex < 0) return;
        lock (_gate) _sent[userIndex] = stamp ?? "";
    }

    /// <summary>
    /// True when <paramref name="userIndex"/> was sent a request whose rows differ from <paramref name="now"/>. A user never
    /// sent one this generation is not stale: whatever asks it next asks with the rows of then.
    /// </summary>
    internal bool IsStale(int userIndex, string now)
    {
        lock (_gate) return _sent.TryGetValue(userIndex, out var stamp) && !string.Equals(stamp, now ?? "", StringComparison.Ordinal);
    }

    /// <summary>The required table changed: true when a settle-then-compare should be scheduled (none is pending).</summary>
    internal bool TrySchedule()
    {
        lock (_gate)
        {
            if (_scheduled) return false;
            _scheduled = true;
            return true;
        }
    }

    /// <summary>The compare ran (or could not be posted): the next change schedules another. Called from a finally.</summary>
    internal void Unschedule()
    {
        lock (_gate) _scheduled = false;
    }

    /// <summary>True while a compare is waiting for its settle time.</summary>
    internal bool Scheduled
    {
        get { lock (_gate) return _scheduled; }
    }

    /// <summary>A requirement re-send reached <paramref name="userIndex"/>: one more answer is owed to it.</summary>
    internal void NoteResendSent(int userIndex)
    {
        if (userIndex < 0) return;
        lock (_gate) _resends[userIndex] = (_resends.TryGetValue(userIndex, out int n) ? n : 0) + 1;
    }

    /// <summary>An answer from <paramref name="userIndex"/> is being judged: true (and one fewer owed) when a re-send was owed.</summary>
    internal bool TakeResendAnswer(int userIndex)
    {
        lock (_gate)
        {
            if (!_resends.TryGetValue(userIndex, out int n) || n <= 0) return false;
            if (n == 1) _resends.Remove(userIndex);
            else _resends[userIndex] = n - 1;
            return true;
        }
    }

    /// <summary>How many re-send answers <paramref name="userIndex"/> still owes.</summary>
    internal int ResendsOwed(int userIndex)
    {
        lock (_gate) return _resends.TryGetValue(userIndex, out int n) ? n : 0;
    }

    /// <summary>The user disconnected: its stamp and owed answers go with the connection.</summary>
    internal void Forget(int userIndex)
    {
        lock (_gate)
        {
            _sent.Remove(userIndex);
            _resends.Remove(userIndex);
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _sent.Clear();
            _resends.Clear();
            _scheduled = false;
        }
    }
}

/// <summary>REASK1: how the server asks a connected user.</summary>
internal enum HandshakeAskKind
{
    /// <summary>A join (or a re-ask of a user who never answered): the full rules, the deadline and the kick.</summary>
    Join,

    /// <summary>
    /// A re-ask after a Core generation change of a user who answered this session: the request is sent again and
    /// retried with backoff; a missing answer never disconnects, and the join-time rules (Keep them out, the client mod
    /// policy's wait for a report) do not apply. An actual answer is still checked, and a failing one keeps them connected
    /// in place (NOKICK1, <see cref="HandshakeReask.MismatchOutcome"/>).
    /// </summary>
    SoftReask,
}

/// <summary>NOKICK1: what a failing handshake answer does (<see cref="HandshakeReask.MismatchOutcome"/>).</summary>
internal enum HandshakeMismatchOutcome
{
    /// <summary>A join, or a client-side change: the existing rules (install window, refusal, kick).</summary>
    Refuse,

    /// <summary>An answer to a server-initiated ask: accepted, told in place, enforced at their next join.</summary>
    KeepInPlace,
}

/// <summary>REASK1: the next step of a soft re-ask after one wait.</summary>
internal enum HandshakeReaskStep
{
    /// <summary>The user answered (or left): nothing more.</summary>
    Done,

    /// <summary>Send the request again and wait the next delay.</summary>
    Resend,

    /// <summary>No answer after every retry: keep the user connected and tell the admins in the log.</summary>
    KeepConnected,
}

/// <summary>REASK1: the re-ask's decisions. Game-free.</summary>
internal static class HandshakeReask
{
    /// <summary>The waits between the soft re-ask's sends, in seconds (the first send, then one resend after each).</summary>
    internal static readonly IReadOnlyList<double> RetryDelays = new[] { 5d, 10d, 20d, 40d };

    /// <summary>A re-ask (not a join) of a user remembered as answered this session is soft; everything else is a join's ask.</summary>
    internal static HandshakeAskKind KindFor(bool reask, bool rememberedAnswered) =>
        reask && rememberedAnswered ? HandshakeAskKind.SoftReask : HandshakeAskKind.Join;

    /// <summary>After wait <paramref name="attempt"/> (0 and up): done, resend, or give up and keep the user connected.</summary>
    internal static HandshakeReaskStep After(int attempt, bool answered, bool connected)
    {
        if (answered || !connected) return HandshakeReaskStep.Done;
        return attempt + 1 < RetryDelays.Count ? HandshakeReaskStep.Resend : HandshakeReaskStep.KeepConnected;
    }

    /// <summary>
    /// The client's refresh request after its own Core swap is answered when its handshake is ok, and also (REASK1) when
    /// the server is waiting on a soft re-ask of it: that request is what the client missed during its swap. Before,
    /// a server mid re-ask (status pending) dropped the refresh, so the client never saw the request it had to answer.
    /// </summary>
    internal static bool RefreshAllowed(bool handshakeOk, bool softReaskPending) => handshakeOk || softReaskPending;

    /// <summary>
    /// NOKICK1 (live 2026-10-17, scope decided in review): what a failing handshake answer does. Only an answer to a
    /// SERVER-initiated ask keeps the player connected in place: the soft re-ask after a server Core swap (a remembered
    /// user answering while it waits), or a requirement re-send after a server plugin's <c>Require</c> or hot-load (only ok
    /// users get one). The server changed, not their join. Every other failing answer keeps the existing flow (the install
    /// window, then the refusal): a join, and a re-answer caused by a client-side change (the client hot-unloaded a
    /// required mod, or swapped its own Core to a mismatched version).
    /// </summary>
    internal static HandshakeMismatchOutcome MismatchOutcome(bool softReaskPending, bool requirementResend) =>
        softReaskPending || requirementResend ? HandshakeMismatchOutcome.KeepInPlace : HandshakeMismatchOutcome.Refuse;

    /// <summary>
    /// NOKICK1: whether a connected user's status at a Core swap is remembered for the next generation (beyond the users
    /// who answered, who are remembered when they do): one let in without an answer because nothing required one.
    /// </summary>
    internal static bool RememberAcrossSwap(HandshakeAnswerWatch.Status status) => status == HandshakeAnswerWatch.Status.Unverified;

    /// <summary>
    /// NOKICK1: the game-free half of the swap's let-in memory: every connected user whose status
    /// <see cref="RememberAcrossSwap"/> is remembered in <paramref name="memory"/> (unless it already is). Returns how many.
    /// </summary>
    internal static int RememberLetIn(IEnumerable<(int Index, ulong PlatformId, bool Connected, HandshakeAnswerWatch.Status Status)> users,
        HandshakeSessionMemory memory)
    {
        int kept = 0;
        foreach (var u in users)
        {
            if (!u.Connected || !RememberAcrossSwap(u.Status) || memory.Contains(u.Index, u.PlatformId)) continue;
            memory.Remember(u.Index, u.PlatformId);
            if (memory.Contains(u.Index, u.PlatformId)) kept++;
        }

        return kept;
    }

    /// <summary>NOKICK1: the admins' log line for a failing answer that <see cref="MismatchOutcome"/> kept connected.</summary>
    internal static string KeptAfterRequirementChangeLine(string name, IReadOnlyList<string> problems) =>
        "Handshake: " + name + " no longer matches this server's mods after they changed while " + name + " was connected ("
        + string.Join("; ", problems) + "); they stay connected and are told in place. Only what needs those mods is missing "
        + "for them, and their next join is checked against the new list.";

    /// <summary>The admins' log line when a soft re-ask got no answer.</summary>
    internal static string KeptConnectedLine(string name, int sends) =>
        "Handshake: ADMIN NOTICE: " + name + " has not re-answered after a Core generation change (" + sends.ToString(CultureInfo.InvariantCulture)
        + " request(s) sent); they answered when they joined, so they stay connected. Their game may be reloading its own Core, "
        + "or it has stopped answering; reconnecting them re-checks their mods.";
}
