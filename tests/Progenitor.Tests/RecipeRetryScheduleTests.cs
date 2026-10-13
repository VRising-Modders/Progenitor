using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The recipe frame retry must not attempt a failing apply every frame, and must stop.
/// A failed by-name source lookup cost 3–36 ms per frame on the client before this budget.
/// </summary>
public class RecipeRetryScheduleTests
{
    [Fact]
    public void Attempts_back_off_to_powers_of_two_then_give_up()
    {
        var s = new RecipeRetrySchedule();
        s.Note(7);
        var attempt = new List<int>();
        var gaveUp = new List<int>();
        var attemptFrames = new List<int>();
        int gaveUpAt = -1;
        for (int frame = 1; frame <= RecipeRetrySchedule.GiveUpFrames + 10; frame++)
        {
            s.Step(attempt, gaveUp);
            if (attempt.Count > 0) attemptFrames.Add(frame);
            if (gaveUp.Count > 0) { Assert.Equal(-1, gaveUpAt); gaveUpAt = frame; }
        }
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048 }, attemptFrames);
        Assert.Equal(RecipeRetrySchedule.GiveUpFrames, gaveUpAt);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Note_restarts_the_budget_and_Remove_empties_it()
    {
        var s = new RecipeRetrySchedule();
        var attempt = new List<int>();
        var gaveUp = new List<int>();
        s.Note(1);
        for (int i = 0; i < 5; i++) s.Step(attempt, gaveUp);
        s.Note(1);
        s.Step(attempt, gaveUp);
        Assert.Equal(new[] { 1 }, attempt);
        Assert.True(s.Remove(1));
        s.Step(attempt, gaveUp);
        Assert.Empty(attempt);
        Assert.Empty(gaveUp);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Frame_retry_subscribes_only_while_pending()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Content", "RecipeApply.cs"));
        int hooks = src.IndexOf("private static void EnsureHooksNoLock()", System.StringComparison.Ordinal);
        int end = src.IndexOf("private static void TryApplyNow", hooks, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Plugin.Frame +=", src.Substring(hooks, end - hooks), System.StringComparison.Ordinal);
        SourcePin.AssertUncommented(src, "Pending.Step(AttemptScratch, GaveUpScratch)");
        SourcePin.AssertUncommented(src, "if (Pending.Count == 0) StopFrameRetryNoLock();");
    }
}
