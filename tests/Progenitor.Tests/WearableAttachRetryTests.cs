using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableAttachRetryTests
{
    [Fact]
    public void IncompatibleBodyDoesNotRetryEveryFrameButChangedReferencesRecoverOnNextReferenceTick()
    {
        var retry = new WearableAttachRetry();
        const ulong incompatible = 41;
        const ulong streamed = 42;
        Assert.True(retry.ShouldInspect(false, 100));
        Assert.True(retry.ShouldAttempt(incompatible, 100));
        retry.Failed(incompatible, 100);

        for (int frame = 101; frame < 160; frame++)
        {
            Assert.False(retry.ShouldInspect(false, frame));
            if (frame % 8 != 0) continue;
            Assert.True(retry.ShouldInspect(true, frame));
            Assert.False(retry.ShouldAttempt(incompatible, frame));
        }

        // A newly streamed mesh changes the fingerprint and bypasses the delay.
        Assert.True(retry.ShouldInspect(true, 128));
        Assert.True(retry.ShouldAttempt(streamed, 128));
        Assert.True(retry.ShouldInspect(false, 160));
        Assert.True(retry.ShouldAttempt(incompatible, 160));
    }

    [Fact]
    public void ReplacementPendingStateCanAttemptImmediately()
    {
        var oldModelOrRecipe = new WearableAttachRetry();
        oldModelOrRecipe.Failed(41, 100);
        Assert.False(oldModelOrRecipe.ShouldAttempt(41, 101));

        // Pass removes the old PendingHide when its model key is absent or its
        // graphics reference changes, so the replacement owns a fresh policy.
        var replacement = new WearableAttachRetry();
        Assert.True(replacement.ShouldInspect(false, 101));
        Assert.True(replacement.ShouldAttempt(41, 101));
    }
}
