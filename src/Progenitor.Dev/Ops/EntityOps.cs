using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Progenitor.Units;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge inspection over a live ECS world. The game's IL2CPP interop crashes on missing
/// generic instantiations, so every lookup here is by name → <see cref="ComponentType"/> and every
/// unknown component is read as raw bytes. Returns only JSON-friendly CLR objects; the caller
/// (the bridge) already marshals these onto the main thread.
/// </summary>
public static unsafe class EntityOps
{
    private static readonly string[] InteropAssemblyNames =
    {
        "ProjectM",
        "ProjectM.Shared",
        "Unity.Entities",
        "Unity.Transforms",
        "Stunlock.Core",
        "ProjectM.Network",
        "ProjectM.Gameplay.Systems",
        "ProjectM.Gameplay.Scripting",
        "ProjectM.Behaviours",
    };

    private static readonly object IndexLock = new();
    private static Dictionary<string, Type>? _byFullName;
    private static Dictionary<string, List<Type>>? _bySimpleName;
    private static readonly Dictionary<string, ComponentType> ComponentTypeCache = new(StringComparer.OrdinalIgnoreCase);

    private static ComponentType? _translationType;
    private static ComponentType? _localToWorldType;
    private static ComponentType? _prefabGuidType;
    private static ComponentType? _userType;

    /// <summary>
    /// Resolves an interop component struct to a <see cref="ComponentType"/> the EntityManager
    /// understands. A runtime <see cref="Type"/> has to go through <see cref="Il2CppType.From"/>;
    /// the generic <c>Of&lt;T&gt;</c> helper cannot be used when the name arrives as a string.
    /// </summary>
    public static ComponentType ResolveComponentType(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("component name is empty; pass a simple name (Health) or full name (ProjectM.Health)");

        name = name.Trim();
        lock (IndexLock)
        {
            if (ComponentTypeCache.TryGetValue(name, out var cached)) return cached;
        }

        var managed = FindInteropStruct(name);
        RuntimeHelpers.RunClassConstructor(managed.TypeHandle);

        Il2CppSystem.Type il2CppType;
        try
        {
            il2CppType = Il2CppType.From(managed);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"'{managed.FullName}' has no IL2CPP class pointer. It is not an interop component the game registered. {e.Message}");
        }

        ComponentType componentType;
        try
        {
            componentType = new ComponentType(il2CppType, ComponentType.AccessMode.ReadWrite);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"TypeManager rejected '{managed.FullName}'. Is the default world running? {e.Message}");
        }

        lock (IndexLock)
        {
            ComponentTypeCache[name] = componentType;
            if (managed.FullName != null) ComponentTypeCache[managed.FullName] = componentType;
        }

        return componentType;
    }

    /// <summary>
    /// Finds entities by required / excluded component names. Default queries drop disabled
    /// entities (offline players), so IncludeDisabledEntities is on unless the caller turns it off.
    /// Requires at least one <c>all</c> component unless <c>args.allowUnbounded</c> is true.
    /// Stops after <c>limit</c> accepted rows unless <c>args.total</c> is true, and never visits
    /// more than <see cref="QueryArgs.HardScanCap"/> entities (<c>truncated</c> in the result).
    /// </summary>
    public static object Query(World world, JsonElement args)
    {
        RequireWorld(world);
        var em = world.EntityManager;
        var allNames = ReadStringArray(args, "all");
        var noneNames = ReadStringArray(args, "none");
        bool allowUnbounded = ReadBool(args, "allowUnbounded", false);
        QueryArgs.RequireAllFilter(allNames.Length, allowUnbounded);
        int limit = QueryArgs.ClampLimit(ReadInt(args, "limit", QueryArgs.DefaultLimit));
        bool wantTotal = ReadBool(args, "total", false);
        var limits = new QueryArgs.Limits(limit, wantTotal);
        bool includeDisabled = ReadBool(args, "includeDisabled", true);
        NearFilter? near = ReadNear(args);

        var builder = new EntityQueryBuilder(Allocator.Temp);
        try
        {
            foreach (var name in allNames)
                builder = builder.AddAll(new ComponentType(ResolveIl2CppType(name), ComponentType.AccessMode.ReadOnly));
            foreach (var name in noneNames)
                builder = builder.AddNone(new ComponentType(ResolveIl2CppType(name), ComponentType.AccessMode.ReadOnly));
            if (includeDisabled)
                builder = builder.WithOptions(EntityQueryOptions.IncludeDisabledEntities);

            var query = em.CreateEntityQuery(ref builder);
            NativeArray<Entity> entities = default;
            bool haveEntities = false;
            try
            {
                entities = query.ToEntityArray(Allocator.Temp);
                haveEntities = true;
                var prefabs = TryPrefabCollection(world);
                var rows = new List<object>();
                int total = 0;
                int visited = 0;
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!limits.Continue(visited, total)) break;
                    visited++;
                    var entity = entities[i];
                    var position = TryPosition(em, entity);
                    if (near.HasValue)
                    {
                        if (!position.HasValue) continue;
                        if (!near.Value.Contains(position.Value)) continue;
                    }

                    total++;
                    if (rows.Count >= limit) continue;
                    rows.Add(new
                    {
                        entity = FormatEntity(entity),
                        prefab = TryPrefabInfo(em, entity, prefabs),
                        position = position.HasValue ? PositionObject(position.Value) : null,
                    });
                }

                bool truncated = limits.IsTruncated(visited, entities.Length);
                return new { total, count = rows.Count, truncated, entities = rows };
            }
            finally
            {
                if (haveEntities) entities.Dispose();
                query.Dispose();
            }
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"query failed: {e}");
            throw new InvalidOperationException($"query failed: {e.Message}", e);
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// Reads one component as JSON by walking FieldOffset-annotated fields over the raw pointer.
    /// Generic GetComponentData&lt;T&gt; is not safe for arbitrary T on this interop.
    /// </summary>
    public static object Get(World world, JsonElement args)
    {
        RequireWorld(world);
        var entity = ReadEntity(world.EntityManager, args, "entity");
        var componentName = ReadRequiredString(args, "component");
        var dumped = DumpComponent(world.EntityManager, entity, componentName);
        return new { entity = dumped.Entity, component = dumped.Component, size = dumped.Size, fields = dumped.Fields };
    }

    /// <summary>
    /// Patches listed primitive / Entity / PrefabGUID / float3 fields on a copy of the component
    /// bytes and writes them back. Unknown field names are refused so a typo cannot silently no-op.
    /// </summary>
    public static object Set(World world, JsonElement args)
    {
        RequireWorld(world);
        var em = world.EntityManager;
        var entity = ReadEntity(em, args, "entity");
        var componentName = ReadRequiredString(args, "component");
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.fields is required and must be an object of { fieldName: value }");

        var componentType = ResolveComponentType(componentName);
        EnsureIComponentData(componentType, componentName);
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {FormatEntity(entity)} does not exist");
        if (!em.HasComponent(entity, componentType))
            throw new InvalidOperationException($"entity {FormatEntity(entity)} has no component '{componentName}' (full name {componentType.GetManagedType()?.FullName}). Call 'components' to list what it has.");

        var typeIndex = componentType.TypeIndex;
        var managed = FindInteropStruct(componentName);
        int size = ComponentDump.SizeOf(managed, TypeManager.GetTypeInfo(typeIndex).TypeSize);
        if (size <= 0)
            throw new InvalidOperationException($"'{componentName}' is zero-sized (a tag). There are no fields to set.");

        var specs = ComponentDump.FieldsOf(managed, size);

        byte* src = (byte*)em.GetComponentDataRawRO(entity, typeIndex);
        if (src == null)
            throw new InvalidOperationException($"GetComponentDataRawRO returned null for '{componentName}' on {FormatEntity(entity)}");

        var copy = new byte[size];
        Marshal.Copy((IntPtr)src, copy, 0, size);

        foreach (var prop in fields.EnumerateObject())
        {
            var spec = FindField(specs, prop.Name);
            if (spec == null)
                throw new ArgumentException($"unknown field '{prop.Name}' on {managed.FullName}. Known fields: {FieldNames(specs)}");
            if (!ComponentDump.IsWritable(spec.Kind))
                throw new ArgumentException($"field '{spec.Name}' ({spec.ClrType.Name}) is not writable through this op; only primitives, Entity, PrefabGUID, and float3 can be set");
            ComponentDump.WriteField(copy, spec, prop.Value);
        }

        fixed (byte* ptr = copy)
            em.SetComponentDataRaw(entity, typeIndex, ptr, size);

        var dumped = DumpComponent(em, entity, componentName);
        return new { entity = dumped.Entity, component = dumped.Component, size = dumped.Size, fields = dumped.Fields };
    }

    /// <summary>
    /// Lists the component type names on one entity. Uses GetComponentTypes rather than guessing
    /// from the archetype, so disabled entities and mixed archetypes still report correctly.
    /// </summary>
    public static object Components(World world, JsonElement args)
    {
        RequireWorld(world);
        var em = world.EntityManager;
        var entity = ReadEntity(em, args, "entity");
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {FormatEntity(entity)} does not exist");

        var names = ListComponentNames(em, entity);
        return new { entity = FormatEntity(entity), count = names.Count, components = names };
    }

    /// <summary>
    /// Instantiates a prefab in the server world. Prefab entities live in
    /// PrefabCollectionSystem._PrefabGuidToEntityMap; Instantiate copies them into the live world.
    /// Placement writes Translation, LastTranslation, and SpawnTransform.Position through
    /// <see cref="UnitSpawnPlacement"/> (the Units path that keeps a rat at the requested
    /// pose; evidence 108). The JSON result is deferred one Drain-second later so
    /// <c>position</c> is the live Translation and <c>placed</c> says whether it stuck.
    /// </summary>
    public static object Spawn(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("spawn runs on the server side only; the client cannot instantiate gameplay prefabs");
        RequireWorld(world);

        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (prefabs == null)
            throw new InvalidOperationException($"PrefabCollectionSystem is not in world '{world.Name}' yet; wait until the world has finished booting");

        var guid = ResolvePrefabGuid(prefabs, args);
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var prefabEntity) || prefabEntity == Entity.Null)
            throw new InvalidOperationException($"prefab {guid._Value} is not in _PrefabGuidToEntityMap. Check the name/hash with query on PrefabGUID, or wait until PrefabCollectionSystem has registered.");

        SpawnArgs.ParsePosition(args, out float x, out float y, out float z);
        int count = SpawnArgs.ParseCount(args);
        var pos = new float3(x, y, z);
        string? prefabName = TryPrefabName(prefabs, guid);

        var spawned = new List<Entity>(count);
        for (int i = 0; i < count; i++)
        {
            var entity = em.Instantiate(prefabEntity);
            UnitSpawnPlacement.TryPlace(em, entity, pos, "spawn");
            spawned.Add(entity);
        }

        var deferred = new DeferredResult(SpawnArgs.ReadbackWaitMs);
        try
        {
            MainThread.PostDelayed(SpawnArgs.ReadbackDelaySeconds, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                        return;
                    deferred.SetResult(ReadBackSpawn(world, guid._Value, prefabName, x, y, z, spawned));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"spawn read-back failed: {e}");
                    deferred.SetException(e);
                }
            }, deferred);
        }
        catch (Exception e)
        {
            deferred.SetException(e);
            throw;
        }

        return deferred;
    }

    private static object ReadBackSpawn(
        World world,
        int prefabHash,
        string? prefabName,
        float x,
        float y,
        float z,
        List<Entity> spawned)
    {
        EntityManager em = default;
        bool haveWorld = false;
        try
        {
            if (world.IsCreated)
            {
                em = world.EntityManager;
                haveWorld = true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"spawn read-back world check failed: {e.Message}");
        }

        var rows = new List<Dictionary<string, object?>>(spawned.Count);
        int placedCount = 0;
        for (int i = 0; i < spawned.Count; i++)
        {
            var entity = spawned[i];
            float? ax = null, ay = null, az = null;
            if (haveWorld)
            {
                try
                {
                    if (em.Exists(entity))
                    {
                        var found = TryPosition(em, entity);
                        if (found.HasValue)
                        {
                            ax = found.Value.x;
                            ay = found.Value.y;
                            az = found.Value.z;
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"spawn read-back of {FormatEntity(entity)} failed: {e.Message}");
                }
            }

            bool placed = SpawnArgs.IsPlaced(x, y, z, ax, ay, az);
            if (placed) placedCount++;
            rows.Add(SpawnArgs.EntityRow(FormatEntity(entity), prefabHash, prefabName, ax, ay, az, placed));
        }

        Plugin.Logger?.LogInfo(
            $"spawn: read-back prefab='{prefabName ?? prefabHash.ToString(CultureInfo.InvariantCulture)}' count={rows.Count} placed={placedCount}/{rows.Count}");
        return SpawnArgs.Result(rows.Count, prefabHash, prefabName, rows);
    }

    /// <summary>
    /// Looks a prefab up in <c>PrefabCollectionSystem._PrefabGuidToEntityMap</c> without
    /// instantiating it. Name/hash resolution is the same three lookups <see cref="Spawn"/>
    /// uses. Optional <c>args.component</c> adds a field dump of that type on the collection
    /// entity (the same walk as <see cref="Get"/>).
    /// </summary>
    public static object Prefab(World world, JsonElement args)
    {
        try
        {
            RequireWorld(world);
            var em = world.EntityManager;
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs == null)
                throw new InvalidOperationException($"PrefabCollectionSystem is not in world '{world.Name}' yet; wait until the world has finished booting");

            var guid = ResolvePrefabGuid(prefabs, args);
            if (!prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out var prefabEntity) || prefabEntity == Entity.Null)
                throw new InvalidOperationException($"prefab {guid._Value} is not in _PrefabGuidToEntityMap. Check the name/hash with query on PrefabGUID, or wait until PrefabCollectionSystem has registered.");

            if (!em.Exists(prefabEntity))
                throw new InvalidOperationException($"prefab {guid._Value} maps to {FormatEntity(prefabEntity)} which does not exist");

            var names = ListComponentNames(em, prefabEntity);
            var component = PrefabInspectArgs.OptionalComponent(args);
            Dictionary<string, object?>? fields = null;
            if (component != null)
                fields = DumpComponent(em, prefabEntity, component).Fields;

            return PrefabInspectArgs.Result(
                guid._Value,
                TryPrefabName(prefabs, guid),
                FormatEntity(prefabEntity),
                names,
                fields);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"prefab inspect failed: {e}");
            throw new InvalidOperationException($"prefab inspect failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// PlayerCharacter entities, including disabled (offline) ones. The display name lives on User,
    /// reached through PlayerCharacter.UserEntity — the character component's own Name can lag.
    /// </summary>
    public static object Players(World world, JsonElement args)
    {
        RequireWorld(world);
        _ = args;
        var em = world.EntityManager;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new ComponentType(Il2CppType.Of<PlayerCharacter>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            var list = new List<object>(entities.Length);
            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                string? name = null;
                bool? connected = null;
                string? userEntity = null;
                try
                {
                    var pc = em.GetComponentData<PlayerCharacter>(entity);
                    userEntity = FormatEntity(pc.UserEntity);
                    if (em.Exists(pc.UserEntity) && em.HasComponent(pc.UserEntity, CachedUserType()))
                    {
                        var user = em.GetComponentData<User>(pc.UserEntity);
                        name = user.CharacterName.ToString();
                        connected = user.IsConnected;
                    }
                    else
                    {
                        name = pc.Name.ToString();
                    }
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"players: failed reading PlayerCharacter {FormatEntity(entity)}: {e.Message}");
                    name ??= $"<error: {e.Message}>";
                }

                var position = TryPosition(em, entity);
                list.Add(new
                {
                    entity = FormatEntity(entity),
                    name,
                    connected,
                    userEntity,
                    position = position.HasValue ? PositionObject(position.Value) : null,
                });
            }

            return new { count = list.Count, players = list };
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }
    }

    // ---- component dump ----------------------------------------------------------------------

    internal sealed class DumpedComponent
    {
        public string Entity = "";
        public string Component = "";
        public int Size;
        public Dictionary<string, object?> Fields = new(StringComparer.Ordinal);
    }

    internal static List<string> ListComponentNames(EntityManager em, Entity entity)
    {
        var types = em.GetComponentTypes(entity, Allocator.Temp);
        try
        {
            var names = new List<string>(types.Length);
            for (int i = 0; i < types.Length; i++)
            {
                var ct = types[i];
                string name;
                try
                {
                    var managed = ct.GetManagedType();
                    name = managed != null
                        ? (managed.FullName ?? managed.Name ?? ct.ToString())
                        : ct.ToString();
                }
                catch (Exception e)
                {
                    name = $"<unresolved TypeIndex {ct.TypeIndex}: {e.Message}>";
                }
                names.Add(name);
            }
            return names;
        }
        finally
        {
            types.Dispose();
        }
    }

    internal static DumpedComponent DumpComponent(EntityManager em, Entity entity, string componentName)
    {
        var componentType = ResolveComponentType(componentName);
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {FormatEntity(entity)} does not exist");
        if (!em.HasComponent(entity, componentType))
            throw new InvalidOperationException($"entity {FormatEntity(entity)} has no component '{componentName}'. Call 'components' on this entity to list what it has.");

        EnsureIComponentData(componentType, componentName);

        var managed = FindInteropStruct(componentName);
        var typeIndex = componentType.TypeIndex;
        var info = TypeManager.GetTypeInfo(typeIndex);
        int size = ComponentDump.SizeOf(managed, info.TypeSize);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (size > 0)
        {
            byte* raw = (byte*)em.GetComponentDataRawRO(entity, typeIndex);
            if (raw == null)
                throw new InvalidOperationException($"GetComponentDataRawRO returned null for '{componentName}' on {FormatEntity(entity)}");
            foreach (var spec in ComponentDump.FieldsOf(managed, size))
                fields[spec.Name] = ComponentDump.ReadField(raw, spec);
        }

        return new DumpedComponent
        {
            Entity = FormatEntity(entity),
            Component = managed.FullName ?? managed.Name ?? componentName,
            Size = size,
            Fields = fields,
        };
    }

    // ---- type index --------------------------------------------------------------------------

    private static Il2CppSystem.Type ResolveIl2CppType(string name)
    {
        var managed = FindInteropStruct(name);
        RuntimeHelpers.RunClassConstructor(managed.TypeHandle);
        try
        {
            return Il2CppType.From(managed);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"'{managed.FullName}' has no IL2CPP class pointer: {e.Message}");
        }
    }

    internal static Type FindInteropStruct(string name)
    {
        EnsureTypeIndex();
        lock (IndexLock)
        {
            if (_byFullName!.TryGetValue(name, out var exact)) return exact;
            if (_bySimpleName!.TryGetValue(name, out var list))
            {
                if (list.Count == 1) return list[0];
                var options = new List<string>(list.Count);
                foreach (var t in list) options.Add(t.FullName ?? t.Name);
                throw new ArgumentException(
                    $"component name '{name}' is ambiguous. Pass a full name. Candidates: {string.Join(", ", options)}");
            }
        }

        throw new ArgumentException(
            $"no interop struct named '{name}'. Searched assemblies: {string.Join(", ", InteropAssemblyNames)}. Use a simple name (PlayerCharacter) or full name (ProjectM.PlayerCharacter).");
    }

    private static void EnsureTypeIndex()
    {
        lock (IndexLock)
        {
            if (_byFullName != null) return;
            _byFullName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            _bySimpleName = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);

            var wanted = new HashSet<string>(InteropAssemblyNames, StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string assemblyName;
                try { assemblyName = assembly.GetName().Name ?? ""; }
                catch { continue; }
                if (!wanted.Contains(assemblyName)) continue;

                Type?[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }

                if (types == null) continue;
                foreach (var type in types)
                {
                    if (type == null || !type.IsValueType || type.IsEnum || type.IsPrimitive || type.IsPointer) continue;
                    if (type.IsGenericType) continue;
                    if (string.IsNullOrEmpty(type.Name) || type.Name.StartsWith("<", StringComparison.Ordinal)) continue;

                    if (type.FullName != null) _byFullName[type.FullName] = type;
                    if (!_bySimpleName.TryGetValue(type.Name, out var list))
                    {
                        list = new List<Type>(1);
                        _bySimpleName[type.Name] = list;
                    }
                    bool already = false;
                    foreach (var existing in list)
                        if (existing == type) { already = true; break; }
                    if (!already) list.Add(type);
                }
            }
        }
    }

    private static void EnsureIComponentData(ComponentType componentType, string name)
    {
        try
        {
            if (componentType.IsBuffer)
                throw new InvalidOperationException($"'{name}' is a buffer, not IComponentData. get/set walk a single component blob; use a dedicated buffer op.");
            if (componentType.IsManagedComponent)
                throw new InvalidOperationException($"'{name}' is a managed component. There is no raw blob to dump with GetComponentDataRawRO.");
            if (componentType.IsSharedComponent)
                throw new InvalidOperationException($"'{name}' is a shared component. get/set only handle per-entity IComponentData.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"component kind check for '{name}' failed (continuing): {e.Message}");
        }
    }

    // ---- prefabs / positions -----------------------------------------------------------------

    internal static PrefabCollectionSystem? TryPrefabCollection(World world)
    {
        try { return world.GetExistingSystemManaged<PrefabCollectionSystem>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabCollectionSystem unavailable in '{world.Name}': {e.Message}");
            return null;
        }
    }

    internal static PrefabGUID ResolvePrefabGuid(PrefabCollectionSystem prefabs, JsonElement args)
    {
        var key = PrefabInspectArgs.Parse(args);
        if (key.IsHash)
            return new PrefabGUID(key.Hash);

        var text = key.Name;
        PrefabGUID byName = default;
        try
        {
            if (prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(text, out byName, true) && byName._Value != 0)
                return byName;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"TryGetPrefabGuidWithName('{text}') failed: {e.Message}");
        }

        try
        {
            var dict = prefabs._SpawnableNameToPrefabGuidDictionary;
            if (dict != null && dict.ContainsKey(text))
                return dict[text];
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"_SpawnableNameToPrefabGuidDictionary['{text}'] failed: {e.Message}");
        }

        throw new ArgumentException(PrefabInspectArgs.UnknownNameError(text));
    }

    internal static object? TryPrefabInfo(EntityManager em, Entity entity, PrefabCollectionSystem? prefabs)
    {
        try
        {
            if (!em.HasComponent(entity, PrefabGuidType())) return null;
            var guid = em.GetComponentData<PrefabGUID>(entity);
            if (guid._Value == 0) return null;
            return new { hash = guid._Value, name = prefabs != null ? TryPrefabName(prefabs, guid) : null };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"prefab info for {FormatEntity(entity)} failed: {e.Message}");
            return null;
        }
    }

    internal static string? TryPrefabName(PrefabCollectionSystem prefabs, PrefabGUID guid)
    {
        try
        {
            return prefabs._PrefabLookupMap.TryGetName(guid, out var name) ? name : null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"TryGetName({guid._Value}) failed: {e.Message}");
            return null;
        }
    }

    internal static float3? TryPosition(EntityManager em, Entity entity)
    {
        try
        {
            if (em.HasComponent(entity, TranslationType()))
                return em.GetComponentData<Translation>(entity).Value;
            if (em.HasComponent(entity, LocalToWorldType()))
                return em.GetComponentData<LocalToWorld>(entity).Position;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"position of {FormatEntity(entity)} failed: {e.Message}");
        }
        return null;
    }

    internal static object PositionObject(float3 p) => new { x = p.x, y = p.y, z = p.z };

    internal static ComponentType TranslationType() => _translationType ??= new ComponentType(Il2CppType.Of<Translation>(), ComponentType.AccessMode.ReadWrite);
    private static ComponentType LocalToWorldType() => _localToWorldType ??= new ComponentType(Il2CppType.Of<LocalToWorld>(), ComponentType.AccessMode.ReadOnly);
    private static ComponentType PrefabGuidType() => _prefabGuidType ??= new ComponentType(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly);
    private static ComponentType CachedUserType() => _userType ??= new ComponentType(Il2CppType.Of<User>(), ComponentType.AccessMode.ReadOnly);

    // ---- args --------------------------------------------------------------------------------

    private static void RequireWorld(World world)
    {
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("world is null or not created; wait until ping reports a Server or Client_0 world");
    }

    private static string FormatEntity(Entity entity) => $"{entity.Index}:{entity.Version}";

    private static Entity ReadEntity(EntityManager em, JsonElement args, string name)
    {
        var text = ReadRequiredString(args, name);
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1
            || !int.TryParse(text.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            throw new ArgumentException($"args.{name} must be 'index:version' (got '{text}')");
        }

        var entity = new Entity { Index = index, Version = version };
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {text} does not exist in this world (destroyed, or the index:version is from a different world)");
        return entity;
    }

    private static string ReadRequiredString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"args.{name} is required (string)");
        var value = el.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"args.{name} is empty");
        return value;
    }

    private static int ReadInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return fallback;
    }

    private static bool ReadBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
        return fallback;
    }

    private static float ReadFloat(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var f)) return f;
        if (el.ValueKind == JsonValueKind.String && float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
        throw new ArgumentException($"args.{name} must be a number");
    }

    private static string[] ReadStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"args.{name} must be an array of strings");
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        }
        return list.ToArray();
    }

    private static NearFilter? ReadNear(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("near", out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args.near must be { x, y, z, radius }");
        return new NearFilter(
            ReadFloat(GetRequired(el, "x"), "near.x"),
            ReadFloat(GetRequired(el, "y"), "near.y"),
            ReadFloat(GetRequired(el, "z"), "near.z"),
            ReadFloat(GetRequired(el, "radius"), "near.radius"));
    }

    private static JsonElement GetRequired(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            throw new ArgumentException($"args.near.{name} is required");
        return el;
    }

    private static ComponentDump.FieldSpec? FindField(ComponentDump.FieldSpec[] specs, string name)
    {
        foreach (var spec in specs)
            if (string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase)) return spec;
        return null;
    }

    private static string FieldNames(ComponentDump.FieldSpec[] specs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < specs.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(specs[i].Name);
        }
        return sb.ToString();
    }

    private readonly struct NearFilter
    {
        public readonly float X, Y, Z, RadiusSq;
        public NearFilter(float x, float y, float z, float radius)
        {
            X = x; Y = y; Z = z;
            RadiusSq = radius * radius;
        }

        public bool Contains(float3 p)
        {
            float dx = p.x - X, dy = p.y - Y, dz = p.z - Z;
            return dx * dx + dy * dy + dz * dz <= RadiusSq;
        }
    }
}
