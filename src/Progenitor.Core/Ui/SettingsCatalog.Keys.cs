using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Config;

namespace Progenitor.Ui;

/// <summary>
/// KEYS1: ordinary BepInEx mods' key settings — <c>ConfigEntry&lt;KeyboardShortcut&gt;</c> and
/// <c>ConfigEntry&lt;KeyCode&gt;</c> — found in the config files the catalog already knows (the
/// chainloader sweep's and every Progenitor plugin's), for the Keybinds tab and the winner rule.
/// Matched by type name, so this file names no Unity or BepInEx.Unity type. A write goes through the
/// catalog's own rules: a server-synced entry is never written, and another mod's entry only while
/// <see cref="AllowForeignEdits"/> is on.
/// </summary>
public static partial class SettingsCatalog
{
    internal const string KeyboardShortcutTypeName = "BepInEx.Configuration.KeyboardShortcut";
    internal const string KeyCodeTypeName = "UnityEngine.KeyCode";

    internal const string KeySyncedReason = "The server owns this value for this session.";

    internal const string KeyForeignReason =
        "Progenitor changes other mods' settings only while [Settings] EditOtherModsSettings is on in "
        + "progenitor.ui.cfg.";

    /// <summary>
    /// What a saved cfg key says about when it applies. Progenitor cannot see whether a mod reads its
    /// setting again while running, so it says so rather than claiming either.
    /// </summary>
    internal const string KeySavedNote =
        "Saved to its config file. It applies when the mod reads the setting again; many mods read it only "
        + "at start, so restart the game if the new key does not work.";

    /// <summary>
    /// KEYS1: bumped when a key setting is written or a settings source is added or removed, so the
    /// winner picture (<see cref="KeybindArbiter.Current"/>) is rebuilt then and not otherwise.
    /// </summary>
    internal static int KeyVersion => System.Threading.Volatile.Read(ref _keyVersion);

    private static int _keyVersion;

    internal static void BumpKeyVersion() => System.Threading.Interlocked.Increment(ref _keyVersion);

    /// <summary>True for a <c>KeyboardShortcut</c> or <c>KeyCode</c> entry.</summary>
    internal static bool IsKeyEntry(ConfigEntryBase? entry, out bool keyCodeOnly)
    {
        keyCodeOnly = false;
        string? name = entry?.SettingType?.FullName;
        if (string.Equals(name, KeyboardShortcutTypeName, StringComparison.Ordinal)) return true;
        if (!string.Equals(name, KeyCodeTypeName, StringComparison.Ordinal)) return false;
        keyCodeOnly = true;
        return true;
    }

    /// <summary>
    /// Every key setting of every known source, as cfg claims. Never throws: a source that cannot be
    /// walked is skipped. <paramref name="sweep"/> first asks the chainloader sweep for sources.
    /// </summary>
    internal static IReadOnlyList<KeybindClaim> KeyClaims(bool sweep = true)
    {
        if (sweep)
        {
            try { SourceSweep?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings source sweep threw: " + e.Message); }
        }

        Source[] sources;
        lock (Gate) sources = Sources.ToArray();
        var claims = new List<KeybindClaim>();
        var seen = new HashSet<ConfigEntryBase>();
        for (int i = 0; i < sources.Length; i++)
        {
            IDictionary? entries;
            try { entries = ConfigHelper.GetEntries(sources[i].File); }
            catch { continue; }
            if (entries == null) continue;
            var list = new List<ConfigEntryBase>();
            if (!TryCollect(entries, list, sources[i].PluginId)) continue;
            list.Sort(static (a, b) =>
            {
                int c = string.CompareOrdinal(a.Definition.Section, b.Definition.Section);
                return c != 0 ? c : string.CompareOrdinal(a.Definition.Key, b.Definition.Key);
            });
            foreach (var entry in list)
            {
                if (!IsKeyEntry(entry, out bool keyCodeOnly) || !seen.Add(entry)) continue;
                try { claims.Add(KeyClaim(sources[i].PluginId, entry, sources[i].Foreign, keyCodeOnly)); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Ui: key setting " + sources[i].PluginId + " " + entry.Definition + " could not be read: " + e.Message);
                }
            }
        }

        return claims;
    }

    private static KeybindClaim KeyClaim(string pluginId, ConfigEntryBase entry, bool foreign, bool keyCodeOnly)
    {
        string raw = SafeSerialized(entry);
        bool comparable = LegacyKeyNames.TryParse(raw, out var chord);
        string defaultRaw = SerializedDefault(entry);
        KeyChord? defaultChord = LegacyKeyNames.TryParse(defaultRaw, out var d) && defaultRaw.Length > 0 ? d : null;
        string reason = "";
        if (IsSynced(entry)) reason = KeySyncedReason;
        else if (foreign && !AllowForeignEdits) reason = KeyForeignReason;
        return new KeybindClaim(
            KeybindSource.Cfg,
            pluginId,
            entry.Definition.Section + "/" + entry.Definition.Key,
            ModSettingsLayout.DisplayName(entry.Definition.Key),
            comparable ? chord : KeyChord.None,
            chordText: comparable ? (chord.IsBound ? chord.ToString() : "None") : raw,
            help: FirstLine(DescriptionOf(entry)),
            defaultChord: defaultChord,
            defaultText: defaultChord is { } dc ? (dc.IsBound ? dc.ToString() : "None") : defaultRaw,
            comparable: comparable,
            editable: reason.Length == 0,
            notEditableReason: reason,
            // The picture outlives this walk; it must not hold another plugin's ConfigEntry (review
            // 2026-09-29). WriteKey finds the entry again by owner and Section/Key.
            entry: null,
            keyCodeOnly: keyCodeOnly);
    }

    /// <summary>
    /// Write <paramref name="chord"/> into a cfg key through the catalog's rules. Refused (nothing
    /// written) for a synced entry, another mod's entry without the opt-in, a chord Windows or Steam
    /// reserves (KEYS2, <see cref="ReservedChords"/>), a chord the entry cannot hold (a <c>KeyCode</c>
    /// with modifiers), or a write that did not read back. Conflicts with other keys are the caller's
    /// check (<see cref="KeybindRemapGuard"/>): the Keybinds tab refuses them before it writes.
    /// </summary>
    internal static SettingWriteResult WriteKey(KeybindClaim claim, KeyChord chord)
    {
        if (claim == null || claim.Source != KeybindSource.Cfg)
            return SettingWriteResult.Refused("", "this is not a mod's key setting");
        // Found again now, and its rules asked again now: the claim may be older than the opt-in switch.
        if (!TryFindKeyEntry(claim.OwnerId, claim.ActionId, out var entry, out bool foreign, out bool keyCodeOnly))
            return SettingWriteResult.Refused("", "this key setting is no longer loaded");
        string before = SafeSerialized(entry);
        if (IsSynced(entry)) return SettingWriteResult.Refused(before, KeySyncedReason);
        if (foreign && !AllowForeignEdits) return SettingWriteResult.Refused(before, KeyForeignReason);
        // KEYS2: Progenitor never writes a chord Windows or Steam takes first, into anyone's setting.
        if (ReservedChords.Reason(chord) is { } reserved) return SettingWriteResult.Refused(before, reserved);
        // HOLDMOD1 review: a mod's own key may act on a press, so it never takes Ctrl, Shift or Alt on its own.
        // Its own default is still accepted (a reset puts back what the mod shipped).
        if (chord.IsLoneModifier && claim.DefaultChord != chord) return SettingWriteResult.Refused(before, KeybindsTabLayout.CfgLoneModifierReason(chord));
        if (!LegacyKeyNames.TryFormat(chord, keyCodeOnly, out string text, out string why, before))
            return SettingWriteResult.Refused(before, why);
        try
        {
            if (!string.Equals(before, text, StringComparison.OrdinalIgnoreCase))
            {
                entry.SetSerializedValue(text);
                // KEYS4: saved even while the file does not save on set (a server's config-sync lock).
                Keybinds.Persist(entry);
            }
        }
        catch (Exception e)
        {
            return SettingWriteResult.Failed(before, e.Message);
        }

        BumpKeyVersion();
        KeybindArbiter.Invalidate();
        string after = SafeSerialized(entry);
        if (!LegacyKeyNames.TryParse(after, out var readBack) || readBack != chord)
            return SettingWriteResult.Failed(after, "the setting did not take " + chord + " (it reads " + after + ")");
        Plugin.Logger?.LogInfo("Ui: key setting " + claim.OwnerId + " " + claim.ActionId + " = " + after);
        return SettingWriteResult.Accepted(after);
    }

    /// <summary>The live key entry <paramref name="actionId"/> (<c>Section/Key</c>) of <paramref name="pluginId"/>'s sources.</summary>
    internal static bool TryFindKeyEntry(string pluginId, string actionId, out ConfigEntryBase entry, out bool foreign, out bool keyCodeOnly)
    {
        entry = null!;
        foreign = false;
        keyCodeOnly = false;
        int slash = (actionId ?? "").IndexOf('/');
        if (slash <= 0) return false;
        string section = actionId!.Substring(0, slash), key = actionId.Substring(slash + 1);
        Source[] sources;
        lock (Gate) sources = Sources.ToArray();
        foreach (var source in sources)
        {
            if (!string.Equals(source.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) continue;
            IDictionary? entries;
            try { entries = ConfigHelper.GetEntries(source.File); }
            catch { continue; }
            if (entries == null) continue;
            var list = new List<ConfigEntryBase>();
            if (!TryCollect(entries, list, source.PluginId)) continue;
            foreach (var e in list)
            {
                if (!string.Equals(e.Definition.Section, section, StringComparison.Ordinal)
                    || !string.Equals(e.Definition.Key, key, StringComparison.Ordinal)
                    || !IsKeyEntry(e, out bool codeOnly)) continue;
                entry = e;
                foreign = source.Foreign;
                keyCodeOnly = codeOnly;
                return true;
            }
        }

        return false;
    }

    private static string SafeSerialized(ConfigEntryBase entry)
    {
        try { return (entry.GetSerializedValue() ?? "").Trim(); }
        catch { return ""; }
    }

    /// <summary>The entry's default in its own cfg spelling, or empty when it cannot be read.</summary>
    private static string SerializedDefault(ConfigEntryBase entry)
    {
        object? value;
        try { value = entry.GetType().GetProperty("DefaultValue")?.GetValue(entry); }
        catch { return ""; }
        if (value == null) return "";
#if !PROGENITOR_TESTS
        try { return (TomlTypeConverter.ConvertToString(value, entry.SettingType) ?? "").Trim(); }
        catch { /* fall through */ }
#endif
        try { return (value.ToString() ?? "").Trim(); }
        catch { return ""; }
    }

    private static string DescriptionOf(ConfigEntryBase entry)
    {
        try
        {
            var description = entry.GetType().GetProperty("Description")?.GetValue(entry);
            return description?.GetType().GetProperty("Description")?.GetValue(description) as string ?? "";
        }
        catch { return ""; }
    }

    private static string FirstLine(string text)
    {
        string t = (text ?? "").Trim();
        int nl = t.IndexOfAny(new[] { '\r', '\n' });
        return nl < 0 ? t : t.Substring(0, nl).Trim();
    }
}
