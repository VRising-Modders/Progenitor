using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

#if !PROGENITOR_TESTS
using BepInEx.Configuration;
using Progenitor.Commands;
using Progenitor.Networking;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>Small buildable menu example. No custom Unity objects or unload handler required.</summary>
internal static class SampleMenu
{
    /// <summary>Custom event id ('SmFi'). Server command asks the player's client to add or drop fixture rows.</summary>
    internal const int EventId = 0x536D4669;

    internal const int MaxFixtures = 48;
    internal const string UsageReply = "Usage: .menudemo fixtures <n>|off";
    internal const string PluginIdPrefix = "progenitor.sample.fixture.";

    /// <summary>The custom page's title; <c>ctx.Menu.OpenPage</c> opens it by this title.</summary>
    internal const string ExamplePage = "Example";

    /// <summary>The <c>[Keybinds]</c> key of Shift+F9, which opens <see cref="ExamplePage"/>.</summary>
    internal const string OpenExampleKeybindId = "OpenExamplePage";

    private static readonly object FixtureOwnerFallback = new();
    private static readonly List<ModMenuRegistration> Fixtures = new();
    private static object? _fixtureOwner;

    internal static int FixtureCount => Fixtures.Count;

    internal static string FixtureId(int index) =>
        PluginIdPrefix + index.ToString("00", CultureInfo.InvariantCulture);

    internal static string FixtureName(int index) =>
        "Fixture " + index.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Display name long enough to clip in the 1280×720 sidebar.</summary>
    internal static string FixtureLabel(int index) =>
        FixtureName(index)
        + " — overflow row with a long caption that must stay inside the sidebar ("
        + FixtureId(index) + ")";

    internal static string AppliedReply(int count) =>
        "Registered " + count.ToString(CultureInfo.InvariantCulture) + " Mods-list fixture row(s).";

    internal const string OffReply = "Removed Mods-list fixture rows.";

    internal static bool TryParse(string? action, string? arg, out int count, out bool off)
    {
        count = 0;
        off = false;
        if (!string.Equals((action ?? "").Trim(), "fixtures", StringComparison.OrdinalIgnoreCase))
            return false;
        string token = (arg ?? "").Trim();
        if (string.Equals(token, "off", StringComparison.OrdinalIgnoreCase))
        {
            off = true;
            return true;
        }

        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
            return false;
        return count >= 1 && count <= MaxFixtures;
    }

    internal static string Handle(object owner, string? action, string? arg)
    {
        if (!TryParse(action, arg, out int count, out bool off))
            return UsageReply;
        if (off)
        {
            ClearFixtures();
            return OffReply;
        }

        ApplyFixtures(owner, count);
        return AppliedReply(count);
    }

    internal static int ApplyFixtures(object owner, int count)
    {
        ClearFixtures();
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (count < 1) return 0;
        if (count > MaxFixtures) count = MaxFixtures;
        _fixtureOwner = owner;
        for (int i = 1; i <= count; i++)
        {
            var registration = ModsMenu.Register(owner, FixtureId(i), FixtureLabel(i),
                "Harmless overflow fixture; does not change combat.");
            Fixtures.Add(registration);
        }

        ModsMenu.RefreshDirectory();
        return Fixtures.Count;
    }

    /// <summary>
    /// The Example custom page. DIALOGS1: its controls (switch, slider, text, dropdown, number) are
    /// drawn on the settings page's grid (name left, the control column at one x on every row) with
    /// no layout code here; its labels and the button keep the full row. The demo values are logged,
    /// not stored.
    /// </summary>
    internal static void BuildExamplePage(IUiPanel panel, bool hints, int limit, Action<bool> setHints, Action<string> log)
    {
        panel.Label("Custom pages share Progenitor's controls and cleanup.");
        if (hints) panel.Label("Change ShowHints under Personal, then reopen this page.");
        panel.Label("Server example limit: " + limit.ToString(CultureInfo.InvariantCulture));
        panel.Toggle("Show hints", hints).OnChanged(on => setHints(on));
        panel.Slider("Demo volume", 0f, 1f, 0.5f)
            .OnChanged(v => log("Sample menu: demo volume " + v.ToString("0.00", CultureInfo.InvariantCulture) + "."));
        panel.Input("Demo greeting", "Hello").OnChanged(t => log("Sample menu: demo greeting '" + t + "'."));
        panel.Dropdown("Demo style", new[] { "Short", "Full", "Verbose" }, "Short")
            .OnChanged(v => log("Sample menu: demo style " + v + "."));
        panel.Number("Demo count", 3, 0, 10)
            .OnChanged(v => log("Sample menu: demo count " + v.ToString(CultureInfo.InvariantCulture) + "."));
        // UiLook: a status line in a tone color, and a button drawn as the game's standard button.
        var status = panel.Label("Ready.", LabelTone.Muted);
        panel.Button("Try an action").GameStyle().OnClick(() =>
        {
            status.SetText("Action handled by this generation of the Sample.", LabelTone.Ok);
            log("Sample menu: action handled.");
        });
    }

    internal static void ClearFixtures()
    {
        for (int i = 0; i < Fixtures.Count; i++)
        {
            try { Fixtures[i].Dispose(); }
            catch { /* unload already disposed */ }
        }

        Fixtures.Clear();
        _fixtureOwner = null;
        ModsMenu.RefreshDirectory();
    }

#if !PROGENITOR_TESTS
    internal static void Register(PluginContext ctx)
    {
        // The two keys the stage 1 page used keep their names, so an existing cfg stays valid; the
        // rest are new [Menu] keys that exist to show the stage 2 controls. String-backed on purpose:
        // a plugin-defined enum in a cfg entry is a type from this (collectible) assembly.
        var hints = ctx.Bind("Menu", "ShowHints", true, "Show the help text on the Example page.");
        var limit = ctx.Bind("Menu", "ExampleLimit", 10, "Demonstration limit; does not change combat.",
            synced: true, acceptableValues: new AcceptableValueRange<int>(1, 20));
        var style = ctx.Bind("Menu", "HintStyle", "Short", "How much the hints say.",
            acceptableValues: new AcceptableValueList<string>("Short", "Full"));
        var topics = ctx.Bind("Menu", "HintTopics", "Combat", "Which topics the hints cover (comma-separated).");
        var notes = ctx.Bind("Menu", "Notes", "", "Free notes kept in the cfg (multi-line text).");
        var layout = ctx.Bind("Menu", "ExperimentalLayout", false, "A demonstration setting that only takes effect at the next start.");
        // CONTROLS1: one row for each remaining control, so the page shows every control the builder offers.
        var count = ctx.Bind("Menu", "DemoCount", 3, "A number with steppers; does nothing.");
        var tint = ctx.Bind("Menu", "DemoTint", "#B03030", "A color as #RRGGBB; does nothing.");
        var when = ctx.Bind("Menu", "DemoWhen", "Only out of combat", "A choice whose options are too long for segments; does nothing.",
            acceptableValues: new AcceptableValueList<string>("Only out of combat", "In and out of combat", "Never"));
        // JOINFLOW1: a date and time, stored as ISO 8601 UTC (empty: not set); the page shows it in this computer's zone.
        var eventTime = ctx.Bind("Menu", "DemoEventTime", "", "A date and time as ISO 8601 UTC (2026-10-10T18:00:00Z); does nothing.");
        // TEXTLIMIT1: a server-wide multi-line text. Synced, so the server's value reaches every client;
        // scope: Server below opts it into administrator edits. The server holds it to MaxLength(1000).
        var notice = ctx.Bind("Menu", "ServerNotice", "", "A notice for everyone on the server (multi-line text).",
            synced: true);

        // SETTINGS1 stage 2: the settings page builder. scope: Server opts the synced ExampleLimit into
        // administrator edits (the request path), so ctx.Menu.AllowServerEdits is not needed for it.
        ctx.Settings.Page(page => page
            .Section("Personal")
                .Toggle(hints, help: "Show the help text on the Example page.")
                .Radio(style, showIf: () => hints.Value)
                .MultiSelect(topics, choices: new[] { "Combat", "Travel", "Building" }, showIf: () => hints.Value)
                .Text(notes, multiline: true, lines: 3)
                .Toggle(layout, needsRestart: true)
            .Section("Server")
                .Slider(limit, scope: SettingScope.Server, help: "Demonstration limit; the server's value wins while joined.")
                .Text(notice, multiline: true, lines: 4, scope: SettingScope.Server, label: "Server notice",
                    help: "Shown to everyone on the server; an administrator edits it.")
                .MaxLength(1000)
            .Section("Demo")
                .Info("Local time", () => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Number(count, min: 0, max: 99, help: "A number field with - and + steppers.")
                .Color(tint, help: "A color: the swatch and its hex value.")
                .Radio(when, help: "A radio with long options: one option per line.")
                .DateTime(eventTime, label: "Event time",
                    help: "A demo of the date-time picker: pick a day and a time in your own time zone; it is stored as UTC and does nothing.")
                .Divider()
                .Button("Log a line", () => ctx.Log.LogInfo("Sample settings: button confirmed and run."),
                    confirm: "Write a line to the Sample log?", help: "A confirmed action: it asks first.",
                    label: "Sample log")
            // DIALOGS1: one button per ctx.Dialogs kind (SampleDialogs.cs). The result goes to the log.
            .Section("Dialogs")
                .Button("Confirm", () => SampleDialogs.Confirm(ctx, notes), label: "Confirm dialog",
                    help: "A yes/no question; the main action is drawn in the danger tone.")
                .Button("Notice", () => SampleDialogs.Notice(ctx), label: "Notice dialog",
                    help: "A message with one OK button.")
                .Button("Choose", () => SampleDialogs.Choose(ctx), label: "Choose dialog",
                    help: "Pick one of three options.")
                .Button("Prompt", () => SampleDialogs.Prompt(ctx), label: "Prompt dialog",
                    help: "A line of text, checked before it is accepted.")
                // DIALOGS2: a confirmation drawn as label/value facts.
                .Button("Facts", () => SampleDialogs.Facts(ctx), label: "Facts dialog",
                    help: "A short title, label/value facts and one short note."));

        ctx.Menu.Register("Progenitor Sample", "A working example of settings and custom mod pages.")
            .Page(ExamplePage, panel => BuildExamplePage(
                panel, hints.Value, limit.Value, on => hints.Value = on, line => ctx.Log.LogInfo(line)));

        // Shift+F9 opens the Example page. ctx.Menu.OpenPage opens the Mods menu the way its own key does (in a world
        // through the game's menu, on its MODS tab), then shows the page; drawing the page directly would not.
        // RegisterOpener: the key only opens a page, so it works at the title screen as well as in a world.
        if (ctx.IsClient)
            ctx.Keybinds.RegisterOpener(
                OpenExampleKeybindId,
                new KeyChord(UiKey.F9, KeyModifiers.Shift),
                "Open the Progenitor Sample's Example page in the Mods menu.",
                () => ctx.Menu.OpenPage(ExamplePage));

        _fixtureOwner = ctx.Owner;
        ctx.Track(new FixtureLease());
        if (ctx.IsClient)
            BindReceive(ctx);
    }

    internal struct Packet
    {
        public int Count;
    }

    private static unsafe void BindReceive(PluginContext ctx) =>
        ctx.Net.OnReceive(EventId, OnReceive);

    internal static unsafe void OnReceive(int eventId, byte* payload, int length)
    {
        try
        {
            if (!CustomEvents.TryRead<Packet>(payload, length, out var packet))
                return;
            object owner = _fixtureOwner ?? Plugin.Active?.Owner ?? FixtureOwnerFallback;
            if (packet.Count <= 0)
            {
                ClearFixtures();
                return;
            }

            ApplyFixtures(owner, packet.Count);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .menudemo receive threw: " + e);
        }
    }

    private sealed class FixtureLease : IDisposable
    {
        public void Dispose() => ClearFixtures();
    }
#endif
}

#if !PROGENITOR_TESTS
/// <summary>
/// Admin-only test fixture for Mods-list overflow. Chat commands run on the server;
/// the rows are registered on the player's client through <see cref="SampleMenu.EventId"/>.
/// </summary>
internal static class SampleMenuCommands
{
    [Command("menudemo", adminOnly: true, usage: ".menudemo fixtures <n>|off",
        description: "Admin only. Register n extra Mods-list fixture rows (long labels), or off to remove them.")]
    public static void Run(ICommandContext ctx, string action = "", string arg = "")
    {
        try
        {
            if (!SampleMenu.TryParse(action, arg, out int count, out bool off))
            {
                ctx.Reply(SampleMenu.UsageReply);
                return;
            }

            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }

            var packet = new SampleMenu.Packet { Count = off ? 0 : count };
            bool sent = plugin.Net.SendToUser(ctx.UserEntity, SampleMenu.EventId, packet);
            ctx.Reply(sent
                ? (off ? SampleMenu.OffReply : SampleMenu.AppliedReply(count))
                : "Could not send fixtures to your client (are you connected?).");
            Plugin.Logger?.LogInfo("Sample: .menudemo fixtures " + (off ? "off" : count.ToString(CultureInfo.InvariantCulture))
                + " sent=" + sent);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .menudemo threw: " + e);
            ctx.Reply("Sample menudemo failed. See the server log.");
        }
    }
}
#endif
