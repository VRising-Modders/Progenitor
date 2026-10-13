using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime <c>Il2CppSystem.Action</c> trampolines for IL2CPP <em>Action fields</em>
/// (job NATIVE1). <c>GridSelectionEntry.OnLeftClicked</c> and its siblings are plain
/// <c>System.Action</c> fields on the native side, not <c>UnityEvent</c>s, so
/// <see cref="UiSlots.TryWire"/> cannot listen to them: the only thing that can sit in such a
/// field is a converted delegate, and a converted delegate whose method lives in collectible
/// Core is a process-lifetime pin of that generation (the same class of pin as
/// <c>DelegateSupport.ConvertDelegate</c> everywhere else in this shell). So the converted
/// delegate is Bootstrap's: each <see cref="ActionSlot"/> owns one <c>Il2CppSystem.Action</c>
/// over its own <c>Fire</c>, Core leases it by id, assigns it to the native field itself, and
/// the slot forwards to a replaceable Core dispatcher that is nulled on every Core unload path
/// like the other slots.
///
/// <para><b>Capability, not protocol.</b> Core reaches this type by name through
/// <c>BootstrapLink</c> and reads <see cref="Capability"/>; a Bootstrap that predates this type
/// reports <c>0</c> and Core keeps its imitation route. Old Bootstrap + new Core is the old
/// behavior, new Bootstrap + old Core never leases anything, so
/// <c>CoreLayout.ProtocolVersion</c> does not move. Deploying this file is a Bootstrap change:
/// the process must be restarted for it to exist.</para>
///
/// <para>Same shape as <see cref="UiSlots"/>: a pooled Bootstrap-owned slot per id, a Core
/// dispatcher kept under the current pin generation, <see cref="ClearAll"/> on Uninstall and
/// on the failed-install / NullRuntime sweeps, and pooled instances that stay alive for the
/// process because the native side may still hold the converted delegate.</para>
/// </summary>
internal static class UiActionSlot
{
    /// <summary>
    /// Read by Core through reflection. <c>1</c>: lease / release / dispatch of a
    /// parameterless Action-field trampoline. Bump when the lease contract changes shape.
    /// </summary>
    public const int Capability = 1;

    /// <summary>Core dispatcher. Null means Core is down; a stray click is ignored.</summary>
    internal delegate void Dispatch(ushort slotId);

    private const string PinName = "ui.action.dispatch";

    private static Dispatch? _core;
    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, ActionSlot> Live = new();
    private static readonly Stack<ActionSlot> Pool = new();

    /// <summary>
    /// Point the trampoline at Core's handler (or null it). The delegate is KeepDelegate'd
    /// under the current Core generation so Uninstall can drop it; the converted
    /// <c>Il2CppSystem.Action</c> bodies stay Bootstrap-owned.
    /// </summary>
    public static bool TrySetDispatch(Delegate? dispatcher)
    {
        try
        {
            Dispatch? bound = null;
            if (dispatcher != null)
            {
                bound = dispatcher is Dispatch typed
                    ? typed
                    : (Dispatch)Delegate.CreateDelegate(typeof(Dispatch), dispatcher.Target, dispatcher.Method);
#if !PROGENITOR_TESTS
                NativePinRegistry.KeepDelegate(bound, PinName, CoreLoader.CurrentPinGeneration);
#endif
            }

            Volatile.Write(ref _core, bound);
            return true;
        }
        catch (Exception e)
        {
            Warn("TrySetDispatch threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Null the Core dispatcher. Leased actions stay assigned until <see cref="Release"/> / <see cref="ClearAll"/>.</summary>
    public static void ClearDispatch() => TrySetDispatch(null);

    /// <summary>
    /// Lease a Bootstrap-owned <c>Il2CppSystem.Action</c> for <paramref name="slotId"/>. Core
    /// assigns the returned object to the native Action field itself (typed interop on its
    /// side) and must <see cref="Release"/> the id when it removes that assignment. A second
    /// lease of a live id returns the same action.
    /// </summary>
    public static bool TryLease(ushort slotId, out object? action)
    {
        action = null;
        if (slotId == 0) return false;
        try
        {
            ActionSlot? slot;
            lock (Gate)
            {
                if (!Live.TryGetValue(slotId, out slot))
                {
                    slot = Pool.Count > 0 ? Pool.Pop() : new ActionSlot();
                    slot.Id = slotId;
                    Live[slotId] = slot;
                }
            }

            action = slot.Action;
            return action != null;
        }
        catch (Exception e)
        {
            Warn("TryLease id=" + slotId + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Return the slot for <paramref name="slotId"/> to the pool; a later fire of it is ignored.</summary>
    public static void Release(ushort slotId)
    {
        if (slotId == 0) return;
        lock (Gate)
        {
            if (!Live.TryGetValue(slotId, out var slot)) return;
            Live.Remove(slotId);
            slot.Id = 0;
            Pool.Push(slot);
        }
    }

    /// <summary>
    /// Release every lease and null the Core dispatcher. Core Uninstall and the Bootstrap
    /// unload sweeps both call this; safe to call twice. Pooled slots stay alive for process
    /// lifetime — the native field a Core generation forgot to clear may still hold the
    /// converted delegate, and <see cref="Invoke"/> returns when <c>_core</c> is null or the
    /// id is not live. Do not clear the pool.
    /// </summary>
    public static void ClearAll()
    {
        ushort[] ids;
        lock (Gate)
        {
            ids = new ushort[Live.Count];
            Live.Keys.CopyTo(ids, 0);
        }

        for (int i = 0; i < ids.Length; i++)
            Release(ids[i]);
        Volatile.Write(ref _core, null);
    }

    public static string DescribePins()
    {
        int live;
        lock (Gate) live = Live.Count;
        bool dispatch = Volatile.Read(ref _core) != null;
        return "pin-diag UiActionSlot capability=" + Capability.ToString(CultureInfo.InvariantCulture)
            + " dispatch=" + (dispatch ? "True" : "False")
            + " leased=" + live.ToString(CultureInfo.InvariantCulture);
    }

    internal static int LiveCount
    {
        get { lock (Gate) return Live.Count; }
    }

    internal static bool DispatchIsSet => Volatile.Read(ref _core) != null;

    /// <summary>Trampoline body. Ignores a fire whose id is not leased (a stale native field after Release).</summary>
    internal static void Invoke(ushort id)
    {
        if (id == 0) return;
        lock (Gate)
        {
            if (!Live.ContainsKey(id)) return;
        }

        var core = Volatile.Read(ref _core);
        if (core == null) return;
        try { core(id); }
        catch (Exception e)
        {
            Warn("dispatch id=" + id + " threw: " + e.Message);
        }
    }

    private static void Warn(string message)
    {
#if PROGENITOR_TESTS
        _ = message;
#else
        ProcessHost.Log?.LogWarning("UiActionSlot: " + message);
#endif
    }

    private sealed class ActionSlot
    {
        public ushort Id;

#if PROGENITOR_TESTS
        /// <summary>Test stand-in for the converted delegate: a managed Action over <see cref="Fire"/>.</summary>
        public readonly System.Action Action;
        public ActionSlot() { Action = Fire; }
#else
        /// <summary>
        /// The converted delegate. Implicit conversion ConvertDelegate's this Bootstrap
        /// method once; the cache pins Bootstrap, which is process-lifetime anyway.
        /// Never build one of these over a Core method.
        /// </summary>
        public readonly Il2CppSystem.Action Action;
        public ActionSlot()
        {
            System.Action managed = Fire;
            Action = managed;
        }
#endif

        private void Fire() => Invoke(Id);
    }
}
