using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Stunlock.Core;

namespace Progenitor.Content;

/// <summary>
/// Inserts (and removes) managed-data dictionary entries with a correctly marshaled
/// <see cref="PrefabLookupKey"/>.
///
/// PrefabLookupKey is a struct with a reference field; the interop represents it as a class
/// deriving from <c>Il2CppSystem.ValueType</c>. Passing that wrapper as a generic TKey hands the
/// runtime the object pointer where it expects unboxed struct bytes, so the dictionary hashes
/// 16 bytes of object header. Interop-side ContainsKey then agrees with itself and misses the
/// real table. Calling the method through <c>il2cpp_runtime_invoke</c> with
/// <c>key.Pointer + 0x10</c> is the path spike 2 proved.
/// </summary>
internal static unsafe class ManagedDataInsert
{
    private static IntPtr _setItem;
    private static IntPtr _remove;

    private static IntPtr FindMethod(string prefix)
    {
        var dictType = typeof(Il2CppSystem.Collections.Generic.Dictionary<PrefabLookupKey, Il2CppSystem.Object>);
        RuntimeHelpers.RunClassConstructor(dictType.TypeHandle);
        foreach (var field in dictType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (field.Name.StartsWith(prefix, StringComparison.Ordinal))
                return (IntPtr)field.GetValue(null)!;
        }
        return IntPtr.Zero;
    }

    private static IntPtr SetItemMethod()
    {
        if (_setItem != IntPtr.Zero) return _setItem;
        _setItem = FindMethod("NativeMethodInfoPtr_set_Item_");
        if (_setItem == IntPtr.Zero)
            throw new MissingMethodException(typeof(Il2CppSystem.Collections.Generic.Dictionary<PrefabLookupKey, Il2CppSystem.Object>).Name, "set_Item");
        return _setItem;
    }

    private static IntPtr RemoveMethod()
    {
        if (_remove != IntPtr.Zero) return _remove;
        // Prefer the single-argument Remove(TKey); the out-value overload has a longer suffix.
        IntPtr found = IntPtr.Zero;
        var dictType = typeof(Il2CppSystem.Collections.Generic.Dictionary<PrefabLookupKey, Il2CppSystem.Object>);
        RuntimeHelpers.RunClassConstructor(dictType.TypeHandle);
        foreach (var field in dictType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.Name.StartsWith("NativeMethodInfoPtr_Remove_", StringComparison.Ordinal)) continue;
            // The bool Remove(TKey) field name does not contain "byref".
            if (field.Name.IndexOf("byref", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            found = (IntPtr)field.GetValue(null)!;
            break;
        }
        _remove = found;
        return _remove;
    }

    /// <summary>dictionary[key] = value, key passed as unboxed struct bytes.</summary>
    public static void Set(
        Il2CppSystem.Collections.Generic.Dictionary<PrefabLookupKey, Il2CppSystem.Object> map,
        PrefabLookupKey key,
        Il2CppSystem.Object value)
    {
        var method = SetItemMethod();
        void* unboxedKey = (void*)(key.Pointer + 0x10);
        void** args = stackalloc void*[2];
        args[0] = unboxedKey;
        args[1] = (void*)value.Pointer;
        IntPtr exc = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(method, map.Pointer, args, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
    }

    /// <summary>dictionary.Remove(key), key passed as unboxed struct bytes. No-op if the Remove method is missing.</summary>
    public static bool Remove(
        Il2CppSystem.Collections.Generic.Dictionary<PrefabLookupKey, Il2CppSystem.Object> map,
        PrefabLookupKey key)
    {
        var method = RemoveMethod();
        if (method == IntPtr.Zero) return false;
        void* unboxedKey = (void*)(key.Pointer + 0x10);
        void** args = stackalloc void*[1];
        args[0] = unboxedKey;
        IntPtr exc = IntPtr.Zero;
        IntPtr result = IL2CPP.il2cpp_runtime_invoke(method, map.Pointer, args, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
        if (result == IntPtr.Zero) return false;
        return *(bool*)IL2CPP.il2cpp_object_unbox(result);
    }
}
