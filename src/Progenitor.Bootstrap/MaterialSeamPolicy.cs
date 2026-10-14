using System;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free facts and rules behind <see cref="MaterialSeams"/>, linked into the test project.
///
/// <para>The two seams are the castle-heart overloads of
/// <c>ProjectM.Shared.MergedInventoriesUtility.GetCastleMergedInventoryDatas</c> (availability; both
/// sides) and <c>RemoveItemGetRemainder</c> with the returned item entity (payment; server). The
/// game's metadata shows every other caller funnels into these two: the territory overloads resolve
/// the heart from the target's position and call them directly; the build check, the build charge,
/// the craft start check and the craft start payment, and the client cost display call them or the
/// territory overloads.</para>
///
/// <para>Native shapes, from the IL2CPP signatures in the game's metadata and the Windows x64 calling
/// convention (the only one on this platform): a struct wider than 8 bytes is passed through a hidden
/// pointer to a caller-owned copy (<c>EntityManager</c>, 16 bytes); an 8-byte <c>Entity</c> and a 4-byte
/// <c>PrefabGUID</c> travel by value in an integer register; a 16-byte <c>NativeArray</c> return is
/// written through a hidden first-argument pointer whose value is also returned; <c>bool</c> is one
/// byte; every IL2CPP method takes a trailing <c>MethodInfo*</c>. The listing of each body agrees
/// (it reads <c>entityManager-&gt;fields</c> through the pointer and writes
/// <c>__return_storage_ptr__</c>). These facts are verified offline; that a detour over them runs
/// cleanly is not, which is why the seams are opt-in.</para>
/// </summary>
public static class MaterialSeamPolicy
{
    /// <summary>BepInEx config section for the opt-in.</summary>
    public const string ConfigSection = "Materials";

    /// <summary>Config key: true arms the native seams at the next process start. Default false.</summary>
    public const string ConfigKey = "NativeSeams";

    /// <summary>Interop field naming the heart overload of the merged count (both sides).</summary>
    public const string CountField =
        "NativeMethodInfoPtr_GetCastleMergedInventoryDatas_Public_Static_NativeArray_1_InventoryBuffer_EntityManager_Entity_Entity_Boolean_Boolean_0";

    /// <summary>Interop field naming the heart overload of the merged removal that returns the item entity (server).</summary>
    public const string RemoveField =
        "NativeMethodInfoPtr_RemoveItemGetRemainder_Public_Static_Void_EntityManager_Entity_Entity_PrefabGUID_Int32_byref_Int32_byref_Entity_Boolean_Boolean_Boolean_0";

    /// <summary>
    /// <c>InventoryBuffer</c> element stride in the returned array (server metadata: <c>ItemEntity</c>
    /// at 0x0, <c>ItemType</c> 0xC, <c>Amount</c> 0x10, <c>MaxAmountOverride</c> 0x14). Core's
    /// <c>Layout.InventoryBufferStride</c> must agree; a test pins the two.
    /// </summary>
    public const int InventoryBufferStride = 24;

    /// <summary>
    /// <c>NativeArray</c> allocator label the game's merged count returns: <c>Allocator.None</c> (1), a
    /// view over a temporary list that no caller frees. A replacement array carries the same label, so no
    /// caller ever tries to free Bootstrap's buffer. Any other label refuses the replacement.
    /// </summary>
    public const int ExpectedAllocatorLabel = 1;

    /// <summary>How many replacement buffers rotate. A caller uses the array before its next count call.</summary>
    public const int ArenaSlots = 16;

    /// <summary>
    /// Rows to reserve for a composed array: everything the game returned plus room for the pool rows.
    /// The compose call reports a larger need when this is not enough, and the arena grows.
    /// </summary>
    public static int InitialCapacity(int fullLength, int bagLength) =>
        Math.Max(16, checked(fullLength + bagLength + 64));

    /// <summary>
    /// What the count seam does with the compose answer: -1 keeps the game's own array; a value within
    /// capacity is used; a larger value asks for a bigger buffer (at most once).
    /// </summary>
    public static CountDecision DecideCount(int composed, int capacity, bool retried)
    {
        if (composed < 0) return CountDecision.KeepVanilla;
        if (composed <= capacity) return CountDecision.UseComposed;
        return retried ? CountDecision.KeepVanilla : CountDecision.GrowAndRetry;
    }

    /// <summary>Result of <see cref="DecideCount"/>.</summary>
    public enum CountDecision
    {
        /// <summary>Return the game's own array untouched.</summary>
        KeepVanilla,

        /// <summary>Point the returned array at the composed rows.</summary>
        UseComposed,

        /// <summary>Grow the buffer and ask again.</summary>
        GrowAndRetry,
    }

    /// <summary>The <c>materials:</c> line Bootstrap logs at start.</summary>
    public static string FormatState(bool enabled, bool server, string count, string pay) =>
        "materials: native seams " + (enabled ? "enabled" : "disabled (opt-in: [" + ConfigSection + "] " + ConfigKey + " = true, then restart)")
        + (enabled ? " count=" + count + (server ? " pay=" + pay : "") : "");
}
