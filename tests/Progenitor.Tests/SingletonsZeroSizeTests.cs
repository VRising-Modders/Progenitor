using System;
using Progenitor.Ecs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the zero-sized (tag) component gate in <c>Singletons.TryGetRawRW</c>
/// (evidence 208: <c>.ecs singleton UserMuteSingleton</c> round-tripped an engine
/// ArgumentException for a case the API can decide itself). Linked through
/// <c>SingletonsZeroSize.cs</c>.
/// </summary>
public class SingletonsZeroSizeTests
{
    /// <summary>
    /// The live TypeIndex from evidence 208 (ProjectM.UserMuteSingleton on the test server):
    /// 0x40000000 (ZeroSizeInChunkTypeFlag) | 0x20000 (HasNoEntityReferencesFlag) | ordinal 3133.
    /// </summary>
    private const int UserMuteSingletonTypeIndex = 1073876029;

    [Fact]
    public void Dump_constants_match_the_engine()
    {
        // TypeManager flag constants in the game's metadata.
        Assert.Equal(1073741824, SingletonsZeroSize.ZeroSizeInChunkTypeFlag);
        Assert.Equal(0x40000000, SingletonsZeroSize.ZeroSizeInChunkTypeFlag);
        Assert.Equal(16383, SingletonsZeroSize.ClearFlagsMask);
    }

    [Fact]
    public void The_live_tag_singleton_is_refused()
    {
        Assert.True(SingletonsZeroSize.IsZeroSized(UserMuteSingletonTypeIndex));
        Assert.Equal(3133, SingletonsZeroSize.IndexOf(UserMuteSingletonTypeIndex));
    }

    [Fact]
    public void An_ordinary_component_is_not_refused()
    {
        // A plain data component: ordinal plus flags that are not the zero-size bit.
        const int ordinary = 0x00020000 | 200; // HasNoEntityReferencesFlag | index 200
        Assert.False(SingletonsZeroSize.IsZeroSized(ordinary));
        Assert.False(SingletonsZeroSize.IsZeroSized(200));
        Assert.Equal(200, SingletonsZeroSize.IndexOf(ordinary));
    }

    [Fact]
    public void Every_other_TypeManager_flag_alone_is_not_a_zero_size_signal()
    {
        // TypeManager flags in the game's metadata. Only ZeroSizeInChunkTypeFlag may refuse; a buffer, managed,
        // shared, chunk or cleanup component still has bytes to point at.
        int[] otherFlags =
        {
            131072,    // HasNoEntityReferencesFlag
            262144,    // IsNotChunkSerializableTypeFlag
            524288,    // HasNativeContainerFlag
            1048576,   // BakingOnlyTypeFlag
            2097152,   // TemporaryBakingTypeFlag
            4194304,   // IRefCountedComponentFlag
            8388608,   // IEquatableTypeFlag
            16777216,  // EnableableComponentFlag
            33554432,  // CleanupComponentTypeFlag
            67108864,  // BufferComponentTypeFlag
            134217728, // SharedComponentTypeFlag
            268435456, // ManagedComponentTypeFlag
            536870912, // ChunkComponentTypeFlag
        };
        foreach (int flag in otherFlags)
            Assert.False(SingletonsZeroSize.IsZeroSized(flag | 77));
    }

    [Fact]
    public void TypeSize_is_not_the_signal()
    {
        // Trap 7: TypeSize reads 0 for ordinary components too (ServerRuntimeSettings logged
        // typeSize=0 with a valid raw pointer in the same evidence line). The gate must not be
        // reachable from a size at all — it takes only the TypeIndex.
        Assert.False(SingletonsZeroSize.IsZeroSized(0));
        Assert.False(SingletonsZeroSize.IsZeroSized(3133));
    }

    [Fact]
    public void Refusal_names_the_component_and_the_reason()
    {
        var message = SingletonsZeroSize.RefusalMessage(UserMuteSingletonTypeIndex, "ProjectM.UserMuteSingleton");
        Assert.Contains("ProjectM.UserMuteSingleton", message, StringComparison.Ordinal);
        Assert.Contains("1073876029", message, StringComparison.Ordinal);
        Assert.Contains("zero-sized (tag) component", message, StringComparison.Ordinal);
        Assert.Contains("ZeroSizeInChunkTypeFlag", message, StringComparison.Ordinal);
        Assert.Contains("TryGetEntity", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_falls_back_to_the_TypeIndex_when_the_name_does_not_resolve()
    {
        foreach (string? unnamed in new[] { null, "" })
        {
            var message = SingletonsZeroSize.RefusalMessage(UserMuteSingletonTypeIndex, unnamed);
            Assert.Contains("TypeIndex 1073876029 (index 3133)", message, StringComparison.Ordinal);
            Assert.DoesNotContain("[TypeIndex", message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The call site itself. <c>Singletons.cs</c> is <c>unsafe</c> and binds Unity.Entities, so it
    /// cannot be compiled here; these are source pins (see <see cref="SourcePin"/>). Without them
    /// the gate above could be a class nothing calls.
    /// </summary>
    [Fact]
    public void TryGetRawRW_refuses_a_tag_component_before_it_calls_the_engine()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ecs", "Singletons.cs");
        string body = SourcePin.MethodBody(
            source,
            "public static bool TryGetRawRW(EntityManager em, TypeIndex typeIndex, out IntPtr ptr, out int ecsTypeSize)");

        SourcePin.AssertStatementExactly(body, "if (SingletonsZeroSize.IsZeroSized(typeIndex.Value))");
        SourcePin.AssertUncommented(body, "SingletonsZeroSize.RefusalMessage(typeIndex.Value");

        // Ordering is the point: the refusal must come before GetComponentDataRawRW, not after it
        // in a catch block. Both fragments are in this body, so index order reads it.
        int gate = body.IndexOf("SingletonsZeroSize.IsZeroSized", StringComparison.Ordinal);
        int engine = body.IndexOf("em.GetComponentDataRawRW(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && engine > gate, "the zero-size gate must precede GetComponentDataRawRW");
    }

    [Fact]
    public void The_gate_does_not_read_a_size()
    {
        // Trap 7 again, as a pin: if this gate ever grows a TypeSize test it will start refusing
        // ordinary components (ServerRuntimeSettings, Translation, LifeTime) that the resilience
        // and unit code depend on.
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ecs", "SingletonsZeroSize.cs");
        SourcePin.AssertNoStatementContaining(source, "GetTypeInfo");
        SourcePin.AssertNoStatementContaining(source, "ecsTypeSize");
        SourcePin.AssertNoStatementContaining(source, "using Unity");
    }

    [Fact]
    public void Describe_brackets_the_index_behind_a_resolved_name()
    {
        Assert.Equal(
            "ProjectM.UserMuteSingleton [TypeIndex 1073876029 (index 3133)]",
            SingletonsZeroSize.DescribeComponent(UserMuteSingletonTypeIndex, "ProjectM.UserMuteSingleton"));
        Assert.Equal(
            "TypeIndex 200 (index 200)",
            SingletonsZeroSize.DescribeComponent(200, null));
    }
}
