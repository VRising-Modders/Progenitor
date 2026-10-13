using System;
using System.IO;
using Progenitor.Bootstrap;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the Bootstrap-owned buff-icon cache: key format, hit / miss /
/// release, apply-from-cache when the plugin asset root is missing, and recreate when
/// the cached texture is Unity-null (evidence 112 / 122).
/// </summary>
public class IconCacheTests
{
    private static readonly byte[] PngA = { 1, 2, 3, 4, 5 };
    private static readonly byte[] PngB = { 9, 8, 7, 6, 5, 4 };

    [Fact]
    public void FormatKey_is_plugin_path_and_sha()
    {
        string sha = IconCachePolicy.HashBytes(PngA);
        string key = IconCachePolicy.FormatKey("progenitor.sample", "icons/nodrain.png", sha);
        Assert.True(IconCachePolicy.TryParseKey(key, out var plugin, out var path, out var parsed));
        Assert.Equal("progenitor.sample", plugin);
        Assert.Equal("icons/nodrain.png", path);
        Assert.Equal(sha, parsed);
        Assert.Equal(key, PrefabIconPath.FormatCacheKey("progenitor.sample", @"icons\nodrain.png", PngA));
    }

    [Fact]
    public void Empty_plugin_id_becomes_unowned()
    {
        string key = IconCachePolicy.FormatKey(null, "a.png", "ab");
        Assert.True(IconCachePolicy.TryParseKey(key, out var plugin, out _, out _));
        Assert.Equal(IconCachePolicy.UnownedId, plugin);
        Assert.Equal(IconCachePolicy.UnownedId, IconCachePolicy.NormalizePluginId("  "));
    }

    [Fact]
    public void Decide_covers_hit_miss_create_recreate_replace()
    {
        Assert.Equal(IconCachePolicy.Resolve.Miss, IconCachePolicy.Decide(false, false, false, false));
        Assert.Equal(IconCachePolicy.Resolve.Create, IconCachePolicy.Decide(false, false, true, false));
        Assert.Equal(IconCachePolicy.Resolve.Replace, IconCachePolicy.Decide(false, false, true, true));
        Assert.Equal(IconCachePolicy.Resolve.Hit, IconCachePolicy.Decide(true, false, true, false));
        Assert.Equal(IconCachePolicy.Resolve.Recreate, IconCachePolicy.Decide(true, true, true, false));
        Assert.Equal(IconCachePolicy.Resolve.Miss, IconCachePolicy.Decide(true, true, false, false));
        Assert.True(IconCachePolicy.MustRecreate(textureIsUnityNull: true));
        Assert.False(IconCachePolicy.MustRecreate(textureIsUnityNull: false));
    }

    [Fact]
    public void Table_create_then_hit_then_release()
    {
        var table = new IconCacheTable();
        var created = table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key);
        Assert.Equal(IconCachePolicy.Resolve.Create, created);
        Assert.Equal(1, table.Count);
        Assert.False(string.IsNullOrEmpty(key));

        var hit = table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key2);
        Assert.Equal(IconCachePolicy.Resolve.Hit, hit);
        Assert.Equal(key, key2);
        Assert.Equal(1, table.Count);

        Assert.True(table.Release(key));
        Assert.Equal(0, table.Count);
        Assert.False(table.Release(key));
    }

    [Fact]
    public void Table_miss_without_bytes_then_hit_after_create()
    {
        var table = new IconCacheTable();
        Assert.False(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out _, out _, out _));

        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key);
        Assert.True(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out var found, out var bytes, out var dead));
        Assert.Equal(key, found);
        Assert.Equal(PngA, bytes);
        Assert.False(dead);
    }

    [Fact]
    public void Table_replace_on_new_hash_for_the_same_plugin_path()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var oldKey);
        var resolve = table.TryGetOrCreate("mod.a", "icons/foo.png", PngB, out var newKey);
        Assert.Equal(IconCachePolicy.Resolve.Replace, resolve);
        Assert.Equal(oldKey, table.LastReplacedKey);
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(1, table.Count);
        Assert.False(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out var current, out _, out _) && current == oldKey);
        Assert.True(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out current, out _, out _));
        Assert.Equal(newKey, current);
    }

    [Fact]
    public void Table_recreates_when_the_texture_is_marked_dead()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key);
        Assert.True(table.MarkTextureDead(key));
        Assert.True(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out _, out _, out var dead));
        Assert.True(dead);
        Assert.True(IconCachePolicy.MustRecreate(dead));

        var resolve = table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var again);
        Assert.Equal(IconCachePolicy.Resolve.Recreate, resolve);
        Assert.Equal(key, again);
        Assert.True(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out _, out _, out dead));
        Assert.False(dead);
    }

    [Fact]
    public void Table_release_plugin_drops_every_row_for_that_id()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out _);
        table.TryGetOrCreate("mod.a", "icons/bar.png", PngB, out _);
        table.TryGetOrCreate("mod.b", "icons/foo.png", PngA, out var keep);
        Assert.Equal(3, table.Count);
        Assert.Equal(2, table.ReleasePlugin("mod.a"));
        Assert.Equal(1, table.Count);
        Assert.True(table.TryGetWithoutBytes("mod.b", "icons/foo.png", out var found, out _, out _));
        Assert.Equal(keep, found);
        table.Clear();
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void File_removed_while_the_clone_lives_keeps_the_sprite_until_the_clone_is_gone()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key);
        Assert.True(table.Retain(key));
        Assert.Equal(1, table.RefsOf(key));
        Assert.False(IconCachePolicy.ShouldDestroyOnPluginRelease(table.RefsOf(key)));

        Assert.Equal(0, table.ReleasePlugin("mod.a"));
        Assert.Equal(1, table.Count);
        Assert.True(table.IsPendingDestroy(key));
        Assert.True(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out var found, out _, out _));
        Assert.Equal(key, found);

        Assert.True(table.ReleaseRef(key));
        Assert.Equal(0, table.Count);
        Assert.False(table.TryGetWithoutBytes("mod.a", "icons/foo.png", out _, out _, out _));
    }

    [Fact]
    public void Mixed_case_paths_share_one_row_and_non_guid_owners_use_the_assembly_name()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/A.png", PngA, out var upper);
        var hit = table.TryGetOrCreate("mod.a", "icons/a.png", PngA, out var lower);
        Assert.Equal(IconCachePolicy.Resolve.Hit, hit);
        Assert.Equal(upper, lower);
        Assert.Equal(1, table.Count);

        Assert.Equal("example.sample", IconCachePolicy.OwnerId("example.sample", "Progenitor.Sample"));
        // The cache id is normalized (lowercased) on both write and lookup, so Core may keep
        // passing the raw BepInEx GUID / assembly name.
        Assert.Equal("progenitor.sample", IconCachePolicy.OwnerId(null, "Progenitor.Sample"));
        Assert.Equal(IconCachePolicy.UnownedId, IconCachePolicy.OwnerId(null, null));
        Assert.Equal("Progenitor.Sample", ContentIconOwnership.OwnerId(null, "Progenitor.Sample"));
    }

    /// <summary>
    /// R27 finding 14b: the plugin id is case-normalized everywhere. The same PNG arriving once
    /// as <c>MyPlugin</c> and once as <c>myplugin</c> is ONE row, not a Replace that retires (or
    /// destroys, when refs happen to be 0) the pair the live clone is drawing; and a file
    /// removal reported with different casing really does release.
    ///
    /// Fails on the pre-FZ code: <c>NormalizePluginId</c> only trimmed and <c>_byKey</c> was
    /// <c>Ordinal</c>, so the second <c>TryGetOrCreate</c> returned <c>Replace</c> with
    /// <c>LastReplacedKey</c> set, and <c>ReleasePlugin("MyPlugin")</c> dropped 0 rows.
    /// </summary>
    [Fact]
    public void A_plugin_id_that_differs_only_in_case_is_the_same_cache_row()
    {
        var table = new IconCacheTable();
        var first = table.TryGetOrCreate("MyPlugin", "icons/foo.png", PngA, out var upperKey);
        Assert.Equal(IconCachePolicy.Resolve.Create, first);
        table.Retain(upperKey);

        var second = table.TryGetOrCreate("myplugin", "icons/foo.png", PngA, out var lowerKey);
        Assert.Equal(IconCachePolicy.Resolve.Hit, second);
        Assert.Equal(upperKey, lowerKey);
        Assert.Null(table.LastReplacedKey);
        Assert.Equal(1, table.Count);
        Assert.Equal(1, table.RefsOf(lowerKey));

        // File removal reported with yet another casing still finds and marks the row.
        table.ReleasePlugin("MYPLUGIN");
        Assert.True(table.IsPendingDestroy(upperKey));
        Assert.True(table.ReleaseRef(upperKey));
        Assert.Equal(0, table.Count);
    }

    /// <summary>
    /// R27 finding 14a: superseded pairs leave the retired list. Iterating on an icon PNG
    /// changes its SHA-256 on every save, so every save is a Replace; before FZ each one parked
    /// a <c>HideAndDontSave</c> Sprite+Texture2D pair that nothing ever released.
    ///
    /// Fails on the pre-FZ code: <c>RetiredIconTable</c> did not exist and the list in
    /// <c>IconCache</c> had no exit but <c>ClearAll</c> at process exit.
    /// </summary>
    [Fact]
    public void A_retired_pair_is_destroyed_when_its_last_reference_goes()
    {
        var retired = new RetiredIconTable();

        // Nothing references it: the caller must destroy it outright, not park it here.
        Assert.False(retired.Retire("k0", refs: 0, payload: "pair0"));
        Assert.Equal(0, retired.Count);

        Assert.True(retired.Retire("k1", refs: 2, payload: "pair1"));
        Assert.Equal(1, retired.Count);
        Assert.Equal(2, retired.RefsOf("k1"));

        Assert.False(retired.ReleaseRef("k1", out var notYet));
        Assert.Empty(notYet);
        Assert.Equal(1, retired.RefsOf("k1"));

        Assert.True(retired.ReleaseRef("K1", out var freed));   // case-insensitive, like the key table
        Assert.Equal(new object?[] { "pair1" }, freed);
        Assert.Equal(0, retired.Count);

        Assert.False(retired.ReleaseRef("k1", out _));
        Assert.False(retired.ReleaseRef(null, out _));
    }

    /// <summary>
    /// R28 finding 4b. The same canonical key is retired twice in an ordinary dev loop: save
    /// icon v2 (v1 retired), save it back to v1 (a fresh v1 pair is created under the same
    /// plugin+path+sha key), save v2 again (v1 retired a second time). Two rows for one key made
    /// every <c>ReleaseRef</c> land on the <em>older</em> row — it hit zero first and its pair
    /// was destroyed while its real holders were still drawing it, and the newer row's count
    /// never dropped at all, so it leaked until the cap destroyed it too.
    ///
    /// Fails on the pre-FZ2 code: <c>Retire</c> did a bare <c>_rows.Add(...)</c>, so
    /// <c>Count</c> here was 2 and the first <c>ReleaseRef</c> freed <c>pair-v1a</c> — the
    /// assertions on <c>Count</c>, <c>RefsOf</c> and the freed payloads all break.
    /// </summary>
    [Fact]
    public void Retire_merges_a_duplicate_key_instead_of_appending_a_second_row()
    {
        var retired = new RetiredIconTable();
        Assert.True(retired.Retire("k", refs: 1, payload: "pair-v1a"));
        Assert.True(retired.Retire("k", refs: 2, payload: "pair-v1b"));

        Assert.Equal(1, retired.Count);          // one row per key
        Assert.Equal(2, retired.PairCount);      // both Unity pairs are still held
        Assert.Equal(3, retired.RefsOf("k"));    // counts add

        // Two of the three holders let go: nothing is freed yet, so neither pair is destroyed
        // under whoever is still drawing it.
        Assert.False(retired.ReleaseRef("k", out var first));
        Assert.Empty(first);
        Assert.False(retired.ReleaseRef("k", out var second));
        Assert.Empty(second);
        Assert.Equal(1, retired.RefsOf("k"));

        // The last one does, and it frees every pair held under that key.
        Assert.True(retired.ReleaseRef("k", out var freed));
        Assert.Equal(new object?[] { "pair-v1a", "pair-v1b" }, freed);
        Assert.Equal(0, retired.Count);
        Assert.Equal(0, retired.PairCount);

        // A merge never records the same payload twice.
        var again = new RetiredIconTable();
        again.Retire("k", refs: 1, payload: "same");
        again.Retire("k", refs: 1, payload: "same");
        Assert.Equal(1, again.PairCount);
    }

    /// <summary>
    /// The cap is a bound on how much is held, not a license to destroy. Inside the cap it does
    /// nothing; the actual reclaim path is <see cref="RetiredIconTable.ReleaseRef"/>, which drops
    /// a row the moment its last holder lets go. <see cref="RetiredIconTable.TakeOverflow"/>'s
    /// zero-reference filter is what keeps those two facts compatible: it can only ever collect a
    /// row nothing is drawing, which is why the cap can be enforced at all without reintroducing
    /// the black square.
    /// </summary>
    [Fact]
    public void The_retired_list_is_capped_and_rows_leave_when_their_last_holder_releases()
    {
        var retired = new RetiredIconTable();
        for (int i = 0; i < IconCachePolicy.MaxRetiredPairs; i++)
            Assert.True(retired.Retire("k" + i, refs: 1, payload: "pair" + i));

        Assert.Empty(retired.TakeOverflow());
        Assert.False(retired.NoteCapExceeded());
        Assert.Equal(IconCachePolicy.MaxRetiredPairs, retired.Count);
        Assert.Equal(IconCachePolicy.MaxRetiredPairs, retired.PairCount);

        // Content releasing a clone is what frees a pair — immediately, oldest or not.
        Assert.True(retired.ReleaseRef("k0", out var freedFirst));
        Assert.Equal(new object?[] { "pair0" }, freedFirst);
        Assert.Equal(IconCachePolicy.MaxRetiredPairs - 1, retired.Count);
        Assert.Equal(0, retired.RefsOf("k0"));

        Assert.Equal(IconCachePolicy.MaxRetiredPairs - 1, retired.TakeAll().Count);
        Assert.Equal(0, retired.Count);

        // The cap is a ceiling on rows, not a count of anything else: filling it exactly must
        // stay quiet and one row past it must not. Driven from the constant, so it fails if the
        // ceiling and the overflow test ever disagree rather than if the number changes.
        var edge = new RetiredIconTable();
        for (int i = 0; i < IconCachePolicy.MaxRetiredPairs; i++)
            edge.Retire("e" + i, refs: 1, payload: "pairE" + i);
        Assert.False(edge.NoteCapExceeded());
        edge.Retire("e-over", refs: 1, payload: "pairE-over");
        Assert.True(edge.NoteCapExceeded());

        Assert.Equal(
            "icon cache retired list over cap; destroyed 2 oldest unreferenced pair(s) (cap 64)",
            IconCachePolicy.FormatRetiredCapped(2, IconCachePolicy.MaxRetiredPairs));
        Assert.Equal(
            "icon cache entry 'k' retired pair released; destroyed",
            IconCachePolicy.FormatRetiredFreed("k"));
    }

    /// <summary>
    /// R29 finding 10, the measurement. A dev loop that re-saves one icon is a chain of
    /// replacements of the same plugin+path: each one supersedes a pair a live clone is drawing,
    /// so each one retires a row that <see cref="RetiredIconTable.TakeOverflow"/> may never
    /// destroy. The bound is not the cap — in this loop every row over the cap is referenced, so
    /// the cap frees nothing — it is that a replacement hands the superseded holder on: whatever
    /// was drawing generation N−1's sprite is exactly what generation N is re-pointing at the new
    /// one, so N−1's retired row drops that reference and is destroyed at zero.
    ///
    /// 200 generations, one row. Fails on the pre-FZ3 code: <c>Retire</c> had no hand-off, every
    /// generation added a row nothing ever released, and this table finished at 199 rows and 199
    /// held Unity pairs with <c>TakeOverflow</c> unable to free a single one.
    /// </summary>
    [Fact]
    public void Two_hundred_replacements_of_one_icon_hold_exactly_one_retired_row()
    {
        const string index = "mod.a\nicons/foo.png";
        var retired = new RetiredIconTable();
        var destroyed = new System.Collections.Generic.List<object?>();

        for (int generation = 1; generation <= 200; generation++)
        {
            // Generation N's PNG supersedes generation N−1's: one clone was drawing it.
            Assert.True(retired.RetireReplaced(
                index, "key-v" + generation, refs: 1, payload: "pair-v" + generation,
                out var freed, out bool handedOn));
            destroyed.AddRange(freed);
            Assert.Equal(generation > 1, handedOn);

            Assert.Equal(1, retired.Count);
            Assert.Equal(1, retired.PairCount);
            Assert.Empty(retired.TakeOverflow());
            Assert.False(retired.NoteCapExceeded());
        }

        // Every superseded pair but the newest was destroyed the moment its holder moved on.
        Assert.Equal(199, destroyed.Count);
        Assert.Equal("pair-v199", destroyed[destroyed.Count - 1]);
        Assert.Equal(1, retired.RefsOf("key-v200"));
        Assert.DoesNotContain("pair-v200", destroyed);
    }

    /// <summary>
    /// The hand-off is a bookkeeping correction, not a license to destroy: two clones drawing the
    /// same superseded pair means one of them is still on screen after the replacement, and that
    /// pair must survive — the black square R28 finding 4a is about.
    /// </summary>
    [Fact]
    public void Handing_a_holder_on_never_destroys_a_pair_a_second_clone_still_draws()
    {
        const string index = "mod.a\nicons/foo.png";
        var retired = new RetiredIconTable();
        retired.RetireReplaced(index, "key-v1", refs: 2, payload: "pair-v1", out _, out _);

        Assert.True(retired.RetireReplaced(
            index, "key-v2", refs: 1, payload: "pair-v2", out var freed, out bool handedOn));
        Assert.True(handedOn);
        Assert.Empty(freed);                       // one holder moved on, one is still drawing it
        Assert.Equal(1, retired.RefsOf("key-v1"));
        Assert.Equal(2, retired.Count);

        // When that last holder does let go, the pair leaves immediately.
        Assert.True(retired.ReleaseRef("key-v1", out var late));
        Assert.Equal(new object?[] { "pair-v1" }, late);
        Assert.Equal(1, retired.Count);
    }

    /// <summary>
    /// The other half of R29 finding 10: a Core swap must retire nothing at all. The cache hands
    /// the same Sprite and Texture2D back to every generation for an unchanged PNG, so there is
    /// no superseded pair to park — <c>LastReplacedKey</c> stays null and
    /// <see cref="IconCachePolicy.ShouldRetireOnReplace"/> refuses even a positive refcount.
    /// Retiring per generation instead of per replacement is what made the table an accumulator
    /// whose rows were "referenced" by generations that had already unloaded.
    ///
    /// Fails on the pre-FZ3 code: <c>ShouldRetireOnReplace</c> did not exist and nothing stated
    /// that a swap is not a retire seam.
    /// </summary>
    [Fact]
    public void A_core_swap_of_an_unchanged_png_replaces_nothing_and_retires_nothing()
    {
        var table = new IconCacheTable();
        Assert.Equal(IconCachePolicy.Resolve.Create, table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var first));
        table.Retain(first);

        for (int generation = 0; generation < 200; generation++)
        {
            // The new Core generation re-applies the same definition from the same PNG.
            var resolve = table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var again);
            Assert.Equal(IconCachePolicy.Resolve.Hit, resolve);
            Assert.Equal(first, again);
            Assert.Null(table.LastReplacedKey);
            Assert.Equal(1, table.Count);
        }

        // Even with a live reference, "no live row was replaced" means "do not retire".
        Assert.False(IconCachePolicy.ShouldRetireOnReplace(liveRowReplaced: false, refs: 3));
        Assert.True(IconCachePolicy.ShouldRetireOnReplace(liveRowReplaced: true, refs: 3));
        Assert.False(IconCachePolicy.ShouldRetireOnReplace(liveRowReplaced: true, refs: 0));

        // A real PNG change is the seam, and it is the only one.
        Assert.Equal(IconCachePolicy.Resolve.Replace, table.TryGetOrCreate("mod.a", "icons/foo.png", PngB, out _));
        Assert.Equal(first, table.LastReplacedKey);
        Assert.Equal(1, table.LastReplacedRefs);
    }

    /// <summary>
    /// R28 finding 4a: the cap used to pop <c>_rows[0]</c> with no refcount test, and
    /// <c>IconCache.SweepRetiredNoLock</c> then destroyed that Sprite and Texture2D. A row is in
    /// this table precisely because <c>ShouldRetire(refs)</c> said <c>refs &gt; 0</c> — a live
    /// clone is drawing it — so the cap reintroduced the black square it exists to prevent, at a
    /// bound. Edit an icon 17 times while a buff instance holds each generation and the first
    /// one's sprite went out from under the clone.
    ///
    /// Fails on the pre-FZ2 code: <c>TakeOverflow</c> returned <c>pair0</c> and <c>pair1</c>
    /// here (both referenced), the table dropped back to the cap, and <c>NoteCapExceeded</c> did
    /// not exist.
    /// </summary>
    [Fact]
    public void The_cap_never_destroys_a_pair_a_clone_is_still_drawing()
    {
        var retired = new RetiredIconTable();
        for (int i = 0; i < IconCachePolicy.MaxRetiredPairs + 2; i++)
            Assert.True(retired.Retire("k" + i, refs: 1, payload: "pair" + i));

        // Over the cap, and every row is still on screen: nothing may be destroyed.
        Assert.Empty(retired.TakeOverflow());
        Assert.Equal(IconCachePolicy.MaxRetiredPairs + 2, retired.Count);

        // The overflow is reported exactly once per episode, so the leak is visible in the log
        // without becoming a per-retire spam source.
        Assert.True(retired.NoteCapExceeded());
        Assert.False(retired.NoteCapExceeded());

        Assert.Equal(
            "icon cache retired list over cap and every pair is still referenced; holding 66"
            + " (cap 64). Nothing was destroyed: a referenced pair is still on screen.",
            IconCachePolicy.FormatRetiredOverCap(IconCachePolicy.MaxRetiredPairs + 2, IconCachePolicy.MaxRetiredPairs));

        // Once holders let go, the cap collects those rows and only those rows.
        Assert.True(retired.ReleaseRef("k3", out _));
        Assert.True(retired.ReleaseRef("k4", out _));
        Assert.Equal(IconCachePolicy.MaxRetiredPairs, retired.Count);
        Assert.Empty(retired.TakeOverflow());

        // Back inside the cap, so a later episode is allowed to log again.
        Assert.False(retired.NoteCapExceeded());
        retired.Retire("later-a", refs: 1, payload: "pairLA");
        retired.Retire("later-b", refs: 1, payload: "pairLB");
        Assert.Empty(retired.TakeOverflow());
        Assert.True(retired.NoteCapExceeded());
    }

    /// <summary>
    /// The Unity half is wired to the policy above: retire carries the refcount,
    /// <c>Release</c> frees a retired pair, the cap is swept after every retire, and pin-diag
    /// reports the retired count (the R27 open question "how big does Retired get?").
    /// <c>IconCache.cs</c> is not compiled into this project (it is Unity-typed), so the wiring
    /// itself can only be asserted against the source; the rules it calls are tested above.
    /// </summary>
    [Fact]
    public void IconCache_wires_the_retired_table()
    {
        var src = ReadRepoFile("src", "Progenitor.Bootstrap", "IconCache.cs");
        Assert.Contains("private static readonly RetiredIconTable Retired = new();", src, StringComparison.Ordinal);
        Assert.Contains("RetirePair(Table.LastReplacedKey, Table.LastReplacedRefs)", src, StringComparison.Ordinal);
        Assert.Contains("SweepRetiredNoLock();", src, StringComparison.Ordinal);
        Assert.Contains("Retired.ReleaseRef(key, out var freed)", src, StringComparison.Ordinal);
        Assert.Contains("Retired.TakeAll()", src, StringComparison.Ordinal);
        Assert.Contains("IconCachePolicy.FormatPinDiag(Table.Count, Retired.Count)", src, StringComparison.Ordinal);
        Assert.Contains("IconCachePolicy.NormalizePluginId(parsed)", src, StringComparison.Ordinal);
        // R28 finding 4: the cap consults NoteCapExceeded and logs the over-cap line, and a
        // release only reaches the retired table when no live row owns that key.
        Assert.Contains("Retired.NoteCapExceeded()", src, StringComparison.Ordinal);
        Assert.Contains("IconCachePolicy.FormatRetiredOverCap(", src, StringComparison.Ordinal);
        Assert.Contains("bool liveRow = Table.Contains(key);", src, StringComparison.Ordinal);
        Assert.Contains("if (!liveRow && Retired.ReleaseRef(key, out var freed))", src, StringComparison.Ordinal);
        // R29 finding 10: the retire seam is guarded by the replacement rule, and the retire
        // itself goes through the hand-off so the previous replacement's row can drain.
        Assert.Contains(
            "IconCachePolicy.ShouldRetireOnReplace(liveRowReplaced: true, Table.LastReplacedRefs)",
            src, StringComparison.Ordinal);
        Assert.Contains(
            "Retired.RetireReplaced(index, key, refs, live, out var freed, out bool handedOn)",
            src, StringComparison.Ordinal);
        Assert.Contains("IconCachePolicy.FormatRetiredHandedOn(index, freed.Count > 0)", src, StringComparison.Ordinal);
        // Exactly one retire into the table, and never the index-less overload: a second seam, or
        // one that cannot be handed on, is the generation-scoped retire the finding is about.
        Assert.Equal(1, CountOccurrences(src, "Retired.RetireReplaced("));
        Assert.DoesNotContain("Retired.Retire(", src, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(src, "RetirePair(Table."));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    /// <summary>
    /// One Content release is one reference and must land on exactly one row. A reverted icon
    /// PNG re-creates a canonical key that is also retired (the pair superseded earlier is still
    /// held under it), and <c>IconCache.Release</c> used to decrement the live row and then the
    /// retired row for the same call, draining the retired pair one release per holder too fast.
    /// </summary>
    [Fact]
    public void A_key_that_is_both_live_and_retired_is_told_apart()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key);
        Assert.True(table.Contains(key));
        Assert.False(table.Contains("mod.a\nicons/foo.png\nnot-a-real-sha"));

        // A row nothing references still exists — Contains and RefsOf are not the same question.
        Assert.Equal(0, table.RefsOf(key));
        Assert.True(table.Contains(key));

        table.Release(key);
        Assert.False(table.Contains(key));
    }

    [Fact]
    public void Log_lines_are_stable()
    {
        Assert.Equal("pin-diag IconCache count=3", IconCachePolicy.FormatPinDiag(3));
        Assert.Equal("pin-diag IconCache count=3 retired=2", IconCachePolicy.FormatPinDiag(3, 2));
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' from cache on 'sample.nodrain'",
            IconCachePolicy.FormatFromCache("icons/nodrain.png", "sample.nodrain"));
        Assert.Equal(
            "icon cache entry 'mod.a\nicons/foo.png\nabc' had a dead texture; recreated",
            IconCachePolicy.FormatDeadRecreated("mod.a\nicons/foo.png\nabc"));
    }

    [Fact]
    public void Apply_from_cache_when_the_asset_root_is_missing()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("progenitor.sample", "icons/nodrain.png", PngA, out var key);

        bool haveBytes = false;
        bool cacheHit = table.TryGetWithoutBytes("progenitor.sample", "icons/nodrain.png", out var found, out _, out var dead);
        var resolve = IconCachePolicy.Decide(
            haveEntryForKey: cacheHit && found == key,
            textureIsUnityNull: dead,
            haveBytes: haveBytes,
            haveOtherHashForPluginPath: false);
        Assert.True(cacheHit);
        Assert.False(dead);
        Assert.Equal(IconCachePolicy.Resolve.Hit, resolve);
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' from cache on 'sample.nodrain'",
            PrefabIconPath.FormatFromCache("icons/nodrain.png", "sample.nodrain"));
    }

    [Fact]
    public void Uninstall_does_not_destroy_cached_objects()
    {
        var src = ReadRepoFile("src", "Progenitor.Core", "Content", "BuffIconApply.cs");
        int uninstall = src.IndexOf("public static void Uninstall()", StringComparison.Ordinal);
        int apply = src.IndexOf("private static void Apply(", StringComparison.Ordinal);
        Assert.True(uninstall >= 0 && apply > uninstall);
        string body = src.Substring(uninstall, apply - uninstall);
        Assert.Contains("ReleaseReference", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DestroyUnity", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DestroyImmediate", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DestroyLeftoverRoots", body, StringComparison.Ordinal);
        // R28 finding 29.11: this used to also assert that a doc comment saying
        // "never <c>Destroy</c>" existed in the file. A sentence in a comment is not a
        // behavior; the four DoesNotContain assertions above are the actual claim, and they
        // break if any destroy call is added back to Uninstall.

        var leaseDispose = src.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(leaseDispose > 0);
        Assert.Contains("if (!_cached)", src.Substring(leaseDispose), StringComparison.Ordinal);
    }

    /// <summary>
    /// Signature contract for the Core↔Bootstrap icon slots. These are source assertions because
    /// <c>ProcessHost</c>, <c>BootstrapLink</c>, <c>CorePinDiagnostics</c> and
    /// <c>ReloaderHost</c> are all Unity/BepInEx-typed and cannot compile into this project;
    /// Core binds these by name across the ALC boundary, so a rename is a runtime miss with no
    /// compiler error anywhere. Behavior, as opposed to shape, is tested on the policy types.
    /// </summary>
    [Fact]
    public void Bootstrap_slot_names_are_stable()
    {
        var host = ReadRepoFile("src", "Progenitor.Bootstrap", "ProcessHost.cs");
        Assert.Contains("public static bool TryGetOrCreateSprite(string key, byte[] bytes, out object? sprite)", host, StringComparison.Ordinal);
        Assert.Contains("public static bool TryGetCachedSprite(string pluginId, string relativePath, out object? sprite)", host, StringComparison.Ordinal);
        Assert.Contains("public static bool ReleaseSprite(string key)", host, StringComparison.Ordinal);
        Assert.Contains("public static int ReleasePluginSprites(string pluginId)", host, StringComparison.Ordinal);

        var link = ReadRepoFile("src", "Progenitor.Core", "BootstrapLink.cs");
        Assert.Contains("TryGetOrCreateSprite", link, StringComparison.Ordinal);
        Assert.Contains("TryGetCachedSprite", link, StringComparison.Ordinal);
        Assert.Contains("ReleaseSprite", link, StringComparison.Ordinal);
        Assert.Contains("ReleasePluginSprites", link, StringComparison.Ordinal);

        var pin = ReadRepoFile("src", "Progenitor.Bootstrap", "CorePinDiagnostics.cs");
        Assert.Contains("IconCache.DescribePins()", pin, StringComparison.Ordinal);

        var reloader = ReadRepoFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        Assert.Contains("Unload(unit, PluginUnloadSlot.Removal, releaseIcons: true)", reloader, StringComparison.Ordinal);
        // R31 finding 18 (job FRLD3): UnloadAllReverse and the new exit-time UnloadAllForShutdown
        // share one loop, so the reason is a parameter. Neither releases icons.
        Assert.Contains("Unload(item, reason, releaseIcons: false)", reloader, StringComparison.Ordinal);
        Assert.Contains("IconCache.ReleasePlugin", reloader, StringComparison.Ordinal);
        Assert.Contains("IconCachePolicy.OwnerId", reloader, StringComparison.Ordinal);
    }

    /// <summary>
    /// FT: the Sprite and Texture2D behind one plugin+path+hash key must be the same objects
    /// in every Core generation. A second pair minted for the same key leaves every live buff
    /// instance pointing at the first (evidence 112 / 122 / 131).
    /// </summary>
    [Fact]
    public void Cached_pair_identity_is_stable_across_core_generations()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("progenitor.sample", "icons/nodrain.png", PngA, out var gen1);
        table.Retain(gen1);

        // Generation 2: the plugin file is not back yet, so Content looks up by plugin+path.
        Assert.True(table.TryGetWithoutBytes("progenitor.sample", "icons/nodrain.png", out var swapKey, out _, out _));
        Assert.Equal(gen1, swapKey);

        // Generation 2 again once the plugin file is back and the PNG bytes are readable.
        var again = table.TryGetOrCreate("progenitor.sample", "icons/nodrain.png", PngA, out var gen2);
        Assert.Equal(IconCachePolicy.Resolve.Hit, again);
        Assert.Equal(gen1, gen2);
        Assert.Null(table.LastReplacedKey);
        Assert.Equal(1, table.Count);

        // The cache reuses any live pair for the canonical key; only a dead texture recreates.
        var cache = ReadRepoFile("src", "Progenitor.Bootstrap", "IconCache.cs");
        Assert.Contains("resolve != IconCachePolicy.Resolve.Recreate", cache, StringComparison.Ordinal);
    }

    /// <summary>
    /// A changed PNG replaces the row, but the superseded Sprite/Texture2D must not be
    /// destroyed while a live clone still draws it: that is the black square. It is retired
    /// (kept alive) and freed only by ClearAll at process exit.
    /// </summary>
    [Fact]
    public void Replaced_pair_is_kept_alive_while_a_clone_still_references_it()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var oldKey);
        table.Retain(oldKey);
        Assert.Equal(1, table.RefsOf(oldKey));

        var replaced = table.TryGetOrCreate("mod.a", "icons/foo.png", PngB, out var newKey);
        Assert.Equal(IconCachePolicy.Resolve.Replace, replaced);
        Assert.Equal(oldKey, table.LastReplacedKey);
        Assert.Equal(1, table.LastReplacedRefs);
        Assert.NotEqual(oldKey, newKey);
        Assert.False(IconCachePolicy.ShouldDestroyReplaced(table.LastReplacedRefs));

        // Nothing referenced it: then the replace may destroy the old pair.
        var fresh = new IconCacheTable();
        fresh.TryGetOrCreate("mod.b", "icons/foo.png", PngA, out _);
        fresh.TryGetOrCreate("mod.b", "icons/foo.png", PngB, out _);
        Assert.Equal(0, fresh.LastReplacedRefs);
        Assert.True(IconCachePolicy.ShouldDestroyReplaced(fresh.LastReplacedRefs));

        Assert.Equal(
            "icon cache entry 'mod.a\nicons/foo.png\nabc' replaced but kept alive; 1 live reference(s)",
            IconCachePolicy.FormatReplacedKept("mod.a\nicons/foo.png\nabc", 1));

        var cache = ReadRepoFile("src", "Progenitor.Bootstrap", "IconCache.cs");
        Assert.Contains("IconCachePolicy.ShouldDestroyReplaced(Table.LastReplacedRefs)", cache, StringComparison.Ordinal);
        // The retire now carries the refcount so the pair can be freed when that count drops
        // (R27 finding 14a); it is no longer parked until process exit.
        Assert.Contains("RetirePair(Table.LastReplacedKey, Table.LastReplacedRefs)", cache, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Core swap must never reach the release rule: ReloaderHost only releases icons on
    /// plugin-file removal, Release drops a retain (destroying only a pending-destroy row
    /// nothing holds), and ReleasePlugin keeps referenced pairs.
    /// </summary>
    [Fact]
    public void Release_never_fires_during_a_core_swap()
    {
        var table = new IconCacheTable();
        table.TryGetOrCreate("mod.a", "icons/foo.png", PngA, out var key);
        table.Retain(key);

        // Plugin file removed while a clone holds the sprite: the row is kept, pending.
        Assert.Equal(0, table.ReleasePlugin("mod.a"));
        Assert.Equal(1, table.Count);
        Assert.True(table.IsPendingDestroy(key));

        // Only Content's last release drops it.
        Assert.True(table.ReleaseRef(key));
        Assert.Equal(0, table.Count);

        // An unreferenced row is dropped immediately on file removal.
        var other = new IconCacheTable();
        other.TryGetOrCreate("mod.b", "icons/foo.png", PngA, out _);
        Assert.Equal(1, other.ReleasePlugin("mod.b"));
        Assert.Equal(0, other.Count);

        var reloader = ReadRepoFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        Assert.Contains("Unload(unit, leave.Reason, releaseIcons: false)", reloader, StringComparison.Ordinal);
        // Job FORD1: a dependent unloaded ahead of its provider comes back, so it keeps its icons too.
        Assert.Contains("Unload(unit, PluginUnloadSlot.Swap, releaseIcons: false)", reloader, StringComparison.Ordinal);
        // R31 finding 18 (job FRLD3): UnloadAllReverse and the new exit-time UnloadAllForShutdown
        // share one loop, so the reason is a parameter. Neither releases icons.
        Assert.Contains("Unload(item, reason, releaseIcons: false)", reloader, StringComparison.Ordinal);
        Assert.Contains("Unload(unit, PluginUnloadSlot.Removal, releaseIcons: true)", reloader, StringComparison.Ordinal);

        // Core-side Uninstall drops leases only; ownership helpers destroy nothing.
        var ownership = ReadRepoFile("src", "Progenitor.Core", "Content", "ContentIconOwnership.cs");
        Assert.DoesNotContain("Object.Destroy", ownership, StringComparison.Ordinal);
        Assert.DoesNotContain("DestroyImmediate", ownership, StringComparison.Ordinal);
        Assert.DoesNotContain("DestroyUnity", ownership, StringComparison.Ordinal);
    }

    [Fact]
    public void Unity_cache_creates_sprite_and_texture_together_with_hide_flags()
    {
        var src = ReadRepoFile("src", "Progenitor.Bootstrap", "IconCache.cs");
        Assert.Contains("hideFlags = HideFlags.HideAndDontSave", src, StringComparison.Ordinal);
        Assert.Contains("Sprite.Create", src, StringComparison.Ordinal);
        Assert.Contains("new Texture2D", src, StringComparison.Ordinal);
        Assert.Contains("FormatDeadRecreated", src, StringComparison.Ordinal);
        Assert.Contains("DestroyPair", src, StringComparison.Ordinal);
        Assert.Contains("IsUnityDead", src, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidateParts = new string[parts.Length + 1];
            candidateParts[0] = dir.FullName;
            Array.Copy(parts, 0, candidateParts, 1, parts.Length);
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }
}
