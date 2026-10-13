using System;

namespace Progenitor.Ui;

/// <summary>
/// The liveness test the UI code runs every frame, without an IL2CPP allocation in the common case.
///
/// <para><b>Why.</b> <c>obj != null</c> on a <c>UnityEngine.Object</c> is Unity's overloaded
/// operator. Through Il2CppInterop it is <c>CompareBaseObjects</c> called via
/// <c>il2cpp_runtime_invoke</c>, which boxes the bool return on the IL2CPP heap: about 32 bytes per
/// check, and the per-frame UI pumps make dozens of them (2026-10-06 audit, unverified live). Every
/// extra IL2CPP byte brings the next Boehm collection closer.</para>
///
/// <para><b>Same answer.</b> <c>CompareBaseObjects(x, null)</c> is <c>!IsNativeObjectAlive(x)</c>,
/// and Unity's <c>IsNativeObjectAlive</c> returns true as soon as the wrapper's
/// <c>m_CachedPtr</c> is non-zero (Unity clears it when the native object is destroyed). Only when
/// it is zero does Unity look further (instance-id lookup for non-MonoBehaviour objects), so that
/// case falls through to the operator itself. <c>m_CachedPtr</c> is a plain field read in the
/// generated interop (field offset plus pointer read), so the common path allocates nothing.</para>
///
/// <para>Throws exactly where the old <c>obj != null &amp;&amp; obj.Pointer != IntPtr.Zero</c> threw
/// (a wrapper whose IL2CPP object was collected); callers that caught keep catching.</para>
/// </summary>
internal static class UnityAlive
{
    internal static bool Of(UnityEngine.Object? obj)
    {
        if (obj is null) return false;
        if (obj.Pointer == IntPtr.Zero) return false;
        if (obj.m_CachedPtr != IntPtr.Zero) return true;
        return obj != null;
    }
}
