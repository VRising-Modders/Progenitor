using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Plugins;

/// <summary>One mod the player turned off in the Mods menu.</summary>
internal sealed class ModEnableEntry
{
    internal string Id { get; set; } = "";

    /// <summary>The display name when it was turned off. Display only.</summary>
    internal string Name { get; set; } = "";

    /// <summary>ISO-8601 UTC of the change. Display only.</summary>
    internal string ChangedUtc { get; set; } = "";

    /// <summary>
    /// The plugins a live switch-off took with it this session (DLL siblings and dependants). Kept so
    /// a later Core generation knows they are held by the switch, not by a server pause.
    /// </summary>
    internal List<string> HeldWith { get; set; } = new();

    /// <summary>
    /// Review (5a6e40f8) blocking finding 1: the process <see cref="HeldWith"/> was recorded in
    /// (<see cref="ModEnableState.ProcessSession"/>). The list means something only in that process:
    /// a restart loads those plugins again, so in any other process it is ignored and pruned.
    /// </summary>
    internal string Session { get; set; } = "";

    /// <summary>
    /// Re-review of 9a27bf01, finding 1: the ordinary BepInEx plugins this switch-off turned off at the
    /// next start because they need this mod. Turning the mod on again brings exactly these back.
    /// </summary>
    internal List<string> PlainOff { get; set; } = new();
}

/// <summary>
/// SETTINGS1 stage 3: <c>BepInEx/config/progenitor-mod-state.json</c>, the player's per-mod on/off
/// choice, remembered across sessions. Same folder and write rules as
/// <c>progenitor-server-profiles.json</c>: a temp file and <c>File.Replace</c>, and a file that
/// exists but cannot be read is never overwritten.
///
/// <para><b>Only mods turned off are stored.</b> Turning a mod on removes its entry.</para>
///
/// <para><b>Fail open.</b> A file that cannot be read means nothing is known to be off: every mod
/// loads (logged once) and the switch refuses to change anything until the file can be read. Staying
/// off is the player's wish, not a safety property, so an unreadable file must not stop mods
/// loading.</para>
/// </summary>
internal static class ModEnableState
{
    internal const string FileName = "progenitor-mod-state.json";
    internal const int Schema = 1;
    internal const int MaxEntries = 512;

    /// <summary>Why a write was refused: the file exists but could not be read.</summary>
    internal const string UnreadableRefusal =
        "The Mods menu's on/off file (BepInEx/config/" + FileName + ") could not be read, so nothing was changed.";

    private static readonly object Gate = new();
    private static bool _warnedUnreadable;

    /// <summary>
    /// This process, as a string that survives a Core swap and changes with every start: the process
    /// id and its start time. Tests replace it.
    /// </summary>
    internal static Func<string> ProcessSession { get; set; } = DefaultSession;

    private static string DefaultSession()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return "pid:" + p.Id.ToString(CultureInfo.InvariantCulture) + "@"
                + p.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static string CurrentSession()
    {
        try { return ProcessSession?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>The plugins that went with <paramref name="entry"/>, if they did so in this process; else none.</summary>
    internal static IReadOnlyList<string> HeldWithNow(ModEnableEntry? entry)
    {
        if (entry == null || entry.HeldWith.Count == 0) return Array.Empty<string>();
        string now = CurrentSession();
        if (now.Length == 0 || !string.Equals(entry.Session, now, StringComparison.Ordinal)) return Array.Empty<string>();
        return entry.HeldWith;
    }

    /// <summary>
    /// At Core load: drop every <c>heldWith</c> list recorded in another process (a restart loads those
    /// plugins again). A Core swap is the same process, so its lists are kept. Returns how many changed.
    /// </summary>
    /// <summary>Record the ordinary plugins a switch-off of <paramref name="id"/> turned off with it.</summary>
    internal static bool SetPlainOff(string id, IReadOnlyList<string> plainIds)
    {
        string key = (id ?? "").Trim();
        lock (Gate)
        {
            if (!TryLoad(out var all) || !all.TryGetValue(key, out var e)) return false;
            e.PlainOff = new List<string>();
            foreach (var p in plainIds ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(p) && !e.PlainOff.Contains(p.Trim())) e.PlainOff.Add(p.Trim());
            if (!Save(all, out _)) return false;
            _cache = all;
            Stamp();
            return true;
        }
    }

    internal static int PruneOtherSessions()
    {
        string now = CurrentSession();
        lock (Gate)
        {
            if (!TryLoad(out var all)) return 0;
            int changed = 0;
            foreach (var e in all.Values)
            {
                if (e.HeldWith.Count == 0 || (now.Length > 0 && string.Equals(e.Session, now, StringComparison.Ordinal))) continue;
                e.HeldWith.Clear();
                e.Session = "";
                changed++;
            }

            if (changed == 0 || !Save(all, out _)) return 0;
            _cache = all;
            Stamp();
            return changed;
        }
    }
    private static Dictionary<string, ModEnableEntry>? _cache;
    private static DateTime _cacheStamp;
    private static long _cacheLength = -1;

    /// <summary>Tests only: a directory instead of <c>BepInEx/config</c>.</summary>
#pragma warning disable CS0649 // assigned by the test project only
    internal static string? TestDirectory;
#pragma warning restore CS0649

    internal static string FilePath =>
        Path.Combine(TestDirectory ?? Progenitor.Networking.ModManifest.ConfigDirectory, FileName);

    /// <summary>
    /// Plugins that can never be turned off: the loader, Progenitor's Core and Bootstrap (one GUID),
    /// the dev bridge and the preloader. A hand-edited file that names one is ignored.
    /// </summary>
    internal static readonly string[] RequiredIds =
    {
        "bepinex", "progenitor", "progenitor.dev", "progenitor.preloader",
    };

    internal static bool IsRequiredPart(string? id)
    {
        string v = Progenitor.Plugins.LegacyIds.Normalize((id ?? "").Trim());
        if (v.Length == 0) return false;
        foreach (var r in RequiredIds)
            if (string.Equals(r, v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when the player turned <paramref name="id"/> off.</summary>
    internal static bool IsDisabled(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || IsRequiredPart(id)) return false;
        lock (Gate)
        {
            if (!TryLoad(out var all)) return false;
            return all.ContainsKey(id!.Trim());
        }
    }

    /// <summary>Every mod the player turned off. Empty when the file cannot be read.</summary>
    internal static List<ModEnableEntry> Disabled()
    {
        lock (Gate)
        {
            var list = new List<ModEnableEntry>();
            if (!TryLoad(out var all)) return list;
            foreach (var e in all.Values) if (!IsRequiredPart(e.Id)) list.Add(e);
            list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            return list;
        }
    }

    /// <summary>The entry for <paramref name="id"/>, or null when it is on (or unknown).</summary>
    internal static ModEnableEntry? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (Gate)
        {
            if (!TryLoad(out var all)) return null;
            return all.TryGetValue(id!.Trim(), out var e) && !IsRequiredPart(e.Id) ? e : null;
        }
    }

    /// <summary>
    /// Record the player's choice. Off adds (or updates) the entry with the plugins taken with it;
    /// on removes it. False with <paramref name="error"/> set when nothing was written.
    /// </summary>
    internal static bool Set(string id, string? name, bool enabled, IReadOnlyList<string>? heldWith, DateTime nowUtc, out string error)
    {
        error = "";
        string key = (id ?? "").Trim();
        if (key.Length == 0) { error = "no mod id"; return false; }
        if (IsRequiredPart(key)) { error = ModSwitchWords.RequiredRefusal; return false; }
        lock (Gate)
        {
            if (!TryLoad(out var all)) { error = UnreadableRefusal; return false; }
            if (enabled)
            {
                if (!all.Remove(key)) return true;
            }
            else
            {
                if (!all.ContainsKey(key) && all.Count >= MaxEntries) { error = "too many mods are turned off already"; return false; }
                var entry = new ModEnableEntry
                {
                    Id = key,
                    Name = string.IsNullOrWhiteSpace(name) ? key : name!.Trim(),
                    ChangedUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture),
                };
                if (heldWith != null)
                    foreach (var h in heldWith)
                        if (!string.IsNullOrWhiteSpace(h) && !string.Equals(h, key, StringComparison.OrdinalIgnoreCase))
                            entry.HeldWith.Add(h.Trim());
                if (entry.HeldWith.Count > 0) entry.Session = CurrentSession();
                all[key] = entry;
            }

            if (!Save(all, out error)) return false;
            _cache = all;
            Stamp();
            return true;
        }
    }

    /// <summary>
    /// Why a plugin must not run its <c>OnLoad</c> this start, or null when it may: the player turned
    /// it off, or one of its hard dependencies is off (turned off, or skipped for that reason).
    /// </summary>
    internal static string? OffReason(string id, IReadOnlyList<string>? hardDependencies, Func<string, bool>? skippedNow = null)
    {
        if (IsRequiredPart(id)) return null;
        // INDEX1: an installed version the Progenitor Index took down (Disable) does not load, at a start or a hot load.
        // Nothing is written: the copy loads again once the takedown is lifted.
        string? takedown = null;
        try { takedown = TakedownReason?.Invoke(id); }
        catch { takedown = null; }
        if (!string.IsNullOrEmpty(takedown)) return takedown;
        if (IsDisabled(id)) return ModSwitchWords.OffByPlayer;
        string? dep = BlockingDependency(id, hardDependencies, skippedNow);
        return dep == null ? null : ModSwitchWords.OffBecause(dep);
    }

    /// <summary>
    /// INDEX1: why a plugin's installed version is taken down from the Progenitor Index (it must not load), or null. Set at
    /// Core load (<c>Install.IndexRevocationSource.DisableReason</c>); null in tests unless a test sets it.
    /// </summary>
    internal static Func<string, string?>? TakedownReason { get; set; }

    /// <summary>The first hard dependency that is off (turned off, or skipped for that reason), or null.</summary>
    internal static string? BlockingDependency(string id, IReadOnlyList<string>? hardDependencies, Func<string, bool>? skippedNow = null)
    {
        if (IsRequiredPart(id) || hardDependencies == null) return null;
        foreach (var dep in hardDependencies)
        {
            if (string.IsNullOrWhiteSpace(dep) || IsRequiredPart(dep)) continue;
            bool off = IsDisabled(dep);
            if (!off && skippedNow != null)
            {
                try { off = skippedNow(dep); }
                catch { off = false; }
            }

            if (off) return dep.Trim();
        }

        return null;
    }

    /// <summary>
    /// Review finding 1: <see cref="ModEnableEntry.HeldWith"/> only means something while the turned-off
    /// mod is still in Bootstrap's pause table (this process, possibly a later Core generation). At a
    /// process start the table is empty and the plugins that went with it load and run again, so their
    /// hold is dropped here. Returns how many entries changed. A file that cannot be read is left alone.
    /// </summary>
    internal static int PruneHeldWith(Func<string, bool> suspendedNow)
    {
        if (suspendedNow == null) return 0;
        lock (Gate)
        {
            if (!TryLoad(out var all)) return 0;
            int changed = 0;
            foreach (var e in all.Values)
            {
                if (e.HeldWith.Count == 0) continue;
                bool live;
                try { live = suspendedNow(e.Id); }
                catch { live = false; }
                if (live) continue;
                e.HeldWith.Clear();
                changed++;
            }

            if (changed == 0) return 0;
            if (!Save(all, out _)) return 0;
            _cache = all;
            Stamp();
            return changed;
        }
    }

    private static bool TryLoad(out Dictionary<string, ModEnableEntry> result)
    {
        result = new Dictionary<string, ModEnableEntry>(StringComparer.OrdinalIgnoreCase);
        string path = FilePath;
        DateTime stamp;
        long length;
        try
        {
            if (!File.Exists(path))
            {
                _cache = result;
                _cacheStamp = DateTime.MinValue;
                _cacheLength = -1;
                return true;
            }

            var info = new FileInfo(path);
            stamp = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch
        {
            return Unreadable("the file could not be inspected", result);
        }

        if (_cache != null && stamp == _cacheStamp && length == _cacheLength)
        {
            foreach (var kv in _cache) result[kv.Key] = kv.Value;
            return true;
        }

        string error;
        try
        {
            if (TryParse(File.ReadAllText(path), out var list, out error))
            {
                foreach (var e in list) result[e.Id] = e;
                _cache = new Dictionary<string, ModEnableEntry>(result, StringComparer.OrdinalIgnoreCase);
                _cacheStamp = stamp;
                _cacheLength = length;
                return true;
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return Unreadable(error, result);
    }

    private static bool Unreadable(string error, Dictionary<string, ModEnableEntry> result)
    {
        if (!_warnedUnreadable)
        {
            _warnedUnreadable = true;
            try
            {
                Progenitor.Plugin.Logger?.LogWarning("Mods: " + FileName + " could not be read (" + error
                    + "); every mod loads this start and the file is left untouched until it can be read.");
            }
            catch { /* a log is not a gate */ }
        }

        result.Clear();
        _cache = null;
        return false;
    }

    private static void Stamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            _cacheStamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            _cacheLength = info.Exists ? info.Length : -1;
        }
        catch
        {
            _cache = null;
        }
    }

    private static bool Save(Dictionary<string, ModEnableEntry> all, out string error)
    {
        error = "";
        string path = FilePath;
        string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, Serialize(all.Values));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            error = "writing " + FileName + " failed: " + e.Message;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            try { Progenitor.Plugin.Logger?.LogWarning("Mods: " + error); } catch { /* */ }
            return false;
        }
    }

    internal static string Serialize(IEnumerable<ModEnableEntry> entries)
    {
        var list = new List<ModEnableEntry>(entries ?? Array.Empty<ModEnableEntry>());
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("disabled");
            foreach (var e in list)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Id)) continue;
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("name", e.Name);
                w.WriteString("changedUtc", e.ChangedUtc);
                w.WriteStartArray("heldWith");
                foreach (var h in e.HeldWith) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WriteString("session", e.Session);
                w.WriteStartArray("plainOff");
                foreach (var p in e.PlainOff) w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static bool TryParse(string? json, out List<ModEnableEntry> entries, out string error)
    {
        entries = new List<ModEnableEntry>();
        error = "";
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject root) { error = "not a JSON object"; return false; }
            if (root["disabled"] is not JsonArray array) return true;
            foreach (var node in array)
            {
                if (node is not JsonObject o) continue;
                string id = Str(o, "id");
                if (id.Length == 0) continue;
                var e = new ModEnableEntry { Id = id, Name = Str(o, "name"), ChangedUtc = Str(o, "changedUtc"), Session = Str(o, "session") };
                if (o["plainOff"] is JsonArray plainOff)
                    foreach (var h in plainOff)
                        if (h is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) e.PlainOff.Add(s.Trim());
                if (o["heldWith"] is JsonArray held)
                    foreach (var h in held)
                        if (h is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) e.HeldWith.Add(s.Trim());
                entries.Add(e);
                if (entries.Count >= MaxEntries) break;
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            entries.Clear();
            return false;
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? (s ?? "").Trim() : "";

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _warnedUnreadable = false;
            ProcessSession = DefaultSession;
            _cache = null;
            _cacheStamp = DateTime.MinValue;
            _cacheLength = -1;
        }
    }
}

/// <summary>
/// SETTINGS1 stage 3: every word the on/off switch and its status use, in one place, so the Mods
/// list, the page's status bar and the dialogs say exactly the same thing.
/// </summary>
internal static class ModSwitchWords
{
    internal const string TurnsOffAtNextStart = "Turns off at next start";
    internal const string TurnsOnAtNextStart = "Turns on at next start";
    internal const string RemovedAtNextStart = "Removed at next start";

    internal const string OffByPlayer = "turned off in the Mods menu";

    internal static string OffBecause(string dependency) => "it needs " + dependency + ", which is turned off";

    /// <summary>Review finding 4: the status word of a mod skipped because a dependency is off.</summary>
    internal static string OffBecauseWord(string dependencyName) => "Off because " + dependencyName + " is off";

    /// <summary>Why its switch cannot turn it on by itself.</summary>
    internal static string OffBecauseReason(string dependencyName) =>
        "It needs " + dependencyName + ", which is turned off. Turn " + dependencyName + " on to turn this on.";

    /// <summary>Review finding 4: a <c>[ProgenitorSide]</c> plugin that does not run on this side.</summary>
    internal const string DormantReason = "It does not run on this side of the game, so there is nothing to switch here.";

    /// <summary>Review finding 2: turning a mod on while the joined server has paused mods.</summary>
    internal const string TurnedOnAfterLeave = "Turned on. It loads when you leave this server.";

    internal const string RetryAfterLeave = "Leave this server first, then press Retry.";

    internal const string RequiredRefusal = "Progenitor needs this to run, so it cannot be turned off.";
    internal const string LoaderRefusal = "BepInEx loads every mod, so it cannot be turned off here.";
    internal const string NotOnClientReason = "This mod is not on this client.";
    internal const string NoBackendReason = "Progenitor could not reach the part that turns mods on and off.";
}
