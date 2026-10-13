using System;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>
/// BepInEx Toml string form for the BCL types <see cref="ConfigSync"/> will put on the wire.
/// Runtime apply prefers <c>ConfigEntryBase.GetSerializedValue</c> /
/// <c>SetSerializedValue</c> (BepInEx's TomlTypeConverter); this helper is the
/// fallback and the unit-testable round trip so a missing converter cannot silently
/// desync bool/int/float/string/enum.
/// </summary>
internal static class ConfigSyncToml
{
    /// <summary>
    /// Serialize <paramref name="value"/> as BepInEx would write it for a cfg value.
    /// Bool is <c>True</c>/<c>False</c> (<see cref="bool.ToString()"/>); numbers use
    /// invariant culture; enums use the member name. Null becomes empty.
    /// </summary>
    public static string Format(object? value, Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (value == null) return "";
        if (type == typeof(string)) return (string)value;
        if (type == typeof(bool)) return (bool)value ? "True" : "False";
        if (type.IsEnum) return value.ToString() ?? "";
        if (type == typeof(float))
            return ((float)value).ToString("R", CultureInfo.InvariantCulture);
        if (type == typeof(double))
            return ((double)value).ToString("R", CultureInfo.InvariantCulture);
        if (type == typeof(decimal))
            return ((decimal)value).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(char))
            return ((char)value).ToString();
        try
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        catch
        {
            return value.ToString() ?? "";
        }
    }

    /// <summary>
    /// Inverse of <see cref="Format"/>. Unknown text returns false so a bad packet cannot
    /// throw from a receive hook.
    /// </summary>
    public static bool TryParse(string? text, Type type, out object? value)
    {
        value = null;
        if (type == null) return false;
        text ??= "";

        if (type == typeof(string))
        {
            value = text;
            return true;
        }

        if (type == typeof(bool))
        {
            if (!bool.TryParse(text, out var b)) return false;
            value = b;
            return true;
        }

        if (type.IsEnum)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return false;
                var parsed = Enum.Parse(type, text.Trim(), ignoreCase: true);
                if (!Enum.IsDefined(type, parsed)) return false;
                value = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (type == typeof(char))
        {
            if (text.Length != 1) return false;
            value = text[0];
            return true;
        }

        try
        {
            value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            return value != null || !type.IsValueType;
        }
        catch
        {
            return false;
        }
    }
}
