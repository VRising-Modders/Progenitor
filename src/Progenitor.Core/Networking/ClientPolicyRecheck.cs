using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>CLIENTPOLICY3 (docs/client-mod-policy.md, "Stage 3 as built"): the re-check's timings and caps.</summary>
internal static class ClientPolicyRecheckLimits
{
    /// <summary>
    /// REQRULE5 (requested 2026-10-08): the default of <c>[ClientPolicy] DisconnectGraceSeconds</c>, the seconds from the
    /// notice to the disconnect under Disconnect them (was a fixed 15).
    /// </summary>
    internal const int DefaultDisconnectGraceSeconds = 60;

    /// <summary>REQRULE5: the setting's range (an out-of-range value is clamped).</summary>
    internal const int MinDisconnectGraceSeconds = 10;

    /// <inheritdoc cref="MinDisconnectGraceSeconds"/>
    internal const int MaxDisconnectGraceSeconds = 300;

    /// <summary>REQRULE5: what a server from before the setting gives (its fixed grace); a client that never heard a value uses it.</summary>
    internal const int LegacyDisconnectGraceSeconds = 15;

    /// <summary>REQRULE5: the shortest grace for a mod you don't allow that was loaded before the admins changed the policy.</summary>
    internal const int MinPolicyChangeGraceSeconds = 60;

    /// <summary>REQRULE5 (server): the configured value (live: the cfg entry). Null: the default.</summary>
    internal static Func<int>? Configured { get; set; }

    /// <summary>A value clamped to the setting's range.</summary>
    internal static int Clamp(int seconds) =>
        seconds < MinDisconnectGraceSeconds ? MinDisconnectGraceSeconds : seconds > MaxDisconnectGraceSeconds ? MaxDisconnectGraceSeconds : seconds;

    /// <summary>
    /// Disconnect them: seconds from the notice to the disconnect for a mod loaded (or a required one removed or turned
    /// off) mid-session. This process's own setting (the server's; a client reads the joined server's through
    /// <see cref="ClientPolicyText.GraceSeconds"/>).
    /// </summary>
    internal static int DisconnectGraceSeconds
    {
        get
        {
            try { return Clamp(Configured?.Invoke() ?? DefaultDisconnectGraceSeconds); }
            catch { return DefaultDisconnectGraceSeconds; }
        }
    }

    /// <summary>
    /// Disconnect them: the grace when the problem is a mod you don't allow that was loaded before the admin changed the
    /// policy: the configured grace, but never under <see cref="MinPolicyChangeGraceSeconds"/> (a rule change must not
    /// give players less time than the old fixed 60 seconds; a longer setting applies to them too).
    /// </summary>
    internal static int PolicyChangeGraceSeconds => PolicyChangeGraceFor(DisconnectGraceSeconds);

    /// <inheritdoc cref="PolicyChangeGraceSeconds"/>
    internal static int PolicyChangeGraceFor(int grace) => Math.Max(grace, MinPolicyChangeGraceSeconds);

    /// <summary>A user's reports are judged at most this often; a report inside the floor is judged when it passes (the latest one).</summary>
    internal const double MinJudgeSeconds = 2;

    /// <summary>Mods one notice names at most (the rest are counted in the last line).</summary>
    internal const int MaxNoticeItems = 24;

    /// <summary>
    /// A disconnect notice is shown again on the main menu when it arrived at most this long before (the player may stay
    /// on the game's in-world disconnect banner a while before pressing Leave Game).
    /// </summary>
    internal const double ReshowAfterLeaveSeconds = 1800;
}

/// <summary>CLIENTPOLICY3: one problem a mid-session re-check found.</summary>
internal sealed class RecheckItem
{
    /// <summary>The mod's key: a plugin GUID (or package) for a not-allowed mod, the admins' row key for a required one.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><see cref="ClientModProblem"/>'s kinds: <c>not-allowed</c>, <c>missing</c>, <c>older</c>, <c>newer</c>, <c>mismatch</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>A not-allowed mod the report lists as hot-loaded by Progenitor (the per-server pause can unload it).</summary>
    public bool Pausable { get; set; }

    /// <summary>A not-allowed mod that was loaded before the admins last changed the policy.</summary>
    public bool PreChange { get; set; }

    /// <summary>A required row this player never met this session (the admins added it, or changed its rule, mid-session).</summary>
    public bool Added { get; set; }

    /// <summary>The required rule as words (<c>1.2.0 or newer</c>), or empty.</summary>
    public string Rule { get; set; } = "";

    /// <summary>The version the player has, or empty.</summary>
    public string Had { get; set; } = "";

    /// <summary>Disconnect the player: seconds left before this item disconnects (0 when it does not count toward one).</summary>
    public int Seconds { get; set; }

    /// <summary>
    /// SRVUNINST1 (client only, never on the wire): what this client has of a missing required mod, so the advice
    /// fits — turned off (turn it back on) or not installed (install it).
    /// </summary>
    public RecheckLocalState Local { get; set; }

    /// <summary>LOCALJOIN1 (client only): the configured local test index provides it.</summary>
    public bool LocalTestSource { get; set; }

    /// <summary>NOTICEOPEN1 (client only): this client's Install Mods catalog lists it (the tab can show its row).</summary>
    public bool InstallListed { get; set; }

    /// <summary>F41 chat (client only): a required mod this client paused for the server (installed; back when the server allows it).</summary>
    public bool PausedHere { get; set; }

    public bool NotAllowed => Kind == ClientModProblem.KindNotAllowed;

    /// <summary>What dedupes a notice: the kind and the key.</summary>
    public string Signature => Kind + ":" + Key.ToLowerInvariant();

    /// <summary>
    /// Counts toward a disconnect under Disconnect the player: a mod that isn't allowed, or a required one this player had
    /// and lost this session. A requirement the admins added mid-session applies from the next join instead.
    /// </summary>
    public bool KickWorthy => NotAllowed || !Added;
}

/// <summary>SRVUNINST1: this client's copy of a required mod the server no longer sees.</summary>
internal enum RecheckLocalState
{
    /// <summary>Not checked, or installed in some other state: the advice stays as the server's notice gives it.</summary>
    Unknown = 0,

    /// <summary>Installed and switched off (the Mods list's switch, or the preloader's next-start off).</summary>
    TurnedOff = 1,

    /// <summary>No copy on this client at all (uninstalled, or never installed).</summary>
    NotInstalled = 2,
}

/// <summary>CLIENTPOLICY3: which notice the server sends the player (the <c>action</c> on the wire).</summary>
internal enum RecheckNoticeKind
{
    None = 0,

    /// <summary>Warn and pause it: the mods named, nobody is disconnected.</summary>
    Warn = 1,

    /// <summary>Disconnect the player: the mods named, the seconds left before the disconnect.</summary>
    Disconnect = 2,

    /// <summary>The disconnect now (shown again on the menu after the leave, where the client can).</summary>
    Disconnected = 3,

    /// <summary>A disconnect that was announced is called off: nothing left breaks the rules.</summary>
    Resolved = 4,
}

/// <summary>CLIENTPOLICY3: what one re-judgment decided.</summary>
internal sealed class RecheckDecision
{
    /// <summary>Every problem now.</summary>
    public List<RecheckItem> Items { get; } = new();

    /// <summary>The problems not told to this player before.</summary>
    public List<RecheckItem> Fresh { get; } = new();

    public MidSessionPolicy Choice { get; set; }
    public string Trigger { get; set; } = "";

    public RecheckNoticeKind Notice { get; set; }

    /// <summary>What the notice names.</summary>
    public List<RecheckItem> NoticeItems { get; } = new();

    /// <summary>Seconds left before the disconnect (Disconnect notices).</summary>
    public int Seconds { get; set; }

    /// <summary>Arm the disconnect (Disconnect the player): due in <see cref="Seconds"/>, generation <see cref="KickGeneration"/>.</summary>
    public bool ArmKick { get; set; }

    public int KickGeneration { get; set; }

    /// <summary>An armed disconnect is called off.</summary>
    public bool CancelKick { get; set; }

    /// <summary>Disconnect now with <see cref="KickReason"/>.</summary>
    public bool KickNow { get; set; }

    public string KickReason { get; set; } = "";

    /// <summary>The decision in a word, for logs and the bridge.</summary>
    public string Word =>
        KickNow ? "disconnect-now" : ArmKick ? "disconnect-armed" : CancelKick ? "disconnect-canceled"
        : Notice == RecheckNoticeKind.Warn ? "warn" : Notice == RecheckNoticeKind.Disconnect ? "notice" : Items.Count > 0 ? "already-told" : "ok";

    /// <summary>One line for the server log.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var i in Items)
            parts.Add(i.Key + " (" + i.Kind + (i.Pausable ? ", pausable" : "") + (i.PreChange ? ", loaded before the policy change" : "")
                + (i.Added ? ", added mid-session" : "") + ")");
        return Word + " [" + ClientModRules.MidSessionWord(Choice) + ", " + Trigger + "]" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
    }
}

/// <summary>
/// CLIENTPOLICY3 (docs/client-mod-policy.md, "Stage 3 as built"), server: the mid-session re-check per connected user.
/// A fresh report (after a hot load or unload on the client), a policy change, or an armed disconnect's deadline is
/// judged with the join's own <see cref="ClientModJudge"/>; what happens then follows the admins' choice
/// (<see cref="ClientPolicyConfig.MidSession"/>):
/// <list type="bullet">
/// <item><b>Warn and pause it:</b> a notice naming each problem once (the client pauses the pausable ones); nobody is disconnected.</item>
/// <item><b>Disconnect the player:</b> a notice naming the mods and the seconds left, then a disconnect unless a later report
/// clears them (a paused mod clears itself). A mod loaded before the policy changed gets the longer grace
/// (<see cref="ClientPolicyRecheckLimits.PolicyChangeGraceSeconds"/>). A requirement the admins added mid-session is told,
/// never kicked for: it applies from the next join.</item>
/// </list>
/// Game-free; the live glue is <c>Handshake.ClientPolicy.cs</c>.
/// </summary>
internal sealed class ClientPolicyRecheckTracker
{
    private sealed class UserState
    {
        public readonly HashSet<string> PreChange = new(StringComparer.OrdinalIgnoreCase);

        // A user with no join record (a join not judged here) has no baseline: its first report is one.
        public bool AwaitBaseline = true;
        public readonly HashSet<string> RequiredSeenOk = new(StringComparer.Ordinal);
        public readonly Dictionary<string, double> ItemDue = new(StringComparer.Ordinal);
        public readonly HashSet<string> Told = new(StringComparer.Ordinal);
        public double KickDue = double.NaN;
        public int KickGeneration;
        public double LastJudged = double.NegativeInfinity;
        public bool Held;
        public int Judgments;
        public string LastWord = "";
        public string LastTrigger = "";
        public List<RecheckItem> Current = new();
    }

    private readonly Dictionary<int, UserState> _users = new();
    private readonly object _gate = new();

    private UserState Get(int user)
    {
        if (!_users.TryGetValue(user, out var s))
        {
            if (_users.Count >= 512) _users.Clear();
            _users[user] = s = new UserState();
        }

        return s;
    }

    /// <summary>
    /// A join passed the policy: start this session's record (the required rows the report meets count as met). A join let
    /// in without a report (Let them join, or a report that came after the answer deadline) has no baseline yet: its first
    /// report is that baseline, everything in it counting as loaded before (never as a mid-session load).
    /// </summary>
    public void OnJoinAccepted(int user, ClientPolicyConfig? policy, ClientModReport? report)
    {
        lock (_gate)
        {
            _users.Remove(user);
            var s = Get(user);
            s.AwaitBaseline = report == null;
            NoteRequiredMet(s, policy, report, null);
        }
    }

    /// <summary>
    /// The admins changed the policy: everything this player has loaded now was loaded before the change. With no report
    /// on record (a player the server never asked), the first report that comes is that baseline. A required row met
    /// earlier counts as met only while it is still in the policy with the same rule and version (<paramref name="policy"/>,
    /// the new one), so a row removed and added again, or tightened, is a requirement added mid-session.
    /// </summary>
    public void OnPolicyChange(int user, ClientModReport? report, ClientPolicyConfig? policy = null)
    {
        lock (_gate)
        {
            var s = Get(user);
            if (policy != null) PruneRequiredMet(s, policy);
            s.PreChange.Clear();
            if (report == null)
            {
                s.AwaitBaseline = true;
                return;
            }

            s.AwaitBaseline = false;
            foreach (string k in Keys(report)) s.PreChange.Add(k);
        }
    }

    /// <summary>
    /// The disconnect's last check, just before it happens: the items that still count toward it in
    /// <paramref name="report"/> (the latest), without changing anything. Empty: a clean report arrived in between.
    /// </summary>
    public List<RecheckItem> KickWorthyNow(int user, ClientPolicyConfig? policy, ClientModReport? report, ClientModServerFacts? facts)
    {
        lock (_gate)
        {
            var list = new List<RecheckItem>();
            if (policy == null || policy.MidSession != MidSessionPolicy.Disconnect || !_users.TryGetValue(user, out var s)) return list;
            foreach (var i in Classify(s, policy, report, facts, out _))
                if (i.KickWorthy) list.Add(i);
            return list;
        }
    }

    /// <summary>
    /// The debounce floor: true when <paramref name="user"/> may be judged now. Otherwise <paramref name="wait"/> is the
    /// seconds until it may, and the caller schedules one held judgment (false with <paramref name="wait"/> below 0 when
    /// one is already held).
    /// </summary>
    public bool Admit(int user, double now, out double wait)
    {
        lock (_gate)
        {
            var s = Get(user);
            wait = 0;
            double left = s.LastJudged + ClientPolicyRecheckLimits.MinJudgeSeconds - now;
            if (left <= 0 || now < s.LastJudged) return true;
            if (s.Held)
            {
                wait = -1;
                return false;
            }

            s.Held = true;
            wait = left;
            return false;
        }
    }

    /// <summary>The held judgment runs: clear the mark.</summary>
    public void ReleaseHeld(int user)
    {
        lock (_gate)
            if (_users.TryGetValue(user, out var s)) s.Held = false;
    }

    /// <summary>
    /// Judge <paramref name="report"/> (the user's latest) mid-session. <paramref name="trigger"/>: <c>report</c>,
    /// <c>policy-change</c>, <c>deadline</c> (an armed disconnect is due) or <c>bridge</c>.
    /// </summary>
    public RecheckDecision Judge(int user, ClientPolicyConfig? policy, ClientModReport? report, ClientModServerFacts? facts, string trigger, double now)
    {
        var choice = policy?.MidSession ?? MidSessionPolicy.Warn;
        var d = new RecheckDecision { Choice = choice, Trigger = trigger ?? "" };
        lock (_gate)
        {
            var s = Get(user);
            s.LastJudged = now;
            s.Judgments++;
            s.LastTrigger = d.Trigger;
            if (policy != null) PruneRequiredMet(s, policy);
            d.Items.AddRange(Classify(s, policy, report, facts, out var verdict));
            if (report != null && s.AwaitBaseline)
            {
                // The first report of a join let in without one (or of a player asked after a policy change): its
                // contents were loaded before, never a mid-session load.
                s.AwaitBaseline = false;
                s.PreChange.Clear();
                foreach (string k in Keys(report)) s.PreChange.Add(k);
            }

            // A mod unloaded and loaded again is a new load: only keys still reported stay "before the change".
            if (report != null) s.PreChange.IntersectWith(Keys(report));
            if (verdict != null) NoteRequiredMet(s, policy, report, verdict);

            s.Current = new List<RecheckItem>(d.Items);
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in d.Items) current.Add(i.Signature);
            s.Told.IntersectWith(current);
            foreach (var i in d.Items)
                if (!s.Told.Contains(i.Signature)) d.Fresh.Add(i);

            var kickWorthy = new List<RecheckItem>();
            if (choice == MidSessionPolicy.Disconnect)
                foreach (var i in d.Items)
                    if (i.KickWorthy) kickWorthy.Add(i);
            bool armed = !double.IsNaN(s.KickDue);

            // Each item keeps its own deadline from when it was first seen: the configured grace (REQRULE5, 60 s by default)
            // for a mod loaded (or a required one lost) mid-session, at least 60 s for a mod you don't allow loaded before
            // the policy change (ClientPolicyRecheckLimits.PolicyChangeGraceFor). The disconnect is armed for the earliest.
            var keep = new HashSet<string>(StringComparer.Ordinal);
            bool added = false;
            foreach (var i in kickWorthy)
            {
                keep.Add(i.Signature);
                if (!s.ItemDue.ContainsKey(i.Signature))
                {
                    s.ItemDue[i.Signature] = now + GraceFor(i);
                    added = true;
                }

                i.Seconds = Math.Max(0, (int)Math.Ceiling(s.ItemDue[i.Signature] - now));
            }

            foreach (string sig in new List<string>(s.ItemDue.Keys))
                if (!keep.Contains(sig)) s.ItemDue.Remove(sig);

            var overdue = new List<RecheckItem>();
            foreach (var i in kickWorthy)
                if (now >= s.ItemDue[i.Signature] - 0.25) overdue.Add(i);

            if (overdue.Count > 0)
            {
                // An item's grace is over and it remains (the deadline's own judgment gets here in practice).
                d.KickNow = true;
                d.Notice = RecheckNoticeKind.Disconnected;
                d.NoticeItems.AddRange(overdue);
                d.KickReason = ClientPolicyNoticeText.KickReason(overdue);
                s.KickDue = double.NaN;
                s.ItemDue.Clear();
                s.KickGeneration++;
            }
            else if (kickWorthy.Count > 0)
            {
                double due = double.PositiveInfinity;
                foreach (var v in s.ItemDue.Values) due = Math.Min(due, v);
                if (!armed || Math.Abs(due - s.KickDue) > 0.001)
                {
                    // Armed (again) for the earliest item: a new one with less time left, or the earliest one gone.
                    s.KickDue = due;
                    s.KickGeneration++;
                    d.ArmKick = true;
                    d.KickGeneration = s.KickGeneration;
                }

                d.Seconds = Math.Max(1, (int)Math.Ceiling(due - now));
                if (!armed || added || d.Fresh.Count > 0)
                {
                    d.Notice = RecheckNoticeKind.Disconnect;
                    d.NoticeItems.AddRange(d.Items);
                }
            }
            else
            {
                s.ItemDue.Clear();
                if (armed)
                {
                    s.KickDue = double.NaN;
                    s.KickGeneration++;
                    d.CancelKick = true;
                    // Called off: either nothing is left (Resolved), or the admins switched to Warn (the mods are named again).
                    if (d.Items.Count == 0) d.Notice = RecheckNoticeKind.Resolved;
                    else
                    {
                        d.Notice = RecheckNoticeKind.Warn;
                        d.NoticeItems.AddRange(d.Items);
                    }
                }
                else if (d.Fresh.Count > 0)
                {
                    // Warn and pause it; or, under Disconnect, only requirements the admins added mid-session are left,
                    // which are told as a warning: nobody is disconnected for them.
                    d.Notice = RecheckNoticeKind.Warn;
                    d.NoticeItems.AddRange(d.Fresh);
                }
            }

            foreach (var i in d.NoticeItems) s.Told.Add(i.Signature);
            s.LastWord = d.Word;
            return d;
        }
    }

    /// <summary>The armed disconnect's own deadline: still this generation and due? (Then the caller re-judges with <c>deadline</c>.)</summary>
    public bool IsDue(int user, int generation, double now)
    {
        lock (_gate)
            return _users.TryGetValue(user, out var s) && s.KickGeneration == generation && !double.IsNaN(s.KickDue) && now >= s.KickDue - 0.25;
    }

    /// <summary>Seconds before the armed disconnect, or null when none is armed.</summary>
    public double? SecondsLeft(int user, double now)
    {
        lock (_gate)
            return _users.TryGetValue(user, out var s) && !double.IsNaN(s.KickDue) ? Math.Max(0, s.KickDue - now) : null;
    }

    /// <summary>F41: the armed disconnect's generation (it changes when the countdown is re-armed or called off), or null when none is armed.</summary>
    public int? ArmedGeneration(int user)
    {
        lock (_gate)
            return _users.TryGetValue(user, out var s) && !double.IsNaN(s.KickDue) ? s.KickGeneration : null;
    }

    /// <summary>F41: copies of the last judgment's problems that count toward the armed disconnect (empty when none is armed).</summary>
    public List<RecheckItem> ArmedItems(int user)
    {
        lock (_gate)
        {
            var list = new List<RecheckItem>();
            if (!_users.TryGetValue(user, out var s) || double.IsNaN(s.KickDue)) return list;
            foreach (var i in s.Current)
                if (i.KickWorthy && s.ItemDue.ContainsKey(i.Signature))
                    list.Add(new RecheckItem
                    {
                        Key = i.Key, Name = i.Name, Kind = i.Kind, Pausable = i.Pausable, PreChange = i.PreChange, Added = i.Added,
                        Rule = i.Rule, Had = i.Had, Seconds = i.Seconds,
                    });
            return list;
        }
    }

    public void Forget(int user)
    {
        lock (_gate) _users.Remove(user);
    }

    public void Clear()
    {
        lock (_gate) _users.Clear();
    }

    /// <summary>The bridge's readback for one user.</summary>
    public Dictionary<string, object?>? Describe(int user, double now)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(user, out var s)) return null;
            var items = new List<object?>();
            foreach (var i in s.Current)
                items.Add(new Dictionary<string, object?>
                {
                    ["key"] = i.Key, ["name"] = i.Name, ["kind"] = i.Kind, ["pausable"] = i.Pausable, ["preChange"] = i.PreChange,
                    ["added"] = i.Added, ["rule"] = i.Rule, ["had"] = i.Had, ["secondsLeft"] = i.Seconds,
                });
            return new Dictionary<string, object?>
            {
                ["judgments"] = s.Judgments,
                ["last"] = s.LastWord,
                ["lastTrigger"] = s.LastTrigger,
                ["problems"] = items,
                ["told"] = new List<string>(s.Told),
                ["disconnectIn"] = double.IsNaN(s.KickDue) ? null : Math.Max(0, s.KickDue - now),
                ["loadedBeforePolicyChange"] = new List<string>(s.PreChange),
                ["awaitingBaseline"] = s.AwaitBaseline,
            };
        }
    }

    /// <summary>The problems in <paramref name="report"/> as this user's record reads them (no change to the record).</summary>
    private static List<RecheckItem> Classify(UserState s, ClientPolicyConfig? policy, ClientModReport? report, ClientModServerFacts? facts,
        out ClientModVerdict? verdict)
    {
        verdict = null;
        var items = new List<RecheckItem>();
        if (policy == null || !policy.Active || report == null) return items;
        verdict = ClientModJudge.Judge(policy, report, facts ?? new ClientModServerFacts());
        foreach (var p in verdict.Required)
        {
            int at = policy.IndexOfRequired(p.Key);
            items.Add(new RecheckItem
            {
                Key = p.Key, Name = p.Name, Kind = p.Kind, Had = p.Had,
                Rule = p.Rule == ClientModRule.Any || p.Required.Length == 0 ? "" : ClientModRules.Label(p.Rule, p.Required),
                // Met this session under this exact row (key, rule, version); anything else was added or tightened since.
                Added = at < 0 || !s.RequiredSeenOk.Contains(RowSignature(policy.Required[at])),
            });
        }

        foreach (var p in verdict.NotAllowed)
        {
            var entry = report.FindGuid(p.Key) ?? report.FindPackage(p.Key);
            items.Add(new RecheckItem
            {
                Key = p.Key, Name = p.Name, Kind = p.Kind, Had = p.Had,
                Pausable = entry != null && entry.Source == ClientModSources.Progenitor,
                PreChange = s.AwaitBaseline || s.PreChange.Contains(p.Key),
            });
        }

        return items;
    }

    private static int GraceFor(RecheckItem i) =>
        i.NotAllowed && i.PreChange ? ClientPolicyRecheckLimits.PolicyChangeGraceSeconds : ClientPolicyRecheckLimits.DisconnectGraceSeconds;

    /// <summary>A required row as "met" remembers it: its key, its effective rule and its version.</summary>
    internal static string RowSignature(ClientModRow row) =>
        row.Key.ToLowerInvariant() + "|" + ClientModRules.Word(ClientModRules.Effective(row.Rule, row.Version)) + "|" + row.Version;

    /// <summary>Forget "met" for rows no longer in the policy as they were (removed, or their rule or version changed).</summary>
    private static void PruneRequiredMet(UserState s, ClientPolicyConfig policy)
    {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in policy.Required) rows.Add(RowSignature(row));
        s.RequiredSeenOk.IntersectWith(rows);
    }

    private static void NoteRequiredMet(UserState s, ClientPolicyConfig? policy, ClientModReport? report, ClientModVerdict? verdict)
    {
        if (policy == null || report == null) return;
        var failing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (verdict != null) foreach (var p in verdict.Required) failing.Add(p.Key);
        foreach (var row in policy.Required)
        {
            var entry = report.Find(row);
            if (entry == null || failing.Contains(row.Key)) continue;
            if (verdict == null && !ClientModRules.Passes(row.Rule, row.Version, entry.Version)) continue;
            s.RequiredSeenOk.Add(RowSignature(row));
        }
    }

    private static IEnumerable<string> Keys(ClientModReport report)
    {
        foreach (var e in report.Entries)
        {
            if (e.Guid.Length > 0) yield return e.Guid;
            if (e.Package.Length > 0) yield return e.Package;
        }
    }
}

/// <summary>
/// REQRULE2 / REQRULE5: the server's mid-session rule as it stands now, on the <c>'PrCm'</c> id (server to client): a version
/// byte, the kind byte <see cref="KindRule"/>, the rule (1 warn, 2 disconnect) and the Disconnect them grace in seconds (two
/// bytes, little-endian). Sent after an ok handshake and whenever the admins change the policy or the grace, only to a client
/// that declared <see cref="Handshake.Capability.PolicyRuleUpdates"/>; a later version may append bytes, which this reader
/// ignores. An older client is never sent it (it would drop the packet as "not a notice").
/// </summary>
internal static class ClientPolicyRuleWire
{
    internal const byte KindRule = 2;
    internal const int Bytes = 5;

    internal static byte[] Encode(MidSessionPolicy rule, int graceSeconds)
    {
        int g = ClientPolicyRecheckLimits.Clamp(graceSeconds);
        return new[]
        {
            ClientModReportWire.Version, KindRule, rule == MidSessionPolicy.Disconnect ? (byte)2 : (byte)1,
            (byte)(g & 0xFF), (byte)((g >> 8) & 0xFF),
        };
    }

    /// <summary>True when <paramref name="packet"/> has this kind byte (the caller then reads it here, never as a notice).</summary>
    internal static bool IsRule(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && packet[0] == ClientModReportWire.Version && packet[1] == KindRule;

    /// <summary>The rule and the grace (clamped). Fails closed: wrong kind, too short, or a rule byte this reader does not know.</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> packet, out ServerMidSessionRule rule, out int graceSeconds)
    {
        rule = ServerMidSessionRule.Unknown;
        graceSeconds = 0;
        if (!IsRule(packet) || packet.Length < Bytes || packet.Length > ClientModPolicyLimits.MaxReportPacketBytes) return false;
        rule = packet[2] switch { 1 => ServerMidSessionRule.Warn, 2 => ServerMidSessionRule.Disconnect, _ => ServerMidSessionRule.Unknown };
        if (rule == ServerMidSessionRule.Unknown) return false;
        graceSeconds = ClientPolicyRecheckLimits.Clamp(packet[3] | (packet[4] << 8));
        return true;
    }
}

/// <summary>
/// F41 chat (decided 2026-10-08): what a client said it has of one mod a held countdown names, so the chat line
/// gives the same fix as the notice (<see cref="ClientPolicyNoticeText.Reason"/>).
/// </summary>
internal sealed class HeldLocalFacts
{
    public RecheckLocalState State { get; set; }

    /// <summary>This client's Install Mods catalog lists it (NOTICEOPEN1).</summary>
    public bool Listed { get; set; }

    /// <summary>The configured local test source provides it (LOCALJOIN1).</summary>
    public bool LocalTest { get; set; }

    /// <summary>A required mod this client paused for the server: installed, and back as soon as the server allows it.</summary>
    public bool Paused { get; set; }

    /// <summary>The client-side facts of a notice item (null when it has none worth sending).</summary>
    internal static HeldLocalFacts? Of(RecheckItem i)
    {
        var f = new HeldLocalFacts { State = i.Local, Listed = i.InstallListed, LocalTest = i.LocalTestSource, Paused = i.PausedHere };
        return f.State == RecheckLocalState.Unknown && !f.Paused && !f.Listed && !f.LocalTest ? null : f;
    }
}

/// <summary>
/// F41 chat (decided 2026-10-08): the client's held-notice message, on the <c>'PrCm'</c> id (client to server): a
/// version byte, the kind byte <see cref="KindHeld"/>, then 1 when a disconnect countdown notice is held behind an open
/// confirmation (0 when none is any more), then optional UTF-8 JSON
/// <c>{"local":[{"id","state":"turned-off|not-installed|unknown","listed","localTest","paused"}]}</c>: what this client has
/// of the named mods, so the chat line gives the notice's own fix. Sent only to a server that declared
/// <see cref="Handshake.Capability.CountdownChat"/>; a report's chunk (kind 0) and this can't be mistaken for each other.
/// Ids are clipped to <see cref="ClientModPolicyLimits.MaxGuidChars"/>; the server matches on the same clipped form
/// (<see cref="KeyOf"/>).
/// </summary>
internal static class ClientPolicyHeldWire
{
    internal const byte KindHeld = 3;
    internal const int HeaderBytes = 3;
    internal const string StateTurnedOff = "turned-off";
    internal const string StateNotInstalled = "not-installed";
    internal const string StateUnknown = "unknown";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The key both ends use for an item: its id clipped as the wire carries it.</summary>
    internal static string KeyOf(string? key) => ClientModEntry.Clip(key, ClientModPolicyLimits.MaxGuidChars);

    /// <summary>The message. It names at most <see cref="ClientPolicyRecheckLimits.MaxNoticeItems"/> mods and always fits one packet.</summary>
    internal static byte[] Encode(bool held, IReadOnlyList<RecheckItem>? items)
    {
        var known = new List<(string Key, HeldLocalFacts Facts)>();
        if (held)
            foreach (var i in items ?? Array.Empty<RecheckItem>())
            {
                if (i == null || i.Key.Length == 0 || known.Count >= ClientPolicyRecheckLimits.MaxNoticeItems) continue;
                var f = HeldLocalFacts.Of(i);
                if (f != null) known.Add((KeyOf(i.Key), f));
            }

        int take = known.Count;
        while (true)
        {
            byte[] bytes = EncodeFirst(held, known, take);
            if (bytes.Length <= ClientModPolicyLimits.MaxReportPacketBytes || take == 0) return bytes;
            take--;
        }
    }

    private static byte[] EncodeFirst(bool held, List<(string Key, HeldLocalFacts Facts)> known, int take)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(ClientModReportWire.Version);
        stream.WriteByte(KindHeld);
        stream.WriteByte(held ? (byte)1 : (byte)0);
        if (take > 0)
        {
            using var w = new Utf8JsonWriter(stream, WriterOptions);
            w.WriteStartObject();
            w.WriteStartArray("local");
            for (int n = 0; n < take; n++)
            {
                var f = known[n].Facts;
                w.WriteStartObject();
                w.WriteString("id", known[n].Key);
                w.WriteString("state", f.State switch
                {
                    RecheckLocalState.NotInstalled => StateNotInstalled,
                    RecheckLocalState.TurnedOff => StateTurnedOff,
                    _ => StateUnknown,
                });
                if (f.Listed) w.WriteBoolean("listed", true);
                if (f.LocalTest) w.WriteBoolean("localTest", true);
                if (f.Paused) w.WriteBoolean("paused", true);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>True when <paramref name="packet"/> has this kind byte (the server then reads it here, never as a report chunk).</summary>
    internal static bool IsHeld(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && packet[0] == ClientModReportWire.Version && packet[1] == KindHeld;

    /// <summary>
    /// Fails closed: wrong kind, a bad size or held byte, or a body that is not strict UTF-8 JSON of this shape. Keys are
    /// the clipped ids (<see cref="KeyOf"/>), compared ignoring case.
    /// </summary>
    internal static bool TryDecode(ReadOnlySpan<byte> packet, out bool held, out Dictionary<string, HeldLocalFacts> local)
    {
        held = false;
        local = new Dictionary<string, HeldLocalFacts>(StringComparer.OrdinalIgnoreCase);
        if (!IsHeld(packet) || packet.Length < HeaderBytes || packet.Length > ClientModPolicyLimits.MaxReportPacketBytes) return false;
        if (packet[2] > 1) return false;
        held = packet[2] == 1;
        if (packet.Length == HeaderBytes) return true;
        try
        {
            using var doc = JsonDocument.Parse(StrictUtf8.GetString(packet.Slice(HeaderBytes).ToArray()), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("local", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var x in list.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object || local.Count >= ClientPolicyRecheckLimits.MaxNoticeItems) continue;
                    string id = Str(x, "id");
                    if (id.Length == 0 || id.Length > ClientModPolicyLimits.MaxGuidChars) continue;
                    var f = new HeldLocalFacts
                    {
                        State = Str(x, "state") switch
                        {
                            StateNotInstalled => RecheckLocalState.NotInstalled,
                            StateTurnedOff => RecheckLocalState.TurnedOff,
                            _ => RecheckLocalState.Unknown,
                        },
                        Listed = Bool(x, "listed"),
                        LocalTest = Bool(x, "localTest"),
                        Paused = Bool(x, "paused"),
                    };
                    local[KeyOf(id)] = f;
                }

            return true;
        }
        catch (Exception e) when (e is JsonException || e is DecoderFallbackException || e is ArgumentException)
        {
            local.Clear();
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static bool Bool(JsonElement el, string name) => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}

/// <summary>
/// F41 chat (decided 2026-10-08), server, game-free: when the countdown is said in chat while a player's disconnect
/// countdown notice is held behind an open confirmation on their client. A line when the client says the notice is held
/// (not again within <see cref="MinGapSeconds"/>, at most <see cref="MaxStartLines"/> per countdown), then one at each of
/// <see cref="Marks"/> seconds left that is at least <see cref="MinGapSeconds"/> after the start; none once the client
/// says the notice is shown. When the server re-arms the disconnect while the notice is held (a fix of one mod leaves
/// another with its own deadline, and no new notice is sent), <see cref="OnRearmed"/> carries the schedule to the new
/// countdown; a called-off disconnect ends the lines (nothing is armed). Held messages are rate limited per player
/// (<see cref="Throttled"/>).
/// </summary>
internal sealed class CountdownChatSchedule
{
    /// <summary>Seconds left at which the countdown is said again while the notice stays held.</summary>
    internal static readonly int[] Marks = { 30, 10 };

    /// <summary>The fewest seconds between the start line and a mark, or between two start lines.</summary>
    internal const double MinGapSeconds = 5;

    /// <summary>The fewest seconds between a line and a mark's line (a notice held again just before a mark).</summary>
    internal const double MinMarkGapSeconds = 3;

    /// <summary>Start lines one countdown gets at most (a client toggling its held state gets no more).</summary>
    internal const int MaxStartLines = 3;

    /// <summary>A player's held messages are acted on (and logged) at most this often.</summary>
    internal const double MinMessageSeconds = 1;

    private sealed class State
    {
        public bool Held;
        public int? Generation;
        public Dictionary<string, HeldLocalFacts> Local = new(StringComparer.OrdinalIgnoreCase);
        public double LastSentAt = double.NegativeInfinity;
        public double LastMessageAt = double.NegativeInfinity;
        public readonly HashSet<int> MarksScheduled = new();
        public readonly HashSet<int> MarksDone = new();
        public int StartLines;
        public int Messages;
        public int Throttled;
        public int Lines;
        public string LastLine = "";
    }

    private readonly Dictionary<int, State> _users = new();
    private readonly object _gate = new();

    private State Get(int user)
    {
        if (!_users.TryGetValue(user, out var s))
        {
            if (_users.Count >= 512) _users.Clear();
            _users[user] = s = new State();
        }

        return s;
    }

    /// <summary>
    /// The per-player rate limit: true when a held message came within <see cref="MinMessageSeconds"/> of the last one
    /// acted on (the caller then only applies a "no longer held" quietly and logs nothing).
    /// </summary>
    public bool Throttled(int user, double now)
    {
        lock (_gate)
        {
            var s = Get(user);
            if (now - s.LastMessageAt < MinMessageSeconds && now >= s.LastMessageAt)
            {
                s.Throttled++;
                return true;
            }

            s.LastMessageAt = now;
            return false;
        }
    }

    /// <summary>
    /// The client's held-notice message. True when a line is due now; <paramref name="marks"/> are the marks to schedule
    /// (each once per countdown, at <paramref name="secondsLeft"/> minus the mark from now). <paramref name="generation"/>
    /// is the armed disconnect's (null: none is armed, so nothing is said).
    /// </summary>
    public bool OnHeld(int user, bool held, IReadOnlyDictionary<string, HeldLocalFacts>? local, int? generation, double secondsLeft,
        double now, out List<int> marks)
    {
        marks = new List<int>();
        lock (_gate)
        {
            var s = Get(user);
            s.Messages++;
            if (!held)
            {
                s.Held = false;
                return false;
            }

            s.Local = new Dictionary<string, HeldLocalFacts>(StringComparer.OrdinalIgnoreCase);
            if (local != null) foreach (var pair in local) s.Local[ClientPolicyHeldWire.KeyOf(pair.Key)] = pair.Value;
            s.Held = true;
            return Arm(s, generation, secondsLeft, now, marks);
        }
    }

    /// <summary>
    /// The server re-armed the disconnect (another generation, a new deadline) without a new notice: while the notice is
    /// held, the schedule moves to the new countdown with what the client said before. False (no marks) when not held.
    /// </summary>
    public bool OnRearmed(int user, int generation, double secondsLeft, double now, out List<int> marks)
    {
        marks = new List<int>();
        lock (_gate)
        {
            if (!_users.TryGetValue(user, out var s) || !s.Held || s.Generation == generation) return false;
            return Arm(s, generation, secondsLeft, now, marks);
        }
    }

    private static bool Arm(State s, int? generation, double secondsLeft, double now, List<int> marks)
    {
        if (s.Generation != generation)
        {
            s.Generation = generation;
            s.MarksScheduled.Clear();
            s.MarksDone.Clear();
            s.StartLines = 0;
        }

        if (generation == null) return false;
        foreach (int m in Marks)
            if (secondsLeft - m >= MinGapSeconds && s.MarksScheduled.Add(m)) marks.Add(m);
        if (now - s.LastSentAt < MinGapSeconds || s.StartLines >= MaxStartLines) return false;
        s.StartLines++;
        return true;
    }

    /// <summary>
    /// The seconds a line says: a mark's own number when the timer fired at most a second or two off it (review: a timer a
    /// little early read 31 s at the 30 s mark), else the seconds left rounded up.
    /// </summary>
    internal static int SecondsToSay(double secondsLeft, int? mark)
    {
        if (mark is int m && Math.Abs(secondsLeft - m) < 2) return m;
        return Math.Max(1, (int)Math.Ceiling(secondsLeft - 0.001));
    }

    /// <summary>A scheduled mark came: true (once) when the notice is still held for this countdown.</summary>
    public bool MarkDue(int user, int generation, int mark, double now)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(user, out var s) || !s.Held || s.Generation != generation || !s.MarksDone.Add(mark)) return false;
            return now - s.LastSentAt >= MinMarkGapSeconds;
        }
    }

    /// <summary>A line went out.</summary>
    public void NoteSent(int user, string line, double now)
    {
        lock (_gate)
        {
            var s = Get(user);
            s.LastSentAt = now;
            s.Lines++;
            s.LastLine = line ?? "";
        }
    }

    /// <summary>What the client said it has of the named mods, by clipped key (empty when it said nothing).</summary>
    public Dictionary<string, HeldLocalFacts> Local(int user)
    {
        lock (_gate)
            return _users.TryGetValue(user, out var s)
                ? new Dictionary<string, HeldLocalFacts>(s.Local, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, HeldLocalFacts>(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsHeld(int user)
    {
        lock (_gate) return _users.TryGetValue(user, out var s) && s.Held;
    }

    public void Forget(int user)
    {
        lock (_gate) _users.Remove(user);
    }

    public void Clear()
    {
        lock (_gate) _users.Clear();
    }

    /// <summary>The bridge's readback for one user, or null.</summary>
    public Dictionary<string, object?>? Describe(int user, double now)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(user, out var s)) return null;
            return new Dictionary<string, object?>
            {
                ["held"] = s.Held,
                ["messages"] = s.Messages,
                ["throttled"] = s.Throttled,
                ["lines"] = s.Lines,
                ["lastLine"] = s.LastLine,
                ["secondsSinceLine"] = double.IsNegativeInfinity(s.LastSentAt) ? null : Math.Max(0, now - s.LastSentAt),
            };
        }
    }
}

/// <summary>
/// CLIENTPOLICY3: the server's mid-session notice to a player, on the <c>'PrCm'</c> id (server to client; the report's
/// chunks go the other way): a version byte, the kind byte <see cref="KindNotice"/>, a sequence number (4 bytes,
/// little-endian), then UTF-8 JSON <c>{"action","seconds","server","items":[{id,name,kind,pausable,preChange,added,rule,had}]}</c>.
/// Sent only to a client that declared <see cref="Handshake.Capability.PolicyRecheck"/>; anything else gets the chat line.
/// </summary>
internal static class ClientPolicyNoticeWire
{
    internal const byte KindNotice = 1;
    internal const int HeaderBytes = 6;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string ActionWord(RecheckNoticeKind kind) => kind switch
    {
        RecheckNoticeKind.Warn => "warn",
        RecheckNoticeKind.Disconnect => "disconnect",
        RecheckNoticeKind.Disconnected => "disconnected",
        RecheckNoticeKind.Resolved => "resolved",
        _ => "",
    };

    internal static RecheckNoticeKind ParseAction(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "warn" => RecheckNoticeKind.Warn,
        "disconnect" => RecheckNoticeKind.Disconnect,
        "disconnected" => RecheckNoticeKind.Disconnected,
        "resolved" => RecheckNoticeKind.Resolved,
        _ => RecheckNoticeKind.None,
    };

    /// <summary>
    /// The notice's packet. It names at most <see cref="ClientPolicyRecheckLimits.MaxNoticeItems"/> mods and always fits
    /// one packet (<see cref="ClientModPolicyLimits.MaxReportPacketBytes"/>): mods are dropped from the end while it does
    /// not, and counted in <c>more</c>.
    /// </summary>
    internal static byte[] Encode(RecheckNoticeKind kind, int seconds, IReadOnlyList<RecheckItem>? items, int sequence, string? server = "")
    {
        int total = items?.Count ?? 0;
        int take = Math.Min(total, ClientPolicyRecheckLimits.MaxNoticeItems);
        while (true)
        {
            byte[] bytes = EncodeFirst(kind, seconds, items, take, total, sequence, server);
            if (bytes.Length <= ClientModPolicyLimits.MaxReportPacketBytes || take == 0) return bytes;
            take--;
        }
    }

    private static byte[] EncodeFirst(RecheckNoticeKind kind, int seconds, IReadOnlyList<RecheckItem>? items, int take, int total, int sequence, string? server)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(ClientModReportWire.Version);
        stream.WriteByte(KindNotice);
        var n = BitConverter.GetBytes(sequence);
        if (!BitConverter.IsLittleEndian) Array.Reverse(n);
        stream.Write(n, 0, 4);
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("action", ActionWord(kind));
            w.WriteNumber("seconds", Math.Max(0, seconds));
            if (!string.IsNullOrEmpty(server)) w.WriteString("server", ClientModEntry.Clip(server, 64));
            w.WriteStartArray("items");
            int count = 0;
            foreach (var i in items ?? Array.Empty<RecheckItem>())
            {
                if (count++ >= take) break;
                w.WriteStartObject();
                w.WriteString("id", ClientModEntry.Clip(i.Key, ClientModPolicyLimits.MaxGuidChars));
                w.WriteString("name", ClientModEntry.Clip(i.Name, ClientModPolicyLimits.MaxNameChars));
                w.WriteString("kind", i.Kind);
                if (i.Pausable) w.WriteBoolean("pausable", true);
                if (i.PreChange) w.WriteBoolean("preChange", true);
                if (i.Added) w.WriteBoolean("added", true);
                if (i.Rule.Length > 0) w.WriteString("rule", ClientModEntry.Clip(i.Rule, 96));
                if (i.Had.Length > 0) w.WriteString("had", ClientModEntry.Clip(i.Had, ClientModPolicyLimits.MaxVersionChars));
                if (i.Seconds > 0) w.WriteNumber("seconds", i.Seconds);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            int more = total - take;
            if (more > 0) w.WriteNumber("more", more);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>A server's notice as the client reads it. Fails closed (not ours, not strict UTF-8 JSON of this shape).</summary>
    internal sealed class Notice
    {
        public RecheckNoticeKind Kind { get; set; }
        public int Seconds { get; set; }
        public int Sequence { get; set; }
        public string Server { get; set; } = "";
        public int More { get; set; }
        public List<RecheckItem> Items { get; } = new();
    }

    internal static bool TryDecode(ReadOnlySpan<byte> packet, out Notice notice)
    {
        notice = new Notice();
        if (packet.Length < HeaderBytes || packet.Length > ClientModPolicyLimits.MaxReportPacketBytes) return false;
        if (packet[0] != ClientModReportWire.Version || packet[1] != KindNotice) return false;
        var n = packet.Slice(2, 4).ToArray();
        if (!BitConverter.IsLittleEndian) Array.Reverse(n);
        notice.Sequence = BitConverter.ToInt32(n, 0);
        try
        {
            using var doc = JsonDocument.Parse(StrictUtf8.GetString(packet.Slice(HeaderBytes).ToArray()), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            notice.Kind = ParseAction(Str(root, "action"));
            if (notice.Kind == RecheckNoticeKind.None) return false;
            notice.Seconds = root.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int sec) ? Math.Max(0, Math.Min(sec, 3600)) : 0;
            notice.Server = ClientModEntry.Clip(Str(root, "server"), 64);
            notice.More = root.TryGetProperty("more", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out int more) ? Math.Max(0, more) : 0;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var x in items.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object || notice.Items.Count >= ClientPolicyRecheckLimits.MaxNoticeItems) continue;
                    var item = new RecheckItem
                    {
                        Key = ClientModEntry.Clip(Str(x, "id"), ClientModPolicyLimits.MaxGuidChars),
                        Name = ClientModEntry.Clip(Str(x, "name"), ClientModPolicyLimits.MaxNameChars),
                        Kind = Str(x, "kind"),
                        Pausable = Bool(x, "pausable"),
                        PreChange = Bool(x, "preChange"),
                        Added = Bool(x, "added"),
                        Rule = ClientModEntry.Clip(Str(x, "rule"), 96),
                        Had = ClientModEntry.Clip(Str(x, "had"), ClientModPolicyLimits.MaxVersionChars),
                        Seconds = x.TryGetProperty("seconds", out var isec) && isec.ValueKind == JsonValueKind.Number && isec.TryGetInt32(out int iv) ? Math.Max(0, Math.Min(iv, 3600)) : 0,
                    };
                    if (item.Key.Length == 0 && item.Name.Length == 0) continue;
                    notice.Items.Add(item);
                }

            return true;
        }
        catch (Exception e) when (e is JsonException || e is DecoderFallbackException || e is ArgumentException)
        {
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static bool Bool(JsonElement el, string name) => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}

/// <summary>
/// CLIENTPOLICY3, client: what a server's mid-session notice does here. Before it shows the notice, the client pauses
/// the mods it names that the per-server pause can take (unless automatic pausing is off for this server), so the lines
/// say what really happened; then one popup in the game's look (<see cref="ServerModPauseNotice"/>) names every mod with
/// its reason. A disconnect notice is kept and shown again on the screen the client lands on after the leave. Game-free:
/// the live side sets the probes (<c>ClientModReportClient.Install</c>); tests pin them.
/// </summary>
internal static class ClientPolicyNoticeClient
{
    /// <summary>Pause these plugins for the joined server (live: <c>ServerModPause.PauseForPolicy</c>).</summary>
    internal static Func<IReadOnlyList<string>, JoinPauseResult>? PauseProbe { get; set; }

    /// <summary>The plugins the per-server pause can take now (live: Bootstrap's suspendable set).</summary>
    internal static Func<IReadOnlyCollection<string>>? PausableProbe { get; set; }

    /// <summary>A plugin already paused for this server (live: <c>ServerModPause.IsPaused</c>).</summary>
    internal static Func<string, bool>? PausedProbe { get; set; }

    /// <summary>SRVUNINST1: this client's copy of a required mod (turned off, not installed). Null: the advice is not tailored.</summary>
    internal static Func<string, RecheckLocalState>? LocalStateProbe { get; set; }

    /// <summary>LOCALJOIN1: the configured local test index provides this mod. Null: never.</summary>
    internal static Func<string, bool>? LocalTestProbe { get; set; }

    /// <summary>NOTICEOPEN1: this client's Install Mods catalog lists this mod. Null: never.</summary>
    internal static Func<string, bool>? InstallListedProbe { get; set; }

    /// <summary>Automatic pausing is on for the joined server (live: its profile's <c>autoPause</c>).</summary>
    internal static Func<bool>? AutoPauseProbe { get; set; }

    /// <summary>The joined server's name (live: its profile), or empty.</summary>
    internal static Func<string>? ServerNameProbe { get; set; }

    /// <summary>Show the popup (live: <c>ServerModPauseNotice.Open</c>).</summary>
    internal static Action<PauseNoticeContent>? Show { get; set; }

    /// <summary>Show the disconnect notice again on the main menu, until the player closes it (live: the same popup, no timer). Null: <see cref="Show"/>.</summary>
    internal static Action<PauseNoticeContent>? ShowAtMenu { get; set; }

    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    private static readonly object Gate = new();
    private static PauseNoticeContent? _last;
    private static RecheckNoticeKind _lastKind;
    private static double _lastAt = double.NegativeInfinity;
    private static int _received;
    private static Dictionary<string, RecheckPauseOutcome> _lastOutcomes = new(StringComparer.OrdinalIgnoreCase);
    private static PauseNoticeContent? _reshow;

    internal static int Received
    {
        get { lock (Gate) return _received; }
    }

    /// <summary>E1: the last notice of this connection that carried an open button (its fix), or null.</summary>
    internal static PauseNoticeContent? LastActionable
    {
        get { lock (Gate) return _last != null && _last.OpenKind.Length > 0 ? _last : null; }
    }

    /// <summary>A "called off" notice this soon after a popup that paused everything named is not shown.</summary>
    internal const double ResolvedQuietSeconds = 30;

    /// <summary>Act on a server's notice: pause what it names that can be paused, then show it. Returns what was shown.</summary>
    internal static PauseNoticeContent Handle(ClientPolicyNoticeWire.Notice notice)
    {
        ClientModReportClient.NoteNoticeRule(notice.Kind, notice.Items);
        var outcomes = new Dictionary<string, RecheckPauseOutcome>(StringComparer.OrdinalIgnoreCase);
        if (notice.Kind == RecheckNoticeKind.Warn || notice.Kind == RecheckNoticeKind.Disconnect)
            PauseNamed(notice.Items, outcomes);

        // Live 2026-10-05: a required mod the server cannot see because this client paused it for the server is
        // installed; the line says it is paused (the policy update that requires it brings it back), not "install it".
        foreach (var i in notice.Items)
        {
            if (i.NotAllowed || i.Key.Length == 0 || outcomes.ContainsKey(i.Key)) continue;
            bool paused;
            try { paused = PausedProbe?.Invoke(i.Key) ?? false; }
            catch { paused = false; }
            if (paused)
            {
                outcomes[i.Key] = RecheckPauseOutcome.Paused;
                // F41 chat: the server's chat line says it the same way.
                if (i.Kind == ClientModProblem.KindMissing) i.PausedHere = true;
            }
        }

        // SRVUNINST1 (live 2026-10-07: an uninstalled mod read "turn it back on in the Mods list"): what this client has.
        foreach (var i in notice.Items)
        {
            if (i.NotAllowed || i.Kind != ClientModProblem.KindMissing || i.Key.Length == 0 || outcomes.ContainsKey(i.Key)) continue;
            try { i.Local = LocalStateProbe?.Invoke(i.Key) ?? RecheckLocalState.Unknown; }
            catch { i.Local = RecheckLocalState.Unknown; }
            try { i.LocalTestSource = LocalTestProbe?.Invoke(i.Key) ?? false; }
            catch { i.LocalTestSource = false; }
            if (i.Local == RecheckLocalState.NotInstalled)
            {
                try { i.InstallListed = InstallListedProbe?.Invoke(i.Key) ?? false; }
                catch { i.InstallListed = false; }
            }
        }

        string server = "";
        try { server = ServerNameProbe?.Invoke() ?? ""; }
        catch { server = ""; }
        var content = ClientPolicyNoticeText.Compose(notice.Kind, notice.Items, notice.Seconds, server, outcomes, notice.More);
        bool quiet;
        lock (Gate)
        {
            // The countdown this client just answered by pausing everything it named: the paused popup already says
            // so, and the server's "called off" would only replace it.
            quiet = notice.Kind == RecheckNoticeKind.Resolved && _last != null
                && _last.Title == ClientPolicyNoticeText.TitlePaused && Clock() - _lastAt <= ResolvedQuietSeconds;
        }

        if (quiet)
        {
            lock (Gate) _received++;
            Plugin.Logger?.LogInfo("ClientPolicy: server notice (resolved) after this client paused the mods; the paused notice stays");
            return content;
        }

        lock (Gate)
        {
            _received++;
            _last = content;
            _lastKind = notice.Kind;
            _lastAt = Clock();
            _lastOutcomes = outcomes;
            _reshow = notice.Kind == RecheckNoticeKind.Disconnected ? content : null;
        }

        ShowOrHold(content, atMenu: false, notice.Kind, notice.Items);
        Plugin.Logger?.LogInfo("ClientPolicy: server notice (" + ClientPolicyNoticeWire.ActionWord(notice.Kind) + "): " + content.Text);
        return content;
    }

    /// <summary>
    /// F41 (live 2026-10-08): a server notice drew over the open "Yes, install on both" box and hid it, so the install was
    /// never confirmed and the countdown ran out; on the main menu the disconnect notice opened over the install box again.
    /// While a confirmation is open (live: a <c>UiDialog</c> is on screen) the notice waits, the latest replacing an older
    /// one, and is shown when the box closes (<see cref="ShowHeldWhenFree"/>, each frame).
    /// </summary>
    internal static Func<bool>? ConfirmationOpenProbe { get; set; }

    private static PauseNoticeContent? _held;
    private static bool _heldAtMenu;

    // F41 chat (decided 2026-10-08): the held notice is an in-world disconnect countdown, the mods it names (with this
    // client's local state), and whether the server was last told one is held.
    private static bool _heldCountdown;
    private static IReadOnlyList<RecheckItem>? _heldItems;
    private static bool _heldReported;

    /// <summary>A notice waiting for an open confirmation to close (readback and tests).</summary>
    internal static PauseNoticeContent? Held
    {
        get { lock (Gate) return _held; }
    }

    /// <summary>F41: the server was last told that its countdown notice is held here (readback and tests).</summary>
    internal static bool HeldReported
    {
        get { lock (Gate) return _heldReported; }
    }

    private static bool ConfirmationOpen()
    {
        try { return ConfirmationOpenProbe?.Invoke() ?? false; }
        catch { return false; }
    }

    private static void ShowOrHold(PauseNoticeContent content, bool atMenu, RecheckNoticeKind kind = RecheckNoticeKind.None,
        IReadOnlyList<RecheckItem>? items = null)
    {
        if (ConfirmationOpen())
        {
            bool countdown = !atMenu && kind == RecheckNoticeKind.Disconnect;
            lock (Gate)
            {
                _held = content;
                _heldAtMenu = atMenu;
                _heldCountdown = countdown;
                _heldItems = countdown ? items : null;
            }

            Plugin.Logger?.LogInfo("ClientPolicy: a confirmation is open; the server notice waits until it closes: " + content.Title);
            ReportHeld(newCountdown: countdown);
            return;
        }

        lock (Gate) _held = null;
        ReportHeld(newCountdown: false);
        Present(content, atMenu);
    }

    /// <summary>
    /// F41 chat: tell the server when a disconnect countdown notice starts waiting here (each new one, so a re-armed
    /// countdown is said again) and when none waits any more (shown, replaced by a notice that is not a countdown, or
    /// dropped), so the server says the countdown in chat only while the player can't see it.
    /// </summary>
    private static void ReportHeld(bool newCountdown)
    {
        bool want, was;
        IReadOnlyList<RecheckItem>? items;
        lock (Gate)
        {
            want = _held != null && _heldCountdown;
            items = _heldItems;
            was = _heldReported;
            _heldReported = want;
        }

        if (want && (newCountdown || !was)) ClientModReportClient.SendNoticeHeld(true, items);
        else if (!want && was) ClientModReportClient.SendNoticeHeld(false, null);
    }

    private static void Present(PauseNoticeContent content, bool atMenu)
    {
        try { (atMenu ? ShowAtMenu ?? Show : Show)?.Invoke(content); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the notice could not be shown: " + e.Message); }
    }

    /// <summary>F41: the held notice, once no confirmation is open. True when it was shown now.</summary>
    internal static bool ShowHeldWhenFree()
    {
        PauseNoticeContent? content;
        bool atMenu;
        lock (Gate)
        {
            content = _held;
            atMenu = _heldAtMenu;
        }

        if (content == null || ConfirmationOpen()) return false;
        lock (Gate)
        {
            if (!ReferenceEquals(_held, content)) return false;
            _held = null;
        }

        ReportHeld(newCountdown: false);
        Present(content, atMenu);
        Plugin.Logger?.LogInfo("ClientPolicy: the confirmation closed; showed the server notice that waited: " + content.Title);
        return true;
    }

    private static void PauseNamed(IReadOnlyList<RecheckItem> items, Dictionary<string, RecheckPauseOutcome> outcomes)
    {
        var want = new List<string>();
        foreach (var i in items)
        {
            if (!i.NotAllowed || !i.Pausable || i.Key.Length == 0) continue;
            bool paused;
            try { paused = PausedProbe?.Invoke(i.Key) ?? false; }
            catch { paused = false; }
            if (paused) outcomes[i.Key] = RecheckPauseOutcome.Paused;
            else want.Add(i.Key);
        }

        if (want.Count == 0) return;
        bool auto;
        try { auto = AutoPauseProbe?.Invoke() ?? true; }
        catch { auto = true; }
        IReadOnlyCollection<string>? pausable = null;
        try { pausable = PausableProbe?.Invoke(); }
        catch { pausable = null; }
        var set = new HashSet<string>(pausable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var take = new List<string>();
        foreach (string id in want)
        {
            if (!auto || PauseProbe == null || !set.Contains(id)) outcomes[id] = RecheckPauseOutcome.Refused;
            else take.Add(id);
        }

        if (take.Count == 0) return;
        JoinPauseResult result;
        try { result = PauseProbe!(take) ?? JoinPauseResult.Fail("the pause did not answer"); }
        catch (Exception e) { result = JoinPauseResult.Fail(e.Message); }
        var taken = new HashSet<string>(result.Taken, StringComparer.OrdinalIgnoreCase);
        foreach (string id in take) outcomes[id] = result.Ok && taken.Contains(id) ? RecheckPauseOutcome.Paused : RecheckPauseOutcome.Refused;
        Plugin.Logger?.LogInfo("ClientPolicy: paused for the server's notice: " + (result.Ok ? string.Join(", ", result.Taken) : "nothing (" + result.Message + ")"));
    }

    /// <summary>
    /// The connection ended: a disconnect notice that arrived within <see cref="ClientPolicyRecheckLimits.ReshowAfterLeaveSeconds"/>
    /// is shown again (the world's popup went with it). Once only.
    /// </summary>
    internal static bool ReshowAfterLeave()
    {
        PauseNoticeContent? content;
        lock (Gate)
        {
            content = _reshow;
            _reshow = null;
            if (content == null || Clock() - _lastAt > ClientPolicyRecheckLimits.ReshowAfterLeaveSeconds) return false;
        }

        // F41: a countdown notice still waiting from the world is stale now; the disconnect notice replaces it, and it too
        // waits while a confirmation is open (live: it opened over the install box on the main menu).
        lock (Gate)
        {
            _held = null;
            // The connection is gone: there is no server to tell, and the next one starts unaware.
            _heldReported = false;
        }

        ShowOrHold(content, atMenu: true);
        Plugin.Logger?.LogInfo("ClientPolicy: showed the disconnect notice again on the main menu");
        return true;
    }

    /// <summary>The bridge's readback (<c>join_admin report</c>): the last notice and what the client did.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        lock (Gate)
        {
            var outcomes = new Dictionary<string, object?>();
            foreach (var pair in _lastOutcomes) outcomes[pair.Key] = pair.Value.ToString().ToLowerInvariant();
            return new Dictionary<string, object?>
            {
                ["received"] = _received,
                ["lastAction"] = _last == null ? "" : ClientPolicyNoticeWire.ActionWord(_lastKind),
                ["lastTitle"] = _last?.Title ?? "",
                ["lastBody"] = _last?.Body ?? "",
                ["lastLines"] = _last == null ? new List<string>() : new List<string>(_last.Lines),
                ["lastNote"] = _last?.Note ?? "",
                ["pauses"] = outcomes,
                ["secondsAgo"] = _last == null ? null : Math.Max(0, Clock() - _lastAt),
                ["reshowPending"] = _reshow != null,
                ["heldForConfirmation"] = _held?.Title,
                // F41 chat: the server was told its countdown notice waits here (it says the countdown in chat meanwhile).
                ["heldCountdownReported"] = _heldReported,
                // NOTICEOPEN1: the notice's open button (kind install|mods, empty for none).
                ["open"] = _last == null || _last.OpenKind.Length == 0 ? null : new Dictionary<string, object?>
                {
                    ["kind"] = _last.OpenKind, ["key"] = _last.OpenKey, ["name"] = _last.OpenName, ["caption"] = _last.OpenCaption,
                },
            };
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            _last = null;
            _reshow = null;
            _held = null;
            _heldCountdown = false;
            _heldItems = null;
            _heldReported = false;
            _lastKind = RecheckNoticeKind.None;
            _lastAt = double.NegativeInfinity;
            _received = 0;
            _lastOutcomes = new Dictionary<string, RecheckPauseOutcome>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static void ResetProbes()
    {
        PauseProbe = null;
        PausableProbe = null;
        PausedProbe = null;
        LocalStateProbe = null;
        LocalTestProbe = null;
        InstallListedProbe = null;
        AutoPauseProbe = null;
        ServerNameProbe = null;
        Show = null;
        ShowAtMenu = null;
        ConfirmationOpenProbe = null;
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        Reset();
    }
}

/// <summary>What the client did about one named mod before it showed the notice.</summary>
internal enum RecheckPauseOutcome
{
    /// <summary>Not a mod the client tried to pause.</summary>
    NotTried = 0,

    /// <summary>Paused for this server now (or it already was).</summary>
    Paused = 1,

    /// <summary>The pause was refused (Bootstrap, a mod the server asks about, auto-pause off for this server).</summary>
    Refused = 2,
}

/// <summary>
/// CLIENTPOLICY3: every sentence of the mid-session notice (the dialog kit's title, body, one line per mod with its reason,
/// a note), the chat line for a client without the notice, and the disconnect reason. Game-free so the tests read them.
/// </summary>
internal static class ClientPolicyNoticeText
{
    internal const string TitleNotAllowed = "Mods not allowed on this server";
    internal const string TitleRequired = "Mods this server requires";
    internal const string TitleDisconnected = "Disconnected for your mods";
    internal const string TitleResolved = "You won't be disconnected";
    internal const string TitlePaused = "Mods paused for this server";

    internal const string ResolvedBody = "Nothing you have loaded breaks this server's mod rules now, so the disconnect is called off.";
    internal const string NextJoinNote = "Your next join is refused until these are fixed.";

    /// <summary>The panel's note after switching to Disconnect the player.</summary>
    internal static string DisconnectNote => DisconnectNoteFor(ClientPolicyRecheckLimits.DisconnectGraceSeconds);

    /// <inheritdoc cref="DisconnectNote"/>
    internal static string DisconnectNoteFor(int seconds)
    {
        int before = ClientPolicyRecheckLimits.PolicyChangeGraceFor(seconds);
        return "Players who load a mod you don't allow, or remove or turn off a required one, get a notice naming it and "
            + seconds.ToString(CultureInfo.InvariantCulture) + " seconds to fix it"
            + (before > seconds ? " (" + before.ToString(CultureInfo.InvariantCulture) + " for mods you don't allow loaded before you change the rules)" : "")
            + ".";
    }

    /// <summary>
    /// F40 (live 2026-10-08: after an uninstall the body said "turn off ... Turn it back on" above a line reading "Required
    /// here and not installed"): the required-mod countdown's sentence by what this client has of the named mods. All
    /// turned off (or not known): turn it back on; all missing: reinstall it; a mix: put them back.
    /// </summary>
    internal static string RequiredDisconnectBody(IReadOnlyList<RecheckItem> items, string within)
    {
        bool anyMissing = false, anyOther = false;
        foreach (var i in items ?? Array.Empty<RecheckItem>())
        {
            if (i.NotAllowed) continue;
            if (i.Local == RecheckLocalState.NotInstalled) anyMissing = true;
            else anyOther = true;
        }

        if (anyMissing && !anyOther)
            return " disconnects players who remove a mod it requires. Reinstall it " + within + ", or you'll be disconnected:";
        if (anyMissing)
            return " disconnects players who remove or turn off a mod it requires. Put them back " + within + ", or you'll be disconnected:";
        return " disconnects players who turn off a mod it requires. Turn it back on " + within + ", or you'll be disconnected:";
    }

    /// <summary>The dialog kit's parts for a notice, after the client acted on it (<paramref name="outcomes"/> by item key).</summary>
    internal static PauseNoticeContent Compose(RecheckNoticeKind kind, IReadOnlyList<RecheckItem> items, int seconds, string? serverName,
        IReadOnlyDictionary<string, RecheckPauseOutcome>? outcomes = null, int more = 0)
    {
        items ??= Array.Empty<RecheckItem>();
        bool named = !string.IsNullOrWhiteSpace(serverName);
        // "this server" mid-sentence, "This server" where it starts one.
        string server = named ? serverName!.Trim() : "this server";
        string Server = named ? server : "This server";
        RecheckPauseOutcome Outcome(RecheckItem i) =>
            outcomes != null && outcomes.TryGetValue(i.Key, out var o) ? o : RecheckPauseOutcome.NotTried;

        if (kind == RecheckNoticeKind.Resolved)
            return new PauseNoticeContent { Title = TitleResolved, Body = ResolvedBody, Text = TitleResolved + ". " + ResolvedBody };

        bool anyNotAllowed = false, anyRequired = false, allPaused = items.Count > 0, anyBlocking = false;
        foreach (var i in items)
        {
            if (i.NotAllowed) anyNotAllowed = true;
            else anyRequired = true;
            if (!(i.NotAllowed && Outcome(i) == RecheckPauseOutcome.Paused)) allPaused = false;
            if (Outcome(i) != RecheckPauseOutcome.Paused || (i.NotAllowed && !i.Pausable)) anyBlocking = true;
        }

        // Disconnect the player: items with different time left (a mod loaded before the policy change has longer) each
        // show their own.
        var distinct = new HashSet<int>();
        foreach (var i in items)
            if (i.Seconds > 0) distinct.Add(i.Seconds);
        bool perItem = kind == RecheckNoticeKind.Disconnect && distinct.Count > 1;

        string title, body, note = "";
        if (kind == RecheckNoticeKind.Disconnected)
        {
            title = TitleDisconnected;
            body = "You were disconnected from " + server + " because of these mods:";
        }
        else if (allPaused)
        {
            // Every named mod is paused now: nothing to fix, nobody is disconnected for them.
            title = TitlePaused;
            body = items.Count == 1
                ? Server + " doesn't allow this mod, so it's paused while you're on it and comes back when you leave:"
                : Server + " doesn't allow these mods, so they're paused while you're on it and come back when you leave:";
        }
        else if (kind == RecheckNoticeKind.Disconnect)
        {
            title = anyNotAllowed ? TitleNotAllowed : TitleRequired;
            string within = perItem ? "in the time shown for each" : "within " + Seconds(seconds);
            body = anyNotAllowed
                ? Server + " disconnects players who load mods it doesn't allow. Fix these " + within + ", or you'll be disconnected:"
                : Server + RequiredDisconnectBody(items, within);
        }
        else
        {
            title = anyNotAllowed ? TitleNotAllowed : TitleRequired;
            body = anyNotAllowed && anyRequired ? Server + " doesn't allow some of your mods and requires others. You can keep playing:"
                : anyNotAllowed ? Server + " doesn't allow " + (items.Count == 1 ? "this mod" : "these mods") + ". You can keep playing:"
                : Server + " requires " + (items.Count == 1 ? "this mod" : "these mods") + ". You can keep playing:";
            if (anyBlocking) note = NextJoinNote;
        }

        var lines = new List<string>();
        var parts = new List<string>();
        foreach (var i in items)
        {
            string line = Line(i, Outcome(i), kind == RecheckNoticeKind.Disconnected);
            if (perItem && i.Seconds > 0 && Outcome(i) != RecheckPauseOutcome.Paused) line += " (" + Seconds(i.Seconds) + ")";
            lines.Add(line);
            parts.Add(line);
        }

        var open = OpenAction(kind, items, Outcome);
        if (more > 0) lines.Add("and " + more.ToString(CultureInfo.InvariantCulture) + " more");
        return new PauseNoticeContent
        {
            Title = title, Body = body, Lines = lines, Note = note,
            OpenKind = open.Kind, OpenKey = open.Key, OpenName = open.Name, OpenCaption = open.Caption,
            Text = title + ". " + body + " " + string.Join("; ", parts) + "." + (note.Length > 0 ? " " + note : ""),
        };
    }

    /// <summary>One mod: its name, then why and what to do.</summary>
    internal static string Line(RecheckItem i, RecheckPauseOutcome outcome, bool disconnected = false)
    {
        string name = i.Name.Length > 0 ? i.Name : i.Key;
        return name + " · " + Reason(i, outcome, disconnected);
    }

    /// <summary>The reason part of a line.</summary>
    internal static string Reason(RecheckItem i, RecheckPauseOutcome outcome, bool disconnected = false)
    {
        if (i.NotAllowed)
        {
            if (disconnected) return i.Pausable ? "Not allowed on this server" : "Not allowed on this server; turn it off in the Mods list and restart V Rising before you rejoin";
            if (outcome == RecheckPauseOutcome.Paused) return "Not allowed; paused for this server, back when you leave";
            if (i.Pausable) return outcome == RecheckPauseOutcome.Refused
                ? "Not allowed and couldn't be paused; turn it off in the Mods list"
                : "Not allowed; pause it for this server or turn it off in the Mods list";
            return "Not allowed and can't be paused; turn it off in the Mods list and restart V Rising before you rejoin";
        }

        // A required mod this client paused for the server: installed, and it comes back as the server allows it now.
        if (outcome == RecheckPauseOutcome.Paused && i.Kind == ClientModProblem.KindMissing)
            return "Required here; it's installed but paused for this server, and it loads again as soon as the server allows it";
        string had = i.Had.Length > 0 ? "; you have " + i.Had : "";
        return i.Kind switch
        {
            // NOTICEOPEN1 (standing rule 2026-10-07): in game a missing mod is installed from the Install Mods tab, right
            // where the player is; the join window is only the fallback for the next join.
            ClientModProblem.KindMissing when i.Added && i.Local == RecheckLocalState.NotInstalled && !disconnected
                => "Now required here; install it from " + InstallModsWords(i) + " before your next join",
            ClientModProblem.KindMissing when i.Added => "Now required here; install it before your next join",
            // Disconnected (the menu's notice): the fix is still made right there, before the rejoin.
            ClientModProblem.KindMissing when i.Local == RecheckLocalState.NotInstalled && disconnected => i.LocalTestSource || i.InstallListed
                ? "Required here and it is not installed; install it from " + InstallModsWords(i) + " before you rejoin"
                : "Required here and it is not installed; look for it in Install Mods, or ask the admins where to get it",
            ClientModProblem.KindMissing when i.Local == RecheckLocalState.NotInstalled
                => i.LocalTestSource || i.InstallListed
                    ? "Required here and not installed; install it from " + InstallModsWords(i) + ", or from the join window when you next join"
                    : "Required here and not installed; look for it in Install Mods, or ask the admins where to get it",
            ClientModProblem.KindMissing when disconnected && i.Local == RecheckLocalState.TurnedOff
                => "Required here and it was turned off; turn it back on in the Mods list before you rejoin",
            ClientModProblem.KindMissing => disconnected ? "Required here and it was turned off" : "Required here; turn it back on in the Mods list",
            ClientModProblem.KindOlder => "Required here at " + (i.Rule.Length > 0 ? i.Rule : "a newer version") + had + "; update it",
            ClientModProblem.KindNewer => "Newer than this server allows (" + (i.Rule.Length > 0 ? i.Rule : "an older version") + ")" + had,
            _ => "Required here at " + (i.Rule.Length > 0 ? i.Rule : "another version") + had,
        };
    }

    /// <summary>NOTICEOPEN1: <c>Install Mods</c>, or <c>Install Mods (local test source)</c> when the local test source provides it (it is what the tab installs).</summary>
    internal static string InstallModsWords(RecheckItem i) =>
        i.LocalTestSource ? InstallModsTab + " (" + Install.LocalTestSource.Words + ")" : InstallModsTab;

    internal const string InstallModsTab = "Install Mods";
    internal const string OpenInstallModsButton = "Open Install Mods";
    internal const string OpenModsButton = "Open Mods";

    /// <summary>
    /// NOTICEOPEN1: the notice's open button for <paramref name="items"/> (a Warn, a Disconnect countdown, or the disconnect notice the main menu shows again):
    /// Install Mods for the first required mod this client has no copy of, else the Mods list for the first one it turned
    /// off; none otherwise.
    /// </summary>
    internal static (string Kind, string Key, string Name, string Caption) OpenAction(RecheckNoticeKind kind, IReadOnlyList<RecheckItem> items,
        Func<RecheckItem, RecheckPauseOutcome> outcome)
    {
        // The disconnect notice too: shown again on the main menu, where the player installs before rejoining.
        if (kind is not (RecheckNoticeKind.Warn or RecheckNoticeKind.Disconnect or RecheckNoticeKind.Disconnected)) return ("", "", "", "");
        RecheckItem? off = null;
        foreach (var i in items)
        {
            if (i.NotAllowed || i.Kind != ClientModProblem.KindMissing || i.Key.Length == 0 || outcome(i) == RecheckPauseOutcome.Paused) continue;
            if (i.Local == RecheckLocalState.NotInstalled) return (OpenKindInstall, i.Key, NameOf(i), OpenInstallModsButton);
            if (i.Local == RecheckLocalState.TurnedOff) off ??= i;
        }

        return off != null ? (OpenKindMods, off.Key, NameOf(off), OpenModsButton) : ("", "", "", "");
    }

    internal const string OpenKindInstall = "install";
    internal const string OpenKindMods = "mods";

    private static string NameOf(RecheckItem i) => i.Name.Length > 0 ? i.Name : i.Key;

    /// <summary>The same notice as one System chat line, for a client without the notice (an older Progenitor). ASCII.</summary>
    internal static string Chat(RecheckNoticeKind kind, IReadOnlyList<RecheckItem> items, int seconds)
    {
        var content = Compose(kind, items, seconds, null);
        return Clean(content.Text, 400);
    }

    /// <summary>
    /// F41 chat: where a missing mod is installed from, in words that survive the ASCII chat line (<see cref="Clean"/> turns
    /// the menu path's angle bracket into a parenthesis).
    /// </summary>
    internal const string InstallModsPlace = "Install Mods in the Mods menu";

    /// <summary>F41 chat: the line's last sentence, so the player knows why the countdown is in chat.</summary>
    internal const string CountdownChatTail = "(The server's notice is waiting behind an open confirmation.)";

    /// <summary>
    /// F41 chat (decided 2026-10-08): the disconnect countdown as one System chat line, for a player whose countdown
    /// notice is held behind an open confirmation. Each mod gets the notice's own advice for what the client said it has
    /// (<paramref name="local"/>, by <see cref="ClientPolicyHeldWire.KeyOf"/>): the same facts as
    /// <see cref="Reason"/>, made where the player is (never the join screen). One action reads
    /// <c>Reinstall X … within N s or you'll be disconnected.</c>; anything else <c>Within N s or you'll be disconnected: …</c>.
    /// ASCII, at most 400. Empty when nothing is named.
    /// </summary>
    internal static string CountdownChat(IReadOnlyList<RecheckItem>? items, int seconds, IReadOnlyDictionary<string, HeldLocalFacts>? local)
    {
        var clauses = new List<(string Text, bool Action)>();
        foreach (var i in items ?? Array.Empty<RecheckItem>())
        {
            HeldLocalFacts? facts = null;
            if (local != null && i.Key.Length > 0) local.TryGetValue(ClientPolicyHeldWire.KeyOf(i.Key), out facts);
            clauses.Add(CountdownFix(i, facts));
        }

        if (clauses.Count == 0) return "";
        string deadline = "within " + Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture) + " s or you'll be disconnected";
        var texts = new List<string>();
        foreach (var c in clauses) texts.Add(c.Text);
        string text = clauses.Count == 1 && clauses[0].Action
            ? Upper(clauses[0].Text) + " " + deadline + "."
            : Upper(deadline) + ": " + string.Join("; ", texts) + ".";
        return Clean(text + " " + CountdownChatTail, 400);
    }

    /// <summary>
    /// F41 chat: one mod's clause, lower case unless it starts with the mod's name. <c>Action</c> is true for an
    /// imperative fix (the line can then read "Fix it within N s"), false for a statement of the facts.
    /// </summary>
    internal static (string Text, bool Action) CountdownFix(RecheckItem i, HeldLocalFacts? facts)
    {
        string name = NameOf(i);
        string had = i.Had.Length > 0 ? "; you have " + i.Had : "";
        if (i.NotAllowed)
            return i.Pausable
                ? ("turn off " + name + " in the Mods list", true)
                : (name + " isn't allowed here and can't be paused; turn it off in the Mods list and restart V Rising", false);

        var state = facts?.State ?? RecheckLocalState.Unknown;
        switch (i.Kind)
        {
            case ClientModProblem.KindMissing when facts != null && facts.Paused:
                return (name + " is required here; it's installed but paused for this server, and it loads again as soon as the server allows it", false);
            case ClientModProblem.KindMissing when state == RecheckLocalState.NotInstalled:
                // NOTICEOPEN1's facts: Install Mods offers it only when its catalog lists it or the local test source has it.
                if (facts!.LocalTest) return ("reinstall " + name + " from " + InstallModsTab + " (" + Install.LocalTestSource.Words + ") in the Mods menu", true);
                if (facts.Listed) return ("reinstall " + name + " from " + InstallModsPlace, true);
                return ("look for " + name + " in " + InstallModsPlace + ", or ask the admins where to get it", true);
            case ClientModProblem.KindMissing:
                return ("turn " + name + " back on in the Mods list", true);
            case ClientModProblem.KindOlder:
                return ("update " + name + " to " + (i.Rule.Length > 0 ? i.Rule : "a newer version") + (i.Had.Length > 0 ? " (you have " + i.Had + ")" : ""), true);
            case ClientModProblem.KindNewer:
                return (name + " is newer than this server allows (" + (i.Rule.Length > 0 ? i.Rule : "an older version") + ")" + had, false);
            default:
                return (name + " is required here at " + (i.Rule.Length > 0 ? i.Rule : "another version") + had, false);
        }
    }

    private static string Upper(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>The disconnect's reason (the server log and the game's own disconnect data; ASCII, at most 479 characters).</summary>
    internal static string KickReason(IReadOnlyList<RecheckItem> items)
    {
        // One sentence per cause: loaded mid-session, loaded before the admins stopped allowing it, a required mod turned
        // off, a required mod at a version the rule refuses.
        var loaded = new List<string>();
        var before = new List<string>();
        var off = new List<string>();
        var version = new List<string>();
        foreach (var i in items ?? Array.Empty<RecheckItem>())
        {
            string name = i.Name.Length > 0 ? i.Name : i.Key;
            if (i.NotAllowed) (i.PreChange ? before : loaded).Add(name);
            else if (i.Kind == ClientModProblem.KindMissing) off.Add(name);
            else version.Add(name + (i.Rule.Length > 0 ? " (needs " + i.Rule + (i.Had.Length > 0 ? ", had " + i.Had : "") + ")" : ""));
        }

        var parts = new List<string>();
        if (loaded.Count > 0) parts.Add("Mods not allowed on this server, loaded during the session: " + string.Join(", ", loaded) + ".");
        if (before.Count > 0) parts.Add("Mods this server no longer allows: " + string.Join(", ", before) + ".");
        if (off.Count > 0) parts.Add("Required mods turned off during the session: " + string.Join(", ", off) + ".");
        if (version.Count > 0) parts.Add("Required mods at a version this server doesn't accept: " + string.Join(", ", version) + ".");
        return Handshake.Compare.TruncateReason(Clean(string.Join(" ", parts), 1000), Handshake.ResultMessageMaxChars);
    }

    private static string Seconds(int n) => n == 1 ? "1 second" : Math.Max(0, n).ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>Printable ASCII without markup brackets (a client's own mod names go back to it in chat).</summary>
    internal static string Clean(string? text, int max)
    {
        string a = ClientModJudge.Ascii(text).Replace('<', '(').Replace('>', ')');
        return a.Length <= max ? a : a.Substring(0, Math.Max(0, max - 3)) + "...";
    }
}

/// <summary>
/// UNINSTREQ1 (standing rule 2026-10-07): the fact every uninstall confirmation that removes this client's copy draws when
/// the connected server requires the mod (its handshake asks for it as must-have, or its remembered client mod policy
/// lists it as required): what that server does about it. Its rule comes from the server (the request's capability row,
/// then its notices: <see cref="ClientModReportClient.MidSessionRule"/>); only an older server leaves it unknown.
/// </summary>
internal static class RequiredHereFact
{
    internal const string Label = "Required by";

    /// <summary>Tests: who requires an id and whether through the client mod policy (null: not required). Null: live evidence.</summary>
    internal static Func<string, (string Server, bool Policy, bool Handshake)?>? Probe { get; set; }

    /// <summary>Tests: whether the server re-checks mods mid-session. Null: the live handshake capability.</summary>
    internal static Func<bool>? RechecksProbe { get; set; }

    /// <summary>Tests: the server's mid-session rule. Null: <see cref="ClientModReportClient.MidSessionRule"/>.</summary>
    internal static Func<ServerMidSessionRule>? RuleProbe { get; set; }

    /// <summary>Tests: the server's Disconnect them seconds (0: never said). Null: <see cref="ClientModReportClient.ServerGraceSeconds"/>.</summary>
    internal static Func<int>? GraceProbe { get; set; }

    private const string NextJoin = "your next join is refused until it's back.";

    /// <summary>The handshake clause: what a must-have row in the server's join check means while you stay.</summary>
    internal const string HandshakeClause =
        "it checks this mod whenever this client answers it again (after a Progenitor update, for one), and that answer would disconnect you";

    /// <summary>
    /// The fact's value. <paramref name="policy"/>: the client mod policy requires it (judged mid-session on a server that
    /// re-checks, by its <paramref name="rule"/>). <paramref name="handshake"/>: the join check asks for it as must-have
    /// (judged on every answer this client sends, so a re-answer while it is missing disconnects).
    /// </summary>
    /// <param name="grace">REQRULE5: the server's Disconnect them seconds as it said them; 0 (never said: a server from before the
    /// setting) is <see cref="ClientPolicyRecheckLimits.LegacyDisconnectGraceSeconds"/>.</param>
    internal static string Value(string server, bool rechecks, ServerMidSessionRule rule = ServerMidSessionRule.Unknown, bool policy = true,
        bool handshake = false, int grace = 0)
    {
        string head = (string.IsNullOrWhiteSpace(server) ? "This server" : server.Trim()) + ": ";
        if (policy && rechecks)
        {
            string clause = rule switch
            {
                ServerMidSessionRule.Disconnect => "it disconnects players who remove a mod it requires unless it's back within "
                    + (grace > 0 ? grace : ClientPolicyRecheckLimits.LegacyDisconnectGraceSeconds).ToString(CultureInfo.InvariantCulture) + " seconds",
                ServerMidSessionRule.Warn => "you'll be warned in game and can keep playing",
                _ => "it checks your mods while you play, so you'll be warned in game (or disconnected, if its admins chose that)",
            };
            return head + clause + (handshake ? "; " + HandshakeClause : "") + "; " + NextJoin;
        }

        if (handshake) return head + HandshakeClause + "; " + NextJoin;
        return head + "nothing checks it again while you stay, but " + NextJoin;
    }

    /// <summary>The value for <paramref name="pluginId"/> now, or empty when the connected server does not require it. Never throws.</summary>
    internal static string ValueFor(string? pluginId)
    {
        string id = (pluginId ?? "").Trim();
        if (id.Length == 0) return "";
        (string Server, bool Policy, bool Handshake)? who;
        try { who = Probe != null ? Probe(id) : LiveServerRequiring(id); }
        catch { who = null; }
        if (who == null) return "";
        bool rechecks;
        try { rechecks = RechecksProbe?.Invoke() ?? ClientModReportClient.ServerRechecks; }
        catch { rechecks = false; }
        ServerMidSessionRule rule;
        try { rule = RuleProbe?.Invoke() ?? ClientModReportClient.MidSessionRule; }
        catch { rule = ServerMidSessionRule.Unknown; }
        int grace;
        try { grace = GraceProbe?.Invoke() ?? ClientModReportClient.ServerGraceSeconds; }
        catch { grace = 0; }
        return Value(who.Value.Server, rechecks, rule, who.Value.Policy, who.Value.Handshake, grace);
    }

    private static (string Server, bool Policy, bool Handshake)? LiveServerRequiring(string id)
    {
        if (!JoinFlowClient.InWorldNow()) return null;
        bool policy = ServerModPause.PolicyStanding(id) == ServerModPause.StandingRequired;
        bool handshake = false;
        foreach (var r in Handshake.CopyServerAsked())
            if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase) && r.Level == Plugins.NetworkCompatibilityLevel.ServerMustHave) { handshake = true; break; }
        return policy || handshake ? (JoinFlowClient.ServerLabelNow, policy, handshake) : null;
    }
}
