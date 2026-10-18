#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.UI;
using Stunlock.Core;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Progenitor.Ui;

internal static partial class BuildMenuSearch
{
    private static readonly BuildRecentHistory Recent = new();
    private static readonly BuildMenuRecentTab RecentTab = new();
    private static readonly Dictionary<IntPtr, (SimpleStunButton Button, bool Enabled, bool Select, bool Highlight, bool SelectDisabled)> SearchTabs = new();
    private static readonly Dictionary<IntPtr, (CanvasGroup Group, float Alpha, bool Interactable, bool Owned)> SearchTabFades = new();
    private static readonly Dictionary<IntPtr, (UnityEngine.UI.Graphic Graphic, bool Enabled)> SearchHighlights = new();
    private static readonly Dictionary<IntPtr, (Behaviour Entry, bool Enabled)> SearchEntries = new();
    private static ConfigEntry<int>? _recentCount;
    private static ConfigEntry<string>? _recentSaved;
    private static IKeybindHandle? _copyBind;
    private static bool _recentSelected;
    private static string _recentEmptyKey = "", _recentQueryKey = "";
    private static int _recentEmptyUntil;
    private static int _recentCategory = -1, _recentTag = -1;
    private static int _copyGuid, _copyGroup, _copyFrames;
    private static bool _copyDispatched;
    private static World? _toolsWorld;
    private static TMPro.TextMeshProUGUI? _scopeHint;

    private static void InstallBuildTools(ConfigFile file)
    {
        _recentCount = file.Bind(Section, "RecentCount", BuildRecentHistory.DefaultCount,
            new ConfigDescription("Number of distinct recently built items to remember.", new AcceptableValueRange<int>(1, BuildRecentHistory.MaximumCount)));
        SettingLeases.Add(SettingsCatalog.RegisterSetting(SettingsPanel.PluginId, _recentCount, new SettingPresentation
        {
            Label = "Recent buildings", Help = "Remember this many distinct built items, newest first.",
            Group = SettingsGroup, Order = 40, CommitPolicy = SettingCommitPolicy.Immediate,
        }, typeof(BuildMenuSearch)));
        // History is data, not an editable setting on the Mods page.
        var history = new ConfigFile(Path.Combine(BepInEx.Paths.ConfigPath, "progenitor.build-history.cfg"), false);
        _recentSaved = history.Bind("History", "Blueprints", "", "Recent blueprint identities, newest first.");
        Recent.Load(_recentSaved.Value, _recentCount.Value);
        _copyBind = Keybinds.RegisterScoped(typeof(BuildMenuSearch), SettingsPanel.PluginId, "CopyBuilding", file,
            new KeyChord(UiKey.MouseMiddle), "Select the building under the cursor to build another, while the build menu is open.",
            CopyHoveredBuilding, null, null, false, false, KeybindScope.Build);
    }

    internal static void RecordBuilt(int guid)
    {
        if (!Recent.Record(guid, _recentCount?.Value ?? BuildRecentHistory.DefaultCount)) return;
        SaveRecent();
    }

    private static void SaveRecent()
    {
        if (_recentSaved == null) return;
        _recentSaved.Value = Recent.Save();
        _recentSaved.ConfigFile.Save();
    }

    private static List<BuildMenuBlob.ResultCell> RecentCells(World world, BuildMenuIndex index, out int matched)
    {
        var cells = Recent.Cells(index.Blob, guid => _words.Length == 0 ||
            BuildSearchMatch.Matches(_words, NameFor(world, new PrefabGUID(guid), out _)));
        matched = cells.Count;
        return cells;
    }

    private static void ChooseRecent()
    {
        if (BuildSearchMatch.QueryChars(_words) > 0) return;
        var mapper = _toolsWorld?.GetExistingSystemManaged<BuildMenuMapper>();
        if (mapper == null || mapper._IsInRelocationBuildMenu) return;
        RestoreResults("recent tab");
        _jumpCategory = _jumpTag = -1;
        _recentSelected = true;
        _recentCategory = mapper._SelectedCategoryIndex;
        _recentTag = mapper._SelectedTagIndex;
        _emptyResultsKey = "";
        _recentEmptyKey = "";
        _group?.ResetState();
        if (_field != null) UnityUiRuntime.BlurExternalField(_field);
    }

    internal static Dictionary<string, object?> ShowRecentForTest()
    {
        if (!_open) throw new InvalidOperationException("Open the build menu first.");
        ChooseRecent();
        return Describe(40);
    }

    private static void LeaveRecent()
    {
        if (!_recentSelected) return;
        _recentSelected = false;
        _jumpCategory = _jumpTag = -1;
        _recentCategory = _recentTag = -1;
        _recentEmptyKey = _recentQueryKey = "";
        RestoreResults("leave recent");
        RestoreHidden();
    }

    private static void UpdateBuildTools(World world, BuildMenuMapper mapper, BuildMenu menu)
    {
        if (!ReferenceEquals(world, _toolsWorld)) { _copyGuid = 0; _toolsWorld = world; }
        if (Recent.Trim(_recentCount?.Value ?? BuildRecentHistory.DefaultCount)) SaveRecent();
        if (!mapper._IsInRelocationBuildMenu)
            RecentTab.Update(menu, _recentSelected, ChooseRecent, LeaveRecent);
        if (_copyBind != null && _copyBind.Chord.Key != UiKey.None && !mapper._IsInRelocationBuildMenu)
            BuildMenuCopyHint.Show(menu, _copyBind.Chord, world);
        else BuildMenuCopyHint.Hide();
        bool allSearch = BuildSearchMatch.QueryChars(_words) > 0;
        SetSearchTabs(menu, allSearch);
        if (_scopeHint == null && _root != null)
        {
            _scopeHint = BuildCountLabel(_root);
            if (_scopeHint != null)
            {
                _scopeHint.gameObject.name = "search-scope";
                var rt = _scopeHint.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
                rt.pivot = new Vector2(0, 1);
                rt.sizeDelta = new Vector2(FieldWidth + CountWidth, 14);
                rt.anchoredPosition = Vector2.zero;
                _scopeHint.fontSize = 12;
            }
        }
        if (_scopeHint != null) _scopeHint.text = allSearch ? "Clear search to browse categories" : "";
        if (_recentSelected && _group != null)
        {
            // Native cells retain their data/index; only their visual sibling order follows recency.
            int sibling = 0;
            foreach (int guid in Recent.Items)
                for (int i = 0; i < Math.Min(_group.DataCount, _group.Entries.Count); i++)
                    if (_group.Entries[i].Primary.PrefabGuid.GuidHash == guid)
                        _group.Entries[i].transform.SetSiblingIndex(sibling++);
        }
        FinishCopySelection();
    }

    private static void SetSearchTabs(BuildMenu menu, bool disabled)
    {
        RecentTab.SetSearchDisabled(disabled);
        if (!disabled) { RestoreSearchTabs(); return; }
        var categories = menu.CategorySelectionGroup;
        var tags = menu.TagSelectionGroup;
        if (categories != null)
            for (int i = 0; i < categories.Entries.Count; i++)
            {
                DisableSearchTab(categories.Entries[i].Button);
                DisableSearchEntry(categories.Entries[i]);
                var highlight = categories.Entries[i].Highlight;
                if (Alive(highlight))
                {
                    if (!SearchHighlights.ContainsKey(highlight.Pointer)) SearchHighlights[highlight.Pointer] = (highlight, highlight.enabled);
                    highlight.enabled = false;
                }
            }
        if (tags != null)
            for (int i = 0; i < tags.Entries.Count; i++)
            {
                DisableSearchTab(tags.Entries[i].GetComponent<SimpleStunButton>());
                DisableSearchEntry(tags.Entries[i]);
                var highlight = tags.Entries[i].Highlight;
                if (Alive(highlight))
                {
                    if (!SearchHighlights.ContainsKey(highlight.Pointer)) SearchHighlights[highlight.Pointer] = (highlight, highlight.enabled);
                    highlight.enabled = false;
                }
            }
        _resultsNote = "Clear search to browse categories";
    }

    private static void DisableSearchTab(SimpleStunButton? button)
    {
        if (!Alive(button)) return;
        if (!SearchTabs.ContainsKey(button!.Pointer)) SearchTabs[button.Pointer] = (button, button.interactable, button.ForceSelect, button.ForceHighlight, button.SelectableWhenNotInteractable);
        button.ForceSelect = false;
        button.ForceHighlight = false;
        button.SelectableWhenNotInteractable = false;
        button.interactable = false;
        var events = EventSystem.current;
        var selected = events?.currentSelectedGameObject;
        if (Alive(selected) && (selected!.Pointer == button.gameObject.Pointer || selected.transform.IsChildOf(button.transform)))
            events!.SetSelectedGameObject(null);
    }

    private static void RestoreSearchTabs()
    {
        bool hadTabs = SearchTabs.Count > 0;
        foreach (var old in SearchTabs.Values) if (Alive(old.Button))
        {
            old.Button.SelectableWhenNotInteractable = old.SelectDisabled;
            old.Button.ForceSelect = old.Select;
            old.Button.ForceHighlight = old.Highlight;
            old.Button.interactable = old.Enabled;
        }
        SearchTabs.Clear();
        foreach (var old in SearchHighlights.Values) if (Alive(old.Graphic)) old.Graphic.enabled = old.Enabled;
        SearchHighlights.Clear();
        foreach (var old in SearchTabFades.Values) if (Alive(old.Group))
        {
            old.Group.alpha = old.Alpha;
            old.Group.interactable = old.Interactable;
            if (old.Owned) UnityEngine.Object.Destroy(old.Group);
        }
        SearchTabFades.Clear();
        foreach (var old in SearchEntries.Values) if (Alive(old.Entry)) old.Entry.enabled = old.Enabled;
        SearchEntries.Clear();
        if (hadTabs && Alive(_menu) && _toolsWorld?.IsCreated == true)
        {
            var mapper = _toolsWorld.GetExistingSystemManaged<BuildMenuMapper>();
            if (mapper != null)
            {
                var categories = _menu!.CategorySelectionGroup?.Entries;
                if (categories != null) for (int i = 0; i < categories.Count; i++)
                    if (Alive(categories[i].Button)) categories[i].Button.ForceSelect = !_recentSelected && categories[i].Index == mapper._SelectedCategoryIndex;
                var tags = _menu.TagSelectionGroup?.Entries;
                if (tags != null) for (int i = 0; i < tags.Count; i++)
                    if (Alive(tags[i].Button)) tags[i].Button.ForceSelect = !_recentSelected && tags[i].Index == mapper._SelectedTagIndex;
            }
        }
    }

    private static void DisableSearchEntry(Behaviour entry)
    {
        if (!Alive(entry)) return;
        if (!SearchEntries.ContainsKey(entry.Pointer)) SearchEntries[entry.Pointer] = (entry, entry.enabled);
        entry.enabled = false;
        if (!SearchTabFades.TryGetValue(entry.Pointer, out var fade))
        {
            var group = entry.GetComponent<CanvasGroup>();
            bool owned = !Alive(group);
            if (owned) group = entry.gameObject.AddComponent<CanvasGroup>();
            fade = (group, group.alpha, group.interactable, owned);
            SearchTabFades[entry.Pointer] = fade;
        }
        fade.Group.alpha = fade.Alpha * 0.45f;
        fade.Group.interactable = false;
    }

    private static void CopyHoveredBuilding()
    {
        if (!Enabled || !_open || UnityUiRuntime.IsExternalFieldFocused(_field)) return;
        var world = _toolsWorld;
        if (world == null || !world.IsCreated) return;
        var mapper = world.GetExistingSystemManaged<BuildMenuMapper>();
        if (mapper == null || mapper._IsInRelocationBuildMenu || !mapper._IsWorldInteractionAllowed) return;
        var em = world.EntityManager;
        if (!mapper._BuildModeEntity.TryRead<HoverTileModelSelection>(em, out var hover)) return;
        var target = hover.Target;
        if (!em.Exists(target) || !target.TryRead<PrefabGUID>(em, out var prefab)) return;
        if (target.TryRead<RebuildBlueprintMapping>(em, out var mapping) && mapping.Prefab.GuidHash != 0)
            prefab = mapping.Prefab;
        var index = BuildMenuIndex.For(world, mapper);
        if (index == null || !index.Places.TryFind(prefab.GuidHash, mapper._SelectedCategoryIndex, mapper._SelectedTagIndex, out var place)) return;
        // Use the ordinary menu entry: unavailable blueprints stay unavailable and payment remains vanilla.
        LeaveRecent();
        RestoreResults("copy building");
        if (_field != null) { UnityUiRuntime.SetExternalFieldText(_field, ""); UnityUiRuntime.BlurExternalField(_field); }
        _words = Array.Empty<string>();
        RestoreSearchTabs();
        mapper.SetSelectedCategory(place.Category);
        mapper.SetSelectedTag(place.Tag, 0);
        _copyGuid = prefab.GuidHash;
        _copyGroup = place.InGroup ? place.GroupGuid : 0;
        _copyFrames = 8;
        _copyDispatched = false;
    }

    private static void FinishCopySelection()
    {
        if (_copyGuid == 0 || _group == null) return;
        if (_copyDispatched)
        {
            var selected = _group.SelectedStructure;
            if (Alive(selected) && selected.PrefabGuid.GuidHash == _copyGuid)
            {
                _copyGuid = 0;
                _copyDispatched = false;
                return;
            }
            if (--_copyFrames <= 0) { _copyGuid = 0; _copyDispatched = false; }
            return;
        }
        for (int i = 0; i < Math.Min(_group.DataCount, _group.Entries.Count); i++)
        {
            var top = _group.Entries[i];
            if (TrySelectCopied(top.Primary)) return;
            var subs = top.SubEntries;
            for (int j = 0; subs != null && j < subs.Count; j++) if (TrySelectCopied(subs[j])) return;
            if (_copyGroup != 0 && Alive(top.Primary) && top.Primary.PrefabGuid.GuidHash == _copyGroup
                && !top.Primary.IsDisabled && !top.Primary.IsDisabledInBuildMenu && !top.Primary.IsLoading)
            {
                ClickBuildEntry(top.Primary);
                _copyGroup = 0;
                return;
            }
        }
        if (--_copyFrames <= 0) _copyGuid = 0;
    }

    private static bool TrySelectCopied(BuildMenu_StructureEntry? entry)
    {
        if (!Alive(entry) || entry!.PrefabGuid.GuidHash != _copyGuid || entry.IsDisabled || entry.IsDisabledInBuildMenu || entry.IsLoading) return false;
        if (!entry.gameObject.activeInHierarchy) return false;
        ClickBuildEntry(entry);
        _copyDispatched = true;
        return true;
    }

    private static void ClickBuildEntry(BuildMenu_StructureEntry entry)
    {
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        if (entry.SelectOnMouseDown) entry.OnPointerDown(pointer); else entry.OnPointerClick(pointer);
    }

    private static void CloseBuildTools()
    {
        RestoreSearchTabs();
        RecentTab.Dispose();
        BuildMenuCopyHint.Release();
        _recentSelected = false;
        _recentCategory = _recentTag = -1;
        _recentEmptyKey = _recentQueryKey = "";
        _copyGuid = 0;
        _copyDispatched = false;
        _toolsWorld = null;
        _scopeHint = null;
    }

    private static void ReleaseBuildTools()
    {
        CloseBuildTools();
        _copyBind?.Dispose();
        _copyBind = null;
        _recentCount = null;
        _recentSaved = null;
    }
}
#endif
