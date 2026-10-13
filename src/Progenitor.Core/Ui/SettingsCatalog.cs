using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Progenitor.Config;

namespace Progenitor.Ui;

/// <summary>
/// Settings and keybinds for known config sources. Registered synced entries remain visible
/// while ConfigSync parks them outside ConfigFile's dictionary to protect local saves.
/// Synced values are read-only unless their mod explicitly enables administrator requests
/// through ServerSettings; server values are never written as local settings.
/// </summary>
public static partial class SettingsCatalog
{
    private static readonly object Gate = new();
    private static readonly List<Source> Sources = new();
    private static Func<ConfigEntryBase, bool>? _syncedProbe;

    /// <summary>
    /// Fills <see cref="Sources"/> from the process's plugin list. Set by
    /// <c>SettingsSourceSweep.Install</c> on a client and left null under tests, where sources are
    /// registered explicitly.
    /// </summary>
    internal static Action? SourceSweep;

    /// <summary>
    /// Record a plugin's config file so its entries appear in the panel. Called for every plugin
    /// that touches <c>ctx.Keybinds</c>, and by the client-side chainloader sweep. Idempotent per
    /// (pluginId, file).
    /// </summary>
    /// <param name="foreign">
    /// True for a source discovered by the chainloader sweep rather than registered by a
    /// Progenitor plugin: another BepInEx mod's own config file. Those rows are shown but not
    /// editable unless <see cref="AllowForeignEdits"/> is set - see that flag for why.
    /// </param>
    public static void Register(string? pluginId, ConfigFile? file, object? owner = null, bool foreign = false)
    {
        if (file == null) return;
        string id = string.IsNullOrWhiteSpace(pluginId) ? "unowned" : pluginId.Trim();
        lock (Gate)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                if (ReferenceEquals(Sources[i].File, file)
                    && string.Equals(Sources[i].PluginId, id, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            Sources.Add(new Source(id, file, owner, foreign));
        }

        BumpKeyVersion();
    }

    /// <summary>
    /// Whether another mod's settings may be edited from this panel. Default <b>false</b>.
    ///
    /// <para>
    /// The only "is this the server's value?" test the panel has is Progenitor's own config sync
    /// (<c>ConfigSyncPeek.IsRegistered</c> / <c>ConfigSync.IsLocked</c>). A third-party mod that
    /// syncs its own settings from a server is invisible to both, so its entries looked like
    /// ordinary editable rows and a write persisted a server-owned value into that mod's
    /// <c>GUID.cfg</c> (R34 panel note 2). Those rows are therefore read-only by default: the
    /// player can see every mod's settings, and change the ones whose rules we actually know.
    /// The operator can turn editing on in the Core cfg when they know the mods involved.
    /// </para>
    /// </summary>
    public static bool AllowForeignEdits { get; set; }

    /// <summary>
    /// SETTINGS1: true when <paramref name="entry"/> lives in a config file that is not
    /// <paramref name="pluginId"/>'s own. When the plugin registered a file (every
    /// <c>ctx.Menu</c> does), any other file is elsewhere; when it registered none, a file another
    /// plugin registered, or one the chainloader sweep found, is elsewhere. An unknown file is taken
    /// as the plugin's own — nothing says otherwise.
    /// </summary>
    internal static bool IsOwnedElsewhere(string pluginId, ConfigEntryBase? entry)
    {
        var file = entry?.ConfigFile;
        if (file == null) return false;
        bool hasOwn = false, ownMatches = false, otherMatches = false;
        lock (Gate)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var source = Sources[i];
                bool mine = string.Equals(source.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) && !source.Foreign;
                bool same = ReferenceEquals(source.File, file);
                if (mine)
                {
                    hasOwn = true;
                    if (same) ownMatches = true;
                }
                else if (same)
                {
                    otherMatches = true;
                }
            }
        }

        if (ownMatches) return false;
        if (hasOwn) return true;
        return otherMatches;
    }

    /// <summary>Drop this plugin's config sources. Plugin unload.</summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        int removed = RemoveBindings(owner);
        lock (Gate)
        {
            for (int i = Sources.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Sources[i].Owner, owner)) continue;
                Sources.RemoveAt(i);
                removed++;
            }

            BumpKeyVersion();
            return removed;
        }
    }

    /// <summary>How many config files the panel would walk.</summary>
    public static int SourceCount
    {
        get { lock (Gate) return Sources.Count; }
    }

    internal static string[] PluginIdsForOwner(object owner)
    {
        var ids = new List<string>();
        lock (Gate)
            foreach (var source in Sources)
                if (ReferenceEquals(source.Owner, owner)) ids.Add(source.PluginId);
        return ids.ToArray();
    }

    /// <summary>
    /// Build the rows: keybinds first (sorted by plugin then id), then config entries grouped by
    /// plugin, section and key. Never throws — a source that cannot be walked is skipped with one
    /// warning, because a broken cfg must not take the panel down with it.
    /// </summary>
    public static IReadOnlyList<SettingsRow> Rows()
    {
        var rows = new List<SettingsRow>();

        var binds = Keybinds.Snapshot();
        var sortedBinds = new List<IKeybindInfo>(binds);
        sortedBinds.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.PluginId, b.PluginId);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });

        for (int i = 0; i < sortedBinds.Count; i++)
        {
            var bind = sortedBinds[i];
            rows.Add(SettingsRow.ForKeybind(bind));
        }

        try { SourceSweep?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings source sweep threw: " + e.Message); }

        Source[] sources;
        lock (Gate)
            sources = Sources.ToArray();

        Array.Sort(sources, static (a, b) => string.CompareOrdinal(a.PluginId, b.PluginId));

        for (int i = 0; i < sources.Length; i++)
        {
            IDictionary? entries;
            try { entries = ConfigHelper.GetEntries(sources[i].File); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: settings could not read entries of '" + sources[i].PluginId + "': " + e.Message);
                continue;
            }

            if (entries == null) continue;

            // ConfigHelper.GetEntries hands back BepInEx's LIVE dictionary and the walk cannot take
            // its lock, so a Bind on another thread while the panel is opening throws
            // InvalidOperationException mid-enumeration. Discarding the plugin's whole row list
            // there left a group header with nothing under it (R34 panel note 5); a mutation is a
            // transient, so retry the walk a few times before giving up on the source.
            var ordered = new List<ConfigEntryBase>();
            if (!TryCollect(entries, ordered, sources[i].PluginId)) continue;

            // Locked synced entries are deliberately parked out of ConfigFile so a save
            // cannot overwrite local preferences. Show current registrations from this
            // same owned source, but never resurrect detached session locks after unload.
            foreach (var registered in Networking.ConfigSync.RegisteredEntries(sources[i].PluginId, sources[i].File))
            {
                int existing = ordered.FindIndex(entry => entry.Definition.Equals(registered.Definition));
                if (existing >= 0) ordered[existing] = registered;
                else ordered.Add(registered);
            }

            ordered.Sort(static (a, b) =>
            {
                int c = string.CompareOrdinal(a.Definition.Section, b.Definition.Section);
                return c != 0 ? c : string.CompareOrdinal(a.Definition.Key, b.Definition.Key);
            });

            for (int j = 0; j < ordered.Count; j++)
            {
                var entry = ordered[j];
                // Keybinds already have their own rows with a rebind flow; do not show the raw
                // string a second time.
                if (string.Equals(entry.Definition.Section, Keybinds.Section, StringComparison.Ordinal)
                    && HasKeybind(binds, sources[i].PluginId, entry.Definition.Key))
                    continue;

                rows.Add(SettingsRow.ForEntry(
                    sources[i].PluginId,
                    entry,
                    IsSynced(entry),
                    foreign: sources[i].Foreign && !AllowForeignEdits,
                    presentation: PresentationFor(
                        sources[i].PluginId, entry.Definition.Section, entry.Definition.Key)));
            }
        }

        var actions = AllActions();
        for (int i = 0; i < actions.Count; i++)
            rows.Add(SettingsRow.ForAction(actions[i]));

        return rows;
    }

    /// <summary>
    /// Settings organization: Bootstrap's shared <c>progenitor.cfg</c>, for <see cref="FindEntry"/> when the
    /// chainloader sweep has not listed it (a server process, tests). Set by the Mods menu; null: not searched.
    /// </summary>
    internal static Func<ConfigFile?>? SharedFileProbe;

    /// <summary>
    /// Settings organization: the bound entry <c>[<paramref name="section"/>] <paramref name="key"/></c> of
    /// <paramref name="pluginId"/>'s config sources — its own files first, then the files the sweep found for it
    /// (Progenitor's own shared cfg is one), then <see cref="SharedFileProbe"/> for Progenitor. Null when no source
    /// has it bound. Lets Progenitor's page and the Configuration tab place a row by its id whenever the module that
    /// owns the entry bound it, without holding the entry themselves.
    /// </summary>
    internal static ConfigEntryBase? FindEntry(string pluginId, string section, string key)
    {
        if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return null;
        string id = string.IsNullOrWhiteSpace(pluginId) ? "unowned" : pluginId.Trim();
        try { SourceSweep?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings source sweep threw: " + e.Message); }

        Source[] sources;
        lock (Gate)
            sources = Sources.ToArray();
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var source in sources)
            {
                if (!string.Equals(source.PluginId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (source.Foreign != (pass == 1)) continue;
                var found = FindIn(source.File, id, section, key);
                if (found != null) return found;
            }
        }

        if (string.Equals(id, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase))
        {
            ConfigFile? shared = null;
            try { shared = SharedFileProbe?.Invoke(); }
            catch { shared = null; }
            if (shared != null) return FindIn(shared, id, section, key);
        }

        return null;
    }

    private static ConfigEntryBase? FindIn(ConfigFile file, string pluginId, string section, string key)
    {
        try
        {
            foreach (var registered in Networking.ConfigSync.RegisteredEntries(pluginId, file))
                if (Same(registered, section, key)) return registered;
        }
        catch { /* the file's own entries are next */ }

        IDictionary? entries;
        try { entries = ConfigHelper.GetEntries(file); }
        catch { return null; }
        if (entries == null) return null;
        var list = new List<ConfigEntryBase>();
        if (!TryCollect(entries, list, pluginId)) return null;
        foreach (var entry in list)
            if (Same(entry, section, key)) return entry;
        return null;
    }

    private static bool Same(ConfigEntryBase? entry, string section, string key) =>
        entry != null
        && string.Equals(entry.Definition.Section, section, StringComparison.Ordinal)
        && string.Equals(entry.Definition.Key, key, StringComparison.Ordinal);

    /// <summary>
    /// True when this entry is owned by the config-sync module (server-authoritative). The probe
    /// is a seam so the panel's rules can be tested without <c>ConfigSync</c> state; production
    /// reads <c>ConfigSync</c>'s own registry (see <see cref="DefaultSyncedProbe"/>).
    /// </summary>
    public static bool IsSynced(ConfigEntryBase? entry)
    {
        if (entry == null) return false;
        var probe = _syncedProbe;
        try { return probe != null ? probe(entry) : DefaultSyncedProbe(entry); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings synced probe threw: " + e.Message);
            // Fail closed: an entry we cannot classify is treated as the server's, which costs an
            // editor rather than writing over a value the server owns.
            return true;
        }
    }

    /// <summary>Point <see cref="IsSynced"/> at a fake. Null restores the production probe.</summary>
    internal static void UseSyncedProbe(Func<ConfigEntryBase, bool>? probe) => _syncedProbe = probe;

    internal static void Uninstall()
    {
        ClearBindings();
        lock (Gate)
            Sources.Clear();
        AllowForeignEdits = false;
        _syncedProbe = null;
        SourceSweep = null;
        SharedFileProbe = null;
        ConfigSyncPeek.Reset();
    }

    internal static string Describe() => "settingsSources=" + SourceCount;

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        ClearBindings();
        lock (Gate)
            Sources.Clear();
        AllowForeignEdits = false;
        _syncedProbe = null;
        SourceSweep = null;
        SharedFileProbe = null;
        ConfigSyncPeek.Reset();
    }
#endif

    /// <summary>
    /// Copy a live config dictionary into <paramref name="into"/>, retrying a concurrent
    /// mutation. False when every attempt was raced; the caller skips that source and the panel
    /// says so rather than showing an empty group.
    /// </summary>
    private static bool TryCollect(IDictionary entries, List<ConfigEntryBase> into, string pluginId)
    {
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            into.Clear();
            try
            {
                foreach (DictionaryEntry kv in entries)
                {
                    if (kv.Value is ConfigEntryBase entry) into.Add(entry);
                }

                return true;
            }
            catch (InvalidOperationException) when (attempt < attempts)
            {
                // Another thread bound a setting while we walked. Try again.
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: settings entry walk of '" + pluginId + "' threw: " + e.Message
                    + "; that plugin has no rows in this panel.");
                into.Clear();
                return false;
            }
        }

        Plugin.Logger?.LogWarning(
            "Ui: settings entry walk of '" + pluginId + "' was raced by a concurrent Bind on every "
            + "attempt; that plugin has no rows in this panel. Re-open the panel.");
        into.Clear();
        return false;
    }

    private static bool HasKeybind(IReadOnlyList<IKeybindInfo> binds, string pluginId, string key)
    {
        for (int i = 0; i < binds.Count; i++)
        {
            if (string.Equals(binds[i].PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binds[i].Id, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Production classification. <c>ConfigSync</c>'s registry is private and lives in a module
    /// this job may not edit, so it is read reflectively once and cached; any failure falls back
    /// to the public <c>ConfigSync.IsLocked</c>, which is the half of "synced" that actually
    /// matters for a write (a locked entry refuses the write anyway).
    /// </summary>
    private static bool DefaultSyncedProbe(ConfigEntryBase entry) =>
        ConfigSyncPeek.IsRegistered(entry) || Progenitor.Networking.ConfigSync.IsLocked(entry);

    private readonly struct Source
    {
        public Source(string pluginId, ConfigFile file, object? owner, bool foreign)
        {
            PluginId = pluginId;
            File = file;
            Owner = owner;
            Foreign = foreign;
        }

        /// <summary>Discovered by the chainloader sweep: another mod's own config file.</summary>
        public bool Foreign { get; }

        public string PluginId { get; }

        public ConfigFile File { get; }

        public object? Owner { get; }
    }

    /// <summary>
    /// Cached reflective read of <c>ConfigSync</c>'s private <c>Entries</c> map. Isolated in its
    /// own type so the one brittle thing in this file is one method with one latch: the first
    /// failure sets <see cref="_broken"/> and every later call takes the public fallback instead
    /// of paying for reflection sixty times a second.
    /// </summary>
    private static class ConfigSyncPeek
    {
        private static readonly object Gate = new();
        private static FieldInfo? _entriesField;
        private static FieldInfo? _slotEntryField;
        private static bool _resolved;
        private static bool _broken;

        public static void Reset()
        {
            lock (Gate)
            {
                _entriesField = null;
                _slotEntryField = null;
                _resolved = false;
                _broken = false;
            }
        }

        public static bool IsRegistered(ConfigEntryBase entry)
        {
            FieldInfo? entries;
            lock (Gate)
            {
                if (_broken) return false;
                if (!_resolved)
                {
                    _resolved = true;
                    _entriesField = typeof(Progenitor.Networking.ConfigSync)
                        .GetField("Entries", BindingFlags.NonPublic | BindingFlags.Static);
                    if (_entriesField == null) _broken = true;
                }

                entries = _entriesField;
            }

            if (entries == null) return false;

            IDictionary? map;
            try { map = entries.GetValue(null) as IDictionary; }
            catch { lock (Gate) _broken = true; return false; }

            if (map == null) return false;

            try
            {
                foreach (DictionaryEntry kv in map)
                {
                    var slot = kv.Value;
                    if (slot == null) continue;
                    var field = SlotEntryField(slot.GetType());
                    if (field == null) return false;
                    if (field.GetValue(slot) is not ConfigEntryBase registered) continue;
                    if (ReferenceEquals(registered, entry)) return true;
                    if (ReferenceEquals(registered.ConfigFile, entry.ConfigFile)
                        && registered.Definition.Equals(entry.Definition))
                        return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The map was mutated while we walked it (a Register on another thread). One
                // missed classification is a stale row, not a wrong write: the panel re-reads on
                // every open, and a locked entry still refuses the write.
                return false;
            }

            return false;
        }

        private static FieldInfo? SlotEntryField(Type slotType)
        {
            lock (Gate)
            {
                if (_slotEntryField != null && _slotEntryField.DeclaringType == slotType)
                    return _slotEntryField;
                _slotEntryField = slotType.GetField("Entry", BindingFlags.Public | BindingFlags.Instance);
                if (_slotEntryField == null) _broken = true;
                return _slotEntryField;
            }
        }
    }
}
