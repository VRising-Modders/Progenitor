using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Items;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// Scoped settings: an item-list row (<see cref="AdminSettingKind.ItemList"/>). The row shows the list's size and
/// <b>Edit</b>; Edit opens a game window with the list (item or category, the count, remove) and <b>+ Add…</b>, which
/// opens the game-look item picker (the kit checklist: search, an Items / Categories filter, icons). Every change sends
/// the whole list to the server, which checks it against its own rules; the window always shows the server's list.
/// </summary>
internal sealed partial class ModSettingsView
{
    internal const string ItemListEditCaption = "Edit";
    internal const string ItemListAddCaption = "+ Add…";
    internal const string ItemListEmptyText = "Nothing listed yet. Use Add to choose items.";

    /// <summary>The item catalog the picker lists (this side's world). A seam: tests supply their own.</summary>
    internal static Func<IReadOnlyList<ItemCatalogEntry>> ItemCatalog = DefaultItemCatalog;

    private readonly Dictionary<string, ItemListEditor> _itemLists = new(StringComparer.Ordinal);

    private static IReadOnlyList<ItemCatalogEntry> DefaultItemCatalog()
    {
#if PROGENITOR_TESTS
        return Array.Empty<ItemCatalogEntry>();
#else
        try { return UnityItemCatalogSource.Cache.Entries(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: item catalog for an item-list setting threw: " + e.Message);
            return Array.Empty<ItemCatalogEntry>();
        }
#endif
    }

    /// <summary>The words a read-only or summary line uses for a stored list.</summary>
    internal static string ItemListSummary(string? value)
    {
        if (!ItemLimitList.TryParse(value, out var list, out _)) return "Not readable";
        if (list.Count == 0) return "Nothing listed";
        int categories = 0;
        foreach (var line in list) if (line.IsCategory) categories++;
        int items = list.Count - categories;
        var parts = new List<string>(2);
        if (items > 0) parts.Add(items.ToString(CultureInfo.InvariantCulture) + (items == 1 ? " item" : " items"));
        if (categories > 0) parts.Add(categories.ToString(CultureInfo.InvariantCulture) + (categories == 1 ? " category" : " categories"));
        return string.Join(", ", parts) + " listed";
    }

    private IUiHandle AddItemListRow(IUiPanel panel, ItemBinding binding, AdminSettingSpec spec, string caption)
    {
        var button = panel.NamedButton(caption, ItemListEditCaption);
        UseGameButton(binding, button);
        // A rebuild of the same page keeps an open list window and picker.
        ItemListEditor editor;
        if (_itemLists.TryGetValue(spec.SettingId, out var previous))
        {
            editor = previous;
            editor.Rebind(binding, spec);
        }
        else _itemLists[spec.SettingId] = editor = new ItemListEditor(this, binding, spec);
        button.OnClick(() =>
        {
            if (!binding.Enabled)
            {
                Plugin.Logger?.LogInfo("Ui: settings " + spec.SettingId + " refused: " + binding.Why);
                return;
            }
            editor.Open();
        });
        binding.Editor = "itemlist";
        binding.Apply = editor.Open;
        binding.Sync = () => SyncAdmin(binding, spec, editor.Show);
        return button;
    }

    /// <summary>The open editor of item-list setting <paramref name="settingId"/> (tests and the bridge).</summary>
    internal ItemListEditor? ItemListFor(string settingId) => _itemLists.TryGetValue(settingId, out var e) ? e : null;

    private void AddItemListReadback(Dictionary<string, object?> d, ItemBinding binding, AdminSettingSpec spec)
    {
        d["summary"] = ItemListSummary(binding.ServerShown);
        if (ItemLimitList.TryParse(binding.ServerShown, out var list, out _))
        {
            var lines = new List<object?>(list.Count);
            foreach (var line in list) lines.Add(new Dictionary<string, object?> { ["key"] = line.Key, ["max"] = line.Max });
            d["lines"] = lines;
        }
        var hints = spec.ListHints;
        d["maxEntries"] = hints?.MaxEntries ?? ItemLimitList.MaxEntries;
        d["maxCount"] = hints?.MaxCount ?? 0;
        d["allowCategories"] = hints?.AllowCategories ?? false;
        d["editorOpen"] = ItemListFor(spec.SettingId)?.IsOpen ?? false;
    }

    private void ReleaseItemLists()
    {
        foreach (var editor in _itemLists.Values)
        {
            try { editor.Close(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: item list editor close threw: " + e.Message); }
        }
        _itemLists.Clear();
    }

    /// <summary>One item-list row's editor window and picker. Built on the first Open; closed with the page.</summary>
    internal sealed class ItemListEditor
    {
        private readonly ModSettingsView _view;
        private ItemBinding _binding;
        private AdminSettingSpec _spec;
        private readonly List<Func<ItemLimitList, ItemLimitList>> _queued = new();
        private IUiPanel? _window;
        private IUiPanel? _pickerWindow;
        private IUiDataTable? _table;
        private IUiLabel? _status;
        private IUiChecklist? _picker;
        private readonly Dictionary<int, ItemCatalogEntry> _byGuid = new();

        internal ItemListEditor(ModSettingsView view, ItemBinding binding, AdminSettingSpec spec)
        {
            _view = view;
            _binding = binding;
            _spec = spec;
        }

        /// <summary>The page drew the row again (a rebuild of the same page): keep the open window and picker, take the new row.</summary>
        internal void Rebind(ItemBinding binding, AdminSettingSpec spec)
        {
            _binding = binding;
            _spec = spec;
            Show(binding.ServerShown);
        }

        internal bool IsOpen => _window is { IsDisposed: false } w && w.Visible;

        internal bool PickerOpen => _pickerWindow is { IsDisposed: false } w && w.Visible;

        private ScopedSettingPresentation? Hints => _spec.ListHints;

        // The server's MaxCount as sent (0 allows only 0, as on the server); no hints: the server alone decides.
        private int MaxCount => Hints is { } h ? h.MaxCount : int.MaxValue;

        private string CountLabel => Hints is { CountLabel.Length: > 0 } h ? h.CountLabel : "Most";

        /// <summary>The list the server holds now (empty while it has not said or the text is unreadable).</summary>
        internal ItemLimitList Current =>
            ItemLimitList.TryParse(_binding.ServerShown ?? _spec.SafeRead(), out var list, out _) ? list : ItemLimitList.Empty;

        internal void Open()
        {
            var window = Window();
            Show(_binding.ServerShown);
            Ui.Show(window);
        }

        internal void Close()
        {
            var window = _window; var picker = _pickerWindow;
            _window = null; _pickerWindow = null; _table = null; _status = null; _picker = null;
            picker?.Dispose();
            window?.Dispose();
        }

        private IUiPanel Window()
        {
            if (_window is { IsDisposed: false } open) return open;
            var window = Ui.Panel(_spec.Label, _view, _spec.PluginId).GameWindow(720);
            window.CaptureMode = UiCaptureMode.WhenHovered;
            if (_spec.Help.Length > 0) window.Label(_spec.Help);
            _status = window.Label("");
            var add = window.Button(ItemListAddCaption);
            add.GameButton();
            add.OnClick(OpenPicker);
            _table = window.DataTable(_spec.SettingId + ":list", new[]
            {
                new UiTableColumn("name", "Item or category") { Hideable = false },
                new UiTableColumn("type", "Type") { Width = 90 },
                new UiTableColumn("max", CountLabel, UiTableColumnKind.Number) { Editable = true, Format = "#,0", Width = 110 },
                new UiTableColumn("actions", "", UiTableColumnKind.Actions) { ActionSlots = 1 },
            }, new UiDataTableOptions
            {
                VisibleRows = 10, MinRows = 5, Selectable = false, ColumnChooser = false, TotalRow = false,
                Sort = new[] { new UiTableSortKey("name", true) },
                EmptyText = ItemListEmptyText,
            });
            _table.OnRowAction((id, action) =>
            {
                if (action == "remove") Apply(list => list.Without(id));
            });
            _table.OnEdit(edit =>
            {
                if (edit.NewValue is not { } v || v < 0 || v > MaxCount || v != Math.Floor(v))
                {
                    edit.Reject("Type a whole number from 0 to " + MaxCount.ToString("#,0", CultureInfo.InvariantCulture) + ".");
                    return;
                }
                if (Apply(list => WithCount(list, edit.RowId, (int)v))) edit.Resolve();
                else edit.Reject(_binding.Status.Length > 0 ? _binding.Status : "Not sent.");
            });
            _window = window;
            return window;
        }

        /// <summary><paramref name="list"/> with the line <paramref name="key"/> holding <paramref name="count"/> (unchanged when it is gone).</summary>
        internal static ItemLimitList WithCount(ItemLimitList list, string key, int count)
        {
            var lines = new List<ItemLimit>(list);
            int at = lines.FindIndex(l => l.Key == key);
            if (at < 0) return list;
            var line = lines[at];
            lines[at] = line.IsCategory ? ItemLimit.ForCategory(line.Category, count) : ItemLimit.ForItem(line.Item, count);
            return ItemLimitList.Unchecked(lines);
        }

        /// <summary>Redraw from <paramref name="shown"/> (the server's text).</summary>
        internal void Show(string? shown)
        {
            if (_table == null || _status == null) return;
            if (!ItemLimitList.TryParse(shown, out var list, out var error))
            {
                _status.Text = "The server's list could not be read: " + error;
                list = ItemLimitList.Empty;
            }
            else _status.Text = _binding.Status;
            var rows = new List<UiTableRow>(list.Count);
            foreach (var line in list)
            {
                rows.Add(new UiTableRow(line.Key) { ItemPrefab = line.IsCategory ? 0 : line.Item }
                    .Text("name", NameOf(line))
                    .Text("type", line.IsCategory ? "Category" : "Item")
                    .Number("max", line.Max)
                    .Action("remove", UiRowActionIcon.Remove, "Take it off the list", _binding.Enabled));
            }
            _table.SetRows(rows);
        }

        internal string NameOf(ItemLimit line)
        {
            if (line.IsCategory)
            {
                var hints = Hints;
                if (hints != null)
                    for (int i = 0; i < hints.CategoryIds.Length; i++)
                        if (string.Equals(hints.CategoryIds[i], line.Category, StringComparison.OrdinalIgnoreCase)) return hints.CategoryLabels[i];
                return line.Category;
            }
            if (_byGuid.Count == 0) Index();
            return _byGuid.TryGetValue(line.Item, out var entry) && !string.IsNullOrEmpty(entry.DisplayName)
                ? entry.DisplayName! : "Item " + line.Item.ToString(CultureInfo.InvariantCulture);
        }

        private void Index()
        {
            _byGuid.Clear();
            IReadOnlyList<ItemCatalogEntry> catalog;
            try { catalog = ItemCatalog(); }
            catch { catalog = Array.Empty<ItemCatalogEntry>(); }
            foreach (var e in catalog) _byGuid[e.PrefabGuid] = e;
        }

        /// <summary>
        /// The picker's entries: the categories the server offers, then every named item (only the ones that stack when
        /// the server sent <see cref="ItemListRules.StackableOnly"/>), the listed ones grayed.
        /// </summary>
        internal List<UiChecklistEntry> PickerEntries()
        {
            Index();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in Current) listed.Add(line.Key);
            var entries = new List<UiChecklistEntry>();
            var hints = Hints;
            if (hints is { AllowCategories: true })
                for (int i = 0; i < hints.CategoryIds.Length; i++)
                {
                    string key = "c:" + hints.CategoryIds[i].ToLowerInvariant();
                    entries.Add(new UiChecklistEntry(key, hints.CategoryLabels[i])
                    {
                        Filters = new[] { "categories" },
                        Keywords = new[] { "category" },
                        DisabledReason = listed.Contains(key) ? "Already listed" : "",
                    }.Text("type", "Category"));
                }
            bool stackableOnly = hints?.StackableOnly ?? false;
            foreach (var e in _byGuid.Values)
            {
                if (e.DisplayName is not { Length: > 0 } name) continue;
                if (!ItemListRules.Offers(stackableOnly, e.MaxAmount)) continue;
                string key = "i:" + e.PrefabGuid.ToString(CultureInfo.InvariantCulture);
                entries.Add(new UiChecklistEntry(key, name)
                {
                    ItemPrefab = e.PrefabGuid,
                    Filters = new[] { "items" },
                    DisabledReason = listed.Contains(key) ? "Already listed" : "",
                }.Text("type", "Item"));
            }
            entries.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        /// <summary>The "+ Add…" button: open (or bring back) the picker over the list window.</summary>
        internal void OpenPicker()
        {
            if (!_binding.Enabled || _spec.ViewOnlyNow()) return;
            if (_pickerWindow is not { IsDisposed: false })
            {
                var window = Ui.Panel("Add to " + _spec.Label, _view, _spec.PluginId).GameWindow(900);
                window.CaptureMode = UiCaptureMode.WhenHovered;
                var filters = new List<UiChecklistFilter> { new("items", "Items") };
                if (Hints is { AllowCategories: true, CategoryIds.Length: > 0 }) filters.Add(new UiChecklistFilter("categories", "Categories"));
                _picker = window.Checklist(_spec.SettingId + ":pick", Array.Empty<UiChecklistEntry>(), new UiChecklistOptions
                {
                    Placeholder = "Search items",
                    Columns = new[] { new UiTableColumn("type", "Type") { Width = 130 } },
                    Filters = filters,
                    AllFilterLabel = "Everything",
                    ConfirmFormat = "Add {0}",
                    FillHeight = true,
                    EmptyText = "Nothing matches.",
                });
                _picker.OnConfirmed(picked =>
                {
                    Ui.Hide(window);
                    Add(picked);
                });
                _picker.OnCancelled(() => Ui.Hide(window));
                _pickerWindow = window;
            }
            _picker!.SetSelected(Array.Empty<string>());
            _picker.Query = "";
            _picker.SetEntries(PickerEntries());
            Ui.Show(_pickerWindow!);
        }

        /// <summary>Add the picked keys (<c>i:&lt;guid&gt;</c>, <c>c:&lt;category&gt;</c>) with a count of 0, and send the list.</summary>
        internal bool Add(IEnumerable<string> keys)
        {
            var picked = new List<string>(keys);
            if (_window is { IsDisposed: false } w) Ui.Show(w);
            return Apply(current =>
            {
                var lines = new List<ItemLimit>(current);
                var have = new HashSet<string>(StringComparer.Ordinal);
                foreach (var l in lines) have.Add(l.Key);
                foreach (var key in picked)
                {
                    if (have.Contains(key)) continue;
                    if (key.StartsWith("i:", StringComparison.Ordinal)
                        && int.TryParse(key.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int guid) && guid != 0)
                        lines.Add(ItemLimit.ForItem(guid, 0));
                    else if (key.StartsWith("c:", StringComparison.Ordinal) && ItemLimitList.IsCategoryId(key.Substring(2)))
                        lines.Add(ItemLimit.ForCategory(key.Substring(2), 0));
                    else continue;
                    have.Add(key);
                }
                return ItemLimitList.Unchecked(lines);
            });
        }

        /// <summary>Send <paramref name="next"/> as the whole list (see <see cref="Apply"/>).</summary>
        internal bool Commit(ItemLimitList next) => Apply(_ => next);

        /// <summary>Changes waiting for the server's answer to the change before them.</summary>
        internal int QueuedCount => _queued.Count;

        internal const string QueuedNote = "Waiting for the server; your change is sent next.";

        /// <summary>
        /// Apply <paramref name="change"/> to the server's list and send the result. While an earlier change waits for
        /// the server, it is queued and applied to the list the server answers with, so no pick is lost. False when
        /// nothing was sent or queued: grayed, unchanged, or refused by the rules the server sent (the reason is shown).
        /// </summary>
        internal bool Apply(Func<ItemLimitList, ItemLimitList> change)
        {
            if (!_binding.Enabled || _spec.ViewOnlyNow())
            {
                Show(_binding.ServerShown);
                return false;
            }
            if (_binding.RequestPending)
            {
                _queued.Add(change);
                Say(QueuedNote);
                return true;
            }
            var current = Current;
            var next = change(current);
            if ((next.Check(Rules) ?? Rules.CheckStackable(next, current, StackSizeHere)) is { } problem)
            {
                Say(problem);
                return false;
            }
            string text = next.ToString();
            if (string.Equals(text, _binding.ServerShown ?? "", StringComparison.Ordinal))
            {
                Show(_binding.ServerShown);
                return false;
            }
            _view.SendAdmin(_binding, _spec, text, Answered);
            return true;
        }

        /// <summary>The server answered (or the request ended): redraw, then send what was queued meanwhile, together.</summary>
        private void Answered(string? shown)
        {
            Show(shown);
            if (_binding.RequestPending || _queued.Count == 0) return;
            var waiting = new List<Func<ItemLimitList, ItemLimitList>>(_queued);
            _queued.Clear();
            Apply(list =>
            {
                foreach (var f in waiting) list = f(list);
                return list;
            });
        }

        private void Say(string text)
        {
            _binding.Status = text;
            _view.WriteHelp(_binding);
            if (_status != null) _status.Text = text;
        }

        /// <summary>The rules the server sent (its own copy decides; these only spare a request it would refuse).</summary>
        private ItemListRules Rules => new()
        {
            MaxEntries = Hints is { MaxEntries: > 0 } h ? h.MaxEntries : ItemLimitList.MaxEntries,
            MaxCount = MaxCount,
            AllowCategories = Hints?.AllowCategories ?? true,
            StackableOnly = Hints?.StackableOnly ?? false,
        };

        /// <summary>A prefab's stack size in this client's catalog (the one the picker lists), null when it is not there.</summary>
        private int? StackSizeHere(int prefabGuid)
        {
            if (_byGuid.Count == 0) Index();
            return _byGuid.TryGetValue(prefabGuid, out var entry) ? entry.MaxAmount : null;
        }
    }
}
