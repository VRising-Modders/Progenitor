using System;
using System.Collections.Generic;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// MENUKIT2 (decided 2026-09-30): the settings page's description column, as the game's Options
/// page details panel (<c>OptionsMenu.DetailsHeader</c> / <c>DetailsDivider</c> / <c>DetailsDescription</c>)
/// and the Keybinds tab's <see cref="KeybindsDescription"/>: the hovered row, else the selected row, else
/// the first row, described as a heading (the setting's name), the help, and the notes that used to follow
/// the help under the row (<see cref="ModSettingsLayout.HelpParts"/>: read-only or grayed reason, scope,
/// <c>The default is …</c>, <c>Pending restart.</c>, the key conflict, a transient status). OPTIONHOVER1: while the
/// pointer is over an option of a dropdown's open list or of a radio list, the column describes that option instead
/// (<see cref="ForOption"/>). Game-free.
/// </summary>
internal sealed class ModSettingsDescription
{
    internal const string SourceHover = "hover";
    internal const string SourceSelected = "selected";
    internal const string SourceFirst = "first";
    internal const string SourceNone = "none";

    /// <summary>OPTIONHOVER1: the column describes the option under the pointer.</summary>
    internal const string SourceOption = "option";

    /// <summary>The readback row key of the column (<c>mods_ui widget_read</c>).</summary>
    internal const string ReadbackKey = "page-settings-description";

    internal const string ToneWarn = "warn";
    internal const string ToneMuted = "muted";

    internal ModSettingsDescription(string rowId, string heading, string body, IReadOnlyList<string>? notes, string tone, string source)
    {
        RowId = rowId ?? "";
        Heading = heading ?? "";
        Body = body ?? "";
        Notes = notes ?? Array.Empty<string>();
        Tone = tone ?? "";
        Source = source ?? SourceNone;
    }

    /// <summary>OPTIONHOVER1: the hovered option's stored value, its label and its explanation (empty unless <see cref="SourceOption"/>).</summary>
    internal string OptionValue { get; private init; } = "";

    /// <inheritdoc cref="OptionValue"/>
    internal string OptionLabel { get; private init; } = "";

    /// <inheritdoc cref="OptionValue"/>
    internal string OptionDescription { get; private init; } = "";

    internal string RowId { get; }

    /// <summary>The setting's display name (the game's centered details heading).</summary>
    internal string Heading { get; }

    /// <summary>The help: the builder's, else the entry's description. Empty when the setting has none.</summary>
    internal string Body { get; }

    /// <summary>The sentences after the help, in the help line's order.</summary>
    internal IReadOnlyList<string> Notes { get; }

    /// <summary><c>warn</c> for a refusal, a grayed row or a key conflict; <c>muted</c> otherwise; empty with no notes.</summary>
    internal string Tone { get; }

    /// <summary><c>hover</c>, <c>selected</c>, <c>first</c>, <c>option</c> or <c>none</c>.</summary>
    internal string Source { get; }

    /// <summary>The notes as one paragraph.</summary>
    internal string NotesText => string.Join(" ", Notes);

    /// <summary>What the column says with no row to describe: the page's title and its one-liner.</summary>
    internal static ModSettingsDescription Empty(string? heading, string? body) =>
        new("", heading ?? "", body ?? "", null, "", SourceNone);

    /// <summary>Which row the column describes: the hovered one, else the selected one, else the first.</summary>
    internal static (string RowId, string Source) Pick(string? hoveredId, string? selectedId, string? firstId)
    {
        if (!string.IsNullOrEmpty(hoveredId)) return (hoveredId!, SourceHover);
        if (!string.IsNullOrEmpty(selectedId)) return (selectedId!, SourceSelected);
        if (!string.IsNullOrEmpty(firstId)) return (firstId!, SourceFirst);
        return ("", SourceNone);
    }

    /// <summary>
    /// The column for a setting row. <paramref name="why"/> is the grayed or refused reason and
    /// <paramref name="status"/> the transient status, as the help line takes them.
    /// </summary>
    internal static ModSettingsDescription For(
        SettingsRow row, ModSettingOptions? options, SettingControl control, string? why, string? status, string source,
        bool scopeWhy = false)
    {
        if (row == null) return Empty("", "");
        var (help, notes) = ModSettingsLayout.HelpParts(row, options, control, why, status, scopeWhy);
        bool conflict = row.Kind == SettingsEditorKind.Keybind && ModSettingsLayout.KeyConflictLineOf(row).Length > 0;
        bool warn = conflict || !string.IsNullOrWhiteSpace(why) || IsRefusal(status);
        string tone = notes.Count == 0 ? "" : warn ? ToneWarn : ToneMuted;
        return new ModSettingsDescription(row.RowId, ModSettingsLayout.DisplayNameOf(row, options), help, notes, tone, source);
    }

    /// <summary>The column for a builder button or info line: its label, its help, and its reason and status.</summary>
    internal static ModSettingsDescription ForExtra(
        string id, string label, string? help, string? why, string? status, string source)
    {
        var notes = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(why)) notes.Add(ModSettingsLayout.Sentence(why));
        if (!string.IsNullOrWhiteSpace(status)) notes.Add(ModSettingsLayout.Sentence(status));
        string tone = notes.Count == 0 ? "" : !string.IsNullOrWhiteSpace(why) || IsRefusal(status) ? ToneWarn : ToneMuted;
        return new ModSettingsDescription(id, label, ModSettingsLayout.Sentence(help), notes, tone, source);
    }

    /// <summary>
    /// OPTIONHOVER1: the column while the pointer is over <paramref name="option"/> of the setting <paramref name="setting"/>
    /// describes: the setting's name as the heading; the option's label and its explanation as the body
    /// (<c>Hard: In the game's Brutal preset, …</c>, the label alone when it has none); then the setting's own notes — its
    /// help's sentences that start with <c>Applies</c> (<c>Applies at the next restart.</c>), followed by the notes the
    /// setting's column carries (a refusal, <c>The default is …</c>, a status). The tone follows the setting's.
    /// </summary>
    internal static ModSettingsDescription ForOption(ModSettingsDescription setting, UiChoiceOption option)
    {
        if (setting == null) return Empty("", "");
        string label = option.Label ?? "";
        string about = (option.Description ?? "").Trim();
        var notes = new List<string>(setting.Notes.Count + 1);
        foreach (string sentence in TimingNotes(setting.Body))
            if (!notes.Contains(sentence)) notes.Add(sentence);
        foreach (string note in setting.Notes)
            if (!notes.Contains(note)) notes.Add(note);
        string tone = notes.Count == 0 ? "" : setting.Tone == ToneWarn ? ToneWarn : ToneMuted;
        return new ModSettingsDescription(setting.RowId, setting.Heading, OptionBody(label, about), notes, tone, SourceOption)
        {
            OptionValue = option.Value ?? "",
            OptionLabel = label,
            OptionDescription = about.Length == 0 ? "" : ModSettingsLayout.Sentence(about),
        };
    }

    /// <summary>OPTIONHOVER1: the option's line in the column: <c>Label: Explanation.</c>, or the label alone.</summary>
    internal static string OptionBody(string? label, string? description)
    {
        string l = (label ?? "").Trim();
        string d = (description ?? "").Trim();
        if (d.Length == 0) return l;
        d = ModSettingsLayout.Sentence(d);
        return l.Length == 0 ? d : l + ": " + d;
    }

    /// <summary>
    /// OPTIONHOVER1: the sentences of <paramref name="help"/> that say when a change takes effect: those that start with
    /// <c>Applies</c> (<c>Applies at the next restart.</c>, <c>Applies at once, to the next player who joins.</c>), in order.
    /// </summary>
    internal static IReadOnlyList<string> TimingNotes(string? help)
    {
        var result = new List<string>(1);
        string text = (help ?? "").Trim();
        if (text.Length == 0) return result;
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            bool end = i == text.Length
                || ((text[i] == '.' || text[i] == '!' || text[i] == '?') && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])));
            if (!end) continue;
            int stop = Math.Min(i + 1, text.Length);
            string sentence = text.Substring(start, stop - start).Trim();
            start = stop;
            if (sentence.StartsWith("Applies", StringComparison.OrdinalIgnoreCase))
                result.Add(ModSettingsLayout.Sentence(sentence));
        }

        return result;
    }

    /// <summary>A status that is not one of the page's confirmations (<c>Saved.</c>, <c>Done.</c>, the server's) reads as a refusal.</summary>
    private static bool IsRefusal(string? status)
    {
        string s = (status ?? "").Trim();
        if (s.Length == 0) return false;
        return !(s == ModSettingsView.SavedNote || s == "Done." || s == ModSettingsView.ServerSavedNote
            || s == ModSettingsView.WaitingNote || s == ModSettingsView.AdminNote);
    }

    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["row"] = RowId,
        ["source"] = Source,
        ["heading"] = Heading,
        ["body"] = Body,
        ["notes"] = NotesText,
        ["tone"] = Tone,
        // OPTIONHOVER1: the option under the pointer (empty unless source is option), and the column's text as drawn.
        ["option"] = OptionValue,
        ["optionLabel"] = OptionLabel,
        ["optionDescription"] = OptionDescription,
        ["text"] = Text,
    };

    /// <summary>OPTIONHOVER1: the column's text as one string: the heading, the body and the notes, a line each (empty parts skipped).</summary>
    internal string Text
    {
        get
        {
            var parts = new List<string>(3);
            if (Heading.Length > 0) parts.Add(Heading);
            if (Body.Length > 0) parts.Add(Body);
            if (Notes.Count > 0) parts.Add(NotesText);
            return string.Join("\n", parts);
        }
    }
}

/// <summary>
/// MENUKIT2: where the description goes. A frame wide enough gets the column at the page's right (the
/// game's details panel, <see cref="GameSettingsLook.DetailsWidth"/> wide at most); a narrower one gets no
/// column, and the selected row alone shows its help as <b>one</b> line under it. Game-free.
/// </summary>
internal static class ModSettingsDescriptionLayout
{
    /// <summary>Below this content width the page has no column (the rows would get too narrow).</summary>
    internal const float MinContentWidthForColumn = 1100f;

    /// <summary>The column's share of the content width, and its floor; its ceiling is the game's 440 units.</summary>
    internal const float ColumnShare = 0.28f;

    internal const float MinColumnWidth = 300f;

    internal static float MaxColumnWidth => GameSettingsLook.Kit(GameSettingsLook.DetailsWidth);

    /// <summary>Between the rows and the column; the page's scrollbar sits in its middle (round D).</summary>
    internal const float ColumnGap = 28f;

    /// <summary>
    /// The column's inner padding (left, right, top, bottom). Round D (live): with the game's 8 the body
    /// started against the scrollbar; the game's own details text sits about 20 px in.
    /// </summary>
    internal static readonly (float L, float R, float T, float B) Padding = (20f, 20f, 14f, 14f);

    /// <summary>The page scrollbar's width while the column is shown: thin, like the game's.</summary>
    internal const float ScrollbarWidth = 6f;

    /// <summary>The narrow fallback's help line: one line, cut with an ellipsis.</summary>
    internal const int NarrowHelpLines = 1;

    internal static bool HasColumn(float contentWidth) =>
        !float.IsNaN(contentWidth) && contentWidth >= MinContentWidthForColumn;

    /// <summary>The column's width for a content area <paramref name="contentWidth"/> wide, or 0 when it has none.</summary>
    internal static float ColumnWidth(float contentWidth)
    {
        if (!HasColumn(contentWidth)) return 0f;
        return Math.Max(MinColumnWidth, Math.Min(MaxColumnWidth, (float)Math.Round(contentWidth * ColumnShare)));
    }

    /// <summary>What is left for the rows beside the column.</summary>
    internal static float RowsWidth(float contentWidth)
    {
        float column = ColumnWidth(contentWidth);
        return column <= 0f ? contentWidth : contentWidth - column - ColumnGap;
    }

    /// <summary>
    /// The heading, ornament and body sizes, from the game's details panel: heading 30, body 20, ornament
    /// <c>Divider_Big_02</c>, 12 between them.
    /// </summary>
    internal static float HeadingFontSize => GameSettingsLook.DetailsTitleText;

    internal static float BodyFontSize => GameSettingsLook.DetailsBodyText;

    internal static float Spacing => GameSettingsLook.Kit(GameSettingsLook.DetailsSpacing);
}
