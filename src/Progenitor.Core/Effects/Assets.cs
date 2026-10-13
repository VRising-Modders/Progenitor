using System;
using System.Collections.Generic;

namespace Progenitor.Effects;

/// <summary>
/// Client-side vanilla-asset borrowing: get-by-name plus clone. Mock resolution and kitbash
/// composition are not part of this cut.
/// Every call is safe on the server: it returns a disabled handle and one log line so a
/// shared plugin does not need a side check. Reload-safe: handles are <see cref="IDisposable"/>
/// and <see cref="RemoveAll"/> destroys that plugin's exact <c>progenitor-effects-&lt;id&gt;</c>
/// root (not a prefix of another plugin's GUID).
/// </summary>
public static class Assets
{
    /// <summary>
    /// SkipCoreModules token. Same string as <see cref="HarmonySlotHandoff.EffectsModule"/>.
    /// </summary>
    public const string ModuleName = HarmonySlotHandoff.EffectsModule;

    private static readonly object Gate = new();
    private static readonly List<EffectHandle> Live = new();
    private static IEffectRuntime? _runtime;
    private static bool _installed;
    private static bool _skipped;

    /// <summary>
    /// When true, play creates client-local markers and binds
    /// <c>SequenceInstance.Target</c> / <c>SecondaryTarget</c> after the driver clone
    /// exists. Evidence 101: the Cardinal graph still writes origin + driver every
    /// frame regardless of those markers, so this defaults off; the Bootstrap LateFrame
    /// <c>SetPosition</c> write (FrameTicker LateUpdate after a silent
    /// <c>Camera.onPreCull</c>, because HDRP never calls <c>FireOnPreCull</c>)
    /// is the drawing path. Left in for a live re-check.
    /// </summary>
    public static bool BindSequencerMarkers { get; set; }

    /// <summary>
    /// Clone or construct a vanilla asset by <paramref name="kind"/> and <paramref name="name"/>.
    /// The returned concrete type is <see cref="UiImageHandle"/>, <see cref="ParticleHandle"/>,
    /// <see cref="VolumeHandle"/> or <see cref="SequenceEffectHandle"/>. VisualEffect is
    /// catalog-only this cut. Pass <paramref name="owner"/> (the BepInEx plugin) and
    /// <paramref name="pluginId"/> (its GUID) so unload can sweep the root; the
    /// <c>PluginContext</c> extension fills both and <c>Track</c>s the handle.
    /// </summary>
    public static IEffectHandle Borrow(AssetKind kind, string name, object? owner = null, string? pluginId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        string id = string.IsNullOrWhiteSpace(pluginId)
            ? InferPluginId(owner)
            : pluginId.Trim();
        string root = EffectOwnership.RootName(id);

        if (_skipped)
        {
            Plugin.Logger?.LogInfo("Effects: Borrow(" + kind + ", '" + name + "') skipped; returning a disabled handle.");
            return Track(CreateHandle(kind, name, id, root, owner, runtime: null, enabled: false));
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsClient)
        {
            Plugin.Logger?.LogInfo("Effects: Borrow(" + kind + ", '" + name + "') is client-only; returning a disabled handle.");
            return Track(CreateHandle(kind, name, id, root, owner, runtime: null, enabled: false));
        }

        if (kind == AssetKind.VisualEffect)
        {
            Plugin.Logger?.LogInfo("Effects: Borrow(VisualEffect, '" + name + "') is catalog-only in this cut; returning a disabled handle.");
            return Track(CreateHandle(kind, name, id, root, owner, runtime: null, enabled: false));
        }

        IRuntimeObject? obj = null;
        try
        {
            obj = runtime.Borrow(kind, name, root);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Borrow(" + kind + ", '" + name + "') threw: " + e);
        }

        bool enabled = obj != null;
        if (!enabled)
        {
            string? miss = runtime.LastMissReason;
            Plugin.Logger?.LogInfo(string.IsNullOrEmpty(miss)
                ? "Effects: no " + kind + " named '" + name + "' on this client; returning a disabled handle."
                : miss);
        }
        else
            Plugin.Logger?.LogInfo("Effects: borrowed " + kind + " '" + name + "' under " + root);

        return Track(CreateHandle(kind, name, id, root, owner, obj, enabled));
    }

    /// <summary>
    /// Borrow a two-point sequence and apply <paramref name="style"/>. GameRed / GameFeed
    /// draw with a copy of a vanilla LineRenderer-bodied beam prefab (evidence 130); the
    /// sequence itself supplies sound and particles and is resolved from coffin /
    /// invalid-placement / arena-flag GUIDs when one of those entities exists, else Cardinal.
    /// Only when neither a style sequence nor a line prefab resolves does this fall back to
    /// <see cref="BeamStyle.Thin"/> with one log line. Server returns a disabled handle.
    /// </summary>
    public static SequenceEffectHandle Beam(BeamStyle style, object? owner = null, string? pluginId = null)
    {
        string name = ResolveBeamBorrowName(style, out BeamStyle used, out string source);
        var handle = Borrow(AssetKind.Sequence, name, owner, pluginId);
        var seq = handle as SequenceEffectHandle
            ?? new SequenceEffectHandle(name, pluginId ?? EffectOwnership.UnownedId,
                EffectOwnership.RootName(pluginId), owner, runtime: null, enabled: false);
        try
        {
            if (seq.Enabled)
                seq.ApplyPreset(used, source);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Beam(" + style + ") ApplyPreset threw: " + e.Message);
        }

        return seq;
    }

    /// <summary>
    /// Sample <c>.fx beam</c> default: GameRed when the client has a castle-family
    /// sequence GUID, otherwise Thin. Server is always Thin (no-op runtime).
    /// </summary>
    public static BeamStyle PreferredBeamStyle()
    {
        try { return BeamCatalog.Preferred(CurrentRuntime()); }
        catch { return BeamStyle.Thin; }
    }

    private static string ResolveBeamBorrowName(BeamStyle style, out BeamStyle used, out string source)
    {
        used = style;
        source = "";
        var runtime = CurrentRuntime();
        if (BeamCatalog.WantsGameLine(style))
        {
            // The sequence supplies sound and particles; the body is a copy of a vanilla
            // beam prefab (evidence 130). A style keeps its identity as long as one of the
            // two resolves, so a client without the castle sequences still gets a red beam.
            string lineSource = "";
            bool haveLine = false;
            try
            {
                haveLine = !string.IsNullOrEmpty(runtime.ResolveBeamLinePrefab(style, out lineSource));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: Beam(" + style + ") line prefab resolve threw: " + e.Message);
            }

            try
            {
                string? found = runtime.ResolveBeamPreset(style, out source);
                if (!string.IsNullOrEmpty(found))
                {
                    if (haveLine) source = source + " + " + lineSource;
                    return found;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: Beam(" + style + ") resolve threw: " + e.Message);
            }

            if (haveLine)
            {
                // No castle/feed sequence entity on this client: play the Cardinal sequence
                // for the audio and let the borrowed line draw the beam. The style keeps its
                // identity (before EV12 this degraded to Thin).
                try
                {
                    string? cardinal = runtime.ResolveBeamPreset(BeamStyle.Thin, out source);
                    if (!string.IsNullOrEmpty(cardinal))
                    {
                        source = source + " + " + lineSource;
                        return cardinal;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Effects: Beam(" + style + ") Cardinal resolve threw: " + e.Message);
                }

                source = "Cardinal AB_Cardinal_SummonOrb_Beam_Buff dump/wiki guid -2014639169 + " + lineSource;
                return BeamCatalog.CardinalBuffHashName;
            }

            Plugin.Logger?.LogInfo("Effects: Beam(" + style + ") not found at runtime; using Thin");
            used = BeamStyle.Thin;
            source = "fallback Thin";
        }

        if (style == BeamStyle.GameBlue || used == BeamStyle.Thin || style == BeamStyle.Thin)
        {
            try
            {
                string? found = runtime.ResolveBeamPreset(used == BeamStyle.GameBlue ? BeamStyle.GameBlue : BeamStyle.Thin, out source);
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: Beam(" + used + ") Cardinal resolve threw: " + e.Message);
            }

            source = "Cardinal AB_Cardinal_SummonOrb_Beam_Buff dump/wiki guid -2014639169";
            return BeamCatalog.CardinalBuffHashName;
        }

        source = "Cardinal AB_Cardinal_SummonOrb_Beam_Buff dump/wiki guid -2014639169";
        return BeamCatalog.CardinalBuffHashName;
    }

    /// <summary>
    /// Host <paramref name="handle"/> on an overlay canvas or through a RenderTexture camera
    /// into a RawImage. Overlay is native for images; world particles and sequences need
    /// RenderTexture. Dispose of the returned host releases RT, camera and canvas only.
    /// Disposing the borrow handle also disposes this host.
    /// </summary>
    public static IHostHandle Host(IEffectHandle handle, HostKind kind)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));

        if (handle is not EffectHandle owned)
        {
            Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") ignored a foreign handle; returning a disabled handle.");
            return HostHandle.Disabled(kind);
        }

        if (owned.IsDisposed)
            return HostHandle.Disabled(kind);

        if (_skipped || !owned.Enabled)
        {
            Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") is client-only; returning a disabled handle.");
            return owned.AttachHost(null, kind, enabled: false);
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsClient)
        {
            Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") is client-only; returning a disabled handle.");
            return owned.AttachHost(null, kind, enabled: false);
        }

        IRuntimeHost? host = null;
        try
        {
            if (owned.RuntimeObject != null)
                host = runtime.Host(owned.RuntimeObject, kind, owned.RootName);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Host(" + kind + ") threw: " + e);
        }

        bool enabled = host != null;
        if (enabled)
            Plugin.Logger?.LogInfo("Effects: hosted " + kind + " for " + owned.Kind + " '" + owned.Name + "'");
        else
            Plugin.Logger?.LogInfo("Effects: Host(" + kind + ") failed for '" + owned.Name + "'; returning a disabled handle.");

        var attached = owned.AttachHost(host, kind, enabled);
        if (owned is SequenceEffectHandle seq)
            seq.ArmDriverPoll();
        return attached;
    }

    /// <summary>
    /// Names the client currently has for <paramref name="kind"/>. <paramref name="nameContains"/>
    /// is a case-insensitive substring filter; null or empty returns every name. Server returns
    /// an empty list and one log line.
    /// </summary>
    public static IReadOnlyList<string> Find(AssetKind kind, string? nameContains = null)
    {
        if (_skipped)
        {
            Plugin.Logger?.LogInfo("Effects: Find(" + kind + ") skipped; returning no names.");
            return Array.Empty<string>();
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsClient)
        {
            Plugin.Logger?.LogInfo("Effects: Find(" + kind + ") is client-only; returning no names.");
            return Array.Empty<string>();
        }

        try
        {
            return runtime.Find(kind, nameContains) ?? Array.Empty<string>();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Effects: Find(" + kind + ") threw: " + e);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Dispose every handle recorded for <paramref name="owner"/> and destroy that plugin's
    /// exact <c>progenitor-effects-&lt;id&gt;</c> root (not a prefix match). PluginContext
    /// unload does this via Track; call it yourself if you used the static API with an owner.
    /// Returns how many handles were disposed, so the reload sweep can log a real count
    /// (R27 finding 25).
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        EffectHandle[] snapshot;
        lock (Gate)
        {
            var list = new List<EffectHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (ReferenceEquals(Live[i].Owner, owner))
                    list.Add(Live[i]);
            }

            snapshot = list.ToArray();
        }

        string root = snapshot.Length > 0
            ? snapshot[0].RootName
            : EffectOwnership.RootName(InferPluginId(owner));

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Effects: RemoveAll dispose threw: " + e.Message); }
        }

        try
        {
            CurrentRuntime().DestroyRoot(root);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: RemoveAll sweep threw: " + e.Message);
        }

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
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(ModuleName));
                return;
            }

            _skipped = false;
            _installed = true;
            _runtime ??= CreateDefaultRuntime();
        }

        try { HarmonySlotHandoff.TrySetLateFrame(SequenceLateDriver.OnLateFrame); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Effects: LateFrame slot fill threw: " + e.Message); }

        Plugin.Logger?.LogInfo(_runtime is { IsClient: true }
            ? "Effects: installed (client)"
            : "Effects: installed (server no-op)");
    }

    internal static void Uninstall()
    {
        EffectHandle[] snapshot;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            Live.Clear();
            _installed = false;
            _skipped = false;
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Effects: Uninstall dispose threw: " + e.Message); }
        }

        int leftover = 0;
        try
        {
            leftover = CurrentRuntime().DestroyByPrefix(EffectOwnership.Prefix);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Uninstall sweep threw: " + e.Message);
        }

        try { SequenceLateDriver.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Effects: late-driver uninstall threw: " + e.Message); }
        try { HarmonySlotHandoff.ClearLateFrame(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Effects: LateFrame slot clear threw: " + e.Message); }

        lock (Gate)
            _runtime = null;

        Plugin.Logger?.LogInfo("Effects: uninstalled leftover=" + leftover);
    }

    internal static string DescribePins()
    {
        if (_skipped)
            return HarmonySlotHandoff.FormatModulePinsSkipped(ModuleName);

        var holds = new List<string>();
        lock (Gate)
        {
            if (Live.Count > 0)
                holds.Add("handles=" + Live.Count);
            if (_installed)
                holds.Add("installed");
        }
        try
        {
            string late = SequenceLateDriver.DescribePins();
            if (!string.IsNullOrEmpty(late))
                holds.Add(late);
        }
        catch { /* */ }

        return HarmonySlotHandoff.FormatModulePins(ModuleName, holds);
    }

    internal static void Forget(EffectHandle handle)
    {
        lock (Gate)
            Live.Remove(handle);
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Live.Clear();
            _runtime = null;
            _installed = false;
            _skipped = false;
            BindSequencerMarkers = false;
        }
        SequenceLateDriver.Uninstall();
        HarmonySlotHandoff.ClearLateFrame();
    }

    internal static void SetRuntimeForTests(IEffectRuntime runtime)
    {
        lock (Gate)
            _runtime = runtime ?? NullEffectRuntime.Instance;
    }

    internal static void SetSkippedForTests(bool skipped)
    {
        lock (Gate)
            _skipped = skipped;
    }
#endif

    private static IEffectRuntime CurrentRuntime()
    {
        lock (Gate)
        {
            if (_runtime != null) return _runtime;
            _runtime = CreateDefaultRuntime();
            return _runtime;
        }
    }

    /// <summary>Bounded late-driver poll length the sequence handle reads. Tests may override via the fake.</summary>
    internal static int DriverPollFrames
    {
        get
        {
            try
            {
                int n = CurrentRuntime().MaxDriverPollFrames;
                return n > 0 ? n : SequenceEffectHandle.DefaultDriverPollFrames;
            }
            catch
            {
                return SequenceEffectHandle.DefaultDriverPollFrames;
            }
        }
    }

    private static IEffectRuntime CreateDefaultRuntime()
    {
#if PROGENITOR_TESTS
        return NullEffectRuntime.Instance;
#else
        // Server: nothing is constructed. NullEffectRuntime is the documented no-op
        // (R22 #10). UnityEffectRuntime.FindObjectsOfType would still walk a dedicated
        // server scene on Uninstall/RemoveAll.
        if (Plugin.IsServerProcess)
            return NullEffectRuntime.Instance;
        return new UnityEffectRuntime();
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

    private static EffectHandle Track(EffectHandle handle)
    {
        lock (Gate)
            Live.Add(handle);
        return handle;
    }

    private static EffectHandle CreateHandle(
        AssetKind kind,
        string name,
        string pluginId,
        string rootName,
        object? owner,
        IRuntimeObject? runtime,
        bool enabled)
    {
        return kind switch
        {
            AssetKind.Image => new UiImageHandle(name, pluginId, rootName, owner, runtime, enabled),
            AssetKind.Particle => new ParticleHandle(name, pluginId, rootName, owner, runtime, enabled),
            AssetKind.Volume => new VolumeHandle(name, pluginId, rootName, owner, runtime, enabled),
            AssetKind.Sequence => new SequenceEffectHandle(name, pluginId, rootName, owner, runtime, enabled),
            _ => new DisabledEffectHandle(kind, name, pluginId, rootName, owner),
        };
    }

    private static string InferPluginId(object? owner)
    {
        if (owner == null) return EffectOwnership.UnownedId;
        try
        {
            var type = owner.GetType();
            string? full = type.FullName;
            if (!string.IsNullOrEmpty(full)) return EffectOwnership.Sanitize(full);
        }
        catch
        {
            // owner.GetType can throw from a collectible ALC that has already unloaded.
        }

        return EffectOwnership.UnownedId;
    }
}
