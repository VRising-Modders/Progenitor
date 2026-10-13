using System;
using System.Collections.Generic;
using System.Globalization;

#if !PROGENITOR_TESTS
using BepInEx.Configuration;
using Progenitor.Commands;
using Progenitor.Config;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// How loud the Sample's config demo is. A <b>plugin</b> enum, on purpose: it is the type
/// <see cref="SampleConfig"/> puts through <c>ConfigHelper.BindEnum</c> / <c>ReadEnum</c>, and the
/// reason that pair exists. A <c>ConfigEntry&lt;SampleVerbosity&gt;</c> on Bootstrap's shared
/// <c>progenitor.cfg</c> would pin the collectible Core generation for the rest of the
/// process (evidence 65 caught exactly that with a Core enum); bound as a <b>string</b> it cannot,
/// and the typed read happens on our side of the boundary.
/// </summary>
internal enum SampleVerbosity
{
    /// <summary>Say nothing beyond the reload line itself.</summary>
    Quiet = 0,

    /// <summary>The default.</summary>
    Normal = 1,

    /// <summary>Everything.</summary>
    Loud = 2,
}

/// <summary>
/// Job FCFGEX (RD6 J11). The first consumer of <c>Progenitor.Config.ConfigHelper</c> in the
/// repository: <c>Bind</c>, <c>BindEnum</c>, <c>ReadEnum</c>, <c>Watch</c>, <c>Reloaded</c> and
/// <c>SharedFileName</c>, driven by <c>.cfg</c>.
///
/// <b>Why this example is shaped around a hand edit.</b> Every other Sample feature can be proved
/// by typing a command. This one cannot: the whole point of the module is that <b>BepInEx only
/// reloads a <c>.cfg</c> when something asks it to</b>, so a value an operator changes on disk
/// while the server is up is ignored until restart unless somebody is watching the file. The
/// thing being demonstrated therefore happens in a text editor, not in chat — and that is why the
/// callback line below (<see cref="ReloadedLine"/>) carries a timestamp, the file name, the
/// callback number and the <b>old and new value of both entries</b>. An operator must be able to
/// open the server log after the fact and see that their edit landed, without having had a chat
/// window open at the moment it did.
///
/// <b>Three separate things, and only two of them are the watch.</b> They are easy to conflate and
/// the command keeps them apart:
/// <list type="number">
/// <item><description><c>ConfigHelper.Watch(file)</c> is a <b>lease</b> on a
/// <c>FileSystemWatcher</c> for that path. Leases refcount (<c>Config/LeaseTable.cs</c>): a second
/// watch on the same path shares the watcher and returns its own <c>IDisposable</c>, and the
/// watcher stops only when the last lease is disposed.</description></item>
/// <item><description><c>ConfigHelper.Reloaded</c> is a <b>single static event for every watched
/// file</b>, raised after the debounced <c>ConfigFile.Reload()</c>, on the main thread. A handler
/// therefore has to filter on <c>ConfigFilePath</c> — ours does, in
/// <see cref="Observe"/>, and <c>.cfg status</c> prints how many callbacks were for other files so
/// the multiplexing is visible rather than surprising. It is <b>raised once per reload of a
/// path</b>, however many <c>ConfigFile</c> instances that path has registered (R69 #4), so one
/// hand edit is one <c>reload #N</c> line. It has to be: the path is the only thing a handler can
/// read off the object it is handed, and two instances of one path carry the same
/// path.</description></item>
/// <item><description><c>ConfigFile.Reload()</c> called by hand (<c>.cfg read</c>) re-reads the
/// same file and updates the same live <c>ConfigEntry&lt;T&gt;</c> values, but it does <b>not</b>
/// raise <c>Reloaded</c> — that event belongs to the watch. The reply says so, because a counter
/// that does not move after <c>.cfg read</c> otherwise reads as a bug.</description></item>
/// </list>
///
/// <b><c>ConfigHelper.Bind</c> also starts a watch, and never lets it go.</b> <c>Bind</c> ends with
/// a bare <c>Watch(file);</c> whose lease it discards (<c>Config/ConfigHelper.cs:93</c>), so from
/// the first bind there is a Core-lifetime lease on this plugin's <c>GUID.cfg</c> that only
/// <c>ConfigHelper.UninstallAll</c> drops. That is deliberate — binding through <c>ConfigHelper</c>
/// is meant to mean "and keep it live" — but it has a consequence this example has to be honest
/// about: <c>.cfg watch off</c> drops <b>our</b> lease and unsubscribes <b>our</b> handler, and the
/// underlying <c>FileSystemWatcher</c> keeps running because Bind's lease is still held. The
/// observable half of "off" is the handler, and that is what the reply claims. A plugin that wants
/// the watcher itself to stop on demand must bind through <c>ctx.Bind</c> / <c>ctx.Config.Bind</c>
/// (neither starts a watch) and own the only lease.
///
/// <b>Nothing survives an unload.</b> <c>ConfigHelper.Reloaded</c> is a <b>Core</b> static, so a
/// delegate pointing at this plugin left on it would hold the plugin's ALC alive past a hot
/// reload — the same shape of pin as the <c>ConfigEntry</c> above, one level up.
/// <see cref="Detach"/> unsubscribes and disposes the lease, and <see cref="Attach"/> tracks a
/// disposable on the context that does the same, so an unload that never reaches
/// <c>Plugin.OnUnload</c> still cleans up.
///
/// <b>Re-arming, not short-circuiting.</b> <c>.cfg watch on</c> while the watch is already on does
/// <b>not</b> answer "already on, nothing to do". The watch has no persistent half to read the
/// state off: the lease and the subscription are both process-lifetime, so "it was on" is a
/// memory of an intention. It unsubscribes, disposes, re-acquires and re-subscribes, then reports a
/// fresh read. That is CLAUDE.md's rule from evidence 162, applied to something much smaller than
/// the buff it was learned on.
///
/// <b>Live-check split: server only.</b> The Sample attaches this on the server, where the file an
/// operator edits by hand actually is.
///
/// Parsing, the counters and every reply and log line are game-free, so the test project compiles
/// this file and pins the text.
/// </summary>
internal static class SampleConfig
{
    /// <summary>Log/grep area: <c>Sample config: …</c>.</summary>
    internal const string Area = "config";

    /// <summary>Section both demo entries live in, in this plugin's own <c>GUID.cfg</c>.</summary>
    internal const string Section = "Watch";

    /// <summary>The plain <c>ConfigHelper.Bind&lt;bool&gt;</c> entry.</summary>
    internal const string EnabledKey = "Enabled";

    /// <summary>Default of <see cref="EnabledKey"/>.</summary>
    internal const bool EnabledDefault = true;

    /// <summary>The <c>ConfigHelper.BindEnum&lt;SampleVerbosity&gt;</c> entry (a string on disk).</summary>
    internal const string VerbosityKey = "Verbosity";

    /// <summary>Default of <see cref="VerbosityKey"/>, and what <c>ReadEnum</c> falls back to.</summary>
    internal const SampleVerbosity VerbosityDefault = SampleVerbosity.Normal;

    /// <summary>Debounce the watch applies before it reloads, in seconds (<c>WatchState.Schedule</c>).</summary>
    internal const double DebounceSeconds = 1.0;

    /// <summary>Cfg comment for <see cref="EnabledKey"/>.</summary>
    internal const string EnabledDescription =
        "Sample config-watch demo. Edit this by hand while the server runs and watch the log.";

    /// <summary>Cfg comment for <see cref="VerbosityKey"/>. Names the members so a typo is fixable.</summary>
    internal const string VerbosityDescription =
        "Sample config-watch demo. One of: Quiet, Normal, Loud. Bound as a string (BindEnum).";

    private static int _ourReloads;
    private static int _otherReloads;

    /// <summary>What <c>.cfg</c> was asked to do.</summary>
    internal enum Kind
    {
        /// <summary>No argument, or <c>status</c>: print the files, the values and the counters.</summary>
        Status = 0,

        /// <summary><c>watch on</c>: (re-)arm the lease and the handler.</summary>
        WatchOn = 1,

        /// <summary><c>watch off</c>: unsubscribe and drop our lease.</summary>
        WatchOff = 2,

        /// <summary><c>read</c>: <c>ConfigFile.Reload()</c> now and print the values.</summary>
        Read = 3,
    }

    /// <summary>Debounced <c>Reloaded</c> callbacks seen for <b>this plugin's</b> file.</summary>
    internal static int OurReloads => System.Threading.Volatile.Read(ref _ourReloads);

    /// <summary>Callbacks seen for some other watched file. The event is one static for all of them.</summary>
    internal static int OtherReloads => System.Threading.Volatile.Read(ref _otherReloads);

    /// <summary>
    /// Parse <c>.cfg</c> / <c>.cfg status</c> / <c>.cfg watch on|off</c> / <c>.cfg read</c>.
    /// A bare <c>.cfg watch</c> is refused rather than guessing a direction: guessing "on" would
    /// make a typo arm a file watcher, and guessing "off" would make it silently disarm one.
    /// </summary>
    internal static bool TryParse(string? token, string? arg, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;

        string head = token!.Trim().ToLowerInvariant();
        if (head == "status") return true;
        if (head == "read")
        {
            kind = Kind.Read;
            return true;
        }

        if (head != "watch") return false;

        string tail = (arg ?? "").Trim().ToLowerInvariant();
        if (tail == "on")
        {
            kind = Kind.WatchOn;
            return true;
        }

        if (tail == "off")
        {
            kind = Kind.WatchOff;
            return true;
        }

        return false;
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal const string UsageReply = "Usage: .cfg [status] | .cfg watch on|off | .cfg read";

    /// <summary>
    /// File-name-only view of a config path. The replies and the log line print the name and never
    /// the directory: the full path is the server operator's filesystem layout, and the name is
    /// what identifies the file.
    /// </summary>
    internal static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        string s = path!.Replace('\\', '/').TrimEnd('/');
        int slash = s.LastIndexOf('/');
        string name = slash >= 0 ? s.Substring(slash + 1) : s;
        return name.Length == 0 ? "(unknown)" : name;
    }

    /// <summary>
    /// Whether a <c>Reloaded</c> callback is about our file. Compared on the whole path, case
    /// insensitively and with separators normalized, because both strings come from
    /// <c>ConfigFile.ConfigFilePath</c> on Windows. Comparing file <em>names</em> would be wrong:
    /// two plugins in different BepInEx roots (a hosted child server beside the client) can have
    /// the same <c>GUID.cfg</c> name.
    /// </summary>
    internal static bool IsOurFile(string? candidatePath, string? ourPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(ourPath)) return false;
        return string.Equals(Normalize(candidatePath!), Normalize(ourPath!), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Count one <c>Reloaded</c> callback against the right counter. Returns true when it was ours
    /// and the caller should log the reload line.
    /// </summary>
    internal static bool Observe(string? changedPath, string? ourPath)
    {
        if (IsOurFile(changedPath, ourPath))
        {
            System.Threading.Interlocked.Increment(ref _ourReloads);
            return true;
        }

        System.Threading.Interlocked.Increment(ref _otherReloads);
        return false;
    }

    /// <summary>UTC clock reading for the reload line. Fixed width so a log column stays aligned.</summary>
    internal static string Timestamp(DateTime utc) =>
        utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// One key's before/after, the shape the reload line is built from. An unchanged key still
    /// prints its value: a reload that changed nothing is a real answer ("your edit did not take"),
    /// and omitting it would leave the reader unable to tell it from a key that vanished.
    /// </summary>
    internal static string Change(string key, string before, string after) =>
        string.Equals(before, after, StringComparison.Ordinal)
            ? key + "=" + after + " (unchanged)"
            : key + ": " + before + " -> " + after;

    /// <summary>
    /// The line the whole example exists to produce. It is written to the server log from the
    /// debounced callback, and it is deliberately self-contained: timestamp, which file, which
    /// callback, and what both entries read before and after. An operator who edited the cfg an
    /// hour ago can grep <c>Sample config: reload #</c> and see whether it landed.
    /// </summary>
    internal static string ReloadedLine(
        string timestamp,
        string fileName,
        int count,
        bool beforeEnabled,
        bool afterEnabled,
        SampleVerbosity beforeVerbosity,
        SampleVerbosity afterVerbosity) =>
        "reload #" + N(count) + " at " + timestamp + " of " + fileName + ": "
        + Change(Section + "." + EnabledKey, B(beforeEnabled), B(afterEnabled)) + ", "
        + Change(Section + "." + VerbosityKey, beforeVerbosity.ToString(), afterVerbosity.ToString())
        + " (ConfigHelper.Reloaded, debounced " + DebounceSeconds.ToString("0.0", CultureInfo.InvariantCulture)
        + "s, main thread)";

    /// <summary>Line written when a callback was about some other watched file. Grep-visible, not silent.</summary>
    internal static string OtherFileLine(string fileName, int count) =>
        "ignored ConfigHelper.Reloaded #" + N(count) + " for " + fileName
        + " (one static event for every watched file; filter on ConfigFilePath)";

    /// <summary>Reply for <c>.cfg</c> / <c>.cfg status</c>.</summary>
    internal static IReadOnlyList<string> StatusLines(
        string pluginFileName,
        string sharedFileName,
        bool enabled,
        SampleVerbosity verbosity,
        int ourReloads,
        int otherReloads,
        bool watching) =>
        new[]
        {
            "file=" + pluginFileName + " (this plugin's own GUID.cfg; the demo is that you edit it "
                + "by hand while the server is running)",
            "shared=" + sharedFileName + " (ConfigHelper.SharedFileName - Bootstrap's "
                + "process-lifetime file; BCL primitives, string and BCL enums only, never a "
                + "plugin type, or the ConfigEntry pins the collectible Core generation)",
            Section + "." + EnabledKey + "=" + B(enabled) + " (ConfigHelper.Bind<bool>)",
            Section + "." + VerbosityKey + "=" + verbosity
                + " (ConfigHelper.BindEnum<SampleVerbosity>, a string on disk, read back with "
                + "ConfigHelper.ReadEnum)",
            "Reloaded callbacks: " + N(ourReloads) + " for this file, " + N(otherReloads)
                + " for other watched files",
            "watch=" + (watching ? "on" : "off") + ". " + (watching
                ? "Edit the file and the reload line appears in the server log within about "
                    + DebounceSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s."
                : "Turn it on with .cfg watch on, then edit the file."),
        };

    /// <summary>Reply for <c>.cfg read</c>: the values after a hand-called <c>ConfigFile.Reload()</c>.</summary>
    internal static IReadOnlyList<string> ReadLines(
        string pluginFileName,
        bool enabled,
        SampleVerbosity verbosity,
        int ourReloads) =>
        new[]
        {
            "re-read " + pluginFileName + " with ConfigFile.Reload(): "
                + Section + "." + EnabledKey + "=" + B(enabled) + ", "
                + Section + "." + VerbosityKey + "=" + verbosity,
            "That was a manual reload, so the ConfigHelper.Reloaded counter is unchanged at "
                + N(ourReloads) + " - that event belongs to the watch (.cfg watch on).",
        };

    /// <summary>
    /// Reply for <c>.cfg watch on</c>. Says whether it was already armed and that it was re-armed
    /// anyway, so the reader learns the rule rather than only the outcome.
    /// </summary>
    internal static string WatchOnReply(bool wasAlreadyOn, string pluginFileName) =>
        (wasAlreadyOn
            ? "Watch was already on; re-armed it anyway (the lease and the handler are both "
                + "process-lifetime, so 'it was on' is not something that can be read back). "
            : "Watch armed. ")
        + "ConfigHelper.Watch(" + pluginFileName + ") plus a ConfigHelper.Reloaded handler. Edit "
        + Section + "." + EnabledKey + " or " + Section + "." + VerbosityKey
        + " in that file and the reload line shows up in the server log within about "
        + DebounceSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s.";

    /// <summary>
    /// Reply for <c>.cfg watch off</c>. It refuses to claim more than it did: the handler is gone,
    /// and the FileSystemWatcher stays up because <c>ConfigHelper.Bind</c> holds a lease of its own
    /// that only Core's UninstallAll drops.
    /// </summary>
    internal static string WatchOffReply(bool wasOn) =>
        (wasOn
            ? "Watch off: the ConfigHelper.Reloaded handler is unsubscribed and this plugin's "
                + "Watch lease is disposed. "
            : "Watch was already off; unsubscribed and disposed again to be sure. ")
        + "The FileSystemWatcher itself keeps running: ConfigHelper.Bind took a lease of its own on "
        + "this file and never disposed it, so only Core's UninstallAll stops it.";

    /// <summary>The line written when the demo attaches.</summary>
    internal static string AttachedLine(string pluginFileName, string sharedFileName) =>
        "bound " + Section + "." + EnabledKey + " (Bind<bool>) and " + Section + "." + VerbosityKey
        + " (BindEnum<SampleVerbosity>) in " + pluginFileName + "; shared file is " + sharedFileName
        + "; watch is off until .cfg watch on";

    /// <summary>Forget the counters. Unload does this; the entries themselves stay on disk.</summary>
    internal static void Reset()
    {
        System.Threading.Interlocked.Exchange(ref _ourReloads, 0);
        System.Threading.Interlocked.Exchange(ref _otherReloads, 0);
    }

#if !PROGENITOR_TESTS
    private static ConfigFile? _file;
    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<string>? _verbosity;
    private static IDisposable? _lease;
    private static Action<ConfigFile>? _handler;
    private static bool _armed;
    private static bool _lastEnabled = EnabledDefault;
    private static SampleVerbosity _lastVerbosity = VerbosityDefault;

    /// <summary>
    /// Server-side registration. Binds the two demo entries through <c>ConfigHelper</c>, registers
    /// <c>.cfg</c>, and tracks a disposable on the context that disarms the watch — so an unload
    /// that bypasses <see cref="Detach"/> still cannot leave a plugin delegate on Core's static
    /// <c>ConfigHelper.Reloaded</c>.
    /// </summary>
    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Detach();
        Reset();

        try
        {
            var file = ctx.Config.File;
            _file = file;

            _enabled = ConfigHelper.Bind(file, Section, EnabledKey, EnabledDefault, EnabledDescription);
            _verbosity = ConfigHelper.BindEnum(file, Section, VerbosityKey, VerbosityDefault, VerbosityDescription);
            _lastEnabled = ReadEnabled();
            _lastVerbosity = ReadVerbosity();

            ctx.Commands.Register(typeof(SampleConfigCommands));
            ctx.Track(new SampleConfigDisarm());

            SampleLog.Info(Area, AttachedLine(FileNameOf(file.ConfigFilePath), ConfigHelper.SharedFileName));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample config attach threw: " + e);
        }
    }

    /// <summary>Whether the watch lease and the handler are currently in place.</summary>
    internal static bool Watching => _armed;

    /// <summary>
    /// (Re-)arm. Always disarms first: the two halves are process-lifetime, so the only honest
    /// answer to "is it on" is to put it on again and say what happened.
    /// </summary>
    internal static bool ArmWatch()
    {
        bool wasOn = _armed;
        DisarmWatch();

        var file = _file;
        if (file == null) return wasOn;

        _lease = ConfigHelper.Watch(file);
        _handler = OnReloaded;
        ConfigHelper.Reloaded += _handler;
        _armed = true;
        _lastEnabled = ReadEnabled();
        _lastVerbosity = ReadVerbosity();
        return wasOn;
    }

    /// <summary>Unsubscribe and drop our lease. Idempotent; safe during unload.</summary>
    internal static bool DisarmWatch()
    {
        bool wasOn = _armed;
        _armed = false;

        var handler = _handler;
        _handler = null;
        if (handler != null)
        {
            try { ConfigHelper.Reloaded -= handler; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Sample config unsubscribe threw: " + e.Message); }
        }

        var lease = _lease;
        _lease = null;
        if (lease != null)
        {
            try { lease.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Sample config lease dispose threw: " + e.Message); }
        }

        return wasOn;
    }

    /// <summary>
    /// The debounced callback, on the main thread, after <c>ConfigFile.Reload()</c> has already
    /// updated the live entries. It fires for <b>every</b> watched file, so the first thing it does
    /// is ask whether this one is ours.
    /// </summary>
    private static void OnReloaded(ConfigFile file)
    {
        try
        {
            string changed = file?.ConfigFilePath ?? "";
            if (!Observe(changed, _file?.ConfigFilePath))
            {
                SampleLog.Info(Area, OtherFileLine(FileNameOf(changed), OtherReloads));
                return;
            }

            bool beforeEnabled = _lastEnabled;
            var beforeVerbosity = _lastVerbosity;
            bool afterEnabled = ReadEnabled();
            var afterVerbosity = ReadVerbosity();
            _lastEnabled = afterEnabled;
            _lastVerbosity = afterVerbosity;

            SampleLog.Info(Area, ReloadedLine(
                Timestamp(DateTime.UtcNow),
                FileNameOf(changed),
                OurReloads,
                beforeEnabled,
                afterEnabled,
                beforeVerbosity,
                afterVerbosity));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample config Reloaded handler threw: " + e);
        }
    }

    /// <summary>Live value of the <c>Bind&lt;bool&gt;</c> entry.</summary>
    internal static bool ReadEnabled()
    {
        var entry = _enabled;
        if (entry == null) return EnabledDefault;
        try { return entry.Value; }
        catch { return EnabledDefault; }
    }

    /// <summary>Live value of the enum entry, through <c>ConfigHelper.ReadEnum</c>.</summary>
    internal static SampleVerbosity ReadVerbosity() => ConfigHelper.ReadEnum(_verbosity, VerbosityDefault);

    /// <summary>Server entry point for <c>.cfg</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        string fileName = FileNameOf(_file?.ConfigFilePath);

        switch (kind)
        {
            case Kind.WatchOn:
            {
                bool wasOn = ArmWatch();
                string reply = WatchOnReply(wasOn, fileName);
                ctx.Reply(reply);
                SampleLog.Info(Area, reply);
                return;
            }

            case Kind.WatchOff:
            {
                bool wasOn = DisarmWatch();
                string reply = WatchOffReply(wasOn);
                ctx.Reply(reply);
                SampleLog.Info(Area, reply);
                return;
            }

            case Kind.Read:
            {
                try { _file?.Reload(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError("Sample config .cfg read threw: " + e);
                    ctx.Reply("Re-reading the config file failed. See the server log.");
                    return;
                }

                _lastEnabled = ReadEnabled();
                _lastVerbosity = ReadVerbosity();
                Emit(ctx, ReadLines(fileName, _lastEnabled, _lastVerbosity, OurReloads));
                return;
            }

            default:
            {
                Emit(ctx, StatusLines(
                    fileName,
                    ConfigHelper.SharedFileName,
                    ReadEnabled(),
                    ReadVerbosity(),
                    OurReloads,
                    OtherReloads,
                    Watching));
                return;
            }
        }
    }

    private static void Emit(ICommandContext ctx, IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            ctx.Reply(lines[i]);
            SampleLog.Info(Area, lines[i]);
        }
    }

    /// <summary>
    /// Unload. Drops the handler and the lease first (a delegate left on Core's static
    /// <c>Reloaded</c> would hold this plugin's ALC alive), then tells the watch to stop reloading
    /// this instance's <c>ConfigFile</c>, then forgets the entries. The entries themselves are
    /// BepInEx's and stay on disk; Core's <c>UninstallAll</c> owns the rest.
    ///
    /// <para>The <c>DropWatchedFile</c> call is the plugin half of FCFG11. BepInEx gives the next
    /// instance of this plugin a <b>new</b> <c>ConfigFile</c> for the same <c>GUID.cfg</c>, so the
    /// object being dropped here is about to be nobody's; a watch reload that still loaded the
    /// operator's edit into it would have BepInEx write this instance's stale values straight back
    /// over that edit. Core reloads every registered instance (FCFG12) with <c>SaveOnConfigSet</c>
    /// held off for the whole loop (R69 #3), so an undropped dead one is brought into agreement
    /// with the disk and cannot write anything while it happens; dropping it here still saves that
    /// pointless reload and this instance's SettingChanged handlers.</para>
    ///
    /// <para><b>Since FCFG13 this call is no longer load-bearing, and it is kept anyway.</b> Core
    /// drops <c>ctx.Config.File</c> for every plugin when the context is torn down
    /// (<c>PluginContext.Attach</c> tracks <c>ConfigHelper.DropWatchedFileOnUnload</c>). This
    /// <c>Detach</c> runs from <c>Plugin.OnUnload</c>, which <c>ProgenitorPlugin.Unload</c> calls
    /// <em>before</em> <c>UndoAll</c>, so the explicit call here is still the first drop and Core's
    /// is the no-op second — the reverse order costs nothing either, because a drop of an
    /// already-dropped instance finds nothing and returns false. It stays because this file is the
    /// example of the config module and <c>DropWatchedFile</c> is still the API for a
    /// <c>ConfigFile</c> Core does not know about, or for dropping one before unload.</para>
    /// </summary>
    internal static void Detach()
    {
        DisarmWatch();

        var file = _file;
        if (file != null)
        {
            try { ConfigHelper.DropWatchedFile(file); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Sample config drop-watched-file threw: " + e.Message);
            }
        }

        _enabled = null;
        _verbosity = null;
        _file = null;
        _lastEnabled = EnabledDefault;
        _lastVerbosity = VerbosityDefault;
        Reset();
    }
#else
    /// <summary>Test build: nothing is armed, because nothing was bound.</summary>
    internal static bool Watching => false;

    /// <summary>Test build: only the counters exist.</summary>
    internal static void Detach() => Reset();
#endif

    /// <summary>
    /// Separators normalized and trailing ones dropped, so <c>C:\a\b.cfg</c> and <c>c:/a/b.cfg</c>
    /// are the same file. Deliberately not <c>Path.GetFullPath</c>: this runs from a
    /// FileSystemWatcher callback path and must not touch the disk or the process's current
    /// directory to answer a question about two strings BepInEx already produced.
    /// </summary>
    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/').Trim();

    private static string B(bool value) => value ? "True" : "False";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}

#if !PROGENITOR_TESTS
/// <summary>
/// The <c>.cfg</c> command (job FCFGEX, RD6 J11). A C# static class registered with
/// <c>ctx.Commands.Register(typeof(...))</c> from <see cref="SampleConfig.Attach"/>, so the
/// registration is tracked on the context and a reload replaces it rather than stacking.
/// </summary>
internal static class SampleConfigCommands
{
    // adminOnly: it names server-side file paths' file names, arms and disarms a FileSystemWatcher,
    // and forces a config reload. None of that is a player's business, and the last one is a disk
    // read a player could otherwise repeat as fast as they can type.
    [Command("cfg", adminOnly: true, usage: ".cfg [status] | .cfg watch on|off | .cfg read", description: "Admin only. Report this plugin's config file and the shared one, watch the file for hand edits, or re-read it now.")]
    public static void Cfg(ICommandContext ctx, string token = "", string arg = "")
    {
        try
        {
            if (!SampleConfig.TryParse(token, arg, out var kind))
            {
                ctx.Reply(SampleConfig.UsageReply);
                return;
            }

            SampleConfig.HandleCommand(ctx, kind);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .cfg threw: " + e);
            ctx.Reply("Sample .cfg failed. See the server log.");
        }
    }
}

/// <summary>
/// Tracked on the <c>PluginContext</c> by <see cref="SampleConfig.Attach"/>. Core disposes the bag
/// on every unload reason, so the <c>ConfigHelper.Reloaded</c> subscription and the
/// <c>ConfigHelper.Watch</c> lease go even on a path that never calls
/// <see cref="SampleConfig.Detach"/>.
/// </summary>
internal sealed class SampleConfigDisarm : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => SampleConfig.DisarmWatch();
}
#endif
