using System.Linq;
using BepInEx.Configuration;
using Progenitor.Debugging;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// 2026-10-18 regression (#83 refused Ctrl+Shift+left click for every map key, so a player's saved debug map
    /// teleport on it registered unbound): Core's <c>progenitor/TeleportToMapPoint</c> is the one bind exempt from the
    /// map's Ctrl+Shift+left click rule, its default is that chord, and a saved value on it binds again. Every other bind
    /// keeps the refusal, and the right-click unlock stays refused for all. Game-free, on <see cref="KeybindTabTests"/>'
    /// fakes.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord MapTp = new(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift);

        private IKeybindHandle RegisterDebugTeleport(ConfigFile file, KeyChord defaultKey) =>
            Keybinds.RegisterScoped(_owner, SettingsPanel.PluginId, DebugMapPolicy.TeleportKeybindId, file, defaultKey, "",
                () => { }, null, () => { }, allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);

        [Fact]
        public void Only_the_debug_map_teleport_may_share_the_games_admin_teleport_chord()
        {
            string id = DebugMapPolicy.TeleportKeybindId;
            Assert.True(MouseChords.IsGameTeleportTwin("progenitor", "TeleportToMapPoint"));
            Assert.True(MouseChords.IsGameTeleportTwin(" Progenitor ", "teleporttomappoint"));
            Assert.False(MouseChords.IsGameTeleportTwin("other.mod", id));
            Assert.False(MouseChords.IsGameTeleportTwin(null, id));
            Assert.False(MouseChords.IsGameTeleportTwin("progenitor", "ShareMapLocation"));

            Assert.Null(MouseChords.Refusal(MapTp, KeybindScope.Map, "progenitor", id));
            Assert.Null(MouseChords.Refusal(new KeyChord(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt),
                KeybindScope.Map, "progenitor", id));
            Assert.Equal(MouseChords.MapTeleportReason, MouseChords.Refusal(MapTp, KeybindScope.Map, "other.mod", "Teleport"));
            Assert.Equal(MouseChords.MapTeleportReason, MouseChords.Refusal(MapTp, KeybindScope.Map, null, id));
            Assert.Equal(MouseChords.MapTeleportReason, MouseChords.Refusal(MapTp, KeybindScope.Map));

            // The exemption is only the teleport chord: the region unlock and a bare click stay refused for it too.
            var unlock = new KeyChord(UiKey.MouseRight, KeyModifiers.Ctrl | KeyModifiers.Shift);
            Assert.Equal(MouseChords.MapUnlockReason, MouseChords.Refusal(unlock, KeybindScope.Map, "progenitor", id));
            Assert.Equal(MouseChords.BareClickReason, MouseChords.Refusal(new KeyChord(UiKey.MouseLeft), KeybindScope.Map, "progenitor", id));
        }

        [Fact]
        public void The_debug_map_teleport_registers_on_its_default_Ctrl_Shift_left_click()
        {
            RegisterDebugTeleport(File("progenitor"), DebugMapPolicy.DefaultTeleportKey);
            var bind = Keybinds.Snapshot().Single(b => b.Id == DebugMapPolicy.TeleportKeybindId);
            Assert.Equal(MapTp, bind.Chord);
            Assert.DoesNotContain(_log.Warnings, l => l.Contains("cannot be bound to"));

            // Another mod's map key on the same default is still registered unbound.
            RegisterMap("c.mod", "Teleport", MapTp);
            Assert.Equal(KeyChord.None, Keybinds.Snapshot().Single(b => b.Id == "Teleport").Chord);
            Assert.Contains(_log.Warnings, l => l.Contains(MouseChords.MapTeleportReason));
        }

        [Fact]
        public void A_saved_Ctrl_Shift_left_click_binds_again_and_the_cfg_value_is_left_as_saved()
        {
            // The user's cfg after #83: the old default was None and the saved value was the game's chord. The refusal
            // only replaced the live chord; it never wrote the cfg, so the next start reads the value back.
            var file = File("progenitor");
            var saved = file.Bind(Keybinds.Section, DebugMapPolicy.TeleportKeybindId, "None", "");
            saved.Value = "MouseLeft + Ctrl + Shift";
            RegisterDebugTeleport(file, KeyChord.None);
            Assert.Equal(MapTp, Keybinds.Snapshot().Single(b => b.Id == DebugMapPolicy.TeleportKeybindId).Chord);
            Assert.Equal("MouseLeft + Ctrl + Shift", saved.Value);

            // For any other map key the saved value is still refused, and its cfg is still not rewritten.
            var other = File("e");
            var otherSaved = other.Bind(Keybinds.Section, "Teleport", "None", "");
            otherSaved.Value = "MouseLeft + Ctrl + Shift";
            RegisterMap("e.mod", "Teleport", KeyChord.None);
            Assert.Equal(KeyChord.None, Keybinds.Snapshot().Single(b => b.PluginId == "e.mod").Chord);
            Assert.Equal("MouseLeft + Ctrl + Shift", otherSaved.Value);
        }

        [Fact]
        public void The_debug_map_teleport_may_be_moved_back_onto_the_games_chord_and_other_map_keys_may_not()
        {
            RegisterDebugTeleport(File("progenitor"), DebugMapPolicy.DefaultTeleportKey);
            Assert.True(Keybinds.Rebind(SettingsPanel.PluginId, DebugMapPolicy.TeleportKeybindId, F8));
            Assert.True(Keybinds.TryRebind(SettingsPanel.PluginId, DebugMapPolicy.TeleportKeybindId, MapTp, Keybinds.RebindCheck.Full, out var why), why);
            Assert.Equal(MapTp, Keybinds.Snapshot().Single(b => b.Id == DebugMapPolicy.TeleportKeybindId).Chord);

            Assert.Null(KeybindRemapGuard.Check(KeybindSource.Progenitor, SettingsPanel.PluginId, DebugMapPolicy.TeleportKeybindId,
                F8, MapTp, scope: KeybindScope.Map));
            // The exemption is for Progenitor's own bind, not for a cfg key that happens to carry the same names.
            Assert.Equal(MouseChords.MapTeleportReason, KeybindRemapGuard.Check(KeybindSource.Cfg, SettingsPanel.PluginId,
                DebugMapPolicy.TeleportKeybindId, F8, MapTp, scope: KeybindScope.Map)?.Reason);

            RegisterMap("third.mod", "Mark", new KeyChord(UiKey.MouseLeft, KeyModifiers.Alt));
            Assert.False(Keybinds.TryRebind("third.mod", "Mark", MapTp, Keybinds.RebindCheck.Full, out why));
            Assert.Equal(MouseChords.MapTeleportReason, why);
        }

        [Fact]
        public void Reset_is_offered_for_the_debug_map_teleport_back_to_the_games_chord()
        {
            var claim = new KeybindClaim(KeybindSource.Progenitor, SettingsPanel.PluginId, DebugMapPolicy.TeleportKeybindId,
                "Teleport to map point", F8, defaultChord: MapTp, editable: true, scope: KeybindScope.Map);
            var row = new KeybindsRowModel(SettingsPanel.PluginId, new KeybindVerdict(claim, true, null, "", null!, ""));
            Assert.True(row.CanReset);

            var other = new KeybindClaim(KeybindSource.Progenitor, "other.mod", "Teleport", "Teleport", F8,
                defaultChord: MapTp, editable: true, scope: KeybindScope.Map);
            Assert.False(new KeybindsRowModel("other.mod", new KeybindVerdict(other, true, null, "", null!, "")).CanReset);
        }
    }
}
