using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Install;

/// <summary>
/// DEPS1: the y/n confirmation body for one install, as plain sentences. Pure: it reads a plan (and,
/// with dependencies, the plan set) and returns text — it never downloads, plans, writes or touches a
/// game folder, so the pane and the bridge can both show exactly the same words.
///
/// <para>Three parts, in order. What it is — <c>Client mod by Modix, version 3.1.0.</c> from the
/// plan's target, the listing's owner and the listing's version. When it loads — <c>Loads at the next
/// restart of V Rising.</c> for a restart decision, <c>Loads in seconds.</c> for an admitted hot load.
/// Then one sentence per dependency: <c>Needs ExampleCommandFramework 0.11.0, which will be installed
/// too.</c> for a package this install writes, <c>… which is installed.</c> for one that is already on
/// the target.</para>
/// </summary>
internal static class InstallSummary
{
    /// <summary>The client's own process, as the sentence names it.</summary>
    public const string ClientName = "V Rising";

    /// <summary>The local server, as the sentence names it.</summary>
    public const string ServerName = "the local server";

    /// <summary>An admitted hot load: no restart is owed.</summary>
    public const string LoadsInSeconds = "Loads in seconds.";

    /// <summary>A plan that cannot be installed has no confirmation body.</summary>
    public const string Blocked = "This cannot be installed.";

    /// <summary>
    /// INSTALLUX1 item 1: the words shown while the download and inspection run behind the already
    /// open confirmation. Nothing about the package's contents is known yet, so the body says only
    /// what the listing itself carries — side, owner, version — and then that it is checking.
    /// </summary>
    public const string Checking = "Checking the package…";

    /// <inheritdoc cref="Checking"/>
    public static string DescribeChecking(string sideWord, string? owner, string? version) =>
        HeadSentence(sideWord, owner, version) + " " + Checking;

    /// <summary>The confirmation body for one listing planned together with its dependencies.</summary>
    public static string Describe(InstallPlanSet? set) => Describe(set, null);

    /// <summary>
    /// <inheritdoc cref="Describe(InstallPlanSet?)"/>
    /// <para>INSTALLUX1 item 1: <paramref name="sideWord"/> overrides the head sentence's side with
    /// the <em>listing's</em> side (Server / Client / Both, from <c>PackageSideDerivation</c> or the
    /// inspector) rather than the target the install writes to. The two used to be the same thing
    /// because the player picked the target; now the setting picks it, and telling a player that
    /// BossRoster is a "Client mod" because their setting sent the server mod to their client would
    /// be false.</para>
    /// </summary>
    public static string Describe(InstallPlanSet? set, string? sideWord) => Describe(set, sideWord, null);

    /// <summary>
    /// <inheritdoc cref="Describe(InstallPlanSet?, string?)"/>
    /// <para>CONFIRMTARGET1: with <paramref name="server"/>, the restart sentence names that server
    /// and each dependency sentence says where it was looked for (<see cref="WhereFound"/>).</para>
    /// </summary>
    public static string Describe(InstallPlanSet? set, string? sideWord, ServerIdentity? server)
    {
        if (set == null || !set.Ok || set.Root == null) return set?.Error ?? Blocked;
        var sentences = new List<string>();
        Head(set.Root, sentences, sideWord, server);
        string where = WhereFound(set.Root.Target, server);
        for (int i = 0; i < set.Closure.Count; i++)
        {
            var member = set.Closure[i];
            sentences.Add(Needs(member.Package, VersionOf(set, member), member.IsMissing, where));
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// The confirmation body for a plan with no dependency closure behind it. The plan knows which
    /// Thunderstore packages it needs and how each is satisfied (INSTORD1), but not their versions, so
    /// those sentences name the package alone.
    /// </summary>
    public static string Describe(InstallPlan? plan) => Describe(plan, null);

    /// <inheritdoc cref="Describe(InstallPlanSet?, string?)"/>
    public static string Describe(InstallPlan? plan, string? sideWord) => Describe(plan, sideWord, null);

    /// <inheritdoc cref="Describe(InstallPlanSet?, string?, ServerIdentity?)"/>
    public static string Describe(InstallPlan? plan, string? sideWord, ServerIdentity? server)
    {
        if (plan == null || plan.Decision == InstallDecision.UnknownBlocked) return plan?.Reason ?? Blocked;
        var sentences = new List<string>();
        Head(plan, sentences, sideWord, server);
        string where = WhereFound(plan.Target, server);
        for (int i = 0; i < plan.OrdinaryDependencies.Count; i++)
        {
            var dependency = plan.OrdinaryDependencies[i];
            // The loader pack is not something the player installs; it is what is already running.
            if (string.Equals(dependency.SatisfiedBy, OrdinaryDependencies.BySatisfiedBepInEx, StringComparison.Ordinal))
                continue;
            sentences.Add(Needs(
                dependency.Package,
                "",
                !dependency.Satisfied
                    || string.Equals(dependency.SatisfiedBy, OrdinaryDependencies.BySatisfiedClosure, StringComparison.Ordinal),
                where));
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// <c>Needs ExampleCommandFramework 0.11.0, which will be installed too.</c> —
    /// <paramref name="package"/> is a <c>&lt;owner&gt;-&lt;name&gt;</c> key and only its name is
    /// spoken, because the owner is not what the player recognizes. An empty version is left out
    /// rather than printed as <c>0</c>.
    /// </summary>
    public static string Needs(string package, string version, bool willBeInstalled) =>
        Needs(package, version, willBeInstalled, "");

    /// <summary>
    /// CONFIRMTARGET1: the same sentence saying where the dependency was looked for —
    /// <c>…, which is installed on that server.</c> / <c>…, which is not on that server yet, so it
    /// will be installed alongside.</c>. An empty <paramref name="where"/> is the sentence above.
    /// A dependency that is missing and cannot be installed never reaches here: the plan refuses
    /// and the confirmation shows that refusal instead.
    /// </summary>
    public static string Needs(string package, string version, bool willBeInstalled, string? where)
    {
        var sb = new StringBuilder("Needs ");
        sb.Append(NameOf(package));
        string v = (version ?? "").Trim();
        if (v.Length > 0) sb.Append(' ').Append(v);
        string w = (where ?? "").Trim();
        if (w.Length == 0)
            sb.Append(willBeInstalled ? ", which will be installed too." : ", which is installed.");
        else if (willBeInstalled)
            sb.Append(", which is not ").Append(w).Append(" yet, so it will be installed alongside.");
        else
            sb.Append(", which is installed ").Append(w).Append('.');
        return sb.ToString();
    }

    /// <summary>Where a server install's dependency was looked for: the server the plan writes to.</summary>
    public const string OnThatServer = "on that server";

    /// <summary>A Client-and-server install looks on both halves and is satisfied by either.</summary>
    public const string OnThisGameOrThatServer = "on this game or that server";

    /// <summary>
    /// CONFIRMTARGET1: the place a plan's dependency sentences name — nothing for a client install
    /// (or a caller that named no server), <see cref="OnThatServer"/> for the local server, and
    /// <see cref="OnThisGameOrThatServer"/> for both (the dependency view is the union of the two).
    /// </summary>
    public static string WhereFound(InstallTarget target, ServerIdentity? server)
    {
        if (server == null) return "";
        return target switch
        {
            InstallTarget.LocalServer => OnThatServer,
            InstallTarget.Both => OnThisGameOrThatServer,
            _ => "",
        };
    }

    /// <summary><c>&lt;owner&gt;-&lt;name&gt;</c> → <c>&lt;name&gt;</c>. A key with no owner is spoken whole.</summary>
    public static string NameOf(string package)
    {
        string p = (package ?? "").Trim();
        int dash = p.IndexOf('-');
        return dash > 0 && dash + 1 < p.Length ? p.Substring(dash + 1) : p;
    }

    /// <summary>
    /// INSTALLUX1 item 1: the head sentence's side word taken from the LISTING's side rather than
    /// the target. An unknown side falls back to <c>Client</c>, the half this process can always
    /// write and the one the install lands on when nothing else is known.
    ///
    /// <para>POLISH3 item D: a both-sided listing says <c>Client and server</c>, the wording
    /// INSTALLUX2 gave the confirmation box, rather than <c>Both</c>. The pane used to override this
    /// answer on its own (<c>InstallModsPane.PromptSideWord</c>) because it did not own this file,
    /// which left two spellings of the same fact and only one of them on screen. The two
    /// <see cref="SideWord(InstallTarget)"/> overloads now agree for the same side.</para>
    /// </summary>
    public static string SideWord(PackageSide side) => side switch
    {
        PackageSide.Server => "Server",
        PackageSide.Both => BothSideWord,
        _ => "Client",
    };

    /// <summary>INSTALLUX2 item 3 / POLISH3 item D: how a both-sided listing names its side.</summary>
    public const string BothSideWord = "Client and server";

    /// <summary><c>Client</c> | <c>Server</c> | <c>Client and server</c> for the head sentence.</summary>
    public static string SideWord(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => "Server",
        InstallTarget.Both => BothSideWord,
        _ => "Client",
    };

    /// <summary>
    /// <c>Loads at the next restart of V Rising.</c> / <c>… of the local server.</c> /
    /// <c>… of V Rising and the local server.</c>, or <see cref="LoadsInSeconds"/> for an apply-now
    /// plan. A <see cref="InstallTarget.Both"/> plan whose halves disagree (HOT4) says both things.
    /// </summary>
    public static string LoadsWhen(InstallPlan plan) => LoadsWhen(plan, null);

    /// <summary>
    /// <inheritdoc cref="LoadsWhen(InstallPlan)"/>
    /// CONFIRMTARGET1: with <paramref name="server"/>, the server is named
    /// (<c>… of Progenitor Forms Test.</c>); unknown names keep <see cref="ServerName"/>.
    /// </summary>
    public static string LoadsWhen(InstallPlan plan, ServerIdentity? server)
    {
        string serverName = server?.ShortName ?? ServerName;
        if (plan.Decision == InstallDecision.ApplyNow)
        {
            if (!plan.IsMixed) return LoadsInSeconds;
            return "Loads in seconds on the client, and at the next restart of " + serverName + ".";
        }

        return "Loads at the next restart of " + RestartSubject(plan.Decision, plan.Target, serverName) + ".";
    }

    private static string RestartSubject(InstallDecision decision, InstallTarget target, string serverName)
    {
        if (decision == InstallDecision.RestartServerRequired) return serverName;
        if (decision == InstallDecision.RestartBothRequired) return ClientName + " and " + serverName;
        if (decision == InstallDecision.RestartClientRequired) return ClientName;
        return target switch
        {
            InstallTarget.LocalServer => serverName,
            InstallTarget.Both => ClientName + " and " + serverName,
            _ => ClientName,
        };
    }

    private static void Head(InstallPlan plan, List<string> sentences, string? sideWord = null, ServerIdentity? server = null)
    {
        string owner = plan.Entry.Owner ?? plan.Entry.Author ?? plan.Entry.Manifest.Namespace ?? "";
        string version = (plan.Entry.Manifest.Version ?? "").Trim();
        sentences.Add(HeadSentence(
            string.IsNullOrWhiteSpace(sideWord) ? SideWord(plan.Target) : sideWord!, owner, version));
        sentences.Add(LoadsWhen(plan, server));
    }

    /// <summary><c>Server mod by Tovan, version 2.1.5.</c> — the first sentence of every body.</summary>
    public static string HeadSentence(string sideWord, string? owner, string? version)
    {
        var sb = new StringBuilder(string.IsNullOrWhiteSpace(sideWord) ? "Mod" : sideWord!.Trim()).Append(" mod");
        string o = (owner ?? "").Trim();
        string v = (version ?? "").Trim();
        if (o.Length > 0) sb.Append(" by ").Append(o);
        if (v.Length > 0) sb.Append(", version ").Append(v);
        return sb.Append('.').ToString();
    }

    /// <summary>The version a member will be installed at: the planned member's, else the closure's own.</summary>
    private static string VersionOf(InstallPlanSet set, ClosureMember member)
    {
        var planned = set.PlannedMember(member.Package);
        return planned != null ? planned.Version : member.Version;
    }
}
