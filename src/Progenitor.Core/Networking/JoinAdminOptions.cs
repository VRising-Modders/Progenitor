using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;
#if !PROGENITOR_TESTS
using BepInEx.Configuration;
using Progenitor.Config;
#endif

namespace Progenitor.Networking;

/// <summary>
/// Settings organization stage 5 (docs/settings-organization.md, decisions 4, 5 and 8): server values an admin edits
/// from Progenitor's page over the admin channel (<c>'PrJa'</c>), with its authorization, rate limit and audit. Each
/// is one entry of the server's <c>progenitor.cfg</c>; the page reads the value from the channel's state and
/// writes it through the operation named here. Game-free.
/// </summary>
internal static class ServerOptionOps
{
    /// <summary><c>[Networking] HandshakeAnswerTimeoutSeconds</c>: seconds before a player without Progenitor is disconnected (0 turns the deadline off).</summary>
    internal const string AnswerTimeout = "answer-timeout";

    /// <summary>KEEPOUT1: <c>[Networking] RequireProgenitor</c>: keep out players without Progenitor (false: let them play).</summary>
    internal const string RequireProgenitor = "require-progenitor";

    /// <summary><c>[PhotoMode] AllowOnThisServer</c>.</summary>
    internal const string PhotoMode = "photo-mode";

    /// <summary><c>[Ai] AllySafeHits</c>.</summary>
    internal const string AllySafeHits = "ally-safe-hits";

    /// <summary><c>[ContainerPreview] RemotePreview</c>: remote chest preview off, on PvE servers only, or on.</summary>
    internal const string ContainerPreview = "container-preview";

    /// <summary><c>[RemoteInstall] Enabled</c>.</summary>
    internal const string RemoteInstall = "remote-install";

    /// <summary><c>[RemoteInstall] AllowedSteamIds</c> (the operator may lock it out of the game: <c>AllowedSteamIdsInGame = false</c>).</summary>
    internal const string AllowedSteamIds = "allowed-steam-ids";

    /// <summary><c>[Networking] NoAnswerNoticeSeconds</c>.</summary>
    internal const string NoticeSeconds = "notice-seconds";

    /// <summary><c>[Networking] NoAnswerNoticeRepeatSeconds</c>.</summary>
    internal const string NoticeRepeatSeconds = "notice-repeat-seconds";

    /// <summary><c>[Networking] NoAnswerCreationLimitSeconds</c>.</summary>
    internal const string NoticeCreationLimitSeconds = "notice-creation-limit-seconds";

    /// <summary>
    /// REQRULE5: <c>[ClientPolicy] DisconnectGraceSeconds</c>: seconds a player gets to fix a mod that breaks the client mod
    /// rules while playing before Disconnect them disconnects them (10 to 300, 60 by default).
    /// </summary>
    internal const string DisconnectGrace = "disconnect-grace";

    // CHAT4 (docs/chat.md, Server settings): the Server tab's Chat group, server-only values in the server's progenitor.cfg.
    // They travel only on this channel (to admins), never in ConfigSync: the word list stays off players' machines, and the
    // webhook address is never sent at all (it reads "set" or empty).

    /// <summary><c>[ChatFilter] Enabled</c>.</summary>
    internal const string ChatFilter = "chat-filter";

    /// <summary><c>[ChatFilter] Words</c>: the filtered words, comma separated.</summary>
    internal const string ChatFilterWords = "chat-filter-words";

    /// <summary><c>[ChatFilter] Action</c>: <c>replace</c> or <c>block</c>.</summary>
    internal const string ChatFilterAction = "chat-filter-action";

    /// <summary><c>[ChatRelay] Enabled</c>.</summary>
    internal const string ChatRelay = "chat-relay";

    /// <summary><c>[ChatRelay] WebhookUrl</c> (write-only: the state carries <c>set</c> or empty).</summary>
    internal const string ChatRelayUrl = "chat-relay-url";

    /// <summary><c>[ChatRelay] Global</c>, <c>Local</c>, <c>Clan</c>, <c>Whispers</c>, <c>System</c>: which channels are posted.</summary>
    internal const string ChatRelayGlobal = "chat-relay-global";

    internal const string ChatRelayLocal = "chat-relay-local";
    internal const string ChatRelayClan = "chat-relay-clan";
    internal const string ChatRelayWhispers = "chat-relay-whispers";
    internal const string ChatRelaySystem = "chat-relay-system";

    /// <summary><c>[HotLoad] Announce</c>: tell every connected player when a mod or Core hot-loads on the server (docs/hot-reload.md).</summary>
    internal const string HotLoadAnnounce = "hot-load-announce";

    // AUTOUPDATE3 (docs/auto-update.md, Server settings): the Server tab's Updates group, the server's [AutoUpdate] section.

    /// <summary><c>[AutoUpdate] Mods</c>: Automatic, Notify or Off.</summary>
    internal const string UpdateMods = "auto-update-mods";

    /// <summary><c>[AutoUpdate] MajorVersions</c>: Automatic or Notify.</summary>
    internal const string UpdateMajors = "auto-update-majors";

    /// <summary><c>[AutoUpdate] RestartWhenEmpty</c> (refused while <c>HostRestarts</c> is empty).</summary>
    internal const string UpdateRestartWhenEmpty = "auto-update-restart-when-empty";

    /// <summary><c>[AutoUpdate] EmptyMinutes</c>: the quiet period.</summary>
    internal const string UpdateEmptyMinutes = "auto-update-empty-minutes";

    /// <summary><c>[AutoUpdate] BackupBeforeUpdate</c>.</summary>
    internal const string UpdateBackup = "auto-update-backup";

    /// <summary><c>[AutoUpdate] PreUpdateBackupKeep</c>.</summary>
    internal const string UpdateBackupKeep = "auto-update-backup-keep";

    /// <summary><c>[AutoUpdate] PreUpdateBackupSkipHours</c>.</summary>
    internal const string UpdateBackupSkipHours = "auto-update-backup-skip-hours";

    /// <summary>AUTOUPDATE3: the Updates group's options, in page order.</summary>
    internal static readonly IReadOnlyList<string> UpdateOps = new[]
    {
        UpdateMods, UpdateMajors, UpdateRestartWhenEmpty, UpdateEmptyMinutes, UpdateBackup, UpdateBackupKeep, UpdateBackupSkipHours,
    };

    internal static readonly IReadOnlyList<string> All = new[]
    {
        AnswerTimeout, RequireProgenitor, DisconnectGrace, PhotoMode, AllySafeHits, ContainerPreview, RemoteInstall, AllowedSteamIds, NoticeSeconds, NoticeRepeatSeconds, NoticeCreationLimitSeconds,
        ChatFilter, ChatFilterWords, ChatFilterAction, ChatRelay, ChatRelayUrl,
        ChatRelayGlobal, ChatRelayLocal, ChatRelayClan, ChatRelayWhispers, ChatRelaySystem, HotLoadAnnounce,
        UpdateMods, UpdateMajors, UpdateRestartWhenEmpty, UpdateEmptyMinutes, UpdateBackup, UpdateBackupKeep, UpdateBackupSkipHours,
    };

    /// <summary>A text-valued option whose value is always written on the wire (it may be empty).</summary>
    internal static bool WritesEmptyValue(string? op) => ServerOptions.Find(op)?.Kind is ServerOptionKind.SteamIds or ServerOptionKind.Text
        or ServerOptionKind.Secret;

    internal static bool IsOption(string? op) => op != null && ServerOptions.Find(op) != null;

    internal static bool IsSeconds(string? op) => ServerOptions.Find(op)?.Kind == ServerOptionKind.Seconds;
}

internal enum ServerOptionKind
{
    /// <summary>Whole seconds, clamped to the option's range (sent as the request's <c>seconds</c>).</summary>
    Seconds = 0,

    /// <summary>On or off (sent as <c>value</c> <c>true</c> / <c>false</c>).</summary>
    Switch = 1,

    /// <summary>A list of SteamID64s (sent as <c>value</c>, comma separated; empty is allowed).</summary>
    SteamIds = 2,

    /// <summary>CHAT4: free text (sent as <c>value</c>; empty is allowed), checked and normalized by the spec.</summary>
    Text = 3,

    /// <summary>CHAT4: one of <see cref="ServerOptionSpec.Choices"/> (sent as <c>value</c>).</summary>
    Choice = 4,

    /// <summary>
    /// CHAT4: write-only text (the webhook address). The state, the audit and every log carry only <c>set</c> or empty
    /// (<see cref="ServerOptions.Shown"/>); every accepted write is stored.
    /// </summary>
    Secret = 5,
}

/// <summary>One server option: its operation, its cfg entry, its kind and its range.</summary>
internal sealed class ServerOptionSpec
{
    internal ServerOptionSpec(string op, string section, string key, ServerOptionKind kind, int min = 0, int max = 0)
    {
        Op = op;
        Section = section;
        Key = key;
        Kind = kind;
        Min = min;
        Max = max;
    }

    internal string Op { get; }
    internal string Section { get; }
    internal string Key { get; }
    internal ServerOptionKind Kind { get; }
    internal int Min { get; }
    internal int Max { get; }

    /// <summary>AUTOUPDATE3: a whole-number option's unit in a refusal (<c>seconds</c> unless set: <c>minutes</c>, <c>hours</c>, <c>backups</c>).</summary>
    internal string Unit { get; init; } = "seconds";

    /// <summary>CHAT4: a choice option's stored words.</summary>
    internal IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>CHAT4: a text or secret option's check: null when the value can be stored, else why not.</summary>
    internal Func<string, string?>? Check { get; init; }

    /// <summary>CHAT4: a text option's stored form of an accepted value (null: trimmed as sent).</summary>
    internal Func<string, string>? Normalize { get; init; }
}

/// <summary>
/// The server options' seams and rules. The server sets <see cref="Get"/> and <see cref="Set"/> to its cfg entries
/// (<c>JoinAdminServer.Install</c>); tests set their own.
/// </summary>
internal static class ServerOptions
{
    /// <summary>The most SteamIDs the install list keeps (a request over it is refused).</summary>
    internal const int MaxSteamIds = 64;

    /// <summary>REQRULE5: the cfg section and key of <see cref="ServerOptionOps.DisconnectGrace"/>.</summary>
    internal const string ClientPolicySection = "ClientPolicy";

    internal const string DisconnectGraceKey = "DisconnectGraceSeconds";

    internal const string NotLoadedError = "The server has not loaded this setting yet; try again in a moment.";
    internal const string StoreFailedError = "The server could not store this setting.";

    internal static readonly IReadOnlyList<ServerOptionSpec> Specs = new[]
    {
        new ServerOptionSpec(ServerOptionOps.AnswerTimeout, "Networking", "HandshakeAnswerTimeoutSeconds", ServerOptionKind.Seconds, 0, 300),
        new ServerOptionSpec(ServerOptionOps.RequireProgenitor, "Networking", NoProgenitorKeepOut.ConfigKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.DisconnectGrace, ClientPolicySection, DisconnectGraceKey, ServerOptionKind.Seconds,
            ClientPolicyRecheckLimits.MinDisconnectGraceSeconds, ClientPolicyRecheckLimits.MaxDisconnectGraceSeconds),
        new ServerOptionSpec(ServerOptionOps.PhotoMode, "PhotoMode", "AllowOnThisServer", ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.AllySafeHits, "Ai", "AllySafeHits", ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ContainerPreview, Progenitor.Inventory.ContainerPreviewSettings.Section,
            Progenitor.Inventory.ContainerPreviewSettings.ModeKey, ServerOptionKind.Choice)
        {
            Choices = Progenitor.Inventory.ContainerPreviewSettings.Words,
        },
        new ServerOptionSpec(ServerOptionOps.RemoteInstall, RemoteInstallSettings.Section, RemoteInstallSettings.EnabledKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.AllowedSteamIds, RemoteInstallSettings.Section, RemoteInstallSettings.AllowedSteamIdsKey, ServerOptionKind.SteamIds),
        new ServerOptionSpec(ServerOptionOps.NoticeSeconds, "Networking", "NoAnswerNoticeSeconds", ServerOptionKind.Seconds, 0, 300),
        new ServerOptionSpec(ServerOptionOps.NoticeRepeatSeconds, "Networking", "NoAnswerNoticeRepeatSeconds", ServerOptionKind.Seconds,
            NoAnswerNoticeSettings.MinRepeatSeconds, 300),
        new ServerOptionSpec(ServerOptionOps.NoticeCreationLimitSeconds, "Networking", "NoAnswerCreationLimitSeconds", ServerOptionKind.Seconds, 0, 3600),
        // CHAT4: the Server tab's Chat group (bound by Progenitor.Chat.ServerChat on the server).
        new ServerOptionSpec(ServerOptionOps.ChatFilter, ServerChatOptions.FilterSection, ServerChatOptions.EnabledKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ChatFilterWords, ServerChatOptions.FilterSection, ServerChatOptions.WordsKey, ServerOptionKind.Text)
        {
            Check = Progenitor.Chat.ChatWordFilter.Check,
            Normalize = Progenitor.Chat.ChatWordFilter.Normalize,
        },
        new ServerOptionSpec(ServerOptionOps.ChatFilterAction, ServerChatOptions.FilterSection, ServerChatOptions.ActionKey, ServerOptionKind.Choice)
        {
            Choices = Progenitor.Chat.ChatWordFilter.ActionWords,
        },
        new ServerOptionSpec(ServerOptionOps.ChatRelay, ServerChatOptions.RelaySection, ServerChatOptions.EnabledKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ChatRelayUrl, ServerChatOptions.RelaySection, ServerChatOptions.WebhookUrlKey, ServerOptionKind.Secret)
        {
            Check = Progenitor.Chat.ChatRelayFormat.CheckWebhookUrl,
        },
        new ServerOptionSpec(ServerOptionOps.ChatRelayGlobal, ServerChatOptions.RelaySection, ServerChatOptions.GlobalKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ChatRelayLocal, ServerChatOptions.RelaySection, ServerChatOptions.LocalKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ChatRelayClan, ServerChatOptions.RelaySection, ServerChatOptions.ClanKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ChatRelayWhispers, ServerChatOptions.RelaySection, ServerChatOptions.WhispersKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.ChatRelaySystem, ServerChatOptions.RelaySection, ServerChatOptions.SystemKey, ServerOptionKind.Switch),
        // Hot-load chat lines (bound by Progenitor.Reload.HotLoadNoticeLive on the server).
        new ServerOptionSpec(ServerOptionOps.HotLoadAnnounce, ServerChatOptions.HotLoadSection, ServerChatOptions.HotLoadAnnounceKey, ServerOptionKind.Switch),
        // AUTOUPDATE3: the Server tab's Updates group (bound by ServerAutoUpdateLive on the server).
        new ServerOptionSpec(ServerOptionOps.UpdateMods, ServerUpdateSettings.Section, ServerUpdateSettings.ModsKey, ServerOptionKind.Choice)
        {
            Choices = UpdateSettings.ModeWords,
        },
        new ServerOptionSpec(ServerOptionOps.UpdateMajors, ServerUpdateSettings.Section, ServerUpdateSettings.MajorKey, ServerOptionKind.Choice)
        {
            Choices = UpdateSettings.MajorWords,
        },
        new ServerOptionSpec(ServerOptionOps.UpdateRestartWhenEmpty, ServerUpdateSettings.Section, ServerUpdateSettings.RestartWhenEmptyKey, ServerOptionKind.Switch)
        {
            // Progenitor never starts a server again: without the operator's HostRestarts the setting would only stop it.
            Check = value => value == "true" ? ServerUpdateSettings.RestartRefusal(SafeHostRestarts()) : null,
        },
        new ServerOptionSpec(ServerOptionOps.UpdateEmptyMinutes, ServerUpdateSettings.Section, ServerUpdateSettings.EmptyMinutesKey, ServerOptionKind.Seconds,
            ServerUpdateSettings.MinEmptyMinutes, ServerUpdateSettings.MaxEmptyMinutes) { Unit = "minutes" },
        new ServerOptionSpec(ServerOptionOps.UpdateBackup, ServerUpdateSettings.Section, ServerUpdateSettings.BackupKey, ServerOptionKind.Switch),
        new ServerOptionSpec(ServerOptionOps.UpdateBackupKeep, ServerUpdateSettings.Section, ServerUpdateSettings.BackupKeepKey, ServerOptionKind.Seconds,
            Progenitor.WorldRestore.WorldBackupRetention.PreUpdateMinKeep, Progenitor.WorldRestore.WorldBackupRetention.PreUpdateMaxKeep) { Unit = "backups" },
        new ServerOptionSpec(ServerOptionOps.UpdateBackupSkipHours, ServerUpdateSettings.Section, ServerUpdateSettings.BackupSkipHoursKey, ServerOptionKind.Seconds,
            0, ServerUpdateSettings.MaxSkipHours) { Unit = "hours" },
    };

    /// <summary>AUTOUPDATE3: the server's <c>[AutoUpdate] HostRestarts</c> as the auto-updater reads it now.</summary>
    private static string SafeHostRestarts()
    {
        try { return ServerAutoUpdate.SettingsProbe()?.HostRestarts ?? ""; }
        catch { return ""; }
    }

    /// <summary>The value as the server holds it now (invariant text; a switch reads <c>true</c> / <c>false</c>), or null when not bound.</summary>
    internal static Func<string, string?>? Get { get; set; }

    /// <summary>Store a decided value; false when it could not be stored.</summary>
    internal static Func<string, string, bool>? Set { get; set; }

    internal static ServerOptionSpec? Find(string? op)
    {
        if (string.IsNullOrEmpty(op)) return null;
        foreach (var spec in Specs)
            if (string.Equals(spec.Op, op, StringComparison.Ordinal)) return spec;
        return null;
    }

    /// <summary>
    /// The value as the server holds it now, or null when not bound. CHAT4: a <see cref="ServerOptionKind.Secret"/> option reads
    /// <c>set</c> or empty, never its value: this is what the state, the audit and the logs carry.
    /// </summary>
    internal static string? Current(string op)
    {
        string? value;
        try { value = Get?.Invoke(op); }
        catch { return null; }
        return value == null ? null : Shown(op, value);
    }

    /// <summary>CHAT4: <paramref name="value"/> as the channel may show it (a secret as <c>set</c> or empty; anything else as is).</summary>
    internal static string Shown(string? op, string? value) =>
        Find(op)?.Kind == ServerOptionKind.Secret ? Progenitor.Chat.ChatRelayFormat.Redact(value) : value ?? "";

    /// <summary>
    /// The SteamID64s of <paramref name="text"/> in the order typed, each once (<see cref="RemoteInstallSettings.IsSteamId64"/>),
    /// and the tokens that are not one.
    /// </summary>
    internal static List<string> ParseSteamIds(string? text, out List<string> rejected)
    {
        var ids = new List<string>();
        rejected = new List<string>();
        foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.Length == 0) continue;
            if (RemoteInstallSettings.IsSteamId64(token, out _))
            {
                if (!ids.Contains(token)) ids.Add(token);
            }
            else rejected.Add(token);
        }

        return ids;
    }

    /// <summary>The list as stored: the ids in the order typed, joined with <c>", "</c>.</summary>
    internal static string NormalizeSteamIds(string? text) => string.Join(", ", ParseSteamIds(text, out _));

    /// <summary>
    /// Whether a change of the install list takes the right to install away from <paramref name="self"/> (the admin
    /// making it): today the list lets them (empty means any admin, or it names them), afterwards it does not. The
    /// page asks for a confirmation before sending such a change.
    /// </summary>
    internal static bool CutsOffSelf(string? before, string? after, ulong self)
    {
        if (self == 0) return false;
        string me = self.ToString(CultureInfo.InvariantCulture);
        var old = ParseSteamIds(before, out _);
        var next = ParseSteamIds(after, out _);
        bool allowedNow = old.Count == 0 || old.Contains(me);
        bool allowedAfter = next.Count == 0 || next.Contains(me);
        return allowedNow && !allowedAfter;
    }

    /// <summary>Tests: forget the seams.</summary>
    internal static void Reset()
    {
        Get = null;
        Set = null;
    }
}

/// <summary>
/// The admin channel's decisions for the server options. Game-free; the caller (<see cref="JoinAdminServer"/>) has
/// applied the rate limit, the handshake and the authorization, and stores <see cref="JoinAdminDecision.OptionValue"/>.
/// </summary>
internal static class ServerOptionDecisions
{
    internal static JoinAdminDecision Decide(JoinAdminConfig current, JoinAdminRequest request, string op)
    {
        string row = JoinAdminPolicy.AuditRowFor(op, "");
        var spec = ServerOptions.Find(op);
        if (spec == null) return JoinAdminDecision.Refused(current, row, JoinAdminPolicy.UnknownOpError);
        string? before = ServerOptions.Current(op);
        // The operator's lock list (ServerSettingsLock) is applied before this, for every operation (JoinAdminServer.Apply).
        if (before == null) return JoinAdminDecision.Refused(current, row, ServerOptions.NotLoadedError);

        switch (spec.Kind)
        {
            case ServerOptionKind.Seconds:
            {
                if (!request.HasSeconds)
                    return JoinAdminDecision.Refused(current, row,
                        "Give whole " + spec.Unit + ", " + spec.Min.ToString(CultureInfo.InvariantCulture) + " to " + spec.Max.ToString(CultureInfo.InvariantCulture) + ".");
                int asked = request.Seconds;
                int value = asked < spec.Min ? spec.Min : asked > spec.Max ? spec.Max : asked;
                string text = value.ToString(CultureInfo.InvariantCulture);
                string note = value != asked
                    ? "Clamped to " + text + " " + spec.Unit + " (" + spec.Min.ToString(CultureInfo.InvariantCulture) + " to " + spec.Max.ToString(CultureInfo.InvariantCulture) + ")."
                    : "";
                return Accepted(current, row, op, before, text, note);
            }

            case ServerOptionKind.Switch:
            {
                string v = (request.Value ?? "").Trim().ToLowerInvariant();
                bool? on = v is "true" or "on" or "1" ? true : v is "false" or "off" or "0" ? false : null;
                if (on == null) return JoinAdminDecision.Refused(current, row, "Choose on or off.", before, request.Value ?? "");
                // AUTOUPDATE3: a switch may refuse a value (Restart when empty without HostRestarts).
                string word = on.Value ? "true" : "false";
                if (!string.Equals(before, word, StringComparison.Ordinal) && SafeCheck(spec, word) is { } refused)
                    return JoinAdminDecision.Refused(current, row, refused, before, word);
                return Accepted(current, row, op, before, word, "");
            }

            case ServerOptionKind.Text:
            {
                if (!request.HasValue) return JoinAdminDecision.Refused(current, row, "Give the text (it may be empty).");
                string asked = request.Value ?? "";
                string? why = SafeCheck(spec, asked);
                if (why != null) return JoinAdminDecision.Refused(current, row, why, before, asked);
                string stored = spec.Normalize != null ? spec.Normalize(asked) : asked.Trim();
                return Accepted(current, row, op, before, stored, "");
            }

            case ServerOptionKind.Choice:
            {
                string v = (request.Value ?? "").Trim();
                foreach (string choice in spec.Choices)
                    if (string.Equals(choice, v, StringComparison.OrdinalIgnoreCase)) return Accepted(current, row, op, before, choice, "");
                return JoinAdminDecision.Refused(current, row, "Choose one of: " + string.Join(", ", spec.Choices) + ".", before, request.Value ?? "");
            }

            case ServerOptionKind.Secret:
            {
                // The audit and the answer carry "set" or empty, never the value; every accepted write is stored (the shown
                // values cannot tell two addresses apart).
                if (!request.HasValue) return JoinAdminDecision.Refused(current, row, "Give the address (empty turns it off).");
                string asked = (request.Value ?? "").Trim();
                string? why = SafeCheck(spec, asked);
                if (why != null) return JoinAdminDecision.Refused(current, row, why, before, ServerOptions.Shown(op, asked));
                return new JoinAdminDecision
                {
                    Ok = true,
                    Config = current,
                    AuditRow = row,
                    Before = before,
                    After = ServerOptions.Shown(op, asked),
                    OptionOp = op,
                    OptionValue = asked,
                    Note = asked.Length == 0 ? "Cleared." : "Saved.",
                };
            }

            default:
            {
                if (!request.HasValue) return JoinAdminDecision.Refused(current, row, "Give the SteamID64 list (empty: any admin).");
                var ids = ServerOptions.ParseSteamIds(request.Value, out var rejected);
                if (rejected.Count > 0)
                {
                    var shown = rejected.Count > 3 ? rejected.GetRange(0, 3) : rejected;
                    return JoinAdminDecision.Refused(current, row, "Not a SteamID64: " + string.Join(", ", shown) + (rejected.Count > 3 ? ", …" : "") + ".",
                        ServerOptions.NormalizeSteamIds(before), request.Value ?? "");
                }

                if (ids.Count > ServerOptions.MaxSteamIds)
                    return JoinAdminDecision.Refused(current, row, "At most " + ServerOptions.MaxSteamIds + " SteamIDs.",
                        ServerOptions.NormalizeSteamIds(before), "");
                return Accepted(current, row, op, ServerOptions.NormalizeSteamIds(before), string.Join(", ", ids),
                    ids.Count == 0 ? "Any admin of this server may install mods remotely." : "");
            }
        }
    }

    private static string? SafeCheck(ServerOptionSpec spec, string value)
    {
        if (spec.Check == null) return null;
        try { return spec.Check(value); }
        catch { return "The server could not check this value."; }
    }

    private static JoinAdminDecision Accepted(JoinAdminConfig current, string row, string op, string before, string after, string note)
    {
        bool same = string.Equals(before, after, StringComparison.Ordinal);
        return new JoinAdminDecision
        {
            Ok = true,
            Config = current,
            AuditRow = row,
            Before = before,
            After = after,
            OptionOp = same ? null : op,
            OptionValue = same ? null : after,
            Note = same ? "Unchanged." : note,
        };
    }
}

/// <summary>
/// One <c>Key = value</c> line of a BepInEx cfg text, changed in place (review 2026-10-09). An admin edit used to save the
/// whole file from memory (BepInEx's save-on-set), which wrote the running server's stale values over every hand edit made
/// since it read the file. Only the edited line is written now; every other line, comment and key stays as it is on disk.
/// Game-free.
/// </summary>
internal static class CfgLineEdit
{
    /// <summary>
    /// <paramref name="text"/> with <paramref name="key"/> in <paramref name="section"/> set to <paramref name="value"/>
    /// (the entry's serialized form). The first uncommented <c>key =</c> line of the section is replaced; a missing key is
    /// added after the section's last line; a missing section is appended. The file's line ending is kept.
    /// </summary>
    public static string Set(string? text, string section, string key, string value)
    {
        text ??= "";
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
        bool trailingNewline = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);
        string newLine = key + " = " + value;
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), "[" + section + "]", StringComparison.OrdinalIgnoreCase)) { start = i; break; }
        }

        if (start < 0)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add("");
            lines.Add(newLine);
            return string.Join(nl, lines) + nl;
        }

        int end = lines.Count;
        for (int i = start + 1; i < lines.Count; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith("[", StringComparison.Ordinal)) { end = i; break; }
        }

        int lastContent = start;
        for (int i = start + 1; i < end; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0) continue;
            lastContent = i;
            if (t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith(";", StringComparison.Ordinal)) continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = newLine;
            return string.Join(nl, lines) + (trailingNewline ? nl : "");
        }

        lines.Insert(lastContent + 1, newLine);
        return string.Join(nl, lines) + (trailingNewline ? nl : "");
    }
}

#if !PROGENITOR_TESTS
/// <summary>The server's options as its <c>progenitor.cfg</c> entries (bound by their own modules).</summary>
internal static class ServerOptionsLive
{
    internal static string? Get(string op)
    {
        var entry = Entry(op);
        if (entry == null) return null;
        try
        {
            object? value = entry.BoxedValue;
            return value is bool b ? (b ? "true" : "false") : Progenitor.Ui.SettingsEditors.Format(value);
        }
        catch { return null; }
    }

    internal static bool Set(string op, string value)
    {
        var spec = ServerOptions.Find(op);
        var entry = Entry(op);
        if (spec == null || entry == null) return false;
        object parsed;
        if (entry.SettingType == typeof(int))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return false;
            parsed = n;
        }
        else if (entry.SettingType == typeof(bool)) parsed = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        else if (entry.SettingType == typeof(string)) parsed = value ?? "";
        else return false;

        if (!StoreOneLine(entry, parsed, out string error))
        {
            Plugin.Logger?.LogWarning("JoinAdmin: storing " + op + " threw: " + error);
            if (!Equals(entry.BoxedValue, parsed)) return false;
        }

        // [RemoteInstall] is read into one plain settings object: read it again so the change applies now.
        if (spec.Section == RemoteInstallSettings.Section) RemoteInstallEvents.RebindSettings();
        return Equals(entry.BoxedValue, parsed);
    }

    /// <summary>
    /// Set <paramref name="entry"/> in memory with BepInEx's save-on-set held off, then write only its own line to the cfg
    /// on disk, atomically (<see cref="CfgLineEdit"/>): other keys' hand edits made while the server runs survive (review
    /// 2026-10-09; before, every admin edit saved the whole file from memory). False with why when the disk write failed
    /// (the value in memory may still have been set).
    /// </summary>
    internal static bool StoreOneLine(ConfigEntryBase entry, object parsed, out string error)
    {
        error = "";
        var file = entry.ConfigFile;
        bool saveOn = file.SaveOnConfigSet;
        try
        {
            file.SaveOnConfigSet = false;
            entry.BoxedValue = parsed;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            file.SaveOnConfigSet = saveOn;
        }

        try
        {
            string path = file.ConfigFilePath;
            string text = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
            string next = CfgLineEdit.Set(text, entry.Definition.Section, entry.Definition.Key, entry.GetSerializedValue());
            if (!string.Equals(next, text, StringComparison.Ordinal)) JoinAdminFiles.WriteAtomic(path, next);
            return true;
        }
        catch (Exception e)
        {
            error = "the cfg line could not be written: " + e.Message;
            return false;
        }
    }

    private static ConfigEntryBase? Entry(string op)
    {
        var spec = ServerOptions.Find(op);
        if (spec == null) return null;
        var file = SharedConfig.TryGet();
        if (file == null) return null;
        try
        {
            var entries = ConfigHelper.GetEntries(file);
            if (entries == null) return null;
            var definition = new ConfigDefinition(spec.Section, spec.Key);
            return entries.Contains(definition) ? entries[definition] as ConfigEntryBase : null;
        }
        catch { return null; }
    }
}
#endif
