using System;
using System.Reflection;

namespace Progenitor.Patches;

/// <summary>
/// A prefix routed through a Bootstrap patch slot. Returning <c>false</c> skips the original.
/// <paramref name="instance"/> is Harmony's <c>__instance</c> (null for a static target) and
/// <paramref name="args"/> is <c>__args</c> — by-ref parameters are written back through it.
/// There is deliberately no <c>__result</c>: see <see cref="PatchSlotLink"/>.
/// </summary>
public delegate bool SlotPrefix(object? instance, object?[] args);

/// <summary>A postfix routed through a Bootstrap patch slot. See <see cref="SlotPrefix"/>.</summary>
public delegate void SlotPostfix(object? instance, object?[] args);

/// <summary>
/// A finalizer routed through a Bootstrap patch slot: return <paramref name="exception"/> to let
/// it propagate, null to swallow it. See <see cref="SlotPrefix"/>.
/// </summary>
public delegate Exception? SlotFinalizer(object? instance, object?[] args, Exception? exception);

/// <summary>
/// Job FHARM5. The Core half of <c>Progenitor.Bootstrap.PatchSlots</c>: it borrows a
/// process-lifetime patch body from Bootstrap's fixed pool and points it at a collectible
/// delegate, so that the <c>MethodInfo</c> HarmonyX is handed always belongs to
/// <c>Progenitor.Bootstrap</c> and never to a plugin (or to this Core generation).
///
/// <para><b>The measurement this exists for.</b> A HarmonyX patch applied from a collectible ALC
/// and then <em>unpatched</em> still keeps that ALC alive. Measured on .NET 6 (the runtime the
/// game runs) with HarmonyX 2.10.2, on a plain managed target, against a control that unloads
/// cleanly. The root is
/// <c>HarmonyLib.Internal.RuntimeFixes.StackTraceFixes.RealMethodMap</c>, a static
/// <c>Dictionary&lt;MethodBase, MethodBase&gt;</c> written on every detour apply and never
/// cleared; for IL2CPP targets
/// <c>Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.DelegateCache</c> pins it a second
/// time. <c>PatchRegistry.RemoveAll</c> stops the call — which is what keeps a dead ALC's code
/// from running — but it is <b>not</b> what releases the ALC. This link is.</para>
///
/// <para><b>What a slot cannot do.</b> A pool body has a fixed, signature-agnostic shape
/// (<c>__instance</c> and <c>__args</c>), so a slot-routed patch cannot read or write
/// <c>__result</c>. That is a real loss and it is why this is not a silent rewrite of every
/// patch: a patch that needs the return value has to use a Bootstrap-owned typed site
/// (<c>HarmonySlotHandoff</c>) instead.</para>
///
/// <para><b>Resolution.</b> Core cannot reference Bootstrap (Bootstrap loads Core), so the pool
/// is reached reflectively, the same way <c>BootstrapLink</c> reaches the typed slots: by
/// assembly simple name, with a same-assembly fallback so the game-free tests can link
/// <c>PatchSlots.cs</c> directly. When Bootstrap is not present — an old Bootstrap, or a unit
/// test that did not link the pool — <see cref="Available"/> is false and the registry refuses
/// the patch with <c>slots-unavailable</c> rather than silently falling back to the pinning
/// path.</para>
/// </summary>
internal static class PatchSlotLink
{
    private const string AssemblySimpleName = "Progenitor.Bootstrap";
    private const string SlotsTypeName = "Progenitor.Bootstrap.PatchSlots";

    private static readonly object Gate = new();
    private static bool _resolved;
    private static Type? _slots;
    private static Type? _bodyType;
    private static MethodInfo? _bind;
    private static MethodInfo? _release;
    private static MethodInfo? _quarantine;
    private static MethodInfo? _releaseOwner;
    private static MethodInfo? _releaseAll;
    private static MethodInfo? _patchMethod;
    private static MethodInfo? _describe;
    private static MethodInfo? _reservedForCore;
    private static MethodInfo? _lostToQuarantine;
    private static MethodInfo? _takeWarning;
    private static PropertyInfo? _free;
    private static FieldInfo? _capacity;

    /// <summary>Sentinel for "the pool had nothing left" / "the pool is not there".</summary>
    internal const long NoSlot = -1;

    /// <summary>
    /// The index half of a slot token, for a log line. Mirrors
    /// <c>PatchSlots.TokenIndexBits</c>; kept local so a log line never needs the pool to have
    /// resolved.
    /// </summary>
    internal static int IndexOf(long token) => token < 0 ? -1 : (int)(token & 0xFF);

    /// <summary>True when Bootstrap's pool resolved and the registry may route through it.</summary>
    internal static bool Available
    {
        get { Resolve(); return _bind != null && _patchMethod != null && _bodyType != null; }
    }

    /// <summary>Pool size, or 0 when unavailable.</summary>
    internal static int Capacity
    {
        get
        {
            Resolve();
            try { return _capacity == null ? 0 : (int)(_capacity.GetRawConstantValue() ?? 0); }
            catch { return 0; }
        }
    }

    /// <summary>Slots left, or 0 when unavailable.</summary>
    internal static int Free
    {
        get
        {
            Resolve();
            try { return _free == null ? 0 : (int)(_free.GetValue(null) ?? 0); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Job FHARM7 (R41 finding 2): true when this owner was refused by the pool's Core
    /// reservation rather than by real exhaustion, so the refusal line can say which. False
    /// against an old Bootstrap with no reservation.
    /// </summary>
    internal static bool ReservedForCore(string? ownerId)
    {
        Resolve();
        if (_reservedForCore == null) return false;
        try { return (bool)(_reservedForCore.Invoke(null, new object?[] { ownerId ?? "" }) ?? false); }
        catch { return false; }
    }

    /// <summary>
    /// Job FHARM8 (R42 finding 2): true when the plugin budget is gone because slots were
    /// <em>quarantined</em> rather than because other plugins are holding them. A different
    /// refusal reason, because the remedy is a restart and not "unload a plugin". False against
    /// an old Bootstrap that has no such query.
    /// </summary>
    internal static bool LostToQuarantine(string? ownerId)
    {
        Resolve();
        if (_lostToQuarantine == null) return false;
        try { return (bool)(_lostToQuarantine.Invoke(null, new object?[] { ownerId ?? "" }) ?? false); }
        catch { return false; }
    }

    /// <summary>
    /// Drain the pool's one pending warning (low free slots, quarantine attrition), or null.
    /// A string and not a callback: a delegate handed to the non-collectible Bootstrap would pin
    /// this Core generation, which is the leak this whole file exists to prevent.
    /// </summary>
    internal static string? TakeWarning()
    {
        Resolve();
        if (_takeWarning == null) return null;
        try { return _takeWarning.Invoke(null, null) as string; }
        catch { return null; }
    }

    /// <summary><c>patch-slots: in-use=…</c>, or "" when the pool is not there.</summary>
    internal static string Describe()
    {
        Resolve();
        try { return (_describe?.Invoke(null, null) as string) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Borrow a slot for <paramref name="body"/> and hand back the process-lifetime
    /// <see cref="MethodInfo"/> to give Harmony. Returns <see cref="NoSlot"/> (and a null
    /// method) when the pool is unavailable or exhausted; the caller turns that into the
    /// refusal line.
    /// </summary>
    /// <param name="kind">Prefix, postfix or finalizer.</param>
    /// <param name="body">The collectible behavior, already adapted to the slot shape.</param>
    /// <param name="ownerId">Plugin id; <see cref="ReleaseOwner"/> sweeps by it.</param>
    /// <param name="instanceTarget">False for a static original (no <c>__instance</c>).</param>
    /// <param name="patchMethod">The Bootstrap static to patch with.</param>
    internal static long Bind(PatchKind kind, Delegate body, string ownerId, bool instanceTarget, out MethodInfo? patchMethod)
    {
        patchMethod = null;
        Resolve();
        if (_bind == null || _patchMethod == null || _bodyType == null || body == null) return NoSlot;

        Delegate bootstrapBody;
        try
        {
            // Delegate.CreateDelegate binds by signature, which is how a collectible assembly
            // hands Bootstrap a delegate of a type it cannot name at compile time.
            bootstrapBody = Delegate.CreateDelegate(_bodyType, body.Target, body.Method);
        }
        catch
        {
            return NoSlot;
        }

        long token;
        try { token = Convert.ToInt64(_bind.Invoke(null, new object?[] { (int)kind, bootstrapBody, ownerId ?? "" }) ?? NoSlot); }
        catch { return NoSlot; }
        if (token < 0) return NoSlot;

        try { patchMethod = _patchMethod.Invoke(null, new object?[] { token, (int)kind, instanceTarget }) as MethodInfo; }
        catch { patchMethod = null; }

        if (patchMethod != null) return token;

        Release(token, ownerId);
        return NoSlot;
    }

    /// <summary>Give one slot back. The Harmony unpatch is the caller's job; this is the half
    /// that actually frees the collectible ALC. <paramref name="token"/> is what
    /// <see cref="Bind"/> returned: a stale token (the slot has since been released and rebound)
    /// releases nothing.</summary>
    internal static bool Release(long token, string? ownerId)
    {
        Resolve();
        if (_release == null || token < 0) return false;
        try { return (bool)(_release.Invoke(null, new object?[] { token, ownerId ?? "" }) ?? false); }
        catch { return false; }
    }

    /// <summary>
    /// Strand one slot permanently (R40 finding 2). Used when the Harmony unpatch on the slot's
    /// static method threw: the detour may still be live, so the index must never be handed to
    /// another owner. Costs one of the 32 for the life of the process; the loss is in
    /// <see cref="Describe"/>. False when the token no longer addresses a live slot, or when the
    /// pool is an old Bootstrap with no <c>Quarantine</c> - in which case the caller falls back
    /// to <see cref="Release"/>, which is the pre-FHARM6 behavior.
    /// </summary>
    internal static bool Quarantine(long token, string? ownerId)
    {
        Resolve();
        if (_quarantine == null || token < 0) return false;
        try { return (bool)(_quarantine.Invoke(null, new object?[] { token, ownerId ?? "" }) ?? false); }
        catch { return false; }
    }

    /// <summary>Give back every slot held by one plugin.</summary>
    internal static int ReleaseOwner(string? ownerId)
    {
        Resolve();
        if (_releaseOwner == null || string.IsNullOrEmpty(ownerId)) return 0;
        try { return (int)(_releaseOwner.Invoke(null, new object?[] { ownerId }) ?? 0); }
        catch { return 0; }
    }

    /// <summary>Give back every slot. Core uninstall's backstop.</summary>
    internal static int ReleaseAll()
    {
        Resolve();
        if (_releaseAll == null) return 0;
        try { return (int)(_releaseAll.Invoke(null, null) ?? 0); }
        catch { return 0; }
    }

    /// <summary>Tests only: forget the resolved pool so a later test can re-resolve.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _resolved = false;
    }

    /// <summary>
    /// Adapt a plugin-supplied slot delegate to the one shape Bootstrap's pool understands.
    /// The returned delegate's target is this adapter — a Core object — which Bootstrap holds
    /// only for as long as the slot is bound.
    /// </summary>
    internal static Delegate? Adapt(PatchKind kind, Delegate? handler)
    {
        if (handler == null) return null;
        switch (kind)
        {
            case PatchKind.Prefix when handler is SlotPrefix p:
                return new Func<object?, object?[], Exception?, object?>(
                    (instance, args, _) => p(instance, args));
            case PatchKind.Postfix when handler is SlotPostfix s:
                return new Func<object?, object?[], Exception?, object?>(
                    (instance, args, _) => { s(instance, args); return null; });
            case PatchKind.Finalizer when handler is SlotFinalizer f:
                return new Func<object?, object?[], Exception?, object?>(f);
            default:
                return null;
        }
    }

    /// <summary>
    /// Bind a plugin's <see cref="MethodInfo"/> to the slot delegate for
    /// <paramref name="kind"/>. Null when the signature is not the slot shape — the registry
    /// turns that into <c>reason=slot-signature</c> rather than patching something that would
    /// pin. The method must be static, for the same reason Harmony patch methods are.
    /// </summary>
    internal static Delegate? FromMethod(PatchKind kind, MethodInfo? patch)
    {
        if (patch == null || !patch.IsStatic) return null;
        Type shape = kind switch
        {
            PatchKind.Prefix => typeof(SlotPrefix),
            PatchKind.Postfix => typeof(SlotPostfix),
            PatchKind.Finalizer => typeof(SlotFinalizer),
            _ => typeof(void),
        };
        if (shape == typeof(void)) return null;
        try { return Delegate.CreateDelegate(shape, patch, throwOnBindFailure: false); }
        catch { return null; }
    }

    private static void Resolve()
    {
        lock (Gate)
        {
            if (_resolved) return;
            _resolved = true;
            _slots = FindSlotsType();
            if (_slots == null) return;

            const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;
            _bodyType = _slots.GetNestedType("Body", BindingFlags.Public);
            _bind = _slots.GetMethod("Bind", Pub);
            _release = _slots.GetMethod("Release", Pub);
            _quarantine = _slots.GetMethod("Quarantine", Pub);
            _releaseOwner = _slots.GetMethod("ReleaseOwner", Pub);
            _releaseAll = _slots.GetMethod("ReleaseAll", Pub);
            _patchMethod = _slots.GetMethod("PatchMethod", Pub);
            _describe = _slots.GetMethod("Describe", Pub);
            _reservedForCore = _slots.GetMethod("ReservedForCore", Pub);
            _lostToQuarantine = _slots.GetMethod("LostToQuarantine", Pub);
            _takeWarning = _slots.GetMethod("TakeWarning", Pub);
            _free = _slots.GetProperty("Free", Pub);
            _capacity = _slots.GetField("Capacity", Pub);
        }
    }

    private static Type? FindSlotsType()
    {
        // Same-assembly first: the game-free tests link PatchSlots.cs next to this file.
        try
        {
            var local = typeof(PatchSlotLink).Assembly.GetType(SlotsTypeName);
            if (local != null) return local;
        }
        catch { /* keep looking */ }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, AssemblySimpleName, StringComparison.Ordinal)) continue;
            try
            {
                var t = asm.GetType(SlotsTypeName);
                if (t != null) return t;
            }
            catch { /* next */ }
        }
        return null;
    }
}
