using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;

namespace Progenitor.Ui;

/// <summary>
/// A small declarative visibility or enable rule for a settings row (<c>ShowWhen</c> / <c>EnableWhen</c> on the
/// settings page builder). Unlike a <c>showIf</c> delegate it is data: the server sends it with the setting's
/// definition, so a client without the mod's DLL evaluates it against the values it received.
///
/// <para>Supported forms: a comparison of another setting of the same mod with a constant (<see cref="Is(ConfigEntryBase, object)"/>,
/// <see cref="IsNot(ConfigEntryBase, object)"/>, <see cref="Above(ConfigEntryBase, double)"/>, <see cref="AtLeast(ConfigEntryBase, double)"/>,
/// <see cref="Below(ConfigEntryBase, double)"/>, <see cref="AtMost(ConfigEntryBase, double)"/>), a server fact
/// (<see cref="ServerHasMod"/>), and <see cref="All"/> / <see cref="Any"/> (or <c>&amp;</c> / <c>|</c>) of those. At most
/// <see cref="MaxNodes"/> parts, nested at most <see cref="MaxDepth"/> deep.</para>
///
/// <para>Equality compares numbers numerically, <c>true</c>/<c>false</c> without case, anything else as text without
/// case. The ordering comparisons hold only when both sides are numbers. A comparison whose setting has no value (not
/// published, not received) is false.</para>
/// </summary>
public sealed class SettingCondition
{
    /// <summary>The most parts (comparisons, facts, All/Any) one condition holds.</summary>
    public const int MaxNodes = 16;

    /// <summary>How deep All/Any may nest.</summary>
    public const int MaxDepth = 4;

    /// <summary>The longest constant, in UTF-8 bytes.</summary>
    public const int MaxConstantBytes = 256;

    /// <summary>The longest encoded condition, in bytes.</summary>
    internal const int MaxEncodedBytes = 1024;

    internal enum Op : byte
    {
        Equal = 1, NotEqual = 2, Less = 3, LessOrEqual = 4, Greater = 5, GreaterOrEqual = 6, All = 7, Any = 8, ServerMod = 9,
    }

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SettingCondition[] _parts;

    private SettingCondition(Op op, string section, string key, string value, SettingCondition[]? parts, ConfigEntryBase? entry)
    {
        Kind = op;
        Section = section ?? "";
        Key = key ?? "";
        Value = value ?? "";
        _parts = parts ?? Array.Empty<SettingCondition>();
        Entry = entry;
    }

    internal Op Kind { get; }

    /// <summary>The compared setting's section (comparisons), or empty.</summary>
    internal string Section { get; }

    /// <summary>The compared setting's key (comparisons), or the mod id (<see cref="ServerHasMod"/>).</summary>
    internal string Key { get; }

    /// <summary>The constant, as invariant text.</summary>
    internal string Value { get; }

    internal IReadOnlyList<SettingCondition> Parts => _parts;

    /// <summary>The bound entry the author named (local evaluation only; never sent).</summary>
    internal ConfigEntryBase? Entry { get; private set; }

    /// <summary>
    /// A server fact as the server answered when it sent this condition (<see cref="ServerHasMod"/>), or null for a
    /// condition built locally.
    /// </summary>
    internal bool? Answer { get; private set; }

    // ---- authoring --------------------------------------------------------------------------------------------

    /// <summary>True while <paramref name="setting"/> equals <paramref name="value"/>.</summary>
    public static SettingCondition Is(ConfigEntryBase setting, object value) => Compare(Op.Equal, setting, value);

    /// <summary>True while <paramref name="setting"/> differs from <paramref name="value"/>.</summary>
    public static SettingCondition IsNot(ConfigEntryBase setting, object value) => Compare(Op.NotEqual, setting, value);

    /// <summary>True while the number <paramref name="setting"/> is greater than <paramref name="value"/>.</summary>
    public static SettingCondition Above(ConfigEntryBase setting, double value) => Compare(Op.Greater, setting, value);

    /// <summary>True while the number <paramref name="setting"/> is at least <paramref name="value"/>.</summary>
    public static SettingCondition AtLeast(ConfigEntryBase setting, double value) => Compare(Op.GreaterOrEqual, setting, value);

    /// <summary>True while the number <paramref name="setting"/> is less than <paramref name="value"/>.</summary>
    public static SettingCondition Below(ConfigEntryBase setting, double value) => Compare(Op.Less, setting, value);

    /// <summary>True while the number <paramref name="setting"/> is at most <paramref name="value"/>.</summary>
    public static SettingCondition AtMost(ConfigEntryBase setting, double value) => Compare(Op.LessOrEqual, setting, value);

    /// <summary>
    /// The same comparisons for a setting named by section and key (a <c>PerPlayer</c> setting, or one bound
    /// elsewhere). It must be a published setting of the same mod for a client without the DLL to evaluate it.
    /// </summary>
    public static SettingCondition Is(string section, string key, object value) => Compare(Op.Equal, section, key, value);

    /// <inheritdoc cref="Is(string, string, object)"/>
    public static SettingCondition IsNot(string section, string key, object value) => Compare(Op.NotEqual, section, key, value);

    /// <inheritdoc cref="Is(string, string, object)"/>
    public static SettingCondition Above(string section, string key, double value) => Compare(Op.Greater, section, key, value);

    /// <inheritdoc cref="Is(string, string, object)"/>
    public static SettingCondition AtLeast(string section, string key, double value) => Compare(Op.GreaterOrEqual, section, key, value);

    /// <inheritdoc cref="Is(string, string, object)"/>
    public static SettingCondition Below(string section, string key, double value) => Compare(Op.Less, section, key, value);

    /// <inheritdoc cref="Is(string, string, object)"/>
    public static SettingCondition AtMost(string section, string key, double value) => Compare(Op.LessOrEqual, section, key, value);

    /// <summary>
    /// True while the joined server runs the mod with BepInEx GUID <paramref name="pluginGuid"/>. The server answers it
    /// and sends the answer with the definition (again when the answer changes); without a server it is false.
    /// </summary>
    public static SettingCondition ServerHasMod(string pluginGuid)
    {
        if (string.IsNullOrWhiteSpace(pluginGuid)) throw new ArgumentException("A plugin GUID is required.", nameof(pluginGuid));
        var guid = pluginGuid.Trim();
        CheckText(guid, 128, nameof(pluginGuid));
        return new SettingCondition(Op.ServerMod, "", guid, "", null, null);
    }

    /// <summary>True while every part is true.</summary>
    public static SettingCondition All(params SettingCondition[] parts) => Group(Op.All, parts);

    /// <summary>True while any part is true.</summary>
    public static SettingCondition Any(params SettingCondition[] parts) => Group(Op.Any, parts);

    /// <summary>This and <paramref name="other"/>.</summary>
    public SettingCondition And(SettingCondition other) => All(this, other);

    /// <summary>This or <paramref name="other"/>.</summary>
    public SettingCondition Or(SettingCondition other) => Any(this, other);

    public static SettingCondition operator &(SettingCondition a, SettingCondition b) => All(a, b);

    public static SettingCondition operator |(SettingCondition a, SettingCondition b) => Any(a, b);

    private static SettingCondition Compare(Op op, ConfigEntryBase setting, object value)
    {
        if (setting == null) throw new ArgumentNullException(nameof(setting));
        var c = Compare(op, setting.Definition.Section, setting.Definition.Key, value);
        c.Entry = setting;
        return c;
    }

    private static SettingCondition Compare(Op op, string section, string key, object value)
    {
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key) || section.IndexOf('/') >= 0 || key.IndexOf('/') >= 0)
            throw new ArgumentException("A setting section and key are required, without '/'.");
        CheckText(section, 128, nameof(section));
        CheckText(key, 128, nameof(key));
        string text = Constant(value);
        CheckText(text, MaxConstantBytes, nameof(value));
        if (op is not (Op.Equal or Op.NotEqual) && !TryNumber(text, out _))
            throw new ArgumentException("An ordering comparison needs a number.", nameof(value));
        return new SettingCondition(op, section, key, text, null, null);
    }

    private static SettingCondition Group(Op op, SettingCondition[] parts)
    {
        if (parts == null || parts.Length == 0) throw new ArgumentException("Give at least one condition.", nameof(parts));
        foreach (var p in parts) if (p == null) throw new ArgumentNullException(nameof(parts));
        var c = new SettingCondition(op, "", "", "", (SettingCondition[])parts.Clone(), null);
        c.CheckShape();
        return c;
    }

    private static string Constant(object value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        string s => s,
        Enum e => e.ToString(),
        IFormattable f => f.ToString(value is double or float ? "R" : null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static void CheckText(string text, int bytes, string name)
    {
        if (Utf8.GetByteCount(text) > bytes) throw new ArgumentException("At most " + bytes + " UTF-8 bytes.", name);
    }

    private void CheckShape()
    {
        int nodes = 0;
        Walk(this, 1, ref nodes);
    }

    private static void Walk(SettingCondition c, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes) throw new ArgumentException("A condition holds at most " + MaxNodes + " parts.");
        if (depth > MaxDepth) throw new ArgumentException("Conditions nest at most " + MaxDepth + " deep.");
        foreach (var p in c._parts) Walk(p, depth + 1, ref nodes);
    }

    // ---- evaluation -------------------------------------------------------------------------------------------

    /// <summary>
    /// Evaluate. <paramref name="valueOf"/> returns a setting's current value as invariant text by
    /// <c>section/key</c> (null: no value); <paramref name="serverMod"/> answers <see cref="ServerHasMod"/> when the
    /// condition carries no server answer (null: unknown, read as false).
    /// </summary>
    internal bool Evaluate(Func<SettingCondition, string?> valueOf, Func<string, bool?>? serverMod = null)
    {
        switch (Kind)
        {
            case Op.All:
                foreach (var p in _parts) if (!p.Evaluate(valueOf, serverMod)) return false;
                return true;
            case Op.Any:
                foreach (var p in _parts) if (p.Evaluate(valueOf, serverMod)) return true;
                return false;
            case Op.ServerMod:
                return Answer ?? serverMod?.Invoke(Key) ?? false;
        }

        string? current;
        try { current = valueOf(this); }
        catch { current = null; }
        if (current == null) return false;
        bool numbers = TryNumber(current, out double a) & TryNumber(Value, out double b);
        switch (Kind)
        {
            case Op.Equal: return Same(current, Value, numbers, a, b);
            case Op.NotEqual: return !Same(current, Value, numbers, a, b);
            case Op.Less: return numbers && a < b;
            case Op.LessOrEqual: return numbers && a <= b;
            case Op.Greater: return numbers && a > b;
            case Op.GreaterOrEqual: return numbers && a >= b;
            default: return false;
        }
    }

    private static bool Same(string x, string y, bool numbers, double a, double b) =>
        numbers ? a == b : string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>The <c>section/key</c> references in this condition (comparisons only).</summary>
    internal IEnumerable<(string Section, string Key)> References()
    {
        if (Kind is Op.All or Op.Any)
        {
            foreach (var p in _parts)
                foreach (var r in p.References()) yield return r;
        }
        else if (Kind != Op.ServerMod) yield return (Section, Key);
    }

    /// <summary>The mod ids this condition asks the server about.</summary>
    internal IEnumerable<string> ServerMods()
    {
        if (Kind == Op.ServerMod) yield return Key;
        foreach (var p in _parts)
            foreach (var m in p.ServerMods()) yield return m;
    }

    /// <summary>A copy for the wire, with the server's answers filled in, the local entry dropped.</summary>
    internal SettingCondition Resolved(Func<string, bool> serverMod)
    {
        var parts = new SettingCondition[_parts.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = _parts[i].Resolved(serverMod);
        var c = new SettingCondition(Kind, Section, Key, Value, parts, null);
        if (Kind == Op.ServerMod) c.Answer = serverMod(Key);
        return c;
    }

    // ---- wire -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pre-order encoding: op byte, then for a comparison section, key and constant (u16 length + UTF-8), for a server fact
    /// the mod id and its answer (0, 1, or 2 for unknown), for All/Any a part count byte and the parts.
    /// </summary>
    internal void Write(BinaryWriter w)
    {
        CheckShape();
        long start = w.BaseStream.Position;
        WriteNode(w, this);
        if (w.BaseStream.Position - start > MaxEncodedBytes) throw new ArgumentException("The condition is too large to send.");
    }

    private static void WriteNode(BinaryWriter w, SettingCondition c)
    {
        w.Write((byte)c.Kind);
        switch (c.Kind)
        {
            case Op.All:
            case Op.Any:
                w.Write((byte)c._parts.Length);
                foreach (var p in c._parts) WriteNode(w, p);
                break;
            case Op.ServerMod:
                WriteText(w, c.Key, 128);
                w.Write((byte)(c.Answer == null ? 2 : c.Answer.Value ? 1 : 0));
                break;
            default:
                WriteText(w, c.Section, 128); WriteText(w, c.Key, 128); WriteText(w, c.Value, MaxConstantBytes);
                break;
        }
    }

    internal static SettingCondition Read(BinaryReader r)
    {
        long start = r.BaseStream.Position;
        int nodes = 0;
        var c = ReadNode(r, 1, ref nodes);
        if (r.BaseStream.Position - start > MaxEncodedBytes) throw new InvalidDataException("Condition too large.");
        return c;
    }

    private static SettingCondition ReadNode(BinaryReader r, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes || depth > MaxDepth) throw new InvalidDataException("Condition too large.");
        var op = (Op)r.ReadByte();
        switch (op)
        {
            case Op.All:
            case Op.Any:
            {
                int count = r.ReadByte();
                if (count == 0 || count > MaxNodes) throw new InvalidDataException("Invalid condition group.");
                var parts = new SettingCondition[count];
                for (int i = 0; i < count; i++) parts[i] = ReadNode(r, depth + 1, ref nodes);
                return new SettingCondition(op, "", "", "", parts, null);
            }
            case Op.ServerMod:
            {
                string id = ReadText(r, 128);
                byte answer = r.ReadByte();
                if (id.Trim().Length == 0 || answer > 2) throw new InvalidDataException("Invalid server fact.");
                return new SettingCondition(op, "", id, "", null, null) { Answer = answer == 2 ? null : answer == 1 };
            }
            case Op.Equal: case Op.NotEqual: case Op.Less: case Op.LessOrEqual: case Op.Greater: case Op.GreaterOrEqual:
            {
                string section = ReadText(r, 128), key = ReadText(r, 128), value = ReadText(r, MaxConstantBytes);
                if (section.Trim().Length == 0 || key.Trim().Length == 0 || section.IndexOf('/') >= 0 || key.IndexOf('/') >= 0)
                    throw new InvalidDataException("Invalid condition reference.");
                return new SettingCondition(op, section, key, value, null, null);
            }
            default:
                throw new InvalidDataException("Unknown condition.");
        }
    }

    private static void WriteText(BinaryWriter w, string text, int max)
    {
        var bytes = Utf8.GetBytes(text ?? "");
        if (bytes.Length > max) throw new ArgumentException("Condition text too long.");
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static string ReadText(BinaryReader r, int max)
    {
        int length = r.ReadUInt16();
        if (length > max || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid condition text.");
        return Utf8.GetString(r.ReadBytes(length));
    }

    /// <summary>A readable form for logs, readback and tests: <c>Server/Enabled == true &amp;&amp; server has satis</c>.</summary>
    public override string ToString() => Kind switch
    {
        Op.All => "(" + string.Join(" && ", Array.ConvertAll(_parts, p => p.ToString())) + ")",
        Op.Any => "(" + string.Join(" || ", Array.ConvertAll(_parts, p => p.ToString())) + ")",
        Op.ServerMod => "server has " + Key,
        _ => Section + "/" + Key + " " + Kind switch
        {
            Op.Equal => "==", Op.NotEqual => "!=", Op.Less => "<", Op.LessOrEqual => "<=", Op.Greater => ">", _ => ">=",
        } + " " + Value,
    };
}
