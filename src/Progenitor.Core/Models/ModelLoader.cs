using System;
using System.Collections.Generic;

namespace Progenitor.Models;

/// <summary>Where a <see cref="ModelLoad"/> is.</summary>
public enum ModelLoadState
{
    /// <summary>The section is being requested or streamed in.</summary>
    Pending,
    /// <summary><see cref="ModelLoad.Prefab"/> is the loaded prefab asset.</summary>
    Ready,
    /// <summary>Gave up; <see cref="ModelLoad.Reason"/> says why. The hold was released.</summary>
    Failed,
    /// <summary>Released by its owner.</summary>
    Released,
}

/// <summary>Stable failure reasons (<see cref="ModelLoad.Reason"/>).</summary>
public static class ModelLoadReasons
{
    public const string ServerSide = "server-side";
    public const string SectionNotFound = "section-not-found";
    public const string RequestRefused = "request-refused";
    public const string Timeout = "timeout";
    public const string PrefabNotInSection = "prefab-not-in-section";
    /// <summary>A resident load (<see cref="ModelSection.None"/>) found no loaded prefab with that name.</summary>
    public const string PrefabNotLoaded = "prefab-not-loaded";
    public const string NoClientWorld = "no-client-world";
    public const string Reloading = "section-unloaded-reloading";
    public const string CoreUnloaded = "core-unloaded";
}

/// <summary>What the streaming side reports for one section.</summary>
internal readonly struct SectionProbe
{
    public SectionProbe(bool found, bool requested, bool loaded)
    {
        Found = found;
        Requested = requested;
        Loaded = loaded;
    }

    /// <summary>A section entity with this scene and index exists in the client world.</summary>
    public bool Found { get; }
    /// <summary>It carries <c>RequestSceneLoaded</c> (anyone's).</summary>
    public bool Requested { get; }
    /// <summary>It carries <c>IsSectionLoaded</c>.</summary>
    public bool Loaded { get; }

    public static SectionProbe Missing => new(false, false, false);
}

/// <summary>
/// The game half behind <see cref="ModelLoader"/>; tests inject a fake. The live adapter is
/// <c>UnityModelStreamingRuntime</c>.
/// </summary>
internal interface IModelStreamingRuntime
{
    bool HasClientWorld { get; }

    SectionProbe Probe(ModelSection section);

    /// <summary>Adds <c>RequestSceneLoaded</c>. False if the section is missing or the add threw.</summary>
    bool Request(ModelSection section);

    /// <summary>Removes <c>RequestSceneLoaded</c>. False if it was not there.</summary>
    bool Unrequest(ModelSection section);

    /// <summary>The loaded prefab asset named <paramref name="prefabName"/>, or null.</summary>
    object? FindPrefab(ModelSection section, string prefabName);

    /// <summary>
    /// True when a game-owned instance of the prefab exists (a hybrid model the game spawned for
    /// a unit). Releasing our request then could unload the model out from under the game's unit.
    /// </summary>
    bool GameUsesPrefab(string prefabName);
}

/// <summary>One owner's claim on a prefab. Created by <see cref="ModelLoader.Acquire"/>.</summary>
internal sealed class ModelLoad
{
    internal ModelLoad(ModelSection section, string prefabName)
    {
        Section = section;
        PrefabName = prefabName;
    }

    public ModelSection Section { get; }
    public string PrefabName { get; }
    private ModelLoadState _state = ModelLoadState.Pending;
    internal Action? BeforeLeavingReady;
    public ModelLoadState State
    {
        get => _state;
        internal set
        {
            if (_state == ModelLoadState.Ready && value != ModelLoadState.Ready)
            {
                // Presentation owners must release borrowed renderers before the section hold.
                try { BeforeLeavingReady?.Invoke(); } catch { /* load release must still complete */ }
            }
            _state = value;
        }
    }
    public string? Reason { get; internal set; }

    /// <summary>The prefab asset (a <c>UnityEngine.GameObject</c> in the game) while Ready.</summary>
    public object? Prefab { get; internal set; }

    /// <summary>Frames spent Pending so far.</summary>
    public int PendingFrames { get; internal set; }

    internal int MissingPrefabFrames { get; set; }
}

/// <summary>What <see cref="ModelLoader.Release"/> did with the section.</summary>
internal enum ModelReleaseResult
{
    /// <summary>Already released or failed; nothing changed.</summary>
    AlreadyReleased,
    /// <summary>Other loads still hold the section.</summary>
    StillHeld,
    /// <summary>Last holder; this framework's request was removed so the game may unload it.</summary>
    Unrequested,
    /// <summary>Last holder, but the request was not ours (the game had it loaded first).</summary>
    NotOurRequest,
    /// <summary>Last holder, but a game unit uses the prefab now; the request stays so it does not vanish.</summary>
    KeptForGame,
    /// <summary>A resident load: there was no section to hold.</summary>
    Resident,
}

/// <summary>
/// Streams presentation prefabs in by entity-scene section and keeps them loaded while held.
///
/// <para><b>Mechanism.</b> The game streams each hybrid model as its own section of the
/// hybrid-model scene (<c>HybridModelStreamingHandler</c> on the client) and loads a
/// section by the standard Entities request, <c>RequestSceneLoaded</c> on the section entity.
/// This loader adds that component itself when the section is not already requested, waits for
/// <c>IsSectionLoaded</c>, then finds the prefab in the section's <c>ManagedAssetReferences</c>.
/// The handler's own token API (<c>ClaimToken</c>) cannot be called: its generic base
/// <c>AssetSubSceneStreamingHandler&lt;Singleton&gt;</c> fails to load through interop
/// (<c>TypeLoadException</c>, live 2026-09-28). Live the same day, a section requested this way
/// stayed loaded for over a minute while the handler unloaded two of its own.</para>
///
/// <para><b>Ownership.</b> Loads of one section share one hold. The request is removed on the last
/// release only when this loader added it and no game-spawned instance of the prefab exists. A
/// game unit that spawns the same model later and then goes away may remove the request out from
/// under a hold; <see cref="Tick"/> re-requests a held section every
/// <see cref="WatchIntervalFrames"/> frames and puts its loads back to Pending until it is loaded
/// again.</para>
/// </summary>
internal sealed class ModelLoader
{
    internal const int DefaultTimeoutFrames = 1800;
    internal const int DefaultMissingPrefabFrames = 120;
    internal const int WatchIntervalFrames = 30;

    private sealed class Hold
    {
        public int Holders;
        public bool RequestedByUs;
    }

    private readonly IModelStreamingRuntime _runtime;
    private readonly int _timeoutFrames;
    private readonly int _missingPrefabFrames;
    private readonly Dictionary<ModelSection, Hold> _holds = new();
    private readonly List<ModelLoad> _loads = new();
    private int _frame;

    internal ModelLoader(IModelStreamingRuntime runtime, int timeoutFrames = DefaultTimeoutFrames, int missingPrefabFrames = DefaultMissingPrefabFrames)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _timeoutFrames = timeoutFrames;
        _missingPrefabFrames = missingPrefabFrames;
    }

    internal int HeldSections => _holds.Count;

    internal int LiveLoads => _loads.Count;

    internal bool IsRequestedByUs(ModelSection section) => _holds.TryGetValue(section, out var h) && h.RequestedByUs;

    /// <summary>Starts a load. It advances on <see cref="Tick"/>; nothing touches the game here.</summary>
    internal ModelLoad Acquire(ModelSection section, string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName)) throw new ArgumentException("prefab name is required.", nameof(prefabName));
        var load = new ModelLoad(section, prefabName);
        if (!section.IsNone)
        {
            if (!_holds.TryGetValue(section, out var hold)) _holds[section] = hold = new Hold();
            hold.Holders++;
        }
        _loads.Add(load);
        return load;
    }

    /// <summary>
    /// Advances every live load one frame. Returns true while anything is held, so the caller
    /// keeps ticking (the watchdog needs it); false once nothing is left.
    /// </summary>
    internal bool Tick()
    {
        _frame++;
        if (_loads.Count == 0) return false;

        bool watch = _frame % WatchIntervalFrames == 0;
        var checkedSections = new Dictionary<ModelSection, SectionProbe>();
        foreach (var load in _loads.ToArray())
        {
            if (load.State is ModelLoadState.Failed or ModelLoadState.Released) continue;
            if (load.State == ModelLoadState.Ready && !watch) continue;

            if (!_runtime.HasClientWorld)
            {
                if (load.State == ModelLoadState.Pending && ++load.PendingFrames > _timeoutFrames)
                    Fail(load, ModelLoadReasons.NoClientWorld);
                continue;
            }

            if (load.Section.IsNone)
            {
                if (load.State == ModelLoadState.Pending) AdvanceResident(load);
                continue;
            }

            if (!checkedSections.TryGetValue(load.Section, out var probe))
                checkedSections[load.Section] = probe = _runtime.Probe(load.Section);

            if (load.State == ModelLoadState.Ready)
            {
                if (probe.Loaded && probe.Requested) continue;
                // Someone removed the request or the section unloaded under a hold: ask again.
                load.State = ModelLoadState.Pending;
                load.Reason = ModelLoadReasons.Reloading;
                load.Prefab = null;
                load.PendingFrames = 0;
            }

            Advance(load, probe);
            // A request made this frame changes what the next load of the same section should see.
            if (load.State == ModelLoadState.Pending) checkedSections.Remove(load.Section);
        }

        return _loads.Count > 0;
    }

    private void AdvanceResident(ModelLoad load)
    {
        load.PendingFrames++;
        object? prefab = _runtime.FindPrefab(load.Section, load.PrefabName);
        if (prefab != null)
        {
            load.Prefab = prefab;
            load.State = ModelLoadState.Ready;
            load.Reason = null;
            return;
        }
        if (load.PendingFrames > _missingPrefabFrames) Fail(load, ModelLoadReasons.PrefabNotLoaded);
    }

    private void Advance(ModelLoad load, SectionProbe probe)
    {
        load.PendingFrames++;
        if (!probe.Found)
        {
            Fail(load, ModelLoadReasons.SectionNotFound);
            return;
        }

        var hold = _holds[load.Section];
        if (!probe.Requested)
        {
            if (_runtime.Request(load.Section)) hold.RequestedByUs = true;
            else
            {
                Fail(load, ModelLoadReasons.RequestRefused);
                return;
            }
        }

        if (!probe.Loaded)
        {
            if (load.PendingFrames > _timeoutFrames) Fail(load, ModelLoadReasons.Timeout);
            return;
        }

        object? prefab = _runtime.FindPrefab(load.Section, load.PrefabName);
        if (prefab != null)
        {
            load.Prefab = prefab;
            load.State = ModelLoadState.Ready;
            load.Reason = null;
            load.MissingPrefabFrames = 0;
            return;
        }

        // The section is loaded but its assets are registered a frame or two later.
        if (++load.MissingPrefabFrames > _missingPrefabFrames)
            Fail(load, ModelLoadReasons.PrefabNotInSection);
    }

    private void Fail(ModelLoad load, string reason)
    {
        load.State = ModelLoadState.Failed;
        load.Reason = reason;
        load.Prefab = null;
        Drop(load);
    }

    /// <summary>Ends <paramref name="load"/>'s hold. Idempotent.</summary>
    internal ModelReleaseResult Release(ModelLoad load)
    {
        if (load == null) throw new ArgumentNullException(nameof(load));
        if (load.State is ModelLoadState.Released or ModelLoadState.Failed) return ModelReleaseResult.AlreadyReleased;
        load.State = ModelLoadState.Released;
        load.Prefab = null;
        return Drop(load);
    }

    private ModelReleaseResult Drop(ModelLoad load)
    {
        _loads.Remove(load);
        if (load.Section.IsNone) return ModelReleaseResult.Resident;
        if (!_holds.TryGetValue(load.Section, out var hold)) return ModelReleaseResult.AlreadyReleased;
        if (--hold.Holders > 0) return ModelReleaseResult.StillHeld;
        _holds.Remove(load.Section);
        if (!hold.RequestedByUs) return ModelReleaseResult.NotOurRequest;
        if (_runtime.HasClientWorld && _runtime.GameUsesPrefab(load.PrefabName)) return ModelReleaseResult.KeptForGame;
        if (_runtime.HasClientWorld) _runtime.Unrequest(load.Section);
        return ModelReleaseResult.Unrequested;
    }

    /// <summary>Releases every load (Core unload, plugin unload of the last owner).</summary>
    internal void ReleaseAll(string reason)
    {
        foreach (var load in _loads.ToArray())
        {
            Release(load);
            load.Reason = reason;
        }
    }
}
