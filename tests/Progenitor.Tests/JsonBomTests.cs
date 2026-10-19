using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Progenitor.Bootstrap;
using Progenitor.Install;
using Progenitor.Persistence;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JSON files with a UTF-8 byte order mark. <c>JsonDocument.Parse</c> over raw file bytes rejects a leading EF BB BF,
/// and hand-edited files often carry one (Notepad, Windows PowerShell's <c>Set-Content -Encoding UTF8</c>, some
/// Thunderstore manifests). Found when a server's <c>ServerHostSettings.json</c> began with a BOM and Progenitor read
/// no server name from it. Game-free: temporary folders only.
/// </summary>
[Collection("HarmonySlotHandoff")] // shares ServerProcessControl.RunningProbe with ServerProcessControlTests
public sealed class JsonBomTests : IDisposable
{
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-jsonbom-" + Guid.NewGuid().ToString("N"));

    public JsonBomTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ServerProcessControl.RunningProbe = null;
        UninstallAtStart.ScheduleDeleteOverride = null;
        try { Directory.Delete(_root, true); }
        catch { /* temp */ }
    }

    private static byte[] WithBom(string json) => Bom.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

    private static string WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>What this would catch: the helper passing a BOM through, or eating the first bytes of plain JSON.</summary>
    [Fact]
    public void The_helper_parses_json_with_and_without_a_byte_order_mark()
    {
        const string json = "{ \"Name\": \"Basin\" }";
        // The trap itself: the framework parser refuses the mark in bytes.
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(WithBom(json)).Dispose());

        using (var doc = JsonFiles.Parse(WithBom(json)))
            Assert.Equal("Basin", doc.RootElement.GetProperty("Name").GetString());
        using (var doc = JsonFiles.Parse(Encoding.UTF8.GetBytes(json)))
            Assert.Equal("Basin", doc.RootElement.GetProperty("Name").GetString());

        Assert.True(JsonFiles.HasBom(WithBom(json)));
        Assert.False(JsonFiles.HasBom(Encoding.UTF8.GetBytes(json)));
        Assert.False(JsonFiles.HasBom(null));
        Assert.Equal(Encoding.UTF8.GetByteCount(json), JsonFiles.WithoutBom(WithBom(json)).Length);
        Assert.Equal(0, JsonFiles.WithoutBom(Bom).Length);
        Assert.Equal(2, JsonFiles.WithoutBom(new byte[] { 0xEF, 0xBB }).Length);

        string path = WriteBytes(Path.Combine(_root, "bom.json"), WithBom(json));
        using (var doc = JsonFiles.ParseFile(path, new JsonDocumentOptions { MaxDepth = 4 }))
            Assert.Equal("Basin", doc.RootElement.GetProperty("Name").GetString());
        // Parsing never rewrites the file: the mark is still there.
        Assert.True(JsonFiles.HasBom(File.ReadAllBytes(path)));
    }

    /// <summary>What this would catch: a server whose host settings start with a BOM showing no name.</summary>
    [Fact]
    public void The_server_name_is_read_from_host_settings_with_a_byte_order_mark()
    {
        ServerProcessControl.RunningProbe = _ => null;
        string server = Path.Combine(_root, "server");
        WriteBytes(Path.Combine(server, "Settings", "ServerHostSettings.json"),
            WithBom("{\r\n  \"Name\": \"Progenitor Forms Test\",\r\n  \"Port\": 9976\r\n}\r\n"));
        Assert.Equal("Progenitor Forms Test", ServerIdentity.HostName(server));
    }

    /// <summary>What this would catch: a mod manager package whose <c>manifest.json</c> has a BOM losing its About facts.</summary>
    [Fact]
    public void A_package_manifest_with_a_byte_order_mark_is_read()
    {
        string dir = Path.Combine(_root, "BepInEx", "plugins", "ExampleTeam-BasinHoverTips");
        string dll = WriteBytes(Path.Combine(dir, "BasinHoverTips.dll"), new byte[] { 0x4D, 0x5A });
        WriteBytes(Path.Combine(dir, "manifest.json"),
            WithBom("{\"name\":\"BasinHoverTips\",\"version_number\":\"1.2.3\",\"website_url\":\"\",\"description\":\"Tips.\",\"dependencies\":[]}"));
        PackageManifestBeside.ForgetCache();

        var manifest = PackageManifestBeside.TryRead(dll);

        Assert.NotNull(manifest);
        Assert.Equal("ExampleTeam", manifest!.Owner);
        Assert.Equal("BasinHoverTips", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("Tips.", manifest.Description);
    }

    /// <summary>
    /// What this would catch: a hand-edited uninstall request (BOM) refused as unreadable by Core's reader or by
    /// Bootstrap's own inline copy.
    /// </summary>
    [Fact]
    public void An_uninstall_request_with_a_byte_order_mark_is_read_by_core_and_bootstrap()
    {
        string bep = Path.Combine(_root, "BepInEx");
        string mod = WriteBytes(Path.Combine(bep, "progenitor-reload", "example.phialstore.dll"), new byte[] { 1 });
        WriteBytes(ClientUninstall.PathFor(bep),
            WithBom("{\"schema\":1,\"requestedUtc\":\"2026-09-13T10:11:12Z\",\"files\":[\"progenitor-reload/example.phialstore.dll\"]}"));

        var request = ClientUninstall.TryRead(bep);
        Assert.NotNull(request);
        Assert.Equal(new[] { "progenitor-reload/example.phialstore.dll" }, request!.Files);

        UninstallAtStart.ScheduleDeleteOverride = _ => true;
        var outcome = UninstallAtStart.Honour(bep, new List<string>().Add);
        Assert.True(outcome.Honoured, outcome.Reason);
        Assert.False(File.Exists(mod));
    }
}
