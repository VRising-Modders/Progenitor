using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>CLIENTPOLICY1: the Players joining panel's words for the two client mod policy sections (docs/client-mod-policy.md).</summary>
internal static class ClientPolicyText
{
    internal const string RequiredHeading = "Required client mods";
    internal const string RequiredHelp = "Players must have these to join. The server doesn't need to run them.";
    internal const string AddRequired = "+ Require a mod";
    internal const string PickRequireTitle = "Require a mod";
    internal const string PickRequireBody = "Client mods only: installed on this PC, or found on Thunderstore. Players see the reason you add next. A recommended mod moves to Required.";
    internal const string Require = "Require";

    internal const string PolicyHeading = "Which client mods players may use";
    internal const string ModeAny = "Any mods";
    internal const string ModeApproved = "Only approved mods";
    internal const string AddApproved = "+ Approve a mod";
    internal const string ApproveEverything = "Approve everything I have";
    internal const string PickApproveTitle = "Approve a mod";
    internal const string PickApproveBody = "Client mods players may bring: installed on this PC, or found on Thunderstore.";
    internal const string Approve = "Approve";
    internal const string AlwaysApproved = "Also allowed: BepInEx, Progenitor, this server's required and recommended mods, and mods this server runs.";
    internal const string UnverifiedHeading = "Players whose mods can't be checked";
    internal const string LetThemJoin = "Let them join";
    internal const string RefuseThem = "Refuse them";
    // COPY2 (user review 2026-10-06): applies only with Only approved mods (ClientModPolicy.RequiresAnswer); the direct way
    // to keep out players without Progenitor is Players without Progenitor → Keep them out (KEEPOUT1).
    // REQRULE7 (requested 2026-10-08: the setting confused; plain words and an example).
    internal const string UnverifiedHelp =
        "Players without Progenitor can't tell the server which mods they use. This only matters with Only approved mods. "
        + "Example: your server allows only a minimap and a chat mod. A player joins without Progenitor; the server can't see "
        + "whether they run anything else. Let them join: they play anyway, unchecked. Refuse them: they can't join until they "
        + "install Progenitor. To keep out players without Progenitor whatever your mod rules are, use Players without "
        + "Progenitor › Keep them out.";

    // RADIO1: each option's one-line explanation in the radio lists (the text that sat under the button pair).
    internal const string ModeAnyShort = "Players may bring any client mods.";
    internal const string ModeApprovedShort = "Only the approved mods below, plus the ones always allowed.";
    // REQRULE7: short, and the same words as the help.
    internal const string LetThemJoinShort = "Players without Progenitor play anyway, unchecked.";
    internal const string RefuseThemShort = "Players without Progenitor can't join until they install it. Only with Only approved mods.";
    // REQRULE1 (2026-10-08): the rule covers a required mod removed or turned off too (live: Disconnect kicked for it).
    // Only a mod the server doesn't allow can be paused; a missing required mod is only ever told. Three lines at most
    // (the full text with "for this server" is MidSessionWarnHelp, shown in the description column).
    internal const string MidSessionWarnShort =
        "A mod you don't allow is paused if Progenitor can. Otherwise, or when a required mod is removed or turned off, the player gets "
        + "a notice. Nobody is disconnected; their next join is refused until it's fixed.";

    /// <summary>RADIO1: the Players joining panel's radio lists (values as the requests send them).</summary>
    internal static IReadOnlyList<Progenitor.Ui.UiChoiceOption> ModeOptions => new[]
    {
        new Progenitor.Ui.UiChoiceOption("any", ModeAny, ModeAnyShort),
        new Progenitor.Ui.UiChoiceOption("approved", ModeApproved, ModeApprovedShort),
    };

    /// <inheritdoc cref="ModeOptions"/>
    internal static IReadOnlyList<Progenitor.Ui.UiChoiceOption> UnverifiedOptions => new[]
    {
        new Progenitor.Ui.UiChoiceOption("allow", LetThemJoin, LetThemJoinShort),
        new Progenitor.Ui.UiChoiceOption("refuse", RefuseThem, RefuseThemShort),
    };

    /// <summary>
    /// RADIO1: the mid-session choice, with the same short explanations as the Server tab's rows (RADIOCLICK1: the full
    /// text ran to four lines under Disconnect the player). The full text (<see cref="MidSessionWarnHelp"/>,
    /// <see cref="MidSessionDisconnectHelp"/>) is the panel readback's <c>midSessionHelp</c> and is in
    /// docs/client-mod-policy.md.
    /// </summary>
    internal static IReadOnlyList<Progenitor.Ui.UiChoiceOption> MidSessionOptions => new[]
    {
        new Progenitor.Ui.UiChoiceOption(ClientModRules.WordWarn, MidSessionWarn, MidSessionWarnShort),
        new Progenitor.Ui.UiChoiceOption(ClientModRules.WordDisconnect, MidSessionDisconnect, MidSessionDisconnectShort),
    };

    internal static string MidSessionDisconnectShort => MidSessionDisconnectShortFor(GraceSeconds);

    /// <inheritdoc cref="MidSessionDisconnectShort"/>
    internal static string MidSessionDisconnectShortFor(int seconds) =>
        "Applies to a mod you don't allow and to a required mod removed or turned off while playing. The player gets a notice naming the mod "
        + "and is disconnected after " + seconds.ToString(CultureInfo.InvariantCulture) + " seconds unless they fix it.";

    /// <summary>
    /// REQRULE5: the joined server's Disconnect them grace for the texts: its Server tab value (an admin's state), else what
    /// it told this client with its rule (<see cref="ClientModReportClient.ServerGraceSeconds"/>), else a joined server that
    /// re-checks without saying is one from before the setting (<see cref="ClientPolicyRecheckLimits.LegacyDisconnectGraceSeconds"/>),
    /// else this process's own setting (the server's texts).
    /// </summary>
    internal static int GraceSeconds
    {
        get
        {
            try
            {
                string? v = JoinAdminRows.Value(ServerOptionOps.DisconnectGrace);
                if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                    return ClientPolicyRecheckLimits.Clamp(n);
                if (ClientModReportClient.ServerGraceSeconds > 0) return ClientModReportClient.ServerGraceSeconds;
                if (ClientModReportClient.ServerRechecks) return ClientPolicyRecheckLimits.LegacyDisconnectGraceSeconds;
            }
            catch { /* this process's own value */ }

            return ClientPolicyRecheckLimits.DisconnectGraceSeconds;
        }
    }
    internal const string Honesty = "Players' own games report their mods. This keeps honest players to the house rules; it doesn't stop cheats.";
    internal const string ConfirmTitle = "Approve everything you have?";
    internal const string ConfirmApprove = "Approve all";
    internal const string NothingToApprove = "Every client mod on this PC is already approved or always allowed.";
    internal const string NoCandidates = "No client mod matches. Type part of a name to search Thunderstore.";
    internal const string Rule = "Rule";
    internal const string More = "More ›";
    internal const string Earlier = "‹ Earlier";

    // CLIENTPOLICY3: the mid-session choice.
    // COPY2: covers a mod loaded after joining and a required mod turned off.
    internal const string MidSessionHeading = "If a player's mods break the rules while playing";
    internal const string MidSessionWarn = "Pause the mod and warn them";
    internal const string MidSessionDisconnect = "Disconnect them";

    internal const string MidSessionWarnHelp =
        "A mod you don't allow that Progenitor can pause is paused for this server; for any other mod you don't allow, and for a required mod "
        + "removed or turned off, the player gets a notice naming it. Nobody is disconnected, but their next join is refused until it's fixed.";

    internal static string MidSessionDisconnectHelp => MidSessionDisconnectHelpFor(GraceSeconds);

    /// <inheritdoc cref="MidSessionDisconnectHelp"/>
    internal static string MidSessionDisconnectHelpFor(int seconds)
    {
        int before = ClientPolicyRecheckLimits.PolicyChangeGraceFor(seconds);
        return "The player gets a notice naming the mod, then is disconnected after " + seconds.ToString(CultureInfo.InvariantCulture)
            + " seconds unless it's fixed (mods Progenitor can pause are paused first). Applies to a mod you don't allow and to a required mod "
            + "removed or turned off while playing."
            + (before > seconds
                ? " Mods you don't allow that were already loaded when you change these rules get " + before.ToString(CultureInfo.InvariantCulture) + " seconds."
                : "")
            + " A requirement you add while they play applies from their next join.";
    }

    /// <summary>The save note after switching to Disconnect the player.</summary>
    internal static string MidSessionDisconnectNote => ClientPolicyNoticeText.DisconnectNote;

    /// <summary>An alert line's one-click approval.</summary>
    internal const string AlertApprove = "Approve";

    internal static string RequiredCount(int n) => n.ToString(CultureInfo.InvariantCulture) + " required";

    internal static string ApprovedHeading(int total) => "Approved client mods · " + total.ToString(CultureInfo.InvariantCulture);

    /// <summary>A required row's second line: <c>&lt;package&gt; · &lt;version rule&gt;</c> (the id when there is no package).</summary>
    internal static string RequiredSecond(JoinAdminClientRow r)
    {
        var parts = new List<string>();
        if (r.Name.Length > 0 && r.Id.Length > 0) parts.Add(r.Id);
        if (r.Package.Length > 0) parts.Add(r.Package);
        parts.Add(ClientModRules.Label(r.RuleValue, r.Version));
        return string.Join(" · ", parts);
    }

    /// <summary>An approved row's second line: <c>&lt;package&gt; · any version</c> (or its rule).</summary>
    internal static string ApprovedSecond(JoinAdminClientRow r)
    {
        var parts = new List<string>();
        if (r.Name.Length > 0 && r.Id.Length > 0) parts.Add(r.Id);
        if (r.Package.Length > 0) parts.Add(r.Package);
        string rule = ClientModRules.Label(r.RuleValue, r.Version);
        parts.Add(rule == "Any version" ? "any version" : rule);
        return string.Join(" · ", parts);
    }

    /// <summary>The row's display name: its name, else its id, else its package.</summary>
    internal static string NameOf(JoinAdminClientRow r) => r.Name.Length > 0 ? r.Name : r.Key;

    /// <summary>The per-row rule button's caption: the rule's short word.</summary>
    internal static string RuleCaption(ClientModRule rule) => rule switch
    {
        ClientModRule.Exact => "Exact",
        ClientModRule.Minor => "Same minor",
        ClientModRule.Any => "Any version",
        _ => "Or newer",
    };

    /// <summary>The pager line under a long approved list: <c>41–80 of 128</c>.</summary>
    internal static string Pager(int from, int shown, int total) =>
        shown <= 0 ? "" : (from + 1).ToString(CultureInfo.InvariantCulture) + "–" + (from + shown).ToString(CultureInfo.InvariantCulture)
            + " of " + total.ToString(CultureInfo.InvariantCulture);

    /// <summary>The confirmation's body: how many mods, then their names (at most 24, then "and N more").</summary>
    internal static string ConfirmBody(IReadOnlyList<JoinAdminBulkItem> items)
    {
        if (items == null || items.Count == 0) return NothingToApprove;
        var names = new List<string>();
        for (int i = 0; i < items.Count && i < 24; i++) names.Add(items[i].Name.Length > 0 ? items[i].Name : items[i].Id.Length > 0 ? items[i].Id : items[i].Package);
        string more = items.Count > 24 ? ", and " + (items.Count - 24).ToString(CultureInfo.InvariantCulture) + " more" : "";
        return "This approves " + (items.Count == 1 ? "1 client mod" : items.Count.ToString(CultureInfo.InvariantCulture) + " client mods")
            + " loaded on this PC, at any version: " + string.Join(", ", names) + more + ".";
    }
}

/// <summary>One client mod loaded on the admin's PC, as the panel's pickers see it.</summary>
internal readonly struct ClientPolicyLocalMod
{
    public ClientPolicyLocalMod(string id, string name, string version, string package)
    {
        Id = id ?? "";
        Name = name ?? "";
        Version = version ?? "";
        Package = package ?? "";
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Package { get; }
}

/// <summary>CLIENTPOLICY1: the panel's game-free decisions (pickers' exclusions, Approve everything's list).</summary>
internal static class ClientPolicyPanelModel
{
    /// <summary>True when the state already requires the mod (by id or package).</summary>
    internal static bool IsRequired(JoinAdminState? s, string? id, string? package) => Has(s?.RequiredClient, id, package);

    /// <summary>True when the state's approved window lists the mod (by id or package).</summary>
    internal static bool IsApproved(JoinAdminState? s, string? id, string? package) => Has(s?.Approved, id, package);

    private static bool Has(List<JoinAdminClientRow>? rows, string? id, string? package)
    {
        if (rows == null) return false;
        foreach (var r in rows)
            if ((!string.IsNullOrEmpty(id) && r.Id.Length > 0 && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(package) && r.Package.Length > 0 && string.Equals(r.Package, package, StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    /// <summary>
    /// Approve everything I have: every client mod loaded on this PC, minus the always-allowed ones (BepInEx,
    /// Progenitor) and those already approved, one row per mod.
    /// </summary>
    internal static List<JoinAdminBulkItem> ApproveEverything(IEnumerable<ClientPolicyLocalMod> local, JoinAdminState? state)
    {
        var list = new List<JoinAdminBulkItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in local ?? Array.Empty<ClientPolicyLocalMod>())
        {
            if (m.Id.Length == 0 || !seen.Add(m.Id)) continue;
            if (ClientModJudge.AlwaysApproved(m.Id) || IsApproved(state, m.Id, m.Package)) continue;
            if (!ModManifestIds.TryValidateId(m.Id, out _)) continue;
            string package = m.Package.Length > 0 && Install.RemoteInstallWire.IsValidPackage(m.Package) ? m.Package : "";
            string name = m.Name.Trim();
            if (name.Length > JoinSectionLimits.MaxNameChars) name = name.Substring(0, JoinSectionLimits.MaxNameChars);
            if (string.Equals(name, m.Id, StringComparison.OrdinalIgnoreCase)) name = "";
            list.Add(new JoinAdminBulkItem { Id = m.Id, Package = package, Name = name });
        }

        return list;
    }

    /// <summary>The next window's first row when paging the approved list (<paramref name="forward"/>: later rows).</summary>
    internal static int PageFrom(int from, int shown, int total, bool forward)
    {
        if (forward) return from + shown < total ? from + Math.Max(1, shown) : from;
        return Math.Max(0, from - Math.Max(1, shown));
    }
}
