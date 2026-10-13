using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
#if !PROGENITOR_TESTS
using HarmonyLib;
#endif
using Progenitor.Dev;
using Progenitor.Networking;

namespace Progenitor.Config;

/// <summary>
/// Typed bind over BepInEx <see cref="ConfigFile"/> plus a disk watch, because BepInEx only
/// reloads when asked. A player (or another process) editing the .cfg while the server is up
/// would otherwise be ignored until restart. The watch is debounced and marshaled onto the
/// game thread: <see cref="ConfigFile.Reload"/> mutates live <see cref="ConfigEntry{T}"/>
/// values that mods read from systems and chat handlers.
///
/// Core never leaves a Core (or plugin) type in BepInEx's process-lifetime ConfigFile: that
/// file is owned by Bootstrap's <c>[BepInPlugin]</c> instance and survives a collectible
/// generation (evidence 65: <c>ConfigEntry&lt;HostedServerPluginsMode&gt;</c> pinned gen 2).
/// <see cref="Bind{T}"/> on the shared file therefore refuses any T that is not a BCL
/// primitive, <see cref="string"/>, or a BCL enum; Core enums go through
/// <see cref="BindEnum{TEnum}"/> (string on disk, typed accessor). Every definition Core
/// binds on the shared file, and every <see cref="ConfigFile.SettingChanged"/> handler Core
/// adds, is recorded and dropped by <see cref="UninstallAll"/>.
/// </summary>
public static class ConfigHelper
{
    /// <summary>
    /// Raised on the main thread after at least one registered <see cref="ConfigFile"/> for a
    /// watched path reloaded from disk. Subscribers re-read <see cref="ConfigEntry{T}.Value"/>;
    /// the entries themselves have already been updated by BepInEx. When every instance's
    /// <c>Reload</c> threw it is <b>not</b> raised: Core logs an error instead, the entries still
    /// hold their pre-edit values, and the watch retries once after one more debounce interval.
    ///
    /// <para><b>Once per successful reload of a path</b>, whatever number of <see cref="ConfigFile"/>
    /// instances that path has registered (R69 #4). The argument is the newest registered
    /// instance whose <c>Reload</c> succeeded, for a subscriber that holds no
    /// <see cref="ConfigFile"/> of its own; every other instance that could load has already been
    /// reloaded when it is raised, and an instance whose <c>Reload</c> threw is never handed
    /// over. It is one static
    /// event for every watched file, so a handler filters on <see cref="ConfigFile.ConfigFilePath"/>
    /// — which is also why it cannot be raised per instance: two instances of one path carry the
    /// same path string by construction, so a subscriber cannot tell them apart and N callbacks
    /// are N repetitions of one fact.</para>
    /// </summary>
    public static event Action<ConfigFile>? Reloaded;

    /// <summary>
    /// File name of Bootstrap's process-lifetime ConfigFile (same GUID as
    /// <c>[BepInPlugin("progenitor")]</c>). Binding a Core type on this file pins
    /// the collectible generation's LoaderAllocator for the rest of the process.
    /// </summary>
    public const string SharedFileName = "progenitor.cfg";

    /// <summary>
    /// <c>[Install] CurrentUpdateTag</c> (job TAG1): an explicit Thunderstore update tag
    /// (<c>Oakveil Update</c>) that beats the game-build adapter when set; empty (the default)
    /// defers to the adapter; the literal <c>unknown</c> forces the unknown state (nothing is
    /// hidden, no stale warnings). Read through <see cref="CurrentUpdateTagValue"/> after
    /// <see cref="BindCurrentUpdateTag"/>; the resolution itself lives in
    /// <c>Install.CatalogUpdateTags.ResolveCurrent</c>.
    /// </summary>
    public const string InstallSection = "Install";
    public const string CurrentUpdateTagKey = "CurrentUpdateTag";
    public const string CurrentUpdateTagDescription =
        "Thunderstore update tag treated as the current game update on the Install tab (for example 'Oakveil Update'). "
        + "Empty: resolved from the detected game build by the V Rising adapter. "
        + "'unknown': treat the current update as unknown (no listing is hidden, no stale warning).";

    /// <summary>
    /// <c>[Install] LocalIndexPath</c> (job HOT1): a folder holding an <c>index.json</c> in the
    /// Thunderstore v1 shape whose version <c>download_url</c> and <c>icon</c> are
    /// <c>file:///…</c> URLs. Its entries are merged in front of the fetched index, so a dummy
    /// listing can be installed end to end with no network. Empty (the default) means no local
    /// source. Read through <see cref="LocalIndexPathValue"/> after
    /// <see cref="BindLocalIndexPath"/>; the merge itself lives in
    /// <c>Install.LocalIndexSource</c>.
    /// </summary>
    public const string LocalIndexPathKey = "LocalIndexPath";
    public const string LocalIndexPathDescription =
        "Folder holding a local index.json in the Thunderstore v1 shape (file:/// download and icon URLs) whose listings are merged "
        + "in front of the fetched index. Empty: no local source. Intended for testing a package before it is published.";

    /// <summary>
    /// <c>[Install] DefaultTarget</c> (job INSTALLUX1): where the Install tab writes a mod. The
    /// per-row target chooser is gone — Install opens one y/n box and installs — so this setting is
    /// the whole answer. <c>followSide</c> (the default) lets the listing's own side decide;
    /// <c>both</c> / <c>client</c> / <c>localServer</c> pin it. Every value that names the local
    /// server falls back to the client when Configuration has no validated dedicated-server folder.
    /// Read through <see cref="DefaultInstallTargetValue"/> after
    /// <see cref="BindDefaultInstallTarget"/>; the rule itself lives in
    /// <c>Install.DefaultInstallTarget.Resolve</c>.
    /// </summary>
    public const string DefaultInstallTargetKey = "DefaultTarget";
    public const string DefaultInstallTargetDescription =
        "No longer used by the Install mods tab: each row has its own Client and Server buttons, and the server picker at the "
        + "top of the tab chooses the server. Kept so existing files load unchanged. Values: 'followSide' (default), 'both', "
        + "'client' or 'server' (the old 'localServer' is read as 'server' and rewritten).";

    /// <summary>
    /// <c>[Install] MaxDownloadMB</c> (LIMITS1): the largest Thunderstore package the Install tab
    /// downloads, in MiB. Default 128; read clamped to 8–1024. A listing whose published size is over
    /// it reads "Too large to install" and is never fetched; the download itself stops at it.
    /// </summary>
    public const string MaxDownloadMbKey = "MaxDownloadMB";
    public const string MaxDownloadMbDescription =
        "Largest Thunderstore package the Install mods tab downloads, in MB (MiB). Default 128; values are clamped to 8-1024. "
        + "Listings over it show 'Too large to install'.";
    public const int MaxDownloadMbDefault = 128;
    public const int MaxDownloadMbMin = 8;
    public const int MaxDownloadMbMax = 1024;

    /// <summary>
    /// <c>[Install] TreatLocalServerAsRemote</c> (REMOTETEST1): test/advanced. When true, a connected
    /// server is always handled through the remote-install route, even when it is the configured
    /// local dedicated-server folder's server. Read through <c>Install.TreatLocalServerAsRemote</c>.
    /// </summary>
    public const string TreatLocalServerAsRemoteKey = "TreatLocalServerAsRemote";
    public const string TreatLocalServerAsRemoteDescription =
        "Test / advanced. When true, the Install mods tab handles a connected server through the remote-install route even when it is "
        + "the local dedicated server configured under [Mods] DedicatedServerPath: nothing is written to that folder or its ledger, "
        + "the Server column reads the server's own rows, admin rules apply, and install, update, uninstall and cancel go to the "
        + "server, which downloads from Thunderstore itself. Use it to exercise the remote route against a server on this PC. "
        + "Other local server folders and the not-connected case are unchanged. Default false.";

    private static ConfigEntry<bool>? _treatLocalServerAsRemote;

    /// <summary>Bind <c>[Install] TreatLocalServerAsRemote</c> once on the given file; later calls return the first entry.</summary>
    public static ConfigEntry<bool> BindTreatLocalServerAsRemote(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        lock (Gate)
        {
            if (_treatLocalServerAsRemote != null) return _treatLocalServerAsRemote;
        }

        var entry = Bind(file, InstallSection, TreatLocalServerAsRemoteKey, false, TreatLocalServerAsRemoteDescription);
        lock (Gate)
        {
            _treatLocalServerAsRemote ??= entry;
            return _treatLocalServerAsRemote;
        }
    }

    /// <summary>The bound value, or null when the key was never bound in this generation.</summary>
    public static bool? TreatLocalServerAsRemoteValue
    {
        get
        {
            ConfigEntry<bool>? entry;
            lock (Gate) entry = _treatLocalServerAsRemote;
            return entry?.Value;
        }
    }

    private static ConfigEntry<int>? _maxDownloadMb;

    /// <summary>Bind <c>[Install] MaxDownloadMB</c> once on the given file; later calls return the first entry.</summary>
    public static ConfigEntry<int> BindMaxDownloadMb(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        lock (Gate)
        {
            if (_maxDownloadMb != null) return _maxDownloadMb;
        }

        var entry = Bind(file, InstallSection, MaxDownloadMbKey, MaxDownloadMbDefault, MaxDownloadMbDescription);
        lock (Gate)
        {
            _maxDownloadMb ??= entry;
            return _maxDownloadMb;
        }
    }

    /// <summary>The bound value, or null when the key was never bound in this generation.</summary>
    public static int? MaxDownloadMbValue
    {
        get
        {
            ConfigEntry<int>? entry;
            lock (Gate) entry = _maxDownloadMb;
            return entry?.Value;
        }
    }

    private static ConfigEntry<string>? _currentUpdateTag;
    private static ConfigEntry<string>? _localIndexPath;
    private static ConfigEntry<string>? _defaultInstallTarget;

    /// <summary>Bind <c>[Install] CurrentUpdateTag</c> once on the given file; later calls return the first entry.</summary>
    public static ConfigEntry<string> BindCurrentUpdateTag(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        lock (Gate)
        {
            if (_currentUpdateTag != null) return _currentUpdateTag;
        }

        var entry = Bind(file, InstallSection, CurrentUpdateTagKey, "", CurrentUpdateTagDescription);
        lock (Gate)
        {
            _currentUpdateTag ??= entry;
            return _currentUpdateTag;
        }
    }

    /// <summary>The bound value, or null when the key was never bound in this generation.</summary>
    public static string? CurrentUpdateTagValue
    {
        get
        {
            ConfigEntry<string>? entry;
            lock (Gate) entry = _currentUpdateTag;
            return entry?.Value;
        }
    }

    /// <summary>Bind <c>[Install] LocalIndexPath</c> once on the given file; later calls return the first entry.</summary>
    public static ConfigEntry<string> BindLocalIndexPath(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        lock (Gate)
        {
            if (_localIndexPath != null) return _localIndexPath;
        }

        var entry = Bind(file, InstallSection, LocalIndexPathKey, "", LocalIndexPathDescription);
        lock (Gate)
        {
            _localIndexPath ??= entry;
            return _localIndexPath;
        }
    }

    /// <summary>The bound value, or null when the key was never bound in this generation.</summary>
    public static string? LocalIndexPathValue
    {
        get
        {
            ConfigEntry<string>? entry;
            lock (Gate) entry = _localIndexPath;
            return entry?.Value;
        }
    }

    /// <summary>Bind <c>[Install] DefaultTarget</c> once on the given file; later calls return the first entry.</summary>
    public static ConfigEntry<string> BindDefaultInstallTarget(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        lock (Gate)
        {
            if (_defaultInstallTarget != null) return _defaultInstallTarget;
        }

        var entry = Bind(
            file,
            InstallSection,
            DefaultInstallTargetKey,
            Progenitor.Install.DefaultInstallTarget.FollowSideValue,
            DefaultInstallTargetDescription);
        lock (Gate)
        {
            _defaultInstallTarget ??= entry;
            return _defaultInstallTarget;
        }
    }

    /// <summary>The bound entry, or null when the key was never bound in this generation.</summary>
    public static ConfigEntry<string>? DefaultInstallTargetEntry
    {
        get
        {
            lock (Gate) return _defaultInstallTarget;
        }
    }

    /// <summary>The bound value, or null when the key was never bound in this generation.</summary>
    public static string? DefaultInstallTargetValue
    {
        get
        {
            ConfigEntry<string>? entry;
            lock (Gate) entry = _defaultInstallTarget;
            return entry?.Value;
        }
    }

    /// <summary>
    /// How long the watch waits after a notification before reloading. Every notification
    /// restarts it (<c>WatchState.Schedule</c>), so this is also the earliest a write-back's own
    /// notification can be evaluated: the arm is written inside one debounced reload and read
    /// inside the next, never sooner than one interval later. <see cref="SelfWriteWindowSeconds"/>
    /// is expressed in these so the two cannot drift apart. Production code never changes it;
    /// tests lower it briefly through <c>SetDebounceForTests</c> so the retry facts stay short.
    /// </summary>
    internal static double DebounceSeconds { get; private set; } = 1.0;

#if PROGENITOR_TESTS
    /// <summary>Test-only: lower (or restore) <see cref="DebounceSeconds"/>.</summary>
    internal static void SetDebounceForTests(double seconds) => DebounceSeconds = seconds;
#endif

    /// <summary>
    /// How long the hash of a reload's own write-back may be used to drop a notification
    /// (FCFG9, R64 #1; FCFG10, R65 #6). BepInEx writes the file back synchronously inside
    /// <see cref="ConfigFile.Reload"/>, so the notification it raises cannot arrive later than a
    /// debounce interval or two. The window is measured from the moment the write-back happened
    /// (<c>WatchState.ArmSelfWrite</c>, taken immediately after <see cref="ConfigFile.Reload"/>
    /// returns) and is <b>three</b> debounce intervals rather than two, because the debounce is
    /// restartable and spends the first of them: the earliest the write-back's notification can
    /// be evaluated is arm + one interval, so two intervals of slack remain for notifications
    /// that restart it. That bounds the interaction rather than removing it — a stream of
    /// notifications arriving faster than the debounce can push the evaluation past the window,
    /// and the cost of that is exactly one extra reload of bytes we already hold, which is the
    /// same cheap failure the fail-open rules elsewhere in this file accept. Long enough for a
    /// notification, short enough that an operator restoring those exact bytes on purpose
    /// (Ctrl+Z and save, a <c>git checkout</c> of a config) is never mistaken for it.
    /// </summary>
    internal static double SelfWriteWindowSeconds => DebounceSeconds * 3.0;

    /// <summary>
    /// Automatic retries after a reload in which every registered instance's <c>Reload</c>
    /// threw, per external notification. One covers an editor's exclusive save or atomic rename;
    /// more would only repeat a persistent failure.
    /// </summary>
    internal const int MaxFailedReloadRetries = 1;

    private static readonly object Gate = new();
    private static readonly LeaseTable<string, WatchState> Watchers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<TrackedBind> TrackedBinds = new();
    private static readonly List<TrackedHandler> TrackedHandlers = new();

    /// <summary>
    /// The <see cref="ConfigFile"/> instances a watched path must reload, keyed by path (FCFG11).
    ///
    /// <para>The watch used to hold the one <see cref="ConfigFile"/> it was created with, and that
    /// object is not stable: BepInEx builds a <b>new</b> <see cref="ConfigFile"/> for the same
    /// <c>GUID.cfg</c> in every <c>BasePlugin</c> constructor (<c>BasePlugin.Config = new
    /// ConfigFile(Paths.ConfigPath/GUID.cfg, ...)</c>, read out of BepInEx.Unity.IL2CPP.dll), so a
    /// plugin hot reload inside one Core generation leaves the watch pointing at the previous
    /// instance. Evidence 222 is what that looks like live: every watch reload printed the old
    /// instance's values as unchanged while the disk held the operator's edit, and the plugin's own
    /// <c>ctx.Config.File.Reload()</c> read the file correctly.</para>
    ///
    /// <para>Reloading an orphaned instance is not merely useless. <see cref="ConfigFile.Reload"/>
    /// assigns through <c>ConfigEntry&lt;T&gt;.Value</c>, whose setter saves the whole file while
    /// <c>SaveOnConfigSet</c> is true, so the orphan writes <em>its</em> stale values back over the
    /// operator's edit — FCFG2's silent revert by another route. An entry that leaves this table is
    /// therefore never reloaded again.</para>
    ///
    /// <para><b>Every registered instance is reloaded</b> (FCFG12, R68 #8). FCFG11 first wrote the
    /// opposite rule beside the loop that reloads them all: a new, distinct instance for a path
    /// <em>superseded</em> every earlier one, justified as a construction guarantee ("BepInEx has one
    /// live ConfigFile per path, so an earlier object is by construction a dead plugin instance's").
    /// That is true of every caller in this repository — <c>PluginContext</c> uses
    /// <c>plugin.Config</c>, Bootstrap constructs no <see cref="ConfigFile"/>, <c>ConfigSync</c>
    /// reuses <c>entry.ConfigFile</c> — and it is not true of the public API: <see cref="Bind{T}"/>
    /// and <see cref="Watch"/> take any file, so two plugins that both bind into one shared
    /// <c>MySuite.cfg</c> (each holding its own <c>new ConfigFile(path, true)</c>) are two live
    /// owners of one path. Supersede evicted the first of them permanently, and that plugin kept
    /// <c>SaveOnConfigSet = true</c>: its next <c>Save</c> wrote its now-stale in-memory values over
    /// the operator's edit — FCFG2's silent revert by a third route, produced by the rule written to
    /// close the second. Nothing available inside this file can tell that case apart from a hot
    /// reload at registration time (an unloaded plugin's <see cref="ConfigFile"/> is still a live
    /// object until it is collected), so the rule is the one that is right for both: reload them
    /// all. A reload makes an instance <em>agree with the disk</em>, so reloading one whose owner
    /// has gone is not merely harmless, it disarms the stale write-back that instance would
    /// otherwise still be carrying.</para>
    ///
    /// <para><b>And no instance writes while it is being reloaded</b> (R69 #3). Reloading them all
    /// was not enough on its own: <see cref="ConfigFile.Reload"/> assigns only the keys the file
    /// still names while <see cref="ConfigFile.Save"/> writes <em>every</em> entry, so with the
    /// oldest registration reloading first — normally the dead plugin generation's instance — its
    /// write-back landed on disk before the live instance read, and a key the operator had deleted
    /// came back at the dead generation's value. <see cref="OnDebouncedReload"/> therefore holds
    /// <c>SaveOnConfigSet</c> off across the whole loop and restores it afterwards, so a reload
    /// reads and never writes.</para>
    ///
    /// <para>Two things take an instance out. <see cref="DropWatchedFile"/> — called for a plugin
    /// by Core when its context is torn down (FCFG13: <c>PluginContext.Attach</c> tracks
    /// <see cref="DropWatchedFileOnUnload"/> for <c>ctx.Config.File</c>, and
    /// <c>PluginReloader.SweepRegistrations</c> repeats it for a plugin whose <c>UndoAll</c> never
    /// ran), and by a plugin itself from its own teardown (the Sample still does, in
    /// <c>SampleConfig.Detach</c>; the drop is idempotent, so the second one is a no-op). And
    /// collection: the references are weak, so this table can never be the thing
    /// keeping an unloaded plugin's <see cref="ConfigFile"/> (and its entries and handlers) alive,
    /// and an instance whose owner vanished without calling anything stops being reloaded the moment
    /// nothing else holds it.</para>
    /// </summary>
    private static readonly Dictionary<string, List<WeakReference<ConfigFile>>> WatchedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bind a setting and start watching its file. Same contract as
    /// <see cref="ConfigFile.Bind{T}(string,string,T,string)"/>; the extra work is the watch,
    /// the shared-file type gate, and recording the definition so <see cref="UninstallAll"/>
    /// can drop it without wiping the on-disk value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The Config module is in <c>Diagnostics.SkipCoreModules</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T"/> is a Core or plugin type, or (on the shared file) anything
    /// other than a BCL primitive, <see cref="string"/>, or BCL enum.
    /// </exception>
    public static ConfigEntry<T> Bind<T>(ConfigFile file, string section, string key, T defaultValue, string description)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (string.IsNullOrEmpty(section)) throw new ArgumentException("Section is required.", nameof(section));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        HarmonySlotHandoff.ThrowIfModuleSkipped(HarmonySlotHandoff.ConfigModule);
        ThrowIfForbiddenBindType(typeof(T), file);

        var definition = new ConfigDefinition(section, key);
        if (IsSharedConfigFile(file))
            EvictMismatchedEntry(file, definition, typeof(T));

        var entry = file.Bind(section, key, defaultValue, description ?? "");
        if (IsSharedConfigFile(file))
        {
            // One tracked record per (file, definition), however many times the key is bound
            // in a generation. A second record would make UninstallAll evict the same
            // definition twice, and the second eviction finds the entry already gone (see
            // EvictToOrphan). ModManifest.Install runs twice per generation - once from
            // Handshake.Install, once from the first-frame kick - so this is the live case.
            lock (Gate)
            {
                if (!IsTracked(file, entry.Definition))
                    TrackedBinds.Add(new TrackedBind(file, entry.Definition));
            }
        }

        Watch(file);
        return entry;
    }

    /// <summary>
    /// Bind a Core enum as a string on the shared file so <c>ConfigEntry&lt;TEnum&gt;</c>
    /// cannot pin a collectible generation. Same section/key as a typed bind; existing cfg
    /// files that stored the enum name (BepInEx's default) still load.
    /// </summary>
    public static ConfigEntry<string> BindEnum<TEnum>(
        ConfigFile file,
        string section,
        string key,
        TEnum defaultValue,
        string description)
        where TEnum : struct, Enum
    {
        return Bind(file, section, key, defaultValue.ToString(), description ?? "");
    }

    /// <summary>
    /// Typed read of a string-bound enum. Logs a warning and returns
    /// <paramref name="defaultValue"/> when the cfg value is missing or not a named (or
    /// numeric) member of <typeparamref name="TEnum"/>.
    /// </summary>
    public static TEnum ReadEnum<TEnum>(ConfigEntry<string>? entry, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (entry == null) return defaultValue;
        return ParseEnum(entry.Value, defaultValue);
    }

    /// <summary>
    /// Parse <paramref name="value"/> as <typeparamref name="TEnum"/>. Bad values log a
    /// warning and return <paramref name="defaultValue"/> so a typo in the cfg cannot throw
    /// from a property getter on the game thread.
    /// </summary>
    public static TEnum ParseEnum<TEnum>(string? value, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out TEnum parsed)
            && Enum.IsDefined(typeof(TEnum), parsed))
            return parsed;

        Plugin.Logger?.LogWarning(
            "ConfigHelper: '" + (value ?? "") + "' is not a valid " + typeof(TEnum).Name
            + "; using " + defaultValue + ".");
        return defaultValue;
    }

    /// <summary>
    /// Subscribe to <see cref="ConfigFile.SettingChanged"/> and record the handler so
    /// <see cref="UninstallAll"/> can unsubscribe it. Core must not add handlers directly:
    /// the multicast lives on Bootstrap's process-lifetime ConfigFile.
    /// </summary>
    public static void AddSettingChanged(ConfigFile file, EventHandler<SettingChangedEventArgs> handler)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        HarmonySlotHandoff.ThrowIfModuleSkipped(HarmonySlotHandoff.ConfigModule);

        file.SettingChanged += handler;
        lock (Gate)
            TrackedHandlers.Add(new TrackedHandler(file, handler));
    }

    /// <summary>
    /// Watch <paramref name="file"/> for external edits. Dispose to drop this caller's lease.
    /// A second watch on the same path shares the <see cref="FileSystemWatcher"/> but returns a
    /// distinct <see cref="IDisposable"/>; the watcher stops only when the last lease is disposed.
    /// Safe to call before <see cref="Install"/>; <see cref="UninstallAll"/> drops every watch.
    /// <para>
    /// It also enrolls <paramref name="file"/> as an instance the watch reloads for that path
    /// (see <see cref="WatchedFiles"/>), alongside any earlier instance that is still registered.
    /// The lease and the enrollment are separate: disposing the lease only gives up this caller's
    /// claim on the <see cref="FileSystemWatcher"/>, and a plugin whose <see cref="ConfigFile"/> is
    /// going away with it should call <see cref="DropWatchedFile"/> (FCFG11, FCFG12).
    /// </para>
    /// </summary>
    public static IDisposable Watch(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        var path = file.ConfigFilePath;
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("ConfigFile has no path to watch.", nameof(file));

        // Register before the lease, and on every call rather than only on the first: the second
        // caller for a path is the one that matters (FCFG11). It is normally a later plugin instance
        // with a new ConfigFile for the same GUID.cfg, and if the watch keeps reloading only the
        // object it was created with, the operator's edit is read into a dead instance and written
        // back stale.
        string note;
        lock (Gate)
            note = RegisterWatchedFile(path, file);

        if (note.Length != 0)
            Plugin.Logger?.LogInfo(note);

        lock (Gate)
        {
            try
            {
                return Watchers.Acquire(
                    path,
                    () =>
                    {
                        var state = new WatchState(path);
                        try
                        {
                            state.Start();
                        }
                        catch
                        {
                            state.Stop();
                            throw;
                        }
                        return state;
                    },
                    state => state.Stop());
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ConfigHelper: could not watch '{path}': {e}");
                return ClosedWatch.Instance;
            }
        }
    }

    /// <summary>
    /// Stop reloading <paramref name="file"/>. Call it from a plugin's teardown, before the
    /// instance that owns the <see cref="ConfigFile"/> goes away: from that moment the watch
    /// reloads whatever other live instance the path has (normally the next plugin instance, once
    /// it binds) and never this one. Returns true when a registration was actually dropped.
    ///
    /// <para>A <c>ProgenitorPlugin</c> no longer has to call it: Core drops
    /// <c>ctx.Config.File</c> when the context is torn down (FCFG13, see
    /// <see cref="DropWatchedFileOnUnload"/>). Calling it as well is free — a second drop of the
    /// same instance finds nothing and returns false — and it is still the way to drop a
    /// <see cref="ConfigFile"/> Core does not know about (one a plugin constructed itself, a shared
    /// suite file) or to drop one earlier than unload.</para>
    ///
    /// <para>Leaving it registered is not a leak of memory — the registry holds weak references —
    /// and since FCFG12 it is not a data-loss window either: the reload loads <em>every</em>
    /// registered instance, so an undropped dead one is brought into agreement with the disk rather
    /// than left holding pre-edit values for its next <c>Save</c> to write back — and since R69 #3
    /// it cannot write anything <em>during</em> the reload either, because <c>SaveOnConfigSet</c> is
    /// held off across it. What it still
    /// costs is one pointless <c>Reload</c> (and the <c>SettingChanged</c> handlers that unloaded
    /// instance still carries, run once per watch event) for as long as something keeps the object
    /// alive. Dropping it makes the window reload nothing at all, which is why the Sample does it
    /// and why <c>docs/modules.md</c> asks plugins to.</para>
    ///
    /// <para>The <see cref="FileSystemWatcher"/> is not stopped: <see cref="Bind{T}"/> takes a
    /// lease it never releases, and <see cref="UninstallAll"/> owns those.</para>
    /// </summary>
    public static bool DropWatchedFile(ConfigFile file)
    {
        if (file == null) return false;
        string path;
        try { path = file.ConfigFilePath; }
        catch { return false; }
        if (string.IsNullOrEmpty(path)) return false;

        lock (Gate)
        {
            if (!WatchedFiles.TryGetValue(path, out var list)) return false;
            bool dropped = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].TryGetTarget(out var registered))
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (!ReferenceEquals(registered, file)) continue;
                list.RemoveAt(i);
                dropped = true;
            }
            if (list.Count == 0) WatchedFiles.Remove(path);
            return dropped;
        }
    }

    /// <summary>
    /// A handle whose <see cref="IDisposable.Dispose"/> is <see cref="DropWatchedFile"/> for
    /// <paramref name="file"/>, for the teardown of something that owns a <see cref="ConfigFile"/>
    /// rather than for a plugin author's own call (FCFG13). <c>PluginContext.Attach</c> tracks one
    /// for <c>ctx.Config.File</c>, so a plugin's registration leaves this table when its context is
    /// torn down whether or not the plugin remembered to drop it — which is the missing piece
    /// FCFG12 named: <see cref="DropWatchedFile"/> was the only explicit way out, and it was
    /// entirely the plugin's to call.
    ///
    /// <para>The drop is guarded here rather than at the call site, because that call site is a
    /// disposable bag running somebody's unload: a throw out of it is reported against the plugin
    /// and tells the reader nothing about the config watch. Disposing twice drops once; a null
    /// file is a handle that does nothing, so a caller does not have to test for one.</para>
    /// </summary>
    internal static IDisposable DropWatchedFileOnUnload(ConfigFile? file)
        => file == null ? ClosedWatch.Instance : new WatchedFileDrop(file);

    /// <summary>
    /// Enroll <paramref name="file"/> as a live instance for <paramref name="path"/>, pruning the
    /// collected ones. Caller holds <see cref="Gate"/>. Nothing is evicted for being older
    /// (FCFG12): see <see cref="WatchedFiles"/> for why an earlier instance cannot be assumed dead.
    /// Returns the log line for a path that has just gained a second instance, or the empty string;
    /// the caller logs it outside the lock.
    /// </summary>
    private static string RegisterWatchedFile(string path, ConfigFile file)
    {
        if (!WatchedFiles.TryGetValue(path, out var list))
        {
            list = new List<WeakReference<ConfigFile>>(1);
            WatchedFiles[path] = list;
        }

        bool already = false;
        var others = new List<ConfigFile>(1);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].TryGetTarget(out var registered))
            {
                list.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(registered, file))
            {
                // The same instance registering again — Bind calls Watch on every key, and
                // ModManifest.Install runs twice per generation. One entry, however many calls.
                already = true;
                continue;
            }
            others.Add(registered);
        }

        // Already registered: say nothing. The line below is about a path *gaining* an instance,
        // and Bind calls Watch once per key.
        if (already) return "";

        list.Add(new WeakReference<ConfigFile>(file));
        if (others.Count == 0) return "";

        return MultipleInstancesLog(path, others, file);
    }

    /// <summary>
    /// The line printed when a path gains a second (or third) registered
    /// <see cref="ConfigFile"/>. It names every instance rather than only the count, because the
    /// two situations that produce it need different answers from whoever reads it and only the
    /// author can tell them apart: a plugin hot reload whose old instance was never dropped, or two
    /// live owners of one file.
    /// </summary>
    internal static string MultipleInstancesLog(
        string path,
        IReadOnlyList<ConfigFile> existing,
        ConfigFile added)
    {
        var names = new List<string>(existing.Count + 1);
        for (int i = existing.Count - 1; i >= 0; i--) names.Add(InstanceName(existing[i]));
        names.Add(InstanceName(added) + " (new)");

        return "ConfigHelper: " + path + " now has " + (existing.Count + 1)
            + " registered ConfigFile instances [" + string.Join(", ", names)
            + "]. BepInEx builds one ConfigFile per plugin instance, so this is either a plugin hot "
            + "reload whose old instance was never dropped (call ConfigHelper.DropWatchedFile(file) "
            + "in teardown, as the Sample does) or two live owners of one file. Every registered "
            + "instance is reloaded, with SaveOnConfigSet held off for the whole reload, so none of "
            + "them can write stale values back over an operator's edit; an instance nobody drops "
            + "stops being reloaded once it is collected (FCFG12, R68 #8; R69 #3).";
    }

    /// <summary>
    /// A stable, allocation-cheap name for one <see cref="ConfigFile"/> object: identity hash plus
    /// how many keys it holds, which is what tells two instances of the same path apart in a log.
    /// </summary>
    private static string InstanceName(ConfigFile file)
    {
        int keys;
        try { keys = GetEntries(file)?.Count ?? -1; }
        catch { keys = -1; }
        var id = "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(file)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return keys < 0 ? id : id + " (" + keys + " key(s))";
    }

    /// <summary>
    /// The live instances for <paramref name="path"/>, oldest registration first, with collected
    /// ones pruned. Normally exactly one — the ordinary case is one plugin instance holding the one
    /// <see cref="ConfigFile"/> BepInEx made for its <c>GUID.cfg</c> — but more than one is a
    /// supported state, not an impossible one (FCFG12): a hot reload whose old instance is not yet
    /// collected, or two live owners of one file. Empty means every owner has gone and the reload
    /// has nothing to load into.
    /// </summary>
    private static List<ConfigFile> LiveWatchedFiles(string path)
    {
        var live = new List<ConfigFile>(1);
        lock (Gate)
        {
            if (!WatchedFiles.TryGetValue(path, out var list)) return live;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].TryGetTarget(out _)) list.RemoveAt(i);
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].TryGetTarget(out var file)) live.Add(file);
            }
            if (list.Count == 0) WatchedFiles.Remove(path);
        }
        return live;
    }

    /// <summary>
    /// True when <paramref name="file"/> is Bootstrap's process-lifetime ConfigFile
    /// (<see cref="SharedFileName"/>). Plugin <c>GUID.cfg</c> files are not shared.
    /// </summary>
    public static bool IsSharedConfigFile(ConfigFile? file)
    {
        if (file == null) return false;
        var path = file.ConfigFilePath;
        if (string.IsNullOrEmpty(path)) return false;
        return string.Equals(Path.GetFileName(path), SharedFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Marks the helper installed. There is no Bootstrap Harmony site and no game method to
    /// patch: the watch is a <see cref="FileSystemWatcher"/>. Per-site ownership is N/A.
    /// The signature matches the other Core modules so <c>InstallModules</c> can call them
    /// uniformly.
    /// </summary>
    public static void Install(
#if !PROGENITOR_TESTS
        Harmony? harmony
#else
        object? harmony
#endif
        )
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.ConfigModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.ConfigModule));
            return;
        }

        _ = harmony;
        _ = HarmonySlotHandoff.SkipCoreHarmony;
        ConfigSync.Install();
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (FileSystemWatcher callbacks, Reloaded multicast, shared-file entries/handlers).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            lock (Gate)
            {
                if (Watchers.KeyCount > 0) holds.Add("fileWatchers=" + Watchers.KeyCount);
                if (WatchedFiles.Count > 0) holds.Add("watchedFiles=" + WatchedFiles.Count);
                if (TrackedBinds.Count > 0) holds.Add("sharedBinds=" + TrackedBinds.Count);
                if (TrackedHandlers.Count > 0) holds.Add("settingChanged=" + TrackedHandlers.Count);
            }
            int reloaded = Reloaded?.GetInvocationList().Length ?? 0;
            if (reloaded > 0) holds.Add("Reloaded=" + reloaded);
            ConfigSync.CollectPinHolds(holds);
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.ConfigModule, holds);
    }

    /// <summary>
    /// Stops every file watch. Shared-file entries and SettingChanged handlers are dropped by
    /// <see cref="UninstallAll"/>; this method is the same call so existing Uninstall sites
    /// stay correct.
    /// </summary>
    public static void Uninstall() => UninstallAll();

    /// <summary>
    /// Drop everything Core attached to process-lifetime config objects: recorded
    /// <see cref="ConfigFile.SettingChanged"/> handlers, recorded shared-file entries (moved
    /// to BepInEx's orphaned-entry table so a later Save still writes the value), file
    /// watches, and <see cref="Reloaded"/>. Idempotent. Called from Core Uninstall before
    /// the modules' own uninstall so Hosted / ModSave statics are not the last root of a
    /// Core-typed <see cref="ConfigEntry{T}"/>.
    ///
    /// BepInEx <see cref="ConfigFile.Remove"/> only removes the definition from the in-memory
    /// <c>Entries</c> dictionary. It does not copy the serialized value into
    /// <c>OrphanedEntries</c>, does not <c>Save</c>, and does not unsubscribe
    /// <c>SettingChanged</c> (verified from BepInEx.Core.dll analyze, not live). A later
    /// <c>Save</c> would therefore drop the key from disk unless we re-add it as an orphan
    /// ourselves. <c>Bind</c> then consumes the orphan on the next generation.
    /// </summary>
    public static void UninstallAll()
    {
        try { ConfigSync.Uninstall(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ConfigHelper: ConfigSync.Uninstall threw: " + e.Message);
        }

        TrackedHandler[] handlers;
        TrackedBind[] binds;
        lock (Gate)
        {
            handlers = TrackedHandlers.ToArray();
            TrackedHandlers.Clear();
            binds = TrackedBinds.ToArray();
            TrackedBinds.Clear();
            _currentUpdateTag = null;
            _defaultInstallTarget = null;
            Reloaded = null;
            Watchers.Drain();
            WatchedFiles.Clear();
        }

        for (int i = 0; i < handlers.Length; i++)
        {
            try { handlers[i].File.SettingChanged -= handlers[i].Handler; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ConfigHelper: unsubscribing SettingChanged threw: " + e.Message);
            }
        }

        var seen = new HashSet<ConfigFile>();
        for (int i = 0; i < binds.Length; i++)
        {
            // mustEvict: false — on this path a row that could not be parked is better kept.
            try { EvictToOrphan(binds[i].File, binds[i].Definition, mustEvict: false); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ConfigHelper: evicting " + binds[i].Definition + " threw: " + e.Message);
            }

            if (seen.Add(binds[i].File))
            {
                try { DropSettingChangedTargetingRemovedEntries(binds[i].File); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("ConfigHelper: stripping SettingChanged threw: " + e.Message);
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="definition"/> on <paramref name="file"/> already has a
    /// tracked bind. Caller holds <see cref="Gate"/>.
    /// </summary>
    private static bool IsTracked(ConfigFile file, ConfigDefinition definition)
    {
        for (int i = 0; i < TrackedBinds.Count; i++)
        {
            if (ReferenceEquals(TrackedBinds[i].File, file)
                && TrackedBinds[i].Definition.Equals(definition))
                return true;
        }
        return false;
    }

    internal static void ThrowIfForbiddenBindType(Type type, ConfigFile file)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (IsAllowedBclBindType(type)) return;
        if (!IsSharedConfigFile(file) && type.Assembly != typeof(ConfigHelper).Assembly)
            return;

        throw new ArgumentException(
            "ConfigHelper.Bind refuses type '" + (type.FullName ?? type.Name)
            + "' from assembly '" + type.Assembly.GetName().Name
            + "' on the shared ConfigFile. Only BCL primitives, string, and BCL enums are allowed "
            + "so ConfigEntry<T> cannot pin a collectible Core generation. Bind Core enums as "
            + "string (ConfigHelper.BindEnum) and parse them with ReadEnum.",
            nameof(type));
    }

    internal static bool IsAllowedBclBindType(Type type)
    {
        if (type == typeof(string) || type == typeof(decimal)) return true;
        if (type.IsPrimitive) return true;
        if (!type.IsEnum) return false;
        var name = type.Assembly.GetName().Name;
        return name is "System.Private.CoreLib" or "mscorlib" or "netstandard" or "System.Runtime";
    }

    private static void EvictMismatchedEntry(ConfigFile file, ConfigDefinition definition, Type expectedType)
    {
        try
        {
            if (!file.ContainsKey(definition)) return;
            var existing = file[definition];
            if (existing.SettingType == expectedType) return;
            Plugin.Logger?.LogWarning(
                "ConfigHelper: replacing " + definition + " of type "
                + existing.SettingType.FullName + " with " + expectedType.FullName
                + " (Core enum-as-string upgrade; on-disk value kept as an orphan).");
            // mustEvict: this caller's next line is file.Bind<T>, and BepInEx's Bind returns the
            // *existing* entry for a definition it already has, so a row of the wrong SettingType
            // that survives here makes Bind<T> throw InvalidCastException out of ConfigHelper.Bind
            // — the try/catch above wraps the eviction, not the bind (R52 #6).
            EvictToOrphan(file, definition, mustEvict: true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ConfigHelper: mismatched-entry check for " + definition + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// Park <paramref name="definition"/>'s value as an orphan and drop the live row.
    ///
    /// <para><paramref name="mustEvict"/> (R52 #6) is how the two callers say what a failed park
    /// means to them. For <see cref="UninstallAll"/> a failed park is a reason to keep the row:
    /// the value is then still in <c>Entries</c> and the next <c>Save</c> writes it. For
    /// <see cref="EvictMismatchedEntry"/> it is not, because that eviction exists only so the
    /// <c>Bind&lt;T&gt;</c> on the next line does not meet a row of a different
    /// <c>SettingType</c>; keeping it turns FCFG4's graceful degradation into an
    /// <c>InvalidCastException</c> out of <see cref="Bind{T}"/>, which is a much worse failure than
    /// one reverted value. So the mismatch path logs and drops anyway.</para>
    /// </summary>
    private static void EvictToOrphan(ConfigFile file, ConfigDefinition definition, bool mustEvict)
    {
        ConfigEntryBase? entry = null;
        try
        {
            if (file.ContainsKey(definition))
                entry = file[definition];
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ConfigHelper: reading " + definition + " before Remove threw: " + e.Message);
        }

        string serialized = "";
        bool read = false;
        if (entry != null)
        {
            try
            {
                serialized = entry.GetSerializedValue() ?? "";
                read = true;
            }
            catch
            {
                try
                {
                    serialized = entry.BoxedValue?.ToString() ?? "";
                    read = true;
                }
                catch { /* nothing was read; leave the existing orphan alone */ }
            }
        }

        // Park before dropping the live row (R50 #7). Only park a value we actually read off a
        // live entry: a repeat eviction of a definition that is already gone would otherwise
        // write serialized == "" over the good orphan; the next generation's Bind consumes the
        // empty string, BepInEx logs "could not be parsed ... Value: ", the entry falls back to
        // its default, and SaveOnConfigSet writes that default over the operator's value on disk.
        // R49 #3: the guard is on the read, not on the entry. A live entry whose
        // GetSerializedValue() and BoxedValue both throw leaves serialized == "" with
        // nothing behind it, which is the same empty write by another route.
        //
        // R50 #7: the order is the one TryAddOrphan's own contract asks for, and the return value
        // is honored. TryAddOrphan returns false when OrphanedEntries could not be found or the
        // write threw; removing the live row after that leaves the operator's value in neither
        // table, and the next Save writes the freshly bound default — FCFG2's silent revert by
        // another route. When nothing was read there is nothing to lose in the orphan table, so
        // the eviction (whose whole point is replacing a mismatched-type row) still goes ahead.
        //
        // R52 #6: "keep the row" is right only for a caller that can live with the row. The
        // mismatched-type path cannot — see the mustEvict note on this method.
        if (read && !TryAddOrphan(file, definition, serialized))
        {
            if (!mustEvict)
            {
                Plugin.Logger?.LogWarning(
                    "ConfigHelper: keeping the live row for " + definition
                    + "; its value could not be parked, and dropping it would lose the value from disk.");
                return;
            }

            Plugin.Logger?.LogWarning(
                "ConfigHelper: dropping the live row for " + definition
                + " even though its value could not be parked; the row has the wrong type and Bind would throw on it.");
        }

        try { file.Remove(definition); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ConfigHelper: ConfigFile.Remove(" + definition + ") threw: " + e.Message);
        }

        if (entry != null)
            DropSettingChangedTargeting(file, entry);
    }

    /// <summary>
    /// Copy <paramref name="serialized"/> into <c>OrphanedEntries</c> then drop the
    /// live row so a later <see cref="ConfigFile.Save"/> writes that string instead
    /// of <c>BoxedValue</c>. Does not drop <c>SettingChanged</c>: the
    /// <see cref="ConfigEntryBase"/> stays live (ConfigSync lock). Orphan first so a
    /// failed Remove cannot drop the key from both tables. If
    /// <see cref="ConfigFile.Remove"/> throws or leaves the row,
    /// the Entries dictionary is dropped directly — Save must still write the
    /// parked local. Returns false only when the live row remains (Save is not safe).
    /// <para>
    /// R49 #3 / R50 #3: parks only a value that was actually read — and "was it read" is a
    /// signal the caller passes (<paramref name="localValueWasRead"/>), never the shape of the
    /// string. FCFG3 inferred it from <c>serialized.Length == 0</c> on the premise that
    /// <c>ConfigSync</c>'s <c>LocalToml</c> is <c>""</c> only when <c>ReadToml</c> threw. That
    /// premise is false: <c>ReadToml</c> is total (it catches around <c>GetSerializedValue</c>,
    /// falls through to <c>ConfigSyncToml.Format</c>, catches again, and ends at
    /// <c>BoxedValue?.ToString() ?? ""</c>), and <c>GetSerializedValue()</c> returns <c>""</c> as
    /// the normal, successful answer for a <c>string</c> entry whose value is empty. So the
    /// string-shaped test fired on the one case that matters — an operator who deliberately
    /// clears a synced <c>string</c> (a MOTD, a chat prefix) — leaving the live row holding the
    /// server's text, which any <c>ConfigFile.Save()</c> then wrote into their <c>.cfg</c>: the
    /// exact loss the park exists to prevent, and the reverse of what the guard intended.
    /// </para>
    /// <para>
    /// With a real signal, an empty value that <em>was</em> read parks like any other (it is the
    /// operator's value), and a value that was not read is refused: parking it would drop the live
    /// row and write the key blank. A refusal returns false, which the caller logs as "an explicit
    /// ConfigFile.Save() may persist the server value" — the locked value on disk rather than a
    /// blank key.
    /// </para>
    /// </summary>
    internal static bool TryParkValueForSave(
        ConfigFile file,
        ConfigDefinition definition,
        string serialized,
        bool localValueWasRead)
    {
        if (file == null || definition == null) return false;
        if (!localValueWasRead)
        {
            Plugin.Logger?.LogWarning(
                "ConfigHelper: refusing to park " + definition
                + "; the local value was never read, and parking it would write the key blank");
            return false;
        }
        try
        {
            var orphans = GetOrphanedEntries(file);
            if (orphans == null) return false;
            // emptyIsAValue: this park knows the empty string is the operator's own value, so it
            // must not be turned away by TryAddOrphan's never-blank-a-good-orphan rule.
            if (!TryAddOrphan(file, definition, serialized, emptyIsAValue: true)) return false;
            try { file.Remove(definition); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "ConfigHelper: ConfigFile.Remove(" + definition + ") while parking for Save threw: "
                    + e.Message);
            }

            if (EntryStillLive(file, definition))
                TryDropLiveEntry(file, definition);

            if (EntryStillLive(file, definition))
            {
                Plugin.Logger?.LogWarning(
                    "ConfigHelper: " + definition
                    + " is still in ConfigFile.Entries after parking; an explicit Save may persist the live value");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ConfigHelper: parking " + definition + " for Save threw: " + e.Message);
            return false;
        }
    }

    private static bool EntryStillLive(ConfigFile file, ConfigDefinition definition)
    {
        try { return file.ContainsKey(definition); }
        catch { return true; }
    }

    private static void TryDropLiveEntry(ConfigFile file, ConfigDefinition definition)
    {
        try
        {
            var entries = GetEntries(file);
            if (entries == null) return;
            if (entries.Contains(definition))
                entries.Remove(definition);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "ConfigHelper: dropping " + definition + " from Entries after Remove failed threw: "
                + e.Message);
        }
    }

    /// <summary>
    /// Put <paramref name="entry"/> back in <c>Entries</c> and drop its orphan so
    /// Save serializes the live <c>BoxedValue</c> again. Inverse of
    /// <see cref="TryParkValueForSave"/>.
    /// </summary>
    internal static bool TryRestoreParkedEntry(ConfigFile file, ConfigEntryBase entry)
    {
        if (file == null || entry == null) return false;
        try
        {
            var entries = GetEntries(file);
            if (entries == null) return false;
            entries[entry.Definition] = entry;
            var orphans = GetOrphanedEntries(file);
            if (orphans != null && orphans.Contains(entry.Definition))
                orphans.Remove(entry.Definition);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "ConfigHelper: restoring parked " + entry.Definition + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Park <paramref name="serialized"/> as the on-disk value for <paramref name="definition"/>.
    /// R49 #3: the "never blank a good orphan" rule lives here rather than in each caller —
    /// an empty string never overwrites an orphan that already holds a value, because the
    /// next generation's Bind cannot parse an empty string, falls back to its default, and
    /// SaveOnConfigSet then writes that default over the operator's value on disk. Returns
    /// true when the orphan table holds <paramref name="serialized"/> (or already held a
    /// better value); false when the write could not happen, in which case a caller that is
    /// about to drop the live row must not drop it.
    /// <para>
    /// <paramref name="emptyIsAValue"/> (R50 #3) turns the rule off for a caller that knows the
    /// empty string is the value rather than the absence of one — <see cref="TryParkValueForSave"/>
    /// with a local value the caller confirms it read. The eviction path never passes it: there,
    /// an empty string is only ever a read that produced nothing.
    /// </para>
    /// </summary>
    internal static bool TryAddOrphan(
        ConfigFile file,
        ConfigDefinition definition,
        string serialized,
        bool emptyIsAValue = false)
    {
        try
        {
            var orphans = GetOrphanedEntries(file);
            if (orphans == null)
            {
                Plugin.Logger?.LogWarning(
                    "ConfigHelper: OrphanedEntries was not found on ConfigFile; "
                    + definition + " may drop from disk on the next Save.");
                return false;
            }

            var value = serialized ?? "";
            if (!emptyIsAValue && value.Length == 0 && !string.IsNullOrEmpty(orphans[definition] as string))
            {
                // Info rather than debug: reaching here means a caller had nothing to park and
                // very nearly blanked the operator's value, which is the diagnosis FCFG2 spent a
                // live server swap on. It is also rare — it needs a failed read over a key that
                // is already parked — so it costs nothing to see it.
                Plugin.Logger?.LogInfo(
                    "ConfigHelper: keeping the parked value for " + definition
                    + " rather than overwriting it with an empty string.");
                return true;
            }

            orphans[definition] = value;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ConfigHelper: writing orphan " + definition + " threw: " + e.Message);
            return false;
        }
    }

    internal static IDictionary? GetOrphanedEntries(ConfigFile file)
    {
        var type = file.GetType();
        var prop = type.GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (prop != null) return prop.GetValue(file) as IDictionary;
        var field = type.GetField("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? type.GetField("<OrphanedEntries>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(file) as IDictionary;
    }

    internal static IDictionary? GetEntries(ConfigFile file)
    {
        if (file == null) return null;
        var type = file.GetType();
        var prop = type.GetProperty("Entries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (prop != null) return prop.GetValue(file) as IDictionary;
        var field = type.GetField("Entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? type.GetField("<Entries>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(file) as IDictionary;
    }

    private static FieldInfo? GetSettingChangedField(Type type)
    {
        return type.GetField("SettingChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? type.GetField("m_SettingChanged", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static void DropSettingChangedTargeting(ConfigFile file, ConfigEntryBase entry)
    {
        var field = GetSettingChangedField(file.GetType());
        if (field == null) return;
        if (field.GetValue(file) is not Delegate current) return;
        foreach (var d in current.GetInvocationList())
        {
            if (!HandlerTargetsEntry(d, entry)) continue;
            try { file.SettingChanged -= (EventHandler<SettingChangedEventArgs>)d; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ConfigHelper: dropping SettingChanged for " + entry.Definition + " threw: " + e.Message);
            }
        }
    }

    private static void DropSettingChangedTargetingRemovedEntries(ConfigFile file)
    {
        var field = GetSettingChangedField(file.GetType());
        if (field == null) return;
        if (field.GetValue(file) is not Delegate current) return;
        foreach (var d in current.GetInvocationList())
        {
            if (d.Target is not ConfigEntryBase entry) continue;
            bool stillBound = false;
            try { stillBound = file.ContainsKey(entry.Definition); }
            catch { /* treat as leftover */ }
            if (stillBound) continue;
            try { file.SettingChanged -= (EventHandler<SettingChangedEventArgs>)d; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ConfigHelper: dropping leftover SettingChanged threw: " + e.Message);
            }
        }
    }

    private static bool HandlerTargetsEntry(Delegate handler, ConfigEntryBase entry)
    {
        if (ReferenceEquals(handler.Target, entry)) return true;
        if (handler.Target is ConfigEntryBase other && other.Definition.Equals(entry.Definition))
            return true;
        return false;
    }

    private static void OnDebouncedReload(WatchState state)
    {
        try
        {
            if (state.Disposed) return;

            // FCFG11: what this reload loads into is looked up now, not captured when the watch
            // was created. See WatchedFiles for why the ConfigFile a path belongs to changes
            // underneath a watch, and what reloading the wrong one costs.
            var files = LiveWatchedFiles(state.Path);
            if (files.Count == 0)
            {
                // Every owner is gone (unloaded, or dropped through DropWatchedFile) and no new
                // instance has bound yet. Loading the file into nothing is the right answer, not a
                // reason to keep the last dead instance around; the next Bind re-enrolls and the
                // entries read the disk at that point anyway.
                if (!state.NoFileLogged)
                {
                    state.NoFileLogged = true;
                    Plugin.Logger?.LogInfo(
                        $"ConfigHelper: {state.Path} changed, but no live ConfigFile is registered for it; "
                        + "nothing to reload until a plugin binds it again.");
                }
                return;
            }
            state.NoFileLogged = false;

            // FCFG8: a reload writes the file back, and the watch used to see its own write.
            // Verified in BepInEx.Core.dll (not inferred): ConfigFile.Reload() calls
            // ConfigEntryBase.SetSerializedValue for every line that names a bound key;
            // that sets ConfigEntry<T>.BoxedValue -> Value, whose setter raises
            // ConfigFile.OnSettingChanged, which calls Save() while SaveOnConfigSet is true
            // (its default). So one hand edit produced two "reloaded" lines and two Reloaded
            // callbacks exactly one debounce apart, the second with every value unchanged
            // (evidence 207). It terminated after two only because Value's setter is
            // equality-checked: the second reload changed nothing, so it saved nothing.
            //
            // The filter is the file's own bytes. After a reload that actually wrote the file we
            // record the hash of what is then on disk — which is precisely BepInEx's write-back —
            // and an event that arrives within <see cref="SelfWriteWindowSeconds"/> whose bytes
            // still hash to that is our own write and is dropped. An unreadable file (mid-write,
            // locked) hashes to null and is never treated as ours: fail open, a spurious reload is
            // cheap and a lost edit is not.
            //
            // FCFG9 (R64 #1) put the two bounds on it that the claim above always implied and the
            // first cut did not have.
            //
            // (a) Armed only when the reload really wrote. SaveOnConfigSet is not always true and
            // Core is what turns it off: ConfigSync holds it false for the whole ConfigFile while
            // any synced slot on it is locked (Networking/ConfigSync.cs, HoldSaveOff). On such a
            // file Reload() writes nothing, so the post-reload bytes are the *operator's* bytes;
            // arming on them turned the filter into "ignore any future event whose content equals
            // what was loaded", and the operator's own re-save of that content was dropped for
            // good while the in-memory value had since moved (a ConfigSync apply, a mod assigning
            // Value).
            //
            // FCFG10 (R65 #1): the test for "the reload wrote" is the file's own SaveOnConfigSet
            // flag, read before Reload(), and *not* "the bytes changed across Reload()". Those two
            // are not the same claim: the span of Reload() includes every SettingChanged
            // subscriber it runs synchronously, so on a save-off file — the very case (a) is
            // written about — the only thing that can move the bytes is somebody else's write, and
            // arming on it discarded an operator's save as our own echo. The bytes must still have
            // changed (a save-on reload that assigned nothing saved nothing), but the flag is what
            // says the change could have been ours.
            //
            // (b) It expires, measured from the write-back. The notification for a write-back that
            // happened synchronously inside Reload() cannot arrive later than a couple of debounce
            // intervals; anything matching after that is somebody restoring those bytes on purpose
            // — Ctrl+Z and save, a git checkout, an editor rewriting what it read — and must
            // reload. The window is what lets the several notifications one temp-file rename
            // delivers for the same bytes all be recognized as the one write they are. It is three
            // debounce intervals because the restartable debounce spends the first one; see
            // SelfWriteWindowSeconds for what that does and does not bound.
            //
            // (c) Since R69 #3 the ordinary reload has no write-back at all: SaveOnConfigSet is
            // held off across the loop below, so the arm can only be reached by an instance whose
            // flag could not be held. The filter is kept for exactly that instance rather than
            // deleted, because the hold is something we do to somebody else's object and Core must
            // not depend on it having worked.
            var current = HashFile(state.Path);
            if (current != null && current == state.SelfWriteHash)
            {
                if (state.SelfWriteIsFresh())
                {
                    // Once per recorded write-back, not once per event: an editor's temp-file
                    // rename can deliver several notifications for the same bytes, and the line is
                    // here to explain a missing second reload, not to count OS events. It can now
                    // only be printed over a write that really happened, so it no longer names a
                    // write-back nobody made.
                    if (!state.SelfWriteLogged)
                    {
                        state.SelfWriteLogged = true;
                        Plugin.Logger?.LogInfo(
                            $"ConfigHelper: ignoring the write-back of {state.Path} caused by the last reload");
                    }
                    return;
                }

                // Expired: past the window, so this is no longer treatable as our own echo. Drop
                // the arm and fall through to the reload.
                //
                // FCFG10 (R65 #6): the line says what is known and nothing more. It used to say
                // the file "was saved with" those bytes, which asserts a deliberate save — and the
                // one case the window's own slack makes reachable is our echo arriving late
                // because our restartable debounce delayed it, where no such save happened and an
                // operator reading the line goes looking for an editor that does not exist.
                state.ClearSelfWrite();
                Plugin.Logger?.LogInfo(
                    $"ConfigHelper: {state.Path} holds the bytes our last reload wrote and the "
                    + "write-back window has passed; reloading it");
            }

            // A reload reads the file and does not write it (R69 #3).
            //
            // Two facts, both read out of BepInEx.Core.dll rather than inferred:
            // ConfigFile.Reload() assigns only the keys the file still names (a key it does not
            // find is left at its in-memory value), and ConfigFile.Save() writes *every* entry
            // plus the orphans. Reload reaches Save because SetSerializedValue -> Value's setter
            // -> OnSettingChanged -> Save() while SaveOnConfigSet is true.
            //
            // With more than one registered instance those two facts combined into a data loss the
            // FCFG12 loop made reachable. LiveWatchedFiles returns them oldest registration first,
            // and the oldest is normally the dead plugin generation's ConfigFile — so on an edit
            // that changed one key and *deleted* another (the documented "delete the line to get
            // the default back" idiom), the dead instance reloaded first, its assignment of the
            // changed key saved the whole file, and the deleted key was rewritten at the dead
            // generation's value before the live instance had read a byte. The live instance then
            // loaded that. FCFG2's silent revert by a fourth route, produced — as FCFG12's own
            // docstring says of FCFG11 — by the rule written to close the third.
            //
            // So SaveOnConfigSet is held off on every instance for the whole loop: the maneuver
            // ConfigSync already owns for its locks (Networking/ConfigSync.cs, HoldSaveOff). The
            // operator's bytes stay on disk exactly as they were typed, no instance can re-inject
            // a key they deleted, N instances cost N reads instead of N reads and N rewrites, and
            // the write-back the self-write filter above exists for does not happen at all.
            //
            // The hold is best effort, so the filter stays for what it cannot cover: an instance
            // whose flag could not be read, could not be written, or did not take — read back
            // after the write, never assumed — can still save inside its own Reload, and that is
            // the only thing that arms the filter now.
            bool reloadCouldWrite = false;
            var heldOff = new bool[files.Count];
            var saveWas = new bool[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    saveWas[i] = files[i].SaveOnConfigSet;
                    files[i].SaveOnConfigSet = false;
                    heldOff[i] = !files[i].SaveOnConfigSet;
                }
                catch (Exception e)
                {
                    heldOff[i] = false;
                    Plugin.Logger?.LogWarning(
                        "ConfigHelper: holding SaveOnConfigSet off for '" + state.Path + "' threw: "
                        + e.Message + "; that instance may write the file back inside its reload.");
                }

                if (heldOff[i]) continue;
                reloadCouldWrite = true;
                Plugin.Logger?.LogWarning(
                    "ConfigHelper: could not hold SaveOnConfigSet off for one of the ConfigFile "
                    + "instances registered for '" + state.Path
                    + "'; its reload may write its own values back over the operator's edit.");
            }

            // One reload per live instance, guarded one at a time so a throwing instance cannot
            // leave the others holding the pre-edit values. Count the ones that loaded: an
            // all-threw reload must not raise Reloaded or log success, and Reloaded is handed the
            // newest instance that actually loaded (files is oldest first).
            int loaded = 0;
            ConfigFile? newestLoaded = null;
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    try
                    {
                        files[i].Reload();
                        loaded++;
                        newestLoaded = files[i];
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogError(
                            $"ConfigHelper: Reload of '{state.Path}' threw for one of its {files.Count} "
                            + $"registered ConfigFile instance(s): {e}");
                    }
                }
            }
            finally
            {
                // Restore only what we turned off, and only while it is still off: a SettingChanged
                // subscriber (or ConfigSync releasing a lock) that turned it back on inside the
                // reload made a decision of its own, and ours is not a value to write over it.
                // Before the Reloaded callbacks, so a subscriber assigning a Value saves as ever.
                for (int i = 0; i < files.Count; i++)
                {
                    if (!heldOff[i]) continue;
                    try
                    {
                        if (!files[i].SaveOnConfigSet) files[i].SaveOnConfigSet = saveWas[i];
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning(
                            "ConfigHelper: restoring SaveOnConfigSet for '" + state.Path + "' threw: "
                            + e.Message + "; that ConfigFile may no longer save on a Value write.");
                    }
                }
            }

            // Every Reload threw: the entries still hold their pre-edit values and the path does not
            // agree with the disk. Say so, raise nothing, and retry once through the debounce: an
            // editor's exclusive save or atomic rename can make a reload throw for a moment. The
            // budget is one retry per external notification (NotifyExternalChange refills it; this
            // re-Schedule does not), so a persistently locked or broken file cannot loop.
            if (loaded == 0 || newestLoaded == null)
            {
                state.ClearSelfWrite();
                bool retry = state.ReloadFailedRetries < MaxFailedReloadRetries;
                Plugin.Logger?.LogError(
                    $"ConfigHelper: no registered ConfigFile for '{state.Path}' could be reloaded; "
                    + "its entries still hold their pre-edit values and Reloaded was not raised"
                    + (retry ? "; retrying once after the debounce." : "; not retrying until the file changes again."));
                if (retry)
                {
                    state.ReloadFailedRetries++;
                    state.Schedule();
                }
                return;
            }
            state.ReloadFailedRetries = 0;

            // Record before the callbacks, not after. BepInEx's write-back is synchronous inside
            // Reload(), so this is the tightest possible window; hashing after Reloaded ran would
            // let a genuine edit that landed while a subscriber was working be mistaken for our
            // own write and skipped for good. Anything written after this line — by an operator
            // or by a Reloaded subscriber — hashes differently and still fires.
            var afterReload = HashFile(state.Path);
            if (reloadCouldWrite && afterReload != null && current != null && afterReload != current)
                state.ArmSelfWrite(afterReload);
            else
                state.ClearSelfWrite();

            // Once per successful reload, handed the newest instance that loaded (R69 #4).
            //
            // FCFG12 raised it once per instance, justified as "a subscriber that cares which file
            // it was reads the ConfigFile it is handed — as the Sample's handler does". The Sample's
            // handler reads exactly one thing off that object, ConfigFilePath, and two registered
            // instances of one path carry the same path string by construction — so the named
            // example cannot tell them apart, and neither can any other subscriber: nothing on a
            // ConfigFile identifies which instance it is. Per-instance dispatch was therefore N
            // identical callbacks for one operator edit, which on the Sample meant two
            // "reload #N" lines, the second reporting every key unchanged because the first had
            // already moved the handler's before-values (and that second line is what the rig's
            // config-watch case counts).
            //
            // The event says "this path now agrees with the disk", which is one fact per reload
            // that loaded at least one instance. Every instance that could load has been reloaded
            // by the time it is raised; the argument is the newest that succeeded, which is what a
            // subscriber holding no ConfigFile of its own should act on. A failed instance is
            // never the argument.
            var handler = Reloaded;
            handler?.Invoke(newestLoaded);
            Plugin.Logger?.LogInfo(
                files.Count == 1
                    ? $"ConfigHelper: reloaded {state.Path}"
                    : loaded == files.Count
                        ? $"ConfigHelper: reloaded {state.Path} into {files.Count} registered ConfigFile instances"
                        : $"ConfigHelper: reloaded {state.Path} into {loaded} of {files.Count} registered ConfigFile instances");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ConfigHelper: Reload of '{state.Path}' threw: {e}");
        }
    }

    /// <summary>
    /// Hash of the file's bytes, or null when it could not be read (missing, locked, being
    /// replaced by an editor's temp-file rename). Content, not length plus timestamp: an editor
    /// that rewrites the same byte count in the same second is exactly the case that must not be
    /// mistaken for a no-op, and BepInEx's write-back is often the same length as the edit that
    /// caused it.
    /// </summary>
    private static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    private sealed class TrackedBind
    {
        public readonly ConfigFile File;
        public readonly ConfigDefinition Definition;

        public TrackedBind(ConfigFile file, ConfigDefinition definition)
        {
            File = file;
            Definition = definition;
        }
    }

    private sealed class TrackedHandler
    {
        public readonly ConfigFile File;
        public readonly EventHandler<SettingChangedEventArgs> Handler;

        public TrackedHandler(ConfigFile file, EventHandler<SettingChangedEventArgs> handler)
        {
            File = file;
            Handler = handler;
        }
    }

    private sealed class WatchState
    {
        /// <summary>
        /// FCFG11: the watch is keyed by path and holds no <see cref="ConfigFile"/> of its own.
        /// It used to hold the instance it was created with, which is the instance that stops
        /// being the file's after the first plugin hot reload; the live ones are looked up per
        /// reload from <see cref="WatchedFiles"/>.
        /// </summary>
        public readonly string Path;
        public bool Disposed;

        /// <summary>
        /// Whether the "no live ConfigFile" line has already been printed for the current empty
        /// spell. One line per spell, not one per notification: an editor's temp-file rename
        /// delivers several, and the unload window it describes is a second or two long.
        /// </summary>
        public bool NoFileLogged;

        /// <summary>
        /// Automatic retries spent after an all-threw reload in the current external-notification
        /// episode (cap <see cref="MaxFailedReloadRetries"/>). An external notification refills
        /// it (<see cref="NotifyExternalChange"/>), as does a reload that loads; the retry's own
        /// re-<see cref="Schedule"/> does not, so the cap binds.
        /// </summary>
        public int ReloadFailedRetries;

        /// <summary>
        /// Hash of the bytes the last reload left on disk — BepInEx's own write-back (FCFG8).
        /// Null until a reload that was *allowed* to write (<c>SaveOnConfigSet</c>) actually
        /// changed the file, so nothing is ever skipped before one has (FCFG9) and a write nobody
        /// on our side could have made is never claimed as ours (FCFG10). Written and read only
        /// from the debounced reload, which runs on the main thread.
        /// </summary>
        public string? SelfWriteHash;

        /// <summary>
        /// When the write-back <see cref="SelfWriteHash"/> describes happened — taken immediately
        /// after <see cref="ConfigFile.Reload"/> returned, which is when BepInEx wrote it, not
        /// when a later notification was processed. A match older than
        /// <see cref="SelfWriteWindowSeconds"/> is not our write-back (FCFG9, FCFG10).
        /// </summary>
        public DateTime SelfWriteAt;

        /// <summary>Whether the current <see cref="SelfWriteHash"/> has already been logged once.</summary>
        public bool SelfWriteLogged;

        /// <summary>Record the bytes this reload's write-back left on disk, with their age.</summary>
        public void ArmSelfWrite(string hash)
        {
            SelfWriteHash = hash;
            SelfWriteAt = DateTime.UtcNow;
            SelfWriteLogged = false;
        }

        /// <summary>Forget the recorded write-back; the next matching event reloads.</summary>
        public void ClearSelfWrite()
        {
            SelfWriteHash = null;
            SelfWriteAt = default;
            SelfWriteLogged = false;
        }

        /// <summary>
        /// True while the recorded write-back is young enough that its own notification could
        /// still be arriving.
        /// </summary>
        public bool SelfWriteIsFresh()
        {
            if (SelfWriteHash == null) return false;
            var age = DateTime.UtcNow - SelfWriteAt;
            // A clock that jumped backwards (NTP step, a VM resume) makes age negative; treat that
            // as fresh rather than as expiry, because the alternative is a spurious reload loop.
            return age < TimeSpan.FromSeconds(SelfWriteWindowSeconds);
        }

        private FileSystemWatcher? _watcher;
        private int _generation;
        private int _stopped;

        public WatchState(string path)
        {
            Path = path;
        }

        public void Start()
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            var name = System.IO.Path.GetFileName(Path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException($"Cannot watch config path '{Path}'.");

            Directory.CreateDirectory(dir);

            var watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnRenamed;
            _watcher = watcher;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                Stop();
                throw;
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs e) => NotifyExternalChange();

        private void OnChanged(object sender, FileSystemEventArgs e) => NotifyExternalChange();

        /// <summary>
        /// A watcher notification (or the test seam): a new operator-save episode, so refill the
        /// all-threw retry budget, then restart the debounce. The all-threw retry calls
        /// <see cref="Schedule"/> directly, never this, or the cap would never bind.
        /// </summary>
        internal void NotifyExternalChange()
        {
            ReloadFailedRetries = 0;
            Schedule();
        }

        internal void Schedule()
        {
            if (Disposed) return;
            int gen = Interlocked.Increment(ref _generation);
            MainThread.PostDelayed(DebounceSeconds, () =>
            {
                if (Disposed) return;
                if (Volatile.Read(ref _generation) != gen) return;
                OnDebouncedReload(this);
            });
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            Disposed = true;
            var watcher = _watcher;
            _watcher = null;
            if (watcher == null) return;
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnChanged;
                watcher.Created -= OnChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Dispose();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ConfigHelper: disposing watcher for '{Path}' threw: {e}");
            }
        }
    }

    private sealed class ClosedWatch : IDisposable
    {
        public static readonly ClosedWatch Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// The handle <see cref="DropWatchedFileOnUnload"/> returns. Holds the
    /// <see cref="ConfigFile"/> strongly — it is disposed with the context that owns that object,
    /// so it cannot outlive it, and a weak reference here would let the instance be collected
    /// between the plugin's last use and its unload, leaving nothing to drop by identity.
    /// </summary>
    private sealed class WatchedFileDrop : IDisposable
    {
        private ConfigFile? _file;

        public WatchedFileDrop(ConfigFile file) => _file = file;

        public void Dispose()
        {
            var file = Interlocked.Exchange(ref _file, null);
            if (file == null) return;
            try { DropWatchedFile(file); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "ConfigHelper: dropping a watched ConfigFile at unload threw: " + e.Message);
            }
        }
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// Test seam for FCFG8: a watch state with no <see cref="FileSystemWatcher"/> and no
    /// debounce timer, so a test can fire the debounced reload exactly as the timer would and
    /// assert what the second event does. The production path (watcher -> Schedule ->
    /// <c>MainThread.PostDelayed(1.0)</c>) is unchanged and is not routed through this type.
    /// </summary>
    internal sealed class TestWatch
    {
        private readonly WatchState _state;

        /// <summary>
        /// Enrolls <paramref name="file"/> the way <see cref="Watch"/> does — through the real
        /// per-path registry (FCFG11), so a test can add a second instance with
        /// <see cref="ConfigHelper.Bind{T}"/> or <see cref="ConfigHelper.Watch"/> and drop one with
        /// <see cref="ConfigHelper.DropWatchedFile"/>, and this watch sees exactly what a live one
        /// would.
        /// </summary>
        public TestWatch(ConfigFile file)
        {
            _state = new WatchState(file.ConfigFilePath);
            lock (Gate)
                RegisterWatchedFile(file.ConfigFilePath, file);
        }

        /// <summary>The instances this watch would reload, oldest registration first.</summary>
        public IReadOnlyList<ConfigFile> LiveFiles => LiveWatchedFiles(_state.Path);

        /// <summary>
        /// What the last reload wrote back, or null when it wrote nothing (FCFG9) and before the
        /// first reload.
        /// </summary>
        public string? SelfWriteHash => _state.SelfWriteHash;

        /// <summary>Run the debounced reload, as the posted timer callback does.</summary>
        public void FireDebounced() => OnDebouncedReload(_state);

        /// <summary>
        /// The path a <see cref="FileSystemWatcher"/> notification takes: refill the all-threw
        /// retry budget, then restart the debounce through <see cref="MainThread.PostDelayed"/>.
        /// </summary>
        public void NotifyChanged() => _state.NotifyExternalChange();

        /// <summary>
        /// Age the recorded write-back past <see cref="SelfWriteWindowSeconds"/> so a test can
        /// reach the expiry branch without sleeping. Only the timestamp moves; the hash is left
        /// alone, which is exactly the state a real watch is in seconds after a reload.
        /// </summary>
        public void ExpireSelfWriteWindow()
        {
            if (_state.SelfWriteHash == null) return;
            _state.SelfWriteAt = DateTime.UtcNow - TimeSpan.FromSeconds(SelfWriteWindowSeconds + 1.0);
        }
    }
#endif
}
