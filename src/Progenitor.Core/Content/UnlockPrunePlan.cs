using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Progenitor.Plugins;

namespace Progenitor.Content;

/// <summary>
/// Game-free half of <c>UnlockPrune</c>: the ledger of GUIDs Progenitor registered as unlockable
/// prefabs, which of them are stale, which buffer rows go, and the log lines. The ECS reads and
/// writes stay in <c>UnlockPrune</c>.
///
/// <para><b>Why.</b> A player's <c>UnlockedProgressionElement</c> (and the recipe and blueprint
/// buffers the game derives from it) is saved with the player and outlives the mod that registered
/// a GUID in it. When the GUID's prefab is gone, the client looks it up about twice a second and
/// logs <c>PrefabLookupMap.TryGet - Prefab with PrefabGUID ... does not exist</c> from
/// <c>ClientProgressionUpdateSystem</c> and <c>AlertsUISystem.GetAvailableSpellPoints</c>
/// (live 2026-10-06: 15,000 lines after the Sample recipe <c>sample.bench.crystal-thread</c> was
/// unlocked by the Dev bridge and the Sample was then removed from the server).</para>
///
/// <para><b>Stale.</b> A GUID is stale when Progenitor registered it (it is a candidate), no
/// loaded plugin registers it now, and it does not resolve to a prefab this process did not clone
/// (so a vanilla or another mod's prefab under the same hash is never touched).</para>
/// </summary>
internal static class UnlockPrunePlan
{
    public const int LedgerVersion = 1;

    /// <summary>Ledger file under the server's <c>BepInEx/config</c>.</summary>
    public const string LedgerFileName = "progenitor-content-guids.json";

    public const string KindRecipe = "recipe";
    public const string KindBlueprint = "blueprint";

    /// <summary>One GUID Progenitor registered: who registered it and under which id.</summary>
    internal sealed class LedgerEntry
    {
        public int Guid;
        public string Id = "";
        public string Kind = KindRecipe;
        public string Mod = "";
        public string? LastRegisteredUtc;
    }

    /// <summary>
    /// GUIDs Progenitor ever registered as unlockable prefabs on this server. Persisted so a world
    /// loaded after a mod is gone can still tell its GUIDs from anything else. Rows are never
    /// dropped: an offline player can hold the GUID long after the mod left.
    /// </summary>
    internal sealed class Ledger
    {
        private readonly Dictionary<int, LedgerEntry> _rows = new();

        public int Count => _rows.Count;

        public IEnumerable<int> Guids => _rows.Keys;

        public bool Contains(int guid) => _rows.ContainsKey(guid);

        public bool TryGet(int guid, out LedgerEntry entry) => _rows.TryGetValue(guid, out entry!);

        /// <summary>
        /// Record a registration. Returns true when the stored row changed (new GUID, or a
        /// different id, kind or mod), so the caller writes the file only then.
        /// </summary>
        public bool Note(int guid, string id, string kind, string mod, DateTime utc)
        {
            if (guid == 0) return false;
            if (_rows.TryGetValue(guid, out var old)
                && old.Id == id && old.Kind == kind && old.Mod == mod)
                return false;
            _rows[guid] = new LedgerEntry
            {
                Guid = guid,
                Id = id ?? "",
                Kind = kind ?? KindRecipe,
                Mod = mod ?? "",
                LastRegisteredUtc = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            return true;
        }

        public string Serialize()
        {
            var rows = _rows.Values.OrderBy(r => r.Guid).Select(r => new Dictionary<string, object?>
            {
                ["guid"] = r.Guid,
                ["id"] = r.Id,
                ["kind"] = r.Kind,
                ["mod"] = r.Mod,
                ["lastRegisteredUtc"] = r.LastRegisteredUtc,
            }).ToList();
            var doc = new Dictionary<string, object?>
            {
                ["version"] = LedgerVersion,
                ["note"] = "GUIDs Progenitor registered as unlockable prefabs on this server. A GUID here that no loaded mod registers is removed from players' unlock buffers (docs/recipes.md, 'When the mod is gone').",
                ["guids"] = rows,
            };
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }

        public static Ledger Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("content GUID ledger is not a JSON object");
            int version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (version != LedgerVersion) throw new FormatException($"content GUID ledger version {version} is not {LedgerVersion}");
            if (!root.TryGetProperty("guids", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new FormatException("content GUID ledger has no guids array");
            var ledger = new Ledger();
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object
                    || !el.TryGetProperty("guid", out var g) || g.ValueKind != JsonValueKind.Number || !g.TryGetInt32(out int guid) || guid == 0)
                    throw new FormatException("content GUID ledger rows need a non-zero int guid");
                ledger._rows[guid] = new LedgerEntry
                {
                    Guid = guid,
                    Id = Text(el, "id") ?? "",
                    Kind = Text(el, "kind") ?? KindRecipe,
                    Mod = Text(el, "mod") ?? "",
                    LastRegisteredUtc = Text(el, "lastRegisteredUtc"),
                };
            }
            return ledger;
        }

        private static string? Text(JsonElement el, string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>
    /// Prune right after an unregister only when the plugin is positively gone
    /// (<see cref="PluginUnloadReason.Removal"/>) or a handle was disposed outside any unload
    /// (<c>null</c>: the plugin dropped the recipe itself). A Swap, Suspend, Shutdown or Unknown
    /// brings the plugin back and keeps the player's unlock, exactly as
    /// <see cref="PluginUnloadReasonPolicy.KeepsState"/> keeps buffs. The join-time sweep catches
    /// whatever a kept unload leaves behind once the mod really is gone.
    /// </summary>
    public static bool ShouldPruneOnUnregister(PluginUnloadReason? reason) =>
        reason == null || reason == PluginUnloadReason.Removal;

    /// <summary>
    /// Ledger sweeps (join, world load) keep a GUID whose mod this process knows in any state but
    /// unloaded: a mod that failed to load, was switched off, is dormant on this side or is still
    /// loading is coming back, and a pruned unlock would not. Only a mod absent from the process,
    /// or unloaded, loses its rows.
    /// </summary>
    public static bool ModStillPresent(bool knownToProcess, bool unloaded) => knownToProcess && !unloaded;

    /// <summary>
    /// Of <paramref name="candidates"/>, those no loaded plugin registers and that do not resolve
    /// to a prefab someone else owns. Zero and duplicates are dropped.
    /// </summary>
    public static HashSet<int> Stale(IEnumerable<int> candidates, ICollection<int> registered, Func<int, bool> resolvesToForeignPrefab)
    {
        var stale = new HashSet<int>();
        foreach (int g in candidates)
        {
            if (g == 0 || stale.Contains(g)) continue;
            if (registered.Contains(g)) continue;
            if (resolvesToForeignPrefab(g)) continue;
            stale.Add(g);
        }
        return stale;
    }

    /// <summary>What one buffer filter keeps and drops; rows are indices into the original buffer.</summary>
    internal sealed class RowFilter
    {
        public List<int> KeptRows = new();
        public List<int> Removed = new();

        public bool Changed => Removed.Count > 0;
    }

    /// <summary>Indices of the rows to keep (in order) and the stale GUIDs dropped (in buffer order).</summary>
    public static RowFilter Filter(IReadOnlyList<int> rowGuids, ISet<int> stale)
    {
        var f = new RowFilter();
        for (int i = 0; i < rowGuids.Count; i++)
        {
            if (stale.Contains(rowGuids[i])) f.Removed.Add(rowGuids[i]);
            else f.KeptRows.Add(i);
        }
        return f;
    }

    /// <summary>
    /// <c>UnlockPrune: removed 1 stale Progenitor GUID(s) from 'Name' (progression -1276252187 [recipe sample.bench.crystal-thread, mod progenitor.sample]; recipes -1276252187) after unregister reason=Removal</c>.
    /// </summary>
    public static string FormatPruned(string user, IReadOnlyList<int> progression, IReadOnlyList<int> recipes,
        IReadOnlyList<int> blueprints, Func<int, string> describe, string trigger)
    {
        var all = new HashSet<int>(progression);
        all.UnionWith(recipes);
        all.UnionWith(blueprints);
        var sb = new StringBuilder();
        sb.Append("UnlockPrune: removed ").Append(all.Count).Append(" stale Progenitor GUID(s) from '").Append(user).Append("' (");
        bool first = true;
        Part(sb, "progression", progression, describe, ref first);
        Part(sb, "recipes", recipes, describe: null, ref first);
        Part(sb, "blueprints", blueprints, describe: null, ref first);
        sb.Append(") ").Append(trigger);
        return sb.ToString();
    }

    private static void Part(StringBuilder sb, string label, IReadOnlyList<int> guids, Func<int, string>? describe, ref bool first)
    {
        if (guids.Count == 0) return;
        if (!first) sb.Append("; ");
        first = false;
        sb.Append(label).Append(' ');
        for (int i = 0; i < guids.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(guids[i].ToString(CultureInfo.InvariantCulture));
            if (describe != null) sb.Append(" [").Append(describe(guids[i])).Append(']');
        }
    }

    /// <summary><c>recipe sample.bench.crystal-thread, mod progenitor.sample</c>, or <c>unknown</c>.</summary>
    public static string Describe(Ledger? ledger, int guid) =>
        ledger != null && ledger.TryGet(guid, out var e)
            ? $"{e.Kind} {e.Id}, mod {(e.Mod.Length == 0 ? "?" : e.Mod)}"
            : "unknown";
}
