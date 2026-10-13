using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>JOINDRAFT1: what one staged change of the Players joining panel does.</summary>
internal enum JoinAdminChangeKind
{
    RecommendAdd,
    RecommendRemove,
    RecommendReason,
    RequiredAdd,
    RequiredRemove,
    RequiredReason,
    RequiredRule,
    ApproveAdd,
    ApproveRemove,
    PolicyMode,
    Unverified,
    MidSession,
    Window,

    /// <summary>Mod compatibility C3: the server-wide Version matching.</summary>
    VersionMatching,

    /// <summary>C3: a per-mod rule for a mod the server runs (<see cref="JoinAdminChange.Rule"/> empty: the server setting).</summary>
    ServerModRule,
}

/// <summary>JOINDRAFT1: the small marker a mod row shows for its staged state.</summary>
internal enum JoinAdminRowMark
{
    None,
    Added,
    Removed,
    Changed,
}

/// <summary>
/// JOINDRAFT1: one staged change. <see cref="Before"/> is the server's value when it was staged (rebased when the server's
/// state moves); <see cref="Conflict"/> says another admin changed the same thing meanwhile; <see cref="Error"/> is the
/// server's refusal from the last Apply (the change stays staged).
/// </summary>
internal sealed class JoinAdminChange
{
    public JoinAdminChangeKind Kind { get; set; }
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A choice's word (<c>approved</c>, <c>refuse</c>, <c>disconnect</c>) or the window's seconds.</summary>
    public string Value { get; set; } = "";

    /// <summary>The server's value when staged: the reason, the rule word, the choice word or the seconds.</summary>
    public string Before { get; set; } = "";

    /// <summary>A rule change: the server's version when staged.</summary>
    public string BeforeVersion { get; set; } = "";

    /// <summary>A required add of a mod the server recommends: the server moves it from Recommended.</summary>
    public bool MovesRecommended { get; set; }

    public string Conflict { get; set; } = "";
    public string Error { get; set; } = "";

    public string Key => Id.Length > 0 ? Id : Package;

    public string DisplayName => Name.Length > 0 ? Name : Key;

    internal bool SameMod(string? id, string? package) => JoinAdminDraft.SameMod(Id, Package, id, package);
}

/// <summary>One mod row as the draft draws it: the row with the staged values applied, and its marker.</summary>
internal readonly struct JoinAdminDraftRow<T>
{
    public JoinAdminDraftRow(T row, JoinAdminRowMark mark)
    {
        Row = row;
        Mark = mark;
    }

    public T Row { get; }
    public JoinAdminRowMark Mark { get; }
}

/// <summary>
/// JOINDRAFT1 (requested 2026-10-07: "This should have an apply button at the bottom instead of changes being
/// instant. Show the changes."): the Players joining panel's staged edits over the server's last state. Every edit
/// changes this draft; nothing is sent until Apply, which sends <see cref="Steps"/> in order (<see cref="JoinAdminApplyRun"/>).
/// An edit that returns the draft to the server's value removes its change. When the server's state moves underneath
/// (another admin), <see cref="Rebase"/> drops what the server now already has and flags real conflicts. Game-free.
/// </summary>
internal sealed class JoinAdminDraft
{
    internal const string NoChanges = "No changes";
    internal const string AlreadyStaged = "That mod is already in your changes.";
    internal const string UndoRemovalFirst = "Undo its removal first.";
    internal const string ConflictRemoved = "another admin removed it";

    /// <summary>The server's state the draft is over (null: none, so no edits).</summary>
    public JoinAdminState? Base { get; private set; }

    public List<JoinAdminChange> Changes { get; } = new();

    /// <summary>The lock note of a request operation (<see cref="JoinAdminOps"/>), or null when editable.</summary>
    public Func<string, string?>? LockedBy { get; set; }

    public bool HasChanges => Changes.Count > 0;

    public int Count => Changes.Count;

    /// <summary>Discard: the draft equals the server's state.</summary>
    public void Reset(JoinAdminState? state)
    {
        Base = state;
        Changes.Clear();
    }

    internal static bool SameMod(string? aId, string? aPackage, string? bId, string? bPackage)
    {
        string ai = (aId ?? "").Trim(), ap = (aPackage ?? "").Trim(), bi = (bId ?? "").Trim(), bp = (bPackage ?? "").Trim();
        if (ai.Length > 0 && (string.Equals(ai, bi, StringComparison.OrdinalIgnoreCase) || string.Equals(ai, bp, StringComparison.OrdinalIgnoreCase))) return true;
        if (ap.Length > 0 && (string.Equals(ap, bp, StringComparison.OrdinalIgnoreCase) || string.Equals(ap, bi, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>A reason as the server stores it: trimmed, the placeholder when empty.</summary>
    internal static string StoredReason(string? reason)
    {
        string t = (reason ?? "").Trim();
        return t.Length == 0 ? JoinAdminLimits.PlaceholderReason : t;
    }

    // ---- finding ------------------------------------------------------------------------------------------------

    private int Find(JoinAdminChangeKind kind, string? id, string? package)
    {
        for (int i = 0; i < Changes.Count; i++)
            if (Changes[i].Kind == kind && Changes[i].SameMod(id, package)) return i;
        return -1;
    }

    private int Find(JoinAdminChangeKind kind)
    {
        for (int i = 0; i < Changes.Count; i++)
            if (Changes[i].Kind == kind) return i;
        return -1;
    }

    private JoinAdminChange? Get(JoinAdminChangeKind kind, string? id, string? package)
    {
        int i = Find(kind, id, package);
        return i >= 0 ? Changes[i] : null;
    }

    private static JoinAdminRecRow? FindRec(JoinAdminState? s, string? id, string? package)
    {
        if (s == null) return null;
        foreach (var r in s.Recommended)
            if (SameMod(r.Id, r.Package, id, package)) return r;
        return null;
    }

    private static JoinAdminClientRow? FindClient(List<JoinAdminClientRow>? rows, string? id, string? package)
    {
        if (rows == null) return null;
        foreach (var r in rows)
            if (SameMod(r.Id, r.Package, id, package)) return r;
        return null;
    }

    /// <summary>The server's row for a key (id or package), as staged edits name it.</summary>
    internal JoinAdminRecRow? BaseRecommended(string key) => FindRec(Base, key, key);

    internal JoinAdminClientRow? BaseRequired(string key) => FindClient(Base?.RequiredClient, key, key);

    internal JoinAdminClientRow? BaseApproved(string key) => FindClient(Base?.Approved, key, key);

    private string? Locked(string op) => LockedBy?.Invoke(op);

    private string? Ready(string op)
    {
        if (Base == null) return "The server's state is not here yet.";
        return Locked(op);
    }

    private void Remove(JoinAdminChange c) => Changes.Remove(c);

    private void RemoveAll(Func<JoinAdminChange, bool> match)
    {
        for (int i = Changes.Count - 1; i >= 0; i--)
            if (match(Changes[i])) Changes.RemoveAt(i);
    }

    private static void Fresh(JoinAdminChange c)
    {
        c.Conflict = "";
        c.Error = "";
    }

    // ---- recommended ----------------------------------------------------------------------------------------------

    /// <summary>Stage a recommendation. Null when staged (or when it undid a staged removal); else why not.</summary>
    public string? Recommend(string? id, string? package, string? version, string? reason, string? name)
    {
        if (Ready(JoinAdminOps.RecommendAdd) is { } why) return why;
        string i = (id ?? "").Trim(), p = (package ?? "").Trim();
        if (i.Length == 0 && p.Length == 0) return "Name a plugin id or a Thunderstore package.";
        if (JoinAdminPolicy.CheckReason((reason ?? "").Trim()) is { } bad) return bad;
        if (Get(JoinAdminChangeKind.RecommendRemove, i, p) is { } removal)
        {
            Remove(removal);
            var stored = FindRec(Base, i, p);
            if (stored != null && (reason ?? "").Trim().Length > 0) return SetRecommendedReason(stored.Key, reason ?? "");
            return null;
        }

        if (Find(JoinAdminChangeKind.RecommendAdd, i, p) >= 0) return AlreadyStaged;
        if (FindRec(Base, i, p) != null) return "That mod is already recommended.";
        if (Get(JoinAdminChangeKind.RequiredAdd, i, p) != null || (FindClient(Base!.RequiredClient, i, p) != null && Find(JoinAdminChangeKind.RequiredRemove, i, p) < 0))
            return "This server requires that mod, so it is not a recommendation.";
        if (RecommendedView().Count >= JoinAdminLimits.MaxRecommended) return "At most " + JoinAdminLimits.MaxRecommended + " mods can be recommended.";
        Changes.Add(new JoinAdminChange
        {
            Kind = JoinAdminChangeKind.RecommendAdd, Id = i, Package = p, Version = (version ?? "").Trim(), Reason = reason ?? "", Name = Clip(name),
        });
        return null;
    }

    public string? Unrecommend(string? key)
    {
        if (Ready(JoinAdminOps.RecommendRemove) is { } why) return why;
        string k = (key ?? "").Trim();
        if (Get(JoinAdminChangeKind.RecommendAdd, k, k) is { } added)
        {
            Remove(added);
            return null;
        }

        var row = FindRec(Base, k, k);
        if (row == null) return "That mod is not recommended.";
        if (row.FromFile) return "That recommendation comes from progenitor-manifest.json; edit that file to remove it.";
        if (Find(JoinAdminChangeKind.RecommendRemove, row.Id, row.Package) >= 0) return null;
        RemoveAll(c => c.Kind == JoinAdminChangeKind.RecommendReason && c.SameMod(row.Id, row.Package));
        Changes.Add(new JoinAdminChange
        {
            Kind = JoinAdminChangeKind.RecommendRemove, Id = row.Id, Package = row.Package, Version = row.Version, Name = row.Name, Before = row.Reason,
        });
        return null;
    }

    public string? SetRecommendedReason(string? key, string reason)
    {
        if (Ready(JoinAdminOps.RecommendReason) is { } why) return why;
        if (JoinAdminPolicy.CheckReason((reason ?? "").Trim()) is { } bad) return bad;
        string k = (key ?? "").Trim();
        if (Get(JoinAdminChangeKind.RecommendAdd, k, k) is { } added)
        {
            added.Reason = reason ?? "";
            Fresh(added);
            return null;
        }

        var row = FindRec(Base, k, k);
        if (row == null) return "That mod is not recommended.";
        if (row.FromFile) return "That recommendation comes from progenitor-manifest.json; edit its reason in that file.";
        if (Find(JoinAdminChangeKind.RecommendRemove, row.Id, row.Package) >= 0) return UndoRemovalFirst;
        var change = Get(JoinAdminChangeKind.RecommendReason, row.Id, row.Package);
        if (string.Equals(StoredReason(reason), row.Reason, StringComparison.Ordinal))
        {
            if (change != null) Remove(change);
            return null;
        }

        if (change == null)
        {
            change = new JoinAdminChange { Kind = JoinAdminChangeKind.RecommendReason, Id = row.Id, Package = row.Package, Name = row.Name, Before = row.Reason };
            Changes.Add(change);
        }

        change.Reason = reason ?? "";
        Fresh(change);
        return null;
    }

    // ---- required -------------------------------------------------------------------------------------------------

    public string? Require(string? id, string? package, string? version, string? rule, string? reason, string? name)
    {
        if (Ready(JoinAdminOps.RequiredAdd) is { } why) return why;
        string i = (id ?? "").Trim(), p = (package ?? "").Trim(), r = (rule ?? "").Trim();
        if (i.Length == 0 && p.Length == 0) return "Name a plugin id or a Thunderstore package.";
        if (r.Length > 0 && !ClientModRules.TryParse(r, out _)) return "Unknown rule \"" + r + "\": use at-least, exact, minor or any.";
        if (JoinAdminPolicy.CheckReason((reason ?? "").Trim()) is { } bad) return bad;
        if (Get(JoinAdminChangeKind.RequiredRemove, i, p) is { } removal)
        {
            Remove(removal);
            var stored = FindClient(Base!.RequiredClient, i, p);
            if (stored != null)
            {
                if ((reason ?? "").Trim().Length > 0 && SetRequiredReason(stored.Key, reason ?? "") is { } refused) return refused;
                if (r.Length > 0 && SetRequiredRule(stored.Key, r, version ?? "") is { } refusedRule) return refusedRule;
            }

            return null;
        }

        if (Find(JoinAdminChangeKind.RequiredAdd, i, p) >= 0) return AlreadyStaged;
        if (FindClient(Base!.RequiredClient, i, p) != null) return "That mod is already required.";
        if (RequiredView().Count >= ClientModPolicyLimits.MaxRequired) return "At most " + ClientModPolicyLimits.MaxRequired + " client mods can be required.";
        // A mod can't be both: the server moves a recommended one. A staged recommendation of it is dropped here.
        bool moves = FindRec(Base, i, p) is { FromFile: false } && Find(JoinAdminChangeKind.RecommendRemove, i, p) < 0;
        RemoveAll(c => (c.Kind == JoinAdminChangeKind.RecommendAdd || c.Kind == JoinAdminChangeKind.RecommendReason) && c.SameMod(i, p));
        Changes.Add(new JoinAdminChange
        {
            Kind = JoinAdminChangeKind.RequiredAdd, Id = i, Package = p, Version = (version ?? "").Trim(), Rule = r, Reason = reason ?? "", Name = Clip(name),
            MovesRecommended = moves,
        });
        return null;
    }

    public string? Unrequire(string? key)
    {
        if (Ready(JoinAdminOps.RequiredRemove) is { } why) return why;
        string k = (key ?? "").Trim();
        if (Get(JoinAdminChangeKind.RequiredAdd, k, k) is { } added)
        {
            Remove(added);
            return null;
        }

        var row = FindClient(Base!.RequiredClient, k, k);
        if (row == null) return "That mod is not required.";
        if (Find(JoinAdminChangeKind.RequiredRemove, row.Id, row.Package) >= 0) return null;
        RemoveAll(c => (c.Kind == JoinAdminChangeKind.RequiredReason || c.Kind == JoinAdminChangeKind.RequiredRule) && c.SameMod(row.Id, row.Package));
        Changes.Add(new JoinAdminChange
        {
            Kind = JoinAdminChangeKind.RequiredRemove, Id = row.Id, Package = row.Package, Version = row.Version, Rule = row.Rule, Name = row.Name, Before = row.Reason,
        });
        return null;
    }

    public string? SetRequiredReason(string? key, string reason)
    {
        if (Ready(JoinAdminOps.RequiredReason) is { } why) return why;
        if (JoinAdminPolicy.CheckReason((reason ?? "").Trim()) is { } bad) return bad;
        string k = (key ?? "").Trim();
        if (Get(JoinAdminChangeKind.RequiredAdd, k, k) is { } added)
        {
            added.Reason = reason ?? "";
            Fresh(added);
            return null;
        }

        var row = FindClient(Base!.RequiredClient, k, k);
        if (row == null) return "That mod is not required.";
        if (Find(JoinAdminChangeKind.RequiredRemove, row.Id, row.Package) >= 0) return UndoRemovalFirst;
        var change = Get(JoinAdminChangeKind.RequiredReason, row.Id, row.Package);
        if (string.Equals(StoredReason(reason), row.Reason, StringComparison.Ordinal))
        {
            if (change != null) Remove(change);
            return null;
        }

        if (change == null)
        {
            change = new JoinAdminChange { Kind = JoinAdminChangeKind.RequiredReason, Id = row.Id, Package = row.Package, Name = row.Name, Before = row.Reason };
            Changes.Add(change);
        }

        change.Reason = reason ?? "";
        Fresh(change);
        return null;
    }

    /// <summary>Stage a required row's rule (and version; empty keeps the row's).</summary>
    public string? SetRequiredRule(string? key, string? rule, string? version)
    {
        if (Ready(JoinAdminOps.RequiredRule) is { } why) return why;
        string k = (key ?? "").Trim(), v = (version ?? "").Trim();
        if (!ClientModRules.TryParse(rule, out var parsed)) return "Unknown rule \"" + rule + "\": use at-least, exact, minor or any.";
        string word = ClientModRules.Word(parsed);
        if (Get(JoinAdminChangeKind.RequiredAdd, k, k) is { } added)
        {
            added.Rule = word;
            if (v.Length > 0) added.Version = v;
            Fresh(added);
            return null;
        }

        var row = FindClient(Base!.RequiredClient, k, k);
        if (row == null) return "That mod is not required.";
        if (Find(JoinAdminChangeKind.RequiredRemove, row.Id, row.Package) >= 0) return UndoRemovalFirst;
        if (v.Length == 0) v = row.Version;
        if (parsed != ClientModRule.Any && v.Length == 0) return "A rule other than Any version needs a version.";
        var change = Get(JoinAdminChangeKind.RequiredRule, row.Id, row.Package);
        if (row.RuleValue == parsed && string.Equals(row.Version, v, StringComparison.Ordinal))
        {
            if (change != null) Remove(change);
            return null;
        }

        if (change == null)
        {
            change = new JoinAdminChange
            {
                Kind = JoinAdminChangeKind.RequiredRule, Id = row.Id, Package = row.Package, Name = row.Name, Before = row.Rule, BeforeVersion = row.Version,
            };
            Changes.Add(change);
        }

        change.Rule = word;
        change.Version = v;
        Fresh(change);
        return null;
    }

    // ---- approved -------------------------------------------------------------------------------------------------

    public string? Approve(string? id, string? package, string? version, string? rule, string? name)
    {
        if (Ready(JoinAdminOps.ApproveAdd) is { } why) return why;
        string i = (id ?? "").Trim(), p = (package ?? "").Trim(), r = (rule ?? "").Trim();
        if (i.Length == 0 && p.Length == 0) return "Name a plugin id or a Thunderstore package.";
        if (r.Length > 0 && !ClientModRules.TryParse(r, out _)) return "Unknown rule \"" + r + "\": use at-least, exact, minor or any.";
        if (Get(JoinAdminChangeKind.ApproveRemove, i, p) is { } removal)
        {
            Remove(removal);
            return null;
        }

        if (Find(JoinAdminChangeKind.ApproveAdd, i, p) >= 0) return AlreadyStaged;
        if (FindClient(Base!.Approved, i, p) != null) return "That mod is already approved.";
        if (ApprovedTotalAfter() >= ClientModPolicyLimits.MaxApproved) return "At most " + ClientModPolicyLimits.MaxApproved + " client mods can be approved.";
        Changes.Add(new JoinAdminChange { Kind = JoinAdminChangeKind.ApproveAdd, Id = i, Package = p, Version = (version ?? "").Trim(), Rule = r, Name = Clip(name) });
        return null;
    }

    public string? Unapprove(string? key)
    {
        if (Ready(JoinAdminOps.ApproveRemove) is { } why) return why;
        string k = (key ?? "").Trim();
        if (Get(JoinAdminChangeKind.ApproveAdd, k, k) is { } added)
        {
            Remove(added);
            return null;
        }

        var row = FindClient(Base!.Approved, k, k);
        if (row == null) return "That mod is not approved.";
        if (Find(JoinAdminChangeKind.ApproveRemove, row.Id, row.Package) >= 0) return null;
        Changes.Add(new JoinAdminChange { Kind = JoinAdminChangeKind.ApproveRemove, Id = row.Id, Package = row.Package, Version = row.Version, Rule = row.Rule, Name = row.Name });
        return null;
    }

    /// <summary>The approved rows the server would store after the staged changes.</summary>
    internal int ApprovedTotalAfter()
    {
        int n = Base?.ApprovedTotal ?? 0;
        foreach (var c in Changes)
        {
            if (c.Kind == JoinAdminChangeKind.ApproveAdd) n++;
            else if (c.Kind == JoinAdminChangeKind.ApproveRemove) n--;
        }

        return n;
    }

    // ---- the choices ----------------------------------------------------------------------------------------------

    public string? SetPolicyMode(string? value)
    {
        if (Ready(JoinAdminOps.PolicyMode) is { } why) return why;
        if (!ClientModRules.TryParseMode(value, out var mode)) return "Choose any or approved.";
        return SetChoice(JoinAdminChangeKind.PolicyMode, ClientModRules.ModeWord(mode), BaseWord(JoinAdminChangeKind.PolicyMode));
    }

    public string? SetUnverified(string? value)
    {
        if (Ready(JoinAdminOps.Unverified) is { } why) return why;
        if (!ClientModRules.TryParseUnverified(value, out var u)) return "Choose allow or refuse.";
        return SetChoice(JoinAdminChangeKind.Unverified, ClientModRules.UnverifiedWord(u), BaseWord(JoinAdminChangeKind.Unverified));
    }

    public string? SetMidSession(string? value)
    {
        if (Ready(JoinAdminOps.MidSession) is { } why) return why;
        if (!ClientModRules.TryParseMidSession(value, out var mid)) return "Choose warn or disconnect.";
        return SetChoice(JoinAdminChangeKind.MidSession, ClientModRules.MidSessionWord(mid), BaseWord(JoinAdminChangeKind.MidSession));
    }

    /// <summary>Mod compatibility C3: Version matching (<c>range</c>, <c>minor</c>, <c>exact</c>); refused while the server's cfg forces it.</summary>
    public string? SetVersionMatching(string? value)
    {
        if (Ready(JoinAdminOps.VersionMatching) is { } why) return why;
        if (!Base!.MatchingSupported) return "This server does not have Version matching.";
        if (!VersionMatchingText.TryParse(value, out var mode)) return "Choose range, minor or exact.";
        if (Base.VersionMatchingForced && VersionMatchingText.TryParse(Base.VersionMatching, out var forced)) return VersionMatchCfg.ForcedNote(forced);
        return SetChoice(JoinAdminChangeKind.VersionMatching, VersionMatchingText.Word(mode), BaseWord(JoinAdminChangeKind.VersionMatching));
    }

    /// <summary>
    /// C3: stage the rule of a mod the server runs (<paramref name="rule"/> <c>exact</c>, <c>at-least</c>, <c>minor</c>, <c>any</c>,
    /// or empty / <c>default</c> for the server setting). Setting the server's own value back removes the change.
    /// </summary>
    public string? SetServerModRule(string? id, string? rule)
    {
        if (Ready(JoinAdminOps.ServerModRule) is { } why) return why;
        string k = (id ?? "").Trim();
        var row = BaseServerMod(k);
        if (row == null) return VersionMatchText.NotRunError;
        if (!VersionMatchText.TryParseRule(rule, out string word)) return "Unknown rule \"" + rule + "\": use exact, at-least, minor, any or default.";
        if (word.Length > 0 && !row.Runs) return VersionMatchText.NotRunError;
        int at = Find(JoinAdminChangeKind.ServerModRule, row.Id, "");
        if (string.Equals(word, row.Rule, StringComparison.OrdinalIgnoreCase))
        {
            if (at >= 0) Changes.RemoveAt(at);
            return null;
        }

        var change = at >= 0 ? Changes[at] : null;
        if (change == null)
        {
            change = new JoinAdminChange { Kind = JoinAdminChangeKind.ServerModRule, Id = row.Id, Name = row.Name, Version = row.Version, Before = row.Rule };
            Changes.Add(change);
        }

        change.Rule = word;
        Fresh(change);
        return null;
    }

    /// <summary>C3: the server's row of mod <paramref name="id"/> in the rule list, or null.</summary>
    internal JoinAdminServerModRow? BaseServerMod(string? id)
    {
        if (Base == null || string.IsNullOrEmpty(id)) return null;
        foreach (var r in Base.ServerMods)
            if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>C3: the rule rows as drawn: the server's, each staged rule applied and marked Changed.</summary>
    public List<JoinAdminDraftRow<JoinAdminServerModRow>> ServerModRows()
    {
        var list = new List<JoinAdminDraftRow<JoinAdminServerModRow>>();
        if (Base == null) return list;
        foreach (var r in Base.ServerMods)
        {
            if (Get(JoinAdminChangeKind.ServerModRule, r.Id, "") is { } c)
                list.Add(new JoinAdminDraftRow<JoinAdminServerModRow>(new JoinAdminServerModRow
                {
                    Id = r.Id, Name = r.Name, Version = r.Version, Rule = c.Rule, Runs = r.Runs,
                }, JoinAdminRowMark.Changed));
            else list.Add(new JoinAdminDraftRow<JoinAdminServerModRow>(r, JoinAdminRowMark.None));
        }

        return list;
    }

    /// <summary>The install window, clamped to 0 to 300 seconds as the server stores it.</summary>
    public string? SetWindow(int seconds)
    {
        if (Ready(JoinAdminOps.Window) is { } why) return why;
        int value = JoinAdminPolicy.ClampWindow(seconds);
        return SetChoice(JoinAdminChangeKind.Window, value.ToString(CultureInfo.InvariantCulture), BaseWord(JoinAdminChangeKind.Window));
    }

    private string? SetChoice(JoinAdminChangeKind kind, string value, string stored)
    {
        int at = Find(kind);
        if (string.Equals(value, stored, StringComparison.OrdinalIgnoreCase))
        {
            if (at >= 0) Changes.RemoveAt(at);
            return null;
        }

        var change = at >= 0 ? Changes[at] : null;
        if (change == null)
        {
            change = new JoinAdminChange { Kind = kind, Before = stored };
            Changes.Add(change);
        }

        change.Value = value;
        Fresh(change);
        return null;
    }

    /// <summary>The server's word for a choice (normalized), or its window seconds.</summary>
    internal string BaseWord(JoinAdminChangeKind kind)
    {
        var s = Base;
        if (s == null) return "";
        return kind switch
        {
            JoinAdminChangeKind.PolicyMode => ClientModRules.TryParseMode(s.PolicyMode, out var m) ? ClientModRules.ModeWord(m) : "any",
            JoinAdminChangeKind.Unverified => ClientModRules.TryParseUnverified(s.Unverified, out var u) ? ClientModRules.UnverifiedWord(u) : "allow",
            JoinAdminChangeKind.MidSession => s.MidSession.Length == 0 ? ""
                : ClientModRules.TryParseMidSession(s.MidSession, out var mid) ? ClientModRules.MidSessionWord(mid) : ClientModRules.WordWarn,
            JoinAdminChangeKind.Window => s.WindowSeconds.ToString(CultureInfo.InvariantCulture),
            JoinAdminChangeKind.VersionMatching => !s.MatchingSupported ? ""
                : VersionMatchingText.TryParse(s.VersionMatching, out var vm) ? VersionMatchingText.Word(vm) : VersionMatchingText.WordRange,
            _ => "",
        };
    }

    /// <summary>A choice's word (or the window's seconds) as the draft has it.</summary>
    internal string Word(JoinAdminChangeKind kind)
    {
        int at = Find(kind);
        return at >= 0 ? Changes[at].Value : BaseWord(kind);
    }

    internal int WindowSeconds => int.TryParse(Word(JoinAdminChangeKind.Window), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : Base?.WindowSeconds ?? 0;

    // ---- undo -----------------------------------------------------------------------------------------------------

    /// <summary>The Changes list's ×: drop change <paramref name="index"/>. False when there is none.</summary>
    public bool Undo(int index)
    {
        if (index < 0 || index >= Changes.Count) return false;
        Changes.RemoveAt(index);
        return true;
    }

    /// <summary>A removed row's Undo: drop the staged removal of that mod from list <paramref name="kind"/>.</summary>
    public bool UndoRemoval(JoinAdminChangeKind kind, string key)
    {
        int at = Find(kind, key, key);
        if (at < 0) return false;
        Changes.RemoveAt(at);
        return true;
    }

    // ---- the draft's rows -----------------------------------------------------------------------------------------

    /// <summary>The recommended rows as drawn: the server's (removed ones marked, edited reasons applied), then the staged ones.</summary>
    public List<JoinAdminDraftRow<JoinAdminRecRow>> RecommendedRows()
    {
        var list = new List<JoinAdminDraftRow<JoinAdminRecRow>>();
        if (Base == null) return list;
        foreach (var r in Base.Recommended)
        {
            if (Get(JoinAdminChangeKind.RequiredAdd, r.Id, r.Package) is { MovesRecommended: true })
            {
                list.Add(new JoinAdminDraftRow<JoinAdminRecRow>(r, JoinAdminRowMark.Removed));
                continue;
            }

            if (Find(JoinAdminChangeKind.RecommendRemove, r.Id, r.Package) >= 0)
            {
                list.Add(new JoinAdminDraftRow<JoinAdminRecRow>(r, JoinAdminRowMark.Removed));
                continue;
            }

            if (Get(JoinAdminChangeKind.RecommendReason, r.Id, r.Package) is { } edit)
            {
                list.Add(new JoinAdminDraftRow<JoinAdminRecRow>(new JoinAdminRecRow
                {
                    Id = r.Id, Package = r.Package, Version = r.Version, Reason = edit.Reason, FromFile = r.FromFile, Name = r.Name,
                }, JoinAdminRowMark.Changed));
                continue;
            }

            list.Add(new JoinAdminDraftRow<JoinAdminRecRow>(r, JoinAdminRowMark.None));
        }

        foreach (var c in Changes)
            if (c.Kind == JoinAdminChangeKind.RecommendAdd)
                list.Add(new JoinAdminDraftRow<JoinAdminRecRow>(new JoinAdminRecRow
                {
                    Id = c.Id, Package = c.Package, Version = c.Version, Reason = c.Reason, Name = c.Name,
                }, JoinAdminRowMark.Added));
        return list;
    }

    public List<JoinAdminDraftRow<JoinAdminClientRow>> RequiredRows()
    {
        var list = new List<JoinAdminDraftRow<JoinAdminClientRow>>();
        if (Base == null) return list;
        foreach (var r in Base.RequiredClient)
        {
            if (Find(JoinAdminChangeKind.RequiredRemove, r.Id, r.Package) >= 0)
            {
                list.Add(new JoinAdminDraftRow<JoinAdminClientRow>(r, JoinAdminRowMark.Removed));
                continue;
            }

            var reason = Get(JoinAdminChangeKind.RequiredReason, r.Id, r.Package);
            var rule = Get(JoinAdminChangeKind.RequiredRule, r.Id, r.Package);
            if (reason == null && rule == null)
            {
                list.Add(new JoinAdminDraftRow<JoinAdminClientRow>(r, JoinAdminRowMark.None));
                continue;
            }

            list.Add(new JoinAdminDraftRow<JoinAdminClientRow>(new JoinAdminClientRow
            {
                Id = r.Id, Package = r.Package, Name = r.Name,
                Version = rule?.Version ?? r.Version, Rule = rule?.Rule ?? r.Rule, Reason = reason?.Reason ?? r.Reason,
            }, JoinAdminRowMark.Changed));
        }

        foreach (var c in Changes)
            if (c.Kind == JoinAdminChangeKind.RequiredAdd)
                list.Add(new JoinAdminDraftRow<JoinAdminClientRow>(new JoinAdminClientRow
                {
                    Id = c.Id, Package = c.Package, Version = c.Version, Name = c.Name, Reason = c.Reason,
                    Rule = c.Rule.Length > 0 ? c.Rule : ClientModRules.WordAtLeast,
                }, JoinAdminRowMark.Added));
        return list;
    }

    public List<JoinAdminDraftRow<JoinAdminClientRow>> ApprovedRows()
    {
        var list = new List<JoinAdminDraftRow<JoinAdminClientRow>>();
        if (Base == null) return list;
        foreach (var r in Base.Approved)
            list.Add(new JoinAdminDraftRow<JoinAdminClientRow>(r,
                Find(JoinAdminChangeKind.ApproveRemove, r.Id, r.Package) >= 0 ? JoinAdminRowMark.Removed : JoinAdminRowMark.None));
        foreach (var c in Changes)
            if (c.Kind == JoinAdminChangeKind.ApproveAdd)
                list.Add(new JoinAdminDraftRow<JoinAdminClientRow>(new JoinAdminClientRow
                {
                    Id = c.Id, Package = c.Package, Version = c.Version, Name = c.Name, Rule = c.Rule.Length > 0 ? c.Rule : ClientModRules.WordAny,
                }, JoinAdminRowMark.Added));
        return list;
    }

    private List<JoinAdminRecRow> RecommendedView()
    {
        var list = new List<JoinAdminRecRow>();
        foreach (var r in RecommendedRows())
            if (r.Mark != JoinAdminRowMark.Removed) list.Add(r.Row);
        return list;
    }

    private List<JoinAdminClientRow> RequiredView()
    {
        var list = new List<JoinAdminClientRow>();
        foreach (var r in RequiredRows())
            if (r.Mark != JoinAdminRowMark.Removed) list.Add(r.Row);
        return list;
    }

    /// <summary>
    /// The state as it will be after Apply (removed rows left out): what the pickers and Approve everything leave out,
    /// and what the draft readback reports.
    /// </summary>
    public JoinAdminState? Effective()
    {
        var s = Base;
        if (s == null) return null;
        var e = new JoinAdminState
        {
            WindowSeconds = WindowSeconds, RefusedJoins = s.RefusedJoins, RefusedDays = s.RefusedDays, PolicyMode = Word(JoinAdminChangeKind.PolicyMode),
            Unverified = Word(JoinAdminChangeKind.Unverified), MidSession = Word(JoinAdminChangeKind.MidSession), ApprovedFrom = s.ApprovedFrom,
            ApprovedTotal = ApprovedTotalAfter(), PolicySupported = s.PolicySupported, OptionsSupported = s.OptionsSupported,
            MatchingSupported = s.MatchingSupported, VersionMatching = Word(JoinAdminChangeKind.VersionMatching), VersionMatchingForced = s.VersionMatchingForced,
            ProgenitorExactLine = s.ProgenitorExactLine,
        };
        foreach (var r in ServerModRows()) e.ServerMods.Add(r.Row);
        e.Recommended.AddRange(RecommendedView());
        e.RequiredClient.AddRange(RequiredView());
        foreach (var r in ApprovedRows())
            if (r.Mark != JoinAdminRowMark.Removed) e.Approved.Add(r.Row);
        e.Required.AddRange(s.Required);
        e.Alert.AddRange(s.Alert);
        foreach (var pair in s.Options) e.Options[pair.Key] = pair.Value;
        e.Lock.AddRange(s.Lock);
        return e;
    }

    // ---- the Changes list -----------------------------------------------------------------------------------------

    /// <summary>The Changes list's lines in plain words, one per change, in the order staged.</summary>
    public List<string> Lines()
    {
        var lines = new List<string>(Changes.Count);
        foreach (var c in Changes) lines.Add(LineOf(c));
        return lines;
    }

    /// <summary>One change in plain words, with its conflict or the server's refusal after it.</summary>
    internal static string LineOf(JoinAdminChange c)
    {
        string line = Describe(c);
        if (c.Error.Length > 0) line += " (not applied: " + c.Error.TrimEnd('.') + ")";
        else if (c.Conflict.Length > 0) line += " (" + c.Conflict + ")";
        return line;
    }

    /// <summary>
    /// The change alone: <c>Require Progenitor Hot Demo (0.1.0 or newer), reason 'We &lt;3 hot demo'</c>,
    /// <c>BasinHoverTips: 1.0.19 or newer → exact 1.0.19</c>, <c>Mod rules: Any mods → Only approved mods</c>.
    /// </summary>
    internal static string Describe(JoinAdminChange c)
    {
        string name = c.DisplayName;
        switch (c.Kind)
        {
            case JoinAdminChangeKind.RecommendAdd:
                return "Recommend " + name + (c.Version.Length > 0 ? " (" + c.Version + ")" : "") + ReasonPart(c.Reason);
            case JoinAdminChangeKind.RecommendRemove:
                return "Stop recommending " + name;
            case JoinAdminChangeKind.RecommendReason:
            case JoinAdminChangeKind.RequiredReason:
                return name + ": reason " + Quote(c.Before) + " → " + Quote(StoredReason(c.Reason));
            case JoinAdminChangeKind.RequiredAdd:
            {
                var rule = c.Rule.Length > 0 && ClientModRules.TryParse(c.Rule, out var r) ? r : ClientModRule.AtLeast;
                return "Require " + name + " (" + Lower(ClientModRules.Label(rule, c.Version)) + ")" + ReasonPart(c.Reason)
                    + (c.MovesRecommended ? "; moves it from Recommended" : "");
            }
            case JoinAdminChangeKind.RequiredRemove:
                return "Unrequire " + name;
            case JoinAdminChangeKind.RequiredRule:
            {
                var before = ClientModRules.TryParse(c.Before, out var b) ? b : ClientModRule.AtLeast;
                var after = ClientModRules.TryParse(c.Rule, out var a) ? a : ClientModRule.AtLeast;
                return name + ": " + Lower(ClientModRules.Label(before, c.BeforeVersion)) + " → " + Lower(ClientModRules.Label(after, c.Version));
            }
            case JoinAdminChangeKind.ApproveAdd:
                return "Approve " + name;
            case JoinAdminChangeKind.ApproveRemove:
                return "Remove " + name + " from the approved list";
            case JoinAdminChangeKind.PolicyMode:
                return "Mod rules: " + OptionLabel(ClientPolicyText.ModeOptions, c.Before) + " → " + OptionLabel(ClientPolicyText.ModeOptions, c.Value);
            case JoinAdminChangeKind.Unverified:
                return "Mods can't be checked: " + OptionLabel(ClientPolicyText.UnverifiedOptions, c.Before) + " → " + OptionLabel(ClientPolicyText.UnverifiedOptions, c.Value);
            case JoinAdminChangeKind.MidSession:
                return "Mods break the rules while playing: " + OptionLabel(ClientPolicyText.MidSessionOptions, c.Before) + " → "
                    + OptionLabel(ClientPolicyText.MidSessionOptions, c.Value);
            case JoinAdminChangeKind.Window:
                return "Install window: " + c.Before + " → " + c.Value + " seconds";
            case JoinAdminChangeKind.VersionMatching:
                return VersionMatchingText.Heading + ": " + MatchLabel(c.Before) + " → " + MatchLabel(c.Value);
            case JoinAdminChangeKind.ServerModRule:
                return name + ": " + Lower(VersionMatchText.RuleLabel(c.Before, c.Version)) + " → " + Lower(VersionMatchText.RuleLabel(c.Rule, c.Version));
            default:
                return name;
        }
    }

    private static string MatchLabel(string word) =>
        VersionMatchingText.TryParse(word, out var mode) ? VersionMatchingText.Label(mode) : word;

    private static string ReasonPart(string reason)
    {
        string t = (reason ?? "").Trim();
        return t.Length > 0 ? ", reason " + Quote(t) : "";
    }

    private static string Quote(string text) => "'" + (text ?? "") + "'";

    private static string Lower(string label) =>
        label.Length > 0 && !char.IsDigit(label[0]) ? char.ToLowerInvariant(label[0]) + label.Substring(1) : label;

    private static string OptionLabel(IReadOnlyList<Progenitor.Ui.UiChoiceOption> options, string value)
    {
        foreach (var o in options)
            if (string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)) return o.Label;
        return value;
    }

    private static string Clip(string? name)
    {
        string n = (name ?? "").Trim();
        return n.Length > JoinSectionLimits.MaxNameChars ? n.Substring(0, JoinSectionLimits.MaxNameChars) : n;
    }

    // ---- rebase ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The server's state moved (an answer, a refresh, another admin's edit): take it as the new base. A change the
    /// server now already has is dropped; one whose value another admin changed keeps ours and says so; an add of a row
    /// another admin added too becomes an edit of that row. Null (no state, or no longer an admin): no draft at all.
    /// </summary>
    public void Rebase(JoinAdminState? state)
    {
        if (state == null)
        {
            Reset(null);
            return;
        }

        Base = state;
        for (int i = Changes.Count - 1; i >= 0; i--)
        {
            var c = Changes[i];
            switch (c.Kind)
            {
                case JoinAdminChangeKind.RecommendAdd:
                {
                    var row = FindRec(state, c.Id, c.Package);
                    if (row == null) break;
                    if (row.FromFile || string.Equals(row.Reason, StoredReason(c.Reason), StringComparison.Ordinal) || c.Reason.Trim().Length == 0)
                    {
                        Changes.RemoveAt(i);
                        break;
                    }

                    c.Kind = JoinAdminChangeKind.RecommendReason;
                    c.Id = row.Id;
                    c.Package = row.Package;
                    c.Before = row.Reason;
                    c.Conflict = "another admin recommended it too; Apply sets your reason";
                    break;
                }

                case JoinAdminChangeKind.RecommendRemove:
                    if (FindRec(state, c.Id, c.Package) is not { FromFile: false }) Changes.RemoveAt(i);
                    break;

                case JoinAdminChangeKind.RecommendReason:
                {
                    var row = FindRec(state, c.Id, c.Package);
                    if (row == null) { c.Conflict = ConflictRemoved; break; }
                    if (string.Equals(row.Reason, StoredReason(c.Reason), StringComparison.Ordinal)) { Changes.RemoveAt(i); break; }
                    if (!string.Equals(row.Reason, c.Before, StringComparison.Ordinal))
                    {
                        c.Conflict = "another admin changed the reason to " + Quote(row.Reason);
                        c.Before = row.Reason;
                    }

                    break;
                }

                case JoinAdminChangeKind.RequiredAdd:
                {
                    var row = FindClient(state.RequiredClient, c.Id, c.Package);
                    if (row == null) break;
                    Changes.RemoveAt(i);
                    string conflict = "another admin required it too; Apply sets yours";
                    if (c.Reason.Trim().Length > 0 && !string.Equals(row.Reason, StoredReason(c.Reason), StringComparison.Ordinal))
                        Changes.Insert(i, new JoinAdminChange
                        {
                            Kind = JoinAdminChangeKind.RequiredReason, Id = row.Id, Package = row.Package, Name = row.Name, Reason = c.Reason, Before = row.Reason,
                            Conflict = conflict,
                        });
                    if (c.Rule.Length > 0 && ClientModRules.TryParse(c.Rule, out var want)
                        && (row.RuleValue != want || (c.Version.Length > 0 && !string.Equals(row.Version, c.Version, StringComparison.Ordinal))))
                        Changes.Insert(i, new JoinAdminChange
                        {
                            Kind = JoinAdminChangeKind.RequiredRule, Id = row.Id, Package = row.Package, Name = row.Name, Rule = ClientModRules.Word(want),
                            Version = c.Version.Length > 0 ? c.Version : row.Version, Before = row.Rule, BeforeVersion = row.Version, Conflict = conflict,
                        });
                    break;
                }

                case JoinAdminChangeKind.RequiredRemove:
                    if (FindClient(state.RequiredClient, c.Id, c.Package) == null) Changes.RemoveAt(i);
                    break;

                case JoinAdminChangeKind.RequiredReason:
                {
                    var row = FindClient(state.RequiredClient, c.Id, c.Package);
                    if (row == null) { c.Conflict = ConflictRemoved; break; }
                    if (string.Equals(row.Reason, StoredReason(c.Reason), StringComparison.Ordinal)) { Changes.RemoveAt(i); break; }
                    if (!string.Equals(row.Reason, c.Before, StringComparison.Ordinal))
                    {
                        c.Conflict = "another admin changed the reason to " + Quote(row.Reason);
                        c.Before = row.Reason;
                    }

                    break;
                }

                case JoinAdminChangeKind.RequiredRule:
                {
                    var row = FindClient(state.RequiredClient, c.Id, c.Package);
                    if (row == null) { c.Conflict = ConflictRemoved; break; }
                    bool same = ClientModRules.TryParse(c.Rule, out var want) && row.RuleValue == want && string.Equals(row.Version, c.Version, StringComparison.Ordinal);
                    if (same) { Changes.RemoveAt(i); break; }
                    if (!string.Equals(ClientModRules.Word(row.RuleValue), ClientModRules.TryParse(c.Before, out var b) ? ClientModRules.Word(b) : c.Before, StringComparison.Ordinal)
                        || !string.Equals(row.Version, c.BeforeVersion, StringComparison.Ordinal))
                    {
                        c.Conflict = "another admin changed it to " + Lower(ClientModRules.Label(row.RuleValue, row.Version));
                        c.Before = row.Rule;
                        c.BeforeVersion = row.Version;
                    }

                    break;
                }

                case JoinAdminChangeKind.ApproveAdd:
                    if (FindClient(state.Approved, c.Id, c.Package) != null) Changes.RemoveAt(i);
                    break;

                case JoinAdminChangeKind.ServerModRule:
                {
                    // C3: a mod the server no longer lists (it stopped running it and holds no rule for it) drops the change.
                    var row = BaseServerMod(c.Id);
                    if (row == null || (!row.Runs && c.Rule.Length > 0)) { Changes.RemoveAt(i); break; }
                    if (string.Equals(row.Rule, c.Rule, StringComparison.OrdinalIgnoreCase)) { Changes.RemoveAt(i); break; }
                    if (!string.Equals(row.Rule, c.Before, StringComparison.OrdinalIgnoreCase))
                    {
                        c.Conflict = "another admin changed it to " + Lower(VersionMatchText.RuleLabel(row.Rule, row.Version));
                        c.Before = row.Rule;
                    }

                    c.Version = row.Version;
                    break;
                }

                case JoinAdminChangeKind.ApproveRemove:
                    // The answer carries a window of the approved rows: only a whole list proves a row is gone.
                    if (FindClient(state.Approved, c.Id, c.Package) == null && state.ApprovedFrom == 0 && state.Approved.Count >= state.ApprovedTotal)
                        Changes.RemoveAt(i);
                    break;

                default:
                {
                    string stored = BaseWord(c.Kind);
                    if (stored.Length == 0 && c.Kind is JoinAdminChangeKind.MidSession or JoinAdminChangeKind.VersionMatching) { Changes.RemoveAt(i); break; }
                    if (string.Equals(stored, c.Value, StringComparison.OrdinalIgnoreCase)) { Changes.RemoveAt(i); break; }
                    if (!string.Equals(stored, c.Before, StringComparison.OrdinalIgnoreCase))
                    {
                        c.Conflict = "another admin changed it to " + (c.Kind == JoinAdminChangeKind.Window ? stored + " seconds" : ChoiceLabel(c.Kind, stored));
                        c.Before = stored;
                    }

                    break;
                }
            }
        }
    }

    private static string ChoiceLabel(JoinAdminChangeKind kind, string word) => kind switch
    {
        JoinAdminChangeKind.PolicyMode => OptionLabel(ClientPolicyText.ModeOptions, word),
        JoinAdminChangeKind.Unverified => OptionLabel(ClientPolicyText.UnverifiedOptions, word),
        JoinAdminChangeKind.MidSession => OptionLabel(ClientPolicyText.MidSessionOptions, word),
        JoinAdminChangeKind.VersionMatching => MatchLabel(word),
        _ => word,
    };

    // ---- Apply ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// What Apply sends, in order: removals first (they free room under the caps), then adds, then edits of rows, then
    /// approvals (two or more plain ones as <c>approve-bulk</c>, <see cref="JoinAdminWire.BulkRequests"/>), then the
    /// choices (so <b>Only approved mods</b> lands after the approvals it relies on), then the install window.
    /// </summary>
    public List<JoinAdminApplyStep> Steps()
    {
        var steps = new List<JoinAdminApplyStep>();
        void Each(params JoinAdminChangeKind[] kinds)
        {
            foreach (var c in Changes)
                if (Array.IndexOf(kinds, c.Kind) >= 0) steps.Add(new JoinAdminApplyStep(RequestFor(c), new List<JoinAdminChange> { c }));
        }

        Each(JoinAdminChangeKind.RecommendRemove, JoinAdminChangeKind.RequiredRemove, JoinAdminChangeKind.ApproveRemove);
        Each(JoinAdminChangeKind.RequiredAdd, JoinAdminChangeKind.RecommendAdd);
        Each(JoinAdminChangeKind.RequiredReason, JoinAdminChangeKind.RequiredRule, JoinAdminChangeKind.RecommendReason);

        var plain = new List<JoinAdminChange>();
        foreach (var c in Changes)
        {
            if (c.Kind != JoinAdminChangeKind.ApproveAdd) continue;
            if (c.Rule.Length == 0 && c.Version.Length == 0) plain.Add(c);
            else steps.Add(new JoinAdminApplyStep(RequestFor(c), new List<JoinAdminChange> { c }));
        }

        if (plain.Count == 1) steps.Add(new JoinAdminApplyStep(RequestFor(plain[0]), plain));
        else if (plain.Count > 1)
        {
            var items = new List<JoinAdminBulkItem>(plain.Count);
            foreach (var c in plain) items.Add(new JoinAdminBulkItem { Id = c.Id, Package = c.Package, Name = c.Name });
            int at = 0;
            foreach (var request in JoinAdminWire.BulkRequests(items))
            {
                var covered = plain.GetRange(at, request.Items.Count);
                at += request.Items.Count;
                steps.Add(new JoinAdminApplyStep(request, covered) { Bulk = true });
            }
        }

        Each(JoinAdminChangeKind.PolicyMode, JoinAdminChangeKind.Unverified, JoinAdminChangeKind.MidSession);
        // C3: Version matching, then the per-mod rules (each wins over it for its mod).
        Each(JoinAdminChangeKind.VersionMatching);
        Each(JoinAdminChangeKind.ServerModRule);
        Each(JoinAdminChangeKind.Window);
        return steps;
    }

    /// <summary>The existing request of one change (the same operations the panel sent at once before).</summary>
    internal static JoinAdminRequest RequestFor(JoinAdminChange c) => c.Kind switch
    {
        JoinAdminChangeKind.RecommendAdd => new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Id = c.Id, Package = c.Package, Version = c.Version, Reason = c.Reason, Name = c.Name },
        JoinAdminChangeKind.RecommendRemove => new JoinAdminRequest { Op = JoinAdminOps.RecommendRemove, Id = c.Id, Package = c.Package },
        JoinAdminChangeKind.RecommendReason => new JoinAdminRequest { Op = JoinAdminOps.RecommendReason, Id = c.Id, Package = c.Package, Reason = c.Reason },
        JoinAdminChangeKind.RequiredAdd => new JoinAdminRequest
        {
            Op = JoinAdminOps.RequiredAdd, Id = c.Id, Package = c.Package, Version = c.Version, Rule = c.Rule, Reason = c.Reason, Name = c.Name,
        },
        JoinAdminChangeKind.RequiredRemove => new JoinAdminRequest { Op = JoinAdminOps.RequiredRemove, Id = c.Id, Package = c.Package },
        JoinAdminChangeKind.RequiredReason => new JoinAdminRequest { Op = JoinAdminOps.RequiredReason, Id = c.Id, Package = c.Package, Reason = c.Reason },
        JoinAdminChangeKind.RequiredRule => new JoinAdminRequest { Op = JoinAdminOps.RequiredRule, Id = c.Id, Package = c.Package, Rule = c.Rule, Version = c.Version },
        JoinAdminChangeKind.ApproveAdd => new JoinAdminRequest { Op = JoinAdminOps.ApproveAdd, Id = c.Id, Package = c.Package, Version = c.Version, Rule = c.Rule, Name = c.Name },
        JoinAdminChangeKind.ApproveRemove => new JoinAdminRequest { Op = JoinAdminOps.ApproveRemove, Id = c.Id, Package = c.Package },
        JoinAdminChangeKind.PolicyMode => new JoinAdminRequest { Op = JoinAdminOps.PolicyMode, Value = c.Value },
        JoinAdminChangeKind.Unverified => new JoinAdminRequest { Op = JoinAdminOps.Unverified, Value = c.Value },
        JoinAdminChangeKind.MidSession => new JoinAdminRequest { Op = JoinAdminOps.MidSession, Value = c.Value },
        JoinAdminChangeKind.VersionMatching => new JoinAdminRequest { Op = JoinAdminOps.VersionMatching, Value = c.Value },
        JoinAdminChangeKind.ServerModRule => new JoinAdminRequest
        {
            Op = JoinAdminOps.ServerModRule, Id = c.Id, Rule = c.Rule.Length > 0 ? c.Rule : VersionMatchText.WordDefault, Name = c.Name,
        },
        _ => new JoinAdminRequest
        {
            Op = JoinAdminOps.Window, Seconds = int.TryParse(c.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0,
        },
    };

    /// <summary>The bridge's readback of the changes.</summary>
    public List<object?> Describe()
    {
        var list = new List<object?>(Changes.Count);
        for (int i = 0; i < Changes.Count; i++)
        {
            var c = Changes[i];
            list.Add(new Dictionary<string, object?>
            {
                ["index"] = i, ["kind"] = KindName(c.Kind), ["op"] = RequestFor(c).Op, ["key"] = c.Key, ["name"] = c.DisplayName,
                ["line"] = LineOf(c), ["text"] = Describe(c), ["conflict"] = c.Conflict.Length > 0 ? c.Conflict : null,
                ["error"] = c.Error.Length > 0 ? c.Error : null,
            });
        }

        return list;
    }

    internal static string KindName(JoinAdminChangeKind kind) => kind switch
    {
        JoinAdminChangeKind.RecommendAdd => "recommend",
        JoinAdminChangeKind.RecommendRemove => "unrecommend",
        JoinAdminChangeKind.RecommendReason => "reason",
        JoinAdminChangeKind.RequiredAdd => "require",
        JoinAdminChangeKind.RequiredRemove => "unrequire",
        JoinAdminChangeKind.RequiredReason => "require-reason",
        JoinAdminChangeKind.RequiredRule => "require-rule",
        JoinAdminChangeKind.ApproveAdd => "approve",
        JoinAdminChangeKind.ApproveRemove => "unapprove",
        JoinAdminChangeKind.PolicyMode => "policy-mode",
        JoinAdminChangeKind.Unverified => "unverified",
        JoinAdminChangeKind.MidSession => "mid-session",
        JoinAdminChangeKind.VersionMatching => "version-matching",
        JoinAdminChangeKind.ServerModRule => "server-mod-rule",
        _ => "window",
    };

    internal static string MarkName(JoinAdminRowMark mark) => mark switch
    {
        JoinAdminRowMark.Added => "added",
        JoinAdminRowMark.Removed => "removed",
        JoinAdminRowMark.Changed => "changed",
        _ => "",
    };
}

/// <summary>JOINDRAFT1: what Close (and Escape on the main page) does.</summary>
internal enum JoinAdminCloseAction
{
    /// <summary>Nothing staged (or the connection ended): close.</summary>
    Close,

    /// <summary>Unapplied changes: ask "Apply your N changes?" (Apply / Discard / Cancel).</summary>
    Ask,

    /// <summary>Apply is sending: stay open until it ends.</summary>
    Wait,
}

/// <summary>JOINDRAFT1: the close rule, game-free.</summary>
internal static class JoinAdminCloseRule
{
    internal static JoinAdminCloseAction Decide(bool hasChanges, bool applying, bool force) =>
        force ? JoinAdminCloseAction.Close : applying ? JoinAdminCloseAction.Wait : hasChanges ? JoinAdminCloseAction.Ask : JoinAdminCloseAction.Close;
}

/// <summary>JOINDRAFT1: one request Apply sends and the staged changes it carries (several for an <c>approve-bulk</c>).</summary>
internal sealed class JoinAdminApplyStep
{
    public JoinAdminApplyStep(JoinAdminRequest request, List<JoinAdminChange> changes)
    {
        Request = request;
        Changes = changes;
    }

    public JoinAdminRequest Request { get; }
    public List<JoinAdminChange> Changes { get; }
    public bool Bulk { get; set; }
}

/// <summary>
/// JOINDRAFT1: Apply. Sends the draft's <see cref="JoinAdminDraft.Steps"/> one at a time (the client allows only a few
/// requests waiting), each after the previous answer. An accepted change leaves the draft; a refused one stays with the
/// server's reason (<see cref="JoinAdminChange.Error"/>). No answer stops the run (the rest stay staged). Then one
/// <c>get</c> refreshes the state. Game-free: <paramref name="send"/> is the client channel (null when sent, else why not).
/// </summary>
internal sealed class JoinAdminApplyRun
{
    internal const string NoAnswer = "No answer from the server.";

    private readonly JoinAdminDraft _draft;
    private readonly List<JoinAdminApplyStep> _steps;
    private readonly Func<JoinAdminRequest, Action<JoinAdminResponse?>, string?> _send;
    private readonly Action<JoinAdminApplyRun>? _done;
    private int _next;

    public JoinAdminApplyRun(JoinAdminDraft draft, Func<JoinAdminRequest, Action<JoinAdminResponse?>, string?> send, Action<JoinAdminApplyRun>? done = null)
    {
        _draft = draft;
        _steps = draft.Steps();
        _send = send;
        _done = done;
        foreach (var s in _steps)
            foreach (var c in s.Changes) c.Error = "";
        Total = 0;
        foreach (var s in _steps) Total += s.Changes.Count;
    }

    public int Total { get; }
    public int Applied { get; private set; }
    public int Failed { get; private set; }
    public bool Running { get; private set; }
    public bool Finished { get; private set; }

    /// <summary>One line per change: <c>ok: Require X</c> or <c>refused: Require X: the server's reason</c>.</summary>
    public List<string> Results { get; } = new();

    public IReadOnlyList<JoinAdminApplyStep> StepsSent => _steps;

    public void Start()
    {
        if (Running || Finished) return;
        Running = true;
        Next();
    }

    private void Next()
    {
        while (_next < _steps.Count)
        {
            var step = _steps[_next++];
            string? refused;
            try { refused = _send(step.Request, r => OnAnswer(step, r)); }
            catch (Exception e) { refused = e.Message; }
            if (refused == null) return;
            foreach (var c in step.Changes) Fail(c, refused);
        }

        Finish();
    }

    private void OnAnswer(JoinAdminApplyStep step, JoinAdminResponse? response)
    {
        if (!Running) return;
        if (response == null)
        {
            foreach (var c in step.Changes) Fail(c, NoAnswer);
            // The server stopped answering: what is left stays staged, not sent.
            while (_next < _steps.Count)
                foreach (var c in _steps[_next++].Changes) Fail(c, "not sent: " + NoAnswer);
            Finish();
            return;
        }

        if (step.Bulk)
        {
            // approve-bulk answers once for every row: a row is applied when the state now lists it (or the answer's
            // window can't show it), else it stays with the answer's words.
            var s = response.State;
            bool whole = s != null && s.ApprovedFrom == 0 && s.Approved.Count >= s.ApprovedTotal;
            foreach (var c in step.Changes)
            {
                bool listed = false;
                if (s != null)
                    foreach (var r in s.Approved)
                        if (c.SameMod(r.Id, r.Package)) listed = true;
                if (response.Ok && (listed || !whole)) Succeed(c);
                else Fail(c, response.Error.Length > 0 ? response.Error : response.Note.Length > 0 ? response.Note : "Not approved.");
            }
        }
        else if (response.Ok)
        {
            foreach (var c in step.Changes) Succeed(c);
        }
        else
        {
            foreach (var c in step.Changes) Fail(c, response.Error.Length > 0 ? response.Error : "Refused.");
        }

        Next();
    }

    private void Succeed(JoinAdminChange c)
    {
        Applied++;
        Results.Add("ok: " + JoinAdminDraft.Describe(c));
        _draft.Changes.Remove(c);
    }

    private void Fail(JoinAdminChange c, string why)
    {
        Failed++;
        c.Error = why ?? "";
        Results.Add("refused: " + JoinAdminDraft.Describe(c) + ": " + c.Error);
    }

    private void Finish()
    {
        if (Finished) return;
        Running = false;
        Finished = true;
        try { _send(new JoinAdminRequest { Op = JoinAdminOps.Get }, _ => { }); }
        catch { /* the next answer refreshes it */ }
        _done?.Invoke(this);
    }

    /// <summary>The status line after a run.</summary>
    public string Summary()
    {
        if (Total == 0) return "Nothing to apply.";
        if (Failed == 0) return "Applied " + (Applied == 1 ? "1 change" : Applied.ToString(CultureInfo.InvariantCulture) + " changes") + ". Players see it on their next join.";
        return "Applied " + Applied.ToString(CultureInfo.InvariantCulture) + " of " + Total.ToString(CultureInfo.InvariantCulture)
            + "; " + (Failed == 1 ? "1 change was not applied and stays" : Failed.ToString(CultureInfo.InvariantCulture) + " changes were not applied and stay")
            + " below.";
    }
}
