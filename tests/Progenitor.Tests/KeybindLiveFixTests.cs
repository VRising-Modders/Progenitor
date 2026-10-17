using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS4 live results 2026-09-30: Give left the holder's cfg unchanged (its file did not save on set
    /// while connected), the Keybinds tab stayed stale across a mod's hot reload, and Progenitor's own
    /// keys were named by id. Game-free, on <see cref="KeybindTabTests"/>' fakes.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        /// <summary>
        /// A startup-loaded plugin's cfg (ctx.Config.File is BepInEx's own plugin.Config) has
        /// SaveOnConfigSet off while ConfigSync locks one of its synced settings on a server; the test
        /// file is put in that state.
        /// </summary>
        private ConfigFile HeldFile(string name)
        {
            var file = File(name);
            file.SaveOnConfigSet = false;
            return file;
        }

        private string DiskText(string name) => System.IO.File.ReadAllText(Path.Combine(_dir, name + ".cfg"));

        [Fact]
        public void Give_writes_None_into_the_holders_cfg_file_on_disk_even_while_the_file_does_not_save_on_set()
        {
            UseNames();
            KeybindArbiter.InstallTimeOf = id => id == SampleId ? Ledger(9) : Ledger(1);
            // Loaded at start (the cfg is written), then the join's config-sync lock turns saving on set off.
            var sample = File("sample");
            Keybinds.Register(_sampleOwner, SampleId, "ToggleLogPanel", sample, new KeyChord(UiKey.F9, KeyModifiers.Ctrl), "", () => { });
            _keys.Frame();
            sample.Save(); // BepInEx wrote the bound entry at start
            Assert.Contains("ToggleLogPanel = F9 + Ctrl", DiskText("sample"));
            sample.SaveOnConfigSet = false;
            KeybindConflictPrompt.ScreenReady = () => true;
            Keybinds.Register(_companionOwner, CompanionId, "CompanionPing", File("companion"), new KeyChord(UiKey.F9, KeyModifiers.Ctrl), "", () => { });
            _keys.Frame();

            Assert.True(KeybindConflictPrompt.Choose(KeybindConflictPrompt.ChoiceGive));

            Assert.Contains("ToggleLogPanel = None", DiskText("sample"));
            Assert.False(sample.SaveOnConfigSet); // left as ConfigSync had it
            // The next start reads it back: no clash.
            var reread = new ConfigFile(Path.Combine(_dir, "sample.cfg"), saveOnInit: false);
            reread.Reload();
            Assert.Equal("None", reread.Bind(Keybinds.Section, "ToggleLogPanel", "F9 + Ctrl").Value);
        }

        [Fact]
        public void Leave_and_a_tab_remap_also_reach_the_disk_while_the_file_does_not_save_on_set()
        {
            var companion = SampleHoldsF8();
            var held = HeldFile("companion-held");
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(held);
            _keys.Frame();
            Assert.True(KeybindConflictPrompt.Choose(KeybindConflictPrompt.ChoiceLeave));
            Assert.Contains("CompanionPing = None", DiskText("companion-held"));

            Assert.True(Keybinds.TryRebind(CompanionId, "CompanionPing", F7, Keybinds.RebindCheck.Full, out _));
            Assert.Contains("CompanionPing = F7", DiskText("companion-held"));
        }

        [Fact]
        public void The_open_Keybinds_tab_follows_a_hot_reload_in_place_and_keeps_a_capture_of_another_row()
        {
            UseNames();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F7, "Opens it.", () => { });
            var companion = File("companion");
            RegisterCompanion(companion);
            OpenKeybindsTab();
            const string companionRow = "progenitor:" + CompanionId + "/CompanionPing";
            Pane().BeginEdit("progenitor:a.mod/Open");

            // A hot reload that comes back on another key: the same rows, rewritten in place.
            Keybinds.RemoveAll(_companionOwner);
            companion.Bind(Keybinds.Section, "CompanionPing", "F8").Value = "F9";
            RegisterCompanion(companion);
            _keys.Frame();
            var row = Pane().Model!.FindRow(companionRow)!;
            Assert.Equal("F9", row.Claim.ChordText);
            Assert.Equal("progenitor:a.mod/Open", Pane().Editing);
            Assert.True(Pane().Capturing);

            // A new mod while a row is being edited: held back, so the capture is not lost …
            Keybinds.Register(_owner, "b.mod", "Go", File("b"), new KeyChord(UiKey.F11), "", () => { });
            _keys.Frame();
            Assert.Null(Pane().Model!.FindRow("progenitor:b.mod/Go"));
            Assert.True(Pane().Capturing);
            // … and drawn once the edit ends.
            Pane().CancelEdit();
            _keys.Frame();
            Assert.NotNull(Pane().Model!.FindRow("progenitor:b.mod/Go"));

            // An unbind (Leave) shows at once, with its note, without closing the tab.
            Keybinds.TryRebind(CompanionId, "CompanionPing", KeyChord.None, Keybinds.RebindCheck.Full, out _);
            _keys.Frame();
            var read = (List<object?>)Pane().Describe()["rows"]!;
            var companionRead = read.Cast<Dictionary<string, object?>>().Single(r => (string)r["id"]! == companionRow);
            Assert.Equal("None", companionRead["chord"]);
        }

        [Fact]
        public void Progenitors_own_keys_are_named_Progenitor_in_the_dialog_the_notes_the_refusals_and_the_log()
        {
            Assert.Equal("Progenitor", KeybindArbiter.DisplayNameOf(SettingsPanel.PluginId));
            Assert.Equal("Progenitor Dev", KeybindArbiter.DisplayNameOf("progenitor.dev"));
            Assert.Equal("Progenitor", KeybindConflictSource.DisplayName(SettingsPanel.PluginId));
            UseNames();
            Keybinds.Register(_owner, SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, File("core"), new KeyChord(UiKey.F10), "", () => { });
            _keys.Frame();
            KeybindConflictPrompt.ScreenReady = () => true;
            Keybinds.Register(_companionOwner, CompanionId, "CompanionPing", File("companion"), new KeyChord(UiKey.F10), "", () => { });
            _keys.Frame();

            var dialog = Shown();
            Assert.Equal("Progenitor has it for Open Mods menu (in gameplay). Progenitor's own keys come right after the game's.",
                dialog.Content.Facts[1].Value);
            Assert.DoesNotContain(dialog.Content.Options, o => o.StartsWith("Give", StringComparison.Ordinal));
            Assert.DoesNotContain(_log.Infos.Concat(_log.Warnings), l => l.Contains("used by " + SettingsPanel.PluginId));
            Assert.True(KeybindConflictPrompt.Choose(KeybindConflictPrompt.ChoiceLeave));
            Assert.Equal("Left unbound: F10 is used by Progenitor (Open Mods menu).",
                KeybindConflictPrompt.NoteOf("progenitor:" + CompanionId + "/CompanionPing"));

            Assert.False(Keybinds.TryRebind(CompanionId, "CompanionPing", new KeyChord(UiKey.F10), Keybinds.RebindCheck.Full, out string? why));
            Assert.Equal("F10 is used by Progenitor (Open Mods menu). Pick another key.", why);
            var verdict = KeybindArbiter.Compute(Array.Empty<KeybindClaim>(), new[]
            {
                new KeybindClaim(KeybindSource.Progenitor, SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, "Open Mods menu", F9),
                new KeybindClaim(KeybindSource.Progenitor, "z.mod", "Go", "Go", F9),
            }, Array.Empty<KeybindClaim>()).FindProgenitor("z.mod", "Go")!;
            Assert.StartsWith("Not active: F9 is used by Progenitor, which is part of Progenitor", verdict.Reason);
        }
    }
}
