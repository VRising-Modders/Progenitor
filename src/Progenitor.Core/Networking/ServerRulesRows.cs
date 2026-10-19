using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stages 6 and 7: the rows Progenitor's page draws for the Server rules (and Server name, under
/// Server list), over the joined server's last <c>'PrSv'</c> state. Every player of a server that has the channel sees
/// them; a player who is not an admin there sees them read-only. Game-free: the client sets the seams
/// (<c>JoinServerSettings.Install</c>); tests set their own.
/// </summary>
internal static class ServerRulesRows
{
    /// <summary>The state the server last answered or pushed (live: <c>ServerRulesClient.State</c>), or null.</summary>
    internal static Func<ServerRulesState?>? StateProbe { get; set; }

    /// <summary>Send a request; the callback gets the answer or null. Returns null when sent, else why not.</summary>
    internal static Func<ServerRulesRequest, Action<ServerRulesResponse?>, string?>? Requester { get; set; }

    /// <summary>The joined server's hello said it has the channel (live: <c>JoinAdminClient.ServerHasRules</c>).</summary>
    internal static Func<bool>? SupportedProbe { get; set; }

    /// <summary>This player is an admin of the joined server (live: the Players joining rule).</summary>
    internal static Func<bool>? CanEditProbe { get; set; }

    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal const double AskEverySeconds = 5;
    internal const double RefreshEverySeconds = 30;
    private static double _lastAsk = double.NegativeInfinity;

    internal const string AdminOnlyReason = "Only an admin of this server can change it.";

    /// <summary>Stage 6 drew the rules read-only; stage 7 turns the writes on.</summary>
    internal static bool WritesEnabled => true;

    internal const string ReadOnlyStageReason = "Read only in this version of Progenitor.";
    internal const string NoAnswerError = "No answer from the server.";

    // The read-only lines' labels.
    /// <summary>
    /// PENDING1 (user review 2026-10-06: "Restart · Nothing waits for a restart." read like a setting): the status row at
    /// the top of Server rules.
    /// </summary>
    internal const string PendingLabel = "Pending changes";

    /// <summary>Kept for callers that still name the old row; the row is <see cref="PendingLabel"/>.</summary>
    internal const string RestartLabel = PendingLabel;
    internal const string MercilessLabel = "Listed as Merciless";
    internal const string TerritoryLabel = "Territory (read only)";
    internal const string DaysRunningLabel = "Days running (read only)";
    internal const string StandardLabel = "Standard settings (read only)";
    internal const string NextWipeLabel = "Next scheduled wipe";
    internal const string PresetLabel = "Preset";

    internal static ServerRulesState? State
    {
        get
        {
            try { return StateProbe?.Invoke(); }
            catch { return null; }
        }
    }

    internal static bool Supported()
    {
        try { return SupportedProbe?.Invoke() ?? false; }
        catch { return false; }
    }

    internal static bool CanEdit()
    {
        try { return CanEditProbe?.Invoke() ?? false; }
        catch { return false; }
    }

    /// <summary>The show-if of every Server rules row: the joined server has the channel. Asks for its state when due.</summary>
    internal static bool Shown()
    {
        if (!Supported()) return false;
        AskIfDue(State == null ? AskEverySeconds : RefreshEverySeconds);
        return true;
    }

    internal static bool AskIfDue(double every = AskEverySeconds)
    {
        double now = Clock();
        if (now - _lastAsk < every && now >= _lastAsk) return false;
        _lastAsk = now;
        var requester = Requester;
        if (requester == null) return false;
        try { return requester(new ServerRulesRequest { Op = ServerRulesRequest.OpGet }, _ => { }) == null; }
        catch { return false; }
    }

    internal static ServerRuleRow? Row(string key) => State?.Row(key);

    /// <summary>Send a new value of <paramref name="key"/> with the value the page showed as the expected one.</summary>
    internal static void Send(string key, string value, Action<bool, string> done) => Send(key, value, done, confirm: false);

    /// <summary>
    /// Send a new value. <paramref name="confirm"/>: the admin answered the wipe confirmation (the server refuses a change
    /// that may reset the world without it).
    /// </summary>
    internal static void Send(string key, string value, Action<bool, string> done, bool confirm)
    {
        var requester = Requester;
        if (requester == null)
        {
            done(false, "Not connected to a server with Progenitor.");
            return;
        }

        var row = Row(key);
        var request = new ServerRulesRequest { Op = ServerRulesRequest.OpWrite, Key = key, Value = value ?? "", Confirm = confirm };
        if (row?.Next != null && !string.Equals(key, ServerRules.Password, StringComparison.OrdinalIgnoreCase))
        {
            request.Expected = row.Next;
            request.HasExpected = true;
        }

        string? refusal;
        try
        {
            refusal = requester(request, response =>
            {
                if (response == null) done(false, NoAnswerError);
                else done(response.Ok, response.Ok ? response.Note : response.Error);
            });
        }
        catch (Exception e) { refusal = e.Message; }

        if (refusal != null) done(false, refusal);
    }

    // ---- the rows ------------------------------------------------------------------------------------------

    internal static AdminSettingSpec Spec(ServerRuleSpec rule)
    {
        string key = rule.Key;
        bool password = rule.Control == ServerRuleControl.Password;
        return new AdminSettingSpec
        {
            SettingId = rule.Id,
            Key = key,
            Label = rule.Label,
            Help = rule.Help,
            Kind = rule.Control switch
            {
                ServerRuleControl.Choice => AdminSettingKind.Choice,
                ServerRuleControl.Switch => AdminSettingKind.Toggle,
                ServerRuleControl.Number => AdminSettingKind.Number,
                _ => AdminSettingKind.Text,
            },
            Choices = rule.Choices,
            ChoiceLabels = rule.ChoiceLabels,
            ChoiceDescriptions = rule.ChoiceDescriptions,
            Min = rule.Min,
            Max = rule.Max,
            Units = key == ServerRules.ClanSize ? "players" : key == ServerRules.MaxConnectedUsers ? "players" : key == ServerRules.ResetDaysInterval ? "days" : "",
            MaxLength = rule.MaxLength,
            WriteOnly = password,
            Read = () => Row(key)?.Next,
            // The page asks first whenever ConfirmFor has a question (the view opens it before Send), so a change that
            // needs the confirmation is sent with it.
            Send = (v, done) => Send(key, v, done, ConfirmFor(key, Row(key)?.Next ?? "", v) != null),
            LockedBy = () => Row(key) is { LockedBy.Length: > 0 } r ? r.LockedBy : null,
            RangeChecked = rule.Control == ServerRuleControl.Number,
            MaxNow = key == ServerRules.MaxConnectedUsers ? () => State?.MaxPlayersMax ?? rule.Max : null,
            ReadOnlyReason = () => ReadOnlyReason(key),
            // Live 2026-10-06: a player who is not an admin reads the rules as every other server row, "Admin only · value".
            ViewOnly = () => !CanEdit(),
            Note = () => NoteFor(key),
            Pending = () => Row(key)?.Pending ?? false,
            Display = v => DisplayOf(rule, v),
            Confirm = (current, next) => ConfirmFor(key, current, next),
            ExtraReadback = () => Readback(key),
        };
    }

    /// <summary>
    /// The line under an editable rule: the server's reason (a start-command override), and for Wipe on with no scheduled
    /// wipe, WIPEDAY1's <c>Wipe on Saturday · takes effect once a wipe is scheduled</c>. Null when there is nothing to say.
    /// </summary>
    internal static string? NoteFor(string key)
    {
        var row = Row(key);
        string? reason = row is { Editable: true, Reason.Length: > 0 } ? row.Reason : null;
        string? waits = key == ServerRules.DayOfReset && row != null
            ? WipePlan.DayWaitsNote(ServerRulesStateBuilder.Int(Row(ServerRules.ResetDaysInterval)?.Next) ?? 0, row.Next)
            : null;
        if (waits == null) return reason;
        return reason == null ? waits : waits + ". " + reason;
    }

    /// <summary>Why a rule is read-only for this player now, or null (an admin may edit it).</summary>
    internal static string? ReadOnlyReason(string key)
    {
        var row = Row(key);
        if (row == null) return null;
        if (!CanEdit()) return AdminOnlyReason;
        // A locked row says why through its lock note (with the entry that matched), not a generic reason.
        if (row.LockedBy.Length > 0) return null;
        if (!WritesEnabled) return ReadOnlyStageReason;
        if (!row.Editable) return row.Reason.Length > 0 ? row.Reason : "This setting can't be changed here.";
        return null;
    }

    internal static string DisplayOf(ServerRuleSpec rule, string? value)
    {
        string v = value ?? "";
        if (rule.Control == ServerRuleControl.Password) return v == "set" ? "Set" : "Not set";
        if (rule.Control == ServerRuleControl.Switch) return SettingPlaces.Display(v);
        if (rule.Key == ServerRules.ResetDaysInterval && v == "0") return "Off";
        for (int i = 0; i < rule.Choices.Count && i < rule.ChoiceLabels.Count; i++)
            if (string.Equals(rule.Choices[i], v, StringComparison.OrdinalIgnoreCase)) return rule.ChoiceLabels[i];
        return v;
    }

    internal const string WipeOnHeading = "Turn on scheduled wipes?";
    internal const string WipeChangeHeading = "Change the scheduled wipes?";
    internal const string WipeOnYes = "Turn on scheduled wipes";
    internal const string WipeChangeYes = "Change them";
    internal const string PasswordClearHeading = "Remove the password?";
    internal const string PasswordClearYes = "Remove it";

    /// <summary>The "now" the wipe confirmation compares the reset date with (the page's local time; tests pin it).</summary>
    internal static Func<DateTime> Today { get; set; } = () => DateTime.Now;

    /// <summary>
    /// The confirmations. Scheduled wipes: whenever a change of the interval or the day turns them on, brings the next reset
    /// date to today or earlier, or the world's start date is not known (the same rule the server enforces,
    /// <see cref="ServerRules.WipeNeedsConfirm"/>). The password: removing it. Every other change asks nothing.
    /// </summary>
    internal static AdminSettingConfirm? ConfirmFor(string key, string current, string next)
    {
        if (key == ServerRules.ResetDaysInterval || key == ServerRules.DayOfReset)
        {
            if (string.Equals(current, next, StringComparison.OrdinalIgnoreCase)) return null;
            int intervalBefore = ServerRulesStateBuilder.Int(Row(ServerRules.ResetDaysInterval)?.Next) ?? 0;
            int intervalAfter = key == ServerRules.ResetDaysInterval ? ServerRulesStateBuilder.Int(next) ?? 0 : intervalBefore;
            // WIPEDAY1 (live 2026-10-19): with no scheduled wipe before or after, a new Wipe on day turns nothing on or off:
            // it is saved without a question, and the row's note says it waits for a scheduled wipe (WipePlan.DayWaitsNote).
            if (intervalBefore <= 0 && intervalAfter <= 0) return null;
            string? dayBefore = Row(ServerRules.DayOfReset)?.Next;
            string? dayAfter = key == ServerRules.DayOfReset ? next : dayBefore;
            return WipeConfirm(intervalBefore, dayBefore, intervalAfter, dayAfter);
        }

        if (key == ServerRules.Password && string.Equals(current, "set", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(next))
            return new AdminSettingConfirm(PasswordClearHeading, "Anyone can then join this server without a password, from the next player who joins.", PasswordClearYes);
        return null;
    }

    /// <summary>WIPECONFIRM1: the server's time zone as this page knows it (the joined server's; tests pin it).</summary>
    internal static Func<TimeZoneInfo>? ZoneProbe { get; set; }

    internal static TimeZoneInfo ServerZone()
    {
        try { return ZoneProbe?.Invoke() ?? TimeZoneInfo.Local; }
        catch { return TimeZoneInfo.Local; }
    }

    /// <summary>
    /// WIPECONFIRM1 (decided 2026-10-06): every change to the wipe settings is confirmed with its exact result
    /// (<see cref="WipePlan.For"/>): before → after, the wipe date and time, how long until then, the world's age and what
    /// happens. A change that makes a wipe due or earlier needs WIPE typed, one that keeps the date CONFIRM; turning wipes
    /// off or moving the date later needs a click.
    /// </summary>
    internal static AdminSettingConfirm WipeConfirm(int intervalBefore, string? dayBefore, int intervalAfter, string? dayAfter)
    {
        var o = WipeOutcome(intervalBefore, dayBefore, intervalAfter, dayAfter);
        // WIPEPOLISH1: no body paragraph; the facts carry the lines (the warning last, in the danger tone).
        return new AdminSettingConfirm(WipePlan.Heading, "", WipePlan.YesCaption(intervalBefore, intervalAfter))
        {
            Word = o.Word,
            Danger = o.Due,
            Facts = o.Facts,
            Text = o.Body,
        };
    }

    internal static WipePlan.Outcome WipeOutcome(int intervalBefore, string? dayBefore, int intervalAfter, string? dayAfter)
    {
        var s = State;
        return WipePlan.For(ServerRules.ParseStartDate(s?.StartDate), intervalBefore, dayBefore, intervalAfter, dayAfter,
            Today(), ServerZone(), TimeZoneInfo.Local, s?.SaveName);
    }

    // ---- WIPECONFIRM1: Next wipe, by date -------------------------------------------------------------------

    internal const string NextWipeDateLabel = "Next wipe";

    internal const string NextWipeDateHelp =
        "Pick the day the game wipes this world; Progenitor sets Scheduled wipes every (days) to the days from the world's start "
        + "to that date. The game wipes only when the server starts on or after that day (a server that restarts nightly wipes "
        + "at the first restart that day). The world is archived, not deleted; the new world gets a new start date and the "
        + "countdown starts over. Set Scheduled wipes every (days) to 0 for no scheduled wipe.";

    /// <summary>WIPECONFIRM1: the Next wipe row: a date that sets the interval (and Wipe on) through the confirmation.</summary>
    internal static AdminSettingSpec NextWipeDateSpec() => new()
    {
        SettingId = SettingPlaces.PluginId + "/ServerRules/NextWipe",
        Key = "NextWipe",
        Label = NextWipeDateLabel,
        Help = NextWipeDateHelp,
        Kind = AdminSettingKind.Date,
        Read = NextWipeIso,
        Send = SendPickedDate,
        Confirm = (_, next) => FromPickedDate(next, out _) is { } plan
            ? WipeConfirm(ServerRulesStateBuilder.Int(Row(ServerRules.ResetDaysInterval)?.Next) ?? 0, Row(ServerRules.DayOfReset)?.Next,
                plan.Interval, plan.Day)
            : null,
        LockedBy = () => Row(ServerRules.ResetDaysInterval) is { LockedBy.Length: > 0 } r ? r.LockedBy : null,
        ReadOnlyReason = () => ReadOnlyReason(ServerRules.ResetDaysInterval),
        ViewOnly = () => !CanEdit(),
        Note = NextWipeNote,
        Pending = () => (Row(ServerRules.ResetDaysInterval)?.Pending ?? false) || (Row(ServerRules.DayOfReset)?.Pending ?? false),
        Display = _ => NextWipeLine(),
    };

    /// <summary>Send a picked date as the interval (confirmed: the page asked first) and, when it changes, Wipe on.</summary>
    internal static void SendPickedDate(string iso, Action<bool, string> done)
    {
        if (FromPickedDate(iso, out string refusal) is not { } plan)
        {
            done(false, refusal);
            return;
        }

        string dayNow = Row(ServerRules.DayOfReset)?.Next ?? "Any";
        Send(ServerRules.ResetDaysInterval, plan.Interval.ToString(CultureInfo.InvariantCulture), (ok, note) =>
        {
            if (!ok || string.Equals(dayNow, plan.Day, StringComparison.OrdinalIgnoreCase))
            {
                done(ok, note);
                return;
            }

            Send(ServerRules.DayOfReset, plan.Day, done, confirm: true);
        }, confirm: true);
    }

    /// <summary>The next wipe's day as ISO UTC (00:00 of it in the server's zone), or empty when off or unknown.</summary>
    internal static string NextWipeIso()
    {
        var wipe = GameWipeSchedule.FromRules(State);
        return wipe == null ? "" : wipe.NextUtc(ServerZone());
    }

    /// <summary>The line under the date: <c>= every 34 days on Any from the world's start (Sep 10, 2026)</c>.</summary>
    internal static string NextWipeNote()
    {
        int interval = ServerRulesStateBuilder.Int(Row(ServerRules.ResetDaysInterval)?.Next) ?? 0;
        return WipePlan.IntervalNote(interval, Row(ServerRules.DayOfReset)?.Next, ServerRules.ParseStartDate(State?.StartDate));
    }

    /// <summary>
    /// A picked date (ISO UTC) as the two game settings: the interval from the world's start, and Wipe on kept when it
    /// still lands on that day, else Any. Null with <paramref name="refusal"/> when it can't be set.
    /// </summary>
    internal static (int Interval, string Day)? FromPickedDate(string iso, out string refusal)
    {
        refusal = "";
        if (!ResetCountdown.TryParseUtc(iso, out var utc))
        {
            refusal = "Pick a date.";
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ServerZone());
        int? interval = WipePlan.IntervalFor(ServerRules.ParseStartDate(State?.StartDate), local, Today(), out refusal);
        if (interval is not { } n) return null;
        return (n, WipePlan.DayFor(local.Date, Row(ServerRules.DayOfReset)?.Next));
    }

    /// <summary>
    /// The wipe confirmation's body: what a reset does, the world's start date and age, and the reset date. When that date
    /// is already reached it says plainly that the next start resets the world.
    /// </summary>
    internal static string WipeBody(int days, DateTime? reset, bool past)
    {
        var s = State;
        string world = s == null || s.StartDate.Length == 0
            ? "This world's start date is not known, so the next start may reset it."
            : "This world started on " + FormatIso(s.StartDate) + (s.DaysRunning is int d ? " and has run " + d + (d == 1 ? " day." : " days.") : ".");
        string date = reset is { } r
            ? " Every " + days + (days == 1 ? " day" : " days") + ", the reset date is " + ServerRules.FormatDate(r)
              + (past ? ", which has passed." : "; the server checks once, when it starts.")
            : "";
        string lead = past
            ? "The next start resets this world: the current save is moved to a backup folder (<save>_<ticks>) and a fresh world starts. "
            : "The next start may reset this world: the current save is moved to a backup folder (<save>_<ticks>) and a fresh world starts. ";
        return lead + world + date;
    }

    private static string FormatIso(string iso) =>
        DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? ServerRules.FormatDate(d) : iso;

    /// <summary>Readback <c>serverRule {key, loaded, next, pending, override, editable, reason, lockedBy, source}</c>; the password reads <c>{set, nextSet}</c>.</summary>
    internal static Dictionary<string, object?>? Readback(string key)
    {
        var row = Row(key);
        if (row == null) return null;
        if (key == ServerRules.Password)
            return new Dictionary<string, object?>
            {
                ["key"] = key, ["set"] = State?.PasswordSet ?? false, ["nextSet"] = State?.PasswordNextSet ?? false,
                ["editable"] = row.Editable, ["reason"] = row.Reason, ["lockedBy"] = row.LockedBy, ["override"] = row.Override,
            };
        return new Dictionary<string, object?>
        {
            ["key"] = key, ["loaded"] = row.Loaded, ["next"] = row.Next, ["pending"] = row.Pending, ["override"] = row.Override,
            ["editable"] = row.Editable, ["reason"] = row.Reason, ["lockedBy"] = row.LockedBy, ["source"] = row.Source,
        };
    }

    // ---- the read-only lines ---------------------------------------------------------------------------------

    /// <summary>PENDING1: the status line with nothing pending (muted).</summary>
    internal const string NothingPendingLine = "None. Changes here apply when the server restarts.";

    /// <summary>PENDING1: what a pending password change reads as (never its value).</summary>
    internal const string PasswordChangedText = "Password changed";

    /// <summary>PENDING1: at most this many changes are named on the line; the rest read "and N more" (all are in the help).</summary>
    internal const int PendingNamedMax = 3;

    /// <summary>PENDING1: the line is cut to "and N more" past this many characters of named changes.</summary>
    internal const int PendingLineChars = 90;

    /// <summary>One rule whose next start's value differs from the running one.</summary>
    internal readonly record struct PendingChange(string Key, string Label, string? Loaded, string? Next)
    {
        /// <summary><c>Clan size 4 → 3</c>; a password change is <c>Password changed</c>, never its value.</summary>
        public string Text => string.Equals(Key, ServerRules.Password, StringComparison.OrdinalIgnoreCase)
            ? PasswordChangedText
            : Label + " " + (Loaded ?? "?") + " → " + (Next ?? "?");

        /// <summary>PENDING2: the list line, <c>Clan size: 4 → 6</c> (a password change reads <c>Password changed</c>).</summary>
        public string ListText => string.Equals(Key, ServerRules.Password, StringComparison.OrdinalIgnoreCase)
            ? PasswordChangedText
            : Label + ": " + (Loaded ?? "?") + " → " + (Next ?? "?");
    }

    /// <summary>The rules waiting for the next start, in the page's order (from the answered state).</summary>
    internal static IReadOnlyList<PendingChange> PendingChanges(ServerRulesState? state = null)
    {
        var s = state ?? State;
        var list = new List<PendingChange>();
        if (s == null) return list;
        foreach (var spec in ServerRules.Specs)
        {
            var row = s.Row(spec.Key);
            if (row == null || !row.Pending) continue;
            bool password = string.Equals(spec.Key, ServerRules.Password, StringComparison.OrdinalIgnoreCase);
            list.Add(new PendingChange(spec.Key, spec.Label,
                password ? null : ShownValue(spec, row.Loaded), password ? null : ShownValue(spec, row.Next)));
        }

        return list;
    }

    /// <summary>A choice's caption (<c>PvE</c>, <c>Normal</c>), else the value as stored.</summary>
    private static string? ShownValue(ServerRuleSpec spec, string? value)
    {
        if (value == null) return null;
        for (int i = 0; i < spec.Choices.Count; i++)
            if (string.Equals(spec.Choices[i], value, StringComparison.OrdinalIgnoreCase))
                return i < spec.ChoiceLabels.Count && spec.ChoiceLabels[i].Length > 0 ? spec.ChoiceLabels[i] : spec.Choices[i];
        return value;
    }

    /// <summary>
    /// PENDING1: <c>None. Changes here apply when the server restarts.</c>, or <c>2 changes apply at the next restart: Clan
    /// size 4 → 3, Game mode PvP → PvE</c> (at most <see cref="PendingNamedMax"/> named and about
    /// <see cref="PendingLineChars"/> characters, then <c>and N more</c>).
    /// </summary>
    internal static string PendingLine(ServerRulesState? state = null)
    {
        var s = state ?? State;
        if (s == null) return "…";
        var changes = PendingChanges(s);
        if (changes.Count == 0) return NothingPendingLine;
        // PENDING2 (user review 2026-10-06: one truncated line): a summary, then a line per change (option labels), at most
        // PendingListMax, then "+N more"; a change that makes a wipe due adds a warning line.
        var sb = new StringBuilder(PendingSummary(changes.Count));
        int shown = 0;
        foreach (var c in changes)
        {
            if (shown >= PendingListMax) break;
            sb.Append('\n').Append(c.ListText);
            shown++;
        }

        if (changes.Count > shown) sb.Append('\n').Append('+').Append(changes.Count - shown).Append(" more");
        if (PendingWipeWarning(s) is { } warning) sb.Append('\n').Append(warning);
        return sb.ToString();
    }

    /// <summary>PENDING2: <c>2 changes apply at the next restart</c>.</summary>
    internal static string PendingSummary(int count) =>
        count == 1 ? "1 change applies at the next restart" : count.ToString(CultureInfo.InvariantCulture) + " changes apply at the next restart";

    /// <summary>PENDING2: most changes listed before <c>+N more</c>.</summary>
    internal const int PendingListMax = 8;

    /// <summary>PENDING2 / WIPECONFIRM1: the warning when the next start's wipe settings make a wipe due.</summary>
    internal static string? PendingWipeWarning(ServerRulesState? s)
    {
        if (s == null) return null;
        var interval = s.Row(ServerRules.ResetDaysInterval);
        var day = s.Row(ServerRules.DayOfReset);
        if (interval is not { Pending: true } && day is not { Pending: true }) return null;
        return WipePlan.PendingWarning(ServerRules.ParseStartDate(s.StartDate), ServerRulesStateBuilder.Int(interval?.Next) ?? 0, day?.Next, Today());
    }

    /// <summary>PENDING1: muted with nothing pending; gold (attention) otherwise, so an admin notices it.</summary>
    internal static bool PendingAttention(ServerRulesState? state = null) => PendingChanges(state).Count > 0;

    /// <summary>PENDING1: the hover/help panel: what the row is, and every pending change on its own line.</summary>
    internal static string PendingHelp(ServerRulesState? state = null)
    {
        const string intro = "Rules marked Restart apply when the server restarts; restarting it is the operator's job.";
        var changes = PendingChanges(state);
        if (changes.Count == 0) return intro + " Nothing is waiting now.";
        var sb = new StringBuilder(intro).Append(" Waiting for the next restart:");
        foreach (var c in changes) sb.Append('\n').Append("• ").Append(c.Text);
        return sb.ToString();
    }

    /// <summary>PENDING1 readback: <c>pendingRestart {count, items [{key, loaded, next}]}</c>; a password item has no values.</summary>
    internal static Dictionary<string, object?> PendingReadback(ServerRulesState? state = null)
    {
        var changes = PendingChanges(state);
        var items = new List<object?>(changes.Count);
        foreach (var c in changes)
        {
            var item = new Dictionary<string, object?> { ["key"] = c.Key, ["loaded"] = c.Loaded, ["next"] = c.Next };
            if (string.Equals(c.Key, ServerRules.Password, StringComparison.OrdinalIgnoreCase)) item["text"] = PasswordChangedText;
            items.Add(item);
        }

        return new Dictionary<string, object?>
        {
            ["count"] = changes.Count,
            ["items"] = items,
            ["line"] = PendingLine(state),
            ["attention"] = changes.Count > 0,
        };
    }

    /// <summary>The old line; now <see cref="PendingLine"/>.</summary>
    internal static string RestartLine() => PendingLine();

    internal static string MercilessLine()
    {
        var s = State;
        if (s == null) return "…";
        string now = s.MercilessNow ? "Yes" : "No";
        return s.MercilessNow == s.MercilessNext ? now : now + " → " + (s.MercilessNext ? "Yes" : "No") + " after the restart";
    }

    internal static string TerritoryLine() => State is { } s ? (s.Territory.Length > 0 ? s.Territory : "Unknown") : "…";

    internal static string DaysRunningLine() => State is { } s ? (s.DaysRunning is int d ? d.ToString(CultureInfo.InvariantCulture) : "Unknown") : "…";

    internal static string StandardLine()
    {
        var s = State;
        if (s == null) return "…";
        string now = s.StandardNow is bool std ? (std ? "Yes" : "No") : "Unknown";
        return s.StandardMayChange ? now + " · may change after the restart" : now;
    }

    internal static string NextWipeLine()
    {
        var s = State;
        if (s == null) return "…";
        if (s.NextReset.Length == 0) return Row(ServerRules.ResetDaysInterval)?.Next is { } n && n != "0" ? "Start date unknown" : "Off";
        return FormatIso(s.NextReset);
    }

    internal static string PresetLine() => State?.PresetWarning ?? "";

    /// <summary>Tests and teardown.</summary>
    internal static void Reset()
    {
        StateProbe = null;
        Requester = null;
        SupportedProbe = null;
        CanEditProbe = null;
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        Today = () => DateTime.Now;
        _lastAsk = double.NegativeInfinity;
    }
}
