using System;
using Progenitor.Bootstrap;
using Progenitor.Patches;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FHARM7. <c>ctx.Harmony</c> became <c>ctx.UnsafeHarmony</c>: taking it is an opt-in whose
/// price is hot reload. What is pinned here is the register behind it — the once-per-plugin note
/// the getter logs on, the refusal <c>PluginReloader</c> asks it for, and both lines — because
/// the property and the reloader themselves are game-bound.
/// </summary>
[Collection("PluginLogger")]
public class UnsafeHarmonyTests : IDisposable
{
    public UnsafeHarmonyTests() => Reset();

    public void Dispose() => Reset();

    // Job FHARM8: the mark now lives in two places on purpose - Core's own table and the
    // non-collectible Bootstrap table both reloaders read - so both are cleared between tests.
    private static void Reset()
    {
        UnsafeHarmonyUse.ResetForTests();
        UnsafeHarmonySlot.ResetForTests();
        UnsafeHarmonySlotLinkReset();
    }

    private static void UnsafeHarmonySlotLinkReset()
    {
        var link = typeof(UnsafeHarmonyUse).Assembly.GetType("Progenitor.Patches.UnsafeHarmonySlotLink");
        link?.GetMethod("ResetForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null);
    }

    [Fact]
    public void The_note_is_once_per_plugin_not_once_per_read()
    {
        Assert.True(UnsafeHarmonyUse.Note("com.example.one"));
        Assert.False(UnsafeHarmonyUse.Note("com.example.one"));
        Assert.False(UnsafeHarmonyUse.Note("com.example.one"));

        // A second plugin gets its own first-use line.
        Assert.True(UnsafeHarmonyUse.Note("com.example.two"));
        Assert.Equal(2, UnsafeHarmonyUse.Count);
    }

    [Fact]
    public void A_plugin_that_never_took_it_is_swappable()
    {
        UnsafeHarmonyUse.Note("com.example.unsafe");

        Assert.False(UnsafeHarmonyUse.RefusesSwap("com.example.safe"));
        // A nameless plugin is swappable too, as long as no nameless plugin has been marked.
        // See The_unknown_guid_bucket_over_refuses_on_purpose for the case where one has.
        Assert.False(UnsafeHarmonyUse.RefusesSwap(null));
    }

    /// <summary>
    /// Job FHARM8, R42's third test note. Two tests used to assert opposite answers for
    /// <c>RefusesSwap(null)</c> and both passed, because a reloader resolves the id as
    /// <c>GetMetadata(plugin)?.GUID ?? ""</c> and null and empty share one <c>"?"</c> bucket.
    /// The behavior is kept — it is the fail-closed direction, and nothing can tell two nameless
    /// plugins apart — but it is now one stated rule with one test, rather than an accident
    /// asserted from both sides.
    /// </summary>
    [Fact]
    public void The_unknown_guid_bucket_over_refuses_on_purpose()
    {
        Assert.False(UnsafeHarmonyUse.RefusesSwap(null));
        Assert.False(UnsafeHarmonyUse.RefusesSwap(""));

        UnsafeHarmonyUse.Note(""); // a plugin whose metadata could not be read

        // Every nameless plugin is now refused: over-refusal (cost: a restart) rather than
        // under-refusal (cost: a pinned generation per save).
        Assert.True(UnsafeHarmonyUse.RefusesSwap(null));
        Assert.True(UnsafeHarmonyUse.RefusesSwap(""));
        // A plugin that does have a name is untouched by it.
        Assert.False(UnsafeHarmonyUse.RefusesSwap("com.example.named"));
    }

    [Fact]
    public void The_reloader_refuses_a_swap_of_a_plugin_that_took_unsafe_harmony()
    {
        UnsafeHarmonyUse.Note("com.example.unsafe");

        Assert.True(UnsafeHarmonyUse.RefusesSwap("com.example.unsafe"));
        Assert.True(UnsafeHarmonyUse.IsMarked("com.example.unsafe"));
    }

    [Fact]
    public void The_mark_is_never_cleared_for_the_life_of_the_process()
    {
        // There is deliberately no Forget: an unload does not unpin the ALC, so a later load of
        // the same GUID is still living beside a pinned generation.
        UnsafeHarmonyUse.Note("com.example.unsafe");
        Assert.Null(typeof(UnsafeHarmonyUse).GetMethod("Forget"));

        // R42's second test note: the assertion above pins the absence of a name, which a future
        // Clear() or a clearing branch inside Note would leave green. The behavioral form: no
        // public surface can un-mark, and the mark survives everything an unload does. Note()
        // again (a reloaded plugin re-reading the property) must not reset it either.
        foreach (var m in typeof(UnsafeHarmonyUse).GetMethods(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            Assert.DoesNotContain("Clear", m.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Remove", m.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Reset", m.Name, StringComparison.Ordinal);
        }
        Assert.False(UnsafeHarmonyUse.Note("com.example.unsafe"));
        Assert.True(UnsafeHarmonyUse.RefusesSwap("com.example.unsafe"));
        Assert.True(UnsafeHarmonySlot.RefusesSwap("com.example.unsafe"));
    }

    [Fact]
    public void The_first_use_line_names_the_pin_and_the_consequence()
    {
        string line = UnsafeHarmonyUse.FormatFirstUse("com.example.unsafe");

        Assert.Contains("com.example.unsafe took ctx.UnsafeHarmony", line, StringComparison.Ordinal);
        Assert.Contains("pins the plugin AND this Core generation", line, StringComparison.Ordinal);
        Assert.Contains("non-hot-swappable", line, StringComparison.Ordinal);
        Assert.Contains("ctx.Patches", line, StringComparison.Ordinal);
        Assert.Single(line.Split('\n'));
    }

    [Fact]
    public void The_refusal_line_names_the_file_the_plugin_and_the_restart()
    {
        string line = UnsafeHarmonyUse.FormatSwapRefused("MyMod.dll", "com.example.unsafe");

        Assert.Contains("refusing to swap MyMod.dll (com.example.unsafe)", line, StringComparison.Ordinal);
        Assert.Contains("NOT loaded", line, StringComparison.Ordinal);
        Assert.Contains("restart", line, StringComparison.Ordinal);
        Assert.Single(line.Split('\n'));
    }

    [Fact]
    public void Describe_says_none_until_a_plugin_opts_out()
    {
        Assert.Equal("unsafe-harmony: none", UnsafeHarmonyUse.Describe());

        UnsafeHarmonyUse.Note("com.example.b");
        UnsafeHarmonyUse.Note("com.example.a");

        Assert.Equal("unsafe-harmony: com.example.a, com.example.b (non-hot-swappable)", UnsafeHarmonyUse.Describe());
    }

    [Fact]
    public void An_empty_guid_is_recorded_rather_than_ignored()
    {
        Assert.True(UnsafeHarmonyUse.Note(""));
        Assert.Contains("?", UnsafeHarmonyUse.Describe(), StringComparison.Ordinal);
        Assert.Equal(1, UnsafeHarmonyUse.Count);
    }

    /// <summary>
    /// Job FHARM8, the item FHARM7 left owed. The refusal was Core-reloader-only, and Core's
    /// reloader is the one that does <em>not</em> run in production: hot reload there is
    /// Bootstrap's <c>ReloaderHost</c>, which cannot reference Core. So the mark travels the
    /// existing Bootstrap slot seam - Core writes <c>UnsafeHarmonySlot</c> reflectively through
    /// <c>UnsafeHarmonySlotLink</c>, both reloaders read it - and the two cannot disagree.
    /// </summary>
    [Fact]
    public void A_note_reaches_the_Bootstrap_table_the_production_reloader_reads()
    {
        Assert.False(UnsafeHarmonySlot.RefusesSwap("com.example.unsafe"));

        UnsafeHarmonyUse.Note("com.example.unsafe");

        // Core's reloader and Bootstrap's now give the same answer for the same plugin.
        Assert.True(UnsafeHarmonyUse.RefusesSwap("com.example.unsafe"));
        Assert.True(UnsafeHarmonySlot.RefusesSwap("com.example.unsafe"));
        Assert.True(UnsafeHarmonySlot.IsMarked("com.example.unsafe"));
        Assert.Equal(1, UnsafeHarmonySlot.Count);

        // And an unmarked plugin is still swappable in both.
        Assert.False(UnsafeHarmonyUse.RefusesSwap("com.example.safe"));
        Assert.False(UnsafeHarmonySlot.RefusesSwap("com.example.safe"));
    }

    /// <summary>
    /// Bootstrap's table is not collectible, so a mark taken by a previous Core generation
    /// survives a <c>.corereload</c> - and so does the Harmony pin it records. Core losing its
    /// own table (a fresh generation) must not un-refuse the swap.
    /// </summary>
    [Fact]
    public void A_mark_from_a_previous_Core_generation_still_refuses_the_swap()
    {
        UnsafeHarmonyUse.Note("com.example.unsafe");

        UnsafeHarmonyUse.ResetForTests(); // the new Core generation starts with an empty table

        Assert.True(UnsafeHarmonySlot.RefusesSwap("com.example.unsafe"));
        Assert.True(UnsafeHarmonyUse.RefusesSwap("com.example.unsafe"));
    }

    /// <summary>
    /// Bootstrap cannot reference Core, so the line the production reloader logs is a spelled-out
    /// mirror. Pinned against Core's the way <c>PluginUnloadSlot.Name</c> is pinned against the
    /// unload-reason enum: a reworded refusal in one file fails here rather than shipping two
    /// different explanations of the same event.
    /// </summary>
    [Fact]
    public void The_Bootstrap_refusal_line_is_a_faithful_mirror_of_Cores()
    {
        Assert.Equal(
            UnsafeHarmonyUse.FormatSwapRefused("MyMod.dll", "com.example.unsafe"),
            UnsafeHarmonySlot.FormatSwapRefused("MyMod.dll", "com.example.unsafe"));
        Assert.Equal(
            UnsafeHarmonyUse.FormatSwapRefused(null, null),
            UnsafeHarmonySlot.FormatSwapRefused(null, null));
    }

    /// <summary>Nothing collectible crosses the seam: the table holds strings, never delegates.</summary>
    [Fact]
    public void The_Bootstrap_table_holds_no_callback_that_could_pin_a_Core_generation()
    {
        foreach (var field in typeof(UnsafeHarmonySlot).GetFields(
                     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
        {
            Assert.False(
                typeof(Delegate).IsAssignableFrom(field.FieldType),
                "UnsafeHarmonySlot." + field.Name + " is a delegate; a delegate held by the "
                + "non-collectible Bootstrap pins the Core generation that supplied its target, "
                + "which is the leak this mark exists to report.");
        }
    }
}
