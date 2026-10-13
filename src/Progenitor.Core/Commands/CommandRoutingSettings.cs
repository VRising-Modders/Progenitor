using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Commands;

/// <summary>
/// Deployment-wide command routing configuration: fallback root plus administrator
/// assign/rename/disable mappings. Parsed from the shared BepInEx cfg
/// (<c>[Commands]</c> string keys). Invalid text is rejected without applying.
/// </summary>
public sealed class CommandRoutingSettings
{
    /// <summary>Default qualified-route root token (<c>.p &lt;owner&gt; …</c>).</summary>
    public const string DefaultFallbackRoot = "p";

    /// <summary>Owner id recorded for Core built-in commands.</summary>
    public const string BuiltInOwnerId = "progenitor";

    public static CommandRoutingSettings Default { get; } = new(
        DefaultFallbackRoot,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    internal CommandRoutingSettings(
        string fallbackRoot,
        Dictionary<string, string> assign,
        Dictionary<string, string> rename,
        HashSet<string> disable)
    {
        FallbackRoot = fallbackRoot;
        Assign = assign;
        Rename = rename;
        Disable = disable;
    }

    /// <summary>Single token reserved for qualified routes. Case-insensitive.</summary>
    public string FallbackRoot { get; }

    /// <summary>Short token → owner id. Explicitly assigns a contested shortcut.</summary>
    public IReadOnlyDictionary<string, string> Assign { get; }

    /// <summary>
    /// <c>ownerId:name</c> or <c>ownerId:group/name</c> → new short token.
    /// Identity (qualified path) is unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string> Rename { get; }

    /// <summary>
    /// Owner id, or <c>ownerId:name</c> / <c>ownerId:group/name</c>. Disables requested
    /// shortcuts; qualified routes stay available.
    /// </summary>
    public IReadOnlySet<string> Disable { get; }

    public static bool TryParse(
        string? fallbackRoot,
        string? assign,
        string? rename,
        string? disable,
        out CommandRoutingSettings settings,
        out string error)
    {
        settings = Default;
        if (!TryNormalizeToken(fallbackRoot, DefaultFallbackRoot, allowDefault: true, out var root, out error))
            return false;

        var assignMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryParsePairs(assign, "Assign", assignMap, out error))
            return false;

        var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryParsePairs(rename, "Rename", renameMap, out error))
            return false;

        var disableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryParseList(disable, "Disable", disableSet, out error))
            return false;

        foreach (var kv in assignMap)
        {
            if (string.Equals(kv.Key, root, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Assign: cannot assign reserved fallback root '{root}'.";
                return false;
            }
            if (!IsSingleToken(kv.Key))
            {
                error = $"Assign: shortcut '{kv.Key}' must be a single token.";
                return false;
            }
            if (!IsOwnerId(kv.Value))
            {
                error = $"Assign: owner id '{kv.Value}' for '{kv.Key}' is not a single token.";
                return false;
            }
        }

        foreach (var kv in renameMap)
        {
            if (!TrySplitOwnerPath(kv.Key, out _, out _))
            {
                error = $"Rename: '{kv.Key}' must be ownerId:name or ownerId:group/name.";
                return false;
            }
            if (!IsSingleToken(kv.Value))
            {
                error = $"Rename: new shortcut '{kv.Value}' must be a single token.";
                return false;
            }
            if (string.Equals(kv.Value, root, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Rename: '{kv.Key}' cannot use reserved fallback root '{root}'.";
                return false;
            }
        }

        foreach (var item in disableSet)
        {
            if (item.IndexOf(':') < 0)
            {
                if (!IsOwnerId(item))
                {
                    error = $"Disable: owner id '{item}' is not a single token.";
                    return false;
                }
            }
            else if (!TrySplitOwnerPath(item, out _, out _))
            {
                error = $"Disable: '{item}' must be ownerId or ownerId:name or ownerId:group/name.";
                return false;
            }
        }

        settings = new CommandRoutingSettings(root, assignMap, renameMap, disableSet);
        return true;
    }

    internal bool IsOwnerDisabled(string ownerId) =>
        Disable.Contains(ownerId);

    internal bool IsCommandDisabled(string ownerId, string groupName, string name)
    {
        if (IsOwnerDisabled(ownerId)) return true;
        if (Disable.Contains(CommandKey(ownerId, groupName, name))) return true;
        if (string.IsNullOrEmpty(groupName) && Disable.Contains(ownerId + ":" + name)) return true;
        return false;
    }

    internal bool TryRename(string ownerId, string groupName, string name, out string newToken)
    {
        if (Rename.TryGetValue(CommandKey(ownerId, groupName, name), out newToken!))
            return true;
        newToken = null!;
        return false;
    }

    internal bool TryAssign(string token, out string ownerId) =>
        Assign.TryGetValue(token, out ownerId!);

    internal static string CommandKey(string ownerId, string groupName, string name) =>
        string.IsNullOrEmpty(groupName)
            ? ownerId + ":" + name
            : ownerId + ":" + groupName + "/" + name;

    internal static bool TrySplitOwnerPath(string spec, out string ownerId, out string path)
    {
        ownerId = "";
        path = "";
        int colon = spec.IndexOf(':');
        if (colon <= 0 || colon == spec.Length - 1) return false;
        ownerId = spec.Substring(0, colon).Trim();
        path = spec.Substring(colon + 1).Trim();
        if (!IsOwnerId(ownerId) || path.Length == 0) return false;
        if (path.IndexOf(':') >= 0) return false;
        return true;
    }

    internal static bool IsSingleToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value!.Trim() == value
        && value.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) < 0;

    internal static bool IsOwnerId(string? value) => IsSingleToken(value);

    internal static bool TryNormalizeToken(
        string? value,
        string defaultValue,
        bool allowDefault,
        out string token,
        out string error)
    {
        error = "";
        token = defaultValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!allowDefault)
            {
                error = "Token is empty.";
                return false;
            }
            return true;
        }

        token = value.Trim();
        if (!IsSingleToken(token))
        {
            error = $"FallbackRoot '{value}' must be a single token (no whitespace).";
            token = defaultValue;
            return false;
        }
        return true;
    }

    private static bool TryParsePairs(
        string? text,
        string label,
        Dictionary<string, string> into,
        out string error)
    {
        error = "";
        foreach (var raw in EnumerateEntries(text))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0 || eq == raw.Length - 1)
            {
                error = $"{label}: expected 'key = value', got '{raw}'.";
                return false;
            }
            string key = raw.Substring(0, eq).Trim();
            string value = raw.Substring(eq + 1).Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                error = $"{label}: expected 'key = value', got '{raw}'.";
                return false;
            }
            if (into.TryGetValue(key, out var existing)
                && !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{label}: '{key}' is mapped to both '{existing}' and '{value}'.";
                return false;
            }
            into[key] = value;
        }
        return true;
    }

    private static bool TryParseList(
        string? text,
        string label,
        HashSet<string> into,
        out string error)
    {
        error = "";
        foreach (var raw in EnumerateEntries(text))
        {
            if (raw.IndexOf('=') >= 0)
            {
                error = $"{label}: expected an owner id or ownerId:path, got '{raw}'.";
                return false;
            }
            into.Add(raw);
        }
        return true;
    }

    private static IEnumerable<string> EnumerateEntries(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var sb = new StringBuilder();
        void Flush(List<string> sink)
        {
            string line = sb.ToString().Trim();
            sb.Clear();
            if (line.Length == 0) return;
            int hash = line.IndexOf('#');
            if (hash == 0) return;
            if (hash > 0) line = line.Substring(0, hash).Trim();
            if (line.Length == 0) return;
            sink.Add(line);
        }

        var lines = new List<string>();
        for (int i = 0; i <= text!.Length; i++)
        {
            char c = i < text.Length ? text[i] : '\n';
            if (c == '\n' || c == '\r' || c == ';')
            {
                Flush(lines);
                continue;
            }
            sb.Append(c);
        }
        foreach (var line in lines) yield return line;
    }
}
