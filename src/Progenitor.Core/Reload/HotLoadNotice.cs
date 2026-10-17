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
    internal string Outcome { get; init; } = "";
    internal string Reason { get; init; } = "";
    internal string Timestamp { get; init; } = "";

    /// <summary>A swap is scheduled or running (<c>CoreLoader.IsReloading</c>, or the outcome <c>scheduled</c>).</summary>
    internal bool InFlight { get; init; }

    /// <summary>
    /// Parses <c>lastReload gen=N outcome=X reason=R timestamp=T</c>. The reason may hold spaces, so it is read between
    /// <c> reason=</c> and the last <c> timestamp=</c>. Null when the text is not that line.
    /// </summary>
    internal static HotLoadCoreStatus? Parse(string? line, bool inFlight)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int o = line!.IndexOf(" outcome=", StringComparison.Ordinal);
        int r = line.IndexOf(" reason=", StringComparison.Ordinal);
        int t = line.LastIndexOf(" timestamp=", StringComparison.Ordinal);
        if (o < 0 || r < o || t < r) return null;
        string outcome = line.Substring(o + 9, r - o - 9).Trim();
        return new HotLoadCoreStatus
        {
            Outcome = outcome,
            Reason = line.Substring(r + 8, t - r - 8).Trim(),
            Timestamp = line.Substring(t + 11).Trim(),
            InFlight = inFlight || string.Equals(outcome, "scheduled", StringComparison.Ordinal),
        };
    }
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
/// <item><b>Startup.</b> A fresh process (no carried state) is quiet until <see cref="StartupMinSeconds"/> have passed and
/// nothing loaded for <see cref="StartupQuietSeconds"/>; the loads it sees then only teach it the running versions.</item>
/// <item><b>Silent events.</b> A join in progress (the caller says so) and a per-server pause (<see cref="PluginUnloadReason.Suspend"/>
/// and the load that ends it) also only update the versions. Process exit ends everything.</item>
/// <item><b>Core swaps.</b> Plugin events while a swap runs are the swap's cascade: an unload is ignored, and a load is named
/// only when its version changed. A new Core generation is built with the old one's <see cref="Save"/> and names the swap
/// itself from the reload status (<c>ok</c>: updated or reloaded, <c>restored</c>: failed). A refused or failed swap seen in
/// the running generation is named once per outcome and reason.</item>
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
    private readonly List<string> _carriedLines = new();
    private readonly string _coreVersion;
    private readonly double _installedAt;

    private bool _startup;
    private bool _shutdown;
    private double _lastAny;
    private double _lastEvent;
    private double _batchStart = -1;

    private bool _swapPending;
    private string _swapFrom = "";
    private string? _coreLine;
    private bool _coreInBatch;
    private string? _lastTimestamp;
    private string _lastFailureKey = "";

    /// <param name="carried">The previous Core generation's <see cref="Save"/> in this process, or null at process start.</param>
    internal HotLoadNotices(double now, string coreVersion, string? carried)
    {
        _coreVersion = (coreVersion ?? "").Trim();
        _installedAt = now;
        _lastAny = now;
        if (carried == null)
        {
            _startup = true;
            return;
        }

        Restore(carried);
        _swapPending = true;
        _coreInBatch = true;
        _batchStart = now;
        _lastEvent = now;
    }

    /// <summary>True until the start-of-process loads settled.</summary>
    internal bool InStartup => _startup;

    /// <summary>True while something waits to be said.</summary>
    internal bool Pending => _batch.Count > 0 || _coreInBatch || _carriedLines.Count > 0;

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
            _carriedLines.Clear();
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

    /// <summary>
    /// Once a frame or so: the line to send now, or null. <paramref name="status"/> is Bootstrap's last Core reload (null:
    /// no Bootstrap, or it cannot say).
    /// </summary>
    internal string? Poll(double now, HotLoadCoreStatus? status)
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
            _coreLine = restored ? FailedToUpdate(ProgenitorName)
                : _swapFrom.Length > 0 && !string.Equals(_swapFrom, _coreVersion, StringComparison.Ordinal) ? UpdatedTo(ProgenitorName, _coreVersion)
                : Reloaded(ProgenitorName, _coreVersion);
        }
        else if (status != null && !string.Equals(status.Timestamp, _lastTimestamp, StringComparison.Ordinal))
        {
            _lastTimestamp = status.Timestamp;
            if (IsFailure(status.Outcome) && status.Reason.IndexOf("dryRun", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string key = status.Outcome + "|" + status.Reason;
                if (!string.Equals(key, _lastFailureKey, StringComparison.Ordinal))
                {
                    _lastFailureKey = key;
                    _coreLine = FailedToUpdate(ProgenitorName);
                    _coreInBatch = true;
                    Touch(now);
                }
            }
        }

        if (!Pending) return null;
        if (now - _lastEvent < QuietSeconds) return null;
        if (_coreInBatch && now - _batchStart < CoreSettleSeconds) return null;
        if (status != null && status.InFlight) return null;
        return Take();
    }

    private static bool IsFailure(string outcome) =>
        string.Equals(outcome, "refused", StringComparison.Ordinal) || string.Equals(outcome, "failed", StringComparison.Ordinal)
        || string.Equals(outcome, "restored", StringComparison.Ordinal);

    /// <summary>Builds the line from the batch and empties it. Null when nothing in it is worth a line.</summary>
    private string? Take()
    {
        var sentences = new List<string>(_carriedLines);
        if (_coreLine != null) sentences.Add(_coreLine);
        foreach (var e in _batch)
        {
            string? s = Sentence(e);
            if (s != null) sentences.Add(s);
        }

        _carriedLines.Clear();
        _batch.Clear();
        _coreLine = null;
        _coreInBatch = false;
        _batchStart = -1;
        return HotLoadNoticeText.Join(sentences);
    }

    private static string? Sentence(Entry e)
    {
        switch (e.State)
        {
            case Final.Loaded:
                bool same = e.Before != null && string.Equals(e.Before, e.Version, StringComparison.Ordinal);
                if (!e.Live && same) return null;
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
        sb.Append("v1\n");
        sb.Append("core\t").Append(Clean(_coreVersion)).Append('\n');
        foreach (var k in _known)
            sb.Append("known\t").Append(Clean(k.Key)).Append('\t').Append(Clean(k.Value.Name)).Append('\t').Append(Clean(k.Value.Version)).Append('\n');
        foreach (string s in _suspended) sb.Append("paused\t").Append(Clean(s)).Append('\n');
        if (!_startup && !_shutdown)
        {
            var lines = new List<string>(_carriedLines);
            if (_coreLine != null) lines.Add(_coreLine);
            foreach (var e in _batch)
            {
                string? s = Sentence(e);
                if (s != null) lines.Add(s);
            }

            foreach (string line in lines) sb.Append("line\t").Append(Clean(line)).Append('\n');
        }

        return sb.ToString();
    }

    private void Restore(string carried)
    {
        foreach (string raw in (carried ?? "").Split('\n'))
        {
            var f = raw.Split('\t');
            if (f.Length >= 2 && f[0] == "core") _swapFrom = f[1].Trim();
            else if (f.Length >= 4 && f[0] == "known" && f[1].Length > 0) _known[f[1]] = (f[2], f[3]);
            else if (f.Length >= 2 && f[0] == "paused" && f[1].Length > 0) _suspended.Add(f[1]);
            else if (f.Length >= 2 && f[0] == "line" && f[1].Length > 0) _carriedLines.Add(f[1]);
        }
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>The hot-load line's text rules. Game-free.</summary>
internal static class HotLoadNoticeText
{
    /// <summary>What a server's line starts with on players' screens.</summary>
    internal const string ServerPrefix = "Server: ";

    /// <summary>The longest line; sentences past it are counted instead.</summary>
    internal const int MaxChars = 300;

    /// <summary>The sentences as one line (null when there are none); past <see cref="MaxChars"/> the rest are counted.</summary>
    internal static string? Join(IReadOnlyList<string> sentences)
    {
        if (sentences == null || sentences.Count == 0) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < sentences.Count; i++)
        {
            string s = sentences[i];
            int rest = sentences.Count - i;
            string more = MoreText(rest);
            bool last = i == sentences.Count - 1;
            int need = (sb.Length > 0 ? 1 : 0) + s.Length + (last ? 0 : 1 + MoreText(rest - 1).Length);
            if (sb.Length > 0 && sb.Length + need > MaxChars)
            {
                sb.Append(' ').Append(more);
                return sb.ToString();
            }

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }

        return sb.ToString();
    }

    internal static string MoreText(int count) =>
        count == 1 ? "1 more mod changed." : count.ToString(CultureInfo.InvariantCulture) + " more mods changed.";

    /// <summary>The line a server sends its players.</summary>
    internal static string ServerLine(string line) => ServerPrefix + line;

    private static readonly Regex Sentence = new(
        @"^(?:\d+ more mods? changed|.+? (?:removed|added(?: \([^)]*\))?|reloaded(?: \([^)]*\))?|updated(?: to \S+)?"
        + @"|failed to load|failed to reload|failed to update; the previous version keeps running))$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// True for a server's hot-load line (<see cref="ServerPrefix"/> and only hot-load sentences). The caller checks that
    /// the line came from the server (a System line with no sender), so a player cannot make one.
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
