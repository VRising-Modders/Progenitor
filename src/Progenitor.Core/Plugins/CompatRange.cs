using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Plugins;

/// <summary>
/// Mod compatibility C3 (docs/mod-compatibility.md, section 2): the peer versions of itself a mod works with, written
/// in the small comparator syntax. Space-separated comparators (<c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>,
/// <c>=</c>) on <c>major.minor.patch</c> must all hold; <c>||</c> joins alternatives; <c>1.2.x</c> means the same minor,
/// <c>1.x</c> the same major and <c>*</c> any version; a bare version means exactly that version. At most
/// <see cref="MaxChars"/> characters. Versions compare as numbers with missing parts as zero, a leading <c>v</c> and
/// <c>+</c> build metadata ignored, and a prerelease (<c>-beta.1</c>) below its release. Game-free.
/// </summary>
/// <example><c>&gt;=1.2.0 &lt;2.0.0</c>, <c>1.4.x</c>, <c>1.2.x || 1.4.x</c>, <c>*</c>, <c>=1.2.0</c>.</example>
public sealed class CompatRange
{
    /// <summary>The longest range text accepted (the attribute, <c>progenitor.json</c> and the feature table).</summary>
    public const int MaxChars = 64;

    private enum Op : byte { Eq, Ge, Gt, Le, Lt }

    private readonly struct Comparator
    {
        public Comparator(Op op, string version)
        {
            Kind = op;
            Version = version;
        }

        public Op Kind { get; }
        public string Version { get; }
    }

    private sealed class Alternative
    {
        public readonly List<Comparator> Parts = new();
        public bool Any;

        /// <summary>The wildcard this alternative came from (<c>1.2.x</c>), for the words; empty otherwise.</summary>
        public string Wildcard = "";
    }

    private readonly List<Alternative> _alternatives;

    private CompatRange(string text, List<Alternative> alternatives)
    {
        Text = text;
        _alternatives = alternatives;
    }

    /// <summary>The range as written (trimmed).</summary>
    public string Text { get; }

    /// <summary>True when the range accepts every version (<c>*</c> in some alternative).</summary>
    public bool IsAny
    {
        get
        {
            foreach (var a in _alternatives) if (a.Any) return true;
            return false;
        }
    }

    public override string ToString() => Text;

    /// <summary>Parse <paramref name="text"/>. False with a readable <paramref name="error"/> when it is not a valid range.</summary>
    public static bool TryParse(string? text, out CompatRange? range, out string? error)
    {
        range = null;
        string t = (text ?? "").Trim();
        if (t.Length == 0) { error = "the range is empty"; return false; }
        if (t.Length > MaxChars) { error = "the range is longer than " + MaxChars.ToString(CultureInfo.InvariantCulture) + " characters"; return false; }
        foreach (char c in t)
            if (c < 0x20 || c > 0x7E) { error = "the range has a character outside printable ASCII"; return false; }

        var alternatives = new List<Alternative>();
        foreach (string rawAlt in t.Split(new[] { "||" }, StringSplitOptions.None))
        {
            string alt = rawAlt.Trim();
            if (alt.Length == 0) { error = "an alternative between || is empty"; return false; }
            var parsed = new Alternative();
            var tokens = new List<string>(alt.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            for (int i = 0; i < tokens.Count; i++)
            {
                string tok = tokens[i];
                // ">= 1.2.0": an operator alone takes the next token as its version.
                if (IsOperatorOnly(tok) && i + 1 < tokens.Count)
                {
                    tok += tokens[i + 1];
                    i++;
                }

                if (!TryParseToken(tok, parsed, out error)) return false;
            }

            if (parsed.Any && parsed.Parts.Count > 0) { error = "* cannot be combined with comparators in one alternative"; return false; }
            alternatives.Add(parsed);
        }

        range = new CompatRange(t, alternatives);
        error = null;
        return true;
    }

    /// <summary>Parse or throw <see cref="ArgumentException"/> naming the problem.</summary>
    public static CompatRange Parse(string text)
    {
        if (TryParse(text, out var range, out var error)) return range!;
        throw new ArgumentException("'" + text + "' is not a valid compatibility range: " + error + ".", nameof(text));
    }

    /// <summary>True when <paramref name="text"/> is a valid range.</summary>
    public static bool IsValid(string? text) => TryParse(text, out _, out _);

    /// <summary>The range of every version.</summary>
    public static CompatRange Any { get; } = Parse("*");

    /// <summary>
    /// Exactly <paramref name="version"/> (<c>=1.2.0</c>). Null when the version is not one the syntax can carry (empty,
    /// too long, or containing spaces or <c>|</c>).
    /// </summary>
    public static CompatRange? Exactly(string? version)
    {
        string v = (version ?? "").Trim();
        return v.Length == 0 ? null : TryParse("=" + v, out var r, out _) ? r : null;
    }

    /// <summary>
    /// The same minor as <paramref name="version"/> (<c>1.4.x</c> for 1.4.3): the default of a mod that declares nothing.
    /// A version without a numeric <c>major.minor</c> falls back to <see cref="Exactly"/>; null when even that fails.
    /// </summary>
    public static CompatRange? SameMinorAs(string? version)
    {
        if (CompatVersion.TryParse(version, out var parts, out _) && parts.Count >= 2)
            return Parse(parts[0].ToString(CultureInfo.InvariantCulture) + "." + parts[1].ToString(CultureInfo.InvariantCulture) + ".x");
        return Exactly(version);
    }

    /// <summary>
    /// True when <paramref name="version"/> is in the range. A version that does not parse is in the range only when an
    /// alternative is <c>*</c>, or it is exactly the text of an <c>=</c> comparator (case-insensitive).
    /// </summary>
    public bool Includes(string? version)
    {
        string v = (version ?? "").Trim();
        foreach (var alt in _alternatives)
        {
            if (alt.Any) return true;
            bool all = alt.Parts.Count > 0;
            foreach (var c in alt.Parts)
            {
                if (!Holds(c, v)) { all = false; break; }
            }

            if (all) return true;
        }

        return false;
    }

    private static bool Holds(Comparator c, string version)
    {
        int? order = CompatVersion.Compare(version, c.Version);
        if (order == null)
            return c.Kind == Op.Eq && string.Equals(version, c.Version, StringComparison.OrdinalIgnoreCase);
        int o = order.Value;
        return c.Kind switch
        {
            Op.Eq => o == 0,
            Op.Ge => o >= 0,
            Op.Gt => o > 0,
            Op.Le => o <= 0,
            Op.Lt => o < 0,
            _ => false,
        };
    }

    /// <summary>
    /// The range in words for players and admins: <c>1.2.0 or newer, below 2.0.0</c>; <c>exactly 1.2.0</c>;
    /// <c>any 1.4.x version</c>; <c>any version</c>; alternatives joined with <c>or</c>.
    /// </summary>
    public string Describe()
    {
        if (IsAny) return "any version";
        var words = new List<string>();
        foreach (var alt in _alternatives)
        {
            if (alt.Wildcard.Length > 0 && alt.Parts.Count == 2)
            {
                words.Add("any " + alt.Wildcard + " version");
                continue;
            }

            var bits = new List<string>();
            foreach (var c in alt.Parts)
            {
                bits.Add(c.Kind switch
                {
                    Op.Eq => "exactly " + c.Version,
                    Op.Ge => c.Version + " or newer",
                    Op.Gt => "newer than " + c.Version,
                    Op.Le => c.Version + " or older",
                    _ => "below " + c.Version,
                });
            }

            words.Add(string.Join(", ", bits));
        }

        return string.Join(" or ", words);
    }

    private static bool IsOperatorOnly(string token) => token is ">=" or "<=" or ">" or "<" or "=";

    private static bool IsLiteral(string text)
    {
        foreach (char c in text)
            if (!(c < 0x80 && char.IsLetterOrDigit(c)) && c != '.' && c != '-' && c != '_' && c != '+') return false;
        return text.Length > 0;
    }

    private static bool TryParseToken(string token, Alternative into, out string? error)
    {
        error = null;
        if (token == "*" || token == "x" || token == "X")
        {
            into.Any = true;
            return true;
        }

        Op op = Op.Eq;
        string rest = token;
        if (token.StartsWith(">=", StringComparison.Ordinal)) { op = Op.Ge; rest = token.Substring(2); }
        else if (token.StartsWith("<=", StringComparison.Ordinal)) { op = Op.Le; rest = token.Substring(2); }
        else if (token.StartsWith(">", StringComparison.Ordinal)) { op = Op.Gt; rest = token.Substring(1); }
        else if (token.StartsWith("<", StringComparison.Ordinal)) { op = Op.Lt; rest = token.Substring(1); }
        else if (token.StartsWith("=", StringComparison.Ordinal)) { op = Op.Eq; rest = token.Substring(1); }
        bool hasOperator = rest.Length != token.Length;
        if (rest.Length == 0) { error = "'" + token + "' has no version"; return false; }

        if (IsWildcard(rest, out var wildParts))
        {
            if (hasOperator) { error = "'" + token + "': a wildcard cannot follow an operator"; return false; }
            if (wildParts.Count == 0)
            {
                into.Any = true;
                return true;
            }

            // 1.x: [1.0.0-0, 2.0.0-0); 1.2.x: [1.2.0-0, 1.3.0-0). The "-0" bounds take in the prereleases of the first
            // release (1.2.0-beta is a 1.2.x build) and keep out those of the next (1.3.0-beta is not).
            string low = (wildParts.Count == 1
                ? wildParts[0].ToString(CultureInfo.InvariantCulture) + ".0.0"
                : wildParts[0].ToString(CultureInfo.InvariantCulture) + "." + wildParts[1].ToString(CultureInfo.InvariantCulture) + ".0") + "-0";
            string high = wildParts.Count == 1
                ? (wildParts[0] + 1).ToString(CultureInfo.InvariantCulture) + ".0.0"
                : wildParts[0].ToString(CultureInfo.InvariantCulture) + "." + (wildParts[1] + 1).ToString(CultureInfo.InvariantCulture) + ".0";
            into.Parts.Add(new Comparator(Op.Ge, low));
            into.Parts.Add(new Comparator(Op.Lt, high + "-0"));
            into.Wildcard = string.Join(".", wildParts) + ".x";
            return true;
        }

        if (!CompatVersion.TryParse(rest, out _, out _))
        {
            // "=<text>" may name a version that is not numeric (a build tag): it then matches that exact text only.
            if (op == Op.Eq && hasOperator && IsLiteral(rest))
            {
                into.Parts.Add(new Comparator(op, rest));
                return true;
            }

            error = "'" + rest + "' is not a version (numbers like 1.2.0)";
            return false;
        }
        into.Parts.Add(new Comparator(op, rest));
        return true;
    }

    private static bool IsWildcard(string text, out List<long> parts)
    {
        parts = new List<long>();
        string[] pieces = text.Split('.');
        if (pieces.Length < 1 || pieces.Length > 3) return false;
        string last = pieces[pieces.Length - 1];
        if (last != "x" && last != "X" && last != "*") return false;
        for (int i = 0; i < pieces.Length - 1; i++)
        {
            if (pieces[i].Length == 0 || pieces[i].Length > 9
                || !long.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return false;
            parts.Add(n);
        }

        return parts.Count <= 2;
    }
}

/// <summary>
/// The version order the compatibility ranges use: dotted numbers (missing parts are zero, so <c>1.2</c> equals
/// <c>1.2.0</c>), a leading <c>v</c> skipped, <c>+</c> build metadata ignored, and a <c>-</c> prerelease below its release
/// (prerelease identifiers compare as SemVer does: numbers numerically and before words). Game-free.
/// </summary>
public static class CompatVersion
{
    /// <summary>Negative, zero or positive as <paramref name="a"/> is below, equal to or above <paramref name="b"/>; null when either does not parse.</summary>
    public static int? Compare(string? a, string? b)
    {
        if (!TryParse(a, out var x, out var xp) || !TryParse(b, out var y, out var yp)) return null;
        for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            long p = i < x.Count ? x[i] : 0, q = i < y.Count ? y[i] : 0;
            if (p != q) return p < q ? -1 : 1;
        }

        if (xp.Length == 0 && yp.Length == 0) return 0;
        if (xp.Length == 0) return 1;
        if (yp.Length == 0) return -1;
        string[] xs = xp.Split('.'), ys = yp.Split('.');
        for (int i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            bool xn = long.TryParse(xs[i], NumberStyles.None, CultureInfo.InvariantCulture, out long xv);
            bool yn = long.TryParse(ys[i], NumberStyles.None, CultureInfo.InvariantCulture, out long yv);
            int c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.Compare(xs[i], ys[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c < 0 ? -1 : 1;
        }

        return xs.Length == ys.Length ? 0 : xs.Length < ys.Length ? -1 : 1;
    }

    /// <summary>Split a version into its numbers and prerelease. False when it is not a version.</summary>
    public static bool TryParse(string? value, out List<long> parts, out string prerelease)
    {
        parts = new List<long>();
        prerelease = "";
        string s = (value ?? "").Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s.Substring(dash + 1);
            s = s.Substring(0, dash);
            if (prerelease.Length == 0) return false;
            foreach (char c in prerelease)
                if (!(char.IsLetterOrDigit(c) && c < 0x80) && c != '.' && c != '-') return false;
        }

        if (s.Length == 0) return false;
        string[] pieces = s.Split('.');
        if (pieces.Length > 6) return false;
        foreach (string p in pieces)
        {
            if (p.Length == 0 || p.Length > 18 || !long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return false;
            parts.Add(n);
        }

        return parts.Count >= 1;
    }

    /// <summary><c>major.minor</c> of a version (<c>1.4</c> for 1.4.3), or null when it has no numeric major.</summary>
    public static string? MinorOf(string? version)
    {
        if (!TryParse(version, out var parts, out _)) return null;
        return parts[0].ToString(CultureInfo.InvariantCulture) + "." + (parts.Count > 1 ? parts[1] : 0).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The effective compatibility range of a mod build: what its declaration means about peer versions of itself. A
/// declared <c>Peers</c> range wins; otherwise an explicit <see cref="NetworkVersionRule"/> (Exact: exactly this
/// version; Minor: the same minor; None: any); otherwise the cautious default, the same minor (C3 decision 3, which
/// replaced the old Exact default for mods that declare neither). Game-free.
/// </summary>
public static class CompatRanges
{
    /// <summary>The effective range text for a build at <paramref name="version"/>; null only when the version cannot be carried.</summary>
    public static string? EffectiveText(string? version, string? declaredPeers, bool ruleDeclared, NetworkVersionRule rule)
    {
        string peers = (declaredPeers ?? "").Trim();
        if (peers.Length > 0 && CompatRange.IsValid(peers)) return peers;
        if (ruleDeclared)
        {
            switch (rule)
            {
                case NetworkVersionRule.None: return "*";
                case NetworkVersionRule.Exact: return CompatRange.Exactly(version)?.Text;
            }
        }

        return CompatRange.SameMinorAs(version)?.Text;
    }

    /// <summary><see cref="EffectiveText"/> parsed; null when there is none.</summary>
    public static CompatRange? Effective(string? version, string? declaredPeers, bool ruleDeclared, NetworkVersionRule rule)
    {
        string? text = EffectiveText(version, declaredPeers, ruleDeclared, rule);
        return text != null && CompatRange.TryParse(text, out var r, out _) ? r : null;
    }

    /// <summary>A range text as players read it (<see cref="CompatRange.Describe"/>), or the text itself when it does not parse.</summary>
    public static string Words(string? range) =>
        CompatRange.TryParse(range, out var r, out _) ? r!.Describe() : (range ?? "").Trim();
}
