using System;
using System.Text;

namespace Progenitor.Effects;

/// <summary>
/// Name-prefix contract for every GameObject this module creates. Spike step-0 swept
/// <c>progenitor-fx-*</c> leftovers; the module uses one prefix so Core Uninstall and plugin
/// unload can <c>DestroyImmediate</c> by name without walking Unity types the tests cannot see.
/// </summary>
public static class EffectOwnership
{
    /// <summary>Every mod-owned effects root starts with this. Uninstall destroys the lot.</summary>
    public const string Prefix = "progenitor-effects-";

    /// <summary>Used when <see cref="Assets.Borrow"/> is called without a plugin id.</summary>
    public const string UnownedId = "unowned";

    /// <summary>
    /// DontDestroyOnLoad root for one plugin: <c>progenitor-effects-&lt;pluginId&gt;</c>.
    /// Dots in a BepInEx GUID stay (Unity GO names allow them); path separators do not.
    /// </summary>
    public static string RootName(string? pluginId) => Prefix + Sanitize(pluginId);

    /// <summary>True when <paramref name="name"/> is this module's prefix (any plugin).</summary>
    public static bool IsOwned(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="name"/> is this plugin's root or a child named under it.
    /// A BepInEx GUID that extends another (example.sample vs example.sample.extra) is not
    /// a child: '.' after the root is part of a longer plugin id, not a child separator.
    /// </summary>
    public static bool IsOwnedBy(string? name, string? pluginId)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string root = RootName(pluginId);
        if (name.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(root, StringComparison.OrdinalIgnoreCase) && name.Length > root.Length)
        {
            char next = name[root.Length];
            return next is '-' or ' ' or '/';
        }

        return false;
    }

    /// <summary>
    /// Plugin GUID to a Unity GameObject name fragment. Empty becomes <see cref="UnownedId"/>.
    /// Strips characters Unity or a later path join would treat as separators.
    /// </summary>
    public static string Sanitize(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return UnownedId;
        var src = pluginId.Trim();
        var sb = new StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0')
                sb.Append('-');
            else if (char.IsControl(c))
                sb.Append('-');
            else
                sb.Append(c);
        }

        string result = sb.ToString();
        return result.Length == 0 ? UnownedId : result;
    }
}
