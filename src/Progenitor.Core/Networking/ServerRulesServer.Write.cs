using System;
using System.Globalization;
using System.Text;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>What one write decided (game-free).</summary>
internal sealed class ServerRulesDecision
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>True when the value is the one the next start already reads: nothing is written.</summary>
    public bool Unchanged { get; set; }

    public ServerRuleSpec? Spec { get; set; }
    public string Value { get; set; } = "";
    public string Literal { get; set; } = "";
    public string Path { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";

    internal static ServerRulesDecision Refused(string error, string before = "", string after = "") =>
        new() { Ok = false, Error = error, Before = before, After = after };
}

/// <summary>
/// Settings organization stage 7: every check a Server rules write passes, in order (game-free): a known rule, the lock
/// list, the row's own read-only reasons (presets, no known file), the expected value, then type and bounds. A
/// password is never echoed in an error or an audit value.
/// </summary>
internal static class ServerRulesDecisions
{
    internal const string ConflictError = "This setting changed on the server. Review the current value and try again.";
    internal const string WipeConfirmError =
        "Confirm first: this change may reset this world at the next start (the request needs confirm: true).";

    internal const string PasswordTooLong = "At most 32 characters: the game's own limit (over 64 the server quits at its next start).";

    internal static ServerRulesDecision Decide(ServerRulesRequest request, ServerRulesState state, ServerRulesFacts facts)
    {
        var spec = ServerRules.Find(request?.Key);
        if (spec == null) return ServerRulesDecision.Refused("Unknown setting.");
        var row = state.Row(spec.Key);
        if (row == null) return ServerRulesDecision.Refused("Unknown setting.");
        bool password = spec.Control == ServerRuleControl.Password;
        // The audit's "before" is the value the write replaces: the files' value (before an override or a clamp).
        string before = password ? (state.PasswordNextSet ? "set" : "not set") : row.Raw ?? row.Next ?? "";
        string raw = request!.Value ?? "";
        string shown = password ? "<hidden>" : raw;
        if (row.LockedBy.Length > 0) return ServerRulesDecision.Refused(ServerSettingsLock.NoteFor(row.LockedBy), before, shown);
        if (!row.Editable) return ServerRulesDecision.Refused(row.Reason.Length > 0 ? row.Reason : "This setting can't be changed here.", before, shown);
        if (!password && request.HasExpected && !string.Equals(request.Expected, row.Next ?? "", StringComparison.OrdinalIgnoreCase))
            return ServerRulesDecision.Refused(ConflictError, before, shown);

        string value;
        string literal;
        switch (spec.Control)
        {
            case ServerRuleControl.Text:
            {
                var sb = new StringBuilder(raw.Length);
                foreach (char c in raw)
                {
                    if (c is '\r' or '\n' or '\t') continue;
                    if (char.IsControl(c)) return ServerRulesDecision.Refused("The name must be plain text.", before, shown);
                    sb.Append(c);
                }

                value = sb.ToString().Trim();
                if (value.Length == 0) return ServerRulesDecision.Refused("Give the server a name.", before, shown);
                if (value.Length > spec.MaxLength) return ServerRulesDecision.Refused("At most " + spec.MaxLength + " characters.", before, shown);
                literal = ServerListDescription.JsonString(value);
                break;
            }

            case ServerRuleControl.Password:
            {
                // Blank or only spaces is no password (the game's own reading).
                value = string.IsNullOrWhiteSpace(raw) ? "" : raw;
                foreach (char c in value)
                    if (char.IsControl(c)) return ServerRulesDecision.Refused("The password must be plain text.", before, "<hidden>");
                if (value.Length > ServerRules.PasswordMax) return ServerRulesDecision.Refused(PasswordTooLong, before, "<hidden>");
                literal = ServerListDescription.JsonString(value);
                shown = value.Length == 0 ? "not set" : "<hidden>";
                break;
            }

            case ServerRuleControl.Choice:
            {
                string? word = null;
                foreach (string c in spec.Choices)
                    if (string.Equals(c, raw.Trim(), StringComparison.OrdinalIgnoreCase)) word = c;
                if (word == null) return ServerRulesDecision.Refused("Choose one of: " + string.Join(", ", spec.ChoiceLabels) + ".", before, shown);
                value = word;
                literal = ServerListDescription.JsonString(word);
                break;
            }

            case ServerRuleControl.Number:
            {
                int max = spec.Key == ServerRules.MaxConnectedUsers ? state.MaxPlayersMax : spec.Max;
                if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < spec.Min || n > max)
                    return ServerRulesDecision.Refused("Give a whole number from " + spec.Min.ToString(CultureInfo.InvariantCulture) + " to "
                        + max.ToString(CultureInfo.InvariantCulture) + ".", before, shown);
                value = n.ToString(CultureInfo.InvariantCulture);
                literal = value;
                break;
            }

            default:
            {
                string v = raw.Trim().ToLowerInvariant();
                bool? on = v is "true" or "on" or "1" ? true : v is "false" or "off" or "0" ? false : null;
                if (on == null) return ServerRulesDecision.Refused("Choose on or off.", before, shown);
                value = on.Value ? "true" : "false";
                literal = value;
                break;
            }
        }

        // The value the next start already reads: nothing to write. Review fixes: a text compares exactly (a rename that only
        // changes capitals is written); the raw file value counts, not the clamped one (max players above the clamp can be
        // written down to it); the password is always written, so "Unchanged" never confirms a guess.
        string current = row.Raw ?? row.Next ?? "";
        bool unchanged = !password && (spec.Control == ServerRuleControl.Text
            ? string.Equals(value, current, StringComparison.Ordinal)
            : string.Equals(value, current, StringComparison.OrdinalIgnoreCase));

        // Review fix (scheduled wipes): a change that turns wipes on, or brings the reset date to today or earlier, needs the
        // admin's confirmation in the request itself (the page's dialog sends it; the bridge must pass it).
        if (!unchanged && (spec.Key == ServerRules.ResetDaysInterval || spec.Key == ServerRules.DayOfReset))
        {
            int intervalBefore = ServerRulesStateBuilder.Int(state.Row(ServerRules.ResetDaysInterval)?.Next) ?? 0;
            int intervalAfter = spec.Key == ServerRules.ResetDaysInterval ? ServerRulesStateBuilder.Int(value) ?? 0 : intervalBefore;
            string? dayAfter = spec.Key == ServerRules.DayOfReset ? value : state.Row(ServerRules.DayOfReset)?.Next;
            DateTime? start = ServerRules.ParseStartDate(state.StartDate);
            if (ServerRules.WipeNeedsConfirm(start, intervalBefore, intervalAfter, dayAfter, facts.NowLocal, out _, out _) && !request.Confirm)
                return ServerRulesDecision.Refused(WipeConfirmError, before, shown);
        }
        var target = spec.File == ServerRuleFile.Host ? facts.Host?.WriteTarget : facts.Game?.WriteTarget;
        if (target == null || target.Path.Length == 0) return ServerRulesDecision.Refused(ServerRulesStateBuilder.NoFileReason, before, shown);

        string note = unchanged ? "Unchanged."
            : password ? (value.Length == 0 ? "Removed: the next player joins without a password." : "Applied now, to the next player who joins.")
            : spec.Key == ServerRules.ResetDaysInterval && value != "0" && before == "0"
                ? "Saved: scheduled wipes are on from the next start, which may reset this world."
                // WIPEDAY1: a day picked while no wipe is scheduled waits for one.
                : spec.Key == ServerRules.DayOfReset
                  && WipePlan.DayWaitsNote(ServerRulesStateBuilder.Int(state.Row(ServerRules.ResetDaysInterval)?.Next) ?? 0, value) is { } waits
                    ? "Saved: " + waits + "."
                    : "Saved: applies at the next restart.";
        if (!unchanged && row.Override.Length > 0) note += " " + row.Reason;
        return new ServerRulesDecision
        {
            Ok = true,
            Unchanged = unchanged,
            Spec = spec,
            Value = value,
            Literal = literal,
            Path = target.Path,
            Before = before,
            After = shown,
            Note = note,
        };
    }
}

/// <summary>Settings organization stage 7: the Server rules writes.</summary>
internal static partial class ServerRulesServer
{
    /// <summary>Write <c>key = literal</c> into a settings file (live and tests: <see cref="JsonFileEdit.Persist"/>).</summary>
    internal static Func<string, string, string, PersistOutcome> Persist { get; set; } = JsonFileEdit.Persist;

    /// <summary>Set the running server's password (live: the game's own <c>ServerBootstrapSystem.SetServerPassword</c>).</summary>
    internal static Action<string>? SetLivePassword { get; set; }

    private static ServerRulesResponse Write(ServerRulesRequest request, in RemoteInstallActor actor, ServerRulesResponse response, string row)
    {
        if (!actor.Authorized)
        {
            response.Error = NotAdminError;
            WriteAudit(false, actor, row, "", AuditValue(request), response.Error);
            return response;
        }

        ServerRulesFacts facts;
        try { facts = FactsProvider?.Invoke() ?? new ServerRulesFacts(); }
        catch (Exception e)
        {
            response.Error = "The server could not read its settings (" + e.Message + ").";
            WriteAudit(false, actor, row, "", AuditValue(request), response.Error);
            return response;
        }

        facts.LockedBy ??= ServerSettingsLock.LockedBy;
        var state = ServerRulesStateBuilder.Build(facts);
        var decision = ServerRulesDecisions.Decide(request, state, facts);
        if (!decision.Ok)
        {
            response.Error = decision.Error;
            response.State = state;
            WriteAudit(false, actor, row, decision.Before, decision.After.Length > 0 ? decision.After : AuditValue(request), response.Error);
            return response;
        }

        var spec = decision.Spec!;
        if (!decision.Unchanged)
        {
            PersistOutcome outcome;
            try { outcome = Persist(decision.Path, spec.Key, decision.Literal); }
            catch (Exception e) { outcome = new PersistOutcome(PersistResult.Failed, e.Message); }
            if (outcome.Result is PersistResult.Failed or PersistResult.NoTarget)
            {
                response.Error = "The server could not write " + decision.Path + " (" + outcome.Error + ").";
                response.State = state;
                WriteAudit(false, actor, row, decision.Before, decision.After, response.Error);
                return response;
            }

            // Only Server name and Password are live; every other rule waits for the restart (never edited in memory).
            if (spec.Live)
            {
                try
                {
                    if (spec.Key == ServerRules.Password) SetLivePassword?.Invoke(decision.Value);
                }
                catch (Exception e)
                {
                    decision.Note = "Saved, but the running server did not take it (" + e.Message + "): it applies at the next start.";
                }
            }
        }

        response.Ok = true;
        response.Note = decision.Note;
        response.State = BuildState();
        // Live 2026-10-06: an unchanged value writes nothing, so it logs no accepted change either.
        if (!decision.Unchanged) WriteAudit(true, actor, row, decision.Before, decision.After, "");
        return response;
    }

    private static void InstallWriteSeams()
    {
#if !PROGENITOR_TESTS
        SetLivePassword = ServerRulesGame.SetLivePassword;
#endif
    }

    private static void ResetWriteSeams()
    {
        Persist = JsonFileEdit.Persist;
        SetLivePassword = null;
    }
}
