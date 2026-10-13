using System;
using System.Collections.Generic;
using System.IO;

namespace BepInEx.Configuration;

/// <summary>
/// Test stand-in for BepInEx <c>ConfigDefinition</c>. Equality is section+key, matching the
/// real type so <see cref="ConfigHelper"/> recording and Remove round-trip.
/// </summary>
public sealed class ConfigDefinition : IEquatable<ConfigDefinition>
{
    public string Section { get; }
    public string Key { get; }

    public ConfigDefinition(string section, string key)
    {
        Section = section ?? throw new ArgumentNullException(nameof(section));
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public bool Equals(ConfigDefinition? other) =>
        other != null
        && string.Equals(Section, other.Section, StringComparison.Ordinal)
        && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ConfigDefinition);

    public override int GetHashCode() =>
        ((Key != null ? Key.GetHashCode() : 0) * 397) ^ (Section != null ? Section.GetHashCode() : 0);

    public override string ToString() => Section + "." + Key;
}

public abstract class ConfigEntryBase
{
    public ConfigFile ConfigFile { get; }
    public ConfigDefinition Definition { get; }
    public Type SettingType { get; }
    public abstract object? BoxedValue { get; set; }

    protected ConfigEntryBase(ConfigFile configFile, ConfigDefinition definition, Type settingType)
    {
        ConfigFile = configFile;
        Definition = definition;
        SettingType = settingType;
    }

    public string GetSerializedValue() => BoxedValue?.ToString() ?? "";

    public void SetSerializedValue(string value)
    {
        BoxedValue = ConvertFromString(value, SettingType);
    }

    internal static object? ConvertFromString(string orphan, Type type)
    {
        if (type == typeof(string)) return orphan;
        if (type == typeof(bool) && bool.TryParse(orphan, out var b)) return b;
        if (type.IsEnum) return Enum.Parse(type, orphan, ignoreCase: true);
        // KEYS1: BepInEx's KeyboardShortcut has a static Deserialize; the stand-in below mirrors it.
        var deserialize = type.GetMethod("Deserialize", new[] { typeof(string) });
        if (deserialize != null && deserialize.IsStatic) return deserialize.Invoke(null, new object[] { orphan });
        return Convert.ChangeType(orphan, type, System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class ConfigEntry<T> : ConfigEntryBase
{
    private T _value;

    /// <summary>The live <c>ConfigEntryBase.DefaultValue</c>, here on the typed entry only (core code reads it by name).</summary>
    public object? DefaultValue { get; }

    internal ConfigEntry(ConfigFile configFile, ConfigDefinition definition, T defaultValue)
        : base(configFile, definition, typeof(T))
    {
        _value = defaultValue;
        DefaultValue = defaultValue;
        // Real BepInEx ConfigEntry ctor subscribes an instance method to ConfigFile.SettingChanged
        // (dump target was the ConfigEntry itself; evidence 65). Mirror that so UninstallAll's
        // handler strip is exercised.
        configFile.SettingChanged += OnFileSettingChanged;
    }

    public T Value
    {
        get => _value;
        set
        {
            // Production BepInEx assigns first, then raises SettingChanged. The
            // lock reverts on that event; do not call TryRefuseWrite here.
            if (Equals(_value, value)) return;
            _value = value;
            ConfigFile.RaiseSettingChanged(this);
        }
    }

    public event EventHandler? SettingChanged;

    public override object? BoxedValue
    {
        get => _value;
        // Live BepInEx: `set { Value = (T)value; }` — so a boxed write goes through the same
        // equality check as a typed one and raises nothing when the value did not move. That
        // check is why a hand edit produced exactly two reloads rather than an endless run
        // (FCFG8): the second reload assigned the same values, saved nothing, and stopped.
        set => Value = (T)value!;
    }

    private void OnFileSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.ChangedSetting == this)
            SettingChanged?.Invoke(sender, e);
    }
}

public sealed class SettingChangedEventArgs : EventArgs
{
    public ConfigEntryBase ChangedSetting { get; }

    public SettingChangedEventArgs(ConfigEntryBase changedSetting) => ChangedSetting = changedSetting;
}

/// <summary>
/// Test stand-in for BepInEx <c>ConfigFile</c>. Layout matches the real type where
/// ConfigHelper and the unit tests reflect: protected <c>Entries</c>, private
/// <c>OrphanedEntries</c>, field-like <c>SettingChanged</c>. <c>Remove</c> only drops
/// <c>Entries</c> — same as BepInEx.Core.dll.
/// </summary>
public class ConfigFile
{
    private readonly object _ioLock = new();

    protected Dictionary<ConfigDefinition, ConfigEntryBase> Entries { get; } = new();

    private Dictionary<ConfigDefinition, string> OrphanedEntries { get; } = new();

    public string ConfigFilePath { get; }

    /// <summary>
    /// Mirrors live BepInEx. ConfigSync holds this false while any Synced slot on
    /// this file is locked so a Value-set cannot auto-Save. Explicit <see cref="Save"/>
    /// still runs; locked keys must already be parked as orphans (not via a disk-Toml
    /// override) so the server value never lands in GUID.cfg.
    /// <para>
    /// FCFG10 (R65 #1): the watch reads this flag immediately before <see cref="Reload"/> and
    /// arms its self-write filter only when it is true, so a test that sets it false and then
    /// writes the file from a <see cref="SettingChanged"/> subscriber — an operator's save landing
    /// inside the reload — reaches the branch that used to swallow that save.
    /// </para>
    /// </summary>
    public bool SaveOnConfigSet
    {
        get => _saveOnConfigSet;
        set
        {
            if (SaveOnConfigSetThrows) throw new InvalidOperationException("test: SaveOnConfigSet setter throws");
            if (SaveOnConfigSetStuck) return;
            _saveOnConfigSet = value;
        }
    }

    private bool _saveOnConfigSet = true;

    /// <summary>
    /// R69 #3: the watch holds <see cref="SaveOnConfigSet"/> off across the reload loop so no
    /// registered instance can write the file back, and it reads the flag <em>back</em> after
    /// writing it rather than assuming the write took. This knob is the instance where it did not:
    /// the setter is silently ignored, the reload saves as BepInEx always did, and the self-write
    /// filter (FCFG8/9/10) is what has to catch the write-back. Live BepInEx has a plain
    /// auto-property here, so this models the defensive case rather than an observed one — the
    /// hold is something Core does to somebody else's object and must not be depended on.
    /// </summary>
    public bool SaveOnConfigSetStuck { get; set; }

    /// <summary>
    /// The other half of the same defense: the setter throws instead of quietly refusing, so the
    /// hold's own <c>catch</c> is what treats the instance as one that can still write.
    /// </summary>
    public bool SaveOnConfigSetThrows { get; set; }

    /// <summary>
    /// When true, <see cref="Remove"/> throws. Parking must still drop the live row
    /// through the Entries dictionary so Save writes the orphaned local.
    /// </summary>
    public bool RemoveThrows { get; set; }

    /// <summary>How many times <see cref="Save"/> ran. Tests assert lock/restore.</summary>
    public int SaveCount { get; private set; }

    /// <summary>Last <see cref="Save"/> body, or null if never saved.</summary>
    public string? LastSavedText { get; private set; }

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public ConfigFile(string configPath, bool saveOnInit)
    {
        if (configPath == null) throw new ArgumentNullException(nameof(configPath));
        ConfigFilePath = Path.GetFullPath(configPath);
        var dir = Path.GetDirectoryName(ConfigFilePath);
        if (dir != null) Directory.CreateDirectory(dir);
        if (saveOnInit && !File.Exists(ConfigFilePath))
            File.WriteAllText(ConfigFilePath, "");
    }

    public void Save()
    {
        SaveCount++;
        var lines = new List<string>();
        lock (_ioLock)
        {
            var written = new HashSet<ConfigDefinition>();
            foreach (var kv in Entries)
            {
                // Live BepInEx serializes BoxedValue / GetSerializedValue, then
                // OrphanedEntries for keys not in Entries. Do not call
                // ConfigSync.TryGetDiskToml: a failed park must fail this test.
                string value = kv.Value.GetSerializedValue();
                lines.Add("[" + kv.Key.Section + "] " + kv.Key.Key + " = " + value);
                written.Add(kv.Key);
            }
            foreach (var kv in OrphanedEntries)
            {
                if (written.Contains(kv.Key)) continue;
                lines.Add("[" + kv.Key.Section + "] " + kv.Key.Key + " = " + kv.Value);
            }
        }
        LastSavedText = string.Join("\n", lines);
        File.WriteAllText(ConfigFilePath, LastSavedText);
    }

    public ConfigEntryBase this[ConfigDefinition key]
    {
        get
        {
            lock (_ioLock) return Entries[key];
        }
    }

    public bool ContainsKey(ConfigDefinition key)
    {
        lock (_ioLock) return Entries.ContainsKey(key);
    }

    public bool Remove(ConfigDefinition key)
    {
        if (RemoveThrows)
            throw new InvalidOperationException("test: ConfigFile.Remove throws");
        lock (_ioLock) return Entries.Remove(key);
    }

    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
    {
        _ = description;
        var definition = new ConfigDefinition(section, key);
        lock (_ioLock)
        {
            if (Entries.TryGetValue(definition, out var existing))
                return (ConfigEntry<T>)existing;

            var entry = new ConfigEntry<T>(this, definition, defaultValue);
            Entries[definition] = entry;
            if (OrphanedEntries.TryGetValue(definition, out var orphan))
            {
                try { entry.BoxedValue = ConfigEntryBase.ConvertFromString(orphan, typeof(T)); }
                catch { /* keep default */ }
                OrphanedEntries.Remove(definition);
            }

            return entry;
        }
    }

    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue) =>
        Bind(section, key, defaultValue, "");

    /// <summary>How many times <see cref="Reload"/> ran. FCFG8 counts these.</summary>
    public int ReloadCount { get; private set; }

    /// <summary>
    /// When true, <see cref="Reload"/> throws before doing anything. FCFG12: with more than one
    /// registered instance for a path, the watch must reload each of them under its own guard, so
    /// one instance whose <c>Reload</c> throws (an entry with a converter that does not survive its
    /// plugin's unload, say) cannot leave the others holding the pre-edit values.
    /// </summary>
    public bool ReloadThrows { get; set; }

    /// <summary>
    /// Models BepInEx <c>ConfigFile.Reload</c>, including the write-back that FCFG8 is about:
    /// clear the orphans, read the file, and hand every line that names a bound key to
    /// <c>SetSerializedValue</c>. That sets <c>BoxedValue</c> -> <c>Value</c>, and a value that
    /// actually moved raises SettingChanged, which saves the whole file under
    /// <see cref="SaveOnConfigSet"/> — the disk write the watch used to mistake for an edit.
    /// The line format is this shim's own <see cref="Save"/> format, so a reload of what Save
    /// wrote is a faithful round trip.
    /// </summary>
    public void Reload()
    {
        if (ReloadThrows)
            throw new InvalidOperationException("test: ConfigFile.Reload throws");
        ReloadCount++;
        List<(ConfigEntryBase entry, string value)> pending = new();
        string[] lines;
        lock (_ioLock)
        {
            OrphanedEntries.Clear();
            lines = File.Exists(ConfigFilePath) ? File.ReadAllLines(ConfigFilePath) : Array.Empty<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!line.StartsWith("[", StringComparison.Ordinal)) continue;
                int close = line.IndexOf(']');
                if (close < 0) continue;
                int eq = line.IndexOf('=', close);
                if (eq < 0) continue;
                var section = line.Substring(1, close - 1).Trim();
                var key = line.Substring(close + 1, eq - close - 1).Trim();
                var value = line.Substring(eq + 1).Trim();
                var definition = new ConfigDefinition(section, key);
                if (Entries.TryGetValue(definition, out var entry)) pending.Add((entry, value));
                else OrphanedEntries[definition] = value;
            }
        }

        // Outside the lock only because this shim's Save takes it too; live BepInEx re-enters
        // its own Monitor on the same thread. The ordering is the same: assign, then save.
        foreach (var (entry, value) in pending)
        {
            try { entry.SetSerializedValue(value); }
            catch { /* live BepInEx logs and keeps the old value */ }
        }
    }

    internal void RaiseSettingChanged(ConfigEntryBase entry)
    {
        // Live BepInEx ConfigFile.OnSettingChanged saves first, then raises the event.
        if (SaveOnConfigSet) Save();
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(entry));
    }
}

/// <summary>
/// KEYS1 test stand-in for BepInEx's <c>KeyboardShortcut</c> (same full name, so the settings catalog
/// recognizes it by type name): the serialized form (<c>F8 + LeftControl</c>) is its whole state.
/// </summary>
public readonly struct KeyboardShortcut
{
    public KeyboardShortcut(string text) => Text = string.IsNullOrWhiteSpace(text) ? "None" : text.Trim();

    public string Text { get; }

    public static KeyboardShortcut Deserialize(string text) => new(text);

    public override string ToString() => Text ?? "None";
}
