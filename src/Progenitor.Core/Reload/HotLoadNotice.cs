using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Progenitor.Plugins;

namespace Progenitor.Reload;

/// <summary>Bootstrap's last Core reload as <c>CoreReloadStatus.FormatLastReload</c> prints it. Plain strings.</summary>
internal sealed class HotLoadCoreStatus
{
    /// <summary>
    /// A <c>scheduled</c> outcome older than this is not read as a swap in flight (a status that never got its result must
    /// not silence plugin events for the rest of the process).
    /// </summary>
    internal const double ScheduledStaleSeconds = 120;

    internal string Outcome { get; init; } = "";
    internal string Reason { get; init; } = "";
    internal string Timestamp { get; init; } = "";

    /// <summary>
    /// A swap is scheduled or running: <c>CoreLoader.IsReloading</c>, or the outcome <c>scheduled</c> (recorded when the
    /// swap is requested and replaced by its result only after the plugins reloaded, so it covers the whole cascade,
    /// where <c>IsReloading</c> already reads false).
    /// </summary>
    internal bool InFlight { get; init; }

    /// <summary>A developer's dry run: it reloads Core into a fresh context, but nothing changed.</summary>
    internal bool DryRun => Reason.IndexOf("dryRun", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Parses <c>lastReload gen=N outcome=X reason=R timestamp=T</c>. The reason may hold spaces, so it is read between
    /// <c> reason=</c> and the last <c> timestamp=</c>. Null when the text is not that line.
    /// </summary>
    internal static HotLoadCoreStatus? Parse(string? line, bool probeInFlight, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int o = line!.IndexOf(" outcome=", StringComparison.Ordinal);
        int r = line.IndexOf(" reason=", StringComparison.Ordinal);
        int t = line.LastIndexOf(" timestamp=", StringComparison.Ordinal);
        if (o < 0 || r < o || t < r) return null;
        string outcome = line.Substring(o + 9, r - o - 9).Trim();
        string stamp = line.Substring(t + 11).Trim();
        bool scheduled = string.Equals(outcome, "scheduled", StringComparison.Ordinal);
        if (scheduled && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && (nowUtc - at.ToUniversalTime()).TotalSeconds > ScheduledStaleSeconds)
            scheduled = false;
        return new HotLoadCoreStatus
        {
            Outcome = outcome,
            Reason = line.Substring(r + 8, t - r - 8).Trim(),
            Timestamp = stamp,
            InFlight = probeInFlight || scheduled,
        };
    }

    /// <summary>
    /// The cascade decision for one plugin event: a Core swap is in flight when Bootstrap's probe says so or its status
    /// line reads <c>scheduled</c>. <c>HotLoadNoticeLive</c> calls this with the raw probe answer and status line.
    /// </summary>
    internal static bool IsCascade(bool probeInFlight, string? statusLine, DateTime nowUtc) =>
        probeInFlight || (Parse(statusLine, false, nowUtc)?.InFlight ?? false);
}

/// <summary>One sentence of a hot-load line and the mod it is about (the merge key of a server's rate limit).</summary>
internal readonly struct HotLoadSentence
{
    internal HotLoadSentence(string subject, string text)
    {
        Subject = subject ?? "";
        Text = text ?? "";
    }

    internal string Subject { get; }
    internal string Text { get; }
}

/// <summary>
/// Hot-load chat lines (docs/hot-reload.md, "Hot-load messages"): what changed when Core or a reload-folder plugin loaded,
/// reloaded or unloaded while the game runs, as one short line per burst. Game-free; <c>HotLoadNoticeLive</c> feeds it
/// the plugin events and Bootstrap's reload status and sends the line.
///
/// <list type="bullet">
/// <item><b>Coalescing.</b> Events gather into a batch; the line is built once nothing happened for
/// <see cref="QuietSeconds"/>, and a batch with a Core swap waits at least <see cref="CoreSettleSeconds"/> from its start,
/// so plugins a deploy copies after the swap join the same line.</item>
/// <item><b>Startup.</b> A fresh process (no carried state, or state of another format) is quiet until
/// <see cref="StartupMinSeconds"/> have passed and nothing loaded for <see cref="StartupQuietSeconds"/>; the loads it sees
/// then only teach it the running versions.</item>
/// <item><b>Joins.</b> The caller marks events during a join silent (they only update the versions) and holds the line
/// while the join runs (<c>Poll(…, holdForJoin)</c>). A per-server pause (<see cref="PluginUnloadReason.Suspend"/>) and
/// the load that ends it are silent too. Process exit ends everything.</item>
/// <item><b>Core swaps.</b> Plugin events while a swap runs are the swap's cascade: an unload is ignored, and a load is named
/// only when its version changed. A new Core generation is built with the old one's <see cref="Save"/> and names the swap
/// itself from the reload status (<c>ok</c>: updated or reloaded, <c>restored</c>: failed; a dry run: nothing). A refused
/// or failed swap seen in the running generation is named once per outcome and reason.</item>
/// </list>
/// </summary>
internal sealed class HotLoadNotices
{
    internal const double QuietSeconds = 1.0;
    internal const double CoreSettleSeconds = 5.0;
    internal const double StartupMinSeconds = 20.0;
    internal const double StartupQuietSeconds = 5.0;

    /// <summary>A swap whose status never settles is named as done after this long.</summary>
    internal const double CoreDecideTimeoutSeconds = 60.0;

    /// <summary>The first line of <see cref="Save"/>; other state is ignored and the generation starts as at process start.</summary>
    internal const string FormatTag = "v1";

    internal const string ProgenitorName = "Progenitor";

    private enum Final { Loaded, Failed, Unloaded, Removed }

    private sealed class Entry
    {
        internal string Id = "";
        internal string Name = "";
        internal string? Before;
        internal Final State;
        internal string Version = "";
        internal bool LoadedInBatch;
        internal bool SawFailure;
        internal string FailedVersion = "";
        internal bool Live;
    }

    private readonly Dictionary<string, (string Name, string Version)> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suspended = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _batch = new();
    private readonly List<HotLoadSentence> _carried = new();
    private readonly string _coreVersion;
    private readonly double _installedAt;

    private bool _startup;
    private bool _shutdown;
    private double _lastAny;
    private double _lastEvent;
    private double _batchStart = -1;

    private bool _swapPending;
    private string _swapFrom = "";
    private HotLoadSentence? _coreLine;
    private bool _coreInBatch;
    private string? _lastTimestamp;
    private string _lastFailureKey = "";

    /// <param name="carried">The previous Core generation's <see cref="Save"/> in this process, or null at process start.</param>
    internal HotLoadNotices(double now, string coreVersion, string? carried)
    {
        _coreVersion = (coreVersion ?? "").Trim();
        _installedAt = now;
        _lastAny = now;
        if (carried == null || !Restore(carried))
        {
            _startup = true;
            return;
        }

        _swapPending = true;
        _coreInBatch = true;
        _batchStart = now;
        _lastEvent = now;
    }

    /// <summary>True until the start-of-process loads settled.</summary>
    internal bool InStartup => _startup;

    /// <summary>True while something waits to be said.</summary>
    internal bool Pending => _batch.Count > 0 || _coreInBatch || _carried.Count > 0;

    /// <summary>The running version of a reload-folder plugin as last seen, or null.</summary>
    internal string? KnownVersion(string id) => _known.TryGetValue(id ?? "", out var k) ? k.Version : null;

    // ---- events ---------------------------------------------------------------------------------------------

    internal void Loaded(string id, string name, string version, double now, bool cascade, bool silent) =>
        Note(id, name, version, now, cascade, silent, Final.Loaded, PluginUnloadReason.Unknown);

    internal void Failed(string id, string name, string version, double now, bool cascade, bool silent) =>
        Note(id, name, version, now, cascade, silent, Final.Failed, PluginUnloadReason.Unknown);

    internal void Unloaded(string id, string name, string version, PluginUnloadReason reason, double now, bool cascade, bool silent) =>
        Note(id, name, version, now, cascade, silent, reason == PluginUnloadReason.Removal ? Final.Removed : Final.Unloaded, reason);

    private void Note(string id, string name, string version, double now, bool cascade, bool silent, Final what, PluginUnloadReason reason)
    {
        if (_shutdown || string.IsNullOrWhiteSpace(id)) return;
        id = id.Trim();
        name = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        version = (version ?? "").Trim();
        _lastAny = now;

        bool unload = what is Final.Unloaded or Final.Removed;
        if (unload && reason == PluginUnloadReason.Shutdown)
        {
            _shutdown = true;
            _batch.Clear();
            _carried.Clear();
            _coreInBatch = false;
            _swapPending = false;
            return;
        }

        // Per-server pause: the unload and the load that ends it are not hot loads.
        if (unload && reason == PluginUnloadReason.Suspend)
        {
            _suspended.Add(id);
            return;
        }

        if (what == Final.Loaded && _suspended.Remove(id))
        {
            _known[id] = (name, version);
            return;
        }

        if (_startup || silent)
        {
            if (what == Final.Loaded) _known[id] = (name, version);
            else if (what == Final.Removed) _known.Remove(id);
            return;
        }

        // A Core swap unloads every Core-bound plugin and loads it again; the unload says nothing by itself.
        if (unload && cascade) return;

        var e = Find(id);
        if (e == null)
        {
            e = new Entry { Id = id, Name = name, Before = KnownVersion(id) };
            _batch.Add(e);
        }

        e.Name = name;
        e.Live |= !cascade;
        switch (what)
        {
            case Final.Loaded:
                e.State = Final.Loaded;
                e.Version = version;
                e.LoadedInBatch = true;
                _known[id] = (name, version);
                break;
            case Final.Failed:
                e.State = Final.Failed;
                e.SawFailure = true;
                e.FailedVersion = version;
                e.Live = true;
                break;
            case Final.Removed:
                e.State = Final.Removed;
                _known.Remove(id);
                break;
            default:
                e.State = Final.Unloaded;
                break;
        }

        Touch(now);
    }

    private void Touch(double now)
    {
        if (_batchStart < 0) _batchStart = now;
        _lastEvent = now;
    }

    private Entry? Find(string id)
    {
        foreach (var e in _batch)
            if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // ---- the line -------------------------------------------------------------------------------------------

    /// <summary>The line to send now, or null (<see cref="PollSentences"/> joined).</summary>
    internal string? Poll(double now, HotLoadCoreStatus? status, bool holdForJoin = false)
    {
        var sentences = PollSentences(now, status, holdForJoin);
        return sentences == null ? null : HotLoadNoticeText.Join(sentences);
    }

    /// <summary>
    /// Once a frame or so: the sentences to send now, or null. <paramref name="status"/> is Bootstrap's last Core reload
    /// (null: no Bootstrap, or it cannot say). <paramref name="holdForJoin"/> keeps a settled batch until a join finished.
    /// </summary>
    internal List<HotLoadSentence>? PollSentences(double now, HotLoadCoreStatus? status, bool holdForJoin = false)
    {
        if (_shutdown) return null;
        if (_startup)
        {
            _lastTimestamp = status?.Timestamp;
            if (now - _installedAt >= StartupMinSeconds && now - _lastAny >= StartupQuietSeconds) _startup = false;
            return null;
        }

        if (_swapPending)
        {
            if (status != null && status.InFlight && now - _batchStart < CoreDecideTimeoutSeconds) return null;
            _swapPending = false;
            _lastTimestamp = status?.Timestamp;
            bool restored = status != null && string.Equals(status.Outcome, "restored", StringComparison.Ordinal);
            if (status != null && status.DryRun) _coreInBatch = _batch.Count > 0 || _carried.Count > 0;
            else
                _coreLine = new HotLoadSentence(ProgenitorName, restored ? FailedToUpdate(ProgenitorName)
                    : _swapFrom.Length > 0 && !string.Equals(_swapFrom, _coreVersion, StringComparison.Ordinal) ? UpdatedTo(ProgenitorName, _coreVersion)
                    : Reloaded(ProgenitorName, _coreVersion));
        }
        else if (status != null && !string.Equals(status.Timestamp, _lastTimestamp, StringComparison.Ordinal))
        {
            _lastTimestamp = status.Timestamp;
            if (IsFailure(status.Outcome) && !status.DryRun)
            {
                string key = status.Outcome + "|" + status.Reason;
                if (!string.Equals(key, _lastFailureKey, StringComparison.Ordinal))
                {
                    _lastFailureKey = key;
                    _coreLine = new HotLoadSentence(ProgenitorName, FailedToUpdate(ProgenitorName));
                    _coreInBatch = true;
                    Touch(now);
                }
            }
        }

        if (!Pending) return null;
        if (now - _lastEvent < QuietSeconds) return null;
        if (_coreInBatch && now - _batchStart < CoreSettleSeconds) return null;
        if (status != null && status.InFlight) return null;
        if (holdForJoin) return null;
        return Take();
    }

    private static bool IsFailure(string outcome) =>
        string.Equals(outcome, "refused", StringComparison.Ordinal) || string.Equals(outcome, "failed", StringComparison.Ordinal)
        || string.Equals(outcome, "restored", StringComparison.Ordinal);

    /// <summary>Builds the sentences from the batch and empties it. Null when nothing in it is worth a line.</summary>
    private List<HotLoadSentence>? Take()
    {
        var sentences = Current();
        _carried.Clear();
        _batch.Clear();
        _coreLine = null;
        _coreInBatch = false;
        _batchStart = -1;
        return sentences.Count == 0 ? null : sentences;
    }

    private List<HotLoadSentence> Current()
    {
        var sentences = new List<HotLoadSentence>(_carried);
        if (_coreLine is { } core) sentences.Add(core);
        foreach (var e in _batch)
        {
            string? s = Sentence(e);
            if (s != null) sentences.Add(new HotLoadSentence(e.Name, s));
        }

        return sentences;
    }

    private static string? Sentence(Entry e)
    {
        switch (e.State)
        {
            case Final.Loaded:
                bool same = e.Before != null && string.Equals(e.Before, e.Version, StringComparison.Ordinal);
                // The swap's own reload of an unchanged (or not yet known) plugin is the Core line's business.
                if (!e.Live && (same || e.Before == null)) return null;
                if (e.SawFailure && same && !string.Equals(e.FailedVersion, e.Version, StringComparison.Ordinal))
                    return FailedToUpdate(e.Name);
                if (e.Before == null) return e.Version.Length > 0 ? e.Name + " added (" + e.Version + ")." : e.Name + " added.";
                if (!same) return UpdatedTo(e.Name, e.Version);
                return Reloaded(e.Name, e.Version);
            case Final.Failed:
                return e.Before == null ? e.Name + " failed to load." : e.Name + " failed to reload.";
            case Final.Removed:
                return e.LoadedInBatch && e.Before == null ? null : e.Name + " removed.";
            default:
                return e.Name + " failed to reload.";
        }
    }

    internal static string UpdatedTo(string name, string version) =>
        version.Length > 0 ? name + " updated to " + version + "." : name + " updated.";

    internal static string Reloaded(string name, string version) =>
        version.Length > 0 ? name + " reloaded (" + version + ")." : name + " reloaded.";

    internal static string FailedToUpdate(string name) => name + " failed to update; the previous version keeps running.";

    // ---- across a Core swap ----------------------------------------------------------------------------------

    /// <summary>
    /// What the next Core generation in this process needs: this Core's version, the running plugin versions, and the
    /// sentences of a batch that had not settled yet (said by the next generation). Plain text, so nothing pins this one.
    /// </summary>
    internal string Save(double now)
    {
        var sb = new StringBuilder();
        sb.Append(FormatTag).Append('\n');
        sb.Append("core\t").Append(Clean(_coreVersion)).Append('\n');
        foreach (var k in _known)
            sb.Append("known\t").Append(Clean(k.Key)).Append('\t').Append(Clean(k.Value.Name)).Append('\t').Append(Clean(k.Value.Version)).Append('\n');
        foreach (string s in _suspended) sb.Append("paused\t").Append(Clean(s)).Append('\n');
        if (!_startup && !_shutdown)
            foreach (var line in Current())
                sb.Append("line\t").Append(Clean(line.Subject)).Append('\t').Append(Clean(line.Text)).Append('\n');

        return sb.ToString();
    }

    /// <summary>Sentences said before this batch's (a server line that was still waiting for its broadcast gap).</summary>
    internal void CarryForward(IEnumerable<HotLoadSentence> sentences)
    {
        if (_shutdown || sentences == null) return;
        _carried.InsertRange(0, sentences);
    }

    /// <summary>False (nothing read) when the state is not <see cref="FormatTag"/>.</summary>
    private bool Restore(string carried)
    {
        var lines = (carried ?? "").Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), FormatTag, StringComparison.Ordinal)) return false;
        for (int i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split('\t');
            if (f.Length >= 2 && f[0] == "core") _swapFrom = f[1].Trim();
            else if (f.Length >= 4 && f[0] == "known" && f[1].Length > 0) _known[f[1]] = (f[2], f[3]);
            else if (f.Length >= 2 && f[0] == "paused" && f[1].Length > 0) _suspended.Add(f[1]);
            else if (f.Length >= 3 && f[0] == "line" && f[2].Length > 0) _carried.Add(new HotLoadSentence(f[1], f[2]));
        }

        return true;
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>
/// The server's rate limit on hot-load broadcasts: at most one every <see cref="MinGapSeconds"/>; what arrives in between
/// waits and merges by mod (the latest sentence per mod wins); a sentence broadcast word for word within
/// <see cref="RepeatSeconds"/> is not sent again. Game-free.
/// </summary>
internal sealed class HotLoadBroadcastLimiter
{
    internal const double MinGapSeconds = 30;
    internal const double RepeatSeconds = 180;

    private readonly List<HotLoadSentence> _pending = new();
    private readonly Dictionary<string, double> _sentAt = new(StringComparer.Ordinal);
    private double _lastSent = double.NegativeInfinity;

    internal bool Pending => _pending.Count > 0;

    internal void Offer(IEnumerable<HotLoadSentence> sentences)
    {
        foreach (var s in sentences)
        {
            int at = _pending.FindIndex(p => string.Equals(p.Subject, s.Subject, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) _pending.RemoveAt(at);
            _pending.Add(s);
        }
    }

    /// <summary>What still waits for the gap, emptied (handed to the next Core generation).</summary>
    internal List<HotLoadSentence> TakePending()
    {
        var list = new List<HotLoadSentence>(_pending);
        _pending.Clear();
        return list;
    }

    /// <summary>The line to broadcast now, or null (waiting for the gap, or every sentence was a recent repeat).</summary>
    internal string? Take(double now)
    {
        if (_pending.Count == 0 || now - _lastSent < MinGapSeconds) return null;
        var fresh = new List<string>();
        foreach (var s in _pending)
            if (!(_sentAt.TryGetValue(s.Text, out double at) && now - at < RepeatSeconds)) fresh.Add(s.Text);
        _pending.Clear();
        if (fresh.Count == 0) return null;
        foreach (string text in fresh) _sentAt[text] = now;
        _lastSent = now;
        var old = new List<string>();
        foreach (var kv in _sentAt) if (now - kv.Value >= RepeatSeconds) old.Add(kv.Key);
        foreach (string k in old) _sentAt.Remove(k);
        return HotLoadNoticeText.Join(fresh);
    }
}

/// <summary>The hot-load line's text rules. Game-free.</summary>
internal static class HotLoadNoticeText
{
    /// <summary>What a server's hot-load line starts with on players' screens; what tells a client it is Progenitor's.</summary>
    internal const string ServerPrefix = "Server hot load: ";

    /// <summary>The longest line; sentences past it are counted instead.</summary>
    internal const int MaxChars = 300;

    internal static string? Join(IReadOnlyList<HotLoadSentence> sentences)
    {
        if (sentences == null) return null;
        var texts = new List<string>(sentences.Count);
        foreach (var s in sentences) texts.Add(s.Text);
        return Join(texts);
    }

    /// <summary>The sentences as one line (null when there are none); past <see cref="MaxChars"/> the rest are counted.</summary>
    internal static string? Join(IReadOnlyList<string> sentences)
    {
        if (sentences == null || sentences.Count == 0) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < sentences.Count; i++)
        {
            string s = sentences[i];
            int after = sentences.Count - i - 1;
            int need = (sb.Length > 0 ? 1 : 0) + s.Length + (after > 0 ? 1 + MoreText(after).Length : 0);
            if (sb.Length > 0 && sb.Length + need > MaxChars)
            {
                sb.Append(' ').Append(MoreText(sentences.Count - i));
                return sb.ToString();
            }

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }

        return sb.ToString();
    }

    internal static string MoreText(int count) =>
        count == 1 ? "1 more mod changed." : count.ToString(CultureInfo.InvariantCulture) + " more mods changed.";

    /// <summary>
    /// A client is joining: in a world on a held connection whose handshake has not answered <c>ok</c>, unless the client
    /// found the server has no Progenitor (that handshake never answers, and the client's own hot loads still count).
    /// </summary>
    internal static bool JoinInProgress(bool inWorld, bool connectionHeld, string? handshakeResult, bool serverWithoutProgenitor) =>
        inWorld && connectionHeld && !serverWithoutProgenitor && !string.Equals(handshakeResult, "ok", StringComparison.Ordinal);

    /// <summary>The line a server sends its players.</summary>
    internal static string ServerLine(string line) => ServerPrefix + line;

    private static readonly Regex Sentence = new(
        @"^(?:\d+ more mods? changed|.+? (?:removed|added(?: \([^)]*\))?|reloaded(?: \([^)]*\))?|updated(?: to \S+)?"
        + @"|failed to load|failed to reload|failed to update; the previous version keeps running))$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// True for a line Progenitor's server sent: <see cref="ServerPrefix"/> followed by hot-load sentences only. The caller
    /// checks that it came from the server (a System line with no sender), so a player cannot make one.
    /// </summary>
    internal static bool IsServerNotice(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text!.StartsWith(ServerPrefix, StringComparison.Ordinal)) return false;
        string body = text.Substring(ServerPrefix.Length).Trim();
        if (!body.EndsWith(".", StringComparison.Ordinal)) return false;
        body = body.Substring(0, body.Length - 1);
        foreach (string part in body.Split(new[] { ". " }, StringSplitOptions.None))
            if (part.Length == 0 || !Sentence.IsMatch(part)) return false;
        return true;
    }
}
