using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// SAVEHOLD1: what a settings write did on disk, and the fix for the case where it did nothing.
///
/// <para><b>The defect.</b> <see cref="ConfigSync"/> holds <c>SaveOnConfigSet</c> off on a whole
/// <see cref="ConfigFile"/> while any synced key on it is locked to the server's value (a joined
/// session). A Personal setting in the same cfg — the Sample's Notes and Demo count share
/// <c>progenitor.sample.cfg</c> with the synced Server notice — was then written to memory
/// only. It reached the disk only when the disconnect's restore saved the file, so a crash or quit
/// while joined lost it, and any reload of the file while joined (a hand edit, a watch event) put the
/// old disk value back. The settings page reported it saved.</para>
///
/// <para><b>The fix.</b> After every page write (<see cref="SettingsRow.TryWrite"/>, the one path every
/// commit takes) on a file the sync holds, the file is saved explicitly through
/// <see cref="ConfigSync.TrySaveHeldFile"/>, which writes the parked pre-join locals for the locked keys
/// (never a server value) and refuses when a park failed. A file whose mod turned
/// <c>SaveOnConfigSet</c> off itself is left to the mod. Each write's outcome is kept per row for the
/// readback (<see cref="Describe"/>): how it was saved, the file, the entry and file instances, and the
/// key's value read back from the disk.</para>
/// </summary>
internal static class SettingPersistence
{
    internal const string SavedOnSet = "save-on-set";
    internal const string SavedExplicitly = "explicit-save (the file's auto-save is held off by a synced lock)";
    internal const string Unchanged = "unchanged";
    internal const string NotSavedLocked = "not-saved: the server owns this value for the session";
    internal const string NotSavedModOff = "not-saved: the mod turned SaveOnConfigSet off";

    internal sealed class Outcome
    {
        public string RowId = "";
        public string How = "";
        public string Why = "";
        public string File = "";
        public string EntryInstance = "";
        public string FileInstance = "";
        public string Value = "";
        public bool Changed;
        public long At;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Outcome> Last = new(StringComparer.OrdinalIgnoreCase);

    internal static Func<long> Clock = () => Environment.TickCount64;

    /// <summary>
    /// After a page write of <paramref name="entry"/> (<paramref name="changed"/>: the value moved): make
    /// sure it reaches the disk, and record how.
    /// </summary>
    internal static Outcome AfterWrite(string rowId, ConfigEntryBase entry, bool changed)
    {
        var outcome = new Outcome { RowId = rowId ?? "", Changed = changed, At = Clock() };
        ConfigFile? file = null;
        try { file = entry.ConfigFile; } catch { file = null; }
        try { outcome.Value = entry.GetSerializedValue(); } catch { outcome.Value = ""; }
        outcome.EntryInstance = InstanceId(entry);
        outcome.FileInstance = InstanceId(file);
        try { outcome.File = file?.ConfigFilePath ?? ""; } catch { outcome.File = ""; }

        bool locked;
        try { locked = ConfigSync.IsLocked(entry); } catch { locked = false; }
        bool held;
        try { held = file != null && ConfigSync.IsSaveHeldOff(file); } catch { held = false; }
        bool saveOn;
        try { saveOn = file != null && file.SaveOnConfigSet; } catch { saveOn = false; }

        if (locked)
        {
            outcome.How = NotSavedLocked;
        }
        else if (held)
        {
            // Saved only when the value moved. A blur with no edit writes nothing: the disk may hold a
            // newer hand edit that the file watch has not reloaded yet, and a save would overwrite it.
            if (!changed)
            {
                outcome.How = Unchanged;
            }
            else if (ConfigSync.TrySaveHeldFile(file, out string why))
            {
                outcome.How = SavedExplicitly;
            }
            else
            {
                outcome.How = "not-saved";
                outcome.Why = why;
            }
        }
        else if (saveOn)
        {
            outcome.How = changed ? SavedOnSet : Unchanged;
        }
        else
        {
            outcome.How = NotSavedModOff;
        }

        lock (Gate) Last[outcome.RowId] = outcome;
        return outcome;
    }

    /// <summary>
    /// The last write's outcome for <paramref name="rowId"/>, with the key read back from the disk now:
    /// <c>{persisted, how, why, file, entryInstance, fileInstance, value, onDisk, diskValue, changed, ageMs}</c>.
    /// Null when the row has not been written in this Core generation.
    /// </summary>
    internal static Dictionary<string, object?>? Describe(string? rowId, ConfigEntryBase? entry = null)
    {
        if (string.IsNullOrEmpty(rowId)) return null;
        Outcome? o;
        lock (Gate) Last.TryGetValue(rowId!, out o);
        if (o == null) return null;
        string current = o.Value;
        if (entry != null)
        {
            try { current = entry.GetSerializedValue(); } catch { /* keep the written value */ }
        }

        bool found = TryReadDiskValue(o.File, entry?.Definition.Section ?? SectionOf(o.RowId), entry?.Definition.Key ?? KeyOf(o.RowId), out string disk);
        // The disk side as BepInEx's Reload reads it (trimmed); the entry side as it is, so a value with
        // edge spaces (which the next reload would lose) does not read as persisted.
        bool onDisk = found && string.Equals(disk.Trim(), current, StringComparison.Ordinal);
        return new Dictionary<string, object?>
        {
            ["persisted"] = onDisk,
            ["how"] = o.How,
            ["why"] = o.Why,
            ["file"] = o.File,
            ["entryInstance"] = o.EntryInstance,
            ["fileInstance"] = o.FileInstance,
            ["entryInstanceNow"] = entry == null ? "" : InstanceId(entry),
            ["changed"] = o.Changed,
            ["valueLength"] = current.Length,
            ["diskFound"] = found,
            ["diskLength"] = found ? disk.Trim().Length : -1,
            ["ageMs"] = Clock() - o.At,
        };
    }

    internal static void ResetForTests()
    {
        lock (Gate) Last.Clear();
    }

    /// <summary>
    /// The raw value of <paramref name="section"/> / <paramref name="key"/> in the cfg at
    /// <paramref name="path"/>, as BepInEx's <c>Reload</c> finds it: a <c>[Section]</c> line, then
    /// <c>Key = value</c>. Also reads the one-line <c>[Section] Key = value</c> form of the test shim.
    /// </summary>
    internal static bool TryReadDiskValue(string? path, string section, string key, out string value)
    {
        value = "";
        if (string.IsNullOrEmpty(path)) return false;
        string[] lines;
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            lines = System.IO.File.ReadAllLines(path);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        // BepInEx's Reload keeps the last line that names a key, so the last one wins here too.
        bool found = false;
        string current = "";
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            string body = line;
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                int close = line.IndexOf(']');
                if (close < 0) continue;
                current = line.Substring(1, close - 1).Trim();
                body = line.Substring(close + 1).Trim();
                if (body.Length == 0) continue;
            }

            int eq = body.IndexOf('=');
            if (eq < 0) continue;
            if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
            if (!string.Equals(body.Substring(0, eq).Trim(), key, StringComparison.Ordinal)) continue;
            value = body.Substring(eq + 1).Trim();
            found = true;
        }

        return found;
    }

    private static string InstanceId(object? o) =>
        o == null ? "" : RuntimeHelpers.GetHashCode(o).ToString("x8");

    private static string SectionOf(string rowId)
    {
        var parts = rowId.Split('/');
        return parts.Length >= 3 ? parts[parts.Length - 2] : "";
    }

    private static string KeyOf(string rowId)
    {
        var parts = rowId.Split('/');
        return parts.Length >= 1 ? parts[parts.Length - 1] : "";
    }
}
