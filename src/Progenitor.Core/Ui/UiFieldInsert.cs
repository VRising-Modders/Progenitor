using System;

namespace Progenitor.Ui;

/// <summary>
/// TOKENINSERT1 (requested 2026-10-06): an Insert button puts a placeholder token into a text field at its caret, as
/// one unit: the whole token or nothing (a half token like <c>{res</c> would render literally). A field that never had
/// focus has no owned caret, so the token lands at the end (<see cref="UiFieldCaret.Read"/>); a selection is replaced.
/// Game-free so the rule has tests; the runtime applies it through its own edit path (undo, change notice, caret push).
/// </summary>
internal static class UiFieldInsert
{
    /// <summary>The state after inserting <paramref name="token"/>, or the same state when it does not fit whole.</summary>
    internal static UiTextEditState Token(UiTextEditState state, string? token, int maxLength = int.MaxValue)
    {
        state = UiTextEdit.Normalize(state);
        string add = token ?? "";
        if (add.Length == 0) return state;
        if (maxLength != int.MaxValue)
        {
            int room = Math.Max(0, maxLength - (state.Text.Length - (state.SelEnd - state.SelStart)));
            if (add.Length > room) return state;
        }

        return UiTextEdit.Insert(state, add, maxLength);
    }

    /// <summary><see cref="Token"/> on the field's live text and its owned caret (none: the end).</summary>
    internal static UiTextEditState At(string? liveText, UiTextEditState? owned, string? token, int maxLength = int.MaxValue) =>
        Token(UiFieldCaret.Read(liveText, owned), token, maxLength);
}
