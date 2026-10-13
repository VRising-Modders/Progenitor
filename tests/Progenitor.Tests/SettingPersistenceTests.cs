using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SAVEHOLD1: a Personal setting that shares a cfg with a synced one. While joined, ConfigSync holds the
/// file's auto-save off (the synced key is locked to the server's value), so a page write used to stay in
/// memory until the disconnect: lost on a crash or quit while joined, and put back to the old disk value
/// by any reload of the file. The page write now saves the file explicitly — with the synced key's
/// pre-join local, never the server's value — and reports how it was saved.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class SettingPersistenceTests : IDisposable
{
    private const string Mod = "example.persist";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-persist-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigFile _file;
    private readonly List<IDisposable> _handles = new();

    public SettingPersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.persist.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        SettingPersistence.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var h in _handles) h.Dispose();
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        SettingPersistence.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The Sample's shape: a synced notice and two Personal values in one cfg, joined (notice locked).</summary>
    private (ConfigEntry<string> notice, ConfigEntry<string> notes, ConfigEntry<int> count) Joined()
    {
        var notice = _file.Bind("Menu", "ServerNotice", "local notice", "");
        var notes = _file.Bind("Menu", "Notes", "first", "");
        var count = _file.Bind("Menu", "DemoCount", 5, "");
        _handles.Add(ConfigSync.Register(Mod, notice));
        _file.Save();
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Mod, "Menu", "ServerNotice", "SERVER TEXT") }, true);
        Assert.True(ConfigSync.IsLocked(notice));
        Assert.True(ConfigSync.IsSaveHeldOff(_file));
        Assert.False(_file.SaveOnConfigSet);
        return (notice, notes, count);
    }

    private static string Disk(ConfigFile file, string key)
    {
        Assert.True(SettingPersistence.TryReadDiskValue(file.ConfigFilePath, "Menu", key, out var v), key + " is not in the cfg");
        return v;
    }

    [Fact]
    public void A_personal_write_while_joined_reaches_the_disk_with_the_synced_keys_local_value()
    {
        var (notice, notes, count) = Joined();
        int savesBefore = _file.SaveCount;

        var notesRow = SettingsRow.ForEntry(Mod, notes, synced: false);
        Assert.True(notesRow.TryWrite("spaced out", out var error), error);
        var countRow = SettingsRow.ForEntry(Mod, count, synced: false);
        Assert.True(countRow.TryWrite("99", out error), error);

        // Before the fix nothing was written until the disconnect (SaveOnConfigSet held off).
        Assert.Equal(savesBefore + 2, _file.SaveCount);
        Assert.Equal("spaced out", Disk(_file, "Notes"));
        Assert.Equal("99", Disk(_file, "DemoCount"));
        // The synced key keeps the player's own value on disk, never the server's.
        Assert.Equal("local notice", Disk(_file, "ServerNotice"));
        Assert.Equal("SERVER TEXT", notice.Value);

        var persist = SettingPersistence.Describe(notesRow.RowId, notes);
        Assert.NotNull(persist);
        Assert.Equal(true, persist!["persisted"]);
        Assert.Equal(SettingPersistence.SavedExplicitly, persist["how"]);
        Assert.Equal(_file.ConfigFilePath, persist["file"]);
        Assert.Equal(persist["entryInstance"], persist["entryInstanceNow"]);
    }

    [Fact]
    public void A_reload_while_joined_no_longer_loses_the_personal_write()
    {
        var (_, notes, _) = Joined();
        Assert.True(SettingsRow.ForEntry(Mod, notes, synced: false).TryWrite("kept", out var error), error);
        // An operator's edit of another line, or any watch event: BepInEx re-reads the whole file.
        _file.Reload();
        Assert.Equal("kept", notes.Value);
    }

    [Fact]
    public void The_disconnect_restore_still_writes_the_local_and_keeps_the_personal_value()
    {
        var (notice, notes, _) = Joined();
        Assert.True(SettingsRow.ForEntry(Mod, notes, synced: false).TryWrite("after join", out var error), error);
        ConfigSync.RestoreLocal();
        Assert.Equal("local notice", notice.Value);
        Assert.True(_file.SaveOnConfigSet);
        Assert.Equal("after join", Disk(_file, "Notes"));
        Assert.Equal("local notice", Disk(_file, "ServerNotice"));
    }

    [Fact]
    public void A_blur_with_no_edit_never_saves_over_a_pending_hand_edit()
    {
        var (_, notes, _) = Joined();
        var row = SettingsRow.ForEntry(Mod, notes, synced: false);
        Assert.True(row.TryWrite("typed", out var error), error);
        int saves = _file.SaveCount;

        // The operator edits the cfg by hand; the watch has not reloaded it yet when the player tabs
        // through the field without an edit. Nothing is written, so the hand edit survives.
        File.WriteAllText(_file.ConfigFilePath, File.ReadAllText(_file.ConfigFilePath).Replace("Notes = typed", "Notes = by hand"));
        Assert.True(row.TryWrite("typed", out error), error);
        Assert.Equal(saves, _file.SaveCount);
        Assert.Equal("by hand", Disk(_file, "Notes"));
        var persist = SettingPersistence.Describe(row.RowId, notes)!;
        Assert.Equal(SettingPersistence.Unchanged, persist["how"]);
        Assert.Equal(false, persist["persisted"]);
    }

    [Fact]
    public void A_session_lock_left_by_an_unloaded_plugin_still_saves_its_local_value()
    {
        var (notice, notes, _) = Joined();
        // The plugin unloads while joined: the lock lives on as a session lock and keeps the hold.
        _handles[0].Dispose();
        _handles.RemoveAt(0);
        Assert.True(ConfigSync.IsSaveHeldOff(_file));

        Assert.True(SettingsRow.ForEntry(Mod, notes, synced: false).TryWrite("after unload", out var error), error);
        Assert.Equal("after unload", Disk(_file, "Notes"));
        Assert.Equal("local notice", Disk(_file, "ServerNotice"));
        _ = notice;
    }

    [Fact]
    public void A_file_whose_synced_key_could_not_be_parked_is_not_saved()
    {
        var (_, notes, _) = Joined();
        // Model a failed park: the locked key is a live row again, so a Save would write the server value.
        var notice = _file.Bind("Menu", "ServerNotice", "x", "");
        Assert.True(_file.ContainsKey(new ConfigDefinition("Menu", "ServerNotice")));
        int saves = _file.SaveCount;
        var row = SettingsRow.ForEntry(Mod, notes, synced: false);
        Assert.True(row.TryWrite("risky", out var error), error);
        Assert.Equal(saves, _file.SaveCount);
        var persist = SettingPersistence.Describe(row.RowId, notes)!;
        Assert.Equal(false, persist["persisted"]);
        Assert.Equal("not-saved", persist["how"]);
        Assert.Contains("could not be parked", (string)persist["why"]!);
        _ = notice;
    }

    [Fact]
    public void Outside_a_session_the_write_is_saved_by_BepInEx_itself()
    {
        var notes = _file.Bind("Menu", "Notes", "first", "");
        var row = SettingsRow.ForEntry(Mod, notes, synced: false);
        Assert.True(row.TryWrite("plain", out var error), error);
        var persist = SettingPersistence.Describe(row.RowId, notes)!;
        Assert.Equal(SettingPersistence.SavedOnSet, persist["how"]);
        Assert.Equal(true, persist["persisted"]);
    }

    [Theory]
    [InlineData("## a comment\n[Menu]\n\n## Free notes.\n# Setting type: String\nNotes = spaced out\n\n[Other]\nNotes = no\n", "Menu", "Notes", "spaced out")]
    [InlineData("[Menu]\nDemoCount = 99\n", "Menu", "DemoCount", "99")]
    [InlineData("[Menu] Notes = shim form\n", "Menu", "Notes", "shim form")]
    [InlineData("[Menu]\nNotes = first\nNotes = last wins\n", "Menu", "Notes", "last wins")]
    public void The_disk_reader_finds_a_key_under_its_section(string text, string section, string key, string expected)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfg");
        File.WriteAllText(path, text);
        Assert.True(SettingPersistence.TryReadDiskValue(path, section, key, out var value));
        Assert.Equal(expected, value);
        Assert.False(SettingPersistence.TryReadDiskValue(path, "Missing", key, out _));
    }
}
