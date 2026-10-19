using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Ui;

namespace Progenitor.Networking;

// Only explicitly published settings enter this registry. Ordinary server cfg entries may contain secrets.
internal static partial class ScopedSettings
{
    internal sealed class Registration : IDisposable
    {
        internal ScopedSettingRecord Schema = new();
        internal Type ValueType = typeof(string);
        internal ConfigEntryBase? Entry;
        internal Func<ulong, string> Read = _ => "";
        internal Func<ulong, string, string, ServerSettingResult> Write = (_, _, _) => new(false, "", "Unavailable.");
        internal Action? Release;
        /// <summary>Presentation the author gave a PerPlayer setting off the page (<c>PlayerSetting.Present</c>).</summary>
        internal SettingPresentation? Catalog;
        internal bool Disposed;
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (Entries.TryGetValue(Schema.Id, out var current) && ReferenceEquals(current, this)) Entries.Remove(Schema.Id);
            Release?.Invoke(); Release = null; Catalog = null;
            Read = _ => ""; Write = (_, _, _) => new(false, "", "This setting was unloaded.");
            Entry = null;
            if (Schema.Personal)
            {
                bool used = false;
                foreach (var row in Entries.Values) if (row.Schema.Personal && row.Schema.PluginId == Schema.PluginId) { used = true; break; }
                if (!used) Stores.Release(Schema.PluginId);
            }
            _schemaRevision++;
        }
    }

    private static readonly Dictionary<string, Registration> Entries = new(StringComparer.Ordinal);
    private static readonly PlayerSettingsStores Stores = new(
        pluginId => new PlayerSettingsStore(Path.Combine(BepInEx.Paths.ConfigPath, "progenitor-players"), pluginId,
            message => Plugin.Logger?.LogWarning(message)),
        message => Plugin.Logger?.LogWarning(message));
    private static readonly RemoteInstallRateLimiter PersonalLimiter = new(30, 60);
    private static int _schemaRevision;

    internal static string RowId(string pluginId, string section, string key)
    {
        foreach (string part in new[] { pluginId, section, key })
            if (string.IsNullOrWhiteSpace(part) || part.Contains('/') || part.Length > 128 || ServerSettings.HasDisallowedControl(part))
                throw new ArgumentException("Setting identifiers must be nonempty, at most 128 characters, and contain no slash or control characters.");
        return pluginId + "/" + section + "/" + key;
    }

    private static void Add(Registration row)
    {
        if (Entries.Count >= 512) throw new InvalidOperationException("The published settings limit is 512 rows.");
        if (Entries.ContainsKey(row.Schema.Id)) throw new ArgumentException("This setting is already published: " + row.Schema.Id);
        // Reject impossible schema before registering, rather than breaking every peer's snapshot later.
        ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = Guid.NewGuid(), Row = row.Schema });
        if (ItemListSettings.Has(row.Schema.Id))
        {
            // An item list's value can reach its full size: the row must still fit one packet with the hints of a list.
            var full = ScopedSettingsSnapshot.Clone(row.Schema);
            full.Value = new string('0', ItemLimitList.MaxEncodedBytes);
            full.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.ItemList, Label = new string('x', ScopedSettingPresentation.MaxLabelBytes) };
            try { ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 2, Generation = Guid.NewGuid(), Row = full }, ScopedSettingsWire.PresentationVersion); }
            catch (ArgumentException) { throw new ArgumentException("This item list's help or default is too long to send with a full list."); }
        }
        Entries.Add(row.Schema.Id, row); _schemaRevision++;
    }

    internal static IDisposable Publish(string pluginId, string name, string version, ConfigEntryBase entry)
    {
        string id = RowId(pluginId, entry.Definition.Section, entry.Definition.Key);
        if (Entries.TryGetValue(id, out var existing) && ReferenceEquals(existing.Entry, entry)) return existing;
        var registration = new Registration
        {
            Schema = Describe(pluginId, name, version, entry.Definition.Section, entry.Definition.Key,
                entry.SettingType, entry.DefaultValue, entry.Description.Description, entry.Description.AcceptableValues, false),
            ValueType = entry.SettingType,
            Entry = entry,
            Read = _ => SettingsEditors.Format(entry.BoxedValue),
        };
        Add(registration);
        return registration;
    }

    internal static IDisposable? PublishLegacy(string pluginId, string name, string version, ConfigEntryBase entry)
    {
        try { return Publish(pluginId, name, version, entry); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Settings publication skipped for " + pluginId + ": " + e.Message); return null; }
    }

    internal static Registration Personal<T>(string pluginId, string name, string version, string section, string key,
        T defaultValue, string description, AcceptableValueBase? bounds, Func<T, string?>? validate,
        Action<ulong, T> changed)
    {
        ConfigSync.ThrowIfNotSyncable(typeof(T));
        if (SettingsEditors.KindFor(typeof(T)) == SettingsEditorKind.ReadOnly) throw new ArgumentException("Unsupported setting type.");
        string id = RowId(pluginId, section, key);
        string fallback = SettingsEditors.Format(defaultValue);
        if (!IsFinite(defaultValue)) throw new ArgumentException("A finite default value is required.");
        if (bounds != null && !bounds.IsValid(defaultValue)) throw new ArgumentException("Default is outside the setting's bounds.");
        if (validate?.Invoke(defaultValue) is string reason) throw new ArgumentException(reason);
        // A store opened here is kept only when the row registers, so a refused row leaves no empty store behind.
        return Stores.Register(pluginId, store => PersonalRow(store, pluginId, name, version, section, key, id, fallback,
            defaultValue, description, bounds, validate, changed));
    }

    private static Registration PersonalRow<T>(PlayerSettingsStore store, string pluginId, string name, string version,
        string section, string key, string id, string fallback, T defaultValue, string description,
        AcceptableValueBase? bounds, Func<T, string?>? validate, Action<ulong, T> changed)
    {
        var row = new Registration
        {
            Schema = Describe(pluginId, name, version, section, key, typeof(T), defaultValue, description, bounds, true),
            ValueType = typeof(T),
        };
        string storageKey = section + "/" + key;
        row.Read = player =>
        {
            if (player == 0) throw new ArgumentException("A stable player account is required.");
            string value = store.Get(player, storageKey, fallback);
            if (SettingsEditors.TryParse(value, typeof(T), out var parsed, out _) &&
                (bounds == null || bounds.IsValid(parsed)) && IsFinite(parsed)) return value;
            if (store.Orphan(player, storageKey))
                Plugin.Logger?.LogWarning("Player setting " + id + " held a value it no longer accepts; it was kept under orphans and reads as the default.");
            return fallback;
        };
        row.Write = (player, value, playerName) =>
        {
            string current = row.Read(player);
            if (!SettingsEditors.TryParse(value, typeof(T), out var parsed, out string error)) return new(false, current, error);
            if (parsed is string listText && ItemListSettings.TryGet(id, out var listRules))
            {
                // An item list: the server's own rules decide it, and the canonical text is stored.
                if (!ItemListSettings.TryCanonical(listRules, listText, out var canonical, out var listError)) return new(false, current, listError);
                parsed = canonical;
            }
            if (bounds != null && !bounds.IsValid(parsed)) return new(false, current, "Outside the values this setting accepts.");
            if (!IsFinite(parsed)) return new(false, current, "A finite number is required.");
            if (validate?.Invoke((T)parsed!) is string refusal) return new(false, current, refusal);
            string actual = SettingsEditors.Format(parsed);
            if (actual.Length > row.Schema.MaxLength || ServerSettings.HasDisallowedControl(actual)) return new(false, current, "The value is too long or contains control characters.");
            // Memory changes now; the store writes coalesced changes on the next poll (Tick).
            store.Set(player, storageKey, actual, fallback, playerName);
            if (current != actual)
            {
                try { changed(player, (T)parsed!); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Player setting subscriber: " + e.Message); }
            }
            return new(true, actual, "");
        };
        Add(row);
        return row;
    }

    private static bool IsFinite(object? value) => value switch
    {
        double number => !double.IsNaN(number) && !double.IsInfinity(number),
        float number => !float.IsNaN(number) && !float.IsInfinity(number),
        _ => true,
    };

    private static ScopedSettingRecord Describe(string pluginId, string name, string version, string section, string key,
        Type type, object? defaultValue, string help, AcceptableValueBase? bounds, bool personal)
    {
        var row = new ScopedSettingRecord
        {
            Id = RowId(pluginId, section, key), PluginId = pluginId, ModName = name, Version = version,
            Section = section, Key = key, Help = help ?? "", Type = type.FullName ?? "", Personal = personal,
            Default = SettingsEditors.Format(defaultValue), Value = SettingsEditors.Format(defaultValue), Editable = true,
            MaxLength = ItemListSettings.Has(RowId(pluginId, section, key)) ? ItemLimitList.MaxEncodedBytes : 1024,
        };
        if (type.IsEnum) row.Choices = Enum.GetNames(type);
        if (bounds != null)
        {
            var t = bounds.GetType();
            if (t.GetProperty("MinValue") is { } min) row.Min = SettingsEditors.Format(min.GetValue(bounds));
            if (t.GetProperty("MaxValue") is { } max) row.Max = SettingsEditors.Format(max.GetValue(bounds));
            if (t.GetProperty("AcceptableValues")?.GetValue(bounds) is System.Collections.IEnumerable choices)
            {
                var values = new List<string>(); foreach (object? value in choices) values.Add(SettingsEditors.Format(value));
                row.Choices = values.ToArray();
            }
        }
        return row;
    }

    internal static ServerSettingResult Apply(ScopedSettingsPacket request, bool ready, in RemoteInstallActor actor)
    {
        if (!ready || actor.SteamId == 0) return new(false, "", "The connection has no authenticated player settings session.");
        if (!Entries.TryGetValue(request.Id, out var row) || row.Disposed) return new(false, "", "This setting is no longer published.");
        if (row.Schema.Personal != request.Personal || row.Schema.Type != request.Type) return new(false, "", "The setting definition changed. Reopen its page.");
        if (!row.Schema.Personal)
            return ServerSettings.Apply(new ServerSettingsMessage(request.RequestId, request.Id, request.Type, request.Expected, request.Value, false, ""), ready, actor);
        if (!PersonalLimiter.TryTake(actor.SteamId, Now())) return new(false, "", "Too many changes. Wait a moment and try again.");
        try
        {
            string current = row.Read(actor.SteamId);
            if (request.Expected != current) return new(false, current, "This setting changed on the server. Review its current value and try again.");
            return row.Write(actor.SteamId, request.Value, actor.CharacterName);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Player setting write refused: " + e.Message); return new(false, "", "The server could not save this setting."); }
    }
}
