using System;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Exact-root ownership for the UI toolkit. Plugin unload must not treat '.' as a child
/// separator or prefix-sweep another plugin's <c>progenitor-ui-*</c> root (R24 #3).
/// </summary>
[Collection("PluginLogger")]
public class UiOwnershipTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };

    public UiOwnershipTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void Overlay_serial_canvas_under_an_owned_root_is_owned_in_hierarchy()
    {
        Assert.False(UiOwnership.IsOwned("overlay-1"));
        Assert.True(UiOwnership.IsOwned("progenitor-ui-core"));
        Assert.True(UiOwnership.IsOwnedInHierarchy("overlay-1", new[] { "progenitor-ui-core" }));
        Assert.True(UiOwnership.IsOwnedInHierarchy("progenitor-ui-core-mods-workspace", null));
        Assert.False(UiOwnership.IsOwnedInHierarchy("CanvasMain", Array.Empty<string>()));
        Assert.False(UiOwnership.IsOwnedInHierarchy("CanvasUnscaledMainMenu", null));
    }

    [Fact]
    public void IsOwnedBy_does_not_treat_dot_as_a_child_separator()
    {
        Assert.True(UiOwnership.IsOwnedBy("progenitor-ui-example.sample", "example.sample"));
        Assert.True(UiOwnership.IsOwnedBy("progenitor-ui-example.sample-panel", "example.sample"));
        Assert.False(UiOwnership.IsOwnedBy("progenitor-ui-example.sample.extra", "example.sample"));
        Assert.False(UiOwnership.IsOwnedBy("progenitor-ui-example.sample.extra-panel", "example.sample"));
        Assert.True(UiOwnership.IsOwnedBy("progenitor-ui-example.sample.extra", "example.sample.extra"));
        Assert.False(UiOwnership.IsOwnedBy("progenitor-ui-other", "example.sample"));
    }

    [Fact]
    public void RemoveAll_of_A_leaves_B_root()
    {
        var ownerA = new Owner("example.sample");
        var ownerB = new Owner("example.sample.extra");
        var a = UiApi.Panel("A", ownerA, "example.sample");
        var b = UiApi.Panel("B", ownerB, "example.sample.extra");
        UiApi.Show(a);
        UiApi.Show(b);
        UiApi.RemoveAll(ownerA);
        Assert.True(a.IsDisposed);
        Assert.False(b.IsDisposed);
        Assert.Equal(1, _client.PanelCount);
        Assert.True(b.Visible);
        Assert.Contains(_client.DestroyedRoots, r => r == "progenitor-ui-example.sample");
        Assert.DoesNotContain(_client.DestroyedRoots, r => r == "progenitor-ui-example.sample.extra");
        Assert.Empty(_client.DestroyedPrefixes);
        b.Dispose();
    }

    [Fact]
    public void RemoveAll_does_not_destroy_a_longer_plugin_guid_root()
    {
        var extraOwner = new Owner("example.sample.extra");
        var owner = new Owner("example.sample");
        var a = UiApi.Panel("Sample", owner, "example.sample");
        var extra = UiApi.Panel("Extra", extraOwner, "example.sample.extra");
        UiApi.RemoveAll(owner);
        Assert.True(a.IsDisposed);
        Assert.False(extra.IsDisposed);
        Assert.Equal("progenitor-ui-example.sample.extra", extra.RootName);
        extra.Dispose();
    }

    [Fact]
    public void Uninstall_prefix_sweep_covers_every_progenitor_ui_root()
    {
        var ownerA = new Owner("example.sample");
        var ownerB = new Owner("example.sample.extra");
        var a = UiApi.Panel("A", ownerA, "example.sample");
        var b = UiApi.Panel("B", ownerB, "example.sample.extra");
        UiApi.Uninstall();
        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Contains(_client.DestroyedPrefixes, p => p == UiOwnership.Prefix);
        Assert.Equal(0, _client.PanelCount);
    }

    private sealed class Owner
    {
        public Owner(string id) => Id = id;
        public string Id { get; }
    }
}
