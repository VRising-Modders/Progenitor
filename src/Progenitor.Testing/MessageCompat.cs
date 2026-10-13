using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Testing;

/// <summary>A compatibility check failed. The message lists every failure.</summary>
public sealed class MessageCompatException : Exception
{
    public MessageCompatException(string message) : base(message)
    {
    }
}

/// <summary>What a replay or range check found.</summary>
public sealed class ReplayResult
{
    private readonly List<string> _failures = new();
    private readonly List<string> _notes = new();
    private readonly SortedSet<string> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _others = new(StringComparer.OrdinalIgnoreCase);

    internal ReplayResult(string what) => What = what;

    /// <summary>What was checked, for the messages.</summary>
    public string What { get; }

    /// <summary>Fixtures replayed (decoded, and handed to the handler when there is one).</summary>
    public int Checked { get; internal set; }

    /// <summary>Every failure, one line each, naming the fixture.</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>What was skipped or tolerated (a fixture outside the range, a dropped message, an unknown enum value).</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>The mod versions whose fixtures were replayed and passed.</summary>
    public IReadOnlyCollection<string> Versions => _versions;

    /// <summary>
    /// <see cref="MessageCompat.CheckRange"/>: the versions inside the range, other than the build under test, whose
    /// fixtures passed. These are what back a range; the build's own fixtures prove nothing about other versions.
    /// </summary>
    public IReadOnlyCollection<string> OtherVersions => _others;

    /// <summary>True when nothing failed.</summary>
    public bool Ok => _failures.Count == 0;

    internal void Fail(string line) => _failures.Add(line);

    internal void Note(string line) => _notes.Add(line);

    internal void Saw(string version) => _versions.Add(version);

    internal void SawOther(string version) => _others.Add(version);

    /// <summary>Throw <see cref="MessageCompatException"/> listing the failures, when there are any.</summary>
    public ReplayResult ThrowIfFailed()
    {
        if (!Ok) throw new MessageCompatException(ToString());
        return this;
    }

    public override string ToString()
    {
        var lines = new List<string>
        {
            What + ": " + Checked.ToString(CultureInfo.InvariantCulture) + " fixture(s) from " + (_versions.Count == 0 ? "no version" : string.Join(", ", _versions))
            + (Ok ? ", ok" : ", " + _failures.Count.ToString(CultureInfo.InvariantCulture) + " failure(s)"),
        };
        lines.AddRange(_failures.Select(f => "  FAIL " + f));
        lines.AddRange(_notes.Select(n => "  note " + n));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Finds a mod's message types without the game.</summary>
public static class MessageTypes
{
    private const string LinkAdvice = "Link the message's source file into the test project instead of referencing the mod's built DLL.";

    /// <summary>
    /// Every class in <paramref name="assembly"/> with <c>[ModMessage]</c> (optionally only in <paramref name="ns"/> and
    /// its sub-namespaces), ordered by message name. The message classes must be compiled into that assembly from source
    /// (link the mod's message file into the test project), so they carry this library's attribute: a class compiled
    /// against the game's Progenitor.dll, or one whose attributes cannot be read without the game, is refused with that
    /// advice.
    /// </summary>
    public static IReadOnlyList<Type> In(Assembly assembly, string? ns = null)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        var found = new List<Type>();
        foreach (var t in types)
        {
            if (!t.IsClass) continue;
            if (ns != null && !(t.Namespace == ns || (t.Namespace ?? "").StartsWith(ns + ".", StringComparison.Ordinal))) continue;
            try
            {
                if (t.GetCustomAttribute<ModMessageAttribute>(inherit: false) != null)
                {
                    found.Add(t);
                    continue;
                }

                foreach (var data in CustomAttributeData.GetCustomAttributes(t))
                    if (data.AttributeType.Name == nameof(ModMessageAttribute) && data.AttributeType != typeof(ModMessageAttribute))
                        throw new MessageCompatException(t.FullName + " carries the game assembly's [ModMessage]. " + LinkAdvice);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            {
                throw new MessageCompatException("The attributes of " + t.FullName + " cannot be read without the game (" + e.Message + "). " + LinkAdvice);
            }
        }

        found.Sort((a, b) => string.CompareOrdinal(MessageCompat.NameOf(a), MessageCompat.NameOf(b)));
        return found;
    }
}

/// <summary>
/// Game-free compatibility checks for a mod's versioned messages (<c>ctx.Messages</c>): encode and decode exactly as the
/// game does, replay recorded fixtures of other versions against today's message types and handlers, prove that unknown
/// fields are skipped, and check a declared <c>Peers</c> range against the recorded versions.
/// </summary>
public static class MessageCompat
{
    /// <summary>Field numbers <see cref="WithUnknownFields"/> uses: far above any number a mod would choose.</summary>
    public const int UnknownFieldBase = 536_870_900;

    private static readonly Dictionary<Type, (MessageSchema Schema, ModMessageAttribute Attr)> Schemas = new();

    internal static (MessageSchema Schema, ModMessageAttribute Attr) SchemaOf(Type type)
    {
        lock (Schemas)
        {
            if (Schemas.TryGetValue(type, out var s)) return s;
            MessageSchema schema;
            try { schema = MessageSchema.For(type); }
            catch (InvalidOperationException e) { throw new MessageCompatException(e.Message); }
            var attr = schema.Message ?? throw new MessageCompatException(type.FullName + " has no [ModMessage] attribute.");
            if (!ModMessageEnvelope.IsValidName(attr.Name))
                throw new MessageCompatException(type.FullName + ": '" + attr.Name + "' is not a valid message name (1 to 64 characters of [a-z0-9._-]).");
            if (!ModMessageEnvelope.IsValidVersion(attr.Version) || attr.MinReaderVersion < 1 || attr.MinReaderVersion > attr.Version)
                throw new MessageCompatException(type.FullName + ": Version " + attr.Version + " / MinReaderVersion " + attr.MinReaderVersion
                                                 + " is not valid (1 <= MinReaderVersion <= Version <= 65535).");
            s = (schema, attr);
            Schemas[type] = s;
            return s;
        }
    }

    /// <summary>The message name of a <c>[ModMessage]</c> type.</summary>
    public static string NameOf(Type messageType) =>
        messageType.GetCustomAttribute<ModMessageAttribute>(inherit: false)?.Name
        ?? throw new MessageCompatException(messageType.FullName + " has no [ModMessage] attribute.");

    internal static byte[] EncodeWith(MessageSchema schema, object message)
    {
        try
        {
            var payload = schema.Encode(message);
            if (payload.Length > ModMessageEnvelope.MaxPayloadBytes)
                throw new MessageCompatException(message.GetType().Name + " encodes to " + payload.Length + " bytes; a message holds at most "
                                                 + ModMessageEnvelope.MaxPayloadBytes + " (the game's send returns TooLarge).");
            return payload;
        }
        catch (MessageTooLargeException e) { throw new MessageCompatException(e.Message + " (the game's send returns TooLarge)."); }
        catch (InvalidOperationException e) { throw new MessageCompatException(e.Message); }
    }

    /// <summary>The payload <c>ctx.Messages</c> sends for <paramref name="message"/> (without the envelope header).</summary>
    public static byte[] Encode<T>(T message) where T : class
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        return EncodeWith(SchemaOf(message.GetType()).Schema, message);
    }

    /// <summary>Decode <paramref name="payload"/> as the game's handler for <typeparamref name="T"/> would. Throws <see cref="FormatException"/> for a malformed payload.</summary>
    public static T Decode<T>(byte[] payload) where T : class, new() => (T)SchemaOf(typeof(T)).Schema.Decode(payload ?? Array.Empty<byte>());

    /// <summary>
    /// <paramref name="payload"/> with fields a newer version might add: one of each wire type (varint, 64-bit,
    /// length-delimited, 32-bit) at numbers from <see cref="UnknownFieldBase"/>, half before and half after the known
    /// fields. Seeded, so a failure reproduces.
    /// </summary>
    public static byte[] WithUnknownFields(byte[] payload, int seed = 1)
    {
        var rng = new Random(seed);
        var before = new MessageWriter();
        var after = new MessageWriter();
        before.UInt64(UnknownFieldBase, (ulong)rng.Next(1, int.MaxValue));
        before.Double(UnknownFieldBase + 1, rng.NextDouble());
        var bytes = new byte[rng.Next(1, 24)];
        rng.NextBytes(bytes);
        after.Bytes(UnknownFieldBase + 2, bytes);
        after.Float(UnknownFieldBase + 3, (float)rng.NextDouble());
        var result = new List<byte>(before.ToArray());
        result.AddRange(payload ?? Array.Empty<byte>());
        result.AddRange(after.ToArray());
        return result.ToArray();
    }

    /// <summary>
    /// Prove that a reader of <typeparamref name="T"/> skips fields it does not know: <paramref name="message"/> with
    /// unknown fields added must decode to the same message. Throws <see cref="MessageCompatException"/> otherwise.
    /// </summary>
    public static void AssertSkipsUnknownFields<T>(T message, int seed = 1) where T : class, new()
    {
        byte[] plain = Encode(message);
        T read;
        try { read = Decode<T>(WithUnknownFields(plain, seed)); }
        catch (FormatException e) { throw new MessageCompatException(typeof(T).Name + " with unknown fields could not be decoded: " + e.Message); }
        byte[] again = Encode(read);
        if (!plain.SequenceEqual(again))
            throw new MessageCompatException(typeof(T).Name + " read with unknown fields differs from the original (" + plain.Length + " bytes before, "
                                             + again.Length + " after).");
    }

    /// <summary>
    /// Replay every fixture of <typeparamref name="T"/>'s message against today's type: each payload must decode (also
    /// with unknown fields added), keep every field this type knows with the same wire type and value, match the
    /// fixture's recorded field schema where it has one (a field number whose type changed fails, also inside nested
    /// messages and where the wire type is the same, such as <c>int</c> to <c>uint</c> or <c>string</c> to
    /// <c>byte[]</c>), and run through <paramref name="handler"/> without throwing. A field the fixture carries that this
    /// type lacks is a note here (<see cref="CheckRange"/> fails it for versions inside the range). A fixture whose
    /// minimum reader version is above this type's <c>Version</c> is dropped by the game, so it is noted, not decoded. No
    /// fixture at all is a failure: a replay test that replays nothing proves nothing.
    /// </summary>
    public static ReplayResult Replay<T>(MessageFixtures fixtures, Action<T, MessageFixture>? handler = null) where T : class, new()
    {
        if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));
        var (schema, attr) = SchemaOf(typeof(T));
        var result = new ReplayResult("Replay " + attr.Name + " (" + typeof(T).Name + ", reads v" + attr.Version + ")");
        foreach (var f in fixtures.For(attr.Name))
        {
            if (f.MinReaderVersion > attr.Version)
            {
                result.Note(f + ": dropped by this reader (needs reader " + f.MinReaderVersion + ", " + typeof(T).Name + " reads " + attr.Version + ")");
                continue;
            }

            if (ReplayOne(schema, typeof(T), f, result, handler == null ? null : o => handler((T)o, f), strict: false)) result.Saw(f.ModVersion);
        }

        if (result.Checked == 0) result.Fail("no fixture of '" + attr.Name + "' was replayed; record some with messages_record or MessageFixtures.SaveRelease");
        return result;
    }

    /// <summary>
    /// Replay the fixtures of a hand-written message (<c>ctx.Messages.On(name, (from, MessageReader r, info) => …)</c>)
    /// through <paramref name="handler"/>. <paramref name="readerVersion"/> is the handler's reader version: a fixture
    /// whose minimum reader version is above it is dropped, as in the game.
    /// </summary>
    public static ReplayResult Replay(string message, MessageFixtures fixtures, Action<MessageReader, MessageFixture> handler, int readerVersion = 1)
    {
        if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var result = new ReplayResult("Replay " + message + " (hand-written, reads v" + readerVersion + ")");
        foreach (var f in fixtures.For(message))
        {
            if (f.MinReaderVersion > readerVersion)
            {
                result.Note(f + ": dropped by this reader (needs reader " + f.MinReaderVersion + ", this handler reads " + readerVersion + ")");
                continue;
            }

            result.Checked++;
            try
            {
                handler(new MessageReader(f.Payload), f);
                handler(new MessageReader(WithUnknownFields(f.Payload)), f);
                result.Saw(f.ModVersion);
            }
            catch (Exception e) { result.Fail(f + ": " + (e is FormatException ? "could not be decoded: " : "the handler threw: ") + e.Message); }
        }

        if (result.Checked == 0) result.Fail("no fixture of '" + message + "' was replayed; record some with messages_record");
        return result;
    }

    /// <summary>
    /// Decode every fixture with the type that reads its message name (<paramref name="messageTypes"/>, e.g.
    /// <see cref="MessageTypes.In"/>), with the checks of <see cref="Replay{T}"/>. A fixture no type reads is a failure:
    /// the game would drop that message here.
    /// </summary>
    public static ReplayResult ReplayAll(MessageFixtures fixtures, IEnumerable<Type> messageTypes)
    {
        if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));
        var byName = ByName(messageTypes);
        var result = new ReplayResult("Replay all (" + byName.Count + " message type(s))");
        foreach (var f in fixtures.All)
        {
            if (!byName.TryGetValue(f.Message, out var type))
            {
                result.Fail(f + ": no [ModMessage] type here reads '" + f.Message + "'; the game drops it");
                continue;
            }

            var (schema, attr) = SchemaOf(type);
            if (f.MinReaderVersion > attr.Version)
            {
                result.Note(f + ": dropped by this reader (needs reader " + f.MinReaderVersion + ", " + type.Name + " reads " + attr.Version + ")");
                continue;
            }

            if (ReplayOne(schema, type, f, result, null, strict: false)) result.Saw(f.ModVersion);
        }

        if (result.Checked == 0) result.Fail("no fixture was replayed");
        return result;
    }

    /// <summary>
    /// Check a declared <c>Peers</c> range (<c>[NetworkCompatibility(..., Peers = "…")]</c>) of the build at
    /// <paramref name="currentVersion"/> against the recorded versions. Every fixture from a version inside the range must
    /// work both ways with today's types: it decodes with no drop and no lost value, no message it sends is unread here,
    /// no field it carries or its recorded schema lists is missing or renumbered (removing a field is a major change),
    /// no field changed type (by the recorded schema, recursively), and each of today's types stays readable by that
    /// version (its <c>MinReaderVersion</c> no higher than the version that peer wrote). Fixtures outside the range are
    /// notes.
    /// <para>The range is backed only by fixtures of <em>other</em> versions inside it (<see cref="ReplayResult.OtherVersions"/>):
    /// when only the build's own version has fixtures, the result fails as not proven, unless
    /// <paramref name="allowSelfOnly"/> is set (a mod's first release, which has no older version yet; it is then a note).
    /// A range with no recorded version inside it at all fails.</para>
    /// </summary>
    public static ReplayResult CheckRange(string peers, string currentVersion, MessageFixtures fixtures, IEnumerable<Type> messageTypes,
        bool allowSelfOnly = false)
    {
        if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));
        var result = new ReplayResult("Range " + (peers ?? "").Trim() + " of " + currentVersion);
        if (!CompatRange.TryParse(peers, out var range, out var error))
        {
            result.Fail("'" + peers + "' is not a valid compatibility range: " + error);
            return result;
        }

        var types = (messageTypes ?? Array.Empty<Type>()).ToList();
        var byName = ByName(types);
        int inside = 0;
        var versionsInside = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesByVersion = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixtures.All)
        {
            if (!range!.Includes(f.ModVersion))
            {
                result.Note(f + ": " + f.ModVersion + " is outside " + range.Text + "; skipped");
                continue;
            }

            inside++;
            versionsInside.Add(f.ModVersion);
            if (!namesByVersion.TryGetValue(f.ModVersion, out var names)) namesByVersion[f.ModVersion] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(f.Message);
            if (!byName.TryGetValue(f.Message, out var type))
            {
                result.Fail(f + ": a peer at " + f.ModVersion + " sends '" + f.Message + "', which no message type here reads; the range claims it works");
                continue;
            }

            var (schema, attr) = SchemaOf(type);
            if (f.MinReaderVersion > attr.Version)
            {
                result.Fail(f + ": a peer at " + f.ModVersion + " sends v" + f.Version + " needing reader " + f.MinReaderVersion
                            + "; " + type.Name + " reads v" + attr.Version + " and drops it");
                continue;
            }

            bool ok = true;
            if (attr.MinReaderVersion > f.Version)
            {
                result.Fail(f + ": " + type.Name + " needs reader " + attr.MinReaderVersion + ", but a peer at " + f.ModVersion
                            + " wrote (and reads) v" + f.Version + " and drops what this build sends");
                ok = false;
            }

            if (ReplayOne(schema, type, f, result, null, strict: true) && ok)
            {
                result.Saw(f.ModVersion);
                if (!SameVersion(f.ModVersion, currentVersion)) result.SawOther(f.ModVersion);
            }
        }

        // A breaking type whose name an older version inside the range never recorded: whether that peer reads it cannot be told.
        foreach (var t in types)
        {
            var attr = SchemaOf(t).Attr;
            if (attr.MinReaderVersion <= 1) continue;
            foreach (string v in versionsInside)
                if (!SameVersion(v, currentVersion) && !(namesByVersion.TryGetValue(v, out var n) && n.Contains(attr.Name)))
                    result.Note(t.Name + " needs reader " + attr.MinReaderVersion + "; no fixture of '" + attr.Name + "' from " + v
                                + " shows whether that version reads it");
        }

        if (inside == 0)
            result.Fail("no recorded fixture comes from a version inside " + range!.Text + "; record one (messages_record or MessageFixtures.SaveRelease) before declaring it");
        else if (!versionsInside.Any(v => !SameVersion(v, currentVersion)))
        {
            string line = "not proven: inside " + range!.Text + " only " + currentVersion + "'s own fixtures were recorded, which say nothing about other "
                          + "versions; keep each release's fixtures and check the next release against them";
            if (allowSelfOnly) result.Note(line + " (allowed: a first release)");
            else result.Fail(line);
        }

        return result;
    }

    private static bool SameVersion(string a, string b) =>
        (CompatVersion.Compare(a, b) ?? (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 0 : 1)) == 0;

    private static Dictionary<string, Type> ByName(IEnumerable<Type> types)
    {
        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var t in types ?? Array.Empty<Type>())
        {
            string name = SchemaOf(t).Attr.Name;
            if (byName.TryGetValue(name, out var other) && other != t)
                throw new MessageCompatException("Both " + other.FullName + " and " + t.FullName + " read '" + name + "'; pass one type per message name.");
            byName[name] = t;
        }

        return byName;
    }

    /// <summary>
    /// Decode, check the fields and schema, the unknown-field read, run the handler. True when the fixture passed.
    /// <paramref name="strict"/> (a version inside a declared range): a field the fixture's version had that this type
    /// lacks fails instead of being noted, when the fixture is not newer than this type.
    /// </summary>
    private static bool ReplayOne(MessageSchema schema, Type type, MessageFixture f, ReplayResult result, Action<object>? handler, bool strict)
    {
        // The schema is shared and its unknown-enum hook is per replay: one replay at a time per type.
        lock (schema) return ReplayLocked(schema, type, f, result, handler, strict);
    }

    private static bool ReplayLocked(MessageSchema schema, Type type, MessageFixture f, ReplayResult result, Action<object>? handler, bool strict)
    {
        result.Checked++;
        var enumNotes = new List<string>();
        schema.UnknownEnum = what => enumNotes.Add(what);
        try
        {
            object decoded;
            try { decoded = schema.Decode(f.Payload); }
            catch (FormatException e)
            {
                result.Fail(f + ": could not be decoded as " + type.Name + ": " + e.Message);
                return false;
            }

            var attr = SchemaOf(type).Attr;
            // A field the fixture's version carried is "removed" only when that version is not newer than this type; a
            // newer version's extra fields are what readers skip.
            bool olderOrSame = f.Version <= attr.Version;
            bool missingFails = strict && olderOrSame;

            byte[] again = schema.Encode(decoded);
            bool ok = CheckFields(schema, type, f, again, result, missingFails);
            if (f.Schema != null) ok &= CheckSchema(f, type.Name, f.Schema, schema.Shape(), "", result, missingFails);
            else result.Note(f + ": no field schema recorded; a retyped field with the same wire type cannot be detected for it");

            object withUnknown;
            try { withUnknown = schema.Decode(WithUnknownFields(f.Payload)); }
            catch (FormatException e)
            {
                result.Fail(f + ": could not be decoded with unknown fields added: " + e.Message);
                return false;
            }

            if (!schema.Encode(withUnknown).SequenceEqual(again))
            {
                result.Fail(f + ": " + type.Name + " read differently once unknown fields were added");
                ok = false;
            }

            foreach (string e in enumNotes.Distinct()) result.Note(f + ": unknown enum value " + e + " read as the field's default");

            if (handler != null)
            {
                try { handler(decoded); }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    result.Fail(f + ": the handler threw " + inner.GetType().Name + ": " + inner.Message);
                    ok = false;
                }
            }

            return ok;
        }
        finally { schema.UnknownEnum = null; }
    }

    /// <summary>
    /// The fixture's recorded schema against today's, recursively: a field number whose type changed fails; a field the
    /// fixture's version had that today's type lacks fails when <paramref name="missingFails"/>, else is noted.
    /// </summary>
    private static bool CheckSchema(MessageFixture f, string typeName, IReadOnlyList<FieldShape> old, IReadOnlyList<FieldShape> now, string path,
        ReplayResult result, bool missingFails)
    {
        bool ok = true;
        var byNumber = new Dictionary<int, FieldShape>();
        foreach (var n in now) byNumber[n.Number] = n;
        foreach (var o in old)
        {
            string where = path + (path.Length > 0 ? "." : "") + o.Number.ToString(CultureInfo.InvariantCulture);
            if (!byNumber.TryGetValue(o.Number, out var n))
            {
                // A schema-only miss: the payload may not have carried it (a null string or list), so it was not seen above.
                string moved = now.FirstOrDefault(x => string.Equals(x.Name, o.Name, StringComparison.Ordinal)) is { } renamed
                    ? " (" + o.Name + " is field " + renamed.Number + " now: renumbered)"
                    : "";
                string line = f + ": field " + where + " (" + o.Name + ", " + o.Describe() + ") of v" + f.Version + " is missing from " + typeName + moved
                              + "; removing or renumbering a field is a major change";
                if (missingFails)
                {
                    result.Fail(line);
                    ok = false;
                }
                else result.Note(line);
                continue;
            }

            if (!string.Equals(o.Describe(), n.Describe(), StringComparison.Ordinal))
            {
                result.Fail(f + ": field " + where + " (" + o.Name + ") was " + o.Describe() + " in v" + f.Version + " and is " + n.Describe() + " in "
                            + typeName + "; a field number is never retyped: add a new number");
                ok = false;
                continue;
            }

            if (o.Fields != null && n.Fields != null) ok &= CheckSchema(f, typeName, o.Fields, n.Fields, where, result, missingFails);
        }

        return ok;
    }

    /// <summary>Every field the fixture carries that <paramref name="type"/> knows must come back with the same wire type and value.</summary>
    private static bool CheckFields(MessageSchema schema, Type type, MessageFixture f, byte[] reencoded, ReplayResult result, bool missingFails)
    {
        var old = new Dictionary<int, List<RawField>>();
        var now = new Dictionary<int, List<RawField>>();
        MessageReader.Index(f.Payload, old);
        MessageReader.Index(reencoded, now);
        var oldReader = new MessageReader(f.Payload);
        var nowReader = new MessageReader(reencoded);
        bool ok = true;
        var unknown = new List<int>();
        var fields = schema.Fields().ToDictionary(x => x.Number);
        var nested = schema.Shape().ToDictionary(x => x.Number);
        foreach (var kv in old)
        {
            if (!fields.TryGetValue(kv.Key, out var field))
            {
                unknown.Add(kv.Key);
                continue;
            }

            var sent = kv.Value.Where(r => r.WireType == field.WireType).ToList();
            if (sent.Count < kv.Value.Count)
            {
                result.Fail(f + ": field " + kv.Key + " (" + type.Name + "." + field.Name + ") arrives as " + WireName(kv.Value[0].WireType) + " but is read as "
                            + WireName(field.WireType) + "; its value is lost. A field number is never retyped: add a new number");
                ok = false;
                continue;
            }

            if (!field.IsList) sent = new List<RawField> { sent[sent.Count - 1] };
            now.TryGetValue(kv.Key, out var back);
            back ??= new List<RawField>();
            if (field.Kind == "enum")
            {
                // An undefined enum value reads as the default by contract (noted by the decode); compare the defined ones.
                sent = sent.Where(r => schema.EnumDefines(kv.Key, r.Value)).ToList();
                if (!field.IsList && sent.Count == 0) continue;
            }

            if (field.Kind == "nested")
            {
                // A nested message keeps only the fields its type knows; compare it whole only when the fixture's own schema
                // says today's nested type knows every field it had (CheckSchema checks the rest).
                if (f.Schema == null || !nested.TryGetValue(kv.Key, out var shape) || !Covers(f.Schema, kv.Key, shape)) continue;
            }

            var oldShape = f.Schema?.FirstOrDefault(x => x.Number == kv.Key);
            bool same = sent.Count == back.Count;
            for (int i = 0; same && i < sent.Count; i++)
            {
                if (field.Kind == "nested")
                    same = SameKnownFields(oldReader.Slice(sent[i]), nowReader.Slice(back[i]), oldShape?.Fields);
                else
                    same = sent[i].WireType == WireFormat.LengthDelimited
                        ? oldReader.Slice(sent[i]).SequenceEqual(nowReader.Slice(back[i]))
                        : sent[i].Value == back[i].Value;
            }

            if (!same)
            {
                result.Fail(f + ": field " + kv.Key + " (" + type.Name + "." + field.Name + ") did not survive the round trip; was its type narrowed or changed?");
                ok = false;
            }
        }

        if (unknown.Count > 0)
        {
            unknown.Sort();
            string line = f + ": field(s) " + string.Join(", ", unknown) + " unknown to " + type.Name;
            if (missingFails)
            {
                result.Fail(line + ": a version inside the range sends them and this build drops them; removing or renumbering a field is a major change");
                ok = false;
            }
            else result.Note(line + ", skipped");
        }

        return ok;
    }

    /// <summary>
    /// A nested message's values: every field the old bytes carry comes back with the same values in the new bytes
    /// (fields the new type added are ignored; nested messages compared the same way, by the old schema).
    /// </summary>
    private static bool SameKnownFields(byte[] oldBytes, byte[] newBytes, IReadOnlyList<FieldShape>? oldShape)
    {
        var old = new Dictionary<int, List<RawField>>();
        var now = new Dictionary<int, List<RawField>>();
        try
        {
            MessageReader.Index(oldBytes, old);
            MessageReader.Index(newBytes, now);
        }
        catch (FormatException) { return false; }

        var oldReader = new MessageReader(oldBytes);
        var nowReader = new MessageReader(newBytes);
        foreach (var kv in old)
        {
            var shape = oldShape?.FirstOrDefault(x => x.Number == kv.Key);
            now.TryGetValue(kv.Key, out var back);
            back ??= new List<RawField>();
            var sent = shape != null && !shape.List ? new List<RawField> { kv.Value[kv.Value.Count - 1] } : kv.Value;
            if (shape != null && !shape.List && back.Count > 0) back = new List<RawField> { back[back.Count - 1] };
            if (sent.Count != back.Count) return false;
            for (int i = 0; i < sent.Count; i++)
            {
                if (sent[i].WireType != back[i].WireType) return false;
                bool same = sent[i].WireType != WireFormat.LengthDelimited
                    ? sent[i].Value == back[i].Value
                    : shape?.Fields != null
                        ? SameKnownFields(oldReader.Slice(sent[i]), nowReader.Slice(back[i]), shape.Fields)
                        : oldReader.Slice(sent[i]).SequenceEqual(nowReader.Slice(back[i]));
                if (!same) return false;
            }
        }

        return true;
    }

    /// <summary>True when the fixture's nested schema at <paramref name="number"/> has no field today's <paramref name="shape"/> lacks (recursively).</summary>
    private static bool Covers(IReadOnlyList<FieldShape> fixtureSchema, int number, FieldShape shape)
    {
        var old = fixtureSchema.FirstOrDefault(x => x.Number == number);
        return old?.Fields != null && shape.Fields != null && Contains(old.Fields, shape.Fields);
    }

    private static bool Contains(IReadOnlyList<FieldShape> old, IReadOnlyList<FieldShape> now)
    {
        foreach (var o in old)
        {
            var n = now.FirstOrDefault(x => x.Number == o.Number);
            if (n == null || !string.Equals(n.Describe(), o.Describe(), StringComparison.Ordinal)) return false;
            if (o.Fields != null && (n.Fields == null || !Contains(o.Fields, n.Fields))) return false;
        }

        return true;
    }

    private static string WireName(int wire) => wire switch
    {
        WireFormat.Varint => "a varint",
        WireFormat.Fixed64 => "64-bit",
        WireFormat.Fixed32 => "32-bit",
        WireFormat.LengthDelimited => "length-delimited",
        _ => "wire type " + wire,
    };
}
