using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument, planning and record helpers for the debug-bridge <c>unlock</c> op
/// (<c>UnlockOps</c>): unlock build-menu blueprints and crafting recipes for one player on the
/// isolated test server, and undo it. Everything that decides what is written lives here so it
/// is covered without a V Rising install; the ECS reads and writes stay in <c>UnlockOps</c>.
///
/// <para>Planning follows the game's own unlock test, <c>ProgressionUtility.HasUnlockedProgression
/// (skipProgressionCheck: false)</c> (native, server): a GUID counts as unlocked
/// when it is in <c>UnlockedProgressionElement</c>, or in <c>UnlockedRecipeElement</c> /
/// <c>UnlockedBlueprintElement</c> with <c>UserHasRequiredContentFlags</c> true, or in
/// <c>UnlockedShapeshiftElement</c>.</para>
/// </summary>
internal static class UnlockArgs
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 1000;

    /// <summary>Most entries one <c>prefabs</c> list may name.</summary>
    public const int MaxPrefabs = 10_000;

    /// <summary>Record file name prefix under the server's <c>BepInEx/config</c>.</summary>
    public const string RecordPrefix = "progenitor-unlock-";

    public const int RecordVersion = 1;

    internal enum UnlockAction
    {
        /// <summary>Read only: buffer counts, prefab totals, locked lists, the record.</summary>
        Status,

        /// <summary>Every blueprint prefab and every recipe prefab.</summary>
        All,

        /// <summary>The GUIDs named in <c>prefabs</c>.</summary>
        Prefabs,

        /// <summary>Remove the GUIDs named in <c>prefabs</c> from <c>UnlockedProgressionElement</c>.</summary>
        Lock,

        /// <summary>Remove exactly the GUIDs the record says this op added.</summary>
        Revert,
    }

    /// <summary>Who: <c>user</c> as a character name (string) or a <c>User.Index</c> (JSON number).</summary>
    internal readonly struct UserRef
    {
        public readonly string? Name;
        public readonly int? Index;

        public UserRef(string? name, int? index)
        {
            Name = name;
            Index = index;
        }

        public override string ToString() => Name ?? ("#" + Index?.ToString(CultureInfo.InvariantCulture));
    }

    internal sealed class Request
    {
        public UnlockAction Action;
        public UserRef User;
        public List<PrefabInspectArgs.Key> Prefabs = new();
        public bool DryRun;
        public int Limit = DefaultLimit;
    }

    public static string ActionName(UnlockAction a) => a switch
    {
        UnlockAction.Status => "status",
        UnlockAction.All => "all",
        UnlockAction.Prefabs => "prefabs",
        UnlockAction.Lock => "lock",
        UnlockAction.Revert => "revert",
        _ => a.ToString().ToLowerInvariant(),
    };

    /// <summary>True for the actions that write player state.</summary>
    public static bool Writes(UnlockAction a) => a != UnlockAction.Status;

    /// <summary>
    /// <c>{"user":"Name"|index, "action":"status|all|prefabs|lock|revert", "prefabs":[name|hash,...],
    /// "dryRun":bool, "limit":n}</c>. <c>action</c> defaults to <c>status</c>; <c>prefabs</c> is
    /// required by <c>prefabs</c> and <c>lock</c> and refused by the others, so a list is never
    /// silently ignored.
    /// </summary>
    public static Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args must be an object with 'user' and 'action'");

        var r = new Request { User = ParseUser(args) };

        r.Action = UnlockAction.Status;
        if (args.TryGetProperty("action", out var a) && a.ValueKind != JsonValueKind.Null)
        {
            if (a.ValueKind != JsonValueKind.String)
                throw new ArgumentException("args.action must be a string (status, all, prefabs, lock, revert)");
            r.Action = (a.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "" or "status" => UnlockAction.Status,
                "all" => UnlockAction.All,
                "prefabs" => UnlockAction.Prefabs,
                "lock" => UnlockAction.Lock,
                "revert" => UnlockAction.Revert,
                var other => throw new ArgumentException($"unknown action '{other}' (status, all, prefabs, lock, revert)"),
            };
        }

        bool hasList = args.TryGetProperty("prefabs", out var list) && list.ValueKind != JsonValueKind.Null;
        bool needsList = r.Action is UnlockAction.Prefabs or UnlockAction.Lock;
        if (needsList && !hasList)
            throw new ArgumentException($"action '{ActionName(r.Action)}' needs args.prefabs (a list of prefab names or GUID hashes)");
        if (!needsList && hasList)
            throw new ArgumentException($"args.prefabs is only used by action 'prefabs' or 'lock', not '{ActionName(r.Action)}'");
        if (hasList) r.Prefabs = ParsePrefabs(list);

        if (args.TryGetProperty("dryRun", out var d) && d.ValueKind != JsonValueKind.Null)
        {
            if (d.ValueKind != JsonValueKind.True && d.ValueKind != JsonValueKind.False)
                throw new ArgumentException("args.dryRun must be true or false");
            r.DryRun = d.ValueKind == JsonValueKind.True;
        }

        if (args.TryGetProperty("limit", out var l) && l.ValueKind != JsonValueKind.Null)
        {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out int n) || n < 0 || n > MaxLimit)
                throw new ArgumentException($"args.limit must be an integer 0..{MaxLimit}");
            r.Limit = n;
        }

        return r;
    }

    private static UserRef ParseUser(JsonElement args)
    {
        if (!args.TryGetProperty("user", out var u) || u.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("args.user is required (character name, or the User.Index as a number)");
        if (u.ValueKind == JsonValueKind.Number)
        {
            if (!u.TryGetInt32(out int index) || index < 0)
                throw new ArgumentException("args.user as a number must be a non-negative User.Index");
            return new UserRef(null, index);
        }
        if (u.ValueKind == JsonValueKind.String)
        {
            var name = (u.GetString() ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("args.user is empty");
            return new UserRef(name, null);
        }
        throw new ArgumentException("args.user must be a character name or a User.Index number");
    }

    private static List<PrefabInspectArgs.Key> ParsePrefabs(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("args.prefabs must be an array of prefab names or GUID hashes");
        var keys = new List<PrefabInspectArgs.Key>();
        int i = 0;
        foreach (var el in list.EnumerateArray())
        {
            if (keys.Count >= MaxPrefabs)
                throw new ArgumentException($"args.prefabs has more than {MaxPrefabs} entries");
            if (el.ValueKind == JsonValueKind.Number)
            {
                if (!el.TryGetInt32(out int n) || n == 0)
                    throw new ArgumentException($"args.prefabs[{i}] must be a non-zero int GUID hash");
                keys.Add(new PrefabInspectArgs.Key(n));
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                var text = (el.GetString() ?? "").Trim();
                if (text.Length == 0) throw new ArgumentException($"args.prefabs[{i}] is empty");
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hash))
                {
                    if (hash == 0) throw new ArgumentException($"args.prefabs[{i}] must be a non-zero int GUID hash");
                    keys.Add(new PrefabInspectArgs.Key(hash));
                }
                else keys.Add(new PrefabInspectArgs.Key(text));
            }
            else throw new ArgumentException($"args.prefabs[{i}] must be a prefab name or an int GUID hash");
            i++;
        }
        if (keys.Count == 0) throw new ArgumentException("args.prefabs is empty");
        return keys;
    }

    // ---- unlocked state as the game tests it ----------------------------------------------------

    /// <summary>
    /// The player's unlock buffers, read once. <see cref="Progression"/> keeps buffer order (the
    /// lock path rewrites it); the rest are sets.
    /// </summary>
    internal sealed class State
    {
        public List<int> Progression = new();
        public HashSet<int> BlueprintsOn = new();
        public HashSet<int> BlueprintsContentLocked = new();
        public HashSet<int> RecipesOn = new();
        public HashSet<int> RecipesContentLocked = new();
        public HashSet<int> Shapeshifts = new();

        private HashSet<int>? _progressionSet;

        public HashSet<int> ProgressionSet => _progressionSet ??= new HashSet<int>(Progression);

        /// <summary><c>HasUnlockedProgression(skipProgressionCheck: false)</c>, native.</summary>
        public bool IsUnlocked(int guid) =>
            ProgressionSet.Contains(guid) || BlueprintsOn.Contains(guid) || RecipesOn.Contains(guid) || Shapeshifts.Contains(guid);

        /// <summary>Present in a derived buffer with <c>UserHasRequiredContentFlags</c> false (DLC the player lacks).</summary>
        public bool IsContentLocked(int guid) =>
            !IsUnlocked(guid) && (BlueprintsContentLocked.Contains(guid) || RecipesContentLocked.Contains(guid));

        /// <summary>
        /// GUIDs a rebuild-trigger event must avoid: the handler skips a GUID the game already
        /// counts as unlocked through a derived buffer, so it would never run the rebuild.
        /// </summary>
        public bool InDerivedUnlocked(int guid) => BlueprintsOn.Contains(guid) || RecipesOn.Contains(guid) || Shapeshifts.Contains(guid);
    }

    internal sealed class UnlockPlan
    {
        /// <summary>GUIDs to add to <c>UnlockedProgressionElement</c>, first-seen order, no duplicates.</summary>
        public List<int> New = new();
        public int AlreadyUnlocked;

        /// <summary>Already in a derived buffer with the content flag false: adding cannot change that flag.</summary>
        public List<int> ContentLocked = new();

        /// <summary>
        /// What is written raw to <c>UnlockedProgressionElement</c> before the trigger event:
        /// the current list plus every new GUID but the last. Null when one event does it all
        /// (zero or one new GUID).
        /// </summary>
        public List<int>? BulkWrite;

        /// <summary>The GUID raised as <c>UnlockProgressionDebugEvent</c>; its handler adds it and rebuilds the derived buffers once.</summary>
        public int? Trigger;
    }

    /// <summary>
    /// Plan an unlock: skip zero, duplicates and anything the game already counts as unlocked;
    /// set aside content-locked rows. All new GUIDs but the last are appended raw in one write,
    /// and the last is raised through the game's own debug event so the handler's
    /// <c>UpdateUnlockedBuffers</c> rebuilds the derived buffers from the whole list once
    /// (one rebuild instead of one per GUID).
    /// </summary>
    public static UnlockPlan PlanUnlock(State state, IEnumerable<int> candidates)
    {
        var plan = new UnlockPlan();
        var seen = new HashSet<int>();
        foreach (int g in candidates)
        {
            if (g == 0 || !seen.Add(g)) continue;
            if (state.IsUnlocked(g)) { plan.AlreadyUnlocked++; continue; }
            if (state.IsContentLocked(g)) { plan.ContentLocked.Add(g); continue; }
            plan.New.Add(g);
        }
        if (plan.New.Count == 0) return plan;
        plan.Trigger = plan.New[plan.New.Count - 1];
        if (plan.New.Count > 1)
        {
            plan.BulkWrite = new List<int>(state.Progression.Count + plan.New.Count - 1);
            plan.BulkWrite.AddRange(state.Progression);
            for (int i = 0; i < plan.New.Count - 1; i++) plan.BulkWrite.Add(plan.New[i]);
        }
        return plan;
    }

    internal sealed class LockPlan
    {
        /// <summary>Targets found in <c>UnlockedProgressionElement</c> and removed.</summary>
        public List<int> Removed = new();

        /// <summary>Targets not in <c>UnlockedProgressionElement</c> (granted by a tech or book, or never unlocked).</summary>
        public List<int> NotInProgression = new();

        /// <summary>What is written raw to <c>UnlockedProgressionElement</c>: the kept rows minus <see cref="Trigger"/>.</summary>
        public List<int>? Write;

        /// <summary>A kept GUID removed and raised again so the handler rebuilds the derived buffers without the targets.</summary>
        public int? Trigger;

        /// <summary>Why nothing can be written, when set.</summary>
        public string? Refusal;
    }

    /// <summary>
    /// Plan a lock. There is no client or server "lock progression" debug event (both metadata sets:
    /// only the server-to-client <c>LockProgressionServerEvent</c>), so the lock removes the
    /// targets from <c>UnlockedProgressionElement</c> and makes the game rebuild the derived
    /// buffers: <c>UpdateUnlockedBuffers</c> zeroes <c>UnlockedRecipe/Blueprint/Shapeshift/
    /// MusicTrackElement</c> and refills them from the list. The rebuild runs only through
    /// <c>DebugEventsSystem.UnlockProgression</c> for a GUID the game does not count as unlocked,
    /// so one kept GUID that is in no derived buffer (normally a tech or V Blood GUID) is taken
    /// out with the targets and raised again: the list ends as before minus the targets.
    /// </summary>
    public static LockPlan PlanLock(State state, IEnumerable<int> targets)
    {
        var plan = new LockPlan();
        var wanted = new HashSet<int>();
        foreach (int g in targets)
            if (g != 0) wanted.Add(g);

        var present = state.ProgressionSet;
        foreach (int g in wanted)
        {
            if (present.Contains(g)) plan.Removed.Add(g);
            else plan.NotInProgression.Add(g);
        }
        plan.Removed.Sort();
        plan.NotInProgression.Sort();
        if (plan.Removed.Count == 0) return plan;

        var kept = new List<int>(state.Progression.Count);
        foreach (int g in state.Progression)
            if (!wanted.Contains(g)) kept.Add(g);

        // Prefer the last eligible kept row: the rewrite then only moves it to the end.
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            if (state.InDerivedUnlocked(kept[i])) continue;
            plan.Trigger = kept[i];
            kept.RemoveAt(i);
            break;
        }
        if (plan.Trigger == null)
        {
            plan.Refusal = "no remaining UnlockedProgressionElement row can trigger the rebuild (every kept GUID is also a derived blueprint, recipe or shapeshift row, or none is kept); refusing to rewrite the list without a way to rebuild the derived buffers";
            return plan;
        }
        plan.Write = kept;
        return plan;
    }

    // ---- record of what this op added ---------------------------------------------------------

    /// <summary>Which mod registered a GUID, when Progenitor registered it (Core <c>UnlockPrune.TryDescribeGuid</c>).</summary>
    internal sealed class GuidOwner
    {
        public string Mod = "";
        public string Id = "";
        public string Kind = "";
    }

    internal sealed class Record
    {
        public int Version = RecordVersion;
        public string User = "";
        public int UserIndex = -1;
        public string? UpdatedUtc;
        public List<int> Guids = new();

        /// <summary>
        /// GUIDs of <see cref="Guids"/> a Progenitor mod registered, with that mod. Optional in
        /// the file (records written before it parse with none). Vanilla GUIDs have no row.
        /// </summary>
        public Dictionary<int, GuidOwner> Owners = new();
    }

    /// <summary><c>progenitor-unlock-&lt;name&gt;.json</c>; the name lower-cased, anything but letters, digits, '-' and '_' replaced by '_'.</summary>
    public static string RecordFileName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("user name is empty");
        var sb = new StringBuilder(RecordPrefix.Length + userName.Length + 5);
        sb.Append(RecordPrefix);
        foreach (char c in userName.Trim().ToLowerInvariant())
            sb.Append((c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c == '-' || c == '_' ? c : '_');
        sb.Append(".json");
        return sb.ToString();
    }

    /// <summary>
    /// The existing record plus <paramref name="added"/>, first-seen order, no duplicates or zeros.
    /// <paramref name="owners"/> (GUID → mod) is merged over the existing owners; a row is kept
    /// only for a GUID the record lists.
    /// </summary>
    public static Record Merge(Record? existing, string user, int userIndex, IEnumerable<int> added, DateTime utc,
        IReadOnlyDictionary<int, GuidOwner>? owners = null)
    {
        if (existing != null && !string.Equals(existing.User, user, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"unlock record belongs to '{existing.User}', not '{user}'");
        var r = new Record
        {
            User = user,
            UserIndex = userIndex,
            UpdatedUtc = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        var seen = new HashSet<int>();
        if (existing != null)
            foreach (int g in existing.Guids)
                if (g != 0 && seen.Add(g)) r.Guids.Add(g);
        foreach (int g in added)
            if (g != 0 && seen.Add(g)) r.Guids.Add(g);
        if (existing != null)
            foreach (var kv in existing.Owners)
                if (seen.Contains(kv.Key)) r.Owners[kv.Key] = kv.Value;
        if (owners != null)
            foreach (var kv in owners)
                if (seen.Contains(kv.Key)) r.Owners[kv.Key] = kv.Value;
        return r;
    }

    public static string Serialize(Record r)
    {
        var doc = new Dictionary<string, object?>
        {
            ["version"] = r.Version,
            ["user"] = r.User,
            ["userIndex"] = r.UserIndex,
            ["updatedUtc"] = r.UpdatedUtc,
            ["note"] = "GUIDs the Dev bridge 'unlock' op added to this player's UnlockedProgressionElement; 'unlock' action 'revert' removes exactly these; 'owners' names the Progenitor mod that registered a GUID",
            ["guids"] = r.Guids,
            ["owners"] = r.Owners.OrderBy(kv => kv.Key).ToDictionary(
                kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                kv => new Dictionary<string, string> { ["mod"] = kv.Value.Mod, ["id"] = kv.Value.Id, ["kind"] = kv.Value.Kind }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public static Record ParseRecord(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("unlock record is not a JSON object");
        int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        if (version != RecordVersion) throw new FormatException($"unlock record version {version} is not {RecordVersion}");
        var r = new Record
        {
            User = root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "",
            UserIndex = root.TryGetProperty("userIndex", out var ui) && ui.ValueKind == JsonValueKind.Number ? ui.GetInt32() : -1,
            UpdatedUtc = root.TryGetProperty("updatedUtc", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
        };
        if (r.User.Length == 0) throw new FormatException("unlock record has no user");
        if (!root.TryGetProperty("guids", out var g) || g.ValueKind != JsonValueKind.Array)
            throw new FormatException("unlock record has no guids array");
        foreach (var el in g.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int n))
                throw new FormatException("unlock record guids must be int GUID hashes");
            if (n != 0) r.Guids.Add(n);
        }
        // Advisory: a malformed owner row is skipped, never a reason to refuse unlock or revert.
        if (root.TryGetProperty("owners", out var owners) && owners.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in owners.EnumerateObject())
            {
                if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guid) || guid == 0) continue;
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                r.Owners[guid] = new GuidOwner
                {
                    Mod = OwnerText(p.Value, "mod"),
                    Id = OwnerText(p.Value, "id"),
                    Kind = OwnerText(p.Value, "kind"),
                };
            }
        }
        return r;
    }

    private static string OwnerText(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- read-back ------------------------------------------------------------------------------

    /// <summary>
    /// Of <paramref name="guids"/>, those the game now counts as unlocked (<paramref name="wantUnlocked"/>)
    /// or as not unlocked (false). Used by the delayed read-back to report what did not take.
    /// </summary>
    public static List<int> Mismatches(State after, IEnumerable<int> guids, bool wantUnlocked)
    {
        var list = new List<int>();
        foreach (int g in guids)
            if (after.IsUnlocked(g) != wantUnlocked) list.Add(g);
        return list;
    }

    /// <summary>At most <paramref name="limit"/> items, in order.</summary>
    public static List<T> Cap<T>(IEnumerable<T> items, int limit) => items.Take(Math.Max(0, limit)).ToList();
}
