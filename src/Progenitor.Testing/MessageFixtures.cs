using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Testing;

/// <summary>
/// One recorded message: the payload one version of a mod wrote, and what its envelope said. Recorded live by the Dev
/// bridge's <c>messages_record</c> op, or written by a test with <see cref="MessageFixtures.Save{T}"/>.
/// </summary>
public sealed class MessageFixture
{
    internal MessageFixture(ModMessageRecord r, string path)
    {
        Mod = r.Mod;
        ModVersion = r.ModVersion;
        Message = r.Message;
        Version = r.Version;
        MinReaderVersion = r.MinReaderVersion;
        Direction = r.Direction;
        Source = r.Source;
        Payload = r.Payload;
        Schema = r.Schema;
        Path = path;
    }

    /// <summary>The mod's GUID.</summary>
    public string Mod { get; }

    /// <summary>The version of the mod that wrote the payload.</summary>
    public string ModVersion { get; }

    /// <summary>The message name (<c>[ModMessage("name")]</c>).</summary>
    public string Message { get; }

    /// <summary>The message version the sender wrote.</summary>
    public int Version { get; }

    /// <summary>The lowest reader version the sender allowed.</summary>
    public int MinReaderVersion { get; }

    /// <summary><c>to-server</c>, <c>to-client</c> or <c>any</c>.</summary>
    public string Direction { get; }

    /// <summary><c>live</c> (recorded in the game) or <c>test</c> (written by a test).</summary>
    public string Source { get; }

    /// <summary>The encoded message, without the envelope header.</summary>
    public byte[] Payload { get; }

    /// <summary>The writer's field schema from the sidecar, when it was recorded (a typed send or a test); else null.</summary>
    internal IReadOnlyList<FieldShape>? Schema { get; }

    /// <summary>True when the sidecar carries the writer's field schema (needed to see a retype with the same wire type).</summary>
    public bool HasSchema => Schema != null;

    /// <summary>The payload file.</summary>
    public string Path { get; }

    public override string ToString() =>
        Message + " v" + Version + " from " + Mod + " " + ModVersion + " (" + Direction + ", " + System.IO.Path.GetFileName(Path) + ")";
}

/// <summary>
/// A mod's recorded messages, read from its fixture directory: <c>&lt;mod version&gt;/&lt;message&gt;@v&lt;n&gt;/&lt;sha12&gt;.bin</c>
/// with a <c>.json</c> sidecar beside each payload (the layout <c>messages_record</c> writes under
/// <c>BepInEx/progenitor-dev/messages/&lt;mod&gt;/</c>; copy that folder into the test project).
/// </summary>
public sealed class MessageFixtures
{
    private readonly List<MessageFixture> _all;

    private MessageFixtures(List<MessageFixture> all) => _all = all;

    /// <summary>Every fixture, ordered by mod version, then message, version and file.</summary>
    public IReadOnlyList<MessageFixture> All => _all;

    /// <summary>The distinct mod versions recorded, oldest first.</summary>
    public IReadOnlyList<string> ModVersions =>
        _all.Select(f => f.ModVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The fixtures of one message name.</summary>
    public IReadOnlyList<MessageFixture> For(string message) =>
        _all.Where(f => string.Equals(f.Message, message, StringComparison.Ordinal)).ToList();

    /// <summary>The fixtures of <typeparamref name="T"/>'s message name.</summary>
    public IReadOnlyList<MessageFixture> For<T>() where T : class => For(MessageCompat.NameOf(typeof(T)));

    /// <summary>
    /// Read every fixture under <paramref name="directory"/> (recursively). A missing directory is empty. When
    /// <paramref name="mod"/> is given, only that mod's fixtures are kept. Throws <see cref="MessageCompatException"/>
    /// naming each sidecar that cannot be read (malformed, another format, a missing payload or a SHA-256 mismatch), so a
    /// damaged fixture never passes silently.
    /// </summary>
    public static MessageFixtures Load(string directory, string? mod = null)
    {
        var list = new List<MessageFixture>();
        var errors = new List<string>();
        if (Directory.Exists(directory))
        {
            foreach (string sidecar in Directory.EnumerateFiles(directory, "*" + ModMessageFixtureFormat.SidecarExtension, SearchOption.AllDirectories))
            {
                if (!ModMessageFixtureFormat.TryRead(sidecar, out var r, out var error))
                {
                    errors.Add(sidecar + ": " + error);
                    continue;
                }

                if (mod != null && !string.Equals(r!.Mod, mod.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new MessageFixture(r!, System.IO.Path.ChangeExtension(sidecar, ModMessageFixtureFormat.PayloadExtension)));
            }
        }

        if (errors.Count > 0)
            throw new MessageCompatException(errors.Count + " fixture(s) under " + directory + " cannot be read:" + Environment.NewLine
                                             + string.Join(Environment.NewLine, errors));
        list.Sort(Compare);
        return new MessageFixtures(list);
    }

    /// <summary>Fixtures from a list (for tests that build their own).</summary>
    internal static MessageFixtures Of(IEnumerable<MessageFixture> fixtures)
    {
        var list = new List<MessageFixture>(fixtures);
        list.Sort(Compare);
        return new MessageFixtures(list);
    }

    private static int Compare(MessageFixture a, MessageFixture b)
    {
        int c = CompatVersion.Compare(a.ModVersion, b.ModVersion) ?? string.Compare(a.ModVersion, b.ModVersion, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Message, b.Message);
        if (c != 0) return c;
        c = a.Version.CompareTo(b.Version);
        return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
    }

    /// <summary>
    /// Record a release's messages: every message in <paramref name="messages"/> as <paramref name="mod"/>
    /// <paramref name="modVersion"/> sends it today, with its field schema, under <paramref name="directory"/>. Refused
    /// (<see cref="MessageCompatException"/>) when <c>&lt;directory&gt;/&lt;modVersion&gt;</c> already exists: a released
    /// version's fixtures are the frozen baseline later builds are checked against, so they are never rewritten. A changed
    /// message is a new version. <paramref name="direction"/> labels each message (<c>to-server</c>, <c>to-client</c>);
    /// null labels all <c>any</c>.
    /// </summary>
    public static IReadOnlyList<MessageFixture> SaveRelease(string directory, string mod, string modVersion, IEnumerable<object> messages,
        Func<object, string>? direction = null) =>
        SaveReleaseWith(directory, mod, modVersion, messages, direction, ModMessageEnvelope.MaxPayloadBytes);

    /// <summary><see cref="SaveRelease"/> with a payload cap: one envelope for <c>ctx.Messages</c>, 1 MiB for exchange types (<see cref="ExchangeCompat.SaveRelease"/>).</summary>
    internal static IReadOnlyList<MessageFixture> SaveReleaseWith(string directory, string mod, string modVersion, IEnumerable<object> messages,
        Func<object, string>? direction, int maxBytes)
    {
        if (messages == null) throw new ArgumentNullException(nameof(messages));
        string folder = System.IO.Path.Combine(directory, ModMessageFixtureFormat.Segment(modVersion));
        if (Directory.Exists(folder))
            throw new MessageCompatException("The fixtures of " + modVersion + " already exist at " + folder + " and are never rewritten: a released "
                                             + "version's fixtures are the baseline later builds are checked against. A changed message is a new "
                                             + "version; delete the folder by hand only if " + modVersion + " was never released.");
        var list = messages.ToList();
        foreach (var m in list) MessageCompat.SchemaOf(m.GetType()); // every type valid before anything is written
        var saved = new List<MessageFixture>(list.Count);
        foreach (var m in list) saved.Add(SaveWith(directory, mod, modVersion, m, direction?.Invoke(m) ?? ModMessageFixtureFormat.AnyDirection, maxBytes));
        return saved;
    }

    /// <summary>
    /// Record <paramref name="message"/> as <paramref name="mod"/> <paramref name="modVersion"/> sends it today, with its
    /// field schema, under <paramref name="directory"/> (the same layout and dedupe as a live recording; the sidecar says
    /// <c>source: test</c>). It only adds: an existing fixture is never rewritten or deleted. To record a whole release
    /// once, use <see cref="SaveRelease"/>. Returns the fixture. Throws <see cref="MessageCompatException"/> when the
    /// message's type is not a valid message type.
    /// </summary>
    public static MessageFixture Save<T>(string directory, string mod, string modVersion, T message, string direction = "any") where T : class =>
        SaveWith(directory, mod, modVersion, message, direction, ModMessageEnvelope.MaxPayloadBytes);

    /// <summary><see cref="Save{T}"/> with a payload cap (see <see cref="SaveReleaseWith"/>).</summary>
    internal static MessageFixture SaveWith(string directory, string mod, string modVersion, object message, string direction, int maxBytes)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        var (schema, attr) = MessageCompat.SchemaOf(message.GetType());
        var r = new ModMessageRecord
        {
            Mod = mod,
            ModVersion = modVersion,
            Message = attr.Name,
            Version = attr.Version,
            MinReaderVersion = attr.MinReaderVersion,
            Direction = string.IsNullOrWhiteSpace(direction) ? ModMessageFixtureFormat.AnyDirection : direction.Trim(),
            Source = "test",
            // One envelope for a ctx.Messages type; the exchange's 1 MiB through ExchangeCompat.Save / SaveRelease.
            Payload = MessageCompat.EncodeWith(schema, message, maxBytes),
            Schema = schema.Shape(),
        };
        try
        {
            string path = ModMessageFixtureFormat.Write(directory, r, DateTime.UtcNow, out _);
            return new MessageFixture(r, path);
        }
        catch (ArgumentException e) { throw new MessageCompatException("Cannot save " + message.GetType().Name + ": " + e.Message); }
    }
}
