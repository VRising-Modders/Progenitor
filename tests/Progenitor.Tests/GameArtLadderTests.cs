using System;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// MENUKIT2: the shared borrowed-art ladder (<see cref="GameArtLadder"/>) and its Unity store
    /// (<c>OwnedSprites</c>, <c>GameArtUnity</c>), extracted from the Keybinds tab so the settings kit can
    /// reuse it. The Keybinds ladder must keep deciding exactly as before.
    /// </summary>
    public class GameArtLadderTests
    {
        private static readonly GameArtSource[] AllSources =
        {
            GameArtSource.Unresolved, GameArtSource.Live, GameArtSource.Cached,
            GameArtSource.Resident, GameArtSource.Kit, GameArtSource.OwnedCopy,
        };

        [Fact]
        public void Rungs_are_taken_in_order_live_owned_copy_cached_resident_kit()
        {
            for (int bits = 0; bits < 16; bits++)
            {
                bool live = (bits & 1) != 0, owned = (bits & 2) != 0, cached = (bits & 4) != 0, resident = (bits & 8) != 0;
                var expected = live ? GameArtSource.Live
                    : owned ? GameArtSource.OwnedCopy
                    : cached ? GameArtSource.Cached
                    : resident ? GameArtSource.Resident
                    : GameArtSource.Kit;
                Assert.Equal(expected, GameArtLadder.Next(live, owned, cached, resident));
                // The Keybinds ladder is the same decision.
                Assert.Equal(expected, KeybindArtLadder.Next(live, owned, cached, resident));
            }
        }

        [Fact]
        public void Only_game_rungs_count_as_borrowed_and_every_rung_has_a_stable_name()
        {
            Assert.True(GameArtLadder.Borrowed(GameArtSource.Live));
            Assert.True(GameArtLadder.Borrowed(GameArtSource.Cached));
            Assert.True(GameArtLadder.Borrowed(GameArtSource.Resident));
            Assert.True(GameArtLadder.Borrowed(GameArtSource.OwnedCopy));
            Assert.False(GameArtLadder.Borrowed(GameArtSource.Kit));
            Assert.False(GameArtLadder.Borrowed(GameArtSource.Unresolved));

            Assert.Equal("unresolved", GameArtLadder.Name(GameArtSource.Unresolved));
            Assert.Equal("live", GameArtLadder.Name(GameArtSource.Live));
            Assert.Equal("cached", GameArtLadder.Name(GameArtSource.Cached));
            Assert.Equal("resident", GameArtLadder.Name(GameArtSource.Resident));
            Assert.Equal("kit", GameArtLadder.Name(GameArtSource.Kit));
            Assert.Equal("owned-copy", GameArtLadder.Name(GameArtSource.OwnedCopy));
            Assert.Equal("unresolved", GameArtLadder.Name((GameArtSource)99));
            foreach (var source in AllSources)
            {
                Assert.Equal(GameArtLadder.Name(source), KeybindArtLadder.Name(source));
                Assert.Equal(GameArtLadder.Borrowed(source), new KeybindArt.Part(source).Borrowed);
            }
        }

        [Fact]
        public void The_resident_budget_and_the_live_rescan_interval_are_per_ladder()
        {
            // A settings ladder may take a different budget than the Keybinds tab's three.
            Assert.True(GameArtLadder.MayScanResident(0, int.MinValue, 5, maxScans: 1));
            Assert.False(GameArtLadder.MayScanResident(1, 4, 5, maxScans: 1));
            Assert.False(GameArtLadder.MayScanResident(0, 5, 5, maxScans: 1));
            Assert.False(GameArtLadder.MayScanResident(-1, int.MinValue, 5, maxScans: 3));
            for (int attempts = 0; attempts <= 4; attempts++)
                Assert.Equal(GameArtLadder.MayScanResident(attempts, 1, 2, KeybindArtLadder.MaxResidentScans),
                    KeybindArtLadder.MayScanResident(attempts, 1, 2));

            Assert.True(GameArtLadder.ShouldScanLive(true, 1, 2, 10, 11, rescanFrames: 60));
            Assert.False(GameArtLadder.ShouldScanLive(true, 2, 2, 10, 5000, rescanFrames: 60));
            Assert.False(GameArtLadder.ShouldScanLive(false, 2, 2, 10, 69, rescanFrames: 60));
            Assert.True(GameArtLadder.ShouldScanLive(false, 2, 2, 10, 70, rescanFrames: 60));
            Assert.True(GameArtLadder.ShouldScanLive(false, 2, 2, 10, 9, rescanFrames: 60), "a frame counter that went back rescans");
            Assert.Equal(GameArtLadder.ShouldScanLive(false, 2, 2, 10, 10 + KeybindArtLadder.RescanFrames - 1, KeybindArtLadder.RescanFrames),
                KeybindArtLadder.ShouldScanLive(false, 2, 2, 10, 10 + KeybindArtLadder.RescanFrames - 1));
        }

        [Fact]
        public void A_fall_through_line_carries_the_ladder_prefix_and_why_each_rung_failed()
        {
            Assert.Equal("Settings kit: art checkbox live=no donor menu in the scene cached=none kept resident=ok -> resident",
                GameArtLadder.FallThrough("Settings kit: art ", "checkbox", "no donor menu in the scene", "none kept", "", GameArtSource.Resident));
            Assert.Equal("Settings kit: art checkbox live=no donor menu in the scene -> owned-copy",
                GameArtLadder.FallThrough("Settings kit: art ", "checkbox", "no donor menu in the scene", "x", "y", GameArtSource.OwnedCopy));
            Assert.Equal("p: art track live=ok cached=ok -> cached",
                GameArtLadder.FallThrough("p: art ", "track", " ", "", "ignored", GameArtSource.Cached));
            // The Keybinds prefix is unchanged.
            Assert.Equal("Keybinds tab: art ", KeybindArtLadder.LogPrefix);
            Assert.Equal(GameArtLadder.FallThrough(KeybindArtLadder.LogPrefix, "rule", "a", "b", "c", GameArtSource.Kit),
                KeybindArtLadder.FallThrough("rule", "a", "b", "c", GameArtSource.Kit));
        }

        [Fact]
        public void Visible_alpha_makes_a_fading_donor_opaque_and_clamps()
        {
            Assert.Equal(1f, GameArtLadder.VisibleAlpha(0f));
            Assert.Equal(1f, GameArtLadder.VisibleAlpha(GameArtLadder.MinVisibleAlpha - 0.001f));
            Assert.Equal(GameArtLadder.MinVisibleAlpha, GameArtLadder.VisibleAlpha(GameArtLadder.MinVisibleAlpha));
            Assert.Equal(1f, GameArtLadder.VisibleAlpha(float.NaN));
            Assert.Equal(1f, GameArtLadder.VisibleAlpha(3f));
            Assert.Equal(0.4f, GameArtLadder.VisibleAlpha(0.4f));
            Assert.Equal(GameArtLadder.MinVisibleAlpha, KeybindArtLadder.MinVisibleAlpha);
        }

        [Fact]
        public void Owned_asset_names_are_owned_and_tagged_per_ladder()
        {
            string keybind = GameArtLadder.OwnedAssetName("keybind-art-", "SimpleMenuButton_Normal");
            Assert.Equal("progenitor-ui-keybind-art-SimpleMenuButton_Normal", keybind);
            Assert.True(UiOwnership.IsOwned(keybind), "the Core-unload sweep recognizes an owned copy");
            Assert.NotEqual(keybind, GameArtLadder.OwnedAssetName("settings-art-", "SimpleMenuButton_Normal"));
            Assert.Equal("progenitor-ui-keybind-art-", GameArtLadder.OwnedAssetName("keybind-art-", null));
            Assert.Equal(UiOwnership.Prefix, GameArtLadder.OwnedAssetName(null!, null));
        }

        [Fact]
        public void The_copy_plan_refuses_bad_regions_and_caps_the_copy_by_area()
        {
            var none = (0f, 0f, 0f, 0f);
            Assert.Equal("no usable texture region", GameArtLadder.PlanCopy(8f, 8f, 64, 64, (0.5f, 0.5f, 0.5f, 0.75f), none, false).Why);
            Assert.Equal("no usable texture region", GameArtLadder.PlanCopy(8f, 8f, 64, 64, (-0.5f, 0f, 0.125f, 0.125f), none, false).Why);
            Assert.Equal("negative packing padding", GameArtLadder.PlanCopy(8f, 8f, 64, 64, (0f, 0f, 0.125f, 0.125f), (-1f, 0f, 0f, 0f), false).Why);

            var square = GameArtLadder.PlanCopy(1024f, 1024f, 1024, 1024, (0f, 0f, 1f, 1f), none, false);
            Assert.True(square.Ok, square.Why);
            Assert.Equal(GameArtLadder.MaxCopyArea, square.FullWidth * square.FullHeight);
            Assert.StartsWith("size 1025x1024", GameArtLadder.PlanCopy(1025f, 1024f, 1025, 1024, (0f, 0f, 1f, 1f), none, false).Why);
            Assert.StartsWith("size 4097x1", GameArtLadder.PlanCopy(4097f, 1f, 4097, 1, (0f, 0f, 1f, 1f), none, false).Why);

            var refused = GameArtLadder.CopyPlan.Refuse("why");
            Assert.False(refused.Ok);
            Assert.Equal("why", refused.Why);
            Assert.False(refused.Padded);
        }

        [Fact]
        public void The_Unity_store_holds_copies_and_releases_them_and_the_scans_stay_non_generic()
        {
            string store = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "OwnedSprites.cs");
            string hold = SourcePin.MethodBody(store, "internal void Hold(");
            // Held on an inactive DontDestroyOnLoad holder, then an owned GPU copy once per donor sprite
            // (now or, for the settings kit, on an idle frame through CopyPending).
            SourcePin.AssertUncommented(hold, "Object.DontDestroyOnLoad(go)");
            SourcePin.AssertUncommented(hold, "go.SetActive(false)");
            SourcePin.AssertUncommented(hold, "if (copyNow) EnsureCopy(i, sprite);");
            string ensure = SourcePin.MethodBody(store, "private void EnsureCopy(");
            SourcePin.AssertUncommented(ensure, "if (OwnedUsable(i) && string.Equals(SafeName(_owned[i]!), OwnedName(sprite), StringComparison.Ordinal)) return;");
            SourcePin.AssertUncommented(ensure, "CopySprite(sprite, OwnedName(sprite), out var texture, out string why, keepReadable: true)");
            SourcePin.AssertUncommented(store, "GameArtLadder.OwnedAssetName(_assetTag,");
            string release = SourcePin.MethodBody(store, "internal void Release()");
            SourcePin.AssertUncommented(release, "DestroyOwned(_holder)");
            SourcePin.AssertUncommented(release, "DropOwnedCopy(i)");
            string copy = SourcePin.MethodBody(store, "internal static Sprite? CopySprite(");
            SourcePin.AssertUncommented(copy, "GameArtLadder.PlanCopy(");
            SourcePin.AssertUncommented(copy, "RenderTexture.active = previous");

            // Donor scans use only the non-generic overloads plus TryCast (interop trap 2).
            string unity = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "GameArtUnity.cs");
            Assert.DoesNotContain("FindObjectsOfType<", unity, StringComparison.Ordinal);
            Assert.DoesNotContain("FindObjectsOfTypeAll<", unity, StringComparison.Ordinal);
            SourcePin.AssertUncommented(SourcePin.MethodBody(unity, "internal static List<T> Scene<T>()"), "Object.FindObjectsOfType(type, true)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(unity, "internal static List<T> Loaded<T>()"), "Resources.FindObjectsOfTypeAll(type)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(unity, "internal static void DestroyOwned("), "BootstrapLink.MayDestroyUiImmediate()");

            // The Keybinds donors keep their holder name and asset tag, so owned names are unchanged.
            string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindArtDonors.cs");
            SourcePin.AssertUncommented(donors, "internal const string HolderName = UiOwnership.Prefix + \"core-keybind-art\"");
            SourcePin.AssertUncommented(donors, "internal const string AssetTag = \"keybind-art-\"");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void Hold("), "name = Art.OwnedName(part.SourceMaterial)");
        }
    }
}
