#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Effects;

/// <summary>
/// <see cref="PluginContext.Effects"/> surface. Fills owner / plugin id on
/// <see cref="Assets"/> and <see cref="PluginContext.Track"/>s every handle so unload
/// releases the plugin's <c>progenitor-effects-*</c> root.
/// </summary>
public sealed class PluginEffects
{
    private readonly PluginContext _ctx;

    internal PluginEffects(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Borrow a vanilla asset for this plugin and record the handle so unload releases it.
    /// Client-only; the server returns a disabled handle (one log line).
    /// </summary>
    public IEffectHandle Borrow(AssetKind kind, string name)
    {
        _ctx.EnsureAttached();
        var handle = Assets.Borrow(kind, name, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Host a borrowed handle on an overlay canvas or a RenderTexture RawImage. Tracked so
    /// unload releases the RT / camera / canvas even if the caller drops the host reference.
    /// </summary>
    public IHostHandle Host(IEffectHandle handle, HostKind kind)
    {
        _ctx.EnsureAttached();
        var host = Assets.Host(handle, kind);
        _ctx.Track(host);
        return host;
    }

    /// <summary>Catalog names currently in the client scene. Server returns an empty list.</summary>
    public IReadOnlyList<string> Find(AssetKind kind, string? nameContains = null)
    {
        _ctx.EnsureAttached();
        return Assets.Find(kind, nameContains);
    }

    /// <summary>
    /// Borrow a two-point sequence and apply <paramref name="style"/> (thin unlit,
    /// game red, or game blue). Tracked so unload releases the owned line and glow.
    /// </summary>
    public SequenceEffectHandle Beam(BeamStyle style)
    {
        _ctx.EnsureAttached();
        var handle = Assets.Beam(style, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Spawn a server-authored networked beam (professor coil entity with synced Start/End).
    /// Tracked so unload destroys it. Client returns a disabled handle.
    /// </summary>
    public IServerBeamHandle ServerBeam(BeamPreset preset, EffectVec3 start, EffectVec3 end, float seconds)
    {
        _ctx.EnsureAttached();
        var handle = Progenitor.Effects.ServerBeam.Spawn(preset, start, end, seconds, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Spawn <paramref name="count"/> parallel networked beams offset sideways by
    /// <paramref name="spacing"/> meters. One handle owns them all.
    /// </summary>
    public IServerBeamHandle Beams(BeamPreset preset, EffectVec3 start, EffectVec3 end, float seconds, int count, float spacing)
    {
        _ctx.EnsureAttached();
        var handle = Progenitor.Effects.ServerBeam.Beams(preset, start, end, seconds, count, spacing, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Link two <b>networked</b> entities with the game's own <c>NetworkedSequence</c>
    /// (<see cref="BeamPreset.Link"/>): the endpoints travel as entities, so every client
    /// resolves both positions itself each frame and the server writes nothing per tick.
    /// Both entities need a <c>NetworkId</c> (a character or a spawned unit; a bare marker
    /// entity does not replicate). <paramref name="sequenceGuid"/> 0 uses
    /// <see cref="BeamPresets.DefaultLinkSequence"/>. Tracked so unload destroys it.
    /// </summary>
    public IServerBeamHandle ServerBeamLink(
        Unity.Entities.Entity start,
        Unity.Entities.Entity end,
        float seconds,
        int sequenceGuid = 0)
    {
        _ctx.EnsureAttached();
        var handle = Progenitor.Effects.ServerBeam.Link(
            start.Index, start.Version, end.Index, end.Version, seconds, sequenceGuid, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Mark one networked entity with a server-authored sequence that follows it
    /// (<see cref="ServerBeam.Mark"/>). <paramref name="sequenceGuid"/> 0 uses
    /// <see cref="BeamPresets.MarkSequenceRed"/> — the build-mode invalid-placement beams.
    /// Tracked so unload destroys it.
    /// </summary>
    public IServerBeamHandle ServerBeamMark(
        Unity.Entities.Entity target,
        float seconds,
        int sequenceGuid = 0)
    {
        _ctx.EnsureAttached();
        var handle = Progenitor.Effects.ServerBeam.Mark(
            target.Index, target.Version, seconds, sequenceGuid, _ctx.Owner, _ctx.Guid);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Dispose every handle this plugin borrowed and destroy its <c>progenitor-effects-*</c>
    /// root and every server beam it spawned. Plugin unload also does this via
    /// <see cref="PluginContext.Track"/>.
    /// </summary>
    public void RemoveAll()
    {
        _ctx.EnsureAttached();
        Assets.RemoveAll(_ctx.Owner);
        Progenitor.Effects.ServerBeam.RemoveAll(_ctx.Owner);
    }
}

/// <summary>
/// Back-compat extensions that forward to <see cref="PluginContext.Effects"/>. Prefer
/// <c>ctx.Effects.Borrow</c> in new code.
/// </summary>
public static class PluginContextEffects
{
    /// <summary>Same as <see cref="PluginEffects.Borrow"/>.</summary>
    public static IEffectHandle Borrow(this PluginContext ctx, AssetKind kind, string name)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        return ctx.Effects.Borrow(kind, name);
    }

    /// <summary>Same as <see cref="PluginEffects.Host"/>.</summary>
    public static IHostHandle Host(this PluginContext ctx, IEffectHandle handle, HostKind kind)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        return ctx.Effects.Host(handle, kind);
    }

    /// <summary>Same as <see cref="PluginEffects.Find"/>.</summary>
    public static IReadOnlyList<string> Find(this PluginContext ctx, AssetKind kind, string? nameContains = null)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        return ctx.Effects.Find(kind, nameContains);
    }
}
#endif
