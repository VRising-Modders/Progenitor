using System;
using System.IO;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// PrefabRegistry is not compiled into this game-free test project. These tests lock the
/// DefineBuff builder shape against the source: CloneOf / Named / Described / Register, no
/// MaxAmount, Kind = Buff.
/// </summary>
public class PrefabRegistryContractTests
{
    [Fact]
    public void DefineBuff_builder_has_CloneOf_Named_Described_Register_and_no_MaxAmount()
    {
        var src = ReadPrefabRegistry();
        SourcePin.AssertStatement(src, "public static BuffDefinitionBuilder DefineBuff(string id)");

        int start = src.IndexOf("public sealed class BuffDefinitionBuilder", StringComparison.Ordinal);
        Assert.True(start >= 0, "BuffDefinitionBuilder is missing");
        int end = src.IndexOf("// ---- registration", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound BuffDefinitionBuilder");
        string builder = src.Substring(start, end - start);

        SourcePin.AssertUncommented(builder, "CloneOf(PrefabGUID source)");
        SourcePin.AssertUncommented(builder, "CloneOf(string spawnableName)");
        SourcePin.AssertUncommented(builder, "Named(string localizedName)");
        SourcePin.AssertUncommented(builder, "Described(string text)");
        SourcePin.AssertUncommented(builder, "Icon(string relativePath)");
        SourcePin.AssertUncommented(builder, "RefreshLiveInstances()");
        SourcePin.AssertUncommented(builder, "Register(object owner)");
        SourcePin.AssertUncommented(builder, "PrefabKind.Buff");
        SourcePin.AssertUncommented(builder, "DefineBuff('");
        SourcePin.AssertStatement(builder, "_iconPath");
        SourcePin.AssertStatement(builder, "_refreshLiveInstances");
        Assert.DoesNotContain("MaxAmount(int", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("DefineItem(", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefabDefinition_stores_IconPath_and_does_not_put_it_on_the_wire()
    {
        var def = ReadContentFile("PrefabDefinition.cs");
        SourcePin.AssertStatement(def, "public string? IconPath { get; internal set; }");
        SourcePin.AssertStatement(def, "public bool RefreshLiveInstances { get; internal set; }");
        SourcePin.AssertStatement(def, "string? iconPath = null");
        SourcePin.AssertStatement(def, "bool refreshLiveInstances = false");
        Assert.Contains("Not a wire field", def, StringComparison.Ordinal);

        var packet = ReadContentFile("PrefabDefinitionPacket.cs");
        Assert.DoesNotContain("IconPath", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("iconPath", packet, StringComparison.Ordinal);

        var wire = ReadContentFile("PrefabWire.cs");
        Assert.DoesNotContain("IconPath", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void DefineItem_builder_still_has_MaxAmount_and_Kind_Item()
    {
        var src = ReadPrefabRegistry();
        SourcePin.AssertStatement(src, "public static ItemDefinitionBuilder DefineItem(string id)");
        SourcePin.AssertUncommented(src, "PrefabKind.Item");
        SourcePin.AssertStatement(src, "public ItemDefinitionBuilder MaxAmount(int maxAmount)");
    }

    [Fact]
    public void Core_swap_snapshot_unions_retired_and_applied_into_cloned()
    {
        var src = ReadContentFile("PrefabRegistry.cs");
        SourcePin.AssertUncommented(src, "UnionClonedHashes(cloned, applied, tombHashes)");
        SourcePin.AssertUncommented(src, "AppliedHashesNoLock()");
        SourcePin.AssertUncommented(src, "ShouldRememberRetiredClone(entityKept, entry.Applied)");
        SourcePin.AssertStatement(src, "PrefabApplicator.RememberClone(entry.Definition.Guid)");
        SourcePin.AssertUncommented(src, "FormatRetiredOwnership(keepOwnership)");
        Assert.Contains("cloned in any earlier generation", src, StringComparison.Ordinal);
    }

    [Fact]
    public void In_place_update_and_successful_apply_remember_the_clone()
    {
        var src = ReadContentFile("PrefabApplicator.cs");
        int update = src.IndexOf("updated existing clone", StringComparison.Ordinal);
        Assert.True(update >= 0, "UpdateExisting report is missing");
        int remember = src.IndexOf("RememberClone(target)", update, StringComparison.Ordinal);
        Assert.True(remember > update, "UpdateExisting must RememberClone so a Core-swap snapshot owns the GUID");
        SourcePin.AssertUncommented(src, "out bool entityKept");
    }

    [Fact]
    public void Same_id_source_change_retargets_or_refuses_and_rolls_back_new_keys()
    {
        var applicator = ReadContentFile("PrefabApplicator.cs");
        SourcePin.AssertUncommented(applicator, "TryDecideSameId");
        SourcePin.AssertStatement(applicator, "PrepareRetarget");
        SourcePin.AssertUncommented(applicator, "TrySnapshotRetargetRows");
        SourcePin.AssertStatement(applicator, "RemapClone(prefabs, gameData, target, oldRows");
        SourcePin.AssertUncommented(applicator, "HadAsset");
        SourcePin.AssertUncommented(applicator, "HadState");
        SourcePin.AssertUncommented(applicator, "HadItem");
        SourcePin.AssertUncommented(applicator, "FormatRetargetRemapFailed");
        SourcePin.AssertUncommented(applicator, "CountLiveInstances");
        SourcePin.AssertUncommented(applicator, "ShouldRollbackKey(ok, keyWasNew)");
        SourcePin.AssertUncommented(applicator, "SourceMatchesKind(def.Kind, existingHasItem, existingHasBuff)");
        SourcePin.AssertStatement(applicator, "replaceManaged: true");
        SourcePin.AssertUncommented(applicator, "DestroyEntity(existing)");
        SourcePin.AssertStatement(applicator, "RememberAppliedIdentity");
        SourcePin.AssertUncommented(applicator, "ExportAppliedSources");
        SourcePin.AssertUncommented(applicator, "ImportAppliedSources");
        SourcePin.AssertUncommented(applicator, "LiveCountForDecision");
        SourcePin.AssertUncommented(applicator, "TryBuildLiveQuery");
        SourcePin.AssertUncommented(applicator, "AddNone(prefabType)");
        SourcePin.AssertUncommented(applicator, "keepOwnership: true");

        var definition = ReadContentFile("PrefabDefinition.cs");
        SourcePin.AssertUncommented(definition, "EnsureLocalizedKeys");

        var registry = ReadPrefabRegistry();
        SourcePin.AssertUncommented(registry, "IsFatalIdentityRefuse");
        SourcePin.AssertUncommented(registry, "ShouldTombstone(true, hasLive, liveKind)");
        SourcePin.AssertUncommented(registry, "liveKind == PrefabKind.Item");
        SourcePin.AssertUncommented(registry, "RememberAppliedIdentity");
        SourcePin.AssertUncommented(registry, "ExportAppliedSources");
        SourcePin.AssertUncommented(registry, "ReplaceCloneSources");
        SourcePin.AssertUncommented(registry, "TakeCloneSources");
        SourcePin.AssertUncommented(registry, "FormatPrfDAfterRetarget");
        SourcePin.AssertUncommented(registry, "EnsureLocalizedKeys");
        Assert.Contains("Editors may be re-run on live", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_calls_RecipeApply_Uninstall()
    {
        var src = ReadPrefabRegistry();
        int uninstall = src.IndexOf("public static void Uninstall()", StringComparison.Ordinal);
        Assert.True(uninstall >= 0, "PrefabRegistry.Uninstall is missing");
        int next = src.IndexOf("internal static void NotifyLiveWorldAfterCoreSwap()", uninstall, StringComparison.Ordinal);
        Assert.True(next > uninstall, "could not bound PrefabRegistry.Uninstall");
        string body = src.Substring(uninstall, next - uninstall);

        SourcePin.AssertUncommented(body, "RecipeApply.Uninstall()");
        int buffBar = body.IndexOf("BuffBarTextApply.Uninstall()", StringComparison.Ordinal);
        int recipe = body.IndexOf("RecipeApply.Uninstall()", StringComparison.Ordinal);
        Assert.True(buffBar >= 0 && recipe > buffBar,
            "RecipeApply.Uninstall must run from PrefabRegistry.Uninstall after the buff-bar uninstall");
    }

    [Fact]
    public void Client_sends_and_receives_PrfI_before_definitions()
    {
        var src = ReadPrefabRegistry();
        SourcePin.AssertUncommented(src, "CustomEvents.OnReceive(KeyLedgerPacket.EventId, OnClientKeyLedger)");
        SourcePin.AssertUncommented(src, "CustomEvents.RemoveHandler(KeyLedgerPacket.EventId)");
        SourcePin.AssertUncommented(src, "TryBuildKeyLedgerPayload(");
        SourcePin.AssertUncommented(src, "KeyLedgerPacket.EventId");
        SourcePin.AssertUncommented(src, "ApplyPendingInServerOrder(");
        SourcePin.AssertUncommented(src, "ShouldSkipItemForJoinAgreement(");
        SourcePin.AssertUncommented(src, "ClientPacket.IndexLedger");
        int prfI = src.IndexOf("on-connect PrfI", StringComparison.Ordinal);
        Assert.True(prfI >= 0, "server join must send PrfI");
        int prfD = src.IndexOf("ItemDefinitionDelivery.Begin(", prfI, StringComparison.Ordinal);
        Assert.True(prfD > prfI, "PrfI must be sent before on-connect PrfD");
        SourcePin.AssertUncommented(src, "JoinSession.TryAcceptLedger(");
        SourcePin.AssertUncommented(src, "JoinSession.NoteDefinitionPacket(");
        SourcePin.AssertUncommented(src, "JoinSession.RefusePacket(");
        int noteDef = src.IndexOf("JoinSession.NoteDefinitionPacket", StringComparison.Ordinal);
        int applyPending = src.IndexOf("ApplyPending(Worlds.Client", noteDef, StringComparison.Ordinal);
        Assert.True(applyPending > noteDef, "a PrfD-first join must refuse items before dumping pending");
        SourcePin.AssertUncommented(src, "PrefabEditLog.RestoreIncomplete(");
        SourcePin.AssertUncommented(src, "JoinSession.ShouldSkipItemAppend(");

        int exportStart = src.IndexOf("internal static void ExportProcessMemory(", StringComparison.Ordinal);
        int importStart = src.IndexOf("internal static void ImportProcessMemory(", StringComparison.Ordinal);
        Assert.True(exportStart >= 0 && importStart > exportStart, "ExportProcessMemory / ImportProcessMemory missing");
        string exportBody = src.Substring(exportStart, importStart - exportStart);
        SourcePin.AssertUncommented(exportBody, "WriteJoinSessionToMemory()");
        SourcePin.AssertUncommented(src, "snapshot key-table join session refused=");
        SourcePin.AssertUncommented(src, "Runtime.HostContentMemory()");

        int importEnd = src.IndexOf("private static void WriteCloneSourcesToMemory(", importStart, StringComparison.Ordinal);
        Assert.True(importEnd > importStart, "could not bound ImportProcessMemory");
        string importBody = src.Substring(importStart, importEnd - importStart);
        SourcePin.AssertUncommented(importBody, "TryRestoreJoinSessionFromMemory()");

        int skipStart = src.IndexOf("private static bool ShouldSkipItemForJoinAgreement(", StringComparison.Ordinal);
        int skipEnd = src.IndexOf("private static void ResetJoinSessionNoLock()", skipStart, StringComparison.Ordinal);
        Assert.True(skipStart >= 0 && skipEnd > skipStart, "could not bound ShouldSkipItemForJoinAgreement");
        string skipBody = src.Substring(skipStart, skipEnd - skipStart);
        SourcePin.AssertUncommented(skipBody, "JoinSession.ShouldSkipItemAppend(");
        SourcePin.AssertUncommented(skipBody, "requireAgreedOrder = !Plugin.IsServerProcess && ApplyGate.Ready");

        int restoreStart = src.IndexOf("private static void TryRestoreJoinSessionFromMemory()", StringComparison.Ordinal);
        int restoreEnd = src.IndexOf("private static void ClearJoinSessionInMemory()", restoreStart, StringComparison.Ordinal);
        Assert.True(restoreStart >= 0 && restoreEnd > restoreStart, "could not bound TryRestoreJoinSessionFromMemory");
        string restoreBody = src.Substring(restoreStart, restoreEnd - restoreStart);
        SourcePin.AssertUncommented(restoreBody, "KeyJoinHandoff.TryRestoreInto(");
        SourcePin.AssertUncommented(restoreBody, "failClosed = !Plugin.IsServerProcess");
        SourcePin.AssertUncommented(restoreBody, "JoinSession.RefusePacket(why)");

        int resetClient = src.IndexOf("private static void ResetClientJoinState(", StringComparison.Ordinal);
        Assert.True(resetClient >= 0, "ResetClientJoinState is missing");
        int resetClientEnd = src.IndexOf("private static void ApplyKeyPlaceholders(", resetClient, StringComparison.Ordinal);
        Assert.True(resetClientEnd > resetClient, "could not bound ResetClientJoinState");
        string resetClientBody = src.Substring(resetClient, resetClientEnd - resetClient);
        SourcePin.AssertUncommented(resetClientBody, "ClearJoinSessionInMemory()");
        SourcePin.AssertUncommented(resetClientBody, "ResetJoinSessionNoLock()");
    }

    [Fact]
    public void New_client_world_forgets_old_table_ownership_but_Core_swap_preserves_it()
    {
        // Exercise the lifecycle discriminator: clearing ownership during the same-world
        // republish would bypass the saved-index protection that Core swaps require.
        var gate = new PrefabApplyGate(PrefabApplyGate.Side.Client);
        gate.NotifyLiveWorldAfterCoreSwap();
        var sameWorld = gate.NotifyWorldReady();
        Assert.False(sameWorld.DropSyncedOwnerEntries || sameWorld.ClearTombstones);
        var newWorld = gate.NotifyWorldReady();
        Assert.True(newWorld.DropSyncedOwnerEntries && newWorld.ClearTombstones);

        string registry = ReadPrefabRegistry();
        int reset = registry.IndexOf("private static void ResetClientJoinState(", StringComparison.Ordinal);
        int end = registry.IndexOf("private static void ApplyKeyPlaceholders(", reset, StringComparison.Ordinal);
        string resetBody = registry.Substring(reset, end - reset);
        SourcePin.AssertStatement(resetBody, "Entries[i].Applied = false;");
        SourcePin.AssertStatement(resetBody, "PendingFrames.Clear();");
        // Connection end may keep the old world alive. Only the explicit world-change
        // branch may discard ownership; same-world Core swaps still need its guard.
        int forget = resetBody.IndexOf("if (forgetWorld)", StringComparison.Ordinal);
        int forgetEnd = resetBody.IndexOf("Edits.Wire.ClearReceived();", forget, StringComparison.Ordinal);
        string forgetBody = resetBody.Substring(forget, forgetEnd - forget);
        SourcePin.AssertStatement(forgetBody, "forgottenClones = PrefabApplicator.ForgetClientWorld();");
        Assert.Equal(1, registry.Split("PrefabApplicator.ForgetClientWorld();").Length - 1);
        Assert.DoesNotContain("ResetForNewClientWorld", registry, StringComparison.Ordinal);

        string applicator = ReadContentFile("PrefabApplicator.cs");
        SourcePin.AssertStatement(applicator, "internal static int ForgetClientWorld() => Ledger.ForgetWorld();");
        SourcePin.AssertUncommented(applicator, "if (!KeyRowStillHolds(savedIndex, now))");
    }

    [Fact]
    public void PrefabKind_wire_values_are_item_zero_buff_one()
    {
        Assert.Equal(0, (int)PrefabKind.Item);
        Assert.Equal(1, (int)PrefabKind.Buff);
        Assert.Equal(PrefabKind.Item, PrefabWire.KindFromWire(0));
        Assert.Equal(PrefabKind.Buff, PrefabWire.KindFromWire(1));
        Assert.Equal(PrefabKind.Item, PrefabWire.KindFromWire(99));
        Assert.True(PrefabWire.TryValidateKind(0, out var error));
        Assert.Equal("", error);
        Assert.True(PrefabWire.TryValidateKind(1, out error));
        Assert.False(PrefabWire.TryValidateKind(2, out error));
        Assert.Contains("PrefabKind", error);
    }

    private static string ReadPrefabRegistry() => ReadContentFile("PrefabRegistry.cs");

    private static string ReadContentFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Content", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("src/Progenitor.Core/Content/" + fileName);
    }
}
