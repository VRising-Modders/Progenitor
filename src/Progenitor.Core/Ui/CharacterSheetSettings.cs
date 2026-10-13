#if !PROGENITOR_TESTS
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using ProjectM.UI;
namespace Progenitor.Ui;

/// <summary>Pass reference-bearing native value types as unboxed data, not object headers.</summary>
internal static unsafe class CharacterSheetSettings
{
    internal static void ClearCaps(AttributeValue value)
    {
        var klass = IL2CPP.il2cpp_object_get_class(value.Pointer);
        var field = IL2CPP.il2cpp_class_get_field_from_name(klass, "ModificationData");
        if (field == IntPtr.Zero) throw new MissingFieldException("AttributeValue", "ModificationData");
        var fieldClass = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field));
        uint alignment = 0;
        int size = IL2CPP.il2cpp_class_value_size(fieldClass, ref alignment);
        if (size != 28) throw new InvalidOperationException("Unexpected character-sheet cap layout.");
        byte* empty = stackalloc byte[size];
        new Span<byte>(empty, size).Clear();
        IL2CPP.il2cpp_field_set_value(value.Pointer, field, empty);
    }
    private static IntPtr _setRow;
    internal static void SetRow(Il2CppSystem.Collections.Generic.List<CharacterAttributeEntry.Data> list, int index, CharacterAttributeEntry.Data row)
    {
        if (_setRow == IntPtr.Zero)
        {
            var type = typeof(Il2CppSystem.Collections.Generic.List<CharacterAttributeEntry.Data>);
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.Name.StartsWith("NativeMethodInfoPtr_set_Item_", StringComparison.Ordinal))
                { _setRow = (IntPtr)field.GetValue(null)!; break; }
            if (_setRow == IntPtr.Zero) throw new MissingMethodException(type.Name, "set_Item");
        }
        void** args = stackalloc void*[2];
        args[0] = &index;
        args[1] = (void*)IL2CPP.il2cpp_object_unbox(row.Pointer);
        IntPtr exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(_setRow, list.Pointer, args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
    }
    private static IntPtr _add;
    internal static void Add(Il2CppSystem.Collections.Generic.List<AttributeSetting> list, AttributeSetting setting)
    {
        if (_add == IntPtr.Zero)
        {
            var type = typeof(Il2CppSystem.Collections.Generic.List<AttributeSetting>);
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.Name.StartsWith("NativeMethodInfoPtr_Add_", StringComparison.Ordinal))
                { _add = (IntPtr)field.GetValue(null)!; break; }
            if (_add == IntPtr.Zero) throw new MissingMethodException(type.Name, "Add");
        }
        void** args = stackalloc void*[1];
        args[0] = (void*)IL2CPP.il2cpp_object_unbox(setting.Pointer);
        IntPtr exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(_add, list.Pointer, args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
    }
}
#endif



