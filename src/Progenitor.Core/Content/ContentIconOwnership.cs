using System;
using System.Globalization;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Name-prefix contract for plugin-supplied buff icons. The Sprite/Texture2D pair now
/// lives in Bootstrap <c>IconCache</c> (process-lifetime, <c>hideFlags = HideAndDontSave</c>)
/// so a Core ALC swap does not <c>Destroy</c> it. This type still sanitizes plugin ids for
/// cache keys and leftover-holder names; Uninstall only drops Core-side leases.
/// </summary>
public static class ContentIconOwnership
{
    /// <summary>Every mod-owned content-icon root starts with this. Uninstall destroys the lot.</summary>
    public const string Prefix = "progenitor-content-icons-";

    /// <summary>Used when the definition owner has no BepInEx GUID.</summary>
    public const string UnownedId = "unowned";

    /// <summary>
    /// Icon-cache owner id: BepInEx GUID when present, otherwise the assembly simple name.
    /// Must resolve to the same cache row as Bootstrap <c>IconCachePolicy.OwnerId</c> so
    /// file-removal releases non-GUID owners (R26 finding 11). It does not have to be the
    /// same <em>string</em>: since R27 finding 14b the Bootstrap side case-normalizes the
    /// plugin id on every write and every lookup, so the raw id is safe to pass through.
    /// </summary>
    public static string OwnerId(string? guid, string? assemblyName)
    {
        if (!string.IsNullOrWhiteSpace(guid)) return guid.Trim();
        if (!string.IsNullOrWhiteSpace(assemblyName)) return assemblyName.Trim();
        return UnownedId;
    }

    /// <summary>
    /// Once-per-swap self check written after a clone's icon is applied:
    /// <c>PrefabRegistry: icon cache check 'sample.nodrain' sprite=-1234 texture=-1235 alive=True</c>.
    /// The two instance ids must not move across Core generations — a changed id means a new
    /// Sprite/Texture2D pair was minted and every live buff instance is still pointing at the
    /// old one (evidence 112 / 122 / 131). Logged at Warning with <see cref="NotAliveSuffix"/>
    /// when the pair failed the Unity null check.
    /// </summary>
    public static string FormatIconCacheCheck(string? id, int spriteInstanceId, int textureInstanceId, bool alive)
    {
        return "PrefabRegistry: icon cache check '" + (id ?? "") + "'"
            + " sprite=" + spriteInstanceId.ToString(CultureInfo.InvariantCulture)
            + " texture=" + textureInstanceId.ToString(CultureInfo.InvariantCulture)
            + " alive=" + (alive ? "True" : "False")
            + (alive ? "" : NotAliveSuffix);
    }

    /// <summary>Appended to <see cref="FormatIconCacheCheck"/> when the cached pair is dead.</summary>
    public const string NotAliveSuffix =
        "; the cached pair was destroyed under a live instance and the HUD will draw a blank icon";

    /// <summary>Grep token for the self-check line.</summary>
    public const string IconCacheCheckToken = "icon cache check";

    /// <summary>DontDestroyOnLoad root for one plugin: <c>progenitor-content-icons-&lt;pluginId&gt;</c>.</summary>
    public static string RootName(string? pluginId) => Prefix + Sanitize(pluginId);

    /// <summary>True when <paramref name="name"/> is this module's prefix (any plugin).</summary>
    public static bool IsOwned(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="name"/> is this plugin's root. A BepInEx GUID that extends
    /// another is not a child: '.' after the root is part of a longer plugin id.
    /// </summary>
    public static bool IsOwnedBy(string? name, string? pluginId)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string root = RootName(pluginId);
        return name.Equals(root, StringComparison.OrdinalIgnoreCase);
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
