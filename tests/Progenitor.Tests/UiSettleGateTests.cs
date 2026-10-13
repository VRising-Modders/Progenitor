using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The deferred re-measure's owed list, behaviorally — job FUI33, closing R48 finding 2.
///
/// <para>Job FUI32 wrote this machinery inline in <c>UnityUiRuntime.cs</c>, which the test project
/// does not compile, so the only coverage it had was source pins asserting that certain lines
/// exist. R48's sentence is the one worth keeping: a pin that says <c>Plugin.Frame += SettlePump;</c>
/// is on its own line cannot notice that the line after it deletes what it added — and the line
/// after it did, for a panel whose own re-layout armed it again from inside the tick the pump was
/// about to act on.</para>
///
/// <para>Everything here drives <see cref="UiSettleGate"/> through <see cref="ISettlePanel"/>, which
/// is the whole point of the extraction: the four transitions a waiting panel has are decisions,
/// and none of them needs a <c>Canvas</c>.</para>
/// </summary>
[Collection("PluginLogger")]
public class UiSettleGateTests : IDisposable
{
    private readonly TestLogSource _log = new();

    public UiSettleGateTests()
    {
        Plugin.Logger = _log;
        UiSettleGate.Reset();
    }

    public void Dispose()
    {
        UiSettleGate.Reset();
        Plugin.Logger = null;
    }

    /// <summary>
    /// A panel scripted with what its <c>SettleTick</c> answers, frame by frame. <c>ReArm</c> is
    /// the interesting one: it models <c>SettleTick</c>'s <c>Relayout</c> branch, which runs a
    /// layout pass that can call <c>ArmSettle</c> again and then returns <c>false</c> regardless.
    /// </summary>
    private sealed class FakePanel : ISettlePanel
    {
        private readonly Func<int, bool> _answer;

        internal FakePanel(Func<int, bool> answer) => _answer = answer;

        /// <summary>When set, the tick arms itself again before answering.</summary>
        internal bool ReArm { get; set; }

        internal int Ticks { get; private set; }

        public bool SettleTick()
        {
            Ticks++;
            if (ReArm) UiSettleGate.Arm(this, null);
            return _answer(Ticks);
        }
    }

    private static FakePanel Waiting() => new(_ => true);

    private static FakePanel SettlesOn(int tick) => new(n => n < tick);

    [Fact]
    public void The_first_arm_asks_for_the_frame_pass_and_the_rest_do_not()
    {
        var a = Waiting();
        var b = Waiting();

        Assert.True(UiSettleGate.Arm(a, null));
        Assert.False(UiSettleGate.Arm(b, null));
        Assert.False(UiSettleGate.Arm(a, null));

        Assert.Equal(2, UiSettleGate.Count);
        Assert.True(UiSettleGate.PumpArmed);
        Assert.Contains("settleOwed=2", UiSettleGate.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_panel_still_waiting_is_ticked_again_and_the_pass_is_kept()
    {
        var panel = Waiting();
        UiSettleGate.Arm(panel, null);

        Assert.False(UiSettleGate.Pump());
        Assert.False(UiSettleGate.Pump());

        Assert.Equal(2, panel.Ticks);
        Assert.Equal(1, UiSettleGate.Count);
        Assert.True(UiSettleGate.PumpArmed);
    }

    [Fact]
    public void A_panel_that_settles_is_dropped_and_the_pass_is_given_back()
    {
        var panel = SettlesOn(2);
        UiSettleGate.Arm(panel, null);

        Assert.False(UiSettleGate.Pump());
        Assert.True(UiSettleGate.Pump());

        Assert.Equal(0, UiSettleGate.Count);
        Assert.False(UiSettleGate.PumpArmed);

        // And a panel that is no longer owed is not ticked by a later pass.
        UiSettleGate.Pump();
        Assert.Equal(2, panel.Ticks);
    }

    /// <summary>
    /// <b>R48 finding 2.</b> <c>SettleTick</c>'s re-layout branch returns <c>false</c> after
    /// running the layout pass, and that pass logs <c>settle=armed</c> and arms the panel again
    /// whenever it too measured against a fallback. The old pump dropped the entry anyway, on the
    /// strength of that <c>false</c>, leaving the panel on the fallback size for the rest of the
    /// session with the log's last word being that a re-measure was coming.
    /// </summary>
    [Fact]
    public void A_panel_re_armed_by_its_own_tick_is_not_dropped_by_the_pass_that_ticked_it()
    {
        var panel = new FakePanel(_ => false) { ReArm = true };
        UiSettleGate.Arm(panel, null);

        Assert.False(UiSettleGate.Pump());

        Assert.Equal(1, UiSettleGate.Count);
        Assert.True(UiSettleGate.PumpArmed);

        // Still owed means still ticked: the re-measure the log promised actually happens.
        UiSettleGate.Pump();
        Assert.Equal(2, panel.Ticks);
    }

    /// <summary>
    /// And the keep is for that pass only. A tick that stops re-arming — the canvas answered, or
    /// the budget ran out — is dropped by the very next pass, so the re-arm rule cannot become a
    /// panel that is owed for ever.
    /// </summary>
    [Fact]
    public void The_keep_lasts_one_pass_and_the_wait_stays_bounded()
    {
        var panel = new FakePanel(_ => false) { ReArm = true };
        UiSettleGate.Arm(panel, null);

        for (int i = 0; i < 5; i++)
        {
            Assert.False(UiSettleGate.Pump());
            Assert.Equal(1, UiSettleGate.Count);
        }

        panel.ReArm = false;
        Assert.True(UiSettleGate.Pump());
        Assert.Equal(0, UiSettleGate.Count);
        Assert.False(UiSettleGate.PumpArmed);
    }

    /// <summary>
    /// The give-up bound, driven by the state machine the production tick uses. A panel whose
    /// canvas never becomes readable waits <see cref="RulerSettle.MaxFrames"/> frames, says so
    /// once and is dropped — it does not re-measure for the rest of the session.
    /// </summary>
    [Fact]
    public void A_canvas_that_never_answers_gives_up_inside_the_budget()
    {
        var fallback = PanelRuler.Choose(0f, designSpace: true, 2160f, 1080f);
        int gaveUp = 0;
        int frames = 0;

        var panel = new FakePanel(_ =>
        {
            frames++;
            switch (RulerSettle.Step(fallback, frames, RulerSettle.MaxFrames))
            {
                case RulerSettleStep.GiveUp:
                    gaveUp++;
                    return false;
                case RulerSettleStep.Relayout:
                    return false;
                default:
                    return true;
            }
        });

        UiSettleGate.Arm(panel, null);
        for (int i = 0; i < RulerSettle.MaxFrames + 20; i++)
        {
            if (UiSettleGate.Pump()) break;
        }

        Assert.Equal(1, gaveUp);
        Assert.Equal(RulerSettle.MaxFrames, panel.Ticks);
        Assert.Equal(0, UiSettleGate.Count);
        Assert.False(UiSettleGate.PumpArmed);
    }

    /// <summary>A panel hidden or released mid-wait leaves quietly, and nothing is logged for it.</summary>
    [Fact]
    public void A_panel_dropped_mid_wait_is_never_ticked_again()
    {
        var panel = Waiting();
        UiSettleGate.Arm(panel, null);
        UiSettleGate.Pump();

        UiSettleGate.Drop(panel);

        Assert.True(UiSettleGate.Pump());
        Assert.Equal(1, panel.Ticks);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void A_tick_that_throws_drops_its_panel_and_says_so_once()
    {
        var thrower = new FakePanel(_ => throw new InvalidOperationException("interop is down"));
        var other = Waiting();
        UiSettleGate.Arm(thrower, null);
        UiSettleGate.Arm(other, null);

        Assert.False(UiSettleGate.Pump());

        Assert.Equal(1, UiSettleGate.Count);
        Assert.Single(_log.Warnings);
        Assert.Contains("interop is down", _log.Warnings[0], StringComparison.Ordinal);

        // The throw belongs to one panel: the pass carries on over the rest.
        Assert.Equal(1, other.Ticks);
    }

    /// <summary>
    /// A subscribe that threw means the pass is not held, whatever the latch said. Without this
    /// the latch would claim the pump is running and no later arm would ever subscribe.
    /// </summary>
    [Fact]
    public void A_failed_subscribe_un_latches_so_the_next_arm_tries_again()
    {
        var a = Waiting();
        Assert.True(UiSettleGate.Arm(a, null));
        UiSettleGate.SubscribeFailed();
        Assert.False(UiSettleGate.PumpArmed);

        Assert.True(UiSettleGate.Arm(Waiting(), null));
    }

    /// <summary>
    /// R48 finding 6: the teardown. <c>Reset</c> gives the frame pass back through the disarm the
    /// owner handed in, clears the latch, and leaves a gate that arms from scratch — the state a
    /// second Core generation has to start from.
    /// </summary>
    [Fact]
    public void Reset_gives_the_frame_pass_back_and_clears_the_latch()
    {
        int disarms = 0;
        UiSettleGate.Arm(Waiting(), () => disarms++);
        UiSettleGate.Arm(Waiting(), null);

        UiSettleGate.Reset();

        Assert.Equal(1, disarms);
        Assert.Equal(0, UiSettleGate.Count);
        Assert.False(UiSettleGate.PumpArmed);
        Assert.True(UiSettleGate.Arm(Waiting(), null));
    }

    /// <summary>
    /// And a reset with nothing armed asks for no disarm at all: an uninstall on a client that
    /// never opened a panel must not touch a subscription that was never taken.
    /// </summary>
    [Fact]
    public void Reset_with_nothing_armed_disarms_nothing()
    {
        int disarms = 0;
        UiSettleGate.Arm(Waiting(), () => disarms++);
        UiSettleGate.Reset();
        UiSettleGate.Reset();

        Assert.Equal(1, disarms);
        Assert.Empty(_log.Warnings);
    }

    /// <summary>A disarm that throws is reported and does not escape the teardown.</summary>
    [Fact]
    public void A_disarm_that_throws_is_reported_and_swallowed()
    {
        UiSettleGate.Arm(Waiting(), () => throw new InvalidOperationException("frame is gone"));

        UiSettleGate.Reset();

        Assert.Single(_log.Warnings);
        Assert.Contains("frame is gone", _log.Warnings[0], StringComparison.Ordinal);
        Assert.False(UiSettleGate.PumpArmed);
    }

    /// <summary>
    /// The production wiring, which cannot be compiled here: the pump's walk is the gate's, the
    /// drop is the gate's, and the subscription is still taken on its own line in
    /// <c>ArmSettle</c> — a re-measure driven by anything but <c>Plugin.Frame</c> would never run
    /// for a panel whose plugin did not register for the hover pass.
    /// </summary>
    [Fact]
    public void The_runtime_drives_the_gate_and_owns_only_the_subscription()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");

        string arm = SourcePin.MethodBody(runtime, "private static void ArmSettle(");
        SourcePin.AssertStatementExactly(arm, "if (!UiSettleGate.Arm(panel, SettleDisarm)) return;");
        SourcePin.AssertStatementExactly(arm, "Plugin.Frame += SettlePump;");

        string pump = SourcePin.MethodBody(runtime, "private static void SettlePump()");
        SourcePin.AssertStatementExactly(pump, "if (!UiSettleGate.Pump()) return;");

        // And the teardown R48 finding 6 asked for, on Uninstall's list and in the pin line.
        string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(ui, "internal static void Uninstall()"), "UiSettleGate.Reset();");
        SourcePin.AssertStatement(
            SourcePin.MethodBody(ui, "internal static string DescribePins()"),
            "holds.Add(UiSettleGate.Describe());");
    }
}
