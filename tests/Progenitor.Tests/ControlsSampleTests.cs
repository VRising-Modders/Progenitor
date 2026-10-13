using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Progenitor.ControlsSample;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// samples/Progenitor.ControlsSample: the sample must use every control the public UI API offers.
/// A new IUiPanel method, SettingsPageBuilder row or ctx.Dialogs kind without a demo fails here.
/// </summary>
[Collection("PluginLogger")]
public sealed class ControlsSampleTests
{
    /// <summary>Signature key "Name/argc" for every IUiPanel member that adds a control or changes the panel.</summary>
    private static IEnumerable<string> PanelSurface() =>
        typeof(IUiPanel).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name + "/" + m.GetParameters().Length)
            .Distinct();

    [Fact]
    public void Window_uses_every_IUiPanel_control_and_member()
    {
        var log = new List<string>();
        var panel = new RecordingPanel();
        var window = new ControlsWindow("progenitor.controlssample", () => panel, log.Add, _ => { });
        window.Open();

        // Close the window through its own Close button so Hide is recorded too.
        panel.Controls.OfType<FakeButton>().Single(b => b.Text == "Close").Click();

        var missing = PanelSurface().Where(sig => !panel.Calls.Contains(sig)).ToList();
        Assert.True(missing.Count == 0, "Controls sample does not demo: " + string.Join(", ", missing));
        foreach (var prop in new[] { "set_ContentAlignment", "set_Footer", "get_CaptureMode" })
            Assert.Contains(prop, panel.Calls);
        Assert.True(panel.Disposed);
    }

    [Fact]
    public void Every_interactive_control_is_wired_to_log_and_status()
    {
        var log = new List<string>();
        var panel = new RecordingPanel();
        var demo = new ControlsDemo(log.Add, _ => { }, () => { });
        demo.Build(panel, "progenitor.controlssample", ControlsDemo.OpenBindId);

        var interactive = panel.Controls.Where(c => c is not FakeLabel).ToList();
        Assert.NotEmpty(interactive);
        var dialogButtons = new HashSet<string>(ControlsDemo.DialogKinds);
        foreach (var control in interactive)
        {
            Assert.True(control.Handlers.Count > 0, control.GetType().Name + " '" + control.Text + "' has no handler");
            if (control is FakeButton b && dialogButtons.Contains(b.Text)) continue;
            int before = log.Count;
            control.FireAll();
            Assert.True(log.Count > before, control.GetType().Name + " '" + control.Text + "' wrote no log line");
            Assert.StartsWith(ControlsDemo.LogPrefix, log[log.Count - 1]);
            Assert.EndsWith(log[log.Count - 1].Substring(ControlsDemo.LogPrefix.Length), demo.LastStatus);
        }
        Assert.Equal("Events: " + demo.EventCount, panel.FooterText);
    }

    [Fact]
    public void Dialog_buttons_open_every_dialog_kind()
    {
        var opened = new List<string>();
        var panel = new RecordingPanel();
        new ControlsDemo(_ => { }, opened.Add).Build(panel, "p", "b");
        foreach (var b in panel.Controls.OfType<FakeButton>().Where(b => ControlsDemo.DialogKinds.Contains(b.Text)))
            b.Click();
        Assert.Equal(ControlsDemo.DialogKinds, opened);

        // Every public ctx.Dialogs method has at least one case in the plugin.
        string dialogs = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "PluginDialogs.cs");
        string plugin = SourcePin.ReadFile("samples", "Progenitor.ControlsSample", "Plugin.cs");
        foreach (Match m in Regex.Matches(dialogs, @"public IDisposable (\w+)\("))
            Assert.Contains("ctx.Dialogs." + m.Groups[1].Value + "(", plugin);
    }

    /// <summary>Every public <see cref="UiLook"/> method has a demo: a new game-look helper ships with one.</summary>
    [Fact]
    public void Every_UiLook_helper_is_demoed()
    {
        string demo = SourcePin.ReadFile("samples", "Progenitor.ControlsSample", "ControlsDemo.cs");
        var helpers = typeof(UiLook).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).Distinct().ToList();
        Assert.Contains("GameStyle", helpers);
        foreach (string name in helpers)
        {
            bool used = name == "Label" ? demo.Contains("LabelTone.") : demo.Contains("." + name + "(");
            Assert.True(used, "Controls sample does not demo UiLook." + name);
        }
    }

    /// <summary>
    /// NAV1: built on a real panel, every interactive control of the sample page is a controller stop, and walking
    /// down from the top reaches each of them (build order; the test runtime has no screen rects).
    /// </summary>
    [Fact]
    public void A_controller_reaches_every_interactive_control_on_the_page()
    {
        var runtime = new UiTests.FakeUiRuntime();
        var host = new FakeNavHost();
        var previous = UiNav.Host;
        UiLayoutBatch.ResetForTests();
        Progenitor.Ui.Ui.ResetForTests();
        Progenitor.Ui.Ui.SetRuntimeForTests(runtime);
        UiNav.Host = host;
        UiNav.Reset();
        try
        {
            var panel = (UiPanelHandle)Progenitor.Ui.Ui.Panel("Controls", new object(), "progenitor.controlssample");
            new ControlsDemo(_ => { }, _ => { }, () => { }).Build(panel, "progenitor.controlssample", ControlsDemo.OpenBindId);
            var children = panel.ChildrenSnapshot().ToList();
            host.Surface = new NavSurface("page", new IReadOnlyList<UiHandle>[] { children });

            var interactive = children.Where(c => UiNav.RoleOf(c.GetType()) != NavRole.None).ToList();
            var notStops = interactive.Where(c => !UiNav.IsStop(c)).Select(c => c.GetType().Name + " '" + c.Text + "'").ToList();
            Assert.True(notStops.Count == 0, "Not reachable with a controller: " + string.Join(", ", notStops));

            var reached = new HashSet<UiHandle>(ReferenceEqualityComparer.Instance);
            UiNav.Execute(NavCommand.Down);
            for (int i = 0; i < children.Count * 2; i++)
            {
                if (!UiNav.Focus.IsEmpty) reached.Add(UiNav.Focus.Handle);
                if (!UiNav.Execute(NavCommand.Down)) break;
            }

            var missed = interactive.Where(c => !reached.Contains(c)).Select(c => c.GetType().Name + " '" + c.Text + "'").ToList();
            Assert.True(missed.Count == 0, "Walking down never reached: " + string.Join(", ", missed));
            Assert.True(reached.Count >= 20, "only " + reached.Count + " stops reached");
            panel.Dispose();
        }
        finally
        {
            UiNav.Reset();
            UiNav.Host = previous;
            UiLayoutBatch.ResetForTests();
            Progenitor.Ui.Ui.ResetForTests();
        }
    }

    private sealed class FakeNavHost : INavHost
    {
        internal NavSurface? Surface;

        public NavSurface? Active() => Surface;

        public ScreenRect Rect(NavTarget target) => ScreenRect.Unknown;

        public void Mark(NavTarget target, bool on) { }

        public void Reveal(NavTarget target) { }

        public bool FocusText(UiHandle handle) => true;

        public void Click(UiHandle handle) { }

        public void Escape() { }

        public void Scroll(NavTarget target, float amount) { }
    }

    [Fact]
    public void Settings_page_uses_every_SettingsPageBuilder_row()
    {
        string builder = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsPage.cs");
        string plugin = SourcePin.ReadFile("samples", "Progenitor.ControlsSample", "Plugin.cs");
        // InScope marks a button/info line server-wide: meaningless for this client-only mod.
        var exempt = new HashSet<string> { "InScope" };
        var methods = Regex.Matches(builder, @"public SettingsPageBuilder (\w+)\(").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("Toggle", methods);
        var missing = methods.Where(n => !exempt.Contains(n) && !plugin.Contains("." + n + "(")).ToList();
        Assert.True(missing.Count == 0, "Controls sample settings page does not use: " + string.Join(", ", missing));
    }

    // ---- recording fakes ----

    private abstract class FakeHandle : IUiHandle
    {
        public readonly List<(Delegate handler, Func<object?> arg)> Handlers = new();
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State { get; set; } = UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test-root";
        public bool Interactable { get; set; } = true;
        public void Dispose() { IsDisposed = true; State = UiHandleState.Disposed; }

        protected IDisposable Add(Delegate d, Func<object?> arg)
        {
            Handlers.Add((d, arg));
            return new Undo();
        }

        public void FireAll()
        {
            foreach (var (h, arg) in Handlers.ToList())
            {
                if (h is Action a) a();
                else h.DynamicInvoke(arg());
            }
        }

        private sealed class Undo : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeLabel : FakeHandle, IUiInfo
    {
        public string Value => Text;
        public double RefreshSeconds => 1;
        public void Refresh() { }
    }

    private sealed class FakeButton : FakeHandle, IUiButton
    {
        public IDisposable OnClick(Action handler) => Add(handler, () => null);
        public void Click() => FireAll();
    }

    private sealed class FakeToggle : FakeHandle, IUiToggle
    {
        public bool Value { get; set; }
        public IDisposable OnChanged(Action<bool> handler) => Add(handler, () => !Value);
    }

    private sealed class FakeSlider : FakeHandle, IUiSlider, IUiSliderField
    {
        public float Value { get; set; }
        public float Min => 0;
        public float Max => 100;
        public float Step => 1;
        public IDisposable OnChanged(Action<float> handler) => Add(handler, () => 7f);
    }

    private sealed class FakeText : FakeHandle, IUiInput, IUiColorField, IUiDateTimeField
    {
        public string Value { get; set; } = "";
        public string Color { get => Value; set => Value = value; }
        public bool AlphaEnabled => false;
        public TimeZoneInfo Zone => TimeZoneInfo.Utc;
        public DateTimePickerPrecision Precision => DateTimePickerPrecision.Minute;
        public bool IsOpen => false;
        public void Open() { }
        public IDisposable OnChanged(Action<string> handler) => Add(handler, () => "typed");
    }

    private sealed class FakeChoice : FakeHandle, IUiDropdown, IUiRadio
    {
        public FakeChoice(IReadOnlyList<string> values) { Values = values; Labels = values; Value = values[0]; }
        public string Value { get; set; }
        public IReadOnlyList<string> Values { get; }
        public IReadOnlyList<string> Labels { get; }
        public IDisposable OnChanged(Action<string> handler) => Add(handler, () => Values[Values.Count - 1]);
    }

    private sealed class FakeMulti : FakeHandle, IUiMultiSelect
    {
        public FakeMulti(IReadOnlyList<string> values) { Values = values; Labels = values; }
        public IReadOnlyList<string> Selected { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Values { get; }
        public IReadOnlyList<string> Labels { get; }
        public bool IsSelected(string value) => Selected.Contains(value);
        public IDisposable OnChanged(Action<IReadOnlyList<string>> handler) => Add(handler, () => (IReadOnlyList<string>)Values);
    }

    private sealed class FakeNumber : FakeHandle, IUiNumber
    {
        public double Value { get; set; }
        public double Min => 0;
        public double Max => 99;
        public double Step => 1;
        public string Units => "";
        public IDisposable OnChanged(Action<double> handler) => Add(handler, () => 4.0);
    }

    private sealed class FakeKey : FakeHandle, IUiKeyField
    {
        public KeyChord Chord { get; private set; }
        public string ChordText => Chord.ToString();
        public bool Capturing => false;
        public string? ConflictNotice => null;
        public bool BeginCapture() => true;
        public void CancelCapture() { }
        public bool Clear() => true;
        public bool ResetToDefault() => true;
        public IDisposable OnChanged(Action<KeyChord> handler) => Add(handler, () => new KeyChord(UiKey.F8, KeyModifiers.Ctrl));
    }

    private sealed class RecordingPanel : FakeHandle, IUiPanel
    {
        public readonly HashSet<string> Calls = new();
        public readonly List<FakeHandle> Controls = new();
        public FakeLabel? Status;
        public bool Disposed => IsDisposed;
        public string FooterText = "";
        private string _align = "centre";
        private UiCaptureMode _mode = UiCaptureMode.WhenHovered;

        private T Rec<T>(string sig, T handle) where T : FakeHandle
        {
            Calls.Add(sig);
            Controls.Add(handle);
            return handle;
        }

        private static T Named<T>(T h, string text) where T : FakeHandle { h.Text = text; return h; }

        public IUiButton Button(string text) => Rec("Button/1", Named(new FakeButton(), text));
        public IUiToggle Toggle(string text, bool initial = false) => Rec("Toggle/2", Named(new FakeToggle { Value = initial }, text));
        public IUiLabel Label(string text)
        {
            var l = Rec("Label/1", Named(new FakeLabel(), text));
            Status ??= l;
            return l;
        }
        public IUiSlider Slider(string text, float min, float max, float initial) => Rec("Slider/4", Named(new FakeSlider(), text));
        public IUiInput Input(string text, string initial = "") => Rec("Input/2", Named(new FakeText(), text));
        public IUiDropdown Dropdown(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null) =>
            Rec("Dropdown/4", Named(new FakeChoice(values), text));
        public IUiDropdown Dropdown(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions) =>
            Rec("Dropdown/5", Named(new FakeChoice(values), text));
        public IUiNumber Number(string text, double value, double min, double max, double step = 1, int decimals = 0, string units = "") =>
            Rec("Number/7", Named(new FakeNumber(), text));
        public IUiSliderField SliderField(string text, float min, float max, float step, float value) =>
            Rec("SliderField/5", Named(new FakeSlider(), text));
        public IUiColorField ColorField(string text, string hex, bool alpha = false) => Rec("ColorField/3", Named(new FakeText(), text));
        public IUiDateTimeField DateTimeField(string text, string isoUtc, TimeZoneInfo? zone = null, DateTimePickerPrecision precision = DateTimePickerPrecision.Minute) =>
            Rec("DateTimeField/4", Named(new FakeText(), text));
        public IUiKeyField KeyField(string text, string pluginId, string bindId) => Rec("KeyField/3", Named(new FakeKey(), text));
        public IUiInput TextArea(string text, string initial = "", int lines = 3) => Rec("TextArea/3", Named(new FakeText(), text));
        public IUiRadio Radio(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null) =>
            Rec("Radio/4", Named(new FakeChoice(values), text));
        public IUiRadio Radio(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions) =>
            Rec("Radio/5", Named(new FakeChoice(values), text));
        public IUiMultiSelect MultiSelect(string text, IReadOnlyList<string> values, IEnumerable<string>? selected, IReadOnlyList<string>? labels = null) =>
            Rec("MultiSelect/4", Named(new FakeMulti(values), text));
        public IUiInfo Info(string text, Func<string> value, double refreshSeconds = 1) => Rec("Info/3", Named(new FakeLabel(), text));
        public IUiLabel Divider() => Rec("Divider/0", new FakeLabel());
        public IUiButton ConfirmButton(string text, string question) => Rec("ConfirmButton/2", Named(new FakeButton(), text));
        public IUiButton ConfirmButton(string text, string question, string body) => Rec("ConfirmButton/3", Named(new FakeButton(), text));
        public IUiLabel Heading(string text) => Rec("Heading/1", Named(new FakeLabel(), text));
        public IUiLabel Note(string text) => Rec("Note/1", Named(new FakeLabel(), text));
        public IUiLabel Fact(string name, string value) => Rec("Fact/2", Named(new FakeLabel(), name + ": " + value));
        public void BeginInline() => Calls.Add("BeginInline/0");
        public void EndInline() => Calls.Add("EndInline/0");
        public void Show() { Calls.Add("Show/0"); State = UiHandleState.Shown; }
        public void Hide() { Calls.Add("Hide/0"); State = UiHandleState.Built; }
        public string ContentAlignment { get => _align; set { Calls.Add("set_ContentAlignment"); _align = value; } }
        public UiCaptureMode CaptureMode
        {
            get { Calls.Add("get_CaptureMode"); return _mode; }
            set { Calls.Add("set_CaptureMode"); _mode = value; }
        }
        public string Footer { get => FooterText; set { Calls.Add("set_Footer"); FooterText = value; } }
    }
}
