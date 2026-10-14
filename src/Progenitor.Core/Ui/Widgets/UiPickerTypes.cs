using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: one choice in a picker (<see cref="UiWidgets.Picker"/>): an item, a container, a station, a player — anything the
/// caller can name. <see cref="Id"/> is the caller's stable key; the picker never interprets it.
/// </summary>
public sealed class UiPickerEntry
{
    /// <summary>An entry with a stable <paramref name="id"/> and the caption <paramref name="label"/>.</summary>
    public UiPickerEntry(string id, string label)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("An entry needs an id.", nameof(id));
        Id = id;
        Label = label ?? "";
    }

    public string Id { get; }

    /// <summary>The caption, and what the search matches first.</summary>
    public string Label { get; set; }

    /// <summary>The section heading the entry is listed under (<c>Resources</c>, <c>Stations</c>); empty for none.</summary>
    public string Section { get; set; } = "";

    /// <summary>A short muted note at the row's right (<c>in 4 chests</c>, <c>plot A</c>); empty for none.</summary>
    public string Note { get; set; } = "";

    /// <summary>A <c>UnityEngine.Sprite</c> for the row's icon, or null.</summary>
    public object? Icon { get; set; }

    /// <summary>A game item's prefab id whose own icon the row shows when <see cref="Icon"/> is null; 0 for none.</summary>
    public int ItemPrefab { get; set; }

    /// <summary>
    /// Why the entry cannot be picked now (<c>Not your clan</c>); empty when it can. A row with a reason is drawn grayed,
    /// cannot be selected, and shows the reason when hovered.
    /// </summary>
    public string DisabledReason { get; set; } = "";

    /// <summary>Hover text for an entry that can be picked; empty for none.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>More words the search matches (a prefab name, a nickname); never shown.</summary>
    public IReadOnlyList<string> Keywords { get; set; } = Array.Empty<string>();

    public bool Disabled => !string.IsNullOrWhiteSpace(DisabledReason);

    /// <inheritdoc />
    public override string ToString() => Id + ":" + Label;
}

/// <summary>P7: what a picker offers.</summary>
public sealed class UiPickerOptions
{
    /// <summary>Several entries can be selected (each click flips one); false (the default) keeps one.</summary>
    public bool Multi { get; set; }

    /// <summary>Result lines shown at once, section headings included (4 to 30; 8 by default).</summary>
    public int Rows { get; set; } = 8;

    /// <summary>The search field's hint while it is empty.</summary>
    public string Placeholder { get; set; } = "Search";

    /// <summary>Offer the best match's full name as the query is typed (a click on it completes the query). On by default.</summary>
    public bool Autofill { get; set; } = true;

    /// <summary>The ids selected at the start.</summary>
    public IEnumerable<string>? Selected { get; set; }
}

/// <summary>
/// P7: a searchable picker: a search field with autofill, results grouped under section headings, icons, grayed entries
/// that say why, single or multiple selection. Selection changes raise <see cref="OnPicked"/>.
/// </summary>
public interface IUiPicker : IUiHandle
{
    /// <summary>The search text. Setting it filters as typing does.</summary>
    string Query { get; set; }

    /// <summary>The entries matching <see cref="Query"/>, best first within each section.</summary>
    IReadOnlyList<UiPickerEntry> Matches { get; }

    /// <summary>The autofill completion: the best enabled match's label when it starts with the query, else empty.</summary>
    string Suggestion { get; }

    /// <summary>Complete the query to <see cref="Suggestion"/> (and select it in single mode). False when there is none.</summary>
    bool AcceptSuggestion();

    /// <summary>The selected ids, in the order they were selected.</summary>
    IReadOnlyList<string> Selected { get; }

    /// <summary>Replace the entries. Selected ids that remain stay selected; ids that left are dropped (no event).</summary>
    void SetEntries(IEnumerable<UiPickerEntry> entries);

    /// <summary>Select or unselect <paramref name="id"/>. False for an unknown or disabled entry, or no change.</summary>
    bool Select(string id, bool selected = true);

    /// <summary>Unselect everything; raises <see cref="OnPicked"/> when something was selected.</summary>
    void ClearSelection();

    /// <summary>
    /// P7b, multi mode: select every pickable entry matching <see cref="Query"/> (also those past the drawn lines), the
    /// status line's <c>Select all</c> link. Raises <see cref="OnPicked"/> once; returns how many were added (0 in single
    /// mode or when all were selected).
    /// </summary>
    int SelectAllMatches();

    /// <summary>After the selection changed, with the selected entries.</summary>
    IDisposable OnPicked(Action<IReadOnlyList<UiPickerEntry>> handler);

    /// <summary>Readback: <c>{kind: picker, key, query, suggestion, matches, selected, lines [{kind, id, label, disabled}]}</c>.</summary>
    Dictionary<string, object?> Describe();
}
