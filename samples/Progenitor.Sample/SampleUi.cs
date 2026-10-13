using System;
using Progenitor.Ui;
using UiApi = Progenitor.Ui.Ui;

#if !PROGENITOR_TESTS
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Sample <c>.ui demo</c>. Parsing and handle tracking are game-free so the test
/// project can compile this file; Unity / custom events stay behind
/// <c>!PROGENITOR_TESTS</c>.
/// </summary>
internal static class SampleUi
{
    /// <summary>Custom event id ('SmUi'). Distinctive so a collision with a game hash is silent, not mixed.</summary>
    internal const int EventId = 0x536D5569;

    internal const string DemoTitle = "Sample";
    internal const string LogButtonText = "Log click";
    internal const string CloseButtonText = "Close";
    internal const string ToggleText = "Demo toggle";

    /// <summary>FSAMP: cfg key of the <c>AcceptableValueRange</c> demo entry.</summary>
    internal const string SpawnMultiplierKey = "SpawnMultiplier";

    /// <summary>Default of <see cref="SpawnMultiplierKey"/>.</summary>
    internal const int SpawnMultiplierDefault = 1;

    /// <summary>Low end of the accepted range for <see cref="SpawnMultiplierKey"/>.</summary>
    internal const int SpawnMultiplierMin = 1;

    /// <summary>High end of the accepted range for <see cref="SpawnMultiplierKey"/>.</summary>
    internal const int SpawnMultiplierMax = 10;

    /// <summary>FSAMP: cfg key of the <c>AcceptableValueList</c> demo entry.</summary>
    internal const string DifficultyKey = "Difficulty";

    /// <summary>Default of <see cref="DifficultyKey"/>. Must be one of <see cref="DifficultyValues"/>.</summary>
    internal const string DifficultyDefault = "Med";

    /// <summary>
    /// The accepted values for <see cref="DifficultyKey"/>. Order matters for one reason worth
    /// knowing: <c>AcceptableValueList.Clamp</c> returns element 0 for anything invalid, which is
    /// why the settings panel refuses an out-of-set edit instead of letting BepInEx clamp it.
    /// </summary>
    internal static readonly string[] DifficultyValues = { "Low", "Med", "High" };

    private static readonly object Gate = new();
    private static readonly OwnedPanel Owned = new();
    private static object? _owner;
    private static string _pluginId = "progenitor.sample";
    private static bool _toggleValue = true;
    internal static int EmberStackForTests = 99;
    internal static Action<string>? LogOverride;

    internal enum Kind
    {
        Demo = 1,
        Close = 2,
    }

#if !PROGENITOR_TESTS
    internal struct Packet
    {
        public int Value;
    }
#endif

    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            kind = Kind.Demo;
            return true;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "demo": kind = Kind.Demo; return true;
            case "close":
            case "off": kind = Kind.Close; return true;
            default: return false;
        }
    }

    internal static void AttachForTests(object owner, string pluginId)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? "progenitor.sample" : pluginId;
    }

    internal static void Detach()
    {
        Close();
        _owner = null;
        LogOverride = null;
        EmberStackForTests = 99;
        _toggleValue = true;
    }

#if !PROGENITOR_TESTS
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void AutoAttach()
    {
        try
        {
            Progenitor.Dev.MainThread.Post(TryAttachFromActive, owner: typeof(SampleUi));
        }
        catch
        {
            // MainThread may not exist yet if this assembly loaded before Core.
        }
    }

    internal static void TryAttachFromActive()
    {
        var ctx = Plugin.Active;
        if (ctx == null) return;
        Attach(ctx);
    }

    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        _owner = ctx.Owner;
        _pluginId = ctx.Guid;
        try
        {
            var entry = ctx.Config.Bind("Sample", "UiDemoToggle", true, "Sample .ui demo toggle.");
            _toggleValue = entry.Value;
            ctx.Track(new ActionDisposable(() => Close()));

            // FUI5. One keybind and one plain config entry, so the settings panel has something
            // of somebody else's to list. The bind is tracked by ctx.Keybinds, so unload drops it;
            // touching ctx.Keybinds also registers this plugin's cfg as a settings source.
            ctx.Config.Bind(
                "Sample",
                "UiDemoGreeting",
                "Hello from the Sample",
                "Free text the Sample logs when its demo keybind is pressed. Editable in the settings panel (F10).");
            // RegisterOpener: the key only opens or closes this window, so it works at the title screen too, and closes
            // the window it opened while that window has input.
            ctx.Keybinds.RegisterOpener(
                "ToggleDemoPanel",
                new KeyChord(UiKey.F9),
                "Open or close the Sample UI demo panel.",
                () => Toggle());

            // FSAMP feature 4. Two constrained entries so the settings panel (F10) has a row of
            // each shape to show and to refuse an edit against: BepInEx does not throw on an
            // out-of-range set, it *clamps* (and AcceptableValueList.Clamp returns element 0 for
            // anything invalid), so the panel reads the allowed set off the description, prints it
            // on the row, and refuses rather than writing. FCFG gave ctx.Bind an
            // acceptableValues parameter, so these no longer have to reach past it to
            // ctx.Config.File — and the same call would take synced: true, with the bounds
            // staying local metadata the client clamps a stray server value against.
            ctx.Bind(
                "Sample",
                SpawnMultiplierKey,
                SpawnMultiplierDefault,
                "How many demo units spawn.",
                acceptableValues: new AcceptableValueRange<int>(SpawnMultiplierMin, SpawnMultiplierMax));
            ctx.Bind(
                "Sample",
                DifficultyKey,
                DifficultyDefault,
                "How hard the demo encounters are.",
                acceptableValues: new AcceptableValueList<string>(DifficultyValues));

            // FSAMP feature 4. A second opener, a modifier chord (cfg value "F9 + Ctrl"), toggling the log panel; as an
            // opener it also closes the panel it opened while that panel captures gameplay input.
            SampleLogPanel.Attach(ctx.Owner, ctx.Guid);
            ctx.Keybinds.RegisterOpener(
                SampleLogPanel.KeybindId,
                new KeyChord(UiKey.F9, KeyModifiers.Ctrl),
                "Open or close the Sample log panel (last Sample log lines in this process).",
                () => SampleLogPanel.Toggle());
            ctx.Track(new ActionDisposable(() => SampleLogPanel.Detach()));
        }
        catch (Exception e)
        {
            Log("attach bind threw: " + e.Message);
        }

        if (ctx.IsClient)
            BindReceive(ctx);
    }

    private static unsafe void BindReceive(PluginContext ctx) =>
        ctx.Net.OnReceive(EventId, OnReceive);

    internal static unsafe void OnReceive(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<Packet>(payload, length, out var packet))
            {
                Log("receive ignored (bad payload)");
                return;
            }

            if (!TryFromWire(packet.Value, out var kind))
            {
                Log("receive ignored (unknown kind " + packet.Value + ")");
                return;
            }

            Run(kind);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample: ui receive threw: " + e);
        }
    }

    internal static void HandleCommand(Progenitor.Commands.ICommandContext ctx, Kind kind)
    {
        var plugin = Plugin.Active;
        if (plugin == null)
        {
            ctx.Reply("Sample plugin context is not available.");
            return;
        }

        var packet = new Packet { Value = (int)kind };
        bool sent = plugin.Net.SendToUser(ctx.UserEntity, EventId, packet);
        ctx.Reply(sent
            ? "Sample ui " + kind.ToString().ToLowerInvariant() + " sent to your client."
            : "Could not send ui to your client (are you connected?).");
        Plugin.Logger?.LogInfo("Sample: ui server " + kind + " sent=" + sent);
    }
#endif

    internal static void Run(Kind kind)
    {
        try
        {
            if (kind == Kind.Close)
            {
                Close();
                return;
            }

            OpenDemo();
        }
        catch (Exception e)
        {
            Log("run " + kind + " threw: " + e.Message);
        }
    }

    /// <summary>Open the demo panel if it is closed, close it if it is open. The Sample keybind.</summary>
    internal static void Toggle()
    {
        if (LiveCount > 0)
        {
            Close();
            return;
        }

        try { OpenDemo(); }
        catch (Exception e) { Log("keybind open threw: " + e.Message); }
    }

    /// <summary>
    /// 1 while the demo panel is on screen. Derived from the handle (job FUI29): it used to ask
    /// "does a handle exist and is it undisposed", which Escape leaves true with nothing on
    /// screen, so <see cref="Toggle"/> spent the next press closing a panel the player had already
    /// dismissed and the sample - the file mod authors copy - taught the defect.
    /// </summary>
    internal static int LiveCount => Owned.IsOpen ? 1 : 0;

    internal static bool ToggleValue
    {
        get { lock (Gate) return _toggleValue; }
        set { lock (Gate) _toggleValue = value; }
    }

    private static void OpenDemo()
    {
        Close();
        Log("demo open");
        var panel = UiApi.Panel(DemoTitle, _owner, _pluginId);
        // UiLook.GameWindow (decided 2026-10-10): the game's popup frame, and the Mods page's grid, compact buttons and
        // controls inside. Called before any control is added.
        panel.GameWindow();

        // Buttons: an inline row of the game's compact buttons, and a button that asks first.
        panel.Heading("Buttons");
        panel.BeginInline();
        var logBtn = panel.Button(LogButtonText);
        logBtn.OnClick(() => Log("click log"));
        var closeBtn = panel.Button(CloseButtonText);
        closeBtn.OnClick(() =>
        {
            Log("click close");
            Close();
        });
        panel.EndInline();
        panel.ConfirmButton("Log after asking", "Write a line to the Sample log?").OnClick(() => Log("confirmed log"));

        // Read-only rows: a note, a fact, a live readout, a colored status line, a rule.
        panel.Heading("Read-only rows");
        panel.Note("Notes, facts and readouts take no input.");
        panel.Fact("Plugin", _pluginId ?? "");
        panel.Info("Ember stack", () => CurrentEmberStack().ToString(System.Globalization.CultureInfo.InvariantCulture), 1);
        var status = panel.Label("Nothing picked yet.", LabelTone.Muted);
        panel.Divider();

        // Pickers: each one reports to the status line and the log.
        panel.Heading("Pickers");
        panel.Number("Amount", 3, 0, 10, 1, 0, "units").OnChanged(v => Picked(status, "amount", v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        panel.ColorField("Tint", "#B03030").OnChanged(v => Picked(status, "tint", v));
        panel.DateTimeField("Reminder", "", null, DateTimePickerPrecision.Minute).OnChanged(v => Picked(status, "reminder", v.Length == 0 ? "(none)" : v));
        panel.KeyField("Window key", _pluginId ?? "", "ToggleDemoPanel").OnChanged(chord => Picked(status, "key", chord.ToString()));

        // The toggle stays the last control (the tests click it as the last one wired).
        panel.Heading("Toggle");
        var toggle = panel.Toggle(ToggleText, ToggleValue);
        toggle.OnChanged(v =>
        {
            ToggleValue = v;
            Log("toggle value=" + v);
            TryWriteToggle(v);
        });
        int stack = CurrentEmberStack();
        panel.Label("Ember stack: " + stack);
        Log("ember-stack " + stack);
        UiApi.Show(panel);
        var stale = Owned.Adopt(panel);
        if (stale != null)
        {
            try { stale.Dispose(); }
            catch (Exception e) { Log("stale dispose threw: " + e.Message); }
        }

        Log("demo shown");
    }

    private static void Picked(IUiToneLabel status, string what, string value)
    {
        status.SetText("Picked " + what + ": " + value, LabelTone.Ok);
        Log("picked " + what + " " + value);
    }

    internal static void Close()
    {
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Hide(); }
        catch (Exception e) { Log("hide threw: " + e.Message); }
        try { panel.Dispose(); }
        catch (Exception e) { Log("dispose threw: " + e.Message); }
        Log("demo closed");
    }

    private static int CurrentEmberStack()
    {
#if PROGENITOR_TESTS
        return EmberStackForTests;
#else
        return Plugin.EmberStack?.Value ?? Plugin.EmberStackAfter;
#endif
    }

    private static void TryWriteToggle(bool value)
    {
#if !PROGENITOR_TESTS
        try
        {
            var ctx = Plugin.Active;
            if (ctx == null) return;
            var entry = ctx.Config.Bind("Sample", "UiDemoToggle", true, "Sample .ui demo toggle.");
            entry.Value = value;
        }
        catch (Exception e)
        {
            Log("toggle bind threw: " + e.Message);
        }
#endif
    }

    private static bool TryFromWire(int value, out Kind kind)
    {
        kind = (Kind)value;
        return kind is Kind.Demo or Kind.Close;
    }

    internal static void Log(string step)
    {
        string line = "Sample: ui " + step;
        try
        {
            // FSAMP: the Ctrl+F9 log panel reads this process's ring, and on a client almost
            // every Sample line comes from here. Record only - this method does its own LogInfo.
            SampleLog.Record(line);
            LogOverride?.Invoke(line);
#if PROGENITOR_TESTS
            global::Progenitor.Plugin.Logger?.LogInfo(line);
#else
            Plugin.Logger?.LogInfo(line);
#endif
        }
        catch
        {
            // Logger can be torn down during unload.
        }
    }

#if !PROGENITOR_TESTS
    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _action;
        public ActionDisposable(Action action) => _action = action;
        public void Dispose()
        {
            try { _action(); }
            catch { /* unload */ }
        }
    }
#endif
}
