using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Progenitor.Install;
using Progenitor.Plugins;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>
/// JOINADMIN1 (join flow stage 3), server: the admin's "Players joining" choices — the recommended client mods,
/// the client mod policy and the install window — behind one admin request channel
/// (<c>'PrJa'</c>, <see cref="CustomEventPolicy.JoinAdminEventId"/>).
///
/// <para><b>Authorization</b> is the server-wide settings rule (<see cref="ServerSettings"/>): a completed
/// Progenitor handshake, and the sender on the server's admin list or <c>User.IsAdmin</c>, read off a fresh
/// <c>User</c> on the server; nothing in a request names or vouches for its sender. Every edit, and every
/// refused request, is one audit line in <c>BepInEx/progenitor-settings.log</c>; edits are rate limited per
/// SteamID like setting edits, reads separately.</para>
///
/// <para><b>Storage.</b> The recommended list and the client mod policy are <c>BepInEx/config/progenitor-join-admin.json</c>
/// (<see cref="JoinAdminConfig"/>), written atomically. The window is <c>Networking.InstallGraceSeconds</c>.
/// A change re-signs the catalog at once, and the catalog scan resends it to every handshake-ok client, so
/// players see it on their next join.</para>
///
/// <para><b>Shares</b> (the server's own copies offered to refused players) were removed 2026-10-05: servers no longer
/// send mod files. A stored <c>shared</c> list still loads and is ignored.</para>
/// </summary>
internal static class JoinAdminServer
{
    internal const string FileName = "progenitor-join-admin.json";
    internal const int RateMaxEdits = ServerSettings.RateMaxEdits;
    internal const int RateMaxReads = 30;
    internal const double RateWindowSeconds = ServerSettings.RateWindowSeconds;

    private static readonly object Gate = new();
    private static JoinAdminConfig? _config;
    private static string _loadNote = "";
    private static RemoteInstallRateLimiter _edits = new(RateMaxEdits, RateWindowSeconds);
    private static RemoteInstallRateLimiter _reads = new(RateMaxReads, RateWindowSeconds);
    private static readonly Dictionary<ulong, double> RateAudited = new();

    /// <summary>The current window (live: <c>Networking.InstallGraceSeconds</c>); tests set it.</summary>
    internal static Func<int>? WindowGet;

    /// <summary>Write the window; true when it was stored. Live: the handshake's config entry.</summary>
    internal static Func<int, bool>? WindowSet;

    /// <summary>
    /// CLIENTPOLICY3: raised (with the operation) after a stored policy change. Live: <c>Handshake</c> re-judges every
    /// connected player against the new policy. Tests read it.
    /// </summary>
    internal static Action<string>? PolicyChanged;

    /// <summary>REQRULE5: raised after the Disconnect them grace was stored. Live: <c>Handshake</c> sends connected players the rule.</summary>
    internal static Action? GraceChanged;

    /// <summary>Where a decided request's audit line goes (live: the BepInEx log and <c>progenitor-settings.log</c>).</summary>
    internal static Action<string>? Audit;

    /// <summary>
    /// AUTOUPDATE3: run one of the Updates group's actions (<see cref="JoinAdminOps.UpdateCheck"/>, <see cref="JoinAdminOps.UpdateRestart"/>,
    /// <see cref="JoinAdminOps.UpdateCancelRestart"/>) for an authorized admin: (ok, refusal, note). Live: <c>ServerAutoUpdateLive</c>.
    /// </summary>
    internal static Func<string, RemoteInstallActor, (bool Ok, string Error, string Note)>? UpdateAction;

    /// <summary>AUTOUPDATE3: the server's update status for the state, or null (no auto-updater on this server).</summary>
    internal static Func<JoinAdminUpdates?>? UpdatesState;

    internal static Func<DateTime> AuditClock = () => DateTime.UtcNow;

    internal static Func<double> Now = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static string FilePath => Path.Combine(ModManifest.ConfigDirectory, FileName);

    /// <summary>The stored choices, read from the file on first use.</summary>
    internal static JoinAdminConfig Config
    {
        get { lock (Gate) return EnsureLoaded(); }
    }

    /// <summary>The note from the last load (a skipped row, a set-aside unreadable file), for the readback.</summary>
    internal static string LoadNote
    {
        get { lock (Gate) return _loadNote; }
    }

    /// <summary>The last read of the store failed (locked, I/O): nothing is cached and edits are refused until a read works.</summary>
    private static bool _readFailed;
    private static string _lastReadWarning = "";

    /// <summary>True while the store cannot be read (readback).</summary>
    internal static bool ReadFailed
    {
        get { lock (Gate) { EnsureLoaded(); return _readFailed; } }
    }

    internal const string UnreadableStoreError = "The server could not read its saved choices (progenitor-join-admin.json); try again in a moment.";

    private static JoinAdminConfig EnsureLoaded()
    {
        if (_config != null) return _config;
        _config = new JoinAdminConfig();
        _loadNote = "";
        _readFailed = false;
        string path = FilePath;
        try
        {
            if (!File.Exists(path)) return _config;
            if (new FileInfo(path).Length > JoinAdminLimits.MaxConfigFileBytes)
            {
                SetAside(path, "it is larger than " + JoinAdminLimits.MaxConfigFileBytes + " bytes");
                return _config;
            }

            var skipped = new List<string>();
            if (!JoinAdminConfig.TryParse(File.ReadAllText(path), out var parsed, out var error, skipped))
            {
                SetAside(path, error ?? "unreadable");
                return _config;
            }

            _config = parsed;
            if (skipped.Count > 0)
            {
                _loadNote = string.Join("; ", skipped);
                Plugin.Logger?.LogWarning("JoinAdmin: " + FileName + " rows skipped: " + _loadNote);
            }
        }
        catch (Exception e)
        {
            // A file that cannot be read (locked) is neither set aside nor cached as empty: the next access reads it
            // again, and an edit is refused meanwhile, so a save can never replace the real file with an empty list.
            _loadNote = "could not be read: " + e.Message;
            _readFailed = true;
            if (!string.Equals(_lastReadWarning, e.Message, StringComparison.Ordinal))
            {
                _lastReadWarning = e.Message;
                Plugin.Logger?.LogWarning("JoinAdmin: " + FileName + " could not be read: " + e.Message);
            }
            var empty = _config;
            _config = null;
            return empty;
        }

        return _config;
    }

    private static void SetAside(string path, string why)
    {
        string bad = path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try { File.Move(path, bad); }
        catch { bad = "(could not be renamed)"; }
        _loadNote = "set aside (" + why + "): " + bad;
        Plugin.Logger?.LogWarning("JoinAdmin: " + FileName + " " + _loadNote + "; starting with no recommended mods and no client mod policy");
    }

    /// <summary>
    /// The recommended rows the server signs (<see cref="JoinAdminPolicy.Merge"/>): the admin's rows, then the
    /// operator overlay's rows for other mods.
    /// </summary>
    internal static IReadOnlyList<RecommendedMod> MergedRecommended(IReadOnlyList<RecommendedMod>? overlay)
    {
        var admin = Config.Recommended;
        if (admin.Count == 0) return overlay ?? Array.Empty<RecommendedMod>();
        return JoinAdminPolicy.Merge(admin, overlay);
    }

    /// <summary>
    /// The join section's <c>names</c>: the required mods' names (<paramref name="required"/>, which win), plus the
    /// readable names stored with the admin's recommended rows, within the section's caps. A player who lacks a
    /// recommended mod then sees its name instead of its id.
    /// </summary>
    internal static Dictionary<string, string> WithRecommendedNames(Dictionary<string, string> required)
    {
        var names = new Dictionary<string, string>(required ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var config = Config;
        foreach (var r in config.Recommended)
        {
            if (names.Count >= JoinSectionLimits.MaxNames) break;
            if (r.Id.Length == 0 || r.Id.Length > JoinSectionLimits.MaxIdChars) continue;
            string key = r.Id.ToLowerInvariant();
            string name = config.NameOf(r.Id);
            if (name.Length == 0 || name.Length > JoinSectionLimits.MaxNameChars || names.ContainsKey(key)) continue;
            names[key] = name;
        }

        return names;
    }

    /// <summary>The window as the handshake reads it now.</summary>
    internal static int WindowSeconds()
    {
        try { return WindowGet?.Invoke() ?? Handshake.DefaultInstallGraceSeconds; }
        catch { return Handshake.DefaultInstallGraceSeconds; }
    }

    // ---- requests -------------------------------------------------------------------------

    /// <summary>
    /// Decide one request (the production path and the tests). Order: rate limit (reads and edits each spend
    /// their own budget), handshake, authorization, then <see cref="JoinAdminPolicy.Decide"/>. A refused request
    /// carries no state, so a non-admin learns nothing. Edits and refusals are audited; a successful read is not.
    /// </summary>
    internal static JoinAdminResponse Apply(JoinAdminRequest request, bool handshakeOk, in RemoteInstallActor actor)
    {
        request ??= new JoinAdminRequest();
        string op = (request.Op ?? "").Trim().ToLowerInvariant();
        var response = new JoinAdminResponse { Nonce = request.Nonce, Op = op };
        bool edit = JoinAdminOps.IsEdit(op);
        double now = Now();
        string row = JoinAdminPolicy.AuditRowFor(op, request.Id.Length > 0 ? request.Id : request.Package);
        if (!(edit ? _edits : _reads).TryTake(actor.SteamId, now))
        {
            response.Error = JoinAdminPolicy.RateLimitedError;
            if (!RateAudited.TryGetValue(actor.SteamId, out double last) || now - last >= RateWindowSeconds || now < last)
            {
                if (RateAudited.Count >= 1024) RateAudited.Clear();
                RateAudited[actor.SteamId] = now;
                WriteAudit(false, actor, row, "", RequestText(request), response.Error);
            }

            return response;
        }

        if (!handshakeOk)
        {
            response.Error = JoinAdminPolicy.HandshakeError;
            WriteAudit(false, actor, row, "", RequestText(request), response.Error);
            return response;
        }

        if (!actor.Authorized)
        {
            response.Error = JoinAdminPolicy.NotAdminError;
            WriteAudit(false, actor, row, "", RequestText(request), response.Error);
            return response;
        }

        // The operator's lock list ([ServerSettings] LockedInGame): a locked setting is refused whatever a client sends.
        if (edit && ServerSettingsLock.JoinAdminSettingId(op) is { } settingId && ServerSettingsLock.LockedBy(settingId) is { } lockedBy)
        {
            response.Error = ServerSettingsLock.NoteFor(lockedBy);
            WriteAudit(false, actor, row, ServerOptionOps.IsOption(op) ? ServerOptions.Current(op) ?? "" : "", RequestText(request), response.Error);
            response.State = BuildState(request.ApprovedFrom);
            return response;
        }

        // AUTOUPDATE3: the Updates group's actions change no stored choice; the auto-updater decides them.
        if (JoinAdminOps.IsUpdateAction(op))
        {
            (bool Ok, string Error, string Note) r;
            try { r = UpdateAction?.Invoke(op, actor) ?? (false, "This server does not run Progenitor's auto-updater.", ""); }
            catch (Exception e) { r = (false, "The server could not do this (" + e.Message + ").", ""); }
            response.Ok = r.Ok;
            response.Error = r.Ok ? "" : r.Error;
            response.Note = r.Note;
            WriteAudit(r.Ok, actor, row, "", op, response.Error);
            response.State = BuildState(request.ApprovedFrom);
            return response;
        }

        JoinAdminDecision decision;
        lock (Gate)
        {
            var current = EnsureLoaded();
            if (_readFailed && edit)
            {
                response.Error = UnreadableStoreError;
                WriteAudit(false, actor, row, "", RequestText(request), response.Error);
                return response;
            }

            var loaded = SafeLoaded();
            IReadOnlyList<RecommendedMod> overlay;
            try { overlay = ModManifest.ReadOverrides().Recommended; }
            catch { overlay = Array.Empty<RecommendedMod>(); }
            decision = JoinAdminPolicy.Decide(current, request, RequiredIds(loaded), overlay, WindowSeconds(),
                JoinAdminOps.IsMatching(op) ? ServerModIds(loaded) : null, VersionMatchCfg.ForcedNow());
            if (decision.Ok && decision.Changed)
            {
                try
                {
                    JoinAdminFiles.WriteAtomic(FilePath, decision.Config.ToJson());
                    _config = decision.Config;
                    _lastPolicy = decision.Config.Policy;
                }
                catch (Exception e)
                {
                    decision = JoinAdminDecision.Refused(current, decision.AuditRow, "The server could not save this change (" + e.Message + ").",
                        decision.Before, decision.After);
                }
            }
        }

        if (decision.Ok && decision.WindowSeconds is int seconds)
        {
            bool stored;
            try { stored = WindowSet?.Invoke(seconds) ?? false; }
            catch { stored = false; }
            if (!stored)
            {
                decision.Ok = false;
                decision.Error = "The server could not store the install window.";
            }
        }

        // Settings organization stage 5: a server option (one progenitor.cfg entry).
        if (decision.Ok && decision.OptionOp is { } optionOp && decision.OptionValue is { } optionValue)
        {
            bool stored;
            try { stored = ServerOptions.Set?.Invoke(optionOp, optionValue) ?? false; }
            catch { stored = false; }
            if (!stored)
            {
                decision.Ok = false;
                decision.Error = ServerOptions.StoreFailedError;
            }
            else if (optionOp == ServerOptionOps.DisconnectGrace)
            {
                // REQRULE5: connected players learn the new grace with the rule (REQRULE2).
                try { GraceChanged?.Invoke(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: telling players the new grace threw: " + e.Message); }
            }
        }

        response.Ok = decision.Ok;
        response.Error = decision.Ok ? "" : decision.Error;
        response.Note = decision.Note;
        if (decision.Ok && decision.Changed)
        {
            // Re-sign now; the next frame's scan resends it to every handshake-ok client (live 2026-10-04 this direct
            // re-sign hid the change from the scan and nothing was resent).
            try { ModManifest.RebuildCatalog("join-admin " + (request.Op ?? "").Trim().ToLowerInvariant()); }
            catch { /* the next scan signs it */ }
        }

        // Mod compatibility C3: version matching applies from each player's next join (the catalog was re-signed above and the
        // judge reads the new settings on the next answer); nobody in the world is re-judged or disconnected.
        if (decision.Ok && decision.MatchingChanged)
            Plugin.Logger?.LogInfo("VersionMatching: now " + CurrentMatching().Describe() + " after " + op + "; applies from the next join");

        // CLIENTPOLICY1: whether a client that never answers is refused can change with the policy.
        if (decision.Ok && decision.PolicyChanged)
        {
            try { Handshake.ReevaluatePendingDeadlines(); }
            catch { /* the next arm reads it */ }

            // CLIENTPOLICY3: re-judge the players in the world against the new policy (and ask the ones never asked for
            // their mods). After the re-sign above, so the catalog the scan resends carries the same policy.
            try { PolicyChanged?.Invoke(op); }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: the mid-session re-check after " + op + " threw: " + e.Message); }
        }

        if (edit || !decision.Ok) WriteAudit(decision.Ok, actor, decision.AuditRow, decision.Before, decision.Ok ? decision.After : RequestText(request), response.Error);
        response.State = BuildState(request.ApprovedFrom);
        return response;
    }

    private static string RequestText(JoinAdminRequest r) => (r.Op ?? "").Trim().ToLowerInvariant() switch
    {
        JoinAdminOps.Window => r.Seconds.ToString(CultureInfo.InvariantCulture),
        JoinAdminOps.RecommendAdd or JoinAdminOps.RecommendReason or JoinAdminOps.RequiredAdd or JoinAdminOps.RequiredReason => r.Reason ?? "",
        JoinAdminOps.RequiredRule or JoinAdminOps.ApproveAdd => (r.Rule ?? "") + " " + (r.Version ?? ""),
        JoinAdminOps.PolicyMode or JoinAdminOps.Unverified or JoinAdminOps.MidSession or JoinAdminOps.VersionMatching => r.Value ?? "",
        JoinAdminOps.ServerModRule => (r.Rule ?? "").Length > 0 ? r.Rule ?? "" : VersionMatchText.WordDefault,
        JoinAdminOps.ApproveBulk => r.Items.Count.ToString(CultureInfo.InvariantCulture) + " mod(s)",
        var o when ServerOptionOps.IsSeconds(o) => r.HasSeconds ? r.Seconds.ToString(CultureInfo.InvariantCulture) : "",
        // CHAT4: a secret (the webhook address) is audited as "set" or empty, never its value.
        var o when ServerOptionOps.IsOption(o) => ServerOptions.Shown(o, r.Value),
        _ => r.Version ?? "",
    };

    // ---- CLIENTPOLICY1: the policy the handshake judges ---------------------------------------

    private static ClientPolicyConfig? _lastPolicy;

    /// <summary>
    /// The client mod policy the server enforces now: the stored one, or — while the store cannot be read — the last
    /// one it could read, so a locked file never switches the policy off.
    /// </summary>
    internal static ClientPolicyConfig CurrentPolicy
    {
        get
        {
            lock (Gate)
            {
                var config = EnsureLoaded();
                if (_readFailed) return _lastPolicy ?? config.Policy;
                _lastPolicy = config.Policy;
                return config.Policy;
            }
        }
    }

    /// <summary>The facts rule 2 reads: every mod this server runs (with the handshake rule its client copy must meet) and the recommended keys.</summary>
    internal static ClientModServerFacts PolicyFacts()
    {
        var facts = new ClientModServerFacts();
        try
        {
            foreach (var r in Handshake.CopyRequired())
            {
                if (string.IsNullOrEmpty(r.Guid) || Handshake.Compare.IsReservedGuid(r.Guid)) continue;
                facts.Runs[r.Guid] = (r.Version, r.Declared ? r.VersionRule : NetworkVersionRule.Exact);
            }
        }
        catch { /* evidence */ }

        foreach (var m in SafeLoaded())
            if (!string.IsNullOrEmpty(m.Id) && !facts.Runs.ContainsKey(m.Id)) facts.Runs[m.Id] = (m.Version, null);
        IReadOnlyList<RecommendedMod> overlay;
        try { overlay = ModManifest.ReadOverrides().Recommended; }
        catch { overlay = Array.Empty<RecommendedMod>(); }
        foreach (var r in MergedRecommended(overlay))
        {
            if (r.Id.Length > 0) facts.Recommended.Add(r.Id);
            if (r.Package.Length > 0) facts.Recommended.Add(r.Package);
        }

        return facts;
    }

    /// <summary>CLIENTPOLICY2: the GUIDs rule 2 counts as "a mod this server runs" (signed into the join section under Only approved).</summary>
    internal static IEnumerable<string> PolicyRunIds()
    {
        try
        {
            if (CurrentPolicy.Mode != ClientPolicyMode.Approved) return Array.Empty<string>();
            return new List<string>(PolicyFacts().Runs.Keys);
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>A readable name for an alert line's mod: the server's plugin name, else the admins' client row name.</summary>
    internal static string? AlertName(string id, Dictionary<string, string> names)
    {
        if (names.TryGetValue(id.ToLowerInvariant(), out var n)) return n;
        var policy = CurrentPolicy;
        int at = policy.IndexOfRequired(id);
        if (at >= 0 && policy.Required[at].Name.Length > 0) return policy.Required[at].Name;
        at = policy.IndexOfApproved(id);
        return at >= 0 && policy.Approved[at].Name.Length > 0 ? policy.Approved[at].Name : null;
    }

    private static void WriteAudit(bool accepted, in RemoteInstallActor actor, string row, string before, string after, string error)
    {
        var sink = Audit;
        if (sink == null) return;
        try
        {
            sink(SettingsAuditLog.Line(AuditClock(), accepted, actor.CharacterName, actor.SteamId,
                ServerAdminList.Word(actor.AuthorizedBy), row, before, after, error));
        }
        catch { /* the audit never decides a request */ }
    }

    // ---- what the server runs --------------------------------------------------------------

    private static IReadOnlyList<LoadedModInfo> SafeLoaded()
    {
        try { return ModManifest.EnumerateLoaded(); }
        catch { return Array.Empty<LoadedModInfo>(); }
    }

    /// <summary>Every id the server requires now (its loaded <c>ServerMustHave</c> mods).</summary>
    internal static List<string> RequiredIds(IReadOnlyList<LoadedModInfo> loaded)
    {
        var ids = new List<string>();
        foreach (var m in loaded)
            if (m.Level == NetworkCompatibilityLevel.ServerMustHave && !string.IsNullOrEmpty(m.Id)) ids.Add(m.Id);
        return ids;
    }

    // ---- Mod compatibility C3: version matching ------------------------------------------------------

    /// <summary>
    /// The mods a per-mod rule may be set for: the handshake's required rows (the mods the join compares) and the loaded
    /// plugins, without Progenitor's own and the handshake's reserved rows. Id and version, in that order.
    /// </summary>
    internal static List<(string Id, string Version)> ServerModList(IReadOnlyList<LoadedModInfo> loaded)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id, string? version)
        {
            string g = (id ?? "").Trim();
            if (g.Length == 0 || Handshake.Compare.IsReservedGuid(g) || ClientModJudge.AlwaysApproved(g) || !seen.Add(g)) return;
            list.Add((g, (version ?? "").Trim()));
        }

        try
        {
            foreach (var r in Handshake.CopyRequired()) Add(r.Guid, r.Version);
        }
        catch { /* the loaded list below */ }

        if (loaded != null)
            foreach (var m in loaded) Add(m.Id, m.Version);
        return list;
    }

    /// <summary>The ids of <see cref="ServerModList"/>.</summary>
    internal static List<string> ServerModIds(IReadOnlyList<LoadedModInfo> loaded)
    {
        var ids = new List<string>();
        foreach (var m in ServerModList(loaded)) ids.Add(m.Id);
        return ids;
    }

    /// <summary>
    /// The panel's rule rows: each mod the server runs (at most <see cref="VersionMatchLimits.MaxServerModRows"/>, by name) with
    /// the admins' rule for it, then each stored rule whose mod the server does not run now (so it can be cleared).
    /// </summary>
    internal static List<JoinAdminServerModRow> ServerModRows(IReadOnlyList<LoadedModInfo> loaded, Dictionary<string, string>? names, JoinAdminConfig config)
    {
        var rows = new List<JoinAdminServerModRow>();
        foreach (var (id, version) in ServerModList(loaded))
        {
            int at = config.IndexOfServerModRule(id);
            string name = names != null && names.TryGetValue(id.ToLowerInvariant(), out var n) ? n : at >= 0 ? config.ServerModRules[at].Name : "";
            rows.Add(new JoinAdminServerModRow
            {
                Id = id, Name = name, Version = version, Rule = at >= 0 ? ClientModRules.Word(config.ServerModRules[at].Rule) : "",
            });
        }

        rows.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        if (rows.Count > VersionMatchLimits.MaxServerModRows)
        {
            // A mod with a rule is never left out.
            var keep = rows.FindAll(r => r.Rule.Length > 0);
            foreach (var r in rows)
                if (r.Rule.Length == 0 && keep.Count < VersionMatchLimits.MaxServerModRows) keep.Add(r);
            keep.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
            rows = keep;
        }

        foreach (var r in config.ServerModRules)
        {
            if (rows.Exists(x => string.Equals(x.Id, r.Id, StringComparison.OrdinalIgnoreCase))) continue;
            rows.Add(new JoinAdminServerModRow { Id = r.Id, Name = r.Name, Rule = ClientModRules.Word(r.Rule), Runs = false });
        }

        return rows;
    }

    private static JoinAdminConfig? _lastMatchConfig;
    private static JoinAdminConfig? _matchConfig;
    private static string? _matchCfgText;
    private static VersionMatchSettings _match = VersionMatchSettings.Default;

    /// <summary>
    /// C3: what the join judge reads (<see cref="VersionMatching.SettingsSource"/>): the effective mode (the cfg override wins)
    /// and the per-mod rules. Cached per stored config and cfg text, so a handshake answer costs a lookup; rebuilt when an
    /// edit stores a new config or the cfg value changes. While the store cannot be read, the last one read stays in force.
    /// </summary>
    internal static VersionMatchSettings CurrentMatching()
    {
        string cfg = VersionMatchCfg.Text();
        lock (Gate)
        {
            var config = EnsureLoaded();
            if (_readFailed) config = _lastMatchConfig ?? config;
            else _lastMatchConfig = config;
            if (!ReferenceEquals(config, _matchConfig) || !string.Equals(cfg, _matchCfgText, StringComparison.Ordinal))
            {
                _matchConfig = config;
                _matchCfgText = cfg;
                _match = VersionMatchSetting.Build(config, VersionMatchCfg.Forced(cfg));
            }

            return _match;
        }
    }

    /// <summary>C3: the judge reads <see cref="CurrentMatching"/> (server; the tests' Install wires the same source).</summary>
    internal static void InstallMatchingSource() => VersionMatching.SettingsSource = CurrentMatching;

    /// <summary>C3: forget the source (only when it is still ours) and the cache.</summary>
    private static void UninstallMatchingSource()
    {
        Func<VersionMatchSettings> mine = CurrentMatching;
        if (VersionMatching.SettingsSource != null && VersionMatching.SettingsSource.Method == mine.Method) VersionMatching.SettingsSource = null;
        lock (Gate)
        {
            _lastMatchConfig = null;
            _matchConfig = null;
            _matchCfgText = null;
            _match = VersionMatchSettings.Default;
        }
    }

    /// <summary>Everything the panel draws, as this server sees it now.</summary>
    internal static JoinAdminState BuildState(int approvedFrom = 0)
    {
        var state = new JoinAdminState { WindowSeconds = WindowSeconds() };
        var loaded = SafeLoaded();
        Dictionary<string, string> names;
        try { names = ModManifest.RequiredModNames(loaded); }
        catch { names = new Dictionary<string, string>(); }
        JoinAdminConfig config;
        lock (Gate) config = EnsureLoaded();

        IReadOnlyList<RecommendedMod> overlay;
        try { overlay = ModManifest.ReadOverrides().Recommended; }
        catch { overlay = Array.Empty<RecommendedMod>(); }
        var stored = new HashSet<RecommendedMod>(config.Recommended);
        foreach (var m in JoinAdminPolicy.Merge(config.Recommended, overlay))
        {
            bool fromFile = !stored.Contains(m);
            // An overlay reason may be 200 characters; the panel shows it read-only, cut to the panel's own limit, so
            // the answer always fits one packet.
            string reason = m.Reason;
            if (fromFile && reason.Length > JoinAdminLimits.MaxReasonChars)
            {
                int cut = JoinAdminLimits.MaxReasonChars - 1;
                if (char.IsHighSurrogate(reason[cut - 1])) cut--;
                reason = reason.Substring(0, cut) + "…";
            }

            state.Recommended.Add(new JoinAdminRecRow
            {
                Id = m.Id, Package = m.Package, Version = m.Version, Reason = reason, FromFile = fromFile,
                Name = fromFile ? "" : config.NameOf(m.Id),
            });
        }

        state.Required.AddRange(RequiredIds(loaded));
        try
        {
            var (joins, rows) = RefusedJoins.Log.Aggregate(RefusedJoins.Clock(), JoinAdminLimits.MaxAlertRows, id => AlertName(id, names));
            state.RefusedJoins = joins;
            // CLIENTPOLICY3: a not-approved line whose mod is approved now reads so and offers no Approve.
            foreach (var a in rows) MarkApproved(a, config.Policy);
            state.Alert.AddRange(rows);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: refused-join summary threw: " + e.Message); }

        // CLIENTPOLICY1: the client mod policy, the approved rows from approvedFrom on (the wire trims what does not fit).
        var policy = config.Policy;
        state.PolicySupported = true;
        state.PolicyMode = ClientModRules.ModeWord(policy.Mode);
        state.Unverified = ClientModRules.UnverifiedWord(policy.Unverified);
        state.MidSession = ClientModRules.MidSessionWord(policy.MidSession);
        foreach (var r in policy.Required) state.RequiredClient.Add(JoinAdminClientRow.From(r));
        state.ApprovedTotal = policy.Approved.Count;
        int from = approvedFrom < 0 ? 0 : approvedFrom >= policy.Approved.Count ? Math.Max(0, policy.Approved.Count - 1) : approvedFrom;
        if (policy.Approved.Count == 0) from = 0;
        state.ApprovedFrom = from;
        for (int i = from; i < policy.Approved.Count; i++) state.Approved.Add(JoinAdminClientRow.From(policy.Approved[i]));

        // Mod compatibility C3: Version matching (the cfg override wins) and the mods the server runs with their rules.
        var forced = VersionMatchCfg.ForcedNow();
        state.MatchingSupported = true;
        state.VersionMatching = VersionMatchingText.Word(VersionMatchSetting.Effective(config, forced));
        state.VersionMatchingForced = forced != null;
        state.ServerMods.AddRange(ServerModRows(loaded, names, config));
        // Mod compatibility C4: what Exact version for all would mean for Progenitor here (shown while Exact is chosen).
        state.ProgenitorExactLine = ProgenitorExactSource.LineIfExact();

        // Settings organization stage 5: the server options Progenitor's page edits (a bound entry only).
        state.OptionsSupported = true;
        foreach (string op in ServerOptionOps.All)
        {
            string? value = ServerOptions.Current(op);
            if (value != null) state.Options[op] = value;
        }

        // The lock list in force (the deprecated AllowedSteamIdsInGame switch included), for the page's read-only rows.
        state.Lock.AddRange(ServerSettingsLock.Current().Entries);

        // AUTOUPDATE3: the Updates group's status.
        try { state.Updates = UpdatesState?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: the update status threw: " + e.Message); }

        return state;
    }

    /// <summary>
    /// CLIENTPOLICY3: a <c>not-approved</c> alert line whose mod the policy now allows (an approved row by id or package,
    /// or the always-allowed set) is marked <see cref="JoinAdminAlertRow.Approved"/> and its text rewritten.
    /// </summary>
    internal static void MarkApproved(JoinAdminAlertRow a, ClientPolicyConfig policy)
    {
        if (a == null || a.Kind != RefusedJoinLog.KindNotApproved || policy == null) return;
        a.Approved = policy.IndexOfApproved(a.Mod) >= 0 || policy.IndexOfRequired(a.Mod) >= 0 || ClientModJudge.AlwaysApproved(a.Mod);
        a.Text = RefusedJoinLog.AlertText(a);
    }

    /// <summary>Server frame: the refused-join flush.</summary>
    internal static void Tick()
    {
        RefusedJoins.Tick(Now());
    }

    // ---- lifetime --------------------------------------------------------------------------

    /// <summary>Forget everything (Core going down, tests).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _config = null;
            _lastPolicy = null;
            _loadNote = "";
            _lastMatchConfig = null;
            _matchConfig = null;
            _matchCfgText = null;
            _match = VersionMatchSettings.Default;
        }

        _edits = new RemoteInstallRateLimiter(RateMaxEdits, RateWindowSeconds);
        _reads = new RemoteInstallRateLimiter(RateMaxReads, RateWindowSeconds);
        RateAudited.Clear();
    }

#if PROGENITOR_TESTS
    internal static void Install() => InstallMatchingSource();

    internal static void Uninstall()
    {
        UninstallMatchingSource();
        VersionMatchCfg.Reset();
        Reset();
        RefusedJoins.Uninstall();
        WindowGet = null;
        WindowSet = null;
        Audit = null;
        PolicyChanged = null;
        GraceChanged = null;
        UpdateAction = null;
        UpdatesState = null;
        ServerOptions.Reset();
        ServerSettingsLock.Reset();
        AuditClock = () => DateTime.UtcNow;
        Now = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }
#else
    private static bool _installed;
    private static IDisposable? _disconnected;

    /// <summary>Server: read the store, register the channel and the frame tick.</summary>
    internal static unsafe void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        WindowGet = Handshake.InstallGraceSecondsSetting;
        WindowSet = Handshake.TrySetInstallGraceSeconds;
        string root = "";
        try { root = BepInEx.Paths.BepInExRootPath ?? ""; } catch { /* the log line stays */ }
        Audit = line =>
        {
            Plugin.Logger?.LogInfo("JoinAdmin audit: " + line);
            SettingsAuditLog.Append(root, line);
        };
        PolicyChanged = Handshake.OnPolicyChangedMidSession;
        GraceChanged = () => Handshake.BroadcastPolicyRule("disconnect-grace");
        // Mod compatibility C3: the join judge reads the admins' Version matching and per-mod rules (the cfg override wins).
        VersionMatchCfg.Warn = line => Plugin.Logger?.LogWarning("VersionMatching: " + line);
        try { Progenitor.Config.SharedConfig.BindWhenAvailable("[" + VersionMatchCfg.Section + "] " + VersionMatchCfg.Key, BindMatchingCfg); }
        catch (Exception e) { Plugin.Logger?.LogWarning("VersionMatching: [ClientPolicy] VersionMatching could not be bound (the admins' choice applies): " + e.Message); }
        InstallMatchingSource();
        // Mod compatibility C4: Exact version for all holds players to this server's own Progenitor build while it is
        // published on Thunderstore (ProgenitorExact.StatusOf); the judge, the catalog and the Server tab read this.
        ProgenitorExactSource.Log = line => Plugin.Logger?.LogInfo(line);
        ProgenitorExactSource.Install();
        ServerOptions.Get = ServerOptionsLive.Get;
        ServerOptions.Set = ServerOptionsLive.Set;
        // The lock list is bound by ServerSettingsLock.Install (review fix 5: independent of this channel, fail closed).
        try { CustomEvents.OnReceiveFromClient(CustomEventPolicy.JoinAdminEventId, OnRequest); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: the request channel could not be registered: " + e.Message); }
        try { _disconnected = Events.EventBus.On<Events.PlayerDisconnected>(ev => RefusedJoins.Drop(ev.User.Index), priority: 898); }
        catch { /* held notes are replaced on the next answer */ }
        try { Plugin.Frame += Tick; }
        catch { /* refused joins are then flushed at unload */ }
        _ = Config;
    }

    internal static unsafe void Uninstall()
    {
        if (_installed)
        {
            try { Plugin.Frame -= Tick; } catch { /* teardown */ }
            try { CustomEvents.RemoveClientHandler(CustomEventPolicy.JoinAdminEventId); } catch { /* teardown */ }
            _disconnected?.Dispose();
            _disconnected = null;
        }

        _installed = false;
        UninstallMatchingSource();
        ProgenitorExactSource.Uninstall();
        ProgenitorExactSource.Log = null;
        VersionMatchCfg.Reset();
        Reset();
        RefusedJoins.Uninstall();
        Audit = null;
        PolicyChanged = null;
        GraceChanged = null;
        UpdateAction = null;
        UpdatesState = null;
        ServerOptions.Reset();
    }

    /// <summary>
    /// C3: bind <c>[ClientPolicy] VersionMatching</c> (server-wide; read on use). A change of it (an edit or a cfg reload)
    /// re-signs the catalog, as an admin's change does; nobody in the world is re-judged.
    /// </summary>
    private static void BindMatchingCfg(BepInEx.Configuration.ConfigFile file)
    {
        try
        {
            var entry = Progenitor.Config.ConfigHelper.Bind(file, VersionMatchCfg.Section, VersionMatchCfg.Key, "", VersionMatchCfg.Description);
            VersionMatchCfg.Source = () => entry?.Value ?? "";
            if (VersionMatchCfg.ForcedNow() is { } forced)
                Plugin.Logger?.LogInfo("VersionMatching: forced by the cfg: " + VersionMatchingText.Word(forced));
            Progenitor.Config.ConfigHelper.AddSettingChanged(file, (_, e) =>
            {
                if (!ReferenceEquals(e.ChangedSetting, entry) || !Plugin.IsServerProcess) return;
                try
                {
                    Dev.MainThread.Post(() =>
                    {
                        try { ModManifest.RebuildCatalog("version matching cfg"); }
                        catch { /* the next scan signs it */ }
                    });
                }
                catch { /* tearing down */ }
            });
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("VersionMatching: binding [ClientPolicy] VersionMatching threw (the admins' choice applies): " + e.Message); }
    }

    /// <summary>
    /// After an ok handshake: tell a client that declared <see cref="Handshake.Capability.JoinAdmin"/> that this
    /// server has the channel, and whether it is an admin here (only the button's visibility; every request is
    /// authorized again). Never sent to a client that did not declare it, so no older client gets an id it has no
    /// handler for.
    /// </summary>
    internal static void OnHandshakeOk(World? world, Entity userEntity, Handshake.Capability clientCaps)
    {
        if (!_installed || world == null || (clientCaps & Handshake.Capability.JoinAdmin) == 0) return;
        try
        {
            if (!world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var user = world.EntityManager.GetComponentData<User>(userEntity);
            if (!user.IsConnected) return;
            bool admin = ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) != RemoteAuthorizedBy.None;
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap != null)
                CustomEvents.SendToUser(bootstrap, user.Index, CustomEventPolicy.JoinAdminEventId, new ReadOnlySpan<byte>(JoinAdminWire.EncodeHello(admin, rules: true)));
            // AUTOUPDATE3: an admin who joins while updates wait is told once.
            if (admin && bootstrap != null && ServerAutoUpdate.NoticeForJoiningAdmin(user.PlatformId) is { } notice)
                CustomEvents.SendToUser(bootstrap, user.Index, CustomEventPolicy.JoinAdminEventId,
                    new ReadOnlySpan<byte>(JoinAdminWire.EncodeNotice(notice.Kind, notice.Text, notice.OfferRestart)));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: hello threw: " + e.Message); }
    }

    /// <summary>
    /// AUTOUPDATE3 (main thread): send an update notice to every admin online (<see cref="ServerAdminList.AuthorizeShared"/>):
    /// the channel's notice to a client whose handshake declared the channel (the Progenitor notice with View and Restart now),
    /// a System chat line to any other admin. Returns how many admins got it.
    /// </summary>
    internal static int SendUpdateNotice(AdminUpdateNotice notice)
    {
        var world = Worlds.Server;
        if (notice == null || world == null || !world.IsCreated) return 0;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        int sent = 0;
        try
        {
            foreach (var p in Progenitor.Players.PlayerDirectory.Online(world))
            {
                try
                {
                    var entity = new Entity { Index = p.Session.UserIndex, Version = p.Session.UserVersion };
                    if (!world.EntityManager.Exists(entity) || !world.EntityManager.HasComponent<User>(entity)) continue;
                    var user = world.EntityManager.GetComponentData<User>(entity);
                    if (!user.IsConnected || ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) == RemoteAuthorizedBy.None) continue;
                    bool channel = (Handshake.CapsOf(user.Index) & Handshake.Capability.JoinAdmin) != 0 && Handshake.UserAnswerStatus(user.Index) == "ok";
                    if (channel && bootstrap != null)
                        CustomEvents.SendToUser(bootstrap, user.Index, CustomEventPolicy.JoinAdminEventId,
                            new ReadOnlySpan<byte>(JoinAdminWire.EncodeNotice(notice.Kind, notice.Text, notice.OfferRestart)));
                    else if (!Progenitor.Chat.Chat.SendTo(world, entity, "Progenitor: " + notice.Text + ". Mods > Server > Updates.")) continue;
                    ServerAutoUpdate.NoteSent(user.PlatformId, notice);
                    sent++;
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: an update notice threw: " + e.Message); }
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: the update notice threw: " + e.Message); }

        return sent;
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var span = new ReadOnlySpan<byte>(payload, length);
        if (!JoinAdminWire.TryDecodeRequest(span, out var request)) return;
        try
        {
            // Re-read the User the event resolved; no privilege or identity travels in the request.
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            var actor = new RemoteInstallActor(current.CharacterName.ToString(), current.PlatformId, current.IsAdmin,
                ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin));
            var response = Apply(request, Handshake.UserAnswerStatus(current.Index) == "ok", actor);
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap != null)
                CustomEvents.SendToUser(bootstrap, current.Index, CustomEventPolicy.JoinAdminEventId, new ReadOnlySpan<byte>(JoinAdminWire.EncodeResponse(response)));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin request: " + e.Message); }
    }
#endif
}
