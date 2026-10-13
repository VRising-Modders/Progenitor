using System;
using System.Collections.Generic;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// Settings organization (requested 2026-10-05, "lock any server setting"): the operator's list of server settings
/// that cannot be changed in the game, one cfg-only key in the server's <c>progenitor.cfg</c>:
/// <c>[ServerSettings] LockedInGame</c>. Its value is a list (commas, semicolons or spaces) of
/// <list type="bullet">
/// <item>setting ids (<see cref="SettingPlaces"/>: <c>progenitor/ServerMessage/Message</c>,
/// <c>progenitor/JoinAdmin/allowed-steam-ids</c>, <c>progenitor/ServerRules/ClanSize</c>, or a mod's
/// server-wide row <c>&lt;pluginId&gt;/&lt;Section&gt;/&lt;Key&gt;</c>);</item>
/// <item>group ids for whole subheadings of Progenitor's Server settings (<see cref="Groups"/>);</item>
/// <item><c>*</c> for every server setting, mods' server-wide rows included.</item>
/// </list>
/// Empty (the default) leaves everything editable. The key is never drawn or editable in the game and is read on every
/// use, so a cfg reload applies it. Every edit path on the server refuses a locked setting with <see cref="Note"/> and an
/// audit line (<c>'PrJa'</c> operations, administrator ConfigSync edits <c>'PrSe'</c>, and the server rules' <c>'PrSv'</c>);
/// the page draws a locked row read-only. Game-free.
/// </summary>
internal static class ServerSettingsLock
{
    internal const string Section = "ServerSettings";
    internal const string Key = "LockedInGame";

    /// <summary>The note on a locked row, and the refusal of an edit of one.</summary>
    internal const string Note = "Locked in the server's config file.";

    internal const string All = "*";

    // Group ids: whole subheadings of Progenitor's Server settings.
    internal const string GroupServerRules = "server-rules";
    internal const string GroupOperator = "operator";
    internal const string GroupGameplay = "gameplay";
    internal const string GroupServerList = "server-list";
    internal const string GroupJoin = "join";
    internal const string GroupModsPlayersNeed = "mods-players-need";
    internal const string GroupNoProgenitor = "no-progenitor";

    /// <summary>CHAT4: the Server tab's Chat group.</summary>
    internal const string GroupChat = "chat";

    /// <summary>AUTOUPDATE3: the Server tab's Updates group (its settings and Restart now).</summary>
    internal const string GroupUpdates = "updates";

    internal static readonly IReadOnlyList<string> Groups = new[]
    {
        GroupServerRules, GroupOperator, GroupGameplay, GroupServerList, GroupJoin, GroupModsPlayersNeed, GroupNoProgenitor, GroupChat, GroupUpdates,
    };

    /// <summary>The setting-id prefix of the vanilla server rules (stages 6 and 7).</summary>
    internal const string ServerRulesPrefix = SettingPlaces.PluginId + "/ServerRules/";

    /// <summary>The deprecated stage 5 switch the lock replaces: <c>[RemoteInstall] AllowedSteamIdsInGame = false</c>.</summary>
    internal const string LegacyEntry = "[RemoteInstall] AllowedSteamIdsInGame";

    internal const string Description =
        "Server. Settings that cannot be changed in the game (Progenitor's page, the Players joining popup, any admin edit): a list of "
        + "setting ids (as mods_ui settings_places lists them, e.g. progenitor/JoinAdmin/allowed-steam-ids), group ids "
        + "(server-rules, operator, gameplay, server-list, join, mods-players-need, no-progenitor, chat, updates) or * for every server setting."
        + "Separated by commas. Empty: everything is editable by admins. The page shows a locked row read-only and the server refuses "
        + "an edit of it. Applies at once.";

    /// <summary>The lock list's text as the server reads it now (live: the cfg entry); null when not bound.</summary>
    internal static Func<string?>? Source { get; set; }

    /// <summary>True while the deprecated <c>AllowedSteamIdsInGame</c> is false (live: the bound entry).</summary>
    internal static Func<bool>? LegacyAllowedIdsLocked { get; set; }

    /// <summary>Where a warning goes once per unknown entry (live: the BepInEx log).</summary>
    internal static Action<string>? Warn { get; set; }

    /// <summary>Whether a setting id names a setting that can be edited here (live: the server's registries); unknown ids are warned once.</summary>
    internal static Func<string, bool>? KnownId { get; set; }

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);
    private static string? _parsedText;
    private static LockSet _parsed = LockSet.Empty;
    private static bool _legacyLogged;

    /// <summary>One parsed lock list: the entries in their order, each a setting id, a group id or <c>*</c>.</summary>
    internal sealed class LockSet
    {
        internal static readonly LockSet Empty = new(Array.Empty<string>());

        internal LockSet(IReadOnlyList<string> entries) => Entries = entries ?? Array.Empty<string>();

        internal IReadOnlyList<string> Entries { get; }

        internal bool IsEmpty => Entries.Count == 0;

        /// <summary>The entry that locks <paramref name="settingId"/> (its own id first, then a group, then <c>*</c>), or null.</summary>
        internal string? Match(string? settingId)
        {
            if (string.IsNullOrEmpty(settingId) || Entries.Count == 0) return null;
            foreach (string e in Entries)
                if (string.Equals(e, settingId, StringComparison.OrdinalIgnoreCase)) return e;
            var groups = GroupsOf(settingId);
            foreach (string e in Entries)
                if (groups.Contains(e)) return e;
            foreach (string e in Entries)
                if (e == All) return e;
            return null;
        }

        internal string Fingerprint => string.Join(",", Entries);
    }

    /// <summary>
    /// Parse a lock list. Entries are trimmed and kept once each, in order; a group id or <c>*</c> is kept as written in
    /// lower case. <paramref name="warn"/> gets each entry that is neither <c>*</c>, a group, nor a well-formed setting
    /// id (<c>a/b/c</c>), and each well-formed id <see cref="KnownId"/> does not know; well-formed ids are kept either way
    /// (a mod may register its row later).
    /// </summary>
    internal static LockSet Parse(string? text, Action<string>? warn = null)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // A setting id written before the 2026-10 id rename reads as its current id (LegacyIds).
            string token = Progenitor.Plugins.LegacyIds.NormalizeRowId(raw.Trim());
            if (token.Length == 0) continue;
            string lower = token.ToLowerInvariant();
            if (lower == All || IsGroup(lower))
            {
                if (seen.Add(lower)) entries.Add(lower);
                continue;
            }

            if (!WellFormedId(token))
            {
                warn?.Invoke("[" + Section + "] " + Key + ": '" + token + "' is not *, a group (" + string.Join(", ", Groups)
                    + ") or a setting id (plugin/Section/Key); ignored.");
                continue;
            }

            if (!IsKnown(token))
                warn?.Invoke("[" + Section + "] " + Key + ": no server setting '" + token + "' is known yet; it is kept and applies if one appears.");
            if (seen.Add(token)) entries.Add(token);
        }

        return entries.Count == 0 ? LockSet.Empty : new LockSet(entries);
    }

    internal static bool IsGroup(string? word)
    {
        foreach (string g in Groups)
            if (string.Equals(g, word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool WellFormedId(string token)
    {
        string[] parts = token.Split('/');
        if (parts.Length != 3) return false;
        foreach (string p in parts)
            if (p.Trim().Length == 0) return false;
        return true;
    }

    private static bool IsKnown(string id)
    {
        if (SettingPlaces.Find(id) is { } place && !string.Equals(place.Home, SettingPlaces.CfgOnly, StringComparison.Ordinal)) return true;
        if (id.StartsWith(ServerRulesPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        try { return KnownId?.Invoke(id) ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// The groups <paramref name="settingId"/> belongs to: the subheadings of its home and mirrors on Progenitor's Server
    /// settings (<see cref="SettingPlaces"/>), and <see cref="GroupServerRules"/> for a server rule. None for a mod's row.
    /// </summary>
    internal static HashSet<string> GroupsOf(string? settingId)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(settingId)) return groups;
        if (settingId!.StartsWith(ServerRulesPrefix, StringComparison.OrdinalIgnoreCase)) groups.Add(GroupServerRules);
        if (SettingPlaces.Find(settingId) is not { } place) return groups;
        foreach (string where in place.Places)
        {
            string? g = where switch
            {
                SettingPlaces.WhenPlayersJoin => GroupJoin,
                SettingPlaces.ModsPlayersNeed or SettingPlaces.PlayersJoiningPanel => GroupModsPlayersNeed,
                SettingPlaces.ServerList => GroupServerList,
                SettingPlaces.PlayersWithoutProgenitor => GroupNoProgenitor,
                // Three homes: Progenitor's two server toggles moved to its page (On this server); the group word stays.
                SettingPlaces.OnThisServer => GroupGameplay,
                SettingPlaces.Operator => GroupOperator,
                SettingPlaces.ChatServer => GroupChat,
                SettingPlaces.UpdatesServer => GroupUpdates,
                _ => null,
            };
            if (g != null) groups.Add(g);
        }

        // Three homes moved "If mods can't be checked" under Mods players need; the lock list keeps it in
        // no-progenitor too, so an operator's list from before the move still locks it (#178's rule).
        if (string.Equals(settingId, SettingPlaces.JoinAdminId(SettingPlaces.OpUnverified), StringComparison.OrdinalIgnoreCase))
            groups.Add(GroupNoProgenitor);

        return groups;
    }

    /// <summary>
    /// The lock list in force on this server now: <see cref="Source"/> parsed (cached per text, each unknown entry warned
    /// once), plus <c>progenitor/JoinAdmin/allowed-steam-ids</c> while the deprecated switch is false.
    /// </summary>
    internal static LockSet Current()
    {
        string? text;
        try { text = Source?.Invoke(); }
        catch { text = null; }
        if (!string.Equals(text, _parsedText, StringComparison.Ordinal))
        {
            _parsedText = text;
            _parsed = Parse(text, line =>
            {
                if (Warned.Add(line)) Warn?.Invoke(line);
            });
        }

        bool legacy;
        try { legacy = LegacyAllowedIdsLocked?.Invoke() ?? false; }
        catch { legacy = false; }
        if (!legacy) return _parsed;
        if (!_legacyLogged)
        {
            _legacyLogged = true;
            Warn?.Invoke(LegacyEntry + " = false is deprecated: it locks " + LegacyId + ". Use [" + Section + "] " + Key + " = " + LegacyId + " instead.");
        }

        if (_parsed.Match(LegacyId) != null) return _parsed;
        var entries = new List<string>(_parsed.Entries) { LegacyId };
        return new LockSet(entries);
    }

    /// <summary>The setting id the deprecated switch locks.</summary>
    internal static string LegacyId => SettingPlaces.JoinAdminId(ServerOptionOps.AllowedSteamIds);

    /// <summary>The entry that locks <paramref name="settingId"/> on this server, or null (the deprecated switch reads as its own name).</summary>
    internal static string? LockedBy(string? settingId)
    {
        // Review fix 5: a server whose lock list is not bound yet refuses every edit (fail closed), not none.
        if (RequireBound && Source == null) return NotBoundEntry;
        var set = Current();
        string? by = set.Match(settingId);
        if (by != null && string.Equals(by, LegacyId, StringComparison.OrdinalIgnoreCase) && _parsed.Match(settingId) == null) return LegacyEntry;
        return by;
    }

    /// <summary>
    /// Review fix 5: set on a server at Core install (<see cref="Install"/>). While the lock list's entry is not bound,
    /// <see cref="LockedBy"/> answers <see cref="NotBoundEntry"/> for every setting, so no edit gets through before the
    /// operator's list is read.
    /// </summary>
    internal static bool RequireBound { get; set; }

    /// <summary>The <see cref="LockedBy"/> answer while the lock list is not bound yet.</summary>
    internal const string NotBoundEntry = "(the lock list is not loaded yet)";

    internal const string NotBoundError = "The server has not loaded its settings lock list yet; try again in a moment.";

    /// <summary>
    /// The refusal and the row's note for a setting the lock list locks, naming the entry that matched:
    /// <c>Locked in the server's config file (ServerSettings LockedInGame: operator).</c>
    /// </summary>
    internal static string NoteFor(string? entry)
    {
        if (string.IsNullOrEmpty(entry)) return Note;
        if (entry == NotBoundEntry) return NotBoundError;
        if (entry == LegacyEntry) return "Locked in the server's config file (" + LegacyEntry + " = false).";
        return "Locked in the server's config file (" + Section + " " + Key + ": " + entry + ").";
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Review fix 5: a server binds the lock list on its own, as soon as the shared cfg is available, independent of the
    /// admin channel. Until then every edit is refused (<see cref="RequireBound"/>).
    /// </summary>
    internal static void Install()
    {
        if (!Plugin.IsServerProcess || _installed) return;
        _installed = true;
        RequireBound = true;
        Warn = line => Plugin.Logger?.LogWarning("ServerSettings: " + line);
        KnownId = ServerSettings.IsEditable;
        LegacyAllowedIdsLocked = Progenitor.Install.RemoteInstallEvents.AllowedSteamIdsLocked;
        try { Progenitor.Config.SharedConfig.BindWhenAvailable("[" + Section + "] " + Key, Bind); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerSettings: the lock list could not be bound (every edit stays refused): " + e.Message); }
    }

    private static void Bind(BepInEx.Configuration.ConfigFile file)
    {
        try
        {
            var entry = Progenitor.Config.ConfigHelper.Bind(file, Section, Key, "", Description);
            Source = () => entry?.Value ?? "";
            if (!string.IsNullOrWhiteSpace(entry?.Value))
                Plugin.Logger?.LogInfo("ServerSettings: locked in the game: " + string.Join(", ", Current().Entries));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ServerSettings: binding the lock list threw (every edit stays refused): " + e.Message); }
    }

    internal static void Uninstall() => Reset();
#endif

    private static bool _installed;

    // ---- the admin channel's operations --------------------------------------------------------------------

    /// <summary>
    /// The setting an admin-channel operation changes: the panel's list operations share one id per list
    /// (<c>recommended</c>, <c>required-client</c>, <c>approved</c>), and a per-mod rule is Version matching's
    /// (<c>version-matching</c>); every other operation is its own. Null for <c>get</c>.
    /// </summary>
    internal static string? JoinAdminSettingId(string? op)
    {
        string o = (op ?? "").Trim().ToLowerInvariant();
        if (o.Length == 0 || o == JoinAdminOps.Get) return null;
        string key = o switch
        {
            JoinAdminOps.RecommendAdd or JoinAdminOps.RecommendRemove or JoinAdminOps.RecommendReason => SettingPlaces.OpRecommended,
            JoinAdminOps.RequiredAdd or JoinAdminOps.RequiredRemove or JoinAdminOps.RequiredReason or JoinAdminOps.RequiredRule => SettingPlaces.OpRequiredClient,
            JoinAdminOps.ApproveAdd or JoinAdminOps.ApproveRemove or JoinAdminOps.ApproveBulk => SettingPlaces.OpApproved,
            // Mod compatibility C3: the per-mod rules lock with Version matching (one setting id).
            JoinAdminOps.ServerModRule => SettingPlaces.OpVersionMatching,
            _ => o,
        };
        return SettingPlaces.JoinAdminId(key);
    }

    // ---- the client's copy -------------------------------------------------------------------------------

    /// <summary>
    /// A client: the joined server's lock list as its admin channel last said (live: <c>JoinAdminRows.LockedBy</c>), so the
    /// page draws locked rows read-only. Null (a server, tests that set none): nothing is drawn locked.
    /// </summary>
    internal static Func<string, string?>? ClientProbe { get; set; }

    /// <summary>A text that changes whenever the client's lock list does (the page rebuilds on a change).</summary>
    internal static Func<string>? ClientFingerprint { get; set; }

    internal static string? ClientLockedBy(string? settingId)
    {
        if (string.IsNullOrEmpty(settingId)) return null;
        try { return ClientProbe?.Invoke(settingId!); }
        catch { return null; }
    }

    internal static string ClientKey()
    {
        try { return ClientFingerprint?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>Tests and teardown: forget the seams, the cache and the warnings.</summary>
    internal static void Reset()
    {
        Source = null;
        LegacyAllowedIdsLocked = null;
        Warn = null;
        KnownId = null;
        ClientProbe = null;
        ClientFingerprint = null;
        Warned.Clear();
        _parsedText = null;
        _parsed = LockSet.Empty;
        _legacyLogged = false;
        RequireBound = false;
        _installed = false;
    }
}
