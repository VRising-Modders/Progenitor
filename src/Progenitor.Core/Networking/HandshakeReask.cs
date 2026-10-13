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

    /// <summary>The admins' log line when a soft re-ask got no answer.</summary>
    internal static string KeptConnectedLine(string name, int sends) =>
        "Handshake: ADMIN NOTICE: " + name + " has not re-answered after a Core generation change (" + sends.ToString(CultureInfo.InvariantCulture)
        + " request(s) sent); they answered when they joined, so they stay connected. Their game may be reloading its own Core, "
        + "or it has stopped answering; reconnecting them re-checks their mods.";
}
