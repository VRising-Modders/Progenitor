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
/// RESEND1 (live 2026-10-14): what the server's handshake request said last, so a mid-session change of the required
/// table — a server plugin hot-loaded or unloaded from <c>progenitor-reload</c> — is re-sent to every connected client.
/// Before, the request went out only at a join or a Core generation change, so a client never learned that a server part
/// had loaded (its needs-server-part rows stayed gray) or gone (nothing paused). A swap (unload, then load) settles for
/// <see cref="SettleSeconds"/> and is re-sent only when the rows differ from the ones last sent. Game-free.
/// </summary>
internal sealed class HandshakeRequirementWatch
{
    /// <summary>How long a change settles before it is compared and re-sent (an unload and the load after it are one change).</summary>
    internal const double SettleSeconds = 1.0;

    private readonly object _gate = new();
    private string? _announced;
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

    /// <summary>A request with these rows went out (a join's ask, a re-ask, a refresh, a re-send).</summary>
    internal void NoteSent(string stamp)
    {
        lock (_gate) _announced = stamp ?? "";
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

    /// <summary>The scheduled compare could not be posted: let the next change schedule one.</summary>
    internal void Unschedule()
    {
        lock (_gate) _scheduled = false;
    }

    /// <summary>True while a compare is waiting for its settle time.</summary>
    internal bool Scheduled
    {
        get { lock (_gate) return _scheduled; }
    }

    /// <summary>
    /// The settled compare: true (and <paramref name="now"/> becomes what was sent) when the rows differ from the last
    /// request sent. Nothing sent yet this generation is not a change: whoever joins next is asked with the rows of then.
    /// </summary>
    internal bool Due(string now)
    {
        lock (_gate)
        {
            _scheduled = false;
            now ??= "";
            if (_announced == null)
            {
                _announced = now;
                return false;
            }

            if (string.Equals(_announced, now, StringComparison.Ordinal)) return false;
            _announced = now;
            return true;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _announced = null;
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
    /// policy's wait for a report) do not apply. An actual answer is still checked.
    /// </summary>
    SoftReask,
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
    /// RESEND1: the judgment of an answer to a requirement re-send (<see cref="HandshakeRequirementWatch"/>) from a user
    /// whose handshake was already ok. A mismatch keeps them connected (a server mod loading mid-session is not their
    /// join); the next join is judged in full.
    /// </summary>
    internal static bool KeepConnectedOnMismatch(bool requirementResend, bool wasAlreadyOk) => requirementResend && wasAlreadyOk;

    /// <summary>RESEND1: the admins' log line for a mismatch that <see cref="KeepConnectedOnMismatch"/> kept connected.</summary>
    internal static string KeptAfterRequirementChangeLine(string name, IReadOnlyList<string> problems) =>
        "Handshake: " + name + " no longer matches this server's mods after they changed mid-session ("
        + string.Join("; ", problems) + "); they stay connected, and their next join is checked against the new list.";

    /// <summary>The admins' log line when a soft re-ask got no answer.</summary>
    internal static string KeptConnectedLine(string name, int sends) =>
        "Handshake: ADMIN NOTICE: " + name + " has not re-answered after a Core generation change (" + sends.ToString(CultureInfo.InvariantCulture)
        + " request(s) sent); they answered when they joined, so they stay connected. Their game may be reloading its own Core, "
        + "or it has stopped answering; reconnecting them re-checks their mods.";
}
