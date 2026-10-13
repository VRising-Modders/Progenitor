using System;
using System.Text;
using Il2CppInterop.Runtime;
using ProjectM;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;

namespace Progenitor.Content;

/// <summary>
/// Raw access to the game's <see cref="ItemKeyLookup"/> singleton: the ordered list of item GUIDs
/// whose index is what inventory serialization sends, plus the reverse map.
///
/// Generic interop over this type (<c>GetSingleton&lt;ItemKeyLookup&gt;</c>,
/// <c>NativeList&lt;PrefabGUID&gt;.Add</c>, <c>NativeHashMap&lt;PrefabGUID,int&gt;.Add</c>) hard-crashes
/// when the IL2CPP instantiation is missing. The containers are therefore read and written through
/// their Collections 2.x layouts, which spike 2 verified against a live server and client.
///
/// The list itself lives in ECS and survives a collectible Core ALC swap. Which rows this
/// process owns (cloned / appended / tombstones) is a managed snapshot in Bootstrap
/// <c>ContentMemory</c>, not a scan of this table: vanilla count is not stored.
/// </summary>
internal static unsafe class ItemKeyTable
{
    // Overlay structs: fields are populated by reading game memory, never assigned from C#.
#pragma warning disable CS0649
    // Unity.Collections.LowLevel.Unsafe.UnsafeList<T>
    private struct UnsafeListRaw
    {
        public void* Ptr;
        public int Length;
        public int Capacity;
        public ushort AllocatorIndex;
        public ushort AllocatorVersion;
    }

    // Unity.Collections.LowLevel.Unsafe.HashMapHelper<TKey>, field order taken from the interop.
    private struct HashMapHelperRaw
    {
        public byte* Values;
        public int* Keys;
        public int* Next;
        public int* Buckets;
        public int Count;
        public int Capacity;
        public int Log2MinGrowth;
        public int BucketCapacity;
        public int AllocatedIndex;
        public int FirstFreeIdx;
        public int SizeOfTValue;
        public ushort AllocatorIndex;
        public ushort AllocatorVersion;
    }

    // ProjectM.ItemKeyLookup: NativeList at 0 (one pointer), NativeHashMap at 8 (one pointer).
    private struct ItemKeyLookupRaw
    {
        public UnsafeListRaw* Items;
        public HashMapHelperRaw* ItemToIndex;
    }
#pragma warning restore CS0649

    private static EntityQuery BuildQuery(EntityManager em)
    {
        // The singleton lives on an unmanaged system entity; a default query never sees it.
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new ComponentType(Il2CppType.Of<ItemKeyLookup>(), ComponentType.AccessMode.ReadWrite))
            .WithOptions(EntityQueryOptions.IncludeSystems);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        return query;
    }

    private static bool TryGetRaw(EntityManager em, out ItemKeyLookupRaw* raw, out string why)
    {
        raw = null;
        why = "";
        EntityQuery query = default;
        try
        {
            query = BuildQuery(em);
            int count = query.CalculateEntityCount();
            if (count != 1)
            {
                why = $"singleton count {count}";
                return false;
            }
            var entity = query.GetSingletonEntity();
            var typeIndex = TypeManager.GetTypeIndex(Il2CppType.Of<ItemKeyLookup>());
            raw = (ItemKeyLookupRaw*)em.GetComponentDataRawRW(entity, typeIndex);
            if (raw == null || raw->Items == null || raw->ItemToIndex == null)
            {
                why = "null container pointers";
                raw = null;
                return false;
            }
            var list = raw->Items;
            var map = raw->ItemToIndex;
            if (!PrefabWire.IsValidItemKeyOverlay(
                    list->Ptr != null,
                    map->Keys != null,
                    map->Values != null,
                    map->Next != null,
                    map->Buckets != null,
                    list->Length,
                    list->Capacity,
                    map->BucketCapacity,
                    map->SizeOfTValue,
                    out why))
            {
                raw = null;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            why = "EXC " + e.Message;
            raw = null;
            return false;
        }
        finally
        {
            if (!query.Equals(default(EntityQuery))) query.Dispose();
        }
    }

    private static int Hash(PrefabGUID guid) => guid.GuidHash;

    private static bool Find(HashMapHelperRaw* map, PrefabGUID guid, out int value, out int slot)
    {
        value = -1;
        slot = -1;
        if (map == null
            || map->Keys == null
            || map->Values == null
            || map->Next == null
            || map->Buckets == null
            || map->SizeOfTValue != 4
            || !PrefabWire.IsPowerOfTwo(map->BucketCapacity))
            return false;
        int bucket = Hash(guid) & (map->BucketCapacity - 1);
        int idx = map->Buckets[bucket];
        int guard = 0;
        while (idx >= 0 && idx < map->Capacity && guard++ < map->Capacity)
        {
            if (map->Keys[idx] == guid.GuidHash)
            {
                value = *(int*)(map->Values + (long)idx * map->SizeOfTValue);
                slot = idx;
                return true;
            }
            idx = map->Next[idx];
        }
        return false;
    }

    private static bool Find(HashMapHelperRaw* map, PrefabGUID guid, out int value) =>
        Find(map, guid, out value, out _);

    /// <summary>Index of <paramref name="guid"/> in the item key list, or -1 if absent / unreadable.</summary>
    public static int IndexOf(EntityManager em, PrefabGUID guid)
    {
        if (!TryGetRaw(em, out var raw, out _)) return -1;
        return Find(raw->ItemToIndex, guid, out var i) ? i : -1;
    }

    /// <summary>
    /// Copy the live list (guid hashes in index order) plus length/capacity. Used to send the
    /// server's authoritative tail on join. Fails closed when the overlay is unreadable.
    /// </summary>
    public static bool TrySnapshot(EntityManager em, out int length, out int capacity, out int[] hashes, out string why)
    {
        length = 0;
        capacity = 0;
        hashes = Array.Empty<int>();
        if (!TryGetRaw(em, out var raw, out why)) return false;
        var list = raw->Items;
        length = list->Length;
        capacity = Math.Min(list->Capacity, raw->ItemToIndex->Capacity);
        if (length < 0 || capacity < 0 || length > capacity)
        {
            why = $"list {length}/{capacity}";
            length = 0;
            capacity = 0;
            return false;
        }
        if (length == 0)
        {
            why = "ok";
            return true;
        }
        int* items = (int*)list->Ptr;
        if (items == null)
        {
            why = "null list pointer";
            length = 0;
            return false;
        }
        hashes = new int[length];
        for (int i = 0; i < length; i++)
            hashes[i] = items[i];
        why = "ok";
        return true;
    }

    /// <summary>Reserve storage without changing any GUID or index. Experimental, bounded to
    /// 16384 rows. Uses the game's tracked Persistent allocator; headers stay in place so
    /// existing NativeList/NativeHashMap copies retain their identity and normal disposal.</summary>
    internal static bool TryReserve(EntityManager em, int required, out string why)
    {
        why = "";
        if (required < 0 || required > ItemKeyCapacityPlan.MaximumCapacity)
        { why = "Requested item-key capacity exceeds the experimental bound."; return false; }
        // Copies held by jobs must finish before backing arrays can be replaced.
        em.CompleteAllTrackedJobs();
        if (!TryGetRaw(em, out var raw, out why)) return false;
        var list = raw->Items;
        var map = raw->ItemToIndex;
        if (required <= Math.Min(list->Capacity, map->Capacity)) return true;
        if (list->AllocatorIndex != (ushort)Allocator.Persistent || list->AllocatorVersion != 0
            || map->AllocatorIndex != (ushort)Allocator.Persistent || map->AllocatorVersion != 0
            || list->Capacity < 1 || list->Capacity > ItemKeyCapacityPlan.MaximumCapacity
            || map->Capacity < 1 || map->Capacity > ItemKeyCapacityPlan.MaximumCapacity
            || !PrefabWire.IsPowerOfTwo(list->Capacity) || !PrefabWire.IsPowerOfTwo(map->Capacity)
            || list->Length < 0 || list->Length > map->Capacity
            || map->Count != list->Length || map->AllocatedIndex != map->Count || map->FirstFreeIdx != -1
            || map->BucketCapacity != map->Capacity * 2
            || map->Keys != (int*)(map->Values + map->Capacity * 4)
            || map->Next != (int*)(map->Values + map->Capacity * 8)
            || map->Buckets != (int*)(map->Values + map->Capacity * 12))
        { why = "Item-key allocator or packed-map layout does not match the supported layout."; return false; }
        var hashes = new int[list->Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = ((int*)list->Ptr)[i];
            if (!Find(map, new PrefabGUID(hashes[i]), out int index) || index != i)
            { why = "Existing item-key list/map disagree."; return false; }
        }
        if (!ItemKeyCapacityPlan.TryBuild(hashes, list->Capacity, map->Capacity, required, out var plan, out why)) return false;
        if (plan == null) return true;
        void* newList = null;
        byte* newMap = null;
        bool committed = false;
        try
        {
            newList = UnsafeUtility.MallocTracked(plan.TargetCapacity * 4L, 64, Allocator.Persistent, 0);
            newMap = (byte*)UnsafeUtility.MallocTracked(plan.TargetCapacity * 20L, 64, Allocator.Persistent, 0);
            if (newList == null || newMap == null) throw new InvalidOperationException("Item-key allocation returned null.");
            for (int i = 0; i < hashes.Length; i++) ((int*)newList)[i] = hashes[i];
            for (int i = 0; i < plan.MapData.Length; i++) ((int*)newMap)[i] = plan.MapData[i];
            void* oldList = list->Ptr;
            byte* oldMap = map->Values;
            // Publish only after both allocations and all preparation succeed. No game calls
            // occur while the two headers are being updated on the main thread.
            list->Ptr = newList;
            list->Capacity = plan.TargetCapacity;
            map->Values = newMap;
            map->Keys = (int*)(newMap + plan.TargetCapacity * 4);
            map->Next = (int*)(newMap + plan.TargetCapacity * 8);
            map->Buckets = (int*)(newMap + plan.TargetCapacity * 12);
            map->Capacity = plan.TargetCapacity;
            map->BucketCapacity = plan.TargetCapacity * 2;
            committed = true;
            string cleanup = ReleaseTracked(oldList) + ReleaseTracked(oldMap);
            if (cleanup.Length != 0)
            { why = "Item-key storage expanded but old-storage cleanup failed: " + cleanup; return false; }
            why = "Reserved " + plan.TargetCapacity + " item-key rows; existing indices preserved.";
            return true;
        }
        catch (Exception e)
        {
            why = (committed ? "Item-key storage expanded but old-storage cleanup failed: " : "Item-key reservation failed: ") + e.Message;
            return false;
        }
        finally
        {
            if (!committed)
            {
                string cleanup = ReleaseTracked(newMap) + ReleaseTracked(newList);
                if (cleanup.Length != 0) why += "; temporary-storage cleanup failed: " + cleanup;
            }
        }
    }

    private static string ReleaseTracked(void* pointer)
    {
        if (pointer == null) return "";
        try { UnsafeUtility.FreeTracked(pointer, Allocator.Persistent); return ""; }
        catch (Exception e) { return e.Message + "; "; }
    }

    /// <summary>Length, capacity, and whether <paramref name="guid"/> is present, for diagnostics.</summary>
    public static string Describe(EntityManager em, PrefabGUID guid)
    {
        if (!TryGetRaw(em, out var raw, out var why))
            return $"itemKey table: unavailable ({why})";

        var list = raw->Items;
        var map = raw->ItemToIndex;
        var sb = new StringBuilder();
        sb.Append($"itemKey table: list {list->Length}/{list->Capacity}; map count {map->Count} cap {map->Capacity} buckets {map->BucketCapacity} alloc {map->AllocatedIndex} free {map->FirstFreeIdx} valueSize {map->SizeOfTValue}; ");

        int listIdx = -1;
        int* items = (int*)list->Ptr;
        if (items != null)
        {
            for (int i = 0; i < list->Length; i++)
            {
                if (items[i] == guid.GuidHash) { listIdx = i; break; }
            }
        }
        bool inMap = Find(map, guid, out var mapIdx);
        sb.Append($"guid in list at {listIdx}; map={(inMap ? mapIdx.ToString() : "-")}");
        if (inMap && listIdx != mapIdx)
            sb.Append(" (list/map mismatch)");
        return sb.ToString();
    }

    /// <summary>
    /// Appends the GUID to both containers in place. Refuses when either would need to grow: we
    /// do not call the native allocator (a guessed convention crashed a process during spike 2).
    /// Already-present is success.
    /// </summary>
    public static bool TryAppend(EntityManager em, PrefabGUID guid, out int index, out string why)
    {
        index = -1;
        if (!TryGetRaw(em, out var raw, out why)) return false;
        var list = raw->Items;
        var map = raw->ItemToIndex;

        if (Find(map, guid, out index))
        {
            why = "already present";
            return true;
        }
        if (list->Length >= list->Capacity)
        {
            why = $"list full ({list->Length}/{list->Capacity})";
            return false;
        }

        int slot;
        if (map->FirstFreeIdx >= 0)
        {
            slot = map->FirstFreeIdx;
            map->FirstFreeIdx = map->Next[slot];
        }
        else
        {
            if (map->AllocatedIndex >= map->Capacity)
            {
                why = $"map full ({map->AllocatedIndex}/{map->Capacity})";
                return false;
            }
            slot = map->AllocatedIndex++;
        }

        index = list->Length;
        ((int*)list->Ptr)[index] = guid.GuidHash;
        list->Length = index + 1;

        map->Keys[slot] = guid.GuidHash;
        *(int*)(map->Values + (long)slot * map->SizeOfTValue) = index;
        int bucket = Hash(guid) & (map->BucketCapacity - 1);
        map->Next[slot] = map->Buckets[bucket];
        map->Buckets[bucket] = slot;
        map->Count++;

        if (Find(map, guid, out var check) && check == index)
        {
            why = "ok";
            return true;
        }

        why = "written but lookup mismatch";
        try
        {
            UnlinkSlot(map, guid, slot);
            list->Length = index;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ItemKeyTable: mismatch rollback threw: {e.Message}");
        }
        index = -1;
        return false;
    }

    /// <summary>
    /// Removes the GUID from both containers. A last-element pop is index-stable for everything
    /// else; a middle remove shifts later entries and logs via <paramref name="why"/>. Missing is
    /// success. Compact-on-middle remaps every later inventory-slot index and will desync any
    /// connected client that already hashed the old order. Prefab unapply must not call this:
    /// the registry's ItemKeyLookup never shrinks, so a later Register of the same GUID reuses
    /// the index and a tombstone/placeholder keeps new joiners on the same count+hash.
    /// </summary>
    public static bool TryRemove(EntityManager em, PrefabGUID guid, out string why)
    {
        if (!TryGetRaw(em, out var raw, out why)) return false;
        var list = raw->Items;
        var map = raw->ItemToIndex;

        if (!Find(map, guid, out var index, out var slot))
        {
            why = "not present";
            return true;
        }
        if (index < 0 || index >= list->Length)
        {
            why = $"map index {index} outside list 0..{list->Length - 1}";
            return false;
        }

        UnlinkSlot(map, guid, slot);

        int* items = (int*)list->Ptr;
        int last = list->Length - 1;
        if (index != last)
        {
            for (int i = index; i < last; i++)
            {
                items[i] = items[i + 1];
                var moved = new PrefabGUID(items[i]);
                if (Find(map, moved, out _, out var movedSlot))
                    *(int*)(map->Values + (long)movedSlot * map->SizeOfTValue) = i;
            }
            why = $"removed at {index}; compacted {last - index} later index(es)";
        }
        else
        {
            why = "ok (popped last)";
        }
        list->Length = last;
        return true;
    }

    private static void UnlinkSlot(HashMapHelperRaw* map, PrefabGUID guid, int slot)
    {
        if (map == null
            || map->Next == null
            || map->Buckets == null
            || !PrefabWire.IsPowerOfTwo(map->BucketCapacity))
            return;
        int bucket = Hash(guid) & (map->BucketCapacity - 1);
        int prev = -1;
        int idx = map->Buckets[bucket];
        int guard = 0;
        while (idx >= 0 && idx < map->Capacity && guard++ < map->Capacity)
        {
            if (idx == slot)
            {
                int next = map->Next[idx];
                if (prev < 0) map->Buckets[bucket] = next;
                else map->Next[prev] = next;
                break;
            }
            prev = idx;
            idx = map->Next[idx];
        }
        map->Next[slot] = map->FirstFreeIdx;
        map->FirstFreeIdx = slot;
        map->Count--;
    }
}
