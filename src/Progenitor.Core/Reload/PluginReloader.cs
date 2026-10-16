using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Progenitor.Commands;
using Progenitor.Compat;
using Progenitor.Content;
using Progenitor.Dev;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Progenitor.Systems;

namespace Progenitor.Reload;

/// <summary>
/// Hot reload for plugins placed in <c>BepInEx/progenitor-reload/</c>.
///
/// Each DLL there is loaded into its own collectible <see cref="AssemblyLoadContext"/> from a byte
/// copy, so the file is never locked and the same assembly name can be loaded again after a change.
/// When the file changes, the old plugin's <c>Unload()</c> runs (it must reverse its own patches and
/// registrations), the context is unloaded, and the new file is loaded. A <c>Resolving</c> handler
/// returns this Core assembly (and Bootstrap, if loaded) by simple name so the current collectible
/// Core generation is visible to plugin ALCs; BepInEx and interop still fall through to the default
/// context. When Bootstrap owns the reloader, plugin ALCs resolve Core through Bootstrap's current
/// generation pointer instead of this fallback.
///
/// Rules for a reloadable plugin, from the spec: keep state out of statics that outlive Unload, do
/// not inject IL2CPP types, and undo everything in Unload.
/// </summary>
public static class PluginReloader
{
    private sealed class Loaded
    {
        public string Path = "";
        public DateTime LastWrite;
        public AssemblyLoadContext? Context;
        public List<BasePlugin> Plugins = new();
        public Assembly? Assembly;
        public Assembly? BoundCore;
        /// <summary>Stored so <see cref="Unload"/> can unsubscribe and drop the <c>Loaded</c> capture.</summary>
        public Func<AssemblyLoadContext, AssemblyName, Assembly?>? Resolving;
        /// <summary>
        /// SHA-256 of the image the allowlist admitted for this file (job FENC2). A
        /// <c>.delta</c> may only be applied when its header names this exact hash: the delta is
        /// IL emitted against one specific baseline, and applying it to any other image is at
        /// best a corrupt method table.
        /// </summary>
        public string ImageSha = "";
        /// <summary>
        /// Job FENC3 (R38 finding 1): the compatibility rewriter returned different bytes than it
        /// was given, so the loaded metadata is Cecil's row layout and not the on-disk image's.
        /// <see cref="ImageSha"/> is cleared when this is set — a rewritten plugin has no baseline
        /// a delta can name — and this flag is what makes the refusal say why.
        /// </summary>
        public bool Rewritten;
        /// <summary>Simple assembly name, so a <c>&lt;plugin&gt;.&lt;n&gt;.delta</c> finds its plugin.</summary>
        public string AssemblyName = "";
        /// <summary>
        /// The plugin DLL's file stem, and the ONE key the <c>.delta-state</c> status file is
        /// named by — every writer and the deleter (job FENC6, R41 finding 5). Set before the
        /// image is loaded, so unlike <see cref="AssemblyName"/> it is always available: a load
        /// that throws inside <c>LoadFromStream</c> is precisely the case FENC5's refusal file
        /// exists for, and it is the case where no assembly name can be read. See
        /// <see cref="DeltaApply.StateFileName"/>.
        /// </summary>
        public string Stem = "";
        /// <summary>Deltas apply in generation order; this is the only sequence that will be taken.</summary>
        public int NextDeltaSequence = DeltaApply.FirstSequence;
        /// <summary>
        /// Job FENC4 (R39 finding 1): which load this is. <see cref="NextDeltaSequence"/> goes
        /// back to 1 on every load and the status file therefore says <c>applied=0</c> for both a
        /// process that has applied nothing and one that applied four deltas and restarted. This
        /// value is fresh per <c>Loaded</c>, so the watcher can tell those apart and rebaseline
        /// from the DEPLOYED image's sources rather than from a snapshot of a process that is gone.
        /// </summary>
        public string LoadNonce = DeltaApply.NextLoadNonce();
    }

    private static readonly Dictionary<string, Loaded> ByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Files whose last load attempt threw, keyed by path with the write stamp that failed.
    /// Retried only when the file changes. Path → DateTime only: never an assembly or ALC.
    /// </summary>
    private static readonly Dictionary<string, DateTime> Failed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The EnC-unavailable sentence is logged once per process, not once per delta.</summary>
    private static int _loggedEncDisabled;
    private static string? _folder;
    private static double _nextScan;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private const double ScanIntervalSeconds = 1.0;
    private const double SettleSeconds = 1.0; // a file must be unchanged this long before it is (re)loaded

    /// <summary>
    /// Job FREL1 (R61 finding 1): how this reloader reads <c>[BepInPlugin]</c>, at discovery and
    /// in the identity fallback alike. <b>False, because that is what BepInEx uses</b>
    /// (<c>MetadataHelper.GetMetadata</c> reads the attribute with <c>inherit: false</c> and
    /// returns null when it finds none). Discovery used to take the generic overload's default,
    /// <c>inherit: true</c>, which admitted plugins BepInEx could not describe — the single
    /// trigger behind all five null-metadata dereferences in this file. A type that inherits the
    /// attribute is refused at discovery with a line saying why.
    /// </summary>
    private const bool DiscoveryInherit = false;

    /// <summary>
    /// The identity of one plugin instance, resolved once and never null (job FREL1,
    /// R61 finding 1): BepInEx's metadata, then the same attribute read without BepInEx, then the
    /// type's full name. Every consumer in this file goes through here, so none of them can NRE on
    /// a plugin BepInEx cannot describe and all of them agree about who the plugin is.
    ///
    /// <para>Job FREL3 (R62 finding 1): the first two are <b>one question asked twice</b>, not two
    /// sources — <c>MetadataHelper.GetMetadata</c> is itself
    /// <c>GetCustomAttributes(typeof(BepInPlugin), inherit: false)</c> — and the second exists for
    /// a metadata read that <em>throws</em> and for an attribute from a second
    /// <c>BepInEx.Core</c> image, which is why it matches by name. The type name is the fallback.
    /// <see cref="PluginIdentity"/> owns both projections and the attribute lookup so
    /// <c>PluginReloaderTests</c> can compose the production readers rather than stand-ins; the
    /// one line BepInEx has to own is the <c>GetMetadata</c> call below.</para>
    /// </summary>
    private static PluginIdentity IdentityOf(BasePlugin plugin)
    {
        var type = plugin?.GetType();
        // Job FREL4 (R63 finding 4): one sink for both the reader that threw and the decoy line
        // the attribute reader emits when a same-named non-BepInEx [BepInPlugin] is present.
        Action<string> note = message => Plugin.Logger?.LogWarning("Reload: " + message);
        return PluginIdentity.Resolve(
            type?.FullName,
            new Func<PluginIdentity?>?[]
            {
                PluginIdentity.MetadataReader(() => plugin == null ? null : MetadataHelper.GetMetadata(plugin)),
                PluginIdentity.AttributeReader(type, DiscoveryInherit, note),
            },
            note);
    }

    public static string Folder =>
        BootstrapLink.Reloader != null
            ? BootstrapLink.Reloader.Folder
            : (_folder ??= System.IO.Path.Combine(Paths.BepInExRootPath, "progenitor-reload"));

    /// <summary>
    /// The DLL path a reloadable plugin was loaded from. <see cref="Assembly.Location"/> is empty
    /// for assemblies loaded via <see cref="AssemblyLoadContext.LoadFromStream"/>, so anything that
    /// needs files next to the DLL (localization JSON) looks the path up on the live
    /// <c>ByPath</c> record. Returns false for assemblies the reloader does not own, including
    /// after <see cref="Unload"/> (the record is gone, so this cannot pin a collectible ALC).
    /// </summary>
    public static bool TryGetSourcePath(Assembly asm, out string path)
    {
        if (BootstrapLink.Reloader != null)
            return BootstrapLink.Reloader.TryGetSourcePath(asm, out path);
        path = "";
        if (asm == null) return false;
        foreach (var loaded in ByPath.Values)
        {
            if (loaded.Assembly == null || !ReferenceEquals(loaded.Assembly, asm))
                continue;
            path = loaded.Path ?? "";
            return path.Length > 0;
        }
        return false;
    }

    private static readonly List<string> Events = new();

    private static void Note(string text)
    {
        Plugin.Logger?.LogInfo(text);
        lock (Events) Events.Add(text);
    }

    /// <summary>Scan immediately (main thread only) and return what changed. Used by the debug bridge.</summary>
    public static List<string> RescanNow()
    {
        if (BootstrapLink.Reloader != null) return BootstrapLink.Reloader.RescanNow();
        Scan();
        lock (Events)
        {
            var copy = new List<string>(Events);
            Events.Clear();
            return copy;
        }
    }

    /// <summary>Names of the plugins currently loaded through the reloader.</summary>
    public static List<string> LoadedPluginNames()
    {
        if (BootstrapLink.Reloader != null) return BootstrapLink.Reloader.LoadedPluginNames();
        var names = new List<string>();
        foreach (var loaded in ByPath.Values)
            foreach (var plugin in loaded.Plugins)
                names.Add(IdentityOf(plugin).Name);
        return names;
    }

    /// <summary>Called from the frame tick on either side. Cheap: a directory scan once a second.</summary>
    public static void Tick()
    {
        if (BootstrapLink.OwnsLifetime) return;
        double now = Clock.Elapsed.TotalSeconds;
        if (now < _nextScan) return;
        _nextScan = now + ScanIntervalSeconds;
        try
        {
            Scan();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Reload scan failed: {e}");
        }
    }

    private static void Scan()
    {
        Directory.CreateDirectory(Folder);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(Folder, "*.dll"))
        {
            seen.Add(file);
            var write = File.GetLastWriteTimeUtc(file);
            if ((DateTime.UtcNow - write).TotalSeconds < SettleSeconds) continue; // still being written

            // Job FBUFF2, tightened by FBUFF4 (R30 finding 15): one evaluation of the
            // replacement image — read, hash, allow-store decide — picks the unload reason and is
            // then handed to Load, so the reason and the load can never disagree. A replacement
            // that will load is a Swap (the same plugin is back in milliseconds and its live
            // world state is still wanted); one the allow policy refuses is a Removal; one that
            // cannot be read this tick is no verdict at all — the live plugin stays loaded and
            // the next scan retries. Whether the new image throws inside Load cannot be known
            // before the old instance is unloaded, so that case gets a Swap and the log line below.
            byte[]? bytes = null;
            string? hash = null;
            AllowDecision allow = default;
            bool haveImage = false;

            bool unloadedForReplacement = false;
            if (ByPath.TryGetValue(file, out var loaded))
            {
                if (loaded.LastWrite == write) continue;
                // Job FHARM7. A plugin that took ctx.UnsafeHarmony pinned its own ALC and this
                // Core generation for the life of the process; swapping it in place would load
                // the new build beside the pinned old one and do it again on the next save. Say
                // so and keep the running build. The write stamp is adopted so the refusal is
                // one line per change rather than one a second; deleting the file still unloads,
                // and a restart is what actually picks the new build up.
                if (TryRefuseUnsafeSwap(file, loaded))
                {
                    loaded.LastWrite = write;
                    continue;
                }
                var verdict = CheckReplacement(file, out bytes, out hash, out allow, out string readError);
                if (!PluginUnloadReasonPolicy.MayUnloadForReplacement(verdict))
                {
                    Note(PluginUnloadReasonPolicy.FormatUnreadable(System.IO.Path.GetFileName(file), readError));
                    continue;
                }
                haveImage = bytes != null;
                var replacementReason = PluginUnloadReasonPolicy.ReasonFor(verdict);
                unloadedForReplacement = replacementReason == PluginUnloadReason.Swap;
                Note($"Reload: {System.IO.Path.GetFileName(file)} changed, reloading (unload reason={replacementReason}).");
                Unload(loaded, replacementReason);
                ByPath.Remove(file);
            }
            if (Failed.TryGetValue(file, out var failedWrite) && failedWrite == write) continue;
            var fresh = haveImage ? Load(file, write, bytes!, hash!, allow) : Load(file, write);
            if (fresh != null) { ByPath[file] = fresh; Failed.Remove(file); }
            else
            {
                Failed[file] = write;
                if (unloadedForReplacement)
                {
                    // Honest limitation: the old instance is gone, so nothing can re-run its
                    // OnUnload with Removal. Say so instead of leaving state stuck silently.
                    Note($"Reload: {System.IO.Path.GetFileName(file)} failed to load after a Swap unload; "
                        + "live state the previous version kept is not swept. Delete the file to unload it for good.");
                }
            }
        }

        // Files removed from the folder unload their plugins.
        foreach (var path in new List<string>(ByPath.Keys))
        {
            if (seen.Contains(path)) continue;
            Failed.Remove(path);
            Note($"Reload: {System.IO.Path.GetFileName(path)} removed, unloading (unload reason={PluginUnloadReason.Removal}).");
            Unload(ByPath[path], PluginUnloadReason.Removal);
            ByPath.Remove(path);
        }

        ScanDeltas();
    }

    /// <summary>
    /// Job FHARM7: true when one of <paramref name="loaded"/>'s plugins took
    /// <c>ctx.UnsafeHarmony</c>, in which case the swap is refused and
    /// <see cref="Progenitor.Patches.UnsafeHarmonyUse.FormatSwapRefused"/> says why. The mark is
    /// never cleared for the life of the process — the Harmony pin is not undoable, so a later
    /// generation of the same GUID is still living beside a pinned one.
    ///
    /// <para>Job FHARM8 closed the mirror FHARM7 owed: the production reloader is Bootstrap's
    /// <c>ReloaderHost</c> (this one only runs when <c>BootstrapLink.OwnsLifetime</c> is false)
    /// and it now has the same hunk, reading the same table —
    /// <c>Progenitor.Bootstrap.UnsafeHarmonySlot</c>, which Core fills through
    /// <c>Progenitor.Patches.UnsafeHarmonySlotLink</c> — so the two reloaders cannot disagree.</para>
    /// </summary>
    private static bool TryRefuseUnsafeSwap(string file, Loaded loaded)
    {
        try
        {
            foreach (var plugin in loaded.Plugins)
            {
                if (plugin == null) continue;
                string guid = IdentityOf(plugin).Guid;
                if (!Progenitor.Patches.UnsafeHarmonyUse.RefusesSwap(guid)) continue;
                Note(Progenitor.Patches.UnsafeHarmonyUse.FormatSwapRefused(
                    System.IO.Path.GetFileName(file), guid));
                return true;
            }
        }
        catch (Exception e)
        {
            // Never let the mark lookup stop the reloader; an un-refused swap is the old
            // behavior, and the once-per-plugin warning already went to the log.
            Plugin.Logger?.LogWarning("Reload: unsafe-Harmony check for "
                + System.IO.Path.GetFileName(file) + ": " + e.Message);
        }
        return false;
    }

    /// <summary>
    /// Job FENC2 (roadmap M4 item 5): the method-body inner loop. <c>tools/enc --watch</c> drops a
    /// <c>&lt;plugin&gt;.&lt;n&gt;.delta</c> here and this applies it with
    /// <c>MetadataUpdater.ApplyUpdate</c>, which replaces the bodies in place - no unload, no
    /// re-<c>Load()</c>, nothing owner-keyed touched. Four things must hold, and the file is
    /// deleted either way so a refusal is logged once rather than once a second:
    ///
    /// <list type="number">
    /// <item>the runtime allows metadata updates at all (the launcher set
    /// <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>; nothing this process does can turn it on);</item>
    /// <item>the header's baseline sha256 is the sha256 of the image the allowlist admitted for
    /// that plugin, so a delta from a stale build cannot reach a swapped plugin;</item>
    /// <item>the sequence is the next one expected, so a missed generation is a refusal rather
    /// than a silently skipped edit;</item>
    /// <item>the delta itself passes the allowlist. It is arbitrary IL - the eighth automatic
    /// code-loading path, gated exactly like the other seven.</item>
    /// </list>
    ///
    /// Job FENC3 (R38 finding 3) adds the order. The names are <c>&lt;plugin&gt;.&lt;n&gt;.delta</c>
    /// and an ordinal sort of them stops being generation order at ten, so
    /// <see cref="DeltaApply.NextInScanOrder"/> parses the sequence out of the name and this takes
    /// exactly ONE delta per tick, the lowest. The scan runs once a second; nothing waits long,
    /// and the second delta of a pair is never judged against a sequence the first just moved.
    ///
    /// No settle window: the watcher writes through a temp plus a move, so a delta is either
    /// there whole or not there. A delta whose <c>ApplyUpdate</c> throws (a rude edit the
    /// watcher's conservative classifier let through) is logged and dropped and <em>nothing else
    /// happens</em>: the developer's normal rebuild-and-deploy is the generation swap, and the
    /// watcher has already told them to do it. Mirrors <c>ReloaderHost.ScanDeltas</c>.
    /// </summary>
    private static void ScanDeltas()
    {
        string[] files;
        try { files = Directory.GetFiles(Folder, "*.delta"); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Reload: delta scan: " + e.Message); return; }
        if (files.Length == 0) return;

        string? file = DeltaApply.NextInScanOrder(files);
        if (file == null) return;
        {
            string fileName = System.IO.Path.GetFileName(file);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch { return; } // still landing; the next scan sees it
            ApplyDelta(fileName, bytes);
            try { File.Delete(file); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: could not delete {fileName}: {e.Message}"); }
        }
    }

    private static void ApplyDelta(string fileName, byte[] bytes)
    {
        if (!DeltaApply.IsSupported && System.Threading.Interlocked.Exchange(ref _loggedEncDisabled, 1) == 0)
            Note("Reload: EnC unavailable — " + DeltaApply.DisabledMessage);

        DeltaApply.TryParse(bytes, out var header, out string parseError);
        Loaded? target = null;
        if (header != null && header.Plugin.Length > 0)
        {
            foreach (var loaded in ByPath.Values)
            {
                // Job FENC7 (R42 finding 4): matched on the FILE STEM, the one identity in this
                // pipeline, through the shared test. The watcher never loads the image, so the
                // stem is the only name it can put in the header; matching on the assembly name
                // refused every delta for the life of the session wherever the two differed,
                // while FENC6's status file went on reading loaded=1 with the right sha.
                if (!DeltaApply.NamesPlugin(loaded.Stem, header.Plugin)) continue;
                target = loaded;
                break;
            }
        }

        var allow = ReloadAllowStore.Decide(fileName, ReloadAllowPolicy.Sha256Hex(bytes));
        var verdict = parseError.Length > 0
            ? DeltaVerdict.Malformed
            : DeltaApply.Decide(
                header, DeltaApply.IsSupported, target?.ImageSha,
                target?.NextDeltaSequence ?? DeltaApply.FirstSequence, allow,
                target?.Rewritten ?? false);

        if (verdict != DeltaVerdict.Apply)
        {
            string why = verdict == DeltaVerdict.Malformed && parseError.Length > 0
                ? parseError
                : DeltaApply.RefusalReason(verdict, header, target?.ImageSha,
                    target?.NextDeltaSequence ?? DeltaApply.FirstSequence);
            Note(DeltaApply.FormatRefused(fileName, why));
            // The watcher rebaselines off the status file, so it must be readable after a refusal
            // too: a refusal is precisely when the two sequences are out of step (job FENC3).
            WriteDeltaState(target);
            return;
        }

        if (!DeltaApply.TryApply(target!.Assembly, header!, bytes, out string applyError))
        {
            Note(DeltaApply.FormatRude(fileName, applyError));
            WriteDeltaState(target);
            return;
        }
        target.NextDeltaSequence = header!.Sequence + 1;
        Note(DeltaApply.FormatApplied(header));
        WriteDeltaState(target);
    }

    /// <summary>
    /// Job FENC3 (R38 finding 2): publish this plugin's delta position for <c>tools/enc</c>.
    ///
    /// The reloader's sequence and the watcher's were two counters that could not see each other,
    /// so a server restart, a run without <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>, or one rude
    /// apply desynchronized them permanently: the watcher counted up on every emit, the reloader
    /// stayed where it was, and every later delta was refused out-of-order and deleted until the
    /// developer rebuilt. The reloader's own counter is still <b>memory only</b> — it is per
    /// loaded image and dies with the process, which is the whole point — and this file is the
    /// one-way status channel the watcher reads to rebaseline from it.
    ///
    /// It is written NEXT TO the reload folder, never in it, so no scan can pick it up. Best
    /// effort by construction: the reload folder may be read-only in a locked-down install and a
    /// missing status file simply leaves the watcher where it was.
    /// </summary>
    private static void WriteDeltaState(Loaded? loaded)
    {
        // Job FENC6 (R41 finding 5): keyed on the FILE STEM, like every other touch of this file,
        // so a successful load OVERWRITES the loaded=0 refusal a previous failed load of the same
        // DLL left behind — including the ordinary case where that load threw before its assembly
        // name could be read. The assembly name still goes inside the file.
        if (loaded == null) return;
        DeltaApply.WriteStateFile(
            Folder, loaded.Stem, loaded.AssemblyName, loaded.ImageSha,
            loaded.NextDeltaSequence - DeltaApply.FirstSequence, loaded.Rewritten,
            loaded.LoadNonce);
    }

    /// <summary>
    /// The plugin is gone; so is its position. Pairs with <see cref="DropDeltasFor"/>. Keyed on
    /// the file stem (job FENC6), so it also clears a refusal written for an unreadable assembly.
    /// </summary>
    private static void DropDeltaStateFor(string? stem) => DeltaApply.DeleteStateFile(Folder, stem);

    /// <summary>
    /// Job FENC5 (R40 finding 9): a load that threw leaves a REFUSAL in the channel, not silence.
    ///
    /// The failed-load path routes through <see cref="Unload"/>, which drops the status file —
    /// correctly, because the position that file published belongs to an image that is gone.
    /// Stopping there left the watcher with no file at all, and a missing file used to read as
    /// "carry on": it kept emitting deltas for a plugin nothing held, every one refused for want
    /// of a target, and a refusal with no target writes no status file, so the channel never
    /// reopened. Writing <c>loaded=0</c> and the reason keeps it open, and the watcher blocks on
    /// it and prints the reason. An ordinary unload still deletes the file: nothing is loading,
    /// nothing failed, and the watcher's own missing-file rule says so.
    ///
    /// Job FENC6 (R41 finding 5): named by the FILE STEM, not by a best guess at the assembly
    /// name. FENC5 wrote the refusal under the stem only when the name was unreadable — which is
    /// the usual case here — while the success write and the unload delete keyed on the assembly
    /// name, so where the two differed nothing could ever clear this file and the watcher blocked
    /// on it for the rest of time. One key, every writer, every deleter.
    /// </summary>
    private static void WriteFailedDeltaState(string? stem, string? plugin, string? reason) =>
        DeltaApply.WriteFailedStateFile(Folder, stem, plugin, reason);

    /// <summary>
    /// A generation swap invalidates every delta: they were emitted against the image that is
    /// going away. Deleting them here is what stops the next scan from feeding a delta for
    /// baseline N to the freshly loaded image N+1 (it would be refused on the baseline check
    /// anyway; this keeps the log honest and the folder clean).
    ///
    /// Job FENC7 (R42 finding 4): by FILE STEM, because that is what the watcher names the file
    /// on disk and what the header inside it carries. Keyed on the assembly name this swept
    /// nothing wherever the two names differed.
    /// </summary>
    private static void DropDeltasFor(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return;
        try
        {
            foreach (var file in Directory.GetFiles(Folder, stem + ".*.delta"))
            {
                try { File.Delete(file); }
                catch { /* the next scan refuses it on the baseline check */ }
            }
        }
        catch { /* folder gone; nothing to drop */ }
    }

    /// <summary>
    /// One evaluation of a replacement image while the old plugin is still live: read the bytes,
    /// hash them, ask the operator's optional hash allowlist (job FRLD). Deliberately loads
    /// nothing. <see cref="ReplacementVerdict.Unreadable"/> is a non-answer, not a refusal —
    /// R30 finding 15: a transient read failure (the tool writing the DLL still has it open) used
    /// to be reported as "refused", which the caller turned into a Removal and a sweep of live
    /// player state. The bytes and the decision are handed back so <see cref="Load"/> does not
    /// read the file a second time, closing the TOCTOU where a file allowed at T1 is refused at
    /// T2 and the plugin never comes back. Mirrors <c>ReloaderHost.CheckReplacement</c>.
    /// </summary>
    private static ReplacementVerdict CheckReplacement(
        string file, out byte[]? bytes, out string? hash, out AllowDecision allow, out string readError)
    {
        bytes = null;
        hash = null;
        allow = default;
        readError = "";
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception e)
        {
            readError = e.Message;
            return ReplacementVerdict.Unreadable;
        }
        hash = ReloadAllowPolicy.Sha256Hex(bytes);
        allow = ReloadAllowStore.Decide(System.IO.Path.GetFileName(file), hash);
        return ReloadAllowPolicy.MayLoad(allow) ? ReplacementVerdict.Allowed : ReplacementVerdict.Refused;
    }

    /// <summary>
    /// First sight of a file: read and decide here. A replacement whose predecessor was just
    /// unloaded comes through the overload below carrying the decision its unload reason was
    /// taken from.
    /// </summary>
    private static Loaded? Load(string file, DateTime write)
    {
        string fileName = System.IO.Path.GetFileName(file);

        // Job FRLD: hash and check the bytes before a collectible ALC exists, so a file the
        // operator's optional BepInEx/config/progenitor-allow.txt does not carry never
        // reaches LoadFromStream. Scan stamps Failed when this returns null, so the refusal
        // is logged once per version of the file. Mirrors ReloaderHost.Load in Bootstrap.
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception e)
        {
            Note($"Reload: reading {fileName} failed: {e.Message}");
            return null;
        }

        string firstHash = ReloadAllowPolicy.Sha256Hex(bytes);
        return Load(file, write, bytes, firstHash, ReloadAllowStore.Decide(fileName, firstHash));
    }

    private static Loaded? Load(string file, DateTime write, byte[] bytes, string hash, AllowDecision allow)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        string fileName = System.IO.Path.GetFileName(file);

        long size = bytes.LongLength;
        Note(ReloadAllowPolicy.FormatPluginLoad(fileName, hash, size, allow));
        if (!ReloadAllowPolicy.MayLoad(allow))
        {
            Note(ReloadAllowPolicy.FormatRefusedPlugin(fileName, hash, size, allow));
            return null;
        }

        // Job FCOMPAT2 (M4 item 2, docs/modules.md "Compatibility rewriter"): absorb interop
        // drift in this plugin's IL, between the allow gate and LoadFromStream. Three properties
        // of that position are deliberate (research compat-rewriter.md §2.1/§4.1): the allowlist
        // verdict above was taken on the ORIGINAL bytes and is never re-taken, a refused file is
        // therefore never parsed by Cecil, and the rewritten image is never hashed for a gate and
        // never written to disk. [Compat] RewriteMode defaults to DryRun, which reports what
        // would change and hands back this same array. Mirrors ReloaderHost.Load in Bootstrap.
        //
        // Job FCOMPAT3 adds two things to the same hunk. The allowlist is handed to the rewriter
        // as a delegate so the mirrored file can gate the drift map on the operator's list
        // without naming a type that lives in a different namespace on each side; and the pass
        // now walks the plugin's own folder for local dependency DLLs and rewrites them
        // leaf-first, each through this same gate order, so a helper DLL that names a moved type
        // is fixed before the plugin that calls into it.
        CompatRewriteHost.AllowTokenReader ??= static (n, h) => ReloadAllowPolicy.Token(ReloadAllowStore.Decide(n, h));
        var original = bytes;
        var rewrite = CompatRewriteHost.RewriteForLoad(Paths.BepInExRootPath, file, bytes, Note);
        bytes = rewrite.Image;
        // Job FENC3 (R38 finding 1). RewriteBundle.Image is documented as "the input array unless
        // applied", so this reference comparison is the only place the rewrite verdict exists as a
        // fact rather than as a hope: after this line nothing can tell a rewritten load from an
        // unrewritten one, because ImageSha is the hash of the ORIGINAL bytes in both cases and
        // that is exactly the number tools/enc computes from the same file on disk. A preloaded
        // dependency counts too — the plugin binds a rewritten helper, which is the same mismatch
        // one level down.
        bool rewritten = !ReferenceEquals(rewrite.Image, original) || rewrite.Dependencies.Count > 0;

        var context = new AssemblyLoadContext($"progenitor-reload:{name}:{write.Ticks}", isCollectible: true);
        // Leaf-first, into the plugin's own collectible context and BEFORE the plugin: a name
        // already loaded in an ALC wins over the file on disk, which is how the rewritten
        // dependency becomes the one the plugin binds. Empty in every mode but Apply.
        rewrite.PreloadInto(
            (_, image) =>
            {
                using var dependency = new MemoryStream(image, writable: false);
                context.LoadFromStream(dependency);
            },
            Note);
        // ImageSha is the hash the allowlist decided on -- the ORIGINAL bytes, before the compat
        // rewriter touched them -- because that is the identity a delta names. When the rewriter
        // actually changed something there is no such identity: the baseline is CLEARED and every
        // delta for this plugin is refused with DeltaApply.RewrittenMessage. Until job FENC3 the
        // comment here claimed the baseline check enforced that, which it could not: the two
        // hashes are hashes of the same bytes, so they were always equal (R38 finding 1).
        var loaded = new Loaded
        {
            Path = file,
            Stem = name, // job FENC6: the status file's one key, known before anything loads
            LastWrite = write,
            Context = context,
            BoundCore = typeof(Plugin).Assembly,
            ImageSha = rewritten ? "" : hash,
            Rewritten = rewritten,
        };
        loaded.Resolving = (_, an) => OnPluginResolving(loaded, an);
        context.Resolving += loaded.Resolving;
        try
        {
            Assembly assembly;
            using (var stream = new MemoryStream(bytes, writable: false))
                assembly = context.LoadFromStream(stream);

            loaded.Assembly = assembly;
            string asmName;
            try { asmName = assembly.GetName().Name ?? ""; }
            catch { asmName = ""; }
            loaded.AssemblyName = asmName;
            Note(ReloadAllowPolicy.FormatPluginLoadVerbose(fileName, hash, size, asmName));
            if (rewritten)
                Note("Reload: " + asmName + " takes no method-body deltas — " + DeltaApply.RewrittenMessage);
            // Job FENC3: publish the starting position (last applied 0, so the next expected is 1)
            // so a watcher that was already running resynchronizes to this load instead of
            // counting up against a sequence that just went back to the beginning.
            WriteDeltaState(loaded);

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(BasePlugin).IsAssignableFrom(type)) continue;
                // Job FREL1 (R61 finding 1): DiscoveryInherit, and it is false, because that is
                // what BepInEx's MetadataHelper uses. Discovery used to inherit (the default of
                // the generic overload) while BepInEx did not, so a type that took [BepInPlugin]
                // from a base class was admitted here and then described by nobody: every
                // GetMetadata read in this file returned null and NRE'd. Agreeing with BepInEx
                // removes the trigger at the root rather than guarding its five symptoms.
                if (type.GetCustomAttribute<BepInPlugin>(DiscoveryInherit) == null)
                {
                    // Say so once rather than dropping it silently: a plugin whose attribute is
                    // inherited is not a BepInEx plugin — BepInEx's own chainloader would not
                    // load it either — and "has no BepInPlugin types" below would be misleading.
                    if (type.GetCustomAttribute<BepInPlugin>(inherit: true) != null)
                        Plugin.Logger?.LogWarning(
                            $"Reload: {type.FullName} inherits [BepInPlugin] from a base type; BepInEx reads "
                            + "the attribute with inherit:false and cannot describe it, so it is not loaded. "
                            + "Put [BepInPlugin] on the plugin class itself.");
                    continue;
                }

                var plugin = (BasePlugin)Activator.CreateInstance(type)!;
                var identity = IdentityOf(plugin);
                plugin.Load();
                loaded.Plugins.Add(plugin);
                Note($"Reload: loaded {identity.Name} {identity.Version} from {System.IO.Path.GetFileName(file)}.");
            }
            if (loaded.Plugins.Count == 0)
                Plugin.Logger?.LogWarning($"Reload: {System.IO.Path.GetFileName(file)} has no BepInPlugin types.");
            return loaded;
        }
        catch (Exception e)
        {
            Note($"Reload: loading {System.IO.Path.GetFileName(file)} failed: {e}");
            // Whatever of this image did load is going away for good.
            Unload(loaded, PluginUnloadReason.Removal);
            // Job FENC5 (R40 finding 9): Unload just dropped the status file; reopen it as a
            // refusal so the watcher blocks with this reason instead of emitting deltas into a
            // process that holds no such plugin. Job FENC6 (R41 finding 5): the FILE STEM is the
            // key the watcher looks this file up by and the key every writer and the deleter use,
            // so the refusal lands where the next successful load of this DLL will overwrite it.
            // The assembly name, if the load got far enough to read one, goes inside the file.
            WriteFailedDeltaState(loaded.Stem, loaded.AssemblyName, e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Unload one file's plugins. <paramref name="reason"/> is published on
    /// <see cref="PluginUnloadScope"/> for the duration of each <c>plugin.Unload()</c>, so a
    /// <c>ProgenitorPlugin</c> sees it in <c>OnUnload(ctx, reason)</c> (job FBUFF2).
    /// <see cref="SweepRegistrations"/> stays reason-blind on purpose: everything it drops is a
    /// Core-owned, owner-keyed handle (units, beams, panels, effects, commands, hooks) living in
    /// the Core generation that is about to die. Keeping those across a swap would orphan the
    /// entity or GameObject with no handle left to dispose it — the opposite of the buff case,
    /// where the instance is a plain persisted game entity the next generation can find by query.
    /// </summary>
    private static void Unload(Loaded loaded, PluginUnloadReason reason)
    {
        var context = loaded.Context;
        var asm = loaded.Assembly;
        var resolving = loaded.Resolving;
        loaded.Resolving = null;
        loaded.Assembly = null;
        loaded.BoundCore = null;
        loaded.Context = null;
        Failed.Remove(loaded.Path);

        // Drop the Resolving capture of `loaded` (and BoundCore) before Unload so
        // the ALC is not kept alive by its own handler after bookkeeping is gone.
        if (context != null && resolving != null)
        {
            try { context.Resolving -= resolving; }
            catch { /* already unloading */ }
        }

        // Pass the ALC name explicitly so MainThread can retire it without GetLoadContext
        // on an assembly that is about to unload (and without storing that Assembly on
        // queued work — WorkItem keeps only the name strings).
        string? contextName = null;
        try { contextName = context?.Name; }
        catch { contextName = null; }

        // ---- unload steps: Unload (job FREL4, R63 finding 3): each one guarded on its own ----
        // The marker is not decoration: ReloadHardeningTests.Every_unload_step_is_inside_a_try
        // enumerates every statement between it and the matching end marker and fails on a bare
        // call. It brackets THIS method, not just the sweep it calls: R63 finding 3 found FREL3's
        // pair sitting around SweepRegistrations only, so the five bare statements here — the two
        // the deltas travel on, the owned-assembly derivation (now wrapped), the identity and the
        // list clear — were outside the pin, in the method whose last step is context.Unload().
        // Anything that escapes here skips that call, the collectible plugin ALC is never asked to
        // unload, and CoreLoader's PluginAlcsAlive refusal then blocks every later Core swap.
        //
        // Job FENC2: a generation swap invalidates every delta for this plugin (they were emitted
        // against the image that is going away), so they go with it. Job FENC3: and so does the
        // status file, because the position it publishes belongs to the image that is unloading.
        // Job FENC7 (R42 finding 4): both by STEM. The deltas and the status file are two
        // halves of one channel and they now share one key -- the plugin DLL's file stem, the
        // only name available before anything loads and the only name the watcher can produce.
        DropDeltasFor(loaded.Stem);
        DropDeltaStateFor(loaded.Stem);

        // Job FCOMPAT4 (R34 finding 4): the sweeps below are keyed on an ASSEMBLY, and since
        // FCOMPAT3 a plugin can have more than one — the rewriter preloads each rewritten
        // plugin-local dependency into this same collectible context. Every one of them is
        // owned by this plugin and has to be swept with it, or a handler declared in a helper
        // DLL keeps the LoaderAllocator alive and the ALC never collects. The owner id is the
        // context itself; PluginOwnedAssemblies.Of derives the set from it.
        //
        // Job FREL4 (R63 finding 3): guarded rather than exempted. PluginOwnedAssemblies.Of
        // documents "never throws and never returns null" and its body is a try/catch, but
        // "guarded by callee courtesy" is not the same claim as "guarded" — the sentence FREL3
        // wrote one method down about RemoveEventHandlers — and this one stands in front of
        // context.Unload(). The fallback is the sweep this code did before FCOMPAT4: the plugin
        // assembly alone, which is never worse than doing nothing.
        IReadOnlyList<Assembly> owned;
        try { owned = Progenitor.Compat.PluginOwnedAssemblies.Of(asm, context); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Reload: PluginOwnedAssemblies.Of: {e.Message}");
            owned = asm == null ? Array.Empty<Assembly>() : new[] { asm };
        }

        foreach (var plugin in loaded.Plugins)
        {
            // Job FREL1 (R61 finding 1): resolved once, before anything, and never null. This
            // used to be a MetadataHelper.GetMetadata(plugin) dereference INSIDE the try below
            // and AHEAD of plugin.Unload(), so a plugin BepInEx could not describe never got to
            // run its own teardown at all — the NRE landed in the catch and the log line blamed
            // "Unload threw". The identity is also what the sweep and the Harmony unpatch use,
            // so all of them now agree and none of them can throw for want of metadata.
            var identity = IdentityOf(plugin);

            try
            {
                bool ok;
                using (PluginUnloadScope.Push(reason))
                    ok = plugin.Unload();
                Note($"Reload: unloaded {identity.Name}: {(ok ? "clean" : "plugin reported it cannot unload; continuing anyway")}");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Reload: Unload threw for {identity.Name}: {e}");
            }

            // Always sweep even when Unload threw: a raw BasePlugin does not get
            // ProgenitorPlugin.UndoAll, and a throwing Unload still leaves Harmony
            // delegates that pin the collectible ALC. Job FREL1: and guarded, so that a throw
            // out of the sweep's own preamble cannot skip the DropOwnedBy loop below it — every
            // step of this teardown is independent of the one before it.
            try { SweepRegistrations(plugin, owned, identity); }
            catch (Exception e) { Plugin.Logger?.LogError($"Reload: SweepRegistrations for {identity.Name}: {e}"); }
            for (int i = 0; i < owned.Count; i++)
            {
                try { MainThread.DropOwnedBy(plugin, owned[i], i == 0 ? contextName : null); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: MainThread.DropOwnedBy: {e.Message}"); }
            }
        }
        loaded.Plugins.Clear();

        for (int i = 0; i < owned.Count; i++)
        {
            try { HarmonySlotHandoff.ClearPluginContext(owned[i], i == 0 ? contextName : null); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: HarmonySlotHandoff.ClearPluginContext: {e.Message}"); }

            try { MainThread.DropOwnedBy(plugin: null, owned[i], i == 0 ? contextName : null); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: MainThread.DropOwnedBy(assembly): {e.Message}"); }
        }

        try { context?.Unload(); } catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: context unload: {e.Message}"); }
        // ---- end unload steps: Unload ----
    }

    private static Assembly? OnPluginResolving(Loaded loaded, AssemblyName name)
    {
        if (string.Equals(name.Name, "Progenitor", StringComparison.Ordinal))
            return loaded.BoundCore;
        if (string.Equals(name.Name, "Progenitor.Bootstrap", StringComparison.Ordinal))
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, "Progenitor.Bootstrap", StringComparison.Ordinal))
                    return asm;
            }
        }
        return null;
    }

    /// <summary>
    /// Drop every owner-keyed registration this plugin instance could have left behind, then
    /// unpatch Harmony ids matching its BepInPlugin GUID. A raw <see cref="BasePlugin"/> never
    /// runs <c>ProgenitorPlugin.UndoAll</c>, so VirtualSystems / Localization / PrefabRegistry
    /// have to be swept here too or a sim tick can jump into a collectible ALC. SystemHooks
    /// <c>RemoveAll</c> drops leftover Before/After hooks; <see cref="Plugin.RemoveFrameHandlers"/>
    /// drops <c>Plugin.Frame</c> subscribers; Harmony ids that start with the plugin GUID
    /// (suffixes like <c>.chat_observe</c>) are UnpatchID'd as well as the bare GUID.
    /// <see cref="PluginStaticApiSweep.Run"/> releases the static-API effect, panel and
    /// server-beam handles (R26 finding 6, R27 findings 4 and 25) and logs the real counts.
    /// Bootstrap's reloader calls this so Core modules are swept without Bootstrap
    /// referencing them.
    /// </summary>
    internal static void SweepRegistrations(BasePlugin plugin) => SweepRegistrations(plugin, null);

    /// <summary>
    /// <paramref name="owned"/> is the plugin assembly plus the rewritten dependency assemblies
    /// loaded into its collectible context (R34 finding 4). Null means "work it out from the
    /// plugin", which is the path Bootstrap's reloader takes: it reaches this method through
    /// <c>ProcessHost.SweepPlugin</c>, an <c>Action&lt;BasePlugin&gt;</c> that carries the plugin
    /// and nothing else, so the set is derived from the plugin's own load context here rather
    /// than handed across that seam.
    /// </summary>
    internal static void SweepRegistrations(BasePlugin plugin, IReadOnlyList<Assembly>? owned)
        => SweepRegistrations(plugin, owned, IdentityOf(plugin));

    /// <summary>
    /// Job FREL1 (R61 finding 1): <paramref name="identity"/> is resolved once per unload by
    /// <see cref="IdentityOf"/> and passed in, so the GUID the Units sweep claims handles by and
    /// the GUID the Harmony unpatch removes trampolines by are the same value read once, and
    /// neither can throw for want of BepInEx metadata.
    /// </summary>
    private static void SweepRegistrations(BasePlugin plugin, IReadOnlyList<Assembly>? owned, PluginIdentity identity)
    {
        // ---- unload steps: SweepRegistrations (job FREL3, R62 finding 3): each one guarded on its own ----
        // The marker is not decoration: ReloadHardeningTests.Every_unload_step_is_inside_a_try
        // enumerates every statement between it and the end marker and fails on a bare call, so a
        // sixteenth step added without a try goes red here instead of waiting for a review. Only
        // the plugin's own logger calls are exempt, and the exemption list in that test names them.
        //
        // Job FREL5 (R64 finding 7): the marker starts HERE, above the owned-assembly derivation,
        // not below it. The derivation was this method's first statement and sat one line outside
        // its own bracket, so the pin could not see it — R63 finding 3's shape ("a hand-placed
        // marker is a silent way past the pin") recurring one method down — and it is a bare
        // PluginOwnedAssemblies.Of plus a plugin.GetType() dereference, which is exactly the pair
        // FREL4 stopped exempting in Unload. Same treatment as there: wrapped, with the pre-
        // FCOMPAT4 behavior as the fallback (the plugin assembly alone, never worse than nothing).
        if (owned == null)
        {
            try { owned = Progenitor.Compat.PluginOwnedAssemblies.Of(plugin.GetType().Assembly); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"Reload: PluginOwnedAssemblies.Of: {e.Message}");
                try { owned = new[] { plugin.GetType().Assembly }; }
                catch { owned = Array.Empty<Assembly>(); }
            }
        }

        // Weakly, for the pin diagnosis: a context recorded here that is still alive at the next
        // Core swap names its Core holders (pin-diag plugin-holders).
        try { PluginPinHolders.NoteUnloaded(plugin.GetType(), identity.Name); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: PluginPinHolders.NoteUnloaded: {e.Message}"); }

        try { EventBus.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: EventBus.RemoveAll: {e.Message}"); }

        // Job FREL3 (R62 finding 3): guarded like its fourteen neighbors. It was the one call in
        // this method whose safety was a property of somewhere else — PluginOwnedAssemblies wraps
        // each EventBus.RemoveDeclaredIn internally — in a method whose whole design is that each
        // step runs whatever the step before it did. "Guarded by callee courtesy" is not the same
        // claim as "guarded", and only the second one survives an edit to the callee.
        try { Progenitor.Compat.PluginOwnedAssemblies.RemoveEventHandlers(owned); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: PluginOwnedAssemblies.RemoveEventHandlers: {e.Message}"); }

        try { CommandRegistry.UnregisterOwner(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: CommandRegistry.UnregisterOwner: {e.Message}"); }

        try { VirtualSystems.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: VirtualSystems.RemoveAll: {e.Message}"); }

        try { Localization.Localization.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: Localization.RemoveAll: {e.Message}"); }

        // Wearer before definition: Buffs.SweepForUnload must run before PrefabRegistry.UnregisterAll.
        // A plugin that applied through the static Buffs API otherwise has its buff definition
        // retired while its live instances are still on wearers. The tracked ctx.Buffs path is
        // only ordered correctly by DisposableBag's LIFO undo when the plugin registered before it
        // applied; this sweep order is the guarantee. Same rule as Core uninstall (Buffs before
        // Units). The id is passed for the same reason as Units.SweepForUnload below.
        try { Progenitor.Buffs.Buffs.SweepForUnload(plugin, identity.Guid); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: Buffs.SweepForUnload: {e.Message}"); }

        try { PrefabRegistry.UnregisterAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: PrefabRegistry.UnregisterAll: {e.Message}"); }

        try { SystemHooks.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: SystemHooks.RemoveAll: {e.Message}"); }

        try { Plugin.RemoveFrameHandlers(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: Plugin.RemoveFrameHandlers: {e.Message}"); }

        // Job FUNIT6 (R59 findings 1 and 4): the plugin id is passed, not read off the plugin.
        // The sweep claims a handle by the id recorded at Spawn as well as by owner reference,
        // and BasePlugin exposes no GUID property for it to read, so a plugin that spawned with
        // a bare owner token was filed as a stranger and left playing. This is also why the
        // entry point is SweepForUnload rather than the public RemoveAll: the assembly-wide part
        // is teardown, not something Units.RemoveAll(owner) should do for a live plugin.
        //
        // Job FEX9 (R60 finding 5) guarded the metadata read here; job FREL1 (R61 finding 1)
        // moved the whole question into IdentityOf, which resolves it once per unload and cannot
        // return null. The GUID may still be empty (nothing readable supplied one); SweepForUnload
        // accepts that and matches by owner reference alone.
        if (!identity.HasGuid)
            Plugin.Logger?.LogWarning(
                $"Reload: no BepInPlugin GUID for {plugin.GetType().FullName}; sweeping its units by owner reference only.");
        try { Progenitor.Units.Units.SweepForUnload(plugin, identity.Guid); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: Units.SweepForUnload: {e.Message}"); }

        // Job FEX7: a WhenReady callback registered on a store that is still pending holds a
        // delegate from the plugin assembly, and it would pin the ALC until the merge. Drop it
        // here like every other registration.
        //
        // Job FEX8 (R59 finding 10): `owned` is passed, not just the plugin. RemoveAll reduced the
        // plugin to one simple name, so a callback declared in a rewritten dependency inside the
        // same collectible context — the very set this method computed eighteen lines earlier for
        // PluginOwnedAssemblies — did not match and stayed pending, holding a delegate into the
        // ALC this sweep is emptying.
        try { Progenitor.Persistence.ModSave.RemoveAll(plugin, owned); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: ModSave.RemoveAll: {e.Message}"); }
        // The server-level stores take WhenReady callbacks too: same sweep, same owned set.
        try { Progenitor.Persistence.ServerSave.RemoveAll(plugin, owned); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: ServerSave.RemoveAll: {e.Message}"); }

        // Job FHARM: reason-blind on purpose. Unlike a buff instance there is nothing to keep
        // across a Swap — the patch method and the guard's owner reference both live in the
        // plugin assembly that is about to be unloaded, so an applied patch pins its ALC and a
        // leftover guard leaves a player unkillable with nothing able to undo it.
        try { Progenitor.Patches.PatchRegistry.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: PatchRegistry.RemoveAll: {e.Message}"); }

        // A plugin's GameHelper.LaunchGame bind chained behind Core's GcBeforeLaunch dispatcher
        // is a Core static root into the plugin ALC; Bootstrap's slot sweep cannot see it.
        try { Progenitor.Networking.GcBeforeLaunch.ClearChainForAssemblies(owned); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: GcBeforeLaunch.ClearChainForAssemblies: {e.Message}"); }

        try { Progenitor.GodMode.GodMode.RemoveAll(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: GodMode.RemoveAll: {e.Message}"); }
        try { Progenitor.Buffs.DebugProtection.Forget(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: DebugProtection.Forget: {e.Message}"); }

        // Job FCFG13: the backstop for the drop PluginContext.Attach tracks. That one runs from
        // ProgenitorPlugin's UndoAll, and there are two unloads it never sees — a raw BasePlugin,
        // which has no context at all, and a Unload that threw before reaching UndoAll (the
        // metadata read at the top of ProgenitorPlugin.Unload is one such throw, and it is the
        // shape FREL1 found one file over). This sweep runs after every one of them. The drop is
        // idempotent, so on the ordinary path it finds the registration already gone and returns
        // false; plugin.Config is BepInEx's own property and is read inside the guard.
        try { Progenitor.Config.ConfigHelper.DropWatchedFile(plugin.Config); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: ConfigHelper.DropWatchedFile: {e.Message}"); }

        // Assets / Ui / ServerBeam, with per-API guards and one accurate log line
        // (R27 findings 4 and 25). This is PluginStaticApiSweep's only production caller;
        // do not inline these again.
        try { PluginStaticApiSweep.Run(plugin); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: PluginStaticApiSweep.Run: {e.Message}"); }

        // Job FREL1 (R61 finding 1): this used to be its own MetadataHelper.GetMetadata(plugin)
        // dereference, and it is the one whose failure costs the most — an NRE here landed in the
        // catch and the unloading plugin's Harmony patches were never unpatched by id, leaving the
        // IL2CPP trampolines that are THE pin on a collectible generation (CLAUDE.md, Step D).
        // The identity is resolved once at the top of the unload now; when it carries no GUID
        // there is no id to unpatch, so say that instead of throwing it away silently.
        if (!identity.HasGuid)
        {
            Plugin.Logger?.LogWarning(
                $"Reload: no BepInPlugin GUID for {plugin.GetType().FullName}; its Harmony patches cannot be "
                + "unpatched by id and will pin this load context if it applied any.");
        }
        else
        {
            try { HarmonyFallback.UnpatchOwnerAndSuffixes(identity.Guid); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: Harmony.UnpatchID: {e.Message}"); }
        }

        // Last, after the plugin's own Unload has run (it may serialize on the way out): a type the
        // plugin handed to System.Text.Json is cached by the process-wide default options for the
        // life of the process and pins this context — and through it the Core generation. Found by
        // a live dump on 2026-09-25; see JsonCacheSweep.
        try
        {
            int dropped = JsonCacheSweep.ReleaseProcessCaches(out var unsupported);
            if (unsupported != null)
                Plugin.Logger?.LogWarning($"Reload: JsonCacheSweep: this runtime has no {unsupported}; a type this plugin serialized may pin its context.");
            else if (dropped > 0)
                Plugin.Logger?.LogInfo($"Reload: dropped {dropped} System.Text.Json cache entr{(dropped == 1 ? "y" : "ies")} for collectible types after {identity.Name}.");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: JsonCacheSweep: {e.Message}"); }
        // ---- end unload steps: SweepRegistrations ----
    }
}
