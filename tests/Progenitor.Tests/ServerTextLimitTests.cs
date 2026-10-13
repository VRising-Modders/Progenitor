using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TEXTLIMIT1: the server checks every administrator edit of a server-wide text against the setting's
/// own declared limit — its own declaration, never one a client sends — and the transport caps:
/// 2,048 UTF-8 bytes in the edit packet and 4,096 escaped bytes in ConfigSync. Line breaks survive the
/// edit packet, the cfg escape and the ConfigSync packet unchanged. Game-free: the ConfigFile shim and
/// the real wire encoders.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ServerTextLimitTests : IDisposable
{
    private const string Mod = "example.mod";
    private const string Row = Mod + "/General/Notice";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-text-limit-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigFile _file;
    private readonly List<string> _audit = new();
    private readonly List<IDisposable> _handles = new();
    private byte[]? _sent;

    public ServerTextLimitTests()
    {
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestClock = () => 0;
        ServerSettings.TestSend = bytes => { _sent = bytes; return true; };
        ServerSettings.Audit = _audit.Add;
    }

    public void Dispose()
    {
        foreach (var h in _handles) h.Dispose();
        ServerSettings.Uninstall();
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A synced, admin-editable string, declared a text area of <paramref name="maxLength"/> characters (null: the default).</summary>
    private ConfigEntry<string> Notice(int? maxLength = 1000, bool multiline = true, bool declare = true, bool client = false)
    {
        var entry = _file.Bind("General", "Notice", "", "");
        _handles.Add(ConfigSync.Register(Mod, entry));
        _handles.Add(ServerSettings.Register(Mod, entry));
        if (declare) _handles.Add(ServerSettings.DeclareText(Row, maxLength, multiline));
        if (client) ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Mod, "General", "Notice", "") }, true);
        return entry;
    }

    private static ServerSettingsMessage Change(string value, string expected = "") =>
        new(Guid.NewGuid(), Row, "System.String", expected, value, false, "");

    private static ServerSettingResult Admin(ServerSettingsMessage request) => ServerSettings.Apply(request, handshakeOk: true, isAdmin: true);

    // ---- the server's own limit ---------------------------------------------------------------------

    [Fact]
    public void A_value_at_the_limit_is_stored_and_one_over_is_refused_with_the_limit_in_words()
    {
        var entry = Notice(maxLength: 1000);
        var at = Admin(Change(new string('a', 1000)));
        Assert.True(at.Success, at.Error);
        Assert.Equal(1000, entry.Value.Length);

        var over = Admin(Change(new string('b', 1001), expected: entry.Value));
        Assert.False(over.Success);
        Assert.Equal("The value is longer than 1,000 characters (1,001); shorten it to 1,000.", over.Error);
        Assert.Equal(new string('a', 1000), entry.Value);
        Assert.True(ServerSettings.IsValueRefusal(over.Error));

        // Both decisions are audited; the refusal carries its reason.
        Assert.Equal(2, _audit.Count);
        Assert.Contains(" settings accepted ", _audit[0]);
        Assert.Contains(" settings refused ", _audit[1]);
        Assert.EndsWith(": The value is longer than 1,000 characters (1,001); shorten it to 1,000.", _audit[1]);
    }

    [Fact]
    public void Multi_byte_characters_count_once_for_the_limit_but_their_bytes_must_fit_the_packet()
    {
        var entry = Notice(maxLength: 1000);
        // 1,000 two-byte characters: 1,000 characters, 2,000 bytes. Fits both.
        string accented = new string('é', 1000);
        Assert.True(Admin(Change(accented)).Success);
        Assert.Equal(accented, entry.Value);

        // 700 three-byte characters: under the character limit, over the 2,048-byte packet.
        string euros = new string('€', 700);
        var refused = Admin(Change(euros, expected: accented));
        Assert.False(refused.Success);
        Assert.StartsWith("The value is too long to send: 2,100 of 2,048 bytes", refused.Error);
        Assert.Equal(accented, entry.Value);

        // A character outside the Basic Multilingual Plane is two UTF-16 units and four bytes.
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 500));
        Assert.Equal(1000, emoji.Length);
        Assert.Equal(2000, ServerSettingsLimits.Utf8Bytes(emoji));
        Assert.True(Admin(Change(emoji, expected: accented)).Success);
    }

    [Fact]
    public void The_server_uses_its_own_declaration_not_a_larger_limit_the_client_held()
    {
        // The client side believed the row holds 5,000 characters and its own check let 1,500 through.
        var entry = Notice(maxLength: 5000, client: true);
        using var lease = ServerSettings.Request(Row, new string('x', 1500), "", _ => { });
        Assert.NotNull(_sent);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, response: false, out var request));
        // No limit travels: the packet carries the row, type, expected and requested value only.
        Assert.Equal(1500, request.Value.Length);

        // The server's own declaration says 1,000; it is the one applied.
        _handles.Add(ServerSettings.DeclareText(Row, 1000, multiline: true));
        var result = Admin(request);
        Assert.False(result.Success);
        Assert.Equal("The value is longer than 1,000 characters (1,500); shorten it to 1,000.", result.Error);
        Assert.Equal("", entry.Value);
    }

    [Fact]
    public void A_client_that_skips_its_check_is_still_refused_and_answered()
    {
        var entry = Notice(maxLength: 1000, client: true);
        string? error = null;
        using var lease = ServerSettings.RequestCore(Row, new string('y', 1200), "", r => error = r.Error, clientCheck: false);
        Assert.Null(error);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, response: false, out var request));
        var result = Admin(request);
        Assert.Equal("The value is longer than 1,000 characters (1,200); shorten it to 1,000.", result.Error);

        // The answer goes back through the response packet and completes the request.
        var response = ServerSettingsWire.Encode(request.WithResult(result), response: true);
        Assert.True(ServerSettingsWire.TryDecode(response, response: true, out var decoded));
        ServerSettings.AcceptResponse(decoded);
        Assert.Equal("The value is longer than 1,000 characters (1,200); shorten it to 1,000.", error);
        Assert.Equal("", entry.Value);
    }

    [Fact]
    public void An_over_long_packet_field_is_decoded_so_the_server_refuses_and_audits_it()
    {
        Notice(maxLength: 1000);
        // Built by hand: the encoder itself refuses a field over 2,048 bytes, a hostile client need not.
        string value = new string('€', 1000); // 3,000 bytes
        var packet = RawRequest(Row, "System.String", "", value);
        Assert.True(ServerSettingsWire.TryDecode(packet, response: false, out var request));
        Assert.Equal(value, request.Value);
        var result = Admin(request);
        Assert.False(result.Success);
        Assert.Equal("The value is too long to send: 3,000 of 2,048 bytes (an accented or non-Latin character takes 2 to 4).", result.Error);
        Assert.Single(_audit);
        Assert.Contains("refused", _audit[0]);
        // The row id and type stay at the field limit.
        Assert.False(ServerSettingsWire.TryDecode(RawRequest(new string('r', 2049), "System.String", "", "x"), false, out _));
    }

    [Fact]
    public void A_row_nobody_declared_is_a_single_line_field_of_256_characters()
    {
        Notice(declare: false);
        Assert.Equal(256, ServerSettings.EffectiveTextLimit(Row));
        Assert.True(Admin(Change(new string('a', 256))).Success);
        Assert.Equal("The value is longer than 256 characters (257); shorten it to 256.", Admin(Change(new string('a', 257), new string('a', 256))).Error);
    }

    [Fact]
    public void The_client_refuses_before_sending_in_the_servers_words()
    {
        Notice(maxLength: 1000, client: true);
        string? error = null;
        using var lease = ServerSettings.Request(Row, new string('z', 1001), "", r => error = r.Error);
        Assert.Null(_sent);
        Assert.Equal("The value is longer than 1,000 characters (1,001); shorten it to 1,000.", error);
        Assert.Equal(0, ServerSettings.PendingCount);
    }

    [Fact]
    public void Control_characters_are_still_refused_and_line_breaks_and_tabs_are_not()
    {
        var entry = Notice();
        Assert.Equal(ServerSettings.ControlCharactersError, Admin(Change("bell\u0007")).Error);
        string text = "Line one\nLine two\r\nTabbed\tend\n";
        Assert.True(Admin(Change(text)).Success);
        Assert.Equal(text, entry.Value);
    }

    [Fact]
    public void Withdrawing_a_declaration_restores_the_default_and_a_replaced_one_stays()
    {
        var first = ServerSettings.DeclareText(Row, 100, multiline: true);
        var second = ServerSettings.DeclareText(Row, 900, multiline: true);
        first.Dispose();
        Assert.Equal(900, ServerSettings.EffectiveTextLimit(Row));
        second.Dispose();
        Assert.Equal(ModSettingsLayout.DefaultTextMaxLength, ServerSettings.EffectiveTextLimit(Row));
    }

    // ---- the transports -----------------------------------------------------------------------------

    [Theory]
    [InlineData(null, false, 256, 256)]
    [InlineData(null, true, 4096, 2048)]
    [InlineData(1000, true, 1000, 1000)]
    [InlineData(3000, false, 3000, 2048)]
    public void The_effective_limit_is_the_declared_one_capped_at_the_packet(int? max, bool multiline, int declared, int effective)
    {
        Assert.Equal(declared, ServerSettingsLimits.Declared(max, multiline));
        Assert.Equal(effective, ServerSettingsLimits.Effective(max, multiline));
    }

    [Fact]
    public void A_value_that_fits_the_packet_always_fits_ConfigSync_after_the_cfg_escape()
    {
        // The worst case for the escape: every character a one-byte character BepInEx escapes to two.
        string worst = new string('\n', ServerSettingsLimits.WireTextBytes);
        Assert.Null(ServerSettingsLimits.Check(worst, 4096, true));
        Assert.Equal(ServerSettingsLimits.SyncFieldBytes, ServerSettingsLimits.EscapedBytes(worst));

        var random = new Random(1234);
        string[] pool = { "a", "b", " ", "\n", "\r", "\t", "\"", "'", "\\", "\0", "é", "€", "\U0001F600", "." };
        for (int trial = 0; trial < 300; trial++)
        {
            var sb = new StringBuilder();
            int len = random.Next(0, 2048);
            while (sb.Length < len) sb.Append(pool[random.Next(pool.Length)]);
            string s = sb.ToString();
            if (ServerSettingsLimits.Utf8Bytes(s) > ServerSettingsLimits.WireTextBytes) continue;
            Assert.True(ServerSettingsLimits.EscapedBytes(s) <= ServerSettingsLimits.SyncFieldBytes, "escaped " + ServerSettingsLimits.EscapedBytes(s));
            var record = new ConfigSyncRecord(Mod, "General", "Notice", ServerSettingsLimits.TomlEscape(s));
            Assert.True(ConfigSyncWire.TryMeasure(record, out _, out var error), error);
        }
    }

    [Fact]
    public void A_multi_line_value_round_trips_the_edit_packet_the_cfg_escape_and_ConfigSync()
    {
        string text = "Welcome!\nRules:\r\n\t1. Be kind.\n\"Quotes\" and 'apostrophes'\n\nÉtoile € \U0001F600\n";

        // Client -> server: the edit packet.
        var request = Change(text);
        Assert.True(ServerSettingsWire.TryDecode(ServerSettingsWire.Encode(request, response: false), response: false, out var decoded));
        Assert.Equal(text, decoded.Value);

        // Server -> cfg: BepInEx's escape and unescape (mirrored from BepInEx.Core's TomlTypeConverter).
        string toml = ServerSettingsLimits.TomlEscape(text);
        Assert.DoesNotContain('\n', toml);
        Assert.DoesNotContain('\r', toml);
        Assert.Equal(text, ServerSettingsLimits.TomlUnescape(toml));

        // Server -> clients: the ConfigSync packet carries the escaped form.
        var packets = ConfigSyncWire.Pack(new[] { new ConfigSyncRecord(Mod, "General", "Notice", toml) }, snapshot: true);
        Assert.Single(packets);
        Assert.True(ConfigSyncWire.TryRead(packets[0], out _, out _, out var records, out var readError), readError);
        Assert.Equal(text, ServerSettingsLimits.TomlUnescape(records.Single().Toml));

        // And the server's own write keeps it whole.
        var entry = Notice();
        Assert.True(Admin(Change(text)).Success);
        Assert.Equal(text, entry.Value);
    }

    [Fact]
    public void The_escape_mirror_keeps_BepInExs_backslash_quirk()
    {
        // BepInEx writes a backslash as itself, so a typed backslash-n reads back as a line break
        // (docs/mod-settings.md, BepInEx cfg round trip).
        Assert.Equal("a\\nb", ServerSettingsLimits.TomlEscape("a\\nb"));
        Assert.Equal("a\nb", ServerSettingsLimits.TomlUnescape("a\\nb"));
        Assert.Equal("\\\"x\\\"", ServerSettingsLimits.TomlEscape("\"x\""));
    }

    [Theory]
    [InlineData("The value is longer than 1,000 characters (1,001); shorten it to 1,000.", true)]
    [InlineData("The value contains control characters.", true)]
    [InlineData("Only server administrators can change this setting.", false)]
    [InlineData("This setting changed on the server. Review the current value and try again.", false)]
    [InlineData("", false)]
    public void Value_refusals_are_told_apart_from_request_refusals(string error, bool value) =>
        Assert.Equal(value, ServerSettings.IsValueRefusal(error));

    /// <summary>
    /// A backslash before an escape letter does not survive BepInEx's cfg escape (it is written as is
    /// and read back as a control character), so the server and the client refuse it rather than store
    /// a value ConfigSync and the next load would change. A lone backslash or one before another
    /// character round-trips and is accepted.
    /// </summary>
    [Theory]
    [InlineData("C:\\new\\tools", false)]
    [InlineData("tab\\there", false)]
    [InlineData("two\\\\slashes", false)]
    [InlineData("zero\\0", false)]
    [InlineData("quote\\\"", true)]
    [InlineData("C:\\Games\\VRising", true)]
    [InlineData("ends with \\", true)]
    [InlineData("a \\ b", true)]
    [InlineData("line\nbreak", true)]
    public void A_value_the_cfg_escape_would_change_is_refused_before_it_is_stored(string text, bool survives)
    {
        Assert.Equal(survives, ServerSettingsLimits.SurvivesCfg(text));
        var entry = Notice(maxLength: 1000);
        var result = Admin(Change(text));
        Assert.Equal(survives, result.Success);
        if (!survives)
        {
            Assert.Equal(ServerSettingsLimits.BackslashError, result.Error);
            Assert.True(ServerSettings.IsValueRefusal(result.Error));
            Assert.Equal("", entry.Value);
        }
        else Assert.Equal(text, entry.Value);
    }

    [Fact]
    public void The_client_refuses_a_backslash_escape_before_sending()
    {
        Notice(maxLength: 1000, client: true);
        string? error = null;
        using var lease = ServerSettings.Request(Row, "C:\\new", "", r => error = r.Error);
        Assert.Null(_sent);
        Assert.Equal(ServerSettingsLimits.BackslashError, error);
    }

    [Fact]
    public void The_newest_live_declaration_applies_and_withdrawing_it_falls_back()
    {
        var page = ServerSettings.DeclareText(Row, 100, multiline: true);
        var binding = ServerSettings.DeclareText(Row, 900, multiline: false);
        Assert.Equal((900, false), ServerSettings.TextLimitFor(Row));
        binding.Dispose();
        Assert.Equal((100, true), ServerSettings.TextLimitFor(Row));
        binding.Dispose();
        Assert.Equal((100, true), ServerSettings.TextLimitFor(Row));
        page.Dispose();
        Assert.Equal(((int?)null, false), ServerSettings.TextLimitFor(Row));
    }

    [Fact]
    public void The_admins_help_line_says_the_typed_text_is_kept_after_a_refusal()
    {
        Assert.Equal("Only server administrators can change this setting. " + ModSettingsView.KeptNote,
            ModSettingsView.RefusalNote(ServerSettings.NotAdminError, "typed", "stored"));
        Assert.Equal("This setting changed on the server.", ModSettingsView.RefusalNote("This setting changed on the server.", "same", "same"));
        Assert.Equal("The request failed. " + ModSettingsView.KeptNote, ModSettingsView.RefusalNote("", "a", "b"));
    }

    // ---- TEXTUNDO1 polish: the commit trim ----------------------------------------------------------

    [Fact]
    public void The_server_trims_before_its_length_check_so_the_stored_value_is_what_the_cfg_keeps()
    {
        var entry = Notice(maxLength: 1000);
        // 1,000 characters ending in a space: the live 999 case. Stored trimmed, answered trimmed.
        string typed = new string('a', 999) + " ";
        var result = Admin(Change(typed));
        Assert.True(result.Success, result.Error);
        Assert.Equal(999, entry.Value.Length);
        Assert.Equal(entry.Value, result.Value);

        // 1,000 characters plus surrounding spaces still fits: the check counts the trimmed text.
        string padded = "  " + new string('b', 1000) + "  ";
        var fits = Admin(Change(padded, expected: entry.Value));
        Assert.True(fits.Success, fits.Error);
        Assert.Equal(new string('b', 1000), entry.Value);

        // Line breaks and tabs at the ends survive: the cfg escapes them.
        var kept = Admin(Change("\tnote\n", expected: entry.Value));
        Assert.True(kept.Success, kept.Error);
        Assert.Equal("\tnote\n", entry.Value);
    }

    [Fact]
    public void The_client_sends_the_trimmed_value()
    {
        Notice(maxLength: 1000, client: true);
        using var lease = ServerSettings.Request(Row, "  Maintenance at 22:00  ", "", _ => { });
        Assert.NotNull(_sent);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, response: false, out var request));
        Assert.Equal("Maintenance at 22:00", request.Value);
    }

    /// <summary>A request packet written field by field, as a client that skipped every check could send it.</summary>
    private static byte[] RawRequest(string row, string type, string expected, string value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(Guid.NewGuid().ToByteArray());
        foreach (var field in new[] { row, type, expected, value })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
