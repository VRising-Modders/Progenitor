using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// Keybind groups (docs/mods-menu.md#keybind-groups): a feature's binds in their own titled section of
    /// the Keybinds tab, right after the owner's, in a chosen order, with read-only rows for controls the
    /// keybind API cannot bind. Photo mode's group is the first user. Game-free, on
    /// <see cref="KeybindTabTests"/>' fakes. Not run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private const string PhotoGroup = "photo-mode";

        private static readonly string[] PhotoOrder =
        {
            "PhotoMode", "PhotoModeForward", "PhotoModeBack", "PhotoModeLeft", "PhotoModeRight", "PhotoModeUp",
            "PhotoModeDown", "PhotoModeFast", "PhotoModeSlow", "PhotoModeZoomIn", "PhotoModeZoomOut",
            "PhotoModeHideHud", "PhotoModePause",
        };

        private static readonly KeybindInfoRow[] PhotoInfo =
        {
            new("Look", "Look", "Hold right mouse", "Turn the camera.", "Fixed: the right mouse button and the wheel cannot be rebound."),
            new("FlySpeed", "Flying speed", "Mouse wheel", "Change how fast the camera flies.", "Fixed: the right mouse button and the wheel cannot be rebound."),
        };

        /// <summary>Photo mode's binds, registered in a scrambled order, plus one Progenitor key that stays in its section.</summary>
        private void RegisterPhotoBinds()
        {
            var keys = new[]
            {
                UiKey.A, UiKey.B, UiKey.C, UiKey.D, UiKey.E, UiKey.F, UiKey.G, UiKey.H, UiKey.I, UiKey.J, UiKey.K, UiKey.L,
            };
            var scrambled = new[] { 7, 2, 11, 0, 9, 4, 1, 12, 6, 3, 10, 5, 8 };
            foreach (int i in scrambled)
            {
                string id = PhotoOrder[i];
                if (id == "PhotoMode")
                {
                    Keybinds.Register(_owner, SettingsPanel.PluginId, id, File("core"), new KeyChord(UiKey.F6),
                        "Toggle photo mode.", () => { });
                    continue;
                }

                Keybinds.RegisterScoped(_owner, SettingsPanel.PluginId, id, File("core"), new KeyChord(keys[i - 1]), "Photo " + id + ".",
                    () => { }, null, null, allowWhileCaptured: true, allowWhenChatUnknown: false, KeybindScope.Photo);
            }

            Keybinds.Register(_owner, SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, File("core"), new KeyChord(UiKey.F10), "", () => { });
        }

        private static IDisposable DefinePhotoGroup(Func<string?>? unavailable = null) =>
            KeybindGroups.Define(SettingsPanel.PluginId, PhotoGroup, "Photo mode", PhotoOrder, PhotoInfo, unavailable);

        private static List<Dictionary<string, object?>> SectionReadbacks() =>
            ((List<object?>)Pane().Describe()["sections"]!).Cast<Dictionary<string, object?>>().ToList();

        private static Dictionary<string, object?> GroupReadback() =>
            SectionReadbacks().Single(s => (string)s["id"]! == KeybindsTabLayout.GroupSectionId(PhotoGroup));

        [Fact]
        public void A_group_is_its_own_section_right_after_its_owner_with_rows_in_the_defined_order()
        {
            RegisterPhotoBinds();
            Keybinds.Register(_owner, "a.mod", "Wave", File("a"), F7, "", () => { });
            Keybinds.Register(_owner, "zz.mod", "Wave", File("zz"), F8, "", () => { });
            using var group = DefinePhotoGroup();
            OpenKeybindsTab();

            var sections = Pane().Model!.Sections.Where(s => !s.IsGame).ToList();
            int owner = sections.FindIndex(s => s.Id == KeybindsTabLayout.ModSectionId(SettingsPanel.PluginId));
            Assert.True(owner >= 0);
            var photo = sections[owner + 1];
            Assert.Equal(KeybindsTabLayout.GroupSectionId(PhotoGroup), photo.Id);
            Assert.Equal("Photo mode", photo.Title);
            Assert.Equal(PhotoGroup, photo.GroupId);

            var expected = PhotoOrder.Concat(PhotoInfo.Select(i => i.Id)).ToArray();
            Assert.Equal(expected, photo.Rows.Select(r => r.Claim.ActionId).ToArray());
            Assert.Equal("13 keys", photo.CountText);
            Assert.All(photo.Rows, r => Assert.Equal(photo.Id, r.SectionId));

            // The owner keeps its other key and none of the group's.
            var ownerRows = sections[owner].Rows.Select(r => r.Claim.ActionId).ToList();
            Assert.Equal(new[] { SettingsPanel.OpenKeybindId }, ownerRows);

            // The heading is drawn, reads back, and is a jump target of its own.
            var read = GroupReadback();
            Assert.Equal("Photo mode", read["title"]);
            Assert.Equal(PhotoGroup, read["group"]);
            Assert.Equal(2, read["infoRows"]);
            Assert.Null(read["unavailable"]);
            Assert.True(Pane().JumpTo(KeybindsTabLayout.GroupSectionId(PhotoGroup)));
            Assert.Equal(KeybindsTabLayout.GroupSectionId(PhotoGroup), Pane().LastJump!.Section);
            Pane().JumpTo(KeybindsPane.JumpGame);
            ModsMenu.KeysOp("jump", "group:" + PhotoGroup, null);
            Assert.Equal(KeybindsTabLayout.GroupSectionId(PhotoGroup), Pane().LastJump!.Section);
            var targets = (List<object?>)((Dictionary<string, object?>)Pane().Describe()["toolbar"]!)["jumpTargets"]!;
            Assert.Contains(targets.Cast<Dictionary<string, object?>>(), t => (string)t["target"]! == KeybindsTabLayout.GroupSectionId(PhotoGroup));

            // A grouped key row still describes itself (its registered help) and is still editable.
            var forward = photo.Rows.Single(r => r.Claim.ActionId == "PhotoModeForward");
            Assert.True(forward.Editable);
            Assert.Equal(PhotoGroup, forward.Readback(true)["group"]);
            Assert.Equal("Photo PhotoModeForward.", KeybindsTabLayout.Describe(forward, "selected").Body);
            Assert.True(Pane().BeginEdit(forward.Id));
        }

        [Fact]
        public void Info_rows_are_read_only_and_never_reset_cleared_or_in_a_conflict()
        {
            RegisterPhotoBinds();
            using var group = DefinePhotoGroup();
            OpenKeybindsTab();

            var model = Pane().Model!;
            var look = model.ModRows().Single(r => r.IsInfo && r.Info!.Id == "Look");
            Assert.Equal("Look", look.Name);
            Assert.Equal("Hold right mouse", look.KeyText);
            Assert.False(look.Editable);
            Assert.False(look.CanReset);
            Assert.False(look.CanClear);
            Assert.False(look.HasGutter);
            Assert.False(look.Conflict);
            Assert.False(look.Changed);
            Assert.StartsWith(KeybindsTabLayout.InfoRowPrefix, look.Id);
            Assert.Same(look, model.FindRow(look.Id));
            Assert.DoesNotContain(model.ResettableRows(), r => r.IsInfo);

            var read = look.Readback(true);
            Assert.Equal(true, read["info"]);
            Assert.Equal(false, read["editable"]);
            Assert.Equal("info", read["source"]);
            Assert.Equal("Hold right mouse", read["chord"]);
            Assert.Equal(PhotoInfo[0].Note, read["note"]);
            Assert.Equal("Turn the camera.", read["description"]);

            var desc = KeybindsTabLayout.Describe(look, "selected");
            Assert.Equal("Look", desc.Heading);
            Assert.Equal("Turn the camera.", desc.Body);
            Assert.Contains(PhotoInfo[0].Note, desc.Info);

            // No capture, no reset, no clear: the click shows why instead.
            Assert.False(Pane().BeginEdit(look.Id));
            Pane().PressPrimary(look.Id);
            Assert.Equal("", Pane().Editing);
            Assert.Contains(PhotoInfo[0].Note, Pane().Description.Info);
            Pane().ResetRow(look.Id);
            Pane().ClearRow(look.Id);
        }

        [Fact]
        public void The_unavailable_reason_is_a_section_note_and_clears_when_it_stops()
        {
            RegisterPhotoBinds();
            string? reason = "Unavailable on this server: photo mode is disabled by the server.";
            using var group = DefinePhotoGroup(() => reason);
            OpenKeybindsTab();

            var read = GroupReadback();
            Assert.Equal(reason, read["unavailable"]);
            Assert.Equal(reason, read["unavailableShown"]);
            // The rows stay, and can still be changed.
            var forward = Pane().Model!.ModRows().Single(r => r.Claim.ActionId == "PhotoModeForward");
            Assert.True(forward.Editable);
            Assert.Equal(13, Pane().Model!.Find(KeybindsTabLayout.GroupSectionId(PhotoGroup))!.KeyRowCount);

            reason = null;
            Assert.Null(GroupReadback()["unavailable"]);
            // The drawn note follows on the next filter pass.
            Pane().ApplySearch("photo");
            Pane().ApplySearch("");
            Assert.Equal("", GroupReadback()["unavailableShown"]);
        }

        [Fact]
        public void Disposing_a_group_returns_its_binds_to_the_owner_section()
        {
            RegisterPhotoBinds();
            var group = DefinePhotoGroup();
            OpenKeybindsTab();
            Assert.NotNull(Pane().Model!.Find(KeybindsTabLayout.GroupSectionId(PhotoGroup)));

            group.Dispose();
            group.Dispose();
            Assert.Equal(0, KeybindGroups.Count);
            ModsMenu.TickKeybindsTab();

            var model = Pane().Model!;
            Assert.Null(model.Find(KeybindsTabLayout.GroupSectionId(PhotoGroup)));
            var owner = model.Find(KeybindsTabLayout.ModSectionId(SettingsPanel.PluginId))!;
            Assert.Equal(14, owner.Rows.Count);
            Assert.DoesNotContain(model.ModRows(), r => r.IsInfo);
            Assert.All(owner.Rows, r => Assert.Equal("", r.GroupId));
        }

        [Fact]
        public void Defining_the_same_group_again_replaces_it_and_unknown_ids_are_skipped()
        {
            RegisterPhotoBinds();
            using var first = DefinePhotoGroup();
            using var second = KeybindGroups.Define(SettingsPanel.PluginId, PhotoGroup, "Photo",
                new[] { "PhotoModePause", "NotRegistered", "PhotoMode" }, Array.Empty<KeybindInfoRow>());
            Assert.Equal(1, KeybindGroups.Count);
            OpenKeybindsTab();

            var photo = Pane().Model!.Find(KeybindsTabLayout.GroupSectionId(PhotoGroup))!;
            Assert.Equal("Photo", photo.Title);
            Assert.Equal(new[] { "PhotoModePause", "PhotoMode" }, photo.Rows.Select(r => r.Claim.ActionId).ToArray());

            // The replaced handle no longer removes the group.
            first.Dispose();
            Assert.Equal(1, KeybindGroups.Count);
        }

        [Fact]
        public void Search_matches_info_rows_by_name_and_fixed_input()
        {
            RegisterPhotoBinds();
            using var group = DefinePhotoGroup();
            OpenKeybindsTab();

            var model = Pane().Model!;
            var photo = model.Find(KeybindsTabLayout.GroupSectionId(PhotoGroup))!;
            var speed = photo.Rows.Single(r => r.IsInfo && r.Info!.Id == "FlySpeed");
            Assert.True(KeybindsTabModel.RowVisible(speed, photo, KeybindsFilter.All, "flying"));
            Assert.True(KeybindsTabModel.RowVisible(speed, photo, KeybindsFilter.All, "wheel"));
            Assert.False(KeybindsTabModel.RowVisible(speed, photo, KeybindsFilter.Changed, "flying"));

            Pane().ApplySearch("flying");
            var rows = ((List<object?>)Pane().Describe()["rows"]!).Cast<Dictionary<string, object?>>().ToList();
            var visible = rows.Where(r => (bool)r["visible"]!).Select(r => (string)r["name"]!).ToList();
            Assert.Equal(new[] { "Flying speed" }, visible);
            Assert.Equal(1, GroupReadback()["shown"]);
        }
    }
}
