using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

// Mod compatibility C1 (docs/mod-compatibility.md, section 1): feature negotiation. Everything in this file is game-free
// and compiled into the test suite; the live half (sending, receiving, the plugin facades) is ModCompatNet.cs and
// Plugins/PluginCompat.cs.

/// <summary>The limits of feature negotiation (docs/mod-compatibility.md, "Limits").</summary>
public static class ModFeatureLimits
{
    /// <summary>Longest feature name. Names match <c>[a-z0-9][a-z0-9._-]*</c>.</summary>
    public const int MaxNameChars = 48;

    /// <summary>Highest feature level. The lowest is 1.</summary>
    public const int MaxLevel = 65535;

    /// <summary>Distinct features one mod may offer.</summary>
    public const int MaxFeaturesPerMod = 32;

    /// <summary>Mods listed in one table; the rest are left out and the table says <c>"more": true</c>.</summary>
    public const int MaxModsPerTable = 256;

    /// <summary>Largest encoded table body (at most three packets).</summary>
    public const int MaxTableBytes = 32 * 1024;

    /// <summary>One packet, the transport's single-packet limit (<c>CustomEvents.MaxPayloadBytes</c>).</summary>
    public const int MaxPacketBytes = 15 * 1024;

    /// <summary>Changes are collected this long before one delta goes out, and deltas are at least this far apart.</summary>
    public const double DeltaSeconds = 1.0;
}

/// <summary>
/// What a mod can ask about the other side: its mods' features and versions. <see cref="Known"/> is false until the
/// peer's feature table has arrived, and always false for a peer whose Progenitor predates feature negotiation;
/// every <see cref="Has(string, int)"/> is false while it is.
/// </summary>
public interface IPeerView
{
    /// <summary>True once the peer's feature table has arrived.</summary>
    bool Known { get; }

    /// <summary>True when the peer runs this mod and offers <paramref name="feature"/> at <paramref name="minLevel"/> or higher.</summary>
    bool Has(string feature, int minLevel = 1);

    /// <summary>True when the peer runs <paramref name="modGuid"/> and it offers <paramref name="feature"/> at <paramref name="minLevel"/> or higher.</summary>
    bool Has(string modGuid, string feature, int minLevel = 1);

    /// <summary>The peer's version of <paramref name="modGuid"/>: from its feature table, else (client) the server's handshake rows; null when the peer lacks it.</summary>
    string? ModVersion(string modGuid);

    /// <summary>The peer's Progenitor (Core) version, from its feature table; null while <see cref="Known"/> is false.</summary>
    string? CoreVersion { get; }
}

/// <summary>Feature-name rules shared by <c>ctx.Features.Offer</c> and the table parser.</summary>
internal static class FeatureNames
{
    internal static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > ModFeatureLimits.MaxNameChars) return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (i == 0 ? !alnum : !(alnum || c == '.' || c == '_' || c == '-')) return false;
        }

        return true;
    }

    internal static bool IsValidLevel(int level) => level >= 1 && level <= ModFeatureLimits.MaxLevel;

    /// <summary>The exception <c>Offer</c> throws for a bad name or level, or null when both are fine.</summary>
    internal static string? Problem(string? name, int level)
    {
        if (!IsValid(name))
            return "Feature name '" + name + "' is not valid: 1 to " + ModFeatureLimits.MaxNameChars
                   + " characters, [a-z0-9] first, then [a-z0-9._-].";
        if (!IsValidLevel(level))
            return "Feature level " + level.ToString(CultureInfo.InvariantCulture) + " is out of range (1 to "
                   + ModFeatureLimits.MaxLevel.ToString(CultureInfo.InvariantCulture) + ").";
        return null;
    }
}

/// <summary>One mod's row in a feature table.</summary>
internal sealed class FeatureTableMod
{
    internal FeatureTableMod(string id, string version, string? peers = null)
    {
        Id = id ?? "";
        Version = version ?? "";
        Peers = peers;
    }

    internal string Id { get; }
    internal string Version { get; }

    /// <summary>
    /// Compat C3: the mod build's effective peer range (its declared <c>Peers</c>, else its explicit rule, else the same
    /// minor version). A C3 Core lists it for every mod; a C1/C2 Core never wrote it (null).
    /// </summary>
    internal string? Peers { get; }

    internal Dictionary<string, int> Features { get; } = new(StringComparer.Ordinal);

    internal bool SameAs(FeatureTableMod? other)
    {
        if (other == null) return false;
        if (!string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Version, other.Version, StringComparison.Ordinal)) return false;
        if (!string.Equals(Peers, other.Peers, StringComparison.Ordinal)) return false;
        if (Features.Count != other.Features.Count) return false;
        foreach (var kv in Features)
            if (!other.Features.TryGetValue(kv.Key, out int l) || l != kv.Value) return false;
        return true;
    }

    internal FeatureTableMod Copy()
    {
        var c = new FeatureTableMod(Id, Version, Peers);
        foreach (var kv in Features) c.Features[kv.Key] = kv.Value;
        return c;
    }
}

/// <summary>A peer's (or this process's) whole feature table.</summary>
internal sealed class FeatureTable
{
    internal string Core { get; set; } = "";
    internal string CoreSha { get; set; } = "";
    internal int BootstrapProtocol { get; set; }

    /// <summary>True when the sender left mods out (over <see cref="ModFeatureLimits.MaxModsPerTable"/> or the byte cap).</summary>
    internal bool More { get; set; }

    internal Dictionary<string, FeatureTableMod> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal FeatureTable Copy()
    {
        var t = new FeatureTable { Core = Core, CoreSha = CoreSha, BootstrapProtocol = BootstrapProtocol, More = More };
        foreach (var kv in Mods) t.Mods[kv.Key] = kv.Value.Copy();
        return t;
    }

    internal bool Has(string? modGuid, string? feature, int minLevel)
    {
        if (string.IsNullOrEmpty(modGuid) || string.IsNullOrEmpty(feature)) return false;
        if (minLevel < 1) minLevel = 1;
        return Mods.TryGetValue(modGuid!, out var mod) && mod.Features.TryGetValue(feature!, out int level) && level >= minLevel;
    }
}

/// <summary>
/// The <c>'PrFt'</c> wire (docs/mod-compatibility.md, "Wire"). A packet is
/// <c>[version u8][kind u8][seq u16le][count u16le][nbytes u16le][bytes]</c>: kind 1 a full table, kind 2 a delta; the
/// body is UTF-8 JSON split into at most three packets (<see cref="ModFeatureLimits.MaxTableBytes"/> in all).
/// Full: <c>{"v":1,"core":…,"coreSha":…,"bootstrapProtocol":5,"mods":[{"id","ver","peers"?,"f"?:{name:level}}],"more"?:true}</c>.
/// Delta: the same keys, where each listed mod replaces that mod's row whole, plus <c>"gone":[ids]</c> for mods no
/// longer loaded. Unknown keys are ignored; an invalid feature name or level is skipped.
/// </summary>
internal static class FeatureTableWire
{
    /// <summary><c>'PrFt'</c>. Inside the reserved range, so only Core may register or send on it.</summary>
    internal const int EventId = 0x50724674;

    internal const byte Version = 1;
    internal const byte KindFull = 1;
    internal const byte KindDelta = 2;
    internal const int HeaderBytes = 8;
    internal const int MaxSliceBytes = ModFeatureLimits.MaxPacketBytes - HeaderBytes;
    internal const int MaxPackets = (ModFeatureLimits.MaxTableBytes + MaxSliceBytes - 1) / MaxSliceBytes;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The body of a full table, cut to the limits: at most <see cref="ModFeatureLimits.MaxModsPerTable"/> mods and
    /// <see cref="ModFeatureLimits.MaxTableBytes"/> bytes, mods dropped from the end (ordinal id order) and
    /// <c>"more": true</c> set. <paramref name="kept"/> is how many mods made it.
    /// </summary>
    internal static byte[] FullBody(FeatureTable table, out int kept)
    {
        var mods = Sorted(table.Mods.Values);
        bool more = table.More;
        int n = mods.Count;
        if (n > ModFeatureLimits.MaxModsPerTable)
        {
            n = ModFeatureLimits.MaxModsPerTable;
            more = true;
        }

        while (true)
        {
            byte[] bytes = Serialize(table, mods, n, more, null);
            if (bytes.Length <= ModFeatureLimits.MaxTableBytes || n == 0)
            {
                kept = n;
                return bytes;
            }

            int over = bytes.Length - ModFeatureLimits.MaxTableBytes;
            int per = Math.Max(1, bytes.Length / Math.Max(1, n));
            n = Math.Max(0, n - Math.Max(1, over / per));
            more = true;
        }
    }

    /// <summary>The body of a delta: <paramref name="changed"/> rows replace the peer's, <paramref name="gone"/> ids are removed.</summary>
    internal static byte[] DeltaBody(FeatureTable header, IReadOnlyList<FeatureTableMod> changed, IReadOnlyList<string> gone, out int kept)
    {
        var mods = Sorted(changed);
        int n = Math.Min(mods.Count, ModFeatureLimits.MaxModsPerTable);
        bool more = mods.Count > n;
        while (true)
        {
            byte[] bytes = Serialize(header, mods, n, more, gone);
            if (bytes.Length <= ModFeatureLimits.MaxTableBytes || n == 0)
            {
                kept = n;
                return bytes;
            }

            int over = bytes.Length - ModFeatureLimits.MaxTableBytes;
            int per = Math.Max(1, bytes.Length / Math.Max(1, n));
            n = Math.Max(0, n - Math.Max(1, over / per));
            more = true;
        }
    }

    private static List<FeatureTableMod> Sorted(IEnumerable<FeatureTableMod> mods)
    {
        var list = new List<FeatureTableMod>(mods);
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        return list;
    }

    private static byte[] Serialize(FeatureTable header, List<FeatureTableMod> mods, int count, bool more, IReadOnlyList<string>? gone)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            if (header.Core.Length > 0) w.WriteString("core", header.Core);
            if (header.CoreSha.Length > 0) w.WriteString("coreSha", header.CoreSha);
            if (header.BootstrapProtocol != 0) w.WriteNumber("bootstrapProtocol", header.BootstrapProtocol);
            w.WriteStartArray("mods");
            for (int i = 0; i < count; i++)
            {
                var m = mods[i];
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("ver", m.Version);
                if (!string.IsNullOrEmpty(m.Peers)) w.WriteString("peers", m.Peers);
                if (m.Features.Count > 0)
                {
                    w.WriteStartObject("f");
                    var names = new List<string>(m.Features.Keys);
                    names.Sort(StringComparer.Ordinal);
                    foreach (var name in names) w.WriteNumber(name, m.Features[name]);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (gone != null && gone.Count > 0)
            {
                w.WriteStartArray("gone");
                int g = 0;
                foreach (var id in gone)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    w.WriteStringValue(id);
                    if (++g >= ModFeatureLimits.MaxModsPerTable) break;
                }

                w.WriteEndArray();
            }

            if (more) w.WriteBoolean("more", true);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Split a body into packets. Throws when the body is over <see cref="ModFeatureLimits.MaxTableBytes"/>.</summary>
    internal static List<byte[]> Pack(byte kind, byte[] body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (body.Length > ModFeatureLimits.MaxTableBytes)
            throw new ArgumentOutOfRangeException(nameof(body), "feature table exceeds " + ModFeatureLimits.MaxTableBytes + " bytes");
        int count = Math.Max(1, (body.Length + MaxSliceBytes - 1) / MaxSliceBytes);
        var packets = new List<byte[]>(count);
        int offset = 0;
        for (int seq = 0; seq < count; seq++)
        {
            int n = Math.Min(MaxSliceBytes, body.Length - offset);
            if (n < 0) n = 0;
            var p = new byte[HeaderBytes + n];
            p[0] = Version;
            p[1] = kind;
            p[2] = (byte)seq;
            p[3] = (byte)(seq >> 8);
            p[4] = (byte)count;
            p[5] = (byte)(count >> 8);
            p[6] = (byte)n;
            p[7] = (byte)(n >> 8);
            if (n > 0) Buffer.BlockCopy(body, offset, p, HeaderBytes, n);
            packets.Add(p);
            offset += n;
        }

        return packets;
    }

    /// <summary>Read one packet's header. False when it is not a packet of this version or breaks a cap.</summary>
    internal static bool TryHeader(ReadOnlySpan<byte> packet, out byte kind, out int seq, out int count, out int bodyBytes)
    {
        kind = 0;
        seq = count = bodyBytes = 0;
        if (packet.Length < HeaderBytes || packet.Length > ModFeatureLimits.MaxPacketBytes) return false;
        if (packet[0] != Version) return false;
        kind = packet[1];
        if (kind != KindFull && kind != KindDelta) return false;
        seq = packet[2] | (packet[3] << 8);
        count = packet[4] | (packet[5] << 8);
        bodyBytes = packet[6] | (packet[7] << 8);
        return count >= 1 && count <= MaxPackets && seq < count && bodyBytes == packet.Length - HeaderBytes;
    }

    /// <summary>
    /// Parse a body. Fails closed on anything that is not strict UTF-8 JSON with a <c>mods</c> list. Within that,
    /// unknown keys are ignored and a bad row, name or level is skipped; at most
    /// <see cref="ModFeatureLimits.MaxModsPerTable"/> mods and <see cref="ModFeatureLimits.MaxFeaturesPerMod"/> features
    /// per mod are read (the rest set <c>More</c>).
    /// </summary>
    internal static bool TryParse(byte[] body, out FeatureTable table, out List<string> gone, out string? error)
    {
        table = new FeatureTable();
        gone = new List<string>();
        error = null;
        if (body == null || body.Length == 0 || body.Length > ModFeatureLimits.MaxTableBytes)
        {
            error = "the table is empty or larger than " + ModFeatureLimits.MaxTableBytes + " bytes";
            return false;
        }

        try
        {
            string json = StrictUtf8.GetString(body);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
            {
                error = "the table is not an object with a mods list";
                return false;
            }

            table.Core = Str(root, "core");
            table.CoreSha = Str(root, "coreSha");
            if (root.TryGetProperty("bootstrapProtocol", out var bp) && bp.ValueKind == JsonValueKind.Number && bp.TryGetInt32(out int proto))
                table.BootstrapProtocol = proto;
            table.More = root.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True;

            foreach (var m in mods.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (table.Mods.Count >= ModFeatureLimits.MaxModsPerTable)
                {
                    table.More = true;
                    break;
                }

                string id = Str(m, "id");
                if (id.Length == 0 || id.Length > 128) continue;
                string ver = Str(m, "ver");
                if (ver.Length > 64) ver = ver.Substring(0, 64);
                string peers = Str(m, "peers");
                var row = new FeatureTableMod(id, ver, peers.Length == 0 || peers.Length > 64 ? null : peers);
                if (m.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in f.EnumerateObject())
                    {
                        if (row.Features.Count >= ModFeatureLimits.MaxFeaturesPerMod)
                        {
                            table.More = true;
                            break;
                        }

                        if (!FeatureNames.IsValid(p.Name)) continue;
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int level) || !FeatureNames.IsValidLevel(level)) continue;
                        row.Features[p.Name] = level;
                    }
                }

                table.Mods[id] = row;
            }

            if (root.TryGetProperty("gone", out var g) && g.ValueKind == JsonValueKind.Array)
                foreach (var e in g.EnumerateArray())
                {
                    if (gone.Count >= ModFeatureLimits.MaxModsPerTable) break;
                    if (e.ValueKind != JsonValueKind.String) continue;
                    string id = e.GetString() ?? "";
                    if (id.Length > 0 && id.Length <= 128) gone.Add(id);
                }

            return true;
        }
        catch (Exception e) when (e is JsonException || e is DecoderFallbackException || e is ArgumentException)
        {
            error = "the table is not valid JSON (" + e.Message + ")";
            return false;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// Reassembles one peer's <c>'PrFt'</c> packets. Packets of one table arrive in order on the reliable channel; a
/// packet with seq 0 starts a new table, anything out of order or over the caps drops what was collected.
/// </summary>
internal sealed class FeatureTableAssembler
{
    private byte _kind;
    private int _count;
    private int _next;
    private MemoryStream? _buffer;

    /// <summary>Add a packet. True with the whole body when this was the last one.</summary>
    internal bool Add(ReadOnlySpan<byte> packet, out byte kind, out byte[] body, out string? error)
    {
        kind = 0;
        body = Array.Empty<byte>();
        error = null;
        if (!FeatureTableWire.TryHeader(packet, out byte k, out int seq, out int count, out int n))
        {
            Reset();
            error = "not a feature table packet of this version";
            return false;
        }

        if (seq == 0)
        {
            _kind = k;
            _count = count;
            _next = 0;
            _buffer = new MemoryStream();
        }
        else if (_buffer == null || seq != _next || k != _kind || count != _count)
        {
            Reset();
            error = "a feature table packet arrived out of order";
            return false;
        }

        if (_buffer!.Length + n > ModFeatureLimits.MaxTableBytes)
        {
            Reset();
            error = "the feature table is over " + ModFeatureLimits.MaxTableBytes + " bytes";
            return false;
        }

        _buffer.Write(packet.Slice(FeatureTableWire.HeaderBytes, n));
        _next = seq + 1;
        if (_next < _count) return false;
        kind = _kind;
        body = _buffer.ToArray();
        Reset();
        return true;
    }

    internal void Reset()
    {
        _buffer = null;
        _next = 0;
        _count = 0;
        _kind = 0;
    }
}

/// <summary>
/// This process's offers (<c>ctx.Features.Offer</c>), per mod. An offer is a handle: the feature's level is the
/// highest level among its live handles, and disposing the last handle withdraws it. Every change raises
/// <see cref="Changed"/> with the mod id.
/// </summary>
internal sealed class FeatureOfferBook
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Offer>> _byMod = new(StringComparer.OrdinalIgnoreCase);

    internal event Action<string>? Changed;

    private sealed class Offer : IDisposable
    {
        private FeatureOfferBook? _book;
        internal readonly string Mod;
        internal readonly string Name;
        internal readonly int Level;

        internal Offer(FeatureOfferBook book, string mod, string name, int level)
        {
            _book = book;
            Mod = mod;
            Name = name;
            Level = level;
        }

        public void Dispose()
        {
            var book = System.Threading.Interlocked.Exchange(ref _book, null);
            book?.Withdraw(this);
        }
    }

    /// <summary>Add an offer. Throws <see cref="ArgumentException"/> for a bad name or level or a 33rd distinct feature.</summary>
    internal IDisposable Add(string modId, string name, int level = 1)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
        string? problem = FeatureNames.Problem(name, level);
        if (problem != null) throw new ArgumentException(problem, nameof(name));
        var offer = new Offer(this, modId.Trim(), name, level);
        lock (_gate)
        {
            if (!_byMod.TryGetValue(offer.Mod, out var list)) _byMod[offer.Mod] = list = new List<Offer>();
            bool known = false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in list)
            {
                names.Add(o.Name);
                if (o.Name == name) known = true;
            }

            if (!known && names.Count >= ModFeatureLimits.MaxFeaturesPerMod)
                throw new ArgumentException(
                    "Mod '" + offer.Mod + "' already offers " + ModFeatureLimits.MaxFeaturesPerMod
                    + " features, the limit per mod; '" + name + "' was not added.", nameof(name));
            list.Add(offer);
        }

        Raise(offer.Mod);
        return offer;
    }

    private void Withdraw(Offer offer)
    {
        bool removed;
        lock (_gate)
        {
            removed = _byMod.TryGetValue(offer.Mod, out var list) && list.Remove(offer);
            if (removed && list!.Count == 0) _byMod.Remove(offer.Mod);
        }

        if (removed) Raise(offer.Mod);
    }

    /// <summary>One mod's current features (name to highest live level).</summary>
    internal Dictionary<string, int> Of(string? modId)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(modId)) return result;
        lock (_gate)
        {
            if (!_byMod.TryGetValue(modId!.Trim(), out var list)) return result;
            foreach (var o in list)
                if (!result.TryGetValue(o.Name, out int l) || o.Level > l) result[o.Name] = o.Level;
        }

        return result;
    }

    /// <summary>Every mod with at least one live offer.</summary>
    internal List<string> Mods()
    {
        lock (_gate) return new List<string>(_byMod.Keys);
    }

    internal void Clear()
    {
        lock (_gate) _byMod.Clear();
    }

    private void Raise(string mod)
    {
        try { Changed?.Invoke(mod); }
        catch { /* a listener must never cost an offer */ }
    }
}

/// <summary>Builds this process's table from the loaded mods and the offer book.</summary>
internal static class FeatureTableBuilder
{
    /// <summary>
    /// Every loaded mod (<paramref name="loaded"/>: id and version) with its offered features, plus any mod with an
    /// offer that is not in the list (it is loading). Row order is irrelevant; the encoder sorts.
    /// </summary>
    internal static FeatureTable Build(
        IEnumerable<(string Id, string Version)> loaded, FeatureOfferBook offers, string core, string coreSha, int bootstrapProtocol)
    {
        var withPeers = new List<(string, string, string?)>();
        foreach (var (id, version) in loaded) withPeers.Add((id, version, null));
        return Build(withPeers, offers, core, coreSha, bootstrapProtocol);
    }

    /// <summary>
    /// Compat C3: the same, with each loaded mod's effective peer range (<paramref name="loaded"/>' third item; null or
    /// longer than <see cref="Progenitor.Plugins.CompatRange.MaxChars"/> leaves it out).
    /// </summary>
    internal static FeatureTable Build(
        IEnumerable<(string Id, string Version, string? Peers)> loaded, FeatureOfferBook offers, string core, string coreSha, int bootstrapProtocol)
    {
        var table = new FeatureTable { Core = core ?? "", CoreSha = coreSha ?? "", BootstrapProtocol = bootstrapProtocol };
        foreach (var (id, version, peers) in loaded)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            string? p = string.IsNullOrWhiteSpace(peers) || peers!.Trim().Length > Progenitor.Plugins.CompatRange.MaxChars ? null : peers.Trim();
            table.Mods[id.Trim()] = new FeatureTableMod(id.Trim(), version ?? "", p);
        }

        foreach (var mod in offers.Mods())
        {
            if (!table.Mods.TryGetValue(mod, out var row)) table.Mods[mod] = row = new FeatureTableMod(mod, "");
            foreach (var kv in offers.Of(mod)) row.Features[kv.Key] = kv.Value;
        }

        return table;
    }
}

/// <summary>
/// The peers' tables, keyed by an int (the client uses 0 for its server; the server uses the user index). A full
/// table replaces, a delta replaces the rows it lists and removes the <c>gone</c> ids; a delta for a peer with no
/// table is ignored (a delta alone never makes a peer known). <see cref="Changed"/> fires after every change.
/// </summary>
internal sealed class PeerFeatureBook
{
    private readonly object _gate = new();
    private readonly Dictionary<int, FeatureTable> _tables = new();
    private readonly Dictionary<int, FeatureTableAssembler> _assemblers = new();

    internal event Action<int>? Changed;

    /// <summary>The peer's table, or null when none has arrived.</summary>
    internal FeatureTable? Get(int peer)
    {
        lock (_gate) return _tables.TryGetValue(peer, out var t) ? t : null;
    }

    internal bool Known(int peer)
    {
        lock (_gate) return _tables.ContainsKey(peer);
    }

    internal List<int> Peers()
    {
        lock (_gate) return new List<int>(_tables.Keys);
    }

    /// <summary>What <see cref="Receive"/> did with a packet.</summary>
    internal enum Outcome
    {
        /// <summary>A middle packet of a table, or nothing usable (see the log line).</summary>
        None,

        /// <summary>A full table replaced the peer's.</summary>
        Full,

        /// <summary>A delta was merged into the peer's table.</summary>
        Delta,

        /// <summary>A full table from a peer not yet accepted was held (<see cref="PromotePending"/> applies it).</summary>
        Held,
    }

    private readonly Dictionary<int, FeatureTable> _pending = new();

    // Compat C3: how many full tables each peer has sent (held or applied), so the join hold can wait for one that came
    // after the answer it is judging.
    private readonly Dictionary<int, int> _fullCounts = new();

    /// <summary>How many full tables <paramref name="peer"/> has sent since it was last forgotten.</summary>
    internal int FullCount(int peer)
    {
        lock (_gate) return _fullCounts.TryGetValue(peer, out int n) ? n : 0;
    }

    /// <summary>The peer's held table when one is waiting for its handshake, else its applied table, else null.</summary>
    internal FeatureTable? TableOrPending(int peer)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(peer, out var p)) return p;
            return _tables.TryGetValue(peer, out var t) ? t : null;
        }
    }

    private void CountFull(int peer)
    {
        lock (_gate)
        {
            if (_fullCounts.Count >= 512 && !_fullCounts.ContainsKey(peer)) _fullCounts.Clear();
            _fullCounts[peer] = (_fullCounts.TryGetValue(peer, out int n) ? n : 0) + 1;
        }
    }

    /// <summary>
    /// Feed one <c>'PrFt'</c> packet from <paramref name="peer"/>. A peer that is not <paramref name="accepted"/> (its
    /// handshake has not passed) cannot change the book or raise <see cref="Changed"/>: its last full table is held
    /// (one per peer, replacing the previous one) until <see cref="PromotePending"/>, and its deltas are dropped.
    /// <paramref name="log"/> is set when something was refused.
    /// </summary>
    internal Outcome Receive(int peer, ReadOnlySpan<byte> packet, out string? log, bool accepted = true)
    {
        log = null;
        FeatureTableAssembler asm;
        lock (_gate)
        {
            if (!_assemblers.TryGetValue(peer, out asm!))
            {
                if (_assemblers.Count >= 512) _assemblers.Clear();
                _assemblers[peer] = asm = new FeatureTableAssembler();
            }
        }

        if (!asm.Add(packet, out byte kind, out byte[] body, out string? error))
        {
            if (error != null) log = "Features: dropped a table packet: " + error;
            return Outcome.None;
        }

        if (!FeatureTableWire.TryParse(body, out var table, out var gone, out error))
        {
            log = "Features: dropped a table: " + error;
            return Outcome.None;
        }

        if (!accepted)
        {
            if (kind != FeatureTableWire.KindFull)
            {
                log = "Features: dropped a delta from a peer whose handshake has not passed";
                return Outcome.None;
            }

            lock (_gate)
            {
                if (_pending.Count >= 512 && !_pending.ContainsKey(peer)) _pending.Clear();
                _pending[peer] = table;
            }

            CountFull(peer);
            return Outcome.Held;
        }

        if (kind == FeatureTableWire.KindFull)
        {
            CountFull(peer);
            ApplyFull(peer, table);
            return Outcome.Full;
        }

        return ApplyDelta(peer, table, gone, out log) ? Outcome.Delta : Outcome.None;
    }

    /// <summary>The peer's handshake passed: apply the full table it sent before that, if any. True when one was applied.</summary>
    internal bool PromotePending(int peer)
    {
        FeatureTable? table;
        lock (_gate)
        {
            if (!_pending.TryGetValue(peer, out table)) return false;
            _pending.Remove(peer);
        }

        ApplyFull(peer, table);
        return true;
    }

    internal bool ApplyFull(int peer, FeatureTable table)
    {
        lock (_gate)
        {
            if (_tables.Count >= 512 && !_tables.ContainsKey(peer)) _tables.Clear();
            _tables[peer] = table;
            _pending.Remove(peer);
        }

        Raise(peer);
        return true;
    }

    /// <summary>
    /// Merge a delta in place. The merged table never holds more than <see cref="ModFeatureLimits.MaxModsPerTable"/>
    /// mods: a new id past the cap is left out and the table says <c>More</c>, so a peer cannot grow it by sending
    /// deltas of fresh ids. Cost is the size of the delta, not of the table.
    /// </summary>
    internal bool ApplyDelta(int peer, FeatureTable delta, IReadOnlyList<string> gone, out string? log)
    {
        log = null;
        lock (_gate)
        {
            if (!_tables.TryGetValue(peer, out var current))
            {
                log = "Features: a delta arrived before the full table; ignored";
                return false;
            }

            if (delta.Core.Length > 0) current.Core = delta.Core;
            if (delta.CoreSha.Length > 0) current.CoreSha = delta.CoreSha;
            if (delta.BootstrapProtocol != 0) current.BootstrapProtocol = delta.BootstrapProtocol;
            if (delta.More) current.More = true;
            foreach (var id in gone) current.Mods.Remove(id);
            foreach (var kv in delta.Mods)
            {
                if (current.Mods.ContainsKey(kv.Key) || current.Mods.Count < ModFeatureLimits.MaxModsPerTable)
                    current.Mods[kv.Key] = kv.Value;
                else
                    current.More = true;
            }
        }

        Raise(peer);
        return true;
    }

    /// <summary>Forget a peer (it left, or a new join starts). Raises <see cref="Changed"/> when it had a table.</summary>
    internal void Forget(int peer)
    {
        bool had;
        lock (_gate)
        {
            had = _tables.Remove(peer);
            _assemblers.Remove(peer);
            _pending.Remove(peer);
            _fullCounts.Remove(peer);
        }

        if (had) Raise(peer);
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _tables.Clear();
            _assemblers.Clear();
            _pending.Clear();
            _fullCounts.Clear();
        }
    }

    private void Raise(int peer)
    {
        var handlers = Changed;
        if (handlers == null) return;
        foreach (Action<int> h in handlers.GetInvocationList())
        {
            try { h(peer); }
            catch { /* one listener must not stop the others */ }
        }
    }
}

/// <summary>A peer as one mod sees it: <see cref="Has(string, int)"/> asks about that mod's own features.</summary>
internal sealed class PeerTableView : IPeerView
{
    private readonly string _self;
    private readonly Func<FeatureTable?> _table;
    private readonly Func<string, string?>? _fallbackVersion;

    internal PeerTableView(string selfModId, Func<FeatureTable?> table, Func<string, string?>? fallbackVersion = null)
    {
        _self = selfModId ?? "";
        _table = table ?? (() => null);
        _fallbackVersion = fallbackVersion;
    }

    /// <summary>A view that never knows anything (the server's <c>ctx.Peer</c>, the client's <c>ctx.Peers.Of</c>).</summary>
    internal static readonly IPeerView Unknown = new PeerTableView("", () => null);

    public bool Known => SafeTable() != null;

    public bool Has(string feature, int minLevel = 1) => SafeTable()?.Has(_self, feature, minLevel) ?? false;

    public bool Has(string modGuid, string feature, int minLevel = 1) => SafeTable()?.Has(modGuid, feature, minLevel) ?? false;

    public string? ModVersion(string modGuid)
    {
        if (string.IsNullOrEmpty(modGuid)) return null;
        var t = SafeTable();
        if (t != null && t.Mods.TryGetValue(modGuid, out var row) && row.Version.Length > 0) return row.Version;
        if (t != null && t.Mods.ContainsKey(modGuid)) return "";
        try { return _fallbackVersion?.Invoke(modGuid); }
        catch { return null; }
    }

    public string? CoreVersion
    {
        get
        {
            var t = SafeTable();
            return t == null || t.Core.Length == 0 ? null : t.Core;
        }
    }

    private FeatureTable? SafeTable()
    {
        try { return _table(); }
        catch { return null; }
    }
}

/// <summary>
/// The delta schedule: changes are collected for <see cref="ModFeatureLimits.DeltaSeconds"/> after the first one, and
/// two deltas are at least that far apart. <see cref="TakeDelta"/> compares the current table with the last one sent
/// and returns only what differs (each delta row replaces the peer's row whole, so a peer that already has a row is
/// unaffected by seeing it again).
/// </summary>
internal sealed class FeatureDeltaSchedule
{
    private Dictionary<string, FeatureTableMod> _sent = new(StringComparer.OrdinalIgnoreCase);
    private double _dirtySince = double.NaN;
    private double _lastSent = double.NegativeInfinity;

    internal bool Dirty => !double.IsNaN(_dirtySince);

    /// <summary>Something changed at <paramref name="now"/>. Later changes before the send keep the first time.</summary>
    internal void MarkDirty(double now)
    {
        if (double.IsNaN(_dirtySince)) _dirtySince = now;
    }

    /// <summary>True when a delta is due: dirty for a full interval and an interval since the last one.</summary>
    internal bool Due(double now) =>
        Dirty && now - _dirtySince >= ModFeatureLimits.DeltaSeconds && now - _lastSent >= ModFeatureLimits.DeltaSeconds;

    /// <summary>Make <paramref name="table"/> the state every peer has (install, and after a delta or full broadcast).</summary>
    internal void Baseline(FeatureTable table)
    {
        var copy = new Dictionary<string, FeatureTableMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in table.Mods) copy[kv.Key] = kv.Value.Copy();
        _sent = copy;
    }

    /// <summary>
    /// A full table went to one peer. Ids it listed that the baseline lacks are added, so a later unload of one of them
    /// reaches that peer as <c>gone</c>; rows the baseline already has are kept, so a change not yet broadcast to the
    /// other peers still shows up in the next delta (seeing a row twice is harmless: delta rows replace whole).
    /// </summary>
    internal void NoteFullSent(FeatureTable table)
    {
        foreach (var kv in table.Mods)
            if (!_sent.ContainsKey(kv.Key)) _sent[kv.Key] = kv.Value.Copy();
    }

    /// <summary>What differs from the baseline. False when nothing does. Changes nothing; <see cref="Commit"/> does.</summary>
    internal bool Diff(FeatureTable current, out List<FeatureTableMod> changed, out List<string> gone)
    {
        changed = new List<FeatureTableMod>();
        gone = new List<string>();
        foreach (var kv in current.Mods)
            if (!_sent.TryGetValue(kv.Key, out var old) || !old.SameAs(kv.Value)) changed.Add(kv.Value.Copy());
        foreach (var id in _sent.Keys)
            if (!current.Mods.ContainsKey(id)) gone.Add(id);
        return changed.Count > 0 || gone.Count > 0;
    }

    /// <summary>
    /// True when a delta carrying all of <paramref name="changedCount"/> rows (of which the encoder kept
    /// <paramref name="keptRows"/>) and <paramref name="goneCount"/> removals is whole. Otherwise send a full table: a
    /// cut delta would leave rows or removals behind for good.
    /// </summary>
    internal static bool DeltaIsWhole(int changedCount, int keptRows, int goneCount) =>
        keptRows >= changedCount && goneCount <= ModFeatureLimits.MaxModsPerTable;

    /// <summary><paramref name="current"/> went to every peer (as a whole delta or a full table): it is the new baseline.</summary>
    internal void Commit(FeatureTable current, double now)
    {
        _dirtySince = double.NaN;
        Baseline(current);
        _lastSent = now;
    }

    /// <summary><see cref="Diff"/> then <see cref="Commit"/> when something differs (clears the dirty mark either way).</summary>
    internal bool TakeDelta(FeatureTable current, double now, out List<FeatureTableMod> changed, out List<string> gone)
    {
        _dirtySince = double.NaN;
        if (!Diff(current, out changed, out gone)) return false;
        Commit(current, now);
        return true;
    }

    internal void Reset()
    {
        _sent = new Dictionary<string, FeatureTableMod>(StringComparer.OrdinalIgnoreCase);
        _dirtySince = double.NaN;
        _lastSent = double.NegativeInfinity;
    }
}

/// <summary>
/// The raw <c>ctx.Net</c> warning (docs/mod-compatibility.md, "Unversioned-message warning"): one line per mod and
/// event id per Core generation when a mod sends a raw message while the peer runs the same mod and the two may be
/// on different versions. It never blocks a send.
/// </summary>
internal sealed class RawSendWarnings
{
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The line to log, or null. Warns when the peer's table lists the mod and either its version differs from ours or
    /// the mod's declared rule lets two versions meet (<paramref name="ruleAllowsOtherVersions"/>: no attribute, or a
    /// rule other than Exact).
    /// </summary>
    internal string? Check(string modId, int eventId, string localVersion, string? peerVersion, bool ruleAllowsOtherVersions)
    {
        if (string.IsNullOrEmpty(modId) || peerVersion == null) return null;
        bool differs = !string.Equals(peerVersion, localVersion, StringComparison.OrdinalIgnoreCase);
        if (!differs && !ruleAllowsOtherVersions) return null;
        lock (_warned)
            if (!_warned.Add(modId + "|" + eventId.ToString(CultureInfo.InvariantCulture))) return null;
        return "Messages: " + modId + " sends raw ctx.Net messages (id " + ReceiveGate.Fourcc(eventId)
               + "); peers on another version may misread them. Use ctx.Messages.";
    }

    /// <summary>True when this mod and id were already warned about this generation.</summary>
    internal bool Warned(string modId, int eventId)
    {
        lock (_warned) return _warned.Contains(modId + "|" + eventId.ToString(CultureInfo.InvariantCulture));
    }

    internal void Clear()
    {
        lock (_warned) _warned.Clear();
    }
}

/// <summary>
/// Process-wide C1/C2 state that the handshake (compiled into the tests) touches: which capability bits this Core adds
/// to a request, and what the joined server's request declared (client).
/// </summary>
internal static class ModCompatCaps
{
    /// <summary>The bits a server puts on every handshake request (and the client refresh request).</summary>
    internal const Handshake.Capability RequestBits = Handshake.Capability.ModFeatures | Handshake.Capability.ModMessages
        | Handshake.Capability.VersionRanges;

    /// <summary>
    /// <see cref="RequestBits"/> plus compat C3's Version matching bits for the server's current setting (a strict mode
    /// tells the client, so it can pause an optional mod at another version before it answers).
    /// </summary>
    internal static Handshake.Capability RequestBitsNow() => RequestBits | VersionMatchJudge.ModeBits(VersionMatching.Current());

    private static Handshake.Capability _serverCaps;

    /// <summary>Client: the capability bits of the joined server's last request.</summary>
    internal static Handshake.Capability ServerCaps => _serverCaps;

    /// <summary>Client: a handshake request arrived (called after a new join's context was cleared).</summary>
    internal static void NoteServerCaps(Handshake.Capability caps) => _serverCaps = caps;

    internal static void Reset() => _serverCaps = Handshake.Capability.None;
}
