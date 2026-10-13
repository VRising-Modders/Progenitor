using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stage 5 (docs/settings-organization.md, decisions 4, 5 and 8): the rows Progenitor's page draws
/// for values the admin channel (<c>'PrJa'</c>) holds — the mid-session choice and the can't-be-checked choice (also in the
/// Players joining popup: one stored value), the answer deadline, photo mode and ally-safe hits on this server, remote
/// installs and their admin list, and the no-Progenitor notice's timing. Every row is shown only to an admin of the joined
/// server (the Players joining rule) and reads the state the server last answered with. Game-free: the client sets
/// <see cref="StateProbe"/>, <see cref="Requester"/> and <see cref="SelfSteamId"/> (<c>JoinServerSettings.Install</c>); tests set
/// their own.
/// </summary>
internal static class JoinAdminRows
{
    /// <summary>The state the server last answered with (live: <c>JoinAdminClient.State</c>), or null.</summary>
    internal static Func<JoinAdminState?>? StateProbe { get; set; }

    /// <summary>
    /// Send one request; the callback gets the answer, or null when none came. Returns null when it was sent, else why not
    /// (live: <c>JoinAdminClient.Request</c>).
    /// </summary>
    internal static Func<JoinAdminRequest, Action<JoinAdminResponse?>, string?>? Requester { get; set; }

    /// <summary>This player's SteamID64 (0 when unknown): the install list's confirmation names it.</summary>
    internal static Func<ulong>? SelfSteamId { get; set; }

    /// <summary>The admin rule (live: <see cref="JoinServerSettings.PlayersJoiningShown"/>); tests set it.</summary>
    internal static Func<bool>? AdminProbe { get; set; }

    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>A missing state is asked for at most this often while a row is considered.</summary>
    internal const double AskEverySeconds = 5;

    private static double _lastAsk = double.NegativeInfinity;

    internal const string NoAnswerError = "No answer from the server.";

    // ---- labels and help (the page's words) --------------------------------------------------------------

    internal const string AnswerTimeoutLabel = "Seconds before disconnect";
    // COPY2 / KEEPOUT1: when it applies (Handshake.RequiresAnswer: Keep them out, a server mod that is not NoNeedForSync or
    // ClientOnly, or a client mod policy that needs an answer).
    internal const string AnswerTimeoutHelp =
        "How long a player without Progenitor can stay before being disconnected (the countdown waits while they create a "
        + "character). Applies when Players without Progenitor is Keep them out, when a mod on this server needs Progenitor on "
        + "clients, or when the client mod rules need a check. 0: no deadline (Keep them out uses 10). 0 to 300.";

    internal const string PhotoModeLabel = "Allow photo mode on this server";
    internal const string PhotoModeHelp =
        "Off: players with Progenitor cannot enter photo mode here and leave it if they are in it, and the admin world pause is "
        + "refused. A player without Progenitor is not affected. Applies at once.";

    internal const string AllySafeHitsLabel = "Ally-safe hits";
    internal const string AllySafeHitsHelp =
        "Hits with no team check skip allies when the caster was spawned by a mod through Progenitor; vanilla units and bosses are "
        + "unchanged. Applies at once.";

    internal const string RemoteInstallLabel = "Remote mod installs";
    internal const string RemoteInstallHelp =
        "Lets the admins below install, update and uninstall Thunderstore mods on this server from their Install tab. Off: those "
        + "requests are refused.";

    internal const string AllowedIdsLabel = "Admins who may install mods";
    internal const string AllowedIdsHelp =
        "SteamID64s, separated by commas. Empty: any admin of this server. Whoever is listed can install, update and remove mods "
        + "on this server.";

    internal const string NoticeSecondsLabel = "First notice after (seconds)";
    internal const string NoticeSecondsHelp =
        "Seconds after the countdown starts before a player without Progenitor gets the chat message. 0: no message. A value at or "
        + "past the disconnect is moved to one second before it.";

    internal const string NoticeRepeatLabel = "Repeat notice every (seconds)";
    internal const string NoticeRepeatHelp = "Seconds between repeats of the message until the disconnect. 2 to 300.";

    internal const string CreationLimitLabel = "Creation time limit (seconds)";
    internal const string CreationLimitHelp =
        "Character creation shows no chat, so the message and the disconnect wait until the character is in the world, but no "
        + "longer than this from the ask. 0 to 3600.";

    /// <summary>
    /// Review fix (live: long names were cut with an ellipsis): the page's row names are short; the Players joining popup keeps
    /// its longer headings (<see cref="ClientPolicyText.MidSessionHeading"/>, <see cref="ClientPolicyText.UnverifiedHeading"/>).
    /// </summary>
    // COPY2: the Server tab row (the name column holds 30 characters); the panel heading is ClientPolicyText.MidSessionHeading.
    internal const string MidSessionLabel = "Mods break rules while playing";

    internal const string UnverifiedLabel = "If mods can't be checked";

    // REQRULE5 (requested 2026-10-08): Disconnect them's delay, under Mods break rules while playing. Named apart from
    // Players without Progenitor's Seconds before disconnect, which is a different setting.
    internal const string DisconnectGraceLabel = "Seconds to fix a mod";

    internal static readonly string DisconnectGraceHelp =
        "With Disconnect them: how long a player has to fix a mod that breaks the rules while playing (a mod you don't allow, or a "
        + "required mod removed or turned off) before they're disconnected. "
        + ClientPolicyRecheckLimits.MinDisconnectGraceSeconds.ToString(CultureInfo.InvariantCulture) + " to "
        + ClientPolicyRecheckLimits.MaxDisconnectGraceSeconds.ToString(CultureInfo.InvariantCulture) + "; "
        + ClientPolicyRecheckLimits.DefaultDisconnectGraceSeconds.ToString(CultureInfo.InvariantCulture) + " by default. A mod you don't allow "
        + "that was loaded before you change the rules gets at least " + ClientPolicyRecheckLimits.MinPolicyChangeGraceSeconds.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>REQRULE5: the note on Seconds to fix a mod while the rule is Pause the mod and warn them (nobody is disconnected).</summary>
    internal const string DisconnectGraceInactive = "Not in effect: Mods break rules while playing is Pause the mod and warn them.";

    internal const string MidSessionHelp =
        "What happens when, after joining, a player's game loads a mod this server doesn't allow, or a mod it requires is removed "
        + "or turned off: a pause or a notice, or a notice and a disconnect.";

    // KEEPOUT1 (decided 2026-10-06): the explicit Players without Progenitor choice.
    internal const string KeepOutLabel = "Players without Progenitor";
    internal const string KeepOutLetPlay = "Let them play";
    internal const string KeepOutKeepOut = "Keep them out";
    internal const string KeepOutLetPlayShort = "They can play, unless this server's mods need Progenitor on clients or its mod rules need a check.";
    internal static readonly string KeepOutKeepOutShort =
        "They get the Message without Progenitor and are disconnected after Seconds before disconnect (0 counts as "
        + NoProgenitorKeepOut.KeepOutMinSeconds.ToString(CultureInfo.InvariantCulture) + ").";
    internal const string KeepOutHelp =
        "Whether a player whose game has no Progenitor (it never answers the handshake) can play here. Keep them out applies "
        + "whatever this server's mods need; If mods can't be checked refuses such players only with Only approved mods.";

    // REQRULE7: plain words (was "Also in the Players joining popup (one stored value).").
    internal const string AlsoInPanel = " You can also change this in the Players joining popup.";

    internal const string RemoteOffHeading = "Turn off remote installs?";
    internal const string RemoteOffBody =
        "No admin, you included, can install, update or remove mods on this server from the Install tab until it is turned on "
        + "again here or in the server's config file.";
    internal const string RemoteOffYes = "Turn off";

    internal const string SelfOffHeading = "Remove yourself from the list?";
    internal const string SelfOffBody =
        "You will no longer be able to install, update or remove mods on this server. Only the admins listed can add you back "
        + "(or the server's config file).";
    internal const string SelfOffYes = "Remove me";

    // ---- the server's values -------------------------------------------------------------------------------

    internal static JoinAdminState? State
    {
        get
        {
            try { return StateProbe?.Invoke(); }
            catch { return null; }
        }
    }

    /// <summary>The value of operation <paramref name="op"/> in the server's last state, or null while it has not said.</summary>
    internal static string? Value(string op)
    {
        var s = State;
        if (s == null) return null;
        switch (op)
        {
            case JoinAdminOps.MidSession:
                return s.MidSession.Length > 0 ? s.MidSession : null;
            case JoinAdminOps.Unverified:
                return s.PolicySupported && s.Unverified.Length > 0 ? s.Unverified : null;
            case JoinAdminOps.VersionMatching:
                return s.MatchingSupported && s.VersionMatching.Length > 0 ? s.VersionMatching : null;
            default:
                return s.OptionsSupported && s.Options.TryGetValue(op, out var v) ? v : null;
        }
    }

    private static string _lockText = "";
    private static ServerSettingsLock.LockSet _lock = ServerSettingsLock.LockSet.Empty;

    /// <summary>The joined server's lock list as its last state said (empty before one arrives).</summary>
    internal static ServerSettingsLock.LockSet LockSet
    {
        get
        {
            var s = State;
            string text = s == null ? "" : string.Join(",", s.Lock);
            if (!string.Equals(text, _lockText, StringComparison.Ordinal))
            {
                _lockText = text;
                _lock = ServerSettingsLock.Parse(text);
            }

            return _lock;
        }
    }

    /// <summary>The entry of the joined server's lock list that locks <paramref name="settingId"/>, or null.</summary>
    internal static string? LockedBy(string settingId) => LockSet.Match(settingId);

    /// <summary>A text that changes when the joined server's lock list does.</summary>
    internal static string LockFingerprint() => LockSet.Fingerprint;

    /// <summary>
    /// Whether the joined server has row <paramref name="op"/>: true until its state arrives (the row then waits), else
    /// what the state carries (a server older than stage 5 sends no options; one older than CLIENTPOLICY3 no mid-session).
    /// </summary>
    internal static bool Supported(string op)
    {
        var s = State;
        if (s == null) return true;
        return op switch
        {
            JoinAdminOps.MidSession => s.MidSession.Length > 0,
            JoinAdminOps.Unverified => s.PolicySupported,
            JoinAdminOps.VersionMatching => s.MatchingSupported,
            _ => s.OptionsSupported && s.Options.ContainsKey(op),
        };
    }

    /// <summary>
    /// The show-if of every row (decision 5): this player is an admin of the joined server (the Players joining rule) and
    /// the server has the row. While no state has arrived it asks for one, at most every <see cref="AskEverySeconds"/>;
    /// with a state it asks again at most every <see cref="RefreshEverySeconds"/>, so an operator's change to the server's
    /// files (its lock list, a value edited in the cfg) reaches an open page.
    /// </summary>
    /// <summary>Three homes: this player is an admin of the joined server (the rule every admin-channel row is shown by).</summary>
    internal static bool IsAdmin()
    {
        try { return AdminProbe?.Invoke() ?? JoinServerSettings.PlayersJoiningShown(); }
        catch { return false; }
    }

    internal static bool Shown(string op)
    {
        bool admin;
        try { admin = AdminProbe?.Invoke() ?? JoinServerSettings.PlayersJoiningShown(); }
        catch { admin = false; }
        if (!admin) return false;
        var s = State;
        AskIfDue(s == null || (!s.OptionsSupported && ServerOptionOps.IsOption(op)) ? AskEverySeconds : RefreshEverySeconds);
        return Supported(op);
    }

    /// <summary>A known state is asked for again at most this often while a row is considered.</summary>
    internal const double RefreshEverySeconds = 30;

    /// <summary>Ask for the server's state (a <c>get</c>) unless one was asked within <paramref name="every"/> seconds.</summary>
    internal static bool AskIfDue(double every = AskEverySeconds)
    {
        double now = Clock();
        if (now - _lastAsk < every && now >= _lastAsk) return false;
        _lastAsk = now;
        try { return Requester?.Invoke(new JoinAdminRequest { Op = JoinAdminOps.Get }, _ => { }) == null && Requester != null; }
        catch { return false; }
    }

    /// <summary>Send <paramref name="value"/> for <paramref name="op"/>; <paramref name="done"/> gets the server's note or refusal.</summary>
    internal static void Send(string op, string value, Action<bool, string> done)
    {
        var request = new JoinAdminRequest { Op = op };
        if (ServerOptionOps.IsSeconds(op))
        {
            if (!int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                done(false, "Give whole " + (ServerOptions.Find(op)?.Unit ?? "seconds") + ".");
                return;
            }

            request.Seconds = seconds;
        }
        else request.Value = value ?? "";

        var requester = Requester;
        if (requester == null)
        {
            done(false, "Not connected to a server with Progenitor.");
            return;
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

    private static AdminSettingSpec Spec(string op, AdminSettingKind kind, string label, string help, int min = 0, int max = 0,
        Func<string?>? note = null) => new()
    {
        Note = note,
        SettingId = SettingPlaces.JoinAdminId(op),
        Key = op,
        Label = label,
        Help = help,
        Kind = kind,
        Min = min,
        Max = max,
        Units = kind == AdminSettingKind.Number ? "seconds" : "",
        Read = () => Value(op),
        Send = (v, done) => Send(op, v, done),
        LockedBy = () => LockedBy(SettingPlaces.JoinAdminId(op)),
        Display = kind == AdminSettingKind.Number ? v => v + " s" : null,
    };

    internal static AdminSettingSpec MidSession() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(JoinAdminOps.MidSession),
        Key = JoinAdminOps.MidSession,
        Label = MidSessionLabel,
        Help = MidSessionHelp + AlsoInPanel,
        Kind = AdminSettingKind.Choice,
        Choices = new[] { ClientModRules.WordWarn, ClientModRules.WordDisconnect },
        ChoiceLabels = new[] { ClientPolicyText.MidSessionWarn, ClientPolicyText.MidSessionDisconnect },
        ChoiceRadio = true,
        ChoiceDescriptions = new[] { ClientPolicyText.MidSessionWarnShort, ClientPolicyText.MidSessionDisconnectShort },
        Read = () => Value(JoinAdminOps.MidSession),
        Send = (v, done) => Send(JoinAdminOps.MidSession, v, done),
        LockedBy = () => LockedBy(SettingPlaces.JoinAdminId(JoinAdminOps.MidSession)),
    };

    /// <summary>
    /// Mod compatibility C3: Version matching (range / minor / exact), the Players joining popup's radio list mirrored here.
    /// Read-only while the server's cfg forces it (<c>[ClientPolicy] VersionMatching</c>) or the lock list names it.
    /// </summary>
    internal static AdminSettingSpec VersionMatching()
    {
        var options = VersionMatchText.Options;
        var choices = new string[options.Count];
        var labels = new string[options.Count];
        var about = new string[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            choices[i] = options[i].Value;
            labels[i] = options[i].Label;
            about[i] = options[i].Description;
        }

        return new AdminSettingSpec
        {
            SettingId = SettingPlaces.JoinAdminId(JoinAdminOps.VersionMatching),
            Key = JoinAdminOps.VersionMatching,
            Label = VersionMatchText.Label,
            Help = VersionMatchText.Help + AlsoInPanel,
            Kind = AdminSettingKind.Choice,
            Choices = choices,
            ChoiceLabels = labels,
            ChoiceRadio = true,
            ChoiceDescriptions = about,
            Read = () => Value(JoinAdminOps.VersionMatching),
            Send = (v, done) => Send(JoinAdminOps.VersionMatching, v, done),
            LockedBy = () => LockedBy(SettingPlaces.JoinAdminId(JoinAdminOps.VersionMatching)),
            ReadOnlyReason = () => VersionMatchingForcedNote(),
            // Mod compatibility C4: what Exact means for Progenitor on this server, while Exact is the value.
            Note = () => ProgenitorExactNote(),
        };
    }

    /// <summary>C4: the joined server's Progenitor line while its Version matching is Exact version for all, or null.</summary>
    internal static string? ProgenitorExactNote()
    {
        JoinAdminState? s;
        try { s = StateProbe?.Invoke(); }
        catch { s = null; }
        string line = ProgenitorExact.AdminLine(s, Value(JoinAdminOps.VersionMatching));
        return line.Length > 0 ? line : null;
    }

    /// <summary>C3: the note while the joined server's cfg forces Version matching, or null.</summary>
    internal static string? VersionMatchingForcedNote()
    {
        JoinAdminState? s;
        try { s = StateProbe?.Invoke(); }
        catch { s = null; }
        return s is { MatchingSupported: true, VersionMatchingForced: true } && VersionMatchingText.TryParse(s.VersionMatching, out var mode)
            ? VersionMatchCfg.ForcedNote(mode) : null;
    }

    /// <summary>C3: the Server tab's one-line summary of the per-mod rules (<c>None</c>, or each mod and its rule).</summary>
    internal static string ServerModRulesLine()
    {
        var s = State;
        return s is { MatchingSupported: true } ? VersionMatchText.RulesSummary(s.ServerMods) : "";
    }

    /// <summary>REQRULE5: Seconds to fix a mod (<c>[ClientPolicy] DisconnectGraceSeconds</c>), right below Mods break rules while playing.</summary>
    internal static AdminSettingSpec DisconnectGrace()
    {
        return Spec(ServerOptionOps.DisconnectGrace, AdminSettingKind.Number, DisconnectGraceLabel, DisconnectGraceHelp,
            ClientPolicyRecheckLimits.MinDisconnectGraceSeconds, ClientPolicyRecheckLimits.MaxDisconnectGraceSeconds, DisconnectGraceInactiveNote);
    }

    internal static string? DisconnectGraceInactiveNote()
    {
        JoinAdminState? s;
        try { s = StateProbe?.Invoke(); }
        catch { s = null; }
        if (s == null || s.MidSession.Length == 0) return null;
        return string.Equals(s.MidSession, ClientModRules.WordDisconnect, StringComparison.OrdinalIgnoreCase) ? null : DisconnectGraceInactive;
    }

    internal static AdminSettingSpec Unverified() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(JoinAdminOps.Unverified),
        Key = JoinAdminOps.Unverified,
        Label = UnverifiedLabel,
        Help = ClientPolicyText.UnverifiedHelp + AlsoInPanel,
        Kind = AdminSettingKind.Choice,
        Choices = new[] { "allow", "refuse" },
        ChoiceLabels = new[] { ClientPolicyText.LetThemJoin, ClientPolicyText.RefuseThem },
        ChoiceRadio = true,
        ChoiceDescriptions = new[] { ClientPolicyText.LetThemJoinShort, ClientPolicyText.RefuseThemShort },
        Read = () => Value(JoinAdminOps.Unverified),
        Send = (v, done) => Send(JoinAdminOps.Unverified, v, done),
        LockedBy = () => LockedBy(SettingPlaces.JoinAdminId(JoinAdminOps.Unverified)),
        // COPY2: it has no effect while Which client mods players may use is Any mods (ClientModPolicy.RequiresAnswer).
        // REQRULE7: grayed with a one-line reason while the mod rule is Any mods (it does nothing then); the Players joining
        // popup still edits it.
        ReadOnlyReason = () => UnverifiedInactiveNote(),
    };

    /// <summary>COPY2: the note on If mods can't be checked while the mode is Any mods (the choice then does nothing).</summary>
    internal const string UnverifiedInactive = "Only applies with Only approved mods.";

    internal static string? UnverifiedInactiveNote()
    {
        JoinAdminState? s;
        try { s = StateProbe?.Invoke(); }
        catch { s = null; }
        if (s == null || !s.PolicySupported) return null;
        return string.Equals(s.PolicyMode, "approved", StringComparison.OrdinalIgnoreCase) ? null : UnverifiedInactive;
    }

    /// <summary>KEEPOUT1: Players without Progenitor: Let them play / Keep them out (<c>[Networking] RequireProgenitor</c>).</summary>
    internal static AdminSettingSpec KeepOut() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.RequireProgenitor),
        Key = ServerOptionOps.RequireProgenitor,
        Label = KeepOutLabel,
        Help = KeepOutHelp,
        Kind = AdminSettingKind.Choice,
        Choices = new[] { NoProgenitorKeepOut.ValueLetPlay, NoProgenitorKeepOut.ValueKeepOut },
        ChoiceLabels = new[] { KeepOutLetPlay, KeepOutKeepOut },
        ChoiceRadio = true,
        ChoiceDescriptions = new[] { KeepOutLetPlayShort, KeepOutKeepOutShort },
        Read = () => Value(ServerOptionOps.RequireProgenitor),
        Send = (v, done) => Send(ServerOptionOps.RequireProgenitor, v, done),
        LockedBy = () => LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.RequireProgenitor)),
    };

    internal static AdminSettingSpec AnswerTimeout() =>
        Spec(ServerOptionOps.AnswerTimeout, AdminSettingKind.Number, AnswerTimeoutLabel, AnswerTimeoutHelp, 0, 300);

    internal static AdminSettingSpec PhotoMode() =>
        Spec(ServerOptionOps.PhotoMode, AdminSettingKind.Toggle, PhotoModeLabel, PhotoModeHelp);

    internal static AdminSettingSpec AllySafeHits() =>
        Spec(ServerOptionOps.AllySafeHits, AdminSettingKind.Toggle, AllySafeHitsLabel, AllySafeHitsHelp);

    internal static AdminSettingSpec RemoteInstall()
    {
        var spec = Spec(ServerOptionOps.RemoteInstall, AdminSettingKind.Toggle, RemoteInstallLabel, RemoteInstallHelp);
        return new AdminSettingSpec
        {
            SettingId = spec.SettingId, Key = spec.Key, Label = spec.Label, Help = spec.Help, Kind = spec.Kind,
            Read = spec.Read, Send = spec.Send, LockedBy = spec.LockedBy,
            Confirm = (current, next) => IsOn(current) && !IsOn(next) ? new AdminSettingConfirm(RemoteOffHeading, RemoteOffBody, RemoteOffYes) : null,
        };
    }

    internal static AdminSettingSpec AllowedSteamIds() => new()
    {
        SettingId = SettingPlaces.JoinAdminId(ServerOptionOps.AllowedSteamIds),
        Key = ServerOptionOps.AllowedSteamIds,
        Label = AllowedIdsLabel,
        Help = AllowedIdsHelp,
        Kind = AdminSettingKind.Text,
        MaxLength = ServerOptions.MaxSteamIds * 22,
        Read = () => Value(ServerOptionOps.AllowedSteamIds),
        Send = (v, done) => Send(ServerOptionOps.AllowedSteamIds, v, done),
        LockedBy = () => LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.AllowedSteamIds)),
        Display = v => v.Trim().Length == 0 ? "Any admin" : v,
        Confirm = (current, next) =>
        {
            ulong self = 0;
            try { self = SelfSteamId?.Invoke() ?? 0; }
            catch { self = 0; }
            return ServerOptions.CutsOffSelf(current, next, self) ? new AdminSettingConfirm(SelfOffHeading, SelfOffBody, SelfOffYes) : null;
        },
    };

    internal static AdminSettingSpec NoticeSeconds() =>
        Spec(ServerOptionOps.NoticeSeconds, AdminSettingKind.Number, NoticeSecondsLabel, NoticeSecondsHelp, 0, 300);

    internal static AdminSettingSpec NoticeRepeatSeconds() =>
        Spec(ServerOptionOps.NoticeRepeatSeconds, AdminSettingKind.Number, NoticeRepeatLabel, NoticeRepeatHelp, NoAnswerNoticeSettings.MinRepeatSeconds, 300);

    internal static AdminSettingSpec NoticeCreationLimitSeconds() =>
        Spec(ServerOptionOps.NoticeCreationLimitSeconds, AdminSettingKind.Number, CreationLimitLabel, CreationLimitHelp, 0, 3600);

    private static bool IsOn(string? v) => string.Equals((v ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The settings_places reader (<c>ModsMenu.JoinAdminValueReader</c>): <c>(value, display)</c> of admin-channel
    /// operation <paramref name="op"/> in the server's last state — the same value the page's row and the Players joining
    /// popup show — or null while the server has not said.
    /// </summary>
    internal static (string Value, string Display)? ReadPlace(string op)
    {
        var s = State;
        string? value = op switch
        {
            JoinAdminOps.PolicyMode => s is { PolicySupported: true } ? s.PolicyMode : null,
            JoinAdminOps.Window => s?.WindowSeconds.ToString(CultureInfo.InvariantCulture),
            _ => Value(op ?? ""),
        };
        return value == null ? null : (value, SettingPlaces.Display(value));
    }

    /// <summary>Tests and teardown: forget the seams and the last ask.</summary>
    internal static void Reset()
    {
        StateProbe = null;
        Requester = null;
        SelfSteamId = null;
        AdminProbe = null;
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastAsk = double.NegativeInfinity;
        _lockText = "";
        _lock = ServerSettingsLock.LockSet.Empty;
    }
}
