using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Progenitor.Reload;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// PINS2 (live 2026-10-16): a Core swap was refused because a removed plugin's load context was still alive. The
    /// player had opened its page in the Mods workspace and closed the workspace; the description column's static
    /// "page drawn last" (<see cref="ModPageDetails"/>) still held the page's settings view, whose keybind rows and
    /// server-part callback reach the plugin. Each test opens a collectible stand-in plugin's page with a key on it,
    /// leaves the page one way, runs the unload sweep, and asserts nothing in Core still reaches the plugin: no static
    /// path (the same walk the pin diagnostics run) and a weak reference to the plugin instance cleared by a GC.
    /// Game-free: the fake UI runtime and key source of <see cref="KeybindTabTests"/>.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private const string PinnedId = "pinned.mod";

        public enum PageExit
        {
            /// <summary>The workspace is closed, then the plugin unloads (the live sequence).</summary>
            CloseThenUnload,

            /// <summary>The plugin unloads while its page is on screen.</summary>
            UnloadWhileOpen,

            /// <summary>The player moved to the Keybinds tab (the workspace stays open), then the plugin unloads.</summary>
            KeybindsTabThenUnload,

            /// <summary>The player moved to Preferences, then the plugin unloads.</summary>
            PreferencesTabThenUnload,

            /// <summary>The player moved to the Server tab, then the plugin unloads.</summary>
            ServerTabThenUnload,

            /// <summary>The player opened another mod's page, closed the workspace, then the plugin unloads.</summary>
            OtherPageThenUnload,

            /// <summary>The player opened the mod's own custom page, closed the workspace, then the plugin unloads.</summary>
            CustomPageThenUnload,

            /// <summary>The player opened the mod's settings in Preferences (All settings), closed, then the plugin unloads.</summary>
            SettingsPanelThenUnload,
        }

        [Theory]
        [InlineData(PageExit.CloseThenUnload)]
        [InlineData(PageExit.UnloadWhileOpen)]
        [InlineData(PageExit.KeybindsTabThenUnload)]
        [InlineData(PageExit.PreferencesTabThenUnload)]
        [InlineData(PageExit.ServerTabThenUnload)]
        [InlineData(PageExit.OtherPageThenUnload)]
        [InlineData(PageExit.CustomPageThenUnload)]
        [InlineData(PageExit.SettingsPanelThenUnload)]
        public void A_mod_page_the_player_left_does_not_keep_its_unloaded_plugin_alive(PageExit exit)
        {
            var (weakOwner, weakCallback, holders) = OpenPageLeaveAndUnload(exit);
            Assert.True(holders.Count == 0, "still held:\n" + string.Join("\n", holders));
            Assert.Null(ModPageDetails.Active);
            for (int i = 0; i < 10 && (weakOwner.IsAlive || weakCallback.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(weakOwner.IsAlive, "the plugin instance is still reachable");
            Assert.False(weakCallback.IsAlive, "the plugin's key callback is still reachable");
        }

        /// <summary>Reopening the workspace after a close still draws the same mod's page (by id) with its keys.</summary>
        [Fact]
        public void A_closed_page_reopens_by_id_with_its_keys()
        {
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens the atlas.", () => { });
            ModsMenu.Open();
            ModsMenu.OpenMod("a.mod");
            Assert.Same(ModsMenu.ModPage!.Details, ModPageDetails.Active);
            ModsMenu.Close();
            Assert.Null(ModPageDetails.Active);
            Assert.Null(ModsMenu.ModPage);

            ModsMenu.Open();
            ModsMenu.OpenMod("a.mod");
            Assert.NotNull(ModsMenu.ModPage!.SettingsView!.KeysHeading);
            Assert.Same(ModsMenu.ModPage.Details, ModPageDetails.Active);
            Assert.Equal("a.mod", ModPageDetails.Active!.PluginId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private (WeakReference owner, WeakReference callback, List<string> holders) OpenPageLeaveAndUnload(PageExit exit)
        {
            var plugin = UnloadingPluginStandIn.Load();
            var alc = AssemblyLoadContext.GetLoadContext(plugin.Owner.GetType().Assembly)!;
            RegisterMod(PinnedId, "Pinned");
            RegisterMod("other.mod", "Other");
            using var other = ModsMenu.Register(_owner, "other.mod", "Other");
            var callback = plugin.Callback;
            var file = File("pinned");
            var level = file.Bind("General", "Level", 3, "How much.");
            // The mod's header, its custom page and a config source, each reaching the plugin.
            var registration = ModsMenu.Register(plugin.Owner, PinnedId, "Pinned")
                .Settings("General", level)
                .Header(panel => { callback(); panel.Label("Pinned"); })
                .Page("Guide", panel => { callback(); panel.Label("Guide"); });
            SettingsCatalog.Register(PinnedId, file, plugin.Owner);
            Keybinds.Register(plugin.Owner, PinnedId, "Open", file, F9, "Opens it.", callback);
            ModsMenu.Open();
            ModsMenu.OpenMod(PinnedId);
            Assert.NotNull(ModsMenu.ModPage!.SettingsView!.KeysHeading);
            Assert.NotNull(ModPageDetails.Active?.View);

            switch (exit)
            {
                case PageExit.CloseThenUnload:
                    ModsMenu.Close();
                    break;
                case PageExit.KeybindsTabThenUnload:
                    ModsMenu.SelectTab("keybinds");
                    break;
                case PageExit.PreferencesTabThenUnload:
                    ModsMenu.SelectTab("preferences");
                    break;
                case PageExit.ServerTabThenUnload:
                    ModsMenu.SelectTab("server");
                    break;
                case PageExit.CustomPageThenUnload:
                    ModsMenu.OpenPage(PinnedId, "Guide");
                    ModsMenu.Close();
                    break;
                case PageExit.SettingsPanelThenUnload:
                    SettingsPanel.OpenForModInMenu(PinnedId);
                    ModsMenu.Close();
                    break;
                case PageExit.OtherPageThenUnload:
                    ModsMenu.OpenMod("other.mod");
                    ModsMenu.Close();
                    break;
            }

            // The unload: the tracked registration goes first (ctx.Track), then the owner sweep.
            registration.Dispose();
            PluginStaticApiSweep.Run(plugin.Owner);

            var holders = new List<string>();
            var roots = typeof(ModsMenu).Assembly.GetTypes().Where(t => !t.ContainsGenericParameters).ToList();
            bool InPlugin(Assembly a) => ReferenceEquals(AssemblyLoadContext.GetLoadContext(a), alc);
            PluginPinHolders.Walk(roots, t => InPlugin(t.Assembly), InPlugin, line =>
            {
                if (line.StartsWith("pin-diag plugin-holder ", StringComparison.Ordinal)) holders.Add(line);
            }, 20000, new[] { alc });

            var result = (new WeakReference(plugin.Owner), new WeakReference(plugin.Callback), holders);
            if (ModsMenu.IsOpen) ModsMenu.Close();
            plugin.Dispose();
            return result;
        }
    }
}
