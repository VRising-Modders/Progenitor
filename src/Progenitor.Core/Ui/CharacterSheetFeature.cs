#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Networking;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;
using Loc = Progenitor.Localization.Localization;

namespace Progenitor.Ui;

/// <summary>Native sheet rows and tooltips, populated with own-player effective server values.</summary>
internal static class CharacterSheetFeature
{
    private static IDisposable? _hook;
    private static InventorySubMenu? _menu;
    private static AttributeSettings? _original, _settings;
    private static readonly List<(CharacterAttributeSection Section, int Id)> Added = new();
    private static readonly List<IDisposable> Text = new();
    private static readonly Dictionary<(int, int, int, int), int> Keys = new();
    private static readonly Dictionary<int, Stunlock.Localization.LocalizationKey> Tooltips = new();
    private static double _lastSeen, _lastError;
    private static bool _open;
    private static bool _layoutPending;
    private static Entity _character;
    private static World? _characterWorld;
    private static double _characterRetry;

    internal static void Install()
    {
        CharacterSheetChannel.Install();
        if (Plugin.IsServerProcess || _hook != null) return;
        _hook = SystemHooks.After<InventorySubMenuMapper>(Update);
        Plugin.Frame += Tick;
        Handshake.ServerContextCleared += ContextCleared;
    }

    internal static void Uninstall()
    {
        _hook?.Dispose(); _hook = null;
        Plugin.Frame -= Tick;
        Handshake.ServerContextCleared -= ContextCleared;
        Detach();
        CharacterSheetChannel.Uninstall();
    }

    private static void Tick()
    {
        if (_open && CharacterSheetChannel.Now - _lastSeen > 0.5)
        { _open = false; Detach(); CharacterSheetChannel.Clear("menu closed"); }
    }

    private static void ContextCleared(string reason) { _open = false; Detach(); }

    private static void Update(SystemHookContext context)
    {
        try
        {
            var world = context.World;
            if (world == null || !world.IsCreated) return;
            var mapper = world.GetExistingSystemManaged<InventorySubMenuMapper>();
            var menu = mapper?._Menu;
            if (menu == null || !menu.gameObject.activeInHierarchy) return;
            var sections = menu.AttributeSections;
            if (sections == null) return;
            bool visible = false;
            for (int i = 0; i < sections.Count; i++)
                if (sections[i] != null && sections[i].gameObject.activeInHierarchy) { visible = true; break; }
            if (!visible) return;
            _lastSeen = CharacterSheetChannel.Now; _open = true;
            var character = LocalCharacterEntity(world);
            if (character == Entity.Null || !character.Has<LocalControlled>(world.EntityManager))
            { Detach(); CharacterSheetChannel.Clear("controlled entity changed"); return; }
            var values = CharacterSheetChannel.Update(world, character);
            if (values == null) { Detach(); return; } // Old servers retain their native presentation.
            bool attachedNow = _menu == null || _menu.Pointer != menu.Pointer;
            if (attachedNow) { Detach(); Attach(menu, values); }
            if (_menu == null) return;
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section == null || !section.gameObject.activeInHierarchy || section.DataCache == null) continue;
                bool changed = false;
                var data = section.DataCache;
                for (int j = 0; j < data.Count; j++)
                {
                    var row = data[j];
                    if (!Keys.TryGetValue(Key(row.TextKey), out int id) || !values.TryGetValue(id, out float value)) continue;
                    var attribute = row.AttributeValue;
                    bool correction = MathF.Abs(attribute.Value - value) > 0.00001f;
                    attribute.Value = value;
                    // Native caps remain visible only when their reconstructed total agrees with the
                    // effective value. Never invent a cap contribution to make a stale breakdown add up.
                    if (attribute.ModificationData.HasValue)
                    {
                        var cap = attribute.ModificationData.Value;
                        if (CharacterSheetSnapshot.NeedsCorrection(value, cap.CapData.Start, cap.SoftCappedValue,
                            cap.CapData.SoftCap, cap.HardCappedValue, cap.CapData.HardCap, cap.UncappedValue))
                        { CharacterSheetSettings.ClearCaps(attribute); correction = true; }
                    }
                    if (!correction) continue;
                    if (id is 9 or 10 or 11 or 19 or 20)
                    {
                        if (!Tooltips.TryGetValue(id, out var tooltip)
                            || !character.TryRead(world.EntityManager, out ProjectM.ResistanceData resistance)) continue;
                        row.TooltipDesc = ProjectM.ResistanceHelper.GetTooltip((ProjectM.UnitStatType)id, (int)value, tooltip, resistance);
                    }
                    row.AttributeValue = attribute;
                    CharacterSheetSettings.SetRow(data, j, row);
                    changed = true;
                }
                // The same native refresh retains selection, hover descriptions, fonts, art and pooling.
                if (changed && section.GridSelectionGroup != null) section.GridSelectionGroup.Refresh(data, false);
            }
            // Attach runs after the native mapper. Wait for its following pass to create the
            // additional entries before fitting the grids and positioning their section headings.
            if (_layoutPending && !attachedNow)
            {
                RebuildLayout(menu);
                _layoutPending = false;
            }
        }
        catch (Exception e)
        {
            if (CharacterSheetChannel.Now - _lastError > 30)
            { _lastError = CharacterSheetChannel.Now; Plugin.Logger?.LogWarning("Character sheet update: " + e); }
        }
    }

    private static (int, int, int, int) Key(Stunlock.Localization.LocalizationKey key) =>
        (key.Key._a, key.Key._b, key.Key._c, key.Key._d);

    private static void Attach(InventorySubMenu menu, IReadOnlyDictionary<int, float> values)
    {
        if (menu.AttributeSettings == null || menu.AttributeSections == null) return;
        _menu = menu; _original = menu.AttributeSettings;
        // Unity copies the serialized settings list. The shared vanilla asset is never edited.
        _settings = UnityEngine.Object.Instantiate(_original);
        menu.AttributeSettings = _settings;
        var present = new HashSet<int>();
        var sections = menu.AttributeSections;
        for (int i = 0; i < sections.Count; i++)
        {
            var list = sections[i]?.Attributes;
            if (list == null) continue;
            for (int j = 0; j < list.Count; j++) present.Add((int)list[j].Type);
        }
        var settings = _settings.Settings;
        var configured = new HashSet<int>();
        for (int i = 0; i < settings.Count; i++)
        {
            var setting = settings[i];
            configured.Add((int)setting.UnitStatType);
            Keys[Key(setting.Header)] = (int)setting.UnitStatType;
            Tooltips[(int)setting.UnitStatType] = setting.Tooltip;
        }
        foreach (var row in CharacterStatPresentation.Rows)
        {
            if (row.NativeId == 66) continue; // Native AttributeValueGetter rejects BloodDrain; do not break later sections.
            if (present.Contains(row.NativeId) || !values.ContainsKey(row.NativeId)) continue;
            var section = FindSection(menu, row.Category);
            if (section == null || section.Attributes == null) continue;
            if (!configured.Contains(row.NativeId))
            {
                string key = "progenitor.character-stat." + row.NativeId;
                Text.Add(Loc.Register(key + ".name", row.Label, owner: typeof(CharacterSheetFeature)));
                Text.Add(Loc.Register(key + ".description", row.Description, owner: typeof(CharacterSheetFeature)));
                var setting = new AttributeSetting { UnitStatType = (ProjectM.UnitStatType)row.NativeId,
                    Header = Loc.KeyFor(key + ".name"), Tooltip = Loc.KeyFor(key + ".description"),
                    ValueFormat = row.Format, MultiplyBy100 = row.MultiplyBy100, HideSoftCap = true };
                CharacterSheetSettings.Add(settings, setting);
                Keys[Key(setting.Header)] = row.NativeId;
                configured.Add(row.NativeId);
            }
            section.Attributes.Add(new CharacterAttributeType { Type = (ProjectM.UnitStatType)row.NativeId });
            Added.Add((section, row.NativeId)); present.Add(row.NativeId);
        }
        _layoutPending = Added.Count > 0;
    }

    private static void RebuildLayout(InventorySubMenu menu)
    {
        foreach (var section in menu.AttributeSections)
        {
            var rect = section?.LayoutGroup?.transform.TryCast<RectTransform>();
            if (rect != null && rect.gameObject.activeInHierarchy)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }
        var content = menu.AttributesAutoScroll?._ContentRectTransform;
        if (content != null && content.gameObject.activeInHierarchy)
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }

    private static CharacterAttributeSection? FindSection(InventorySubMenu menu, string category)
    {
        int anchor = category switch { "Offense" => 29, "Defense" => 74, "Speed" or "Movement" => 83,
            "Resources" => 3, "Abilities" => 70, "Blood" => 77, "Recovery" => 75, _ => -1 };
        var sections = menu.AttributeSections;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var list = section?.Attributes;
            if (list == null) continue;
            for (int j = 0; j < list.Count; j++) if ((int)list[j].Type == anchor) return section;
        }
        return null; // Do not append to an unrelated or unknown future section.
    }

    private static Entity LocalCharacterEntity(World world)
    {
        var em = world.EntityManager;
        if (_characterWorld != world)
        { _characterWorld = world; _character = Entity.Null; _characterRetry = 0; }
        if (_character != Entity.Null && _character.Has<LocalCharacter>(em)) return _character;
        _character = Entity.Null;
        if (CharacterSheetChannel.Now < _characterRetry) return Entity.Null;
        _characterRetry = CharacterSheetChannel.Now + 0.5;
        var found = Query.Entities(em, false, false, ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (found.Count == 1) _character = found[0];
        return _character;
    }
    private static void Detach()
    {
        var changedSections = new HashSet<CharacterAttributeSection>();
        foreach (var (section, id) in Added)
        {
            if (section == null || section.Attributes == null) continue;
            var list = section.Attributes;
            for (int i = list.Count - 1; i >= 0; i--) if ((int)list[i].Type == id) { list.RemoveAt(i); break; }
            // Unload may have no following mapper pass. Retire pooled rows now, while their
            // localization and settings still exist, before measuring the restored layout.
            var data = section.DataCache;
            if (data != null)
                for (int i = data.Count - 1; i >= 0; i--)
                    if (Keys.TryGetValue(Key(data[i].TextKey), out int rowId) && rowId == id)
                        data.RemoveAt(i);
            changedSections.Add(section);
        }
        foreach (var section in changedSections)
            if (section.DataCache != null && section.GridSelectionGroup != null)
                section.GridSelectionGroup.Refresh(section.DataCache, false);
        if (changedSections.Count > 0 && _menu != null) RebuildLayout(_menu);
        _layoutPending = false;
        Added.Clear(); Keys.Clear(); Tooltips.Clear();
        if (_menu != null && _settings != null && _menu.AttributeSettings?.Pointer == _settings.Pointer)
            _menu.AttributeSettings = _original;
        if (_settings != null) UnityEngine.Object.Destroy(_settings);
        _settings = null; _original = null; _menu = null;
        foreach (var lease in Text) lease.Dispose();
        Text.Clear();
    }

    internal static object Describe()
    {
        var rows = new List<object>();
        var sections = _menu?.AttributeSections;
        if (sections != null)
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var data = section?.DataCache;
                if (data == null) continue;
                for (int j = 0; j < data.Count; j++)
                {
                    var row = data[j];
                    Keys.TryGetValue(Key(row.TextKey), out int id);
                    rows.Add(new { id, value = row.AttributeValue.Value, format = row.AttributeValue.Format,
                        percent = row.AttributeValue.MultiplyBy100, caps = row.AttributeValue.ModificationData.HasValue,
                        description = row.TooltipDesc, section = i });
                }
            }
        return new { open = _open, attached = _menu != null, added = Added.Count,
            snapshot = CharacterSheetChannel.Describe(), rows };
    }
}

/// <summary>Development readback for native sheet qualification. Not a plugin authoring contract.</summary>
public static class CharacterSheetTestBridge
{
    public static object Describe() => CharacterSheetFeature.Describe();
}
#endif






