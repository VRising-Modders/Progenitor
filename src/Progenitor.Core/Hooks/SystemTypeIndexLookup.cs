using System;

namespace Progenitor.Hooks;

/// <summary>What one attempt to turn a system type into its TypeManager index found.</summary>
internal enum SystemTypeIndexState
{
    /// <summary>TypeManager has not built its system table yet; ask again on a later frame.</summary>
    NotReady,

    /// <summary>The index is valid and can be cached.</summary>
    Resolved,

    /// <summary>The table exists but does not list the type: not a system on this side (yet).</summary>
    NotRegistered,
}

/// <summary>
/// Game-free rules <see cref="SystemHooks"/> applies to a <c>TypeManager.GetSystemTypeIndexNoThrow</c>
/// lookup, and the once-per-type log policy for the two non-resolved outcomes.
///
/// The native method dereferences <c>TypeManager.s_ManagedSystemTypeToIndex</c> without a null
/// check, so it throws a NullReferenceException until the first world initializes TypeManager.
/// On the client, Core loads at the title screen and registers its UI system hooks before that,
/// so the lookup is deferred rather than attempted. Once the table exists the method returns -1
/// (not 0) for a type it does not list; neither value is a usable index.
/// </summary>
internal static class SystemTypeIndexLookup
{
    /// <summary>Classify one lookup. <paramref name="value"/> is ignored while the table is missing.</summary>
    public static SystemTypeIndexState Classify(bool tableReady, int value)
    {
        if (!tableReady) return SystemTypeIndexState.NotReady;
        return value > 0 ? SystemTypeIndexState.Resolved : SystemTypeIndexState.NotRegistered;
    }

    /// <summary>The single warning for a hooked type the initialized table does not list.</summary>
    public static string NotRegisteredMessage(string typeName) =>
        $"SystemHooks: {typeName} is not a registered system in this process (TypeManager does not list it); " +
        "its hooks stay inactive until it is registered. This is expected for a system that only exists on the other side.";

    /// <summary>The single error for a lookup that threw after the table existed.</summary>
    public static string ThrewMessage(string typeName, Exception e) =>
        $"SystemHooks: TypeManager.GetSystemTypeIndexNoThrow({typeName}) threw although the system table exists; " +
        $"its hooks stay inactive and later failures for this type are not logged. {e.GetType().Name}: {e.Message}";
}

