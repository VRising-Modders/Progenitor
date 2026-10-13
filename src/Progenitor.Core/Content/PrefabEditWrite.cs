using System;
using System.Runtime.InteropServices;
using Progenitor.Ecs;

namespace Progenitor.Content;

/// <summary>
/// Size gate for <see cref="PrefabEditHandle.SetComponent{T}"/> (review R19 finding 12).
/// <c>Write&lt;T&gt;</c> already calls <see cref="RawWriteGuard.EnsureTypedWrite"/>;
/// <c>SetComponentData&lt;T&gt;</c> did not, so a plugin could skip the TypeSize-0 check
/// (evidence §32: a guessed Follower blob killed the server). This helper is game-free so
/// the unit tests lock the same rule the live handle uses.
/// </summary>
internal static class PrefabEditWrite
{
    /// <summary><c>typeof(ProjectM.ItemData).Name</c>. Documented known-good <c>SetComponentData</c> path.</summary>
    public const string ItemDataTypeName = "ItemData";

    /// <summary><c>typeof(ProjectM.ItemData).FullName</c>.</summary>
    public const string ItemDataFullName = "ProjectM.ItemData";

    /// <summary>
    /// True when <c>SetComponent&lt;T&gt;</c> may call <c>SetComponentData&lt;T&gt;</c>.
    /// Only <c>ItemData</c> is on that list (bool fields, not C# <c>unmanaged</c>; the
    /// applicator already uses this path). Unmanaged types go through <c>Write&lt;T&gt;</c>.
    /// </summary>
    public static bool IsKnownGoodSetComponent(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        return typeName.Equals(ItemDataTypeName, StringComparison.Ordinal)
            || typeName.Equals(ItemDataFullName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Instance layout size of a value type for the typed-write gate. Classes are refused:
    /// <c>SetComponent</c> is for interop structs.
    /// </summary>
    public static int ManagedSizeOf(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (!type.IsValueType)
        {
            throw new InvalidOperationException(
                "Refusing SetComponent of " + (type.FullName ?? type.Name) + ": not a value type.");
        }

        return Marshal.SizeOf(type);
    }

    /// <summary>
    /// Same TypeSize / sizeof gate <c>Entity.Write&lt;T&gt;</c> uses. TypeSize 0 is the interop
    /// norm and is allowed; a nonzero TypeSize that does not match <paramref name="managedSize"/>
    /// is refused.
    /// </summary>
    public static void EnsureTyped(int ecsTypeSize, int managedSize, int sizeInChunk, string typeName) =>
        RawWriteGuard.EnsureTypedWrite(ecsTypeSize, managedSize, sizeInChunk, typeName);
}
