using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace UnityEngine
{
    /// <summary>KEYS1 test stand-in for Unity's <c>KeyCode</c> (the catalog matches the full type name).</summary>
    public enum KeyCode
    {
        None = 0,
        F7 = 288,
        F8 = 289,
        F9 = 290,
        Alpha1 = 49,
        Mouse0 = 323,
        Mouse3 = 326,
    }
}

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS1 (docs/mods-menu.md#keybinds-tab): the Keybinds tab, the "first installed" winner rule and
    /// its enforcement, ordinary mods' key settings, the remap flow, the jumps between the Keybinds tab
    /// and the Mods tab, and the keybind API additions (hold, release, simulated press). Game-free: a
    /// fake UI runtime, a fake key source and fake game bindings. Nothing here has run in the game.
    /// </summary>
    [Collection("PluginLogger")]
    public sealed partial class KeybindTabTests : IDisposable
    {
        private readonly UiTests.FakeUiRuntime _runtime = new();
        private readonly TestLogSource _log = new();
        private readonly string _dir;
        private readonly object _owner = new();
        private readonly FakeKeys _keys = new();
        private readonly List<(IRuntimeWidget First, float RowTop)> _reveals = new();
        private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

        public KeybindTabTests()
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
            Keybinds.ResetForTests();
            KeybindArbiter.ResetForTests();
            ResetArt();
            Keybinds.SetSourceForTests(_keys);
            _dir = Path.Combine(Path.GetTempPath(), "progenitor-keys1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ModEnableState.TestDirectory = _dir;
            ModsMenu.KeybindsPostForTests = step => step();
            UiReveal.Hook = FakeReveal;
        }

        public void Dispose()
        {
            UiReveal.Hook = null;
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
            Handshake.ResetTables();
            WorldEpoch.ResetForTests();
            SettingsCatalog.ResetForTests();
            ModSettingsPages.ResetForTests();
            ModRegistry.ResetForTests();
            ModProblems.ResetForTests();
            ModSwitch.ResetForTests();
            ModEnableState.TestDirectory = null;
            ModEnableState.ResetForTests();
            Keybinds.ResetForTests();
            KeybindArbiter.ResetForTests();
            ResetArt();
            NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
            Plugin.Logger = null;
            UiApi.ResetForTests();
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ---- fakes ---------------------------------------------------------------------------

        /// <summary>A key source with held keys: a press edge on the frame a key goes down, held until released.</summary>
        private sealed class FakeKeys : IKeyInputSource
        {
            private readonly HashSet<KeyChord> _edges = new();
            private readonly HashSet<UiKey> _held = new();

            public bool IsAvailable => true;

            public bool Chat { get; set; }

            public bool ChatUnknown { get; set; }

            public ChatFocus ChatFocus => ChatUnknown ? Progenitor.Ui.ChatFocus.Unknown
                : Chat ? Progenitor.Ui.ChatFocus.Focused : Progenitor.Ui.ChatFocus.NotFocused;

            public bool TextFieldFocused => false;

            public KeyChord? Next { get; set; }

            public void Down(KeyChord chord)
            {
                _edges.Add(chord);
                _held.Add(chord.Key);
            }

            public void Up(UiKey key) => _held.Remove(key);

            public bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr)
            {
                refusedAsAltGr = false;
                return _edges.Contains(chord);
            }

            public bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides)
            {
                sides = ModifierSides.None;
                chord = Next ?? KeyChord.None;
                if (Next == null) return false;
                Next = null;
                return true;
            }

            public bool IsHeld(KeyChord chord) => _held.Contains(chord.Key);

            /// <summary>HOLDMOD1: the modifier keys held now, for the capture's lone-modifier rule; null: unreadable.</summary>
            public ModifierSides? Mods { get; set; }

            /// <summary>HOLDMOD1 review: modifier press edges of this frame only (a tap shorter than a frame); cleared by <see cref="Frame"/>.</summary>
            public ModifierSides ModsPressed { get; set; }

            public bool TryReadModifiers(out ModifierSides held, out ModifierSides pressed)
            {
                held = Mods ?? ModifierSides.None;
                pressed = ModsPressed;
                return Mods != null;
            }

            /// <summary>HOLDMOD1 review: <c>Application.isFocused</c>; null: unknown.</summary>
            public bool? Focused { get; set; }

            public bool? WindowFocused => Focused;

            /// <summary>PAD1: the controller's held buttons (<see cref="KeyChord.GamepadBit"/> bits); null: no controller.</summary>
            public uint? PadHeld { get; set; }

            public bool TryReadGamepadHeld(out uint held)
            {
                held = PadHeld ?? 0;
                return PadHeld != null;
            }

            /// <summary>One frame: the pump runs and the edges of this frame are consumed.</summary>
            public void Frame()
            {
                Keybinds.Pump();
                _edges.Clear();
                ModsPressed = ModifierSides.None;
            }
        }

        private bool FakeReveal(IRuntimePanel panel, IRuntimeWidget first, IRuntimeWidget last, bool toTop,
            out float rowTop, out float viewportTop, out float viewportHeight)
        {
            // The real hook measures the heading's distance down the content and scrolls it to the top of
            // the viewport; the fake gives each revealed heading a distinct distance and "scrolls" there.
            rowTop = 100f * (_reveals.Count + 1);
            viewportTop = rowTop;
            viewportHeight = 700f;
            _reveals.Add((first, rowTop));
            return true;
        }

        private ConfigFile File(string name) => new(Path.Combine(_dir, name + ".cfg"), saveOnInit: true);

        private static KeybindClaim Game(string action, KeyChord chord, int slot = 0) =>
            new(KeybindSource.Game, KeybindArbiter.GameOwnerId, action, action, chord, slot: slot, category: GameKeybindCatalog.General);

        private static KeybindClaim Mod(string owner, string id, KeyChord chord, KeybindSource source = KeybindSource.Progenitor) =>
            new(source, owner, id, id, chord, defaultChord: chord, editable: true);

        private static void UseGame(params KeybindClaim[] claims) =>
            KeybindArbiter.GameSource = () => new GameKeybindSnapshot(true, claims, "");

        private static ModInstallTime Ledger(int daysAgo) => new(Now.AddDays(-daysAgo), KeybindArbiter.InstallLedger);

        private static ModInstallTime FileTime(int daysAgo) => new(Now.AddDays(-daysAgo), KeybindArbiter.InstallFile);

        private static readonly KeyChord F8 = new(UiKey.F8);
        private static readonly KeyChord F9 = new(UiKey.F9);
        private static readonly KeyChord F7 = new(UiKey.F7);

        private static KeybindsPane Pane() => ModsMenu.KeybindsPane ?? throw new InvalidOperationException("no Keybinds pane");

        private void OpenKeybindsTab()
        {
            if (!ModsMenu.IsOpen) ModsMenu.Open();
            ModsMenu.SelectTab("keybinds");
        }

        private static IRuntimeWidget? RuntimeOf(IUiHandle? handle) => (handle as UiHandle)?.Runtime;

        private void RegisterMod(string id, string name)
        {
            ModRegistry.NoteLoading(id, name, "1.0.0", null, null);
            ModRegistry.NoteLoaded(id);
        }

        // ---- tab order, both hosts -----------------------------------------------------------

        /// <summary>Three homes (decided 2026-10-06): Mods / Install Mods / Keybinds / Preferences / Server, on both hosts.</summary>
        [Theory]
        [InlineData("main-menu")]
        [InlineData("top-tab")]
        public void The_Keybinds_tab_is_fourth_on_both_hosts(string site)
        {
            NativeModsEntry.SetOwnedForTests(site, curtain: false, observers: 0);
            ModsMenu.Open(ModsMenu.ReasonModsUi);
            var captions = ModsMenu.LastStripRowsForTests.Select(r => r.Caption).ToArray();
            Assert.Equal(new[] { "Mods", "Install Mods", "Keybinds", "Preferences", "Server" }, captions);
            Assert.Equal(new[] { ModsTab.Mods, ModsTab.InstallMods, ModsTab.Keybinds, ModsTab.Preferences, ModsTab.Server }, ModsTabStrip.Order);

            ModsMenu.SelectTab("keybinds");
            Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
            Assert.Equal(MenuRowTone.Selected, ModsMenu.LastStripRowsForTests[2].Tone);
            Assert.True(ModsTabState.TryParseStatusName("keybinds", out var parsed) && parsed == ModsTab.Keybinds);
            Assert.Equal("keybinds", ModsTabState.StatusName(ModsTab.Keybinds));
            Assert.True(ModsMenu.TryParseWorkspaceTab("keys", out string name) && name == "keybinds");
        }

        /// <summary>
        /// Live check 2026-09-29 (blocking): the in-game strip must keep the native inner-tab look for all four
        /// tabs, and the title screen its own flat buttons. Both hosts build the same strip panel; the in-game
        /// look comes from cloning the FullscreenMenu's inner-tab entry for every strip button (and painting its
        /// native selected state); where no FullscreenMenu is up (the title screen, or the F10 shortcut in a
        /// world) the strip falls back to built buttons. Pinned so neither path is lost when the tab list changes.
        /// </summary>
        [Fact]
        public void The_in_game_strip_clones_the_native_inner_tab_for_every_tab_and_the_title_screen_walks_the_donor_ladder()
        {
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            SourcePin.AssertUncommented(runtime,
                "CloneOrCreateButton(host.InlineHostOrRowParent, text, out var caption, out var source, host.CaptionWantsLeft(text), host.IsStrip)");
            SourcePin.AssertUncommented(runtime, "() => workspaceTab ? CloneWorkspaceTab(parent) : null");
            SourcePin.AssertUncommented(runtime, "if (host.IsStrip) PaintWorkspaceTab(go, host.ResolvedTone());");
            // TABS2: the ladder's order comes from WorkspaceTabLadder; rung 1 is still the in-game inner tab.
            string clone = SourcePin.MethodBody(runtime, "private static GameObject? CloneWorkspaceTab");
            SourcePin.AssertUncommented(clone, "WorkspaceTabLadder.Next(!fullscreenOut, !templateOut, !residentOut)");
            SourcePin.AssertUncommented(clone, "TryCloneFullscreenInnerTab(parent)");
            SourcePin.AssertUncommented(clone, "WorkspaceTabDonors.UsableTemplate()");
            SourcePin.AssertUncommented(clone, "WorkspaceTabDonors.ResidentDonor()");
            string inner = SourcePin.MethodBody(runtime, "private static GameObject? TryCloneInnerTabOf");
            SourcePin.AssertUncommented(inner, "menu.InnerTabGroup?.EntryPrefab");
            string paint = SourcePin.MethodBody(runtime, "private static void PaintWorkspaceTab");
            SourcePin.AssertUncommented(paint, "native.ForceHighlight = selected;");

            // One strip, one list, one panel configuration for both hosts: nothing picks the look per tab.
            string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
            string strip = SourcePin.MethodBody(menu, "private static void BuildStrip");
            SourcePin.AssertUncommented(strip, "ConfigureStripPanel(panel)");
            SourcePin.AssertUncommented(strip, "ModsTabStrip.Build(panel, selected, SelectWorkspaceTab)");
            foreach (var site in new[] { "main-menu", "top-tab" })
            {
                NativeModsEntry.SetOwnedForTests(site, curtain: false, observers: 0);
                ModsMenu.Open(ModsMenu.ReasonModsUi);
                Assert.Equal(5, ModsMenu.LastStripRowsForTests.Length);
                Assert.All(ModsMenu.LastStripRowsForTests, r => Assert.NotEqual(MenuRowTone.Chrome, r.Tone));
                ModsMenu.Close();
            }
        }

        /// <summary>
        /// KEYS3: every row is one line with two key caps (the game's Bindings Menu); a game row has no
        /// reset or clear but keeps an empty gutter so its caps line up; the toolbar is compact and Reset
        /// all mod keys sits pinned at the bottom right.
        /// </summary>
        [Fact]
        public void Rows_are_one_line_with_two_key_caps_and_Reset_all_sits_at_the_bottom_right()
        {
            UseGame(Game("Interact", new KeyChord(UiKey.E)));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            OpenKeybindsTab();
            var live = _runtime.CellRows.Where(r => !r.Released).ToList();
            var gameRow = live.First(r => r.Cells().Any(c => c.Key == KeybindsPane.CellName && c.Text == "Interact"));
            Assert.DoesNotContain(gameRow.Cells(), c => c.Key == KeybindsPane.CellReset || c.Key == KeybindsPane.CellClear);
            Assert.Contains(gameRow.Cells(), c => c.Key == KeybindsTabLayout.CellGutter);
            Assert.DoesNotContain(live.SelectMany(r => r.Cells()), c => c.Kind == CellKind.Mono);

            var modRow = live.First(r => r.Cells().Any(c => c.Key == KeybindsPane.CellName && c.Text == "Open"));
            Assert.Contains(modRow.Cells(), c => c.Key == KeybindsPane.CellReset);
            foreach (var row in new[] { gameRow, modRow })
            {
                Assert.False(row.TwoLine);
                Assert.Equal(CellWrap.Ellipsis, row.Cells().Single(c => c.Key == KeybindsPane.CellName).Wrap);
                foreach (var key in new[] { KeybindsPane.CellKey, KeybindsPane.CellKey2 })
                {
                    var cap = row.Cells().Single(c => c.Key == key);
                    Assert.Equal(CellKind.Button, cap.Kind);
                    Assert.Equal(CellSkin.KeyCap, cap.Skin);
                    Assert.Equal(KeybindsTabLayout.KeyCapWidth, cap.Width.Value);
                }
            }
            Assert.DoesNotContain(live, r => r.Text.StartsWith(KeybindsPane.HelpRowPrefix, StringComparison.Ordinal));

            // The caps line up: the game row's gutter is as wide as the mod row's two buttons and their gap.
            var gamePlaced = MenuPanelLayout.CellColumns(1200f, gameRow.Cells());
            var modPlaced = MenuPanelLayout.CellColumns(1200f, modRow.Cells());
            float Right(CellPlacement[] p, string key) => p.Single(c => c.Key == key).Right;
            Assert.Equal(Right(modPlaced, KeybindsPane.CellKey2), Right(gamePlaced, KeybindsPane.CellKey2), 1);
            Assert.Equal(Right(modPlaced, KeybindsPane.CellKey), Right(gamePlaced, KeybindsPane.CellKey), 1);

            var toolbar = _runtime.CellRow(KeybindsPane.ToolbarKey)!;
            Assert.DoesNotContain(toolbar.Cells(), c => c.Key == KeybindsPane.CellResetAll);
            Assert.Contains(toolbar.Cells(), c => c.Key == KeybindsPane.CellJumpToMod);
            Assert.Null(_runtime.CellRow("keys-jump"));
            var footer = _runtime.CellRow(KeybindsPane.FooterKey)!;
            var reset = footer.Cells().Single(c => c.Key == KeybindsPane.CellResetAll);
            Assert.Equal(CellAlign.Right, reset.Align);
            Assert.Equal(KeybindsTabLayout.ButtonWidth(KeybindsTabLayout.ResetAllCaption), reset.Width.Value);
            var all = toolbar.Cells().Single(c => c.Key == KeybindsPane.CellAll);
            Assert.True(all.Width.Value < reset.Width.Value, "a chip is compact, Reset all mod keys is sized to its caption");
            string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindsPane.cs");
            string build = SourcePin.MethodBody(pane, "internal void Build(IUiPanel panel, float rowWidth)");
            SourcePin.AssertUncommented(build, "Ui.PinMenuRows(panel, MenuRowPin.Bottom)");
        }

        /// <summary>Live check 2026-09-29: only the loser is warn-toned; the winner is normal with a muted note.</summary>
        [Fact]
        public void Only_the_losing_row_is_warn_toned_and_the_opener_is_named_Open_Mods_menu()
        {
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            Keybinds.Register(_owner, "first.mod", "Ping", File("first"), F9, "", () => { });
            Keybinds.Register(_owner, "second.mod", "Ping", File("second"), F9, "", () => { });
            Keybinds.Register(_owner, SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, File("core"), new KeyChord(UiKey.F10), "", () => { });
            OpenKeybindsTab();
            var rows = Pane().Model!.ModRows().ToList();
            var winner = rows.Single(r => r.Claim.OwnerId == "first.mod");
            var loser = rows.Single(r => r.Claim.OwnerId == "second.mod");
            Assert.Equal("normal", winner.Tone);
            Assert.StartsWith("Also bound by second.mod (not active there).", winner.HelpLine);
            Assert.Equal("warn", loser.Tone);
            Assert.StartsWith("Not active: F9", loser.HelpLine);
            CellSpec Cell(KeybindsRowModel r, string key) => _runtime.CellRow(NameRowOf(r.Id))!
                .Cells().Single(c => c.Key == key);
            // KEYS3: a name not hovered or selected is muted (it brightens when current); a loser's is warn.
            Assert.Equal(CellTone.Muted, Cell(winner, KeybindsPane.CellName).Tone);
            Assert.Equal(CellTone.Warn, Cell(loser, KeybindsPane.CellName).Tone);
            Assert.Equal(CellTone.Normal, Cell(winner, KeybindsPane.CellKey).Tone);
            Assert.Equal(CellTone.Warn, Cell(loser, KeybindsPane.CellKey).Tone);
            Assert.Equal("Open Mods menu", rows.Single(r => r.Claim.OwnerId == SettingsPanel.PluginId).Name);
        }

        // ---- the winner rule -----------------------------------------------------------------

        [Fact]
        public void The_game_always_comes_first()
        {
            KeybindArbiter.InstallTimeOf = _ => Ledger(100);
            var picture = KeybindArbiter.Compute(new[] { Game("Interact", F8) }, new[] { Mod("a.mod", "Open", F8) }, Array.Empty<KeybindClaim>());
            var mod = picture.FindProgenitor("a.mod", "Open")!;
            Assert.False(mod.Active);
            Assert.True(mod.Enforced);
            Assert.Equal(KeybindArbiter.RuleGame, mod.Rule);
            Assert.Equal("Not active: F8 is used by the game (Interact), which always comes first. Pick another key.", mod.Reason);
            var game = picture.Find(Game("Interact", F8).Key)!;
            Assert.True(game.Active);
            Assert.Equal("Also wanted by a.mod; the game keeps it.", game.Reason);

            // An unbound game slot and a different modifier are not clashes.
            var none = KeybindArbiter.Compute(new[] { Game("Interact", KeyChord.None), Game("Chat", F8) },
                new[] { Mod("a.mod", "Open", new KeyChord(UiKey.F8, KeyModifiers.Ctrl)) }, Array.Empty<KeybindClaim>());
            Assert.True(none.FindProgenitor("a.mod", "Open")!.Active);
        }

        [Fact]
        public void Between_mods_the_one_installed_first_wins_by_ledger_then_file_time()
        {
            // Ledger time for one, file time for the other: each mod's install time is its ledger's, else its file's.
            KeybindArbiter.InstallTimeOf = id => id == "z.late" ? Ledger(1) : FileTime(5);
            var picture = KeybindArbiter.Compute(Array.Empty<KeybindClaim>(),
                new[] { Mod("z.late", "Go", F9), Mod("a.early", "Go", F9) }, Array.Empty<KeybindClaim>());
            Assert.True(picture.FindProgenitor("a.early", "Go")!.Active);
            var late = picture.FindProgenitor("z.late", "Go")!;
            Assert.False(late.Active);
            Assert.Equal(KeybindArbiter.RuleInstall, late.Rule);
            Assert.Equal("Not active: F9 is used by a.early, installed first. Pick another key.", late.Reason);
            Assert.Equal("Also bound by z.late (not active there).", picture.FindProgenitor("a.early", "Go")!.Reason);

            // The later ledger entry for "a" flips it: z was installed first now.
            KeybindArbiter.InstallTimeOf = id => id == "z.late" ? Ledger(10) : Ledger(2);
            var flipped = KeybindArbiter.Compute(Array.Empty<KeybindClaim>(),
                new[] { Mod("z.late", "Go", F9), Mod("a.early", "Go", F9) }, Array.Empty<KeybindClaim>());
            Assert.True(flipped.FindProgenitor("z.late", "Go")!.Active);
            Assert.False(flipped.FindProgenitor("a.early", "Go")!.Active);
        }

        [Fact]
        public void A_tie_falls_back_to_mod_id_and_an_unknown_time_comes_after_every_known_one()
        {
            KeybindArbiter.InstallTimeOf = _ => Ledger(3);
            var tie = KeybindArbiter.Compute(Array.Empty<KeybindClaim>(),
                new[] { Mod("b.mod", "Go", F9), Mod("a.mod", "Go", F9) }, Array.Empty<KeybindClaim>());
            var loser = tie.FindProgenitor("b.mod", "Go")!;
            Assert.False(loser.Active);
            Assert.Equal(KeybindArbiter.RuleLoadOrder, loser.Rule);
            // Review item 10: the words say what the code does (mod id order), not "BepInEx order".
            Assert.Contains("which comes first by mod id (the install times are the same or unknown)", loser.Reason);

            // Review item 1: a known time beats an unknown one, whatever the ids, and the row says why.
            KeybindArbiter.InstallTimeOf = id => id == "b.mod" ? Ledger(3) : ModInstallTime.Unknown;
            var unknown = KeybindArbiter.Compute(Array.Empty<KeybindClaim>(),
                new[] { Mod("b.mod", "Go", F9), Mod("a.mod", "Go", F9) }, Array.Empty<KeybindClaim>());
            Assert.True(unknown.FindProgenitor("b.mod", "Go")!.Active);
            var a = unknown.FindProgenitor("a.mod", "Go")!;
            Assert.False(a.Active);
            Assert.Equal(KeybindArbiter.RuleUnknownTime, a.Rule);
            Assert.Contains("this mod's install time is unknown", a.Reason);

            // Two binds of one mod on one key are not a clash.
            var same = KeybindArbiter.Compute(Array.Empty<KeybindClaim>(),
                new[] { Mod("a.mod", "One", F9), Mod("a.mod", "Two", F9) }, Array.Empty<KeybindClaim>());
            Assert.All(same.Verdicts, v => Assert.False(v.InConflict));
        }

        /// <summary>Review item 1: Precedence is a total, transitive order over every mix of known and unknown times.</summary>
        [Fact]
        public void Precedence_is_total_and_transitive_with_mixed_known_and_unknown_times()
        {
            var times = new Dictionary<string, ModInstallTime>
            {
                ["a.mod"] = ModInstallTime.Unknown,
                ["b.mod"] = Ledger(1),
                ["c.mod"] = ModInstallTime.Unknown,
                ["d.mod"] = Ledger(5),
                ["e.mod"] = Ledger(5),
            };
            KeybindArbiter.InstallTimeOf = id => times.TryGetValue(id, out var t) ? t : ModInstallTime.Unknown;
            var claims = new List<KeybindClaim>(times.Keys.Select(id => Mod(id, "Go", F9)))
            {
                Mod("progenitor", "Open", F9),
                Game("Interact", F9),
            };
            foreach (var x in claims)
            foreach (var y in claims)
            {
                int xy = Math.Sign(KeybindArbiter.Precedence(x, y, null, out _));
                int yx = Math.Sign(KeybindArbiter.Precedence(y, x, null, out _));
                Assert.Equal(-xy, yx);
                foreach (var z in claims)
                    if (xy < 0 && KeybindArbiter.Precedence(y, z, null, out _) < 0)
                        Assert.True(KeybindArbiter.Precedence(x, z, null, out _) < 0);
            }

            var sorted = claims.OrderBy(c => c, Comparer<KeybindClaim>.Create((x, y) => KeybindArbiter.Precedence(x, y, null, out _)))
                .Select(c => c.OwnerId).ToArray();
            Assert.Equal(new[] { "game", "progenitor", "d.mod", "e.mod", "b.mod", "a.mod", "c.mod" }, sorted);
        }

        /// <summary>
        /// Review item 1 (blocking): Progenitor's own keys rank right after the game, so an ordinary mod on F10
        /// whose id sorts first can never take the Mods-menu opener, even with no install times at all.
        /// </summary>
        [Fact]
        public void Framework_keys_rank_right_after_the_game_so_no_mod_takes_the_Mods_opener()
        {
            var cfg = File("aaa.mod");
            cfg.Bind("Hotkeys", "Open", new KeyboardShortcut("F10"), "");
            SettingsCatalog.Register("aaa.mod", cfg, new object(), foreign: true);
            KeybindArbiter.InstallTimeOf = id => id == "aaa.mod" ? Ledger(900) : ModInstallTime.Unknown;
            int opened = 0, dev = 0;
            Keybinds.Register(_owner, SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, File("core"), new KeyChord(UiKey.F10), "", () => opened++);
            Keybinds.Register(_owner, "progenitor.dev", "Probe", File("dev"), F7, "", () => dev++);
            Keybinds.Register(_owner, "aaa.early", "Probe", File("early"), F7, "", () => { });
            KeybindArbiter.InstallTimeOf = id => id.StartsWith("aaa", StringComparison.Ordinal) ? Ledger(900) : ModInstallTime.Unknown;

            _keys.Down(new KeyChord(UiKey.F10));
            _keys.Frame();
            Assert.Equal(1, opened);
            var picture = KeybindArbiter.Current();
            var cfgVerdict = picture.Verdicts.Single(v => v.Claim.OwnerId == "aaa.mod");
            Assert.False(cfgVerdict.Active);
            Assert.Contains("part of Progenitor and comes right after the game", cfgVerdict.Reason);
            _keys.Down(F7);
            _keys.Frame();
            Assert.Equal(1, dev);
            Assert.False(picture.FindProgenitor("aaa.early", "Probe")!.Active);

            // Only the game outranks them.
            UseGame(Game("Interact", new KeyChord(UiKey.F10)));
            Assert.False(KeybindArbiter.Current().FindProgenitor(SettingsPanel.PluginId, SettingsPanel.OpenKeybindId)!.Active);
        }

        /// <summary>Review item 1: a hot-loaded plugin's DLL is found through its keybind owner when the chainloader does not list it.</summary>
        [Fact]
        public void The_plugin_file_is_asked_of_each_source_in_order_and_the_owner_of_its_binds_is_known()
        {
            var calls = new List<string>();
            string? Chain(string id) { calls.Add("chain"); return null; }
            string? Owner(string id) { calls.Add("owner"); return @"C:\game\BepInEx\progenitor-reload\Hot.dll"; }
            string? Scan(string id) { calls.Add("scan"); return "never"; }
            Assert.Equal(@"C:\game\BepInEx\progenitor-reload\Hot.dll", KeybindPluginFiles.First("hot.mod", Chain, Owner, Scan));
            Assert.Equal(new[] { "chain", "owner" }, calls);
            Assert.Null(KeybindPluginFiles.First("x", _ => null, _ => throw new InvalidOperationException()));

            var owner = new object();
            Keybinds.Register(owner, "hot.mod", "Go", File("hot"), F9, "", () => { });
            Assert.Same(owner, Keybinds.OwnerOf("HOT.MOD"));
            Assert.Null(Keybinds.OwnerOf("nobody"));
        }

        /// <summary>Review item 2: mode-only game keys and EnforceGameWins off are warnings, never a stop.</summary>
        [Fact]
        public void Mode_only_game_keys_warn_but_never_stop_a_mod_key_and_the_switch_turns_game_wins_off()
        {
            Assert.Equal(GameKeybindCatalog.WhileBuilding, GameKeybindCatalog.ModeOf("Building_Place", F8));
            Assert.Equal(GameKeybindCatalog.InEmoteMenu, GameKeybindCatalog.ModeOf("Emotes_3", F8));
            Assert.Equal(GameKeybindCatalog.WhileChoosingForm, GameKeybindCatalog.ModeOf("Shapeshift_12", F8));
            Assert.Equal(GameKeybindCatalog.AsModifier, GameKeybindCatalog.ModeOf("HUD_Modifier", F8));
            Assert.Equal(GameKeybindCatalog.AsModifier, GameKeybindCatalog.ModeOf("Walk", new KeyChord(UiKey.LeftShift)));
            Assert.Equal("", GameKeybindCatalog.ModeOf("Interact", F8));
            var place = GameKeybindCatalog.Claim(new GameKeybindCatalog.Entry("Building_Place", GameKeybindCatalog.Building),
                "Place", 0, null, "<Keyboard>/f8", "");
            Assert.Equal(GameKeybindCatalog.WhileBuilding, place.Mode);

            int fired = 0;
            UseGame(place);
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F8, "", () => fired++);
            var v = KeybindArbiter.Current().FindProgenitor("a.mod", "Go")!;
            Assert.True(v.Active);
            Assert.True(v.InConflict);
            Assert.Equal("Also used by the game while building (Place); both fire there, and Progenitor does not stop either until that is checked in game.", v.Reason);
            Assert.Empty(ModProblems.For("a.mod"));
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(1, fired);

            UseGame(Game("Interact", F8));
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(1, fired);
            KeybindArbiter.EnforceGameWins = false;
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(2, fired);
            Assert.Contains("EnforceGameWins is off", KeybindArbiter.Current().FindProgenitor("a.mod", "Go")!.Reason);
        }

        /// <summary>Review item 3: a press only looks the bind up; the picture is rebuilt on change events, not on the press.</summary>
        [Fact]
        public void A_press_never_rebuilds_the_picture_or_reads_the_game()
        {
            int gameReads = 0, cfgReads = 0;
            KeybindArbiter.GameSource = () => { gameReads++; return new GameKeybindSnapshot(true, new[] { Game("Interact", F8) }, ""); };
            KeybindArbiter.CfgSource = () => { cfgReads++; return Array.Empty<KeybindClaim>(); };
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Go", File("b"), F8, "", () => { });
            _keys.Frame();
            int g = gameReads, c = cfgReads;
            Assert.Equal(1, g);
            for (int i = 0; i < 20; i++)
            {
                _keys.Down(i % 2 == 0 ? F9 : F8);
                _keys.Frame();
            }

            Assert.Equal(g, gameReads);
            Assert.Equal(c, cfgReads);
            Assert.NotNull(KeybindArbiter.BlockedReason("b.mod", "Go"));

            // A remap is a change event: rebuilt once, still without re-reading the game.
            Assert.True(Keybinds.Rebind("b.mod", "Go", F7));
            _keys.Frame();
            Assert.Equal(c + 1, cfgReads);
            Assert.Equal(g, gameReads);
            Assert.Null(KeybindArbiter.BlockedReason("b.mod", "Go"));

            // Opening the tab (Invalidate(game: true)) re-reads the game once.
            KeybindArbiter.Invalidate(game: true);
            _keys.Frame();
            Assert.Equal(g + 1, gameReads);
        }

        /// <summary>Review item 4: at the title screen (no chat window) the tab's capture runs, and a wait times out.</summary>
        [Fact]
        public void A_capture_from_our_panel_runs_without_a_chat_window_and_times_out()
        {
            long now = 1000;
            Keybinds.Clock = () => now;
            _keys.ChatUnknown = true;
            int opened = 0;
            Keybinds.Register(_owner, SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, File("core"), new KeyChord(UiKey.F10),
                "", () => opened++, allowWhileCaptured: true, allowWhenChatUnknown: true);

            // Nothing of ours has the input: the capture waits (the player could be typing in a game field).
            KeyCaptureResult? got = null;
            Keybinds.BeginCapture(r => got = r);
            _keys.Next = F8;
            _keys.Frame();
            Assert.Null(got);
            Keybinds.CancelCapture();

            // The Keybinds tab (a WhileShown panel) holds the input: the capture runs.
            OpenKeybindsTab();
            Assert.True(UiInputCapture.IsFullyBlocking);
            got = null;
            Keybinds.BeginCapture(r => got = r);
            _keys.Next = F8;
            _keys.Frame();
            Assert.Equal(RebindOutcome.Bound, got!.Value.Outcome);
            Assert.Equal(F8, got.Value.Chord);

            // Nobody presses anything: after 10 s the wait ends and the opener works again.
            got = null;
            Keybinds.BeginCapture(r => got = r);
            now += Keybinds.CaptureTimeoutMs + 1;
            _keys.Frame();
            Assert.Equal(RebindOutcome.Cancelled, got!.Value.Outcome);
            Assert.Equal(Keybinds.CaptureTimedOutReason, got.Value.Reason);
            Assert.False(Keybinds.Capturing);
            _keys.Down(new KeyChord(UiKey.F10));
            _keys.Frame();
            Assert.Equal(1, opened);
        }

        /// <summary>Review item 5: a bind disposed or unloaded while held runs its release handler.</summary>
        [Fact]
        public void A_bind_removed_while_held_is_released()
        {
            int released = 0;
            var handle = Keybinds.Register(_owner, "a.mod", "Charge", File("a"), F9, "", null, () => { }, () => released++);
            _keys.Down(F9);
            _keys.Frame();
            handle.Dispose();
            Assert.Equal(1, released);

            var owner = new object();
            Keybinds.Register(owner, "b.mod", "Charge", File("b"), F8, "", null, () => { }, () => released++);
            _keys.Down(F8);
            _keys.Frame();
            Keybinds.RemoveAll(owner);
            Assert.Equal(2, released);
        }

        /// <summary>Review items 6-8: writes re-check the opt-in, parse like BepInEx, keep Right* modifiers, and hold no entry.</summary>
        [Fact]
        public void Cfg_writes_recheck_the_opt_in_parse_like_BepInEx_and_keep_the_right_side()
        {
            var cfg = File("plain.mod");
            var shortcut = cfg.Bind("Hotkeys", "Open", new KeyboardShortcut("F9 RightControl"), "");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            SettingsCatalog.AllowForeignEdits = true;
            var claim = SettingsCatalog.KeyClaims(sweep: false).Single();
            Assert.Null(claim.Entry);
            Assert.True(claim.Editable);
            Assert.Equal(new KeyChord(UiKey.F9, KeyModifiers.Ctrl), claim.Chord);

            // The switch goes off after the claim was read: the write is still refused.
            SettingsCatalog.AllowForeignEdits = false;
            Assert.Equal(SettingWriteState.Refused, SettingsCatalog.WriteKey(claim, F8).State);
            SettingsCatalog.AllowForeignEdits = true;
            Assert.Equal(SettingWriteState.Accepted,
                SettingsCatalog.WriteKey(claim, new KeyChord(UiKey.F7, KeyModifiers.Ctrl | KeyModifiers.Shift)).State);
            Assert.Equal("F7 + RightControl + LeftShift", shortcut.Value.Text);

            foreach (var text in new[] { "F9,LeftControl", "F9;LeftControl", "F9|LeftControl", "F9 + LeftControl", "F9 LeftControl" })
            {
                Assert.True(LegacyKeyNames.TryParse(text, out var c), text);
                Assert.Equal(new KeyChord(UiKey.F9, KeyModifiers.Ctrl), c);
            }
        }

        /// <summary>Review item 9: a hold-only bind is not reported fired by a press; a simulated hold runs it.</summary>
        [Fact]
        public void A_simulated_press_reports_a_hold_only_bind_honestly_and_a_simulated_hold_runs_it()
        {
            int held = 0, released = 0;
            Keybinds.Register(_owner, "a.mod", "Charge", File("a"), F9, "", null, () => held++, () => released++);
            var press = Assert.Single(Keybinds.SimulatePress("a.mod", "Charge"));
            Assert.False(press.Fired);
            Assert.StartsWith("no press handler", press.Reason);
            var hold = Assert.Single(Keybinds.SimulateHold("a.mod", "Charge", 3));
            Assert.True(hold.Fired);
            Assert.Equal((3, 1), (held, released));
        }

        // ---- enforcement ---------------------------------------------------------------------

        [Fact]
        public void The_losing_Progenitor_handler_does_not_run_and_fires_again_after_a_remap()
        {
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            int first = 0, second = 0;
            Keybinds.Register(_owner, "second.mod", "Go", File("second"), F9, "", () => second++);
            Keybinds.Register(_owner, "first.mod", "Go", File("first"), F9, "", () => first++);

            _keys.Down(F9);
            _keys.Frame();
            Assert.Equal(1, first);
            Assert.Equal(0, second);

            Assert.True(Keybinds.Rebind("second.mod", "Go", F8));
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(1, second);

            // The game takes a key: the mod bind on it stops until the game lets go of it.
            UseGame(Game("Interact", F8));
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(1, second);
            UseGame(Game("Interact", new KeyChord(UiKey.E)));
            _keys.Down(F8);
            _keys.Frame();
            Assert.Equal(2, second);

            // The bridge's press dispatches through the same rule.
            UseGame(Game("Interact", F8));
            var blocked = Assert.Single(Keybinds.SimulatePress("second.mod", "Go"));
            Assert.False(blocked.Fired);
            Assert.StartsWith("not-active: Not active: F8 is used by the game (Interact)", blocked.Reason);
            var fired = Assert.Single(Keybinds.SimulatePress(F9));
            Assert.True(fired.Fired);
            Assert.Equal("first.mod", fired.PluginId);
            Assert.Equal("unknown bind", Assert.Single(Keybinds.SimulatePress("nobody", "Go")).Reason);
        }

        [Fact]
        public void A_focused_framework_field_keeps_its_own_keys_from_binds_and_lets_F_keys_through()
        {
            // TEXTUNDO1: Ctrl+Z in a Mods field is the field's undo, never also a mod's bind; F9 is not the
            // field's, so it still fires (as F10 and the mouse's Back and Forward do).
            int undo = 0, f9 = 0;
            var ctrlZ = new KeyChord(UiKey.Z, KeyModifiers.Ctrl);
            Keybinds.Register(_owner, "a.mod", "Undo", File("a"), ctrlZ, "", () => undo++);
            Keybinds.Register(_owner, "b.mod", "Go", File("b"), F9, "", () => f9++);
            try
            {
                MenuHotkeyGuard.SetFocusedField(MenuHotkeyGuard.InstallSearch);
                _keys.Down(ctrlZ);
                _keys.Frame();
                _keys.Up(UiKey.Z);
                _keys.Down(F9);
                _keys.Frame();
                _keys.Up(F9.Key);
                Assert.Equal(0, undo);
                Assert.Equal(1, f9);
                var refused = Assert.Single(Keybinds.SimulatePress("a.mod", "Undo"));
                Assert.False(refused.Fired);
                Assert.Equal(Keybinds.FieldTakesKeyReason, refused.Reason);
            }
            finally
            {
                MenuHotkeyGuard.SetFocusedField(MenuHotkeyGuard.NoField);
            }

            _keys.Down(ctrlZ);
            _keys.Frame();
            Assert.Equal(1, undo);
        }

        [Theory]
        [InlineData((int)UiKey.Z, (int)KeyModifiers.Ctrl, true)]
        [InlineData((int)UiKey.Y, (int)KeyModifiers.Ctrl, true)]
        [InlineData((int)UiKey.Z, (int)(KeyModifiers.Ctrl | KeyModifiers.Shift), true)]
        [InlineData((int)UiKey.P, (int)KeyModifiers.None, true)]
        [InlineData((int)UiKey.P, (int)KeyModifiers.Shift, true)]
        [InlineData((int)UiKey.Q, (int)(KeyModifiers.Ctrl | KeyModifiers.Alt), true)]
        [InlineData((int)UiKey.Backspace, (int)KeyModifiers.Ctrl, true)]
        [InlineData((int)UiKey.LeftArrow, (int)KeyModifiers.None, true)]
        [InlineData((int)UiKey.P, (int)KeyModifiers.Alt, false)]
        [InlineData((int)UiKey.F10, (int)KeyModifiers.None, false)]
        [InlineData((int)UiKey.F9, (int)KeyModifiers.Ctrl, false)]
        [InlineData((int)UiKey.Escape, (int)KeyModifiers.None, false)]
        public void A_framework_field_takes_printable_keys_ctrl_letters_and_editing_keys(int key, int modifiers, bool takes) =>
            Assert.Equal(takes, UiTypedKeys.FieldTakes(new KeyChord((UiKey)key, (KeyModifiers)modifiers)));

        // ---- ordinary mods' key settings -----------------------------------------------------

        [Fact]
        public void A_cfg_key_conflict_is_shown_but_never_enforced()
        {
            var cfg = File("plain.mod");
            var shortcut = cfg.Bind("Hotkeys", "OpenPanel", new KeyboardShortcut("F9 + LeftControl"), "Opens the panel.");
            var keycode = cfg.Bind("Hotkeys", "Toggle", UnityEngine.KeyCode.F7, "Toggles it.");
            cfg.Bind("Hotkeys", "Click", UnityEngine.KeyCode.Mouse0, "A mouse button.");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            KeybindArbiter.InstallTimeOf = id => id == "plain.mod" ? Ledger(1) : Ledger(20);

            int fired = 0;
            Keybinds.Register(_owner, "prog.mod", "Open", File("prog"), new KeyChord(UiKey.F9, KeyModifiers.Ctrl), "", () => fired++);
            var claims = SettingsCatalog.KeyClaims(sweep: false);
            Assert.Equal(3, claims.Count);
            var open = claims.Single(c => c.ActionId == "Hotkeys/OpenPanel");
            Assert.Equal(new KeyChord(UiKey.F9, KeyModifiers.Ctrl), open.Chord);
            Assert.False(open.Editable);
            Assert.Equal(SettingsCatalog.KeyForeignReason, open.NotEditableReason);
            Assert.True(claims.Single(c => c.ActionId == "Hotkeys/Toggle").KeyCodeOnly);
            // KEYS8: KeyCode's Mouse0 is the left button, compared like any chord.
            Assert.Equal(new KeyChord(UiKey.MouseLeft), claims.Single(c => c.ActionId == "Hotkeys/Click").Chord);

            var picture = KeybindArbiter.Current();
            var cfgVerdict = picture.Find(open.Key)!;
            Assert.False(cfgVerdict.Active);
            Assert.False(cfgVerdict.Enforced);
            Assert.Equal("F9 + Ctrl is also used by prog.mod, installed first. Both fire; Progenitor can't stop this mod.", cfgVerdict.Reason);
            Assert.True(picture.FindProgenitor("prog.mod", "Open")!.Active);
            var problem = Assert.Single(ModProblems.For("plain.mod"));
            Assert.Equal(ModProblems.CfgKeyWhatToDo, problem.WhatToDo);
            Assert.Contains(KeybindArbiter.BothFire, problem.Sentence);

            _keys.Down(new KeyChord(UiKey.F9, KeyModifiers.Ctrl));
            _keys.Frame();
            Assert.Equal(1, fired);

            // Installed the other way round, the Progenitor bind is the one stopped.
            KeybindArbiter.InstallTimeOf = id => id == "plain.mod" ? Ledger(30) : Ledger(2);
            _keys.Down(new KeyChord(UiKey.F9, KeyModifiers.Ctrl));
            _keys.Frame();
            Assert.Equal(1, fired);
            Assert.StartsWith("Not active: F9 + Ctrl is used by plain.mod, installed first.", ModProblems.KeyConflictLine("prog.mod", "Open"));

            // Writes follow the catalog's rules: another mod's key needs the opt-in, a KeyCode takes no modifiers.
            var toggle = claims.Single(c => c.ActionId == "Hotkeys/Toggle");
            Assert.Equal(SettingWriteState.Refused, SettingsCatalog.WriteKey(toggle, F8).State);
            SettingsCatalog.AllowForeignEdits = true;
            toggle = SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/Toggle");
            var refused = SettingsCatalog.WriteKey(toggle, new KeyChord(UiKey.F8, KeyModifiers.Ctrl));
            Assert.Equal(SettingWriteState.Refused, refused.State);
            Assert.Contains("without Ctrl", refused.Error);
            Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.WriteKey(toggle, F8).State);
            Assert.Equal(UnityEngine.KeyCode.F8, keycode.Value);
            var openNow = SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/OpenPanel");
            Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.WriteKey(openNow, new KeyChord(UiKey.Digit1, KeyModifiers.Shift)).State);
            Assert.Equal("Alpha1 + LeftShift", shortcut.Value.Text);
        }

        [Fact]
        public void Legacy_and_Input_System_names_become_the_same_chord()
        {
            Assert.True(LegacyKeyNames.TryParse("F9 + LeftControl", out var a));
            Assert.Equal(new KeyChord(UiKey.F9, KeyModifiers.Ctrl), a);
            Assert.True(LegacyKeyNames.TryParse("Alpha1", out var b));
            Assert.Equal(new KeyChord(UiKey.Digit1), b);
            Assert.True(LegacyKeyNames.TryParse("None", out var none) && !none.IsBound);
            Assert.True(LegacyKeyNames.TryParse("Mouse0", out var left) && left == new KeyChord(UiKey.MouseLeft));
            Assert.False(LegacyKeyNames.TryParse("Mouse3", out _)); // KEYS8 review: only Mouse0 and Mouse1
            Assert.True(LegacyKeyNames.TryFormat(new KeyChord(UiKey.Enter, KeyModifiers.Alt), false, out string text, out _));
            Assert.Equal("Return + LeftAlt", text);

            Assert.True(GameKeybindCatalog.TryChord(null, "<Keyboard>/f", out var f));
            Assert.Equal(new KeyChord(UiKey.F), f);
            Assert.True(GameKeybindCatalog.TryChord("<Keyboard>/leftCtrl", "<Keyboard>/1", out var c1));
            Assert.Equal(new KeyChord(UiKey.Digit1, KeyModifiers.Ctrl), c1);
            Assert.False(GameKeybindCatalog.TryChord(null, "<Mouse>/leftButton", out _));
            Assert.Equal("Building: rotate left", GameKeybindCatalog.FallbackName("Building_RotateLeft"));
            Assert.Contains(GameKeybindCatalog.Listed, e => e.Action == "Interact" && e.Category == GameKeybindCatalog.General);
            Assert.DoesNotContain(GameKeybindCatalog.Listed, e => e.Action.StartsWith("Menu_", StringComparison.Ordinal));
        }

        // ---- the tab: sections, rows, readback -------------------------------------------------

        [Fact]
        public void The_tab_lists_the_game_first_then_each_mod_in_list_order_with_readback()
        {
            RegisterMod("zeta.mod", "Zeta");
            RegisterMod("alpha.mod", "Alpha");
            using var zeta = ModsMenu.Register(_owner, "zeta.mod", "Zeta");
            using var alpha = ModsMenu.Register(_owner, "alpha.mod", "Alpha");
            UseGame(Game("Interact", new KeyChord(UiKey.E)), Game("Interact", new KeyChord(UiKey.F), slot: 1), Game("Chat", F8));
            Keybinds.Register(_owner, "zeta.mod", "Open", File("zeta"), F8, "Opens Zeta.", () => { });
            Keybinds.Register(_owner, "alpha.mod", "Show", File("alpha"), F9, "Shows Alpha.", () => { });
            OpenKeybindsTab();

            var tab = Pane().Describe();
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(tab));
            var sections = doc.RootElement.GetProperty("sections").EnumerateArray().ToList();
            // Requested 2026-09-30: the mods first (in list order), then the game's categories (the test
            // claims are General).
            var modTitles = sections.Take(2).Select(s => s.GetProperty("title").GetString()).ToList();
            Assert.Equal(new[] { "Alpha", "Zeta" }, modTitles);
            Assert.All(sections.Take(2), s => Assert.True(s.GetProperty("hasModPage").GetBoolean()));
            Assert.Equal("1 key", sections[0].GetProperty("countText").GetString());
            Assert.Equal("game:general", sections[2].GetProperty("id").GetString());
            Assert.Equal("General", sections[2].GetProperty("title").GetString());
            Assert.Equal("game", sections[2].GetProperty("source").GetString());

            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            var interact = rows.Single(r => r.GetProperty("action").GetString() == "Interact");
            Assert.Equal("E / F", interact.GetProperty("keyText").GetString());
            Assert.Equal("game", interact.GetProperty("source").GetString());
            Assert.Equal("readonly", interact.GetProperty("tone").GetString());
            var zetaRow = rows.Single(r => r.GetProperty("owner").GetString() == "zeta.mod");
            Assert.Equal("progenitor", zetaRow.GetProperty("source").GetString());
            Assert.False(zetaRow.GetProperty("active").GetBoolean());
            Assert.Equal("warn", zetaRow.GetProperty("tone").GetString());
            var conflict = zetaRow.GetProperty("conflict");
            Assert.Equal("game", conflict.GetProperty("winner").GetString());
            Assert.Equal("game", conflict.GetProperty("rule").GetString());
            Assert.StartsWith("Not active: F8 is used by the game (Chat)", conflict.GetProperty("reason").GetString());
            Assert.Equal("F8", zetaRow.GetProperty("chord").GetString());
            Assert.Equal("F8", zetaRow.GetProperty("default").GetString());
            var toolbar = doc.RootElement.GetProperty("toolbar");
            Assert.Equal(1, toolbar.GetProperty("filters").GetProperty("conflicts").GetInt32());
            Assert.Contains(toolbar.GetProperty("jumpTargets").EnumerateArray(), t => t.GetProperty("target").GetString() == "mod:zeta.mod");

            // The same rows answer mods_ui widget_read.
            using var widgets = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
            Assert.Contains(widgets.RootElement.EnumerateArray(), r => r.TryGetProperty(KeybindsPane.RowToolbar, out _));
            Assert.Contains(widgets.RootElement.EnumerateArray(), r => r.TryGetProperty(KeybindsPane.RowSection, out _));
            Assert.Contains(widgets.RootElement.EnumerateArray(), r => r.TryGetProperty(KeybindsPane.RowKey, out var k)
                && k.GetProperty("owner").GetString() == "alpha.mod" && k.GetProperty("active").GetBoolean());
            Assert.Contains(widgets.RootElement.EnumerateArray(), r => r.TryGetProperty(KeybindsPane.RowGameNote, out var n)
                && n.GetProperty("note").GetString() == KeybindsTabLayout.GameCapNote && n.GetProperty("shown").GetBoolean());
        }

        [Fact]
        public void Without_the_game_bindings_the_Game_section_is_absent_with_a_note()
        {
            Keybinds.Register(_owner, "alpha.mod", "Show", File("alpha"), F9, "", () => { });
            OpenKeybindsTab();
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Pane().Describe()));
            var sections = doc.RootElement.GetProperty("sections").EnumerateArray().ToList();
            Assert.DoesNotContain(sections, s => s.GetProperty("id").GetString() == "game");
            Assert.Equal(KeybindArbiter.GameUnavailableNote, doc.RootElement.GetProperty("gameNote").GetString());
            Assert.False(doc.RootElement.GetProperty("toolbar").GetProperty("gameAvailable").GetBoolean());
            using var widgets = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
            Assert.Contains(widgets.RootElement.EnumerateArray(), r => r.TryGetProperty(KeybindsPane.RowGameNote, out var n)
                && !n.GetProperty("available").GetBoolean());
        }

        [Fact]
        public void Filters_and_search_hide_rows_in_place()
        {
            KeybindArbiter.InstallTimeOf = id => id == "a.mod" ? Ledger(5) : Ledger(1);
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens the atlas.", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Zoom", File("b2"), F7, "", () => { });
            Assert.True(Keybinds.Rebind("b.mod", "Zoom", F8));
            OpenKeybindsTab();
            var pane = Pane();

            int Shown() => JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe())).RootElement
                .GetProperty("rows").EnumerateArray().Count(r => r.GetProperty("visible").GetBoolean());

            Assert.Equal(3, Shown());
            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellConflicts, out _));
            Assert.Equal(KeybindsFilter.Conflicts, pane.Filter);
            Assert.Equal(2, Shown());
            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellChanged, out _));
            Assert.Equal(1, Shown());
            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellAll, out _));
            pane.ApplySearch("atlas");
            Assert.Equal(1, Shown());
            pane.ApplySearch("F9");
            Assert.Equal(2, Shown());
            pane.ApplySearch("nothing like this");
            Assert.Equal(0, Shown());
            pane.ApplySearch("");
            Assert.Equal(3, Shown());
        }

        // ---- remapping -------------------------------------------------------------------------

        [Fact]
        public void Remap_captures_previews_the_clash_saves_resets_and_clears()
        {
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            KeybindArbiter.InstallTimeOf = id => id == "b.mod" ? Ledger(9) : Ledger(1);
            var fileA = File("a");
            Keybinds.Register(_owner, "a.mod", "Open", fileA, F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            string rowId = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod").Id;
            string nameRow = NameRowOf(rowId);

            // Click the key: a capture starts, no bind fires while it waits, and nothing is stored.
            Assert.True(UiCellRowHandle.TryInvoke(nameRow + "/" + KeybindsPane.CellKey, out _));
            Assert.True(Keybinds.Capturing);
            Assert.True(pane.Capturing);

            // Requested 2026-10-10: a key another mod has is pending, named with what loses it; nothing is stored
            // and Swap stays offered.
            _keys.Next = F8;
            _keys.Frame();
            Assert.False(pane.Capturing);
            Assert.Equal(F8, pane.Pending);
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            var edit = JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe())).RootElement.GetProperty("edit");
            Assert.StartsWith("F8 is used by b.mod (Close). Save moves it to Open; Close will have no key.",
                edit.GetProperty("status").GetString());
            Assert.Equal("b.mod", edit.GetProperty("swapWith").GetProperty("owner").GetString());
            pane.CancelEdit();
            Assert.Equal("", pane.Editing);
            Assert.Equal(F8, Keybinds.Snapshot().Single(b => b.PluginId == "b.mod").Chord);

            // Escape cancels; the key is unchanged.
            pane.BeginEdit(rowId);
            _keys.Next = new KeyChord(UiKey.Escape);
            _keys.Frame();
            Assert.Equal("", pane.Editing);

            // Save a free key.
            pane.BeginEdit(rowId);
            _keys.Next = F7;
            _keys.Frame();
            // At the title screen with no remembered game keys, the preview also says the game was not checked.
            Assert.Equal("F7 is free. Save to use it. " + KeybindRemapGuard.GameUncheckedNote, JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe()))
                .RootElement.GetProperty("edit").GetProperty("status").GetString());
            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.EditKey + "/" + KeybindsPane.CellSave, out _));
            Assert.Equal(F7, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.Equal("F7", ((ConfigEntry<string>)fileA[new ConfigDefinition(Keybinds.Section, "Open")]).Value);

            // The mod page's Keys section reads the same value (one cfg entry, one registry).
            var keysRow = SettingsCatalog.Rows().Single(r => r.PluginId == "a.mod" && r.Kind == SettingsEditorKind.Keybind);
            Assert.Equal("F7", keysRow.Read());
            Assert.True(ModSettingsLayout.IsModified(keysRow));

            // Reset (the « in the gutter) and clear (the ×).
            pane = Pane();
            var changed = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod");
            Assert.True(changed.Changed);
            Assert.True(changed.CanReset);
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(rowId) + "/" + KeybindsPane.CellReset, out _));
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(rowId) + "/" + KeybindsPane.CellClear, out _));
            Assert.False(Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord.IsBound);

            // Unbind from the edit bar, and a bridge capture.
            Keybinds.Rebind("a.mod", "Open", F9);
            pane = Pane();
            Assert.True(pane.CaptureForBridge(rowId, new KeyChord(UiKey.K, KeyModifiers.Ctrl)));
            pane.SaveEdit();
            Assert.Equal(new KeyChord(UiKey.K, KeyModifiers.Ctrl), Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
        }

        [Fact]
        public void Remapping_the_loser_clears_the_conflict_everywhere_at_once()
        {
            RegisterMod("late.mod", "Late");
            using var reg = ModsMenu.Register(_owner, "late.mod", "Late");
            KeybindArbiter.InstallTimeOf = id => id == "early.mod" ? Ledger(9) : Ledger(1);
            KeybindConflictSource.NameOf = id => id == "early.mod" ? "Early" : id == "late.mod" ? "Late" : null;
            Keybinds.Register(_owner, "early.mod", "Go", File("early"), F9, "", () => { });
            Keybinds.Register(_owner, "late.mod", "Go", File("late"), F9, "", () => { });
            Assert.StartsWith("Not active: F9 is used by Early, installed first.", ModProblems.KeyConflictLine("late.mod", "Go"));
            Assert.Single(ModProblems.For("late.mod"));
            var keysRow = SettingsCatalog.Rows().Single(r => r.PluginId == "late.mod" && r.Kind == SettingsEditorKind.Keybind);
            string help = ModSettingsLayout.HelpText(keysRow, null, ModSettingsLayout.ControlFor(keysRow), null, null);
            Assert.Contains("Not active: F9 is used by Early, installed first. Pick another key.", help);

            OpenKeybindsTab();
            var row = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == "late.mod");
            Assert.Equal(row.ConflictText, ModProblems.KeyConflictLine("late.mod", "Go"));
            Pane().CaptureForBridge(row.Id, F8);
            Pane().SaveEdit();
            Assert.Empty(ModProblems.For("late.mod"));
            Assert.Equal("", ModProblems.KeyConflictLine("early.mod", "Go"));
            Assert.Equal(0, Pane().Model!.ConflictCount);
        }

        [Fact]
        public void Reset_all_mod_keys_asks_first_and_resets_every_changed_key()
        {
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            Keybinds.Rebind("a.mod", "Open", F7);
            Keybinds.Rebind("b.mod", "Close", new KeyChord(UiKey.K));
            OpenKeybindsTab();
            Assert.Equal(2, Pane().Model!.ResettableRows().Count);

            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.FooterKey + "/" + KeybindsPane.CellResetAll, out _));
            var dialog = UiDialog.Current;
            Assert.NotNull(dialog);
            Assert.True(dialog!.IsOpen);
            Assert.Equal(KeybindsPane.ResetAllDialogTag, dialog.Tag);
            Assert.Equal(F7, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);

            // No changes nothing; Yes (the dialog's handler, as ModSettingsPageTests drives Reset all) resets.
            // The fake runtime does not draw the dialog's buttons, so the handler is called directly.
            dialog.Close();
            Assert.Equal(F7, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.Equal(2, Pane().ConfirmResetAll());
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.Equal(F8, Keybinds.Snapshot().Single(b => b.PluginId == "b.mod").Chord);
            Assert.Empty(Pane().Model!.ResettableRows());
        }

        // ---- jumps and the two-way links ---------------------------------------------------------

        [Fact]
        public void The_jumps_land_on_the_section_heading()
        {
            UseGame(Game("Interact", new KeyChord(UiKey.E)));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();

            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellModJump, out _));
            // Requested 2026-09-30: the section jumps land on their group header ("Mod keys").
            Assert.Same(RuntimeOf(pane.HeadingOf("mods")), _reveals[^1].First);
            Assert.Same(pane.GroupHeaderOf("mods"), pane.HeadingOf("mods"));
            Assert.Equal("mod:a.mod", pane.LastJump!.Section);
            Assert.True(pane.LastJump.Landed);
            Assert.Equal(_reveals[^1].RowTop, pane.LastJump.ViewportTop);

            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellGameJump, out _));
            Assert.Same(RuntimeOf(pane.HeadingOf("game")), _reveals[^1].First);

            // Jump to mod…: the selector's pick.
            Assert.True(pane.JumpTo("mod:b.mod"));
            Assert.Same(RuntimeOf(pane.HeadingOf("mod:b.mod")), _reveals[^1].First);

            // A filter hiding the section is cleared by the jump, and the bridge's op reaches it too.
            pane.SetFilter(KeybindsFilter.Changed);
            ModsMenu.KeysOp("jump", "a.mod", null);
            Assert.Equal(KeybindsFilter.All, pane.Filter);
            Assert.Same(RuntimeOf(pane.HeadingOf("mod:a.mod")), _reveals[^1].First);
            Assert.False(pane.JumpTo("mod:nobody"));
        }

        [Fact]
        public void Mods_page_and_Keybinds_tab_link_both_ways()
        {
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens the atlas.", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            ModsMenu.Open();
            ModsMenu.OpenMod("a.mod");
            var keys = ModsMenu.ModPage!.SettingsView!.KeysHeading;
            Assert.NotNull(keys);

            // Mods page -> Keybinds: the Keys section's button switches tab and scrolls to the mod's section.
            string keysRowKey = ((IUiCellRow)keys!).Key;
            Assert.True(UiCellRowHandle.TryInvoke(keysRowKey + "/" + ModSettingsView.CellOpenKeybinds, out _));
            Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
            Assert.Same(RuntimeOf(Pane().HeadingOf("mod:a.mod")), _reveals[^1].First);
            Assert.Equal("mod:a.mod", Pane().LastJump!.Section);

            // Keybinds -> Mods page: the heading (and its button) select the mod and scroll to its Keys section.
            var sectionRow = (IUiCellRow)Pane().HeadingOf("mod:a.mod")!;
            // The page glyph is plain text now: the whole heading is the link (a row click).
            Assert.True(UiCellRowHandle.TryInvoke(sectionRow.Key, out _));
            Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
            Assert.Equal("a.mod", ModsMenu.TabStateForTests!.Mods.Selection!.Value.Value);
            var keysAgain = ModsMenu.ModPage!.SettingsView!.KeysHeading;
            Assert.Same(RuntimeOf(keysAgain), _reveals[^1].First);
            var jumped = ModsMenu.ModsKeysJumpReadback!;
            Assert.Equal(true, jumped["found"]);
            Assert.Equal(true, jumped["landed"]);
            Assert.Equal("a.mod", jumped["plugin"]);

            // The name itself is clickable (a row click on the heading).
            ModsMenu.SelectTab("keybinds");
            sectionRow = (IUiCellRow)Pane().HeadingOf("mod:a.mod")!;
            Assert.True(UiCellRowHandle.TryInvoke(sectionRow.Key, out _));
            Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);

            // A mod with no Mods-list entry gets no link (every mod with keys is listed in the workspace, so
            // the pane is built here with a list that has none).
            ModsMenu.Close();
            var bare = new KeybindsPane(null, _ => false);
            var panel = UiApi.Panel("Keybinds", _owner, SettingsPanel.PluginId);
            bare.Build(panel, 1200f);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(bare.Describe()));
            var b = doc.RootElement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("owner").GetString() == "b.mod");
            Assert.False(b.GetProperty("hasModPage").GetBoolean());
            var bRow = (UiCellRowHandle)bare.HeadingOf("mod:b.mod")!;
            Assert.DoesNotContain(bRow.Cells, c => c.Key == KeybindsPane.CellOpenPage);
            bare.Release();
            panel.Dispose();
        }

        // ---- API completeness: hold and release ------------------------------------------------

        [Fact]
        public void A_hold_bind_runs_while_held_and_releases_on_key_up_or_suppression()
        {
            int pressed = 0, held = 0, released = 0;
            Keybinds.Register(_owner, "a.mod", "Charge", File("a"), F9, "", () => pressed++, () => held++, () => released++);
            _keys.Down(F9);
            _keys.Frame();
            Assert.Equal((1, 0, 0), (pressed, held, released));
            _keys.Frame();
            _keys.Frame();
            Assert.Equal((1, 2, 0), (pressed, held, released));
            _keys.Up(UiKey.F9);
            _keys.Frame();
            Assert.Equal((1, 2, 1), (pressed, held, released));

            // Chat takes focus mid-hold: released, not stuck.
            _keys.Down(F9);
            _keys.Frame();
            _keys.Chat = true;
            _keys.Frame();
            Assert.Equal((2, 2, 2), (pressed, held, released));
            _keys.Chat = false;
            _keys.Frame();
            Assert.Equal((2, 2, 2), (pressed, held, released));

            Assert.Throws<ArgumentNullException>(() =>
                Keybinds.Register(_owner, "a.mod", "Empty", File("a2"), F8, "", null, null, null));
        }

        private string NameRowOf(string rowId)
        {
            var tab = JsonDocument.Parse(JsonSerializer.Serialize(Pane().Describe()));
            return tab.RootElement.GetProperty("rows").EnumerateArray()
                .Single(r => r.GetProperty("id").GetString() == rowId).GetProperty("nameRow").GetString()!;
        }
    }
}
