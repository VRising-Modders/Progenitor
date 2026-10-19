using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>The control a client draws a received setting with. Wire values; never renumber.</summary>
internal enum ScopedWidget : byte
{
    /// <summary>The per-type default (switch, number field, selector, text field).</summary>
    Default = 0,
    Toggle = 1,
    Slider = 2,
    Radio = 3,
    Dropdown = 4,
    Number = 5,
    Text = 6,
    TextArea = 7,
    /// <summary>An item-list setting (<see cref="ItemLimitList"/>): a summary and an editor with the item picker.</summary>
    ItemList = 8,
}

/// <summary>
/// Declarative presentation hints of one published setting (PrSt version 2). Derived on the server from the mod's
/// settings page row that places the setting, else its <c>ctx.Menu.RegisterSetting</c> presentation, else empty (the
/// client's per-type defaults). Data only: no delegates, no client code.
/// </summary>
internal sealed class ScopedSettingPresentation
{
    internal const int MaxLabelBytes = 256;
    internal const int MaxUnitsBytes = 32;
    internal const int MaxCategories = 64;
    internal const int MaxCategoryLabelBytes = 64;
    internal const int MaxBlockBytes = 6144;

    internal ScopedWidget Widget { get; set; }
    internal string Label { get; set; } = "";
    /// <summary>The page section's display name (empty: the cfg section).</summary>
    internal string SectionTitle { get; set; } = "";
    /// <summary>The section's place on the page, 0 first; -1: not placed.</summary>
    internal int SectionOrder { get; set; } = -1;
    /// <summary>The row's place on the page, 0 first; -1: not placed.</summary>
    internal int RowOrder { get; set; } = -1;
    /// <summary>A slider's or number's step as invariant text, or null.</summary>
    internal string? Step { get; set; }
    /// <summary>Unit or format text drawn after the value (<c>m</c>, <c>%</c>), or empty.</summary>
    internal string Units { get; set; } = "";
    /// <summary>Display words for the row's choices, index-aligned with them; empty: the stored values.</summary>
    internal string[] ChoiceLabels { get; set; } = Array.Empty<string>();
    internal SettingCondition? ShowIf { get; set; }
    internal SettingCondition? EnableIf { get; set; }
    /// <summary>An item list's categories for the picker (ids and names, same length), and whether lines may name one.</summary>
    internal string[] CategoryIds { get; set; } = Array.Empty<string>();
    internal string[] CategoryLabels { get; set; } = Array.Empty<string>();
    internal bool AllowCategories { get; set; }
    internal int MaxEntries { get; set; }
    internal int MaxCount { get; set; }
    internal string CountLabel { get; set; } = "";

    internal bool IsEmpty => Widget == ScopedWidget.Default && Label.Length == 0 && SectionTitle.Length == 0
        && SectionOrder < 0 && RowOrder < 0 && Step == null && Units.Length == 0 && ChoiceLabels.Length == 0
        && ShowIf == null && EnableIf == null && CategoryIds.Length == 0 && !AllowCategories && MaxEntries == 0
        && MaxCount == 0 && CountLabel.Length == 0;

    internal ScopedSettingPresentation Copy()
    {
        var copy = (ScopedSettingPresentation)MemberwiseClone();
        copy.ChoiceLabels = (string[])ChoiceLabels.Clone();
        copy.CategoryIds = (string[])CategoryIds.Clone();
        copy.CategoryLabels = (string[])CategoryLabels.Clone();
        return copy;
    }

    /// <summary>The last <see cref="Reduced"/> step: what is left is always sent.</summary>
    internal const int MaxReduction = 4;

    /// <summary>
    /// A copy with the optional parts dropped, in order, up to <paramref name="level"/>: 1 category names (the ids are
    /// shown), 2 the categories, 3 the choice labels (the stored values are shown), 4 the show/enable rules. The
    /// control, label, section, order, step, units and list bounds are never dropped.
    /// </summary>
    internal ScopedSettingPresentation Reduced(int level)
    {
        var p = Copy();
        if (level >= 1) p.CategoryLabels = (string[])p.CategoryIds.Clone();
        if (level >= 2) { p.CategoryIds = Array.Empty<string>(); p.CategoryLabels = Array.Empty<string>(); }
        if (level >= 3) p.ChoiceLabels = Array.Empty<string>();
        if (level >= 4) { p.ShowIf = null; p.EnableIf = null; }
        return p;
    }

    /// <summary>What <see cref="Reduced"/> drops at <paramref name="level"/>, for the log.</summary>
    internal static string ReductionWords(int level) => level switch
    {
        1 => "category names",
        2 => "categories",
        3 => "categories and choice labels",
        _ => "categories, choice labels and show/enable rules",
    };

    /// <summary>The block's encoded size for a row with <paramref name="choices"/> choices.</summary>
    internal int EncodedBytes(int choices)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Utf8, leaveOpen: true)) WriteUnchecked(w);
        Validate(choices);
        return (int)ms.Length;
    }

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// The version 2 row block: widget byte, label, section title, section order (i32), row order (i32), step presence +
    /// text, units, choice label count (u16) + labels, show-if presence + condition, enable-if presence + condition,
    /// category count (u16) + id/label pairs, allow-categories, max entries (i32), max count (i32), count label.
    /// </summary>
    internal void Write(BinaryWriter w, int choices)
    {
        Validate(choices);
        long start = w.BaseStream.Position;
        WriteUnchecked(w);
        if (w.BaseStream.Position - start > MaxBlockBytes) throw new ArgumentException("Presentation hints exceed their byte limit.");
    }

    private void WriteUnchecked(BinaryWriter w)
    {
        w.Write((byte)Widget);
        Text(w, Label); Text(w, SectionTitle);
        w.Write(SectionOrder); w.Write(RowOrder);
        w.Write(Step != null); if (Step != null) Text(w, Step);
        Text(w, Units);
        w.Write((ushort)ChoiceLabels.Length);
        foreach (var l in ChoiceLabels) Text(w, l);
        w.Write(ShowIf != null); ShowIf?.Write(w);
        w.Write(EnableIf != null); EnableIf?.Write(w);
        w.Write((ushort)CategoryIds.Length);
        for (int i = 0; i < CategoryIds.Length; i++) { Text(w, CategoryIds[i]); Text(w, CategoryLabels[i]); }
        w.Write(AllowCategories); w.Write(MaxEntries); w.Write(MaxCount); Text(w, CountLabel);
    }

    internal static ScopedSettingPresentation Read(BinaryReader r, int choices)
    {
        long start = r.BaseStream.Position;
        var p = new ScopedSettingPresentation { Widget = (ScopedWidget)r.ReadByte() };
        p.Label = Read(r); p.SectionTitle = Read(r);
        p.SectionOrder = r.ReadInt32(); p.RowOrder = r.ReadInt32();
        p.Step = Bool(r) ? Read(r) : null;
        p.Units = Read(r);
        int labels = r.ReadUInt16();
        if (labels > ScopedSettingsWire.MaxChoices) throw new InvalidDataException("Too many choice labels.");
        p.ChoiceLabels = new string[labels];
        for (int i = 0; i < labels; i++) p.ChoiceLabels[i] = Read(r);
        p.ShowIf = Bool(r) ? SettingCondition.Read(r) : null;
        p.EnableIf = Bool(r) ? SettingCondition.Read(r) : null;
        int categories = r.ReadUInt16();
        if (categories > MaxCategories) throw new InvalidDataException("Too many categories.");
        p.CategoryIds = new string[categories];
        p.CategoryLabels = new string[categories];
        for (int i = 0; i < categories; i++) { p.CategoryIds[i] = Read(r); p.CategoryLabels[i] = Read(r); }
        p.AllowCategories = Bool(r); p.MaxEntries = r.ReadInt32(); p.MaxCount = r.ReadInt32(); p.CountLabel = Read(r);
        if (r.BaseStream.Position - start > MaxBlockBytes) throw new InvalidDataException("Presentation hints too large.");
        try { p.Validate(choices); }
        catch (ArgumentException e) { throw new InvalidDataException(e.Message); }
        return p;
    }

    internal void Validate(int choices)
    {
        if (!Enum.IsDefined(typeof(ScopedWidget), Widget)) Fail("Unknown widget.");
        Limit(Label, MaxLabelBytes); Limit(SectionTitle, MaxLabelBytes); Limit(Units, MaxUnitsBytes); Limit(CountLabel, MaxUnitsBytes);
        if (SectionOrder < -1 || RowOrder < -1) Fail("Invalid order.");
        if (Step != null && (!double.TryParse(Step, NumberStyles.Float, CultureInfo.InvariantCulture, out double step)
            || !(step > 0) || double.IsInfinity(step) || Step.Length > 32)) Fail("Invalid step.");
        if (ChoiceLabels == null || (ChoiceLabels.Length != 0 && ChoiceLabels.Length != choices)) Fail("Choice labels must match the choices.");
        foreach (var l in ChoiceLabels!) Limit(l, MaxLabelBytes);
        if (CategoryIds == null || CategoryLabels == null || CategoryIds.Length != CategoryLabels.Length || CategoryIds.Length > MaxCategories)
            Fail("Invalid categories.");
        for (int i = 0; i < CategoryIds!.Length; i++)
        {
            if (!ItemLimitList.IsCategoryId(CategoryIds[i])) Fail(ItemLimitList.CategoryRule);
            Limit(CategoryLabels![i], MaxCategoryLabelBytes);
        }
        if (MaxEntries < 0 || MaxEntries > ItemLimitList.MaxEntries || MaxCount < 0) Fail("Invalid list bounds.");
    }

    private static void Limit(string? text, int bytes)
    {
        if (text == null || Utf8.GetByteCount(text) > bytes || ServerSettings.HasDisallowedControl(text)) Fail("A presentation text is too long.");
    }

    private static void Fail(string why) => throw new ArgumentException(why);

    private static void Text(BinaryWriter w, string value)
    {
        var bytes = Utf8.GetBytes(value ?? "");
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static string Read(BinaryReader r)
    {
        int length = r.ReadUInt16();
        if (length > ScopedSettingsWire.MaxFieldBytes || length > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Invalid presentation text length.");
        return Utf8.GetString(r.ReadBytes(length));
    }

    private static bool Bool(BinaryReader r)
    {
        byte value = r.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid boolean.");
        return value == 1;
    }

    /// <summary>
    /// The largest item-list row the server can send for <paramref name="schema"/>: a full list as its value, the
    /// longest help a page can give it (the field limit), and the hints that are never dropped at their limits
    /// (control, label, section, step, units, count label, orders and bounds). A row is accepted at registration only
    /// when this fits one packet, so every later snapshot fits with at least those hints.
    /// </summary>
    internal static ScopedSettingRecord WorstCaseListRow(ScopedSettingRecord schema)
    {
        var row = ScopedSettingsSnapshot.Clone(schema);
        row.Value = new string('0', ItemLimitList.MaxEncodedBytes);
        row.Help = new string('h', ScopedSettingsWire.MaxFieldBytes);
        row.Presentation = new ScopedSettingPresentation
        {
            Widget = ScopedWidget.ItemList, Label = new string('l', MaxLabelBytes), SectionTitle = new string('s', MaxLabelBytes),
            SectionOrder = int.MaxValue, RowOrder = int.MaxValue, Step = new string('1', 32), Units = new string('u', MaxUnitsBytes),
            CountLabel = new string('c', MaxUnitsBytes), AllowCategories = true, MaxEntries = ItemLimitList.MaxEntries, MaxCount = int.MaxValue,
        };
        return row;
    }

    // ---- derivation (server; game-free) ---------------------------------------------------------------------

    /// <summary>
    /// The hints for <paramref name="row"/> from the page row that places it (<paramref name="item"/>, in section
    /// <paramref name="sectionTitle"/> at <paramref name="sectionOrder"/>, row <paramref name="rowOrder"/>), else from the
    /// catalog presentation <paramref name="catalog"/>. Either may be null. A page row wins field by field.
    /// </summary>
    internal static ScopedSettingPresentation Derive(ScopedSettingRecord row, PageItemSpec? item, string sectionTitle,
        int sectionOrder, int rowOrder, SettingPresentation? catalog) =>
        Derive(row, item?.Options, item?.Presentation, item != null, sectionTitle, sectionOrder, rowOrder, catalog);

    /// <summary>
    /// <see cref="Derive(ScopedSettingRecord, PageItemSpec, string, int, int, SettingPresentation)"/> from a row's
    /// options and page presentation (<paramref name="placed"/>: the page places the row).
    /// </summary>
    internal static ScopedSettingPresentation Derive(ScopedSettingRecord row, ModSettingOptions? options, SettingPresentation? page,
        bool placed, string sectionTitle, int sectionOrder, int rowOrder, SettingPresentation? catalog)
    {
        var p = new ScopedSettingPresentation();
        p.Label = Clip(options?.Label ?? catalog?.Label ?? "", MaxLabelBytes);
        p.Widget = WidgetOf(options?.Control) is var w && w != ScopedWidget.Default ? w
            : WidgetOf(page?.EditorHint) is var h && h != ScopedWidget.Default ? h : WidgetOf(catalog?.EditorHint);
        p.Step = ValidStep(page?.Step ?? catalog?.Step);
        p.Units = Clip(page?.Units ?? catalog?.Units ?? "", MaxUnitsBytes);
        p.ChoiceLabels = page?.ChoiceLabels != null
            ? LabelsFor(row.Choices, page.Choices, page.ChoiceLabels)
            : LabelsFor(row.Choices, catalog?.Choices, catalog?.ChoiceLabels);
        if (placed)
        {
            p.SectionTitle = Clip(sectionTitle ?? "", MaxLabelBytes);
            p.SectionOrder = sectionOrder < 0 ? -1 : sectionOrder;
            p.RowOrder = rowOrder < 0 ? -1 : rowOrder;
            p.ShowIf = options?.ShowWhen;
            p.EnableIf = options?.EnableWhen;
        }
        else if (catalog != null)
        {
            p.SectionTitle = Clip(catalog.Group ?? "", MaxLabelBytes);
            if (catalog.Order != 0) p.RowOrder = Math.Max(0, catalog.Order);
        }
        return p;
    }

    /// <summary>The help a published row carries: the page's or catalog's help line, else the cfg description.</summary>
    internal static string HelpFor(string description, PageItemSpec? item, SettingPresentation? catalog) =>
        item?.Options.Help ?? catalog?.Help ?? description ?? "";

    internal static ScopedWidget WidgetOf(SettingControl? control) => control switch
    {
        SettingControl.Toggle => ScopedWidget.Toggle,
        SettingControl.Slider => ScopedWidget.Slider,
        SettingControl.Radio or SettingControl.Segmented => ScopedWidget.Radio,
        SettingControl.Dropdown => ScopedWidget.Dropdown,
        SettingControl.Number => ScopedWidget.Number,
        SettingControl.Text => ScopedWidget.Text,
        SettingControl.TextArea => ScopedWidget.TextArea,
        _ => ScopedWidget.Default,
    };

    private static ScopedWidget WidgetOf(SettingsEditorHint? hint) => hint switch
    {
        SettingsEditorHint.Toggle => ScopedWidget.Toggle,
        SettingsEditorHint.Slider => ScopedWidget.Slider,
        SettingsEditorHint.Dropdown => ScopedWidget.Dropdown,
        SettingsEditorHint.Numeric => ScopedWidget.Number,
        SettingsEditorHint.Text => ScopedWidget.Text,
        _ => ScopedWidget.Default,
    };

    /// <summary>
    /// Display words for <paramref name="rowChoices"/>: <paramref name="labels"/> aligned with <paramref name="authored"/>
    /// (the choices the author listed; null: the row's own order). A choice the author does not label keeps its value.
    /// </summary>
    internal static string[] LabelsFor(string[] rowChoices, string[]? authored, string[]? labels)
    {
        if (labels == null || labels.Length == 0 || rowChoices.Length == 0) return Array.Empty<string>();
        var result = new string[rowChoices.Length];
        bool any = false;
        for (int i = 0; i < rowChoices.Length; i++)
        {
            int at = authored == null ? i : Array.FindIndex(authored, c => string.Equals(c, rowChoices[i], StringComparison.OrdinalIgnoreCase));
            string label = at >= 0 && at < labels.Length && !string.IsNullOrWhiteSpace(labels[at]) ? labels[at].Trim() : rowChoices[i];
            result[i] = Clip(label, MaxLabelBytes);
            any |= at >= 0 && at < labels.Length;
        }
        return any ? result : Array.Empty<string>();
    }

    private static string? ValidStep(string? step) =>
        step != null && double.TryParse(step, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0
            && !double.IsInfinity(s) && step.Length <= 32 ? step : null;

    internal static string Clip(string text, int bytes)
    {
        text = (text ?? "").Trim();
        if (Utf8.GetByteCount(text) <= bytes && !ServerSettings.HasDisallowedControl(text)) return text;
        var sb = new StringBuilder();
        int used = 0;
        foreach (char c in text)
        {
            if (char.IsControl(c)) continue;
            int n = Utf8.GetByteCount(c.ToString());
            if (char.IsSurrogate(c) || used + n > bytes) break;
            sb.Append(c); used += n;
        }
        return sb.ToString();
    }
}
