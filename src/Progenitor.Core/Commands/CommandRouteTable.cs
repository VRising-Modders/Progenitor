using System;
using System.Collections.Generic;

namespace Progenitor.Commands;

/// <summary>
/// Immutable dispatch table built from the live registration set plus administrator settings.
/// Built as a whole; the registry installs it only when construction succeeds.
/// </summary>
internal sealed class CommandRouteTable
{
    internal static readonly StringComparer TokenComparer = StringComparer.OrdinalIgnoreCase;

    internal readonly string FallbackRoot;
    internal readonly Dictionary<string, RouteSlot> Root;
    internal readonly Dictionary<string, Dictionary<string, RouteSlot>> Groups;
    internal readonly Dictionary<string, Dictionary<string, CommandBundle>> Qualified;
    internal readonly List<CommandBundle> Live;
    internal readonly List<string> Diagnostics;

    private CommandRouteTable(
        string fallbackRoot,
        Dictionary<string, RouteSlot> root,
        Dictionary<string, Dictionary<string, RouteSlot>> groups,
        Dictionary<string, Dictionary<string, CommandBundle>> qualified,
        List<CommandBundle> live,
        List<string> diagnostics)
    {
        FallbackRoot = fallbackRoot;
        Root = root;
        Groups = groups;
        Qualified = qualified;
        Live = live;
        Diagnostics = diagnostics;
    }

    internal static CommandRouteTable Empty(string fallbackRoot) =>
        new(
            fallbackRoot,
            new Dictionary<string, RouteSlot>(TokenComparer),
            new Dictionary<string, Dictionary<string, RouteSlot>>(TokenComparer),
            new Dictionary<string, Dictionary<string, CommandBundle>>(TokenComparer),
            new List<CommandBundle>(),
            new List<string>());

    internal bool TryGetRoot(string token, out RouteSlot slot) =>
        Root.TryGetValue(token, out slot!);

    internal bool TryGetGrouped(string group, string name, out RouteSlot slot)
    {
        slot = null!;
        return Groups.TryGetValue(group, out var cmds) && cmds.TryGetValue(name, out slot!);
    }

    internal bool TryGetGroup(string group, out Dictionary<string, RouteSlot> commands) =>
        Groups.TryGetValue(group, out commands!);

    internal bool TryGetQualified(string ownerId, string path, out CommandBundle bundle)
    {
        bundle = null!;
        return Qualified.TryGetValue(ownerId, out var byPath)
            && byPath.TryGetValue(path, out bundle!);
    }

    internal bool TryGetQualifiedOwner(string ownerId, out Dictionary<string, CommandBundle> byPath) =>
        Qualified.TryGetValue(ownerId, out byPath!);

    /// <summary>
    /// Collapse same-owner qualified identities to the last registration, then assign shortcuts.
    /// Duplicate qualified identities from two different owner ids are a build error.
    /// Shortcut collisions disable every competing short route unless Assign names one owner.
    /// </summary>
    internal static bool TryBuild(
        List<CommandBundle> bundles,
        CommandRoutingSettings settings,
        out CommandRouteTable table,
        out string error)
    {
        table = null!;
        error = "";
        if (!CommandRoutingSettings.IsSingleToken(settings.FallbackRoot))
        {
            error = $"FallbackRoot '{settings.FallbackRoot}' must be a single token (no whitespace).";
            return false;
        }

        var live = CollapseLive(bundles);
        var qualified = new Dictionary<string, Dictionary<string, CommandBundle>>(TokenComparer);
        var seenIdentity = new Dictionary<string, CommandBundle>(TokenComparer);

        foreach (var bundle in live)
        {
            string identity = QualifiedIdentity(bundle);
            if (seenIdentity.TryGetValue(identity, out var other)
                && !string.Equals(other.OwnerId, bundle.OwnerId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Duplicate qualified identity '{identity}' from '{other.OwnerId}' and '{bundle.OwnerId}'.";
                return false;
            }
            seenIdentity[identity] = bundle;

            if (!qualified.TryGetValue(bundle.OwnerId, out var byPath))
            {
                byPath = new Dictionary<string, CommandBundle>(TokenComparer);
                qualified[bundle.OwnerId] = byPath;
            }
            byPath[bundle.DisplayPath] = bundle;
        }

        var rootClaims = new Dictionary<string, List<CommandBundle>>(TokenComparer);
        var groupClaims = new Dictionary<string, Dictionary<string, List<CommandBundle>>>(TokenComparer);
        var diagnostics = new List<string>();

        foreach (var bundle in live)
        {
            bundle.ShortcutsEnabled = false;
            bundle.EffectiveShortcut = bundle.Name;
            if (settings.IsCommandDisabled(bundle.OwnerId, bundle.GroupName, bundle.Name))
                continue;

            if (settings.TryRename(bundle.OwnerId, bundle.GroupName, bundle.Name, out var renamed))
                bundle.EffectiveShortcut = renamed;

            if (string.IsNullOrEmpty(bundle.GroupName))
            {
                AddClaim(rootClaims, bundle.EffectiveShortcut, bundle);
                foreach (var alias in bundle.Aliases)
                {
                    if (string.Equals(alias, bundle.EffectiveShortcut, StringComparison.OrdinalIgnoreCase))
                        continue;
                    AddClaim(rootClaims, alias, bundle);
                }
            }
            else
            {
                ClaimGrouped(groupClaims, bundle.GroupName, bundle.EffectiveShortcut, bundle);
                if (!string.IsNullOrEmpty(bundle.GroupShortHand))
                    ClaimGrouped(groupClaims, bundle.GroupShortHand!, bundle.EffectiveShortcut, bundle);
                foreach (var alias in bundle.Aliases)
                {
                    if (string.Equals(alias, bundle.EffectiveShortcut, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ClaimGrouped(groupClaims, bundle.GroupName, alias, bundle);
                    if (!string.IsNullOrEmpty(bundle.GroupShortHand))
                        ClaimGrouped(groupClaims, bundle.GroupShortHand!, alias, bundle);
                }
            }
        }

        var root = new Dictionary<string, RouteSlot>(TokenComparer);
        foreach (var kv in rootClaims)
        {
            root[kv.Key] = ResolveSlot(kv.Key, subcommand: null, kv.Value, settings, diagnostics);
        }

        ReserveFallbackRoot(root, settings.FallbackRoot, diagnostics);

        var groups = new Dictionary<string, Dictionary<string, RouteSlot>>(TokenComparer);
        foreach (var groupKv in groupClaims)
        {
            var inner = new Dictionary<string, RouteSlot>(TokenComparer);
            foreach (var subKv in groupKv.Value)
                inner[subKv.Key] = ResolveSlot(groupKv.Key, subKv.Key, subKv.Value, settings, diagnostics);
            groups[groupKv.Key] = inner;
        }

        foreach (var bundle in live)
        {
            string? published = PickPublishedShortcut(bundle, root, groups);
            bundle.ShortcutsEnabled = published != null;
            if (published != null) bundle.EffectiveShortcut = published;
        }

        live.Sort((a, b) => string.Compare(a.DisplayPath, b.DisplayPath, StringComparison.OrdinalIgnoreCase));
        diagnostics.Sort(StringComparer.OrdinalIgnoreCase);

        table = new CommandRouteTable(settings.FallbackRoot, root, groups, qualified, live, diagnostics);
        return true;
    }

    private static List<CommandBundle> CollapseLive(List<CommandBundle> bundles)
    {
        // Last registration of a qualified identity wins so a Swap can install before the
        // previous handle is disposed, and the old handle can only drop its own rows.
        var last = new Dictionary<string, CommandBundle>(TokenComparer);
        var order = new List<string>();
        foreach (var bundle in bundles)
        {
            string id = QualifiedIdentity(bundle);
            if (!last.ContainsKey(id)) order.Add(id);
            last[id] = bundle;
        }
        var live = new List<CommandBundle>(order.Count);
        foreach (var id in order) live.Add(last[id]);
        return live;
    }

    internal static string QualifiedIdentity(CommandBundle bundle) =>
        bundle.OwnerId + "\n" + bundle.GroupName + "\n" + bundle.Name;

    private static void AddClaim(Dictionary<string, List<CommandBundle>> map, string token, CommandBundle bundle)
    {
        if (string.IsNullOrEmpty(token)) return;
        if (!map.TryGetValue(token, out var list))
        {
            list = new List<CommandBundle>();
            map[token] = list;
        }
        list.Add(bundle);
    }

    private static void ClaimGrouped(
        Dictionary<string, Dictionary<string, List<CommandBundle>>> groups,
        string group,
        string name,
        CommandBundle bundle)
    {
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(name)) return;
        if (!groups.TryGetValue(group, out var inner))
        {
            inner = new Dictionary<string, List<CommandBundle>>(TokenComparer);
            groups[group] = inner;
        }
        AddClaim(inner, name, bundle);
    }

    private static RouteSlot ResolveSlot(
        string token,
        string? subcommand,
        List<CommandBundle> claims,
        CommandRoutingSettings settings,
        List<string> diagnostics)
    {
        var unique = DistinctOwners(claims);
        if (subcommand == null && settings.TryAssign(token, out var assigned))
        {
            var kept = new List<CommandBundle>();
            foreach (var b in claims)
            {
                if (string.Equals(b.OwnerId, assigned, StringComparison.OrdinalIgnoreCase))
                    kept.Add(b);
            }
            if (kept.Count > 0)
            {
                unique = DistinctOwners(kept);
                claims = kept;
            }
        }

        if (unique.Count > 1)
        {
            unique.Sort(StringComparer.OrdinalIgnoreCase);
            string path = subcommand == null ? token : token + " " + subcommand;
            var qualified = new List<string>();
            foreach (var b in DistinctBundles(claims))
                qualified.Add(FormatQualified(settings.FallbackRoot, b));
            qualified.Sort(StringComparer.OrdinalIgnoreCase);
            diagnostics.Add(
                $"Shortcut '{path}' disabled for {string.Join(", ", unique)}; use {string.Join(" or ", qualified)}.");
            return RouteSlot.Conflict(unique, qualified);
        }

        return RouteSlot.Of(claims[claims.Count - 1], settings.FallbackRoot);
    }

    internal static string FormatQualified(string fallbackRoot, CommandBundle bundle) =>
        CommandRegistry.Prefix + fallbackRoot + " " + bundle.QualifiedPath;

    private static void ReserveFallbackRoot(
        Dictionary<string, RouteSlot> root,
        string fallbackRoot,
        List<string> diagnostics)
    {
        if (root.TryGetValue(fallbackRoot, out var existing) && existing.Bundle != null)
        {
            diagnostics.Add(
                $"Shortcut '{fallbackRoot}' from {existing.Bundle.OwnerId} is reserved as the qualified fallback root.");
        }
        root[fallbackRoot] = RouteSlot.ReservedRoot();
    }

    private static List<string> DistinctOwners(List<CommandBundle> claims)
    {
        var ids = new List<string>();
        foreach (var b in claims)
        {
            bool seen = false;
            foreach (var id in ids)
            {
                if (string.Equals(id, b.OwnerId, StringComparison.OrdinalIgnoreCase))
                {
                    seen = true;
                    break;
                }
            }
            if (!seen) ids.Add(b.OwnerId);
        }
        return ids;
    }

    private static string? PickPublishedShortcut(
        CommandBundle bundle,
        Dictionary<string, RouteSlot> root,
        Dictionary<string, Dictionary<string, RouteSlot>> groups)
    {
        if (string.IsNullOrEmpty(bundle.GroupName))
        {
            if (SlotOwned(root, bundle.EffectiveShortcut, bundle))
                return bundle.EffectiveShortcut;
            foreach (var alias in bundle.Aliases)
            {
                if (SlotOwned(root, alias, bundle))
                    return alias;
            }
            return null;
        }

        if (groups.TryGetValue(bundle.GroupName, out var cmds))
        {
            if (SlotOwned(cmds, bundle.EffectiveShortcut, bundle))
                return bundle.EffectiveShortcut;
            foreach (var alias in bundle.Aliases)
            {
                if (SlotOwned(cmds, alias, bundle))
                    return alias;
            }
        }
        return null;
    }

    private static bool SlotOwned(Dictionary<string, RouteSlot> map, string token, CommandBundle bundle) =>
        !string.IsNullOrEmpty(token)
        && map.TryGetValue(token, out var slot)
        && slot.Bundle == bundle;

    private static List<CommandBundle> DistinctBundles(List<CommandBundle> claims)
    {
        var list = new List<CommandBundle>();
        foreach (var b in claims)
        {
            bool seen = false;
            foreach (var e in list)
            {
                if (ReferenceEquals(e, b)) { seen = true; break; }
            }
            if (!seen) list.Add(b);
        }
        return list;
    }
}

internal sealed class RouteSlot
{
    public readonly CommandBundle? Bundle;
    public readonly bool Ambiguous;
    public readonly bool Reserved;
    public readonly IReadOnlyList<string> OwnerIds;
    public readonly IReadOnlyList<string> QualifiedPaths;

    private RouteSlot(
        CommandBundle? bundle,
        bool ambiguous,
        bool reserved,
        IReadOnlyList<string> ownerIds,
        IReadOnlyList<string> qualifiedPaths)
    {
        Bundle = bundle;
        Ambiguous = ambiguous;
        Reserved = reserved;
        OwnerIds = ownerIds;
        QualifiedPaths = qualifiedPaths;
    }

    public static RouteSlot Of(CommandBundle bundle, string fallbackRoot) =>
        new(bundle, false, false, new[] { bundle.OwnerId }, new[] { CommandRouteTable.FormatQualified(fallbackRoot, bundle) });

    public static RouteSlot Conflict(List<string> owners, List<string> qualified) =>
        new(null, true, false, owners, qualified);

    public static RouteSlot ReservedRoot() =>
        new(null, false, true, Array.Empty<string>(), Array.Empty<string>());
}
