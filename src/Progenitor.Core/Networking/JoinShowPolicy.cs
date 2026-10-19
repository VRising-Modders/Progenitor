using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW1: what this client remembers about one server's join screens, in
/// <c>BepInEx/config/progenitor-join-seen.json</c>: the message it last showed (a hash of the text as the
/// admins wrote it), the reset period it last showed it in, a dismissal, the mod lists it last showed, and
/// the world it last showed the in-world screens in.
/// </summary>
internal sealed class JoinSeenRow
{
    /// <summary>The server: <c>&lt;operator fingerprint&gt;@&lt;address&gt;</c>, or the address alone.</summary>
    public string Key { get; set; } = "";

    /// <summary>Hash of the message last shown (<see cref="JoinShowPolicy.MessageHash"/>), or empty.</summary>
    public string MessageHash { get; set; } = "";

    /// <summary>"Don't show again until it changes": the message hash it was ticked for, or empty.</summary>
    public string Dismissed { get; set; } = "";

    /// <summary>When the message was last shown (ISO UTC), or empty; a reset that passed after it re-shows the message.</summary>
    public string ResetSeen { get; set; } = "";

    /// <summary>The mod lists last shown on the mods page (<see cref="JoinShowPolicy.ModsSnapshot"/>).</summary>
    public Dictionary<string, string> Mods { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Hash of <see cref="Mods"/>, or empty when the mods page was never shown.</summary>
    public string ModsHash { get; set; } = "";

    /// <summary>The process and client world the in-world screens last opened in (a Core swap keeps both).</summary>
    public string ShownIn { get; set; } = "";

    /// <summary>When this row was last written (ISO UTC); the store drops the oldest rows first.</summary>
    public string Seen { get; set; } = "";
}

/// <summary>
/// JOINFLOW1: the "seen" store. One row per server, at most <see cref="MaxServers"/> (the least recently
/// written dropped first). A file that cannot be read is an empty store: the screens show, nothing worse.
/// Written with a temp file and a move.
/// </summary>
internal static class JoinSeenStore
{
    public const string FileName = "progenitor-join-seen.json";
    public const int MaxServers = 200;

    /// <summary>Entries kept per row's mods snapshot on read (far above any real server's lists).</summary>
    public const int MaxModKeys = 4096;

    /// <summary>The server key: <c>&lt;fingerprint&gt;@&lt;address&gt;</c>, the fingerprint alone, or the address alone.</summary>
    public static string ServerKey(string? fingerprint, string? host)
    {
        string fp = (fingerprint ?? "").Trim().ToLowerInvariant();
        string h = (host ?? "").Trim().ToLowerInvariant();
        if (fp.Length > 0 && h.Length > 0) return fp + "@" + h;
        return fp.Length > 0 ? fp : h;
    }

    public static JoinSeenRow? Get(string path, string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var row in Read(path))
            if (string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase)) return row;
        return null;
    }

    /// <summary>Change this server's row (creating it) and write the store. Never throws; false when it could not be written.</summary>
    public static bool Update(string path, string key, Action<JoinSeenRow> change, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(key) || change == null) return false;
        try
        {
            var rows = Read(path, out bool unreadable);
            if (unreadable)
            {
                // A locked or unreadable file (antivirus, another process) is not an empty store: writing
                // one row back would drop every other server's state. Skip this write.
                Plugin.Logger?.LogWarning("JoinFlow: " + FileName + " could not be read; not updated this time");
                return false;
            }

            var row = rows.Find(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            if (row != null) rows.Remove(row);
            row ??= new JoinSeenRow { Key = key };
            change(row);
            row.Seen = ModManifestJson.FormatIssued(nowUtc);
            rows.Add(row);
            while (rows.Count > MaxServers) rows.RemoveAt(0);
            Write(path, rows);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: could not write " + FileName + ": " + e.Message);
            return false;
        }
    }

    /// <summary>Forget everything about this server (its screens show as on a first join). True when a row was removed.</summary>
    public static bool Forget(string path, string key)
    {
        try
        {
            var rows = Read(path, out bool unreadable);
            if (unreadable) return false;
            int removed = rows.RemoveAll(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Write(path, rows);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Forget every server whose key is <paramref name="host"/> or ends <c>@</c><paramref name="host"/> (any operator at
    /// that address), or every server when <paramref name="host"/> is empty. The number of rows removed; -1 when the
    /// file could not be read or written.
    /// </summary>
    public static int ForgetHost(string path, string? host)
    {
        string h = (host ?? "").Trim().ToLowerInvariant();
        try
        {
            var rows = Read(path, out bool unreadable);
            if (unreadable) return -1;
            int removed = rows.RemoveAll(r => h.Length == 0
                || string.Equals(r.Key, h, StringComparison.OrdinalIgnoreCase)
                || (r.Key ?? "").EndsWith("@" + h, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return 0;
            Write(path, rows);
            return removed;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Rows, oldest written first.</summary>
    internal static List<JoinSeenRow> Read(string path) => Read(path, out _);

    /// <summary>
    /// Rows; <paramref name="unreadable"/> when the file exists but could not be read (an IO error). A file
    /// that reads but does not parse is an empty store (it is rewritten on the next update).
    /// </summary>
    internal static List<JoinSeenRow> Read(string path, out bool unreadable)
    {
        unreadable = false;
        var rows = new List<JoinSeenRow>();
        string text;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rows;
            text = File.ReadAllText(path);
        }
        catch
        {
            unreadable = true;
            return rows;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, ModManifestJson.ParseOptions);
            if (!doc.RootElement.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array) return rows;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var row = new JoinSeenRow
                {
                    Key = Str(el, "key"),
                    MessageHash = Str(el, "message"),
                    Dismissed = Str(el, "dismissed"),
                    ResetSeen = Str(el, "reset"),
                    ModsHash = Str(el, "modsHash"),
                    ShownIn = Str(el, "shownIn"),
                    Seen = Str(el, "seen"),
                };
                if (el.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object)
                    foreach (var p in mods.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && row.Mods.Count < MaxModKeys) row.Mods[p.Name] = p.Value.GetString() ?? "";
                if (row.Key.Length > 0) rows.Add(row);
            }
        }
        catch
        {
            // An unreadable file forgets what was seen: the screens show again, nothing worse.
            rows.Clear();
        }

        return rows;
    }

    private static void Write(string path, List<JoinSeenRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("{\"servers\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Pair(sb, "dismissed", r.Dismissed, first: true);
            Pair(sb, "key", r.Key);
            Pair(sb, "message", r.MessageHash);
            sb.Append(",\"mods\":{");
            var keys = new List<string>(r.Mods.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int k = 0; k < keys.Count; k++)
            {
                if (k > 0) sb.Append(',');
                ModManifestJson.WriteString(sb, keys[k]);
                sb.Append(':');
                ModManifestJson.WriteString(sb, r.Mods[keys[k]]);
            }

            sb.Append('}');
            Pair(sb, "modsHash", r.ModsHash);
            Pair(sb, "reset", r.ResetSeen);
            Pair(sb, "seen", r.Seen);
            Pair(sb, "shownIn", r.ShownIn);
            sb.Append('}');
        }

        sb.Append("]}");
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static void Pair(StringBuilder sb, string key, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        ModManifestJson.WriteString(sb, key);
        sb.Append(':');
        ModManifestJson.WriteString(sb, value ?? "");
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

/// <summary>Whether a page shows on this join, and why (the <c>join_ui</c> readback's <c>reason</c>).</summary>
internal readonly struct JoinShowDecision
{
    public JoinShowDecision(bool show, string reason)
    {
        Show = show;
        Reason = reason ?? "";
    }

    public bool Show { get; }

    /// <summary><c>always</c>, <c>first-join</c>, <c>message-changed</c>, <c>reset-passed</c>, <c>mods-changed</c>, <c>needs-attention</c>, <c>skipped-unchanged</c>, <c>dismissed</c> or <c>no-message</c>.</summary>
    public string Reason { get; }

    public override string ToString() => (Show ? "show:" : "skip:") + Reason;
}

/// <summary>
/// JOINFLOW1: when each join page shows (the server's <see cref="MessageFrequency"/> and
/// <see cref="ModsPageFrequency"/>, against what this client last saw), game-free.
///
/// <list type="bullet">
/// <item><b>Message, Always:</b> every join; no "don't show again".</item>
/// <item><b>Message, When it changes or after a reset</b> (default): the first join, again when the text
/// changes (a hash of the text as written, so a ticking countdown is no change), and the first join after
/// each reset time passes (the period starts at <see cref="ResetCountdown.LastPassed"/>). No reset set:
/// only on changes. A player's "don't show again" skips the reset re-shows of that same text.</item>
/// <item><b>Message, First join only:</b> once, then only when the text changes.</item>
/// <item><b>Mods page, When needed</b> (default): when a required mod needs attention, else only when the
/// required list (ids and versions), the recommended list (ids, versions, reasons) or the client mod policy
/// changed since the page was last shown. A first join shows it only when the server
/// recommends mods.</item>
/// <item><b>Mods page, Always:</b> every join.</item>
/// </list>
/// </summary>
internal static class JoinShowPolicy
{
    public const string ReasonAlways = "always";
    public const string ReasonFirstJoin = "first-join";
    public const string ReasonMessageChanged = "message-changed";
    public const string ReasonResetPassed = "reset-passed";
    public const string ReasonModsChanged = "mods-changed";
    public const string ReasonNeedsAttention = "needs-attention";
    public const string ReasonUnchanged = "skipped-unchanged";
    public const string ReasonDismissed = "dismissed";
    public const string ReasonNoMessage = "no-message";

    /// <summary>A refused join shows the server's message whatever its frequency.</summary>
    public const string ReasonRefused = "refused";

    /// <summary>A first visit with every required mod ready and nothing recommended: the mods page is skipped.</summary>
    public const string ReasonSkippedReady = "skipped-ready";

    /// <summary>
    /// Asked 2026-10-06 ("when exactly should that be happening?"): the server's lists changed since the last visit, but
    /// every required mod is ready, no recommended mod is new or changed and still to consider, and no mod is not
    /// allowed. Nothing to do or decide, so the page is skipped and the new lists are recorded as seen.
    /// </summary>
    public const string ReasonSkippedReadyChanged = "skipped-ready-changed";

    /// <summary>
    /// Reported 2026-10-06 ("Thought we weren't showing this if nothing was needed"): whether the Joining / Rejoining /
    /// Resuming page stays open once its join answered. Only when a required mod still needs attention (or a not-allowed
    /// mod is still loaded); otherwise it closes by itself and the in-world screens decide as for any join. A null model
    /// (a resumed private game) has nothing to decide.
    /// </summary>
    public static bool JoinedPageNeeded(JoinPageModel? model) => model != null && !model.AllRequiredReady;

    /// <summary>The reasons whose skip records the server's lists as seen (no page, but the player has them now).</summary>
    public static bool RecordsListsWhenSkipped(string? reason) =>
        reason is ReasonSkippedReady or ReasonSkippedInstalled or ReasonSkippedReadyChanged;

    /// <summary>
    /// The player just installed what this join needed in its install window: the in-world mods page is not
    /// shown for that (it is no change on the server) and the lists are recorded as seen.
    /// </summary>
    public const string ReasonSkippedInstalled = "skipped-installed";

    /// <summary>The message was already shown in this join (its install window): not shown again in the world.</summary>
    public const string ReasonShownThisJoin = "shown-this-join";

    /// <summary>The row tag for a mod installed in this join's install window.</summary>
    public const string ChangeInstalled = "installed";

    /// <summary>
    /// The page decision after this join's install window showed its screens. A first visit is skipped
    /// (<see cref="ReasonSkippedInstalled"/>): the player just saw the server's lists in the window. A
    /// "changed" page is skipped only when every changed row is a mod this join installed (the server
    /// sharing the copy the player just took); any other change still shows, as do needs-attention and
    /// Always.
    /// </summary>
    public static JoinShowDecision AfterInstallWindow(
        JoinShowDecision mods, IReadOnlyDictionary<string, string>? changes, ICollection<string>? installed)
    {
        if (!mods.Show) return mods;
        if (mods.Reason == ReasonFirstJoin) return new JoinShowDecision(false, ReasonSkippedInstalled);
        if (mods.Reason != ReasonModsChanged || installed == null || installed.Count == 0) return mods;
        if (changes == null || changes.Count == 0) return mods;
        foreach (var key in changes.Keys)
        {
            bool mine = false;
            foreach (string id in installed)
                if (string.Equals(id, key, StringComparison.OrdinalIgnoreCase)) { mine = true; break; }
            if (!mine) return mods;
        }

        return new JoinShowDecision(false, ReasonSkippedInstalled);
    }

    /// <summary>Row changes with the mods installed in this join tagged <see cref="ChangeInstalled"/> instead.</summary>
    public static IReadOnlyDictionary<string, string> MarkInstalled(IReadOnlyDictionary<string, string>? changes, ICollection<string>? installed)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (changes != null)
            foreach (var pair in changes) result[pair.Key] = pair.Value;
        if (installed != null)
            foreach (string id in installed)
                if (!string.IsNullOrEmpty(id)) result[id] = ChangeInstalled;
        return result;
    }

    /// <summary><c>Installed 1 mod. You're ready to play.</c></summary>
    public static string InstalledLine(int n) =>
        "Installed " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + (n == 1 ? " mod" : " mods") + ". You're ready to play.";

    /// <summary>16 hex characters of SHA-256 over the message as the admins wrote it (line endings normalized).</summary>
    public static string MessageHash(string? message) => Hash16((message ?? "").Replace("\r\n", "\n"));

    public static JoinShowDecision Message(JoinSection? section, JoinSeenRow? seen, DateTime nowUtc)
    {
        if (section == null || section.Message.Trim().Length == 0) return new(false, ReasonNoMessage);
        if (section.ShowMessage == MessageFrequency.Always) return new(true, ReasonAlways);
        string hash = MessageHash(section.Message);
        if (seen == null || seen.MessageHash.Length == 0) return new(true, ReasonFirstJoin);
        if (!string.Equals(seen.MessageHash, hash, StringComparison.Ordinal)) return new(true, ReasonMessageChanged);
        if (section.ShowMessage == MessageFrequency.FirstJoin) return new(false, ReasonUnchanged);
        if (string.Equals(seen.Dismissed, hash, StringComparison.Ordinal)) return new(false, ReasonDismissed);
        if (ResetPassedSince(section, seen, nowUtc)) return new(true, ReasonResetPassed);
        return new(false, ReasonUnchanged);
    }

    /// <summary>
    /// True when a reset time passed after the message was last shown (<see cref="JoinSeenRow.ResetSeen"/>,
    /// the show time; an older row without it falls back to the row's last write). Comparing with the show
    /// time, not the period start, means moving the reset anchor does not count as a reset that passed.
    /// </summary>
    public static bool ResetPassedSince(JoinSection section, JoinSeenRow seen, DateTime nowUtc)
    {
        TimeZoneInfo zone;
        try { zone = section.Zone.Resolve(); }
        catch { zone = TimeZoneInfo.Utc; }
        var last = ResetCountdown.LastPassed(section.Reset, nowUtc, zone);
        if (last == null) return false;
        if (!ResetCountdown.TryParseUtc(seen.ResetSeen, out var shownAt)
            && !ResetCountdown.TryParseUtc(seen.Seen, out shownAt)) return false;
        return last.Value > shownAt;
    }

    /// <summary>The reset period to remember when the message is shown now (ISO UTC), or empty.</summary>
    public static string CurrentPeriod(JoinSection? section, DateTime nowUtc)
    {
        if (section?.Reset == null) return "";
        TimeZoneInfo zone;
        try { zone = section.Zone.Resolve(); }
        catch { zone = TimeZoneInfo.Utc; }
        var last = ResetCountdown.LastPassed(section.Reset, nowUtc, zone);
        return last == null ? "" : ResetCountdown.FormatUtc(last.Value);
    }

    /// <summary>
    /// When the mods page opens (decided 2026-10-06: only when the player has something to do or decide). Needs attention
    /// (a missing or mismatched required mod, a pending install, a not-allowed mod still loaded) always opens it, as does
    /// Every join. Otherwise, under When needed: a first visit opens it for a recommended mod this client does not have or
    /// a not-allowed mod; a change opens it for a recommended mod that is new or changed and not installed, or a
    /// not-allowed mod. A change with nothing to do is <see cref="ReasonSkippedReadyChanged"/>.
    /// </summary>
    public static JoinShowDecision Mods(JoinPageModel model, JoinSeenRow? seen)
    {
        if (model == null) return new(false, ReasonUnchanged);
        if (!model.AllRequiredReady) return new(true, ReasonNeedsAttention);
        var frequency = model.Facts.Section?.ShowMods ?? ModsPageFrequency.WhenNeeded;
        if (frequency == ModsPageFrequency.Always) return new(true, ReasonAlways);
        if (seen == null || seen.ModsHash.Length == 0)
        {
            // A first visit with everything ready shows the page only for something to consider: a recommended mod
            // this client lacks, or a not-allowed mod (paused here). The lists are remembered, so a later change counts.
            return ToConsider(model, null) ? new(true, ReasonFirstJoin) : new(false, ReasonSkippedReady);
        }

        if (string.Equals(seen.ModsHash, ModsHash(ModsSnapshot(model)), StringComparison.Ordinal)) return new(false, ReasonUnchanged);
        var changes = seen.Mods != null && seen.Mods.Count > 0 ? RowChanges(model, seen.Mods) : null;
        return ToConsider(model, changes) ? new(true, ReasonModsChanged) : new(false, ReasonSkippedReadyChanged);
    }

    /// <summary>
    /// Something on the page to decide with every required mod ready: a not-allowed mod, or a recommended mod not
    /// installed — any such row when <paramref name="changes"/> is null (a first visit, or a stored row without its
    /// lists), else only one that is new or changed since the last visit.
    /// </summary>
    internal static bool ToConsider(JoinPageModel model, IReadOnlyDictionary<string, string>? changes)
    {
        if (model == null) return false;
        if (model.NotAllowed.Count > 0) return true;
        foreach (var r in model.Recommended)
        {
            if (r.Installed) continue;
            if (changes == null || changes.ContainsKey(JoinPageModel.KeyOf(r))) return true;
        }

        return false;
    }

    /// <summary>
    /// The mod lists as one canonical map: <c>r:&lt;id&gt;</c> → required version for each mod the server
    /// requires, <c>c:&lt;id or package&gt;</c> → version and a hash of the reason for each recommended mod,
    /// and the client mod policy (below). Ids are lowercase. Only
    /// the server's lists: what this client has is not part of it. (The <c>s:</c> entries for mods the server
    /// shared went with server distribution, 2026-10-05: a stored hash that had one no longer matches, so such a
    /// server shows its mods page once as changed after the update; a server that shared nothing hashes as before.)
    /// </summary>
    public static Dictionary<string, string> ModsSnapshot(JoinPageModel model)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (model == null) return map;
        foreach (var r in model.Required)
        {
            if (r.Origin != JoinRowOrigin.ServerRequires) continue;
            string id = r.Id.ToLowerInvariant();
            map["r:" + id] = r.RequiredVersion;
        }

        foreach (var c in model.Recommended)
            map["c:" + JoinPageModel.KeyOf(c).ToLowerInvariant()] = c.Mod.Version + "|" + Hash16(c.Mod.Reason).Substring(0, 8);
        AddPolicy(map, model.Facts?.Section);
        return map;
    }

    /// <summary>
    /// CLIENTPOLICY1: the signed client mod policy's part of the snapshot — <c>q:&lt;key&gt;</c> → version, rule and a hash
    /// of the reason for each required client mod, <c>p:mode</c> → <c>approved</c> under Only approved, and
    /// <c>a:&lt;key&gt;</c> → version and rule for each approved mod. Added only when set, so a server without a policy
    /// hashes exactly as before (no spurious "changed").
    /// </summary>
    internal static void AddPolicy(Dictionary<string, string> map, JoinSection? section)
    {
        if (map == null || section == null) return;
        foreach (var r in section.RequiredClient)
            map["q:" + r.Key.ToLowerInvariant()] = r.Version + "|" + ClientModRules.Word(r.Rule) + "|" + Hash16(r.Reason).Substring(0, 8);
        if (section.PolicyMode == ClientPolicyMode.Approved) map["p:mode"] = "approved";
        foreach (var r in section.Approved)
            map["a:" + r.Key.ToLowerInvariant()] = r.Version + "|" + ClientModRules.Word(r.Rule);
    }

    /// <summary>16 hex characters of SHA-256 over the snapshot, keys sorted.</summary>
    public static string ModsHash(IReadOnlyDictionary<string, string> snapshot)
    {
        var keys = new List<string>(snapshot.Keys);
        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string k in keys) sb.Append(k).Append('=').Append(snapshot[k]).Append('\n');
        return Hash16(sb.ToString());
    }

    /// <summary>
    /// Which rows changed since the snapshot <paramref name="before"/>: a required or recommended row that
    /// was not there is <c>new</c>; one whose version or reason differs is <c>changed</c>. Keys
    /// are the model's row ids (a recommended row's <see cref="JoinPageModel.KeyOf"/>). Empty when nothing was
    /// seen before (a first visit marks nothing).
    /// </summary>
    public static Dictionary<string, string> RowChanges(JoinPageModel model, IReadOnlyDictionary<string, string>? before)
    {
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model == null || before == null || before.Count == 0) return changes;
        var now = ModsSnapshot(model);
        foreach (var r in model.Required)
        {
            if (r.Origin != JoinRowOrigin.ServerRequires) continue;
            string id = r.Id.ToLowerInvariant();
            if (!before.TryGetValue("r:" + id, out var version)) { changes[r.Id] = "new"; continue; }
            if (!string.Equals(version, r.RequiredVersion, StringComparison.Ordinal))
                changes[r.Id] = "changed";
        }

        foreach (var c in model.Recommended)
        {
            string key = JoinPageModel.KeyOf(c);
            string k = "c:" + key.ToLowerInvariant();
            if (!before.TryGetValue(k, out var value)) changes[key] = "new";
            else if (!string.Equals(value, now[k], StringComparison.Ordinal)) changes[key] = "changed";
        }

        return changes;
    }

    private static string Hash16(string text)
    {
        using var sha = SHA256.Create();
        return ModManifestCrypto.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Substring(0, 16);
    }

    /// <summary>The <c>New</c> / <c>Changed</c> tag a row draws.</summary>
    public static string TagWord(string change) =>
        change == "new" ? "New" : change == "changed" ? "Changed" : change == ChangeInstalled ? "Installed" : "";

    /// <summary>The mods page's subtitle line when it shows because the lists changed.</summary>
    public const string ModsChangedLine = "The server's mod list changed since your last visit.";
}

/// <summary>
/// JOINFLOW1 live fix (2026-10-04): when a verified catalog may decide whether the in-world screens open.
/// Only the first catalog after a join attempt this Core generation observed decides; a resend later in
/// the session (an admin's setting change, a plugin load) and a Core loaded mid-session never do.
/// </summary>
internal static class JoinInWorldGate
{
    public static bool MayDecide(bool joinObserved, bool alreadyDecided, bool openPending) =>
        joinObserved && !alreadyDecided && !openPending;

    /// <summary>
    /// Live fix (2026-10-04, round 3): a catalog that arrives while the install window is still armed, with
    /// no screens open, after the window's screens were shown, and with every required mod ready, is the
    /// join completing (the server's catalog to a handshake-ok client). It is decided as the in-world
    /// catalog, never opened as a new install-window session.
    /// </summary>
    public static bool WindowJoinCompleted(bool windowArmed, bool screensOpen, bool windowScreensShown, Func<bool> allRequiredReady) =>
        windowArmed && !screensOpen && windowScreensShown && allRequiredReady();
}
