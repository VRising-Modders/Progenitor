using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>
/// Reports reflected model field names for wearable item clones. Reflection is
/// diagnostic only: model references stay unchanged until a native layout and
/// access contract is verified. The original client mesh overlay supplies the look.
/// </summary>
internal static class WearableModelWrite
{
    private static readonly HashSet<int> Logged = new();

    internal static void TryWrite(World world, PrefabDefinition def)
    {
        var graphics = def.Wearable;
        if (graphics == null) return;
        if (string.IsNullOrEmpty(graphics.MaleModel) && string.IsNullOrEmpty(graphics.FemaleModelOrMale))
            return;
        if (world == null || !world.IsCreated) return;
        if (!PrefabLookup.TryGetEntity(world, def.Guid, out var clone) || clone == Entity.Null)
            return;

        var em = world.EntityManager;
        var found = new List<FieldHit>();
        string scanned = Scan(em, clone, found);
        var sb = new StringBuilder();
        sb.Append("Wearables: '").Append(def.Id).Append("' ");
        if (found.Count == 0)
            sb.Append("no model PrefabGUID field on the clone (").Append(scanned).Append("); ");
        else
        {
            sb.Append("reflected model fields:");
            for (int i = 0; i < found.Count; i++)
                sb.Append(' ').Append(found[i].Component).Append('.').Append(found[i].Name);
            sb.Append("; ");
        }
        sb.Append("model references unchanged: no verified layout contract; the worn mesh is the client overlay.");
        LogOnce(def.Guid.GuidHash, sb.ToString());
    }

    private static string Scan(EntityManager em, Entity clone, List<FieldHit> found)
    {
        var names = new StringBuilder();
        try
        {
            var types = em.GetComponentTypes(clone, Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    // GetManagedType returns Il2CppSystem.Type in this interop. Diagnostic
                    // field inspection needs the matching System.Type stub from a loaded assembly.
                    Il2CppSystem.Type? il2 = null;
                    try { il2 = types[i].GetManagedType(); }
                    catch { continue; }
                    if (il2 == null) continue;
                    string shortName;
                    try { shortName = il2.Name ?? "?"; }
                    catch { shortName = "?"; }
                    if (names.Length > 0) names.Append(", ");
                    names.Append(shortName);
                    Type? managed = ResolveManaged(il2);
                    if (managed == null) continue;
                    Walk(managed, "", managed.Name, found, 0);
                }
            }
            finally { types.Dispose(); }
        }
        catch (Exception e)
        {
            names.Append("scan threw: ").Append(e.Message);
        }
        return names.Length == 0 ? "no components" : names.ToString();
    }

    /// <summary>
    /// Map an Il2CppSystem.Type from <c>ComponentType.GetManagedType</c> to the System.Type
    /// stub the AppDomain already loaded (interop assemblies). Same shape as
    /// PrefabEditWireApply's loaded-assembly search; no new assembly loads.
    /// </summary>
    private static Type? ResolveManaged(Il2CppSystem.Type il2)
    {
        string? full = null;
        try { full = il2.FullName; }
        catch { return null; }
        if (string.IsNullOrEmpty(full)) return null;
        try
        {
            var direct = Type.GetType(full, throwOnError: false);
            if (direct != null) return direct;
        }
        catch { /* fall through */ }
        Assembly[] loaded;
        try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return null; }
        for (int i = 0; i < loaded.Length; i++)
        {
            try
            {
                var t = loaded[i].GetType(full, throwOnError: false);
                if (t != null) return t;
            }
            catch { /* skip reflection-only / unloadable */ }
        }
        return null;
    }

    private static void Walk(Type type, string prefix, string component, List<FieldHit> found, int depth)
    {
        if (depth > 2) return;
        FieldInfo[] fields;
        try { fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public); }
        catch { return; }
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            string name = prefix + field.Name;
            if (IsPrefabGuid(field.FieldType))
            {
                if (IsFemaleModel(name) || IsMaleModel(name))
                    found.Add(new FieldHit(name, component));
                continue;
            }
            if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                Walk(field.FieldType, name + ".", component, found, depth + 1);
        }
    }

    private static bool IsPrefabGuid(Type type)
    {
        string n = type.Name;
        return string.Equals(n, "PrefabGUID", StringComparison.Ordinal);
    }

    private static bool IsFemaleModel(string name) =>
        name.Contains("Female", StringComparison.OrdinalIgnoreCase)
        && IsModelish(name);

    private static bool IsMaleModel(string name) =>
        !name.Contains("Female", StringComparison.OrdinalIgnoreCase)
        && IsModelish(name)
        && (name.Contains("Male", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Model", StringComparison.OrdinalIgnoreCase));

    private static bool IsModelish(string name) =>
        (name.Contains("Model", StringComparison.OrdinalIgnoreCase) || name.Contains("Prefab", StringComparison.OrdinalIgnoreCase))
        && !name.Contains("Buff", StringComparison.OrdinalIgnoreCase);

    private static void LogOnce(int guid, string line)
    {
        if (!Logged.Add(guid)) return;
        Plugin.Logger?.LogInfo(line);
    }

    private readonly struct FieldHit
    {
        internal FieldHit(string name, string component)
        {
            Name = name;
            Component = component;
        }

        internal string Name { get; }
        internal string Component { get; }
    }
}
