using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>inspect</c> op: one call that returns every component on an entity with
/// its decoded fields. It composes the two ops an agent used to chain — <c>components</c> for
/// the type list and one <c>get</c> per type for the fields — so reading an unfamiliar entity
/// is a single round trip instead of thirty.
///
/// Decoding is the same walk <c>get</c> uses (<see cref="ComponentDump"/> over
/// <c>GetComponentDataRawRO</c>); buffers add <c>GetBufferRawRO</c> /
/// <c>GetBufferLength</c> (both public on this interop's <c>EntityManager</c>) with
/// <c>args.depth</c> elements decoded per buffer. A component whose interop struct cannot be
/// found, or whose blob cannot be read, is listed with a <c>skipped</c> reason rather than
/// failing the whole call — an inspect that dies on one exotic type is useless.
///
/// Both sides, main thread. Budget and argument policy: <see cref="InspectArgs"/>.
/// </summary>
public static unsafe class InspectOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>entity</c> required (<c>index:version</c>);
    /// <c>depth</c> optional (buffer elements per buffer, 0..256, default 0);
    /// <c>max</c> optional (decoded-byte budget, default 64 KiB); <c>only</c> optional
    /// (component names to decode — the rest are still listed).
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var request = InspectArgs.Parse(args);
            var em = world.EntityManager;
            var entity = ResolveEntity(em, request.Entity);

            var budget = new InspectArgs.Budget(request.MaxBytes);
            string? truncatedAt = null;
            var rows = new List<Dictionary<string, object?>>();

            var types = em.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    var row = Describe(em, entity, types[i], request, budget, ref truncatedAt);
                    rows.Add(row);
                }
            }
            finally
            {
                types.Dispose();
            }

            return InspectArgs.Result(
                FormatEntity(entity),
                TryName(em, entity),
                EntityOps.TryPrefabInfo(em, entity, EntityOps.TryPrefabCollection(world)),
                rows.Count,
                request.Depth,
                budget,
                truncatedAt,
                rows);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"inspect op threw: {e}");
            throw new InvalidOperationException($"inspect op failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// One component row. Never throws: an unreadable component becomes a <c>skipped</c>
    /// reason so the rest of the entity still comes back.
    /// </summary>
    private static Dictionary<string, object?> Describe(
        EntityManager em,
        Entity entity,
        ComponentType componentType,
        InspectArgs.Request request,
        InspectArgs.Budget budget,
        ref string? truncatedAt)
    {
        string name = TypeName(componentType);

        if (!request.Wants(name))
            return InspectArgs.ComponentRow(name, KindOf(componentType), 0, null, "not requested");

        if (SafeIsManaged(componentType))
            return InspectArgs.ComponentRow(name, "managed", 0, null, "managed component: there is no raw blob to decode");
        if (SafeIsShared(componentType))
            return InspectArgs.ComponentRow(name, "shared", 0, null, "shared component: the value lives in the chunk header, not on this entity");

        Type managed;
        try
        {
            managed = EntityOps.FindInteropStruct(name);
        }
        catch (Exception e)
        {
            return InspectArgs.ComponentRow(name, KindOf(componentType), 0, null, $"no interop struct to decode with: {e.Message}");
        }

        return SafeIsBuffer(componentType)
            ? DescribeBuffer(em, entity, componentType, managed, name, request, budget, ref truncatedAt)
            : DescribeComponent(em, entity, componentType, managed, name, budget, ref truncatedAt);
    }

    private static Dictionary<string, object?> DescribeComponent(
        EntityManager em,
        Entity entity,
        ComponentType componentType,
        Type managed,
        string name,
        InspectArgs.Budget budget,
        ref string? truncatedAt)
    {
        int size;
        try
        {
            size = ComponentDump.SizeOf(managed, TypeManager.GetTypeInfo(componentType.TypeIndex).TypeSize);
        }
        catch (Exception e)
        {
            return InspectArgs.ComponentRow(name, "component", 0, null, $"size lookup failed: {e.Message}");
        }

        if (size <= 0)
            return InspectArgs.ComponentRow(name, "tag", 0, null, null);

        if (!budget.TryTake(size))
        {
            truncatedAt ??= name;
            return InspectArgs.ComponentRow(name, "component", size, null, "size cap reached; re-run with a larger max or an only filter");
        }

        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRO(entity, componentType.TypeIndex);
            if (raw == null)
                return InspectArgs.ComponentRow(name, "component", size, null, "GetComponentDataRawRO returned null");

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var spec in ComponentDump.FieldsOf(managed, size))
                fields[spec.Name] = ComponentDump.ReadField(raw, spec);
            return InspectArgs.ComponentRow(name, "component", size, fields, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"inspect: '{name}' on {FormatEntity(entity)} failed: {e.Message}");
            return InspectArgs.ComponentRow(name, "component", size, null, $"read failed: {e.Message}");
        }
    }

    private static Dictionary<string, object?> DescribeBuffer(
        EntityManager em,
        Entity entity,
        ComponentType componentType,
        Type managed,
        string name,
        InspectArgs.Request request,
        InspectArgs.Budget budget,
        ref string? truncatedAt)
    {
        int elementSize = 0;
        int length = 0;
        try
        {
            var info = TypeManager.GetTypeInfo(componentType.TypeIndex);
            elementSize = info.ElementSize;
            if (elementSize <= 0) elementSize = ComponentDump.SizeOf(managed, 0);
            length = em.GetBufferLength(entity, componentType.TypeIndex);
        }
        catch (Exception e)
        {
            return InspectArgs.BufferRow(name, elementSize, length, null, $"buffer header read failed: {e.Message}");
        }

        if (request.Depth <= 0)
            return InspectArgs.BufferRow(name, elementSize, length, null, length == 0 ? null : "pass depth to decode elements");
        if (elementSize <= 0)
            return InspectArgs.BufferRow(name, elementSize, length, null, "element size is not known through this interop");
        if (length <= 0)
            return InspectArgs.BufferRow(name, elementSize, 0, Array.Empty<object?>(), null);

        int wanted = Math.Min(request.Depth, length);
        // Take the budget one element at a time so a huge buffer fills the cap and stops
        // rather than being refused whole (and so `bytes` stays honest).
        var elements = new List<object?>(wanted);
        try
        {
            byte* raw = (byte*)em.GetBufferRawRO(entity, componentType.TypeIndex);
            if (raw == null)
                return InspectArgs.BufferRow(name, elementSize, length, null, "GetBufferRawRO returned null");

            var specs = ComponentDump.FieldsOf(managed, elementSize);
            for (int i = 0; i < wanted; i++)
            {
                if (!budget.TryTake(elementSize))
                {
                    truncatedAt ??= name;
                    break;
                }
                byte* element = raw + ((long)i * elementSize);
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var spec in specs)
                    fields[spec.Name] = ComponentDump.ReadField(element, spec);
                elements.Add(fields);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"inspect: buffer '{name}' on {FormatEntity(entity)} failed: {e.Message}");
            return InspectArgs.BufferRow(name, elementSize, length, elements.Count > 0 ? elements : null, $"read failed: {e.Message}");
        }

        return InspectArgs.BufferRow(name, elementSize, length, elements, null);
    }

    // ---- shared helpers -----------------------------------------------------------------------

    /// <summary>
    /// The component's full name, or a <c>&lt;unresolved TypeIndex N&gt;</c> placeholder.
    /// Same fallback <c>components</c> uses so the two ops never disagree on a name.
    /// </summary>
    internal static string TypeName(ComponentType componentType)
    {
        try
        {
            var managed = componentType.GetManagedType();
            return managed != null
                ? (managed.FullName ?? managed.Name ?? componentType.ToString())
                : componentType.ToString();
        }
        catch (Exception e)
        {
            return $"<unresolved TypeIndex {componentType.TypeIndex}: {e.Message}>";
        }
    }

    internal static string KindOf(ComponentType componentType)
    {
        if (SafeIsBuffer(componentType)) return "buffer";
        if (SafeIsManaged(componentType)) return "managed";
        if (SafeIsShared(componentType)) return "shared";
        return "component";
    }

    internal static bool SafeIsBuffer(ComponentType componentType)
    {
        try { return componentType.IsBuffer; } catch { return false; }
    }

    internal static bool SafeIsManaged(ComponentType componentType)
    {
        try { return componentType.IsManagedComponent; } catch { return false; }
    }

    internal static bool SafeIsShared(ComponentType componentType)
    {
        try { return componentType.IsSharedComponent; } catch { return false; }
    }

    /// <summary>
    /// <c>index:version</c> that must already exist. Shared by <c>inspect</c>,
    /// <c>snapshot</c> and <c>restore</c> so the three agree on the error text.
    /// </summary>
    internal static Entity ResolveEntity(EntityManager em, string text)
    {
        if (!InspectArgs.TryParseEntityId(text, out var index, out var version))
            throw new ArgumentException($"args.entity must be 'index:version' (got '{text}')");
        var entity = new Entity { Index = index, Version = version };
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {text} does not exist in this world (destroyed, or the index:version is from a different world)");
        return entity;
    }

    /// <summary>The character name when the entity is a PlayerCharacter or a User; otherwise null.</summary>
    internal static string? TryName(EntityManager em, Entity entity)
    {
        try
        {
            var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(entity, userType))
            {
                var name = em.GetComponentData<User>(entity).CharacterName.ToString();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"inspect: User read on {FormatEntity(entity)}: {e.Message}");
        }

        try
        {
            var pcType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(entity, pcType)) return null;
            var pc = em.GetComponentData<PlayerCharacter>(entity);
            if (em.Exists(pc.UserEntity))
            {
                var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
                if (em.HasComponent(pc.UserEntity, userType))
                {
                    var name = em.GetComponentData<User>(pc.UserEntity).CharacterName.ToString();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            var own = pc.Name.ToString();
            return string.IsNullOrEmpty(own) ? null : own;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"inspect: name of {FormatEntity(entity)} failed: {e.Message}");
            return null;
        }
    }

    internal static string FormatEntity(Entity entity) => $"{entity.Index}:{entity.Version}";
}
