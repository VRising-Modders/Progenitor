using System;
using System.Collections.Generic;

namespace Progenitor.Install;

/// <summary>DEPS1: the readback's target word for one install half.</summary>
internal static class InstallTargetNames
{
    public static string Of(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => "localServer",
        InstallTarget.Both => "both",
        _ => "client",
    };

    /// <summary>The half a narrowed dependency lands on, in words, for the plan reason.</summary>
    public static string Half(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => "local server",
        InstallTarget.Both => "client and the local server",
        _ => "client",
    };
}

/// <summary>
/// DEPS1: one package of an ordered install — a closure dependency, or the root listing the player
/// clicked. Each member carries its own plan, which was decided for its own target: a dependency
/// whose <c>[BepInProcess]</c> does not run on the chosen target is planned for the half it does run
/// on, and <see cref="TargetReason"/> says so.
/// </summary>
internal sealed class InstallPlanMember
{
    public InstallPlanMember(
        string package,
        string version,
        InstallPlan plan,
        bool isRoot,
        string satisfiedBy,
        string versionRule,
        string targetReason)
    {
        Package = package ?? "";
        Version = version ?? "";
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        IsRoot = isRoot;
        SatisfiedBy = satisfiedBy ?? "";
        VersionRule = versionRule ?? "";
        TargetReason = targetReason ?? "";
    }

    public string Package { get; }
    public string Version { get; }
    public InstallPlan Plan { get; }
    public bool IsRoot { get; }

    /// <summary><c>closure</c> for a member this install writes; the root's is <c>root</c>.</summary>
    public string SatisfiedBy { get; }

    public string VersionRule { get; }

    /// <summary>Empty unless this member was planned for a different half than the request asked for.</summary>
    public string TargetReason { get; }

    public InstallTarget Target => Plan.Target;
}

/// <summary>
/// DEPS1: one listing's plan together with the plans of every package it needs, in the order they
/// have to be written (dependencies first, the root last). Refused as a whole: if any member is
/// refused, <see cref="Ok"/> is false and nothing is executed.
/// </summary>
internal sealed class InstallPlanSet
{
    private InstallPlanSet(
        bool ok,
        string? error,
        InstallPlan? root,
        IReadOnlyList<InstallPlanMember> members,
        IReadOnlyList<ClosureMember> closure,
        InstallTarget target)
    {
        Ok = ok;
        Error = error;
        Root = root;
        Members = members ?? Array.Empty<InstallPlanMember>();
        Closure = closure ?? Array.Empty<ClosureMember>();
        Target = target;
    }

    public bool Ok { get; }

    /// <summary>The one refusal sentence the confirmation shows. Never null when <see cref="Ok"/> is false.</summary>
    public string? Error { get; }

    /// <summary>The root listing's plan, built with its closure members counted as satisfied.</summary>
    public InstallPlan? Root { get; }

    /// <summary>Dependencies first, then the root: the order <see cref="InstallCatalogService.ExecuteWithDependencies"/> writes.</summary>
    public IReadOnlyList<InstallPlanMember> Members { get; }

    /// <summary>Every dependency the closure found, including the ones already on the target.</summary>
    public IReadOnlyList<ClosureMember> Closure { get; }

    /// <summary>The target the player chose for the root package.</summary>
    public InstallTarget Target { get; }

    /// <summary>How many packages besides the root this install writes: the readback's <c>closureCount</c>.</summary>
    public int ClosureCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Members.Count; i++)
                if (!Members[i].IsRoot) n++;
            return n;
        }
    }

    public static InstallPlanSet Refuse(string reason) =>
        new(false, reason, null, Array.Empty<InstallPlanMember>(), Array.Empty<ClosureMember>(), InstallTarget.Client);

    public static InstallPlanSet Planned(
        InstallPlan root,
        IReadOnlyList<InstallPlanMember> members,
        IReadOnlyList<ClosureMember> closure,
        InstallTarget target) =>
        new(true, null, root, members, closure, target);

    /// <summary>
    /// The readback rows for <c>plan.dependencies[{package, version, satisfiedBy, decision, reason}]</c>.
    /// One row per closure member, in closure order: a member already on the target has no plan, so
    /// its decision is null and its reason says why it was not planned again.
    /// </summary>
    public List<Dictionary<string, object?>> DependencyRows()
    {
        var rows = new List<Dictionary<string, object?>>(Closure.Count);
        for (int i = 0; i < Closure.Count; i++)
        {
            var member = Closure[i];
            var planned = PlannedMember(member.Package);
            rows.Add(new Dictionary<string, object?>
            {
                ["package"] = member.Package,
                ["version"] = planned?.Version ?? member.Version,
                ["satisfiedBy"] = member.SatisfiedBy,
                ["decision"] = planned?.Plan.Decision.ToString(),
                ["reason"] = planned != null
                    ? (planned.TargetReason.Length > 0 ? planned.TargetReason + "; " + planned.Plan.Reason : planned.Plan.Reason)
                    : InstallReasons.ClosureAlreadySatisfied,
                ["target"] = planned != null ? InstallTargetNames.Of(planned.Target) : null,
                ["versionRule"] = member.VersionRule,
            });
        }

        return rows;
    }

    /// <summary>The member plan for one package, or null when that package is not being written.</summary>
    public InstallPlanMember? PlannedMember(string package)
    {
        for (int i = 0; i < Members.Count; i++)
        {
            if (Members[i].IsRoot) continue;
            if (string.Equals(Members[i].Package, package, StringComparison.OrdinalIgnoreCase)) return Members[i];
        }

        return null;
    }
}

/// <summary>DEPS1: one member of an executed set and the file it wrote.</summary>
internal sealed class ExecuteMemberResult
{
    public ExecuteMemberResult(
        string package,
        string version,
        string? path,
        bool hotLoaded,
        string? serverPendingPath,
        bool isRoot)
    {
        Package = package ?? "";
        Version = version ?? "";
        Path = path;
        HotLoaded = hotLoaded;
        ServerPendingPath = serverPendingPath;
        IsRoot = isRoot;
    }

    public string Package { get; }
    public string Version { get; }
    public string? Path { get; }
    public bool HotLoaded { get; }
    public string? ServerPendingPath { get; }
    public bool IsRoot { get; }

    public Dictionary<string, object?> ToRow() => new()
    {
        ["package"] = Package,
        ["version"] = Version,
        ["path"] = Path,
        ["hotLoaded"] = HotLoaded,
        ["serverPendingPath"] = ServerPendingPath,
        ["root"] = IsRoot,
    };
}

/// <summary>
/// DEPS1: the result of writing one ordered install. A failure stops the set at the member that
/// failed and names it; every member <em>before</em> it is a real install that stays on disk and in
/// the root's ledger, so <see cref="Members"/> is what was written, not what was intended.
/// </summary>
internal sealed class ExecuteSetResult
{
    private ExecuteSetResult(bool ok, string? error, string? failedPackage, IReadOnlyList<ExecuteMemberResult> members)
    {
        Ok = ok;
        Error = error;
        FailedPackage = failedPackage;
        Members = members ?? Array.Empty<ExecuteMemberResult>();
    }

    public bool Ok { get; }
    public string? Error { get; }

    /// <summary>The package the set stopped at, or null when every member was written.</summary>
    public string? FailedPackage { get; }

    /// <summary>The members that were written, in the order they were written.</summary>
    public IReadOnlyList<ExecuteMemberResult> Members { get; }

    public static ExecuteSetResult Refuse(string reason) =>
        new(false, reason, null, Array.Empty<ExecuteMemberResult>());

    public static ExecuteSetResult Failed(string package, string reason, IReadOnlyList<ExecuteMemberResult> written) =>
        new(false, InstallReasons.ClosureMemberFailed(package, reason), package, written);

    public static ExecuteSetResult Written(IReadOnlyList<ExecuteMemberResult> written) =>
        new(true, null, null, written);

    /// <summary>The readback rows for <c>result.members[{package, version, path, hotLoaded, serverPendingPath}]</c>.</summary>
    public List<Dictionary<string, object?>> MemberRows()
    {
        var rows = new List<Dictionary<string, object?>>(Members.Count);
        for (int i = 0; i < Members.Count; i++) rows.Add(Members[i].ToRow());
        return rows;
    }
}
