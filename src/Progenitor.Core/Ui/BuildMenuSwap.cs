#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ai;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.UI;
using Stunlock.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH3: search results from every tab, shown by the game itself.
///
/// <para><b>How.</b> The build menu builds its row every frame from the composition blob's selected tag
/// (<c>BuildMenuMapper.UpdateBlueprintRequirements</c>, research build-menu-mapper-natives-2026-10-04.md
/// section 1). While a query is active, the composition prefab's <c>BuildMenuComposition.Blob</c> pointer is
/// swapped, before the mapper's update, to a private copy of the blob whose selected tag lists the result
/// cells (<see cref="BuildMenuBlob.ComposeTag"/>). The game then makes the cells, icons, costs, limits,
/// tooltips, selection and placement exactly as for any tag, and no native method is patched or called.
/// The original pointer is written back when the query clears, the menu closes, the search is turned off,
/// the world changes or Core unloads; the write checks the slot still holds our copy first.</para>
///
/// <para><b>Memory.</b> A copy is allocated Persistent and freed two seconds after it stops being the
/// one in the slot (the mapper reads the blob only on the main thread, during its own update), and all of
/// them on Core unload after the restore.</para>
/// </summary>
internal static unsafe class BuildMenuSwap
{
    private const int FreeDelayFrames = 120;

    private sealed class Copy
    {
        public byte* Block;
        public byte* Data;
        public int FreeAtFrame = int.MaxValue;

        /// <summary>The slot could not be confirmed free of this copy: it is never freed (a leak, not a use after free).</summary>
        public bool Pinned;
    }

    private static readonly List<Copy> Copies = new();
    private static World? _world;
    private static Entity _entity;
    private static IntPtr _original;
    private static Copy? _live;
    private static string _liveKey = "";
    private static int _resultCells;

    /// <summary>The composition's own blob while a copy is in its slot, else zero.</summary>
    internal static IntPtr Original => _live != null ? _original : IntPtr.Zero;

    internal static bool Active => _live != null;

    internal static int ResultCells => _live != null ? _resultCells : 0;

    internal static string LiveKey => _liveKey;

    /// <summary>The blob the index should be built from: the original when <paramref name="data"/> is our copy.</summary>
    internal static IntPtr Resolve(IntPtr data) => _live != null && (IntPtr)_live.Data == data ? _original : data;

    /// <summary>
    /// Make the selected tag show <paramref name="cells"/> (a non-empty list), keyed so an unchanged
    /// query and tab reuse the copy in the slot. Returns false (and leaves the original) when the slot
    /// cannot be written safely.
    /// </summary>
    internal static bool Show(World world, BuildMenuMapper mapper, BuildMenuIndex index, int category, int tag,
        List<BuildMenuBlob.ResultCell> cells, string key, int frame)
    {
        if (cells.Count == 0) return false;
        if (_live != null && string.Equals(_liveKey, key, StringComparison.Ordinal) && ReferenceEquals(_world, world)) return true;

        byte* slot = Slot(world, mapper, out var entity);
        if (slot == null) return false;
        // A copy in another world's or entity's slot goes back first, so it is never retired while still in use.
        if (_live != null && (!ReferenceEquals(_world, world) || _entity != entity))
        {
            Restore(frame, "slot changed");
            if (_live != null) return false;
        }

        byte* current = *(byte**)slot;
        byte* original = _live != null && current == _live.Data ? (byte*)_original : current;
        if (original == null || (IntPtr)original != index.BlobData) return false;

        int length = AiRuntime.BlobLength(original);
        if (length <= 0) return false;
        var bytes = new ReadOnlySpan<byte>(original, length);
        byte[] composed;
        try
        {
            composed = index.Blob.ComposeTag(bytes, category, tag, cells,
                Layout.BuildMenuStructureGroupGuidOffset, Layout.BuildMenuStructureBlueprintsOffset,
                Layout.BuildMenuStructureBlueprintsOffset + Bytes.BlobArrayWidth, Layout.BuildMenuBlueprintStride,
                Bytes.BlobArrayWidth);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("BuildSearch: results could not be composed: " + e.Message);
            return false;
        }

        var copy = Allocate(original, composed);
        *(byte**)slot = copy.Data;
        Retire(_live, frame);
        _live = copy;
        _liveKey = key;
        _world = world;
        _entity = entity;
        _original = (IntPtr)original;
        _resultCells = cells.Count;
        return true;
    }

    /// <summary>
    /// Put the composition's own blob back. True when the slot held our copy and now holds the original
    /// (the row will change under the selection, so the caller resets it). The copy is retired only when
    /// the slot is confirmed not to hold it any more (written back, read back, or its world or entity gone);
    /// otherwise it is pinned and never freed, a small leak instead of a read of freed memory.
    /// </summary>
    internal static bool Restore(int frame, string why)
    {
        if (_live == null) return false;
        bool wrote = false;
        bool confirmed = false;
        try
        {
            var world = _world;
            if (world == null || !world.IsCreated)
            {
                confirmed = true;
            }
            else
            {
                var em = world.EntityManager;
                var type = ComponentIndex.Of<BuildMenuComposition>(ComponentType.AccessMode.ReadWrite);
                if (!em.Exists(_entity) || !em.HasComponent(_entity, type))
                {
                    confirmed = true;
                }
                else
                {
                    byte* component = (byte*)em.GetComponentDataRawRW(_entity, type.TypeIndex);
                    byte* slot = component == null ? null : component + Layout.BuildMenuCompositionBlobOffset;
                    if (slot != null)
                    {
                        if (*(byte**)slot == _live.Data)
                        {
                            *(byte**)slot = (byte*)_original;
                            wrote = true;
                        }

                        confirmed = *(byte**)slot != _live.Data;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("BuildSearch: restoring the build menu blob threw (" + why + "): " + e.Message);
        }

        if (confirmed) Retire(_live, frame);
        else _live.Pinned = true;
        _live = null;
        _liveKey = "";
        _resultCells = 0;
        return wrote;
    }

    /// <summary>Free copies whose delay ran out. Called every frame.</summary>
    internal static void Sweep(int frame)
    {
        for (int i = Copies.Count - 1; i >= 0; i--)
        {
            var c = Copies[i];
            if (c == _live || c.Pinned || frame < c.FreeAtFrame) continue;
            Free(c);
            Copies.RemoveAt(i);
        }
    }

    /// <summary>Core unload: restore, then free every copy now.</summary>
    internal static void Release(int frame)
    {
        Restore(frame, "unload");
        for (int i = Copies.Count - 1; i >= 0; i--)
        {
            if (Copies[i].Pinned) continue;
            Free(Copies[i]);
            Copies.RemoveAt(i);
        }

        if (Copies.Count > 0)
            Plugin.Logger?.LogWarning("BuildSearch: " + Copies.Count + " build menu blob copy(ies) could not be confirmed out of use and are kept for the process.");
        Copies.Clear();
        _world = null;
    }

    internal static int CopyCount => Copies.Count;

    private static byte* Slot(World world, BuildMenuMapper mapper, out Entity entity)
    {
        entity = default;
        var em = world.EntityManager;
        if (!PrefabLookup.TryGetEntity(world, mapper._CompositionPrefabGuid, out entity) || !em.Exists(entity)) return null;
        var type = ComponentIndex.Of<BuildMenuComposition>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(entity, type)) return null;
        byte* component = (byte*)em.GetComponentDataRawRW(entity, type.TypeIndex);
        return component == null ? null : component + Layout.BuildMenuCompositionBlobOffset;
    }

    private static Copy Allocate(byte* template, byte[] data)
    {
        int header = Layout.BlobAssetHeaderBytes;
        byte* block = (byte*)UnsafeUtility.Malloc(data.Length + header, 16, Allocator.Persistent);
        if (block == null) throw new InvalidOperationException("blob allocation failed");
        new ReadOnlySpan<byte>(template - header, header).CopyTo(new Span<byte>(block, header));
        byte* copy = block + header;
        data.AsSpan().CopyTo(new Span<byte>(copy, data.Length));
        *(void**)(block + Layout.BlobAssetHeaderValidationPtrOffset) = copy;
        *(int*)(block + Layout.BlobAssetHeaderLengthOffset) = data.Length;
        var c = new Copy { Block = block, Data = copy };
        Copies.Add(c);
        return c;
    }

    private static void Retire(Copy? c, int frame)
    {
        if (c != null) c.FreeAtFrame = frame + FreeDelayFrames;
    }

    private static void Free(Copy c)
    {
        if (c.Block == null) return;
        try { UnsafeUtility.Free(c.Block, Allocator.Persistent); } catch { /* */ }
        c.Block = null;
        c.Data = null;
    }
}
#endif
