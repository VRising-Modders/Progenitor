namespace Progenitor.Ecs;

/// <summary>
/// The zero-sized (tag) component test for <see cref="Singletons"/>. Kept free of Unity /
/// BepInEx so the test project can compile this file without interop assemblies.
///
/// Live (evidence §208, 2026-09-05): <c>.ecs singleton UserMuteSingleton</c> logged
/// <c>Singletons.TryGetRawRW(TypeIndex 1073876029) failed: ... ArgumentException: This operation
/// can not be called with a zero sized component (ProjectM.UserMuteSingleton)</c>. Core caught it,
/// but the API can decide that case itself and never call the engine.
///
/// <b>The signal is the TypeIndex value, not TypeSize.</b> Trap 7: <c>TypeManager.GetTypeInfo
/// (index).TypeSize</c> reads 0 for ORDINARY components through this interop (Translation,
/// LifeTime, Follower, and <c>ServerRuntimeSettings</c> in that same evidence line read
/// <c>typeSize=0</c> while the raw pointer was valid), so TypeSize 0 can never mean "tag".
/// <c>SizeInChunk</c> is garbage in both directions for the same reason. What is trustworthy is
/// the <see cref="ZeroSizeInChunkTypeFlag"/> bit baked into the TypeIndex itself at registration:
/// it is part of the integer the engine dispatches on, it travels with the value the caller
/// already holds, and reading it needs no interop call at all.
/// </summary>
public static class SingletonsZeroSize
{
    /// <summary>
    /// <c>Unity.Entities.TypeManager.ZeroSizeInChunkTypeFlag</c> = 0x40000000
    /// (server metadata: <c>public const int ZeroSizeInChunkTypeFlag = 1073741824;</c>).
    /// Set on a component's TypeIndex when it occupies no bytes in the chunk, which is exactly the
    /// condition <c>EntityManager.GetComponentDataRawRW</c> refuses with "This operation can not be
    /// called with a zero sized component ({0})."
    /// </summary>
    public const int ZeroSizeInChunkTypeFlag = 1073741824;

    /// <summary>
    /// <c>Unity.Entities.TypeManager.ClearFlagsMask</c> = 16383 (server metadata:
    /// <c>public const int ClearFlagsMask = 16383;</c>). The low bits of a TypeIndex are the type's
    /// ordinal in <c>s_TypeInfos</c>; everything above them is a flag. <c>TypeIndex.Value</c> is
    /// the whole flagged int at offset 0, and <c>TypeIndex.IsZeroSized</c>
    /// is derived from it.
    /// </summary>
    public const int ClearFlagsMask = 16383;

    /// <summary>
    /// True when <paramref name="typeIndexValue"/> (a <c>TypeIndex.Value</c>) names a zero-sized
    /// component. Pure bit test: no TypeManager call, no TypeInfo read, nothing that goes through
    /// the interop's broken struct marshal.
    /// </summary>
    public static bool IsZeroSized(int typeIndexValue) => (typeIndexValue & ZeroSizeInChunkTypeFlag) != 0;

    /// <summary>The type's ordinal, with every flag bit cleared. For log lines only.</summary>
    public static int IndexOf(int typeIndexValue) => typeIndexValue & ClearFlagsMask;

    /// <summary>
    /// A name for the refused component: the managed full name when the caller could resolve one,
    /// otherwise the TypeIndex and its ordinal.
    /// </summary>
    public static string DescribeComponent(int typeIndexValue, string? componentName)
    {
        string index = "TypeIndex " + typeIndexValue + " (index " + IndexOf(typeIndexValue) + ")";
        return string.IsNullOrEmpty(componentName) ? index : componentName + " [" + index + "]";
    }

    /// <summary>
    /// The single line logged when a raw read is refused. Names the component and the reason, and
    /// points at the API that does work for a tag component.
    /// </summary>
    public static string RefusalMessage(int typeIndexValue, string? componentName)
    {
        return "Singletons.TryGetRawRW(" + DescribeComponent(typeIndexValue, componentName)
            + "): refused, zero-sized (tag) component. Its TypeIndex carries "
            + "TypeManager.ZeroSizeInChunkTypeFlag (0x40000000), so the component owns no bytes in "
            + "the chunk and GetComponentDataRawRW would throw \"This operation can not be called "
            + "with a zero sized component\" (evidence 208: ProjectM.UserMuteSingleton). A tag has "
            + "nothing to read or poke: use Singletons.TryGetEntity / HasComponent instead.";
    }
}
