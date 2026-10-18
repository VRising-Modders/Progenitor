using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH2: the build menu's baked layout, read from the <c>BuildMenuCompositionBlob</c> bytes
/// (game-free, so it is tested without a client). Categories hold tags, tags hold structures, and a
/// structure is one build-menu cell: a group of blueprints (a group cell) or a single blueprint.
///
/// <para>Layout (client dump 170235-170264, strides from the mapper's own code; see
/// <c>layout.txt</c> "Build menu search"): every level is a Unity <c>BlobArray</c>
/// <c>{int offset; int length}</c> whose elements start at the header's own address plus the offset.
/// A category and a tag are one BlobArray each (8 bytes); a structure is <c>GroupPrefabGuid</c> then
/// <c>Blueprints</c> (12 bytes); a blueprint is <c>PrefabGUID</c>, a 16-byte name key and a 16-byte icon
/// key (36 bytes). Every array is bounds-checked against the blob length, so a malformed blob yields
/// <c>false</c>, never a read past the end.</para>
/// </summary>
internal sealed class BuildMenuBlob
{
    /// <summary>One cell of the build menu.</summary>
    internal readonly struct Structure
    {
        public Structure(int category, int tag, int index, int groupGuid, int[] blueprints, int[] blueprintAt)
        {
            Category = category;
            Tag = tag;
            Index = index;
            GroupGuid = groupGuid;
            Blueprints = blueprints;
            BlueprintAt = blueprintAt;
        }

        /// <summary>Absolute offset of each blueprint's record in the blob (copied whole into a results tag).</summary>
        public int[] BlueprintAt { get; }

        public int Category { get; }
        public int Tag { get; }

        /// <summary>Position inside its tag (the game's <c>Data.Index</c>).</summary>
        public int Index { get; }

        public int GroupGuid { get; }
        public int[] Blueprints { get; }

        /// <summary>A group cell: more than one blueprint. A one-blueprint structure is shown as the item.</summary>
        public bool IsGroup => Blueprints.Length > 1;
    }

    private BuildMenuBlob(int[][] tagCounts, int[][] tagHeaderAt, List<Structure> structures)
    {
        TagCounts = tagCounts;
        TagHeaderAt = tagHeaderAt;
        Structures = structures;
    }

    /// <summary>Per category and tag, the absolute offset of the tag's <c>Structures</c> BlobArray header.</summary>
    internal int[][] TagHeaderAt { get; }

    /// <summary>Per category, the number of structures in each tag.</summary>
    internal int[][] TagCounts { get; }

    internal int CategoryCount => TagCounts.Length;

    internal IReadOnlyList<Structure> Structures { get; }

    /// <summary>Hard limits a sane composition stays far below; past them the blob is treated as unreadable.</summary>
    internal const int MaxCategories = 64;
    internal const int MaxTags = 64;
    internal const int MaxStructures = 4096;
    internal const int MaxBlueprints = 256;

    internal static bool TryParse(
        ReadOnlySpan<byte> blob, int categoriesAt, int tagsAt, int structuresAt,
        int groupAt, int blueprintsAt, int structureStride, int blueprintGuidAt, int blueprintStride,
        int arrayWidth, out BuildMenuBlob? parsed, out string error)
    {
        parsed = null;
        error = "";
        if (!TryArray(blob, categoriesAt, arrayWidth, arrayWidth, out int catStart, out int catCount) || catCount > MaxCategories)
        {
            error = "categories array is unreadable";
            return false;
        }

        var tagCounts = new int[catCount][];
        var tagHeaders = new int[catCount][];
        var structures = new List<Structure>();
        for (int c = 0; c < catCount; c++)
        {
            int catAt = catStart + c * arrayWidth;
            if (!TryArray(blob, catAt + tagsAt, arrayWidth, arrayWidth, out int tagStart, out int tagCount) || tagCount > MaxTags)
            {
                error = "tags of category " + c + " are unreadable";
                return false;
            }

            tagCounts[c] = new int[tagCount];
            tagHeaders[c] = new int[tagCount];
            for (int t = 0; t < tagCount; t++)
            {
                int tagAt = tagStart + t * arrayWidth;
                tagHeaders[c][t] = tagAt + structuresAt;
                if (!TryArray(blob, tagAt + structuresAt, structureStride, arrayWidth, out int sStart, out int sCount)
                    || structures.Count + sCount > MaxStructures)
                {
                    error = "structures of category " + c + " tag " + t + " are unreadable";
                    return false;
                }

                tagCounts[c][t] = sCount;
                for (int s = 0; s < sCount; s++)
                {
                    int sAt = sStart + s * structureStride;
                    int group = I32(blob, sAt + groupAt);
                    if (!TryArray(blob, sAt + blueprintsAt, blueprintStride, arrayWidth, out int bStart, out int bCount)
                        || bCount > MaxBlueprints)
                    {
                        error = "blueprints of category " + c + " tag " + t + " structure " + s + " are unreadable";
                        return false;
                    }

                    var blueprints = new int[bCount];
                    var at = new int[bCount];
                    for (int b = 0; b < bCount; b++)
                    {
                        at[b] = bStart + b * blueprintStride;
                        blueprints[b] = I32(blob, at[b] + blueprintGuidAt);
                    }

                    structures.Add(new Structure(c, t, s, group, blueprints, at));
                }
            }
        }

        parsed = new BuildMenuBlob(tagCounts, tagHeaders, structures);
        return true;
    }

    /// <summary>One cell of a results tag: a group GUID and the blueprints (by record offset) it shows.</summary>
    internal readonly struct ResultCell
    {
        public ResultCell(int groupGuid, int[] blueprintAt)
        {
            GroupGuid = groupGuid;
            BlueprintAt = blueprintAt;
        }

        public int GroupGuid { get; }
        public int[] BlueprintAt { get; }
    }

    /// <summary>Appended arrays start on this boundary (Unity bakes blob arrays at least 4-aligned).</summary>
    internal const int AppendAlignment = 16;

    /// <summary>
    /// BUILDSEARCH3: a copy of <paramref name="original"/> in which the tag at
    /// (<paramref name="category"/>, <paramref name="tag"/>) lists <paramref name="cells"/> instead of its
    /// own structures. Every original byte keeps its offset (so every other relative offset stays valid);
    /// the cells and their blueprint records are appended and only that tag's <c>Structures</c> header is
    /// rewritten. Blueprint records are copied whole (GUID, name key, icon key).
    /// </summary>
    internal byte[] ComposeTag(
        ReadOnlySpan<byte> original, int category, int tag, IReadOnlyList<ResultCell> cells,
        int groupAt, int blueprintsAt, int structureStride, int blueprintStride, int arrayWidth)
    {
        if (category < 0 || category >= TagHeaderAt.Length || tag < 0 || tag >= TagHeaderAt[category].Length)
            throw new ArgumentOutOfRangeException(nameof(tag), "no such category or tag");
        if (cells == null || cells.Count == 0) throw new ArgumentException("no cells", nameof(cells));

        static int Align(int n) => (n + AppendAlignment - 1) / AppendAlignment * AppendAlignment;

        int structStart = Align(original.Length);
        int cursor = structStart + cells.Count * structureStride;
        var blueprintStart = new int[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            cursor = Align(cursor);
            blueprintStart[i] = cursor;
            cursor += cells[i].BlueprintAt.Length * blueprintStride;
        }

        var bytes = new byte[Align(cursor)];
        original.CopyTo(bytes);
        var span = bytes.AsSpan();
        for (int i = 0; i < cells.Count; i++)
        {
            int sAt = structStart + i * structureStride;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(sAt + groupAt, 4), cells[i].GroupGuid);
            int header = sAt + blueprintsAt;
            var at = cells[i].BlueprintAt;
            WriteArray(span, header, blueprintStart[i], at.Length);
            for (int b = 0; b < at.Length; b++)
            {
                if (at[b] < 0 || at[b] + blueprintStride > original.Length)
                    throw new ArgumentOutOfRangeException(nameof(cells), "a blueprint record lies outside the blob");
                original.Slice(at[b], blueprintStride).CopyTo(span.Slice(blueprintStart[i] + b * blueprintStride, blueprintStride));
            }
        }

        WriteArray(span, TagHeaderAt[category][tag], structStart, cells.Count);
        _ = arrayWidth;
        return bytes;
    }

    private static void WriteArray(Span<byte> blob, int headerAt, int start, int length)
    {
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(headerAt, 4), length == 0 ? 0 : start - headerAt);
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(headerAt + 4, 4), length);
    }

    /// <summary>Absolute start and length of the BlobArray whose header sits at <paramref name="at"/>.</summary>
    internal static bool TryArray(ReadOnlySpan<byte> blob, int at, int elementSize, int arrayWidth, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (at < 0 || at + arrayWidth > blob.Length || arrayWidth < 8) return false;
        int rel = I32(blob, at);
        int len = I32(blob, at + sizeof(int));
        if (len < 0) return false;
        if (len == 0)
        {
            start = at;
            return true;
        }

        long s = (long)at + rel;
        long end = s + (long)len * elementSize;
        if (s < 0 || end > blob.Length) return false;
        start = (int)s;
        length = len;
        return true;
    }

    private static int I32(ReadOnlySpan<byte> blob, int at) =>
        at < 0 || at + 4 > blob.Length ? 0 : BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(at, 4));
}

/// <summary>
/// BUILDSEARCH2: where a blueprint or group lives in the build menu, as the breadcrumb shows it
/// (game-free). Built from a <see cref="BuildMenuBlob"/> plus the localized category, tag and group names.
/// </summary>
internal sealed class BuildMenuPlaces
{
    internal readonly struct Place
    {
        public Place(int category, int tag, int structure, bool inGroup, int groupGuid)
        {
            Category = category;
            Tag = tag;
            Structure = structure;
            InGroup = inGroup;
            GroupGuid = groupGuid;
        }

        public int Category { get; }
        public int Tag { get; }

        /// <summary>Index into <see cref="BuildMenuBlob.Structures"/>.</summary>
        public int Structure { get; }

        /// <summary>A blueprint inside a group cell (the crumb adds the group's name).</summary>
        public bool InGroup { get; }

        public int GroupGuid { get; }
    }

    private readonly Dictionary<int, List<Place>> _byGuid = new();

    internal BuildMenuPlaces(BuildMenuBlob blob)
    {
        Blob = blob;
        for (int i = 0; i < blob.Structures.Count; i++)
        {
            var s = blob.Structures[i];
            if (s.IsGroup && s.GroupGuid != 0) Add(s.GroupGuid, new Place(s.Category, s.Tag, i, inGroup: false, s.GroupGuid));
            for (int b = 0; b < s.Blueprints.Length; b++)
            {
                int guid = s.Blueprints[b];
                if (guid != 0) Add(guid, new Place(s.Category, s.Tag, i, inGroup: s.IsGroup, s.GroupGuid));
            }
        }
    }

    internal BuildMenuBlob Blob { get; }

    private void Add(int guid, Place place)
    {
        if (!_byGuid.TryGetValue(guid, out var list)) _byGuid[guid] = list = new List<Place>(1);
        list.Add(place);
    }

    /// <summary>
    /// Where <paramref name="guid"/> is, preferring the selected category and tag when it appears in
    /// several (a blueprint can be listed in more than one tag).
    /// </summary>
    internal bool TryFind(int guid, int selectedCategory, int selectedTag, out Place place)
    {
        place = default;
        if (!_byGuid.TryGetValue(guid, out var list) || list.Count == 0) return false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Category == selectedCategory && list[i].Tag == selectedTag)
            {
                place = list[i];
                return true;
            }
        }

        place = list[0];
        return true;
    }

    /// <summary>"Category > Tag", plus "> Group" for an item inside a group. Blank names are skipped.</summary>
    internal static string Crumb(string? category, string? tag, string? group, bool inGroup)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(category)) parts.Add(category!.Trim());
        if (!string.IsNullOrWhiteSpace(tag)) parts.Add(tag!.Trim());
        if (inGroup && !string.IsNullOrWhiteSpace(group)) parts.Add(group!.Trim());
        return string.Join(" > ", parts);
    }
}

/// <summary>
/// BUILDSEARCH3: which cells a query shows across the whole build menu (game-free).
///
/// <para>Per structure: when the group's name matches every word, the whole group is shown as it is.
/// Otherwise the blueprints whose own names match are kept: none, the structure is dropped; one, it
/// is shown as that single item; several, as a group cell holding only those. A cell that repeats one
/// already chosen (a blueprint listed in two tags) is shown once. Order is the menu's own order
/// (category, tag, position); the game sorts the row again anyway.</para>
/// </summary>
internal static class BuildSearchResults
{
    /// <summary>
    /// The most cells the row is given. The game rebuilds every cell every frame (costs, inventory, icons,
    /// limits) and its cell pool only grows, so a one-letter query must not hand it the whole menu.
    /// </summary>
    internal const int MaxCells = 48;

    /// <summary>Shortest query (in characters, spaces excluded) that searches every tab.</summary>
    internal const int MinQueryChars = 2;

    internal static List<BuildMenuBlob.ResultCell> Select(
        BuildMenuBlob blob, IReadOnlyList<string> words,
        Func<int, string> blueprintText, Func<int, string> groupText, int maxCells = MaxCells) =>
        Select(blob, words, blueprintText, groupText, out _, maxCells);

    /// <summary>
    /// As <see cref="Select(BuildMenuBlob, IReadOnlyList{string}, Func{int, string}, Func{int, string}, int)"/>; <paramref name="matched"/>
    /// counts every matching cell, shown or not. The cell holding <paramref name="preferGuid"/> (Copy building's item) is
    /// always given, even past the cap: it takes the last place.
    /// </summary>
    internal static List<BuildMenuBlob.ResultCell> Select(
        BuildMenuBlob blob, IReadOnlyList<string> words,
        Func<int, string> blueprintText, Func<int, string> groupText, out int matched, int maxCells = MaxCells, int preferGuid = 0)
    {
        matched = 0;
        bool preferShown = false;
        var cells = new List<BuildMenuBlob.ResultCell>();
        if (words == null || words.Count == 0) return cells;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new List<int>(8);
        foreach (var s in blob.Structures)
        {
            keep.Clear();
            bool wholeGroup = s.IsGroup && s.GroupGuid != 0 && BuildSearchMatch.Matches(words, groupText(s.GroupGuid));
            for (int b = 0; b < s.Blueprints.Length; b++)
            {
                if (wholeGroup || BuildSearchMatch.Matches(words, blueprintText(s.Blueprints[b]))) keep.Add(b);
            }

            if (keep.Count == 0) continue;
            var guids = new int[keep.Count];
            var at = new int[keep.Count];
            for (int i = 0; i < keep.Count; i++)
            {
                guids[i] = s.Blueprints[keep[i]];
                at[i] = s.BlueprintAt[keep[i]];
            }

            string key = s.GroupGuid + ":" + string.Join(",", guids);
            if (!seen.Add(key)) continue;
            matched++;
            bool preferred = preferGuid != 0 && Array.IndexOf(guids, preferGuid) >= 0;
            if (cells.Count < maxCells)
            {
                cells.Add(new BuildMenuBlob.ResultCell(s.GroupGuid, at));
                preferShown |= preferred;
            }
            else if (preferred && !preferShown && maxCells > 0)
            {
                cells[maxCells - 1] = new BuildMenuBlob.ResultCell(s.GroupGuid, at);
                preferShown = true;
            }
        }

        return cells;
    }
}
