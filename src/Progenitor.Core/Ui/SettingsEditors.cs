using System;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>Which editor widget a settings row wants.</summary>
public enum SettingsEditorKind
{
    /// <summary>Not editable here (unsupported type, or a synced entry the server owns).</summary>
    ReadOnly = 0,

    /// <summary>Toggle.</summary>
    Bool = 1,

    /// <summary>Text field, integer parse.</summary>
    Int = 2,

    /// <summary>Text field, invariant float parse.</summary>
    Float = 3,

    /// <summary>Text field, verbatim.</summary>
    String = 4,

    /// <summary>Text field, enum name parse (case-insensitive).</summary>
    Enum = 5,

    /// <summary>Press-a-key button.</summary>
    Keybind = 6,

    /// <summary>Registered action button. Not returned by <see cref="SettingsEditors.KindFor"/>.</summary>
    Action = 7,
}

/// <summary>
/// Parse and format rules for the settings panel's editors. Deliberately game-free and
/// deliberately <b>invariant</b>: a cfg written on a German client must read back the same on an
/// English one, so <c>1.5</c> is always a dot and never a comma, exactly as BepInEx's own Toml
/// writer does. This type is where the panel's whole "what does the player's typing mean" contract
/// lives, so it is the part the tests pin.
/// </summary>
public static class SettingsEditors
{
    private static bool IsFlags(Type type)
    {
        try { return type.IsDefined(typeof(FlagsAttribute), inherit: false); }
        catch { return false; }
    }

    /// <summary>
    /// True when every comma-separated part of <paramref name="text"/> is a declared name of
    /// <paramref name="type"/>. Empty parts (a trailing comma) fail, so "Combat," is refused
    /// rather than silently meaning "Combat".
    /// </summary>
    private static bool AllNamesDefined(Type type, string text)
    {
        var names = Enum.GetNames(type);
        var parts = text.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0) return false;
            bool known = false;
            for (int j = 0; j < names.Length; j++)
            {
                if (!string.Equals(names[j], part, StringComparison.OrdinalIgnoreCase)) continue;
                known = true;
                break;
            }

            if (!known) return false;
        }

        return true;
    }

    /// <summary>Which editor a CLR type gets. Anything else is <see cref="SettingsEditorKind.ReadOnly"/>.</summary>
    public static SettingsEditorKind KindFor(Type? type)
    {
        if (type == null) return SettingsEditorKind.ReadOnly;
        if (type == typeof(bool)) return SettingsEditorKind.Bool;
        if (type == typeof(string)) return SettingsEditorKind.String;
        if (type.IsEnum) return SettingsEditorKind.Enum;
        if (type == typeof(sbyte) || type == typeof(byte)
            || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint)
            || type == typeof(long) || type == typeof(ulong))
            return SettingsEditorKind.Int;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return SettingsEditorKind.Float;
        return SettingsEditorKind.ReadOnly;
    }

    /// <summary>
    /// Default of the eight-control hint for a CLR type. Authors override via
    /// <see cref="SettingPresentation.EditorHint"/>.
    /// </summary>
    public static SettingsEditorHint HintFor(Type? type, SettingsEditorHint? overrideHint = null)
    {
        if (overrideHint.HasValue) return overrideHint.Value;
        var kind = KindFor(type);
        switch (kind)
        {
            case SettingsEditorKind.Bool: return SettingsEditorHint.Toggle;
            case SettingsEditorKind.Int:
            case SettingsEditorKind.Float: return SettingsEditorHint.Numeric;
            case SettingsEditorKind.Enum: return SettingsEditorHint.Dropdown;
            case SettingsEditorKind.String: return SettingsEditorHint.Text;
            case SettingsEditorKind.Keybind: return SettingsEditorHint.Keybind;
            case SettingsEditorKind.Action: return SettingsEditorHint.Action;
            default: return SettingsEditorHint.Text;
        }
    }

    /// <summary>Default commit policy for one of the eight controls.</summary>
    public static SettingCommitPolicy CommitPolicyFor(SettingsEditorHint hint)
    {
        switch (hint)
        {
            case SettingsEditorHint.Toggle:
            case SettingsEditorHint.Dropdown:
            case SettingsEditorHint.Action:
                return SettingCommitPolicy.Immediate;
            case SettingsEditorHint.Slider:
                return SettingCommitPolicy.OnRelease;
            default:
                return SettingCommitPolicy.OnSubmit;
        }
    }

    /// <summary>Invariant <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public static string FormatColor(int r, int g, int b, int a = 255, bool includeAlpha = false)
    {
        r = ClampByte(r); g = ClampByte(g); b = ClampByte(b); a = ClampByte(a);
        return includeAlpha
            ? "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2") + a.ToString("X2")
            : "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    }

    /// <summary>Parse <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. Hash optional.</summary>
    public static bool TryParseColor(string? text, bool requireAlpha, out int r, out int g, out int b, out int a, out string error)
    {
        r = g = b = 0;
        a = 255;
        error = "";
        string s = (text ?? "").Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.Length == 3)
        {
            if (!TryNibble(s[0], out r) || !TryNibble(s[1], out g) || !TryNibble(s[2], out b))
            {
                error = "expected #RRGGBB";
                return false;
            }

            r *= 17; g *= 17; b *= 17;
            return true;
        }

        if (s.Length == 6 || s.Length == 8)
        {
            if (!byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rb)
                || !byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gb)
                || !byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bb))
            {
                error = "expected #RRGGBB";
                return false;
            }

            r = rb; g = gb; b = bb;
            if (s.Length == 8)
            {
                if (!byte.TryParse(s.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ab))
                {
                    error = "expected #RRGGBBAA";
                    return false;
                }

                a = ab;
            }
            else if (requireAlpha)
            {
                error = "expected #RRGGBBAA";
                return false;
            }

            return true;
        }

        error = requireAlpha ? "expected #RRGGBBAA" : "expected #RRGGBB";
        return false;
    }

    /// <summary>Parse a color into the invariant string stored on a string setting.</summary>
    public static bool TryParseColor(string? text, bool requireAlpha, out string formatted, out string error)
    {
        formatted = "";
        if (!TryParseColor(text, requireAlpha, out int r, out int g, out int b, out int a, out error))
            return false;
        formatted = FormatColor(r, g, b, a, includeAlpha: requireAlpha || a != 255);
        return true;
    }

    private static int ClampByte(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    private static bool TryNibble(char c, out int value)
    {
        if (c >= '0' && c <= '9') { value = c - '0'; return true; }
        if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
        if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
        value = 0;
        return false;
    }

    /// <summary>
    /// The text the editor shows. Invariant culture throughout; <c>float</c> and <c>double</c>
    /// use round-trip ("R") so a value the player did not touch is written back byte-identical
    /// instead of losing its last digits every time the panel opens.
    /// </summary>
    public static string Format(object? value)
    {
        switch (value)
        {
            case null: return "";
            case bool b: return b ? "true" : "false";
            case string s: return s;
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString(CultureInfo.InvariantCulture);
            case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
            default: return value.ToString() ?? "";
        }
    }

    /// <summary>
    /// Parse the player's text into <paramref name="type"/>. Returns false with a one-line
    /// <paramref name="error"/> the panel shows next to the row; the entry is not written.
    ///
    /// <para>The rules, in the order they surprise people:</para>
    /// <list type="bullet">
    /// <item><description><b>string</b> is verbatim — leading and trailing spaces are kept, because
    /// a prefix setting of <c>"[mod] "</c> means the trailing space.</description></item>
    /// <item><description>Everything else is trimmed first, so <c>" 12 "</c> is 12.</description></item>
    /// <item><description><b>bool</b> accepts <c>true/false</c>, <c>1/0</c>, <c>yes/no</c>,
    /// <c>on/off</c>, case-insensitively. Nothing else.</description></item>
    /// <item><description><b>int</b> is integer-only: <c>1.0</c> is refused rather than silently
    /// truncated, and the value must fit the target type.</description></item>
    /// <item><description><b>float</b> is invariant — <c>1,5</c> is refused, not read as 15 — and
    /// refuses NaN / Infinity, which round-trip through a cfg badly.</description></item>
    /// <item><description><b>enum</b> is by name, case-insensitive; a bare number is refused so a
    /// renumbered enum cannot silently mean something else.</description></item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? text, Type? type, out object? value, out string error)
    {
        value = null;
        error = "";
        if (type == null)
        {
            error = "no type";
            return false;
        }

        if (type == typeof(string))
        {
            value = text ?? "";
            return true;
        }

        string s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            error = "value is empty";
            return false;
        }

        if (type == typeof(bool))
        {
            switch (s.ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": value = true; return true;
                case "false": case "0": case "no": case "off": value = false; return true;
                default:
                    error = "expected true or false";
                    return false;
            }
        }

        if (type.IsEnum)
        {
            bool flags = IsFlags(type);
            string expected = "expected " + (flags ? "one or more (comma-separated) of: " : "one of: ")
                + string.Join(", ", Enum.GetNames(type));

            if (char.IsDigit(s[0]) || s[0] == '-' || s[0] == '+')
            {
                // SETTINGS1 stage 2: a [Flags] value with bits no name covers can only be written as
                // its number (Enum.ToString gives "9"), so a flags enum takes a bare whole number as
                // raw bits. A plain enum still refuses one: a renumbered enum must not silently
                // mean something else.
                if (flags)
                {
                    try
                    {
                        if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong bits))
                        {
                            value = Enum.ToObject(type, bits);
                            return true;
                        }

                        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed))
                        {
                            value = Enum.ToObject(type, signed);
                            return true;
                        }
                    }
                    catch (ArgumentException) { }
                    catch (OverflowException) { }
                }

                error = expected;
                return false;
            }

            // A [Flags] enum's own Format produces "Combat, Loot", and Enum.IsDefined rejects
            // exactly that — so the panel used to refuse the string it had just printed and a
            // combined value could never be typed (R34 panel note 4). Validate the parts instead
            // of the combination: every component must be a name this enum declares.
            if (flags && !AllNamesDefined(type, s))
            {
                error = expected;
                return false;
            }

            try
            {
                value = Enum.Parse(type, s, ignoreCase: true);
                if (value != null && (flags || Enum.IsDefined(type, value))) return true;
            }
            catch (ArgumentException) { }
            catch (OverflowException) { }

            value = null;
            error = expected;
            return false;
        }

        var kind = KindFor(type);
        if (kind == SettingsEditorKind.Int)
        {
            if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
            {
                if (!ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong unsigned))
                {
                    error = "expected a whole number";
                    return false;
                }

                if (type != typeof(ulong))
                {
                    error = "value is out of range for " + type.Name;
                    return false;
                }

                value = unsigned;
                return true;
            }

            try
            {
                value = Convert.ChangeType(parsed, type, CultureInfo.InvariantCulture);
                return true;
            }
            catch (OverflowException)
            {
                error = "value is out of range for " + type.Name;
                return false;
            }
        }

        if (kind == SettingsEditorKind.Float)
        {
            if (type == typeof(decimal))
            {
                if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dec))
                {
                    error = "expected a number (use '.' for the decimal point)";
                    return false;
                }

                value = dec;
                return true;
            }

            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                error = "expected a number (use '.' for the decimal point)";
                return false;
            }

            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                error = "expected a finite number";
                return false;
            }

            if (type == typeof(float))
            {
                if (d > float.MaxValue || d < float.MinValue)
                {
                    error = "value is out of range for Single";
                    return false;
                }

                value = (float)d;
                return true;
            }

            value = d;
            return true;
        }

        error = type.Name + " is not editable here";
        return false;
    }
}
