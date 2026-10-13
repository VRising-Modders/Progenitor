using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Progenitor.LayoutGen;

/// <summary>
/// A read-only index over the game's metadata (the C#-shaped type listing), good enough to answer the four
/// questions <c>layout.txt</c> asks: a field's byte offset, a type's native size, an enum
/// member's value, and an arbitrary line pattern.
///
/// <para><b>What the dump actually states.</b> A type block looks like</para>
/// <code>
/// public struct LineRendererPositionElement : IBufferElementData // TypeDefIndex: 5468
/// {
///     // Fields
///     public int LineRendererIndex; // 0x0
///     public int PositionIndex; // 0x4
///     public float3 PositionValue; // 0x8
/// }
/// </code>
/// <para>so <b>offsets are stated and sizes are not</b>. A size is therefore <em>derived</em>
/// here, from the stated offsets plus the widths of the field types (resolved recursively, with
/// only the C# primitives hard-coded) — the same arithmetic the migrated constants already spell
/// out by hand, and the reason CLAUDE.md's EV17 rule says the dump, not <c>sizeof</c>, decides a
/// stride. When any part of that arithmetic is unavailable the resolver <b>fails loudly</b>; it
/// never guesses a width, because a guessed stride is the wrong-memory write this tool exists to
/// prevent.</para>
///
/// <para><b>The open-generic trap.</b> <c>NativeArray&lt;T&gt;</c> is dumped with every field at
/// <c>// 0x0</c>: an open generic has no laid-out instance. Those zeros are placeholders, not
/// offsets, so a multi-field type whose fields are all at 0 is refused rather than resolved —
/// otherwise this tool would "discover" that <c>m_Length</c> lives at 0 and report the real 8 as
/// a drift.</para>
/// </summary>
public sealed class DumpIndex
{
    private readonly string[] _lines;
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.Ordinal);

    /// <summary>Simple names of every declared type, mapped to the declaration line index.</summary>
    private static readonly Regex TypeDeclaration = new(
        @"^(?<mods>[\w\[\]\s,<>\.]*?)\b(?<kind>struct|class|enum|interface)\s+(?<name>[A-Za-z_][\w\.]*)(?<generic><[^>]*>)?\s*(?::[^/]*)?//\s*TypeDefIndex:",
        RegexOptions.Compiled);

    /// <summary>An instance field line: <c>public float3 PositionValue; // 0x8</c>.</summary>
    private static readonly Regex FieldLine = new(
        @"^\s+(?<mods>(?:public|private|internal|protected|readonly|static|volatile|unsafe|const|fixed|new|\s)*)"
        + @"(?<type>[^\s].*?)\s+(?<name>[A-Za-z_@][\w]*)\s*;\s*//\s*0x(?<offset>[0-9A-Fa-f]+)\s*$",
        RegexOptions.Compiled);

    /// <summary>An enum member: <c>public const AiTargettingMode Entity = 3;</c>.</summary>
    private static readonly Regex EnumMember = new(
        @"^\s+public\s+const\s+(?<enum>[\w\.]+)\s+(?<name>[A-Za-z_][\w]*)\s*=\s*(?<value>-?\d+)\s*;",
        RegexOptions.Compiled);

    private DumpIndex(string[] lines)
    {
        _lines = lines;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t') continue;
            if (line.IndexOf("TypeDefIndex:", StringComparison.Ordinal) < 0) continue;
            var m = TypeDeclaration.Match(line);
            if (!m.Success) continue;
            var name = m.Groups["name"].Value;
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<int>();
            list.Add(i);
            // Also index the leaf of a nested spelling (Outer.Nested), so a query may use either.
            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                var leaf = name.Substring(dot + 1);
                if (!_byName.TryGetValue(leaf, out var leaves)) _byName[leaf] = leaves = new List<int>();
                leaves.Add(i);
            }
        }
    }

    public static DumpIndex Load(string path) => new(System.IO.File.ReadAllLines(path));

    /// <summary>For the tests: build an index straight from dump text.</summary>
    public static DumpIndex FromText(string text) =>
        new(text.Replace("\r\n", "\n").Split('\n'));

    /// <summary>How many types the index found. A sanity number for the console output.</summary>
    public int TypeCount => _byName.Count;

    // ---- resolution ------------------------------------------------------------------------

    /// <summary>
    /// Resolve one declaration. Returns false with a reason rather than a guess; the caller
    /// reports the reason and leaves the constant where it is.
    /// </summary>
    public bool TryResolve(LayoutDeclaration declaration, out long value, out string error)
    {
        value = 0;
        error = "";
        switch (declaration.Kind)
        {
            case LayoutQueryKind.Offset:
            {
                if (!Split(declaration.Query, out var type, out var member, out error)) return false;
                if (!TryFindType(type, out int at, out error)) return false;
                foreach (var f in Fields(at))
                    if (string.Equals(f.Name, member, StringComparison.Ordinal)) { value = f.Offset; return true; }
                error = type + " has no instance field '" + member + "' in the dump";
                return false;
            }
            case LayoutQueryKind.Size:
                return TrySizeOf(declaration.Query, 0, out value, out error);
            case LayoutQueryKind.EnumValue:
            {
                if (!Split(declaration.Query, out var type, out var member, out error)) return false;
                if (!TryFindType(type, out int at, out error)) return false;
                foreach (var (name, v) in EnumMembers(at))
                    if (string.Equals(name, member, StringComparison.Ordinal)) { value = v; return true; }
                error = type + " has no enum member '" + member + "' in the dump";
                return false;
            }
            case LayoutQueryKind.Match:
            {
                int from = 0, to = _lines.Length;
                if (declaration.Anchor.Length > 0)
                {
                    if (!TryFindType(declaration.Anchor, out int at, out error)) return false;
                    from = at;
                    to = EndOfBlock(at);
                }
                return TryMatch(declaration.Query, from, to, out value, out error);
            }
            default:
                error = "unknown query kind";
                return false;
        }
    }

    /// <summary>One past the last line of the type block that starts at the declaration line.</summary>
    private int EndOfBlock(int declarationLine)
    {
        for (int i = declarationLine + 1; i < _lines.Length; i++)
            if (_lines[i].Length > 0 && _lines[i][0] == '}') return i;
        return _lines.Length;
    }

    private bool TryMatch(string pattern, int from, int to, out long value, out string error)
    {
        value = 0;
        error = "";
        Regex regex;
        try { regex = new Regex(pattern); }
        catch (ArgumentException e) { error = "bad regex: " + e.Message; return false; }
        if (regex.GetGroupNumbers().Length != 2)
        {
            error = "a match regex needs exactly one capture group";
            return false;
        }
        long? found = null;
        int hits = 0;
        for (int i = from; i < to; i++)
        {
            var m = regex.Match(_lines[i]);
            if (!m.Success) continue;
            hits++;
            if (!LayoutDeclaration.ParseValue(m.Groups[1].Value.Trim(), out var v))
            {
                error = "the capture group matched '" + m.Groups[1].Value + "', which is not a number";
                return false;
            }
            if (found is null) found = v;
            else if (found.Value != v)
            {
                error = "the pattern matches " + hits + " lines with different values ("
                    + found.Value + " and " + v + ")";
                return false;
            }
        }
        if (found is null) { error = "the pattern matches no line in the searched range"; return false; }
        value = found.Value;
        return true;
    }

    private static bool Split(string query, out string type, out string member, out string error)
    {
        error = "";
        int dot = query.LastIndexOf('.');
        if (dot <= 0 || dot == query.Length - 1)
        {
            type = member = "";
            error = "'" + query + "' must be spelled Type.Member";
            return false;
        }
        type = query.Substring(0, dot);
        member = query.Substring(dot + 1);
        return true;
    }

    private bool TryFindType(string name, out int line, out string error)
    {
        line = -1;
        error = "";
        if (!_byName.TryGetValue(name, out var hits))
        {
            // A namespace-qualified query: the dump declares the simple name under a
            // `// Namespace: X` comment line, so match on the leaf and confirm the namespace.
            int dot = name.LastIndexOf('.');
            if (dot > 0 && _byName.TryGetValue(name.Substring(dot + 1), out var leaves))
            {
                var wanted = name.Substring(0, dot);
                var narrowed = new List<int>();
                foreach (var at in leaves)
                    if (string.Equals(NamespaceOf(at), wanted, StringComparison.Ordinal)) narrowed.Add(at);
                if (narrowed.Count == 1) { line = narrowed[0]; return true; }
                if (narrowed.Count > 1)
                {
                    error = "'" + name + "' is declared " + narrowed.Count + " times in the dump";
                    return false;
                }
            }
            error = "the dump declares no type named '" + name + "'";
            return false;
        }
        if (hits.Count > 1)
        {
            error = "'" + name + "' is declared " + hits.Count
                + " times in the dump; qualify it with its namespace";
            return false;
        }
        line = hits[0];
        return true;
    }

    private string NamespaceOf(int declarationLine)
    {
        for (int i = declarationLine - 1; i >= 0 && i > declarationLine - 8; i--)
        {
            var line = _lines[i];
            if (line.StartsWith("// Namespace:", StringComparison.Ordinal))
                return line.Substring("// Namespace:".Length).Trim();
        }
        return "";
    }

    private readonly struct Field
    {
        public Field(string type, string name, int offset) { Type = type; Name = name; Offset = offset; }
        public string Type { get; }
        public string Name { get; }
        public int Offset { get; }
    }

    /// <summary>Instance fields of the type declared at <paramref name="declarationLine"/>, in order.</summary>
    private List<Field> Fields(int declarationLine)
    {
        var fields = new List<Field>();
        for (int i = declarationLine + 1; i < _lines.Length; i++)
        {
            var line = _lines[i];
            if (line.Length > 0 && line[0] == '}') break;
            var trimmed = line.TrimStart();
            // Everything after "// Properties" / "// Methods" is not layout.
            if (trimmed.StartsWith("// Properties", StringComparison.Ordinal)
                || trimmed.StartsWith("// Methods", StringComparison.Ordinal)
                || trimmed.StartsWith("// Nested types", StringComparison.Ordinal)) break;
            var m = FieldLine.Match(line);
            if (!m.Success) continue;
            var mods = m.Groups["mods"].Value;
            // A `static readonly X zero; // 0x0` is not part of the instance layout, and neither
            // is a `const`. Both carry an offset comment in the dump, so both must be dropped.
            if (mods.Contains("static") || mods.Contains("const")) continue;
            var offset = int.Parse(m.Groups["offset"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            fields.Add(new Field(m.Groups["type"].Value.Trim(), m.Groups["name"].Value, offset));
        }
        return fields;
    }

    private List<(string Name, long Value)> EnumMembers(int declarationLine)
    {
        var members = new List<(string, long)>();
        for (int i = declarationLine + 1; i < _lines.Length; i++)
        {
            var line = _lines[i];
            if (line.Length > 0 && line[0] == '}') break;
            var m = EnumMember.Match(line);
            if (m.Success) members.Add((m.Groups["name"].Value, long.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture)));
        }
        return members;
    }

    /// <summary>
    /// The C# primitives, which are the only widths this tool is allowed to know without asking
    /// the dump. Everything else is resolved from the dump's own field lines.
    /// </summary>
    private static readonly Dictionary<string, (int Size, int Align)> Primitives = new(StringComparer.Ordinal)
    {
        ["bool"] = (1, 1),
        ["byte"] = (1, 1),
        ["sbyte"] = (1, 1),
        ["char"] = (2, 2),
        ["short"] = (2, 2),
        ["ushort"] = (2, 2),
        ["int"] = (4, 4),
        ["uint"] = (4, 4),
        ["float"] = (4, 4),
        ["long"] = (8, 8),
        ["ulong"] = (8, 8),
        ["double"] = (8, 8),
        ["nint"] = (8, 8),
        ["nuint"] = (8, 8),
        ["IntPtr"] = (8, 8),
        ["UIntPtr"] = (8, 8),
    };

    /// <summary>
    /// Native size of a type, derived from the dump's stated field offsets: the widest field
    /// extent, rounded up to the widest field alignment. Refuses rather than guesses.
    /// </summary>
    public bool TrySizeOf(string typeName, int depth, out long size, out string error)
    {
        size = 0;
        error = "";
        if (depth > 8) { error = "field-type recursion is deeper than 8 levels at '" + typeName + "'"; return false; }

        var name = typeName.Trim();
        if (name.EndsWith("*", StringComparison.Ordinal)) { size = 8; return true; }
        if (Primitives.TryGetValue(name, out var primitive)) { size = primitive.Size; return true; }
        // A generic instantiation has no laid-out fields of its own in the dump.
        if (name.IndexOf('<') >= 0)
        {
            error = "'" + name + "' is a generic instantiation; the dump lays out no instance for it";
            return false;
        }
        if (!TryFindType(name, out int at, out error)) return false;

        var declaration = _lines[at];
        if (declaration.Contains(" enum "))
        {
            foreach (var f in Fields(at))
                if (f.Name == "value__") return TrySizeOf(f.Type, depth + 1, out size, out error);
            error = "enum '" + name + "' has no value__ field in the dump";
            return false;
        }
        if (declaration.Contains(" class "))
        {
            error = "'" + name + "' is a reference type; it has no inline size";
            return false;
        }

        var fields = Fields(at);
        if (fields.Count == 0) { error = "'" + name + "' has no instance fields in the dump"; return false; }
        if (fields.Count > 1)
        {
            bool allZero = true;
            foreach (var f in fields) if (f.Offset != 0) { allZero = false; break; }
            if (allZero)
            {
                error = "'" + name + "' has " + fields.Count
                    + " fields all at offset 0 — an open generic the dump lays out no instance for";
                return false;
            }
        }

        long extent = 0;
        long alignment = 1;
        foreach (var f in fields)
        {
            if (!TryAlignAndSizeOf(f.Type, depth + 1, out long fieldSize, out long fieldAlign, out error))
            {
                error = name + "." + f.Name + ": " + error;
                return false;
            }
            extent = Math.Max(extent, f.Offset + fieldSize);
            alignment = Math.Max(alignment, fieldAlign);
        }
        size = ((extent + alignment - 1) / alignment) * alignment;
        return true;
    }

    private bool TryAlignAndSizeOf(string typeName, int depth, out long size, out long alignment, out string error)
    {
        size = 0;
        alignment = 1;
        var name = typeName.Trim();
        if (name.EndsWith("*", StringComparison.Ordinal)) { size = 8; alignment = 8; return (error = "") == ""; }
        if (Primitives.TryGetValue(name, out var primitive))
        {
            size = primitive.Size;
            alignment = primitive.Align;
            error = "";
            return true;
        }
        if (!TrySizeOf(name, depth, out size, out error)) return false;
        // A composite's alignment is the widest alignment among its own fields.
        if (TryFindType(name, out int at, out _))
        {
            foreach (var f in Fields(at))
            {
                if (!TryAlignAndSizeOf(f.Type, depth + 1, out _, out long inner, out _)) continue;
                alignment = Math.Max(alignment, inner);
            }
        }
        if (alignment < 1) alignment = 1;
        return true;
    }
}
