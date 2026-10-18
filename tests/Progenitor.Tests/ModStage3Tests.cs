using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.PluginActions;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SETTINGS1 stage 3 (docs/mod-settings.md) and MODLIST1 (docs/mods-menu.md): the real on/off
/// switch, mod problems on the status bar, counterpart declarations and the Mods list redesign.
///
/// <para>Game-free: a fake pause backend stands in for Bootstrap's reloader, the fake UI runtime for
/// Unity, and a temp folder for <c>BepInEx/config</c>. Nothing here has been observed in the game.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class ModStage3Tests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly FakeBackend _backend = new();
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    public ModStage3Tests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModSettingsPages.ResetForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModRegistry.ResetForTests();
        ModProblems.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.ResetForTests();
        ModsMenu.ResetStage3ForTests();
        UiDialog.ForgetForTests();
        KeybindArbiter.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-stage3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModEnableState.TestDirectory = _dir;
        ModSwitch.BackendOverride = () => _backend;
        ModSwitch.ClockUtc = () => Now;
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        ModsMenu.Close();
        ModsMenu.ResetStage3ForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        ModMenuInventory.TestDisabled = null;
        ModMenuInventory.TestPaused = null;
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModSettingsPages.ResetForTests();
        ModRegistry.ResetForTests();
        ModProblems.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.TestDirectory = null;
        ModEnableState.ResetForTests();
        ServerModPause.Test = null;
        ServerModPause.HeldByPlayer = null;
        Keybinds.ResetForTests();
        KeybindArbiter.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- a stand-in for Bootstrap's reloader --------------------------------------------------

    private sealed class FakeBackend : IServerModPauseBackend
    {
        internal sealed class Unit
        {
            internal string Dll = "";
            internal List<string> Guids = new();
            internal List<string> Deps = new();
            internal bool Unsafe;
        }

        internal readonly List<Unit> Loaded = new();
        internal readonly List<(Unit Unit, bool Resuming)> Out = new();
        internal readonly List<string> Calls = new();
        internal bool IsSupported = true;
        internal bool ResumeLoadsNow = true;

        internal Unit Add(string dll, string[] guids, params string[] deps)
        {
            var u = new Unit { Dll = dll, Guids = guids.ToList(), Deps = deps.ToList() };
            Loaded.Add(u);
            return u;
        }

        public bool Supported => IsSupported;
        public string UnsupportedReason => IsSupported ? "" : "old";
        public string PendingRequest { get; set; } = "";
        public string ResumeReports { get; set; } = "";
        public bool? HostsPrivateGame() => false;
        public string ConnectionIdentity() => "";
        public IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids)
        {
            Calls.Add("keep:" + string.Join(",", guids));
            var kept = new List<string>();
            for (int i = 0; i < Out.Count; i++)
            {
                if (!Out[i].Resuming || !Out[i].Unit.Guids.Any(g => guids.Contains(g, StringComparer.OrdinalIgnoreCase))) continue;
                Out[i] = (Out[i].Unit, false);
                kept.AddRange(Out[i].Unit.Guids);
            }

            return kept;
        }

        public IReadOnlyList<string> Suspendable() =>
            Loaded.Where(u => !u.Unsafe).SelectMany(u => u.Guids).ToList();

        public IReadOnlyList<PausedPlugin> Suspended() =>
            Out.SelectMany(o => o.Unit.Guids.Select(g => new PausedPlugin(g, g.ToUpperInvariant(), "1.0.0", o.Resuming))).ToList();

        private List<Unit> ClosureUnits(IReadOnlyList<string> guids)
        {
            var set = Loaded.Where(u => u.Guids.Any(g => guids.Contains(g, StringComparer.OrdinalIgnoreCase))).ToList();
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var u in Loaded)
                {
                    if (set.Contains(u)) continue;
                    if (u.Deps.Any(d => set.Any(s => s.Guids.Contains(d, StringComparer.OrdinalIgnoreCase)))) { set.Add(u); grew = true; }
                }
            }

            return set;
        }

        public IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal)
        {
            refusal = "";
            var units = ClosureUnits(guids);
            if (units.Count == 0) { refusal = "not a reload plugin"; return Array.Empty<string>(); }
            return units.SelectMany(u => u.Guids).ToList();
        }

        public IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report)
        {
            Calls.Add("suspend:" + string.Join(",", guids));
            report = "";
            var units = ClosureUnits(guids);
            foreach (var u in units) { Loaded.Remove(u); Out.Add((u, false)); }
            return units.SelectMany(u => u.Guids).ToList();
        }

        public IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report)
        {
            Calls.Add("resume:" + (guids == null ? "(all)" : string.Join(",", guids)));
            report = "";
            var loaded = new List<string>();
            // Like Bootstrap: the named DLLs, plus the paused DLLs they depend on and those that depend on them.
            var chosen = Out.Where(o => guids == null || o.Unit.Guids.Any(g => guids.Contains(g, StringComparer.OrdinalIgnoreCase)))
                .Select(o => o.Unit).ToList();
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var o in Out)
                {
                    if (chosen.Contains(o.Unit)) continue;
                    bool dep = chosen.Any(c => c.Deps.Any(d => o.Unit.Guids.Contains(d, StringComparer.OrdinalIgnoreCase)));
                    bool dependant = o.Unit.Deps.Any(d => chosen.Any(c => c.Guids.Contains(d, StringComparer.OrdinalIgnoreCase)));
                    if (dep || dependant) { chosen.Add(o.Unit); grew = true; }
                }
            }

            foreach (var o in Out.ToList())
            {
                if (!chosen.Contains(o.Unit)) continue;
                Out.Remove(o);
                if (ResumeLoadsNow) { Loaded.Add(o.Unit); loaded.AddRange(o.Unit.Guids); }
                else Out.Add((o.Unit, true));
            }

            return loaded;
        }

        public IReadOnlyList<string> Closure2(string id) => Closure(new[] { id }, out _);
    }

    private static void Known(string id, string name, params string[] deps)
    {
        ModRegistry.NoteLoading(id, name, "1.0.0", deps, null);
        ModRegistry.NoteLoaded(id);
    }

    private ConfigFile File(string name) => new(Path.Combine(_dir, name + ".cfg"), saveOnInit: true);

    private static JsonDocument Widgets(string pane) =>
        JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets(pane)));

    private static JsonElement? Extra(JsonDocument doc, string key, Func<JsonElement, bool>? where = null)
    {
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value)) continue;
            if (where == null || where(value)) return value;
        }

        return null;
    }

    // ---- A. the switch --------------------------------------------------------------------------

    /// <summary>
    /// A hot-loadable mod switches live: off is Bootstrap's suspend (the mod and what depends on it
    /// go), the choice is written to the state file and the plugins are held; on resumes them.
    /// What this would catch: a switch that only records, a dependant left running on a mod that is
    /// gone, or a mod that is not held (a server resume would bring it back).
    /// </summary>
    [Fact]
    public void A_hot_loadable_mod_switches_live_off_and_on_with_its_dependants()
    {
        _backend.Add("Base.dll", new[] { "test.s3.base" });
        _backend.Add("Addon.dll", new[] { "test.s3.addon" }, "test.s3.base");
        Known("test.s3.base", "Base");
        Known("test.s3.addon", "Addon", "test.s3.base");

        var view = ModSwitch.Describe("test.s3.base", "Base", ModOwner.Progenitor);
        Assert.Equal(ModSwitchMode.Live, view.Mode);
        Assert.True(view.Interactive);
        Assert.Equal(new[] { "Addon" }, ModSwitch.AlsoTurnsOff("test.s3.base", view));

        var off = ModSwitch.Set("test.s3.base", "Base", ModOwner.Progenitor, on: false);
        Assert.True(off.Ok, off.Message);
        Assert.Equal(ModSwitch.TurnedOff, off.Message);
        Assert.Contains("suspend:test.s3.base", _backend.Calls);
        Assert.Empty(_backend.Loaded);
        Assert.True(ModEnableState.IsDisabled("test.s3.base"));
        Assert.False(ModEnableState.IsDisabled("test.s3.addon"));
        Assert.True(ModSwitch.IsHeld("test.s3.base"));
        Assert.True(ModSwitch.IsHeld("test.s3.addon"), "a dependant that went with it is held too");
        var offView = ModSwitch.Describe("test.s3.base", "Base", ModOwner.Progenitor);
        Assert.False(offView.On);
        Assert.True(offView.Disabled);

        var on = ModSwitch.Set("test.s3.base", "Base", ModOwner.Progenitor, on: true);
        Assert.True(on.Ok, on.Message);
        Assert.Equal(ModSwitch.TurnedOn, on.Message);
        Assert.Equal(2, _backend.Loaded.Count);
        Assert.False(ModEnableState.IsDisabled("test.s3.base"));
        Assert.False(ModSwitch.IsHeld("test.s3.addon"));
    }

    /// <summary>
    /// A mod loaded at game start switches at the next start: the choice is recorded and the status
    /// says so; at the next start its load (and its dependants' loads) is skipped. What this would
    /// catch: a restart-only mod that claims to be off while still running, or one that comes back
    /// on at the next start.
    /// </summary>
    [Fact]
    public void A_restart_only_mod_is_pending_and_stays_off_at_the_next_start()
    {
        Known("test.s3.plugins", "InPlugins");
        Known("test.s3.child", "Child", "test.s3.plugins");

        var view = ModSwitch.Describe("test.s3.plugins", "InPlugins", ModOwner.Progenitor);
        Assert.Equal(ModSwitchMode.Restart, view.Mode);
        Assert.Equal(new[] { "Child" }, ModSwitch.AlsoTurnsOff("test.s3.plugins", view));

        var off = ModSwitch.Set("test.s3.plugins", "InPlugins", ModOwner.Progenitor, on: false);
        Assert.True(off.Ok, off.Message);
        Assert.Equal(ModSwitchWords.TurnsOffAtNextStart + ".", off.Message);
        var pending = ModSwitch.Describe("test.s3.plugins", "InPlugins", ModOwner.Progenitor);
        Assert.Equal(ModSwitchPending.TurnsOff, pending.Pending);
        Assert.False(pending.On);
        var line = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            EnabledIntent = pending.On, SwitchAvailable = true, SwitchMode = pending.Mode, SwitchPending = pending.Pending,
        });
        Assert.Equal(ModRunStatus.PendingRestart, line.Status);
        Assert.Equal(ModSwitchWords.TurnsOffAtNextStart, line.StatusWord);
        Assert.Equal("switch-off", line.RestartReason);

        // The next start: ProgenitorPlugin.Load asks this before it runs OnLoad.
        ModRegistry.ResetForTests();
        Assert.Equal(ModSwitchWords.OffByPlayer, ModEnableState.OffReason("test.s3.plugins", Array.Empty<string>(), ModRegistry.IsSkipped));
        ModRegistry.NoteLoading("test.s3.plugins", "InPlugins", "1.0.0", null, null);
        ModRegistry.NoteSkipped("test.s3.plugins", ModSwitchWords.OffByPlayer);
        Assert.Equal(ModSwitchWords.OffBecause("test.s3.plugins"),
            ModEnableState.OffReason("test.s3.child", new[] { "test.s3.plugins" }, ModRegistry.IsSkipped));
        var after = ModSwitch.Describe("test.s3.plugins", "InPlugins", ModOwner.Progenitor);
        Assert.Equal(ModSwitchPending.None, after.Pending);
        Assert.True(after.Disabled);

        // And the load really checks it before OnLoad (the one place a restart-only mod is kept off).
        string load = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs"), "public sealed override void Load()");
        SourcePin.AssertUncommented(load, "ModEnableState.OffReason(guid, dependencies, ModRegistry.IsSkipped)");
        Assert.True(load.IndexOf("ModEnableState.OffReason", StringComparison.Ordinal) < load.IndexOf("OnLoad(ctx)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The choice survives a restart (the file), a file that cannot be read turns nothing off and is
    /// never overwritten, and Progenitor's own parts can never be recorded off.
    /// </summary>
    [Fact]
    public void The_choice_is_remembered_and_an_unreadable_file_fails_open()
    {
        Assert.True(ModEnableState.Set("test.s3.kept", "Kept", false, new[] { "test.s3.sibling" }, Now, out string error), error);
        ModEnableState.ResetForTests();
        Assert.True(ModEnableState.IsDisabled("test.s3.kept"));
        var entry = ModEnableState.Get("test.s3.kept")!;
        Assert.Equal("Kept", entry.Name);
        Assert.Equal(new[] { "test.s3.sibling" }, entry.HeldWith);
        string json = System.IO.File.ReadAllText(ModEnableState.FilePath);
        Assert.Contains("\"disabled\"", json);

        System.IO.File.WriteAllText(ModEnableState.FilePath, "{ not json");
        ModEnableState.ResetForTests();
        Assert.False(ModEnableState.IsDisabled("test.s3.kept"), "an unreadable file must not keep mods off");
        Assert.False(ModEnableState.Set("test.s3.other", "Other", false, null, Now, out error));
        Assert.Equal(ModEnableState.UnreadableRefusal, error);
        Assert.Equal("{ not json", System.IO.File.ReadAllText(ModEnableState.FilePath));

        System.IO.File.Delete(ModEnableState.FilePath);
        ModEnableState.ResetForTests();
        Assert.False(ModEnableState.Set("progenitor", "Progenitor", false, null, Now, out error));
        Assert.Equal(ModSwitchWords.RequiredRefusal, error);
    }

    /// <summary>
    /// Disabled and paused are distinct, and the player's choice wins: a mod turned off is Disabled
    /// even while a server would pause it, is not reported as paused, and a leave never resumes it.
    /// </summary>
    [Fact]
    public void Disabled_wins_over_paused_and_a_leave_never_resumes_it()
    {
        var both = ModFrameworkLine.Compose(new ModFrameworkFacts { EnabledIntent = false, Paused = true });
        Assert.Equal(ModRunStatus.Disabled, both.Status);
        Assert.Equal(ModFrameworkLine.WordDisabled, both.StatusWord);
        Assert.Equal(ModRunStatus.Paused, ModFrameworkLine.Compose(new ModFrameworkFacts { Paused = true }).Status);

        _backend.Add("Held.dll", new[] { "test.s3.held" });
        _backend.Add("Paused.dll", new[] { "test.s3.paused" });
        _backend.Suspend(new[] { "test.s3.held" }, Array.Empty<string>(), out _);
        _backend.Suspend(new[] { "test.s3.paused" }, Array.Empty<string>(), out _);
        ModEnableState.Set("test.s3.held", "Held", false, null, Now, out _);
        ServerModPause.Test = new ServerModPause.TestSeams { Backend = _backend, Post = a => a() };

        var state = ServerModPause.Current;
        Assert.Equal(new[] { "test.s3.paused" }, state.PausedIds);
        Assert.False(ServerModPause.IsPaused("test.s3.held"));

        _backend.Calls.Clear();
        ServerModPause.OnConnectionEnded();
        Assert.Equal(new[] { "resume:test.s3.paused" }, _backend.Calls);
        Assert.Contains(_backend.Out, o => o.Unit.Guids.Contains("test.s3.held"));
    }

    /// <summary>
    /// Turning off a mod others depend on asks first, in the framework dialog, naming them; No
    /// changes nothing, Yes turns both off. Progenitor itself is refused with its reason, and its
    /// switch is not clickable.
    /// </summary>
    [Fact]
    public void Turning_off_a_mod_others_need_asks_first_and_Progenitor_is_refused()
    {
        _backend.Add("Base.dll", new[] { "test.s3.base" });
        _backend.Add("Addon.dll", new[] { "test.s3.addon" }, "test.s3.base");
        Known("test.s3.base", "Base");
        Known("test.s3.addon", "Addon", "test.s3.base");
        using var reg = ModsMenu.Register(_owner, "test.s3.base", "Base");
        using var regAddon = ModsMenu.Register(_owner, "test.s3.addon", "Addon");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.s3.base");

        using (var page = Widgets("content"))
        {
            var framework = Extra(page, ModPagePane.RowFramework)!.Value;
            Assert.True(framework.GetProperty("switch").GetProperty("interactive").GetBoolean());
            Assert.Equal("live", framework.GetProperty("switch").GetProperty("mode").GetString());
        }

        ModsMenu.ClickVisibleWidget("content", ModPagePane.FrameworkKey + "/" + ModFrameworkLine.CellSwitch, null, null);
        var dialog = UiDialog.Current;
        Assert.NotNull(dialog);
        Assert.Equal("switch-off", dialog!.Kind);
        Assert.Equal(ModsMenu.SwitchOffHeading("Base"), dialog.Content.Title);
        Assert.Contains("Addon", dialog.Content.Body);
        Assert.Empty(_backend.Calls);

        // No: nothing changes.
        ModsMenu.ClickVisibleWidget("content", ModsMenu.SwitchDialogTag + "-buttons/" + UiPromptBlock.CellNo, null, null);
        Assert.Empty(_backend.Calls);
        Assert.False(ModEnableState.IsDisabled("test.s3.base"));

        // Yes: both go.
        ModsMenu.ClickVisibleWidget("content", ModPagePane.FrameworkKey + "/" + ModFrameworkLine.CellSwitch, null, null);
        ModsMenu.ClickVisibleWidget("content", ModsMenu.SwitchDialogTag + "-buttons/" + UiPromptBlock.CellYes, null, null);
        Assert.Contains("suspend:test.s3.base", _backend.Calls);
        Assert.True(ModEnableState.IsDisabled("test.s3.base"));
        Assert.Equal(ModSwitch.TurnedOff, ModsMenu.ModsActionNote);
        Assert.True(ModsMenu.IsOpen, "switching off the mod on show must not close the workspace");

        // Progenitor itself: refused, with its reason, and not clickable.
        var core = ModSwitch.Decide(new ModSwitchFacts { Id = "progenitor", Owner = ModOwner.Required });
        Assert.Equal(ModSwitchMode.Refused, core.Mode);
        Assert.Equal(ModSwitchWords.RequiredRefusal, core.Reason);
        Assert.False(ModSwitch.Set("progenitor", "Progenitor", ModOwner.Required, on: false).Ok);
        var coreLine = ModFrameworkLine.Compose(new ModFrameworkFacts { SwitchMode = core.Mode, SwitchReason = core.Reason });
        Assert.False(coreLine.SwitchInteractive);
        Assert.False(coreLine.BuildRow("page-framework", 1200f).Find(ModFrameworkLine.CellSwitch)!.Clickable);
        Assert.Equal(ModSwitchWords.RequiredRefusal, coreLine.SwitchReason);
        Assert.Equal(ModSwitchMode.Refused, ModSwitch.Decide(new ModSwitchFacts { Owner = ModOwner.Loader }).Mode);
    }

    /// <summary>
    /// An ordinary BepInEx plugin: with certain files the switch writes a next-start request (and
    /// pressing it back cancels it); without, it stays "Managed elsewhere" with the reason.
    /// </summary>
    [Fact]
    public void An_ordinary_plugin_switches_through_a_next_start_request()
    {
        string root = Path.Combine(_dir, "BepInEx");
        string folder = Path.Combine(root, "plugins", "Author-Plain");
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(Path.Combine(folder, "Plain.dll"), "plain bytes");
        ModSwitch.BepInExRoot = () => root;
        var ident = PluginFileActions.Identify(root, "test.s3.plain", Path.Combine(folder, "Plain.dll"), Array.Empty<LoadedPluginFact>());
        Assert.True(ident.Certain, ident.Why);

        var view = ModSwitch.Describe("test.s3.plain", "Plain", ModOwner.Plain, ident);
        Assert.Equal(ModSwitchMode.Restart, view.Mode);
        var off = ModSwitch.Set("test.s3.plain", "Plain", ModOwner.Plain, on: false, ident);
        Assert.True(off.Ok, off.Message);
        Assert.True(PluginFileActions.TryRead(root, out var state, out _));
        Assert.Equal(PluginFileAction.Disable, Assert.Single(state.Requests).Action);
        var pending = ModSwitch.Describe("test.s3.plain", "Plain", ModOwner.Plain, ident);
        Assert.Equal(ModSwitchPending.TurnsOff, pending.Pending);
        Assert.Equal(ModSwitchWords.TurnsOffAtNextStart, pending.PendingWord);

        var back = ModSwitch.Set("test.s3.plain", "Plain", ModOwner.Plain, on: true, ident);
        Assert.True(back.Ok, back.Message);
        Assert.StartsWith(ModSwitch.RequestCanceled, back.Message);
        Assert.True(PluginFileActions.TryRead(root, out state, out _));
        Assert.Empty(state.Requests);

        var remove = ModSwitch.RequestRemove("test.s3.plain", "Plain", ident);
        Assert.True(remove.Ok, remove.Message);
        Assert.Equal(ModSwitchPending.Removed, ModSwitch.Describe("test.s3.plain", "Plain", ModOwner.Plain, ident).Pending);

        var shared = new PluginIdentification { Certain = false, Why = PluginFileActions.WhyShared("Other") };
        var refused = ModSwitch.Decide(new ModSwitchFacts { Owner = ModOwner.Plain, PlainWhy = shared.Why });
        Assert.Equal(ModSwitchMode.Unavailable, refused.Mode);
        Assert.Equal(ModSwitch.ManagedElsewhereReason(shared.Why), refused.Reason);
    }

    // ---- review findings ----------------------------------------------------------------------

    /// <summary>
    /// Review finding 1: a plugin that went with a turned-off mod is held only while both are still
    /// in Bootstrap's table. After a restart (an empty table) it runs like any other, so a server pause
    /// of it later is ended by the leave, and the stale list is pruned.
    /// </summary>
    [Fact]
    public void A_dll_mate_is_not_held_after_a_restart_and_a_leave_resumes_it()
    {
        _backend.Add("Pair.dll", new[] { "test.s3.a", "test.s3.z" });
        Known("test.s3.a", "A");
        Known("test.s3.z", "Z");
        Assert.True(ModSwitch.Set("test.s3.a", "A", ModOwner.Progenitor, on: false).Ok);
        Assert.Equal(new[] { "test.s3.z" }, ModEnableState.Get("test.s3.a")!.HeldWith);
        Assert.True(ModSwitch.IsHeld("test.s3.z"));

        // A Core swap keeps Bootstrap's table: the hold stays (read from the file, not the session).
        ModSwitch.ResetForTests();
        ModSwitch.BackendOverride = () => _backend;
        ModSwitch.ClockUtc = () => Now;
        Assert.True(ModSwitch.IsHeld("test.s3.z"));
        Assert.Equal(0, ModSwitch.PruneStaleHolds());

        // A process start: the table is empty and the DLL loads again (A inert, Z running).
        _backend.Out.Clear();
        _backend.Loaded.Clear();
        _backend.Add("Pair.dll", new[] { "test.s3.a", "test.s3.z" });
        ModSwitch.ResetForTests();
        ModSwitch.BackendOverride = () => _backend;
        Assert.False(ModSwitch.IsHeld("test.s3.z"));
        Assert.True(ModSwitch.IsHeld("test.s3.a"), "the mod the player turned off stays held");
        Assert.Equal(1, ModSwitch.PruneStaleHolds());
        Assert.Empty(ModEnableState.Get("test.s3.a")!.HeldWith);

        // A server pauses that DLL; the leave resumes Z's DLL (A's load is skipped again when it loads).
        _backend.Suspend(new[] { "test.s3.z" }, Array.Empty<string>(), out _);
        ServerModPause.Test = new ServerModPause.TestSeams { Backend = _backend, Post = a => a() };
        Assert.Contains("test.s3.z", ServerModPause.Current.PausedIds);
        _backend.Calls.Clear();
        ServerModPause.OnConnectionEnded();
        Assert.Contains(_backend.Calls, c => c.StartsWith("resume:", StringComparison.Ordinal) && c.Contains("test.s3.z"));
    }

    /// <summary>
    /// Review finding 2: while the joined server has mods paused, turning a mod on or pressing Retry
    /// never resumes (Bootstrap's resume would bring the server-paused dependant back too). The mod is
    /// released from the player's hold and loads on the leave.
    /// </summary>
    [Fact]
    public void Turning_on_or_retrying_never_resumes_a_mod_the_server_paused()
    {
        _backend.Add("Base.dll", new[] { "test.s3.base" });
        _backend.Add("Addon.dll", new[] { "test.s3.addon" }, "test.s3.base");
        Known("test.s3.base", "Base");
        Known("test.s3.addon", "Addon", "test.s3.base");
        _backend.Suspend(new[] { "test.s3.addon" }, Array.Empty<string>(), out _); // the server's pause
        Assert.True(ModSwitch.Set("test.s3.base", "Base", ModOwner.Progenitor, on: false).Ok);
        Assert.Equal(new[] { "test.s3.addon" }, ModSwitch.ServerPausedNow());

        _backend.Calls.Clear();
        var on = ModSwitch.Set("test.s3.base", "Base", ModOwner.Progenitor, on: true);
        Assert.True(on.Ok, on.Message);
        Assert.Equal(ModSwitchWords.TurnedOnAfterLeave, on.Message);
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("resume:", StringComparison.Ordinal));
        Assert.Contains(_backend.Out, o => o.Unit.Guids.Contains("test.s3.addon"));
        Assert.False(ModSwitch.IsHeld("test.s3.base"), "no longer the player's: the leave loads it");

        _backend.Add("Broken.dll", new[] { "test.s3.broken" });
        ModRegistry.NoteLoading("test.s3.broken", "Broken", "1.0.0", null, null);
        ModRegistry.NoteFailed("test.s3.broken");
        var retry = ModSwitch.Retry("test.s3.broken");
        Assert.False(retry.Ok);
        Assert.Equal(ModSwitchWords.RetryAfterLeave, retry.Message);
    }

    /// <summary>
    /// Review finding 4 and the running rule: a dormant plugin has no switch here, a plugin skipped
    /// because its dependency is off says so, and a failed load is neither "turns on" nor "turns off".
    /// </summary>
    [Fact]
    public void Dormant_off_because_of_a_dependency_and_failed_have_their_own_states()
    {
        ModRegistry.NoteLoading("test.s3.dormant", "ServerSide", "1.0.0", null, null);
        ModRegistry.NoteDormant("test.s3.dormant");
        var dormant = ModSwitch.Describe("test.s3.dormant", "ServerSide", ModOwner.Progenitor);
        Assert.Equal(ModSwitchMode.Unavailable, dormant.Mode);
        Assert.Equal(ModSwitchWords.DormantReason, dormant.Reason);
        Assert.Equal(ModSwitchPending.None, dormant.Pending);

        Known("test.s3.base", "Base");
        ModEnableState.Set("test.s3.base", "Base", false, null, Now, out _);
        ModRegistry.NoteLoading("test.s3.child", "Child", "1.0.0", new[] { "test.s3.base" }, null);
        ModRegistry.NoteSkipped("test.s3.child", ModSwitchWords.OffBecause("test.s3.base"), "test.s3.base");
        var child = ModSwitch.Describe("test.s3.child", "Child", ModOwner.Progenitor);
        Assert.Equal(ModSwitchMode.Unavailable, child.Mode);
        Assert.Equal(ModSwitchPending.None, child.Pending);
        Assert.Equal("Base", child.OffBecause);
        Assert.Equal(ModSwitchWords.OffBecauseReason("Base"), child.Reason);
        var line = ModFrameworkLine.Compose(new ModFrameworkFacts { EnabledIntent = child.On, OffBecause = child.OffBecause });
        Assert.Equal(ModRunStatus.Disabled, line.Status);
        Assert.Equal(ModSwitchWords.OffBecauseWord("Base"), line.StatusWord);

        ModRegistry.NoteLoading("test.s3.failed", "Failed", "1.0.0", null, null);
        ModRegistry.NoteFailed("test.s3.failed");
        Assert.False(ModRegistry.TryGet("test.s3.failed", out var failed) && failed.Running);
        Assert.Equal(ModSwitchPending.None, ModSwitch.Describe("test.s3.failed", "Failed", ModOwner.Progenitor).Pending);
        ModEnableState.Set("test.s3.failed", "Failed", false, null, Now, out _);
        var failedOff = ModSwitch.Describe("test.s3.failed", "Failed", ModOwner.Progenitor);
        Assert.Equal(ModSwitchPending.None, failedOff.Pending);
        Assert.True(failedOff.Disabled);
    }

    /// <summary>
    /// Review finding 5: turning on a mod whose load was skipped unloads it first, so the page steps off
    /// it for every live action, not only for turning off; the workspace stays open on its page.
    /// </summary>
    [Fact]
    public void Turning_on_a_skipped_hot_mod_keeps_its_page_open()
    {
        _backend.Add("Skipped.dll", new[] { "test.s3.skipped" });
        ModRegistry.NoteLoading("test.s3.skipped", "Skipped", "1.0.0", null, null);
        ModRegistry.NoteSkipped("test.s3.skipped", ModSwitchWords.OffByPlayer);
        ModEnableState.Set("test.s3.skipped", "Skipped", false, null, Now, out _);
        using var reg = ModsMenu.Register(_owner, "test.s3.skipped", "Skipped");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.s3.skipped");

        ModsMenu.ClickVisibleWidget("content", ModPagePane.FrameworkKey + "/" + ModFrameworkLine.CellSwitch, null, null);
        Assert.Contains("suspend:test.s3.skipped", _backend.Calls);
        Assert.Contains(_backend.Calls, c => c.StartsWith("resume:", StringComparison.Ordinal));
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(ModSwitch.TurnedOn, ModsMenu.ModsActionNote);
        using (var page = Widgets("content"))
            Assert.NotNull(Extra(page, ModPagePane.RowFramework));

        string apply = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ModList.cs"), "private static void ApplySwitch(");
        SourcePin.AssertUncommented(apply, "bool live = mode == ModSwitchMode.Live;");
    }

    // ---- review of 5a6e40f8 ---------------------------------------------------------------------

    /// <summary>
    /// Blocking finding 1: the "taken with it" list belongs to the process that recorded it. In a new
    /// process (a restart, even without opening the menu) a sibling is not held, a server pause of it
    /// is ended by the leave, and Core load prunes the list.
    /// </summary>
    [Fact]
    public void Held_siblings_belong_to_the_process_that_took_them()
    {
        ModEnableState.ProcessSession = () => "pid:1@100";
        _backend.Add("Pair.dll", new[] { "test.s3.x", "test.s3.y" });
        Known("test.s3.x", "X");
        Known("test.s3.y", "Y");
        Assert.True(ModSwitch.Set("test.s3.x", "X", ModOwner.Progenitor, on: false).Ok);
        Assert.Equal("pid:1@100", ModEnableState.Get("test.s3.x")!.Session);
        Assert.True(ModSwitch.IsHeld("test.s3.y"));

        // A restart: another process. Suppose a server pause has both out before any menu opens.
        ModEnableState.ProcessSession = () => "pid:2@200";
        ModSwitch.ResetForTests();
        ModSwitch.BackendOverride = () => _backend;
        Assert.Empty(ModEnableState.HeldWithNow(ModEnableState.Get("test.s3.x")));
        Assert.False(ModSwitch.IsHeld("test.s3.y"), "the new process never took it");
        Assert.True(ModSwitch.IsHeld("test.s3.x"));
        ServerModPause.Test = new ServerModPause.TestSeams { Backend = _backend, Post = a => a() };
        _backend.Calls.Clear();
        ServerModPause.OnConnectionEnded();
        Assert.Contains(_backend.Calls, c => c.StartsWith("resume:", StringComparison.Ordinal) && c.Contains("test.s3.y"));

        // Core load prunes another process's list (Plugin.Install calls this first thing).
        Assert.Equal(1, ModEnableState.PruneOtherSessions());
        Assert.Empty(ModEnableState.Get("test.s3.x")!.HeldWith);
        string install = SourcePin.MethodBody(SourcePin.ReadFile("src", "Progenitor.Core", "Plugin.cs"), "internal static bool Install(");
        SourcePin.AssertUncommented(install, "Progenitor.Plugins.ModEnableState.PruneOtherSessions()");
    }

    /// <summary>
    /// Medium finding 4: the declaration is directional. On the client it lowers the client's own row;
    /// on the server only Optional or ClientExtras lowers the requirement that clients have the mod. A
    /// lowered row that fails on a version mismatch still pauses, and a counterpart plugin never makes
    /// the DLL it shares unpausable.
    /// </summary>
    [Fact]
    public void A_counterpart_is_directional_and_does_not_protect_its_dll()
    {
        var smh = NetworkCompatibilityLevel.ServerMustHave;
        var nnfs = NetworkCompatibilityLevel.NoNeedForSync;
        Assert.Equal(nnfs, CounterpartPolicy.EffectiveLevel(smh, CounterpartNeed.Limited, isServer: false, null));
        Assert.Equal(smh, CounterpartPolicy.EffectiveLevel(smh, CounterpartNeed.Limited, isServer: true, null));
        Assert.Equal(nnfs, CounterpartPolicy.EffectiveLevel(smh, CounterpartNeed.Optional, isServer: true, null));
        Assert.Equal(nnfs, CounterpartPolicy.EffectiveLevel(smh, CounterpartNeed.Required, isServer: true, "A minimap"));
        Assert.Equal(smh, CounterpartPolicy.EffectiveLevel(smh, null, isServer: false, null));

        ServerModPausePolicy.LoweredProbe = id => id == "test.s3.cp";
        try
        {
            (IReadOnlyList<string>, string) Closure(IReadOnlyList<string> g) =>
                (g.Contains("test.s3.hard") ? new[] { "test.s3.hard", "test.s3.cp" } : g.ToArray(), "");
            // The server runs the counterpart mod at another version: the lowered row is decided exactly
            // as its ServerMustHave row was (the server asks about it, so it is not paused either way).
            var askedOld = new[] { new Handshake.ModRecord("test.s3.cp", "0.9", smh, NetworkVersionRule.Exact, true) };
            var lowered = new[] { new Handshake.ModRecord("test.s3.cp", "1.0", nnfs, NetworkVersionRule.Exact, true) };
            var original = new[] { new Handshake.ModRecord("test.s3.cp", "1.0", smh, NetworkVersionRule.Exact, true) };
            var a1 = ServerModPausePolicy.PlanPause(askedOld, false, lowered, null, new[] { "test.s3.cp" }, Closure, autoPause: true);
            var a2 = ServerModPausePolicy.PlanPause(askedOld, false, original, null, new[] { "test.s3.cp" }, Closure, autoPause: true);
            Assert.Equal(a2.Pause, a1.Pause);
            Assert.Equal(a2.Why, a1.Why);

            // A ServerMustHave DLL-mate fails; the counterpart plugin in its DLL does not block the pause.
            var asked = new[] { new Handshake.ModRecord("server.only", "1.0", nnfs, NetworkVersionRule.Exact, true) };
            var both = new[]
            {
                new Handshake.ModRecord("test.s3.hard", "1.0", smh, NetworkVersionRule.Exact, true),
                new Handshake.ModRecord("test.s3.cp", "1.0", nnfs, NetworkVersionRule.Exact, true),
            };
            var plan = ServerModPausePolicy.PlanPause(asked, false, both, null, new[] { "test.s3.hard", "test.s3.cp" }, Closure, autoPause: true);
            Assert.Equal("", plan.Why);
            Assert.Contains("test.s3.hard", plan.Pause);
            Assert.False(ServerModPausePolicy.DeclaresItWorksAlone("test.s3.cp", both, null));
        }
        finally { ServerModPausePolicy.LoweredProbe = null; }
    }

    // ---- CLIENTPOLICY3 live 2026-10-05: a mod switched off and on mid-session ----------------------------------------

    private List<Handshake.ModRecord> JoinedServer(string? rememberedPolicy = null)
    {
        var asked = new List<Handshake.ModRecord>
        {
            new("other.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        ServerModPause.Test = new ServerModPause.TestSeams
        {
            Backend = _backend, Post = a => a(), Host = () => "203.0.113.5:9876", Asked = () => asked.ToArray(),
            Provided = () => Array.Empty<Handshake.ModRecord>(), Owners = () => new Dictionary<string, string>(),
            ServerAsksReport = () => rememberedPolicy != null, RememberedPolicy = _ => rememberedPolicy ?? "",
            ClientMods = () => Array.Empty<ClientModEntry>(),
        };
        ServerModPause.OnRequest(new List<Handshake.ModRecord>(asked), false, () => true);
        return asked;
    }

    private static string Policy(ClientPolicyMode mode, string[]? required = null, string[]? approved = null) =>
        ClientPolicyView.Remembered(new JoinSection("", null, new List<RecommendedMod>(), null).WithPolicy(
            (required ?? Array.Empty<string>()).Select(id => new ClientModRow(id, "", "", ClientModRule.Any, "", id)).ToList(),
            mode,
            (approved ?? Array.Empty<string>()).Select(id => new ClientModRow(id, "", "", ClientModRule.Any)).ToList(),
            new List<string>()));

    /// <summary>
    /// Live 2026-10-05 (step 9): a required mod switched off mid-session came back as "stays paused for this server",
    /// and the server disconnected the player for it. The switch's own unload is the player's pause: turning the mod on
    /// loads it now, with or without a client mod policy.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mod_the_switch_turned_off_loads_again_when_turned_on_mid_session(bool requiredByPolicy)
    {
        _backend.Add("Uia.dll", new[] { "test.s3.uia" });
        Known("test.s3.uia", "Hot Test");
        JoinedServer(requiredByPolicy ? Policy(ClientPolicyMode.Any, required: new[] { "test.s3.uia" }) : null);

        Assert.True(ModSwitch.Set("test.s3.uia", "Hot Test", ModOwner.Progenitor, on: false).Ok);
        Assert.Empty(_backend.Loaded);
        var on = ModSwitch.Set("test.s3.uia", "Hot Test", ModOwner.Progenitor, on: true);
        Assert.True(on.Ok, on.Message);
        Assert.Equal(ModSwitch.TurnedOn, on.Message);
        Assert.Contains(_backend.Loaded, u => u.Guids.Contains("test.s3.uia"));
        Assert.DoesNotContain(_backend.Suspended(), p => p.Guid == "test.s3.uia");
    }

    /// <summary>
    /// A mod the server's own pause took (not the switch) that the joined server requires or approves is not "paused
    /// for this server": turned on, it loads. One the policy does not allow stays paused, and the result says why.
    /// </summary>
    [Fact]
    public void A_server_pause_follows_the_joined_servers_client_mod_policy_when_the_mod_is_turned_on()
    {
        _backend.Add("Req.dll", new[] { "test.s3.req" });
        _backend.Add("Bad.dll", new[] { "test.s3.bad" });
        Known("test.s3.req", "Required Mod");
        Known("test.s3.bad", "Bad Mod");
        // Both were taken by a server pause (an earlier policy, or a pause the server's request asked for).
        _backend.Suspend(new[] { "test.s3.req", "test.s3.bad" }, Array.Empty<string>(), out _);
        JoinedServer(Policy(ClientPolicyMode.Approved, required: new[] { "test.s3.req" }));

        Assert.Equal(ServerModPause.StandingRequired, ServerModPause.PolicyStanding("test.s3.req"));
        Assert.Equal(ServerModPause.StandingNotAllowed, ServerModPause.PolicyStanding("test.s3.bad"));
        Assert.False(ServerModPause.PausedForCurrentServer("test.s3.req"));
        Assert.True(ServerModPause.PausedForCurrentServer("test.s3.bad"));

        // The bad one switched off and on: still paused, and the switch says why.
        Assert.True(ModSwitch.Set("test.s3.bad", "Bad Mod", ModOwner.Progenitor, on: false).Ok);
        var bad = ModSwitch.Set("test.s3.bad", "Bad Mod", ModOwner.Progenitor, on: true);
        Assert.True(bad.Ok, bad.Message);
        Assert.Equal(ModSwitch.TurnedOnNotAllowed, bad.Message);
        Assert.Contains(_backend.Suspended(), p => p.Guid == "test.s3.bad");

        // The required one loads even though another mod is paused for this server.
        Assert.True(ModSwitch.Set("test.s3.req", "Required Mod", ModOwner.Progenitor, on: false).Ok);
        var req = ModSwitch.Set("test.s3.req", "Required Mod", ModOwner.Progenitor, on: true);
        Assert.True(req.Ok, req.Message);
        Assert.Contains(_backend.Loaded, u => u.Guids.Contains("test.s3.req"));
        Assert.Contains(_backend.Suspended(), p => p.Guid == "test.s3.bad");
    }

    /// <summary>
    /// CLIENTPOLICY3, live 2026-10-05: a mod paused because the policy did not allow it stayed paused when the admins then
    /// required it ("Now required here; install it …"). A mid-session policy update that allows it resumes it now; one the
    /// player switched off, and one still not allowed, stay paused.
    /// </summary>
    [Fact]
    public void A_policy_update_that_allows_a_mod_paused_for_the_policy_resumes_it()
    {
        _backend.Add("Uia.dll", new[] { "test.s3.uia" });
        _backend.Add("Bad.dll", new[] { "test.s3.bad" });
        _backend.Add("Mine.dll", new[] { "test.s3.mine" });
        Known("test.s3.uia", "Hot Test");
        Known("test.s3.bad", "Bad Mod");
        Known("test.s3.mine", "Mine");
        string remembered = Policy(ClientPolicyMode.Approved, approved: new[] { "test.s3.mine" });
        var shown = new List<PauseNoticeContent>();
        ServerProfiles.TestDirectory = _dir;
        var asked = JoinedServer(remembered);
        ServerModPause.Test!.RememberedPolicy = _ => remembered;
        ServerModPause.PauseNoticeRequested += shown.Add;
        try
        {
            Assert.True(ServerModPause.PauseForPolicy(new[] { "test.s3.uia" }).Ok);
            Assert.True(ServerModPause.PauseForPolicy(new[] { "test.s3.bad" }).Ok);
            Assert.True(ServerModPause.PausedForPolicy("test.s3.uia"));
            Assert.True(ModSwitch.Set("test.s3.mine", "Mine", ModOwner.Progenitor, on: false).Ok); // the player's own
            Assert.Equal(0, ServerModPause.ResumePolicyPausedNowAllowed()); // nothing changed yet

            // The admins require uia (and approve mine): the update brings uia back now, and says so.
            remembered = Policy(ClientPolicyMode.Approved, required: new[] { "test.s3.uia" }, approved: new[] { "test.s3.mine" });
            Assert.Equal(1, ServerModPause.ResumePolicyPausedNowAllowed());
            Assert.Contains(_backend.Loaded, u => u.Guids.Contains("test.s3.uia"));
            Assert.Contains(_backend.Suspended(), p => p.Guid == "test.s3.bad");
            Assert.Contains(_backend.Suspended(), p => p.Guid == "test.s3.mine");
            Assert.False(ServerModPause.PausedForPolicy("test.s3.uia"));
            var back = Assert.Single(shown, c => c.Title == ServerModPause.BackOnTitle);
            Assert.Equal("This server allows this mod now, so it's back on:", back.Body);
            Assert.Single(back.Lines);

            // The policy turned off altogether: the rest of what it paused comes back too (not the player's).
            remembered = "";
            Assert.Equal(1, ServerModPause.ResumePolicyPausedNowAllowed());
            Assert.Contains(_backend.Loaded, u => u.Guids.Contains("test.s3.bad"));
            Assert.Contains(_backend.Suspended(), p => p.Guid == "test.s3.mine");
            Assert.NotNull(asked);
        }
        finally
        {
            ServerModPause.PauseNoticeRequested -= shown.Add;
            ServerProfiles.TestDirectory = null;
        }
    }

    /// <summary>
    /// Medium finding 5: "paused for this server" comes from the current join, not the host's list of
    /// an earlier visit. A server whose request now names the mod runs it; one that does not keeps it
    /// paused; no join means not paused.
    /// </summary>
    [Fact]
    public void Paused_for_this_server_reads_the_current_join()
    {
        _backend.Add("Mod.dll", new[] { "test.s3.mod" });
        _backend.Suspend(new[] { "test.s3.mod" }, Array.Empty<string>(), out _);
        var asked = new List<Handshake.ModRecord>();
        ServerModPause.Test = new ServerModPause.TestSeams
        {
            Backend = _backend, Post = a => a(), Host = () => "203.0.113.5:9876", Asked = () => asked.ToArray(),
            Provided = () => Array.Empty<Handshake.ModRecord>(), Owners = () => new Dictionary<string, string>(),
        };
        Assert.False(ServerModPause.PausedForCurrentServer("test.s3.mod"), "no join recorded yet");
        asked.Add(new Handshake.ModRecord("other.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true));
        ServerModPause.OnRequest(new List<Handshake.ModRecord>(asked), false, () => true);
        Assert.True(ServerModPause.PausedForCurrentServer("test.s3.mod"));
        asked.Add(new Handshake.ModRecord("test.s3.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true));
        Assert.False(ServerModPause.PausedForCurrentServer("test.s3.mod"), "this join's server runs it");
        string src = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ServerModPause.cs"), "internal static bool PausedForCurrentServer(");
        SourcePin.AssertNoStatementContaining(src, "LastPaused");
    }

    /// <summary>
    /// Medium finding 6: ordinary plugins that hard-depend on a restart-only mod are named in the
    /// confirmation, and each gets a problem while the mod is off; it clears when the mod is on again.
    /// </summary>
    [Fact]
    public void Plain_dependants_are_named_and_told_their_dependency_is_off()
    {
        Known("test.s3.lib", "Lib");
        ModSwitch.PlainDependantsProbe = id => id == "test.s3.lib"
            ? new[] { ("plain.user", "PlainUser") }
            : Array.Empty<(string, string)>();
        var view = ModSwitch.Describe("test.s3.lib", "Lib", ModOwner.Progenitor);
        Assert.Equal(ModSwitchMode.Restart, view.Mode);
        Assert.Contains("PlainUser", ModSwitch.AlsoTurnsOff("test.s3.lib", view));

        Assert.True(ModSwitch.Set("test.s3.lib", "Lib", ModOwner.Progenitor, on: false).Ok);
        var p = Assert.Single(ModProblems.For("plain.user"));
        Assert.Equal(ModProblemKinds.DependencyOff, p.Kind);
        Assert.Equal(DependencyOffSource.Sentence("Lib"), p.Sentence);
        Assert.Equal(DependencyOffSource.ListLine("Lib"), p.ShortLine);
        Assert.True(ModSwitch.Set("test.s3.lib", "Lib", ModOwner.Progenitor, on: true).Ok);
        Assert.Empty(ModProblems.For("plain.user"));
        Assert.Equal("PlainUser turns off at next start too.", ModsMenu.PlainAlsoOffNote(new List<string> { "PlainUser" }));
    }

    /// <summary>
    /// Re-review of 9a27bf01, finding 1: turning a restart-only mod off turns its ordinary dependants off
    /// at the next start and records that; turning it on again cancels a request still waiting and asks
    /// to turn back on one already carried out. A dependant the player turned off separately stays off.
    /// </summary>
    [Fact]
    public void Plain_dependants_come_back_when_the_mod_they_need_is_on_again()
    {
        string root = Path.Combine(_dir, "BepInEx");
        PluginIdentification Mod(string folder, string dll)
        {
            string dir = Path.Combine(root, "plugins", folder);
            Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(Path.Combine(dir, dll), dll + " bytes");
            return PluginFileActions.Identify(root, "plain." + folder.ToLowerInvariant(), Path.Combine(dir, dll), Array.Empty<LoadedPluginFact>());
        }

        var waiting = Mod("Waiting", "Waiting.dll");
        var done = Mod("Done", "Done.dll");
        var own = Mod("Own", "Own.dll");
        Assert.True(waiting.Certain && done.Certain && own.Certain, waiting.Why + done.Why + own.Why);
        ModSwitch.BepInExRoot = () => root;
        var idents = new Dictionary<string, PluginIdentification>
        {
            ["plain.waiting"] = waiting, ["plain.done"] = done, ["plain.own"] = own,
        };
        ModSwitch.PlainDependantsProbe = id => id == "test.s3.lib"
            ? new[] { ("plain.waiting", "Waiting"), ("plain.done", "Done"), ("plain.own", "Own") }
            : Array.Empty<(string, string)>();
        Known("test.s3.lib", "Lib");

        // The player turned Own off first; that is not the library's doing.
        Assert.True(ModSwitch.Set("plain.own", "Own", ModOwner.Plain, on: false, own).Ok);
        Assert.True(ModSwitch.Set("test.s3.lib", "Lib", ModOwner.Progenitor, on: false).Ok);
        var names = ModSwitch.TurnOffPlainDependants("test.s3.lib", id => idents[id]);
        Assert.Equal(new[] { "Waiting", "Done" }, names);
        Assert.Equal(new[] { "plain.waiting", "plain.done" }, ModEnableState.Get("test.s3.lib")!.PlainOff);

        // The next start carries out Done's request (Waiting's is canceled below while still waiting).
        Assert.True(PluginFileActions.TryRead(root, out var state, out _));
        var doneRequest = state.Requests.Single(r => r.PluginId == "plain.done");
        PluginFileActions.Cancel(root, "plain.done", out _);
        PluginFileActions.Request(root, doneRequest, out _);
        var executed = PluginFileActions.Execute(root, doneRequest, Now);
        Assert.True(executed.Done, executed.Reason);
        PluginFileActions.TryRead(root, out state, out _);
        state.Requests.RemoveAll(r => r.PluginId == "plain.done");
        state.Results.Add(executed);
        Assert.True(PluginFileActions.TryWrite(root, state, out _));

        Assert.True(ModSwitch.Set("test.s3.lib", "Lib", ModOwner.Progenitor, on: true).Ok);
        PluginFileActions.TryRead(root, out state, out _);
        Assert.Null(PluginFileActions.PendingFor(state, "plain.waiting"));
        Assert.Equal(PluginFileAction.Enable, PluginFileActions.PendingFor(state, "plain.done")!.Action);
        Assert.Equal(PluginFileAction.Disable, PluginFileActions.PendingFor(state, "plain.own")!.Action);
    }

    /// <summary>
    /// The mod page of a mod another tool installed: Uninstall in the status bar's action slot asks in
    /// the danger tone, then the page says "Removed at next start" and offers Cancel.
    /// </summary>
    [Fact]
    public void A_plain_mod_page_uninstalls_at_the_next_start_and_can_cancel()
    {
        string root = Path.Combine(_dir, "BepInEx");
        string dir = Path.Combine(root, "plugins", "Author-Plain");
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(Path.Combine(dir, "Plain.dll"), "plain bytes");
        var ident = PluginFileActions.Identify(root, "test.s3.plain", Path.Combine(dir, "Plain.dll"), Array.Empty<LoadedPluginFact>());
        Assert.True(ident.Certain, ident.Why);
        ModSwitch.BepInExRoot = () => root;
        ModsMenu.PlainIdentityProbe = id => id == "test.s3.plain" ? ident : null;
        using var reg = ModsMenu.Register(_owner, "test.s3.plain", "Plain");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.s3.plain");
        Assert.Equal(ModPageCopy.Uninstall, ModsMenu.ModPage!.Framework.ActionCaption);

        ModsMenu.ClickVisibleWidget("content", ModPagePane.FrameworkKey + "/" + ModFrameworkLine.CellAction, null, null);
        var dialog = UiDialog.Current!;
        Assert.Equal(ModsMenu.PlainUninstallBody, dialog.Content.Body);
        Assert.Equal(CellTone.Danger, dialog.Content.MainTone);
        ModsMenu.ClickVisibleWidget("content", ModsMenu.PlainUninstallDialogTag + "-buttons/" + UiPromptBlock.CellYes, null, null);
        Assert.True(PluginFileActions.TryRead(root, out var state, out _));
        Assert.Equal(PluginFileAction.Remove, Assert.Single(state.Requests).Action);
        Assert.Equal(ModSwitchWords.RemovedAtNextStart, ModsMenu.ModPage!.Framework.StatusWord);
        Assert.Equal(ModsMenu.PlainCancelCaption, ModsMenu.ModPage!.Framework.ActionCaption);

        ModsMenu.ClickVisibleWidget("content", ModPagePane.FrameworkKey + "/" + ModFrameworkLine.CellAction, null, null);
        Assert.True(PluginFileActions.TryRead(root, out state, out _));
        Assert.Empty(state.Requests);
    }

    /// <summary>A loose single-DLL mod in <c>BepInEx/plugins</c> that another tool installed, on its mod page.</summary>
    private string PlaceLoosePlainMod()
    {
        string root = Path.Combine(_dir, "BepInEx");
        string plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(plugins);
        System.IO.File.WriteAllText(Path.Combine(plugins, "Loose.dll"), "loose bytes");
        var ident = PluginFileActions.Identify(root, "test.s3.loose", Path.Combine(plugins, "Loose.dll"),
            Array.Empty<LoadedPluginFact>(), pluginGuidsOf: _ => new[] { "test.s3.loose" });
        Assert.True(ident.Loose);
        Assert.True(ident.Certain, ident.Why);
        ModSwitch.BepInExRoot = () => root;
        ModsMenu.PlainIdentityProbe = id => id == "test.s3.loose" ? ident : null;
        // The live wiring: the bar's pending changes are the Mods window's, and its confirmation is a framework dialog.
        JoinFlowClient.PendingRestartProbe = ModsMenu.ClientPendingRestart;
        JoinFlowClient.RestartDialogFactory = title => UiApi.Panel(title, _owner, "test.s3.restart");
        return root;
    }

    private static void ClickPlainUninstall()
    {
        ModsMenu.ClickVisibleWidget("content", ModPagePane.FrameworkKey + "/" + ModFrameworkLine.CellAction, null, null);
        Assert.Equal(ModsMenu.PlainUninstallDialogTag, UiDialog.Current!.Tag);
        Assert.Contains(UiDialog.Current!.Content.ExtraButtons, b => b.Key == ModsMenu.YesRestartKey);
    }

    /// <summary>
    /// Review of PR 133, finding 1: a loose DLL with nothing else pending. <b>Yes, uninstall and restart</b> marks the
    /// removal, the removal is part of the pending restart (the page's Pending changes row, the bar), and the shared
    /// restart button asks: its confirmation, in the Manual-mode wording when nothing can relaunch the game here.
    /// Nothing quits before that answer.
    /// What removal breaks: the restart offer never saw a next-start removal of a plugin another tool installed, so the
    /// combined answer marked it and then nothing restarted and nothing said so.
    /// </summary>
    [Fact]
    public void Plain_uninstall_and_restart_marks_the_removal_and_asks_for_its_restart()
    {
        string root = PlaceLoosePlainMod();
        try
        {
            using var reg = ModsMenu.Register(_owner, "test.s3.loose", "Loose");
            ModsMenu.Open();
            ModsMenu.OpenMod("test.s3.loose");
            Assert.False(JoinFlowClient.CurrentRestartOffer().Shown);

            ClickPlainUninstall();
            ModsMenu.ClickVisibleWidget("content", ModsMenu.YesRestartCaption, null, null);

            Assert.True(PluginFileActions.TryRead(root, out var state, out _));
            Assert.Equal(PluginFileAction.Remove, Assert.Single(state.Requests).Action);
            var offer = JoinFlowClient.CurrentRestartOffer();
            Assert.True(offer.Shown);
            var change = Assert.Single(offer.Pending);
            Assert.Equal("test.s3.loose", change.Id);
            Assert.True(change.IsRemoval);

            // The shared button's confirmation, not a restart: no relauncher here, so the Manual wording.
            var confirm = UiDialog.Current;
            Assert.NotNull(confirm);
            Assert.Equal("install-restart", confirm!.Tag);
            Assert.Equal(ClientRestartText.ManualTitle, confirm.Content.Title);
            Assert.Equal(ClientRestartText.QuitToDesktop, confirm.Content.MainCaption);
            Assert.Equal(ModSwitchWords.RemovedAtNextStart + ".", ModsMenu.ModsActionNote);

            // The page shows the pending restart too.
            Assert.Contains("Pending changes", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        }
        finally { JoinFlowClient.Uninstall(); }
    }

    /// <summary>
    /// Review of PR 133, finding 1: an unrelated change waits and this removal is not part of the offer (a stand-in
    /// pending list). The combined answer marks the removal but asks for no restart, and the page says to restart.
    /// What removal breaks: the restart went ahead for the other change instead.
    /// </summary>
    [Fact]
    public void Plain_uninstall_and_restart_never_restarts_for_an_offer_that_does_not_cover_it()
    {
        string root = PlaceLoosePlainMod();
        ModsMenu.PendingRestartForTests = () => new[]
        {
            new PendingClientChange { Id = "other.mod", Name = "Other", Version = "1.0.0", Action = PendingClientChange.ActionInstall },
        };
        try
        {
            using var reg = ModsMenu.Register(_owner, "test.s3.loose", "Loose");
            ModsMenu.Open();
            ModsMenu.OpenMod("test.s3.loose");
            ClickPlainUninstall();
            ModsMenu.ClickVisibleWidget("content", ModsMenu.YesRestartCaption, null, null);

            Assert.True(PluginFileActions.TryRead(root, out var state, out _));
            Assert.Equal(PluginFileAction.Remove, Assert.Single(state.Requests).Action);
            Assert.Null(UiDialog.Current);
            Assert.EndsWith(ModsMenu.RestartAfterUninstallNotCovered, ModsMenu.ModsActionNote);
        }
        finally
        {
            ModsMenu.PendingRestartForTests = null;
            JoinFlowClient.Uninstall();
        }
    }

    /// <summary>
    /// Review of PR 133, finding 2: during a join (the client connecting, the server not answered yet) the combined
    /// answer marks the removal and opens no restart confirmation; the page says to restart once the join has finished.
    /// The bar's own button refuses the same way.
    /// </summary>
    [Fact]
    public void Plain_uninstall_and_restart_never_asks_mid_join()
    {
        PlaceLoosePlainMod();
        JoinFlowClient.JoinConnectingProbe = () => true;
        try
        {
            using var reg = ModsMenu.Register(_owner, "test.s3.loose", "Loose");
            ModsMenu.Open();
            ModsMenu.OpenMod("test.s3.loose");
            ClickPlainUninstall();
            ModsMenu.ClickVisibleWidget("content", ModsMenu.YesRestartCaption, null, null);

            Assert.True(JoinFlowClient.CurrentRestartOffer().Shown);
            Assert.Null(UiDialog.Current);
            Assert.EndsWith(ModsMenu.RestartAfterUninstallJoining, ModsMenu.ModsActionNote);
            Assert.Equal(JoinFlowClient.RestartRefusedJoining, JoinFlowClient.PressRestartButton());

            // Once the join is over, the same button asks.
            JoinFlowClient.JoinConnectingProbe = () => false;
            Assert.Equal("confirm", JoinFlowClient.PressRestartButton());
            Assert.Equal("install-restart", UiDialog.Current!.Tag);
        }
        finally { JoinFlowClient.Uninstall(); }
    }

    /// <summary>
    /// Re-review of 9a27bf01, finding 4: the switch never waits long on the main thread for the
    /// next-start file's lock; while another process holds it the press says busy, try again.
    /// Also: a loose single-DLL mod's request carries the loose flag.
    /// </summary>
    [Fact]
    public void The_switch_says_busy_quickly_while_the_request_file_is_locked()
    {
        string root = Path.Combine(_dir, "BepInEx");
        string plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(plugins);
        System.IO.File.WriteAllText(Path.Combine(plugins, "Loose.dll"), "loose bytes");
        var ident = PluginFileActions.Identify(root, "test.s3.loose", Path.Combine(plugins, "Loose.dll"),
            Array.Empty<LoadedPluginFact>(), pluginGuidsOf: _ => new[] { "test.s3.loose" });
        Assert.True(ident.Certain, ident.Why);
        Assert.True(ident.Loose);
        ModSwitch.BepInExRoot = () => root;

        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            var gate = PluginFileActions.AcquireLock(root, out _);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            PluginFileActions.ReleaseLock(gate);
        });
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var busy = ModSwitch.Set("test.s3.loose", "Loose", ModOwner.Plain, on: false, ident);
            Assert.False(busy.Ok);
            Assert.Equal(ModSwitch.BusyTryAgain, busy.Message);
            // The lock wait is 250 ms; waiting for the holder would take its full 30 s.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }

        Assert.True(ModSwitch.Set("test.s3.loose", "Loose", ModOwner.Plain, on: false, ident).Ok);
        Assert.True(PluginFileActions.TryRead(root, out var state, out _));
        Assert.True(Assert.Single(state.Requests).Loose);
    }

    /// <summary>Re-review of 9a27bf01, finding 5: a failing row is judged by the plugin that owns it.</summary>
    [Fact]
    public void A_counterpart_row_is_judged_by_its_owning_plugin()
    {
        var owners = new Dictionary<string, string> { ["test.s3.row"] = "test.s3.owner" };
        Assert.Equal("test.s3.owner", ServerModPausePolicy.RowOwner("test.s3.row", owners));
        Assert.Equal("test.s3.self", ServerModPausePolicy.RowOwner("test.s3.self", owners));
        var seen = new List<string>();
        ServerModPausePolicy.LoweredProbe = id => { seen.Add(id); return false; };
        try
        {
            var nnfs = NetworkCompatibilityLevel.NoNeedForSync;
            var asked = new[] { new Handshake.ModRecord("test.s3.row", "2.0", nnfs, NetworkVersionRule.Exact, true) };
            var provided = new[] { new Handshake.ModRecord("test.s3.row", "1.0", nnfs, NetworkVersionRule.Exact, true) };
            ServerModPausePolicy.PlanPause(asked, false, provided, owners, new[] { "test.s3.owner" },
                g => (g, ""), autoPause: true);
            Assert.DoesNotContain("test.s3.row", seen);
            string src = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ServerModPause.cs");
            SourcePin.AssertUncommented(src, "LoweredByCounterpart(RowOwner(id, owners))");
        }
        finally { ServerModPausePolicy.LoweredProbe = null; }
    }

    /// <summary>Low finding 10: after a Core swap a plugin skipped for a dependency keeps that state.</summary>
    [Fact]
    public void A_dependency_skip_survives_a_Core_swap()
    {
        Assert.Equal(ModLoadOutcome.SkippedOff, ModsMenu.RehydratedOutcome(false, false, true, "test.s3.dep", out string dep));
        Assert.Equal("test.s3.dep", dep);
        Assert.Equal(ModLoadOutcome.Failed, ModsMenu.RehydratedOutcome(false, false, true, null, out _));
        Assert.Equal(ModLoadOutcome.Loaded, ModsMenu.RehydratedOutcome(true, true, true, "x", out _));
        Assert.Equal(ModLoadOutcome.SkippedOff, ModsMenu.RehydratedOutcome(false, true, true, null, out dep));
        Assert.Equal("", dep);
        Assert.Equal(ModLoadOutcome.Dormant, ModsMenu.RehydratedOutcome(false, false, false, null, out _));
        Assert.True(ModRegistry.NoteRehydrated("test.s3.child2", "Child", "1.0", new[] { "test.s3.dep" }, null, ModLoadOutcome.SkippedOff, "test.s3.dep"));
        Assert.True(ModRegistry.TryGet("test.s3.child2", out var e));
        Assert.Equal("test.s3.dep", e.OffDependency);
        Assert.Equal(ModSwitchMode.Unavailable, ModSwitch.Describe("test.s3.child2", "Child", ModOwner.Progenitor).Mode);
    }

    /// <summary>Low finding 11: deploy treats a hosted VRisingServer child under the game folder as holding the patchers.</summary>
    [Fact]
    public void Deploy_does_not_copy_the_preloader_under_a_running_hosted_server()
    {
        string deploy = System.IO.File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "tools", "deploy.ps1"));
        Assert.Contains("(Test-Running \"VRisingServer\" $GameDir)", deploy, StringComparison.Ordinal);
        Assert.Contains("Copy-Preloader $dll.FullName $t.Patchers $t.Name $t.Proc $inUse $proj", deploy, StringComparison.Ordinal);
    }

    // ---- B. problems ----------------------------------------------------------------------------

    /// <summary>Each source raises its problem and the problem clears when its cause is gone.</summary>
    [Fact]
    public void Each_problem_source_raises_and_clears()
    {
        // A load failure (critical), cleared by the next load (Retry succeeding).
        ModProblems.Report(ModProblems.LoadFailed("test.s3.a", "System.Exception: boom", retry: true));
        var a = ModProblems.For("test.s3.a");
        Assert.Equal(ModProblemSeverity.Critical, Assert.Single(a).Severity);
        Assert.Equal(ModProblems.LoadFailedRetry, a[0].WhatToDo);
        ModProblems.ClearLoadProblems("test.s3.a");
        Assert.Empty(ModProblems.For("test.s3.a"));

        // Translation and content files (warnings), cleared when the mod unloads.
        ModProblems.Report(ModProblems.LocalizationFailed("test.s3.a", "bad json"));
        ModProblems.Report(ModProblems.ContentFailed("test.s3.a", "bad content"));
        Assert.Equal(2, ModProblems.For("test.s3.a").Count);
        ModProblems.Clear("test.s3.a");
        Assert.Empty(ModProblems.For("test.s3.a"));

        // KEYS1: keybind conflicts from the winner rule. Only the side that lost has a problem (its key
        // is not active); it clears by a rebind of either side and by an unload. "Ay" was installed
        // first, so "Bee" loses F9.
        KeybindConflictSource.NameOf = id => id == "test.s3.b" ? "Bee" : id == "test.s3.a" ? "Ay" : null;
        KeybindArbiter.InstallTimeOf = id => id == "test.s3.a"
            ? new ModInstallTime(Now.AddDays(-2), KeybindArbiter.InstallLedger)
            : new ModInstallTime(Now.AddDays(-1), KeybindArbiter.InstallFile);
        using var first = Keybinds.Register(_owner, "test.s3.a", "Open", File("a"), new KeyChord(UiKey.F9), "", () => { });
        var second = Keybinds.Register(new object(), "test.s3.b", "Show", File("b"), new KeyChord(UiKey.F9), "", () => { });
        Assert.Empty(ModProblems.For("test.s3.a"));
        var conflict = Assert.Single(ModProblems.For("test.s3.b"));
        Assert.Equal(ModProblemKinds.KeybindConflict, conflict.Kind);
        Assert.Equal(ModProblemSeverity.Warning, conflict.Severity);
        Assert.Equal("Ay", conflict.OtherMod);
        Assert.Equal(ModProblems.KeySentence("F9", "Ay, installed first"), conflict.Sentence);
        Assert.Equal("Not active: F9 is used by Ay, installed first.", conflict.Sentence);
        Assert.Equal(ModProblems.KeyWhatToDo, conflict.WhatToDo);
        Assert.Equal(ModProblems.KeyListLine("F9", "Ay"), conflict.ShortLine);
        Assert.True(Keybinds.Rebind("test.s3.a", "Open", new KeyChord(UiKey.F8)));
        Assert.Empty(ModProblems.For("test.s3.b"));
        Assert.True(Keybinds.Rebind("test.s3.a", "Open", new KeyChord(UiKey.F9), checkConflicts: false));
        Assert.Single(ModProblems.For("test.s3.b"));
        Assert.True(Keybinds.Rebind("test.s3.b", "Show", new KeyChord(UiKey.F7)));
        Assert.Empty(ModProblems.For("test.s3.b"));
        // KEYS2: a remap onto a used key is refused; the unchecked path rebuilds the conflict for this test.
        Assert.False(Keybinds.Rebind("test.s3.b", "Show", new KeyChord(UiKey.F9)));
        Assert.True(Keybinds.Rebind("test.s3.b", "Show", new KeyChord(UiKey.F9), checkConflicts: false));
        Assert.Single(ModProblems.For("test.s3.b"));
        second.Dispose();
        Assert.Empty(ModProblems.For("test.s3.b"));
        KeybindArbiter.InstallTimeOf = null;

        // Command conflicts: the command, the other owner, the long form that still works.
        CommandConflictSource.Probe = () => new List<(string, IReadOnlyList<string>, IReadOnlyList<string>)>
        {
            ("hunt", new[] { "test.s3.a", "test.s3.b" }, new[] { ".p a hunt", ".p b hunt" }),
        };
        var command = Assert.Single(ModProblems.For("test.s3.a"));
        Assert.Equal(ModProblemKinds.CommandConflict, command.Kind);
        Assert.Contains(".hunt", command.Sentence);
        Assert.Contains("Bee", command.Sentence);
        Assert.Contains(".p a hunt", command.WhatToDo);
        CommandConflictSource.Probe = () => new List<(string, IReadOnlyList<string>, IReadOnlyList<string>)>();
        Assert.Empty(ModProblems.For("test.s3.a"));

        // A later source (the M4.7 health check) plugs in without another UI.
        using (ModProblems.AddSource(new OneProblem()))
            Assert.Equal("m47", Assert.Single(ModProblems.For("test.s3.m47")).Kind);
        Assert.Empty(ModProblems.For("test.s3.m47"));
    }

    private sealed class OneProblem : IModProblemSource
    {
        public string Name => "m47";

        public IEnumerable<ModProblem> Current()
        {
            yield return new ModProblem(ModProblemSeverity.Info, "m47", "test.s3.m47", "Built against another Core.", "");
        }
    }

    /// <summary>
    /// The status word, worst problem first: Failed to load beats Loaded with problems beats the
    /// switch, the pause and a restart; a conflict never changes the word, it adds the chip.
    /// </summary>
    [Fact]
    public void The_status_word_is_the_worst_problem_first_and_a_conflict_is_a_chip()
    {
        var critical = ModProblems.LoadFailed("m", "", retry: false);
        var warning = ModProblems.LocalizationFailed("m", "");
        var conflict = new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.KeybindConflict, "m", "F9 …", "", key: "k1");
        var conflict2 = new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.CommandConflict, "m", "cmd", "", key: "k2");

        var failed = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            Problems = new[] { critical, warning }, EnabledIntent = false, Paused = true, PageState = ModPageState.PendingLoad,
        });
        Assert.Equal(ModRunStatus.FailedToLoad, failed.Status);
        Assert.Equal(ModFrameworkLine.WordFailed, failed.StatusWord);
        Assert.Equal(LabelTone.Danger, failed.StatusTone);

        var problems = ModFrameworkLine.Compose(new ModFrameworkFacts { Problems = new[] { warning }, EnabledIntent = false });
        Assert.Equal(ModRunStatus.LoadedWithProblems, problems.Status);
        Assert.Equal(ModFrameworkLine.WordProblems, problems.StatusWord);

        var clash = ModFrameworkLine.Compose(new ModFrameworkFacts { Problems = new[] { conflict, conflict2 }, Version = "1.0.0" });
        Assert.Equal(ModRunStatus.Enabled, clash.Status);
        Assert.Equal("2 conflicts", clash.ConflictChip);
        Assert.Equal("On · Enabled · 2 conflicts · v1.0.0", clash.Text);
        var chip = clash.BuildRow("page-framework", 1200f).Find(ModFrameworkLine.CellConflicts)!;
        Assert.Equal("2 conflicts", chip.Text);
        Assert.Equal(CellTone.Warn, chip.Tone);
        Assert.Equal("1 conflict", ModProblems.ConflictChip(1));
        Assert.Equal("", ModProblems.ConflictChip(0));
    }

    /// <summary>
    /// The problems line: under the bar, at most three sentences, then Retry (a hot-loadable mod that
    /// failed) and Show all; Show all opens the notice with every problem and, where there is no
    /// clipboard (this runtime), the report text and a sentence saying so.
    /// </summary>
    [Fact]
    public void The_problems_line_shows_three_and_Show_all_opens_the_notice_with_the_report()
    {
        _backend.Add("Broken.dll", new[] { "test.s3.broken" });
        ModRegistry.NoteLoading("test.s3.broken", "Broken", "2.0.0", null, null);
        ModRegistry.NoteFailed("test.s3.broken");
        ModProblems.Report(ModProblems.LoadFailed("test.s3.broken", "System.Exception: boom at OnLoad", retry: true));
        ModProblems.Report(ModProblems.LocalizationFailed("test.s3.broken", "loc"));
        ModProblems.Report(ModProblems.ContentFailed("test.s3.broken", "content"));
        ModProblems.Report(new ModProblem(ModProblemSeverity.Info, "note", "test.s3.broken", "Fourth.", ""));
        using var reg = ModsMenu.Register(_owner, "test.s3.broken", "Broken");
        ModsMenu.Open();
        ModsMenu.OpenMod("test.s3.broken");

        using (var page = Widgets("content"))
        {
            var status = Extra(page, ModPagePane.RowFramework)!.Value;
            Assert.Equal("failed", status.GetProperty("status").GetString());
            var lines = page.RootElement.EnumerateArray().Count(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(ModPagePane.RowProblem, out _));
            Assert.Equal(3, lines);
            var summary = Extra(page, ModPagePane.RowProblems)!.Value;
            Assert.Equal(4, summary.GetProperty("count").GetInt32());
            Assert.Equal(3, summary.GetProperty("shown").GetInt32());
            Assert.True(summary.GetProperty("showAll").GetBoolean());
            Assert.True(summary.GetProperty("retry").GetBoolean());
            var first = Extra(page, ModPagePane.RowProblem)!.Value;
            Assert.Equal("critical", first.GetProperty("severity").GetString());
        }

        ModsMenu.ClickVisibleWidget("content", ModPagePane.ProblemsKey + "-2/" + ModPagePane.CellShowAll, null, null);
        var dialog = UiDialog.Current;
        Assert.NotNull(dialog);
        Assert.Equal("problems", dialog!.Kind);
        Assert.Equal(ModProblems.NoticeTitlePrefix + "Broken", dialog.Content.Title);
        Assert.Contains(ModProblems.LoadFailedSentence + " " + ModProblems.LoadFailedRetry, dialog.Content.Lines);
        Assert.Contains(ModProblems.NotCopiedNote, dialog.Content.Lines);
        Assert.Contains(dialog.Content.Lines, l => l.Contains("System.Exception: boom at OnLoad"));
        Assert.Contains("[critical] load-failed", ModsMenu.LastProblemReport);

        // Retry: unload and load again (the next load clears the load-time problems itself).
        UiDialog.ForgetForTests();
        ModsMenu.ClickVisibleWidget("content", ModPagePane.ProblemsKey + "-2/" + ModPagePane.CellRetry, null, null);
        Assert.Contains("suspend:test.s3.broken", _backend.Calls);
        Assert.Contains("resume:test.s3.broken", _backend.Calls);
        Assert.Equal(ModSwitch.RetryStarted, ModsMenu.ModsActionNote);
    }

    /// <summary>
    /// Live 2026-09-29 (3840x1080, the Sample's one conflict): the problem line was centered, orange,
    /// at body size, and Show all was a row of its own before the title. Now: left-aligned lines at
    /// the help size, warn / danger tones, Show all (and Retry) at the right end of the last line, no
    /// extra row, and no Show all for one fully shown problem with nothing more to read.
    /// </summary>
    [Fact]
    public void Problem_lines_are_left_aligned_help_size_with_Show_all_at_the_end_of_the_last_line()
    {
        var conflict = new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.KeybindConflict, "m",
            ModProblems.KeySentence("F8", "Companion"), ModProblems.KeyWhatToDo, "the log line restating it");
        var oneLines = ModProblems.PageLines(new[] { conflict }, out bool oneShowAll);
        Assert.Single(oneLines);
        Assert.False(oneShowAll, "one conflict, fully shown, has nothing more to show");
        var oneRows = ModPagePane.ProblemRowSpecs(new[] { conflict }, oneLines, oneShowAll, retry: false, 1200f);
        var only = Assert.Single(oneRows);
        var text = only.Find(ModPagePane.CellProblemText)!;
        Assert.Equal(CellAlign.Left, text.Align);
        Assert.Equal(CellKind.Text, text.Kind);
        Assert.Equal(ModSettingsGrid.HelpFontSize, text.FontSize);
        Assert.Equal(CellTone.Warn, text.Tone);
        Assert.Null(only.Find(ModPagePane.CellShowAll));
        Assert.Null(only.Find(ModPagePane.CellProblemMarker));
        Assert.Equal(0f, CellLayout.Resolve(1200f, only.Cells, only.RightInset)[0].Left + 600f, 1);

        var failed = ModProblems.LoadFailed("m", "System.Exception: boom", retry: true);
        var twoLines = ModProblems.PageLines(new[] { failed, conflict }, out bool twoShowAll);
        Assert.True(twoShowAll, "an exception is more to read");
        var rows = ModPagePane.ProblemRowSpecs(new[] { failed, conflict }, twoLines, twoShowAll, retry: true, 1200f);
        Assert.Equal(2, rows.Count);
        Assert.Equal(CellTone.Danger, rows[0].Find(ModPagePane.CellProblemText)!.Tone);
        Assert.Null(rows[0].Find(ModPagePane.CellShowAll));
        var showAll = rows[1].Find(ModPagePane.CellShowAll)!;
        Assert.Equal(CellAlign.Right, showAll.Align);
        Assert.Equal(CellKind.Button, showAll.Kind);
        Assert.NotNull(rows[1].Find(ModPagePane.CellRetry));
        var placed = CellLayout.Resolve(1200f, rows[1].Cells, rows[1].RightInset);
        var textPlace = placed[0];
        var buttonPlace = placed.Single(p => p.Key == ModPagePane.CellShowAll);
        Assert.True(buttonPlace.Left >= textPlace.Right, "Show all sits after the sentence, on the same line");
        Assert.True(buttonPlace.Right <= 600f - rows[1].RightInset + 0.5f);

        // On the page: the lines follow the status bar, the last one carries the summary, and no
        // separate actions row exists.
        ModRegistry.NoteLoading("test.s3.one", "One", "1.0.0", null, null);
        ModRegistry.NoteLoaded("test.s3.one");
        using var reg = ModsMenu.Register(_owner, "test.s3.one", "One");
        KeybindConflictSource.NameOf = id => id == "test.s3.two" ? "Two" : null;
        // KEYS1: Two was installed first, so One's key is the one that is not active (One's problem).
        KeybindArbiter.InstallTimeOf = id => new ModInstallTime(id == "test.s3.two" ? Now.AddDays(-1) : Now, KeybindArbiter.InstallLedger);
        using var a = Keybinds.Register(_owner, "test.s3.one", "Go", File("one"), new KeyChord(UiKey.F8), "", () => { });
        using var b = Keybinds.Register(new object(), "test.s3.two", "Go", File("two"), new KeyChord(UiKey.F8), "", () => { });
        ModsMenu.Open();
        ModsMenu.OpenMod("test.s3.one");
        Assert.NotNull(_runtime.CellRow(ModPagePane.ProblemsKey + "-0"));
        Assert.Null(_runtime.CellRow(ModPagePane.ProblemsKey + "-actions"));
        using var page = Widgets("content");
        var summary = Extra(page, ModPagePane.RowProblems)!.Value;
        Assert.False(summary.GetProperty("showAll").GetBoolean());
        Assert.Equal(1, summary.GetProperty("count").GetInt32());
    }

    /// <summary>A key row in conflict is marked "Also used by &lt;mod&gt;" on its help line and in the readback.</summary>
    [Fact]
    public void A_key_row_in_conflict_is_marked_also_used_by_the_other_mod()
    {
        KeybindConflictSource.NameOf = id => id == "test.s3.other" ? "ClanResourceHUD" : null;
        using var mine = Keybinds.Register(_owner, "test.s3.mine", "Open", File("mine"), new KeyChord(UiKey.F9), "Open the thing.", () => { });
        using var theirs = Keybinds.Register(new object(), "test.s3.other", "Treasury", File("other"), new KeyChord(UiKey.F9), "", () => { });
        Assert.Equal("ClanResourceHUD", ModProblems.KeyConflictOwner("test.s3.mine", "Open"));
        var row = SettingsCatalog.Rows().FirstOrDefault(r => r.PluginId == "test.s3.mine" && r.Kind == SettingsEditorKind.Keybind);
        Assert.NotNull(row);
        Assert.Equal("ClanResourceHUD", ModSettingsLayout.KeyConflictOf(row!));
        string help = ModSettingsLayout.HelpText(row!, null, ModSettingsLayout.ControlFor(row!), null, null);
        // KEYS1: this row keeps F9 (first by id with no install times), so it says who lost it.
        Assert.Contains("Also bound by ClanResourceHUD (not active there).", help);
        Assert.Equal(true, ModSettingsView.Describe(row!, null, ModSettingsLayout.ControlFor(row!))["active"]);
        string theirs2 = ModProblems.KeyConflictLine("test.s3.other", "Treasury");
        Assert.StartsWith("Not active: F9 is used by test.s3.mine", theirs2, StringComparison.Ordinal);
        Assert.Equal("ClanResourceHUD", ModSettingsView.Describe(row!, null, ModSettingsLayout.ControlFor(row!))["alsoUsedBy"]);
    }

    // ---- C. counterparts ------------------------------------------------------------------------

    /// <summary>
    /// A client mod that declares it runs partly without its server part is not paused for a server
    /// that lacks it (its handshake row is NoNeedForSync), its needs-server-part rows are grayed with
    /// "not on this server", and the status bar says "Server part not on this server · limited".
    /// </summary>
    [Fact]
    public void A_counterpart_mod_that_runs_limited_is_not_paused_and_says_limited()
    {
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync,
            CounterpartPolicy.EffectiveLevel(NetworkCompatibilityLevel.ServerMustHave, CounterpartNeed.Limited));
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync,
            CounterpartPolicy.EffectiveLevel(NetworkCompatibilityLevel.ServerMustHave, CounterpartNeed.Optional));
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave,
            CounterpartPolicy.EffectiveLevel(NetworkCompatibilityLevel.ServerMustHave, CounterpartNeed.Required));
        Assert.Equal(NetworkCompatibilityLevel.ClientOnly,
            CounterpartPolicy.EffectiveLevel(NetworkCompatibilityLevel.ClientOnly, CounterpartNeed.Limited));

        // The server does not run it (it asks about nothing of ours).
        var asked = new[] { new Handshake.ModRecord("server.only", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, true) };
        var limited = new[] { new Handshake.ModRecord("test.s3.limited", "1.0",
            CounterpartPolicy.EffectiveLevel(NetworkCompatibilityLevel.ServerMustHave, CounterpartNeed.Limited), NetworkVersionRule.Exact, true) };
        var required = new[] { new Handshake.ModRecord("test.s3.limited", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        (IReadOnlyList<string>, string) Closure(IReadOnlyList<string> g) => (g, "");
        var plan = ServerModPausePolicy.PlanPause(asked, false, limited, null, new[] { "test.s3.limited" }, Closure, autoPause: true);
        Assert.Empty(plan.Pause);
        var hard = ServerModPausePolicy.PlanPause(asked, false, required, null, new[] { "test.s3.limited" }, Closure, autoPause: true);
        Assert.Equal(new[] { "test.s3.limited" }, hard.Pause);

        // A declared counterpart makes the mod two-sided, and a known absence reads "limited".
        Assert.Equal(ModSides.Both, ModFrameworkLine.DecideSides(PackageSide.Unknown, false, NetworkCompatibilityLevel.NoNeedForSync, hasCounterpart: true));
        Assert.Equal(ServerPartState.NotOnServer, ModFrameworkLine.DecideServerPart(ModSides.Both, false, false, knownAbsent: true));
        var line = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            Sides = ModSides.Both, ServerPart = ServerPartState.NotOnServer, Limited = true,
        });
        Assert.Equal(ModRunStatus.Enabled, line.Status);
        Assert.Equal("Server part not on this server · limited", line.ServerPartText);

        // Its needs-server-part rows are grayed with the "not on this server" reason and their fallback.
        var part = ModSettingsLayout.ServerPartForSettings(paused: false, serverAskedAboutIt: false, knownAbsent: true);
        Assert.Equal(ServerPartState.NotOnServer, part);
        Assert.Equal(ModSettingsLayout.ServerPartMissingReason, ModSettingsLayout.ServerPartReason(part));

        // The declaration is plain data the page reads.
        var decl = CounterpartDeclaration.From(new CounterpartAttribute(CounterpartNeed.Limited) { Features = new[] { "Clan markers", " " } })!;
        Assert.True(decl.RunsWithout);
        Assert.Equal(new[] { "Clan markers" }, decl.Features);
        Assert.Null(CounterpartDeclaration.From(null));
    }

    // ---- D. the list ----------------------------------------------------------------------------

    /// <summary>Every state maps to its diamond (and the diamond is never the only signal).</summary>
    [Fact]
    public void Every_state_maps_to_its_diamond()
    {
        ModDiamond D(ModFrameworkFacts f) => ModsListLayout.DiamondOf(ModFrameworkLine.Compose(f), f.Problems);
        Assert.Equal(ModDiamond.Running, D(new ModFrameworkFacts()));
        Assert.Equal(ModDiamond.Running, D(new ModFrameworkFacts { PageState = ModPageState.PendingUninstall }));
        Assert.Equal(ModDiamond.Warning, D(new ModFrameworkFacts { Problems = new[] { ModProblems.LocalizationFailed("m", "") } }));
        Assert.Equal(ModDiamond.Warning, D(new ModFrameworkFacts
        {
            Problems = new[] { new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.KeybindConflict, "m", "x", "") },
        }));
        Assert.Equal(ModDiamond.Failed, D(new ModFrameworkFacts { Problems = new[] { ModProblems.LoadFailed("m", "", false) } }));
        Assert.Equal(ModDiamond.Disabled, D(new ModFrameworkFacts { EnabledIntent = false }));
        Assert.Equal(ModDiamond.Disabled, D(new ModFrameworkFacts { EnabledIntent = false, SwitchPending = ModSwitchPending.TurnsOff }));
        Assert.Equal(ModDiamond.Waiting, D(new ModFrameworkFacts { Paused = true }));
        Assert.Equal(ModDiamond.Waiting, D(new ModFrameworkFacts { Resuming = true }));
        Assert.Equal(ModDiamond.Waiting, D(new ModFrameworkFacts { PageState = ModPageState.PendingLoad }));
        Assert.Equal(ModDiamond.Waiting, D(new ModFrameworkFacts { SwitchPending = ModSwitchPending.TurnsOn }));
        Assert.Equal(ModDiamond.Waiting, D(new ModFrameworkFacts { OnClient = false }));

        var hollow = ModsListLayout.EntryRow(new ModsListEntry { Id = "x", Name = "X", Diamond = ModDiamond.Waiting }, 320f);
        Assert.Equal(ModsListLayout.DiamondHollow, hollow.Find(ModsListLayout.CellDiamond)!.Text);
        Assert.Equal(CellKind.Diamond, hollow.Find(ModsListLayout.CellDiamond)!.Kind);
        var red = ModsListLayout.EntryRow(new ModsListEntry { Id = "x", Name = "X", Diamond = ModDiamond.Failed }, 320f);
        Assert.Equal(ModsListLayout.DiamondFilled, red.Find(ModsListLayout.CellDiamond)!.Text);
        Assert.Equal(CellTone.Danger, red.Find(ModsListLayout.CellDiamond)!.Tone);
        Assert.Equal(MenuPalette.Red, ModsListLayout.DiamondColor(CellTone.Danger));
        Assert.Equal(MenuPalette.Ok, ModsListLayout.DiamondColor(ModsListLayout.ToneOf(ModDiamond.Running)));
        Assert.Equal(MenuPalette.Warn, ModsListLayout.DiamondColor(ModsListLayout.ToneOf(ModDiamond.Warning)));
        Assert.Equal(MenuPalette.Muted, ModsListLayout.DiamondColor(ModsListLayout.ToneOf(ModDiamond.Disabled)));
    }

    /// <summary>
    /// The name is left-aligned over the second line: version · sides, or the problem or state in
    /// words, which is exactly the page's status word.
    /// </summary>
    [Fact]
    public void The_entry_is_left_aligned_and_its_second_line_states_the_state_in_words()
    {
        ModsListEntry E(ModFrameworkFacts f, string version = "1.0.19", ModSides sides = ModSides.Client, bool update = false) =>
            ModsListLayout.Decide(new ModsListEntryFacts
            {
                Id = "m", Name = "Minimap", Version = version, Sides = sides, Line = ModFrameworkLine.Compose(f),
                Problems = f.Problems, UpdateAvailable = update,
            });

        Assert.Equal("1.0.19 · Client", E(new ModFrameworkFacts()).SecondLine);
        Assert.Equal("1.0.19", E(new ModFrameworkFacts(), sides: ModSides.Unknown).SecondLine);
        Assert.Equal(ModMenuInventory.VersionUnknown + " · Client", E(new ModFrameworkFacts(), version: "").SecondLine);
        Assert.Equal(ModProblems.LoadFailedListLine, E(new ModFrameworkFacts { Problems = new[] { ModProblems.LoadFailed("m", "", true) } }).SecondLine);
        Assert.Equal(ModFrameworkLine.WordProblems, E(new ModFrameworkFacts { Problems = new[] { ModProblems.ContentFailed("m", "") } }).SecondLine);
        var keyClash = new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.KeybindConflict, "m",
            ModProblems.KeySentence("F9", "ClanResourceHUD"), "", listLine: ModProblems.KeyListLine("F9", "ClanResourceHUD"));
        Assert.Equal("Key F9 not active: used by ClanResourceHUD", E(new ModFrameworkFacts { Problems = new[] { keyClash } }).SecondLine);
        Assert.Equal(ModMenuInventory.StatusPaused, E(new ModFrameworkFacts { Paused = true }).SecondLine);
        Assert.Equal(ModFrameworkLine.WordDisabled, E(new ModFrameworkFacts { EnabledIntent = false }).SecondLine);
        Assert.Equal(ModSwitchWords.TurnsOffAtNextStart,
            E(new ModFrameworkFacts { EnabledIntent = false, SwitchPending = ModSwitchPending.TurnsOff }).SecondLine);

        // The same word as the page: both come from ModFrameworkLine.
        var facts = new ModFrameworkFacts { PageState = ModPageState.PendingLoad };
        Assert.Equal(ModFrameworkLine.Compose(facts).StatusWord, E(facts).SecondLine);

        var entry = E(new ModFrameworkFacts(), update: true);
        var row = ModsListLayout.EntryRow(entry, 320f);
        var name = row.Find(ModsListLayout.CellName)!;
        Assert.Equal(CellAlign.Left, name.Align);
        Assert.Equal(CellWrap.TwoLine, name.Wrap);
        Assert.Equal("Minimap\n1.0.19 · Client", name.Text);
        Assert.Equal(ModsListLayout.UpdateTag, row.Find(ModsListLayout.CellUpdate)!.Text);
        Assert.Equal(CellAlign.Right, row.Find(ModsListLayout.CellUpdate)!.Align);
        Assert.Null(ModsListLayout.EntryRow(E(new ModFrameworkFacts()), 320f).Find(ModsListLayout.CellUpdate));
        Assert.Equal(CellKind.Diamond, row.Cells[0].Kind);
    }

    /// <summary>The summary counts and the three filters.</summary>
    [Fact]
    public void The_summary_counts_and_the_filters_pick_the_right_entries()
    {
        Assert.Equal("9 mods · 2 need attention", ModsListLayout.SummaryText(9, 2));
        Assert.Equal("1 mod · 1 needs attention", ModsListLayout.SummaryText(1, 1));
        Assert.Equal("3 mods", ModsListLayout.SummaryText(3, 0));

        var fine = new ModsListEntry { Id = "a" };
        var broken = new ModsListEntry { Id = "b", NeedsAttention = true };
        var off = new ModsListEntry { Id = "c", Disabled = true };
        var all = new[] { fine, broken, off };
        Assert.Equal(3, all.Count(e => ModsListLayout.Passes(e, ModsListFilter.All)));
        Assert.Equal(new[] { broken }, all.Where(e => ModsListLayout.Passes(e, ModsListFilter.NeedsAttention)));
        Assert.Equal(new[] { off }, all.Where(e => ModsListLayout.Passes(e, ModsListFilter.Disabled)));

        var chips = ModsListLayout.FilterRow(ModsListFilter.NeedsAttention);
        Assert.Equal(CellTone.Gold, chips.Find(ModsListLayout.ChipKey(ModsListFilter.NeedsAttention))!.Tone);
        Assert.Equal(CellTone.Muted, chips.Find(ModsListLayout.ChipKey(ModsListFilter.All))!.Tone);
        Assert.True(chips.Plain);
        Assert.True(ModsListLayout.TryParseFilter("attention", out var parsed));
        Assert.Equal(ModsListFilter.NeedsAttention, parsed);
    }

    /// <summary>
    /// The list in the workspace: the summary and chips on top, the Installed heading, the entries
    /// with their diamonds and second lines; a chip filters; the Required heading collapses and
    /// expands; the selected entry has the accent bar and the soft fill.
    /// </summary>
    [Fact]
    public void The_list_draws_summary_chips_groups_entries_and_the_selected_style()
    {
        Known("test.s3.fine", "Fine");
        Known("test.s3.broken", "Broken");
        ModProblems.Report(ModProblems.LoadFailed("test.s3.broken", "", false));
        ModEnableState.Set("test.s3.off", "Off", false, null, Now, out _);
        using var fine = ModsMenu.Register(_owner, "test.s3.fine", "Fine");
        using var broken = ModsMenu.Register(_owner, "test.s3.broken", "Broken");
        using var offMod = ModsMenu.Register(_owner, "test.s3.off", "Off");
        // The group's remembered state is process-wide; start from the default (collapsed).
        ModsMenu.SetRequiredGroupExpandedForTests(false);
        ModsMenu.Open();
        ModsMenu.OpenMod("test.s3.fine");

        using (var list = Widgets("list"))
        {
            var summary = Extra(list, "modsListSummary")!.Value;
            Assert.Equal(3, summary.GetProperty("mods").GetInt32());
            Assert.Equal(1, summary.GetProperty("attention").GetInt32());
            Assert.Equal(ModsListLayout.SummaryText(3, 1), summary.GetProperty("text").GetString());
            Assert.Equal("all", Extra(list, "modsListFilter")!.Value.GetProperty("filter").GetString());
            var installed = Extra(list, "modsListGroup", g => g.GetProperty("group").GetString() == "installed")!.Value;
            Assert.False(installed.GetProperty("collapsible").GetBoolean());
            Assert.Equal(3, installed.GetProperty("count").GetInt32());
            var required = Extra(list, "modsListGroup", g => g.GetProperty("group").GetString() == "required")!.Value;
            Assert.True(required.GetProperty("collapsible").GetBoolean());
            Assert.False(required.GetProperty("expanded").GetBoolean());

            var selected = Extra(list, "modsListEntry", e => e.GetProperty("id").GetString() == "test.s3.fine")!.Value;
            Assert.True(selected.GetProperty("selected").GetBoolean());
            Assert.Equal("running", selected.GetProperty("diamond").GetString());
            Assert.Equal("left", selected.GetProperty("align").GetString());
            var red = Extra(list, "modsListEntry", e => e.GetProperty("id").GetString() == "test.s3.broken")!.Value;
            Assert.Equal("failed", red.GetProperty("diamond").GetString());
            Assert.Equal(ModProblems.LoadFailedListLine, red.GetProperty("secondLine").GetString());
            var gray = Extra(list, "modsListEntry", e => e.GetProperty("id").GetString() == "test.s3.off")!.Value;
            Assert.Equal("disabled", gray.GetProperty("diamond").GetString());
            Assert.Equal(ModFrameworkLine.WordDisabled, gray.GetProperty("secondLine").GetString());
        }

        // The selected entry's row: the soft style, selected (accent bar + soft fill), no black hover.
        var selectedRow = _runtime.CellRow(ModsListLayout.RowKey("test.s3.fine"))!;
        Assert.True(selectedRow.Selected);
        Assert.False(_runtime.CellRow(ModsListLayout.RowKey("test.s3.broken"))!.Selected);
        Assert.Equal(MenuPalette.Gold.WithAlpha(ModsListLayout.SelectedAlpha), ModsListLayout.RowFill(true));
        Assert.Equal(MenuPalette.RowFill, ModsListLayout.RowFill(false));
        Assert.Equal(MenuPalette.Text.WithAlpha(ModsListLayout.HoverAlpha), ModsListLayout.RowHover(false));
        Assert.True(ModsListLayout.EntryRow(new ModsListEntry { Id = "x" }, 320f).Soft);

        // A chip filters the entries.
        ModsMenu.ClickVisibleWidget("list", ModsListLayout.FilterKey + "/" + ModsListLayout.ChipKey(ModsListFilter.Disabled), null, null);
        Assert.Equal(ModsListFilter.Disabled, ModsMenu.ListFilter);
        using (var list = Widgets("list"))
        {
            var ids = list.RootElement.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("modsListEntry", out _))
                .Select(r => r.GetProperty("modsListEntry").GetProperty("id").GetString())
                .ToList();
            Assert.Equal(new[] { "test.s3.off" }, ids);
        }

        ModsMenu.OnListFilter(ModsListFilter.All);

        // The Required heading expands and collapses in place.
        ModsMenu.ClickVisibleWidget("list", ModsListLayout.GroupKey(ModsListGroup.Required), null, null);
        using (var list = Widgets("list"))
            Assert.True(Extra(list, "modsListGroup", g => g.GetProperty("group").GetString() == "required")!.Value.GetProperty("expanded").GetBoolean());
        ModsMenu.ClickVisibleWidget("list", ModsListLayout.GroupKey(ModsListGroup.Required), null, null);
        using (var list = Widgets("list"))
            Assert.False(Extra(list, "modsListGroup", g => g.GetProperty("group").GetString() == "required")!.Value.GetProperty("expanded").GetBoolean());

        // The heading rows: collapsible ones carry the expand marker and take the click.
        var group = ModsListLayout.GroupRow(ModsListGroup.Required, 10, collapsible: true);
        Assert.NotNull(group.Find(ModsListLayout.CellGroupChevron));
        Assert.False(group.Plain);
        Assert.True(ModsListLayout.GroupRow(ModsListGroup.Installed, 9, collapsible: false).Plain);
        Assert.Equal(ModsListLayout.GroupRequired, group.Find(ModsListLayout.CellGroupTitle)!.Text);
        Assert.Equal("10", group.Find(ModsListLayout.CellGroupCount)!.Text);
    }

    // ---- live check of e7523542 -----------------------------------------------------------------

    /// <summary>
    /// A mod running limited says so on the status bar, even when its sides read as client only, and
    /// the words are never the cell dropped for width. What this would catch: readback limited:true
    /// with an empty bar.
    /// </summary>
    [Fact]
    public void A_limited_mod_says_so_on_the_status_bar()
    {
        foreach (var sides in new[] { ModSides.Both, ModSides.Client })
        {
            var line = ModFrameworkLine.Compose(new ModFrameworkFacts
            {
                Sides = sides, ServerPart = ServerPartState.NotOnServer, Limited = true, Version = "1.0.0",
                SwitchMode = ModSwitchMode.Live, SwitchAvailable = true,
            });
            Assert.Equal("Server part not on this server · limited", line.ServerPartText);
            Assert.Contains("Server part not on this server · limited", line.Text, StringComparison.Ordinal);
            var cell = line.BuildRow("bar", 640f).Find(ModFrameworkLine.CellServerPart);
            Assert.NotNull(cell);
            Assert.False(cell!.Optional);
        }

        // A client-only mod that is not limited still says nothing about a server part.
        Assert.Equal("", ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            Sides = ModSides.Client, ServerPart = ServerPartState.NotOnServer,
        }).ServerPartText);
    }

    /// <summary>A settings section heading takes the whole row, so "Companion" is never cut to "Compani…".</summary>
    [Fact]
    public void A_section_heading_takes_the_full_row_width()
    {
        const float width = 600f;
        var row = ModSettingsView.SectionRow(0, "Companion");
        var placed = MenuPanelLayout.CellColumns(width, row);
        var title = Assert.Single(placed);
        Assert.True(title.Visible);
        Assert.True(title.Width >= width - row.RightInset - 1f, "heading width " + title.Width);
        Assert.True(title.Width > CellLayout.AutoWidth(row.Find(ModSettingsView.CellTitle)!) * 2f);
    }

    /// <summary>
    /// A loose mod the preloader turned off (BasinHoverTips after its restart) is not loaded: its page
    /// reads the version from the renamed DLL and says Progenitor turned it off, not "Managed elsewhere".
    /// </summary>
    [Fact]
    public void A_loose_mod_turned_off_by_the_preloader_keeps_its_version_and_says_who_turned_it_off()
    {
        var fixtures = new OrdinaryPackageFixtures();
        string root = Path.Combine(_dir, "BepInEx");
        string plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(plugins);
        string dll = Path.Combine(plugins, "PhialStore.dll");
        System.IO.File.WriteAllBytes(dll, fixtures.Plugin);
        const string id = OrdinaryPackageFixtures.PluginGuid;
        var ident = PluginFileActions.Identify(root, id, dll, Array.Empty<LoadedPluginFact>());
        Assert.True(ident.Certain && ident.Loose, ident.Why);
        ModSwitch.BepInExRoot = () => root;

        // Before anything is off, nothing is read from disk.
        Assert.Equal("", ModSwitch.DisabledVersionOf(id));
        Assert.False(ModSwitch.PlainOffOnDisk(id));

        Assert.True(ModSwitch.Set(id, "PhialStore", ModOwner.Plain, on: false, ident).Ok);
        Assert.True(PluginFileActions.TryRead(root, out var state, out _));
        var request = PluginFileActions.PendingFor(state, id)!;
        var result = PluginFileActions.Execute(root, request, Now);
        Assert.True(result.Done, result.Reason);
        Assert.True(System.IO.File.Exists(dll + PluginFileActions.DisabledSuffix));
        state.Requests.Clear();
        state.Results.Add(result);
        Assert.True(PluginFileActions.TryWrite(root, state, out _));

        Assert.Equal("1.2.3", PluginFileActions.PluginVersionIn(dll + PluginFileActions.DisabledSuffix, id));
        Assert.Equal("1.2.3", ModSwitch.DisabledVersionOf(id));
        Assert.True(ModSwitch.PlainOffOnDisk(id));

        // The status bar's version comes from the renamed DLL, not "Version unknown".
        var facts = ModsMenu.FrameworkFactsFor(
            id, null, ModPageState.Installed, PackageSide.Unknown, false, ModMenuInventory.VersionUnknown,
            null, LabelTone.Normal, settingsPendingRestart: false);
        Assert.Equal("1.2.3", facts.Version);

        // Unloaded, it cannot be identified from a loaded plugin, but the note says Progenitor manages it.
        var unloaded = PluginFileActions.Identify(root, id, "", Array.Empty<LoadedPluginFact>());
        Assert.False(unloaded.Certain);
        string note = ModsMenu.PlainNoteOf(unloaded, ModSwitch.PlainOffOnDisk(id));
        Assert.Equal(ModsMenu.PlainOffByProgenitorNote, note);
        Assert.DoesNotContain("Managed elsewhere", note, StringComparison.Ordinal);
        Assert.StartsWith("Managed elsewhere", ModsMenu.PlainNoteOf(unloaded), StringComparison.Ordinal);
    }

    // ---- live F24 (2026-10-07): a hand-installed loose DLL -----------------------------------------

    private const string LooseId = OrdinaryPackageFixtures.PluginGuid;

    /// <summary>A loose DLL in a temp <c>BepInEx/plugins</c>, with the leftovers a mod's own deploy script leaves.</summary>
    private (string Root, string Dll) MakeLooseHover(byte[] image)
    {
        string root = Path.Combine(_dir, "BepInEx");
        string plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(plugins);
        string dll = Path.Combine(plugins, "ExampleHover.dll");
        System.IO.File.WriteAllBytes(dll, image);
        foreach (var leftover in new[] { ".100.crash.bak", ".105.live.bak", ".next.105", ".next.crash" })
            System.IO.File.WriteAllText(dll + leftover, "an older build");
        ModSwitch.BepInExRoot = () => root;
        return (root, dll);
    }

    private static JsonElement SidebarEntry(JsonElement workspace, string id)
    {
        foreach (var entry in workspace.GetProperty("entries").EnumerateArray())
            if (string.Equals(entry.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase)) return entry;
        throw new Xunit.Sdk.XunitException("no sidebar entry for " + id);
    }

    private static (bool Removable, string Line) ReadbackOf(string id)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace()));
        var entry = SidebarEntry(doc.RootElement, id);
        return (entry.GetProperty("removable").GetBoolean(), entry.GetProperty("provenanceLine").GetString() ?? "");
    }

    /// <summary>Every string value the visible page widgets carry, unescaped.</summary>
    private static List<string> PageStrings()
    {
        var all = new List<string>();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: all.Add(e.GetString() ?? ""); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x); break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
            }
        }

        Walk(doc.RootElement);
        return all;
    }

    /// <summary>
    /// What this would catch (live F24): a hand-installed loose DLL with <c>*.bak</c> and <c>*.next</c>
    /// leftovers beside it read <c>removable: false</c> and "Installed by another tool — remove it with that
    /// tool." in the Mods readback while its page offered Uninstall. The readback now says what the page
    /// says: removable, with the line that Progenitor can turn it off or uninstall it.
    /// </summary>
    [Fact]
    public void A_loose_dll_with_leftovers_reads_removable_with_the_pages_own_line()
    {
        var fixtures = new OrdinaryPackageFixtures();
        var (root, dll) = MakeLooseHover(fixtures.Plugin);
        var ident = PluginFileActions.Identify(root, LooseId, dll, Array.Empty<LoadedPluginFact>());
        Assert.True(ident.Certain && ident.Loose, ident.Why);
        ModsMenu.PlainIdentityProbe = id => string.Equals(id, LooseId, StringComparison.OrdinalIgnoreCase) ? ident : null;
        using var reg = ModsMenu.Register(_owner, LooseId, "ExampleHover");
        ModsMenu.Open();

        string certain = ModsMenu.PlainNoteOf(ident);
        Assert.Equal("Installed by another tool. Progenitor can turn it off or uninstall it at the next start.", certain);
        var (removable, line) = ReadbackOf(LooseId);
        Assert.True(removable);
        Assert.Equal(certain, line);
        Assert.DoesNotContain(ModMenuInventory.OtherToolNote, line, StringComparison.Ordinal);

        ModsMenu.OpenMod(LooseId);
        Assert.Equal(ModPageCopy.Uninstall, ModsMenu.ModPage!.Framework.ActionCaption);
        Assert.Contains(PageStrings(), s => s.Contains(certain, StringComparison.Ordinal));
        Assert.DoesNotContain(PageStrings(), s => s.Contains(ModMenuInventory.OtherToolNote, StringComparison.Ordinal));
    }

    /// <summary>
    /// What this would catch: a local DLL whose ordinary-plugin actions are not offered still reading the
    /// generic other-tool line, on its page or in the readback, instead of <c>Managed elsewhere: &lt;why&gt;</c>
    /// with the identification's own reason — and an identification that could not be made at all (null)
    /// falling back to the generic line too.
    /// </summary>
    [Fact]
    public void A_local_dll_without_ordinary_actions_says_managed_elsewhere_with_the_reason()
    {
        var fixtures = new OrdinaryPackageFixtures();
        var (root, dll) = MakeLooseHover(fixtures.TwoEntryPoints);
        var ident = PluginFileActions.Identify(root, LooseId, dll, Array.Empty<LoadedPluginFact>());
        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyManyPlugins(dll), ident.Why);
        PluginIdentification? probe = ident;
        ModsMenu.PlainIdentityProbe = id => string.Equals(id, LooseId, StringComparison.OrdinalIgnoreCase) ? probe : null;
        using var reg = ModsMenu.Register(_owner, LooseId, "ExampleHover");
        ModsMenu.Open();

        string managed = ModSwitch.ManagedElsewhereReason(ident.Why);
        Assert.StartsWith("Managed elsewhere: ", managed, StringComparison.Ordinal);
        var (removable, line) = ReadbackOf(LooseId);
        Assert.False(removable);
        Assert.Equal(managed, line);

        ModsMenu.OpenMod(LooseId);
        Assert.NotEqual(ModPageCopy.Uninstall, ModsMenu.ModPage!.Framework.ActionCaption);
        Assert.Contains(PageStrings(), s => s.Contains(managed, StringComparison.Ordinal));
        Assert.DoesNotContain(PageStrings(), s => s.Contains(ModMenuInventory.OtherToolNote, StringComparison.Ordinal));

        // No identification at all: the page and the readback say the file is not known.
        probe = null;
        string unknown = ModSwitch.ManagedElsewhereReason(PluginFileActions.WhyNoLocation);
        Assert.Equal((false, unknown), ReadbackOf(LooseId));
        ModsMenu.OpenMod(LooseId);
        Assert.Contains(PageStrings(), s => s.Contains(unknown, StringComparison.Ordinal));
        Assert.DoesNotContain(PageStrings(), s => s.Contains(ModMenuInventory.OtherToolNote, StringComparison.Ordinal));
    }
}
