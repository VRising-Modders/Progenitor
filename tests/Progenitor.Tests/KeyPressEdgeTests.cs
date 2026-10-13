using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI16 (R43 finding 1, research evidence 161). The press detector behind Bootstrap's
/// Escape forwarding in <c>UiInputContext.Update</c>.
///
/// <para>
/// The bug this covers is the closing half of evidence 161. FUI12 fixed Core's keybinds, so the
/// settings panel opened on one press of F10 — but <c>UiInputContext.Update</c> still read
/// <c>kb.escapeKey.wasPressedThisFrame</c> bare from a MonoBehaviour <c>Update</c>, the exact
/// detector at the exact cadence that drops the presses whose input update step was never
/// sampled. Closing the panel still took two presses. These tests are written as frame-by-frame
/// samples with the flag deliberately withheld, the same way <see cref="KeyEdgeTrackerTests"/>
/// covers Core's version; the two types share the rule, not the code, and the reasons are in
/// <see cref="KeyPressEdge"/>'s remarks.
/// </para>
/// </summary>
public class KeyPressEdgeTests
{
    /// <summary>The flag is trusted whenever the Input System does set it.</summary>
    [Fact]
    public void RawFlagFires()
    {
        var edge = new KeyPressEdge();

        Assert.True(edge.Observe(frame: 10, isPressed: true, wasPressedThisFrame: true));
        Assert.Equal(0, edge.EdgeOnlyPresses);
    }

    /// <summary>
    /// The bug, reproduced: Escape is up on one sample and down on the next with the flag never
    /// set. The old <c>Update</c> returned here without forwarding anything and the panel stayed
    /// open.
    /// </summary>
    [Fact]
    public void HeldEdgeRecoversAPressTheFlagMissed()
    {
        var edge = new KeyPressEdge();

        Assert.False(edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(1, edge.EdgeOnlyPresses);
    }

    /// <summary>
    /// Escape held down closes the panel once, not once per frame. A repeat here would forward
    /// Escape and Back into Core on every frame of a held key.
    /// </summary>
    [Fact]
    public void HoldingTheKeyFiresOnce()
    {
        var edge = new KeyPressEdge();

        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edge.Observe(frame: 12, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edge.Observe(frame: 13, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(1, edge.EdgeOnlyPresses);
    }

    /// <summary>Release then press again is a second edge.</summary>
    [Fact]
    public void ReleaseThenPressFiresAgain()
    {
        var edge = new KeyPressEdge();

        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edge.Observe(frame: 12, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edge.Observe(frame: 13, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(2, edge.EdgeOnlyPresses);
    }

    /// <summary>
    /// The contiguity rule, which this reader needs for its own reason: <c>Update</c> returns
    /// before sampling whenever capture is off or the Core slot is null, so state goes stale
    /// across every closed panel and every Core swap. Escape still held when capture comes back
    /// must not read as a fresh press and close the panel that just opened.
    /// </summary>
    [Fact]
    public void AGapDoesNotTurnAHeldKeyIntoAPress()
    {
        var edge = new KeyPressEdge();

        Assert.False(edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false));
        Assert.False(edge.Observe(frame: 40, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(0, edge.EdgeOnlyPresses);
    }

    /// <summary>
    /// After a gap the tracker is re-seeded, so the next genuine press still fires: the gap costs
    /// at most the one press that happened inside it, never every press after it.
    /// </summary>
    [Fact]
    public void AGapReseedsRatherThanLatchingOff()
    {
        var edge = new KeyPressEdge();

        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.False(edge.Observe(frame: 40, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edge.Observe(frame: 41, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edge.Observe(frame: 42, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// The flag is still trusted across a gap, because it carries its own proof that the press
    /// happened on this sample.
    /// </summary>
    [Fact]
    public void TheRawFlagStillFiresAcrossAGap()
    {
        var edge = new KeyPressEdge();

        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edge.Observe(frame: 40, isPressed: true, wasPressedThisFrame: true));
    }

    /// <summary>
    /// A tap shorter than one sample reads held=false with the flag set. It fires once, and the
    /// release on the next sample is not a second edge.
    /// </summary>
    [Fact]
    public void ATapInsideOneFrameFiresOnceAndDoesNotRepeat()
    {
        var edge = new KeyPressEdge();

        Assert.True(edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: true));
        Assert.False(edge.Observe(frame: 11, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edge.Observe(frame: 12, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// Deliberately the opposite of <c>KeyEdgeTracker</c>'s per-frame verdict cache. Nothing asks
    /// twice in one frame here — one key, one reader — and if a second <c>UiInputContext</c>
    /// component ever did sample the same frame, a cache would hand it the same <c>true</c> and
    /// close two panels on one press. The second sample must see the key already recorded as
    /// held.
    /// </summary>
    [Fact]
    public void ASecondSampleInTheSameFrameDoesNotFireAgain()
    {
        var edge = new KeyPressEdge();

        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        Assert.True(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.False(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(1, edge.EdgeOnlyPresses);
    }

    /// <summary>
    /// A keyboard swap drops held state: state read from a dead device says nothing about the
    /// live one, and the contiguity test must not compare across it.
    /// </summary>
    [Fact]
    public void ADeviceChangeClearsHeldState()
    {
        var edge = new KeyPressEdge();

        edge.NoteDevice(1);
        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);

        edge.NoteDevice(2);
        Assert.False(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(0, edge.EdgeOnlyPresses);
    }

    /// <summary>The same id twice is not a swap; state survives.</summary>
    [Fact]
    public void TheSameDeviceIdKeepsHeldState()
    {
        var edge = new KeyPressEdge();

        edge.NoteDevice(7);
        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        edge.NoteDevice(7);
        Assert.True(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// The first device id seen is not a swap either — the tracker starts with no device, and a
    /// keyboard that never changes must not have its very first press eaten.
    /// </summary>
    [Fact]
    public void TheFirstDeviceIdDoesNotEatThePressAfterIt()
    {
        var edge = new KeyPressEdge();

        edge.NoteDevice(3);
        edge.Observe(frame: 10, isPressed: false, wasPressedThisFrame: false);
        edge.NoteDevice(3);
        Assert.True(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// An unreadable <c>Time.frameCount</c> must degrade to the raw flag, not to frame zero. A
    /// fabricated number in the contiguity test is R43 finding 2's failure in Core; this reader
    /// does not repeat it.
    /// </summary>
    [Fact]
    public void AnUnreadableFrameFallsBackToTheRawFlag()
    {
        var edge = new KeyPressEdge();

        Assert.True(edge.Observe(KeyPressEdge.NoFrame, isPressed: true, wasPressedThisFrame: true));
        Assert.False(edge.Observe(KeyPressEdge.NoFrame, isPressed: true, wasPressedThisFrame: false));
        Assert.Equal(0, edge.EdgeOnlyPresses);
    }

    /// <summary>
    /// A sample with no frame number cannot be one half of a contiguous pair in either direction,
    /// so it neither fakes an edge nor latches the tracker off once the frame reads again.
    /// </summary>
    [Fact]
    public void AnUnreadableFrameNeitherFakesNorLatches()
    {
        var edge = new KeyPressEdge();

        // Up at NoFrame, down at a real frame: not contiguous, no edge invented.
        edge.Observe(KeyPressEdge.NoFrame, isPressed: false, wasPressedThisFrame: false);
        Assert.False(edge.Observe(frame: 11, isPressed: true, wasPressedThisFrame: false));

        // And the very next real pair still works, so one bad read costs one press, not all of them.
        Assert.False(edge.Observe(frame: 12, isPressed: false, wasPressedThisFrame: false));
        Assert.True(edge.Observe(frame: 13, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// <c>NoFrame</c> is <c>int.MinValue</c>, so a naive <c>frame - 1</c> comparison would wrap.
    /// The sample immediately "after" it by arithmetic must still not count as contiguous.
    /// </summary>
    [Fact]
    public void TheNoFrameSentinelDoesNotWrapIntoContiguity()
    {
        var edge = new KeyPressEdge();

        edge.Observe(KeyPressEdge.NoFrame, isPressed: false, wasPressedThisFrame: false);
        Assert.False(edge.Observe(KeyPressEdge.NoFrame + 1, isPressed: true, wasPressedThisFrame: false));
    }

    /// <summary>
    /// The counter is the diagnostic <c>UiInputContext</c> logs once from, and the
    /// <c>escapeEdgeOnly=</c> field on the pin line. It counts only presses the flag missed, so a
    /// healthy build leaves it at zero.
    /// </summary>
    [Fact]
    public void EdgeOnlyCounterIgnoresPressesTheFlagCaught()
    {
        var edge = new KeyPressEdge();

        edge.Observe(frame: 10, isPressed: true, wasPressedThisFrame: true);
        edge.Observe(frame: 11, isPressed: false, wasPressedThisFrame: false);
        edge.Observe(frame: 12, isPressed: true, wasPressedThisFrame: true);

        Assert.Equal(0, edge.EdgeOnlyPresses);
    }
}
