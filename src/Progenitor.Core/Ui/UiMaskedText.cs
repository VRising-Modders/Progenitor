namespace Progenitor.Ui;

/// <summary>
/// MASKED1, game-free: the rules every password field follows (<see cref="IUiInput.Masked"/>,
/// <c>ctx.Dialogs.PromptSecret</c>, a masked <see cref="DialogFormOptions"/> and the container open gate's prompt). The
/// field draws one asterisk per character; a readback shows the same asterisks; copy and cut do nothing; the field keeps
/// no undo history. Nothing here ever returns the text of a masked field.
/// </summary>
internal static class UiMaskedText
{
    /// <summary>The character a masked field draws and reads back for each typed one (TMP's password default).</summary>
    internal const char Mask = '*';

    /// <summary>What a readback shows for <paramref name="text"/>: asterisks when <paramref name="masked"/>.</summary>
    internal static string Shown(string? text, bool masked) =>
        masked ? new string(Mask, (text ?? "").Length) : text ?? "";

    /// <summary>Whether a Ctrl+<paramref name="letter"/> may reach the clipboard: never copy or cut from a masked field.</summary>
    internal static bool ClipboardAllowed(char letter, bool masked)
    {
        if (!masked) return true;
        char c = char.ToLowerInvariant(letter);
        return c != 'c' && c != 'x';
    }

    /// <summary>Whether the field records undo steps: a masked field keeps none (the text would live on in them).</summary>
    internal static bool KeepsHistory(bool masked) => !masked;
}
