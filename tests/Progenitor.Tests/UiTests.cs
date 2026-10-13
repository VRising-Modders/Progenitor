using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the UI toolkit: name-prefix ownership, the Bootstrap-style
/// ushort slot dispatch, and the Panel → Show → Hide → Dispose state machine.
/// Unity stays behind <see cref="IUiRuntime"/>; this file supplies a fake.
/// </summary>
[Collection("PluginLogger")]
public class UiTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly FakeUiRuntime _client = new() { IsClient = true };
    private readonly object _owner = new Owner("example.ui.test");

    public UiTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void RootName_uses_progenitor_ui_prefix_and_sanitizes_separators()
    {
        Assert.Equal("progenitor-ui-example.sample", UiOwnership.RootName("example.sample"));
        Assert.Equal("progenitor-ui-unowned", UiOwnership.RootName(null));
        Assert.Equal("progenitor-ui-unowned", UiOwnership.RootName("  "));
        Assert.Equal("a-b", UiOwnership.Sanitize("a/b"));
        Assert.True(UiOwnership.IsOwned("progenitor-ui-example.sample"));
        Assert.True(UiOwnership.IsOwnedBy("progenitor-ui-example.sample", "example.sample"));
        Assert.True(UiOwnership.IsOwnedBy("progenitor-ui-example.sample-panel", "example.sample"));
        Assert.False(UiOwnership.IsOwnedBy("progenitor-ui-example.sample.extra", "example.sample"));
        Assert.False(UiOwnership.IsOwnedBy("progenitor-ui-example.sample.extra-panel", "example.sample"));
        Assert.True(UiOwnership.IsOwnedBy("progenitor-ui-example.sample.extra", "example.sample.extra"));
        Assert.False(UiOwnership.IsOwnedBy("progenitor-ui-other", "example.sample"));
        Assert.False(UiOwnership.IsOwned("progenitor-effects-x"));
        Assert.Equal(5000, UiOwnership.OverlaySorting);
    }

    [Fact]
    public void Slot_dispatch_invokes_the_bound_click_handler()
    {
        int clicks = 0;
        ushort id = UiSlotTable.Alloc(_owner);
        UiSlotTable.BindClick(id, () => clicks++);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(2, clicks);
        UiSlotTable.Release(id);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void Slot_dispatch_bool_float_text_and_pointer()
    {
        bool? flag = null;
        float number = 0;
        string? text = null;
        int enters = 0;
        int exits = 0;
        ushort id = UiSlotTable.Alloc(_owner);
        UiSlotTable.BindBool(id, v => flag = v);
        UiSlotTable.BindFloat(id, v => number = v);
        UiSlotTable.BindText(id, v => text = v);
        UiSlotTable.BindPointerEnter(id, () => enters++);
        UiSlotTable.BindPointerExit(id, () => exits++);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Bool, 0f, true, null);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Float, 0.5f, false, null);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Text, 0f, false, "hi");
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.PointerEnter, 0f, false, null);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.PointerExit, 0f, false, null);
        Assert.True(flag);
        Assert.Equal(0.5f, number);
        Assert.Equal("hi", text);
        Assert.Equal(1, enters);
        Assert.Equal(1, exits);
        UiSlotTable.ReleaseOwner(_owner);
        Assert.Equal(0, UiSlotTable.LiveCount);
    }

    /// <summary>
    /// TMP onValueChanged is one dispatch per character. The owned text handler must see every
    /// value; a slot that only ran on onEndEdit would leave the last commit as the only call.
    /// </summary>
    [Fact]
    public void Text_slot_raises_the_owned_handler_on_every_character_from_a_fake_tmp_event()
    {
        var received = new List<string>();
        ushort id = UiSlotTable.Alloc(_owner);
        UiSlotTable.BindText(id, v => received.Add(v));

        var tmp = new FakeTmpOnValueChanged();
        tmp.OnValueChanged += value =>
            UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Text, 0f, false, value);

        tmp.Type("f");
        tmp.Type("fi");
        tmp.Type("fix");

        Assert.Equal(new[] { "f", "fi", "fix" }, received);
        UiSlotTable.Release(id);
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Text, 0f, false, "zzz");
        Assert.Equal(new[] { "f", "fi", "fix" }, received);
    }

    /// <summary>Stand-in for TMP_InputField.onValueChanged: one raise per keystroke, no Unity.</summary>
    private sealed class FakeTmpOnValueChanged
    {
        public event Action<string>? OnValueChanged;
        public void Type(string value) => OnValueChanged?.Invoke(value);
    }

    [Fact]
    public void Panel_Show_Hide_Dispose_state_machine_and_double_dispose_is_safe()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        Assert.True(panel.Enabled);
        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.Equal("progenitor-ui-example.ui.test", panel.RootName);
        Assert.Equal("Sample", panel.Text);
        Assert.Equal(1, _client.PanelCount);

        var button = panel.Button("Log click");
        Assert.True(button.Enabled);
        Assert.Equal("Log click", button.Text);
        int clicks = 0;
        button.OnClick(() => clicks++);
        Assert.True(_client.LastWiredId != 0);

        UiApi.Show(panel);
        Assert.Equal(UiHandleState.Shown, panel.State);
        Assert.Equal(1, _client.ShowCount);

        UiSlotTable.BootstrapDispatch(_client.LastWiredId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(1, clicks);

        UiApi.Hide(panel);
        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.Equal(1, _client.HideCount);

        panel.Dispose();
        Assert.True(panel.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, panel.State);
        Assert.True(button.IsDisposed);
        Assert.Equal(0, _client.OverlayCount);
        Assert.Equal(0, _client.RaycasterCount);
        panel.Dispose();
        button.Dispose();
        Assert.Contains(_log.Infos, l => l.Contains("Ui: panel 'Sample'", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Ui: show 'Sample'", StringComparison.Ordinal));
        Assert.Contains(_log.Infos, l => l.Contains("Ui: hide 'Sample'", StringComparison.Ordinal));
    }

    [Fact]
    public void Toggle_Value_and_OnChanged_round_trip_through_the_slot()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        var toggle = panel.Toggle("Demo toggle", initial: true);
        Assert.True(toggle.Value);
        bool? seen = null;
        toggle.OnChanged(v => seen = v);
        toggle.Value = false;
        Assert.False(toggle.Value);
        UiSlotTable.BootstrapDispatch(_client.LastWiredId, (byte)UiSlotKind.Bool, 0f, true, null);
        Assert.True(seen);
        panel.Dispose();
    }

    [Fact]
    public void Server_Panel_Show_return_disabled_and_log_once()
    {
        var server = new FakeUiRuntime { IsClient = false };
        UiApi.SetRuntimeForTests(server);

        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        Assert.False(panel.Enabled);
        Assert.Equal(UiHandleState.Disabled, panel.State);
        Assert.Equal(0, server.PanelCount);
        Assert.Contains(_log.Infos, l => l == "Ui: Panel('Sample') is client-only; returning a disabled handle.");

        panel.Show();
        Assert.Equal(0, server.ShowCount);
        panel.Dispose();
        panel.Dispose();
        Assert.True(panel.IsDisposed);
    }

    [Fact]
    public void Uninstall_unwires_every_widget_before_DestroyByPrefix()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        panel.Button("Log click");
        UiApi.Show(panel);
        UiApi.Uninstall();
        int unwire = _client.SweepOrder.IndexOf("unwire");
        int destroy = _client.SweepOrder.IndexOf("destroy");
        Assert.True(unwire >= 0);
        Assert.True(destroy >= 0);
        Assert.True(unwire < destroy);
        Assert.Contains(_log.Infos, l => l.Contains("uninstalled leftover=", StringComparison.Ordinal));
    }

    [Fact]
    public void Hide_does_not_keep_the_panel_shown()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        panel.Button("Log click");
        UiApi.Show(panel);
        Assert.Equal(UiHandleState.Shown, panel.State);
        UiApi.Hide(panel);
        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.Equal(1, _client.HideCount);
        panel.Dispose();
    }

    [Fact]
    public void RemoveAll_disposes_owner_handles_and_sweeps_the_exact_root()
    {
        var a = UiApi.Panel("A", _owner, "example.ui.test");
        a.Button("One");
        UiApi.Show(a);
        UiApi.RemoveAll(_owner);
        Assert.True(a.IsDisposed);
        Assert.Contains(_client.DestroyedRoots, r => r == "progenitor-ui-example.ui.test");
        Assert.Empty(_client.DestroyedPrefixes);
        Assert.Equal(0, UiSlotTable.LiveCount);
    }

    [Fact]
    public void Hide_of_panel_A_keeps_panel_B_visible_on_the_same_root()
    {
        var a = UiApi.Panel("A", _owner, "example.ui.test");
        var b = UiApi.Panel("B", _owner, "example.ui.test");
        UiApi.Show(a);
        UiApi.Show(b);
        Assert.Equal(UiHandleState.Shown, a.State);
        Assert.Equal(UiHandleState.Shown, b.State);
        Assert.True(_client.OverlayVisibleFor("A"));
        Assert.True(_client.OverlayVisibleFor("B"));
        UiApi.Hide(a);
        Assert.Equal(UiHandleState.Built, a.State);
        Assert.Equal(UiHandleState.Shown, b.State);
        Assert.False(a.Visible);
        Assert.True(b.Visible);
        Assert.False(_client.OverlayVisibleFor("A"));
        Assert.True(_client.OverlayVisibleFor("B"));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void Dispose_of_one_panel_destroys_its_overlay_and_leaves_the_other()
    {
        var a = UiApi.Panel("A", _owner, "example.ui.test");
        var b = UiApi.Panel("B", _owner, "example.ui.test");
        Assert.Equal(2, _client.PanelCount);
        Assert.Equal(2, _client.OverlayCount);
        Assert.Equal(2, _client.RaycasterCount);
        Assert.Equal(_client.PanelCount, _client.OverlayCount);

        a.Dispose();
        Assert.True(a.IsDisposed);
        Assert.False(b.IsDisposed);
        Assert.Equal(1, _client.PanelCount);
        Assert.Equal(1, _client.OverlayCount);
        Assert.Equal(1, _client.RaycasterCount);
        Assert.Equal(_client.PanelCount, _client.OverlayCount);
        Assert.False(_client.OverlayAliveFor("A"));
        Assert.True(_client.OverlayAliveFor("B"));
        Assert.False(_client.RaycasterAliveFor("A"));
        Assert.True(_client.RaycasterAliveFor("B"));

        b.Dispose();
        Assert.Equal(0, _client.PanelCount);
        Assert.Equal(0, _client.OverlayCount);
        Assert.Equal(0, _client.RaycasterCount);
    }

    [Fact]
    public void Double_Show_then_Hide_leaves_capture_at_zero()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        _client.CursorLocked = true;
        _client.CursorVisible = false;
        UiApi.Show(panel);
        UiApi.Show(panel);
        Assert.Equal(1, _client.Capture);
        Assert.Equal(2, _client.ShowCount);
        UiApi.Hide(panel);
        Assert.Equal(0, _client.Capture);
        Assert.True(_client.CursorLocked);
        Assert.False(_client.CursorVisible);
        panel.Dispose();
    }

    [Fact]
    public void Cursor_snapshot_restores_the_state_from_this_capture_not_process_first_seen()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        _client.CursorLocked = true;
        _client.CursorVisible = false;
        UiApi.Show(panel);
        Assert.False(_client.CursorLocked);
        Assert.True(_client.CursorVisible);
        UiApi.Hide(panel);
        Assert.True(_client.CursorLocked);
        Assert.False(_client.CursorVisible);

        _client.CursorLocked = false;
        _client.CursorVisible = true;
        UiApi.Show(panel);
        Assert.False(_client.CursorLocked);
        Assert.True(_client.CursorVisible);
        UiApi.Hide(panel);
        Assert.False(_client.CursorLocked);
        Assert.True(_client.CursorVisible);
        Assert.Equal(2, _client.CursorRestores.Count);
        Assert.Equal((true, false), _client.CursorRestores[0]);
        Assert.Equal((false, true), _client.CursorRestores[1]);
        panel.Dispose();
    }

    [Fact]
    public void Uninstall_sweeps_every_progenitor_ui_root()
    {
        var extraOwner = new Owner("example.sample.extra");
        var a = UiApi.Panel("A", _owner, "example.sample");
        var b = UiApi.Panel("B", extraOwner, "example.sample.extra");
        UiApi.Uninstall();
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Contains(_client.DestroyedPrefixes, p => p == UiOwnership.Prefix);
        Assert.Equal(0, _client.PanelCount);
    }

    [Fact]
    public void Skipped_module_returns_disabled_handle()
    {
        UiApi.SetSkippedForTests(true);
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        Assert.False(panel.Enabled);
        Assert.Contains(_log.Infos, l => l.Contains("skipped", StringComparison.Ordinal));
        panel.Dispose();
    }

    [Fact]
    public void Panel_requires_a_title()
    {
        Assert.Throws<ArgumentException>(() => UiApi.Panel(" "));
    }

    [Fact]
    public void Slider_and_input_Value_round_trip()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.ui.test");
        var slider = panel.Slider("Volume", 0f, 1f, 0.25f);
        Assert.Equal(0.25f, slider.Value);
        slider.Value = 0.8f;
        Assert.Equal(0.8f, slider.Value);
        var input = panel.Input("Name", "ember");
        Assert.Equal("ember", input.Value);
        input.Value = "shard";
        Assert.Equal("shard", input.Value);
        panel.Dispose();
    }

    private sealed class Owner
    {
        public Owner(string id) => Id = id;
        public string Id { get; }
    }

    internal sealed class FakeUiRuntime : IUiRuntime
    {
        private readonly List<FakePanel> _panels = new();
        private readonly List<FakeWidget> _widgets = new();
        private bool _haveCursorSnap;
        private bool _snapLocked;
        private bool _snapVisible;
        private long _caretBlinkOriginMs;

        public bool IsClient { get; set; } = true;
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public int Capture { get; private set; }
        public bool CursorLocked { get; set; } = true;
        public bool CursorVisible { get; set; }
        public ushort LastWiredId { get; private set; }
        public List<string> DestroyedPrefixes { get; } = new();
        public List<string> DestroyedRoots { get; } = new();
        public List<(bool Locked, bool Visible)> CursorRestores { get; } = new();
        public List<ushort> Wired { get; } = new();
        public List<string> SweepOrder { get; } = new();

        public bool OverlayVisibleFor(string title)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Text == title)
                    return _panels[i].OverlayVisible;
            }

            return false;
        }

        /// <summary>
        /// The spot the handle last placed this panel at (jobs FUI20, FUI21), or
        /// <see cref="PanelSpot.None"/> for a panel that was never placed.
        /// </summary>
        /// <summary>Version footer asked of each runtime panel (<see cref="IUiRuntime.SetPanelFooter"/>), in order.</summary>
        public List<(string Title, string Text)> FooterAsks { get; } = new();

        public void SetPanelFooter(IRuntimePanel panel, string text) =>
            FooterAsks.Add((panel.Text, text ?? ""));

        /// <summary>The footer last asked for the panel titled <paramref name="title"/>, or null when none was.</summary>
        public string? FooterFor(string title)
        {
            for (int i = FooterAsks.Count - 1; i >= 0; i--)
                if (FooterAsks[i].Title == title) return FooterAsks[i].Text;
            return null;
        }

        public PanelSpot SpotFor(string title)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Text == title)
                    return _panels[i].Spot;
            }

            return PanelSpot.None;
        }

        /// <summary>
        /// Where the runtime would actually draw this panel, for a 640-wide frame on a canvas of
        /// <paramref name="canvasWidth"/> x <paramref name="canvasHeight"/> canvas units — the
        /// same call <c>UnityPanel.RunLayout</c> makes, so a handle-level test can assert a
        /// position rather than only an index.
        ///
        /// <para>
        /// The panel's own height is not passed because the placement no longer takes one
        /// (job FUI23, R44 finding 5): a re-layout that changes a panel's height must not move it.
        /// </para>
        /// </summary>
        public PanelOffset OffsetFor(
            string title,
            float panelWidth = 640f,
            float canvasWidth = 1638f,
            float canvasHeight = 1080f) =>
            PanelFlow.Offset(SpotFor(title), panelWidth, canvasWidth, canvasHeight);

        public bool OverlayAliveFor(string title)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Text == title)
                    return _panels[i].Overlay.Alive;
            }

            return false;
        }

        public bool RaycasterAliveFor(string title)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Text == title)
                    return _panels[i].Overlay.HasRaycaster;
            }

            return false;
        }

        public int PanelCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _panels.Count; i++)
                    if (_panels[i].Alive) n++;
                return n;
            }
        }

        public int OverlayCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _panels.Count; i++)
                    if (_panels[i].Overlay.Alive) n++;
                return n;
            }
        }

        public int RaycasterCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _panels.Count; i++)
                    if (_panels[i].Overlay.HasRaycaster) n++;
                return n;
            }
        }

        public IRuntimePanel? CreatePanel(string title, string rootName)
        {
            if (!IsClient) return null;
            NoteFont();
            var p = new FakePanel(title, rootName);
            _panels.Add(p);
            return p;
        }

        public IRuntimeWidget? AddButton(IRuntimePanel panel, string text)
        {
            NoteFont();
            return Add(panel, text, kind: "button");
        }

        /// <summary>
        /// KIT1: cell rows, so a page built out of kit blocks (the mod page's head block, its
        /// prompts) is exercised here rather than degrading to disabled state-only rows. Before this
        /// the fake inherited <c>IUiRuntime.AddCellRow => null</c>, which made every button cell
        /// unclickable and every cell rect unreadable in this suite. The row itself is
        /// <see cref="CellRowTests.FakeCellRow"/>, so there is one fake cell row, not two.
        /// </summary>
        public readonly List<CellRowTests.FakeCellRow> CellRows = new();

        public IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            if (!IsClient) return null;
            NoteFont();
            var row = new CellRowTests.FakeCellRow(null, spec, header);
            CellRows.Add(row);
            return row;
        }

        /// <summary>The live cell row whose key is <paramref name="key"/>, or null.</summary>
        public CellRowTests.FakeCellRow? CellRow(string key)
        {
            for (int i = 0; i < CellRows.Count; i++)
            {
                if (!CellRows[i].Released && string.Equals(CellRows[i].Text, key, StringComparison.Ordinal))
                    return CellRows[i];
            }
            return null;
        }

        /// <summary>
        /// SERVERINST2 opt-in: a toggle whose <c>BoolValue</c> changes dispatches its bound
        /// handler, the way Unity's <c>Toggle.isOn</c> setter fires <c>onValueChanged</c>. Off
        /// by default so existing tests keep the inert widget; with it on, use
        /// <c>ModsMenu.ClickVisibleWidget</c> rather than <see cref="FireToggle"/> (which
        /// would dispatch twice).
        /// </summary>
        public bool ToggleSetterDispatches { get; set; }

        public IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial)
        {
            NoteFont();
            var w = Add(panel, text, kind: "toggle");
            if (w != null)
            {
                w.BoolValue = initial;
                if (ToggleSetterDispatches)
                {
                    var wired = w;
                    w.OnBoolValueChanged = v =>
                    {
                        if (wired.WiredId != 0)
                            UiSlotTable.BootstrapDispatch(wired.WiredId, (byte)UiSlotKind.Bool, 0f, v, null);
                    };
                }
            }
            return w;
        }

        public IRuntimeWidget? AddLabel(IRuntimePanel panel, string text)
        {
            NoteFont();
            return Add(panel, text, kind: "label");
        }

        public IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial)
        {
            NoteFont();
            var w = Add(panel, text, kind: "slider");
            if (w != null) w.FloatValue = initial;
            return w;
        }

        public IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial)
        {
            NoteFont();
            bool search = panel is FakePanel fp && fp.IsSidebar && fp.NextTone == MenuRowTone.Search;
            var w = Add(panel, search ? "" : text, kind: "input");
            if (_inlineOpen)
            {
                InlineFields.Add(text);
                _inlineOpen = false;
                if (w != null) w.InlineField = true;
            }

            if (w != null)
            {
                w.StringValue = initial;
                if (search)
                {
                    // Same strings Unity assigns to the hint TMPs. Not the container caption.
                    w.PlaceholderText = MenuRowStyle.SearchPlaceholder;
                    w.GlyphText = MenuRowStyle.PickSearchGlyph(null);
                    w.HasSearchGlyphIcon = string.IsNullOrEmpty(w.GlyphText);
                }
            }
            return w;
        }

        public IRuntimeWidget? AddChoice(
            IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value)
        {
            NoteFont();
            var w = Add(panel, text, kind: "choice");
            if (w != null) w.StringValue = value ?? "";
            return w;
        }

        public IRuntimeWidget? AddNumeric(
            IRuntimePanel panel, string text, double value, double min, double max, double step, int decimals, string units)
        {
            NoteFont();
            var w = Add(panel, text, kind: "numeric");
            if (w != null)
            {
                w.FloatValue = (float)value;
                w.StringValue = UiNumericParse.Format(value, decimals);
            }
            return w;
        }

        public IRuntimeWidget? AddSliderEntry(
            IRuntimePanel panel, string text, float min, float max, float step, float initial)
        {
            NoteFont();
            var w = Add(panel, text, kind: "slider-entry");
            if (w != null)
            {
                w.FloatValue = initial;
                w.StringValue = UiNumericParse.Format(initial, UiSliderEntryState.StepDecimals(step));
            }
            return w;
        }

        public IRuntimeWidget? AddColorPicker(IRuntimePanel panel, string text, UiColor initial, bool alpha)
        {
            NoteFont();
            var w = Add(panel, text, kind: "color");
            if (w != null)
            {
                w.StringValue = initial.ToHex(alpha);
                w.FloatValue = initial.R;
                w.BoolValue = alpha;
            }
            return w;
        }

        public IRuntimeWidget? AddKeybindPicker(IRuntimePanel panel, string text, string pluginId, string bindId)
        {
            NoteFont();
            var w = Add(panel, text, kind: "keybind");
            if (w != null) w.StringValue = "None";
            return w;
        }

        // ---- SETTINGS1 stage 2 --------------------------------------------------------------

        public IRuntimeWidget? AddTextArea(IRuntimePanel panel, string text, string initial, int lines)
        {
            NoteFont();
            var w = Add(panel, text, kind: "textarea");
            if (w != null)
            {
                w.StringValue = initial ?? "";
                w.Lines = lines;
            }
            return w;
        }

        /// <summary>An options row: one clickable part per option, keyed like the real runtime's.</summary>
        public IRuntimeWidget? AddOptions(
            IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, bool multi, UiOptionsLayout layout)
        {
            NoteFont();
            if (!IsClient) return null;
            var w = new FakePartsWidget(text, multi ? "multiselect" : "radio", panel is FakePanel p ? p.RootName : "");
            w.Panel = panel as FakePanel;
            w.Grid = w.Panel?.Grid ?? false;
            w.Layout = layout;
            for (int i = 0; i < (options?.Count ?? 0); i++)
                w.AddPart(UiOptionsRules.PartKey(i), options![i].Label);
            _widgets.Add(w);
            return w;
        }

        public void SetOptionStates(IRuntimeWidget widget, IReadOnlyList<bool> selected)
        {
            if (widget is FakeWidget w) w.OptionStates = new List<bool>(selected ?? Array.Empty<bool>());
        }

        public IRuntimeWidget? AddInfo(IRuntimePanel panel, string text, string value)
        {
            NoteFont();
            var w = Add(panel, text, kind: "info");
            if (w != null) w.StringValue = value ?? "";
            return w;
        }

        public IRuntimeWidget? AddDivider(IRuntimePanel panel)
        {
            NoteFont();
            return Add(panel, "", kind: "divider");
        }

        public void SetSettingsGrid(IRuntimePanel panel, bool on)
        {
            if (panel is FakePanel p) p.Grid = on;
        }

        public void SetRowHelp(IRuntimeWidget row, string help)
        {
            if (row is FakeWidget w) w.Help = help ?? "";
        }

        // ---- MENUKIT2: the game-look settings page, recorded -----------------------------------------

        public bool DescriptionMode { get; private set; }

        public readonly List<(string Heading, string Body, string Notes, string Tone)> Descriptions = new();

        /// <summary>Row caption → lit (hovered or selected).</summary>
        public readonly Dictionary<string, bool> LitRows = new(StringComparer.Ordinal);

        /// <summary>The caption of the row a reset link belongs to → its last mark.</summary>
        public readonly Dictionary<string, SettingsResetState> ResetMarks = new(StringComparer.Ordinal);

        /// <summary>Row caption → its chip text (empty: none).</summary>
        public readonly Dictionary<string, string> Chips = new(StringComparer.Ordinal);

        public int SectionRules { get; private set; }

        public void SetSettingsDescriptionMode(IRuntimePanel panel, bool on) => DescriptionMode = on;

        public void SetSettingsDescription(IRuntimePanel panel, string heading, string body, string notes, string tone) =>
            Descriptions.Add((heading, body, notes, tone));

        public void SetGridRowLit(IRuntimeWidget row, bool lit)
        {
            if (row is FakeWidget w) LitRows[w.Text] = lit;
        }

        public void SetResetMark(IRuntimeWidget link, SettingsResetState state)
        {
            if (link is FakeWidget w && w.GutterOf != null) ResetMarks[w.GutterOf.Text] = state;
        }

        public void SetRowChip(IRuntimeWidget row, string text)
        {
            if (row is FakeWidget w) Chips[w.Text] = text ?? "";
        }

        public void AddSectionRule(IRuntimeWidget headingRow) => SectionRules++;

        /// <summary>A settings-grid button row: the button captioned <paramref name="caption"/>, remembering the row's name.</summary>
        public IRuntimeWidget? AddGridButton(IRuntimePanel panel, string name, string caption)
        {
            NoteFont();
            var w = Add(panel, caption, kind: "button");
            if (w != null) w.Name = name ?? "";
            return w;
        }

        /// <summary>SETTINGS1 stage 2: the name drawn on the left of the button row captioned <paramref name="caption"/>.</summary>
        public string? NameOf(string caption) => FindWidget("button", caption)?.Name;

        /// <summary>A reset link in a row's gutter: a button widget that remembers its row.</summary>
        public IRuntimeWidget? AddGutterLink(IRuntimePanel panel, IRuntimeWidget row, string glyph, string tooltip)
        {
            NoteFont();
            var w = Add(panel, glyph, kind: "button");
            if (w != null) w.GutterOf = row as FakeWidget;
            return w;
        }

        /// <summary>SETTINGS1 stage 2: click option <paramref name="index"/> of the options row captioned <paramref name="label"/>.</summary>
        public bool FireOption(string kind, string label, int index)
        {
            if (FindWidget(kind, label) is not FakePartsWidget w) return false;
            var part = w.PartAt(index);
            if (part == null || part.WiredId == 0) return false;
            UiSlotTable.BootstrapDispatch(part.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
            return true;
        }

        /// <summary>OPTIONHOVER1: the pointer enters (or leaves) option <paramref name="index"/> of the options row captioned <paramref name="label"/>.</summary>
        public bool FireOptionHover(string kind, string label, int index, bool enter = true)
        {
            if (FindWidget(kind, label) is not FakePartsWidget w) return false;
            var part = w.PartAt(index);
            if (part == null || part.HoverId == 0) return false;
            UiSlotTable.BootstrapDispatch(part.HoverId, (byte)(enter ? UiSlotKind.PointerEnter : UiSlotKind.PointerExit), 0f, false, null);
            return true;
        }

        /// <summary>SETTINGS1 stage 2: an end-of-edit on any text-carrying row found by kind and caption.</summary>
        public bool FireTextOn(string kind, string label, string value)
        {
            var w = FindWidget(kind, label);
            if (w == null || w.WiredId == 0) return false;
            w.StringValue = value ?? "";
            UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Text, 0f, false, value ?? "");
            return true;
        }

        /// <summary>SETTINGS1 stage 2: the help line drawn under the row of <paramref name="kind"/> captioned <paramref name="label"/>.</summary>
        public string? HelpOf(string kind, string label) => FindWidget(kind, label)?.Help;

        /// <summary>SETTINGS1 stage 2: whether that row is grayed.</summary>
        public bool? InteractableOf(string kind, string label) => FindWidget(kind, label)?.Interactable;

        /// <summary>SETTINGS1 stage 2: whether that row is on screen.</summary>
        public bool? VisibleOf(string kind, string label) => FindWidget(kind, label)?.Visible;

        /// <summary>SETTINGS1 stage 2: whether that row was added in settings-grid mode.</summary>
        public bool? GridOf(string kind, string label) => FindWidget(kind, label)?.Grid;

        /// <summary>SETTINGS1 stage 2: the selection an options row last painted.</summary>
        public IReadOnlyList<bool>? OptionStatesOf(string kind, string label) => FindWidget(kind, label)?.OptionStates;

        /// <summary>SETTINGS1 stage 2: the live value text of a row (an info line's value).</summary>
        public string? ValueOf(string kind, string label) => FindWidget(kind, label)?.StringValue;

        /// <summary>Settings organization review: whether the field captioned <paramref name="label"/> masks its text.</summary>
        public bool MaskedOf(string label) => FindWidget("input", label)?.Masked ?? false;

        /// <summary>The caption the Unity runtime would draw: its own rule (<see cref="ModSettingsRowGrid.InputDrawsCaption"/>) for a field.</summary>
        public string? RowCaption(IRuntimeWidget row)
        {
            if (row is not FakeWidget w) return null;
            if (w.Kind == "input" && !ModSettingsRowGrid.InputDrawsCaption(false, w.InlineField, w.Grid)) return "";
            return w.Text;
        }

        /// <summary>Whether a live widget of <paramref name="kind"/> captioned <paramref name="label"/> is drawn.</summary>
        public bool HasWidget(string kind, string label) => FindWidget(kind, label) != null;

        public void SetInputMasked(IRuntimeWidget input, bool masked)
        {
            if (input is FakeWidget w) w.Masked = masked;
        }

        /// <summary>Settings organization review: inline field-and-button groups opened (BeginInline).</summary>
        public int InlineGroups { get; private set; }

        /// <summary>Settings organization review: the captions of the fields that opened an inline group (Apply beside them).</summary>
        public readonly List<string> InlineFields = new();

        private bool _inlineOpen;

        public void BeginInlineControls(IRuntimePanel panel)
        {
            InlineGroups++;
            _inlineOpen = true;
        }

        public void EndInlineControls(IRuntimePanel panel) => _inlineOpen = false;

        /// <summary>SETTINGS1 stage 2: the gutter links alive now, with the caption of the row each sits in.</summary>
        public List<string> GutterRows()
        {
            var list = new List<string>();
            for (int i = 0; i < _widgets.Count; i++)
                if (_widgets[i].Alive && _widgets[i].GutterOf is { } row) list.Add(row.Text);
            return list;
        }

        public void SetContentAlignment(IRuntimePanel panel, string alignment)
        {
            if (panel is FakePanel p) p.ContentAlignment = SettingsAlign.Normalize(alignment);
        }

        public void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar)
        {
            if (panel is FakePanel p)
            {
                p.IsSidebar = sidebar;
                p.IsStrip = false;
            }
        }

        public void ConfigureMenuStrip(IRuntimePanel panel)
        {
            if (panel is FakePanel p)
            {
                p.IsSidebar = false;
                p.IsStrip = true;
                p.HasHiddenTitle = true;
            }
        }

        public void StyleMenuRow(IRuntimePanel panel, MenuRowTone tone)
        {
            if (panel is FakePanel p) p.NextTone = tone;
        }

        /// <summary>
        /// KIT1 gen 1: the panes that asked to drop their own centered title, in order, by the title
        /// they would have drawn. A strip hides its title through <see cref="ConfigureMenuStrip"/>
        /// instead and is deliberately not in here, so the count is exactly the pages that asked.
        /// </summary>
        public readonly List<string> HiddenTitles = new();

        public void HidePaneTitle(IRuntimePanel panel)
        {
            if (panel is not FakePanel p) return;
            p.HasHiddenTitle = true;
            HiddenTitles.Add(p.Text);
        }

        /// <summary>CONSENT2: titles of the panels that asked to own their own frame rect.</summary>
        public List<string> SelfPlaced { get; } = new();

        public void SelfPlacePanel(IRuntimePanel panel, bool selfPlaced)
        {
            if (panel is not FakePanel p) return;
            if (selfPlaced)
            {
                if (!SelfPlaced.Contains(p.Text)) SelfPlaced.Add(p.Text);
            }
            else
            {
                SelfPlaced.Remove(p.Text);
            }
        }

        /// <summary>Whether the panel titled <paramref name="title"/> dropped its own title band.</summary>
        public bool TitleHiddenFor(string title)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Text == title) return _panels[i].HasHiddenTitle;
            }

            return false;
        }

        /// <summary>Jobs FUI20, FUI21: records the spot each panel was last placed at.</summary>
        public void PlacePanel(IRuntimePanel panel, PanelSpot spot)
        {
            PlaceCount++;
            if (panel is not FakePanel p) return;
            p.Spot = spot;
            if (!spot.IsPlaced)
            {
                p.Visible = false;
                p.OverlayVisible = false;
            }
        }

        public int PlaceCount { get; private set; }

        /// <summary>
        /// How many times this runtime ran the font scene-sweep stand-in. Production
        /// <c>AddButton</c> / <c>MakeLabel</c> call <c>FindFont</c> per widget; this fake
        /// takes the same <see cref="UiFontLookup"/> path so a sidebar of N rows cannot
        /// hide a per-row sweep behind a green suite.
        /// </summary>
        public int SceneSweepCount { get; private set; }

        private static readonly object FontMarker = new();

        private void NoteFont()
        {
            UiFontLookup.Resolve(SweepFont, static _ => true);
        }

        private object SweepFont()
        {
            SceneSweepCount++;
            return FontMarker;
        }

        public void Show(IRuntimePanel panel)
        {
            ShowCount++;
            panel.Visible = true;
            if (panel is FakePanel p)
                p.OverlayVisible = true;
        }

        public void Hide(IRuntimePanel panel)
        {
            HideCount++;
            panel.Visible = false;
            if (panel is FakePanel p)
                p.OverlayVisible = false;
        }

        /// <summary>
        /// Raise a wired widget's event the way the game does, so a test drives the panel through
        /// the callback the player's click or keystroke drives rather than calling the panel's
        /// internals. Without this seam nothing covered <c>input.OnChanged</c> at all, which is
        /// how a per-keystroke commit shipped (R34 finding 1 and its test-quality note).
        ///
        /// <para>
        /// Text is the COMMIT event: TMP's <c>onEndEdit</c> sets <c>field.text</c> and then raises
        /// it once, on Enter or focus loss - so this sets <see cref="FakeWidget.StringValue"/>
        /// first and dispatches once, exactly like the field.
        /// </para>
        /// </summary>
        public bool FireText(string label, string value)
        {
            var w = FindWidget("input", label);
            if (w == null || w.WiredId == 0) return false;
            w.StringValue = value ?? "";
            UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Text, 0f, false, value ?? "");
            return true;
        }

        /// <summary>
        /// One physical key through the owned mapping, then one live-text dispatch if the
        /// string changed. Models TMP: assigning the string resets the native caret to 0;
        /// the next key reads <see cref="UiFieldCaret"/>, not that native 0. A path that
        /// re-reads the native caret reverses <c>blood</c> into <c>doolb</c>.
        /// </summary>
        public bool TypeTextKey(
            string label,
            UiKey key,
            bool ctrl = false,
            bool alt = false,
            bool meta = false,
            bool shift = false,
            string? clipboard = null)
        {
            var w = FindWidget("input", label);
            if (w == null || w.WiredId == 0) return false;
            _ownedFocus = w;
            _eventSystemFocus = true;
            var result = UiFieldCaret.Apply(
                w.NativeText,
                w.Owned,
                key,
                ctrl,
                alt,
                meta,
                shift,
                clipboard,
                int.MaxValue,
                out var nativeAfterAssign);
            Assert.Equal(0, nativeAfterAssign.Caret);
            if (!result.Consumed) return false;
            w.WriteOwned(result.State);
            int caret = result.State.Caret;
            int anchor = result.State.Anchor;
            if (result.TextChanged)
                UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Text, 0f, false, result.State.Text);
            // RebuildSidebar in tests replaces the widget; keep focus and the owned caret.
            var live = FindWidget("input", label) ?? w;
            _ownedFocus = live;
            _eventSystemFocus = true;
            _caretBlinkOriginMs = Environment.TickCount64;
            if (!ReferenceEquals(live, w))
                live.WriteOwned(new UiTextEditState(live.NativeText, caret, anchor));
            return true;
        }

        public int WidgetCaret(string kind, string label) => FindWidget(kind, label)?.Caret ?? 0;

        public int WidgetAnchor(string kind, string label) => FindWidget(kind, label)?.Anchor ?? 0;

        public int WidgetNativeCaret(string kind, string label) => FindWidget(kind, label)?.NativeCaret ?? 0;

        /// <summary>Uniform glyph advance used to assert caret X tracks the owned index.</summary>
        public const float FakeCaretAdvance = 8f;

        public float WidgetCaretX(string kind, string label)
        {
            var w = FindWidget(kind, label);
            if (w == null) return 0f;
            string text = w.NativeText ?? "";
            int n = text.Length;
            if (n <= 0) return 0f;
            var origins = new float[n];
            var advances = new float[n];
            for (int i = 0; i < n; i++)
            {
                origins[i] = i * FakeCaretAdvance;
                advances[i] = (i + 1) * FakeCaretAdvance;
            }

            return UiOwnedCaret.LocalX(w.Caret, n, origins, advances, 0f);
        }

        public bool WidgetCaretDrawable(string kind, string label)
        {
            var w = FindWidget(kind, label);
            if (w == null) return false;
            bool focused = ReferenceEquals(_ownedFocus, w) && w.Alive;
            bool lit = UiOwnedCaret.IsLit(focused, Environment.TickCount64, _caretBlinkOriginMs);
            return UiOwnedCaret.IsDrawable(
                focused,
                assigned: focused,
                active: focused,
                graphicEnabled: focused,
                alpha: focused && lit ? 1f : 0f,
                rectW: focused ? UiOwnedCaret.WidthPx : 0f,
                rectH: focused ? UiOwnedCaret.HeightPx : 0f,
                siblingIndex: focused ? 1 : -1,
                fillSiblingIndex: -1,
                meshCount: focused ? 1 : 0);
        }

        public bool WidgetCaretBlinkOn(string kind, string label)
        {
            var w = FindWidget(kind, label);
            if (w == null) return false;
            bool focused = ReferenceEquals(_ownedFocus, w) && w.Alive;
            return UiOwnedCaret.IsLit(focused, Environment.TickCount64, _caretBlinkOriginMs);
        }

        /// <summary>
        /// Fake layout of the live field: 28 px tall, 4 px pad, 20 px caret on the
        /// text line. UIC5's bar centered on the bottom edge fails the same check.
        /// </summary>
        public bool WidgetCaretFitsInField(string kind, string label)
        {
            if (!CaretFocused(kind, label)) return false;
            float top = UiOwnedCaret.PadYPx;
            return UiOwnedCaret.ContainedVertically(
                top, UiOwnedCaret.HeightPx, fieldTop: 0f, fieldHeight: UiOwnedCaret.FieldHeightPx);
        }

        public bool WidgetCaretOnTextLine(string kind, string label)
        {
            if (!CaretFocused(kind, label)) return false;
            float top = UiOwnedCaret.PadYPx;
            return UiOwnedCaret.OnTextLine(
                top, UiOwnedCaret.HeightPx, UiOwnedCaret.FieldHeightPx * 0.5f, UiOwnedCaret.LineCenterTolerancePx);
        }

        private bool CaretFocused(string kind, string label)
        {
            var w = FindWidget(kind, label);
            return w != null && ReferenceEquals(_ownedFocus, w) && w.Alive;
        }

        public bool TryReadSearchField(out string text, out bool focused)
        {
            var w = FindWidget("input", MenuRowStyle.SearchPlaceholder);
            if (w == null)
            {
                text = "";
                focused = false;
                return false;
            }

            text = w.NativeText ?? "";
            // Owned activation, not EventSystem selection. Cancel on Escape drops
            // EventSystem focus before UiInputContext.Update hides; using that
            // bit is how UIC2 always closed.
            focused = ReferenceEquals(_ownedFocus, w) && w.Alive;
            return true;
        }

        /// <summary>
        /// EventSystem Cancel on Escape. Owned focus stays — that is the live
        /// ordering <c>UiInputContext.Update</c> sees.
        /// </summary>
        public void LoseEventSystemFocus() => _eventSystemFocus = false;

        public void ClearOwnedFocus()
        {
            _ownedFocus = null;
            _eventSystemFocus = false;
        }

        /// <summary>
        /// The live sidebar's <c>RestoreSearchFocus</c> after a rebuild (game-only code): the rebuilt
        /// search box has owned focus again.
        /// </summary>
        public bool FocusSearchField()
        {
            var w = FindWidget("input", MenuRowStyle.SearchPlaceholder);
            if (w == null) return false;
            _ownedFocus = w;
            _eventSystemFocus = true;
            return true;
        }

        /// <summary>SEARCHCLEAR1: Escape on the empty focused search leaves it (the runtime's blur).</summary>
        public int LeaveSearchCalls { get; private set; }

        public bool LeaveSearchField()
        {
            LeaveSearchCalls++;
            var w = FindWidget("input", MenuRowStyle.SearchPlaceholder);
            if (w == null || !ReferenceEquals(_ownedFocus, w)) return false;
            ClearOwnedFocus();
            return true;
        }

        public bool EventSystemFocused => _eventSystemFocus;

        public int PlaceholderMeshCount(string kind, string label) =>
            FindWidget(kind, label)?.PlaceholderMeshCount ?? 0;

        public bool FireToggle(string label, bool value)
        {
            var w = FindWidget("toggle", label);
            if (w == null || w.WiredId == 0) return false;
            w.BoolValue = value;
            UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Bool, 0f, value, null);
            return true;
        }

        /// <summary>SETTINGS1: the slot id wired to the live widget of <paramref name="kind"/> captioned <paramref name="label"/>, or 0.</summary>
        public ushort WiredIdOf(string kind, string label) => FindWidget(kind, label)?.WiredId ?? 0;

        /// <summary>SETTINGS1: a click on a settings-kit control row (choice, keybind) found by kind and caption.</summary>
        public bool FireControlClick(string kind, string label)
        {
            var w = FindWidget(kind, label);
            if (w == null || w.WiredId == 0) return false;
            UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
            return true;
        }

        public bool FireClick(string label)
        {
            var w = FindStripButton(label) ?? FindWidget("button", label);
            if (w == null || w.WiredId == 0) return false;
            UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
            return true;
        }

        /// <summary>
        /// Strip buttons are their own control list. A click labeled Mods/Install/Configuration
        /// must not resolve as a sidebar row that happens to share a caption.
        /// </summary>
        private FakeWidget? FindStripButton(string label)
        {
            for (int i = _widgets.Count - 1; i >= 0; i--)
            {
                var w = _widgets[i];
                if (!w.Alive || w.Kind != "button") continue;
                if (w.Panel is not { IsStrip: true }) continue;
                if (string.Equals(w.Text, label, StringComparison.Ordinal)) return w;
            }

            return null;
        }

        public bool ClickIsStripButton(string label) => FindStripButton(label) != null;

        public bool StripHasHiddenTitle
        {
            get
            {
                for (int i = 0; i < _panels.Count; i++)
                {
                    if (_panels[i].Alive && _panels[i].IsStrip && _panels[i].HasHiddenTitle)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Hidden title row on the strip panel, matching LAY2
        /// <c>Title.SetActive(false)</c>. Must not occupy a uGUI click slot.
        /// </summary>
        public bool InsertHiddenStripTitle()
        {
            FakePanel? strip = null;
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Alive && _panels[i].IsStrip)
                {
                    strip = _panels[i];
                    break;
                }
            }

            if (strip == null) return false;
            strip.HasHiddenTitle = true;
            var title = Add(strip, strip.Text, kind: "label");
            if (title == null) return false;
            title.Visible = false;
            int titleIndex = _widgets.Count - 1;
            int firstButton = -1;
            for (int i = 0; i < _widgets.Count; i++)
            {
                var w = _widgets[i];
                if (w.Alive && w.Kind == "button" && ReferenceEquals(w.Panel, strip))
                {
                    firstButton = i;
                    break;
                }
            }

            if (firstButton >= 0 && titleIndex > firstButton)
            {
                var row = _widgets[titleIndex];
                _widgets.RemoveAt(titleIndex);
                _widgets.Insert(firstButton, row);
            }

            return true;
        }

        /// <summary>
        /// Invoke the uGUI listener registered on strip button i (the Button.onClick
        /// slot), not <see cref="ClickAt"/>. Skips a hidden title row so button i
        /// selects <see cref="ModsTabStrip.Order"/>[i].
        /// </summary>
        public bool FireUguiStripListener(int buttonIndex)
        {
            int n = 0;
            for (int i = 0; i < _widgets.Count; i++)
            {
                var w = _widgets[i];
                if (!w.Alive || w.Kind != "button") continue;
                if (w.Panel is not { IsStrip: true }) continue;
                if (n == buttonIndex)
                {
                    if (w.WiredId == 0) return false;
                    UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
                    return true;
                }

                n++;
            }

            return false;
        }

        /// <summary>
        /// Left-origin hit rect for a widget, in the same space as
        /// <see cref="ClickAt"/>. Strip buttons and the Install search field use this
        /// so a test can drive the live pump path (point in a rect) instead of a caption lookup.
        /// </summary>
        public bool SetHitRect(string kind, string label, float x, float y, float width, float height)
        {
            var w = kind == "button"
                ? FindStripButton(label) ?? FindWidget(kind, label)
                : FindWidget(kind, label);
            if (w == null) return false;
            w.HitX = x;
            w.HitY = y;
            w.HitW = width;
            w.HitH = height;
            w.HasHitRect = true;
            return true;
        }

        /// <summary>
        /// Same order as the shown-input pump: focus a field under the point, else
        /// invoke the strip slot under the point via <see cref="ModsTabStrip.HitIndex"/>
        /// on the union of strip-button rects (not last-matching overlapping rows).
        /// </summary>
        public bool ClickAt(float x, float y)
        {
            FakeWidget? field = null;
            float best = float.MaxValue;
            for (int i = 0; i < _widgets.Count; i++)
            {
                var w = _widgets[i];
                if (!w.Alive || w.Kind != "input" || !w.HasHitRect) continue;
                if (!HitContains(w, x, y)) continue;
                float d = HitCenterDist2(w, x, y);
                if (d < best)
                {
                    best = d;
                    field = w;
                }
            }

            if (field != null)
            {
                _ownedFocus = field;
                _eventSystemFocus = true;
                return true;
            }

            var stripButtons = new List<FakeWidget>();
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < _widgets.Count; i++)
            {
                var w = _widgets[i];
                if (!w.Alive || w.Kind != "button" || !w.HasHitRect) continue;
                if (w.Panel is not { IsStrip: true }) continue;
                stripButtons.Add(w);
                if (w.HitX < minX) minX = w.HitX;
                if (w.HitY < minY) minY = w.HitY;
                if (w.HitX + w.HitW > maxX) maxX = w.HitX + w.HitW;
                if (w.HitY + w.HitH > maxY) maxY = w.HitY + w.HitH;
            }

            if (stripButtons.Count == 0) return false;
            var strip = ModsTabStrip.GeometryFromLeftOrigin(minX, minY, maxX - minX, maxY - minY);
            int index = ModsTabStrip.HitIndex(strip, x, y);
            if (index < 0 || index >= stripButtons.Count) return false;
            var button = stripButtons[index];
            if (button.WiredId == 0) return false;
            UiSlotTable.BootstrapDispatch(button.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
            return true;
        }

        /// <summary>
        /// Type into the field <see cref="ClickAt"/> focused. Unlike
        /// <see cref="TypeTextKey"/>, this does not look up by caption and does not
        /// grant focus — no focused field means the key does not land.
        /// </summary>
        public bool TypeFocusedKey(
            UiKey key,
            bool ctrl = false,
            bool alt = false,
            bool meta = false,
            bool shift = false,
            string? clipboard = null)
        {
            var w = _ownedFocus;
            if (w == null || !w.Alive || w.Kind != "input" || w.WiredId == 0) return false;
            string label = string.IsNullOrEmpty(w.PlaceholderText) ? w.Text : w.PlaceholderText;
            return TypeTextKey(label, key, ctrl, alt, meta, shift, clipboard);
        }

        private static bool HitContains(FakeWidget w, float x, float y) =>
            x >= w.HitX && x <= w.HitX + w.HitW && y >= w.HitY && y <= w.HitY + w.HitH;

        private static float HitCenterDist2(FakeWidget w, float x, float y)
        {
            float cx = w.HitX + w.HitW * 0.5f;
            float cy = w.HitY + w.HitH * 0.5f;
            float dx = x - cx;
            float dy = y - cy;
            return dx * dx + dy * dy;
        }

        /// <summary>The live text of a widget, so a test can see what the panel wrote back into it.</summary>
        public string? WidgetValue(string kind, string label) => FindWidget(kind, label)?.StringValue;

        public string? WidgetText(string kind, string label) => FindWidget(kind, label)?.Text;

        /// <summary>Every label of a given kind, in the order the panel added them.</summary>
        public List<string> WidgetTexts(string kind)
        {
            var list = new List<string>();
            for (int i = 0; i < _widgets.Count; i++)
            {
                if (!_widgets[i].Alive) continue;
                if (_widgets[i].Kind != kind) continue;
                list.Add(_widgets[i].Text);
            }

            return list;
        }

        /// <summary>
        /// Text that would draw inside an empty input, not a container caption.
        /// Hidden once the field has typed text, matching <see cref="MenuRowStyle.SearchHintVisible"/>.
        /// </summary>
        public List<string> RenderedPlaceholders(string kind)
        {
            var list = new List<string>();
            for (int i = 0; i < _widgets.Count; i++)
            {
                if (!_widgets[i].Alive) continue;
                if (_widgets[i].Kind != kind) continue;
                if (!MenuRowStyle.SearchHintVisible(_widgets[i].StringValue)) continue;
                if (string.IsNullOrEmpty(_widgets[i].PlaceholderText)) continue;
                list.Add(_widgets[i].PlaceholderText);
            }

            return list;
        }

        public List<string> RenderedGlyphs(string kind)
        {
            var list = new List<string>();
            for (int i = 0; i < _widgets.Count; i++)
            {
                if (!_widgets[i].Alive) continue;
                if (_widgets[i].Kind != kind) continue;
                if (!MenuRowStyle.SearchHintVisible(_widgets[i].StringValue)) continue;
                if (_widgets[i].HasSearchGlyphIcon) list.Add("(icon)");
                else if (!string.IsNullOrEmpty(_widgets[i].GlyphText)) list.Add(_widgets[i].GlyphText);
            }

            return list;
        }

        private FakeWidget? FindWidget(string kind, string label)
        {
            for (int i = _widgets.Count - 1; i >= 0; i--)
            {
                var w = _widgets[i];
                if (!w.Alive) continue;
                if (w.Kind != kind) continue;
                if (string.Equals(w.Text, label, StringComparison.Ordinal)
                    || string.Equals(w.PlaceholderText, label, StringComparison.Ordinal))
                    return w;
            }

            return null;
        }

        public bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget)
        {
            LastWiredId = widgetId;
            Wired.Add(widgetId);
            // OPTIONHOVER1: an option part's pointer slot is its own; its click slot stays WiredId.
            if (widget is FakeWidget { Kind: "option" } option && kind is UiSlotKind.PointerEnter or UiSlotKind.PointerExit)
            {
                option.HoverId = widgetId;
                return true;
            }

            if (widget is FakeWidget fake)
                fake.WiredId = widgetId;
            // RADIOCLICK1: as UnityUiRuntime.TryWire: a cell row without a click event wires nothing.
            if (widget is CellRowTests.FakeCellRow row && kind == UiSlotKind.Click) return row.WireClick(widgetId);
            return true;
        }

        public void Unwire(ushort widgetId)
        {
            SweepOrder.Add("unwire");
        }

        public void UnwireMany(IReadOnlyList<ushort> ids)
        {
            SweepOrder.Add("unwire");
        }

        public int DestroyRoot(string rootName)
        {
            SweepOrder.Add("destroy-root");
            DestroyedRoots.Add(rootName);
            int n = 0;
            for (int i = 0; i < _panels.Count; i++)
            {
                if (!_panels[i].Alive) continue;
                if (!_panels[i].RootName.Equals(rootName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _panels[i].Release();
                n++;
            }

            for (int i = 0; i < _widgets.Count; i++)
            {
                if (!_widgets[i].Alive) continue;
                if (!_widgets[i].RootName.Equals(rootName, StringComparison.OrdinalIgnoreCase))
                    continue;
                _widgets[i].Release();
            }

            return n;
        }

        public int DestroyByPrefix(string prefix)
        {
            SweepOrder.Add("destroy");
            DestroyedPrefixes.Add(prefix);
            int n = 0;
            for (int i = 0; i < _panels.Count; i++)
            {
                if (!_panels[i].Alive) continue;
                if (!_panels[i].RootName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                _panels[i].Release();
                n++;
            }

            return n;
        }

        public void SetCapture(bool capture)
        {
            if (capture)
            {
                Capture++;
                if (Capture == 1)
                {
                    _snapLocked = CursorLocked;
                    _snapVisible = CursorVisible;
                    _haveCursorSnap = true;
                    CursorLocked = false;
                    CursorVisible = true;
                }
            }
            else
            {
                Capture--;
                if (Capture <= 0)
                {
                    Capture = 0;
                    if (_haveCursorSnap)
                    {
                        CursorLocked = _snapLocked;
                        CursorVisible = _snapVisible;
                        CursorRestores.Add((_snapLocked, _snapVisible));
                        _haveCursorSnap = false;
                    }
                }
            }
        }

        // ---- job FUI19: where the panel is, and where the pointer is -------------------------

        /// <summary>Screen rect per panel title. Absent means "unreadable", the fail-closed case.</summary>
        public Dictionary<string, ScreenRect> Rects { get; } = new(StringComparer.Ordinal);

        public PointerSample Pointer { get; set; } = PointerSample.Unreadable;

        public bool ThrowOnRect { get; set; }

        public bool ThrowOnPointer { get; set; }

        public void SetRect(string title, float minX, float minY, float maxX, float maxY) =>
            Rects[title] = ScreenRect.FromCorners(minX, minY, maxX, maxY);

        public ScreenRect PanelRect(IRuntimePanel panel)
        {
            if (ThrowOnRect) throw new InvalidOperationException("rect");
            if (panel is FakePanel p && Rects.TryGetValue(p.Text, out var rect)) return rect;
            return ScreenRect.Unknown;
        }

        public PointerSample ReadPointer()
        {
            if (ThrowOnPointer) throw new InvalidOperationException("pointer");
            return Pointer;
        }

        private FakeWidget? Add(IRuntimePanel panel, string text, string kind)
        {
            if (!IsClient) return null;
            var w = new FakeWidget(text, kind, panel is FakePanel p ? p.RootName : "");
            w.Panel = panel as FakePanel;
            if (panel is FakePanel host)
            {
                w.Align = host.ContentAlignment;
                w.Grid = host.Grid;
            }
            _widgets.Add(w);
            return w;
        }

        private sealed class FakePanel : FakeWidget, IRuntimePanel, IHasOverlay
        {
            public FakePanel(string title, string rootName) : base(title, "panel", rootName)
            {
                Overlay = new FakeOverlay(title, rootName);
                OverlayVisible = true;
            }

            public FakeOverlay Overlay { get; }

            IRuntimeWidget? IHasOverlay.Overlay => Overlay;

            public bool OverlayVisible { get; set; }

            /// <summary>The spot the handle last placed this panel at (jobs FUI20, FUI21).</summary>
            public PanelSpot Spot { get; set; } = PanelSpot.None;

            public bool IsSidebar { get; set; }

            public bool IsStrip { get; set; }

            public bool HasHiddenTitle { get; set; }

            public MenuRowTone NextTone { get; set; }

            public string ContentAlignment { get; set; } = SettingsAlign.Centre;

            /// <summary>SETTINGS1 stage 2: the settings grid is on for rows added now.</summary>
            public bool Grid { get; set; }

            public override void Release()
            {
                Overlay.Release();
                base.Release();
            }
        }

        /// <summary>SETTINGS1 stage 2: an options row whose parts are clickable option widgets.</summary>
        private sealed class FakePartsWidget : FakeWidget, IRuntimePartsWidget
        {
            private readonly List<(string Key, FakeWidget Part)> _parts = new();

            public FakePartsWidget(string text, string kind, string rootName) : base(text, kind, rootName)
            {
            }

            public UiOptionsLayout Layout { get; set; }

            public void AddPart(string key, string caption) => _parts.Add((key, new FakeWidget(caption, "option", RootName)));

            public FakeWidget? PartAt(int index) => index >= 0 && index < _parts.Count ? _parts[index].Part : null;

            public IRuntimeWidget? Part(string key)
            {
                for (int i = 0; i < _parts.Count; i++)
                    if (string.Equals(_parts[i].Key, key, StringComparison.Ordinal)) return _parts[i].Part;
                return null;
            }

            public override void Release()
            {
                for (int i = 0; i < _parts.Count; i++) _parts[i].Part.Release();
                base.Release();
            }
        }

        private sealed class FakeOverlay : FakeWidget
        {
            public FakeOverlay(string title, string rootName) : base(title + "-overlay", "overlay", rootName)
            {
                HasRaycaster = true;
            }

            public bool HasRaycaster { get; private set; }

            public override void Release()
            {
                HasRaycaster = false;
                base.Release();
            }
        }

        private FakeWidget? _ownedFocus;
        private bool _eventSystemFocus;

        private class FakeWidget : IRuntimeWidget
        {
            private bool _released;
            private string _nativeText = "";
            private int _nativeCaret;
            private int _nativeAnchor;
            private UiTextEditState? _owned;

            public FakeWidget(string text, string kind, string rootName)
            {
                Text = text;
                Kind = kind;
                RootName = rootName;
                Visible = true;
                StringValue = text;
            }

            public string Kind { get; }
            public string RootName { get; }
            public string Align { get; set; } = SettingsAlign.Centre;
            public FakePanel? Panel { get; set; }
            public ushort WiredId { get; set; }

            /// <summary>OPTIONHOVER1: an option part's pointer enter/exit slot.</summary>
            public ushort HoverId { get; set; }
            public bool HasHitRect { get; set; }
            public float HitX { get; set; }
            public float HitY { get; set; }
            public float HitW { get; set; }
            public float HitH { get; set; }
            public bool Alive => !_released;
            public string Text { get; set; }
            public string PlaceholderText { get; set; } = "";
            public string GlyphText { get; set; } = "";
            public bool HasSearchGlyphIcon { get; set; }
            public bool Visible { get; set; }
            public bool Masked { get; set; }
            private bool _boolValue;
            /// <summary>Set by <c>FakeUiRuntime.AddToggle</c> when <c>ToggleSetterDispatches</c> is on; runs only when the value changes.</summary>
            public Action<bool>? OnBoolValueChanged { get; set; }
            public bool BoolValue
            {
                get => _boolValue;
                set
                {
                    bool changed = _boolValue != value;
                    _boolValue = value;
                    if (changed) OnBoolValueChanged?.Invoke(value);
                }
            }
            public float FloatValue { get; set; }

            public UiTextEditState? Owned => _owned;

            public string NativeText => _nativeText;

            /// <summary>Owned caret. Native stays 0 after a text assign (TMP / readOnly).</summary>
            public int Caret => UiFieldCaret.Read(_nativeText, _owned).Caret;

            public int Anchor => UiFieldCaret.Read(_nativeText, _owned).Anchor;

            public int NativeCaret => _nativeCaret;

            public int NativeAnchor => _nativeAnchor;

            public string StringValue
            {
                get => _nativeText;
                set
                {
                    AssignNative(value);
                    // Panel / AddInput / FireText wrote the whole string. Land owned at the end
                    // so the next typed key appends, matching a rebuilt field.
                    _owned = new UiTextEditState(_nativeText, _nativeText.Length, _nativeText.Length);
                }
            }

            public int PlaceholderMeshCount =>
                Kind == "input"
                && MenuRowStyle.SearchHintVisible(_nativeText)
                && !string.IsNullOrEmpty(PlaceholderText)
                    ? PlaceholderText.Length
                    : 0;

            public void AssignNative(string? value)
            {
                var reset = UiFieldCaret.NativeAfterTextAssign(value);
                _nativeText = reset.Text;
                _nativeCaret = reset.Caret;
                _nativeAnchor = reset.Anchor;
            }

            public void WriteOwned(UiTextEditState state)
            {
                state = UiTextEdit.Normalize(state);
                AssignNative(state.Text);
                _owned = state;
            }

            public void SetTextState(UiTextEditState state) => WriteOwned(state);

            // SETTINGS1 stage 2 facts the fake records for the tests.
            public bool Interactable { get; set; } = true;
            public string Help { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Grid { get; set; }
            public bool InlineField { get; set; }
            public int Lines { get; set; }
            public FakeWidget? GutterOf { get; set; }
            public List<bool>? OptionStates { get; set; }

            public object? ClickEvent => Kind is "button" or "option" ? this : null;
            public object? FloatEvent => Kind is "slider" or "numeric" or "slider-entry" ? this : null;
            public object? BoolEvent => Kind == "toggle" ? this : null;
            public object? TextEvent => Kind is "input" or "choice" or "color" or "keybind" or "textarea" ? this : null;
            public object? PointerEnterEvent => Kind is "button" or "option" ? this : null;
            public object? PointerExitEvent => Kind is "button" or "option" ? this : null;

            public virtual void Release() => _released = true;
        }
    }
}
