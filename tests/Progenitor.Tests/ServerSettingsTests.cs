using System;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

[Collection("HarmonySlotHandoff")]
public sealed class ServerSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-server-settings-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigFile _file;
    private const string Row = "example.mod/General/Count";
    private byte[]? _sent;
    private double _now;

    public ServerSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestClock = () => _now;
        ServerSettings.TestSend = bytes => { _sent = bytes; return true; };
    }

    public void Dispose()
    {
        ServerSettings.Uninstall();
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ConfigEntry<int> Entry(bool client = false)
    {
        var entry = _file.Bind("General", "Count", 3, "");
        ConfigSync.Register("example.mod", entry);
        ServerSettings.Register("example.mod", entry);
        if (client) ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord("example.mod", "General", "Count", "3") }, true);
        return entry;
    }

    private static ServerSettingsMessage Change(string expected = "3", string value = "5", string row = Row, string type = "System.Int32") =>
        new(Guid.NewGuid(), row, type, expected, value, false, "");

    [Fact]
    public void Synced_and_remote_edit_registrations_are_both_required()
    {
        var entry = _file.Bind("General", "Count", 3, "");
        Assert.Throws<InvalidOperationException>(() => ServerSettings.Register("example.mod", entry));
        ConfigSync.Register("example.mod", entry);
        Assert.False(ServerSettings.Apply(Change(), true, true).Success);
        using var registration = ServerSettings.Register("example.mod", entry);
        Assert.True(ServerSettings.Apply(Change(), true, true).Success);
        Assert.Equal(5, entry.Value);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Missing_handshake_or_admin_never_writes(bool handshake, bool admin)
    {
        var entry = Entry();
        Assert.False(ServerSettings.Apply(Change(), handshake, admin).Success);
        Assert.Equal(3, entry.Value);
    }

    [Theory]
    [InlineData("2", "5", "System.Int32")]
    [InlineData("3", "garbage", "System.Int32")]
    [InlineData("3", "5", "System.String")]
    [InlineData("3", "2147483648", "System.Int32")]
    public void Conflicts_schema_drift_and_bad_values_preserve_server_value(string expected, string value, string type)
    {
        var entry = Entry();
        var result = ServerSettings.Apply(Change(expected, value, type: type), true, true);
        Assert.False(result.Success);
        Assert.Equal("3", result.Value);
        Assert.Equal(3, entry.Value);
    }

    [Fact]
    public void A_second_admin_with_stale_value_cannot_overwrite_the_first()
    {
        var entry = Entry();
        Assert.True(ServerSettings.Apply(Change(value: "5"), true, true).Success);
        var second = ServerSettings.Apply(Change(value: "7"), true, true);
        Assert.False(second.Success);
        Assert.Equal("5", second.Value);
        Assert.Equal(5, entry.Value);
    }

    [Fact]
    public void Removed_sync_registration_revokes_remote_edit_even_if_opt_in_handle_remains()
    {
        var entry = Entry();
        ConfigSync.Unregister("example.mod", "General", "Count");
        Assert.False(ServerSettings.Apply(Change(), true, true).Success);
        Assert.Equal(3, entry.Value);
    }

    [Fact]
    public void Replacing_sync_entry_requires_fresh_remote_opt_in()
    {
        var entry = Entry();
        var other = new ConfigFile(Path.Combine(_dir, "other.cfg"), true).Bind("General", "Count", 3, "");
        ConfigSync.Register("example.mod", other);
        Assert.False(ServerSettings.Apply(Change(), true, true).Success);
        Assert.Equal(3, entry.Value);
        Assert.Equal(3, other.Value);
    }

    [Fact]
    public void Editor_can_submit_its_displayed_value_even_after_a_new_sync_push()
    {
        Entry(client: true);
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord("example.mod", "General", "Count", "4") }, true);
        using var lease = ServerSettings.Request(Row, "5", "3", _ => { });
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        Assert.Equal("3", request.Expected);
    }

    [Fact]
    public void Peer_allowlist_is_specific_to_snapshot_and_replaced_on_new_handshake()
    {
        ServerSettings.RecordPeerSnapshot(8, new[] { new ConfigSyncRecord("example.mod", "General", "Count", "3") });
        Assert.True(ServerSettings.PeerAllows(8, Row));
        Assert.False(ServerSettings.PeerAllows(9, Row));
        Assert.False(ServerSettings.PeerAllows(8, "foreign.mod/General/Count"));
        ServerSettings.RecordPeerSnapshot(8, Array.Empty<ConfigSyncRecord>());
        Assert.False(ServerSettings.PeerAllows(8, Row));
    }

    [Fact]
    public void Request_waits_for_response_without_writing_client_value_and_ignores_wrong_correlation()
    {
        var entry = Entry(client: true);
        ServerSettingResult? result = null;
        using var lease = ServerSettings.Request(Row, "5", r => result = r);
        Assert.Null(result);
        Assert.Equal(3, entry.Value);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        Assert.Equal("3", request.Expected);
        ServerSettings.AcceptResponse(new(Guid.NewGuid(), Row, "", "", "99", true, ""));
        ServerSettings.AcceptResponse(new(request.Id, "other/row/key", "", "", "99", true, ""));
        Assert.Null(result);
        ServerSettings.AcceptResponse(request.WithResult(new(true, "5", "")));
        Assert.True(result!.Value.Success);
        Assert.Equal("5", result.Value.Value);
        Assert.Equal(3, entry.Value); // Only ConfigSync pushes update the actual client setting.
        Assert.Equal(0, ServerSettings.PendingCount);
    }

    [Fact]
    public void Timeout_completes_once_and_late_response_does_not_revive_callback()
    {
        Entry(client: true);
        int calls = 0;
        ServerSettingResult result = default;
        using var lease = ServerSettings.Request(Row, "5", r => { calls++; result = r; });
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        _now = ServerSettings.TimeoutSeconds;
        ServerSettings.Tick();
        Assert.Equal(1, calls);
        Assert.False(result.Success);
        ServerSettings.AcceptResponse(request.WithResult(new(true, "5", "")));
        ServerSettings.Tick();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Registration_unload_silently_detaches_pending_callbacks_and_old_handle_cannot_remove_replacement()
    {
        var entry = Entry(client: true);
        var first = ServerSettings.Register("example.mod", entry);
        int calls = 0;
        ServerSettings.Request(Row, "5", _ => calls++);
        var replacement = ServerSettings.Register("example.mod", entry);
        Assert.Equal(0, ServerSettings.PendingCount);
        first.Dispose();
        Assert.True(ServerSettings.CanRequest(Row));
        _now = 20;
        ServerSettings.Tick();
        Assert.Equal(0, calls);
        replacement.Dispose();
        Assert.False(ServerSettings.CanRequest(Row));
    }

    [Fact]
    public void Panel_close_cancels_callback_and_disconnect_retires_pending_request()
    {
        Entry(client: true);
        int calls = 0;
        var lease = ServerSettings.Request(Row, "5", _ => calls++);
        lease.Dispose();
        Assert.Equal(0, ServerSettings.PendingCount);
        ServerSettings.Request(Row, "5", r => { calls++; Assert.False(r.Success); });
        ConfigSync.RestoreLocal();
        ServerSettings.Tick();
        Assert.Equal(1, calls);
        Assert.Equal(0, ServerSettings.PendingCount);
    }

    [Fact]
    public void Disconnect_drops_the_config_lock_so_admin_source_is_pending()
    {
        var entry = Entry(client: true);
        Assert.True(ServerSettings.CanRequest(Row));
        Assert.True(ConfigSync.IsLocked(entry));
        Handshake.InvalidateClientServerContext("disconnect");
        Assert.False(ConfigSync.IsLocked(entry));
        Assert.False(ServerSettings.CanRequest(Row));
    }

    [Fact]
    public void Duplicate_pending_edit_and_transport_failure_are_reported_without_local_write()
    {
        var entry = Entry(client: true);
        var lease = ServerSettings.Request(Row, "5", _ => { });
        ServerSettingResult? result = null;
        ServerSettings.Request(Row, "7", r => result = r);
        Assert.False(result!.Value.Success);
        Assert.Equal(1, ServerSettings.PendingCount);
        lease.Dispose();
        ServerSettings.TestSend = _ => false;
        ServerSettings.Request(Row, "7", r => result = r);
        Assert.False(result.Value.Success);
        Assert.Equal(0, ServerSettings.PendingCount);
        Assert.Equal(3, entry.Value);
    }

    [Fact]
    public void Bounds_are_checked_before_assignment_and_broken_bounds_fail_closed()
    {
        var entry = new BoundedEntry(_file);
        ConfigSync.Register("example.mod", entry);
        ServerSettings.Register("example.mod", entry);
        Assert.False(ServerSettings.Apply(Change(value: "11"), true, true).Success);
        Assert.Equal(0, entry.Writes);
        Assert.True(ServerSettings.Apply(Change(value: "7"), true, true).Success);
        Assert.Equal(1, entry.Writes);
        entry.Description.AcceptableValues.Throw = true;
        Assert.False(ServerSettings.Apply(Change(expected: "7", value: "8"), true, true).Success);
        Assert.Equal(1, entry.Writes);
    }

    [Fact]
    public void Wire_rejects_truncation_trailing_bytes_wrong_direction_invalid_boolean_and_large_fields()
    {
        var message = Change();
        var packet = ServerSettingsWire.Encode(message, false);
        Assert.True(ServerSettingsWire.TryDecode(packet, false, out var decoded));
        Assert.Equal(message.Id, decoded.Id);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(ServerSettingsWire.TryDecode(packet.AsSpan(0, length), false, out _));
        Assert.False(ServerSettingsWire.TryDecode(packet, true, out _));
        Array.Resize(ref packet, packet.Length + 1);
        Assert.False(ServerSettingsWire.TryDecode(packet, false, out _));
        Assert.Throws<ArgumentException>(() => ServerSettingsWire.Encode(Change(value: new string('x', 2049)), false));
        var response = ServerSettingsWire.Encode(message.WithResult(new(true, "5", "")), true);
        Assert.True(ServerSettingsWire.TryDecode(response, true, out var decodedResponse));
        Assert.True(decodedResponse.Success);
        response[20 + System.Text.Encoding.UTF8.GetByteCount(Row)] = 2;
        Assert.False(ServerSettingsWire.TryDecode(response, true, out _));
        packet = ServerSettingsWire.Encode(message, false);
        packet[20] = 0xff;
        Assert.False(ServerSettingsWire.TryDecode(packet, false, out _));
    }

    public sealed class BoundedEntry : ConfigEntryBase
    {
        private int _value = 3;
        public BoundedEntry(ConfigFile file) : base(file, new("General", "Count"), typeof(int)) { }
        public DescriptionModel Description { get; } = new();
        public int Writes;
        public override object? BoxedValue { get => _value; set { Writes++; _value = (int)value!; } }
    }
    public sealed class DescriptionModel { public BoundsModel AcceptableValues { get; } = new(); }
    public sealed class BoundsModel
    {
        public bool Throw;
        public bool IsValid(object value) => Throw ? throw new InvalidOperationException("broken bounds") : (int)value >= 0 && (int)value <= 10;
    }
}
