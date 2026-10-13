using System;
using Progenitor.Systems;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the nesting-safe world/delta ambient used by VirtualSystems.DispatchKey.
/// Thread-statics cannot be exercised through the dispatcher itself (the wrapper lives in
/// VirtualSystems, which this project does not compile), so the save/restore helper is the seam.
/// </summary>
public class DispatchAmbientTests
{
    [Fact]
    public void Push_installs_world_and_delta()
    {
        var token = new object();
        var previous = DispatchAmbient.Push(token, 0.016f);
        try
        {
            Assert.Same(token, DispatchAmbient.World);
            Assert.Equal(0.016f, DispatchAmbient.Delta);
            Assert.Null(previous.World);
            Assert.Equal(0f, previous.Delta);
        }
        finally
        {
            DispatchAmbient.Restore(previous);
        }

        Assert.Null(DispatchAmbient.World);
        Assert.Equal(0f, DispatchAmbient.Delta);
    }

    [Fact]
    public void Nested_Push_restores_the_outer_pair_not_null()
    {
        var outer = new object();
        var inner = new object();
        var outerSnap = DispatchAmbient.Push(outer, 0.016f);
        try
        {
            var innerSnap = DispatchAmbient.Push(inner, 0.033f);
            try
            {
                Assert.Same(inner, DispatchAmbient.World);
                Assert.Equal(0.033f, DispatchAmbient.Delta);
                Assert.Same(outer, innerSnap.World);
                Assert.Equal(0.016f, innerSnap.Delta);
            }
            finally
            {
                DispatchAmbient.Restore(innerSnap);
            }

            // The bug: nulling in finally left later outer slots with world == null.
            Assert.Same(outer, DispatchAmbient.World);
            Assert.Equal(0.016f, DispatchAmbient.Delta);
        }
        finally
        {
            DispatchAmbient.Restore(outerSnap);
        }

        Assert.Null(DispatchAmbient.World);
        Assert.Equal(0f, DispatchAmbient.Delta);
    }

    [Fact]
    public void Three_deep_restore_unwinds_in_order()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var snapA = DispatchAmbient.Push(a, 1f);
        try
        {
            var snapB = DispatchAmbient.Push(b, 2f);
            try
            {
                var snapC = DispatchAmbient.Push(c, 3f);
                try
                {
                    Assert.Same(c, DispatchAmbient.World);
                    Assert.Equal(3f, DispatchAmbient.Delta);
                }
                finally
                {
                    DispatchAmbient.Restore(snapC);
                }

                Assert.Same(b, DispatchAmbient.World);
                Assert.Equal(2f, DispatchAmbient.Delta);
            }
            finally
            {
                DispatchAmbient.Restore(snapB);
            }

            Assert.Same(a, DispatchAmbient.World);
            Assert.Equal(1f, DispatchAmbient.Delta);
        }
        finally
        {
            DispatchAmbient.Restore(snapA);
        }

        Assert.Null(DispatchAmbient.World);
        Assert.Equal(0f, DispatchAmbient.Delta);
    }

    [Fact]
    public void Restore_after_inner_throw_still_leaves_outer_intact()
    {
        var outer = new object();
        var inner = new object();
        var outerSnap = DispatchAmbient.Push(outer, 0.5f);
        try
        {
            try
            {
                var innerSnap = DispatchAmbient.Push(inner, 0.25f);
                try
                {
                    throw new InvalidOperationException("inner boom");
                }
                finally
                {
                    DispatchAmbient.Restore(innerSnap);
                }
            }
            catch (InvalidOperationException)
            {
                // DispatchKey's finally is the same shape: Restore even when Dispatch throws.
            }

            Assert.Same(outer, DispatchAmbient.World);
            Assert.Equal(0.5f, DispatchAmbient.Delta);
        }
        finally
        {
            DispatchAmbient.Restore(outerSnap);
        }
    }

    [Fact]
    public void Push_rejects_null_world()
    {
        Assert.Throws<ArgumentNullException>(() => DispatchAmbient.Push(null!, 0f));
        Assert.Null(DispatchAmbient.World);
    }
}
