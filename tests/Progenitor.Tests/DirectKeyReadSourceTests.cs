using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Enforces the keybind rule (docs/modules.md "Keybinds"): every key a mod or framework tool acts
/// on is registered through <c>ctx.Keybinds</c> / <c>Keybinds.Register</c>, so it appears in the
/// Keybinds tab, can be remapped and takes part in conflict resolution. Nothing outside the
/// framework's own input layer polls <c>UnityEngine.Input</c>, the Input System's
/// <c>Keyboard</c> / <c>Mouse</c> devices, or the game's input state directly.
///
/// <para><b>Scanned roots.</b> <c>src/</c> (Core, Bootstrap, Preloader and the dev bridge
/// <c>src/Progenitor.Dev</c>, which is not exempt) and <c>samples/</c> — every
/// tree that compiles into an assembly loaded by the game. <c>tools/</c> is not scanned: it holds
/// offline generators and PowerShell harnesses (the OS-level input injection in
/// <c>tools/observe.ps1</c> is the explicit, user-approved foreground test path) plus
/// <c>tools/scratch</c> one-shot diagnostic probes that are never shipped as a mod.
/// <c>tests/</c> is not scanned because it names these patterns as data.</para>
///
/// <para><b>What counts as code.</b> Comments (<c>//</c>, <c>///</c>, <c>/* … */</c>) and the
/// contents of string and character literals are blanked before matching, so prose and log text
/// that names <c>Keyboard.current</c> never trips the scan. Code inside an interpolation hole
/// (<c>$"{Keyboard.current}"</c>) is blanked too; that is a known residual.</para>
/// </summary>
public class DirectKeyReadSourceTests
{
    /// <summary>
    /// The framework's own input layer: the only files allowed to read devices directly. Each
    /// entry must still contain a direct read (a stale entry fails
    /// <see cref="Every_allowlisted_file_exists_and_still_reads_input"/>), so the list stays exact.
    /// Paths are repository-relative with forward slashes.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Progenitor.Core/Ui/UnityKeyInputSource.cs"] =
            "The keybind input pump (WasPressedThisFrame, read by Keybinds.Pump) and the key-capture picker "
            + "(TryReadPressedChord, the Keybinds tab's press-a-key flow); PAD1: the same for controller buttons on "
            + "Gamepad.current (TryReadGamepadHeld, the controller capture and chord match).",
        ["src/Progenitor.Core/Ui/UnityUiRuntime.cs"] =
            "The UI kit: the typed-key pump for its text fields (PumpTypedKeys) and the pointer/wheel "
            + "sample that drives hover, drag and scroll on mod panels (SamplePointer, PumpShownInputBody), "
            + "the primary-button sample Ui.WatchPointer attaches to a PointerDown (PrimaryPointerHeld), "
            + "the Shift and Ctrl held at a click for the widgets' multi-select (HeldClickModifiers; it binds no action), "
            + "and NAV1's controller and arrow-key sample for panel navigation (SampleNav, read by UiNav.Tick). "
            + "BRIDGEUI1: each of these reads the dev bridge's synthetic input (UiSyntheticInput) instead while its lease "
            + "is held, and PhysicalInputUsed reads the devices only to end that lease when the player uses them.",
        ["src/Progenitor.Dev/Ops/NavOps.cs"] =
            "NAV1: the dev bridge's nav op writes input: it adds a virtual gamepad and queues its state events, "
            + "and reads Gamepad.current only to report whether that pad is the current one. It binds no action.",
        ["src/Progenitor.Core/Ui/NativeModsEntry.cs"] =
            "The UI kit: pointer-position hover probe for the Mods entry on the title screen.",
        ["src/Progenitor.Core/Inputs/UnityInputRuntime.cs"] =
            "ctx.Input's user-activity sample (SampleActivity): detects the player moving or clicking so an "
            + "input lease can yield to them; and photo mode's pointer sample (TrySamplePointer: right-button "
            + "look, mouse movement, wheel). It binds no action to a key.",
        ["src/Progenitor.Bootstrap/UiInputContext.cs"] =
            "Bootstrap's process-lifetime Escape/Back forwarder that closes mod panels; it must outlive Core "
            + "generations, so it cannot sit on the Core keybind registry.",
        ["src/Progenitor.Dev/Ops/KeyInjectOps.cs"] =
            "The dev bridge's key op writes input: it queues keyboard state and text events for "
            + "Keyboard.current (the device the events are for) and reads that keyboard's isPressed only to "
            + "report whether its own queued press was applied. It binds no action to a key.",
        ["src/Progenitor.Dev/Ops/MouseInjectOps.cs"] =
            "The dev bridge's mouse op writes input: it queues mouse state events for Mouse.current (the device "
            + "the events are for) and reads that mouse's position and isPressed only to report whether its own "
            + "queued move and press were applied. It binds no action to a key or button.",
        ["src/Progenitor.Bootstrap/TextInputQueue.cs"] =
            "TEXTINPUT1: Bootstrap's process-lifetime subscription to Keyboard.onTextInput for the UI kit's "
            + "text fields (typed characters, not key binds); a converted delegate must not target collectible Core.",
    };

    /// <summary>Roots scanned, repository-relative.</summary>
    internal static readonly string[] Roots = { "src", "samples", "templates" };

    [Fact]
    public void No_file_outside_the_framework_input_layer_reads_keys_directly()
    {
        string repo = SourcePin.RepoRoot();
        var offenders = new List<string>();
        foreach (string file in ScannedFiles(repo))
        {
            string relative = Relative(repo, file);
            if (Allowlist.ContainsKey(relative)) continue;
            foreach (var hit in DirectKeyReadScanner.Scan(File.ReadAllText(file)))
                offenders.Add(relative + ":" + hit.Line + ": " + hit.Match);
        }

        Assert.True(
            offenders.Count == 0,
            "Direct key/device reads outside the framework input layer. Register the key through "
            + "ctx.Keybinds.Register (or Keybinds.Register for framework code) instead:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_allowlisted_file_exists_and_still_reads_input()
    {
        string repo = SourcePin.RepoRoot();
        var stale = new List<string>();
        foreach (var entry in Allowlist)
        {
            string path = Path.Combine(repo, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) stale.Add(entry.Key + " (missing)");
            else if (DirectKeyReadScanner.Scan(File.ReadAllText(path)).Count == 0) stale.Add(entry.Key + " (no direct read left)");
        }

        Assert.True(stale.Count == 0, "Remove stale allowlist entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Scanned_roots_exist_and_contain_source()
    {
        string repo = SourcePin.RepoRoot();
        foreach (string root in Roots)
            Assert.True(Directory.Exists(Path.Combine(repo, root)), root + "/ not found under " + repo);
        Assert.Contains(ScannedFiles(repo), f => Relative(repo, f).StartsWith("samples/", StringComparison.Ordinal));
        Assert.Contains(ScannedFiles(repo), f => Relative(repo, f).StartsWith("src/Progenitor.Dev/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("if (Input.GetKeyDown(KeyCode.F5)) Toggle();", "Input.GetKeyDown")]
    [InlineData("if (UnityEngine.Input.GetKey(KeyCode.LeftShift)) run = true;", "Input.GetKey")]
    [InlineData("bool b = Input.GetButtonDown(\"Fire1\");", "Input.GetButtonDown")]
    [InlineData("if (Input.GetMouseButtonDown(0)) Click();", "Input.GetMouseButtonDown")]
    [InlineData("if (Input.anyKeyDown) Wake();", "Input.anyKeyDown")]
    [InlineData("var s = Input.inputString;", "Input.inputString")]
    [InlineData("var kb = Keyboard.current;", "Keyboard.current")]
    [InlineData("if (UnityEngine.InputSystem.Keyboard.current.f5Key.wasPressedThisFrame) Go();", "Keyboard.current")]
    [InlineData("var m = Mouse.current; if (m.rightButton.isPressed) Aim();", "Mouse.current")]
    [InlineData("foreach (var d in InputSystem.devices) Poll(d);", "InputSystem.devices")]
    [InlineData("var g = InputSystem.GetDevice<Keyboard>();", "InputSystem.GetDevice")]
    [InlineData("InputSystem.onAnyButtonPress.Call(OnPress);", "InputSystem.onAnyButtonPress")]
    [InlineData("InputSystem.onEvent += OnEvent;", "InputSystem.onEvent")]
    [InlineData("if (state.IsInputPressed(ButtonInputAction.Interact)) Use();", ".IsInputPressed(")]
    [InlineData("if (state.IsInputDown((ButtonInputAction)7)) Use();", ".IsInputDown(")]
    [InlineData("if (Event.current.keyCode == KeyCode.F5) Go();", "Event.current")]
    [InlineData("int x = 1; /* note */ var kb = Keyboard.current;", "Keyboard.current")]
    [InlineData("Log(\"a\" + \"b\"); var kb = Keyboard.current;", "Keyboard.current")]
    public void Planted_violations_are_reported(string code, string expected)
    {
        var hits = DirectKeyReadScanner.Scan("class C {\n  void M() {\n    " + code + "\n  }\n}\n");
        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.Line);
        Assert.Contains(expected.TrimStart('.').TrimEnd('('), hit.Match, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// if (Input.GetKeyDown(KeyCode.F5)) Toggle();")]
    [InlineData("/// Reads <c>Keyboard.current</c> once per frame.")]
    [InlineData("/* var kb = Keyboard.current; */")]
    [InlineData("Log(\"falls back to Keyboard.current Escape\");")]
    [InlineData("Log(@\"Mouse.current \"\"quoted\"\" text\");")]
    [InlineData("Log($\"value {x} from Input.GetKey is not used\");")]
    [InlineData("Log(\"\"\"raw Keyboard.current text\"\"\");")]
    [InlineData("var c = '\"'; var s = \"Keyboard.current\";")]
    [InlineData("ctx.Keybinds.Register(\"Toggle\", new KeyChord(UiKey.F5), \"Toggle.\", Toggle);")]
    [InlineData("var result = ctx.Input.TryAcquire(options, out var lease);")]
    [InlineData("using UnityEngine.InputSystem;")]
    [InlineData("using GameKey = UnityEngine.InputSystem.Key;")]
    [InlineData("try { mode = InputSystem.settings.updateMode.ToString(); } catch { }")]
    [InlineData("var e = SomeEvent.current; var k = MyKeyboard.current;")]
    public void Comments_literals_and_the_keybind_api_are_not_reported(string code)
    {
        Assert.Empty(DirectKeyReadScanner.Scan("class C {\n  void M() {\n    " + code + "\n  }\n}\n"));
    }

    [Fact]
    public void A_block_comment_spanning_lines_hides_its_contents_and_keeps_line_numbers()
    {
        string source = "/*\n var kb = Keyboard.current;\n*/\nvar m = Mouse.current;\n";
        var hit = Assert.Single(DirectKeyReadScanner.Scan(source));
        Assert.Equal(4, hit.Line);
        Assert.Equal("Mouse.current", hit.Match);
    }

    [Fact]
    public void Every_offending_line_is_reported()
    {
        string source = "var a = Keyboard.current;\nvar b = 1;\nif (Input.GetKey(KeyCode.A)) { }\nvar c = Mouse.current; var d = Keyboard.current;\n";
        var hits = DirectKeyReadScanner.Scan(source);
        Assert.Equal(new[] { 1, 3, 4, 4 }, hits.Select(h => h.Line).ToArray());
    }

    private static IEnumerable<string> ScannedFiles(string repo)
    {
        foreach (string root in Roots)
        {
            string dir = Path.Combine(repo, root);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Relative(repo, file);
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                    continue;
                yield return file;
            }
        }
    }

    private static string Relative(string repo, string file) =>
        Path.GetRelativePath(repo, file).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
/// The matcher behind <see cref="DirectKeyReadSourceTests"/>: blanks comments and literal
/// contents (keeping newlines, so line numbers hold), then reports every direct key or device
/// read on the remaining code.
/// </summary>
internal static class DirectKeyReadScanner
{
    internal readonly record struct Hit(int Line, string Match);

    private static readonly Regex Pattern = new(
        @"\bInput\.(?:GetKey(?:Down|Up)?|GetButton(?:Down|Up)?|GetMouseButton(?:Down|Up)?|anyKey(?:Down)?|inputString)\b"
        + @"|\b(?:Keyboard|Mouse|Gamepad|Joystick)\.current\b"
        + @"|\bInputSystem\.(?:devices|GetDevice|onEvent|onAnyButtonPress)\b"
        + @"|\.IsInput(?:Pressed|Down|Up)\s*\("
        + @"|\bEvent\.current\b",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<Hit> Scan(string source)
    {
        string code = CodeOnly(source);
        var hits = new List<Hit>();
        string[] lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            foreach (Match m in Pattern.Matches(lines[i]))
                hits.Add(new Hit(i + 1, m.Value));
        return hits;
    }

    /// <summary>
    /// <paramref name="source"/> with comments and the contents of string/char literals replaced
    /// by spaces. Newlines are kept everywhere so a hit's line number is the source line.
    /// Interpolated strings are treated as whole literals, holes included.
    /// </summary>
    internal static string CodeOnly(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < source.Length) { sb.Append("  "); i += 2; }
                continue;
            }

            // Prefixes: $, @, $@, @$ (any number of $ for raw interpolated literals).
            int start = i;
            bool verbatim = false;
            while (i < source.Length && (source[i] == '$' || source[i] == '@'))
            {
                if (source[i] == '@') verbatim = true;
                i++;
            }

            if (i < source.Length && source[i] == '"')
            {
                sb.Append(' ', i - start);
                if (i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
                {
                    // Raw literal: count the opening quotes, then find the same run.
                    int q = 0;
                    while (i < source.Length && source[i] == '"') { q++; i++; }
                    sb.Append(' ', q);
                    string close = new string('"', q);
                    int end = source.IndexOf(close, i, StringComparison.Ordinal);
                    if (end < 0) end = source.Length;
                    Blank(sb, source, i, end);
                    i = Math.Min(source.Length, end + q);
                    sb.Append(' ', Math.Max(0, i - end));
                    continue;
                }

                sb.Append(' ');
                i++;
                while (i < source.Length)
                {
                    char d = source[i];
                    if (verbatim)
                    {
                        if (d == '"' && i + 1 < source.Length && source[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                        if (d == '"') break;
                    }
                    else
                    {
                        if (d == '\\' && i + 1 < source.Length) { sb.Append("  "); i += 2; continue; }
                        if (d == '"' || d == '\n') break;
                    }
                    sb.Append(d == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < source.Length && source[i] == '"') { sb.Append(' '); i++; }
                continue;
            }

            if (i > start)
            {
                // A $ or @ that did not open a literal (an identifier like @class): keep it.
                sb.Append(source, start, i - start);
                continue;
            }

            if (c == '\'')
            {
                sb.Append(' ');
                i++;
                while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { sb.Append("  "); i += 2; continue; }
                    sb.Append(' ');
                    i++;
                }
                if (i < source.Length && source[i] == '\'') { sb.Append(' '); i++; }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static void Blank(StringBuilder sb, string source, int from, int to)
    {
        for (int k = from; k < to && k < source.Length; k++)
            sb.Append(source[k] == '\n' ? '\n' : ' ');
    }
}
