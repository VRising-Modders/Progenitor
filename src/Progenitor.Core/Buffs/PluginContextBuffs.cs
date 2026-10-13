#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Buffs;

/// <summary>
/// <see cref="PluginContext.Buffs"/> surface. Fills owner and plugin id on <see cref="Buffs"/> and
/// <see cref="PluginContext.Track"/>s every application, so plugin unload removes the buffs this
/// plugin applied without the author writing any teardown.
///
/// <para>The whole beginner workflow is three calls: <c>ctx.Content.DefineBuff(...)</c> once at
/// load, <c>ctx.Buffs.Stats(...)</c> once to say what the buff does, and <c>ctx.Buffs.Apply(...)</c>
/// when something should happen. See <c>docs/buffs.md</c> and the
/// <c>samples/Progenitor.TimedBuffExample</c> plugin.</para>
/// </summary>
public sealed class PluginBuffs
{
    private readonly PluginContext _ctx;

    internal PluginBuffs(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>
    /// Apply a buff this plugin defined. The returned handle is tracked: unload removes it.
    /// A refusal is a handle in <see cref="BuffApplicationState.Refused"/> with a reason you can
    /// show a player, not an exception.
    ///
    /// <para><b>Unload removes the buff silently</b> — it does <i>not</i> raise
    /// <see cref="IBuffApplication.OnStateChanged"/>, because <c>UndoAll</c> runs while this
    /// plugin's own handlers are still attached to a collectible assembly that is going away. Your
    /// own <see cref="IBuffApplication.Remove"/> from a loaded plugin still publishes.</para>
    /// </summary>
    public IBuffApplication Apply(BuffRequest request)
    {
        _ctx.EnsureAttached();
        var application = Buffs.Apply(request, _ctx.Owner, _ctx.Guid, out bool isNew);
        // Only a NEW application is tracked. A refresh returns the live one, which is already
        // tracked, and a refusal is already terminal - recording either again would add one entry
        // per call to a context bag that is only emptied on unload, and a plugin refreshing on a
        // timer does exactly that thousands of times.
        //
        // The tracked object is a silent release token, not the application: see
        // BuffApplication.TerminateForUnload (integration review F4).
        if (isNew) _ctx.Track(BuffApplication.UnloadReleaseFor(application));
        return application;
    }

    /// <summary>
    /// Apply <paramref name="buffId"/> to <paramref name="target"/> for
    /// <paramref name="seconds"/>. Pass a non-positive <paramref name="seconds"/> to keep the
    /// duration the cloned vanilla buff carries.
    /// </summary>
    public IBuffApplication Apply(string buffId, Entity target, float seconds)
    {
        var request = BuffRequest.For(buffId, BuffEntityId.From(target));
        return Apply(seconds > 0f ? request.WithDuration(seconds) : request.WithPrefabDuration());
    }

    /// <summary>
    /// Apply for a duration, refreshing the live application instead of refusing when this plugin
    /// already has this buff on this target.
    /// </summary>
    public IBuffApplication ApplyOrRefresh(string buffId, Entity target, float seconds)
    {
        var request = BuffRequest.For(buffId, BuffEntityId.From(target))
            .WithRefresh(BuffRefreshPolicy.Refresh);
        return Apply(seconds > 0f ? request.WithDuration(seconds) : request.WithPrefabDuration());
    }

    /// <summary>
    /// Put supported stat modifiers on a buff clone this plugin registered. Returns a tracked edit
    /// handle; unsupported modifiers are refused individually with their reason and the rest still
    /// apply. Call it once at load, beside <c>DefineBuff</c> — not per application.
    /// </summary>
    public IDisposable Stats(string buffId, params BuffStat[] stats)
    {
        _ctx.EnsureAttached();
        return BuffStats.Apply(_ctx, buffId, stats);
    }

    /// <summary>
    /// The live application of <paramref name="buffId"/> on <paramref name="target"/>, whoever owns
    /// it, or null. A read; it writes nothing.
    /// </summary>
    public IBuffApplication? Find(string buffId, Entity target)
    {
        _ctx.EnsureAttached();
        return Buffs.Find(buffId, BuffEntityId.From(target));
    }

    /// <summary>Every live application this plugin holds, as a snapshot.</summary>
    public IReadOnlyList<IBuffApplication> Active()
    {
        _ctx.EnsureAttached();
        return Buffs.Active(_ctx.Owner);
    }

    /// <summary>
    /// Remove every buff this plugin applied. Plugin unload does this via
    /// <see cref="PluginContext.Track"/>; call it yourself to retire one feature while the plugin
    /// runs on. Safe to call twice.
    /// </summary>
    public void RemoveAll()
    {
        _ctx.EnsureAttached();
        Buffs.RemoveAll(_ctx.Owner);
    }
}
#endif
