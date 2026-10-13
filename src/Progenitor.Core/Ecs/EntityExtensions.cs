using System;
using System.Text;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>
/// Crash-safe component access on a single entity. Reads and writes go through
/// <see cref="EntityManager.GetComponentDataRawRO"/> / <see cref="EntityManager.SetComponentDataRaw"/>
/// with an unmanaged-struct constraint and a runtime size check against
/// <c>TypeManager.GetTypeInfo(typeIndex).TypeSize</c> (the field the interop actually exposes;
/// <c>Unsafe.SizeOf&lt;T&gt;</c> is not used because <c>sizeof(T)</c> is already the managed
/// layout of an <c>unmanaged</c> T). <see cref="Write{T}"/> refuses TypeSize 0 except for
/// Translation (live <c>SetComponentDataRaw</c> of 12) or Follower with a known
/// <c>SizeInChunk &gt;= sizeof</c> (<see cref="RawWriteGuard"/>). Caller-chosen blobs must
/// use <see cref="WriteRaw"/>, which refuses TypeSize 0 or a TypeSize smaller than the write
/// (evidence §32).
///
/// Buffer access is omitted: <c>EntityManager.GetBuffer&lt;T&gt;</c> is a generic interop call
/// over the element type and will hard-crash when that instantiation does not exist. The one
/// proven case (<c>InventoryBuffer</c> in the prefab spike) is not a general guarantee.
/// </summary>
public static unsafe class EntityExtensions
{
    /// <summary>
    /// True when the entity is still allocated in <paramref name="em"/>.
    /// Generic-instantiation-safe: <see cref="EntityManager.Exists"/> is non-generic.
    /// </summary>
    public static bool Exists(this Entity entity, EntityManager em)
    {
        try
        {
            return em.Exists(entity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Entity.Exists({entity.Index}:{entity.Version}) failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// True when the entity exists and carries <typeparamref name="T"/>.
    /// Generic-instantiation-safe: uses <see cref="EntityManager.HasComponent(Entity, ComponentType)"/>,
    /// not <c>HasComponent&lt;T&gt;</c>.
    /// </summary>
    public static bool Has<T>(this Entity entity, EntityManager em)
    {
        try
        {
            return em.Exists(entity)
                   && em.HasComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Entity.Has<{typeof(T).Name}>({entity.Index}:{entity.Version}) failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// Copies <typeparamref name="T"/> out of the entity.
    /// Generic-instantiation-safe: <c>GetComponentDataRawRO</c> takes a <see cref="TypeIndex"/>;
    /// the copy is a managed <c>Buffer.MemoryCopy</c>. <typeparamref name="T"/> is constrained
    /// to <c>unmanaged</c> so a ValueType-derived interop wrapper cannot be passed. Throws if
    /// <c>sizeof(T)</c> does not match ECS <c>TypeSize</c> rather than over/under-copying.
    /// </summary>
    public static T Read<T>(this Entity entity, EntityManager em) where T : unmanaged
    {
        var typeIndex = ComponentIndex.IndexOf<T>();
        EnsureManagedSizeMatchesEcs<T>(typeIndex);
        EnsureAliveWith(entity, em, typeIndex, ComponentType.AccessMode.ReadOnly);

        void* src = em.GetComponentDataRawRO(entity, typeIndex);
        if (src == null)
            throw new InvalidOperationException($"GetComponentDataRawRO returned null for {typeof(T).FullName} on {entity.Index}:{entity.Version}.");

        T value = default;
        Buffer.MemoryCopy(src, &value, sizeof(T), sizeof(T));
        return value;
    }

    /// <summary>
    /// <see cref="Read{T}"/> that returns false when the entity is missing or lacks the component.
    /// Still throws on a size mismatch: that is a layout bug, not a "not found".
    /// Generic-instantiation-safe for the same reason as <see cref="Read{T}"/>.
    /// </summary>
    public static bool TryRead<T>(this Entity entity, EntityManager em, out T value) where T : unmanaged
    {
        value = default;
        var typeIndex = ComponentIndex.IndexOf<T>();
        EnsureManagedSizeMatchesEcs<T>(typeIndex);
        if (!em.Exists(entity))
            return false;
        if (!em.HasComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly)))
            return false;

        void* src = em.GetComponentDataRawRO(entity, typeIndex);
        if (src == null)
            return false;

        T copy = default;
        Buffer.MemoryCopy(src, &copy, sizeof(T), sizeof(T));
        value = copy;
        return true;
    }

    /// <summary>
    /// Copies <paramref name="value"/> onto the entity. The component must already be present;
    /// use <see cref="Add{T}(Entity, EntityManager, in T)"/> to add-and-set.
    /// Generic-instantiation-safe: <c>SetComponentDataRaw</c> takes a <see cref="TypeIndex"/>,
    /// a pointer, and a byte count — no IL2CPP generic. When interop <c>TypeSize</c> is 0 this
    /// refuses unless <typeparamref name="T"/> is Translation (live <c>SetComponentDataRaw</c>
    /// of sizeof 12) or Follower with a known <c>SizeInChunk &gt;= sizeof</c>. Unknown
    /// SizeInChunk on Follower is refused and the message names the typed pointer overlay
    /// (evidence §33). Guessed blobs must go through <see cref="WriteRaw"/>, which refuses
    /// TypeSize 0 for every type.
    /// </summary>
    public static void Write<T>(this Entity entity, EntityManager em, in T value) where T : unmanaged
    {
        var typeIndex = ComponentIndex.IndexOf<T>();
        EnsureTypedWriteSize<T>(typeIndex);
        EnsureAliveWith(entity, em, typeIndex, ComponentType.AccessMode.ReadWrite);

        T local = value;
        em.SetComponentDataRaw(entity, typeIndex, &local, sizeof(T));
    }

    /// <summary>
    /// <c>SetComponentDataRaw</c> with the TypeSize gate from evidence §32. Refuses to write
    /// when <c>TypeManager.GetTypeInfo(typeIndex).TypeSize</c> is 0 or smaller than
    /// <paramref name="size"/>, throwing <see cref="InvalidOperationException"/> that names
    /// <paramref name="typeName"/>. Call this instead of <c>em.SetComponentDataRaw</c> for a
    /// caller-chosen blob; typed <see cref="Write{T}"/> already has a sizeof match check.
    /// </summary>
    public static void WriteRaw(this Entity entity, EntityManager em, TypeIndex typeIndex, void* data, int size, string typeName)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(typeName))
            typeName = $"TypeIndex {typeIndex.Value}";

        int ecsSize;
        try
        {
            ecsSize = TypeManager.GetTypeInfo(typeIndex).TypeSize;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"Refusing SetComponentDataRaw of {typeName}: TypeManager.GetTypeInfo({typeIndex.Value}) failed ({e.GetType().Name}: {e.Message}).",
                e);
        }

        RawWriteGuard.EnsureWritable(ecsSize, size, typeName);
        EnsureAliveWith(entity, em, typeIndex, ComponentType.AccessMode.ReadWrite);
        em.SetComponentDataRaw(entity, typeIndex, data, size);
    }

    /// <summary>
    /// Adds the <typeparamref name="T"/> component (tag or data) if it is not already present.
    /// Generic-instantiation-safe: <see cref="EntityManager.AddComponent(Entity, ComponentType)"/>
    /// is non-generic. Zero-sized tags should use this overload, not the value-taking one —
    /// an empty C# struct is 1 byte and will fail the TypeSize check on Write.
    /// </summary>
    public static void Add<T>(this Entity entity, EntityManager em)
    {
        if (!em.Exists(entity))
            throw new InvalidOperationException($"Add<{typeof(T).Name}> on destroyed entity {entity.Index}:{entity.Version}.");
        em.AddComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite));
    }

    /// <summary>
    /// Adds <typeparamref name="T"/> if missing, then writes <paramref name="value"/>.
    /// Generic-instantiation-safe: structural change is non-generic <c>AddComponent</c>; the
    /// write is <see cref="Write{T}"/>. Not for zero-sized tags (see <see cref="Add{T}(Entity, EntityManager)"/>).
    /// </summary>
    public static void Add<T>(this Entity entity, EntityManager em, in T value) where T : unmanaged
    {
        entity.Add<T>(em);
        entity.Write(em, in value);
    }

    /// <summary>
    /// Removes <typeparamref name="T"/> if present. No-op when the entity is gone or lacks it.
    /// Generic-instantiation-safe: <see cref="EntityManager.RemoveComponent(Entity, ComponentType)"/>
    /// is non-generic.
    /// </summary>
    public static void Remove<T>(this Entity entity, EntityManager em)
    {
        if (!em.Exists(entity))
            return;
        em.RemoveComponent(entity, ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite));
    }

    /// <summary>
    /// Lists the entity's component type names for logs and the inspector.
    /// Generic-instantiation-safe: <see cref="EntityManager.GetComponentTypes(Entity, Allocator)"/>
    /// and <see cref="ComponentType.GetManagedType"/> are both non-generic. The native array is
    /// disposed before return.
    /// </summary>
    public static string Describe(this Entity entity, EntityManager em)
    {
        try
        {
            if (!em.Exists(entity))
                return $"{entity.Index}:{entity.Version} (not exists)";

            var types = em.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                var sb = new StringBuilder();
                sb.Append(entity.Index).Append(':').Append(entity.Version).Append(" [");
                for (int i = 0; i < types.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    string? name = null;
                    try { name = types[i].GetManagedType()?.Name; }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning($"Describe: GetManagedType failed for typeIndex {types[i].TypeIndex.Value}: {e.Message}");
                    }
                    sb.Append(string.IsNullOrEmpty(name) ? types[i].TypeIndex.Value.ToString() : name);
                }
                sb.Append(']');
                return sb.ToString();
            }
            finally
            {
                types.Dispose();
            }
        }
        catch (Exception e)
        {
            return $"{entity.Index}:{entity.Version} (describe failed: {e.GetType().Name}: {e.Message})";
        }
    }

    /// <summary>
    /// Compares the managed layout of <typeparamref name="T"/> to the ECS type table.
    /// <see cref="TypeManager.TypeInfo.TypeSize"/> is the field the interop exposes at offset 88;
    /// <see cref="TypeManager.GetTypeInfo(TypeIndex)"/> returns a ref into that native table
    /// (not a boxed copy), which is why reading <c>.TypeSize</c> is safe.
    ///
    /// Typed <see cref="Read{T}"/> still trusts <c>sizeof(T)</c> when TypeSize is 0 (trap 7:
    /// Translation). <see cref="Write{T}"/> does not: it goes through
    /// <see cref="EnsureTypedWriteSize{T}"/>. Caller-chosen blobs must use <see cref="WriteRaw"/>,
    /// which calls <see cref="RawWriteGuard.EnsureWritable"/> and refuses TypeSize 0 or a
    /// TypeSize smaller than the requested write (evidence §32).
    /// </summary>
    private static void EnsureManagedSizeMatchesEcs<T>(TypeIndex typeIndex) where T : unmanaged
    {
        int ecsSize = TypeManager.GetTypeInfo(typeIndex).TypeSize;
        int managedSize = sizeof(T);
        // Verified live: TypeInfo.TypeSize reads 0 through the interop for ordinary components such as
        // Translation, so it cannot be the authority for a typed sizeof(T) *read*. When it is 0 the
        // interop struct's own layout is trusted for Read; when it is nonzero it must agree.
        // Write does not take this branch (EnsureTypedWriteSize). WriteRaw refuses TypeSize 0.
        if (ecsSize == 0) return;
        if (ecsSize < managedSize)
        {
            RawWriteGuard.EnsureWritable(ecsSize, managedSize, typeof(T).FullName ?? typeof(T).Name);
        }
        if (ecsSize != managedSize)
        {
            throw new InvalidOperationException(
                $"Component {typeof(T).FullName} managed size {managedSize} does not match ECS TypeInfo.TypeSize {ecsSize}. " +
                "Refusing to copy to avoid memory corruption. Zero-sized ECS tags have TypeSize 0 while an empty C# struct is 1 byte — use Add<T>/Remove<T>/Has<T> for those.");
        }
    }

    /// <summary>
    /// TypeSize / SizeInChunk gate for <see cref="Write{T}"/>. TypeSize 0 is the §32 crash
    /// class unless T is Translation (live SetComponentDataRaw of 12) or Follower with a
    /// known SizeInChunk &gt;= sizeof. Unknown SizeInChunk on Follower refuses; the overlay
    /// path never calls SetComponentDataRaw.
    /// </summary>
    private static void EnsureTypedWriteSize<T>(TypeIndex typeIndex) where T : unmanaged
    {
        int ecsSize;
        int sizeInChunk;
        try
        {
            // Chained field reads match the proven TypeSize access. Copying the whole TypeInfo
            // through this interop is the same class of marshal as TypeSize reading 0.
            ecsSize = TypeManager.GetTypeInfo(typeIndex).TypeSize;
            sizeInChunk = TypeManager.GetTypeInfo(typeIndex).SizeInChunk;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"Refusing Write<{typeof(T).Name}>: TypeManager.GetTypeInfo({typeIndex.Value}) failed ({e.GetType().Name}: {e.Message}).",
                e);
        }

        RawWriteGuard.EnsureTypedWrite(
            ecsSize,
            sizeof(T),
            sizeInChunk,
            typeof(T).FullName ?? typeof(T).Name);
    }

    private static void EnsureAliveWith(Entity entity, EntityManager em, TypeIndex typeIndex, ComponentType.AccessMode access)
    {
        if (!em.Exists(entity))
            throw new InvalidOperationException($"Entity {entity.Index}:{entity.Version} does not exist.");
        var componentType = ComponentType.FromTypeIndex(typeIndex);
        componentType.AccessModeType = access;
        if (!em.HasComponent(entity, componentType))
            throw new InvalidOperationException($"Entity {entity.Index}:{entity.Version} has no component TypeIndex {typeIndex.Value}.");
    }
}
