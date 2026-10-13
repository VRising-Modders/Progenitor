using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Patches;

/// <summary>
/// Which native sites a plugin may patch, and every log line the registry writes. Game-free on
/// purpose so the refusal rules and the grep lines are pinned by
/// <c>tests/Progenitor.Tests/PatchRegistryTests.cs</c> without a V Rising install.
///
/// <para>Two classes of target are refused outright:</para>
/// <list type="number">
/// <item><description><b>The shared empty-body thunk.</b> The method-address listing in the
/// game's metadata gives thousands of empty method bodies the same address, because the AOT
/// compiler deduplicates them: 48727 methods on the client (v1.1.13) and 49458 on the server.
/// Detouring one of them detours all of them. The project rule "do not patch
/// <c>GetConsumedInputs</c> on a system whose body is the shared empty stub" is
/// enforced here in code rather than in prose.</description></item>
/// <item><description><b>Burst jobs.</b> A <c>[BurstCompile]</c> job's managed
/// <c>Execute</c> is not the body that runs — the body lives in
/// <c>lib_burst_generated.dll</c> (verified on
/// <c>StatChangeSystem.ApplyChanges_Execute</c>). Patching the IL2CPP shell either does nothing
/// or corrupts a job scheduled onto worker threads.</description></item>
/// </list>
/// </summary>
public static class PatchSitePolicy
{
    /// <summary>
    /// Client GameAssembly.dll relative address of the deduplicated empty method body. Measured
    /// from the method-address listing in the client's metadata (v1.1.13): the single
    /// most-repeated method address, 48727 methods.
    /// </summary>
    public const long ClientEmptyStubImageOffset = 0x171E170;

    /// <summary>
    /// Server equivalent of <see cref="ClientEmptyStubImageOffset"/>, measured the same way from
    /// the server's metadata (v1.1.13): 49458 methods share it. The server offset is the
    /// client's plus <c>0x8000</c>, which is also the delta on the next four most-repeated
    /// bodies, so the two are the same thunk in two builds and not a coincidence of counts.
    /// </summary>
    public const long ServerEmptyStubImageOffset = 0x1726170;

    /// <summary>Refusal reason token: the resolved address is the shared empty-body thunk.</summary>
    public const string ReasonSharedEmptyStub = "shared-empty-stub";

    /// <summary>Refusal reason token: the target is (or is declared by) a Burst job.</summary>
    public const string ReasonBurstJob = "burst-job";

    /// <summary>Refusal reason token: no target or no patch method was supplied.</summary>
    public const string ReasonNoTarget = "no-target";

    /// <summary>
    /// Refusal reason token: the target takes a value type <b>by reference</b>
    /// (<c>ref</c>, <c>in</c> or <c>out</c> on a struct). See
    /// <see cref="FindByRefValueTypeParameter"/> for why that is refused and not patched.
    /// </summary>
    public const string ReasonByRefValueType = "byref-struct";

    /// <summary>
    /// The first parameter of <paramref name="target"/> that passes a value type by reference,
    /// as <c>"ref Health health"</c>, or null when there is none (or no target).
    ///
    /// <para><b>Why a by-ref struct is a refusal.</b> The game's Harmony support is
    /// Il2CppInterop <c>1.4.6-dev</c> (BepInEx 6 bleeding-edge
    /// <c>3a12d1810bf4dd5ca1635352ccf5bff5d95f987f</c>), whose
    /// <c>Il2CppDetourMethodPatcher.EmitConvertArgumentToManaged</c> does not handle a by-ref
    /// value type: the native-to-managed trampoline does <c>Ldind_I</c> on the caller's pointer
    /// (8 bytes, not the struct) into a local of the struct type, hands the patched method a
    /// reference to that local, and after the call writes the local back with <c>Stind_I</c> —
    /// 8 bytes of whatever the JIT made of it, written over the <b>first field</b> of the
    /// caller's struct (upstream's own comment: "directType being ValueType is not handled yet";
    /// fixed upstream only in 2026-05, commit <c>97aa893287</c>, after the 1.5.0 tag). The
    /// original also runs against that managed local, so its writes never reach the caller.
    /// A prefix that "blocks" changes nothing about this: the trampoline runs on every call
    /// while the patch is installed, guarded entity or not.</para>
    ///
    /// <para>This is how god mode corrupted <c>Health.MaxHealth</c> (the field at <c>0x0</c>)
    /// to <c>4.03e20</c> on the 2026-10-07 facade smoke: the 11-arg
    /// <c>StatChangeUtility.KillEntity(..., ref Health, in HealthConstants, ...)</c> was
    /// Harmony-patched. Plugins hit the same trap, so the registry refuses every such target
    /// rather than leaving it to each caller to know. A struct wrapped as an interop class
    /// (<c>Il2CppSystem.ValueType</c> subclass) is detected by its base-type name so this file
    /// stays game-free.</para>
    /// </summary>
    public static string? FindByRefValueTypeParameter(System.Reflection.MethodBase? target)
    {
        if (target == null) return null;
        System.Reflection.ParameterInfo[] pars;
        try { pars = target.GetParameters(); }
        catch { return null; }
        foreach (var p in pars)
        {
            var type = p.ParameterType;
            if (type == null || !type.IsByRef) continue;
            var element = type.GetElementType();
            if (element == null || !IsValueTypeLike(element)) continue;
            string mode = p.IsOut ? "out" : p.IsIn ? "in" : "ref";
            return mode + " " + element.Name + " " + (p.Name ?? "?");
        }
        return null;
    }

    private static bool IsValueTypeLike(Type type)
    {
        if (type.IsValueType) return true;
        for (var t = type.BaseType; t != null; t = t.BaseType)
        {
            if (string.Equals(t.FullName, "Il2CppSystem.ValueType", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Refusal reason token: the registry is not installed (no applier attached).</summary>
    public const string ReasonNotInstalled = "not-installed";

    /// <summary>
    /// Refusal reason token (job FHARM5): Bootstrap's patch-slot pool is full. A plugin patch
    /// body must live in Bootstrap — a Harmony patch pins the ALC its patch method comes from
    /// even after <c>Unpatch</c> — and the pool is a fixed number of process-lifetime static
    /// methods, so there is no way to grow it at run time. See
    /// <c>Progenitor.Bootstrap.PatchSlots</c>.
    /// </summary>
    public const string ReasonSlotsExhausted = "slots-exhausted";

    /// <summary>
    /// Refusal reason token (job FHARM5): Bootstrap's patch-slot pool did not resolve — an old
    /// Bootstrap, or a host that is not Progenitor's. Refused rather than fallen back to a
    /// direct patch, because the fallback is exactly the permanent ALC pin.
    /// </summary>
    public const string ReasonSlotsUnavailable = "slots-unavailable";

    /// <summary>
    /// Refusal reason token (job FHARM7, R41 finding 2): the pool still has free slots, but they
    /// are the ones reserved for Core's own lazily-armed patches (the world-load epoch postfix,
    /// god mode's kill group, the event fallbacks). Distinct from
    /// <see cref="ReasonSlotsExhausted"/> because the fix is different: unload a plugin, or raise
    /// <c>PatchSlots.Capacity</c>, rather than "the pool is full".
    /// </summary>
    public const string ReasonSlotsReserved = "slots-reserved";

    /// <summary>
    /// Refusal reason token (job FHARM8, R42 finding 2): the plugin budget is gone because slots
    /// were <b>quarantined</b> — permanently stranded by an unpatch that threw — rather than
    /// because other plugins are holding them. Distinct from <see cref="ReasonSlotsReserved"/>
    /// and <see cref="ReasonSlotsExhausted"/> because those two send the operator to "unload a
    /// plugin, or raise the capacity", and neither frees a quarantined slot: attrition is one-way
    /// for the life of the process, so the only remedy is a restart. Before this existed, a
    /// server starved purely by one plugin's failing unpatches told the operator to unload an
    /// innocent plugin, which freed nothing.
    /// </summary>
    public const string ReasonSlotsLostToQuarantine = "slots-lost-to-quarantine";

    /// <summary>
    /// The remedy sentence for a slot refusal, appended by the registry to
    /// <see cref="FormatRefused"/> (job FHARM8, R42 finding 2). The token names what happened;
    /// this names what the operator can do about it, which is different for each of the three
    /// budget refusals and was previously only in a doc comment.
    /// </summary>
    public static string SlotRefusalRemedy(string? reason) => reason switch
    {
        ReasonSlotsLostToQuarantine =>
            " Slots were permanently stranded by unpatches that threw; unloading a plugin will "
            + "NOT free them. Restart the process (see the patch-slots quarantine warnings above).",
        ReasonSlotsReserved =>
            " The pool still has free slots, but they are the ones reserved for Core's own "
            + "lazily-armed patches. Unload a plugin, or raise PatchSlots.CoreReserve/Capacity "
            + "(a Bootstrap change: restart both).",
        ReasonSlotsExhausted =>
            " Every slot is held. Unload a plugin, or raise PatchSlots.Capacity (a Bootstrap "
            + "change: restart both).",
        ReasonSlotsUnavailable =>
            " Progenitor.Bootstrap's slot pool did not resolve (an old Bootstrap, or a host that "
            + "is not Progenitor's). Refused rather than patched directly, because a direct patch "
            + "would pin this plugin's ALC for the life of the process. Update Bootstrap and "
            + "restart both.",
        _ => "",
    };

    /// <summary>
    /// Refusal reason token (job FHARM5): the patch method is not the slot shape. A slot-routed
    /// patch is <c>static bool F(object instance, object[] args)</c> (prefix),
    /// <c>static void F(object, object[])</c> (postfix) or
    /// <c>static Exception F(object, object[], Exception)</c> (finalizer).
    /// </summary>
    public const string ReasonSlotSignature = "slot-signature";

    /// <summary>
    /// Suffix on the owner id every <b>Core-owned</b> registry patch carries:
    /// <c>progenitor.core</c> (job FHARM6, R40 finding 1). One id for all of them,
    /// because they share one lifetime (this Core generation) and one sweep. Spelled as a suffix
    /// here, rather than as the whole id, so this file stays game-free and never needs
    /// <c>MyPluginInfo</c>.
    /// </summary>
    public const string CoreOwnerSuffix = ".core";

    /// <summary><see cref="PatchSiteInfo.ImageOffset"/> value meaning "the address could not be resolved".</summary>
    public const long UnknownImageOffset = -1;

    /// <summary><see cref="PatchSiteInfo.Path"/> value when nothing resolved the native address.</summary>
    public const string PathUnknown = "unknown";

    /// <summary>
    /// True when <paramref name="imageOffset"/> is the shared empty-body thunk on either side. Both
    /// sides are checked regardless of the running process so a client-side table accidentally
    /// loaded in a server build still refuses.
    /// </summary>
    public static bool IsSharedEmptyStub(long imageOffset) =>
        imageOffset == ClientEmptyStubImageOffset || imageOffset == ServerEmptyStubImageOffset;

    /// <summary>
    /// True when the target looks like a Burst job: any attribute on the method or its declaring
    /// type whose name contains <c>BurstCompile</c>, or a declaring type that implements one of
    /// Unity's <c>IJob*</c> interfaces. Name-based because the test assembly cannot reference
    /// Unity; <see cref="Progenitor.Patches.PatchSites"/> feeds it the live reflection names.
    /// </summary>
    public static bool IsBurstJob(
        IReadOnlyList<string>? attributeNames,
        IReadOnlyList<string>? declaringTypeInterfaceNames)
    {
        if (attributeNames != null)
        {
            for (int i = 0; i < attributeNames.Count; i++)
            {
                var name = attributeNames[i];
                if (!string.IsNullOrEmpty(name)
                    && name.IndexOf("BurstCompile", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }

        if (declaringTypeInterfaceNames != null)
        {
            for (int i = 0; i < declaringTypeInterfaceNames.Count; i++)
            {
                var name = declaringTypeInterfaceNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (name.Equals("IJob", StringComparison.Ordinal)
                    || name.StartsWith("IJob", StringComparison.Ordinal)
                    || name.StartsWith("IJobEntity", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The whole gate in one pure call. Returns false and fills <paramref name="reason"/> when
    /// the site must not be patched. An unresolved address (<see cref="UnknownImageOffset"/>) is allowed:
    /// the address library has no table entry for most methods, and refusing everything it
    /// cannot see would make the registry useless. The stub check only ever fires on an address we
    /// actually read.
    /// </summary>
    public static bool MayPatch(in PatchSiteInfo site, out string reason)
    {
        if (IsSharedEmptyStub(site.ImageOffset))
        {
            reason = ReasonSharedEmptyStub;
            return false;
        }
        if (site.IsBurstJob)
        {
            reason = ReasonBurstJob;
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Lower-case token for a <see cref="PatchKind"/>, used in every log line.</summary>
    public static string KindToken(PatchKind kind) => kind switch
    {
        PatchKind.Prefix => "prefix",
        PatchKind.Postfix => "postfix",
        PatchKind.Finalizer => "finalizer",
        _ => "patch",
    };

    /// <summary>The relative address in hex (<c>0x…</c>), or <c>?</c> for <see cref="UnknownImageOffset"/>.</summary>
    public static string FormatImageOffset(long imageOffset) =>
        imageOffset == UnknownImageOffset ? "?" : "0x" + imageOffset.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>
    /// One line per applied patch. Grep <c>patches: prefix</c>.
    /// <c>patches: prefix ProjectM.DeathUtilities::Kill owner=progenitor path=table offset=0x... priority=400 id=1</c>
    /// </summary>
    public static string FormatApplied(
        PatchKind kind, string target, string owner, string path, long imageOffset, int priority, long id) =>
        "patches: " + KindToken(kind) + " " + Safe(target)
        + " owner=" + Safe(owner)
        + " path=" + (string.IsNullOrEmpty(path) ? PathUnknown : path)
        + " offset=" + FormatImageOffset(imageOffset)
        + " priority=" + priority.ToString(CultureInfo.InvariantCulture)
        + " id=" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One line per unpatch. Grep <c>patches: unpatch</c>.
    /// <c>patches: unpatch prefix ProjectM.DeathUtilities::Kill owner=progenitor id=1</c>
    /// </summary>
    public static string FormatUnpatched(PatchKind kind, string target, string owner, long id) =>
        "patches: unpatch " + KindToken(kind) + " " + Safe(target)
        + " owner=" + Safe(owner)
        + " id=" + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A refused target. <c>patches: refused prefix ProjectM.GameplayInputSystem::GetConsumedInputs
    /// owner=x reason=shared-empty-stub offset=0x...</c>
    /// </summary>
    public static string FormatRefused(PatchKind kind, string target, string owner, string reason, long imageOffset) =>
        "patches: refused " + KindToken(kind) + " " + Safe(target)
        + " owner=" + Safe(owner)
        + " reason=" + reason
        + " offset=" + FormatImageOffset(imageOffset);

    /// <summary>
    /// Fail-closed line: the applier threw, so <b>the registry</b> holds no entry for it.
    /// <c>patches: apply failed prefix X owner=y: boom (not registered)</c>
    ///
    /// <para>"Not registered" is deliberately narrow. It says nothing about what HarmonyX
    /// recorded before it threw, which the registry has no token for — see
    /// <see cref="FormatApplySwept"/> and <see cref="FormatApplySweepFailed"/>, the two lines
    /// that report the best-effort undo.</para>
    /// </summary>
    public static string FormatApplyFailed(PatchKind kind, string target, string owner, string error) =>
        "patches: apply failed " + KindToken(kind) + " " + Safe(target)
        + " owner=" + Safe(owner) + ": " + (error ?? "") + " (not registered)";

    /// <summary>
    /// The best-effort unpatch after a throwing apply came back without complaint.
    /// <c>patches: apply swept prefix X owner=y (best-effort unpatch of a failed apply)</c>
    /// </summary>
    public static string FormatApplySwept(PatchKind kind, string target, string owner) =>
        "patches: apply swept " + KindToken(kind) + " " + Safe(target)
        + " owner=" + Safe(owner) + " (best-effort unpatch of a failed apply)";

    /// <summary>
    /// The best-effort unpatch after a throwing apply <b>also</b> failed. This is the one state
    /// the registry cannot clean up: if HarmonyX kept anything for that original, the collectible
    /// ALC is pinned by a patch no entry, handle or sweep will ever find, and
    /// <c>patches: Uninstall removed=0 left=0</c> will still read clean.
    /// <c>patches: apply sweep FAILED prefix X owner=y: boom (a HarmonyX-side remnant may pin the collectible ALC)</c>
    /// </summary>
    public static string FormatApplySweepFailed(PatchKind kind, string target, string owner, string error) =>
        "patches: apply sweep FAILED " + KindToken(kind) + " " + Safe(target)
        + " owner=" + Safe(owner) + ": " + (error ?? "")
        + " (a HarmonyX-side remnant may pin the collectible ALC)";

    /// <summary>
    /// An atomic group registration that did not complete: the named member was refused or threw,
    /// the members that had already landed were unpatched, and the registry holds nothing for the
    /// group.
    /// <c>patches: group refused owner=progenitor.godmode member=godmode.killentity rolled-back=1 of=2 (nothing registered)</c>
    /// </summary>
    public static string FormatGroupRefused(string owner, string member, int rolledBack, int total) =>
        "patches: group refused owner=" + Safe(owner)
        + " member=" + Safe(member)
        + " rolled-back=" + rolledBack.ToString(CultureInfo.InvariantCulture)
        + " of=" + total.ToString(CultureInfo.InvariantCulture)
        + " (nothing registered)";

    /// <summary><c>patches: RemoveAll owner=progenitor.sample removed=2 left=0</c></summary>
    public static string FormatRemoveAll(string owner, int removed, int left) =>
        "patches: RemoveAll owner=" + Safe(owner)
        + " removed=" + removed.ToString(CultureInfo.InvariantCulture)
        + " left=" + left.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Core Uninstall's line. Every remaining patch is Core-generation-owned and must go before
    /// the collectible ALC unloads. <c>patches: Uninstall removed=2 left=0</c>
    /// </summary>
    public static string FormatUninstall(int removed, int left) =>
        "patches: Uninstall removed=" + removed.ToString(CultureInfo.InvariantCulture)
        + " left=" + left.ToString(CultureInfo.InvariantCulture);

    /// <summary>Pin line for the Core boot log / <c>ping</c>. <c>patches: live=2 owners=1</c></summary>
    /// <summary>
    /// The line a failed unpatch on a slot-routed patch logs after the slot is quarantined
    /// (job FHARM6, R40 finding 2): <c>patches: slot quarantined index=3 owner=x id=7 ...</c>.
    /// </summary>
    public static string FormatSlotQuarantined(int index, string owner, long id) =>
        "patches: slot quarantined index=" + index + " owner=" + owner + " id=" + id
        + " (the unpatch threw, so the detour may survive; this slot is out of service for the "
        + "life of the process rather than rebound under another owner)";

    public static string FormatPins(int live, int owners) =>
        "patches: live=" + live.ToString(CultureInfo.InvariantCulture)
        + " owners=" + owners.ToString(CultureInfo.InvariantCulture);

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "?" : s!;
}

/// <summary>
/// What <see cref="Progenitor.Patches.PatchRegistry"/> knows about a target before it patches it:
/// which resolver produced the native address (<c>table</c> / <c>fallback</c> / <c>missing</c>,
/// the <see cref="Progenitor.Native.AddressId"/> tokens), the relative address it landed on, and whether the
/// method is a Burst job. Filled by <c>PatchSites.Describe</c> in the game build and by the tests
/// directly in the fakes.
/// </summary>
public readonly struct PatchSiteInfo
{
    /// <summary>Display name of the target: <c>ProjectM.DeathUtilities::Kill</c>.</summary>
    public string Target { get; }

    /// <summary>Resolver that produced <see cref="ImageOffset"/>: <c>table</c>, <c>fallback</c>, <c>missing</c> or <c>unknown</c>.</summary>
    public string Path { get; }

    /// <summary>Relative address in GameAssembly.dll, or <see cref="PatchSitePolicy.UnknownImageOffset"/>.</summary>
    public long ImageOffset { get; }

    /// <summary>True when the target is a Burst job shell (see <see cref="PatchSitePolicy.IsBurstJob"/>).</summary>
    public bool IsBurstJob { get; }

    public PatchSiteInfo(string target, string path, long imageOffset, bool isBurstJob)
    {
        Target = target ?? "";
        Path = string.IsNullOrEmpty(path) ? PatchSitePolicy.PathUnknown : path;
        ImageOffset = imageOffset;
        IsBurstJob = isBurstJob;
    }

    /// <summary>A site nothing could be established about. Allowed to patch; logged as <c>path=unknown offset=?</c>.</summary>
    public static PatchSiteInfo Unknown(string target) =>
        new(target, PatchSitePolicy.PathUnknown, PatchSitePolicy.UnknownImageOffset, isBurstJob: false);
}
