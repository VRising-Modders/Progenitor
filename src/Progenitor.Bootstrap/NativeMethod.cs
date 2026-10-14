using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Progenitor.Bootstrap;

/// <summary>
/// Resolves an IL2CPP compiled function pointer the same way Core's
/// <c>Progenitor.Interop.Il2CppNative.MethodPointerByPrefix</c> does. Duplicated here so
/// Bootstrap can apply receive detours without a compile-time reference to Core.
/// </summary>
internal static unsafe class NativeMethod
{
    /// <summary>
    /// Finds the generated <c>NativeMethodInfoPtr_{methodName}_*</c> field and returns the
    /// compiled function pointer stored in the il2cpp MethodInfo. Throws if zero or more than
    /// one field matches, so a game update that adds an overload fails loudly.
    /// </summary>
    public static IntPtr PointerByPrefix(Type interopType, string methodName)
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
        return Pointer(interopType, match.Name);
    }

    /// <summary>
    /// The compiled function pointer behind one exact <c>NativeMethodInfoPtr_*</c> field: the way to
    /// name one overload unambiguously (the address library's overload ids collide when two overloads
    /// share an IL2CPP type signature).
    /// </summary>
    public static IntPtr PointerByField(Type interopType, string nativeMethodInfoField) =>
        Pointer(interopType, nativeMethodInfoField);

    private static IntPtr Pointer(Type interopType, string nativeMethodInfoField)
    {
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
}
