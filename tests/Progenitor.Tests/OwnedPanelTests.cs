using System;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Sample;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI29. A panel is hidden by paths its owner never runs — Escape through
/// <c>Ui.OnBootstrapKey</c>, a Core Uninstall, a plugin sweep — and the owner is not told. Every
/// module that answered "am I open" out of its own field therefore lied from the first Escape
/// until the process ended.
///
/// <para>These tests drive the real sweep (<see cref="UiApi.PressKeyForTests"/> →
/// <c>OnBootstrapKey</c>), not a stand-in for it, and they assert on the owners' own public
/// answers. The pre-FUI29 settings test called <c>panel.Hide()</c> with a comment saying that is
/// what Escape does, which is a test of the comment: it would have passed against a build where
/// the Escape sweep hid nothing at all.</para>
/// </summary>
[Collection("PluginLogger")]
public class OwnedPanelTests : IDisposable
{
    /// <summary>A well-formed operator fingerprint: 16 hex chars, the only shape TrustOperator pins.</summary>
    private static readonly string Fp = new string('b', 16);

    /// <summary>Unity <c>KeyCode.Escape</c>, as Bootstrap's <c>UiInputContext</c> forwards it.</summary>
    private const int EscapeKey = 27;

    /// <summary>Bootstrap's <c>UiInputContext.BackAction</c>: the gamepad / Back half of the same sweep.</summary>
    private const int BackAction = 17;

    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public OwnedPanelTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
        SampleUi.AttachForTests(_owner, "progenitor.sample");
        SampleLogPanel.Attach(_owner, "progenitor.sample");
    }

    public void Dispose()
    {
        ModManifestDenyPanel.Close();
        SettingsPanel.Close();
        SampleUi.Detach();
        SampleLogPanel.Detach();
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    // ---- the defect, one test per owner -------------------------------------------------------

    /// <summary>
    /// The required test of the whole job, on the owner the defect was measured on twice.
    /// </summary>
    [Fact]
    public void Escape_leaves_the_deny_panel_reporting_closed()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        Assert.True(ModManifestDenyPanel.IsOpen);
        Assert.Equal(UiHandleState.Shown, panel.State);

        UiApi.PressKeyForTests(EscapeKey, down: true);

        // The handle knows. Before this job the module did not, for the life of the process.
        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.False(ModManifestDenyPanel.IsOpen);
    }

    [Fact]
    public void Escape_leaves_the_settings_panel_reporting_closed_through_the_real_sweep()
    {
        var panel = SettingsPanel.Open();
        Assert.True(SettingsPanel.IsOpen);

        UiApi.PressKeyForTests(EscapeKey, down: true);

        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.False(SettingsPanel.IsOpen);
    }

    [Fact]
    public void Escape_leaves_the_sample_demo_panel_reporting_closed()
    {
        SampleUi.Run(SampleUi.Kind.Demo);
        Assert.Equal(1, SampleUi.LiveCount);

        UiApi.PressKeyForTests(EscapeKey, down: true);

        Assert.Equal(0, SampleUi.LiveCount);
    }

    [Fact]
    public void Escape_leaves_the_sample_log_panel_reporting_closed()
    {
        SampleLogPanel.Toggle();
        Assert.Equal(1, SampleLogPanel.LiveCount);

        UiApi.PressKeyForTests(EscapeKey, down: true);

        Assert.Equal(0, SampleLogPanel.LiveCount);
    }

    /// <summary>
    /// The Back action is the same sweep with the other key code, and it is the one a controller
    /// sends. An owner fixed for 27 and not for 17 would be fixed for keyboards only.
    /// </summary>
    [Fact]
    public void The_Back_action_hides_panels_the_same_way_and_the_owners_agree()
    {
        ModManifestDenyPanel.Open(Report());
        Assert.True(ModManifestDenyPanel.IsOpen);

        UiApi.PressKeyForTests(BackAction, down: true);

        Assert.False(ModManifestDenyPanel.IsOpen);
    }

    /// <summary>
    /// A key-up, and any key that is not one of the two, must change nothing — otherwise the
    /// tests above would pass over a sweep that hides panels on every keystroke.
    /// </summary>
    [Fact]
    public void A_key_up_and_an_unrelated_key_leave_the_panel_open()
    {
        ModManifestDenyPanel.Open(Report());

        UiApi.PressKeyForTests(EscapeKey, down: false);
        Assert.True(ModManifestDenyPanel.IsOpen);

        UiApi.PressKeyForTests(97, down: true);
        Assert.True(ModManifestDenyPanel.IsOpen);
    }

    // ---- the other paths that hide a panel behind its owner's back ----------------------------

    [Fact]
    public void A_Core_teardown_leaves_every_owner_reporting_closed()
    {
        ModManifestDenyPanel.Open(Report());
        SettingsPanel.Open();
        SampleUi.Run(SampleUi.Kind.Demo);
        SampleLogPanel.Toggle();

        // Ui.Uninstall disposes every live handle and tells no module either. This is the Core
        // swap half of the same gap: a next generation would inherit five owners saying "open".
        UiApi.Uninstall();

        Assert.False(ModManifestDenyPanel.IsOpen);
        Assert.False(SettingsPanel.IsOpen);
        Assert.Equal(0, SampleUi.LiveCount);
        Assert.Equal(0, SampleLogPanel.LiveCount);
    }

    [Fact]
    public void A_plugin_sweep_of_the_owner_leaves_the_sample_reporting_closed()
    {
        SampleUi.Run(SampleUi.Kind.Demo);
        Assert.Equal(1, SampleUi.LiveCount);

        // Ui.RemoveAll(owner) is what a plugin unload runs. Same shape: the handles go, the
        // module keeps its field.
        UiApi.RemoveAll(_owner);

        Assert.Equal(0, SampleUi.LiveCount);
    }

    // ---- what the honest answer buys ----------------------------------------------------------

    /// <summary>
    /// The user-visible half. With the old answer, the next press of a toggle spent itself
    /// "closing" a panel the player had already dismissed with Escape, and showed nothing.
    /// </summary>
    [Fact]
    public void Reopening_after_escape_shows_a_panel_on_the_first_press()
    {
        SampleUi.Toggle();
        Assert.Equal(1, SampleUi.LiveCount);

        UiApi.PressKeyForTests(EscapeKey, down: true);
        SampleUi.Toggle();

        Assert.Equal(1, SampleUi.LiveCount);
        Assert.Equal(2, _client.ShowCount);
    }

    /// <summary>
    /// The handle Escape hid is still a live GameObject, and it is released by the next Open —
    /// deliberately there and not in the IsOpen getter, which must have no side effects.
    /// </summary>
    [Fact]
    public void The_next_open_disposes_the_panel_escape_hid()
    {
        var first = ModManifestDenyPanel.Open(Report());
        UiApi.PressKeyForTests(EscapeKey, down: true);
        Assert.Equal(UiHandleState.Built, first.State);

        var second = ModManifestDenyPanel.Open(Report());

        Assert.Equal(UiHandleState.Disposed, first.State);
        Assert.Equal(UiHandleState.Shown, second.State);
        Assert.True(ModManifestDenyPanel.IsOpen);
        Assert.Contains(_log.Infos, l => l.Contains("deny panel closed.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reading the state must never destroy it. A diagnostic — or the operator status line — that
    /// asked "is it open" and got the panel disposed as a side effect would be a worse bug than
    /// the one being fixed.
    /// </summary>
    [Fact]
    public void Asking_whether_it_is_open_never_disposes_anything()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        UiApi.PressKeyForTests(EscapeKey, down: true);

        for (int i = 0; i < 5; i++)
            Assert.False(ModManifestDenyPanel.IsOpen);

        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.False(panel.IsDisposed);
    }

    /// <summary>The Escape sweep says what it did, so a live log can tell it from an owner's close.</summary>
    [Fact]
    public void The_escape_sweep_logs_that_it_hid_panels_and_told_nobody()
    {
        ModManifestDenyPanel.Open(Report());
        UiApi.PressKeyForTests(EscapeKey, down: true);

        Assert.Contains(_log.Infos, l =>
            l.Contains("hid 1 panel(s)", StringComparison.Ordinal)
            && l.Contains("owners are not told", StringComparison.Ordinal));
    }

    [Fact]
    public void The_sweep_stays_silent_when_there_was_nothing_shown()
    {
        UiApi.PressKeyForTests(EscapeKey, down: true);
        Assert.DoesNotContain(_log.Infos, l => l.Contains("owners are not told", StringComparison.Ordinal));
    }

    // ---- OwnedPanel itself --------------------------------------------------------------------

    [Fact]
    public void OwnedPanel_derives_open_from_the_handle_and_Take_hands_it_back()
    {
        var owned = new OwnedPanel();
        Assert.False(owned.IsOpen);
        Assert.Null(owned.Live);
        Assert.Null(owned.Held);
        Assert.Null(owned.Take());

        var panel = UiApi.Panel("Owned", _owner, "example.ui.test");
        Assert.Null(owned.Adopt(panel));

        // Built: adopted, not yet shown. Not open, and still held so it cannot leak.
        Assert.False(owned.IsOpen);
        Assert.Same(panel, owned.Held);

        UiApi.Show(panel);
        Assert.True(owned.IsOpen);
        Assert.Same(panel, owned.Live);

        panel.Hide();
        Assert.False(owned.IsOpen);
        Assert.Null(owned.Live);
        Assert.Same(panel, owned.Held);

        // Take releases regardless of whether it was still standing - a panel Escape hid is
        // exactly the one that still has to be disposed.
        Assert.Same(panel, owned.Take());
        Assert.Null(owned.Held);
        Assert.False(owned.IsOpen);
    }

    [Fact]
    public void OwnedPanel_Adopt_hands_back_the_handle_it_replaced()
    {
        var owned = new OwnedPanel();
        var first = UiApi.Panel("First", _owner, "example.ui.test");
        var second = UiApi.Panel("Second", _owner, "example.ui.test");

        Assert.Null(owned.Adopt(first));
        Assert.Same(first, owned.Adopt(second));
        Assert.Same(second, owned.Held);

        // Adopting the same handle twice replaces nothing, so nothing is handed back to dispose.
        Assert.Null(owned.Adopt(second));
        Assert.Throws<ArgumentNullException>(() => owned.Adopt(null!));
    }

    [Fact]
    public void A_disposed_panel_is_closed_and_a_disabled_one_counts_as_open()
    {
        var owned = new OwnedPanel();
        var panel = UiApi.Panel("Owned", _owner, "example.ui.test");
        owned.Adopt(panel);
        UiApi.Show(panel);
        panel.Dispose();
        Assert.False(owned.IsOpen);

        // A server, a skipped module or a failed overlay: no Unity object was ever made, so
        // re-opening on every key press would only churn. This is the rule SettingsPanel arrived
        // at first (R34 panel note 1) and the reason SampleUi's tests, which run without a client
        // runtime, still count a panel.
        UiApi.SetRuntimeForTests(new UiTests.FakeUiRuntime { IsClient = false });
        var server = new OwnedPanel();
        var disabled = UiApi.Panel("Owned", _owner, "example.ui.test");
        Assert.Equal(UiHandleState.Disabled, disabled.State);
        server.Adopt(disabled);
        Assert.True(server.IsOpen);
    }

    // ---- source pins --------------------------------------------------------------------------

    /// <summary>
    /// The test hook has to reach the production sweep. Pointed at anything else, every
    /// behavioral test above becomes a test of the stand-in — which is exactly how this defect
    /// survived a suite that already had an Escape test.
    /// </summary>
    [Fact]
    public void The_escape_test_hook_calls_the_production_sweep()
    {
        SourcePin.AssertStatementExactly(
            Source("src", "Progenitor.Core", "Ui", "Ui.cs"),
            "internal static void PressKeyForTests(int keyCode, bool down) => OnBootstrapKey(keyCode, down);");
    }

    /// <summary>
    /// The owners' open state is one line each, and it is a read of the handle. The line is
    /// character-identical in three files, so each pin reads only its own: deleting it from one
    /// while the other two stand fails exactly one of these three tests. That is why they are
    /// three tests and not one — fd11dd6 measured a twin defeating a pin on this very shape.
    /// </summary>
    [Fact]
    public void The_deny_panel_derives_its_open_state_from_the_handle() =>
        SourcePin.AssertStatementExactly(
            Source("src", "Progenitor.Core", "Networking", "ModManifestDenyPanel.cs"),
            "public static bool IsOpen => Owned.IsOpen;");

    [Fact]
    public void The_settings_panel_derives_its_open_state_from_the_handle() =>
        SourcePin.AssertStatementExactly(
            Source("src", "Progenitor.Core", "Ui", "SettingsPanel.cs"),
            "public static bool IsOpen => Owned.IsOpen;");

    /// <summary>
    /// A row refresh belongs to a panel on screen. Pinned per <em>method body</em>, because the
    /// same line stands in the settings panel's twin of this method and a file-wide pin would be
    /// satisfied by the wrong one.
    /// </summary>
    [Fact]
    public void The_settings_row_refresh_is_gated_on_the_panel_still_being_on_screen() =>
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(
                Source("src", "Progenitor.Core", "Ui", "SettingsPanel.cs"),
                "private static void Refresh(string rowId, string? notice)"),
            "if (!Owned.IsOpen) return;");

    /// <summary>
    /// The negative half: no owner keeps a panel field of its own beside the derivation. A second
    /// representation is what this job removed, and re-adding one is how it comes back — a
    /// positive pin over <c>IsOpen</c> cannot see that.
    /// </summary>
    [Fact]
    public void No_owner_shadows_the_handle_with_a_panel_field_of_its_own()
    {
        string[][] owners =
        {
            new[] { "src", "Progenitor.Core", "Networking", "ModManifestDenyPanel.cs" },
            new[] { "src", "Progenitor.Core", "Ui", "SettingsPanel.cs" },
            new[] { "samples", "Progenitor.Sample", "SampleUi.cs" },
            new[] { "samples", "Progenitor.Sample", "SampleLogPanel.cs" },
        };

        foreach (string[] parts in owners)
        {
            SourcePin.AssertNoStatementContaining(Source(parts), "IUiPanel? _panel");
            SourcePin.AssertNoStatementContaining(Source(parts), "IUiPanel _panel");
        }
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private static string Source(params string[] parts) => SourcePin.ReadFile(parts);

    private static ModDenyReport Report() =>
        new ModDenyReport(
            "Cannot join — server mods",
            "127.0.0.1:9976",
            "Night Garden",
            "Ada",
            Fp,
            descriptorReceived: false,
            new[]
            {
                new ModDenyRow(
                    "progenitor.sample",
                    "1.0.0",
                    null,
                    ModDenyOrigin.ServerRequires,
                    "the server requires it"),
            });

}
