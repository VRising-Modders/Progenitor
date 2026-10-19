using System;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// Live 2026-10-14 ("why are some of these install buttons highlighted?"): after the settings kit's idle
    /// preparation found the game's Options menu live, it replaced the adopted (kept) popup-button copy and
    /// destroyed the old sprite. Every Install-tab button painted before then kept a reference to the
    /// destroyed sprite, so Unity drew a flat white quad under the column tint (#e6f0fa / #fae6e6) and the
    /// 55% plate: pale blue and pale pink buttons that were neither disabled nor hovered. The bridge readback
    /// showed <c>sprite: null</c> on all 157 visible column buttons while their tone stayed gold.
    /// </summary>
    public class PopupButtonArtLifetimeTests
    {
        [Fact]
        public void A_control_painted_with_art_is_repainted_only_when_its_sprite_is_gone()
        {
            Assert.True(GameArtLadder.NeedsRepaint(paintedWithArt: true, drawnSpriteAlive: false));
            Assert.False(GameArtLadder.NeedsRepaint(paintedWithArt: true, drawnSpriteAlive: true));
            // The kit look draws no sprite: it is never repainted for that (no repaint every layout pass).
            Assert.False(GameArtLadder.NeedsRepaint(paintedWithArt: false, drawnSpriteAlive: false));
            Assert.False(GameArtLadder.NeedsRepaint(paintedWithArt: false, drawnSpriteAlive: true));
        }

        [Fact]
        public void A_replaced_owned_copy_is_retired_not_destroyed_while_the_Core_lives()
        {
            string store = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "OwnedSprites.cs");

            // Holding a new donor sprite no longer drops the copy that is on screen; CopyPending replaces it.
            string hold = SourcePin.MethodBody(store, "internal void Hold(");
            Assert.DoesNotContain("DropOwnedCopy", hold, StringComparison.Ordinal);
            Assert.DoesNotContain("RetireOwnedCopy", hold, StringComparison.Ordinal);

            // The replacement retires the old copy only once the new one exists.
            string ensure = SourcePin.MethodBody(store, "private void EnsureCopy(");
            Assert.DoesNotContain("DropOwnedCopy", ensure, StringComparison.Ordinal);
            int copied = ensure.IndexOf("CopySprite(sprite, OwnedName(sprite)", StringComparison.Ordinal);
            int failed = ensure.IndexOf("if (copy == null)", StringComparison.Ordinal);
            int retired = ensure.IndexOf("RetireOwnedCopy(i);", StringComparison.Ordinal);
            Assert.True(copied >= 0 && failed > copied && retired > failed, "retire after the new copy is made");

            // Adopting a kept copy retires too; only Release destroys, and it destroys the retired list.
            string adopt = SourcePin.MethodBody(store, "internal bool Adopt(");
            SourcePin.AssertUncommented(adopt, "RetireOwnedCopy(i);");
            Assert.DoesNotContain("DropOwnedCopy", adopt, StringComparison.Ordinal);
            string release = SourcePin.MethodBody(store, "internal void Release()");
            SourcePin.AssertUncommented(release, "DropOwnedCopy(i)");
            SourcePin.AssertUncommented(release, "foreach (var retired in _retired) DestroyAsset(retired);");
            string retire = SourcePin.MethodBody(store, "private void RetireOwnedCopy(");
            Assert.DoesNotContain("Destroy", retire, StringComparison.Ordinal);
            // A kept texture is Bootstrap's: never listed for destruction.
            SourcePin.AssertUncommented(retire, "if (!_textureKept[i] && AliveObject(_ownedTexture[i])) _retired.Add(_ownedTexture[i]!);");
        }

        [Fact]
        public void Buttons_painted_with_borrowed_art_heal_on_the_next_layout_pass()
        {
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static void PaintButton(Cell cell)"), "cell.PaintedArt = false;");
            SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static bool PaintPopupButton(Cell cell)"), "cell.PaintedArt = true;");
            SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static bool PaintChip(Cell cell)"), "cell.PaintedArt = true;");
            SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static bool PaintKeyCap(Cell cell)"), "cell.PaintedArt = true;");
            SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "public void PlaceCells(float rowWidth)"),
                "if (cell.PaintedArt && cell.Spec.Kind == CellKind.Button) RepaintIfArtGone(cell);");
            string heal = SourcePin.MethodBody(runtime, "private static void RepaintIfArtGone(Cell cell)");
            SourcePin.AssertUncommented(heal, "GameArt.GameArtLadder.NeedsRepaint(cell.PaintedArt, GameArt.GameArtUnity.AliveSprite(drawn))");
            SourcePin.AssertUncommented(heal, "PaintButton(cell);");
        }
    }
}
