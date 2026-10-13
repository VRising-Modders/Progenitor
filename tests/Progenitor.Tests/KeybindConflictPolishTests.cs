using System;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// Live findings 2026-10-02 on the key-conflict dialog: it named a mod without a menu registration
    /// by its raw plugin id, and it drew before the dialog art was prepared after a Core swap.
    /// Game-free, on <see cref="KeybindTabTests"/>' fakes.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        /// <summary>The Sample holds F8; the companion arrives later. No menu names, only loaded-plugin names.</summary>
        private void CompanionLosesF8WithLoadedNamesOnly()
        {
            KeybindArbiter.NameOf = null;
            KeybindConflictSource.NameOf = null;
            KeybindConflictSource.LoadedNameOf = id =>
                id == SampleId ? "Progenitor Sample" : id == CompanionId ? "Progenitor Sample Companion" : null;
            KeybindArbiter.InstallTimeOf = id => id == SampleId ? Ledger(9) : Ledger(1);
            Keybinds.Register(_sampleOwner, SampleId, "NetPing", File("sample"), F8, "Ping the server.", () => { });
            _keys.Frame();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(File("companion"));
        }

        [Fact]
        public void A_mod_without_a_menu_registration_is_named_by_its_loaded_plugin_name_in_title_and_body()
        {
            CompanionLosesF8WithLoadedNamesOnly();
            _keys.Frame();

            var dialog = Shown();
            Assert.Equal("Key conflict: Progenitor Sample Companion", dialog.Content.Title);
            Assert.Equal("Companion ping wants F8, used by Progenitor Sample (Net ping). Companion ping is not active until you choose.",
                dialog.Content.Body);
            Assert.DoesNotContain(SampleId, dialog.Content.Title + dialog.Content.Body + string.Join("|", dialog.Content.Lines));
        }

        [Fact]
        public void An_unknown_plugin_still_falls_back_to_its_id()
        {
            KeybindArbiter.NameOf = null;
            KeybindConflictSource.NameOf = null;
            KeybindConflictSource.LoadedNameOf = _ => null;
            Assert.Equal("some.unknown.mod", KeybindArbiter.DisplayNameOf("some.unknown.mod"));
        }

        [Fact]
        public void A_conflict_dialog_waits_for_the_dialog_art_and_shows_when_it_is_ready()
        {
            long now = 1000;
            bool art = false;
            KeybindConflictPrompt.NowMs = () => now;
            KeybindConflictPrompt.DialogArtReady = () => art;
            CompanionLosesF8WithLoadedNamesOnly();

            _keys.Frame();
            Assert.Null(UiDialog.Current);
            now += KeybindConflictPrompt.ArtWaitCapMs - 1;
            _keys.Frame();
            Assert.Null(UiDialog.Current);

            art = true;
            _keys.Frame();
            Assert.Equal("Key conflict: Progenitor Sample Companion", Shown().Content.Title);
        }

        [Fact]
        public void A_conflict_dialog_is_never_lost_when_the_art_never_arrives()
        {
            long now = 1000;
            KeybindConflictPrompt.NowMs = () => now;
            KeybindConflictPrompt.DialogArtReady = () => false;
            CompanionLosesF8WithLoadedNamesOnly();

            _keys.Frame();
            Assert.Null(UiDialog.Current);
            now += KeybindConflictPrompt.ArtWaitCapMs;
            _keys.Frame();
            Assert.Equal(KeybindConflictPrompt.DialogKind, Shown().Kind);
        }
    }
}
