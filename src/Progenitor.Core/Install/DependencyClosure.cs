using System;
using System.Collections.Generic;

namespace Progenitor.Install;

/// <summary>
/// DEPS1: one package the root listing needs, and why it is in the closure. Plain data — the
/// resolver never downloads, plans or writes anything.
/// </summary>
internal sealed class ClosureMember
{
    public ClosureMember(
        CatalogEntry? entry,
        string package,
        string version,
        string requestedVersion,
        string why,
        string satisfiedBy,
        string versionRule)
    {
        Entry = entry;
        Package = package ?? "";
        Version = version ?? "";
        RequestedVersion = requestedVersion ?? "";
        Why = why ?? "";
        SatisfiedBy = satisfiedBy ?? "";
        VersionRule = versionRule ?? "";
    }

    /// <summary>
    /// The index listing this member installs, or null for a member that is already on the target
    /// and that the loaded index does not happen to carry.
    /// </summary>
    public CatalogEntry? Entry { get; }

    /// <summary><c>Namespace-Name</c> — the package, without the version.</summary>
    public string Package { get; }

    /// <summary>The version that will be installed: the index listing's own version.</summary>
    public string Version { get; }

    /// <summary>The version the depending listing's dependency string named.</summary>
    public string RequestedVersion { get; }

    /// <summary><see cref="PackageDependencyClosure.WhyMissing"/> | <see cref="PackageDependencyClosure.WhyPresent"/>.</summary>
    public string Why { get; }

    /// <summary><c>installed</c> | <c>loaded</c> | <c>closure</c>: the readback's <c>satisfiedBy</c>.</summary>
    public string SatisfiedBy { get; }

    /// <summary>Which version rule picked <see cref="Version"/>, in words.</summary>
    public string VersionRule { get; }

    /// <summary>True when this member still has to be downloaded, planned and written.</summary>
    public bool IsMissing => string.Equals(Why, PackageDependencyClosure.WhyMissing, StringComparison.Ordinal);
}

/// <summary>DEPS1: the ordered closure of one listing's Thunderstore dependencies, or one named refusal.</summary>
internal sealed class DependencyClosureResult
{
    private DependencyClosureResult(bool ok, string? refusal, IReadOnlyList<ClosureMember> members)
    {
        Ok = ok;
        Refusal = refusal;
        Members = members ?? Array.Empty<ClosureMember>();
    }

    public bool Ok { get; }

    /// <summary>Why nothing was resolved. Never null when <see cref="Ok"/> is false.</summary>
    public string? Refusal { get; }

    /// <summary>
    /// Dependencies first: a member never appears before a package it itself depends on, and the
    /// root listing is not a member of its own closure.
    /// </summary>
    public IReadOnlyList<ClosureMember> Members { get; }

    /// <summary>The members that still have to be installed, in the same order.</summary>
    public IReadOnlyList<ClosureMember> Missing
    {
        get
        {
            var list = new List<ClosureMember>();
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].IsMissing) list.Add(Members[i]);
            return list;
        }
    }

    /// <summary>How many packages this install writes besides the root: the readback's <c>closureCount</c>.</summary>
    public int MissingCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].IsMissing) n++;
            return n;
        }
    }

    /// <summary>Every member's package name, for <see cref="OrdinaryDependencyView.ClosurePackages"/>.</summary>
    public IReadOnlyList<string> Packages
    {
        get
        {
            var list = new List<string>(Members.Count);
            for (int i = 0; i < Members.Count; i++) list.Add(Members[i].Package);
            return list;
        }
    }

    public static DependencyClosureResult Resolved(IReadOnlyList<ClosureMember> members) =>
        new(true, null, members);

    public static DependencyClosureResult Refuse(string reason) =>
        new(false, reason, Array.Empty<ClosureMember>());
}

/// <summary>
/// DEPS1: resolve, from the loaded index alone, every Thunderstore package one listing needs before
/// it can load — recursively, dependencies first.
///
/// <para>Each <c>&lt;owner&gt;-&lt;name&gt;-&lt;version&gt;</c> dependency string is split by the
/// INSTORD1 rule (<see cref="OrdinaryDependencies.TrySplit"/>) and looked up in the index by its
/// <c>&lt;owner&gt;-&lt;name&gt;</c> key. The version installed is the index listing's own: the
/// <see cref="RuleListed"/> version when it equals the one the dependency string named, otherwise
/// <see cref="RuleNewest"/> — the newest the index carries, which must still be at least the listed
/// one. An index whose newest copy is <em>older</em> than the listed version is refused rather than
/// downgraded.</para>
///
/// <para>Two kinds of package are never members. The loader pack
/// (<see cref="LoaderPackages.IsLoaderPackage"/> / <see cref="OrdinaryDependencies.IsBepInExPack"/>)
/// is what is already running this process and Progenitor never writes <c>BepInEx/core</c>. A
/// dependency that is already satisfied on the target — a Progenitor-ledgered install of that
/// package, or a loaded plugin whose GUID a prior inspection of that package recorded — is listed
/// as <see cref="WhyPresent"/> and is not walked, downloaded or planned again.</para>
///
/// <para>A cycle, an unknown package, a malformed dependency string, a graph deeper than
/// <see cref="MaxDepth"/> or wider than <see cref="MaxMembers"/> all refuse with a named reason and
/// resolve nothing: this type never returns a partial closure.</para>
/// </summary>
internal static class PackageDependencyClosure
{
    /// <summary>How deep one closure walks before it refuses.</summary>
    public const int MaxDepth = 16;

    /// <summary>How many packages one install may place besides the root.</summary>
    public const int MaxMembers = 64;

    public const string WhyMissing = "missing";
    public const string WhyPresent = "present";

    /// <summary>Readback <c>satisfiedBy</c>: a Progenitor-ledgered install already on the target.</summary>
    public const string BySatisfiedInstalled = "installed";

    /// <summary>Readback <c>satisfiedBy</c>: a plugin the target already has loaded.</summary>
    public const string BySatisfiedLoaded = "loaded";

    /// <summary>Readback <c>satisfiedBy</c>: this install's own closure writes it.</summary>
    public const string BySatisfiedClosure = "closure";

    /// <summary>The index carries exactly the version the dependency string named.</summary>
    public const string RuleListed = "the listed version";

    /// <summary>The index's newest version, which is at least the listed one.</summary>
    public const string RuleNewest = "the newest version in the index, which is at least the listed one";

    /// <summary>This member is already on the target, so no version was chosen for it.</summary>
    public const string RuleAlreadyThere = "already on the target; no version was chosen";

    /// <summary>
    /// Resolve <paramref name="root"/>'s closure. <paramref name="index"/> answers with the loaded
    /// index listing for a <c>&lt;owner&gt;-&lt;name&gt;</c> key, or null when the index has none;
    /// <paramref name="view"/> is what may be consulted about the target (INSTORD1 rules).
    /// </summary>
    public static DependencyClosureResult Resolve(
        CatalogEntry root,
        Func<string, CatalogEntry?> index,
        OrdinaryDependencyView? view)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        if (index == null) throw new ArgumentNullException(nameof(index));
        view ??= new OrdinaryDependencyView();

        var members = new List<ClosureMember>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        string? refusal = null;

        string rootKey = (root.Key ?? "").Trim();
        if (rootKey.Length > 0)
        {
            onStack.Add(rootKey);
            chain.Add(rootKey);
        }

        Walk(root, 0);
        return refusal != null ? DependencyClosureResult.Refuse(refusal) : DependencyClosureResult.Resolved(members);

        bool Walk(CatalogEntry entry, int depth)
        {
            var dependencies = entry.ForeignDependencies;
            for (int i = 0; i < dependencies.Count; i++)
            {
                if (!OrdinaryDependencies.TrySplit(dependencies[i], out string ns, out string name, out string wanted))
                {
                    refusal = InstallReasons.ClosureMalformedDependency(dependencies[i]);
                    return false;
                }

                string package = ns + "-" + name;

                // The loader is what is running this process; Progenitor never writes BepInEx/core.
                if (OrdinaryDependencies.IsBepInExPack(ns, name) || LoaderPackages.IsLoaderPackage(ns, name, package))
                    continue;

                if (onStack.Contains(package))
                {
                    refusal = InstallReasons.ClosureCycle(Chain(package));
                    return false;
                }

                if (done.Contains(package)) continue;

                // Already on the target: listed for the confirmation text, never walked or planned.
                if (OrdinaryDependencies.SatisfiedOn(view, ns, name, wanted, out string satisfiedBy)
                    && !string.Equals(satisfiedBy, OrdinaryDependencies.BySatisfiedClosure, StringComparison.Ordinal))
                {
                    done.Add(package);
                    members.Add(new ClosureMember(
                        index(package),
                        package,
                        wanted,
                        wanted,
                        WhyPresent,
                        string.Equals(satisfiedBy, OrdinaryDependencies.BySatisfiedLoaded, StringComparison.Ordinal)
                            ? BySatisfiedLoaded
                            : BySatisfiedInstalled,
                        RuleAlreadyThere));
                    continue;
                }

                var listing = index(package);
                if (listing == null)
                {
                    refusal = InstallReasons.ClosureUnknownPackage(package);
                    return false;
                }

                string have = (listing.Manifest.Version ?? "").Trim();
                int compared = InstalledState.CompareVersions(have, wanted);
                string rule;
                if (compared == 0) rule = RuleListed;
                else if (compared > 0) rule = RuleNewest;
                else
                {
                    refusal = InstallReasons.ClosureOlderThanListed(package, have, wanted);
                    return false;
                }

                if (depth + 1 > MaxDepth)
                {
                    refusal = InstallReasons.ClosureTooDeep(package);
                    return false;
                }

                onStack.Add(package);
                chain.Add(package);
                bool walked = Walk(listing, depth + 1);
                chain.RemoveAt(chain.Count - 1);
                onStack.Remove(package);
                if (!walked) return false;

                if (members.Count >= MaxMembers)
                {
                    refusal = InstallReasons.ClosureTooMany(package);
                    return false;
                }

                // Post-order: this member's own dependencies are already in the list before it.
                done.Add(package);
                members.Add(new ClosureMember(listing, package, have, wanted, WhyMissing, BySatisfiedClosure, rule));
            }

            return true;
        }

        string Chain(string repeated)
        {
            var parts = new List<string>(chain.Count + 1);
            for (int i = 0; i < chain.Count; i++) parts.Add(chain[i]);
            parts.Add(repeated);
            return string.Join(" → ", parts);
        }
    }
}
