using System;
using System.Collections.Generic;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free decisions for a collectible Core swap. <see cref="CoreLoader"/> and the unit
/// tests share this so generation naming, the "previous ALC still alive" refuse, and the
/// bounded collect wait cannot drift from what the live cascade logs.
/// </summary>
public static class CoreReloadPolicy
{
    /// <summary>
    /// Prefix of every Core <c>AssemblyLoadContext.Name</c>. Logged and grepped as
    /// <c>progenitor-core:{generation}</c>. Step E watches <c>Progenitor.dll.next</c>
    /// and requests a swap with reason <c>watch</c>; the generation name stays
    /// request-driven so two settled copies cannot collide.
    /// </summary>
    public const string AlcNamePrefix = "progenitor-core";

    /// <summary>
    /// FrameTicker ticks between Unity teardown (Core Uninstall, <c>DestroyByPrefix</c>,
    /// <c>UiSlots.ClearAll</c>, drop-core-work, null-runtime) and <c>ALC.Unload</c>.
    /// Default 1: EventSystem and every other MonoBehaviour of the teardown frame
    /// must run against dead/unwired GameObjects <em>before</em> the collectible
    /// ALC is asked to unload. 0 is same-tick Unload (tests / no Unity). See
    /// <c>client-hot-swap-crash.md</c>.
    /// </summary>
    public const int UnityFlushFrames = 1;

    /// <summary>
    /// True when Uninstall may <c>DestroyImmediate</c> UI/Effects roots. Only safe
    /// when <see cref="UnityFlushFrames"/> yields at least one FrameTicker tick
    /// before <c>ALC.Unload</c>; otherwise use deferred <c>Object.Destroy</c> so
    /// EventSystem of this frame still sees live (unwired) objects.
    /// </summary>
    public static bool MayDestroyImmediateOnUninstall => UnityFlushFrames >= 1;

    /// <summary>
    /// Il2CppInterop <c>NativeToManagedTrampolines</c> and Harmony IL2CPP
    /// <c>DelegateCache</c> are append-only for process lifetime (upstream;
    /// <c>hot-reload-prior-art.md</c>). Removing a row drops the GC root while
    /// Unity may still hold <c>method_ptr</c> — that is the client Core-swap AV
    /// (evidence 94). Pin-diag them; never delete.
    /// </summary>
    public const bool MayMutateDelegateCaches = false;

    /// <summary>Grep line after Unity teardown, before the flush yield. <c>frame</c> is Unity <c>Time.frameCount</c>.</summary>
    public static string FormatUnityTeardown(int frame) =>
        "unity-teardown frame=" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Grep line when <c>ALC.Unload</c> is requested. Expected <c>F+1</c> after <see cref="FormatUnityTeardown"/>.</summary>
    public static string FormatUnloadAlcFrame(int frame) =>
        "unload-alc frame=" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Grep line on the first collect-poll pulse. Expected <c>F+2</c> after <see cref="FormatUnityTeardown"/>.</summary>
    public static string FormatCollectFrame(int frame) =>
        "collect frame=" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Pin-release line for DelegateSupport. Count only; the cache is never mutated
    /// on a Core swap.
    /// </summary>
    public static string FormatDelegateSupportAppendOnly(int coreRows) =>
        "pin-diag DelegateSupport NativeToManagedTrampolines core="
        + coreRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " (append-only; not removed)";

    /// <summary>
    /// Pin-release line for Harmony IL2CPP <c>DelegateCache</c>. Count only.
    /// </summary>
    public static string FormatHarmonyIl2CppAppendOnly(int coreRows) =>
        "pin-diag HarmonyIL2CPP DelegateCache core="
        + coreRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " (append-only; not removed)";

    /// <summary>
    /// How many <em>frames</em> to poll <c>WeakReference.IsAlive</c> after <c>ALC.Unload</c>
    /// before deciding restore vs load N+1. Core-ALC pulses are
    /// <see cref="ShouldPulseCoreCollect"/> (every frame for the first
    /// <see cref="CollectPulseEveryFrameUntil"/> regardless of hitch backoff, then
    /// every <see cref="CollectPulseStrideAfter"/>th) so a miss does not hitch the
    /// main thread 60 times (evidence 70). Floor is 60 (~1 s at 60 fps): the
    /// dry-run collected at 11 frames, and the real path that waited 30 and never
    /// collected was pinned by a restore reference, not by a short window. A remaining
    /// GCHandle / Harmony <c>MethodInfo</c> will not collect in a longer window either,
    /// and stacking another generation on top is the failure we refuse.
    /// </summary>
    public const int CollectWaitFrames = 60;

    /// <summary>
    /// How many frames to poll plugin-ALC <c>WeakReference</c>s after
    /// <c>UnloadAll</c> before refusing the swap. Same budget as Core: a plugin
    /// ALC that still pins Core after this is not going to collect in a longer
    /// window either. Evidence 59: gen 1→2 collected because the original plugin
    /// ALCs were already gone; gen 2→3 missed because the cascade never waited
    /// for Sample/Dev's collectible contexts.
    /// </summary>
    public const int PluginAlcWaitFrames = 60;

    /// <summary>
    /// FrameTicker ticks between blocking <c>GC.Collect</c> +
    /// <c>WaitForPendingFinalizers</c> pulses while waiting on <em>plugin</em> ALCs.
    /// Core-ALC collect uses <see cref="ShouldPulseCoreCollect"/>. 0 would be every
    /// plugin-wait tick (the hitch R13 named).
    /// </summary>
    public const int CollectPulseEveryFrames = 10;

    /// <summary>
    /// Core-ALC collect pulses every frame for this many frames so a generation
    /// that actually dies still collects in 3–4 ticks (verified live). After this,
    /// pulses drop to every <see cref="CollectPulseStrideAfter"/>th frame.
    /// </summary>
    public const int CollectPulseEveryFrameUntil = 10;

    /// <summary>
    /// Core-ALC collect pulse stride after <see cref="CollectPulseEveryFrameUntil"/>.
    /// A pinned generation then hitches ~10 extra times across the 60-frame budget,
    /// not 50.
    /// </summary>
    public const int CollectPulseStrideAfter = 5;

    /// <summary>
    /// A blocking pulse slower than this, twice in a row, backs Core-ALC collect
    /// off to <see cref="HitchBackoffStride"/> so a miss does not hitch the player
    /// loop on every remaining frame.
    /// </summary>
    public const int HitchBackoffMs = 8;

    /// <summary>Consecutive slow pulses that trigger <see cref="HitchBackoffStride"/>.</summary>
    public const int HitchBackoffAfter = 2;

    /// <summary>
    /// Core-ALC pulse stride while hitch backoff is active (every 10th frame).
    /// Plugin-ALC wait already uses <see cref="CollectPulseEveryFrames"/>.
    /// </summary>
    public const int HitchBackoffStride = 10;

    /// <summary>
    /// FrameTicker ticks between <c>AssemblyLoadContext.All</c> walks while a
    /// plugin-ALC latch is live (R17 finding 5). Cheap <c>WeakReference.IsAlive</c>
    /// compact can run more often; the enumerator must not.
    /// </summary>
    public const int PluginLatchPollEveryFrames = 30;

    /// <summary>
    /// Bounded-wait helper used by unit tests of <see cref="WaitUntilCollected"/>. The
    /// live cascade does not call that helper: it uses <see cref="ShouldPulseCoreCollect"/>
    /// up to <see cref="CollectWaitFrames"/>.
    /// </summary>
    public const int CollectWaitAttempts = 20;

    /// <summary>Unused by the live cascade (no <c>Thread.Sleep</c> on the main thread).</summary>
    public const int CollectPulseMilliseconds = 50;

    /// <summary>First successful load is generation 1, not 0.</summary>
    public const int FirstGeneration = 1;

    public static string AlcName(int generation)
    {
        if (generation < 1)
            throw new ArgumentOutOfRangeException(nameof(generation), "Core generation is 1-based.");
        return AlcNamePrefix + ":" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="alcName"/> is a Core generation context.</summary>
    public static bool IsCoreAlcName(string? alcName)
    {
        if (string.IsNullOrEmpty(alcName)) return false;
        return alcName.StartsWith(AlcNamePrefix + ":", StringComparison.Ordinal);
    }

    /// <summary>
    /// Next generation to load. <paramref name="current"/> 0 (never loaded) becomes
    /// <see cref="FirstGeneration"/>.
    /// </summary>
    public static int NextGeneration(int current) => current < FirstGeneration ? FirstGeneration : current + 1;

    /// <summary>
    /// Whether <c>RequestReload</c> may start the cascade on this call. A swap nested inside
    /// Core's tick is scheduled for the next Bootstrap tick instead (not a hard refuse).
    /// A previous generation that did not collect is a hard refuse: stacking ALCs is how
    /// a later <c>SystemBase.Update</c> jumps into a dead context.
    /// </summary>
    public static bool MayStart(bool previousGenerationAlive, bool alreadyReloading) =>
        !previousGenerationAlive && !alreadyReloading;

    public static string RefuseAlreadyReloading { get; } =
        "Core reload refused: a swap is already in progress.";

    public static string ScheduledInsideTick { get; } =
        "Core reload scheduled: cannot swap from inside Core tick.";

    /// <summary>
    /// Operator-facing line when <c>RequestReload</c> only queues the cascade. The
    /// swap itself runs on a later Bootstrap <c>FrameTicker</c> tick so no plugin or
    /// Core frame is on the stack at collect time. Grep line for the live check.
    /// </summary>
    public static string FormatScheduled(int generation) =>
        "scheduled gen=" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Same as <see cref="FormatScheduled(int)"/> with a dry-run marker.</summary>
    public static string FormatScheduled(int generation, bool dryRun) =>
        dryRun
            ? FormatScheduled(generation) + " dryRun"
            : FormatScheduled(generation);

    /// <summary>
    /// Logged after the collect poll, once per swap. <paramref name="frames"/> is how
    /// many FrameTicker ticks the driver waited before the decision, not the GC pulse
    /// count (Core-ALC pulses are <see cref="ShouldPulseCoreCollect"/>).
    /// </summary>
    public static string FormatCollectWait(int frames, bool collected) =>
        "collect-wait frames=" + frames.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " collected=" + collected;

    /// <summary>
    /// Grep line per plugin ALC at the end of the plugin-collect wait. Name is the
    /// <c>AssemblyLoadContext.Name</c> (<c>progenitor-reload:Sample:…</c>).
    /// </summary>
    public static string FormatPluginAlcCollect(string? name, bool collected, int frames) =>
        "plugin-alc " + (name ?? "")
        + " collected=" + collected
        + " frames=" + frames.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Operator-facing refuse when a plugin ALC did not collect. Core was not
    /// Uninstall'd; plugins are put back. <paramref name="pluginsReloaded"/> is the
    /// actual <c>ReloadAll</c> count so the line cannot say <c>restored</c> while
    /// the process is plugin-less (R16 finding 2). Grep <c>still alive; restored plugins=</c>.
    /// </summary>
    public static string FormatPluginAlcRefused(IReadOnlyList<string>? names) =>
        FormatPluginAlcRefused(names, pluginsReloaded: 0);

    public static string FormatPluginAlcRefused(IReadOnlyList<string>? names, int pluginsReloaded) =>
        "refused: plugin ALC " + JoinAlcNames(names)
        + " still alive; restored plugins="
        + pluginsReloaded.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Subsequent <c>RequestReload</c> after a plugin-ALC wait miss. Same shape as
    /// <see cref="RefusePreviousAlive"/>: the next swap is refused until the weaks
    /// die or the process restarts. Grep <c>still alive; restart the process</c>.
    /// </summary>
    public static string RefusePluginAlcAlive(IReadOnlyList<string>? names) =>
        "Core reload refused: plugin ALC " + JoinAlcNames(names) + " still alive; restart the process.";

    /// <summary>
    /// Token inserted when <c>AssemblyLoadContext.All</c> throws. Fail closed: a
    /// non-empty list so the plugin wait and UnloadAlc refuse fire. Grep this exact
    /// string.
    /// </summary>
    public const string EnumerationFailedAlcName = "(enumeration-failed)";

    /// <summary>Listed in <c>pin-diag alcs=</c> when a collectible ALC has no <c>Name</c>.</summary>
    public const string UnnamedAlcName = "(unnamed)";

    /// <summary>
    /// Fail-closed plugin-ALC listing. An exception during enumeration counts as
    /// "plugins still alive" so Core is not Uninstall'd while a plugin context may
    /// still pin it.
    /// </summary>
    public static List<string> ClosePluginAlcListing(List<string>? names, bool enumerationFailed)
    {
        var list = names ?? new List<string>();
        if (enumerationFailed)
            list.Add(EnumerationFailedAlcName);
        return list;
    }

    /// <summary>
    /// Every live collectible ALC in the process at collect time. Sorted so log
    /// lines compare. Empty names become <see cref="UnnamedAlcName"/>. Grep
    /// <c>pin-diag alcs=</c>.
    /// </summary>
    public static string FormatCollectibleAlcs(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0)
            return "pin-diag alcs=[]";
        var copy = new string[names.Count];
        int n = 0;
        for (int i = 0; i < names.Count; i++)
        {
            string name = string.IsNullOrEmpty(names[i]) ? UnnamedAlcName : names[i];
            copy[n++] = name;
        }
        if (n == 0)
            return "pin-diag alcs=[]";
        Array.Sort(copy, 0, n, StringComparer.Ordinal);
        return "pin-diag alcs=[" + string.Join(", ", copy, 0, n) + "]";
    }

    private static string JoinAlcNames(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0)
            return "(unknown)";
        var parts = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
            parts[i] = string.IsNullOrEmpty(names[i]) ? UnnamedAlcName : names[i];
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Operator-facing hitch line for one blocking collect pulse on the main thread.
    /// Grep for <c>collect-wait hitch</c>.
    /// </summary>
    public static string FormatCollectHitch(int milliseconds) =>
        "collect-wait hitch ms=" + milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Logged once when two consecutive Core-ALC pulses exceeded
    /// <see cref="HitchBackoffMs"/>. Grep <c>collect-wait hitch backoff</c>.
    /// </summary>
    public static string FormatCollectHitchBackoff() =>
        "collect-wait hitch backoff: pulses >8ms twice; stepping to every 10th frame after frame 10";

    /// <summary>
    /// Whether this collecting frame should run a blocking GC pulse. Frame 0 (first
    /// collecting tick) always pulses; then every <paramref name="everyFrames"/>.
    /// Plugin-ALC wait uses <see cref="CollectPulseEveryFrames"/>; Core-ALC wait
    /// uses <see cref="ShouldPulseCoreCollect"/>.
    /// </summary>
    public static bool ShouldPulseCollect(int collectFrames, int everyFrames)
    {
        if (collectFrames < 0) return false;
        int n = everyFrames < 1 ? 1 : everyFrames;
        return collectFrames % n == 0;
    }

    /// <summary>
    /// Core-ALC collect pulse. Every frame for the first
    /// <see cref="CollectPulseEveryFrameUntil"/> (live swaps collect in 3–4), then
    /// every <see cref="CollectPulseStrideAfter"/>th frame so a miss does not
    /// hitch 60 times. Two consecutive pulses slower than
    /// <see cref="HitchBackoffMs"/> switch to <see cref="HitchBackoffStride"/>
    /// <em>after</em> that every-frame window — hitch backoff must not skip
    /// pulses 2–9. The last allowed frame always pulses so a miss is not latched
    /// without a final <c>WaitForPendingFinalizers</c>.
    /// </summary>
    public static bool ShouldPulseCoreCollect(int collectFrames) =>
        ShouldPulseCoreCollect(collectFrames, consecutiveHitches: 0);

    /// <summary>
    /// Same as <see cref="ShouldPulseCoreCollect(int)"/> with a hitch counter from
    /// the live pulse clock. <paramref name="consecutiveHitches"/> is how many
    /// immediately previous pulses exceeded <see cref="HitchBackoffMs"/>.
    /// </summary>
    public static bool ShouldPulseCoreCollect(int collectFrames, int consecutiveHitches)
    {
        if (collectFrames < 0) return false;
        if (collectFrames < CollectPulseEveryFrameUntil) return true;
        if (collectFrames == CollectWaitFrames - 1) return true;
        if (consecutiveHitches >= HitchBackoffAfter)
        {
            int n = HitchBackoffStride < 1 ? 1 : HitchBackoffStride;
            return collectFrames % n == 0;
        }
        int stride = CollectPulseStrideAfter < 1 ? 1 : CollectPulseStrideAfter;
        return collectFrames % stride == 0;
    }

    /// <summary>
    /// Whether this FrameTicker tick may walk <c>AssemblyLoadContext.All</c> for
    /// the plugin-ALC latch. Frame 0 (just latched) always lists; then every
    /// <see cref="PluginLatchPollEveryFrames"/>. Negative is never.
    /// </summary>
    public static bool ShouldPollPluginLatch(int framesSinceLatch, int everyFrames)
    {
        if (framesSinceLatch < 0) return false;
        int n = everyFrames < 1 ? 1 : everyFrames;
        return framesSinceLatch % n == 0;
    }

    /// <summary>
    /// Pin-diag line for the main-thread stack at collect start. <c>dirty=True</c>
    /// means a plugin ALC or the Core generation still has a frame on the stack,
    /// which is enough to keep a collectible ALC alive.
    /// </summary>
    public static string FormatStackCheck(int frames, int plugin, int core) =>
        "pin-diag stack frames=" + frames.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " plugin=" + plugin.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " core=" + core.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " dirty=" + (plugin > 0 || core > 0);

    /// <summary>Reloadable plugin ALC names start with this (<c>progenitor-reload:Sample:...</c>).</summary>
    public const string PluginAlcPrefix = "progenitor-reload";

    /// <summary>True when <paramref name="alcName"/> is a reloadable plugin context.</summary>
    public static bool IsPluginAlcName(string? alcName)
    {
        if (string.IsNullOrEmpty(alcName)) return false;
        return alcName.StartsWith(PluginAlcPrefix + ":", StringComparison.Ordinal)
            || string.Equals(alcName, PluginAlcPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="alcName"/> is a Progenitor-owned collectible
    /// (<c>progenitor-core:*</c> or <c>progenitor-reload:*</c>). Third-party and
    /// unnamed contexts are pin-diag only (R17 finding 1).
    /// </summary>
    public static bool IsProgenitorAlcName(string? alcName) =>
        IsCoreAlcName(alcName) || IsPluginAlcName(alcName);

    /// <summary>
    /// Collectible ALCs the plugin-wait / UnloadAlc backstop must treat as alive:
    /// only <c>progenitor-reload:*</c> (plus the fail-closed enumeration token).
    /// Core is waited via the generation weak, not this list, so the wait before
    /// Uninstall cannot stall on the generation we are about to unload. Third-party
    /// and unnamed collectibles are listed on <c>pin-diag alcs=</c> only and must
    /// not block a Progenitor reload (R17 finding 1).
    /// </summary>
    public static bool IsWaitedCollectibleAlc(string? alcName)
    {
        if (string.Equals(alcName, EnumerationFailedAlcName, StringComparison.Ordinal))
            return true;
        return IsPluginAlcName(alcName);
    }

    /// <summary>
    /// Names from <c>AssemblyLoadContext.All</c> that the plugin wait actually
    /// blocks on. Third-party / unnamed / Core generation names are dropped.
    /// </summary>
    public static List<string> FilterWaitedAlcNames(IReadOnlyList<string>? names)
    {
        var list = new List<string>();
        if (names == null) return list;
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i] ?? "";
            if (!IsWaitedCollectibleAlc(name)) continue;
            list.Add(name);
        }
        return list;
    }

    /// <summary>Count of <see cref="FilterWaitedAlcNames"/>.</summary>
    public static int CountWaitedAlcNames(IReadOnlyList<string>? names)
    {
        if (names == null) return 0;
        int n = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (IsWaitedCollectibleAlc(names[i])) n++;
        }
        return n;
    }

    /// <summary>
    /// True when <paramref name="alcName"/> is a collectible context for
    /// <paramref name="pluginName"/> (<c>progenitor-reload:{name}:*</c> or
    /// <c>progenitor-reload:{name}</c>). Used so a wait-miss reload cannot mint a
    /// second Sample ALC against the still-installed Core (R17 finding 4).
    /// </summary>
    public static bool PluginAlcNameMatches(string? alcName, string? pluginName)
    {
        if (string.IsNullOrEmpty(alcName) || string.IsNullOrEmpty(pluginName)) return false;
        string exact = PluginAlcPrefix + ":" + pluginName;
        if (string.Equals(alcName, exact, StringComparison.Ordinal)) return true;
        string prefix = exact + ":";
        return alcName.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Idle Scan / wait-miss <c>ReloadAll</c> must not create
    /// <c>progenitor-reload:{pluginName}:*</c> while any listed collectible still
    /// uses that prefix. Other plugin names are allowed (R17 finding 4).
    /// </summary>
    public static bool ShouldRefusePluginAlcName(string? pluginName, IReadOnlyList<string>? listedAlcNames)
    {
        if (string.IsNullOrEmpty(pluginName) || listedAlcNames == null) return false;
        for (int i = 0; i < listedAlcNames.Count; i++)
        {
            if (PluginAlcNameMatches(listedAlcNames[i], pluginName)) return true;
        }
        return false;
    }

    /// <summary>
    /// Idle <c>Scan</c> / <c>Load</c> must not mint a new plugin ALC while a
    /// previous one is latched. Cascade <c>ReloadAll</c> after a wait miss
    /// passes <paramref name="evenIfLatched"/> so the process is never left
    /// plugin-less (R16 finding 2).
    /// </summary>
    public static bool MayCreatePluginAlc(bool latchedAlive, bool evenIfLatched) =>
        evenIfLatched || !latchedAlive;

    /// <summary>
    /// <c>_forcePluginLatch</c> (enumeration threw at latch time) clears when a
    /// later listing <em>succeeds</em>, even if waited names remain: the weaks
    /// taken at latch time (or on that successful list) are then enough. A throw
    /// leaves it set so the next 30-frame poll retries. <paramref name="waitedCount"/>
    /// is accepted for call-site compatibility and ignored (R17 finding 5).
    /// </summary>
    public static bool ShouldClearForcePluginLatch(bool enumerationSucceeded, int waitedCount)
    {
        _ = waitedCount;
        return enumerationSucceeded;
    }

    /// <summary>
    /// A failed <c>GetType</c> / missing <c>Install</c> unloads an empty
    /// collectible ALC that nothing KeepDelegate'd. Do not latch it as
    /// <c>_previousAlc</c> (R16 finding 7). A Core-side protocol refusal
    /// (<see cref="FormatProtocolMismatch"/>) also filled no slots — treat it
    /// like that empty Install and release the ALC (R20 finding 6). Latch only
    /// when Install actually ran and was not a protocol mismatch.
    /// </summary>
    public static bool ShouldLatchFailedInstallContext(bool installInvoked) =>
        ShouldLatchFailedInstallContext(installInvoked, error: null);

    /// <summary>
    /// Same as <see cref="ShouldLatchFailedInstallContext(bool)"/> with the
    /// failed Install error. A <see cref="FormatProtocolMismatch"/> message
    /// must not latch.
    /// </summary>
    public static bool ShouldLatchFailedInstallContext(bool installInvoked, string? error) =>
        installInvoked && !IsProtocolMismatchMessage(error);

    /// <summary>
    /// True when <paramref name="error"/> is (or wraps)
    /// <see cref="FormatProtocolMismatch"/>. The Core-side throw prefixes the
    /// exception type name.
    /// </summary>
    public static bool IsProtocolMismatchMessage(string? error) =>
        !string.IsNullOrEmpty(error)
        && error.IndexOf("does not accept Core protocol", StringComparison.Ordinal) >= 0;

    /// <summary>
    /// <c>LogBuffer._listenerInstalled</c> after an Uninstall attempt. The flag
    /// clears only when <c>Listeners.Remove</c> succeeded so a throw leaves it
    /// set and a later Uninstall retries (R16 finding 6).
    /// </summary>
    public static bool LogListenerInstalledAfterUninstall(bool removeSucceeded) =>
        !removeSucceeded;

    public static string RefusePreviousAlive(int generation, string? alcName)
    {
        string name = string.IsNullOrEmpty(alcName) ? AlcNamePrefix + ":" + generation : alcName;
        return "Core reload refused: generation " + generation + " (" + name + ") is still alive; restart the process.";
    }

    /// <summary>
    /// A swap that Unload'd generation N but the ALC did not collect must not load N+1.
    /// Stacking a live prefix from an unloading collectible ALC is a silent process death.
    /// </summary>
    public static bool MayLoadNextGeneration(bool collected) => collected;

    /// <summary>
    /// Logged while waiting on collect. The cascade then reloads generation N's bytes
    /// into a <em>fresh</em> ALC (same as a collecting dry-run) instead of leaving a
    /// null runtime or re-Install'ing the old object; the operator-facing summary is
    /// <see cref="FormatRestored"/>.
    /// </summary>
    public static string DidNotCollect(int generation, string? alcName)
    {
        string name = string.IsNullOrEmpty(alcName) ? AlcNamePrefix + ":" + generation : alcName;
        return "Core reload: generation " + generation + " (" + name + ") did not collect; restoring via fresh ALC instead of stacking N+1.";
    }

    /// <summary>
    /// Operator-facing summary when collect fails and generation N is loaded again from
    /// bytes into a new ALC. The previous context has already had <c>Unload()</c>
    /// requested (or leftover Harmony forbids a second attempt); the next
    /// <c>core_reload</c> is refused until process restart. Grep line for the live
    /// check after a refused swap. Do <em>not</em> use this for a failed N+1 Install
    /// that then restored N — that path is <see cref="FormatFailedInstallRestored"/>.
    /// </summary>
    public static string FormatRestored(int generation) =>
        "refused: generation " + generation + " still alive; restored; restart the process";

    /// <summary>
    /// Operator-facing summary (and bridge lastReload reason) when generation N
    /// collected, N+1 <c>LoadAndInstall</c> failed, and N's last-good bytes were
    /// loaded into a fresh ALC. Nothing of N is still alive; no process restart is
    /// required (evidence 70). Grep <c>restored; no restart needed</c>.
    /// </summary>
    public static string FormatFailedInstallRestored(int failedGeneration, string? error, int restoredGeneration) =>
        "install of generation " + failedGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " failed (" + (error ?? "") + "); generation "
        + restoredGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " restored; no restart needed";

    /// <summary>
    /// Last-resort line when collect failed <em>and</em> loading N's bytes into a fresh
    /// ALC also failed. The process has no Core until restart. Grep <c>CORE-LESS</c>.
    /// </summary>
    public static string FormatCoreLess(int generation, string error) =>
        "CORE-LESS last resort: generation " + generation
        + " did not collect and fresh LoadAndInstall failed: " + (error ?? "")
        + "; process has no Core until restart";

    /// <summary>
    /// Grep line when <c>Install</c> of an uncommitted generation threw (or returned
    /// false) and every Bootstrap slot that may hold its delegates is being nulled
    /// <em>before</em> <c>ALC.Unload</c>. A live Harmony / receive / lifetime /
    /// MainThread slot into that collectible context is a silent process death on
    /// the next <c>SystemBase.Update</c> (R14 finding 2).
    /// </summary>
    public static string FormatAbandonFailedInstall(int generation, string? alcName)
    {
        string name = !string.IsNullOrEmpty(alcName)
            ? alcName
            : generation >= FirstGeneration ? AlcName(generation) : AlcNamePrefix;
        return "abandon failed install gen=" + generation
            + " alc=" + name
            + "; slots cleared before Unload";
    }

    /// <summary>
    /// Grep line when generation N collected, N+1 <c>LoadAndInstall</c> failed, and
    /// the cascade reloads N's bytes into a fresh ALC (same restore as a collect miss).
    /// </summary>
    public static string FormatRestoreAfterFailedNext(int current, int failedNext) =>
        "N+1 install failed gen=" + failedNext
        + "; restoring gen=" + current + " via fresh LoadAndInstall";

    /// <summary>
    /// Dry-run summary: sweep + diagnostics + collect test ran, then generation N was
    /// put back so the process is never left Core-less. A dry-run that collected is a
    /// full reinstall of the same bits into a new ALC (same generation number), not a
    /// no-op that leaves the world identical.
    /// </summary>
    public static string FormatDryRun(int generation, bool collected, bool restored) =>
        "dryRun gen=" + generation
        + " collected=" + collected
        + " restored=" + restored;

    /// <summary>
    /// Harmony IL2CPP <c>DelegateCache</c> / DelegateSupport mutation is never safe on
    /// a collectible swap (<see cref="MayMutateDelegateCaches"/>). This leftover-Harmony
    /// gate still skips <c>NativePinRegistry.ReleaseGeneration</c> when a JMP remains:
    /// freeing a pin a leftover trampoline still points at is a silent native death.
    /// </summary>
    public static bool MayClearIl2CppCaches(int leftoverHarmonyCount) => leftoverHarmonyCount <= 0;

    /// <summary>
    /// Whether <c>ALC.Unload</c> may run. Leftover Harmony JMPs, an in-flight receive
    /// dispatch, or a plugin ALC that did not collect after UnloadAll all pin Core.
    /// </summary>
    public static bool MayUnloadCollectibleAlc(int leftoverHarmonyCount, int inFlightReceive, int alivePluginAlcs) =>
        leftoverHarmonyCount <= 0 && inFlightReceive <= 0 && alivePluginAlcs <= 0;

    /// <summary>
    /// FullName Bootstrap registers once per process. ClassInjector keys by FullName and
    /// has no unregister; Core must never register this name (or any other).
    /// </summary>
    public const string BootstrapFrameTickerFullName = "Progenitor.Ticks.FrameTicker";

    /// <summary>True when <paramref name="fullName"/> is Bootstrap's process-lifetime injected type.</summary>
    public static bool IsBootstrapInjectedFullName(string? fullName) =>
        string.Equals(fullName, BootstrapFrameTickerFullName, StringComparison.Ordinal);

    /// <summary>
    /// True when an InjectedTypes name is a collectible-Core pin. Bootstrap's FrameTicker
    /// FullName starts with <c>Progenitor.</c> and does not contain <c>Bootstrap</c>, so a
    /// naive prefix match (evidence 53 <c>progenitor-named=1 will-not-collect</c>) is a
    /// false positive and must be excluded.
    /// </summary>
    public static bool IsClassInjectorCorePin(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return false;
        if (IsBootstrapInjectedFullName(fullName)) return false;
        if (fullName.IndexOf("Bootstrap", StringComparison.Ordinal) >= 0) return false;
        return fullName.StartsWith("Progenitor.", StringComparison.Ordinal);
    }

    /// <summary>
    /// ClassInjector has no unregister. A Core-injected type (not Bootstrap's FrameTicker)
    /// means this generation will not collect; the operator must restart.
    /// </summary>
    public static string FormatClassInjectorWillNotCollect(int progenitorNamedHits) =>
        "pin-diag ClassInjector progenitor-named=" + progenitorNamedHits + " will-not-collect";

    /// <summary>One injected-type line so the next client run names the pin, not just a count.</summary>
    public static string FormatInjectedTypeLine(string? fullName) =>
        "pin-diag injected-type name=" + (fullName ?? "")
        + " bootstrap=" + IsBootstrapInjectedFullName(fullName);

    /// <summary>One native-pin line: kind, name, owner (ALC or assembly), generation at Keep time.</summary>
    public static string FormatNativePinLine(string? kind, string? name, string? owner, string? generation) =>
        "pin-diag native-pin kind=" + (kind ?? "")
        + " name=" + (name ?? "")
        + " owner=" + (owner ?? "")
        + " gen=" + (generation ?? "");

    /// <summary>
    /// One Bootstrap field that still strongly references the unloading generation.
    /// Grep <c>pin-diag holder=</c>. <paramref name="path"/> is the static/instance
    /// field path (e.g. <c>HarmonySlots._managedPrefix</c>); <paramref name="typeName"/>
    /// is the runtime type of the value.
    /// </summary>
    public static string FormatHolder(string? path, string? typeName) =>
        "pin-diag holder=" + (path ?? "") + " type=" + (typeName ?? "");

    /// <summary>
    /// Wide-scan holder line. <paramref name="assembly"/> is the non-collectible
    /// assembly whose static (or extra root) reached the unloading generation.
    /// Grep <c>pin-diag holder=</c>.
    /// </summary>
    public static string FormatHolder(string? assembly, string? path, string? typeName)
    {
        if (string.IsNullOrEmpty(assembly))
            return FormatHolder(path, typeName);
        return "pin-diag holder=" + assembly + ":" + (path ?? "") + " type=" + (typeName ?? "");
    }

    /// <summary>Elapsed time of a holder scan. Wide scans can hitch a few hundred ms on the freeze.</summary>
    public static string FormatHolderScanElapsed(int milliseconds, bool wide) =>
        "pin-diag holder-scan" + (wide ? " wide" : "")
        + " ms=" + milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Grep line when the holder scan stopped at the 400 ms budget. <c>N</c> is
    /// elapsed milliseconds, not remaining budget.
    /// </summary>
    public static string FormatHolderScanTruncated(int milliseconds) =>
        "pin-diag holder-scan truncated ms="
        + milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Grep line when last-good Core bytes are remembered after a successful Install.</summary>
    public static string FormatLastGoodBytes(int length) =>
        "last-good bytes=" + length.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// NativePinRegistry entries grouped by Keep-time generation id. Grep
    /// <c>pin-diag native-pin-gen</c>. Logged at collect start so a stale
    /// <c>PinGeneration</c> (gen 2 pins still tagged as gen 1) is visible.
    /// </summary>
    public static string FormatNativePinGeneration(string? generation, int count) =>
        "pin-diag native-pin-gen gen=" + (generation ?? "")
        + " count=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Grep line after <see cref="NativePinRegistry.ReleaseGeneration"/>.
    /// <paramref name="before"/> / <paramref name="after"/> are counts for that generation
    /// (empty-generation Core pins included); <paramref name="dropped"/> is entries actually
    /// removed (KeepDelegate and GCHandle both count).
    /// </summary>
    public static string FormatPinRelease(string? generation, int before, int after, int dropped) =>
        "pin-release NativePinRegistry gen=" + (generation ?? "")
        + " before=" + before.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " after=" + after.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " dropped=" + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// True when the bridge/reason string asks for a collect test that reinstalls
    /// immediately. Dev currently forwards <c>args.reason</c> only, so
    /// <c>core_reload {"reason":"dryRun"}</c> and a reason of <c>{"dryRun":true}</c>
    /// both match; a future Dev can pass the bool through
    /// <c>RequestReload(reason, dryRun)</c>.
    /// </summary>
    public static bool ParseDryRun(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        if (ParseDryRunWide(reason)) return true;
        int i = IndexOfDryRunToken(reason);
        if (i < 0) return false;
        return !IsDryRunFalse(reason, i);
    }

    /// <summary>
    /// True when the reason asks for the wide holder scan as well as a dry-run
    /// (<c>dryRunWide</c> token). Implies <see cref="ParseDryRun"/>.
    /// </summary>
    public static bool ParseDryRunWide(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return false;
        return IndexOfToken(reason, "dryRunWide") >= 0;
    }

    private static int IndexOfDryRunToken(string reason)
    {
        for (int i = 0; i <= reason.Length - 6; i++)
        {
            if (!StartsWithDryRun(reason, i)) continue;
            int after = i + 6;
            if (i > 0 && IsTokenChar(reason[i - 1])) continue;
            if (after < reason.Length && IsTokenChar(reason[after])) continue;
            return i;
        }
        return -1;
    }

    private static int IndexOfToken(string reason, string token)
    {
        if (string.IsNullOrEmpty(token) || reason.Length < token.Length) return -1;
        for (int i = 0; i <= reason.Length - token.Length; i++)
        {
            if (!StartsWithIgnoreCase(reason, i, token)) continue;
            int after = i + token.Length;
            if (i > 0 && IsTokenChar(reason[i - 1])) continue;
            if (after < reason.Length && IsTokenChar(reason[after])) continue;
            return i;
        }
        return -1;
    }

    private static bool StartsWithIgnoreCase(string s, int i, string token)
    {
        if (i + token.Length > s.Length) return false;
        for (int k = 0; k < token.Length; k++)
        {
            char a = s[i + k];
            char b = token[k];
            if (a == b) continue;
            if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
            if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

    private static bool StartsWithDryRun(string s, int i)
    {
        // "dryRun" or "dryrun" — six ASCII letters.
        if (i + 6 > s.Length) return false;
        return (s[i] == 'd' || s[i] == 'D')
            && (s[i + 1] == 'r' || s[i + 1] == 'R')
            && (s[i + 2] == 'y' || s[i + 2] == 'Y')
            && (s[i + 3] == 'R' || s[i + 3] == 'r')
            && (s[i + 4] == 'u' || s[i + 4] == 'U')
            && (s[i + 5] == 'n' || s[i + 5] == 'N');
    }

    private static bool IsTokenChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static bool IsDryRunFalse(string reason, int tokenIndex)
    {
        int i = tokenIndex + 6;
        while (i < reason.Length && (reason[i] == ' ' || reason[i] == '"' || reason[i] == '\'' || reason[i] == ':'))
            i++;
        if (i + 4 <= reason.Length
            && (reason[i] == 'f' || reason[i] == 'F')
            && (reason[i + 1] == 'a' || reason[i + 1] == 'A')
            && (reason[i + 2] == 'l' || reason[i + 2] == 'L')
            && (reason[i + 3] == 's' || reason[i + 3] == 'S')
            && (reason[i + 4] == 'e' || reason[i + 4] == 'E'))
            return true;
        return false;
    }

    public static string StepLog(int generation, string step) =>
        "Core reload gen " + generation + ": " + step;

    /// <summary>
    /// Bounded wait for a collectible ALC. <paramref name="stillAlive"/> is typically
    /// <c>weak.IsAlive</c>. <paramref name="pulse"/> is <c>GC.Collect</c> + sleep in
    /// production and a no-op or counter in tests.
    /// </summary>
    public static bool WaitUntilCollected(Func<bool> stillAlive, Action? pulse, int attempts)
    {
        if (stillAlive == null) throw new ArgumentNullException(nameof(stillAlive));
        if (attempts < 1) attempts = 1;
        for (int i = 0; i < attempts; i++)
        {
            if (!stillAlive()) return true;
            pulse?.Invoke();
        }
        return !stillAlive();
    }

    public static string FormatOk(int generation, bool collected, int pluginsUnloaded, int pluginsReloaded, string reason) =>
        "ok gen=" + generation
        + " collected=" + collected
        + " pluginsUnloaded=" + pluginsUnloaded
        + " pluginsReloaded=" + pluginsReloaded
        + " reason=" + (reason ?? "");

    public static string FormatFailed(int generation, string step, string error, bool oldRunning) =>
        "failed gen=" + generation
        + " step=" + step
        + " oldRunning=" + oldRunning
        + " error=" + (error ?? "");

    public static string FormatRefused(string reason) => reason ?? "Core reload refused.";

    /// <summary>
    /// Weak used to poll collect. <c>trackResurrection: true</c> is Microsoft's
    /// unloadability guidance: a finalizer that still roots the LoaderAllocator
    /// must keep <c>IsAlive</c> true until that finalizer has run.
    /// </summary>
    public static WeakReference CreateGenerationWeak(object target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        return new WeakReference(target, trackResurrection: true);
    }

    public const string PulseCollectStep = "gc-collect";
    public const string PulseWaitStep = "wait-pending-finalizers";
    public const string PulseCheckStep = "check";

    /// <summary>
    /// One collect-poll pulse: <c>GC.Collect</c> then <c>GC.WaitForPendingFinalizers</c>
    /// then the caller checks the weak. Fake ops record <see cref="PulseCollectStep"/> /
    /// <see cref="PulseWaitStep"/> so tests can assert the sequence without running GC.
    /// </summary>
    public static bool PulseCollectThenCheck(Func<bool>? stillAlive, Action<string>? note = null)
    {
        note?.Invoke(PulseCollectStep);
        GC.Collect();
        note?.Invoke(PulseWaitStep);
        GC.WaitForPendingFinalizers();
        bool collected = stillAlive == null || !stillAlive();
        note?.Invoke(PulseCheckStep);
        return collected;
    }

    /// <summary>
    /// True when Core's reported protocol matches Bootstrap's. A missing field reads as
    /// 0 and is refused (both numbers in <paramref name="error"/>).
    /// </summary>
    public static bool AcceptProtocol(int bootstrap, int core, out string error)
    {
        if (bootstrap > 0 && bootstrap == core)
        {
            error = "";
            return true;
        }
        error = FormatProtocolMismatch(bootstrap, core);
        return false;
    }

    public static string FormatProtocolMismatch(int bootstrap, int core) =>
        "Core reload refused: Bootstrap protocol " + bootstrap.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " does not accept Core protocol " + core.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";

    /// <summary>
    /// Read <c>Progenitor.BootstrapLink.ProtocolVersion</c> from a loaded Core assembly.
    /// Missing type/field is 0 (refused by <see cref="AcceptProtocol"/>).
    /// </summary>
    public static int ReadProtocolVersion(System.Reflection.Assembly? asm)
    {
        if (asm == null) return 0;
        try
        {
            var type = asm.GetType(CoreLayout.ProtocolTypeName);
            if (type == null) return 0;
            var field = type.GetField(
                CoreLayout.ProtocolFieldName,
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);
            if (field == null || field.FieldType != typeof(int)) return 0;
            return field.GetValue(null) is int v ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// One operator-facing line when N+1 Install / protocol / missing type failed.
    /// Names the generation, the failure, the quarantined file, and whether last-good
    /// bytes were written back so the watcher cannot loop on the broken image.
    /// </summary>
    public static string FormatFailedInstall(int generation, string error, string failedFileName, bool restoredLastGood) =>
        "Core reload failed: Install of generation " + generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " failed (" + (error ?? "") + "); quarantined " + (failedFileName ?? "")
        + (restoredLastGood ? "; restored last-good" : "; no last-good to restore");

    public static string FormatLastReload(int gen, string outcome, string reason, DateTime timestampUtc) =>
        "lastReload gen=" + gen.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " outcome=" + (outcome ?? "")
        + " reason=" + (reason ?? "")
        + " timestamp=" + (timestampUtc == default
            ? ""
            : timestampUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Grep line when a staged <c>Progenitor.dll.next</c> is rotated onto the live
    /// path. Same text as <c>tools/test-server.ps1 start</c> /
    /// <c>tools/launch-client.ps1</c>. First load logs this <em>before</em> the
    /// first read of <c>Progenitor.dll</c> (BepInEx preloader and script-reloader plugins
    /// both promote a staged copy before <c>Assembly.Load</c>; they never
    /// quarantine a file they did not load).
    /// </summary>
    public static string FormatPromotedNext() => "promoted Progenitor.dll.next";

    /// <summary>
    /// Grep line when quarantine would have renamed a live dll that is no longer
    /// the image <c>LoadFromStream</c> used (a later promote already replaced it).
    /// </summary>
    public static string FormatQuarantineSkipped() =>
        "Core quarantine skipped: on-disk file is not the loaded image";

    /// <summary>
    /// First-load / core-less protocol refuse. Last-good of the old protocol is
    /// not restored; the watch stays armed for a matching Core.
    /// </summary>
    public static string FormatProtocolMismatchStayCoreLess(int bootstrapProtocol) =>
        "Core not loaded: protocol mismatch; deploy a Core built for Bootstrap protocol "
        + bootstrapProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Grep line when no generation is loaded and a settled <c>.next</c> / bridge
    /// <c>core_reload</c> / live-dll change Installs gen 1 without snapshot/freeze/unload.
    /// </summary>
    public static string FormatCoreLessInstall(int generation, string? reason) =>
        "Core install gen=" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " from " + (string.IsNullOrEmpty(reason) ? "watch" : reason)
        + " (no previous generation)";

    /// <summary>
    /// Bootstrap slots <see cref="CoreLoader"/> nulls before <c>ALC.Unload</c> of a
    /// collectible Core generation — on the failed-install path and on the cascade's
    /// unload alike (R24 finding 6). Tests lock this list; production
    /// <c>ClearFailedInstallSlots</c> must cover every name.
    ///
    /// <para><b>This list is a description, not the guarantee</b> (job FLATCH4, R47
    /// finding 3). A length-locked list can only fail on an addition to itself; it cannot
    /// fail on a slot that exists in the code and was never written down here, which is
    /// exactly how <c>UiMaskDetour</c> was missing from the clear for a whole batch. The
    /// property that an unload cannot happen without the clear is enforced by
    /// <see cref="CoreReloadCollect.PrepareAndUnload"/> taking the clear as a required
    /// argument, and pinned by <c>CoreUnloadSlotClearTests</c>, which enumerates the
    /// <c>Unload</c> call sites out of the Bootstrap sources instead of out of a list.</para>
    /// </summary>
    public static readonly string[] FailedInstallSlots =
    {
        "HarmonySlots",
        "receive.client",
        "receive.server",
        "lifetime.quitting",
        "lifetime.frame",
        "lifetime.lateframe",
        "CoreTick",
        "SweepPlugin",
        "UiSlots",
        "UiInputContext.keys",
        "UiInputContext.capture",
        "UiMaskDetour",
        "UiActionSlot",
    };

    /// <summary>
    /// First load always rotates a staged <c>.next</c> before reading
    /// <c>Progenitor.dll</c>. Independent of <c>Reload.WatchCore</c> and of the
    /// scheduled-reload path (those also promote, but a protocol bump restarts
    /// both processes and must pick up the staged Core on the first
    /// <c>LoadFromStream</c>).
    /// </summary>
    public const bool PromoteBeforeFirstRead = true;

    /// <summary>True for any first-load, even when the Step E watcher is off.</summary>
    public static bool ShouldPromoteOnFirstLoad(bool watchEnabled)
    {
        _ = watchEnabled;
        return PromoteBeforeFirstRead;
    }

    /// <summary>
    /// The staged-Core promote path's gate (R30 finding 1). <c>SelfUpdate.PromoteCoreFile</c>
    /// used to hand <c>Progenitor.dll.next</c>'s bytes straight to
    /// <c>CoreLoader.TryValidateCoreImage</c>, which <c>LoadFromStream</c>s them into a
    /// throwaway ALC and reads a static field off the image — running its static constructor
    /// — <em>before</em> the allowlist was consulted, on first load and at the head of every
    /// <c>RequestReload</c>, regardless of <c>Reload.WatchCore</c>. That is the fifth
    /// automatic code-loading path and it was the only ungated one.
    ///
    /// The order is a property of this function rather than of a call site: the bytes are
    /// hashed once, <paramref name="decide"/> runs, and <paramref name="inspect"/> — the step
    /// that loads and reflects — is only reached when <see cref="ReloadAllowPolicy.MayLoad"/>
    /// says so. A refusal comes back as the same
    /// <see cref="ReloadAllowPolicy.FormatRefusedCore"/> line the other four paths print, and
    /// the caller quarantines <c>.next</c> and keeps the live Core. The same
    /// <paramref name="bytes"/> array is what the caller then writes over the live dll: no
    /// re-read, so there is nothing to swap between the hash and the promote.
    /// </summary>
    /// <param name="fileName">Name the allowlist row is matched against (<c>Progenitor.dll</c>).</param>
    /// <param name="bytes">The staged image, read once by the caller.</param>
    /// <param name="decide">The allowlist. <c>(fileName, sha256Hex) =&gt; AllowDecision</c>.</param>
    /// <param name="inspect">
    /// The load-and-reflect step. Returns "" when the image is acceptable, otherwise the
    /// reason. Never invoked for a refused image.
    /// </param>
    /// <param name="decision">
    /// The allowlist's verdict, so the caller can log the refusal at error level with the
    /// <c>allowlist=</c> shape and tell it apart from a plain bad-image rejection.
    /// </param>
    public static bool TryAcceptStagedCoreBytes(
        string? fileName,
        byte[]? bytes,
        Func<string, string, AllowDecision> decide,
        Func<byte[], string> inspect,
        out AllowDecision decision,
        out string error)
    {
        if (decide == null) throw new ArgumentNullException(nameof(decide));
        if (inspect == null) throw new ArgumentNullException(nameof(inspect));

        error = "";
        byte[] image = bytes ?? Array.Empty<byte>();
        string name = fileName ?? "";
        string hash = ReloadAllowPolicy.Sha256Hex(image);
        long size = image.LongLength;

        decision = decide(name, hash);
        if (!ReloadAllowPolicy.MayLoad(decision))
        {
            error = ReloadAllowPolicy.FormatRefusedCore(name, hash, size, decision);
            return false;
        }

        error = inspect(image) ?? "";
        return error.Length == 0;
    }

    /// <summary>Simple assembly name a staged Bootstrap image must carry.</summary>
    public const string BootstrapAssemblySimpleName = "Progenitor.Bootstrap";

    /// <summary>File name of the live Bootstrap plugin, and the row the allowlist matches.</summary>
    public const string BootstrapFileName = BootstrapAssemblySimpleName + ".dll";

    /// <summary>
    /// The staged-Bootstrap promote path's gate (R31 finding 1). <c>SelfUpdate.PromoteFile</c>
    /// sent Core to <see cref="TryAcceptStagedCoreBytes"/> and let everything else through raw:
    /// <c>Progenitor.Bootstrap.dll.next</c> was <c>File.Move</c>d over the live chainloader
    /// plugin on every process start and again at every quit, with no hash, no allowlist row and
    /// no PE check. That is a strictly larger payoff than the Core path R30 finding 1 closed,
    /// because Bootstrap is the non-collectible assembly that <em>enforces</em> the allowlist for
    /// the other five paths: replace it and all five are gated by attacker code.
    ///
    /// Same order as the Core gate, and for the same reason: the bytes are hashed once,
    /// <paramref name="decide"/> runs on that hash, and <paramref name="inspect"/> is reached
    /// only when <see cref="ReloadAllowPolicy.MayLoad"/> says so. Unlike Core, inspect never
    /// loads anything - Bootstrap is not collectible and is not mapped by this promote, so
    /// <see cref="CheckStagedBootstrapImage"/> is a static PE + assembly-name check and no
    /// <c>LoadFromStream</c> is needed or wanted.
    /// </summary>
    public static bool TryAcceptStagedBootstrapBytes(
        string? fileName,
        byte[]? bytes,
        Func<string, string, AllowDecision> decide,
        Func<byte[], string> inspect,
        out AllowDecision decision,
        out string error)
    {
        if (decide == null) throw new ArgumentNullException(nameof(decide));
        if (inspect == null) throw new ArgumentNullException(nameof(inspect));

        error = "";
        byte[] image = bytes ?? Array.Empty<byte>();
        string name = string.IsNullOrEmpty(fileName) ? BootstrapFileName : fileName!;
        string hash = ReloadAllowPolicy.Sha256Hex(image);
        long size = image.LongLength;

        decision = decide(name, hash);
        if (!ReloadAllowPolicy.MayLoad(decision))
        {
            error = ReloadAllowPolicy.FormatRefusedBootstrap(name, hash, size, decision);
            return false;
        }

        error = inspect(image) ?? "";
        return error.Length == 0;
    }

    /// <summary>
    /// PE sanity for a staged Bootstrap image: a complete managed PE (the same check the Core
    /// promote uses) whose assembly name is <see cref="BootstrapAssemblySimpleName"/>. Returns
    /// "" when the image may be promoted, otherwise the reason for the refusal line.
    /// <paramref name="assemblyName"/> is read from the staged file's metadata without loading
    /// it - a null or empty name means the caller could not read one, which is a refusal.
    /// </summary>
    public static string CheckStagedBootstrapImage(byte[]? bytes, string? assemblyName)
    {
        if (bytes == null || bytes.Length == 0) return "empty";
        if (!CoreImagePolicy.IsPeImage(bytes)) return "not a PE image or truncated";
        if (!CoreImagePolicy.HasCompleteCliMetadata(bytes))
            return "truncated PE (section table or CLI metadata extends past file length)";
        string name = (assemblyName ?? "").Trim();
        if (name.Length == 0)
            return "no managed assembly name (not a managed assembly, or its metadata could not be read)";
        if (!string.Equals(name, BootstrapAssemblySimpleName, StringComparison.Ordinal))
            return "assembly name is " + name + ", expected " + BootstrapAssemblySimpleName;
        return "";
    }

    /// <summary>
    /// Disk name a refused staged image is quarantined as, for any live file (the Core watcher's
    /// <c>CoreWatchPolicy.FailedFileName</c> spells <c>Progenitor.dll</c> into the name and
    /// cannot be reused for Bootstrap). Milliseconds keep two failures in the same second apart.
    /// </summary>
    public static string StagedQuarantineName(string? liveFileName, DateTime utc) =>
        (string.IsNullOrEmpty(liveFileName) ? BootstrapFileName : liveFileName)
        + ".failed-" + utc.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Rejection line for a staged Bootstrap image that is not a usable Bootstrap PE.</summary>
    public static string FormatRejectedBootstrapNext(string? error) =>
        "Bootstrap promote refused: " + BootstrapFileName + ".next rejected: " + (error ?? "")
        + "; quarantined, not promoted.";

    /// <summary>Companion line naming the quarantined file, so an operator can find the bytes.</summary>
    public static string FormatQuarantinedBootstrapNext(string? failedFileName) =>
        "Bootstrap: quarantined " + BootstrapFileName + ".next as " + (failedFileName ?? "")
        + " (live Bootstrap untouched).";

    /// <summary>
    /// Generation listed on <c>scheduled gen=N</c>. A core-less process has never
    /// published gen 1, so the line names the generation about to Install.
    /// </summary>
    public static int ScheduledGeneration(int currentGeneration) =>
        currentGeneration < FirstGeneration ? FirstGeneration : currentGeneration;

    /// <summary>
    /// No published Core: skip freeze/snapshot/uninstall and Install gen 1
    /// directly. A running generation still takes the full cascade.
    /// </summary>
    public static bool ShouldInstallDirect(int currentGeneration, bool hasPublishedAssembly) =>
        currentGeneration < FirstGeneration || !hasPublishedAssembly;

    /// <summary>
    /// Plugin ALC <c>Resolving</c> returns the loaded Core by simple name. Dev
    /// and Sample cannot bind without that assembly, so Bootstrap does not scan
    /// <c>progenitor-reload</c> while Core-less. CoreWatch still runs.
    /// </summary>
    public const bool MayScanPluginsWithoutCore = false;

    /// <summary>
    /// True when <paramref name="loaded"/> and <paramref name="onDisk"/> are the
    /// same non-empty image. Quarantine uses this so a later promote cannot be
    /// renamed away as "the failed file".
    /// </summary>
    public static bool ImagesMatch(byte[]? loaded, byte[]? onDisk) =>
        CoreWatchPolicy.BytesIdentical(loaded, onDisk);

    /// <summary>
    /// Cheap fingerprint of a Core image (length plus sampled bytes). Stored
    /// next to <c>_lastLoadedBytes</c> so quarantine can log why a skip fired
    /// without dumping the PE.
    /// </summary>
    public static int ImageHash(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return 0;
        unchecked
        {
            int h = 17;
            h = (h * 31) + bytes.Length;
            h = (h * 31) + bytes[0];
            h = (h * 31) + bytes[bytes.Length - 1];
            for (int i = 4096; i < bytes.Length; i += 4096)
                h = (h * 31) + bytes[i];
            return h;
        }
    }

    /// <summary>
    /// True when the live dll is still the image that <c>LoadFromStream</c>
    /// used. A different on-disk file is left alone.
    /// </summary>
    public static bool MayQuarantineOnDisk(byte[]? loaded, byte[]? onDisk) =>
        ImagesMatch(loaded, onDisk);

    /// <summary>
    /// Last-good of the same Bootstrap protocol may replace the failed image.
    /// An older protocol, an empty last-good, or a live file that is not the
    /// failed image must not be overwritten (first Bootstrap protocol bump).
    /// </summary>
    public static bool MayRestoreLastGood(
        byte[]? lastGood,
        int lastGoodProtocol,
        int bootstrapProtocol,
        byte[]? loaded,
        byte[]? onDisk)
    {
        if (lastGood == null || lastGood.Length == 0) return false;
        if (!MayQuarantineOnDisk(loaded, onDisk)) return false;
        if (ImagesMatch(lastGood, loaded)) return false;
        if (!ShouldRestoreLastGoodOnProtocolMismatch(lastGoodProtocol, bootstrapProtocol))
            return false;
        return true;
    }

    /// <summary>
    /// Protocol mismatch on first load is not "restore last-good" when last-good
    /// is the same old protocol the refuse just rejected.
    /// </summary>
    public static bool ShouldRestoreLastGoodOnProtocolMismatch(int lastGoodProtocol, int bootstrapProtocol) =>
        bootstrapProtocol > 0 && lastGoodProtocol == bootstrapProtocol;

    /// <summary>
    /// When Core is absent, a settled live <c>Progenitor.dll</c> whose stamp is
    /// not the one we already tried must RequestReload (Install direct).
    /// </summary>
    public static bool ShouldRequestCoreLessLiveChange(
        bool coreLoaded,
        bool liveExists,
        bool liveSettled,
        bool sameStampAsLastTry)
    {
        if (coreLoaded) return false;
        if (!liveExists) return false;
        if (!liveSettled) return false;
        if (sameStampAsLastTry) return false;
        return true;
    }
}
