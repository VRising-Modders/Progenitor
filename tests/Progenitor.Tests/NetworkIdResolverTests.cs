using System;
using System.Collections.Generic;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Inventory.TryResolveNetworkId</c>'s lookup half: validation of
/// the untrusted words, the map, read-back confirmation, stale generations, rebuild throttling
/// and world-epoch changes.
/// </summary>
public class NetworkIdResolverTests
{
    private sealed class FakeSource : INetworkIdSource
    {
        // entity (index, version) -> network id (index, generation, type)
        public readonly Dictionary<(int, int), (int Index, int Gen, int Type)> Entities = new();
        public int Scans;
        public bool Throw;

        public void Scan(List<NetworkIdEntry> into)
        {
            Scans++;
            if (Throw) throw new InvalidOperationException("boom");
            foreach (var kv in Entities)
                if (kv.Value.Type == NetworkIdWords.TypeNormal)
                    into.Add(new NetworkIdEntry(kv.Value.Index, kv.Value.Gen, kv.Key.Item1, kv.Key.Item2));
        }

        public bool TryReadBack(int entityIndex, int entityVersion, out int index, out int generation, out int type)
        {
            index = generation = type = 0;
            if (!Entities.TryGetValue((entityIndex, entityVersion), out var v)) return false;
            (index, generation, type) = v;
            return true;
        }
    }

    private const int Epoch = 3;
    private long _now = 10_000;

    private (FakeSource, NetworkIdResolver) Rig()
    {
        var src = new FakeSource();
        src.Entities[(812, 3)] = (40, 1, 1);   // a chest
        src.Entities[(900, 1)] = (41, 0, 1);   // a station
        return (src, new NetworkIdResolver(src));
    }

    private bool Resolve(NetworkIdResolver r, int index, int gen, int type, out int ei, out int ev, out string reason, int epoch = Epoch) =>
        r.TryResolve(index, gen, type, epoch, _now, out ei, out ev, out reason);

    [Theory]
    [InlineData(40, 1, 0, "empty")]
    [InlineData(40, 1, 2, "static map object")]
    [InlineData(40, 1, 7, "not a type")]
    [InlineData(0, 1, 1, "out of range")]
    [InlineData(-5, 1, 1, "out of range")]
    [InlineData(40, 256, 1, "out of range")]
    [InlineData(40, -1, 1, "out of range")]
    [InlineData(int.MinValue, int.MaxValue, int.MinValue, "not a type")]
    public void MalformedWords_AreRefusedWithoutAScan(int index, int gen, int type, string expect)
    {
        var (src, r) = Rig();
        Assert.False(Resolve(r, index, gen, type, out int ei, out int ev, out string reason));
        Assert.Contains(expect, reason);
        Assert.Equal(0, ei);
        Assert.Equal(0, ev);
        Assert.Equal(0, src.Scans);
    }

    [Fact]
    public void KnownId_ResolvesToItsEntity_AndIsCachedAndConfirmed()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out int ei, out int ev, out string reason));
        Assert.Equal((812, 3), (ei, ev));
        Assert.Equal("", reason);
        Assert.Equal(1, src.Scans);

        _now += 1;
        Assert.True(Resolve(r, 41, 0, 1, out ei, out ev, out _));
        Assert.Equal((900, 1), (ei, ev));
        Assert.Equal(1, src.Scans); // served from the map
    }

    [Fact]
    public void Prime_BuildsTheMapSoTheFirstLookupDoesNotScan()
    {
        var (src, r) = Rig();
        r.Prime(Epoch, _now);
        Assert.Equal(1, src.Scans);
        Assert.Equal(1, r.Primes);
        Assert.Equal(1, r.Rebuilds);
        _now += 1;
        Assert.True(Resolve(r, 40, 1, 1, out int ei, out _, out _));
        Assert.Equal(812, ei);
        Assert.Equal(1, src.Scans);

        // A prime for another epoch is used by that epoch's lookups; a throwing source leaves an empty map.
        src.Throw = true;
        r.Prime(Epoch + 1, _now);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void UnknownId_IsRefused_AndRebuildsAreThrottled()
    {
        var (src, r) = Rig();
        Assert.False(Resolve(r, 99, 0, 1, out _, out _, out string reason));
        Assert.Contains("no placed container", reason);
        Assert.Equal(1, src.Scans);

        // A flood of unknown ids within the window scans no more.
        for (int i = 0; i < 50; i++)
        {
            _now += 1;
            Assert.False(Resolve(r, 1000 + i, 0, 1, out _, out _, out reason));
        }

        Assert.Equal(1, src.Scans);
        Assert.Contains("refreshed", reason);

        // A chest placed since is found once the window has passed.
        src.Entities[(950, 2)] = (99, 0, 1);
        _now += NetworkIdResolver.MinRebuildMs;
        Assert.True(Resolve(r, 99, 0, 1, out int ei, out _, out _));
        Assert.Equal(950, ei);
        Assert.Equal(2, src.Scans);
    }

    [Fact]
    public void StaleGeneration_SaysSo()
    {
        var (src, r) = Rig();
        Assert.False(Resolve(r, 40, 2, 1, out _, out _, out string reason));
        Assert.Contains("stale", reason);
        Assert.Contains("generation 1", reason);
    }

    [Fact]
    public void ForgedGeneration_OnAWarmCache_IsStale_AndKeepsTheRow()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        int scans = src.Scans;

        _now += 1;
        Assert.False(Resolve(r, 40, 7, 1, out _, out _, out string reason));
        Assert.Contains("stale", reason);
        Assert.Contains("generation 1", reason);

        // The valid lookup right after still resolves from the map: nothing was evicted or rescanned.
        _now += 1;
        Assert.True(Resolve(r, 40, 1, 1, out int ei, out int ev, out _));
        Assert.Equal((812, 3), (ei, ev));
        Assert.Equal(scans, src.Scans);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void DestroyedEntity_IsNotTrustedFromTheCache()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        src.Entities.Remove((812, 3));
        _now += NetworkIdResolver.MinRebuildMs;
        Assert.False(Resolve(r, 40, 1, 1, out _, out _, out string reason));
        Assert.Contains("no placed container", reason);
    }

    [Fact]
    public void ReusedIndex_FollowsTheNewEntity()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        src.Entities.Remove((812, 3));
        src.Entities[(812, 4)] = (40, 2, 1); // the game reused index 40 with the next generation
        _now += NetworkIdResolver.MinRebuildMs;

        Assert.False(Resolve(r, 40, 1, 1, out _, out _, out string reason));
        Assert.Contains("stale", reason);
        _now += NetworkIdResolver.MinRebuildMs;
        Assert.True(Resolve(r, 40, 2, 1, out int ei, out int ev, out _));
        Assert.Equal((812, 4), (ei, ev));
    }

    [Fact]
    public void EpochChange_DropsTheMap()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        Assert.Equal(1, src.Scans);
        // A new world load: the old map is never consulted, and the throttle starts over.
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _, epoch: Epoch + 1));
        Assert.Equal(2, src.Scans);
    }

    [Fact]
    public void SourceThatThrows_GivesAReason()
    {
        var (src, r) = Rig();
        src.Throw = true;
        Assert.False(Resolve(r, 40, 1, 1, out _, out _, out string reason));
        Assert.Contains("lookup failed", reason);
    }

    [Fact]
    public void ReadBackOfAnotherType_IsNotAMatch()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        src.Entities[(812, 3)] = (40, 1, 2);
        _now += 1;
        Assert.False(Resolve(r, 40, 1, 1, out _, out _, out _));
    }

    [Fact]
    public void SampleVerbs_ParseTheWords()
    {
        Assert.True(Progenitor.Sample.SampleInventory.Handles("resolve"));
        Assert.True(Progenitor.Sample.SampleInventory.Handles("NET"));
        Assert.True(Progenitor.Sample.SampleInventory.TryParseNetWords("40", "1", "", out int i, out int g, out int t));
        Assert.Equal((40, 1, 1), (i, g, t));
        Assert.True(Progenitor.Sample.SampleInventory.TryParseNetWords("40", "1", "2", out _, out _, out t));
        Assert.Equal(2, t);
        Assert.False(Progenitor.Sample.SampleInventory.TryParseNetWords("x", "1", "", out _, out _, out _));
        Assert.False(Progenitor.Sample.SampleInventory.TryParseNetWords("40", "", "", out _, out _, out _));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var (src, r) = Rig();
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        Assert.Equal(2, r.Count);
        r.Clear();
        Assert.Equal(0, r.Count);
        Assert.True(Resolve(r, 40, 1, 1, out _, out _, out _));
        Assert.Equal(2, src.Scans);
    }
}
