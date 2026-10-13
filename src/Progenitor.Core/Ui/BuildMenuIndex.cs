#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ai;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.UI;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH2: the whole build menu as data, for the breadcrumb and the all-tabs search. Read once per
/// composition from the game's own baked layout and cached.
///
/// <para><b>Where the data comes from.</b> <c>BuildMenuMapper._CompositionPrefabGuid</c> names the
/// composition prefab; its <c>BuildMenuComposition</c> component (client dump 170275) holds the blob
/// reference, read raw through the component's type index (no generic over a game value type). The
/// blob is copied out under its own header length and walked by <see cref="BuildMenuBlob"/>, every array
/// bounds-checked. Names come from managed data (reference types only): category and tag names from
/// <c>ManagedBuildMenuCompositionData</c> (the arrays parallel the blob's, as the mapper's own
/// <c>TryGetManagedTagData</c> indexes them), group names from <c>ManagedBuildMenuGroupData</c>,
/// blueprint names from <c>ManagedBlueprintData</c>.</para>
/// </summary>
internal sealed class BuildMenuIndex
{
    private readonly string[] _categoryNames;
    private readonly string[][] _tagNames;
    private readonly Dictionary<int, string> _groupNames = new();
    private readonly World _world;

    private BuildMenuIndex(World world, int compositionGuid, IntPtr blobData, BuildMenuBlob blob,
        string[] categoryNames, string[][] tagNames)
    {
        _world = world;
        CompositionGuid = compositionGuid;
        BlobData = blobData;
        Blob = blob;
        Places = new BuildMenuPlaces(blob);
        _categoryNames = categoryNames;
        _tagNames = tagNames;
    }

    internal int CompositionGuid { get; }
    internal IntPtr BlobData { get; }
    internal BuildMenuBlob Blob { get; }
    internal BuildMenuPlaces Places { get; }

    internal string CategoryName(int category) =>
        category >= 0 && category < _categoryNames.Length ? _categoryNames[category] : "";

    internal string TagName(int category, int tag) =>
        category >= 0 && category < _tagNames.Length && tag >= 0 && tag < _tagNames[category].Length
            ? _tagNames[category][tag]
            : "";

    internal string GroupName(int groupGuid)
    {
        if (groupGuid == 0) return "";
        if (_groupNames.TryGetValue(groupGuid, out var cached)) return cached;
        string name = "";
        try
        {
            var registry = _world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedBuildMenuGroupData>(new PrefabGUID(groupGuid), out var data)
                && data != null && Localization.Localization.TryGetGameString(data.Name, out var s))
                name = s ?? "";
        }
        catch { name = ""; }
        // A blank name is not cached: localization can come up after the first read.
        if (name.Length > 0) _groupNames[groupGuid] = name;
        return name;
    }

    /// <summary>The crumb for <paramref name="guid"/> (a group or a blueprint), or "" when it is not in the menu.</summary>
    internal string CrumbFor(int guid, int selectedCategory, int selectedTag)
    {
        if (!Places.TryFind(guid, selectedCategory, selectedTag, out var place)) return "";
        return BuildMenuPlaces.Crumb(
            CategoryName(place.Category), TagName(place.Category, place.Tag),
            place.InGroup ? GroupName(place.GroupGuid) : null, place.InGroup);
    }

    // ---- building ---------------------------------------------------------------------------------

    private static BuildMenuIndex? _cached;
    private static string _lastFailure = "";

    internal static string LastFailure => _lastFailure;

    internal static void Forget()
    {
        _cached = null;
        _entityWorld = null;
        _typeKnown = false;
    }

    /// <summary>The index for this mapper's composition, built on first use and reused while the blob is the same.</summary>
    private static World? _entityWorld;
    private static int _entityGuid;
    private static Entity _entity;
    private static bool _typeKnown;
    private static ComponentType _type;

    /// <summary>The composition entity, looked up once per world and composition (and again if it is gone).</summary>
    private static bool TryEntity(World world, int guid, out Entity entity)
    {
        var em = world.EntityManager;
        if (ReferenceEquals(_entityWorld, world) && _entityGuid == guid && em.Exists(_entity))
        {
            entity = _entity;
            return true;
        }

        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(guid), out entity) || !em.Exists(entity)) return false;
        _entityWorld = world;
        _entityGuid = guid;
        _entity = entity;
        return true;
    }

    internal static unsafe BuildMenuIndex? For(World world, BuildMenuMapper mapper)
    {
        int guid = mapper._CompositionPrefabGuid.GuidHash;
        var em = world.EntityManager;
        if (!TryEntity(world, guid, out var entity))
            return Fail("the composition prefab " + guid + " is not in the prefab map");

        if (!_typeKnown)
        {
            _type = ComponentIndex.Of<BuildMenuComposition>(ComponentType.AccessMode.ReadOnly);
            _typeKnown = true;
        }

        var type = _type;
        if (!em.HasComponent(entity, type)) return Fail("the composition prefab has no BuildMenuComposition");
        byte* component = (byte*)em.GetComponentDataRawRO(entity, type.TypeIndex);
        if (component == null) return Fail("BuildMenuComposition read returned null");
        byte* data = *(byte**)(component + Layout.BuildMenuCompositionBlobOffset);
        if (data == null) return Fail("the composition blob reference is null");
        // While the search shows its results the slot holds Progenitor's copy: index the game's own blob.
        data = (byte*)BuildMenuSwap.Resolve((IntPtr)data);

        var cached = _cached;
        if (cached != null && ReferenceEquals(cached._world, world) && cached.CompositionGuid == guid && cached.BlobData == (IntPtr)data)
            return cached;

        // The header in front of the data names it (ValidationPtr) and gives its length.
        byte* header = data - Layout.BlobAssetHeaderBytes;
        if (*(long*)(header + Layout.BlobAssetHeaderValidationPtrOffset) != (long)data)
            return Fail("the composition blob header does not name its data");
        int length = AiRuntime.BlobLength(data);
        if (length <= 0) return Fail("the composition blob length is unreadable");

        var bytes = new ReadOnlySpan<byte>(data, length).ToArray();
        int structureStride = Layout.BuildMenuStructureBlueprintsOffset + Bytes.BlobArrayWidth;
        if (!BuildMenuBlob.TryParse(bytes,
                Layout.BuildMenuCompositionCategoriesOffset, Layout.BuildMenuCategoryTagsOffset,
                Layout.BuildMenuTagStructuresOffset, Layout.BuildMenuStructureGroupGuidOffset,
                Layout.BuildMenuStructureBlueprintsOffset, structureStride,
                Layout.BuildMenuBlueprintGuidOffset, Layout.BuildMenuBlueprintStride,
                Bytes.BlobArrayWidth, out var blob, out var error))
            return Fail("the composition blob could not be walked: " + error);

        ReadNames(world, guid, blob!, out var categories, out var tags);
        var index = new BuildMenuIndex(world, guid, (IntPtr)data, blob!, categories, tags);
        _cached = index;
        _lastFailure = "";
        Plugin.Logger?.LogInfo("BuildSearch: indexed the build menu (" + blob!.CategoryCount + " categories, "
            + blob.Structures.Count + " cells, " + length + " blob bytes).");
        return index;
    }

    private static void ReadNames(World world, int guid, BuildMenuBlob blob, out string[] categories, out string[][] tags)
    {
        categories = new string[blob.CategoryCount];
        tags = new string[blob.CategoryCount][];
        for (int c = 0; c < blob.CategoryCount; c++)
        {
            categories[c] = "";
            tags[c] = new string[blob.TagCounts[c].Length];
            for (int t = 0; t < tags[c].Length; t++) tags[c][t] = "";
        }

        try
        {
            var registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry;
            if (registry == null || !registry.TryGet<ManagedBuildMenuCompositionData>(new PrefabGUID(guid), out var managed)
                || managed == null)
                return;
            var catDatas = managed.CategoryDatas;
            int n = catDatas == null ? 0 : Math.Min(catDatas.Length, categories.Length);
            for (int c = 0; c < n; c++)
            {
                var cat = catDatas![c];
                if (cat == null) continue;
                if (Localization.Localization.TryGetGameString(cat.Name, out var cn)) categories[c] = cn ?? "";
                var tagDatas = cat.TagDatas;
                int m = tagDatas == null ? 0 : Math.Min(tagDatas.Length, tags[c].Length);
                for (int t = 0; t < m; t++)
                {
                    var tag = tagDatas![t];
                    if (tag != null && Localization.Localization.TryGetGameString(tag.Name, out var tn)) tags[c][t] = tn ?? "";
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("BuildSearch: category and tag names could not be read: " + e.Message);
        }
    }

    private static BuildMenuIndex? Fail(string why)
    {
        if (!string.Equals(why, _lastFailure, StringComparison.Ordinal))
        {
            _lastFailure = why;
            Plugin.Logger?.LogWarning("BuildSearch: build menu index unavailable: " + why);
        }

        return null;
    }
}
#endif
