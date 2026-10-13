using System;
using Progenitor.Ecs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the SetComponentDataRaw TypeSize gate (evidence §32: Follower
/// TypeSize=0 then a 32-byte blob killed the server). Linked through <c>RawWriteGuard.cs</c>.
/// </summary>
public class RawWriteGuardTests
{
    [Fact]
    public void TypeSize_zero_throws_and_names_the_type()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureWritable(0, 32, "ProjectM.Follower"));
        Assert.Contains("ProjectM.Follower", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TypeSize is 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeSize_negative_throws_and_names_the_type()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureWritable(-1, 16, "Unity.Transforms.Translation"));
        Assert.Contains("Unity.Transforms.Translation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeSize_smaller_than_requested_throws_and_names_the_type()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureWritable(4, 82, "ProjectM.Pathfinding.PathRequest"));
        Assert.Contains("ProjectM.Pathfinding.PathRequest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("82", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_TypeSize_is_allowed()
    {
        RawWriteGuard.EnsureWritable(80, 80, "ProjectM.AiMove_Server");
    }

    [Fact]
    public void TypeSize_larger_than_requested_is_allowed()
    {
        // Gate is overrun-only. A short write is the native SetComponentDataRaw's problem.
        RawWriteGuard.EnsureWritable(84, 82, "ProjectM.Pathfinding.PathRequest");
    }

    [Fact]
    public void Null_or_empty_type_name_is_refused_before_the_size_check()
    {
        Assert.Throws<ArgumentException>(() => RawWriteGuard.EnsureWritable(0, 8, null!));
        Assert.Throws<ArgumentException>(() => RawWriteGuard.EnsureWritable(0, 8, string.Empty));
    }

    [Fact]
    public void Non_positive_requested_size_throws_and_names_the_type()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureWritable(16, 0, "ProjectM.Follower"));
        Assert.Contains("ProjectM.Follower", ex.Message, StringComparison.Ordinal);
    }

    // ---- EnsureTypedWrite: TypeSize 0 is the interop norm; typed sizeof writes are allowed
    // unless SizeInChunk is known-positive and smaller (2026-09-03, unit spike regression).

    [Fact]
    public void Typed_write_TypeSize_zero_allows_any_type_when_chunk_is_unknown()
    {
        RawWriteGuard.EnsureTypedWrite(0, 12, 0, RawWriteGuard.TranslationTypeName);
        RawWriteGuard.EnsureTypedWrite(0, 30, 0, RawWriteGuard.FollowerTypeName);
        RawWriteGuard.EnsureTypedWrite(0, 80, 0, "ProjectM.AiMove_Server");
        RawWriteGuard.EnsureTypedWrite(0, 16, -1, "ProjectM.LifeTime");
    }

    [Fact]
    public void Typed_write_TypeSize_zero_allows_when_chunk_fits()
    {
        RawWriteGuard.EnsureTypedWrite(0, 30, 32, RawWriteGuard.FollowerTypeName);
        RawWriteGuard.EnsureTypedWrite(0, 30, 30, RawWriteGuard.FollowerTypeName);
    }

    [Fact]
    public void Typed_write_TypeSize_zero_ignores_SizeInChunk_in_both_directions()
    {
        // Evidence 11 and 37: SizeInChunk reads 525 for PathRequest and 515 for Follower; a small
        // garbage value must not refuse the live-verified Translation write (R8 regression A).
        RawWriteGuard.EnsureTypedWrite(0, 30, 16, RawWriteGuard.FollowerTypeName);
        RawWriteGuard.EnsureTypedWrite(0, 12, 4, RawWriteGuard.TranslationTypeName);
        RawWriteGuard.EnsureTypedWrite(0, 30, 515, RawWriteGuard.FollowerTypeName);
    }

    [Fact]
    public void Typed_write_nonzero_TypeSize_must_equal_managed_size()
    {
        RawWriteGuard.EnsureTypedWrite(12, 12, 12, RawWriteGuard.TranslationTypeName);
        Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureTypedWrite(16, 12, 16, RawWriteGuard.TranslationTypeName));
        Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureTypedWrite(4, 82, 4, "ProjectM.Pathfinding.PathRequest"));
        Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureTypedWrite(-1, 12, 0, RawWriteGuard.TranslationTypeName));
    }

    [Fact]
    public void Typed_write_null_or_empty_type_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => RawWriteGuard.EnsureTypedWrite(0, 12, 0, null!));
        Assert.Throws<ArgumentException>(() => RawWriteGuard.EnsureTypedWrite(0, 12, 0, string.Empty));
    }

    [Fact]
    public void Overlay_memcpy_budget_is_zero_when_TypeSize_is_not_positive()
    {
        Assert.Equal(0, RawWriteGuard.OverlayMemcpyBudget(0));
        Assert.Equal(0, RawWriteGuard.OverlayMemcpyBudget(-1));
        Assert.Equal(12, RawWriteGuard.OverlayMemcpyBudget(12));
    }

    [Fact]
    public void Overlay_memcpy_refuses_TypeSize_zero_and_names_the_overlay_path()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureOverlayMemcpy(0, 30, RawWriteGuard.FollowerTypeName));
        Assert.Contains(RawWriteGuard.FollowerTypeName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("poke fields", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MemoryCopy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_memcpy_refuses_when_copy_exceeds_TypeSize()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureOverlayMemcpy(16, 32, "ProjectM.ItemKeyLookup"));
        Assert.Contains("16", ex.Message, StringComparison.Ordinal);
        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_memcpy_allows_a_copy_that_fits_TypeSize()
    {
        RawWriteGuard.EnsureOverlayMemcpy(32, 32, "ProjectM.ItemKeyLookup");
        RawWriteGuard.EnsureOverlayMemcpy(32, 8, "ProjectM.ItemKeyLookup");
    }
}
