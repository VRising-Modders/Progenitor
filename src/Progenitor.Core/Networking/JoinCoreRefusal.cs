using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Progenitor.Networking;

/// <summary>What kind of Core requirement refused the join.</summary>
internal enum JoinCoreRequirementKind
{
    /// <summary>A capability only a newer Progenitor has: the fix is updating Progenitor on the client.</summary>
    Capability,

    /// <summary>The client accepts fewer custom items than the server adds: the fix is a config value.</summary>
    ItemBudget,
}

/// <summary>
/// One server-side Progenitor Core check that refused this client: the capability in the server's words
/// (<c>catalog-v2 equipment-stat synchronization</c>), the server's Progenitor version when it named one, and for the
/// item budget the limit it needs and its own instruction.
/// </summary>
internal sealed class JoinCoreRequirement
{
    public JoinCoreRequirement(string capability, string extraFix)
        : this(JoinCoreRequirementKind.Capability, capability, "", 0, extraFix)
    {
    }

    public JoinCoreRequirement(JoinCoreRequirementKind kind, string capability, string serverCore, int limit, string extraFix)
    {
        Kind = kind;
        Capability = capability ?? "";
        ServerCore = serverCore ?? "";
        Limit = limit;
        ExtraFix = extraFix ?? "";
    }

    public JoinCoreRequirementKind Kind { get; }

    /// <summary>What the client's Progenitor lacks, in the server's words (empty for the item budget).</summary>
    public string Capability { get; }

    /// <summary>The server's Progenitor version, or empty when its refusal did not name one (an older server).</summary>
    public string ServerCore { get; }

    /// <summary>Item budget: the custom items per join the server needs the client to accept (0 when not named).</summary>
    public int Limit { get; }

    /// <summary>The server's own instruction beyond updating Progenitor, or empty.</summary>
    public string ExtraFix { get; }
}

/// <summary>
/// Join refusals that are not about any mod row: the server's handshake refused this client because its Progenitor
/// Core lacks a capability the server needs, or accepts fewer custom items than the server adds. Live 2026-10-13: such
/// a refusal produced zero deny rows, so the join screens read "All ready" and the player was kicked with no
/// explanation on screen.
///
/// <para>The client has nothing to judge these with: the server decides from the capability bits the client
/// announced and from its own content, and it sends its own Progenitor version only after an accepted join. The kick
/// reason is the only record, so this reads the problems the server composed with
/// <see cref="Handshake.Compare.CoreCapabilityProblem"/> and <see cref="Handshake.Compare.ItemBudgetProblem"/>, and the
/// wordings of servers from before those (problems ending in <see cref="Handshake.Compare.CoreUpdateFix"/>, and
/// <see cref="Handshake.Compare.LegacyItemBudgetHead"/>). Game-free.</para>
/// </summary>
internal static class JoinCoreRefusal
{
    private static readonly Regex CapabilityForm = new(
        @"^Needs Progenitor (?<v>\S+) or newer \((?<cap>.+)\)$", RegexOptions.CultureInvariant);

    private static readonly Regex UnversionedForm = new(
        @"^Needs a newer Progenitor \((?<cap>.+)\)$", RegexOptions.CultureInvariant);

    private static readonly Regex BudgetForm = new(
        @"^Needs Progenitor set to accept (?<n>\d+) custom items(?: \(this server runs (?<v>\S+)\))?\.\s*(?<fix>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex LegacyBudgetLimit = new(@"MaxSyncedPrefabsPerJoin to (?<n>\d+)", RegexOptions.CultureInvariant);

    /// <summary>The Core requirements in <paramref name="kickReason"/>, in the server's order, without duplicates.</summary>
    public static IReadOnlyList<JoinCoreRequirement> Parse(string? kickReason)
    {
        var list = new List<JoinCoreRequirement>();
        if (string.IsNullOrWhiteSpace(kickReason)) return list;
        string text = kickReason!.Trim();
        if (text.StartsWith(Handshake.Compare.MismatchPrefix, StringComparison.Ordinal))
            text = text.Substring(Handshake.Compare.MismatchPrefix.Length);
        // MismatchReason puts a note first, then " | ", then the problems list.
        int bar = text.LastIndexOf(" | ", StringComparison.Ordinal);
        if (bar >= 0) text = text.Substring(bar + 3);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
        {
            var req = ParseProblem(raw.Trim());
            if (req != null && seen.Add(req.Kind + "|" + req.Capability)) list.Add(req);
        }

        return list;
    }

    /// <summary>One problem of the list as a Core requirement, or null when it is about something else (a mod row).</summary>
    internal static JoinCoreRequirement? ParseProblem(string problem)
    {
        if (string.IsNullOrEmpty(problem)) return null;
        if (problem.EndsWith(Handshake.Compare.CoreUpdateFix, StringComparison.Ordinal))
        {
            string head = problem.Substring(0, problem.Length - Handshake.Compare.CoreUpdateFix.Length).Trim().TrimEnd('.');
            var m = CapabilityForm.Match(head);
            if (m.Success) return new JoinCoreRequirement(JoinCoreRequirementKind.Capability, m.Groups["cap"].Value.Trim(), m.Groups["v"].Value, 0, "");
            m = UnversionedForm.Match(head);
            if (m.Success) return new JoinCoreRequirement(m.Groups["cap"].Value.Trim(), "");
            string capability = LegacyCapabilityOf(head);
            return capability.Length == 0 ? null : new JoinCoreRequirement(capability, "");
        }

        var b = BudgetForm.Match(problem);
        if (b.Success)
            return new JoinCoreRequirement(JoinCoreRequirementKind.ItemBudget, "", b.Groups["v"].Value, LimitOf(b.Groups["n"].Value), b.Groups["fix"].Value.Trim());

        if (problem.StartsWith(Handshake.Compare.LegacyItemBudgetHead, StringComparison.Ordinal))
        {
            string rest = problem.Substring(Handshake.Compare.LegacyItemBudgetHead.Length).Trim();
            var n = LegacyBudgetLimit.Match(rest);
            return new JoinCoreRequirement(JoinCoreRequirementKind.ItemBudget, "", "", n.Success ? LimitOf(n.Groups["n"].Value) : 0, rest);
        }

        return null;
    }

    private static int LimitOf(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>
    /// An older server's wording: <c>This server requires X</c> or <c>This server's content packs require X</c> → <c>X</c>;
    /// any other sentence is kept whole, so a future wording still names something.
    /// </summary>
    private static string LegacyCapabilityOf(string sentence)
    {
        string s = sentence.TrimEnd('.', ' ');
        foreach (string lead in new[] { "This server requires ", "This server's content packs require " })
        {
            if (s.StartsWith(lead, StringComparison.Ordinal)) return s.Substring(lead.Length).Trim();
        }

        return s;
    }
}
