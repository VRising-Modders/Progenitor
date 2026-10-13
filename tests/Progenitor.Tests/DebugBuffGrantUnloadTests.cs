using System;
using Progenitor.Persistence;
using Progenitor.Plugins;
using Progenitor.Debugging;
using PluginUnloadSlot = Progenitor.Bootstrap.PluginUnloadSlot;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// R75 finding 1: nodrain grant keep-or-forget must key off the OnUnload reason the framework
/// actually delivers (Bootstrap ReloaderHost → PluginUnloadSlot → OnUnload), never
/// <see cref="PluginUnloadScope.Current"/> alone. Game-free: the decision is a pure function
/// and the ledger is a ModStore.
/// </summary>
[Collection("PluginLogger")]
public class DebugBuffGrantUnloadTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private const string CharacterId = "76561198000000042";
    private const string Identity = "Alaric";

    public DebugBuffGrantUnloadTests()
    {
        Plugin.Logger = _log;
        PluginUnloadScope.ResetForTests();
        PluginUnloadSlot.ResetForTests();
        BootstrapUnloadSlotProbe.ResetForTests();
        CoreSwapProbe.ResetForTests();
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => typeof(PluginUnloadSlot);
        CoreSwapProbe.TypeOverrideForTests = () => null;
    }

    public void Dispose()
    {
        Plugin.Logger = null;
        PluginUnloadScope.ResetForTests();
        PluginUnloadSlot.ResetForTests();
        BootstrapUnloadSlotProbe.ResetForTests();
        CoreSwapProbe.ResetForTests();
    }

    [Theory]
    [InlineData(PluginUnloadReason.Swap, true)]
    [InlineData(PluginUnloadReason.Shutdown, true)]
    [InlineData(PluginUnloadReason.Unknown, true)]
    [InlineData(PluginUnloadReason.Suspend, true)]
    [InlineData(PluginUnloadReason.Removal, false)]
    public void ShouldKeepGrant_maps_every_named_reason(PluginUnloadReason reason, bool keep)
    {
        Assert.Equal(keep, DebugBuff.ShouldKeepGrant(reason));
        Assert.Equal(!keep, DebugBuff.ShouldSweep(reason));
    }

    [Fact]
    public void ShouldKeepGrant_covers_every_UnloadReason_value_and_null()
    {
        var values = (PluginUnloadReason[])Enum.GetValues(typeof(PluginUnloadReason));
        Assert.Equal(5, values.Length);

        foreach (var reason in values)
            Assert.Equal(reason != PluginUnloadReason.Removal, DebugBuff.ShouldKeepGrant(reason));

        Assert.False(DebugBuff.ShouldKeepGrant(null));
    }

    [Theory]
    [InlineData(PluginUnloadReason.Swap, "kept", 1)]
    [InlineData(PluginUnloadReason.Shutdown, "kept", 0)]
    [InlineData(PluginUnloadReason.Unknown, "kept", 2)]
    [InlineData(PluginUnloadReason.Removal, "forgotten", 1)]
    public void FormatGrantUnloadDecision_names_reason_kept_or_forgotten_and_count(
        PluginUnloadReason reason, string grants, int count)
    {
        Assert.Equal(
            "unload reason=" + reason + " grants=" + grants + " count=" + count,
            DebugBuff.FormatGrantUnloadDecision(reason, count));
        DebugBuff.Log(DebugBuff.FormatGrantUnloadDecision(reason, count));
        Assert.Contains(
            "Debug: unload reason=" + reason + " grants=" + grants + " count=" + count,
            _log.Infos);
    }

    [Fact]
    public void FormatGrantUnloadDecision_null_reason_is_forgotten()
    {
        Assert.Equal(
            "unload reason=none grants=forgotten count=1",
            DebugBuff.FormatGrantUnloadDecision(null, 1));
    }

    [Theory]
    [InlineData(PluginUnloadReason.Swap)]
    [InlineData(PluginUnloadReason.Shutdown)]
    [InlineData(PluginUnloadReason.Unknown)]
    public void KeepOrForgetGrant_keeps_the_ledger_row_on_keep_reasons(PluginUnloadReason reason)
    {
        var store = NewStore();
        Assert.True(DebugBuff.RecordGrant(store, CharacterId, Identity, preExisting: false));

        Assert.True(DebugBuff.KeepOrForgetGrant(store, CharacterId, reason));
        Assert.True(DebugBuff.TryGetGrant(store, CharacterId, Identity, out _));
        Assert.Equal(new[] { CharacterId }, DebugBuff.GrantIds(store));
        Assert.Contains("Debug: " + DebugBuff.FormatGrantKept(CharacterId, reason), _log.Infos);
        Assert.DoesNotContain(
            "Debug: " + DebugBuff.FormatGrantForgotten(CharacterId),
            _log.Infos);
    }

    [Fact]
    public void KeepOrForgetGrant_forgets_on_Removal()
    {
        var store = NewStore();
        Assert.True(DebugBuff.RecordGrant(store, CharacterId, Identity, preExisting: false));

        Assert.True(DebugBuff.KeepOrForgetGrant(store, CharacterId, PluginUnloadReason.Removal));
        Assert.False(DebugBuff.TryGetGrant(store, CharacterId, Identity, out _));
        Assert.Empty(DebugBuff.GrantIds(store));
        Assert.Contains(
            "Debug: " + DebugBuff.FormatGrantForgotten(CharacterId),
            _log.Infos);
    }

    [Fact]
    public void KeepOrForgetGrant_forgets_on_null_reason()
    {
        var store = NewStore();
        Assert.True(DebugBuff.RecordGrant(store, CharacterId, Identity, preExisting: false));

        Assert.True(DebugBuff.KeepOrForgetGrant(store, CharacterId, null));
        Assert.False(DebugBuff.TryGetGrant(store, CharacterId, Identity, out _));
        Assert.Empty(DebugBuff.GrantIds(store));
    }

    [Fact]
    public void Bootstrap_slot_inference_yields_Swap_when_the_slot_says_Swap()
    {
        Assert.Null(PluginUnloadScope.Current);

        var inferred = PluginUnloadReasonPolicy.Infer(
            signalled: null,
            handedOff: PluginUnloadReason.Swap,
            coreSwap: CoreSwapProbeResult.Idle,
            processExiting: false,
            source: out var source);

        Assert.Equal(PluginUnloadReason.Swap, inferred);
        Assert.Equal(PluginUnloadReasonPolicy.SourceBootstrapSlot, source);
        Assert.True(DebugBuff.ShouldKeepGrant(inferred));
        Assert.False(
            DebugBuff.ShouldKeepGrant(PluginUnloadScope.Current),
            "Current is null on the production ReloaderHost path; that must not become forget");
    }

    [Fact]
    public void Current_being_null_does_not_flip_a_slot_Swap_into_Removal()
    {
        Assert.Null(PluginUnloadScope.Current);

        PluginUnloadReason seen;
        using (PluginUnloadSlot.Push(PluginUnloadSlot.Swap))
        {
            Assert.Null(PluginUnloadScope.Current);
            seen = PluginUnloadReasonPolicy.Resolve("Sample", m => _log.LogInfo(m), m => _log.LogWarning(m));
        }

        Assert.Equal(PluginUnloadReason.Swap, seen);
        Assert.Contains("Reload: Sample unload reason=Swap (bootstrap-slot)", _log.Infos);
        Assert.True(DebugBuff.ShouldKeepGrant(seen));
        Assert.False(DebugBuff.ShouldKeepGrant(PluginUnloadScope.Current));
        Assert.False(DebugBuff.ShouldKeepGrant(null));

        var store = NewStore();
        Assert.True(DebugBuff.RecordGrant(store, CharacterId, Identity, preExisting: false));
        Assert.True(DebugBuff.KeepOrForgetGrant(store, CharacterId, seen));
        Assert.True(DebugBuff.TryGetGrant(store, CharacterId, Identity, out _));
        DebugBuff.Log(DebugBuff.FormatGrantUnloadDecision(seen, 1));
        Assert.Contains("Debug: unload reason=Swap grants=kept count=1", _log.Infos);
    }

    [Fact]
    public void Production_ClearImmortal_keys_off_the_OnUnload_reason_not_PluginUnloadScope_Current()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");

        SourcePin.AssertStatement(src, "internal static bool ShouldKeepGrant(PluginUnloadReason? reason) =>");
        SourcePin.AssertUncommented(src, "reason is PluginUnloadReason r && PluginUnloadReasonPolicy.KeepsState(r)");

        string resetBody = SourcePin.MethodBody(src, "internal static void Reset(PluginUnloadReason reason)");
        SourcePin.AssertStatementExactly(resetBody, "_grantUnloadReason = reason;");
        SourcePin.AssertUncommented(resetBody, "FormatGrantUnloadDecision(reason, GrantIds(DebugTools.Store).Count)");
        SourcePin.AssertStatement(resetBody, "Reset(reason, ServerSweep, ServerCount, ServerClearImmortalGuards);");

        string clearBody = SourcePin.MethodBody(
            src, "private static void ClearImmortal(EntityManager em, Entity character)");
        SourcePin.AssertUncommented(clearBody, "KeepOrForgetGrant(DebugTools.Store, id, _grantUnloadReason)");
        SourcePin.AssertNoStatementContaining(clearBody, "PluginUnloadScope.Current");
        SourcePin.AssertNoStatementContaining(clearBody, "PluginUnloadReasonPolicy.Resolve(");
    }

    private static ModStore NewStore() => new("sample.grant-unload.test", filePath: "");
}
