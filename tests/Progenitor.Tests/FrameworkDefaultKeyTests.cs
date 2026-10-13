using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// A framework default key must never take a chord a mod already uses (live finding 2026-10-02:
/// photo mode's Ctrl+F9 took the Sample's log-panel key, and the framework-first winner rule
/// deactivated the Sample's bind behind a conflict dialog). Both sides are read from source: every
/// <c>static readonly KeyChord Default* = new(...)</c> in Core is a framework default, and every
/// <c>Keybinds.Register(</c> call under <c>samples/</c> names its default chord. Framework keys in a
/// scope that never meets a gameplay key (the Mods menu's navigation keys, photo mode's in-mode keys)
/// are checked only against their own scope.
/// </summary>
public class FrameworkDefaultKeyTests
{
    /// <summary>Core defaults that live in a scope other than gameplay, and so cannot take a mod's gameplay key.</summary>
    private static readonly Dictionary<string, string> ScopedFrameworkDefaults = new(StringComparer.Ordinal)
    {
        ["DefaultNavigateBack"] = "Menu",
        ["DefaultNavigateForward"] = "Menu",
        ["DefaultHudKey"] = "Photo",
        ["DefaultExitKey"] = "Photo",
        ["DefaultLockKey"] = "Photo",
        ["DefaultPauseKey"] = "Photo",
        ["DefaultZoomInKey"] = "Photo",
        ["DefaultZoomOutKey"] = "Photo",
    };

    private static readonly Regex FrameworkDecl = new(
        @"static\s+readonly\s+KeyChord\s+(?<name>Default\w*)\s*=\s*new(?:\s*KeyChord)?\s*\(\s*(?:Progenitor\.Ui\.)?UiKey\.(?<key>\w+)\s*(?:,\s*(?<mods>[^)]*))?\)",
        RegexOptions.Compiled);

    private static readonly Regex ChordExpr = new(
        @"KeyChord\s*\(\s*(?:Progenitor\.Ui\.)?UiKey\.(?<key>\w+)\s*(?:,\s*(?<mods>[^)]*))?\)",
        RegexOptions.Compiled);

    private static KeyChord Chord(string key, string mods)
    {
        var k = (UiKey)Enum.Parse(typeof(UiKey), key);
        var m = KeyModifiers.None;
        foreach (Match t in Regex.Matches(mods ?? "", @"KeyModifiers\.(\w+)"))
            m |= (KeyModifiers)Enum.Parse(typeof(KeyModifiers), t.Groups[1].Value);
        return new KeyChord(k, m);
    }

    private static IEnumerable<string> Files(string root) =>
        Directory.EnumerateFiles(Path.Combine(SourcePin.RepoRoot(), root), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    internal static List<(string Name, KeyChord Chord, string Scope)> FrameworkDefaults()
    {
        var list = new List<(string, KeyChord, string)>();
        foreach (var f in Files(Path.Combine("src", "Progenitor.Core")))
            foreach (Match m in FrameworkDecl.Matches(File.ReadAllText(f)))
            {
                string name = m.Groups["name"].Value;
                list.Add((name, Chord(m.Groups["key"].Value, m.Groups["mods"].Value),
                    ScopedFrameworkDefaults.TryGetValue(name, out var scope) ? scope : "Gameplay"));
            }
        return list;
    }

    internal static List<(string Where, KeyChord Chord)> SampleDefaults()
    {
        var list = new List<(string, KeyChord)>();
        foreach (var f in Files("samples"))
        {
            string text = File.ReadAllText(f);
            // KEYOPEN1: an opener key (ctx.Keybinds.RegisterOpener) holds its default chord like any other bind.
            foreach (string call in new[] { "Keybinds.Register(", "Keybinds.RegisterOpener(" })
            {
                int at = 0;
                while ((at = text.IndexOf(call, at, StringComparison.Ordinal)) >= 0)
                {
                    string window = text.Substring(at, Math.Min(600, text.Length - at));
                    var m = ChordExpr.Match(window);
                    string where = Path.GetFileName(f) + ":" + (text.Take(at).Count(c => c == '\n') + 1);
                    Assert.True(m.Success, "Could not read the default chord of the " + call + " call at " + where
                        + "; teach FrameworkDefaultKeyTests its shape so the clash check still covers it.");
                    list.Add((where, Chord(m.Groups["key"].Value, m.Groups["mods"].Value)));
                    at += call.Length;
                }
            }
        }
        return list;
    }

    [Fact]
    public void The_scan_finds_the_known_defaults_on_both_sides()
    {
        var fw = FrameworkDefaults();
        Assert.Contains(fw, d => d.Name == "DefaultOpenKey" && d.Chord == new KeyChord(UiKey.F10));
        Assert.Contains(fw, d => d.Name == "DefaultReleaseKey" && d.Chord == new KeyChord(UiKey.F12, KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.Contains(fw, d => d.Name == "DefaultToggleKey" && d.Scope == "Gameplay");
        var samples = SampleDefaults();
        Assert.Contains(samples, s => s.Chord == new KeyChord(UiKey.F9, KeyModifiers.Ctrl));
        Assert.Contains(samples, s => s.Chord == new KeyChord(UiKey.F6, KeyModifiers.Ctrl));
        Assert.Contains(samples, s => s.Chord == new KeyChord(UiKey.F8));
        foreach (var name in ScopedFrameworkDefaults.Keys)
            Assert.Contains(fw, d => d.Name == name);
    }

    [Fact]
    public void No_gameplay_framework_default_takes_a_key_a_sample_or_plugin_uses_by_default()
    {
        var samples = SampleDefaults();
        var clashes = new List<string>();
        foreach (var d in FrameworkDefaults().Where(d => d.Scope == "Gameplay" && d.Chord.IsBound))
            foreach (var s in samples.Where(s => s.Chord == d.Chord))
                clashes.Add(d.Name + " (" + d.Chord + ") clashes with " + s.Where);
        Assert.True(clashes.Count == 0,
            "A framework default must not take a chord a mod already holds; pick a free default or register it unbound:\n  "
            + string.Join("\n  ", clashes));
    }

    [Fact]
    public void Gameplay_framework_defaults_do_not_clash_with_each_other()
    {
        var fw = FrameworkDefaults().Where(d => d.Scope == "Gameplay" && d.Chord.IsBound).ToList();
        var dup = fw.GroupBy(d => d.Chord).Where(g => g.Count() > 1).Select(g => string.Join(", ", g.Select(d => d.Name))).ToList();
        Assert.True(dup.Count == 0, "Framework defaults on one chord: " + string.Join("; ", dup));
    }

    [Fact]
    public void Photo_mode_keys_are_distinct_among_themselves()
    {
        // The in-mode keys only meet each other (KeybindScope.Photo): HUD, pause, zoom and the flight table.
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        var keys = FrameworkDefaults().Where(d => d.Scope == "Photo").Select(d => d.Chord).ToList();
        foreach (Match m in Regex.Matches(src, @"\(FlightKey\.\w+,\s*""\w+"",\s*UiKey\.(\w+),"))
            keys.Add(new KeyChord((UiKey)Enum.Parse(typeof(UiKey), m.Groups[1].Value)));
        Assert.Equal(14, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
