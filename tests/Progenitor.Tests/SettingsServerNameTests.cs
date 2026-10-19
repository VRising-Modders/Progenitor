using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-19 (release 2026.10.143, the guide rig): a mod page's Server settings line read
/// "Applies to everyone on 127.0.0.1:9966" instead of the server's name. The settings catalog's begin packet must carry
/// the server's name to version 1 and version 2 clients alike, the page must fall back to the name it knows from the
/// signed catalog when that name is empty, and a line players read never shows the address.
/// </summary>
public sealed class SettingsServerNameTests : IDisposable
{
    private static readonly Guid Gen = Guid.Parse("4d1c2b3a-0f9e-4a8b-9c7d-6e5f4a3b2c1d");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-server-name-" + Guid.NewGuid().ToString("N"));

    public SettingsServerNameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ScopedSettingRecord Row(string key) => new()
    {
        Id = "yourteam.mymod/Server/" + key, PluginId = "yourteam.mymod", ModName = "My Mod", Version = "1.0.0",
        Section = "Server", Key = key, Help = "The greeting.", Type = "System.String",
        Default = "Hello", Value = "Hello", Personal = false, Editable = true, Choices = Array.Empty<string>(), MaxLength = 120,
        Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Text, Label = "Greeting", SectionTitle = "Server", SectionOrder = 1, RowOrder = 0 },
    };

    /// <summary>The bytes a server sends one peer, as ScopedSettings.Snapshot builds them: the begin at version 1, rows at the peer's version.</summary>
    private static List<byte[]> Snapshot(byte rowVersion, string serverName, params ScopedSettingRecord[] rows)
    {
        var packets = new List<byte[]>
        {
            ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 1, Generation = Gen, Count = rows.Length, CanEdit = true, ServerName = serverName }),
        };
        foreach (var row in rows)
            packets.Add(ScopedSettingsWire.EncodeRowFitted(new ScopedSettingsPacket { Kind = 2, Generation = Gen, Row = row }, rowVersion, out _));
        packets.Add(ScopedSettingsWire.Encode(new ScopedSettingsPacket { Kind = 3, Generation = Gen, Count = rows.Length }));
        return packets;
    }

    private static ScopedSettingsClient Receive(IEnumerable<byte[]> packets)
    {
        var client = new ScopedSettingsClient(_ => true);
        foreach (var bytes in packets)
        {
            Assert.True(ScopedSettingsWire.TryDecode(bytes, true, out var packet));
            client.Accept(packet, 1);
        }
        return client;
    }

    [Theory]
    [InlineData(ScopedSettingsWire.Version)]
    [InlineData(ScopedSettingsWire.PresentationVersion)]
    public void Version_1_and_2_peers_receive_the_servers_name_with_the_catalog(byte rowVersion)
    {
        var client = Receive(Snapshot(rowVersion, "Progenitor Guide", Row("Greeting"), Row("Farewell")));
        Assert.True(client.Ready);
        Assert.Equal("Progenitor Guide", client.ServerName);
        Assert.Equal(2, client.Rows.Count);
        Assert.Equal(rowVersion == ScopedSettingsWire.PresentationVersion, client.Rows[0].Presentation != null);

        var context = new SettingsScopeContext
        {
            Connection = SettingConnection.Progenitor,
            ServerName = SettingsScopeContext.PickServerName(client.Ready, client.ServerName, ""),
            ServerAddress = "127.0.0.1:9966",
        };
        Assert.Equal("Applies to everyone on Progenitor Guide", context.ServerLine);
        Assert.Equal("Only affects you on Progenitor Guide. Saved by this server.", context.PersonalServerLine);
    }

    [Theory]
    [InlineData(ScopedSettingsWire.Version)]
    [InlineData(ScopedSettingsWire.PresentationVersion)]
    public void A_catalog_without_a_name_keeps_the_name_the_client_knows_and_never_shows_the_address(byte rowVersion)
    {
        // The guide rig's first catalog after a server start: the begin carried an empty name.
        var client = Receive(Snapshot(rowVersion, "", Row("Greeting")));
        Assert.True(client.Ready);
        Assert.Equal("", client.ServerName);

        // The signed catalog (or the server's folder on this PC) named it: the page keeps that name.
        Assert.Equal("Progenitor Guide", SettingsScopeContext.PickServerName(client.Ready, client.ServerName, "Progenitor Guide"));

        // Nothing names it: neutral words, not 127.0.0.1:9966.
        var context = new SettingsScopeContext
        {
            Connection = SettingConnection.Progenitor,
            ServerName = SettingsScopeContext.PickServerName(client.Ready, client.ServerName, ""),
            ServerAddress = "127.0.0.1:9966",
        };
        Assert.Equal("Applies to everyone on this server", context.ServerLine);
        Assert.Equal("Only affects you on this server. Saved by the server.", context.PersonalServerLine);
        Assert.DoesNotContain("127.0.0.1", context.ServerLine);
        Assert.DoesNotContain("127.0.0.1", context.PersonalServerLine);
    }

    [Fact]
    public void The_catalogs_name_wins_and_the_stock_name_is_no_name()
    {
        Assert.Equal("Night Garden", SettingsScopeContext.PickServerName(true, "Night Garden", "Folder Name"));
        Assert.Equal("Folder Name", SettingsScopeContext.PickServerName(false, "Night Garden", "Folder Name"));
        Assert.Equal("Folder Name", SettingsScopeContext.PickServerName(true, "  ", "Folder Name"));
        Assert.Equal("Folder Name", SettingsScopeContext.PickServerName(true, "V Rising Server", "Folder Name"));
        Assert.Equal("", SettingsScopeContext.PickServerName(true, "", "V Rising Server"));
    }

    [Fact]
    public void The_server_names_itself_from_its_command_line_data_path_as_its_catalog_does()
    {
        // The guide rig's layout: the install's default settings file (stock name, with a byte order mark) and the
        // real one under -persistentDataPath.
        string install = Path.Combine(_dir, "server");
        string data = Path.Combine(_dir, "save-data");
        Directory.CreateDirectory(Path.Combine(install, "VRisingServer_Data", "StreamingAssets", "Settings"));
        Directory.CreateDirectory(Path.Combine(data, "Settings"));
        File.WriteAllText(Path.Combine(install, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json"),
            "{\n  \"Name\": \"V Rising Server\",\n  \"Port\": 9876\n}", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(data, "Settings", "ServerHostSettings.json"),
            "{\n  \"Name\": \"Progenitor Guide\",\n  \"Port\": 9966\n}", new UTF8Encoding(false));

        Assert.Equal("Progenitor Guide",
            ModManifest.OwnServerNameFrom(new[] { "VRisingServer.exe", "-persistentDataPath", data }, install, defaultDataPath: ""));
        // Only the stock name: no name (the lines then say "this server").
        Assert.Equal("", ModManifest.OwnServerNameFrom(new[] { "VRisingServer.exe" }, install, defaultDataPath: ""));
    }
}
