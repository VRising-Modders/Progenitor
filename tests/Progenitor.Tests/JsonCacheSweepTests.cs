using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Progenitor.Reload;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <see cref="JsonCacheSweep"/> against an object with System.Text.Json 6.0's private cache shape
/// (this test host is net8.0, whose options are laid out differently; the real 6.0.7 behavior —
/// a default-options serialize pins a collectible context and the sweep releases it — is recorded
/// in the internal research notes on Core hot reload).
/// </summary>
public class JsonCacheSweepTests
{
    /// <summary>Same private field names as System.Text.Json 6.0's JsonSerializerOptions.</summary>
    private sealed class FakeOptions
    {
#pragma warning disable IDE1006, CS0414 // names must match the runtime's private fields
        public readonly ConcurrentDictionary<Type, object> _converters = new();
        public readonly ConcurrentDictionary<Type, FakeTypeInfo> _classes = new();
        // The compiler names this auto-property's field <_lastClass>k__BackingField, as in 6.0.
        public FakeTypeInfo? _lastClass { get; set; }
#pragma warning restore IDE1006, CS0414
    }

    private sealed class FakeTypeInfo
    {
        public FakeTypeInfo(Type type) => Type = type;
        internal Type Type { get; }
    }

    private static Type CollectibleType()
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("JsonCacheSweepTests.Collectible" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect);
        var type = asm.DefineDynamicModule("m").DefineType("Payload", TypeAttributes.Public).CreateType();
        Assert.True(type!.IsCollectible);
        return type;
    }

    [Fact]
    public void Removes_only_collectible_keys_and_the_last_type_cache_that_names_one()
    {
        var collectible = CollectibleType();
        var closedOverCollectible = typeof(List<>).MakeGenericType(collectible);
        var options = new FakeOptions();
        options._converters[typeof(string)] = new object();
        options._converters[collectible] = new object();
        options._converters[closedOverCollectible] = new object();
        options._classes[typeof(int)] = new FakeTypeInfo(typeof(int));
        options._classes[collectible] = new FakeTypeInfo(collectible);
        options._lastClass = new FakeTypeInfo(collectible);

        int removed = JsonCacheSweep.ReleaseCollectible(options, out var missing);

        Assert.Null(missing);
        Assert.Equal(4, removed);
        Assert.Equal(new[] { typeof(string) }, options._converters.Keys);
        Assert.Equal(new[] { typeof(int) }, options._classes.Keys);
        Assert.Null(options._lastClass);
    }

    [Fact]
    public void Keeps_a_last_type_cache_for_a_non_collectible_type()
    {
        var options = new FakeOptions { _lastClass = new FakeTypeInfo(typeof(string)) };

        int removed = JsonCacheSweep.ReleaseCollectible(options, out var missing);

        Assert.Null(missing);
        Assert.Equal(0, removed);
        Assert.NotNull(options._lastClass);
    }

    [Fact]
    public void Count_reports_collectible_entries_with_names_without_removing_them()
    {
        var collectible = CollectibleType();
        var options = new FakeOptions();
        options._converters[collectible] = new object();
        options._classes[collectible] = new FakeTypeInfo(collectible);
        options._classes[typeof(int)] = new FakeTypeInfo(typeof(int));
        var samples = new List<string>();

        int count = JsonCacheSweep.CountCollectible(options, samples, maxSamples: 4, out var missing);

        Assert.Null(missing);
        Assert.Equal(2, count);
        Assert.Equal(new[] { collectible.Assembly.GetName().Name + ":Payload" }, samples);
        Assert.Equal(2, options._classes.Count);
    }

    [Fact]
    public void A_sample_name_names_the_collectible_assembly_not_CoreLib()
    {
        var collectible = CollectibleType();
        var list = typeof(List<>).MakeGenericType(collectible);

        Assert.Equal(collectible.Assembly.GetName().Name + ":List`1", JsonCacheSweep.SafeName(list));
        Assert.Equal(collectible.Assembly.GetName().Name + ":Payload[]", JsonCacheSweep.SafeName(collectible.MakeArrayType()));
    }

    [Fact]
    public void An_unexpected_layout_is_reported_not_thrown()
    {
        int removed = JsonCacheSweep.ReleaseCollectible(new object(), out var missing);
        Assert.Equal(0, removed);
        Assert.Equal(JsonCacheSweep.ConvertersField, missing);

        Assert.Equal(0, JsonCacheSweep.ReleaseCollectible(null, out missing));
        Assert.Equal(JsonCacheSweep.DefaultOptionsField, missing);
    }

    [Fact]
    public void Process_caches_never_throw_on_this_runtime()
    {
        // net8.0 lays the options out differently: the sweep must say so, not throw.
        JsonCacheSweep.ReleaseProcessCaches(out _);
        Assert.StartsWith("pin-diag Json collectible default=", JsonCacheSweep.DescribeProcessCaches());
    }
}
