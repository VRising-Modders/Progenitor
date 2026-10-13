using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Support;

/// <summary>A loaded plugin as the diagnosis matches it: its GUID and name, its assembly names and its types' namespaces.</summary>
internal sealed class DiagnosisPlugin
{
    public DiagnosisPlugin(string guid, string name)
    {
        Guid = guid ?? "";
        Name = name ?? "";
    }

    public string Guid { get; }
    public string Name { get; }

    /// <summary>Simple assembly names (no <c>.dll</c>).</summary>
    public List<string> Assemblies { get; } = new();

    /// <summary>Namespaces of the plugin's types (the diagnosis keeps at most their first three parts).</summary>
    public List<string> Namespaces { get; } = new();

    /// <summary>How the diagnosis, the README title and the status line name it; null: <see cref="Name"/>.</summary>
    public string? Display { get; set; }

    public string Label => !string.IsNullOrWhiteSpace(Display) ? Display!.Trim() : Name.Length > 0 ? Name : Guid;
}

/// <summary>One mod the logs point at, with its evidence.</summary>
internal sealed class DiagnosisSuspect
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Guid { get; set; } = "";
    public double Score { get; set; }

    /// <summary>The part of <see cref="Score"/> from evidence that is not noise; a mod is listed only when it alone reaches <see cref="SupportDiagnosis.MinScore"/>.</summary>
    public double SignalScore { get; set; }
    public SortedDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    public List<string> Samples { get; } = new();
}

/// <summary>The diagnosis's result: the section for summary.txt and the phrase for the status line.</summary>
internal sealed class DiagnosisReport
{
    public List<DiagnosisSuspect> Suspects { get; } = new();
    public string PreviousSession { get; set; } = "";

    /// <summary>The first suspect's name, or "" when no mod stands out.</summary>
    public string TopName => Suspects.Count > 0 ? Suspects[0].Label : "";

    /// <summary><c>Likely involved: Foo (from the logs; not proof).</c>, or "".</summary>
    public string StatusPhrase
    {
        get
        {
            if (Suspects.Count == 0) return "";
            var names = new List<string>();
            foreach (var s in Suspects) names.Add(s.Label);
            return "Likely involved: " + string.Join(", ", names) + " (from the logs; not proof).";
        }
    }

    public string Section
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(SupportDiagnosis.Heading);
            if (Suspects.Count == 0) sb.AppendLine("  " + SupportDiagnosis.NothingStandsOut);
            int n = 1;
            foreach (var s in Suspects)
            {
                var parts = new List<string>();
                foreach (var c in s.Counts) parts.Add(c.Value.ToString(CultureInfo.InvariantCulture) + " " + c.Key);
                sb.AppendLine("  " + n.ToString(CultureInfo.InvariantCulture) + ". " + s.Label
                    + (s.Guid.Length > 0 && !string.Equals(s.Guid, s.Label, StringComparison.Ordinal) ? " (" + s.Guid + ")" : "")
                    + " - score " + Math.Round(s.Score).ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", parts));
                foreach (string line in s.Samples) sb.AppendLine("       " + line);
                n++;
            }
            if (PreviousSession.Length > 0) sb.AppendLine("  " + PreviousSession);
            return sb.ToString().TrimEnd();
        }
    }
}

/// <summary>
/// SUPPORT1: "which mod is likely at fault", read from the bundle's own (already redacted) texts. One pass per text, line
/// by line with plain string checks, so it is linear in the logs. Evidence, each scored:
/// <list type="bullet">
/// <item>BepInEx error and fatal lines by their source tag (<c>[Error  :Source]</c>) mapped to a loaded plugin by name or GUID;</item>
/// <item>exceptions, by the first stack frame (<c>at Namespace.Type.Method</c>) in a plugin's namespace or assembly (framework,
/// game and runtime frames are skipped);</item>
/// <item>Harmony patch failures and patch conflicts;</item>
/// <item>BepInEx load failures and missing dependencies (a plugin that failed to load is named even though it is not loaded);</item>
/// <item>the mod problems Progenitor itself reports;</item>
/// <item>the previous session: a Unity crash block (its native modules mapped to plugin assemblies), and when it did not end
/// normally (Unity writes its memory statistics on a clean quit), the last plugins that wrote to the log before the end.</item>
/// </list>
/// Progenitor is a mod like any other here. The wording stays honest: "Likely involved (from the logs; not proof)", and
/// "No mod stands out" when the evidence is thin. Game-free.
/// </summary>
internal sealed class SupportDiagnosis
{
    internal const string Heading = "Likely involved (from the logs; not proof):";
    internal const string NothingStandsOut = "No mod stands out.";

    internal const int ErrorScore = 3;
    internal const int FatalScore = 5;
    internal const int ExceptionScore = 4;
    internal const int HarmonyScore = 4;
    internal const int LoadScore = 5;
    internal const int ProblemScore = 3;
    internal const int CrashScore = 5;
    internal const int LastBeforeEndScore = 2;

    /// <summary>A mod is listed from this score on.</summary>
    internal const int MinScore = 6;

    internal const int MaxSuspects = 3;
    internal const int MaxSamples = 3;
    internal const int MaxSampleChars = 220;

    /// <summary>Unity writes this on a clean quit (the memory statistics at the end of Player.log).</summary>
    internal const string CleanQuitMarker = "Memory Statistics";

    internal const string CrashMarker = "Crash!!!";

    private static readonly string[] ExcludedRoots =
    {
        "System", "Microsoft", "BepInEx", "Il2CppInterop", "Il2CppSystem", "Il2Cpp", "Unity", "UnityEngine", "ProjectM", "Stunlock",
        "HarmonyLib", "MonoMod", "Mono", "Cpp2IL", "Iced", "Steamworks", "TMPro", "Engine", "Internal", "Interop", "Cysharp",
    };

    private readonly Dictionary<string, DiagnosisPlugin> _bySourceOrGuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiagnosisPlugin?> _byNamespace = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiagnosisPlugin> _byAssembly = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DiagnosisPlugin> _plugins = new();
    private readonly Dictionary<string, DiagnosisSuspect> _suspects = new(StringComparer.OrdinalIgnoreCase);

    // Sessions: 1 this one (LogOutput.log, session.log, Player.log), 2 the previous one (session-1.log, Player-prev.log),
    // 4 the one before (session-2.log); 0 anything else (rolled logs, hosted and local servers).
    private const int CurrentSession = 1, PreviousSession = 2, OlderSession = 4;
    private int _session;
    private bool _nearEnd;
    private int _sessionsSeen;

    /// <summary>One piece of evidence, aggregated by mod, kind and signature (the line with its numbers taken out).</summary>
    private sealed class Evidence
    {
        public DiagnosisPlugin? Plugin;
        public string RawName = "";
        public string Kind = "";
        public int Points;
        public bool Filterable;
        public int Count;
        public int Sessions;
        public int NearEnd;
        public readonly List<string> Samples = new();
    }

    private readonly Dictionary<string, Evidence> _evidence = new(StringComparer.Ordinal);

    /// <summary>The most signatures kept per mod (one noisy mod cannot grow the table without bound).</summary>
    internal const int MaxSignaturesPerMod = 500;

    private readonly Dictionary<string, int> _signaturesPerMod = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A signature also seen in a session that ended normally, or in every session collected, weighs this much.</summary>
    internal const double NoiseWeight = 0.1;

    /// <summary>Evidence from the previous session when it did not end normally weighs this much more; near its end, <see cref="NearEndWeight"/>.</summary>
    internal const double UncleanSessionWeight = 1.5;
    internal const double NearEndWeight = 2;

    /// <summary>The last lines of a session that count as "near its end".</summary>
    internal const int NearEndLines = 200;

    /// <summary>Repeats of one signature give diminishing returns: 1 + log2(count), at most this.</summary>
    internal const double MaxRepeatFactor = 4;

    internal const string NoiseLabel = " (also in sessions that ended normally)";

    private bool _sawPreviousUnity;
    private bool _previousClean;
    private bool _previousCrash;
    private readonly List<DiagnosisPlugin> _lastBeforeEnd = new();

    public SupportDiagnosis(IEnumerable<DiagnosisPlugin>? plugins)
    {
        if (plugins == null) return;
        foreach (var p in plugins)
        {
            if (p == null) continue;
            _plugins.Add(p);
            if (p.Name.Length > 0 && !_bySourceOrGuid.ContainsKey(p.Name)) _bySourceOrGuid[p.Name] = p;
            if (p.Guid.Length > 0 && !_bySourceOrGuid.ContainsKey(p.Guid)) _bySourceOrGuid[p.Guid] = p;
            foreach (string a in p.Assemblies)
                if (!string.IsNullOrWhiteSpace(a) && !_byAssembly.ContainsKey(a.Trim())) _byAssembly[a.Trim()] = p;
            foreach (string ns in p.Namespaces)
            {
                string key = RegisteredKey(ns);
                if (key.Length == 0 || IsExcluded(key)) continue;
                // A namespace two plugins share points at neither.
                if (_byNamespace.TryGetValue(key, out var existing)) { if (!ReferenceEquals(existing, p)) _byNamespace[key] = null; }
                else _byNamespace[key] = p;
            }
        }
    }

    /// <summary>A mod problem Progenitor reported for <paramref name="modId"/> (its GUID).</summary>
    public void AddProblem(string? modId, string? line)
    {
        if (string.IsNullOrWhiteSpace(modId)) return;
        if (_bySourceOrGuid.TryGetValue(modId!.Trim(), out var p)) Score(p, null, ProblemScore, "mod problems", line, filterable: false);
        else Score(null, modId!.Trim(), ProblemScore, "mod problems", line, filterable: false);
    }

    /// <summary>One collected (redacted) text; <paramref name="entry"/> is its path in the zip.</summary>
    public void Feed(string entry, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string e = (entry ?? "").Replace('\\', '/');
        bool previousSession = e.EndsWith("progenitor-logs/session-1.log", StringComparison.OrdinalIgnoreCase)
            && e.StartsWith("bepinex/", StringComparison.OrdinalIgnoreCase);
        bool previousUnity = string.Equals(e, "unity/Player-prev.log", StringComparison.OrdinalIgnoreCase);
        bool unity = e.StartsWith("unity/", StringComparison.OrdinalIgnoreCase) || e.Contains("/unity/");

        if (previousUnity)
        {
            _sawPreviousUnity = true;
            _previousClean = text!.IndexOf(CleanQuitMarker, StringComparison.Ordinal) >= 0;
        }

        _session = SessionOf(e);
        _sessionsSeen |= _session;
        int lines = 0, nearEndFrom = int.MaxValue;
        if (_session == PreviousSession)
        {
            foreach (char c in text!) if (c == '\n') lines++;
            nearEndFrom = Math.Max(0, lines - NearEndLines);
        }
        int lineIndex = 0;

        var ring = previousSession ? new Queue<DiagnosisPlugin>() : null;
        bool inException = false;
        string exceptionLine = "";
        bool inCrash = false;
        int start = 0;
        string t = text!;
        while (start < t.Length)
        {
            int nl = t.IndexOf('\n', start);
            int end = nl < 0 ? t.Length : nl;
            int lineEnd = end > start && t[end - 1] == '\r' ? end - 1 : end;
            _nearEnd = lineIndex >= nearEndFrom;
            Line(t, start, lineEnd, ring, ref inException, ref exceptionLine, ref inCrash, unity, previousUnity);
            start = end + 1;
            lineIndex++;
        }

        if (inException) FlushHost(ref inException, exceptionLine);
        _session = 0;
        _nearEnd = false;
        if (ring != null)
        {
            _lastBeforeEnd.Clear();
            var list = new List<DiagnosisPlugin>(ring);
            for (int i = list.Count - 1; i >= 0 && _lastBeforeEnd.Count < MaxSuspects; i--)
                if (!_lastBeforeEnd.Contains(list[i])) _lastBeforeEnd.Add(list[i]);
        }
    }

    private void Line(string t, int s, int e, Queue<DiagnosisPlugin>? ring, ref bool inException, ref string exceptionLine,
        ref bool inCrash, bool unity, bool previousUnity)
    {
        int len = e - s;
        if (len <= 0) return;
        var span = t.AsSpan(s, len);
        if (inException)
        {
            var lead = span.TrimStart();
            if (!lead.StartsWith("at ".AsSpan(), StringComparison.Ordinal) && !lead.StartsWith("---".AsSpan(), StringComparison.Ordinal))
                FlushHost(ref inException, exceptionLine);
        }

        // Unity's crash block (Player.log): "Crash!!!", then native frames "0x00007FF6... (Module) Symbol".
        if (unity && span.IndexOf(CrashMarker.AsSpan(), StringComparison.Ordinal) >= 0)
        {
            inCrash = true;
            if (previousUnity) _previousCrash = true;
            return;
        }
        if (inCrash)
        {
            int open = span.IndexOf('(');
            int close = open >= 0 ? span.Slice(open + 1).IndexOf(')') : -1;
            if (open >= 0 && close > 0 && span.IndexOf("0x".AsSpan(), StringComparison.Ordinal) >= 0)
            {
                string module = span.Slice(open + 1, close).ToString().Trim();
                if (module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) module = module.Substring(0, module.Length - 4);
                if (_byAssembly.TryGetValue(module, out var crashed)) Score(crashed, null, CrashScore, "crash frames", Sample(span), filterable: false);
            }
            if (span.Trim().Length == 0) inCrash = false;
        }

        // BepInEx's own lines: "[Level  :Source] message".
        DiagnosisPlugin? source = null;
        bool error = false, fatal = false;
        if (span[0] == '[')
        {
            int colon = span.IndexOf(':');
            int close = span.IndexOf(']');
            if (colon > 1 && close > colon)
            {
                var level = span.Slice(1, colon - 1).Trim();
                var name = span.Slice(colon + 1, close - colon - 1).Trim().ToString();
                _bySourceOrGuid.TryGetValue(name, out source);
                error = level.Equals("Error".AsSpan(), StringComparison.OrdinalIgnoreCase);
                fatal = level.Equals("Fatal".AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (source != null)
                {
                    if (fatal) Score(source, null, FatalScore, "fatal lines", Sample(span));
                    else if (error) Score(source, null, ErrorScore, "error lines", Sample(span));
                    if (ring != null)
                    {
                        ring.Enqueue(source);
                        if (ring.Count > 200) ring.Dequeue();
                    }
                }
                inException = false;
            }
        }

        // Load failures: "Could not load [Name 1.2.3]", "Skipping [Name 1.2.3] because ...", missing dependencies.
        if (span.IndexOf("Could not load [".AsSpan(), StringComparison.Ordinal) >= 0
            || span.IndexOf("Skipping [".AsSpan(), StringComparison.Ordinal) >= 0
            || span.IndexOf("missing dependenc".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0
            || span.IndexOf("incompatible dependenc".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string? named = BracketName(span);
            if (named != null)
            {
                if (_bySourceOrGuid.TryGetValue(named, out var p)) Score(p, null, LoadScore, "load failures", Sample(span));
                else Score(null, named, LoadScore, "load failures", Sample(span));
            }
            return;
        }

        // Harmony: a patch that failed or conflicts.
        if (span.IndexOf("Harmony".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0
            && (span.IndexOf("fail".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0
                || span.IndexOf("conflict".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0
                || span.IndexOf("Exception".AsSpan(), StringComparison.Ordinal) >= 0))
        {
            var p = Mentioned(span) ?? source;
            if (p != null) Score(p, null, HarmonyScore, "Harmony patch failures", Sample(span));
            return;
        }

        // An exception's header, then the first frame that belongs to a plugin. A framework frame that only hosts or
        // dispatches to plugins (HostFramePrefixes) counts only when no other plugin's frame follows in that trace.
        var trimmed = span.TrimStart();
        if (trimmed.StartsWith("at ".AsSpan(), StringComparison.Ordinal))
        {
            if (!inException) return;
            var p = FramePlugin(trimmed.Slice(3), out bool host);
            if (p != null)
            {
                if (host)
                {
                    if (_hostFallback == null) { _hostFallback = p; _hostSample = exceptionLine; }
                    return;
                }
                Score(p, null, ExceptionScore, "exceptions", exceptionLine);
                inException = false;
                _hostFallback = null;
            }
            return;
        }
        if (span.IndexOf("Exception".AsSpan(), StringComparison.Ordinal) >= 0 && (error || fatal || span.IndexOf(": ".AsSpan(), StringComparison.Ordinal) >= 0))
        {
            inException = true;
            exceptionLine = Sample(span);
        }
    }

    /// <summary>
    /// Framework code that only hosts, loads or dispatches to plugins: an exception that passes through it belongs to the
    /// plugin frame further down the trace, when there is one.
    /// </summary>
    internal static readonly string[] HostFramePrefixes =
    {
        "Progenitor.Plugins.", "Progenitor.Reload.", "Progenitor.Hooks.", "Progenitor.Events.", "Progenitor.Systems.",
        "Progenitor.Commands.", "Progenitor.Dev.MainThread", "Progenitor.Networking.CustomEvents", "Progenitor.Ticks.",
        "Progenitor.Bootstrap.",
    };

    private DiagnosisPlugin? _hostFallback;
    private string _hostSample = "";

    /// <summary>The trace ended with only a hosting frame matched: that frame's plugin gets the exception after all.</summary>
    private void FlushHost(ref bool inException, string exceptionLine)
    {
        if (_hostFallback != null) Score(_hostFallback, null, ExceptionScore, "exceptions", _hostSample.Length > 0 ? _hostSample : exceptionLine);
        _hostFallback = null;
        _hostSample = "";
        inException = false;
    }

    private DiagnosisPlugin? FramePlugin(ReadOnlySpan<char> frame) => FramePlugin(frame, out _);

    /// <summary>The plugin a frame like <c>Foo.Bar.Type.Method (args) [0x0] in &lt;...&gt;</c> belongs to, or null.</summary>
    private DiagnosisPlugin? FramePlugin(ReadOnlySpan<char> frame, out bool host)
    {
        host = false;
        int stop = 0;
        while (stop < frame.Length && frame[stop] != '(' && frame[stop] != ' ' && frame[stop] != '[') stop++;
        string path = frame.Slice(0, stop).ToString();
        if (path.StartsWith("(wrapper", StringComparison.Ordinal)) return null;
        int g = path.IndexOfAny(new[] { '<', '`' });
        if (g > 0) path = path.Substring(0, g);
        if (path.Length == 0 || IsExcluded(path)) return null;
        foreach (string prefix in HostFramePrefixes)
            if (path.StartsWith(prefix, StringComparison.Ordinal)) { host = true; break; }
        for (int parts = 3; parts >= 1; parts--)
        {
            string key = NamespaceKey(path, parts);
            if (key.Length == 0) continue;
            if (_byNamespace.TryGetValue(key, out var p)) return p; // null when two plugins share it
        }
        return null;
    }

    private DiagnosisPlugin? Mentioned(ReadOnlySpan<char> span)
    {
        foreach (var p in _plugins)
        {
            if (p.Guid.Length >= 3 && span.IndexOf(p.Guid.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return p;
            if (p.Name.Length >= 3 && span.IndexOf(p.Name.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return p;
        }
        return null;
    }

    /// <summary>The name in the line's first <c>[Name 1.2.3]</c> (its version dropped), or null.</summary>
    private static string? BracketName(ReadOnlySpan<char> span)
    {
        int at = 0;
        while (at < span.Length)
        {
            int open = span.Slice(at).IndexOf('[');
            if (open < 0) return null;
            open += at;
            int close = span.Slice(open + 1).IndexOf(']');
            if (close < 0) return null;
            var inside = span.Slice(open + 1, close).Trim();
            at = open + 1 + close + 1;
            if (inside.IndexOf(':') >= 0 || inside.Length == 0) continue; // the "[Error  :Source]" tag
            int space = inside.LastIndexOf(' ');
            if (space > 0 && inside.Length > space + 1 && char.IsDigit(inside[space + 1])) inside = inside.Slice(0, space);
            string name = inside.Trim().ToString();
            return name.Length >= 2 ? name : null;
        }
        return null;
    }

    /// <summary>The first <paramref name="parts"/> dotted parts of <paramref name="path"/> ("" when it has fewer).</summary>
    private static string NamespaceKey(string path, int parts)
    {
        var segments = path.Split('.');
        if (segments.Length < parts) return "";
        return string.Join(".", segments, 0, parts);
    }

    /// <summary>A namespace's key: at most its first three parts.</summary>
    private static string RegisteredKey(string ns)
    {
        var segments = (ns ?? "").Trim().Split('.');
        if (segments.Length == 0 || segments[0].Length == 0) return "";
        return string.Join(".", segments, 0, Math.Min(3, segments.Length));
    }

    private static bool IsExcluded(string path)
    {
        foreach (string root in ExcludedRoots)
            if (path.StartsWith(root, StringComparison.Ordinal) && (path.Length == root.Length || path[root.Length] == '.' || char.IsUpper(path[root.Length])))
                return true;
        return false;
    }

    private static string Sample(ReadOnlySpan<char> span)
    {
        var s = span.Trim();
        return s.Length > MaxSampleChars ? s.Slice(0, MaxSampleChars).ToString() + "..." : s.ToString();
    }

    /// <summary>
    /// Whether <paramref name="entry"/> goes to the diagnosis. <c>LogOutput.log</c> and <c>progenitor-logs/session.log</c>
    /// of one process (this one, <c>hosted-server/</c>, <c>local-server-N/</c>, <c>server/</c>) hold the same session, so
    /// only one of them is read: <c>session.log</c> when the bundle has it.
    /// </summary>
    internal static bool ShouldFeed(string entry, ICollection<string> entries)
    {
        string e = (entry ?? "").Replace('\\', '/');
        const string log = "bepinex/LogOutput.log";
        if (!e.EndsWith(log, StringComparison.OrdinalIgnoreCase)) return true;
        string root = e.Substring(0, e.Length - log.Length);
        if (root.Length > 0 && !root.EndsWith("/", StringComparison.Ordinal)) return true;
        string session = root + "bepinex/progenitor-logs/session.log";
        foreach (string other in entries)
            if (string.Equals(other, session, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Which session an entry belongs to (see the session constants).</summary>
    internal static int SessionOf(string entry)
    {
        string e = (entry ?? "").Replace('\\', '/').ToLowerInvariant();
        if (e == "bepinex/logoutput.log" || e == "bepinex/progenitor-logs/session.log" || e == "unity/player.log") return CurrentSession;
        if (e == "bepinex/progenitor-logs/session-1.log" || e == "unity/player-prev.log") return PreviousSession;
        if (e == "bepinex/progenitor-logs/session-2.log") return OlderSession;
        return 0;
    }

    /// <summary>
    /// A line with what changes from run to run taken out (numbers, hex, ids, times), so the same error in two sessions is
    /// one signature. The BepInEx tag's level and source stay.
    /// </summary>
    internal static string Signature(string? line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        var sb = new StringBuilder(Math.Min(line!.Length, 160));
        bool lastWasNumber = false, lastWasSpace = false;
        for (int i = 0; i < line.Length && sb.Length < 160; i++)
        {
            char c = line[i];
            if (c == '0' && i + 1 < line.Length && (line[i + 1] == 'x' || line[i + 1] == 'X'))
            {
                i += 2;
                while (i < line.Length && Uri.IsHexDigit(line[i])) i++;
                i--;
                if (!lastWasNumber) sb.Append('#');
                lastWasNumber = true;
                lastWasSpace = false;
                continue;
            }
            if (char.IsDigit(c))
            {
                if (!lastWasNumber) sb.Append('#');
                lastWasNumber = true;
                lastWasSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                lastWasNumber = false;
                continue;
            }
            // A short argument in parentheses ("GetSystemTypeIndex(ProjectM.UI.MapSystem) threw") is what varies between
            // repeats of one error, so "(…)" stands for it (live 2026-10-10: one startup error per system type).
            if (c == '(')
            {
                int close = -1;
                for (int j = i + 1; j < line.Length && j <= i + 120; j++)
                {
                    if (line[j] == '(') break;
                    if (line[j] == ')') { close = j; break; }
                }
                if (close > 0)
                {
                    sb.Append("(…)");
                    i = close;
                    lastWasNumber = false;
                    lastWasSpace = false;
                    continue;
                }
            }
            // "#:#" (entity ids) and "#.#" (times, versions) stay one number.
            if (lastWasNumber && (c == ':' || c == '.' || c == ',') && i + 1 < line.Length && char.IsDigit(line[i + 1])) continue;
            sb.Append(c);
            lastWasNumber = false;
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }

    private void Score(DiagnosisPlugin? plugin, string? rawName, int points, string kind, string? sample, bool filterable = true)
    {
        string key = plugin != null ? (plugin.Guid.Length > 0 ? plugin.Guid : plugin.Name) : rawName ?? "";
        if (key.Length == 0) return;
        string signature = filterable ? Signature(sample) : "";
        string id = key + "\u0001" + kind + "\u0001" + signature;
        if (!_evidence.TryGetValue(id, out var ev))
        {
            _signaturesPerMod.TryGetValue(key, out int kept);
            if (kept >= MaxSignaturesPerMod) return;
            _signaturesPerMod[key] = kept + 1;
            ev = new Evidence { Plugin = plugin, RawName = rawName ?? key, Kind = kind, Points = points, Filterable = filterable };
            _evidence[id] = ev;
        }
        ev.Count++;
        ev.Sessions |= _session;
        if (_session == PreviousSession && _nearEnd) ev.NearEnd++;
        if (!string.IsNullOrWhiteSpace(sample) && ev.Samples.Count < MaxSamples && !ev.Samples.Contains(sample!)) ev.Samples.Add(sample!);
    }

    private DiagnosisSuspect SuspectFor(Evidence ev)
    {
        var plugin = ev.Plugin;
        string key = plugin != null ? (plugin.Guid.Length > 0 ? plugin.Guid : plugin.Name) : ev.RawName;
        if (!_suspects.TryGetValue(key, out var s))
        {
            s = new DiagnosisSuspect { Key = key, Label = plugin?.Label ?? ev.RawName, Guid = plugin?.Guid ?? "" };
            _suspects[key] = s;
        }
        return s;
    }

    /// <summary>The ranked result: mods at or above <see cref="MinScore"/>, best first, at most <see cref="MaxSuspects"/>.</summary>
    public DiagnosisReport Finish()
    {
        var report = new DiagnosisReport();
        bool unclean = _sawPreviousUnity && (!_previousClean || _previousCrash);
        bool previousClean = _sawPreviousUnity && _previousClean && !_previousCrash;
        int seen = _sessionsSeen & (CurrentSession | PreviousSession | OlderSession);
        int sessionCount = (seen & 1) + ((seen >> 1) & 1) + ((seen >> 2) & 1);
        if (unclean)
            foreach (var p in _lastBeforeEnd) Score(p, null, LastBeforeEndScore, "last to log before the previous session ended", null, filterable: false);

        foreach (var ev in _evidence.Values)
        {
            // Noise: the same error also in a session that ended normally, or in every session collected.
            bool noise = ev.Filterable
                && ((previousClean && (ev.Sessions & PreviousSession) != 0)
                    || (!unclean && !_previousCrash && sessionCount >= 2 && (ev.Sessions & seen) == seen));
            double weight = noise ? NoiseWeight : 1;
            if (!noise && unclean && (ev.Sessions & PreviousSession) != 0) weight = ev.NearEnd > 0 ? NearEndWeight : UncleanSessionWeight;
            double repeats = Math.Min(MaxRepeatFactor, 1 + Math.Log(ev.Count, 2));
            var s = SuspectFor(ev);
            s.Score += ev.Points * repeats * weight;
            if (!noise) s.SignalScore += ev.Points * repeats * weight;
            s.Counts[ev.Kind] = s.Counts.TryGetValue(ev.Kind, out int c) ? c + ev.Count : ev.Count;
            foreach (string sample in ev.Samples)
            {
                string shown = noise ? sample + NoiseLabel : sample;
                if (s.Samples.Count < MaxSamples && !s.Samples.Contains(shown)) s.Samples.Add(shown);
            }
        }

        if (unclean)
        {
            var names = new List<string>();
            foreach (var p in _lastBeforeEnd) names.Add(p.Label);
            report.PreviousSession = (_previousCrash ? "The previous session ended with a crash" : "The previous session did not end normally (no clean Unity shutdown in Player-prev.log)")
                + (names.Count > 0 ? "; the last mods to write to the log were: " + string.Join(", ", names) + "." : ".");
        }

        var all = new List<DiagnosisSuspect>(_suspects.Values);
        all.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
        // Noise alone never names a mod: many distinct background errors add up, but none of them sets the mod apart.
        foreach (var s in all)
        {
            if (s.Score < MinScore || report.Suspects.Count >= MaxSuspects) break;
            if (s.SignalScore < MinScore) continue;
            report.Suspects.Add(s);
        }
        return report;
    }
}
