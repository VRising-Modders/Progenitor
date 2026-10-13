using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI12 (research evidence 161). The press detector behind <c>UnityKeyInputSource</c>.
///
/// <para>
/// The bug this covers: every panel keybind needed two presses, because the only detector was
/// <c>ButtonControl.wasPressedThisFrame</c> — a flag scoped to the input update step that
/// processed the press, not to the frame we sample on. When the game advanced that step twice
/// between two of our samples, the press was already gone by the time we looked. The recovery
/// clause reads the key's held state instead, so these tests are written as frame-by-frame
/// samples with the flag deliberately withheld.
/// </para>
/// </summary>
public class KeyEdgeTrackerTests
{
    /// <summary>The flag is trusted whenever the Input System does set it.</summary>
    [Fact]
    public void RawFlagFires()
    {
        var edges = new KeyEdgeTracker();

        Assert.True(edges.Observe(UiKey.F10, frame: 10, isPressed: true, wasPressedThisFrame: true));
        Assert.Equal(0, edges.EdgeOnlyPresses);
    }

    /// <summary>
    /// The bug, reproduced: the key is up on one sample and down on the next with the flag never
    /// set. The old code returned false here and the player pressed again.
    /// </summary>
    [Fact]
    public void HeldEdgeRecoversAPressTheFlagMissed()
    {
        var edges = new KeyEdgeTracker();

        Assert.False(edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edges.Observe(UiKey.F10, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(1, edges.EdgeOnlyPresses);
    }

    /// <summary>A key that stays down fires once, not once per frame.</summary>
    [Fact]
    public void HoldingTheKeyFiresOnce()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edges.Observe(UiKey.F10, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F10, frame: 12, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F10, frame: 13, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>Release then press again is a second edge.</summary>
    [Fact]
    public void ReleaseThenPressFiresAgain()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edges.Observe(UiKey.F10, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F10, frame: 12, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edges.Observe(UiKey.F10, frame: 13, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(2, edges.EdgeOnlyPresses);
    }

    /// <summary>
    /// The whole point of the contiguity rule. The pump skips the source entirely while every
    /// bind is suppressed by a captured panel, so state goes stale; a key still held when the
    /// panel closes must not read as a fresh press.
    /// </summary>
    [Fact]
    public void AGapDoesNotTurnAHeldKeyIntoAPress()
    {
        var edges = new KeyEdgeTracker();

        Assert.False(edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F10, frame: 40, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(0, edges.EdgeOnlyPresses);
    }

    /// <summary>
    /// After a gap the tracker is re-seeded, so the next genuine press still fires: the gap costs
    /// at most the one press that happened inside it, never every press after it.
    /// </summary>
    [Fact]
    public void AGapReseedsRatherThanLatchingOff()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.False(edges.Observe(UiKey.F10, frame: 40, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F10, frame: 41, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edges.Observe(UiKey.F10, frame: 42, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// The flag is still trusted across a gap, because it carries its own proof that the press
    /// happened on this sample.
    /// </summary>
    [Fact]
    public void TheRawFlagStillFiresAcrossAGap()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edges.Observe(UiKey.F10, frame: 40, isPressed: true, wasPressedThisFrame: true));
    }

    /// <summary>
    /// Two chords sharing a main key — the Sample binds <c>F9</c> and <c>Ctrl+F9</c> — are asked
    /// about in the same frame, and both must get the same answer. Re-deriving the edge for the
    /// second ask would compare against state the first ask had already advanced.
    /// </summary>
    [Fact]
    public void TwoAsksInOneFrameGetTheSameAnswer()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F9, frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edges.Observe(UiKey.F9, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.True(edges.Observe(UiKey.F9, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.True(edges.Observe(UiKey.F9, frame: 11, isPressed: true, wasPressedThisFrame: false));

        // The repeats are the same verdict, not three separate recoveries.
        Assert.Equal(1, edges.EdgeOnlyPresses);
    }

    /// <summary>A frame where nothing fired is also cached, so a repeat ask stays false.</summary>
    [Fact]
    public void ARepeatAskOnAQuietFrameStaysFalse()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F9, frame: 10, isPressed: false, wasPressedThisFrame: false);
        edges.Observe(UiKey.F9, frame: 11, isPressed: true, wasPressedThisFrame: false);
        Assert.False(edges.Observe(UiKey.F9, frame: 12, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F9, frame: 12, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// A tap shorter than one sample reads held=false with the flag set. It fires once, and the
    /// release on the next sample is not a second edge.
    /// </summary>
    [Fact]
    public void ATapInsideOneFrameFiresOnceAndDoesNotRepeat()
    {
        var edges = new KeyEdgeTracker();

        Assert.True(edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: true));
        Assert.False(edges.Observe(UiKey.F10, frame: 11, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edges.Observe(UiKey.F10, frame: 12, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>Keys are tracked independently.</summary>
    [Fact]
    public void KeysDoNotShareState()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F9, frame: 10, isPressed: false, wasPressedThisFrame: false);
        edges.Observe(UiKey.F10, frame: 10, isPressed: true, wasPressedThisFrame: true);

        Assert.True(edges.Observe(UiKey.F9, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edges.Observe(UiKey.F10, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(2, edges.TrackedKeys);
    }

    /// <summary>
    /// A keyboard swap drops everything: held state read from the old device says nothing about
    /// the new one, and the contiguity test must not compare across it.
    /// </summary>
    [Fact]
    public void ADeviceChangeClearsHeldState()
    {
        var edges = new KeyEdgeTracker();

        edges.NoteDevice(1);
        edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false);

        edges.NoteDevice(2);
        Assert.False(edges.Observe(UiKey.F10, frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(1, edges.TrackedKeys);
    }

    /// <summary>The same id twice is not a swap; state survives.</summary>
    [Fact]
    public void TheSameDeviceIdKeepsHeldState()
    {
        var edges = new KeyEdgeTracker();

        edges.NoteDevice(7);
        edges.Observe(UiKey.F10, frame: 10, isPressed: false, wasPressedThisFrame: false);
        edges.NoteDevice(7);
        Assert.True(edges.Observe(UiKey.F10, frame: 11, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// The counter is the diagnostic <c>UnityKeyInputSource</c> logs once from. It counts only
    /// presses the flag missed, so a healthy build leaves it at zero.
    /// </summary>
    [Fact]
    public void EdgeOnlyCounterIgnoresPressesTheFlagCaught()
    {
        var edges = new KeyEdgeTracker();

        edges.Observe(UiKey.F10, frame: 10, isPressed: true, wasPressedThisFrame: true);
        edges.Observe(UiKey.F10, frame: 11, isPressed: false, wasPressedThisFrame: false);
        edges.Observe(UiKey.F10, frame: 12, isPressed: true, wasPressedThisFrame: true);

        Assert.Equal(0, edges.EdgeOnlyPresses);
    }
}
