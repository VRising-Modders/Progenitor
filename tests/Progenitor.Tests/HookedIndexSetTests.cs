using System;
using System.Collections.Generic;
using Progenitor.Hooks;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Pure-logic tests for the lock-free hooked-index snapshot. No game types: the set is an
/// open-addressing table of ints, and these cases are the unhooked-prefix Contains path.
/// </summary>
public class HookedIndexSetTests
{
    [Fact]
    public void Empty_contains_nothing()
    {
        Assert.Equal(0, HookedIndexSet.Empty.Count);
        Assert.False(HookedIndexSet.Empty.Contains(0));
        Assert.False(HookedIndexSet.Empty.Contains(1));
        Assert.False(HookedIndexSet.Empty.Contains(-1));
        Assert.False(HookedIndexSet.Empty.Contains(int.MaxValue));
    }

    [Fact]
    public void Create_null_empty_or_only_zeros_returns_the_shared_empty()
    {
        Assert.Same(HookedIndexSet.Empty, HookedIndexSet.Create(null));
        Assert.Same(HookedIndexSet.Empty, HookedIndexSet.Create(Array.Empty<int>()));
        Assert.Same(HookedIndexSet.Empty, HookedIndexSet.Create(new[] { 0, 0, 0 }));
    }

    [Fact]
    public void Create_ignores_zero_and_duplicates()
    {
        var set = HookedIndexSet.Create(new[] { 0, 7, 7, 3, 0, 3, 7 });
        Assert.Equal(2, set.Count);
        Assert.True(set.Contains(7));
        Assert.True(set.Contains(3));
        Assert.False(set.Contains(0));
        Assert.False(set.Contains(1));
        Assert.False(set.Contains(4));
    }

    [Fact]
    public void Single_index_is_a_hit_and_neighbors_are_misses()
    {
        var set = HookedIndexSet.Create(new[] { 42 });
        Assert.Equal(1, set.Count);
        Assert.True(set.Contains(42));
        Assert.False(set.Contains(0));
        Assert.False(set.Contains(41));
        Assert.False(set.Contains(43));
    }

    [Fact]
    public void Sequential_type_indices_all_hit_and_the_next_misses()
    {
        var ids = new int[32];
        for (int i = 0; i < ids.Length; i++) ids[i] = i + 1;

        var set = HookedIndexSet.Create(ids);

        Assert.Equal(32, set.Count);
        for (int i = 1; i <= 32; i++)
            Assert.True(set.Contains(i), $"expected hit for {i}");
        Assert.False(set.Contains(0));
        Assert.False(set.Contains(33));
        Assert.False(set.Contains(-1));
    }

    [Fact]
    public void Spread_indices_survive_probing_among_misses()
    {
        var ids = new int[64];
        for (int i = 0; i < ids.Length; i++) ids[i] = (i + 1) * 17;

        var set = HookedIndexSet.Create(ids);

        Assert.Equal(64, set.Count);
        foreach (int id in ids)
            Assert.True(set.Contains(id), $"expected hit for {id}");
        Assert.False(set.Contains(1));
        Assert.False(set.Contains(17 * 65));
        Assert.False(set.Contains(17 * 64 + 1));
    }

    [Fact]
    public void Create_does_not_alias_the_input_array()
    {
        var ids = new[] { 4, 8 };
        var set = HookedIndexSet.Create(ids);
        ids[0] = 99;
        ids[1] = 100;

        Assert.True(set.Contains(4));
        Assert.True(set.Contains(8));
        Assert.False(set.Contains(99));
        Assert.False(set.Contains(100));
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void Negative_and_large_indices_are_valid_members()
    {
        var set = HookedIndexSet.Create(new[] { -3, int.MaxValue, int.MinValue, 1 });
        Assert.Equal(4, set.Count);
        Assert.True(set.Contains(-3));
        Assert.True(set.Contains(int.MaxValue));
        Assert.True(set.Contains(int.MinValue));
        Assert.True(set.Contains(1));
        Assert.False(set.Contains(-2));
        Assert.False(set.Contains(int.MaxValue - 1));
    }

    [Fact]
    public void Random_unique_keys_round_trip_against_a_reference_set()
    {
        var rng = new Random(20260902);
        var hits = new HashSet<int>();
        while (hits.Count < 200)
        {
            int n = rng.Next(int.MinValue, int.MaxValue);
            if (n != 0) hits.Add(n);
        }

        var set = HookedIndexSet.Create(hits);
        Assert.Equal(200, set.Count);
        foreach (int id in hits)
            Assert.True(set.Contains(id));

        int misses = 0;
        while (misses < 200)
        {
            int n = rng.Next(int.MinValue, int.MaxValue);
            if (n == 0 || hits.Contains(n)) continue;
            Assert.False(set.Contains(n), $"expected miss for {n}");
            misses++;
        }
    }
}
