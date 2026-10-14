using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProjectM;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;

namespace Progenitor.Content;

/// <summary>
/// Typed view of one prefab entity during an ordered edit pass. Reads and writes go through
/// the existing ECS helpers so a missing IL2CPP generic cannot hard-crash the process.
/// Editors may change component values (stats, recipe fields, icon references); they must
/// not retarget <see cref="PrefabGUID"/> or strip the Prefab tag — those are the clone's
/// identity and the ItemKeyLookup key. Identity writes are skipped and logged rather than
/// applied. <c>ItemData</c> writes also refresh <c>ItemHashLookupMap</c> so tooltips stay
/// in sync with the entity (the applicator does the same on clone / in-place update).
/// </summary>
public sealed class PrefabEditHandle
{
    private readonly List<string> _touched = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly PrefabEditBaselines? _baselines;
    private readonly PrefabEditWireLayer? _wire;
    private string _pluginId = "";
    private string? _identityRefused;
    private bool _structuralWarned;

    internal PrefabEditHandle(
        World world,
        Entity entity,
        PrefabGUID guid,
        PrefabEditBaselines? baselines = null,
        PrefabEditWireLayer? wire = null)
    {
        World = world;
        Entity = entity;
        Guid = guid;
        _baselines = baselines;
        _wire = wire;
    }

    /// <summary>World that owns <see cref="Entity"/>. Never null while an editor is running.</summary>
    public World World { get; }

    /// <summary>
    /// Entity manager used by <see cref="Has{T}"/> / <see cref="Write{T}"/> /
    /// <see cref="SetComponent{T}"/>. Editors that reach for this property to
    /// <c>AddComponent</c>, <c>RemoveComponent</c> or edit a buffer are unrestorable:
    /// only the typed write surface captures a baseline. Logs once per editor.
    /// </summary>
    public EntityManager EntityManager
    {
        get
        {
            WarnUnrestorableStructuralAccess();
            return Em;
        }
    }

    private EntityManager Em => World.EntityManager;

    /// <summary>The prefab entity (clone or vanilla) this editor is allowed to mutate.</summary>
    public Entity Entity { get; }

    /// <summary>GUID this handle was bound to. Writes of a different <see cref="PrefabGUID"/> are refused.</summary>
    public PrefabGUID Guid { get; }

    /// <summary>Component type names written by the current editor (identity types omitted).</summary>
    internal IReadOnlyList<string> Touched => _touched;

    /// <summary>Type name of the last refused identity write, or null.</summary>
    internal string? IdentityRefused => _identityRefused;

    internal void BeginEditor(string pluginId)
    {
        _pluginId = pluginId ?? "";
        _touched.Clear();
        _seen.Clear();
        _identityRefused = null;
        _structuralWarned = false;
    }

    private void WarnUnrestorableStructuralAccess()
    {
        if (_structuralWarned) return;
        _structuralWarned = true;
        Plugin.Logger?.LogWarning(PrefabEditLog.UnrestorableStructuralEdit(_pluginId, Guid.GuidHash));
    }

    internal string[] TakeTouches()
    {
        if (_touched.Count == 0) return Array.Empty<string>();
        return _touched.ToArray();
    }

    /// <summary>
    /// True when the prefab entity exists and carries <typeparamref name="T"/>.
    /// Generic-instantiation-safe: <see cref="EntityExtensions.Has{T}"/>.
    /// </summary>
    public bool Has<T>() => Entity.Has<T>(Em);

    /// <summary>
    /// Copy <typeparamref name="T"/> off the prefab. Does not count as a touch.
    /// Generic-instantiation-safe: <see cref="EntityExtensions.Read{T}"/>.
    /// </summary>
    public T Read<T>() where T : unmanaged => Entity.Read<T>(Em);

    /// <summary>
    /// <see cref="Read{T}"/> that returns false when the entity is missing or lacks the component.
    /// </summary>
    public bool TryRead<T>(out T value) where T : unmanaged =>
        Entity.TryRead(Em, out value);

    /// <summary>
    /// Write an unmanaged component. Refuses <see cref="PrefabGUID"/> and the Prefab tag
    /// (logged, not thrown) so an editor cannot retarget the clone or drop it from lookups.
    /// Counts as a touch of <typeparamref name="T"/> for conflict reporting.
    /// </summary>
    public void Write<T>(in T value) where T : unmanaged
    {
        if (RefuseIdentity(typeof(T))) return;
        CaptureBaseline<T>(typeof(T).Name);
        RegisterTypedReader<T>();
        Entity.Write(Em, in value);
        NoteTouch(typeof(T).Name);
        AfterValueWrite(typeof(T).Name);
    }

    /// <summary>
    /// <c>GetComponentData&lt;T&gt;</c> for interop structs that are not C# unmanaged
    /// (<c>ItemData</c> has that shape; the applicator already uses this path). Does not
    /// count as a touch. Prefer <see cref="Read{T}"/> when <typeparamref name="T"/> is
    /// <c>unmanaged</c>.
    /// </summary>
    public T GetComponent<T>() => Em.GetComponentData<T>(Entity);

    /// <summary>
    /// <c>SetComponentData&lt;T&gt;</c> for the same types as <see cref="GetComponent{T}"/>.
    /// Documented for <c>ItemData</c> (not C# <c>unmanaged</c>). Goes through
    /// <see cref="Progenitor.Ecs.RawWriteGuard.EnsureTypedWrite"/> the same way
    /// <see cref="Write{T}"/> does (R19 finding 12). Other types are refused with a Warn
    /// naming <c>Write&lt;T&gt;</c>.
    /// Refuses identity types. An <c>ItemData</c> write keeps <c>ItemTypeGUID</c> equal to
    /// <see cref="Guid"/> and refreshes <c>ItemHashLookupMap</c>.
    /// </summary>
    public void SetComponent<T>(T value)
    {
        if (RefuseIdentity(typeof(T))) return;

        string shortName = typeof(T).Name;
        string typeName = typeof(T).FullName ?? shortName;
        if (!PrefabEditWrite.IsKnownGoodSetComponent(shortName)
            && !PrefabEditWrite.IsKnownGoodSetComponent(typeName))
        {
            Plugin.Logger?.LogWarning(PrefabEditLog.SetComponentUseWrite(_pluginId, Guid.GuidHash, shortName));
            return;
        }

        try
        {
            var typeIndex = ComponentIndex.IndexOf<T>();
            int ecsSize = TypeManager.GetTypeInfo(typeIndex).TypeSize;
            int sizeInChunk = TypeManager.GetTypeInfo(typeIndex).SizeInChunk;
            int managedSize = PrefabEditWrite.ManagedSizeOf(typeof(T));
            PrefabEditWrite.EnsureTyped(ecsSize, managedSize, sizeInChunk, typeName);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError(PrefabEditLog.SetComponentGuardFailed(_pluginId, Guid.GuidHash, shortName, e));
            return;
        }

        CaptureManagedBaseline<T>(shortName);
        RegisterMarshalledReader<T>();
        Em.SetComponentData(Entity, value);
        NoteTouch(shortName);
        AfterValueWrite(shortName);
    }

    /// <summary>
    /// Remember the pre-write value of <typeparamref name="T"/> the first time any editor
    /// writes it in this Core generation, so removing that editor can put vanilla back.
    /// Built here because <typeparamref name="T"/> is only known at the write site: the
    /// callback closes over the value and the entity, never over a world or an entity
    /// manager (both are supplied again at restore time). Reads never capture; an identity
    /// write is refused before this runs; a component the entity does not carry is skipped
    /// (there is no vanilla value to put back, and the write itself will fail).
    /// </summary>
    private void CaptureBaseline<T>(string typeName) where T : unmanaged
    {
        var store = _baselines;
        if (!PrefabEditPass.NeedsCapture(store, Guid.GuidHash, typeName)) return;
        try
        {
            bool present = Entity.TryRead<T>(Em, out var original);
            var entity = Entity;
            PrefabEditPass.TryCapture(
                store, Guid.GuidHash, typeName, present, entity.Index, entity.Version, ctx =>
                {
                    if (ctx is not World world || !world.IsCreated) return;
                    entity.Write(world.EntityManager, in original);
                });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                $"PrefabRegistry: baseline capture of {typeName} on guid {Guid.GuidHash} failed: {e.Message}");
        }
    }

    /// <summary>
    /// <see cref="CaptureBaseline{T}"/> for the <c>GetComponentData</c> path (<c>ItemData</c>).
    /// The restore re-syncs <c>ItemHashLookupMap</c> and keeps <c>ItemTypeGUID</c> equal to
    /// <see cref="Guid"/>, exactly as a write does.
    /// </summary>
    private void CaptureManagedBaseline<T>(string typeName)
    {
        var store = _baselines;
        if (!PrefabEditPass.NeedsCapture(store, Guid.GuidHash, typeName)) return;
        try
        {
            bool present = Entity.Has<T>(Em);
            var original = present ? Em.GetComponentData<T>(Entity) : default!;
            var entity = Entity;
            var guid = Guid;
            PrefabEditPass.TryCapture(
                store, Guid.GuidHash, typeName, present, entity.Index, entity.Version, ctx =>
                {
                    if (ctx is not World world || !world.IsCreated) return;
                    world.EntityManager.SetComponentData(entity, original);
                    SyncItemData(world, entity, guid, typeName, out _);
                });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                $"PrefabRegistry: baseline capture of {typeName} on guid {Guid.GuidHash} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Register the <c>PrfE</c> reader for <typeparamref name="T"/> next to the baseline capture
    /// (job FEDW1). Built here for the same reason the baseline callback is: this is the only
    /// place <typeparamref name="T"/> is known. The closure captures the entity and nothing else
    /// — the world is supplied again at serialize time — and does a <em>fresh</em> typed read, so
    /// what goes on the wire is whatever the last editor left on the entity, not the value this
    /// particular write passed in. The bytes are the <b>marshaled</b> layout, not
    /// <c>sizeof(T)</c>: the receiver validates and parses the marshaled one, and FEDW1 shipped
    /// the other (R50 finding 10).
    /// </summary>
    private void RegisterTypedReader<T>() where T : unmanaged
    {
        var wire = _wire;
        if (wire == null) return;
        var entity = Entity;
        var type = typeof(T);
        int guidHash = Guid.GuidHash;
        string fullName = type.FullName ?? type.Name;
        if (!PrefabEditWireTypes.IsVouched(fullName))
        {
            // The wire-safe list is the network boundary (job FEDW4, R52 finding 1). The edit still
            // happens on this side; it just does not travel, and the operator is told once.
            PrefabEditWireApply.LogUnvouchedType(
                guidHash, fullName, PrefabEditWireTypes.NotVouchedReason(fullName));
            return;
        }
        wire.RegisterReader(guidHash, fullName, ctx =>
        {
            if (ctx is not World world || !world.IsCreated) return null;
            if (!entity.TryRead<T>(world.EntityManager, out var value)) return null;
            // Marshaled, not sizeof: the receiver validates and parses the marshaled layout, so
            // the wire has exactly one measure (R50 finding 10; PrefabEditWire.TryMarshalBytes).
            if (!PrefabEditWire.TryMarshalBytes(value, type, out var bytes, out string error))
            {
                Plugin.Logger?.LogWarning(
                    PrefabEditLog.EditLayerReaderFailed(guidHash, fullName, new InvalidOperationException(error)));
                return null;
            }
            return bytes;
        });
    }

    /// <summary>
    /// <see cref="RegisterTypedReader{T}"/> for the <c>GetComponentData</c> path
    /// (<c>ItemData</c>: bool fields, not C# <c>unmanaged</c>). Same marshaled layout as the
    /// typed reader — the wire has one measure — which is what the receiver's
    /// <c>Marshal.SizeOf</c> / <c>PtrToStructure</c> pair expects; both sides run the same
    /// generated interop assembly, so the layout is identical.
    /// </summary>
    private void RegisterMarshalledReader<T>()
    {
        var wire = _wire;
        if (wire == null) return;
        var entity = Entity;
        var type = typeof(T);
        int guidHash = Guid.GuidHash;
        string fullName = type.FullName ?? type.Name;
        if (!PrefabEditWireTypes.IsVouched(fullName))
        {
            PrefabEditWireApply.LogUnvouchedType(
                guidHash, fullName, PrefabEditWireTypes.NotVouchedReason(fullName));
            return;
        }
        wire.RegisterReader(guidHash, fullName, ctx =>
        {
            if (ctx is not World world || !world.IsCreated) return null;
            var em = world.EntityManager;
            if (!entity.Has<T>(em)) return null;
            var value = em.GetComponentData<T>(entity);
            if (!PrefabEditWire.TryMarshalBytes(value, type, out var bytes, out string error))
            {
                Plugin.Logger?.LogWarning(
                    PrefabEditLog.EditLayerReaderFailed(guidHash, fullName, new InvalidOperationException(error)));
                return null;
            }
            return bytes;
        });
    }

    private bool RefuseIdentity(Type type)
    {
        if (type != typeof(PrefabTag)
            && type != typeof(PrefabGUID)
            && !PrefabEditIdentity.IsIdentityType(type))
            return false;
        _identityRefused = type.Name;
        Plugin.Logger?.LogWarning(PrefabEditLog.IdentityRefused(_pluginId, Guid.GuidHash, type.Name));
        return true;
    }

    private void NoteTouch(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return;
        if (PrefabEditIdentity.IsIdentityType(typeName)) return;
        if (!_seen.Add(typeName)) return;
        _touched.Add(typeName);
    }

    private void AfterValueWrite(string typeName)
    {
        if (!SyncItemData(World, Entity, Guid, typeName, out bool retargeted)) return;
        if (!retargeted) return;
        _identityRefused = PrefabEditIdentity.ItemTypeGuidField;
        Plugin.Logger?.LogWarning(
            PrefabEditLog.IdentityRefused(_pluginId, Guid.GuidHash, PrefabEditIdentity.ItemTypeGuidField));
    }

    /// <summary>
    /// Keep <c>ItemTypeGUID</c> and <c>Entity</c> on the clone and refresh
    /// <c>ItemHashLookupMap</c> after an <c>ItemData</c> value landed on the entity.
    /// <c>TryAddInventoryItem</c> spawns <c>ItemData.Entity</c>. Shared by the write path and
    /// by a baseline restore, so putting vanilla back cannot leave the tooltip map pointing
    /// at edited data. False for any other component (nothing to do);
    /// <paramref name="retargeted"/> is true when <c>ItemTypeGUID</c> had to be forced back
    /// to <paramref name="guid"/>.
    /// </summary>
    private static bool SyncItemData(World world, Entity entity, PrefabGUID guid, string typeName, out bool retargeted)
    {
        retargeted = false;
        if (!string.Equals(typeName, "ItemData", StringComparison.Ordinal)) return false;
        // An ItemData value landed on the prefab: the item catalog (ctx.Items.Catalog) must rebuild.
        Progenitor.Items.ItemCatalogVersion.Bump();
        try
        {
            var em = world.EntityManager;
            var data = em.GetComponentData<ItemData>(entity);
            bool changed = false;
            if (data.ItemTypeGUID.GuidHash != guid.GuidHash)
            {
                data.ItemTypeGUID = guid;
                changed = true;
                retargeted = true;
            }
            // A later ItemData write must not put the donor prefab back into the map.
            // TryAddInventoryItem spawns ItemData.Entity.
            if (data.Entity != entity)
            {
                data.Entity = entity;
                changed = true;
            }
            if (changed)
                em.SetComponentData(entity, data);

            var gameData = world.GetExistingSystemManaged<GameDataSystem>();
            if (gameData == null) return true;
            gameData.ItemHashLookupMap.Remove(guid);
            gameData.ItemHashLookupMap.TryAdd(guid, data);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: ItemData sync after edit of guid {guid.GuidHash} threw: {e}");
            return false;
        }
    }

    /// <summary>
    /// <see cref="SyncItemData"/> for the <c>PrfE</c> apply path (job FEDW1): a server layer that
    /// writes <c>ItemData</c> must refresh <c>ItemHashLookupMap</c> and keep <c>ItemTypeGUID</c>
    /// on the clone exactly as a local edit does, or the tooltip and the entity disagree.
    /// </summary>
    internal static void SyncItemDataAfterWire(World world, Entity entity, PrefabGUID guid)
    {
        if (!SyncItemData(world, entity, guid, PrefabEditWrite.ItemDataTypeName, out bool retargeted)) return;
        if (!retargeted) return;
        Plugin.Logger?.LogWarning(
            PrefabEditLog.IdentityRefused("server layer", guid.GuidHash, PrefabEditIdentity.ItemTypeGuidField));
    }

    internal static PrefabEditHandle? TryBind(
        World world,
        PrefabGUID guid,
        PrefabEditBaselines? baselines = null,
        PrefabEditWireLayer? wire = null)
    {
        if (world == null || !world.IsCreated) return null;
        if (!PrefabLookup.TryGetEntity(world, guid, out var entity)) return null;
        try
        {
            if (!entity.Exists(world.EntityManager)) return null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: edit bind Exists({guid.GuidHash}) threw: {e}");
            return null;
        }

        return new PrefabEditHandle(world, entity, guid, baselines, wire);
    }
}
