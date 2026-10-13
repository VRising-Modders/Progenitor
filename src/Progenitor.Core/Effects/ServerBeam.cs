using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Effects;

/// <summary>
/// Server-authored networked two-point beam. Instantiates the professor coil beam prefab
/// (the only vanilla family with synced <c>Start</c>/<c>End</c>) so
/// every client draws the game's own sequence graphic. The client <see cref="Assets.Beam"/>
/// path is a local LineRenderer placeholder and does not replicate.
///
/// Reload-safe: handles are <see cref="IDisposable"/> and <see cref="RemoveAll"/> destroys
/// what that plugin spawned. Spawn / Track / Dispose / RemoveAll throw
/// <see cref="InvalidOperationException"/> off the game main thread (EntityManager writes
/// crash natively from a worker). Call from <c>Plugin.Frame</c>, a command, or EventBus.
/// </summary>
public static class ServerBeam
{
    /// <summary>SkipCoreModules token. Same string as <see cref="HarmonySlotHandoff.EffectsModule"/>.</summary>
    public const string ModuleName = HarmonySlotHandoff.EffectsModule;

    private static readonly object Gate = new();
    private static readonly List<ServerBeamHandle> Live = new();
    private static readonly List<ServerBeamHandle> PollBuffer = new();
    private static IServerBeamRuntime? _runtime;
    private static bool _installed;
    private static bool _skipped;
    private static bool _frameHooked;
    // R28 finding 28.8: one flag was shared by the coil clamp and the link clamp, so whichever
    // logged first silenced the other for the life of the Core generation.
    private static bool _coilClampLogged;
    private static bool _linkClampLogged;
    private static bool _disabledNotTrackedLogged;
    private static bool _callbackCapLogged;

    /// <summary>
    /// Written once per Core generation the first time a disabled handle is returned.
    /// R27 finding 4: on a client <c>runtime.IsServer</c> is false, so <b>every</b> call took
    /// the disabled path, and a disabled handle has <c>NeedsTick == false</c> — it was never
    /// polled, never timed out and never left <c>Live</c>. A client running <c>.beam</c> in a
    /// loop grew the static list without bound inside one session and <c>DescribePins</c>
    /// reported a climbing <c>handles=N</c> that nothing could sweep.
    /// </summary>
    internal const string DisabledNotTrackedLog =
        "Effects: server beam disabled handle not tracked (client, skipped module, or missing prefab); nothing to sweep";

    /// <summary>
    /// Spawn one networked beam from <paramref name="start"/> to <paramref name="end"/>
    /// for <paramref name="seconds"/>. <see cref="BeamPreset.CastleRed"/> is the default
    /// look (coil entity + client red tint). Pass <paramref name="owner"/> so unload can sweep.
    /// </summary>
    public static IServerBeamHandle Spawn(
        BeamPreset preset,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        object? owner = null,
        string? pluginId = null)
    {
        return SpawnCore(preset, 0, "", start, end, seconds, count: 1, spacing: 0f, owner, pluginId);
    }

    /// <summary>Spawn by PrefabGUID hash. Same gating and ownership as the preset overload.</summary>
    public static IServerBeamHandle Spawn(
        int prefabGuid,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        object? owner = null,
        string? pluginId = null)
    {
        return SpawnCore(BeamPreset.Coil, prefabGuid, "", start, end, seconds, count: 1, spacing: 0f, owner, pluginId);
    }

    /// <summary>Spawn by spawnable name or decimal GUID hash string.</summary>
    public static IServerBeamHandle Spawn(
        string prefab,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        object? owner = null,
        string? pluginId = null)
    {
        if (string.IsNullOrWhiteSpace(prefab))
            throw new ArgumentException("Prefab name or GUID hash is required.", nameof(prefab));
        string trimmed = prefab.Trim();
        int guid = 0;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            guid = parsed;
        return SpawnCore(BeamPreset.Coil, guid, trimmed, start, end, seconds, count: 1, spacing: 0f, owner, pluginId);
    }

    /// <summary>
    /// Spawn <paramref name="count"/> parallel beams offset sideways by
    /// <paramref name="spacing"/> meters (the build preview's two red beams are this
    /// class of thing: N entities, not one sequence that bakes several lines). Count is
    /// clamped to 1..<see cref="BeamPresets.MaxCount"/>. One handle owns them all.
    /// </summary>
    public static IServerBeamHandle Beams(
        BeamPreset preset,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        int count,
        float spacing,
        object? owner = null,
        string? pluginId = null)
    {
        if (count < 1) count = 1;
        if (count > BeamPresets.MaxCount) count = BeamPresets.MaxCount;
        if (spacing < 0f) spacing = 0f;
        return SpawnCore(preset, 0, "", start, end, seconds, count, spacing, owner, pluginId);
    }

    /// <summary>
    /// Link two entities with the game's own <c>NetworkedSequence</c>
    /// (<see cref="BeamPreset.Link"/>). Unlike the coil presets this one syncs the endpoints as
    /// <em>entities</em>, so every client resolves both positions itself every frame: no
    /// per-tick endpoint writes, no keep-alive, no Preview/Blast phase, and no
    /// <see cref="BeamPresets.MaxSeconds"/> cap.
    ///
    /// <paramref name="sequenceGuid"/> is a <c>SequenceGUID.GuidHash</c>, which is an ordinary
    /// <c>PrefabGUID</c> hash of a client-world <c>(Sequence)…</c> prefab. Pass 0 for
    /// <see cref="BeamPresets.DefaultLinkSequence"/>. The server cannot resolve or validate one
    /// (sequence prefabs are <c>[ComponentInWorld(4)]</c>), so an unknown hash simply draws
    /// nothing on the client; pick from the <c>BeamPresets.LinkSequence*</c> catalog.
    /// </summary>
    public static IServerBeamHandle Link(
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        float seconds,
        int sequenceGuid = 0,
        object? owner = null,
        string? pluginId = null)
    {
        return LinkCore(
            startIndex, startVersion, endIndex, endVersion, seconds,
            sequenceGuid == 0 ? BeamPresets.DefaultLinkSequence : sequenceGuid,
            owner, pluginId, "link");
    }

    /// <summary>
    /// Shared body of <see cref="Link"/> (two entities) and <see cref="Mark"/> (one entity,
    /// <c>endIndex</c> 0). <paramref name="label"/> only names the operation in the log lines.
    /// </summary>
    private static IServerBeamHandle LinkCore(
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        float seconds,
        int sequenceGuid,
        object? owner,
        string? pluginId,
        string label)
    {
        EnsureMainThread(nameof(Link));
        // R27 finding 23: the coil path clamps to MaxSeconds because PreviewTime cannot sync
        // above 20, but a NetworkedSequence has no such ceiling — so `.beam link 999999999`
        // produced a replicated, always-relevant entity with a ~31-year LifeTime.
        float requestedLink = seconds;
        seconds = BeamPresets.ClampLinkSeconds(seconds);
        if (requestedLink > BeamPresets.MaxLinkSeconds && !_linkClampLogged)
        {
            _linkClampLogged = true;
            Plugin.Logger?.LogInfo(
                "Effects: server beam " + label + " seconds clamped to "
                + BeamPresets.MaxLinkSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                + "; a NetworkedSequence carrier has no proximity gate, so an unbounded LifeTime is a leak.");
        }

        string id = string.IsNullOrWhiteSpace(pluginId) ? InferPluginId(owner) : pluginId.Trim();
        if (_skipped)
        {
            Plugin.Logger?.LogInfo("Effects: ServerBeam skipped; returning a disabled handle.");
            return Track(Disabled(BeamPreset.Link, id, owner, default, default));
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsServer)
        {
            LogDebug("Effects: ServerBeam is server-only; returning a disabled handle.");
            return Track(Disabled(BeamPreset.Link, id, owner, default, default));
        }

        float spawnedAt = Now();
        ServerBeamId entity = ServerBeamId.None;
        try
        {
            entity = runtime.SpawnLink(sequenceGuid, startIndex, startVersion, endIndex, endVersion, seconds);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("Effects: ServerBeam." + label + " threw: " + ex);
        }

        if (entity.IsNone)
        {
            string? miss = runtime.LastMissReason;
            Plugin.Logger?.LogInfo(string.IsNullOrEmpty(miss)
                ? "Effects: server beam " + label + " failed; returning a disabled handle."
                : miss);
            return Track(Disabled(BeamPreset.Link, id, owner, default, default));
        }

        string resolved = string.IsNullOrEmpty(runtime.LastResolvedName) ? "NetworkedSequence" : runtime.LastResolvedName;
        string ends = endIndex == 0
            ? "target=" + startIndex + ":" + startVersion + " playOnSelf=true"
            : startIndex + ":" + startVersion + " -> " + endIndex + ":" + endVersion;
        Plugin.Logger?.LogInfo(
            "Effects: server beam " + label + " spawned prefab=" + resolved
            + " entity=" + entity
            + " sequence=" + sequenceGuid + " " + BeamPresets.LinkSequenceName(sequenceGuid)
            + " " + ends
            + " seconds=" + seconds.ToString("0.##", CultureInfo.InvariantCulture));

        return Track(new ServerBeamHandle(
            resolved,
            id,
            owner,
            BeamPreset.Link,
            new[] { entity },
            Array.Empty<EffectVec3>(),
            default,
            default,
            spawnedAt + seconds,
            spawnedAt,
            runtime,
            enabled: true,
            tracksEntitiesNatively: true));
    }

    /// <summary>
    /// Mark <b>one</b> networked entity with a server-authored sequence: the same
    /// <c>NetworkedSequence</c> carrier as <see cref="Link"/>, but only <c>Target</c> is
    /// filled — <c>SecondaryTarget</c> stays null and <c>PlayOnSelf</c> is set, the shape the
    /// live capture found on entity-targeted rows (evidence 135). The default sequence is the
    /// build-mode invalid-placement effect (<see cref="BeamPresets.MarkSequenceRed"/>): two red
    /// flowing beams that rise from the target and follow it, because that sequence prefab
    /// carries <c>Pull_Entity</c> and reads <c>SequenceInstance.Target</c> every frame.
    /// Nothing is written per tick and there is no phase or lifetime cap.
    /// </summary>
    public static IServerBeamHandle Mark(
        int index,
        int version,
        float seconds,
        int sequenceGuid = 0,
        object? owner = null,
        string? pluginId = null)
    {
        return LinkCore(
            index, version, 0, 0, seconds,
            sequenceGuid == 0 ? BeamPresets.DefaultMarkSequence : sequenceGuid,
            owner, pluginId, "mark");
    }

    /// <summary>
    /// Dispose every handle recorded for <paramref name="owner"/> (destroy the entities).
    /// PluginContext unload does this via Track; call it yourself if you used the static API.
    /// Returns how many handles were disposed, so the reload sweep can log a real count
    /// (R27 finding 25).
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EnsureMainThread(nameof(RemoveAll));
        ServerBeamHandle[] snapshot;
        lock (Gate)
        {
            var list = new List<ServerBeamHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (ReferenceEquals(Live[i].Owner, owner))
                    list.Add(Live[i]);
            }

            snapshot = list.ToArray();
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Effects: ServerBeam.RemoveAll dispose threw: " + e.Message); }
        }

        if (snapshot.Length > 0)
            Plugin.Logger?.LogInfo("Effects: swept " + snapshot.Length + " server beam handle(s)");
        return snapshot.Length;
    }

    internal static void Install()
    {
        lock (Gate)
        {
            if (IsModuleSkipped())
            {
                _skipped = true;
                _installed = true;
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(ModuleName) + " (ServerBeam)");
                return;
            }

            _skipped = false;
            _installed = true;
            _runtime ??= CreateDefaultRuntime();
        }

        HookFrame();
        Plugin.Logger?.LogInfo(_runtime is { IsServer: true }
            ? "Effects: ServerBeam installed (server)"
            : "Effects: ServerBeam installed (client tint only)");
    }

    internal static void Uninstall()
    {
        UnhookFrame();
        ServerBeamHandle[] snapshot;
        IServerBeamRuntime? runtime;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            runtime = _runtime;
            Live.Clear();
            _installed = false;
            _skipped = false;
            _disabledNotTrackedLogged = false;
            _coilClampLogged = false;
            _linkClampLogged = false;
            _callbackCapLogged = false;
        }

        int leftover = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            bool enabled = snapshot[i].Enabled;
            try { snapshot[i].Dispose(); if (enabled) leftover++; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Effects: ServerBeam Uninstall dispose threw: " + e.Message); }
        }

        // R27 finding 10: the client tick caches an EntityQuery for the life of the generation.
        try { runtime?.Shutdown(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Effects: ServerBeam runtime shutdown threw: " + e.Message); }

        lock (Gate)
            _runtime = null;

        Plugin.Logger?.LogInfo("Effects: ServerBeam uninstalled leftover=" + leftover);
    }

    internal static string DescribePins()
    {
        if (_skipped)
            return HarmonySlotHandoff.FormatModulePinsSkipped(ModuleName + ".ServerBeam");

        var holds = new List<string>();
        lock (Gate)
        {
            if (Live.Count > 0)
                holds.Add("handles=" + Live.Count);
            if (_installed)
                holds.Add("installed");
        }

        return HarmonySlotHandoff.FormatModulePins(ModuleName + ".ServerBeam", holds);
    }

    internal static void Forget(ServerBeamHandle handle)
    {
        lock (Gate)
            Live.Remove(handle);
    }

    /// <summary>Bound on disabled handles kept alive only to run their dispose callbacks.</summary>
    internal const int DisabledCallbackCap = 256;

    /// <summary>Written once when <see cref="DisabledCallbackCap"/> is reached.</summary>
    internal const string CallbackCapLog =
        "Effects: server beam disabled handles with dispose callbacks reached the cap ("
        + "256); later callbacks run only on the caller's own Dispose.";

    /// <summary>
    /// R28 finding 28.5. <see cref="Track"/> refuses to record a <b>disabled</b> handle
    /// (R27 finding 4: on a client every call takes the disabled path, and a disabled handle
    /// is never polled and never expires, so <c>Live</c> grew without bound). But
    /// <c>IServerBeamHandle.OnDisposed</c> promises the callback runs "by
    /// <see cref="RemoveAll"/>, or by Core uninstall" — neither of which can reach a handle
    /// that is not in <c>Live</c>. The two fixes contradicted each other and a plugin following
    /// the interface doc leaked whatever it tied to a client-side handle.
    ///
    /// <para>A disabled handle is therefore recorded the moment it is given a dispose callback,
    /// and only then: a <c>.beam</c> loop that registers nothing still records nothing, and each
    /// recorded handle leaves <c>Live</c> on its own <c>Dispose</c>.</para>
    ///
    /// <para><b>R30 finding 19: this refuses after <see cref="Uninstall"/>.</b>
    /// <c>IServerBeamHandle.OnDisposed</c> is documented as callable off-thread and has no
    /// main-thread guard, so a worker thread could register a callback on a disabled handle in
    /// the window between <c>Uninstall</c>'s <c>Live.Clear()</c> and the end of its sweep. The
    /// handle landed in a post-uninstall <c>Live</c>; it was not in the snapshot, so its callback
    /// never ran — the very leak R28 28.5 added this method to close - and
    /// <see cref="DescribePins"/> then reported <c>handles=1</c> forever, which the reload pin
    /// diagnostic reads as a real pin holding the Core generation. The <c>_installed</c> check
    /// is inside the same <c>Gate</c> that <c>Uninstall</c> clears <c>Live</c> under, so the two
    /// cannot interleave: either the add happens first and the sweep disposes it, or the
    /// uninstall happens first and the add is refused. A refused registration is not dropped —
    /// <see cref="ServerBeamHandle.OnDisposed"/> keeps it on the handle's own list and the
    /// handle's own <c>Dispose</c> still runs it.</para>
    /// </summary>
    internal static void TrackForCallbacks(ServerBeamHandle handle)
    {
        if (handle == null) return;
        bool capped = false;
        lock (Gate)
        {
            if (!_installed) return;
            if (Live.Contains(handle)) return;
            if (Live.Count >= DisabledCallbackCap)
            {
                capped = !_callbackCapLogged;
                _callbackCapLogged = true;
            }
            else
            {
                Live.Add(handle);
            }
        }

        if (capped) Plugin.Logger?.LogWarning(CallbackCapLog);
    }

    /// <summary>Main-thread poll of live tracking handles, then the client tint pass.</summary>
    internal static void Poll()
    {
        lock (Gate)
        {
            PollBuffer.Clear();
            for (int i = 0; i < Live.Count; i++)
            {
                if (Live[i].NeedsTick)
                    PollBuffer.Add(Live[i]);
            }
        }

        // Time.unscaledTime boxes its float on the IL2CPP heap; read it only when a beam needs it.
        float now = PollBuffer.Count > 0 ? Now() : 0f;
        for (int i = 0; i < PollBuffer.Count; i++)
        {
            try { PollBuffer[i].Poll(now); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Effects: ServerBeam Poll threw: " + e.Message); }
        }

        try { CurrentRuntime().ClientBeamTick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Effects: server beam client tick threw: " + e.Message); }
    }

#if PROGENITOR_TESTS
    internal static Func<float>? TimeForTests;

    /// <summary>
    /// Test seam for the off-main-thread refusal (R28 finding 28.6). <c>EnsureMainThread</c> is
    /// compiled out under PROGENITOR_TESTS, so without this the ordering of the guard against
    /// the dispose latch could not be driven at all.
    /// </summary>
    internal static bool FailMainThreadCheckForTests;

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Live.Clear();
            PollBuffer.Clear();
            _runtime = null;
            _installed = false;
            _skipped = false;
            _frameHooked = false;
            _coilClampLogged = false;
            _linkClampLogged = false;
            _disabledNotTrackedLogged = false;
            _callbackCapLogged = false;
        }

        TimeForTests = null;
        FailMainThreadCheckForTests = false;
    }

    internal static int LiveCountForTests
    {
        get { lock (Gate) return Live.Count; }
    }

    internal static void SetRuntimeForTests(IServerBeamRuntime runtime)
    {
        lock (Gate)
        {
            _runtime = runtime ?? NullServerBeamRuntime.Instance;
            // A fixture that has handed the module a runtime is standing in for Install, and
            // R30 finding 19 makes _installed load-bearing for TrackForCallbacks.
            _installed = true;
        }
    }

    /// <summary>
    /// Which side of <see cref="Install"/> / <see cref="Uninstall"/> a test is on. R30 finding
    /// 19: <see cref="TrackForCallbacks"/> refuses after uninstall, so that has to be drivable.
    /// </summary>
    internal static void SetInstalledForTests(bool installed)
    {
        lock (Gate)
            _installed = installed;
    }

    internal static void SetSkippedForTests(bool skipped)
    {
        lock (Gate)
            _skipped = skipped;
    }

    internal static void TickForTests() => Poll();
#endif

    private static IServerBeamHandle SpawnCore(
        BeamPreset preset,
        int prefabGuid,
        string prefabName,
        EffectVec3 start,
        EffectVec3 end,
        float seconds,
        int count,
        float spacing,
        object? owner,
        string? pluginId)
    {
        EnsureMainThread(nameof(Spawn));
        float requested = seconds;
        seconds = BeamPresets.ClampSeconds(seconds);
        if (requested > BeamPresets.MaxSeconds && !_coilClampLogged)
        {
            _coilClampLogged = true;
            Plugin.Logger?.LogInfo(
                "Effects: server beam seconds clamped to "
                + BeamPresets.MaxSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                + "; a coil beam stays steady only while the client's local Age is under the synced PreviewTime (max "
                + BeamPresets.PreviewTimeMax.ToString("0.#", CultureInfo.InvariantCulture) + ").");
        }

        if (count < 1) count = 1;
        if (count > BeamPresets.MaxCount) count = BeamPresets.MaxCount;

        string id = string.IsNullOrWhiteSpace(pluginId)
            ? InferPluginId(owner)
            : pluginId.Trim();

        if (_skipped)
        {
            Plugin.Logger?.LogInfo("Effects: ServerBeam skipped; returning a disabled handle.");
            return Track(Disabled(preset, id, owner, start, end));
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsServer)
        {
            LogDebug("Effects: ServerBeam is server-only; returning a disabled handle.");
            return Track(Disabled(preset, id, owner, start, end));
        }

        // Steady by default: the client leaves the Preview sequence the frame its own local
        // Age passes the synced PreviewTime, so PreviewTime is pinned near its sync maximum
        // rather than set to the requested lifetime. BeamPhase.Blast lowers it instead.
        float preview = BeamPresets.PreviewTimeFor(BeamPhase.Steady, alternate: false);
        float blast = BeamPresets.BlastTimeFor(preset);
        float spawnedAt = Now();
        float deadline = spawnedAt + seconds;

        var ids = new List<ServerBeamId>(count);
        var laterals = new EffectVec3[count];
        string resolved = string.IsNullOrEmpty(prefabName) ? preset.ToString() : prefabName;

        for (int i = 0; i < count; i++)
        {
            laterals[i] = BeamPresets.LateralOffset(start, end, i, count, spacing);
            var s = Add(start, laterals[i]);
            var e = Add(end, laterals[i]);
            ServerBeamId entity = ServerBeamId.None;
            try
            {
                entity = runtime.Spawn(prefabName, prefabGuid, s, e, seconds, preview, blast);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError("Effects: ServerBeam.Spawn threw: " + ex);
            }

            if (entity.IsNone)
            {
                string? miss = runtime.LastMissReason;
                Plugin.Logger?.LogInfo(string.IsNullOrEmpty(miss)
                    ? "Effects: no coil beam prefab on this server; returning a disabled handle."
                    : miss);
                for (int d = 0; d < ids.Count; d++)
                {
                    try { runtime.Destroy(ids[d]); }
                    catch { /* */ }
                }

                return Track(Disabled(preset, id, owner, start, end));
            }

            if (!string.IsNullOrEmpty(runtime.LastResolvedName))
                resolved = runtime.LastResolvedName;
            ids.Add(entity);
            float len = Distance(s, e);
            Plugin.Logger?.LogInfo(
                "Effects: server beam spawned prefab=" + resolved
                + " entity=" + entity
                + " len=" + len.ToString("0.##", CultureInfo.InvariantCulture)
                + " preview=" + preview.ToString("0.##", CultureInfo.InvariantCulture)
                + " blast=" + blast.ToString("0.##", CultureInfo.InvariantCulture)
                + " seconds=" + seconds.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return Track(new ServerBeamHandle(
            resolved,
            id,
            owner,
            preset,
            ids.ToArray(),
            laterals,
            start,
            end,
            deadline,
            spawnedAt,
            runtime,
            enabled: true));
    }

    private static ServerBeamHandle Disabled(BeamPreset preset, string pluginId, object? owner, EffectVec3 start, EffectVec3 end) =>
        new(preset.ToString(), pluginId, owner, preset, Array.Empty<ServerBeamId>(), Array.Empty<EffectVec3>(), start, end, 0f, 0f, runtime: null, enabled: false);

    private static IServerBeamRuntime CurrentRuntime()
    {
        lock (Gate)
        {
            if (_runtime != null) return _runtime;
            _runtime = CreateDefaultRuntime();
            return _runtime;
        }
    }

    private static IServerBeamRuntime CreateDefaultRuntime()
    {
#if PROGENITOR_TESTS
        return NullServerBeamRuntime.Instance;
#else
        return new UnityServerBeamRuntime();
#endif
    }

    private static bool IsModuleSkipped()
    {
        if (HarmonySlotHandoff.SkipModulesRefusedLiveWorld) return false;
        if (HarmonySlotHandoff.ShouldSkipModule(ModuleName)) return true;
#if !PROGENITOR_TESTS
        string csv = BootstrapLink.SkipCoreModules;
        if (string.IsNullOrWhiteSpace(csv)) return false;
        var parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i].Trim(), ModuleName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
#endif
        return false;
    }

    /// <summary>
    /// Record a live handle so unload can sweep it. A <b>disabled</b> handle owns nothing and
    /// can never expire, so it is not tracked at all (R27 finding 4); the refusal is logged
    /// once per Core generation.
    /// </summary>
    private static ServerBeamHandle Track(ServerBeamHandle handle)
    {
        if (!handle.Enabled)
        {
            bool log;
            lock (Gate)
            {
                log = !_disabledNotTrackedLogged;
                _disabledNotTrackedLogged = true;
            }

            if (log) Plugin.Logger?.LogInfo(DisabledNotTrackedLog);
            return handle;
        }

        // R31 finding 21: the same `_installed` check TrackForCallbacks got, on the enabled
        // path, under the same Gate that Uninstall clears `Live` under. A Spawn after Uninstall
        // — a plugin spawning from its own unload path, or a queued callback firing after Core's
        // module uninstall — used to add a handle to a post-uninstall `Live` that nothing ever
        // sweeps: UnhookFrame has already run so Poll never expires it, and DescribePins then
        // reports handles=1 forever, which the reload pin diagnostic reads as a live pin holding
        // the Core generation. The two cannot interleave: either the add happens first and
        // Uninstall's snapshot disposes it, or Uninstall happens first and the add is refused.
        // The handle itself is still returned and still disposable by its owner.
        lock (Gate)
        {
            if (!_installed) return handle;
            Live.Add(handle);
        }

        return handle;
    }

    private static string InferPluginId(object? owner)
    {
        if (owner == null) return "unowned";
        try
        {
            var guid = owner.GetType().GetProperty("GUID")?.GetValue(owner) as string
                ?? owner.GetType().GetProperty("Guid")?.GetValue(owner) as string;
            if (!string.IsNullOrWhiteSpace(guid)) return guid.Trim();
        }
        catch { /* */ }
        return owner.GetType().FullName ?? "unowned";
    }

    private static void HookFrame()
    {
#if !PROGENITOR_TESTS
        if (_frameHooked) return;
        Plugin.Frame += Poll;
        _frameHooked = true;
#else
        _frameHooked = true;
#endif
    }

    private static void UnhookFrame()
    {
        if (!_frameHooked) return;
#if !PROGENITOR_TESTS
        Plugin.Frame -= Poll;
#endif
        _frameHooked = false;
    }

    /// <summary>
    /// Whether this call is on the game main thread, as a question rather than a refusal.
    /// <see cref="ServerBeamHandle.Dispose"/> needs the answer <em>before</em> it decides
    /// whether to do the ECS work here or post it (R29 finding 15); every other caller wants
    /// <see cref="EnsureMainThread"/>, which throws.
    /// </summary>
    internal static bool OnMainThread()
    {
#if PROGENITOR_TESTS
        return !FailMainThreadCheckForTests;
#else
        try { return Dev.MainThread.IsMainThread; }
        catch { return false; }
#endif
    }

    internal static void EnsureMainThread(string op)
    {
        if (OnMainThread()) return;
#if PROGENITOR_TESTS
        throw new InvalidOperationException(
            "ServerBeam." + op + " must run on the game main thread (test seam).");
#else
        throw new InvalidOperationException(
            "ServerBeam." + op + " must run on the game main thread (Plugin.Frame, a command, or EventBus). EntityManager writes crash natively from a worker.");
#endif
    }

    internal static void LogDebug(string line)
    {
#if PROGENITOR_TESTS
        _ = line;
#else
        Plugin.Logger?.LogDebug(line);
#endif
    }

    internal static float Now()
    {
#if PROGENITOR_TESTS
        return TimeForTests == null ? 0f : TimeForTests();
#else
        try { return UnityEngine.Time.unscaledTime; }
        catch { return 0f; }
#endif
    }

    private static EffectVec3 Add(EffectVec3 a, EffectVec3 b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static float Distance(EffectVec3 a, EffectVec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
