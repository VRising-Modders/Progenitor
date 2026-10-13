using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Progenitor.Events;
using Unity.Entities;

namespace Progenitor.Plugins;

/// <summary>
/// Helpers that do not need the context type parameter. Kept as a non-generic peer of
/// <see cref="ProgenitorPlugin{TContext}"/> so call sites can write <c>ProgenitorPlugin.WhenWorldReady</c>
/// without repeating <c>TContext</c>.
/// </summary>
public static class ProgenitorPlugin
{
    /// <summary>
    /// Run <paramref name="callback"/> now if this side's world already exists (a reload after
    /// bootstrap), otherwise once on the next matching <see cref="WorldReady"/>. Either way it
    /// fires at most once; dispose the result (or unload the plugin) to cancel a pending wait.
    /// </summary>
    public static IDisposable WhenWorldReady(PluginContext ctx, Action<World> callback)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        ctx.EnsureAttached();

        var world = ctx.World;
        if (world != null)
        {
            callback(world);
            return NopDisposable.Instance;
        }

        IDisposable sub = null!;
        int once = 0;
        sub = ctx.Events.On<WorldReady>(e =>
        {
            if (e.IsServer != ctx.IsServer) return;
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            try { callback(e.World); }
            finally { sub.Dispose(); }
        });

        // Same-thread creation between the first check and the subscribe would otherwise wait forever.
        world = ctx.World;
        if (world != null && Interlocked.Exchange(ref once, 1) == 0)
        {
            try { callback(world); }
            finally { sub.Dispose(); }
        }

        return sub;
    }
}

/// <summary>
/// BepInEx plugin base that owns a <typeparamref name="TContext"/> and reverses every registration
/// that context recorded. Authors implement <see cref="OnLoad"/> and never write <c>Unload</c>
/// cleanup for events, hooks, commands, net handlers, handshake rows, or Harmony patches on
/// <see cref="PluginContext.UnsafeHarmony"/>.
/// </summary>
/// <example>
/// <code>
/// [BepInPlugin("example.mod", "Example", "1.0.0")]
/// [BepInDependency("progenitor")]
/// [ProgenitorSide(Side.Server)]
/// [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
/// public class Plugin : ProgenitorPlugin&lt;ExampleContext&gt;
/// {
///     protected override void OnLoad(ExampleContext ctx)
///     {
///         ctx.Commands.Register&lt;ExampleCommands&gt;();
///         ctx.Events.On&lt;ChatMessage&gt;(e =&gt; ctx.Log.LogInfo(e.Text));
///         ctx.Handshake.DeclareSelf();
///         ProgenitorPlugin.WhenWorldReady(ctx, world =&gt; ctx.Log.LogInfo("ready: " + world.Name));
///     }
/// }
/// public class ExampleContext : PluginContext { }
/// </code>
/// </example>
/// <typeparam name="TContext">
/// Author-owned state. Must have a public parameterless constructor; keep that constructor empty
/// and do setup in <see cref="OnLoad"/>, because the wrappers are attached after <c>new</c>.
/// </typeparam>
public abstract class ProgenitorPlugin<TContext> : BasePlugin
    where TContext : PluginContext, new()
{
    private TContext? _context;

    /// <summary>
    /// The attached context after a successful <see cref="OnLoad"/>, otherwise null (dormant side
    /// or a load that threw and was undone).
    /// </summary>
    protected TContext? Context => _context;

    /// <summary>
    /// Sealed so a derived plugin cannot skip the side check or leak registrations. Reads
    /// <see cref="ProgenitorSideAttribute"/> (default <see cref="Side.Both"/>); a mismatch logs
    /// dormant and creates nothing. On a match, constructs the context, loads file-based
    /// localization next to the DLL, calls <see cref="OnLoad"/>, and undoes whatever was
    /// recorded if <see cref="OnLoad"/> throws.
    /// </summary>
    public sealed override void Load()
    {
        var meta = MetadataHelper.GetMetadata(this);
        var declared = GetType().GetCustomAttribute<ProgenitorSideAttribute>()?.Side ?? Side.Both;
        var process = Plugin.IsServerProcess ? Side.Server : Side.Client;

        string guid = meta.GUID;
        // SETTINGS1 stage 3: what the Mods menu knows about this plugin (plain strings only).
        var counterpart = CounterpartDeclaration.From(GetType().GetCustomAttribute<CounterpartAttribute>());
        var dependencies = HardDependencies();
        // Mod compatibility C3: the peer versions this build works with (the feature table and the join evaluation read it).
        CompatDeclaration? compat = null;
        try
        {
            compat = CompatDeclaration.From(GetType(), meta.Version?.ToString() ?? "");
            if (compat != null && compat.PeersInvalid)
                Log.LogWarning($"{meta.Name}: [NetworkCompatibility] Peers '{compat.InvalidPeers}' is not a valid range; it is ignored and "
                    + $"{compat.EffectiveRange} applies. See docs/modules.md (compatibility ranges).");
        }
        catch { compat = null; }
        try { ModRegistry.NoteLoading(guid, meta.Name, meta.Version?.ToString() ?? "", dependencies, counterpart, compat); }
        catch { /* the registry is evidence, never a gate */ }

        if ((declared & process) == 0)
        {
            Log.LogInfo($"{meta.Name} dormant on this side ({process}); [ProgenitorSide] is {declared}.");
            ModRegistry.NoteDormant(guid);
            return;
        }

        // SETTINGS1 stage 3: the player turned this mod off in the Mods menu (or something it needs is
        // off). Nothing of it runs: no context, no OnLoad. This is what keeps a restart-only mod off
        // at the next start, and what keeps a hot-loadable one inert should anything load it anyway.
        string? off = null;
        try { off = ModEnableState.OffReason(guid, dependencies, ModRegistry.IsSkipped); }
        catch (Exception e) { Log.LogWarning($"{meta.Name}: the Mods menu's on/off state could not be read ({e.Message}); loading it."); }
        if (off != null)
        {
            Log.LogInfo($"{meta.Name} {meta.Version} not loaded: {off}.");
            string blocking = "";
            try
            {
                if (!ModEnableState.IsDisabled(guid))
                    blocking = ModEnableState.BlockingDependency(guid, dependencies, ModRegistry.IsSkipped) ?? "";
            }
            catch { blocking = ""; }
            ModRegistry.NoteSkipped(guid, off, blocking);
            return;
        }

        Progenitor.Ui.ModProblems.ClearLoadProblems(guid);
        DirectKeyReadCheck.Run(GetType().Assembly, guid, meta.Name, Log);
        TContext? ctx = null;
        try
        {
            ctx = new TContext();
            ctx.Attach(this, process);
            _context = ctx;
            try
            {
                var loaded = Progenitor.Localization.Localization.LoadForPlugin(GetType().Assembly, this);
                if (loaded != null) ctx.Track(loaded);
            }
            catch (Exception loc)
            {
                Log.LogError($"{meta.Name} localization load failed: {loc}");
                Progenitor.Ui.ModProblems.Report(Progenitor.Ui.ModProblems.LocalizationFailed(guid, loc.ToString()));
            }
            OnLoad(ctx);
            // JSON content definition files (roadmap M4.6), after OnLoad on purpose: the ids this
            // plugin registered in C# already exist, so a file that redefines one is refused
            // instead of quietly winning. Same folder probe as localization / assets.
            try
            {
                var content = Progenitor.Content.Json.ContentFiles.LoadForPlugin(GetType().Assembly, this);
                if (content != null) ctx.Track(content);
            }
            catch (Exception cf)
            {
                Log.LogError($"{meta.Name} content files load failed: {cf}");
                Progenitor.Ui.ModProblems.Report(Progenitor.Ui.ModProblems.ContentFailed(guid, cf.ToString()));
            }
            // Attribute alone is enough: DeclareSelf is idempotent if OnLoad already called it.
            if (GetType().GetCustomAttribute<NetworkCompatibilityAttribute>() != null)
                ctx.Handshake.DeclareSelf();
            Log.LogInfo($"{meta.Name} {meta.Version} loaded on {process}.");
            ModRegistry.NoteLoaded(guid);
        }
        catch (Exception e)
        {
            Log.LogError($"{meta.Name} OnLoad threw; undoing registrations: {e}");
            // Unknown keeps state: a failed reload of a plugin that was swapped out must not
            // drop what the previous instance kept (UnlockPrune reads this reason).
            try
            {
                using (PluginUnloadScope.Push(PluginUnloadReason.Unknown))
                    ctx?.UndoAll();
            }
            catch (Exception undo) { Log.LogError($"{meta.Name} undo after OnLoad failure threw: {undo}"); }
            _context = null;
            ModRegistry.NoteFailed(guid);
            // SETTINGS1 stage 3: the Mods page says "Failed to load", with Retry when the mod can be
            // loaded again without a restart (decided when the page is drawn: the reloader has not
            // registered this plugin yet while its OnLoad is throwing). Nothing is disabled because of it.
            Progenitor.Ui.ModProblems.Report(Progenitor.Ui.ModProblems.LoadFailed(guid, e.ToString(), retry: false));
        }
    }

    /// <summary>The GUIDs of this plugin's hard <c>[BepInDependency]</c>s.</summary>
    private List<string> HardDependencies()
    {
        var list = new List<string>();
        try
        {
            foreach (var dep in GetType().GetCustomAttributes<BepInDependency>(inherit: true))
            {
                if (dep == null || string.IsNullOrWhiteSpace(dep.DependencyGUID)) continue;
                if ((dep.Flags & BepInDependency.DependencyFlags.HardDependency) == 0) continue;
                list.Add(dep.DependencyGUID);
            }
        }
        catch { /* no dependencies known: the plugin is judged on its own */ }
        return list;
    }

    /// <summary>
    /// Sealed so cleanup cannot be skipped. Calls <see cref="OnUnload"/> (exceptions logged, not
    /// thrown), then disposes every recorded registration in reverse order, each in its own
    /// try/catch, and returns true so the reloader treats the plugin as unloadable.
    /// </summary>
    public sealed override bool Unload()
    {
        var ctx = _context;
        _context = null;
        // SETTINGS1 stage 3: an unloaded mod's problems are no longer evidence (a failed load, a
        // file that did not load); its conflicts clear with its binds and commands.
        try
        {
            string guid = MetadataHelper.GetMetadata(this).GUID;
            ModRegistry.NoteUnloaded(guid);
            Progenitor.Ui.ModProblems.Clear(guid);
        }
        catch { /* evidence, never a gate */ }
        if (ctx == null) return true;

        var name = MetadataHelper.GetMetadata(this).Name;
        var reason = ResolveUnloadReason(name);

        try { OnUnload(ctx, reason); }
        catch (Exception e) { Log.LogError($"{name} OnUnload threw: {e}"); }

        // Registrations read the reason while they are reversed (UnlockPrune keeps a player's
        // unlock of this plugin's recipe across a Swap and drops it on a Removal).
        using (PluginUnloadScope.Push(reason))
            ctx.UndoAll();
        return true;
    }

    /// <summary>Called on the matching side after the context is attached. Register work here.</summary>
    protected abstract void OnLoad(TContext ctx);

    /// <summary>
    /// Optional extra cleanup for things the bag cannot see (statics that should not exist,
    /// IL2CPP types that cannot be uninjected). Runs before registrations are reversed. Default
    /// is a no-op so most plugins never override it.
    ///
    /// This overload does not say <em>why</em> the plugin is unloading. Override
    /// <see cref="OnUnload(TContext, PluginUnloadReason)"/> instead when the plugin owns live
    /// game state (a buff instance on a character) that should survive a Core hot swap.
    /// </summary>
    protected virtual void OnUnload(TContext ctx) { }

    /// <summary>
    /// Same hook with the reason attached (job FBUFF2). The default forwards to
    /// <see cref="OnUnload(TContext)"/>, so every plugin written before this existed keeps
    /// working unchanged; only one of the two is ever called for a given unload.
    ///
    /// <see cref="PluginUnloadReason.Swap"/> means the same DLL is coming back in seconds under
    /// the next Core generation: keep world state the next generation can find again.
    /// <see cref="PluginUnloadReason.Removal"/> means the plugin is gone: sweep anything only
    /// this plugin can undo. <see cref="PluginUnloadReason.Suspend"/> is a per-server pause (the
    /// plugin comes back when the client leaves) or the player's switch in the Mods menu (it comes
    /// back when they turn it on). <see cref="PluginUnloadReason.Shutdown"/> is the process exiting, and
    /// <see cref="PluginUnloadReason.Unknown"/> means nothing could be established — treat both
    /// like <see cref="PluginUnloadReason.Swap"/> and keep. Sweep only on a positive Removal.
    /// </summary>
    protected virtual void OnUnload(TContext ctx, PluginUnloadReason reason) => OnUnload(ctx);

    /// <summary>
    /// The reason the active reloader published, or an inference when none did. Job FBUFF4
    /// (R30 findings 3, 4, 18): the reloader that runs in production is Bootstrap's
    /// <c>ReloaderHost</c>, and it now hands the reason across as a plain <c>int</c> in
    /// <c>Progenitor.Bootstrap.PluginUnloadSlot</c> — so a plugin DLL redeploy is the
    /// <see cref="PluginUnloadReason.Swap"/> it always was in intent, not the
    /// <see cref="PluginUnloadReason.Removal"/> the old inference produced. Process exit comes
    /// from Bootstrap's <c>AppDomain.ProcessExit</c> handler through the same slot.
    /// <see cref="CoreSwapProbe"/> is only the fallback now, and a probe that cannot answer
    /// yields <see cref="PluginUnloadReason.Unknown"/>, which keeps state. Always logged;
    /// a probe failure is warned about once, naming the member.
    /// </summary>
    private PluginUnloadReason ResolveUnloadReason(string name) =>
        PluginUnloadReasonPolicy.Resolve(
            name,
            info: m => { try { Log.LogInfo(m); } catch { /* logging must never block the unload */ } },
            warn: m => { try { Log.LogWarning(m); } catch { } });
}
