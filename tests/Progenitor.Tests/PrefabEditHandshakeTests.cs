using System;
using System.Collections.Generic;
using Progenitor.Content;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Content.Edit live handshake gate (R21 finding 3). Shares Handshake tables with the
/// other handshake suites, so this class is in that collection.
/// </summary>
[Collection("Handshake")]
public class PrefabEditHandshakeTests : IDisposable
{
    private const int GuidHash = 42;

    public PrefabEditHandshakeTests() => Handshake.ResetTables();

    public void Dispose() => Handshake.ResetTables();

    [Fact]
    public void MayEdit_prefers_live_handshake_over_the_attribute()
    {
        var owner = new RequiredPlugin();
        Assert.True(PrefabEditCompatibility.OwnerMayEdit(owner, out _, out _));

        Handshake.Require("need.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact);
        Assert.False(PrefabEditCompatibility.MayEdit("need.mod", owner, out var level, out var rule));
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, level);
        Assert.Equal(NetworkVersionRule.Exact, rule);
        Assert.False(PrefabEditCompatibility.LiveAllows("need.mod"));
    }

    [Fact]
    public void MayEdit_falls_back_to_the_attribute_before_DeclareSelf()
    {
        var owner = new RequiredPlugin();
        Assert.True(PrefabEditCompatibility.MayEdit("need.mod", owner, out var level, out var rule));
        Assert.Equal(NetworkCompatibilityLevel.ServerMustHave, level);
        Assert.Equal(NetworkVersionRule.Exact, rule);
    }

    [Fact]
    public void Unrequire_drops_that_plugin_editors_with_one_log_line()
    {
        Handshake.Require("a.id", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        var table = new PrefabEditTable();
        var owner = new object();
        Assert.True(table.TryAdd(
            GuidHash, "a.id", "a", 0, null, null, owner,
            new Action<Fake>(_ => { }), -1, out _, out _, handshakeId: "a.id"));
        Assert.True(table.TryAdd(
            99, "a.id", "a", 0, null, null, owner,
            new Action<Fake>(_ => { }), -1, out _, out _, handshakeId: "a.id"));
        Assert.Equal(0, PrefabEditCompatibility.DropDisallowed(table, _ => { }));
        Assert.Equal(2, table.Count);

        Handshake.Unrequire("a.id");
        var logs = new List<string>();
        Assert.Equal(2, PrefabEditCompatibility.DropDisallowed(table, logs.Add));
        Assert.Equal(0, table.Count);
        Assert.Equal(
            new[] { PrefabEditLog.HandshakeDropped("a.id", "none") },
            logs);

        Assert.Equal(0, PrefabEditCompatibility.DropDisallowed(table, logs.Add));
        Assert.Single(logs);
    }

    [Fact]
    public void Require_NoNeedForSync_after_Edit_drops_editors()
    {
        Handshake.Require("a.id", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        var table = new PrefabEditTable();
        Assert.True(table.TryAdd(
            GuidHash, "a.id", "a", 0, null, null, new object(),
            new Action<Fake>(_ => { }), -1, out _, out _, handshakeId: "a.id"));
        Handshake.Require("a.id", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact);
        var logs = new List<string>();
        Assert.Equal(1, PrefabEditCompatibility.DropDisallowed(table, logs.Add));
        Assert.Equal(0, table.Count);
        Assert.Equal(
            PrefabEditLog.HandshakeDropped("a.id", "NoNeedForSync/Exact"),
            logs[0]);
    }

    [Fact]
    public void Decoy_owner_does_not_match_the_registering_plugin()
    {
        var decoy = new RequiredPlugin();
        var real = new OptionalPlugin();
        Assert.True(PrefabEditCompatibility.OwnerMatchesContext(real, "opt.mod"));
        Assert.True(PrefabEditCompatibility.OwnerMatchesContext(real, "opt.mod.late"));
        Assert.False(PrefabEditCompatibility.OwnerMatchesContext(decoy, "opt.mod"));
        Assert.False(PrefabEditCompatibility.OwnerMatchesContext(new object(), "opt.mod"));
        Assert.False(PrefabEditCompatibility.TryAcceptOwner(decoy, "opt.mod", out _, out var refuse));
        Assert.Equal(PrefabEditLog.DecoyOwnerRefused("opt.mod", 0), refuse);

        Assert.True(PrefabEditCompatibility.TryAcceptOwner(real, "opt.mod", out var handshakeId, out var okLog));
        Assert.Null(okLog);
        Assert.Equal("opt.mod", handshakeId);
    }

    [Fact]
    public void Decoy_owner_cannot_bypass_a_live_NoNeedForSync_row()
    {
        Handshake.Require("opt.mod", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact);
        Assert.False(PrefabEditCompatibility.MayEdit("opt.mod", new RequiredPlugin(), out var level, out _));
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, level);
    }

    [Fact]
    public void DropDisallowed_does_not_drop_editors_that_never_entered_the_live_table()
    {
        var table = new PrefabEditTable();
        Assert.True(table.TryAdd(
            GuidHash, "a.id", "a", 0, null, null, new object(),
            new Action<Fake>(_ => { }), -1, out _, out _, handshakeId: "a.id"));
        Handshake.Require("other.id", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        var logs = new List<string>();
        Assert.Equal(0, PrefabEditCompatibility.DropDisallowed(table, logs.Add));
        Assert.Equal(1, table.Count);
        Assert.Empty(logs);
    }

    [Fact]
    public void Unprovide_drops_client_editors_that_were_live_allowed()
    {
        Handshake.Provide("a.id", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        var table = new PrefabEditTable();
        Assert.True(table.TryAdd(
            GuidHash, "a.id", "a", 0, null, null, new object(),
            new Action<Fake>(_ => { }), -1, out _, out _, handshakeId: "a.id"));
        Assert.Equal(0, PrefabEditCompatibility.DropDisallowed(table, _ => { }));
        Handshake.Unprovide("a.id");
        var logs = new List<string>();
        Assert.Equal(1, PrefabEditCompatibility.DropDisallowed(table, logs.Add));
        Assert.Equal(0, table.Count);
        Assert.Equal(PrefabEditLog.HandshakeDropped("a.id", "none"), logs[0]);
    }

    [Fact]
    public void LiveFingerprint_changes_on_Require_and_Unrequire()
    {
        string empty = PrefabEditCompatibility.LiveFingerprint();
        Handshake.Require("a.id", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
        string required = PrefabEditCompatibility.LiveFingerprint();
        Assert.NotEqual(empty, required);
        Handshake.Require("a.id", "1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact);
        string weaker = PrefabEditCompatibility.LiveFingerprint();
        Assert.NotEqual(required, weaker);
        Handshake.Unrequire("a.id");
        Assert.Equal(empty, PrefabEditCompatibility.LiveFingerprint());
    }

    [BepInPlugin("need.mod", "Need", "1.0")]
    [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]
    private sealed class RequiredPlugin { }

    [BepInPlugin("opt.mod", "Opt", "1.0")]
    [NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact)]
    private sealed class OptionalPlugin { }

    private sealed class Fake { }
}

/// <summary>
/// Test stand-in for BepInEx <c>BepInPlugin</c>. PrefabEditCompatibility reads GUID by
/// type name so this file compiles without a BepInEx reference.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
internal sealed class BepInPluginAttribute : Attribute
{
    public BepInPluginAttribute(string guid, string name, string version)
    {
        GUID = guid;
        Name = name;
        Version = version;
    }

    public string GUID { get; }
    public string Name { get; }
    public string Version { get; }
}
