using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// CLIENTPOLICY1: the client inventory report's packet (<c>'PrCm'</c>, <see cref="ClientModReportWire.EventId"/>), client
/// to server only. A report is UTF-8 JSON (<c>{"v":1,"truncated":false,"mods":[{guid,name,version,source,package}]}</c>)
/// cut to <see cref="ClientModPolicyLimits.MaxReportBytes"/> (entries dropped from the end, <c>truncated</c> set) and
/// split into at most <see cref="ClientModPolicyLimits.MaxReportChunks"/> packets of at most
/// <see cref="ClientModPolicyLimits.MaxReportPacketBytes"/>: a version byte, a kind byte, the report id (4 bytes,
/// little-endian), the chunk index and the chunk count, then the chunk's bytes.
/// </summary>
internal static class ClientModReportWire
{
    /// <summary><c>'PrCm'</c>. Inside the reserved range, so only Core may register or send on it.</summary>
    internal const int EventId = 0x5072436D;

    internal const byte Version = 1;
    internal const byte KindChunk = 0;
    internal const int HeaderBytes = 8;
    internal const int MaxChunkBody = ClientModPolicyLimits.MaxReportPacketBytes - HeaderBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The report's JSON bytes, cut to fit: entries are dropped from the end and <c>truncated</c> is set.</summary>
    internal static byte[] Body(IReadOnlyList<ClientModEntry> entries, bool truncated, out int kept) => Body(entries, truncated, null, out kept);

    /// <summary>The same with SELFFIX1's claims (an optional <c>fixes</c> list an older server ignores).</summary>
    internal static byte[] Body(IReadOnlyList<ClientModEntry> entries, bool truncated, IReadOnlyList<ClientModFix>? fixes, out int kept)
    {
        int n = entries?.Count ?? 0;
        if (n > ClientModPolicyLimits.MaxReportEntries)
        {
            n = ClientModPolicyLimits.MaxReportEntries;
            truncated = true;
        }

        while (true)
        {
            byte[] bytes = Serialize(entries, n, truncated, fixes);
            if (bytes.Length <= ClientModPolicyLimits.MaxReportBytes || n == 0)
            {
                kept = n;
                return bytes;
            }

            // Over the total: drop about the overflow's share of entries at once, at least one.
            int over = bytes.Length - ClientModPolicyLimits.MaxReportBytes;
            int per = Math.Max(1, bytes.Length / Math.Max(1, n));
            n = Math.Max(0, n - Math.Max(1, over / per));
            truncated = true;
        }
    }

    private static byte[] Serialize(IReadOnlyList<ClientModEntry>? entries, int count, bool truncated, IReadOnlyList<ClientModFix>? fixes = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteBoolean("truncated", truncated);
            w.WriteStartArray("mods");
            for (int i = 0; i < count && entries != null; i++)
            {
                var e = entries[i];
                w.WriteStartObject();
                w.WriteString("guid", e.Guid);
                if (e.Name.Length > 0) w.WriteString("name", e.Name);
                w.WriteString("version", e.Version);
                w.WriteString("source", e.Source);
                if (e.Package.Length > 0) w.WriteString("package", e.Package);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            // SELFFIX1: only when there is a claim, so a report without one is byte for byte as before.
            int fixCount = 0;
            if (fixes != null)
                foreach (var f in fixes)
                {
                    if (f == null || f.Key.Length == 0 || f.Key.Length > ClientModFix.MaxKeyChars || !ClientModFix.KnownKind(f.Kind)) continue;
                    if (f.Version.Length > ClientModFix.MaxVersionChars) continue;
                    if (fixCount == 0) w.WriteStartArray("fixes");
                    w.WriteStartObject();
                    w.WriteString("key", f.Key);
                    w.WriteString("kind", f.Kind);
                    if (f.Version.Length > 0) w.WriteString("version", f.Version);
                    w.WriteEndObject();
                    if (++fixCount >= ClientModFix.MaxFixes) break;
                }

            if (fixCount > 0) w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The packets of one report.</summary>
    internal static List<byte[]> Encode(IReadOnlyList<ClientModEntry> entries, bool truncated, int reportId, IReadOnlyList<ClientModFix>? fixes = null)
    {
        byte[] body = Body(entries, truncated, fixes, out _);
        int count = Math.Max(1, (body.Length + MaxChunkBody - 1) / MaxChunkBody);
        var packets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int start = i * MaxChunkBody;
            int len = Math.Min(MaxChunkBody, body.Length - start);
            var p = new byte[HeaderBytes + Math.Max(0, len)];
            p[0] = Version;
            p[1] = KindChunk;
            var id = BitConverter.GetBytes(reportId);
            if (!BitConverter.IsLittleEndian) Array.Reverse(id);
            Buffer.BlockCopy(id, 0, p, 2, 4);
            p[6] = (byte)i;
            p[7] = (byte)count;
            if (len > 0) Buffer.BlockCopy(body, start, p, HeaderBytes, len);
            packets.Add(p);
        }

        return packets;
    }

    /// <summary>Read a packet's header. False when it is not one of ours or breaks a cap.</summary>
    internal static bool TryHeader(ReadOnlySpan<byte> packet, out int reportId, out int index, out int count)
    {
        reportId = index = count = 0;
        if (packet.Length < HeaderBytes || packet.Length > ClientModPolicyLimits.MaxReportPacketBytes) return false;
        if (packet[0] != Version || packet[1] != KindChunk) return false;
        var id = packet.Slice(2, 4).ToArray();
        if (!BitConverter.IsLittleEndian) Array.Reverse(id);
        reportId = BitConverter.ToInt32(id, 0);
        index = packet[6];
        count = packet[7];
        return count >= 1 && count <= ClientModPolicyLimits.MaxReportChunks && index < count;
    }

    /// <summary>Parse a whole report body. Fails closed: not strict UTF-8 JSON of this shape is no report.</summary>
    internal static bool TryParse(byte[] body, out ClientModReport report, out string? error)
    {
        report = new ClientModReport();
        error = null;
        if (body == null || body.Length == 0 || body.Length > ClientModPolicyLimits.MaxReportBytes)
        {
            error = "the report is empty or larger than " + ClientModPolicyLimits.MaxReportBytes + " bytes";
            return false;
        }

        try
        {
            string json = StrictUtf8.GetString(body);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
            {
                error = "the report is not an object with a mods list";
                return false;
            }

            report.Truncated = root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
            foreach (var m in mods.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (report.Entries.Count >= ClientModPolicyLimits.MaxReportEntries)
                {
                    report.Truncated = true;
                    break;
                }

                var e = new ClientModEntry(Str(m, "guid"), Str(m, "name"), Str(m, "version"), Str(m, "source"), Str(m, "package"));
                if (e.Guid.Length == 0 && e.Package.Length == 0) continue;
                report.Entries.Add(e);
            }

            // SELFFIX1: the client's claims, bounded; unknown kinds and over-long keys are skipped.
            if (root.TryGetProperty("fixes", out var fixes) && fixes.ValueKind == JsonValueKind.Array)
                foreach (var f in fixes.EnumerateArray())
                {
                    if (report.Fixes.Count >= ClientModFix.MaxFixes) break;
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    var fix = new ClientModFix(Str(f, "key"), Str(f, "kind"), Str(f, "version"));
                    if (fix.Key.Length == 0 || fix.Key.Length > ClientModFix.MaxKeyChars || !ClientModFix.KnownKind(fix.Kind)) continue;
                    if (fix.Version.Length > ClientModFix.MaxVersionChars) continue;
                    report.Fixes.Add(fix);
                }

            return true;
        }
        catch (Exception e) when (e is JsonException || e is DecoderFallbackException || e is ArgumentException)
        {
            error = "the report is not valid JSON (" + e.Message + ")";
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

/// <summary>
/// CLIENTPOLICY1, server: puts each user's report chunks back together. One report in flight per user: a chunk of a
/// new report id starts over. Bounded by the chunk count and the 64 KiB total; a duplicate chunk is ignored.
/// Game-free.
/// </summary>
internal sealed class ClientModReportAssembler
{
    private sealed class InFlight
    {
        public int Id;
        public byte[]?[] Parts = Array.Empty<byte[]?>();
        public int Bytes;
        public int Have;
    }

    private readonly Dictionary<int, InFlight> _byUser = new();
    private readonly object _gate = new();

    /// <summary>
    /// Take one packet from <paramref name="userIndex"/>. True with the report when it completed it; false while it
    /// waits for more, or with <paramref name="error"/> when the packet (or the whole report) was refused.
    /// </summary>
    public bool Accept(int userIndex, ReadOnlySpan<byte> packet, out ClientModReport? report, out string? error)
    {
        report = null;
        error = null;
        if (!ClientModReportWire.TryHeader(packet, out int id, out int index, out int count))
        {
            error = "not a report packet";
            return false;
        }

        byte[] body = packet.Slice(ClientModReportWire.HeaderBytes).ToArray();
        byte[] whole;
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userIndex, out var f) || f.Id != id || f.Parts.Length != count)
            {
                if (_byUser.Count >= 256 && !_byUser.ContainsKey(userIndex)) _byUser.Clear();
                f = new InFlight { Id = id, Parts = new byte[count][] };
                _byUser[userIndex] = f;
            }

            if (f.Parts[index] != null) return false;
            if (f.Bytes + body.Length > ClientModPolicyLimits.MaxReportBytes)
            {
                _byUser.Remove(userIndex);
                error = "the report is larger than " + ClientModPolicyLimits.MaxReportBytes + " bytes";
                return false;
            }

            f.Parts[index] = body;
            f.Bytes += body.Length;
            f.Have++;
            if (f.Have < count) return false;
            _byUser.Remove(userIndex);
            whole = new byte[f.Bytes];
            int at = 0;
            foreach (var part in f.Parts)
            {
                Buffer.BlockCopy(part!, 0, whole, at, part!.Length);
                at += part.Length;
            }
        }

        if (!ClientModReportWire.TryParse(whole, out var parsed, out error)) return false;
        report = parsed;
        return true;
    }

    public void Drop(int userIndex)
    {
        lock (_gate) _byUser.Remove(userIndex);
    }

    public void Clear()
    {
        lock (_gate) _byUser.Clear();
    }

    public int InFlightCount
    {
        get { lock (_gate) return _byUser.Count; }
    }
}

/// <summary>
/// CLIENTPOLICY1, server: the joins whose accepted handshake answer waits for the client's report. One per user index;
/// a report or the deadline decides it, exactly once. Game-free (the live code posts the deadline and calls
/// <see cref="TryExpire"/>).
/// </summary>
internal sealed class ClientPolicyGate
{
    internal sealed class Waiting
    {
        public int Generation;
        public string Name = "";
        public double Due;

        /// <summary>Decide the join with this report (null: none arrived).</summary>
        public Action<ClientModReport?, string>? Decide;
    }

    private readonly Dictionary<int, Waiting> _byUser = new();
    private readonly object _gate = new();
    private int _next = 1;

    /// <summary>Start waiting for <paramref name="userIndex"/>'s report until <paramref name="due"/>; replaces an older wait.</summary>
    public int Begin(int userIndex, string name, double due, Action<ClientModReport?, string> decide)
    {
        lock (_gate)
        {
            int g = _next++;
            _byUser[userIndex] = new Waiting { Generation = g, Name = name ?? "", Due = due, Decide = decide };
            return g;
        }
    }

    /// <summary>A report arrived: the waiting join, removed (null when none waits).</summary>
    public Waiting? TakeOnReport(int userIndex)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userIndex, out var w)) return null;
            _byUser.Remove(userIndex);
            return w;
        }
    }

    /// <summary>The deadline's post: the waiting join when it is still this generation and due.</summary>
    public Waiting? TryExpire(int userIndex, int generation, double now)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userIndex, out var w) || w.Generation != generation || now < w.Due) return null;
            _byUser.Remove(userIndex);
            return w;
        }
    }

    public bool IsWaiting(int userIndex)
    {
        lock (_gate) return _byUser.ContainsKey(userIndex);
    }

    public void OnDisconnect(int userIndex)
    {
        lock (_gate) _byUser.Remove(userIndex);
    }

    public void Clear()
    {
        lock (_gate) _byUser.Clear();
    }

    public int Count
    {
        get { lock (_gate) return _byUser.Count; }
    }
}

/// <summary>CLIENTPOLICY1, server: the last report and the last judgment per connected user (readback; stage 3 re-judges from the report).</summary>
internal static class ClientModPolicyRecords
{
    internal sealed class ReportRecord
    {
        public ClientModReport Report = new();
        public DateTime AtUtc;

        /// <summary><c>join</c> (decided a waiting join), <c>resend</c> (after a hot load or unload; recorded only), or <c>injected</c> (the bridge, TEST ONLY).</summary>
        public string How = "";
        public int Reports;
    }

    internal sealed class JudgmentRecord
    {
        public ClientModVerdict Verdict = new();
        public string Name = "";
        public DateTime AtUtc;

        /// <summary>Why it was decided now: <c>report</c>, <c>no-report</c> (the deadline), <c>no-capability</c> (the client cannot report), <c>injected</c>.</summary>
        public string Trigger = "";
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, ReportRecord> Reports = new();
    private static readonly Dictionary<int, JudgmentRecord> Judgments = new();
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    internal static void NoteReport(int userIndex, ClientModReport report, string how)
    {
        lock (Gate)
        {
            if (Reports.Count >= 256 && !Reports.ContainsKey(userIndex)) Reports.Clear();
            int n = Reports.TryGetValue(userIndex, out var old) ? old.Reports : 0;
            Reports[userIndex] = new ReportRecord { Report = report, AtUtc = Clock(), How = how, Reports = n + 1 };
        }
    }

    internal static void NoteJudgment(int userIndex, string name, ClientModVerdict verdict, string trigger)
    {
        lock (Gate)
        {
            if (Judgments.Count >= 256 && !Judgments.ContainsKey(userIndex)) Judgments.Clear();
            Judgments[userIndex] = new JudgmentRecord { Verdict = verdict, Name = name ?? "", AtUtc = Clock(), Trigger = trigger };
        }
    }

    internal static ReportRecord? ReportOf(int userIndex)
    {
        lock (Gate) return Reports.TryGetValue(userIndex, out var r) ? r : null;
    }

    internal static JudgmentRecord? JudgmentOf(int userIndex)
    {
        lock (Gate) return Judgments.TryGetValue(userIndex, out var j) ? j : null;
    }

    internal static List<int> Users()
    {
        lock (Gate)
        {
            var set = new SortedSet<int>(Reports.Keys);
            foreach (int k in Judgments.Keys) set.Add(k);
            return new List<int>(set);
        }
    }

    /// <summary>A disconnect forgets the report; the judgment stays for the readback (a refused user is gone by then).</summary>
    internal static void OnDisconnect(int userIndex)
    {
        lock (Gate) Reports.Remove(userIndex);
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Reports.Clear();
            Judgments.Clear();
        }

        Clock = () => DateTime.UtcNow;
    }

    /// <summary>A report as the bridge reads it.</summary>
    internal static Dictionary<string, object?> Describe(ClientModReport report)
    {
        var mods = new List<object?>();
        foreach (var e in report.Entries)
            mods.Add(new Dictionary<string, object?> { ["guid"] = e.Guid, ["name"] = e.Name, ["version"] = e.Version, ["source"] = e.Source, ["package"] = e.Package });
        return new Dictionary<string, object?> { ["truncated"] = report.Truncated, ["count"] = report.Entries.Count, ["mods"] = mods };
    }

    /// <summary>A verdict as the bridge reads it.</summary>
    internal static Dictionary<string, object?> Describe(ClientModVerdict v)
    {
        static List<object?> Rows(List<ClientModProblem> list)
        {
            var rows = new List<object?>();
            foreach (var p in list)
                rows.Add(new Dictionary<string, object?>
                {
                    ["key"] = p.Key, ["name"] = p.Name, ["kind"] = p.Kind, ["status"] = p.Status, ["required"] = p.Required,
                    ["had"] = p.Had, ["rule"] = ClientModRules.Word(p.Rule),
                });
            return rows;
        }

        return new Dictionary<string, object?>
        {
            ["outcome"] = v.Word, ["checked"] = v.Checked, ["unchecked"] = v.Unchecked, ["reason"] = v.Reason,
            ["required"] = Rows(v.Required), ["notAllowed"] = Rows(v.NotAllowed),
        };
    }

    internal static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>CLIENTPOLICY1, client: one loaded plugin as the inventory observes it.</summary>
internal readonly struct ClientModObservation
{
    public ClientModObservation(string guid, string name, string version, string source)
    {
        Guid = guid ?? "";
        Name = name ?? "";
        Version = version ?? "";
        Source = source ?? "";
    }

    public string Guid { get; }
    public string Name { get; }
    public string Version { get; }
    public string Source { get; }
}

/// <summary>CLIENTPOLICY1, client: the report's entries from what this process loaded (game-free composition).</summary>
internal static class ClientModInventory
{
    /// <summary>
    /// One entry per plugin GUID (a reloaded plugin seen by the chainloader too counts once, as <c>progenitor</c>), the
    /// package from <paramref name="packageOf"/> (the install ledger) when known, sorted by source then GUID.
    /// </summary>
    internal static List<ClientModEntry> Build(IEnumerable<ClientModObservation> observed, Func<string, string>? packageOf)
    {
        var byGuid = new Dictionary<string, ClientModObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in observed)
        {
            if (string.IsNullOrWhiteSpace(o.Guid)) continue;
            string g = o.Guid.Trim();
            if (byGuid.TryGetValue(g, out var had) && Rank(had.Source) >= Rank(o.Source)) continue;
            byGuid[g] = o;
        }

        var list = new List<ClientModEntry>(byGuid.Count);
        foreach (var o in byGuid.Values)
        {
            string package = "";
            try { package = packageOf?.Invoke(o.Guid) ?? ""; }
            catch { package = ""; }
            if (package.Length > 0 && !Install.RemoteInstallWire.IsValidPackage(package)) package = "";
            list.Add(new ClientModEntry(o.Guid, o.Name, o.Version, o.Source, package));
        }

        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Source, b.Source);
            return c != 0 ? c : string.Compare(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static int Rank(string source) => ClientModSources.Clean(source) switch
    {
        ClientModSources.Progenitor => 2,
        ClientModSources.Chainloader => 1,
        _ => 0,
    };

    /// <summary>
    /// The Thunderstore package of an install-ledger row: its <c>source</c> (<c>package:Owner-Name@1.2.3</c>), else its
    /// id when that is an <c>Owner-Name</c>; empty otherwise.
    /// </summary>
    internal static string PackageFromLedger(string? source, string? id)
    {
        string s = (source ?? "").Trim();
        if (s.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring("package:".Length);
            int at = s.IndexOf('@');
            if (at >= 0) s = s.Substring(0, at);
            if (Install.RemoteInstallWire.IsValidPackage(s)) return s;
        }

        string i = (id ?? "").Trim();
        return i.Length > 0 && Install.RemoteInstallWire.IsValidPackage(i) ? i : "";
    }

    /// <summary>A short signature of the entries (guid@version per line), so a resend goes out only when the set changed.</summary>
    internal static string Signature(IReadOnlyList<ClientModEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries) sb.Append(e.Guid).Append('@').Append(e.Version).Append('#').Append(e.Package).Append('\n');
        return sb.ToString();
    }
}
