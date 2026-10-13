using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Progenitor.Interop;

/// <summary>
/// Reaches past the interop stubs to the compiled IL2CPP function behind a method, for the cases
/// where Harmony cannot help (by-ref value-type parameters on native-called methods, proven in
/// spike 1). The interop generator leaves a private static <c>NativeMethodInfoPtr_*</c> field per
/// method holding the il2cpp MethodInfo pointer; the compiled function pointer is its first field.
/// </summary>
public static unsafe class Il2CppNative
{
    /// <summary>
    /// Returns the compiled function pointer for a method of an interop type, given the exact
    /// generated field name (for example
    /// <c>NativeMethodInfoPtr_OnReliableEventsReceived_Private_Void_byref_NetBufferIn_0</c>).
    /// </summary>
    public static IntPtr MethodPointer(Type interopType, string nativeMethodInfoField)
    {
        // The static constructor is what resolves the method info pointers.
        RuntimeHelpers.RunClassConstructor(interopType.TypeHandle);

        var field = interopType.GetField(nativeMethodInfoField, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingFieldException(interopType.FullName, nativeMethodInfoField);
        var methodInfo = (IntPtr)field.GetValue(null)!;
        if (methodInfo == IntPtr.Zero)
            throw new InvalidOperationException($"{interopType.Name}.{nativeMethodInfoField} is null; the interop type did not resolve it.");

        var fn = *(IntPtr*)methodInfo;
        if (fn == IntPtr.Zero)
            throw new InvalidOperationException($"{interopType.Name}.{nativeMethodInfoField} has no compiled method pointer.");
        return fn;
    }

    /// <summary>
    /// Finds the generated field by method name prefix when the exact suffix is not known.
    /// Throws if zero or more than one field matches, so a game update that adds an overload fails loudly.
    /// </summary>
    public static IntPtr MethodPointerByPrefix(Type interopType, string methodName)
    {
        RuntimeHelpers.RunClassConstructor(interopType.TypeHandle);
        var prefix = $"NativeMethodInfoPtr_{methodName}_";
        FieldInfo? match = null;
        foreach (var f in interopType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (!f.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (match != null)
                throw new AmbiguousMatchException($"{interopType.Name}.{methodName} has more than one overload; pass the exact field name.");
            match = f;
        }
        if (match == null) throw new MissingMethodException(interopType.FullName, methodName);
        return MethodPointer(interopType, match.Name);
    }
}
