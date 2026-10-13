using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Plugins;
#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;
#endif

namespace Progenitor.Networking;

/// <summary>
/// Server-authoritative config for entries bound with <c>synced: true</c>. After a join
/// handshake succeeds the server sends every Synced entry whose plugin the client also
/// reported at a compatible version; the client applies the Toml strings, locks the
/// entries until disconnect, then restores the cached local values. A plugin unload
/// while the client is still in the session keeps the lock and the server value; a
/// re-bind of the same key re-attaches (one log line). Locals restore only on
/// disconnect, a new client world, or Core Uninstall. A write while locked is reverted
/// immediately (one log line) via the entry's <c>SettingChanged</c> path — BepInEx
/// assigns first, then we put the server value back. <c>SaveOnConfigSet</c> stays off
/// while any slot on that file is locked, and locked keys are parked as BepInEx
/// orphans holding the pre-join local so an explicit <c>ConfigFile.Save()</c> cannot
/// flush server values into the client's <c>GUID.cfg</c>. Under
/// <see cref="NetworkVersionRule.Minor"/> the client applies a value only when the
/// entry exists with the same type (schema-drift otherwise); a v1 packet with an
/// empty type name is refused under Minor with an explicit type-gate line, not
/// applied. Under <see cref="NetworkVersionRule.None"/> the snapshot follows
/// Handshake: presence only, versions are not compared, so a client that also
/// reported the plugin is included. A later server
/// <c>SettingChanged</c> is pushed to every handshake-ok connected user (including
/// disabled). A push (or server re-register) that lands while the client plugin is
/// unbound updates the session lock so re-attach writes the newest server Toml,
/// never the value captured at Unregister. Unknown plugin or section/key is ignored
/// with one log line. A record that cannot fit the 15 KiB packet is logged and
/// omitted (fail closed for that entry).
///
/// Admin-only config sync (flag at bind, Toml strings, apply-and-lock, restore locals, ignore
/// unknown) over <see cref="CustomEvents"/>: no config-editor plugin attributes, no admin list,
/// and no Harmony prefixes on BepInEx.
/// </summary>
#if !PROGENITOR_TESTS
public static unsafe class ConfigSync
#else
public static class ConfigSync
#endif
{
    /// <summary>Custom event id <c>"PrcS"</c>. One id for join snapshots and later pushes.</summary>
    public const int EventId = 0x50726353;

    private static readonly object Gate = new();
    private static readonly Dictionary<EntryKey, Slot> Entries = new(EntryKey.Comparer);
    private static readonly Dictionary<EntryKey, SessionLock> SessionLocks = new(EntryKey.Comparer);
    private static readonly Dictionary<ConfigFile, int> FileRefs = new();
    private static readonly Dictionary<ConfigFile, FileSaveHold> SaveHolds = new();

    /// <summary>
    /// SAVEHOLD1: files where a restore gave up and could not park its key, so a live row still holds
    /// the server's value. <see cref="TrySaveHeldFile"/> never saves these.
    /// </summary>
    private static readonly HashSet<ConfigFile> ServerValueLeftLive = new();

    /// <summary>
    /// In-flight retry state per key, so the retry FCFG5 added has an end (R53 #4) and that end
    /// arrives on its own (R54 #2). Cleared by <see cref="ClearLock"/>, which is the one place a
    /// lock stops existing — success and give-up both go through it.
    /// </summary>
    private static readonly Dictionary<EntryKey, RestoreRetry> RestoreRetries = new(EntryKey.Comparer);

    /// <summary>
    /// Owner token for the delayed retry posts, so <see cref="Uninstall"/> can drop every one of
    /// them in a single call the way <c>HostedServerLauncher</c> drops its probes. A plain
    /// <see cref="object"/> and not a plugin instance: this work is Core's.
    /// </summary>
    private static readonly object RetryOwner = new();

    /// <summary>
    /// Seconds between a failed restore and the next attempt (R54 #2). The retry rides
    /// <c>MainThread.PostDelayed</c>, which is checked once per frame, so this is the frame pump
    /// FCFG6's docstring claimed and did not have.
    /// </summary>
    internal const double RestoreRetryIntervalSeconds = 1.0;

    /// <summary>
    /// How long a stuck slot may keep retrying, measured from its <em>first</em> failure, before
    /// it gives up loudly (R54 #2, correcting FCFG6). Short on purpose: the hold this releases is
    /// per <see cref="ConfigFile"/>, not per entry — <c>SaveOnConfigSet</c> is a property of the
    /// file and BepInEx offers nothing narrower — so every frame a stuck slot keeps its lock is a
    /// frame in which no entry anywhere in that <c>.cfg</c> autosaves.
    /// <para>
    /// FCFG6 spent its budget in <see cref="RestoreLocal"/> calls, and that method has exactly two
    /// production callers — <c>OnWorldReady</c> on a client world and <see cref="Uninstall"/> —
    /// with no frame hook anywhere in this module. So the unit was "the client built a new world",
    /// a persistent failure spent one attempt per leave-to-menu, and because BepInEx does not call
    /// <c>Plugin.Unload</c> on a normal quit the file's autosave stayed frozen for the session.
    /// Seconds and frames are units this module can actually produce.
    /// </para>
    /// </summary>
    internal const double RestoreRetryBudgetSeconds = 5.0;

    /// <summary>
    /// Ceiling on attempts for one key, independent of the clock. At
    /// <see cref="RestoreRetryIntervalSeconds"/> the budget above spends about six attempts; this
    /// only fires if the clock stands still, and it exists so a broken clock cannot turn the
    /// retry back into the unbounded loop R53 #4 was about.
    /// </summary>
    internal const int MaxRestoreAttempts = 8;

    /// <summary>One key's retry: how many failures, when the budget runs out, and whether a delayed attempt is already posted.</summary>
    private sealed class RestoreRetry
    {
        public int Attempts;
        public double Deadline;
        public bool Scheduled;
    }
    private static readonly HashSet<string> IgnoredOnce = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RefusedOnce = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VersionMismatchOnce = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SchemaDriftOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>C3 (client): plugins the join snapshot in progress marked sent, and the keys it carried, until it completes.</summary>
    private static readonly HashSet<string> SnapshotMarked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<EntryKey> SnapshotSeen = new(EntryKey.Comparer);
    private static readonly EventHandler<SettingChangedEventArgs> SettingChangedHandler = OnSettingChanged;
    private static readonly EventHandler EntrySettingChangedHandler = OnEntrySettingChanged;

    /// <summary>
    /// Message <c>ctx.Config.Bind(..., synced: true)</c> throws. That path cannot
    /// <c>Track</c> the handle; use <c>ctx.Bind(..., synced: true)</c>.
    /// </summary>
    public const string UntrackedSyncedBindMessage =
        "PluginConfig.Bind(synced: true) does not track the registration for unload. Use ctx.Bind(..., synced: true) so plugin unload unregisters the Synced row.";

    /// <summary>
    /// Later pushes walk the same user set as handshake re-arm: connected
    /// <c>User</c> entities including disabled.
    /// </summary>
    internal const bool PushIncludesDisabledUsers = true;

    private static bool _installed;
    private static bool _applying;
    private static int _suppressSaveDepth;
#if PROGENITOR_TESTS
    /// <summary>
    /// When set, <see cref="ApplyRecords"/> uses this rule for the type gate instead
    /// of the Handshake Provide/Require tables. Tests of a v1 packet under Minor
    /// must not mutate Handshake tables shared with the Handshake collection.
    /// </summary>
    internal static NetworkVersionRule? TestApplyRule;
#endif
#if !PROGENITOR_TESTS
    private static IDisposable? _worldReadySub;
    private static IDisposable? _disconnectedSub;
    private static readonly HashSet<int> HandshakeOkUsers = new();
#endif

#if PROGENITOR_TESTS
    /// <summary>When true, a server-side <c>SettingChanged</c> records a push instead of sending.</summary>
    internal static bool TestIsServer;

    /// <summary>Pushes captured while <see cref="TestIsServer"/> is true.</summary>
    internal static readonly List<ConfigSyncRecord> TestPushes = new();

    /// <summary>Log lines this module would have written (Info and Warning).</summary>
    internal static readonly List<string> TestLogs = new();

    /// <summary>
    /// Job FCFG7 (R54 #2). Monotonic seconds for the restore-retry budget. Null is the live
    /// <c>Stopwatch</c>, which is what every test that does not cross the budget wants.
    /// <see cref="Uninstall"/> clears it, so <c>ResetForTests</c> does too.
    /// </summary>
    internal static Func<double>? TestClock;

    /// <summary>
    /// Job FCFG7 (R54 #2). Stands in for <c>MainThread.PostDelayed</c> so a test can hold the
    /// posted retry and run it when it chooses. Null uses the real queue, whose items
    /// <see cref="Uninstall"/> drops by owner.
    /// </summary>
    internal static Action<double, Action, object?>? TestPostDelayed;
#endif

    /// <summary>How many Synced entries are currently registered (both sides).</summary>
    internal static int RegisteredCount
    {
        get { lock (Gate) return Entries.Count; }
    }

    /// <summary>How many registered entries are currently locked to a server value.</summary>
    internal static int LockedCount
    {
        get
        {
            lock (Gate)
            {
                int n = SessionLocks.Count;
                foreach (var slot in Entries.Values)
                    if (slot.Locked) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// Bind a Synced setting on <paramref name="file"/> and register it. Refuses a non-BCL
    /// <typeparamref name="T"/> (same gate as <see cref="ConfigHelper"/> on the shared file)
    /// so the Toml form is well-defined and <c>ConfigEntry&lt;T&gt;</c> cannot pin a
    /// collectible generation. Dispose the returned handle (or plugin unload) to unregister.
    /// </summary>
    public static ConfigEntry<T> Bind<T>(
        ConfigFile file,
        string pluginId,
        string section,
        string key,
        T defaultValue,
        string? description = null,
        string? pluginVersion = null)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        ThrowIfNotSyncable(typeof(T));
        HarmonySlotHandoff.ThrowIfModuleSkipped(HarmonySlotHandoff.ConfigModule);
        if (string.IsNullOrEmpty(pluginId)) throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        if (string.IsNullOrEmpty(section)) throw new ArgumentException("Section is required.", nameof(section));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));

        var entry = file.Bind(section, key, defaultValue, description ?? "");
        Register(pluginId, pluginVersion ?? "", entry);
        return entry;
    }

    /// <summary>
    /// Thrown by <c>ctx.Config.Bind(..., synced: true)</c>. That facade cannot record
    /// the handle for unload; <c>ctx.Bind(..., synced: true)</c> does.
    /// </summary>
    public static void ThrowIfUntrackedSyncedBind() =>
        throw new InvalidOperationException(UntrackedSyncedBindMessage);

    /// <summary>
    /// Mark an already-bound entry Synced. Same <typeparamref name="T"/> gate as
    /// <see cref="Bind{T}"/>. Returns a handle that unregisters; a second register of the
    /// same plugin/section/key replaces the slot and disposes the previous handle.
    /// </summary>
    public static IDisposable Register(string pluginId, ConfigEntryBase entry) =>
        Register(pluginId, pluginVersion: "", entry);

    /// <summary>
    /// Mark an already-bound entry Synced, recording <paramref name="pluginVersion"/> so a
    /// join snapshot can refuse a client on another version of the same guid. Dispose
    /// unregisters; a second register of the same plugin/section/key replaces the slot.
    /// </summary>
    public static IDisposable Register(string pluginId, string pluginVersion, ConfigEntryBase entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrEmpty(pluginId)) throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        ThrowIfNotSyncable(entry.SettingType);
        HarmonySlotHandoff.ThrowIfModuleSkipped(HarmonySlotHandoff.ConfigModule);
        ThrowIfRecordTooLarge(pluginId, entry);

        var key = new EntryKey(pluginId, entry.Definition.Section, entry.Definition.Key);
        Registration handle;
        ConfigFile? watchFile = null;
        bool firstOnFile = false;
        Slot? replacedSlot = null;
        SessionLock? session = null;
        Slot slot;
        lock (Gate)
        {
            slot = new Slot(key, entry, pluginVersion ?? "");
            watchFile = entry.ConfigFile;
            if (SessionLocks.TryGetValue(key, out session))
                SessionLocks.Remove(key);
            if (Entries.TryGetValue(key, out var replaced))
            {
                replacedSlot = replaced;
                if (session == null && replaced.Locked)
                    session = SessionLock.From(replaced);
                Entries[key] = slot;
                handle = new Registration(slot);
                if (!ReferenceEquals(replaced.Entry.ConfigFile, watchFile))
                {
                    RemoveFileRef(replaced.Entry.ConfigFile);
                    firstOnFile = AddFileRef(watchFile);
                }
            }
            else
            {
                Entries[key] = slot;
                handle = new Registration(slot);
                firstOnFile = AddFileRef(watchFile);
            }
        }

        if (replacedSlot != null && !ReferenceEquals(replacedSlot.Entry, entry))
            DetachEntryHandler(replacedSlot.Entry);

        AttachEntryHandler(entry);

        if (session != null)
            ReattachSession(slot, session);
        else
        {
            bool parkNew;
            lock (Gate) parkNew = FileHasLock(watchFile);
            if (parkNew)
                ParkNewBindDuringSessionLock(slot);
        }

        // File-watch so a server cfg edit reloads and SettingChanged pushes. UninstallAll
        // drains every watch; we do not keep the lease on the registration handle.
        if (firstOnFile && watchFile != null)
        {
            try { ConfigHelper.Watch(watchFile); }
            catch (Exception e)
            {
                LogError("ConfigSync: watch of '" + watchFile.ConfigFilePath + "' threw: " + e.Message);
            }
        }

        if (ShouldPushOnRegister())
            PushChange(slot);

        return handle;
    }

    /// <summary>
    /// Drop one Synced entry. A locked client keeps the server value and the cached
    /// local for this session so a plugin reload can re-attach; locals restore on
    /// disconnect, a new client world, or Core Uninstall. Does not <c>Save</c> and
    /// does not write Toml: the parked orphan still holds the pre-join local, and a
    /// later push updates the session lock in memory only.
    /// </summary>
    public static void Unregister(string pluginId, string section, string key)
    {
        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
            return;
        Slot? slot = null;
        lock (Gate)
        {
            var id = new EntryKey(pluginId, section, key);
            if (!Entries.TryGetValue(id, out slot)) return;
            Entries.Remove(id);
            RemoveFileRef(slot.Entry.ConfigFile);
            if (slot.Locked)
                SessionLocks[id] = SessionLock.From(slot);
        }
        if (slot != null)
            DetachEntryHandler(slot.Entry);
    }

    /// <summary>True only for this exact entry's current sync registration.</summary>
    public static bool IsRegistered(string pluginId, ConfigEntryBase entry)
    {
        if (entry == null) return false;
        lock (Gate)
            return Entries.TryGetValue(new EntryKey(pluginId, entry.Definition.Section, entry.Definition.Key), out var slot)
                && ReferenceEquals(slot.Entry, entry);
    }

    /// <summary>TEXTLIMIT1 (dev bridge readback): the entry currently registered under this plugin, section and key.</summary>
    internal static bool TryFindEntry(string pluginId, string section, string key, out ConfigEntryBase? entry)
    {
        entry = null;
        if (string.IsNullOrEmpty(pluginId) || section == null || key == null) return false;
        lock (Gate)
        {
            if (!Entries.TryGetValue(new EntryKey(pluginId, section, key), out var slot)) return false;
            entry = slot.Entry;
            return entry != null;
        }
    }

    /// <summary>
    /// Current registrations for a known config source, including entries parked outside
    /// ConfigFile's dictionary while locked. Detached session locks are not registrations.
    /// </summary>
    internal static ConfigEntryBase[] RegisteredEntries(string pluginId, ConfigFile file)
    {
        lock (Gate)
        {
            var entries = new List<ConfigEntryBase>();
            foreach (var slot in Entries.Values)
                if (string.Equals(slot.Key.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                    && ReferenceEquals(slot.Entry.ConfigFile, file)) entries.Add(slot.Entry);
            return entries.ToArray();
        }
    }

    /// <summary>True when this client currently holds a server value for the entry.</summary>
    public static bool IsLocked(ConfigEntryBase? entry)
    {
        if (entry == null) return false;
        lock (Gate)
        {
            foreach (var slot in Entries.Values)
            {
                if (!ReferenceEquals(slot.Entry, entry) && !SameDefinition(slot.Entry, entry))
                    continue;
                return slot.Locked;
            }
            foreach (var session in SessionLocks.Values)
            {
                if (ReferenceEquals(session.Entry, entry) || SameDefinition(session.Entry, entry))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when this client currently holds a server value for the named entry.</summary>
    public static bool IsLocked(string pluginId, string section, string key)
    {
        lock (Gate)
        {
            var id = new EntryKey(pluginId, section, key);
            if (Entries.TryGetValue(id, out var slot) && slot.Locked) return true;
            return SessionLocks.ContainsKey(id);
        }
    }

    /// <summary>
    /// Write gate used by the production SettingChanged revert (and by tests). Returns true
    /// when the write must not stay: the entry is locked, we are not applying a server
    /// packet, and the attempted value is not already the locked one. Live BepInEx assigns
    /// first; the caller then re-applies the server Toml. Logs at most once per key until
    /// unlock.
    /// </summary>
    public static bool TryRefuseWrite(ConfigEntryBase entry, object? attempted)
    {
        if (entry == null) return false;
        if (_applying) return false;
        Slot? slot;
        lock (Gate)
        {
            if (!TryFindSlot(entry, out slot) || slot == null || !slot.Locked)
                return false;
            if (TomlEquals(attempted, slot.Entry.SettingType, slot.AppliedToml))
                return false;
        }

        LogRefused(slot);
        return true;
    }

    /// <summary>
    /// Restore every locked entry to the value this client had before the join snapshot.
    /// <para>
    /// Called from <c>Handshake.InvalidateClientServerContext</c> on client
    /// <c>WorldLoaded</c> <c>ClientWorld.Ended</c>, on the client-side connection-ended
    /// signal (<c>ClientBootstrapSystem.ClientState</c> Error/Idle after Running — FMAN17),
    /// and on handshake epoch change. A Core-only refresh of a surviving world does not
    /// clear. From <c>OnWorldReady</c> as a reconnect backstop, and from <see cref="Uninstall"/>.
    /// Nothing
    /// pumps this per frame, which is why a failed restore schedules its own retry
    /// (<see cref="RestoreRetryBudgetSeconds"/>) rather than waiting to be called again. Not
    /// called on plugin unload while the session is still live.
    /// </para>
    /// </summary>
    public static int RestoreLocal() => RestoreLocal(finalAttempt: false);

    /// <summary>
    /// <paramref name="finalAttempt"/> (R53 #4) says there will be no later retry: this is
    /// <see cref="Uninstall"/>, the generation is going away, and a slot that fails here would
    /// otherwise leave <c>SaveOnConfigSet = false</c> on a BepInEx process-lifetime
    /// <see cref="ConfigFile"/> with the record of the previous value dying alongside the Core
    /// static that holds it. On the final attempt every failure gives up immediately instead of
    /// scheduling another try inside <see cref="RestoreRetryBudgetSeconds"/>.
    /// </summary>
    internal static int RestoreLocal(bool finalAttempt)
    {
        Slot[] locked;
        SessionLock[] sessions;
        lock (Gate)
        {
            var list = new List<Slot>();
            foreach (var slot in Entries.Values)
            {
                if (!slot.Locked) continue;
                list.Add(slot);
            }
            locked = list.ToArray();
            sessions = new SessionLock[SessionLocks.Count];
            SessionLocks.Values.CopyTo(sessions, 0);
            SessionLocks.Clear();
            IgnoredOnce.Clear();
            RefusedOnce.Clear();
            SchemaDriftOnce.Clear();
            SnapshotMarked.Clear();
            SnapshotSeen.Clear();
        }

        int n = 0;
        var files = new HashSet<ConfigFile>();
        for (int i = 0; i < locked.Length; i++)
        {
            var file = locked[i].Entry.ConfigFile;
            if (RestoreSlot(locked[i], retryable: !finalAttempt))
            {
                n++;
                if (file != null) files.Add(file);
            }
        }
        for (int i = 0; i < sessions.Length; i++)
        {
            var file = sessions[i].Entry.ConfigFile;
            // Never retryable: SessionLocks was emptied above, so this transient slot is the
            // last object that knows about the lock. A failure kept "for the next attempt"
            // would be a hold nothing can ever release (R53 #4).
            if (RestoreSlot(sessions[i].ToSlot(), retryable: false))
            {
                n++;
                if (file != null) files.Add(file);
            }
        }
        foreach (var file in files)
            TrySaveFile(file);
        if (n > 0)
            LogInfo("ConfigSync: restored " + n.ToString() + " local setting(s) after disconnect");
        return n;
    }

    /// <summary>
    /// Disk Toml for a locked entry (the pre-join local). Live BepInEx Save writes
    /// this because the key is parked as an orphan; tests assert the shim's
    /// <c>LastSavedText</c> (Entries then OrphanedEntries) and must not call this
    /// from Save. Returns false when the entry is not locked.
    /// </summary>
    internal static bool TryGetDiskToml(ConfigEntryBase? entry, out string toml)
    {
        toml = "";
        if (entry == null) return false;
        lock (Gate)
        {
            if (TryFindSlot(entry, out var slot) && slot != null && slot.Locked)
            {
                toml = slot.LocalToml ?? "";
                return true;
            }
            foreach (var session in SessionLocks.Values)
            {
                if (!ReferenceEquals(session.Entry, entry) && !SameDefinition(session.Entry, entry))
                    continue;
                toml = session.LocalToml ?? "";
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Synced entries for plugins the client answered, keyed by guid <em>and</em> version.
    /// A version mismatch is skipped with one log line unless the plugin's
    /// <see cref="NetworkVersionRule"/> is <see cref="NetworkVersionRule.Minor"/> and
    /// that rule accepts the pair. <see cref="NetworkVersionRule.None"/> is Handshake's
    /// presence-only rule: versions are not compared, so the snapshot is included
    /// whenever the client also reported the plugin. Reserved handshake guids and empty
    /// versions are omitted. Server rule/version come from <see cref="Handshake.CopyRequired"/>.
    /// </summary>
    public static List<ConfigSyncRecord> SnapshotFor(IEnumerable<Handshake.ModRecord>? answered) =>
        SnapshotFor(answered, Handshake.CopyRequired());

    /// <summary>
    /// Same filter as <see cref="SnapshotFor(IEnumerable{Handshake.ModRecord})"/> with an
    /// explicit server required list. Tests pass a local array so they do not mutate
    /// Handshake tables shared with handshake tests.
    /// </summary>
    public static List<ConfigSyncRecord> SnapshotFor(
        IEnumerable<Handshake.ModRecord>? answered,
        IEnumerable<Handshake.ModRecord>? serverRequired) =>
        SnapshotFor(answered, serverRequired, versions: null, markSent: false);

    /// <summary>
    /// Mod compatibility C3. With <paramref name="versions"/> (the join's version judgment) a plugin's entries are
    /// included when <see cref="VersionMatchJudge.Check"/> passes for it (Version matching, the admins' per-mod rules,
    /// the newer side's range), instead of the attribute rule alone; null keeps the attribute rule. With
    /// <paramref name="markSent"/> (a client that declared <c>Handshake.Capability.VersionRanges</c>) every plugin the
    /// snapshot covered, the ones both sides run that passed, gets a <see cref="ConfigSyncWire.SentMarker"/> record after
    /// its entries, even when this server has none of its synced keys: the client then locks the synced keys of that
    /// plugin the snapshot did not contain to their declared defaults. Progenitor's own settings are never marked.
    /// </summary>
    internal static List<ConfigSyncRecord> SnapshotFor(
        IEnumerable<Handshake.ModRecord>? answered,
        IEnumerable<Handshake.ModRecord>? serverRequired,
        VersionJudgeContext? versions,
        bool markSent)
    {
        var client = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        if (answered != null)
        {
            foreach (var rec in answered)
            {
                if (string.IsNullOrEmpty(rec.Guid)) continue;
                if (Handshake.Compare.IsReservedGuid(rec.Guid)) continue;
                if (string.IsNullOrEmpty(rec.Version)) continue;
                client[rec.Guid] = rec;
            }
        }

        var serverByGuid = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        if (serverRequired != null)
        {
            foreach (var rec in serverRequired)
            {
                if (!string.IsNullOrEmpty(rec.Guid))
                    serverByGuid[rec.Guid] = rec;
            }
        }

        var records = new List<ConfigSyncRecord>();
        // JOINFLOW1: Progenitor's own server-wide settings (the server message and the reset notification)
        // belong to no plugin the client answers for: every client that answered the handshake at all runs
        // Core, so they go to every answering client. Their schema is Core's, not a plugin version's.
        bool answeredHandshake = answered != null;
        if (client.Count == 0 && !answeredHandshake) return records;

        List<string>? mismatchLogs = null;
        // C3: per plugin, whether the judgment passed (evaluated once), and the plugins' entries in order.
        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var byPlugin = new Dictionary<string, List<ConfigSyncRecord>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        int acceptedBefore = versions?.Accepted.Count ?? 0;
        lock (Gate)
        {
            foreach (var slot in Entries.Values)
            {
                if (IsFrameworkSettings(slot.Key.PluginId))
                {
                    if (answeredHandshake) records.Add(ReadRecord(slot));
                    continue;
                }

                if (!client.TryGetValue(slot.Key.PluginId, out var clientRec)) continue;
                if (!verdicts.TryGetValue(slot.Key.PluginId, out bool passes))
                {
                    string? mismatchLine;
                    passes = versions == null
                        ? SchemaCompatible(slot, clientRec, serverByGuid, out mismatchLine)
                        : JudgedCompatible(slot.Key.PluginId, slot.PluginVersion, clientRec, serverByGuid, versions, out mismatchLine);
                    verdicts[slot.Key.PluginId] = passes;
                    if (mismatchLine != null)
                        (mismatchLogs ??= new List<string>()).Add(mismatchLine);
                }

                if (!passes) continue;
                if (!markSent)
                {
                    // Unmarked snapshots keep the registration order they always had.
                    records.Add(ReadRecord(slot));
                    continue;
                }

                if (!byPlugin.TryGetValue(slot.Key.PluginId, out var list))
                {
                    byPlugin[slot.Key.PluginId] = list = new List<ConfigSyncRecord>();
                    order.Add(slot.Key.PluginId);
                }
                list.Add(ReadRecord(slot));
            }
        }

        // C3: a plugin both sides run with none of its synced keys on this server is still covered (and marked): the
        // client's keys for it were not sent because this server's build has none.
        if (markSent)
        {
            foreach (var kv in client)
            {
                if (byPlugin.ContainsKey(kv.Key) || verdicts.ContainsKey(kv.Key)) continue;
                if (IsFrameworkSettings(kv.Key) || !serverByGuid.TryGetValue(kv.Key, out var serverRec)) continue;
                if (serverRec.Level == NetworkCompatibilityLevel.ClientOnly || !RunsHere(kv.Key)) continue;
                string? mismatchLine;
                bool passes = versions == null
                    ? Handshake.Compare.VersionsMatch(serverRec.Version ?? "", kv.Value.Version ?? "", serverRec.VersionRule)
                    : JudgedCompatible(kv.Key, serverRec.Version ?? "", kv.Value, serverByGuid, versions, out mismatchLine);
                if (!passes) continue;
                byPlugin[kv.Key] = new List<ConfigSyncRecord>();
                order.Add(kv.Key);
            }
        }

        foreach (string plugin in order)
        {
            records.AddRange(byPlugin[plugin]);
            if (markSent) records.Add(ConfigSyncWire.SentMarker(plugin));
        }

        // The join's own pass lines were logged by the handshake; the snapshot's re-judgment adds none.
        if (versions != null && versions.Accepted.Count > acceptedBefore)
            versions.Accepted.RemoveRange(acceptedBefore, versions.Accepted.Count - acceptedBefore);

        if (mismatchLogs != null)
        {
            for (int i = 0; i < mismatchLogs.Count; i++)
                LogWarning(mismatchLogs[i]);
        }
        return records;
    }

    /// <summary>
    /// C3: the join's version judgment for one plugin (<see cref="VersionMatchJudge.Check"/>), against the server's row
    /// for it, else a row made from the registered entry's plugin version. One log line per plugin when it fails.
    /// </summary>
    private static bool JudgedCompatible(
        string pluginId,
        string slotVersion,
        Handshake.ModRecord clientRec,
        Dictionary<string, Handshake.ModRecord> serverByGuid,
        VersionJudgeContext versions,
        out string? mismatchLine)
    {
        mismatchLine = null;
        if (!serverByGuid.TryGetValue(pluginId, out var serverRec) || string.IsNullOrEmpty(serverRec.Version))
            serverRec = new Handshake.ModRecord(pluginId, slotVersion ?? "", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, declared: false);

        string? problem;
        try { problem = VersionMatchJudge.Check(serverRec, clientRec.Version, versions); }
        catch (Exception e) { problem = pluginId + ": the version judgment threw: " + e.Message; }
        if (problem == null) return true;

        lock (Gate)
        {
            if (!VersionMismatchOnce.Add(pluginId))
                return false;
        }

        string why = problem.StartsWith(pluginId + ": ", StringComparison.OrdinalIgnoreCase) ? problem.Substring(pluginId.Length + 2) : problem;
        mismatchLine =
            "ConfigSync: ignored " + pluginId
            + " synced settings: client version '" + (clientRec.Version ?? "")
            + "' does not match server '" + serverRec.Version + "' (" + why + ")";
        return false;
    }

    /// <summary>
    /// JOINFLOW1: the plugin id Core's own synced settings are registered under (the server message rows,
    /// <c>JoinServerSettings</c>). They are sent to every client that answered the handshake.
    /// </summary>
    public const string FrameworkSettingsId = "progenitor";

    internal static bool IsFrameworkSettings(string? pluginId) =>
        string.Equals(pluginId, FrameworkSettingsId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same user set as handshake re-arm: a connected user (disabled entities included
    /// by the query) that already passed handshake.
    /// </summary>
    internal static bool ShouldPushToUser(bool isConnected, bool handshakeOk) =>
        isConnected && handshakeOk;

    /// <summary>
    /// Apply a server snapshot only when its handshake epoch is still this join. A packet
    /// older than the last clear is ignored (inventory and locks stay cleared).
    /// </summary>
    internal static bool ApplyIncomingServerPacket(int epoch, IReadOnlyList<ConfigSyncRecord>? records) =>
        ApplyIncomingServerPacket(epoch, records, snapshot: true, more: false);

    /// <summary>
    /// The same, with the packet's flags. Mod compatibility C3: the last packet of a join snapshot
    /// (<paramref name="snapshot"/> and not <paramref name="more"/>) completes it: every synced entry of a plugin the
    /// server marked sent that the snapshot did not contain is locked to its declared default
    /// (<see cref="CompleteSnapshot"/>).
    /// </summary>
    internal static bool ApplyIncomingServerPacket(int epoch, IReadOnlyList<ConfigSyncRecord>? records, bool snapshot, bool more)
    {
        if (!Handshake.AcceptsIncomingServerPacket(epoch)) return false;
        ApplyRecords(records, snapshot);
        if (snapshot && !more) CompleteSnapshot();
        Handshake.RestoreClientServerContext("handshake-ok");
        return true;
    }

    /// <summary>
    /// Apply a decoded packet on the client. Unknown plugin/entry: one log line, skip.
    /// First apply of an entry caches the local value and locks; later pushes update the
    /// locked value without changing the cached local.
    /// </summary>
    public static int ApplyRecords(IReadOnlyList<ConfigSyncRecord>? records, bool snapshot)
    {
        if (records == null || records.Count == 0) return 0;
        int applied = 0;
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            // C3: a snapshot's "plugin sent" marker is remembered until the snapshot completes, and every key the
            // snapshot carried is noted, so the completion knows which of a marked plugin's keys the server lacks.
            if (ConfigSyncWire.IsSentMarker(rec))
            {
                if (snapshot && rec.PluginId.Length > 0)
                    lock (Gate) SnapshotMarked.Add(rec.PluginId);
                continue;
            }

            if (snapshot)
                lock (Gate) SnapshotSeen.Add(new EntryKey(rec.PluginId, rec.Section, rec.Key));
            if (ApplyOne(rec)) applied++;
        }
        if (applied > 0)
            LogInfo("ConfigSync: applied " + applied.ToString() + " synced setting(s); locked until disconnect");
        return applied;
    }

    /// <summary>
    /// Mod compatibility C3: the join snapshot is complete. For every plugin the server marked sent, each synced entry
    /// this client registered for it that the snapshot did not contain is locked to its declared default for the
    /// session (the server's older build predates the key, so it behaves as if the setting were at its default), one
    /// line per key; the local value comes back on disconnect like every other lock. A plugin the server did not mark
    /// keeps its local value, unlocked. Returns how many entries were locked to their defaults.
    /// </summary>
    internal static int CompleteSnapshot()
    {
        var missing = new List<Slot>();
        lock (Gate)
        {
            if (SnapshotMarked.Count > 0)
            {
                foreach (var slot in Entries.Values)
                {
                    if (!SnapshotMarked.Contains(slot.Key.PluginId) || IsFrameworkSettings(slot.Key.PluginId)) continue;
                    if (SnapshotSeen.Contains(slot.Key)) continue;
                    missing.Add(slot);
                }
            }

            SnapshotMarked.Clear();
            SnapshotSeen.Clear();
        }

        int n = 0;
        foreach (var slot in missing)
            if (LockToDefault(slot, slot.Key + " not sent by the server (its version predates it)")) n++;
        return n;
    }

    /// <summary>
    /// C3: lock <paramref name="slot"/> to its entry's declared default (the same lock, parking and save hold as a
    /// server value) and log <c>ConfigSync: &lt;why&gt;; using the default &lt;value&gt;</c>.
    /// </summary>
    private static bool LockToDefault(Slot slot, string why)
    {
        var entry = slot.Entry;
        // Read by name, as SyncedBounds does: the unit-test ConfigFile shim's base type has no DefaultValue.
        object? defaultValue;
        try { defaultValue = entry.GetType().GetProperty("DefaultValue")?.GetValue(entry); }
        catch { defaultValue = null; }
        if (defaultValue == null && entry.SettingType.IsValueType)
        {
            LogWarning("ConfigSync: " + why + "; this entry has no declared default, keeping the local value");
            return false;
        }
        string toml;
        try { toml = ConfigSyncToml.Format(defaultValue, entry.SettingType); }
        catch { toml = defaultValue?.ToString() ?? ""; }

        bool ok = LockSlot(slot, toml, () =>
        {
            BeginApply(entry.ConfigFile);
            try
            {
                entry.BoxedValue = defaultValue;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                EndApply(entry.ConfigFile);
            }
        });
        if (ok) LogWarning("ConfigSync: " + why + "; using the default " + toml);
        else LogWarning("ConfigSync: " + why + "; could not set the default " + toml + ", keeping the local value");
        return ok;
    }

    /// <summary>
    /// Same type gate as <see cref="ConfigHelper.IsAllowedBclBindType"/>: BCL primitives,
    /// <see cref="string"/>, and BCL enums. Synced entries refuse everything else even on a
    /// plugin <c>GUID.cfg</c>, because the value has to round-trip as Toml and must not pin
    /// a collectible generation.
    /// </summary>
    public static void ThrowIfNotSyncable(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (ConfigHelper.IsAllowedBclBindType(type)) return;
        throw new ArgumentException(
            "ConfigSync.Bind refuses type '" + (type.FullName ?? type.Name)
            + "' from assembly '" + type.Assembly.GetName().Name
            + "'. Synced entries must be BCL primitives, string, or BCL enums "
            + "(the same gate as ConfigHelper on the shared ConfigFile).",
            nameof(type));
    }

    /// <summary>
    /// Attach the client receive handler (Bootstrap receive-slot ownership via
    /// <see cref="CustomEvents.OnReceive"/>) and the disconnect restore. No-op when the
    /// Config or Networking module is skipped. Called from <see cref="ConfigHelper.Install"/>.
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        Handshake.InstallClientServerContextWatch();
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.ConfigModule)
            || HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.NetworkingModule))
        {
            return;
        }

#if !PROGENITOR_TESTS
        if (!Plugin.IsServerProcess)
        {
            CustomEvents.OnReceive(EventId, OnPacket);
            _worldReadySub = Events.EventBus.On<Events.WorldReady>(OnWorldReady, priority: 900);
        }
        else
        {
            _disconnectedSub = Events.EventBus.On<Events.PlayerDisconnected>(OnPlayerDisconnected, priority: 900);
            ServerRunsPlugin = id => Progenitor.Plugins.ModRegistry.TryGet(id, out var entry)
                && (entry.Outcome == Progenitor.Plugins.ModLoadOutcome.Loaded || entry.Outcome == Progenitor.Plugins.ModLoadOutcome.Loading);
        }
#endif
        _installed = true;
    }

    /// <summary>
    /// C3 (review fix): whether this server runs a plugin itself (live: a Progenitor plugin loaded here). A handshake row
    /// alone does not say so: a server plugin can <c>Require</c> a companion client mod it does not load, and marking
    /// such a mod "sent" would lock every synced key of the client's copy to its default. Null (tests): every row counts.
    /// </summary>
    internal static Func<string, bool>? ServerRunsPlugin { get; set; }

    private static bool RunsHere(string pluginId)
    {
        var probe = ServerRunsPlugin;
        if (probe == null) return true;
        try { return probe(pluginId); }
        catch { return false; }
    }

    /// <summary>
    /// Drop receive handlers, restore locked locals, and clear the registry so a collectible
    /// generation cannot keep <c>ConfigEntry</c> instances. Idempotent.
    /// </summary>
    internal static void Uninstall()
    {
        Handshake.UninstallClientServerContextWatch();
        ServerRunsPlugin = null;
#if !PROGENITOR_TESTS
        try { _worldReadySub?.Dispose(); } catch { /* teardown */ }
        _worldReadySub = null;
        try { _disconnectedSub?.Dispose(); } catch { /* teardown */ }
        _disconnectedSub = null;
        try { CustomEvents.RemoveHandler(EventId); } catch { /* teardown */ }
        lock (HandshakeOkUsers) HandshakeOkUsers.Clear();
#endif
        // R54 #2. Drop the outstanding retry posts first: the pass below gives up on everything
        // still locked, immediately, so a queued attempt has nothing left to do and must not run
        // against a cleared registry.
        CancelPendingRestoreRetries();
        // finalAttempt: there is no "next restore" after this. Anything that fails here gives up
        // loudly rather than leaving a hold behind (R53 #4).
        RestoreLocal(finalAttempt: true);
        ConfigFile[] files;
        ConfigEntryBase[] entries;
        KeyValuePair<ConfigFile, FileSaveHold>[] strandedHolds;
        lock (Gate)
        {
            entries = new ConfigEntryBase[Entries.Count];
            int i = 0;
            foreach (var slot in Entries.Values)
                entries[i++] = slot.Entry;
            Entries.Clear();
            files = new ConfigFile[FileRefs.Count];
            FileRefs.Keys.CopyTo(files, 0);
            FileRefs.Clear();
            strandedHolds = new KeyValuePair<ConfigFile, FileSaveHold>[SaveHolds.Count];
            ((ICollection<KeyValuePair<ConfigFile, FileSaveHold>>)SaveHolds).CopyTo(strandedHolds, 0);
            SaveHolds.Clear();
            RestoreRetries.Clear();
            SessionLocks.Clear();
            ServerValueLeftLive.Clear();
            IgnoredOnce.Clear();
            RefusedOnce.Clear();
            VersionMismatchOnce.Clear();
            SchemaDriftOnce.Clear();
            SnapshotMarked.Clear();
            SnapshotSeen.Clear();
        }
        // R53 #4, part (b). SaveHolds is a Core static and dies with this ALC; the ConfigFile it
        // describes is a BepInEx process-lifetime object. Clearing the record without honoring
        // it leaves SaveOnConfigSet = false on that file forever — and the next generation's
        // HoldSaveOff would then record that false as the value to "restore" to. So every hold
        // still standing after the final restore is put back by hand, loudly.
        for (int i = 0; i < strandedHolds.Length; i++)
        {
            var file = strandedHolds[i].Key;
            if (file == null) continue;
            LogError(
                "ConfigSync: Uninstall is releasing a save hold on '" + file.ConfigFilePath
                + "' that no restore released; SaveOnConfigSet goes back to "
                + strandedHolds[i].Value.PreviousSaveOnConfigSet.ToString()
                + ". A Core static must never be the only record of a change made to BepInEx's"
                + " process-lifetime ConfigFile.");
            SetSaveOnConfigSet(file, strandedHolds[i].Value.PreviousSaveOnConfigSet, out _);
        }

        for (int i = 0; i < entries.Length; i++)
            DetachEntryHandler(entries[i]);
        for (int i = 0; i < files.Length; i++)
            DropFileHandler(files[i]);
        _installed = false;
        _applying = false;
        _suppressSaveDepth = 0;
#if PROGENITOR_TESTS
        TestPushes.Clear();
        TestLogs.Clear();
        TestIsServer = false;
        TestApplyRule = null;
        TestHandshakeOkUserCount = 0;
        TestClock = null;
        TestPostDelayed = null;
#endif
    }

    internal static void CollectPinHolds(List<string> holds)
    {
        if (holds == null) return;
        try
        {
            int n = RegisteredCount;
            int locked = LockedCount;
            if (n > 0) holds.Add("synced=" + n.ToString());
            if (locked > 0) holds.Add("locked=" + locked.ToString());
            if (_installed) holds.Add("configSync");
        }
        catch (Exception e)
        {
            holds.Add("configSync threw:" + e.Message);
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        Uninstall();
        TestHandshakeOkCalls = 0;
        TestHandshakeOkUserCount = 0;
    }

    internal static int TestHandshakeOkCalls;

    /// <summary>
    /// Stand-in for production <c>HandshakeOkUsers.Count</c>. <see cref="ShouldPushOnRegister()"/>
    /// honors this the same way live code honors the connected-and-matched set.
    /// </summary>
    internal static int TestHandshakeOkUserCount;

    /// <summary>Records the config-sync step of <see cref="Handshake.NotifyHandshakeOk"/>.</summary>
    internal static void NotifyHandshakeOkForTests()
    {
        TestHandshakeOkCalls++;
        TestHandshakeOkUserCount++;
        Handshake.RestoreClientServerContext("handshake-ok");
    }
#endif

#if !PROGENITOR_TESTS
    /// <summary>
    /// Handshake hook: the join passed. Send every Synced entry of plugins this client
    /// reported at a compatible version. One call from <see cref="Handshake"/>; this
    /// method swallows exceptions so a config failure cannot undo the handshake.
    /// </summary>
    /// <param name="versions">Compat C3: the join's version judgment (settings and ranges), or null for the attribute rules.</param>
    internal static void OnHandshakeOk(World? world, Entity userEntity, IReadOnlyList<Handshake.ModRecord> answered, VersionJudgeContext? versions = null)
    {
        try
        {
            if (world == null || !world.IsCreated) return;

            int userIndex = -1;
            string name = "";
            try
            {
                if (world.EntityManager.HasComponent<User>(userEntity))
                {
                    var user = world.EntityManager.GetComponentData<User>(userEntity);
                    userIndex = user.Index;
                    name = user.CharacterName.ToString();
                }
            }
            catch (Exception e)
            {
                LogError("ConfigSync.OnHandshakeOk: reading User threw: " + e.Message);
            }

            if (userIndex >= 0)
            {
                lock (HandshakeOkUsers) HandshakeOkUsers.Add(userIndex);
            }

            // C3: judged like the join (ranges, Version matching, per-mod rules) when the handshake passed its judgment,
            // against the rows its request carried; a client that declared VersionRanges also gets the sent markers.
            bool markSent = userIndex >= 0 && (Handshake.CapsOf(userIndex) & Handshake.Capability.VersionRanges) != 0;
            var records = versions != null
                ? SnapshotFor(answered, Handshake.RequestRowsNow(), versions, markSent)
                : SnapshotFor(answered, Handshake.CopyRequired(), versions: null, markSent);
            ServerSettings.RecordPeerSnapshot(userIndex, records);
            if (records.Count == 0) return;
            SendPackets(world, userEntity, records, snapshot: true);
            int markers = 0;
            foreach (var r in records) if (ConfigSyncWire.IsSentMarker(r)) markers++;
            LogInfo("ConfigSync: sending " + (records.Count - markers).ToString() + " synced setting(s) to " + name
                + (markers > 0 ? " (" + markers.ToString() + " plugin(s) marked sent)" : ""));
        }
        catch (Exception e)
        {
            LogError("ConfigSync.OnHandshakeOk: " + e);
        }
    }

    private static void OnWorldReady(Events.WorldReady ev)
    {
        try
        {
            if (ev == null || ev.IsServer) return;
            // A surviving Client_0 after a Core-only refresh still has LastClientResult ok.
            // RestoreLocal would unlock synced settings while the player is connected.
            if (string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal)) return;
            RestoreLocal();
        }
        catch (Exception e)
        {
            LogError("ConfigSync.OnWorldReady: " + e);
        }
    }

    private static void OnPlayerDisconnected(Events.PlayerDisconnected ev)
    {
        try
        {
            lock (HandshakeOkUsers) HandshakeOkUsers.Remove(ev.User.Index);
        }
        catch (Exception e)
        {
            LogError("ConfigSync.OnPlayerDisconnected: " + e);
        }
    }

    private static unsafe void OnPacket(int eventId, byte* payload, int length)
    {
        try
        {
            _ = eventId;
            if (payload == null || length <= 0) return;
            var copy = new byte[length];
            for (int i = 0; i < length; i++) copy[i] = payload[i];
            if (!ConfigSyncWire.TryRead(
                    new ReadOnlySpan<byte>(copy),
                    out bool snapshot,
                    out bool more,
                    out var records,
                    out var error))
            {
                LogWarning("ConfigSync: dropped a packet (" + (error ?? "unreadable") + ")");
                return;
            }
            // C3: the flags tell the last packet of a join snapshot, which completes it (the default lock).
            ApplyIncomingServerPacket(Handshake.IncomingPacketEpoch, records, snapshot, more);
        }
        catch (Exception e)
        {
            LogError("ConfigSync.OnPacket: " + e);
        }
    }

    private static void SendPackets(World world, Entity userEntity, List<ConfigSyncRecord> records, bool snapshot)
    {
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        if (!world.EntityManager.HasComponent<User>(userEntity)) return;
        var user = world.EntityManager.GetComponentData<User>(userEntity);
        var packets = PackAndLog(records, snapshot);
        for (int i = 0; i < packets.Count; i++)
        {
            try
            {
                CustomEvents.SendToUser(bootstrap, user.Index, EventId, packets[i]);
            }
            catch (Exception e)
            {
                LogError("ConfigSync: SendToUser threw: " + e.Message);
            }
        }
    }

    private static int SendToConnected(List<ConfigSyncRecord> records)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return 0;
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return 0;
        var packets = PackAndLog(records, snapshot: false);
        if (packets.Count == 0) return 0;

        var em = world.EntityManager;
        var users = Query.Entities(
            em,
            includeDisabled: PushIncludesDisabledUsers,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        int sent = 0;
        HashSet<int> ok;
        lock (HandshakeOkUsers) ok = new HashSet<int>(HandshakeOkUsers);
        for (int i = 0; i < users.Count; i++)
        {
            try
            {
                var user = em.GetComponentData<User>(users[i]);
                if (!ShouldPushToUser(user.IsConnected, ok.Contains(user.Index))) continue;
                bool any = false;
                for (int p = 0; p < packets.Count; p++)
                {
                    if (CustomEvents.SendToUser(bootstrap, user.Index, EventId, packets[p]))
                        any = true;
                }
                if (any) sent++;
            }
            catch (Exception e)
            {
                LogError("ConfigSync: push to a user threw: " + e.Message);
            }
        }
        return sent;
    }
#endif

    /// <summary>
    /// The one bounds gate (R40 #7). Every path that writes a server value into a
    /// <c>ConfigEntryBase</c> — <see cref="ApplyOne"/>, <see cref="ApplyToSessionLock"/> and
    /// <see cref="ReattachSession"/> — calls this against the entry it is about to write, and
    /// records <paramref name="toml"/> as applied. Returns false when nothing may be written:
    /// the local value stays and the log names the allowed set.
    /// </summary>
    private static bool TryBoundsGate(ConfigEntryBase entry, string id, string? incoming, out string toml)
    {
        toml = incoming ?? "";
        var verdict = SyncedBounds.Check(entry, toml, out var resolved, out var bounds);
        if (verdict == SyncedBoundsVerdict.Accept) return true;

        string head =
            "ConfigSync: server value '" + toml + "' for " + id + " is outside this client's bounds"
            + (bounds.Length > 0 ? " [" + bounds + "]" : "");

        switch (verdict)
        {
            case SyncedBoundsVerdict.Clamped:
                LogWarning(head + "; clamped to '" + resolved + "'");
                toml = resolved;
                return true;
            case SyncedBoundsVerdict.ClampedToDefault:
                LogWarning(head + "; clamped to this entry's default '" + resolved + "'");
                toml = resolved;
                return true;
            default:
                // A list is an unordered set: there is no nearest legal value, so refuse rather
                // than store one nobody chose (R40 #8).
                LogWarning(head + "; keeping the local value");
                return false;
        }
    }

    private static bool ApplyOne(in ConfigSyncRecord rec)
    {
        Slot? slot = null;
        SessionLock? session = null;
        lock (Gate)
        {
            var id = new EntryKey(rec.PluginId, rec.Section, rec.Key);
            if (!Entries.TryGetValue(id, out slot))
            {
                if (!SessionLocks.TryGetValue(id, out session))
                {
                    LogIgnored(rec);
                    return false;
                }
            }
        }

        Type clientType = slot != null ? slot.Entry.SettingType : session!.Entry.SettingType;
        if (!PassesTypeGate(rec, clientType, RuleFor(rec.PluginId), out bool typeMismatch))
        {
            // C3: a value of another type is skipped and the entry is locked to its declared default for the session
            // (the server's build means something else by this key; the default is the safe reading).
            if (typeMismatch && slot != null)
                LockToDefault(slot, "schema-drift " + slot.Key + ": client type '" + TypeToken(clientType)
                    + "' does not match server '" + rec.TypeName + "'");
            else if (typeMismatch)
                LogSchemaDrift(rec, clientType);
            return false;
        }

        // C3: an enum name this client's enum does not have is a schema drift, not a parse failure: the declared default.
        string incoming = rec.Toml;
        if (clientType.IsEnum && !IsKnownEnumValue(clientType, incoming))
        {
            if (slot != null)
                LockToDefault(slot, "schema-drift " + slot.Key + ": '" + incoming + "' is not a value this client's "
                    + clientType.Name + " knows");
            else
                LogWarning("ConfigSync: schema-drift " + rec.PluginId + "/" + rec.Section + "/" + rec.Key + ": '" + incoming
                    + "' is not a value this client's " + clientType.Name + " knows; keeping the session value");
            return false;
        }

        if (session != null)
            return ApplyToSessionLock(session, rec);

        // FCFG / R40 #7. Bounds are local metadata and never on the wire, so a server value can
        // fall outside what this client's own bind accepts. The gate runs before the local capture
        // so a refusal leaves the slot exactly as it was.
        if (!TryBoundsGate(slot!.Entry, rec.PluginId + "/" + rec.Section + "/" + rec.Key, incoming, out var toml))
            return false;

        var entry = slot.Entry;
        if (!LockSlot(slot, toml, () => TrySetSerialized(entry, toml)))
        {
            LogWarning(
                "ConfigSync: could not apply " + rec.PluginId + "/" + rec.Section + "/" + rec.Key
                + " from Toml '" + toml + "'");
            return false;
        }

        return true;
    }

    /// <summary>
    /// C3: true when <paramref name="toml"/> is a value of <paramref name="enumType"/> (a member name, names joined
    /// for a flags enum, or a number), ignoring case as BepInEx does.
    /// </summary>
    internal static bool IsKnownEnumValue(Type enumType, string? toml)
    {
        string text = (toml ?? "").Trim();
        if (text.Length == 0) return false;
        try { return Enum.TryParse(enumType, text, ignoreCase: true, out _); }
        catch { return false; }
    }

    /// <summary>
    /// The lock every client apply shares (a server value, or C3's declared default): on the first lock the pre-join
    /// local is captured and the file's save is held off; <paramref name="write"/> sets the value; a failed first write
    /// releases the hold; a success marks the slot locked to <paramref name="appliedToml"/> and parks the local for Save.
    /// </summary>
    private static bool LockSlot(Slot slot, string appliedToml, Func<bool> write)
    {
        bool firstLock = !slot.Locked;
        if (firstLock)
        {
            try
            {
                slot.LocalToml = ReadToml(slot.Entry);
                slot.LocalTomlRead = true;
            }
            catch
            {
                slot.LocalToml = "";
                slot.LocalTomlRead = false;
            }
            try { slot.LocalBoxed = slot.Entry.BoxedValue; }
            catch { slot.LocalBoxed = null; }
            HoldSaveOff(slot.Entry.ConfigFile);
        }

        bool written;
        try { written = write(); }
        catch { written = false; }
        if (!written)
        {
            if (firstLock)
            {
                slot.Locked = false;
                ReleaseSaveOff(slot.Entry.ConfigFile);
            }
            return false;
        }

        slot.Locked = true;
        slot.AppliedToml = appliedToml;
        if (firstLock)
            ParkLockedForSave(slot);
        return true;
    }

    /// <summary>
    /// Undo one lock: write the pre-join local back into the live entry, read it back, and only
    /// then unpark the row so a later <c>ConfigFile.Save</c> serializes the entry again.
    ///
    /// <para><b>Write back, verify, then unpark (R52 #4, correcting FCFG4).</b> This function used
    /// to unpark first and then throw away the write's own boolean — the mirror image of the
    /// "park before you drop" ordering FCFG4 fixed one function away in
    /// <c>ConfigHelper.EvictToOrphan</c>. <see cref="UnparkLockedForSave"/> puts the live row —
    /// still holding the <em>server's</em> value — back into <c>Entries</c> and deletes the orphan
    /// holding the local, so a write-back that then did nothing left the operator's value in
    /// neither table and the <c>Save</c> that <see cref="RestoreLocal"/> runs next wrote the
    /// server's value into their <c>.cfg</c>, on every disconnect, new client world and Core
    /// uninstall. The write happens first now and is verified by re-reading the entry, because
    /// <see cref="TrySetSerialized"/> can log and continue on a parse failure; a failure keeps both
    /// the park and the lock, so the next restore retries it, and false keeps the caller from
    /// saving the file.</para>
    ///
    /// <para>A slot with nothing to restore — <c>LocalTomlRead == false</c> <em>and</em>
    /// <c>LocalBoxed == null</c>, which is both captures in <c>ApplyOne</c> having thrown — is not
    /// restored at all. There is no value to write, and unparking could only drop an orphan this
    /// slot never wrote. The lock is released so the entry stops refusing the operator's writes.
    /// This is the second consumer of the <c>LocalTomlRead</c> signal FCFG4 added for the park;
    /// it had one where it needed two.</para>
    ///
    /// <para><b>The retry ends (R53 #4, correcting FCFG5).</b> Keeping the lock on a failed
    /// write-back was right; keeping it forever was not. The hold this lock implies is per
    /// <see cref="ConfigFile"/> — <c>SaveOnConfigSet</c> is a file-level property and
    /// <see cref="ReleaseSaveOff"/> is a query over every slot on that file, not a refcount — so
    /// one stuck slot stops autosave for every entry in that <c>.cfg</c>, including entries no
    /// server ever touched. After <see cref="MaxRestoreAttempts"/> failures (or the first one on
    /// the final attempt) the slot gives up: it logs at error level with the key, the parked
    /// value and the reason, re-parks that value as an orphan so it is still what a Save writes
    /// to disk, and unlocks. A reverted setting is a smaller failure than a config file that
    /// silently stopped saving — the same trade <c>ConfigHelper.EvictToOrphan(mustEvict: true)</c>
    /// makes.</para>
    /// </summary>
    private static bool RestoreSlot(Slot slot, bool retryable)
    {
        if (!slot.Locked) return false;

        if (!slot.LocalTomlRead && slot.LocalBoxed == null)
        {
            LogWarning(
                "ConfigSync: not restoring " + slot.Key
                + "; the pre-join local value was never read, so there is nothing to write back");
            ClearLock(slot);
            return false;
        }

        bool ok = false;
        string failure = "the write-back did not take";
        try
        {
            BeginApply(slot.Entry.ConfigFile);
            try
            {
                if (slot.LocalBoxed != null)
                {
                    slot.Entry.BoxedValue = slot.LocalBoxed;
                    ok = true;
                }
                else
                {
                    ok = TrySetSerialized(slot.Entry, slot.LocalToml ?? "");
                    if (!ok) failure = "TrySetSerialized refused the parked Toml";
                }

                if (ok && slot.LocalTomlRead)
                {
                    string readBack = ReadToml(slot.Entry);
                    if (!string.Equals(readBack, slot.LocalToml ?? "", StringComparison.Ordinal))
                    {
                        ok = false;
                        failure = "the entry read back as '" + readBack
                            + "' and the parked local is '" + (slot.LocalToml ?? "") + "'";
                    }
                }
            }
            finally
            {
                EndApply(slot.Entry.ConfigFile);
            }
        }
        catch (Exception e)
        {
            ok = false;
            failure = "the write-back threw: " + e.Message;
            LogWarning("ConfigSync: restore of " + slot.Key + " threw: " + e.Message);
        }

        if (!ok)
        {
            double now = Now();
            int attempts;
            double deadline;
            lock (Gate)
            {
                if (!RestoreRetries.TryGetValue(slot.Key, out var retry) || retry == null)
                {
                    retry = new RestoreRetry { Deadline = now + RestoreRetryBudgetSeconds };
                    RestoreRetries[slot.Key] = retry;
                }
                retry.Attempts++;
                attempts = retry.Attempts;
                deadline = retry.Deadline;
            }

            bool budgetSpent = now >= deadline || attempts >= MaxRestoreAttempts;
            if (retryable && !budgetSpent)
            {
                LogWarning(
                    "ConfigSync: the local value of " + slot.Key
                    + " could not be written back (" + failure + "); it stays parked and the entry stays"
                    + " locked while a retry is posted every "
                    + RestoreRetryIntervalSeconds.ToString("0.##") + " s (attempt "
                    + attempts.ToString() + ", giving up "
                    + RestoreRetryBudgetSeconds.ToString("0.##")
                    + " s after the first failure)");
                ScheduleRestoreRetry(slot.Key);
                return false;
            }

            GiveUpRestore(slot, attempts, failure);
            return false;
        }

        UnparkLockedForSave(slot);
        ClearLock(slot);
        return true;
    }

    /// <summary>
    /// The end of the retry (R53 #4). Says out loud what was lost, makes sure the operator's own
    /// value is what a later Save writes to disk, and releases the lock — and with it this file's
    /// <c>SaveOnConfigSet</c> hold, which is the thing that must not outlive the attempt.
    /// The live entry keeps the server's value until something rebinds or the operator edits it;
    /// on disk the operator's own value stands, as an orphan row.
    /// <para>
    /// <b>R54 #8.</b> The park's fourth argument is <c>localValueWasRead</c>, and it is not
    /// decoration: <c>ConfigHelper.TryParkValueForSave</c> refuses a park that never read a value,
    /// and passes <c>emptyIsAValue: true</c> when it did — which turns off the rule that stops a
    /// blank string overwriting a good orphan. FCFG6 hard-coded <c>true</c> here from a method
    /// that has a branch where no local Toml was ever read (only a boxed capture), which is the
    /// one caller in the tree that asserted the flag instead of reporting it. It now reports
    /// <see cref="Slot.LocalTomlRead"/>, and when that is false nothing is written at all: there
    /// is no operator value to write, and a formatting of the boxed capture is not one.
    /// </para>
    /// </summary>
    private static void GiveUpRestore(Slot slot, int attempts, string failure)
    {
        bool localValueWasRead = slot.LocalTomlRead;
        string parked = localValueWasRead ? slot.LocalToml ?? "" : "";
        var file = slot.Entry?.ConfigFile;
        LogError(
            "ConfigSync: giving up on restoring " + slot.Key + " after " + attempts.ToString()
            + " attempt(s): " + failure + ". "
            + (localValueWasRead
                ? "The parked local value '" + parked
                    + "' is kept on disk as an orphan row; the live entry still holds the server value."
                : "The operator's value for this key was never read, so nothing is written to disk"
                    + " and the live entry still holds the server value.")
            + " The lock and this file's SaveOnConfigSet hold are released so '"
            + (file?.ConfigFilePath ?? "?") + "' autosaves again for every other entry in it.");

        bool parkedOk = false;
        if (file != null && slot!.Entry != null && localValueWasRead)
        {
            try
            {
                parkedOk = ConfigHelper.TryParkValueForSave(file, slot.Entry.Definition, parked, localValueWasRead);
                if (!parkedOk)
                {
                    LogError(
                        "ConfigSync: the parked local of " + slot.Key
                        + " could not be written as an orphan either; an explicit Save of '"
                        + file.ConfigFilePath + "' will persist the server value for this key.");
                }
            }
            catch (Exception e)
            {
                LogError("ConfigSync: orphaning the parked local of " + slot.Key + " threw: " + e.Message);
            }
        }

        // SAVEHOLD1: the live row still holds the server value and nothing parks it, so a page write's
        // explicit save of this file (TrySaveHeldFile) must not run for the rest of this generation.
        if (file != null && !parkedOk)
            lock (Gate) ServerValueLeftLive.Add(file);

        ClearLock(slot);
    }

    /// <summary>
    /// Post the next attempt for a key whose restore just failed (R54 #2). One post per key at a
    /// time: the flag is set under <see cref="Gate"/> before the post, and cleared by the attempt
    /// it schedules, so a second failure inside the same interval does not stack a second timer.
    /// The retry rides <c>MainThread.PostDelayed</c>, which is drained once per frame, so the
    /// budget is spent in seconds of play instead of waiting for the client to build a new world.
    /// </summary>
    private static void ScheduleRestoreRetry(EntryKey key)
    {
        lock (Gate)
        {
            if (!RestoreRetries.TryGetValue(key, out var retry) || retry == null) return;
            if (retry.Scheduled) return;
            retry.Scheduled = true;
        }

        try
        {
            PostRestoreRetry(RestoreRetryIntervalSeconds, () => RunScheduledRestoreRetry(key));
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                if (RestoreRetries.TryGetValue(key, out var retry) && retry != null)
                    retry.Scheduled = false;
            }
            LogWarning("ConfigSync: posting the restore retry for " + key + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// One posted attempt. Does nothing when the slot stopped being locked in the meantime (a
    /// successful restore, a give-up, an unregister); otherwise runs the same
    /// <see cref="RestoreSlot"/> as a world-ready restore, which either succeeds, posts the next
    /// attempt, or gives up because the budget is spent. A success saves the file, as
    /// <see cref="RestoreLocal"/> does for the restores it drives.
    /// </summary>
    private static void RunScheduledRestoreRetry(EntryKey key)
    {
        Slot? slot;
        lock (Gate)
        {
            if (!RestoreRetries.TryGetValue(key, out var retry) || retry == null) return;
            retry.Scheduled = false;
            if (!Entries.TryGetValue(key, out slot) || slot == null || !slot.Locked)
            {
                RestoreRetries.Remove(key);
                return;
            }
        }

        try
        {
            if (!RestoreSlot(slot, retryable: true)) return;
            var file = slot.Entry?.ConfigFile;
            if (file != null) TrySaveFile(file);
            LogInfo("ConfigSync: restored " + key + " on a posted retry");
        }
        catch (Exception e)
        {
            LogError("ConfigSync: the posted restore retry for " + key + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// Seconds on a monotonic clock. Tests replace it so a five-second budget is spent without
    /// a five-second test, the same seam shape <c>Handshake.TestClock</c> uses.
    /// </summary>
    private static double Now()
    {
#if PROGENITOR_TESTS
        if (TestClock != null) return TestClock();
#endif
        return System.Diagnostics.Stopwatch.GetTimestamp()
            / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// The delayed post the retry rides. Owned by <see cref="RetryOwner"/> so
    /// <see cref="CancelPendingRestoreRetries"/> can drop every outstanding one in a single call.
    /// </summary>
    private static void PostRestoreRetry(double seconds, Action action)
    {
#if PROGENITOR_TESTS
        if (TestPostDelayed != null)
        {
            TestPostDelayed(seconds, action, RetryOwner);
            return;
        }
#endif
        Dev.MainThread.PostDelayed(seconds, action, RetryOwner);
    }

    /// <summary>
    /// Drop every outstanding retry post. <see cref="Uninstall"/> calls this before its final
    /// restore: the generation is going away, so a queued attempt would either run against a
    /// cleared registry or, on the collectible path, be dropped by the reloader anyway — and the
    /// final restore gives up on everything still locked in the same pass, immediately, which is
    /// the pending work's real ending.
    /// </summary>
    private static void CancelPendingRestoreRetries()
    {
        try { Dev.MainThread.DropOwnedBy(RetryOwner, assembly: null); }
        catch (Exception e)
        {
            LogWarning("ConfigSync: dropping pending restore retries threw: " + e.Message);
        }
        lock (Gate)
        {
            foreach (var retry in RestoreRetries.Values)
            {
                if (retry != null) retry.Scheduled = false;
            }
        }
    }

    /// <summary>Drop the lock state and this file's save hold. Every exit of the restore needs it.</summary>
    private static void ClearLock(Slot slot)
    {
        slot.Locked = false;
        slot.AppliedToml = "";
        lock (Gate) RestoreRetries.Remove(slot.Key);
        ReleaseSaveOff(slot.Entry.ConfigFile);
    }

    private static void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        try
        {
            if (_applying) return;
            var changed = e?.ChangedSetting;
            if (changed == null) return;

            Slot? slot;
            lock (Gate)
            {
                if (!TryFindSlot(changed, out slot) || slot == null) return;
            }

            if (slot.Locked)
            {
                RevertLockedWrite(slot, changed);
                return;
            }

            if (!IsServerSide()) return;
            PushChange(slot);
        }
        catch (Exception ex)
        {
            LogError("ConfigSync.OnSettingChanged: " + ex);
        }
    }

    private static void PushChange(Slot slot)
    {
        var record = ReadRecord(slot);
#if PROGENITOR_TESTS
        if (TestIsServer)
        {
            TestPushes.Add(record);
            LogInfo("ConfigSync: pushed " + slot.Key + " to 0 client(s)");
            return;
        }
#endif
#if !PROGENITOR_TESTS
        if (!Plugin.IsServerProcess) return;
        int sent = SendToConnected(new List<ConfigSyncRecord> { record });
        LogInfo("ConfigSync: pushed " + slot.Key + " to " + sent.ToString() + " client(s)");
#endif
    }

    private static ConfigSyncRecord ReadRecord(Slot slot)
    {
        string toml;
        try { toml = ReadToml(slot.Entry); }
        catch { toml = slot.AppliedToml ?? ""; }
        return new ConfigSyncRecord(
            slot.Key.PluginId,
            slot.Key.Section,
            slot.Key.Key,
            toml,
            TypeToken(slot.Entry.SettingType));
    }

    internal static string TypeToken(Type? type)
    {
        if (type == null) return "";
        return type.FullName ?? type.Name;
    }

    private static string ReadToml(ConfigEntryBase entry)
    {
        try
        {
            var serialized = entry.GetSerializedValue();
            if (serialized != null) return serialized;
        }
        catch
        {
            /* fall through to the BCL formatter */
        }
        try
        {
            return ConfigSyncToml.Format(entry.BoxedValue, entry.SettingType);
        }
        catch
        {
            return entry.BoxedValue?.ToString() ?? "";
        }
    }

    private static bool TrySetSerialized(ConfigEntryBase entry, string toml)
    {
        BeginApply(entry.ConfigFile);
        try
        {
            try
            {
                SetSerializedValue(entry, toml);
                return true;
            }
            catch
            {
                if (!ConfigSyncToml.TryParse(toml, entry.SettingType, out var parsed))
                    return false;
                entry.BoxedValue = parsed;
                return true;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            EndApply(entry.ConfigFile);
        }
    }

    private static void SetSerializedValue(ConfigEntryBase entry, string toml)
    {
        var method = entry.GetType().GetMethod("SetSerializedValue", BindingFlags.Instance | BindingFlags.Public);
        if (method != null)
        {
            method.Invoke(entry, new object[] { toml });
            return;
        }
        if (!ConfigSyncToml.TryParse(toml, entry.SettingType, out var parsed))
            throw new InvalidOperationException("Toml parse failed.");
        entry.BoxedValue = parsed;
    }

    private static bool TomlEquals(object? attempted, Type type, string appliedToml)
    {
        try
        {
            var formatted = ConfigSyncToml.Format(attempted, type);
            return string.Equals(formatted, appliedToml, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindSlot(ConfigEntryBase entry, out Slot? slot)
    {
        slot = null;
        foreach (var candidate in Entries.Values)
        {
            if (ReferenceEquals(candidate.Entry, entry) || SameDefinition(candidate.Entry, entry))
            {
                slot = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool SameDefinition(ConfigEntryBase a, ConfigEntryBase b)
    {
        if (a == null || b == null) return false;
        if (!ReferenceEquals(a.ConfigFile, b.ConfigFile)) return false;
        return a.Definition.Equals(b.Definition);
    }

    /// <summary>Caller holds <see cref="Gate"/>. Returns true on the first slot for this file.</summary>
    private static bool AddFileRef(ConfigFile file)
    {
        if (file == null) return false;
        if (FileRefs.TryGetValue(file, out int n))
        {
            FileRefs[file] = n + 1;
            return false;
        }
        FileRefs[file] = 1;
        try { file.SettingChanged += SettingChangedHandler; }
        catch (Exception e)
        {
            LogWarning("ConfigSync: subscribing SettingChanged threw: " + e.Message);
        }
        PrependHandler(file);
        return true;
    }

    private static void RemoveFileRef(ConfigFile file)
    {
        if (file == null) return;
        if (!FileRefs.TryGetValue(file, out int n)) return;
        n--;
        if (n > 0)
        {
            FileRefs[file] = n;
            return;
        }
        FileRefs.Remove(file);
        DropFileHandler(file);
    }

    private static void DropFileHandler(ConfigFile file)
    {
        try { file.SettingChanged -= SettingChangedHandler; }
        catch { /* teardown */ }
    }

    /// <summary>
    /// Put our SettingChanged handler first so a locked write is reverted before other
    /// subscribers (including the ConfigEntry's own multicast) observe the refused value.
    /// That is the "SettingChanged suppressed" half of the lock without Harmony on BepInEx.
    /// </summary>
    private static void PrependHandler(ConfigFile file)
    {
        try
        {
            var field = file.GetType().GetField("SettingChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? file.GetType().GetField("m_SettingChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return;
            if (field.GetValue(file) is not Delegate current) return;
            var list = current.GetInvocationList();
            if (list.Length < 2) return;
            Delegate? combined = list[list.Length - 1];
            for (int i = 0; i < list.Length - 1; i++)
                combined = Delegate.Combine(combined, list[i]);
            field.SetValue(file, combined);
        }
        catch
        {
            /* leave subscription order as-is; revert still lands */
        }
    }

    private static void BeginApply(ConfigFile? file)
    {
        _applying = true;
        if (file == null)
        {
            _suppressSaveDepth++;
            return;
        }
        _suppressSaveDepth++;
        // Keep auto-save off for the duration of this apply. Do not restore it
        // here: lock tracking owns SaveOnConfigSet while any slot is locked.
        SetSaveOnConfigSet(file, false, out _);
    }

    private static void EndApply(ConfigFile? file)
    {
        if (_suppressSaveDepth > 0)
            _suppressSaveDepth--;
        if (_suppressSaveDepth == 0)
            _applying = false;
        // Never force SaveOnConfigSet true. ReleaseSaveOff restores the previous
        // flag when the last lock on this file drops.
        _ = file;
    }

    private static void SetSaveOnConfigSet(ConfigFile file, bool value, out bool previous)
    {
        previous = true;
        try
        {
            previous = file.SaveOnConfigSet;
            file.SaveOnConfigSet = value;
        }
        catch
        {
            /* a broken ConfigFile must not undo the lock */
        }
    }

    private static bool IsServerSide()
    {
#if PROGENITOR_TESTS
        return TestIsServer;
#else
        return Plugin.IsServerProcess;
#endif
    }

    /// <summary>
    /// A server re-register (plugin hot-reload, or a late load after join) pushes
    /// the live value to handshake-ok users. Boot binds before anyone has matched
    /// do not log a push-to-zero. Same predicate live and under tests: server-side
    /// and at least one matched user.
    /// </summary>
    internal static bool ShouldPushOnRegister(bool isServer, int handshakeOkUserCount) =>
        isServer && handshakeOkUserCount > 0;

    private static bool ShouldPushOnRegister()
    {
#if PROGENITOR_TESTS
        return ShouldPushOnRegister(TestIsServer, TestHandshakeOkUserCount);
#else
        int n;
        lock (HandshakeOkUsers) n = HandshakeOkUsers.Count;
        return ShouldPushOnRegister(Plugin.IsServerProcess, n);
#endif
    }

    private static void LogIgnored(in ConfigSyncRecord rec)
    {
        string id = rec.PluginId + "/" + rec.Section + "/" + rec.Key;
        lock (Gate)
        {
            if (!IgnoredOnce.Add(id)) return;
        }
        LogWarning("ConfigSync: ignored " + id + ": no such plugin or entry");
    }

    private static void LogSchemaDrift(in ConfigSyncRecord rec, Type? clientType)
    {
        string id = rec.PluginId + "/" + rec.Section + "/" + rec.Key;
        lock (Gate)
        {
            if (!SchemaDriftOnce.Add(id)) return;
        }
        LogWarning(
            "ConfigSync: schema-drift " + id
            + ": client type '" + TypeToken(clientType)
            + "' does not match server '" + rec.TypeName + "'");
    }

    private static void LogRefused(Slot slot)
    {
        string id = slot.Key.ToString();
        lock (Gate)
        {
            if (!RefusedOnce.Add(id)) return;
        }
        LogWarning("ConfigSync: refused write to " + id + " while locked to the server value");
    }

    private static void LogInfo(string message)
    {
#if PROGENITOR_TESTS
        lock (TestLogs) TestLogs.Add(message);
#endif
        Plugin.Logger?.LogInfo(message);
    }

    private static void LogWarning(string message)
    {
#if PROGENITOR_TESTS
        lock (TestLogs) TestLogs.Add(message);
#endif
        Plugin.Logger?.LogWarning(message);
    }

    private static void LogError(string message)
    {
#if PROGENITOR_TESTS
        lock (TestLogs) TestLogs.Add(message);
#endif
        Plugin.Logger?.LogError(message);
    }

    internal static List<byte[]> PackAndLog(IReadOnlyList<ConfigSyncRecord> records, bool snapshot)
    {
        records = WithoutMarkersOfUnsendable(records);
        var packets = ConfigSyncWire.Pack(records, snapshot, out var skipped);
        for (int i = 0; i < skipped.Count; i++)
        {
            var rec = skipped[i].Record;
            LogWarning(
                "ConfigSync: skipped " + rec.PluginId + "/" + rec.Section + "/" + rec.Key
                + ": " + skipped[i].Error);
        }
        return packets;
    }

    /// <summary>
    /// C3 (review fix): a record that cannot be packed (a value edited past the size limit after it was registered) is
    /// skipped by <see cref="ConfigSyncWire.Pack"/>; its plugin's "sent" marker must go too, or the client would lock that
    /// key to its declared default, a value neither side chose. Without the marker the client keeps its local values.
    /// </summary>
    internal static IReadOnlyList<ConfigSyncRecord> WithoutMarkersOfUnsendable(IReadOnlyList<ConfigSyncRecord> records)
    {
        HashSet<string>? cut = null;
        foreach (var rec in records)
        {
            if (ConfigSyncWire.IsSentMarker(rec)) continue;
            if (!ConfigSyncWire.TryMeasure(rec, out _, out _))
                (cut ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(rec.PluginId);
        }

        if (cut == null) return records;
        var kept = new List<ConfigSyncRecord>(records.Count);
        foreach (var rec in records)
        {
            if (ConfigSyncWire.IsSentMarker(rec) && cut.Contains(rec.PluginId))
            {
                LogWarning("ConfigSync: " + rec.PluginId + " not marked as sent (a synced value could not be sent); the client keeps its local values for the keys the snapshot lacks");
                continue;
            }

            kept.Add(rec);
        }

        return kept;
    }

    private static void ThrowIfRecordTooLarge(string pluginId, ConfigEntryBase entry)
    {
        var rec = new ConfigSyncRecord(
            pluginId,
            entry.Definition.Section,
            entry.Definition.Key,
            ReadToml(entry));
        if (ConfigSyncWire.TryMeasure(rec, out _, out var error)) return;
        throw new ArgumentException(
            "ConfigSync: " + pluginId + "/" + rec.Section + "/" + rec.Key
            + " cannot fit in a packet: " + (error ?? "too large"),
            nameof(entry));
    }

    private static bool SchemaCompatible(
        Slot slot,
        Handshake.ModRecord clientRec,
        Dictionary<string, Handshake.ModRecord> serverByGuid,
        out string? mismatchLine)
    {
        mismatchLine = null;
        string serverVersion = slot.PluginVersion ?? "";
        var rule = NetworkVersionRule.Exact;
        if (serverByGuid.TryGetValue(slot.Key.PluginId, out var serverRec))
        {
            rule = serverRec.VersionRule;
            if (!string.IsNullOrEmpty(serverRec.Version))
                serverVersion = serverRec.Version;
        }

        string clientVersion = clientRec.Version ?? "";
        if (Handshake.Compare.VersionsMatch(serverVersion, clientVersion, rule))
            return true;

        lock (Gate)
        {
            if (!VersionMismatchOnce.Add(slot.Key.PluginId))
                return false;
        }
        mismatchLine =
            "ConfigSync: ignored " + slot.Key.PluginId
            + " synced settings: client version '" + clientVersion
            + "' does not match server '" + serverVersion + "' (" + rule + ")";
        return false;
    }

    private static void RevertLockedWrite(Slot slot, ConfigEntryBase changed)
    {
        if (!TryRefuseWrite(changed, changed.BoxedValue)) return;
        BeginApply(changed.ConfigFile);
        try { TrySetSerialized(changed, slot.AppliedToml); }
        finally { EndApply(changed.ConfigFile); }
    }

    private static void OnEntrySettingChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_applying) return;
            ConfigEntryBase? entry = sender as ConfigEntryBase;
            if (entry == null && e is SettingChangedEventArgs args)
                entry = args.ChangedSetting;
            if (entry == null) return;

            Slot? slot;
            lock (Gate)
            {
                if (!TryFindSlot(entry, out slot) || slot == null || !slot.Locked)
                    return;
            }
            RevertLockedWrite(slot, entry);
        }
        catch (Exception ex)
        {
            LogError("ConfigSync.OnEntrySettingChanged: " + ex);
        }
    }

    private static void AttachEntryHandler(ConfigEntryBase entry)
    {
        if (entry == null) return;
        try
        {
            var evt = entry.GetType().GetEvent("SettingChanged", BindingFlags.Instance | BindingFlags.Public);
            if (evt == null) return;
            if (evt.EventHandlerType == typeof(EventHandler))
                evt.AddEventHandler(entry, EntrySettingChangedHandler);
            else if (evt.EventHandlerType == typeof(EventHandler<SettingChangedEventArgs>))
                evt.AddEventHandler(entry, SettingChangedHandler);
        }
        catch (Exception e)
        {
            LogWarning("ConfigSync: attaching entry SettingChanged threw: " + e.Message);
        }
    }

    private static void DetachEntryHandler(ConfigEntryBase? entry)
    {
        if (entry == null) return;
        try
        {
            var evt = entry.GetType().GetEvent("SettingChanged", BindingFlags.Instance | BindingFlags.Public);
            if (evt == null) return;
            if (evt.EventHandlerType == typeof(EventHandler))
                evt.RemoveEventHandler(entry, EntrySettingChangedHandler);
            else if (evt.EventHandlerType == typeof(EventHandler<SettingChangedEventArgs>))
                evt.RemoveEventHandler(entry, SettingChangedHandler);
        }
        catch
        {
            /* teardown */
        }
    }

    private static void HoldSaveOff(ConfigFile? file)
    {
        if (file == null) return;
        bool first;
        lock (Gate)
            first = !SaveHolds.ContainsKey(file);
        SetSaveOnConfigSet(file, false, out var previous);
        if (!first) return;
        lock (Gate)
        {
            if (!SaveHolds.ContainsKey(file))
                SaveHolds[file] = new FileSaveHold(previous);
        }
    }

    private static void ReleaseSaveOff(ConfigFile? file)
    {
        if (file == null) return;
        bool stillLocked = false;
        lock (Gate)
        {
            foreach (var slot in Entries.Values)
            {
                if (slot.Locked && ReferenceEquals(slot.Entry.ConfigFile, file))
                {
                    stillLocked = true;
                    break;
                }
            }
            if (!stillLocked)
            {
                foreach (var session in SessionLocks.Values)
                {
                    if (ReferenceEquals(session.Entry.ConfigFile, file))
                    {
                        stillLocked = true;
                        break;
                    }
                }
            }
        }
        if (stillLocked) return;
        if (_suppressSaveDepth > 0) return;

        FileSaveHold? hold;
        lock (Gate)
        {
            if (!SaveHolds.TryGetValue(file, out hold)) return;
            SaveHolds.Remove(file);
        }
        SetSaveOnConfigSet(file, hold.PreviousSaveOnConfigSet, out _);
    }

    private static void TrySaveFile(ConfigFile file)
    {
        try { file.Save(); }
        catch (Exception e)
        {
            LogWarning("ConfigSync: Save of '" + file.ConfigFilePath + "' threw: " + e.Message);
        }
    }

    /// <summary>
    /// TEXTUNDO1 follow-up (SAVEHOLD1): save a file whose <c>SaveOnConfigSet</c> this class holds off,
    /// for a write to one of its <b>unlocked</b> entries (a Personal setting that shares a cfg with a
    /// synced one). Without it such a write stayed in memory until the disconnect's restore saved the
    /// file: a crash or quit while joined lost it, and a reload of the file while joined (a hand edit)
    /// overwrote it with the old disk value. Safe only because every locked key on the file is parked
    /// as an orphan holding the pre-join local (<see cref="ParkLockedForSave"/>), so the explicit Save
    /// writes the player's own values and never a server value. Refused — and nothing written — when a
    /// locked key on the file is still a live row (a park that failed): that Save would put the server
    /// value into the player's cfg. False with <paramref name="why"/> when nothing was saved.
    /// </summary>
    internal static bool TrySaveHeldFile(ConfigFile? file, out string why)
    {
        why = "";
        if (file == null)
        {
            why = "no config file";
            return false;
        }

        // A server packet is being applied (or a restore is saving): the rows can hold the server's
        // value before they are parked, so no save may run inside that window.
        if (_applying || _suppressSaveDepth > 0)
        {
            why = "a synced value is being applied right now; the change is saved with the next write or when the session ends";
            return false;
        }

        var lockedDefinitions = new List<ConfigDefinition>();
        lock (Gate)
        {
            if (!SaveHolds.ContainsKey(file))
            {
                why = "the file is not held by a synced lock";
                return false;
            }

            if (ServerValueLeftLive.Contains(file))
            {
                why = "a synced key in this cfg could not be restored or parked and still holds the server's value, "
                    + "so a save would write that value into the cfg";
                return false;
            }

            foreach (var slot in Entries.Values)
            {
                if (slot.Locked && ReferenceEquals(slot.Entry.ConfigFile, file))
                    lockedDefinitions.Add(slot.Entry.Definition);
            }

            foreach (var session in SessionLocks.Values)
            {
                if (ReferenceEquals(session.Entry.ConfigFile, file))
                    lockedDefinitions.Add(session.Entry.Definition);
            }
        }

        for (int i = 0; i < lockedDefinitions.Count; i++)
        {
            bool live;
            try { live = file.ContainsKey(lockedDefinitions[i]); }
            catch { live = true; }
            if (!live) continue;
            why = "the synced key " + lockedDefinitions[i] + " could not be parked, so a save now would write "
                + "the server's value into this cfg; the change is saved when the session ends";
            LogWarning("ConfigSync: not saving '" + file.ConfigFilePath + "' for a local write: " + why);
            return false;
        }

        try { file.Save(); }
        catch (Exception e)
        {
            why = "Save threw: " + e.Message;
            LogWarning("ConfigSync: Save of '" + file.ConfigFilePath + "' threw: " + e.Message);
            return false;
        }

        return true;
    }

    internal static bool IsSaveHeldOff(ConfigFile file)
    {
        if (file == null) return false;
        lock (Gate) return SaveHolds.ContainsKey(file);
    }

    private static void ReattachSession(Slot slot, SessionLock session)
    {
        // The re-bound entry is a different object with its own bounds — the reload may have
        // narrowed them — so the locked value is gated against *this* entry before it is written
        // or recorded (R40 #7). A refusal leaves the slot unlocked on the value Bind produced.
        if (!TryBoundsGate(slot.Entry, slot.Key.ToString(), session.AppliedToml, out var applied))
        {
            slot.Locked = false;
            slot.AppliedToml = "";
            LogWarning(
                "ConfigSync: not re-attaching " + slot.Key
                + " after plugin reload: the locked server value is outside the new bind's bounds");
            return;
        }

        slot.Locked = true;
        slot.LocalToml = session.LocalToml;
        slot.LocalTomlRead = session.LocalTomlRead;
        slot.LocalBoxed = session.LocalBoxed;
        slot.AppliedToml = applied;
        HoldSaveOff(slot.Entry.ConfigFile);
        if (!TrySetSerialized(slot.Entry, slot.AppliedToml) && slot.LocalBoxed == null)
        {
            try { slot.Entry.BoxedValue = session.Entry.BoxedValue; }
            catch { /* keep whatever Bind left */ }
        }
        ParkLockedForSave(slot);
        LogInfo("ConfigSync: re-attached " + slot.Key + " to the locked server value after plugin reload");
    }

    /// <summary>
    /// Caller must hold <see cref="Gate"/>. True when this file already has a locked
    /// slot or a session lock, so a newly bound key must be parked before the next
    /// explicit Save.
    /// </summary>
    private static bool FileHasLock(ConfigFile? file)
    {
        if (file == null) return false;
        foreach (var slot in Entries.Values)
        {
            if (slot.Locked && ReferenceEquals(slot.Entry.ConfigFile, file))
                return true;
        }
        foreach (var session in SessionLocks.Values)
        {
            if (ReferenceEquals(session.Entry.ConfigFile, file))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A first-time bind while this file is already session-locked. Cache the bind
    /// value as the disk local and park it so an explicit Save cannot flush a later
    /// server apply. Does not set <c>Locked</c> — ApplyOne still owns the first lock.
    /// </summary>
    private static void ParkNewBindDuringSessionLock(Slot slot)
    {
        try
        {
            slot.LocalToml = ReadToml(slot.Entry);
            slot.LocalTomlRead = true;
        }
        catch
        {
            slot.LocalToml = "";
            slot.LocalTomlRead = false;
        }
        try { slot.LocalBoxed = slot.Entry.BoxedValue; }
        catch { slot.LocalBoxed = null; }
        HoldSaveOff(slot.Entry.ConfigFile);
        ParkLockedForSave(slot);
    }

    /// <summary>
    /// Handshake <see cref="NetworkVersionRule"/> for this plugin. Client apply uses
    /// Provide; SnapshotFor already walked Required. Tests may override the apply
    /// rule so they do not mutate Handshake tables.
    /// </summary>
    internal static NetworkVersionRule RuleFor(string pluginId)
    {
#if PROGENITOR_TESTS
        if (TestApplyRule.HasValue) return TestApplyRule.Value;
#endif
        if (string.IsNullOrEmpty(pluginId)) return NetworkVersionRule.Exact;
        foreach (var rec in Handshake.CopyProvided())
        {
            if (string.Equals(rec.Guid, pluginId, StringComparison.OrdinalIgnoreCase))
                return rec.VersionRule;
        }
        foreach (var rec in Handshake.CopyRequired())
        {
            if (string.Equals(rec.Guid, pluginId, StringComparison.OrdinalIgnoreCase))
                return rec.VersionRule;
        }
        return NetworkVersionRule.Exact;
    }

    /// <summary>
    /// Type gate used at apply. A present type name must match the client entry.
    /// An empty name is a v1 packet: Handshake Exact/None still apply (mixed-Core
    /// swap); Minor refuses and the caller logs the result.
    /// </summary>
    internal static bool TypeGateAllows(NetworkVersionRule rule, string? serverTypeName, Type? clientType)
    {
        if (!string.IsNullOrEmpty(serverTypeName))
            return string.Equals(serverTypeName, TypeToken(clientType), StringComparison.Ordinal);
        return rule != NetworkVersionRule.Minor;
    }

    /// <param name="typeMismatch">C3: true when the server named another type. The caller logs it (and locks a
    /// registered entry to its default); the v1-under-Minor refusal is logged here.</param>
    private static bool PassesTypeGate(in ConfigSyncRecord rec, Type clientType, NetworkVersionRule rule, out bool typeMismatch)
    {
        typeMismatch = false;
        if (TypeGateAllows(rule, rec.TypeName, clientType))
            return true;
        if (string.IsNullOrEmpty(rec.TypeName) && rule == NetworkVersionRule.Minor)
            LogTypeGateV1Minor(rec, clientType);
        else
            typeMismatch = true;
        return false;
    }

    private static void LogTypeGateV1Minor(in ConfigSyncRecord rec, Type? clientType)
    {
        string id = rec.PluginId + "/" + rec.Section + "/" + rec.Key;
        lock (Gate)
        {
            if (!SchemaDriftOnce.Add(id + ":v1")) return;
        }
        LogWarning(
            "ConfigSync: schema-drift " + id
            + ": v1 packet has no type name; type gate refused under Minor (client type '"
            + TypeToken(clientType) + "')");
    }

    /// <summary>
    /// Server push (or re-register) while the plugin is unbound. Update the session
    /// lock so re-attach writes this Toml, not the value Unregister captured. Does
    /// not Save.
    /// </summary>
    private static bool ApplyToSessionLock(SessionLock session, in ConfigSyncRecord rec)
    {
        // R40 #7: this path used to skip the bounds gate, so the re-attach then wrote an
        // out-of-range Toml into the plugin's real entry, BepInEx clamped it silently, and the
        // lock compared every later write against a value the entry did not hold.
        if (!TryBoundsGate(session.Entry, rec.PluginId + "/" + rec.Section + "/" + rec.Key, rec.Toml, out var toml))
            return false;

        if (!TrySetSerialized(session.Entry, toml))
        {
            LogWarning(
                "ConfigSync: could not apply " + rec.PluginId + "/" + rec.Section + "/" + rec.Key
                + " from Toml '" + toml + "'");
            return false;
        }
        session.AppliedToml = toml;
        return true;
    }

    private static void ParkLockedForSave(Slot slot)
    {
        if (slot.Entry == null) return;
        var file = slot.Entry.ConfigFile;
        if (file == null) return;
        try
        {
            // R50 #3: the park is refused on a value that was never read, and "never read" is this
            // flag — not an empty string. An operator who clears a synced string entry has a local
            // value of "", and that value must be parked or the next Save writes the server's text
            // into their cfg.
            if (!ConfigHelper.TryParkValueForSave(
                    file, slot.Entry.Definition, slot.LocalToml ?? "", slot.LocalTomlRead))
            {
                LogWarning(
                    "ConfigSync: could not park " + slot.Key
                    + " for Save; an explicit ConfigFile.Save() may persist the server value");
            }
        }
        catch (Exception e)
        {
            LogWarning("ConfigSync: parking " + slot.Key + " for Save threw: " + e.Message);
        }
    }

    private static void UnparkLockedForSave(Slot slot)
    {
        if (slot.Entry == null) return;
        var file = slot.Entry.ConfigFile;
        if (file == null) return;
        try { ConfigHelper.TryRestoreParkedEntry(file, slot.Entry); }
        catch (Exception e)
        {
            LogWarning("ConfigSync: unparking " + slot.Key + " for Save threw: " + e.Message);
        }
    }

    private readonly struct EntryKey : IEquatable<EntryKey>
    {
        public readonly string PluginId;
        public readonly string Section;
        public readonly string Key;

        public EntryKey(string pluginId, string section, string key)
        {
            PluginId = pluginId ?? "";
            Section = section ?? "";
            Key = key ?? "";
        }

        public bool Equals(EntryKey other) => Comparer.Equals(this, other);

        public override bool Equals(object? obj) => obj is EntryKey other && Equals(other);

        public override int GetHashCode() => Comparer.GetHashCode(this);

        public override string ToString() => PluginId + "/" + Section + "/" + Key;

        public static readonly IEqualityComparer<EntryKey> Comparer = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<EntryKey>
        {
            public bool Equals(EntryKey x, EntryKey y) =>
                string.Equals(x.PluginId, y.PluginId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Section, y.Section, StringComparison.Ordinal)
                && string.Equals(x.Key, y.Key, StringComparison.Ordinal);

            public int GetHashCode(EntryKey obj)
            {
                unchecked
                {
                    int h = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PluginId ?? "");
                    h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(obj.Section ?? "");
                    h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(obj.Key ?? "");
                    return h;
                }
            }
        }
    }

    private sealed class FileSaveHold
    {
        public FileSaveHold(bool previousSaveOnConfigSet) =>
            PreviousSaveOnConfigSet = previousSaveOnConfigSet;

        public bool PreviousSaveOnConfigSet { get; }
    }

    private sealed class Slot
    {
        public readonly EntryKey Key;
        public readonly ConfigEntryBase Entry;
        public readonly string PluginVersion;
        public bool Locked;
        public string AppliedToml = "";
        public string LocalToml = "";

        /// <summary>
        /// True when <see cref="LocalToml"/> came back from a <c>ReadToml</c> that returned
        /// (R50 #3). <c>LocalToml == ""</c> is a legitimate value — <c>GetSerializedValue()</c>
        /// answers <c>""</c> for a <c>string</c> entry the operator cleared — so the park path
        /// must be told whether a read happened rather than guessing from the string.
        /// </summary>
        public bool LocalTomlRead;

        public object? LocalBoxed;

        public Slot(EntryKey key, ConfigEntryBase entry, string pluginVersion)
        {
            Key = key;
            Entry = entry;
            PluginVersion = pluginVersion ?? "";
        }
    }

    /// <summary>
    /// Locked state kept after plugin Unregister so a re-bind in the same session
    /// re-attaches to the server value instead of restoring the local.
    /// </summary>
    private sealed class SessionLock
    {
        public readonly EntryKey Key;
        public readonly ConfigEntryBase Entry;
        public readonly string PluginVersion;
        public string AppliedToml;
        public readonly string LocalToml;

        /// <summary>Carries <c>Slot.LocalTomlRead</c> across the unregister (R50 #3).</summary>
        public readonly bool LocalTomlRead;

        public readonly object? LocalBoxed;

        private SessionLock(Slot slot)
        {
            Key = slot.Key;
            Entry = slot.Entry;
            PluginVersion = slot.PluginVersion;
            AppliedToml = slot.AppliedToml ?? "";
            LocalToml = slot.LocalToml ?? "";
            LocalTomlRead = slot.LocalTomlRead;
            LocalBoxed = slot.LocalBoxed;
        }

        public static SessionLock From(Slot slot) => new(slot);

        public Slot ToSlot()
        {
            var slot = new Slot(Key, Entry, PluginVersion)
            {
                Locked = true,
                AppliedToml = AppliedToml,
                LocalToml = LocalToml,
                LocalTomlRead = LocalTomlRead,
                LocalBoxed = LocalBoxed,
            };
            return slot;
        }
    }

    private sealed class Registration : IDisposable
    {
        private Slot? _slot;

        public Registration(Slot slot) => _slot = slot;

        public void Dispose()
        {
            var slot = System.Threading.Interlocked.Exchange(ref _slot, null);
            if (slot == null) return;
            lock (Gate)
            {
                if (!Entries.TryGetValue(slot.Key, out var current) || !ReferenceEquals(current, slot))
                    return;
            }
            Unregister(slot.Key.PluginId, slot.Key.Section, slot.Key.Key);
        }
    }
}
