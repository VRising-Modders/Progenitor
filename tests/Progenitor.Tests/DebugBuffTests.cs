using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Commands;
using Progenitor.Plugins;
using Progenitor.Debugging;
using BootstrapSlot = Progenitor.Bootstrap.PluginUnloadSlot;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Sample <c>.nodrain on|off</c>: token parse and the clone/edit
/// intent (id, source spawnable, drain Set-to-zero, infinite lifetime). Apply/remove hit
/// <c>DebugEventsSystem</c> and stay behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
[Collection("PluginLogger")]
public class DebugBuffTests : IDisposable
{
    [Theory]
    // Explicit .debug off follows the ledger rule too: the vanilla player prefab carries
    // Immortal { IsImmortal = true } (it is what makes the downed state possible), so a
    // pre-existing or foreign Immortal always stays (live 2026-10-11).
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, false)]
    public void Explicit_off_keeps_vanilla_Immortal(
        bool granted, bool preExisting, bool protectionHolds, bool removes)
    {
        Assert.Equal(removes, DebugBuff.ImmortalClearRemoves(granted, preExisting, explicitOff: true, protectionHolds));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Explicit_off_removes_admin_invulnerability_unless_protection_holds(bool protectionHolds, bool removes) =>
        Assert.Equal(removes, DebugBuff.OffRemovesInvulnerable(protectionHolds));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Automatic_clears_keep_the_conservative_rule(bool granted, bool preExisting, bool removes)
    {
        // Buff removal, watchdog and Core swap unload: only an Immortal the ledger proves ours goes.
        Assert.Equal(removes, DebugBuff.ImmortalClearRemoves(granted, preExisting, explicitOff: false, protectionHolds: false));
    }

    private readonly TestLogSource _log = new();

    public DebugBuffTests()
    {
        Plugin.Logger = _log;
    }

    public void Dispose()
    {
        Plugin.Logger = null;
    }

    [Theory]
    [InlineData("on", 1)]
    [InlineData("ON", 1)]
    [InlineData(" on ", 1)]
    [InlineData("off", 2)]
    [InlineData("Off", 2)]
    public void TryParse_accepts_on_and_off(string token, int expected)
    {
        Assert.True(DebugBuff.TryParse(token, out var kind));
        Assert.Equal(expected, (int)kind);
    }

    [Theory]
    [InlineData("ember")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("enable")]
    public void TryParse_rejects_unknown(string? token)
    {
        Assert.False(DebugBuff.TryParse(token, out _));
    }

    /// <summary>
    /// A bare <c>.nodrain</c> reports rather than refusing: asking what is applied is the safe
    /// default, and it is what an operator who typed the command with no argument wanted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("status")]
    [InlineData("STATUS")]
    [InlineData(" status ")]
    public void TryParse_reads_bare_and_status_as_status(string? token)
    {
        Assert.True(DebugBuff.TryParse(token, out var kind));
        Assert.Equal(DebugBuff.Kind.Status, kind);
    }

    [Fact]
    public void CommandParser_splits_debug_on_into_name_and_token()
    {
        var tokens = CommandParser.Tokenize(".debug on");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(".debug", tokens[0]);
        Assert.True(DebugBuff.TryParse(tokens[1], out var kind));
        Assert.Equal(1, (int)kind);
    }

    [Fact]
    public void CommandParser_splits_debug_off_into_name_and_token()
    {
        var tokens = CommandParser.Tokenize(".debug off");
        Assert.Equal(2, tokens.Count);
        Assert.True(DebugBuff.TryParse(tokens[1], out var kind));
        Assert.Equal(2, (int)kind);
    }

    [Fact]
    public void CommandParser_debug_without_token_is_one_word()
    {
        var tokens = CommandParser.Tokenize(".debug");
        Assert.Single(tokens);
        Assert.Equal(".debug", tokens[0]);

        // The missing token is what the command's default parameter passes through, and it now
        // means "report", not "refuse": the buff is one HUD icon over five protections, so
        // reading them back has to be the cheapest thing to ask for.
        Assert.True(DebugBuff.TryParse("", out var kind));
        Assert.Equal(DebugBuff.Kind.Status, kind);
    }

    [Fact]
    public void Registration_intent_clones_haste_and_sets_drain_to_zero_forever()
    {
        Assert.Equal("progenitor.debug", DebugBuff.Id);
        Assert.Equal("sample.nodrain", DebugBuff.LegacySampleId);
        Assert.Equal("Buff_General_Haste", DebugBuff.SourceSpawnableName);
        Assert.Equal(0f, DebugBuff.DrainBloodValue);
        Assert.Equal(-1f, DebugBuff.InfiniteDuration);
        Assert.Equal((byte)0, DebugBuff.ModificationTypeSet);
        Assert.Equal(0, DebugBuff.LifeTimeEndActionNone);
        Assert.Equal(1000, DebugBuff.DrainPriority);
        Assert.Equal("icons/debug.png", DebugBuff.IconRelativePath);
        Assert.Equal("progenitor.debug.name", DebugBuff.NameLocalizationKey);
        Assert.Equal("progenitor.debug.desc", DebugBuff.DescriptionLocalizationKey);
        Assert.Equal("Debug", DebugBuff.LocalizedName);
        Assert.Equal("No blood drain; invulnerable, sun-immune, ignored by foes.", DebugBuff.LocalizedDescription);
        Assert.Equal(
            new[] { "BuffSequence", "BuffSequenceSpawned", "SpawnSequenceForEntity", "SequenceGUID" },
            DebugBuff.SourceVisualComponentNames);
        Assert.Equal(
            "id=progenitor.debug source=Buff_General_Haste drain=0 type=Set/0 lifetime=-1 endAction=None/0 priority=1000 icon=icons/debug.png name=progenitor.debug.name desc=progenitor.debug.desc",
            DebugBuff.DescribeRegistration());
    }

    /// <summary>
    /// The strip pass removes the four source-visual components and nothing else — in particular
    /// not the buff-bar components, which are what makes the clone visible. Behavior, not a
    /// grep: <c>StripSourceVisuals</c> iterates <see cref="DebugBuff.SourceVisualComponentNames"/>
    /// and <c>StripsSourceVisual</c> is the membership test it uses.
    /// </summary>
    [Theory]
    [InlineData("BuffSequence", true)]
    [InlineData("BuffSequenceSpawned", true)]
    [InlineData("SpawnSequenceForEntity", true)]
    [InlineData("SequenceGUID", true)]
    [InlineData("ShowOnBuffBar", false)]
    [InlineData("ShowOnBuffBarData", false)]
    [InlineData("Buff", false)]
    [InlineData("LifeTime", false)]
    public void Only_the_source_visual_components_are_stripped(string component, bool stripped)
    {
        Assert.Equal(stripped, DebugBuff.StripsSourceVisual(component));
    }

    /// <summary>
    /// The prefab-tag gate. Stripping <c>BuffSequenceSpawned</c> off a live instance leaves the
    /// sequence playing with nothing to end it (evidence 109), so the strip runs on the prefab
    /// only and says so when it refuses.
    /// </summary>
    [Fact]
    public void Source_visuals_are_stripped_on_the_prefab_only()
    {
        Assert.True(DebugBuff.MayStripSourceVisuals(isPrefabEntity: true));
        Assert.False(DebugBuff.MayStripSourceVisuals(isPrefabEntity: false));
        Assert.Equal(
            "edit skip StripSourceVisuals (not a prefab; would orphan BuffSequenceSpawned)",
            DebugBuff.StripSkipLog);
    }

    /// <summary>
    /// The two edit passes, as the live code runs them: <c>PatchPrefab</c> and
    /// <c>PatchInstance</c> both dispatch from these arrays, so this is the edit order and not a
    /// description of it. The instance pass must not carry the three prefab-only steps — the
    /// source-visual strip (evidence 109) and the two modification writes, which would race the
    /// <c>ModificationId</c>s <c>Apply_BuffModificationsSystem_Server</c> assigned.
    /// </summary>
    [Fact]
    public void The_instance_pass_carries_only_the_prefab_safe_steps()
    {
        Assert.Equal(
            new[]
            {
                DebugBuff.StepLifeTime,
                DebugBuff.StepDrainModifier,
                DebugBuff.StepPersistTag,
                DebugBuff.StepModificationFlags,
                DebugBuff.StepDisableAggro,
                DebugBuff.StepStripSpawnEvents,
                DebugBuff.StepStripSourceVisuals,
            },
            DebugBuff.PrefabPatchSteps);

        Assert.Equal(
            new[]
            {
                DebugBuff.StepLifeTime,
                DebugBuff.StepDrainModifier,
                DebugBuff.StepPersistTag,
                DebugBuff.StepStripSpawnEvents,
            },
            DebugBuff.InstancePatchSteps);

        foreach (var prefabOnly in new[]
                 {
                     DebugBuff.StepStripSourceVisuals,
                     DebugBuff.StepModificationFlags,
                     DebugBuff.StepDisableAggro,
                 })
        {
            Assert.DoesNotContain(prefabOnly, DebugBuff.InstancePatchSteps);
            Assert.Contains(prefabOnly, DebugBuff.PrefabPatchSteps);
        }

        // A step name the dispatcher does not know would silently do nothing at runtime.
        foreach (var step in DebugBuff.PrefabPatchSteps)
            Assert.Contains(step, DebugBuff.KnownPatchSteps);
        foreach (var step in DebugBuff.InstancePatchSteps)
            Assert.Contains(step, DebugBuff.KnownPatchSteps);
    }

    private static string FindDebugBuff()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
            if (System.IO.File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException("src/Progenitor.Core/Debugging/DebugBuff.cs");
    }

    [Fact]
    public void Log_prefixes_every_step_with_Debug()
    {
        DebugBuff.Log("clone begin");
        Assert.Contains(_log.Infos, l => l == "Debug: clone begin");
    }

    /// <summary>
    /// The HUD icon ships inside Core: Core has no assets folder, so the PNG is an embedded
    /// resource under the name the client icon applicator probes for a Core-owned definition
    /// (<c>progenitor.content.</c> + the relative path with '/' as '.'), and the Sample no longer
    /// carries the old <c>sample.nodrain</c> localization rows.
    /// </summary>
    [Fact]
    public void The_icon_is_embedded_in_Core_and_the_Sample_no_longer_names_the_buff()
    {
        string csproj = SourcePin.ReadFile("src", "Progenitor.Core", "Progenitor.Core.csproj");
        Assert.Contains(@"<EmbeddedResource Include=""Debugging\Assets\debug.png"" LogicalName=""progenitor.content.icons.debug.png"" />",
            csproj, StringComparison.Ordinal);
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Debugging", "Assets", "debug.png")));
        Assert.Equal("progenitor.content.icons.debug.png", "progenitor.content." + DebugBuff.IconRelativePath.Replace('/', '.'));

        string apply = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "BuffIconApply.cs");
        Assert.Contains("asm.GetManifestResourceStream(\"progenitor.content.\" + logical)", apply, StringComparison.Ordinal);

        string json = SourcePin.ReadFile("samples", "Progenitor.Sample", "localization", "en.json");
        Assert.DoesNotContain("sample.nodrain", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Debug_buff_grants_invulnerability_and_sun_immunity()
    {
        // ProjectM.BuffModificationTypes in the server metadata.
        Assert.Equal(1L, DebugBuff.ImmuneToHazardsFlag);
        Assert.Equal(2L, DebugBuff.ImmuneToSunFlag);
        Assert.Equal(4L, DebugBuff.InvulnerableFlag);
        // Same value as the vanilla Admin_Invulnerable_Buff (read live, guid -480024072).
        Assert.Equal(7L, DebugBuff.ModificationFlags);
        Assert.Equal("BuffModificationFlagData", DebugBuff.ModificationFlagComponentName);
        Assert.Equal("ImmuneToHazards|ImmuneToSun|Invulnerable", DebugBuff.ModificationFlagNames);
        Assert.Equal("Admin_Invulnerable_Buff", DebugBuff.ModificationFlagVanillaSource);
    }

    [Fact]
    public void Debug_buff_makes_hostile_units_ignore_the_wearer()
    {
        // ProjectM.DisableAggroBuffMode in the server metadata.
        Assert.Equal(1, DebugBuff.OthersDontAttackTargetMode);
        Assert.Equal(2, DebugBuff.TargetDontAttackOthersMode);
        // Only the "others ignore me" half: a player character has Aggroable but no
        // AggroConsumer, so TargetDontAttackOthers would be a no-op.
        Assert.Equal(1, DebugBuff.AggroMode);
        Assert.Equal("DisableAggroBuff", DebugBuff.AggroComponentName);
        Assert.Equal("OthersDontAttackTarget", DebugBuff.AggroModeNames);
        // Same Mode as the vanilla vampire stealth spell mod (read live, guid 1462259032).
        Assert.Equal("AB_Blood_BloodRite_SpellMod_Stealth", DebugBuff.AggroVanillaSource);
    }

    [Fact]
    public void Description_mentions_the_new_effects_and_fits_the_wire_slot()
    {
        Assert.Contains("invulnerable", DebugBuff.LocalizedDescription, StringComparison.Ordinal);
        Assert.Contains("sun", DebugBuff.LocalizedDescription, StringComparison.Ordinal);
        Assert.Contains("drain", DebugBuff.LocalizedDescription, StringComparison.Ordinal);
        Assert.Contains("ignored by foes", DebugBuff.LocalizedDescription, StringComparison.Ordinal);
        // PrefabWire: 1–63 ASCII bytes per display field.
        var bytes = System.Text.Encoding.ASCII.GetBytes(DebugBuff.LocalizedDescription);
        Assert.InRange(bytes.Length, 1, 63);
        foreach (char c in DebugBuff.LocalizedDescription)
            Assert.True(c <= 0x7F, "description must be ASCII");
    }

    [Fact]
    public void Prefab_pass_writes_the_modification_flags_and_the_instance_pass_does_not()
    {
        // Which pass runs the step is behavior: both dispatch from these arrays.
        Assert.Contains(DebugBuff.StepModificationFlags, DebugBuff.PrefabPatchSteps);
        Assert.DoesNotContain(DebugBuff.StepModificationFlags, DebugBuff.InstancePatchSteps);

        // The writer body itself still references ProjectM types and cannot be compiled here.
        // Source pin, labeled as one.
        var src = System.IO.File.ReadAllText(FindDebugBuff());

        int writer = src.IndexOf("private static void WriteModificationFlags", StringComparison.Ordinal);
        int afterWriter = src.IndexOf("private static void WriteDisableAggro", writer, StringComparison.Ordinal);
        Assert.True(writer > 0 && afterWriter > writer, "WriteModificationFlags must sit before WriteDisableAggro");
        string writerBody = src.Substring(writer, afterWriter - writer);

        // Prefab-tag gated (a live instance's ModificationId belongs to the game).
        SourcePin.AssertUncommented(writerBody, "Has<Unity.Entities.Prefab>");
        // Idempotent: add only when missing, OR the bits into whatever is there.
        SourcePin.AssertStatement(writerBody, "bool had = entity.Has<BuffModificationFlagData>(em);");
        SourcePin.AssertStatement(writerBody, "if (!had)");
        SourcePin.AssertStatement(writerBody, "entity.Add<BuffModificationFlagData>(em);");
        SourcePin.AssertUncommented(writerBody, "was | ModificationFlags");
        // Typed write path, same as LifeTime.
        SourcePin.AssertStatement(writerBody, "handle.Write(in flags)");
        SourcePin.AssertStatement(writerBody, "entity.Write(em, in flags)");
        // Never SetComponentDataRaw with a guessed size.
        Assert.DoesNotContain("SetComponentDataRaw", writerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefab_pass_writes_the_no_aggro_mode_and_the_instance_pass_does_not()
    {
        Assert.Contains(DebugBuff.StepDisableAggro, DebugBuff.PrefabPatchSteps);
        Assert.DoesNotContain(DebugBuff.StepDisableAggro, DebugBuff.InstancePatchSteps);

        // Source pin: the writer body references ProjectM types and cannot be compiled here.
        var src = System.IO.File.ReadAllText(FindDebugBuff());

        int writer = src.IndexOf("private static void WriteDisableAggro", StringComparison.Ordinal);
        int afterWriter = src.IndexOf("private static void AddPersistTag", writer, StringComparison.Ordinal);
        Assert.True(writer > 0 && afterWriter > writer, "WriteDisableAggro must sit before AddPersistTag");
        string writerBody = src.Substring(writer, afterWriter - writer);

        // Prefab-tag gated (the live instance's DisableAggroBuffModifications ids belong to the game).
        SourcePin.AssertUncommented(writerBody, "Has<Unity.Entities.Prefab>");
        // Idempotent: add only when missing, OR the mode bits into whatever is there.
        SourcePin.AssertStatement(writerBody, "bool had = entity.Has<DisableAggroBuff>(em);");
        SourcePin.AssertStatement(writerBody, "if (!had)");
        SourcePin.AssertStatement(writerBody, "entity.Add<DisableAggroBuff>(em);");
        SourcePin.AssertUncommented(writerBody, "was | AggroMode");
        // Typed write path, same as LifeTime / BuffModificationFlagData.
        SourcePin.AssertStatement(writerBody, "handle.Write(in aggro)");
        SourcePin.AssertStatement(writerBody, "entity.Write(em, in aggro)");
        Assert.DoesNotContain("SetComponentDataRaw", writerBody, StringComparison.Ordinal);
        // The paired modifications component is written by the game, never by the Sample.
        Assert.DoesNotContain("DisableAggroBuffModifications>", writerBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Kind_values_match_the_parser_theory_data()
    {
        Assert.Equal(1, (int)DebugBuff.Kind.On);
        Assert.Equal(2, (int)DebugBuff.Kind.Off);
    }

    // ------------------------------------------------------------------
    // R27 finding 2 — unloading the Sample must not leave permanent god mode
    // ------------------------------------------------------------------

    /// <summary>
    /// The unload sweep destroys every live instance it is given and reports the count.
    /// Dead entities are skipped rather than destroyed twice, and a destroy that throws does
    /// not abort the rest of the sweep.
    ///
    /// Fails on the pre-FZ code: <c>DebugBuff.Reset()</c> was
    /// <c>internal static void Reset() { }</c> and <c>SweepInstances</c> did not exist, so
    /// there was nothing here to call — the buff is infinite-duration, persists through death,
    /// and after FW/FX carries Invulnerable|ImmuneToSun|ImmuneToHazards plus DisableAggroBuff,
    /// so hot-swapping the Sample left every holder permanently invulnerable with
    /// <c>.nodrain off</c> gone along with the command registration.
    /// </summary>
    [Fact]
    public void Reset_sweep_destroys_every_live_instance_and_skips_dead_ones()
    {
        var instances = new[] { "a", "dead", "b" };
        var destroyed = new System.Collections.Generic.List<string>();

        int removed = DebugBuff.SweepInstances(
            instances,
            e => e != "dead",
            e => destroyed.Add(e));

        Assert.Equal(2, removed);
        Assert.Equal(new[] { "a", "b" }, destroyed);

        // A throw on one instance must not strand the others.
        destroyed.Clear();
        int partial = DebugBuff.SweepInstances(
            instances,
            _ => true,
            e =>
            {
                if (e == "dead") throw new InvalidOperationException("entity vanished");
                destroyed.Add(e);
            });
        Assert.Equal(2, partial);
        Assert.Equal(new[] { "a", "b" }, destroyed);

        Assert.Equal(0, DebugBuff.SweepInstances<string>(null, null, _ => { }));
        Assert.Equal(0, DebugBuff.SweepInstances(instances, null, null));
    }

    /// <summary>
    /// Unload runs the sweep and logs its count — including zero, so an operator can tell the
    /// sweep ran at all. A sweep that throws is caught: unload must still finish.
    /// </summary>
    [Fact]
    public void Reset_logs_how_many_holders_it_freed()
    {
        Assert.Equal(3, DebugBuff.Reset(() => 3));
        Assert.Contains(
            "Debug: reset removed 3 live instance(s) via TryRemoveBuff",
            _log.Infos);

        Assert.Equal(0, DebugBuff.Reset(() => 0));
        Assert.Contains(
            "Debug: reset removed 0 live instance(s) via TryRemoveBuff",
            _log.Infos);

        Assert.Equal(0, DebugBuff.Reset(() => throw new InvalidOperationException("no world")));
        Assert.Contains("Debug: reset sweep threw: no world", _log.Infos);

        Assert.Equal(0, DebugBuff.Reset(null));
        Assert.Equal(
            "reset removed 0 live instance(s) via TryRemoveBuff",
            DebugBuff.FormatResetRemoved(0));
    }

    /// <summary>
    /// The unload path a plugin unload actually takes, driven end to end on fakes: Bootstrap's
    /// <c>ReloaderHost</c> publishes the reason in the hand-off slot, the plugin resolves it with
    /// the production policy, and the Sample's buff hook decides from that. A redeploy or a Core
    /// swap keeps the live instances; only a real removal destroys them; an unresolvable reason
    /// keeps (job FBUFF4, R30 findings 3 and 4).
    /// </summary>
    [Theory]
    [InlineData(BootstrapSlot.Swap, PluginUnloadReason.Swap, 0)]
    [InlineData(BootstrapSlot.Shutdown, PluginUnloadReason.Shutdown, 0)]
    [InlineData(BootstrapSlot.Removal, PluginUnloadReason.Removal, 1)]
    public void The_bootstrap_handoff_decides_whether_the_sample_sweeps(
        int slotCode, PluginUnloadReason expected, int expectedSweeps)
    {
        BootstrapUnloadSlotProbe.ResetForTests();
        CoreSwapProbe.ResetForTests();
        BootstrapSlot.ResetForTests();
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => typeof(BootstrapSlot);
        CoreSwapProbe.TypeOverrideForTests = () => null;
        try
        {
            int sweeps = 0;
            PluginUnloadReason seen;
            using (BootstrapSlot.Push(slotCode))
                seen = PluginUnloadReasonPolicy.Resolve("Sample", m => _log.LogInfo(m), m => _log.LogWarning(m));

            Assert.Equal(expected, seen);
            DebugBuff.Reset(seen, () => { sweeps++; return 2; }, () => 2);
            Assert.Equal(expectedSweeps, sweeps);
            Assert.Contains("Reload: Sample unload reason=" + expected + " (bootstrap-slot)", _log.Infos);
        }
        finally
        {
            BootstrapSlot.ResetForTests();
            BootstrapUnloadSlotProbe.ResetForTests();
            CoreSwapProbe.ResetForTests();
        }
    }

    /// <summary>
    /// With nothing handed off and no Bootstrap to probe, the reason is
    /// <see cref="PluginUnloadReason.Unknown"/> and the buff is <em>kept</em>. Before job FBUFF4
    /// this case answered Removal, so any probe failure silently destroyed every player's buff.
    /// </summary>
    [Fact]
    public void An_unknown_reason_keeps_the_live_instances()
    {
        BootstrapUnloadSlotProbe.ResetForTests();
        CoreSwapProbe.ResetForTests();
        BootstrapUnloadSlotProbe.TypeOverrideForTests = () => null;
        CoreSwapProbe.TypeOverrideForTests = () => null;
        try
        {
            var seen = PluginUnloadReasonPolicy.Resolve("Sample", m => _log.LogInfo(m), m => _log.LogWarning(m));
            Assert.Equal(PluginUnloadReason.Unknown, seen);

            int sweeps = 0;
            Assert.Equal(0, DebugBuff.Reset(seen, () => { sweeps++; return 4; }, () => 4));
            Assert.Equal(0, sweeps);
            Assert.False(DebugBuff.ShouldSweep(PluginUnloadReason.Unknown));
            Assert.Contains("Debug: unload reason=Unknown, keeping 4 live instance(s)", _log.Infos);
        }
        finally
        {
            BootstrapUnloadSlotProbe.ResetForTests();
            CoreSwapProbe.ResetForTests();
        }
    }

    // ------------------------------------------------------------------
    // FBUFF2 — a Core hot swap must not destroy the live buff
    // ------------------------------------------------------------------

    /// <summary>
    /// The whole point of job FBUFF2. Before it, <c>Plugin.OnUnload</c> ran the sweep on every
    /// unload, and a Core swap runs an unload every time: evidence line 154 of build 3820036
    /// destroyed a <c>sample.nodrain</c> instance the world save had restored eleven lines
    /// earlier, before the operator had typed a single <c>.nodrain</c> in that process
    /// (FBUFF). A Swap and a Shutdown now keep the instance and
    /// destroy nothing; only a Removal sweeps.
    /// </summary>
    [Fact]
    public void Swap_and_shutdown_keep_live_instances_and_only_removal_sweeps()
    {
        int sweeps = 0;

        Assert.Equal(0, DebugBuff.Reset(PluginUnloadReason.Swap, () => { sweeps++; return 1; }, () => 1));
        Assert.Equal(0, sweeps);
        Assert.Contains("Debug: unload reason=Swap, keeping 1 live instance(s)", _log.Infos);

        Assert.Equal(0, DebugBuff.Reset(PluginUnloadReason.Shutdown, () => { sweeps++; return 2; }, () => 2));
        Assert.Equal(0, sweeps);
        Assert.Contains("Debug: unload reason=Shutdown, keeping 2 live instance(s)", _log.Infos);

        Assert.Equal(3, DebugBuff.Reset(PluginUnloadReason.Removal, () => { sweeps++; return 3; }, () => 3));
        Assert.Equal(1, sweeps);
        Assert.Contains(
            "Debug: reset removed 3 live instance(s) via TryRemoveBuff",
            _log.Infos);

        Assert.True(DebugBuff.ShouldSweep(PluginUnloadReason.Removal));
        Assert.False(DebugBuff.ShouldSweep(PluginUnloadReason.Swap));
        Assert.False(DebugBuff.ShouldSweep(PluginUnloadReason.Shutdown));
        Assert.Equal(
            "unload reason=Swap, keeping 1 live instance(s)",
            DebugBuff.FormatKept(PluginUnloadReason.Swap, 1));
    }

    /// <summary>
    /// A counting callback that throws (no server world, a query that faults) must not abort the
    /// unload, and must still leave the instance alone.
    /// </summary>
    [Fact]
    public void Keeping_survives_a_counter_that_throws_and_a_null_counter()
    {
        Assert.Equal(0, DebugBuff.Reset(
            PluginUnloadReason.Swap, () => 1, () => throw new InvalidOperationException("no world")));
        Assert.Contains("Debug: reset count threw: no world", _log.Infos);
        Assert.Contains("Debug: unload reason=Swap, keeping 0 live instance(s)", _log.Infos);

        Assert.Equal(0, DebugBuff.Reset(PluginUnloadReason.Swap, null, null));
        Assert.Equal(0, DebugBuff.Reset(PluginUnloadReason.Removal, null, null));
    }

    /// <summary>
    /// The debug buff is Core's now, so its unload is Core's: <c>Plugin.Uninstall</c> runs
    /// <c>DebugTools.Uninstall</c> before the map-lease sweep, the god-mode patches, the world-epoch
    /// hook and ModSave are torn down, and that unload is a <see cref="PluginUnloadReason.Swap"/>
    /// (Core only goes away for a swap or at process exit), which keeps the buff entity and the
    /// ledger and clears <c>Immortal</c> and the guard. The Sample's own unload no longer touches
    /// the buff, and its other live state is still detached unconditionally.
    /// </summary>
    [Fact]
    public void Core_uninstall_runs_the_debug_unload_first_and_the_sample_no_longer_does()
    {
        string core = SourcePin.ReadFile("src", "Progenitor.Core", "Plugin.cs");
        string uninstall = core.Substring(core.IndexOf("internal static bool Uninstall()", StringComparison.Ordinal));
        int debug = uninstall.IndexOf("Debugging.DebugTools.Uninstall();", StringComparison.Ordinal);
        Assert.True(debug > 0, "Core Uninstall must call DebugTools.Uninstall");
        foreach (var later in new[] { "Ui.WorldMap.ReleaseAll();", "WorldState.WorldLoadHook.Uninstall();", "Patches.Patches.Uninstall();", "Persistence.ModSave.Uninstall();", "Content.PrefabRegistry.Uninstall();" })
        {
            int at = uninstall.IndexOf(later, StringComparison.Ordinal);
            Assert.True(at > debug, later + " must run after DebugTools.Uninstall");
        }
        string install = core.Substring(core.IndexOf("internal static bool Install(", StringComparison.Ordinal));
        Assert.Contains("Debugging.DebugTools.Install();", install.Substring(0, install.IndexOf("internal static bool Uninstall()", StringComparison.Ordinal)), StringComparison.Ordinal);

        string tools = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugTools.cs");
        string toolsUninstall = SourcePin.MethodBody(tools, "internal static void Uninstall()");
        SourcePin.AssertUncommented(toolsUninstall, "DebugBuff.Reset(PluginUnloadReason.Swap)");

        var sample = ReadSampleFile("Plugin.cs");
        Assert.DoesNotContain("SampleBuffs", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleDebugMap", sample, StringComparison.Ordinal);
    }

    /// <summary>The Sample's non-buff state is still detached on every reason.</summary>
    [Fact]
    public void Only_the_buff_sweep_is_reason_aware_in_the_sample_unload()
    {
        var plugin = ReadSampleFile("Plugin.cs");
        int unload = plugin.IndexOf(
            "protected override void OnUnload(SampleContext ctx, PluginUnloadReason reason)",
            StringComparison.Ordinal);
        Assert.True(unload > 0);
        string body = plugin.Substring(unload);

        SourcePin.AssertStatement(body, "SampleFx.Detach();");
        SourcePin.AssertStatement(body, "SampleBeam.Detach();");
        Assert.DoesNotContain("SampleFx.Detach(reason)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleBeam.Detach(reason)", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // FBUFF5 — god mode: ProjectM.Immortal on the wearer
    // ------------------------------------------------------------------

    /// <summary>
    /// The character pass exists, names the one component a direct kill honors
    /// (<c>ProjectM.Immortal</c>, <c>bool IsImmortal @0x0</c>), and writes
    /// the same value the vanilla <c>Script_RemoveImmortality_Buff.Server.Destroy</c> restores.
    ///
    /// Fails on the pre-FBUFF5 code: neither step nor array existed, so the buff was
    /// <c>BuffableFlagState.Invulnerable</c> only and every direct <c>Kill*</c> went through.
    /// </summary>
    [Fact]
    public void Debug_buff_makes_the_wearer_immortal()
    {
        Assert.Equal("Immortal", DebugBuff.ImmortalComponentName);
        Assert.True(DebugBuff.ImmortalValue);
        Assert.Equal("Script_RemoveImmortality_Buff", DebugBuff.ImmortalVanillaSource);

        // The step is in the patch list, and the dispatcher knows the name.
        Assert.Equal(new[] { DebugBuff.StepImmortal }, DebugBuff.CharacterApplySteps);
        Assert.Equal(new[] { DebugBuff.StepClearImmortal }, DebugBuff.CharacterRemoveSteps);
        Assert.Contains(DebugBuff.StepImmortal, DebugBuff.KnownPatchSteps);
        Assert.Contains(DebugBuff.StepClearImmortal, DebugBuff.KnownPatchSteps);

        // Immortal lives on the wearer. A character step in either buff-entity pass would
        // write the buff instead of the player.
        foreach (var step in new[] { DebugBuff.StepImmortal, DebugBuff.StepClearImmortal })
        {
            Assert.DoesNotContain(step, DebugBuff.PrefabPatchSteps);
            Assert.DoesNotContain(step, DebugBuff.InstancePatchSteps);
        }
    }

    /// <summary>
    /// <c>.nodrain off</c> takes <c>Immortal</c> away again: the removal step runs on the same
    /// character, and the clear removes the component the Sample added. Nothing in the game
    /// removes it, so a missed clear strands a permanently unkillable player (R27 finding 2).
    /// </summary>
    /// <remarks>
    /// Job FBUFF7 (R32 finding 2) inverted the untracked case. The old rule was
    /// <c>!tracked || addedByUs</c>, so a character the Sample had no record for was treated as
    /// its own and lost the component — which is how vanilla's and other mods' <c>Immortal</c>
    /// was deleted. The rule is now "our grant, and the component was not already there".
    /// </remarks>
    [Theory]
    // granted, preExisting, removes
    [InlineData(true, false, true)]    // our grant, we added it -> take it away
    [InlineData(true, true, false)]    // our grant, but it was already there -> leave it
    [InlineData(false, false, false)]  // no grant -> not ours; never touch it
    [InlineData(false, true, false)]
    public void Nodrain_off_removes_only_the_immortal_the_sample_granted(
        bool granted, bool preExisting, bool removes)
    {
        Assert.Equal(removes, DebugBuff.ImmortalClearRemoves(granted, preExisting));
    }

    /// <summary>
    /// R32 finding 2, the whole rule in one place. A foreign <c>Immortal</c> is never acted on,
    /// a Sample-granted one is removed once the buff is gone, a pre-existing one only ever loses
    /// the Sample's own GodMode guard, and a live wearer keeps everything.
    /// </summary>
    /// <remarks>
    /// The expected plan arrives as an int because <c>DebugBuff</c> — and so its nested
    /// <c>ImmortalPlan</c> — is internal, and xunit's <c>InlineData</c> sits on a public method.
    /// 0 Foreign, 1 Keep, 2 Remove, 3 DropGuardOnly.
    /// </remarks>
    [Theory]
    // granted, preExisting, wearsBuff, plan
    [InlineData(false, false, false, 0)]  // vanilla's / another mod's Immortal: never touched
    [InlineData(false, false, true, 0)]
    [InlineData(false, true, false, 0)]
    [InlineData(true, false, true, 1)]    // ours, still worn: keep, re-assert the guard
    [InlineData(true, true, true, 1)]
    [InlineData(true, false, false, 2)]   // ours, buff gone: remove the component
    [InlineData(true, true, false, 3)]    // pre-existing: drop only the guard
    public void The_sample_only_ever_acts_on_its_own_grants(
        bool granted, bool preExisting, bool wearsBuff, int expected)
    {
        Assert.Equal(expected, (int)DebugBuff.PlanImmortal(granted, preExisting, wearsBuff));

        // Foreign is inert: it can never reach a component removal.
        if (!granted)
            Assert.False(DebugBuff.ImmortalClearRemoves(granted, preExisting));
    }

    /// <summary>
    /// The ledger is durable and per-player: a grant recorded through one store instance is still
    /// there when a later generation reads the same store back (the restart case R32 finding 2's
    /// fix turns on), a second <c>.nodrain on</c> does not overwrite the original
    /// <c>preExisting</c> flag, and a forgotten grant is gone.
    /// </summary>
    [Fact]
    public void The_grant_ledger_survives_a_reload_and_keeps_the_pre_existing_flag()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");

        Assert.Empty(DebugBuff.GrantIds(store));
        Assert.False(DebugBuff.TryGetGrant(store, "76561198000000001", "Alaric", out _));

        Assert.True(DebugBuff.RecordGrant(store, "76561198000000001", "Alaric", preExisting: false));
        Assert.True(DebugBuff.RecordGrant(store, "76561198000000002", "Neris", preExisting: true));
        // Re-recording keeps the original flag: the world-ready adopt re-runs the apply steps on
        // a character whose Immortal the Sample itself wrote, and must not now call it foreign.
        Assert.False(DebugBuff.RecordGrant(store, "76561198000000002", "Neris", preExisting: false));

        Assert.Equal(
            new[] { "76561198000000001", "76561198000000002" },
            DebugBuff.GrantIds(store));

        // A fresh reader over the same store == a later generation reading the save file. The
        // character is the same one (same name), which is exactly the case FBUFF8 must not break.
        Assert.True(DebugBuff.TryGetGrant(store, "76561198000000001", "Alaric", out bool ours));
        Assert.False(ours);
        Assert.True(DebugBuff.TryGetGrant(store, "76561198000000002", "Neris", out bool theirs));
        Assert.True(theirs);

        Assert.True(DebugBuff.ForgetGrant(store, "76561198000000001"));
        Assert.False(DebugBuff.ForgetGrant(store, "76561198000000001"));
        Assert.Equal(new[] { "76561198000000002" }, DebugBuff.GrantIds(store));

        // A null store is a no-op, never a throw: the Sample runs before ModSave is attached.
        Assert.Empty(DebugBuff.GrantIds(null));
        Assert.False(DebugBuff.RecordGrant(null, "76561198000000003", "x", preExisting: false));
        Assert.False(DebugBuff.TryGetGrant(null, "76561198000000003", "x", out _));
    }

    /// <summary>
    /// Job FBUFF8, R33 finding 6 — the whole finding, on the ledger the code actually reads.
    ///
    /// A player runs <c>.nodrain on</c> (grant recorded, <c>preExisting=false</c>), the row
    /// outlives the character (a wipe, a hardcore death, an admin character reset), the account
    /// makes a new character, and something else — vanilla's
    /// <c>Script_RemoveImmortality_Buff</c>, an arena, another mod's god mode — gives that new
    /// character <c>Immortal</c>. The account-keyed ledger answered "granted, not pre-existing,
    /// not wearing the buff" and the reconcile deleted a component the Sample never wrote.
    ///
    /// Fails on the FBUFF7 code, where the plan for the recreated character is
    /// <see cref="DebugBuff.ImmortalPlan.Remove"/>.
    /// </summary>
    [Fact]
    public void A_recreated_character_keeps_the_immortal_the_sample_never_granted()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        DebugBuff.RecordGrant(store, "76561198000000001", "Alaric", preExisting: false);

        // Same account, different character, not wearing sample.nodrain.
        var plan = DebugBuff.DecideForCharacter(
            store, "76561198000000001", "Vasska", wearsBuff: false, out bool preExisting, out bool dropRow);

        Assert.Equal(DebugBuff.ImmortalPlan.Foreign, plan);
        Assert.False(preExisting);
        Assert.False(DebugBuff.ImmortalClearRemoves(granted: false, preExisting));

        // FBUFF9 (R34 finding 6): deciding does not delete. The row is still there, the caller
        // was told it may drop it, and the operator was told why.
        Assert.True(dropRow);
        Assert.Equal(new[] { "76561198000000001" }, DebugBuff.GrantIds(store));
        Assert.Contains(
            _log.Infos,
            l => l.Contains("grant identity mismatch character=76561198000000001", StringComparison.Ordinal)
                && l.Contains("recorded=Alaric live=Vasska", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, l => l.Contains("grant dropped", StringComparison.Ordinal));

        // Only the reconcile's explicit drop removes it, and it says so on its own line.
        Assert.True(DebugBuff.DropGrant(store, "76561198000000001", "Vasska"));
        Assert.Empty(DebugBuff.GrantIds(store));
        Assert.Contains(
            _log.Infos,
            l => l.Contains("grant dropped character=76561198000000001", StringComparison.Ordinal)
                && l.Contains("recorded=Alaric live=Vasska", StringComparison.Ordinal));
        Assert.False(DebugBuff.DropGrant(store, "76561198000000001", "Vasska"));

        // And the next .nodrain on for the new character starts from a fresh, honest reading.
        Assert.True(DebugBuff.RecordGrant(store, "76561198000000001", "Vasska", preExisting: true));
        Assert.Equal(
            DebugBuff.ImmortalPlan.DropGuardOnly,
            DebugBuff.DecideForCharacter(store, "76561198000000001", "Vasska", false, out bool now, out bool dropAgain));
        Assert.True(now);
        Assert.False(dropAgain);
    }

    /// <summary>
    /// Job FBUFF9, R34 finding 6 — the read half. <c>TryGetGrant</c> deleted the ledger row as a
    /// side effect of reading it, so one pass in which the character's name came back empty
    /// destroyed the only record that could ever authorize the removal: the next pass found no
    /// row, answered <c>Foreign</c> forever, and the character stayed immortal for good.
    ///
    /// Reading twice must therefore return the same row, whatever the answer.
    ///
    /// Fails on the FBUFF8 code, where the second read of the mismatch and of the
    /// unreadable-name case both see an empty ledger.
    /// </summary>
    [Fact]
    public void Reading_the_ledger_never_changes_it()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        DebugBuff.RecordGrant(store, "acct", "Alaric", preExisting: false);

        // A mismatch, read twice.
        for (int pass = 0; pass < 2; pass++)
        {
            Assert.Equal(
                DebugBuff.GrantLookup.Recreated,
                DebugBuff.LookupGrant(store, "acct", "Vasska", out _, out var recorded));
            Assert.Equal("Alaric", recorded);
            Assert.False(DebugBuff.TryGetGrant(store, "acct", "Vasska", out _));
            Assert.Equal(new[] { "acct" }, DebugBuff.GrantIds(store));
        }

        // The name could not be read this pass: identity unknown, never a match and never a
        // mismatch, and the row survives to be decided by a pass that can read it.
        for (int pass = 0; pass < 2; pass++)
        {
            Assert.Equal(
                DebugBuff.GrantLookup.Unknown,
                DebugBuff.LookupGrant(store, "acct", "", out _, out _));
            Assert.Equal(
                DebugBuff.ImmortalPlan.Foreign,
                DebugBuff.DecideForCharacter(store, "acct", "", wearsBuff: false, out _, out bool dropRow));
            Assert.False(dropRow);
            Assert.Equal(new[] { "acct" }, DebugBuff.GrantIds(store));
        }
        Assert.Contains(
            _log.Infos,
            l => l.Contains("grant identity unknown character=acct", StringComparison.Ordinal)
                && l.Contains("row kept", StringComparison.Ordinal));

        // And the pass that can read the name still finds its own grant, intact.
        Assert.Equal(
            DebugBuff.GrantLookup.Ours,
            DebugBuff.LookupGrant(store, "acct", "Alaric", out bool preExisting, out _));
        Assert.False(preExisting);
        Assert.Equal(
            DebugBuff.ImmortalPlan.Remove,
            DebugBuff.DecideForCharacter(store, "acct", "Alaric", wearsBuff: false, out _, out bool drop));
        Assert.False(drop);

        // An id with no row at all is None, and reading it writes nothing either.
        Assert.Equal(
            DebugBuff.GrantLookup.None,
            DebugBuff.LookupGrant(store, "stranger", "Alaric", out _, out _));
        Assert.Equal(new[] { "acct" }, DebugBuff.GrantIds(store));
    }

    /// <summary>
    /// Job FBUFF9, R34 finding 6 — the write half. A character whose name cannot be read has no
    /// usable ledger key, so a row recorded for it could never match anything again and the
    /// <c>Immortal</c> written beside it could never be recognized as the Sample's and removed.
    /// The write is refused instead, with a line the operator can grep and a reply the player
    /// can read.
    ///
    /// Fails on the FBUFF8 code, where <c>RecordGrant</c> happily stored <c>Identity = ""</c>.
    /// </summary>
    [Fact]
    public void An_empty_character_name_refuses_the_write()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");

        Assert.False(DebugBuff.CanRecordGrant("acct", ""));
        Assert.False(DebugBuff.CanRecordGrant("acct", null));
        Assert.False(DebugBuff.CanRecordGrant("", "Alaric"));
        Assert.True(DebugBuff.CanRecordGrant("acct", "Alaric"));

        Assert.False(DebugBuff.RecordGrant(store, "acct", "", preExisting: false));
        Assert.False(DebugBuff.RecordGrant(store, "acct", null, preExisting: true));
        Assert.Empty(DebugBuff.GrantIds(store));
        Assert.Contains(
            _log.Infos,
            l => l.Contains("grant refused character=acct", StringComparison.Ordinal)
                && l.Contains("could never be removed", StringComparison.Ordinal));

        // Nothing unmatchable is in the ledger, so nothing claims the account later either.
        Assert.Equal(
            DebugBuff.GrantLookup.None,
            DebugBuff.LookupGrant(store, "acct", "Alaric", out _, out _));

        // The player is told, and told that nothing changed.
        Assert.Contains("not applied", DebugBuff.OnIdentityUnknownReply, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", DebugBuff.OnIdentityUnknownReply, StringComparison.Ordinal);
        Assert.Equal(
            "refusing to write Immortal character=45:1 (identity unknown; "
                + "a grant that cannot be recorded could never be removed)",
            DebugBuff.FormatImmortalWriteRefused("45:1"));

        // A readable name records normally, and a later grant on the same account for a
        // different character replaces the row whole — identity and the pre-existing flag —
        // rather than latching the old character's flag onto the new one.
        Assert.True(DebugBuff.RecordGrant(store, "acct", "Alaric", preExisting: false));
        Assert.True(DebugBuff.RecordGrant(store, "acct", "Vasska", preExisting: true));
        Assert.Equal(new[] { "acct" }, DebugBuff.GrantIds(store));
        Assert.True(DebugBuff.TryGetGrant(store, "acct", "Vasska", out bool preExisting));
        Assert.True(preExisting);
        Assert.Contains(
            _log.Infos,
            l => l.Contains("grant replaced character=acct stale=Alaric identity=Vasska preExisting=True",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Job FBUFF9: the reconcile is the only path that deletes a row, and only for a row it has
    /// resolved to a live character the row demonstrably is not about. The watchdog's and the
    /// unload sweep's candidate gate — <c>TryGetGrant</c> — sees the same mismatch on every pass
    /// and leaves the ledger exactly as it found it.
    /// </summary>
    [Fact]
    public void A_mismatch_drops_only_through_the_reconcile()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        DebugBuff.RecordGrant(store, "acct", "Alaric", preExisting: false);

        // The candidate gates (watchdog, unload sweep, HasOwnGrant) run every pass. None drops.
        for (int pass = 0; pass < 5; pass++)
            Assert.False(DebugBuff.TryGetGrant(store, "acct", "Vasska", out _));
        Assert.Equal(new[] { "acct" }, DebugBuff.GrantIds(store));

        // The reconcile decides first, then drops — in that order, and only on dropRow.
        var plan = DebugBuff.DecideForCharacter(
            store, "acct", "Vasska", wearsBuff: false, out _, out bool dropRow);
        Assert.Equal(DebugBuff.ImmortalPlan.Foreign, plan);
        Assert.True(dropRow);
        Assert.Equal(new[] { "acct" }, DebugBuff.GrantIds(store));

        Assert.True(DebugBuff.DropGrant(store, "acct", "Vasska"));
        Assert.Empty(DebugBuff.GrantIds(store));

        // A null store and an unknown id are no-ops, never throws.
        Assert.False(DebugBuff.DropGrant(null, "acct", "Vasska"));
        Assert.False(DebugBuff.DropGrant(store, "", "Vasska"));
    }

    /// <summary>
    /// The other half of the identity key: a save reload, a Core swap or a restart must NOT look
    /// like a recreation. The character's name is a persisted component field
    /// (<c>PlayerCharacter.Name</c>, mirrored by
    /// <c>User.CharacterName</c>), so the same character reads back the
    /// same identity and keeps its grant, its <c>preExisting</c> flag and its removal.
    /// <c>NetworkId</c> would fail here — it is handed out per session by <c>NetworkIdSystem</c>
    /// — which is why the identity is the name.
    /// </summary>
    [Fact]
    public void The_same_character_across_a_reload_keeps_its_grant()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        DebugBuff.RecordGrant(store, "76561198000000001", "Alaric", preExisting: false);
        DebugBuff.RecordGrant(store, "76561198000000002", "Neris", preExisting: true);

        // Generation 2 reads the save back: buff still on -> adopt; buff stranded -> take the
        // component back; pre-existing -> only the guard goes.
        Assert.Equal(
            DebugBuff.ImmortalPlan.Keep,
            DebugBuff.DecideForCharacter(store, "76561198000000001", "Alaric", true, out _, out _));
        Assert.Equal(
            DebugBuff.ImmortalPlan.Remove,
            DebugBuff.DecideForCharacter(store, "76561198000000001", "Alaric", false, out var ours, out _));
        Assert.False(ours);
        Assert.Equal(
            DebugBuff.ImmortalPlan.DropGuardOnly,
            DebugBuff.DecideForCharacter(store, "76561198000000002", "Neris", false, out var theirs, out _));
        Assert.True(theirs);

        // Nothing was dropped and nothing was logged as a mismatch.
        Assert.Equal(
            new[] { "76561198000000001", "76561198000000002" },
            DebugBuff.GrantIds(store));
        Assert.DoesNotContain(_log.Infos, l => l.Contains("grant identity mismatch", StringComparison.Ordinal));
    }

    /// <summary>
    /// The identity comparison itself, and the two kinds of answer that are not a match: a
    /// provable mismatch (both names readable, different characters) and an unprovable one (an
    /// FBUFF7 row with no identity, or a character whose name could not be read). Neither
    /// removes anything; job FBUFF9 (R34 finding 6) is that only the first may drop the row.
    /// </summary>
    [Theory]
    [InlineData("Alaric", "Alaric", true, (int)DebugBuff.GrantLookup.Ours)]
    [InlineData("Alaric", "alaric", false, (int)DebugBuff.GrantLookup.Recreated)]   // ordinal
    [InlineData("Alaric", "Vasska", false, (int)DebugBuff.GrantLookup.Recreated)]
    [InlineData("", "Alaric", false, (int)DebugBuff.GrantLookup.None)]   // refused at record time
    [InlineData("Alaric", "", false, (int)DebugBuff.GrantLookup.Unknown)]   // name unreadable
    [InlineData("", "", false, (int)DebugBuff.GrantLookup.None)]
    [InlineData(null, null, false, (int)DebugBuff.GrantLookup.None)]
    public void An_unprovable_identity_is_never_a_match(
        string? recorded, string? live, bool matches, int expected)
    {
        // int, not the enum: the test class is public and DebugBuff is internal.
        var lookup = (DebugBuff.GrantLookup)expected;
        Assert.Equal(matches, DebugBuff.IdentityMatches(recorded, live));

        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        // An identity-less row can no longer even be written (FBUFF9), so those rows are None.
        DebugBuff.RecordGrant(store, "acct", recorded, preExisting: false);
        Assert.Equal(lookup, DebugBuff.LookupGrant(store, "acct", live, out _, out _));
        Assert.Equal(
            matches ? DebugBuff.ImmortalPlan.Remove : DebugBuff.ImmortalPlan.Foreign,
            DebugBuff.DecideForCharacter(store, "acct", live, wearsBuff: false, out _, out bool dropRow));

        // Only a provable mismatch is droppable, and even then the read left the row alone.
        Assert.Equal(lookup == DebugBuff.GrantLookup.Recreated, dropRow);
        Assert.Equal(
            lookup != DebugBuff.GrantLookup.None,
            DebugBuff.GrantIds(store).Count == 1);
    }

    /// <summary>
    /// The reconcile's candidate set is the ledger, so a player with a foreign <c>Immortal</c>
    /// is not in it at all, and one whose grant is recorded is. Behavior, not a source grep:
    /// <see cref="DebugBuff.GrantIds"/> is the reconcile's whole work list and
    /// <see cref="DebugBuff.DecideForCharacter"/> is what it does with each row, so an id the
    /// Sample never granted has no plan but <c>Foreign</c> whatever the world looks like.
    /// </summary>
    [Fact]
    public void The_reconcile_candidate_set_is_the_ledger_not_the_world()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        DebugBuff.RecordGrant(store, "granted", "Alaric", preExisting: false);

        var ids = DebugBuff.GrantIds(store);
        Assert.Equal(new[] { "granted" }, ids);
        Assert.DoesNotContain("vanilla-immortal-player", ids);
        Assert.DoesNotContain("other-mod-god-mode", ids);

        // Everyone outside the ledger is Foreign — with or without the buff, with or without a
        // name the Sample happens to have seen before.
        foreach (var wears in new[] { true, false })
        foreach (var identity in new[] { "Alaric", "somebody-else" })
        {
            Assert.Equal(
                DebugBuff.ImmortalPlan.Foreign,
                DebugBuff.DecideForCharacter(store, "vanilla-immortal-player", identity, wears, out _, out _));
            Assert.Equal(
                DebugBuff.ImmortalPlan.Foreign,
                DebugBuff.DecideForCharacter(store, "other-mod-god-mode", identity, wears, out _, out _));
        }

        // The granted row is untouched by all of that.
        Assert.Equal(new[] { "granted" }, DebugBuff.GrantIds(store));
    }

    /// <summary>The two log lines an operator greps for.</summary>
    [Fact]
    public void The_immortal_steps_log_what_they_did()
    {
        Assert.Equal(
            "edit Immortal IsImmortal=True added=True was=False",
            DebugBuff.FormatImmortalSet(added: true, was: false));
        Assert.Equal(
            "edit Immortal cleared removed=True present=True",
            DebugBuff.FormatImmortalCleared(removed: true, present: true));
    }

    /// <summary>
    /// Source pin (the bodies reference ProjectM types and cannot be compiled here): every
    /// removal path runs <see cref="DebugBuff.CharacterRemoveSteps"/> — <c>.nodrain off</c>,
    /// the <c>Removal</c> sweep (through <c>Buff.Target</c>) and the watchdog
    /// for a buff that ended some other way — and the writer uses the typed entity toolkit,
    /// never a raw sized write.
    /// </summary>
    [Fact]
    public void Every_removal_path_clears_immortal()
    {
        var src = System.IO.File.ReadAllText(FindDebugBuff());

        // Apply: exactly one call site, the post-ApplyBuff character pass.
        SourcePin.AssertStatement(src, "RunCharacterSteps(em, characterEntity, CharacterApplySteps);");

        // `.nodrain off`, the unload sweep (via the instance's Buff.Target) and the watchdog.
        // `.debug off` runs the remove steps through RunExplicitOffSteps (2026-10-11: an explicit
        // off removes any Immortal a protection grant does not hold).
        SourcePin.AssertStatement(src, "RunCharacterSteps(em, character, CharacterRemoveSteps);");
        SourcePin.AssertStatement(src, "RunExplicitOffSteps(em, t.Character);");
        SourcePin.AssertStatement(src, "ClearImmortalForInstance(em, e);");
        SourcePin.AssertStatement(src, "RunCharacterSteps(em, buff.Target, CharacterRemoveSteps);");
        SourcePin.AssertStatement(src, "RunCharacterSteps(em, stale[i], CharacterRemoveSteps);");

        int writer = src.IndexOf("private static void WriteImmortal", StringComparison.Ordinal);
        int afterWriter = src.IndexOf("private static void ClearImmortal(", writer, StringComparison.Ordinal);
        Assert.True(writer > 0 && afterWriter > writer, "WriteImmortal must sit before ClearImmortal");
        string writerBody = src.Substring(writer, afterWriter - writer);

        // Typed toolkit, add only when missing, and record whether the Add was ours.
        SourcePin.AssertStatement(writerBody, "bool had = character.Has<Immortal>(em);");
        SourcePin.AssertStatement(writerBody, "if (!had)");
        SourcePin.AssertStatement(writerBody, "character.Add<Immortal>(em);");
        SourcePin.AssertStatement(writerBody, "character.Write(em, in immortal);");
        SourcePin.AssertStatement(writerBody, "ImmortalHolders[character] = !preExisting;");
        Assert.DoesNotContain("SetComponentDataRaw", writerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetComponentDataRawRW", writerBody, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // FBUFF6 — Immortal must never outlive the buff (R31 findings 3, 9, 10)
    // ------------------------------------------------------------------

    /// <summary>
    /// R31 finding 3. <c>ProjectM.Immortal</c> carries no <c>[ExcludeComponent]</c> /
    /// <c>[ComponentExcludesEntity]</c> and PersistenceV2 is
    /// opt-out, so a kept <c>Immortal</c> goes into the world save with nothing in the game able
    /// to remove it. The character clear therefore runs on <b>every</b> unload reason — Swap,
    /// Shutdown and Unknown included, where the buff itself is deliberately kept.
    ///
    /// Fails on the code at <c>3439f90</c>: <c>Reset(reason, sweep, count)</c> took the keep
    /// branch and returned without clearing anything.
    /// </summary>
    [Theory]
    [InlineData(PluginUnloadReason.Swap, 0)]
    [InlineData(PluginUnloadReason.Shutdown, 0)]
    [InlineData(PluginUnloadReason.Unknown, 0)]
    [InlineData(PluginUnloadReason.Removal, 1)]
    public void Every_unload_reason_clears_immortal_even_when_the_buff_is_kept(
        PluginUnloadReason reason, int expectedSweeps)
    {
        int sweeps = 0;
        int clears = 0;

        DebugBuff.Reset(
            reason,
            () => { sweeps++; return 2; },
            () => 2,
            () => { clears++; return 3; });

        Assert.Equal(expectedSweeps, sweeps);
        Assert.Equal(1, clears);
        Assert.Contains(
            "Debug: unload reason=" + reason
                + ", cleared Immortal + god-mode guard from 3 character(s)",
            _log.Infos);
    }

    /// <summary>
    /// The clear is logged even at zero (the operator must see it ran), a clear that throws does
    /// not abort the unload, and the three-argument overload still behaves as before.
    /// </summary>
    [Fact]
    public void The_unload_clear_is_logged_at_zero_and_survives_a_throw()
    {
        DebugBuff.Reset(PluginUnloadReason.Swap, () => 0, () => 0, () => 0);
        Assert.Contains(
            "Debug: unload reason=Swap, cleared Immortal + god-mode guard from 0 character(s)",
            _log.Infos);

        Assert.Equal(
            5,
            DebugBuff.Reset(
                PluginUnloadReason.Removal,
                () => 5,
                () => 5,
                () => throw new InvalidOperationException("no world")));
        Assert.Contains("Debug: reset clear threw: no world", _log.Infos);
        Assert.Contains(
            "Debug: unload reason=Removal, cleared Immortal + god-mode guard from 0 character(s)",
            _log.Infos);

        // The three-argument overload delegates with no clear callback and must not throw.
        Assert.Equal(0, DebugBuff.Reset(PluginUnloadReason.Swap, () => 1, () => 1));
    }

    /// <summary>
    /// R31 finding 10. The stale set is computed from the candidates handed in and the live
    /// "is this character wearing the buff" predicate — no statics table anywhere in the rule.
    /// A predicate that throws is treated as "not wearing it", which errs towards clearing:
    /// a stranded immortal is the unrecoverable outcome, a redundant clear is not.
    /// </summary>
    [Fact]
    public void The_watchdog_rule_clears_every_carrier_whose_buff_is_gone()
    {
        var candidates = new[] { "wearer", "orphan-a", "orphan-b" };

        var stale = DebugBuff.PlanImmortalClears(candidates, c => c == "wearer");
        Assert.Equal(new[] { "orphan-a", "orphan-b" }, stale);

        // Nobody wears it (a fresh generation, or the buff ended some other way).
        Assert.Equal(candidates, DebugBuff.PlanImmortalClears(candidates, null));

        // A predicate that throws must not strand anyone.
        Assert.Equal(
            candidates,
            DebugBuff.PlanImmortalClears<string>(
                candidates, _ => throw new InvalidOperationException("query faulted")));

        Assert.Empty(DebugBuff.PlanImmortalClears<string>(null, _ => false));
        Assert.Empty(DebugBuff.PlanImmortalClears(candidates, _ => true));
    }

    /// <summary>The FBUFF6 log lines an operator greps for.</summary>
    [Fact]
    public void The_fbuff6_paths_log_what_they_found()
    {
        Assert.Equal(
            "unload reason=Swap, cleared Immortal + god-mode guard from 2 character(s)",
            DebugBuff.FormatUnloadCleared(PluginUnloadReason.Swap, 2));
        // FBUFF17 widened the signature with `stateReadable`; the readable strings are
        // byte-identical to what this test pinned before, which is the point of the parameter.
        Assert.Equal(
            "off: buff not present, immortal=True guard=True (clearing anyway)",
            DebugBuff.FormatOffNotPresent(stateReadable: true, hadImmortal: true, hadGuard: true));
        Assert.Equal(
            "off: buff not present, immortal=False guard=False (clearing anyway)",
            DebugBuff.FormatOffNotPresent(stateReadable: true, hadImmortal: false, hadGuard: false));
        Assert.Equal(
            "world-ready reconcile over 4 own grant(s): adopted 1, removed 2, "
                + "pre-existing kept 1, unresolved 0 (unreadable 0, gave up 0), dropped 0",
            DebugBuff.FormatReconcile(
                grants: 4, adopted: 1, removed: 2, foreignKept: 1,
                unresolved: 0, unreadable: 0, gaveUp: 0, dropped: 0));

        // FBUFF18 (R46 finding 1): the unreadable rows are a subset of the unresolved ones - an
        // unreadable read-back holds the latch open exactly like any other unverified row - and
        // the line is worded so an operator can see that rather than adding the two together.
        Assert.Equal(
            "world-ready reconcile over 4 own grant(s): adopted 1, removed 0, "
                + "pre-existing kept 0, unresolved 3 (unreadable 3, gave up 0), dropped 0",
            DebugBuff.FormatReconcile(
                grants: 4, adopted: 1, removed: 0, foreignKept: 0,
                unresolved: 3, unreadable: 3, gaveUp: 0, dropped: 0));

        // FBUFF19 (R47 finding 1): the gave-up rows are the other subset of unresolved, and they
        // do not overlap the unreadable ones - a grant that has given up is skipped before any
        // read-back, so it contributes no unreadable row on the pass that skips it.
        Assert.Equal(
            "world-ready reconcile over 4 own grant(s): adopted 1, removed 0, "
                + "pre-existing kept 0, unresolved 3 (unreadable 1, gave up 2), dropped 0",
            DebugBuff.FormatReconcile(
                grants: 4, adopted: 1, removed: 0, foreignKept: 0,
                unresolved: 3, unreadable: 1, gaveUp: 2, dropped: 0));
        Assert.Equal("The debug buff is not on.", DebugBuff.OffNotPresentReply);
        Assert.NotEqual(DebugBuff.OffNotPresentReply, DebugBuff.OffRecoveredReply);
    }

    /// <summary>
    /// Source pin for the three live paths whose bodies reference ProjectM types (R31 findings 3,
    /// 9 and 10). Fails on the code at <c>3439f90</c>:
    /// <list type="bullet">
    /// <item><c>.nodrain off</c>'s not-present branch returned before the clear;</item>
    /// <item>the watchdog's first statement was <c>if (ImmortalHolders.Count == 0) return;</c>;</item>
    /// <item>nothing enumerated live <c>Immortal</c> carriers at all.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_live_immortal_paths_read_world_state_not_statics()
    {
        var src = System.IO.File.ReadAllText(FindDebugBuff());

        // Finding 9: the manual recovery path clears on the buff-is-gone branch, before the reply.
        int off = src.IndexOf("private static void Off(Target t)", StringComparison.Ordinal);
        Assert.True(off > 0, "Off(Target) not found");
        int offEnd = src.IndexOf("private static void OnCloneApplied", off, StringComparison.Ordinal);
        string offBody = src.Substring(off, offEnd - off);
        int notPresent = offBody.IndexOf("FormatOffNotPresent(", StringComparison.Ordinal);
        int clear = offBody.IndexOf(
            "RunExplicitOffSteps(em, t.Character);", StringComparison.Ordinal);
        Assert.True(notPresent > 0, "the not-present branch must log what it found");
        Assert.True(clear > notPresent, "the not-present branch must clear before it replies");
        Assert.DoesNotContain("Log(\"off: not present\");", offBody, StringComparison.Ordinal);

        // Finding 10: the watchdog reads the world; the statics table is a union, not a gate.
        int dog = src.IndexOf(
            "private static void ImmortalWatchdog(", StringComparison.Ordinal);
        Assert.True(dog > 0, "ImmortalWatchdog not found");
        int dogEnd = src.IndexOf("internal readonly struct Target", dog, StringComparison.Ordinal);
        string dogBody = src.Substring(dog, dogEnd - dog);
        Assert.DoesNotContain("if (ImmortalHolders.Count == 0) return;", dogBody, StringComparison.Ordinal);
        SourcePin.AssertUncommented(dogBody, "ResolveGrantedCharacters(em)");
        SourcePin.AssertUncommented(dogBody, "PlanImmortalClears(");

        SourcePin.AssertStatement(src, "Reset(reason, ServerSweep, ServerCount, ServerClearImmortalGuards);");
        SourcePin.AssertStatement(src, "RunCharacterSteps(em, character, CharacterRemoveSteps);");
    }

    // ------------------------------------------------------------------
    // FBUFF7 — the Sample removes only the Immortal it granted (R32 findings 2, 10)
    // ------------------------------------------------------------------

    /// <summary>
    /// The reconcile's whole loop, driven over a fake world (job FBUFF8; replaces the FBUFF7
    /// source greps, which could not fail for finding 6 because every asserted substring stayed
    /// in place while the reconcile deleted a foreign <c>Immortal</c>).
    ///
    /// The fake is the ledger plus a dictionary standing in for
    /// <c>ResolveGrantedCharacters</c> — an account id mapped to the character that account
    /// currently has and whether it wears <c>sample.nodrain</c>. Every branch of the live loop
    /// (<c>Keep</c> / <c>Remove</c> / <c>DropGuardOnly</c> / unresolved / dropped) is taken here,
    /// through the same <see cref="DebugBuff.DecideForCharacter"/> the live loop calls, and the
    /// summary line is the one an operator greps.
    ///
    /// Fails on the FBUFF7 code: <c>recreated</c> plans <c>Remove</c> there, so both the plan
    /// assertion and the summary's <c>removed</c>/<c>dropped</c> counts are wrong.
    /// </summary>
    [Fact]
    public void The_reconcile_decides_every_branch_from_the_ledger()
    {
        var store = new Progenitor.Persistence.ModStore("sample.test", filePath: "");
        DebugBuff.RecordGrant(store, "wearing", "Alaric", preExisting: false);
        DebugBuff.RecordGrant(store, "stranded", "Neris", preExisting: false);
        DebugBuff.RecordGrant(store, "borrowed", "Vasska", preExisting: true);
        DebugBuff.RecordGrant(store, "offline", "Solarus", preExisting: false);
        DebugBuff.RecordGrant(store, "recreated", "Ferox", preExisting: false);

        // The world as the reconcile sees it: id -> (the character that account has now, wears).
        var world = new System.Collections.Generic.Dictionary<string, (string Identity, bool Wears)>
        {
            ["wearing"] = ("Alaric", true),
            ["stranded"] = ("Neris", false),
            ["borrowed"] = ("Vasska", false),
            // "offline" resolves to nothing — the player is not on and the entity is not there.
            ["recreated"] = ("Bram", false),   // same account, a brand-new character
        };

        var plans = new System.Collections.Generic.Dictionary<string, DebugBuff.ImmortalPlan>(
            StringComparer.Ordinal);
        int adopted = 0, removed = 0, foreignKept = 0, unresolved = 0, dropped = 0;

        var ids = DebugBuff.GrantIds(store);
        foreach (var id in ids)
        {
            if (!world.TryGetValue(id, out var row)) { unresolved++; continue; }
            var plan = DebugBuff.DecideForCharacter(store, id, row.Identity, row.Wears, out _, out bool dropRow);
            if (dropRow) DebugBuff.DropGrant(store, id, row.Identity);
            plans[id] = plan;
            switch (plan)
            {
                case DebugBuff.ImmortalPlan.Keep: adopted++; break;
                case DebugBuff.ImmortalPlan.Remove: removed++; break;
                case DebugBuff.ImmortalPlan.DropGuardOnly: foreignKept++; break;
                case DebugBuff.ImmortalPlan.Foreign: dropped++; break;
            }
        }

        Assert.Equal(DebugBuff.ImmortalPlan.Keep, plans["wearing"]);
        Assert.Equal(DebugBuff.ImmortalPlan.Remove, plans["stranded"]);
        Assert.Equal(DebugBuff.ImmortalPlan.DropGuardOnly, plans["borrowed"]);
        // The finding: the recreated character's Immortal is nobody's business of the Sample's.
        Assert.Equal(DebugBuff.ImmortalPlan.Foreign, plans["recreated"]);
        Assert.False(DebugBuff.ImmortalClearRemoves(granted: false, preExisting: false));

        Assert.Equal(
            "world-ready reconcile over 5 own grant(s): adopted 1, removed 1, "
                + "pre-existing kept 1, unresolved 1 (unreadable 0, gave up 0), dropped 1",
            DebugBuff.FormatReconcile(
                ids.Count, adopted, removed, foreignKept, unresolved,
                unreadable: 0, gaveUp: 0, dropped));

        // The unresolved row is kept (a later pass can still undo it); only the row that cannot
        // be about the character in front of us is gone.
        Assert.Equal(
            new[] { "wearing", "stranded", "borrowed", "offline" },
            DebugBuff.GrantIds(store));

        // The two branches that have no candidate at all still say so out loud.
        Assert.Equal(
            "world-ready reconcile: no server world yet, deferred to the watchdog",
            DebugBuff.ReconcileNoWorldLog);
        Assert.Equal(
            "world-ready reconcile: 0 own grant(s) recorded, nothing to do",
            DebugBuff.ReconcileNothingToDoLog);
    }

    /// <summary>The FBUFF7 lines an operator greps for.</summary>
    [Fact]
    public void The_own_grant_paths_log_each_decision()
    {
        Assert.Equal(
            "grant recorded character=7656 identity=Alaric preExisting=False (ours; removed on clear)",
            DebugBuff.FormatGrantRecorded("7656", "Alaric", preExisting: false));
        Assert.Equal(
            "grant recorded character=7656 identity=Alaric preExisting=True "
                + "(foreign Immortal; never removed, guard only)",
            DebugBuff.FormatGrantRecorded("7656", "Alaric", preExisting: true));
        Assert.Equal(
            "grant identity mismatch character=7656 recorded=Alaric live=Bram "
                + "(character recreated; no grant for this one, Immortal left alone)",
            DebugBuff.FormatGrantIdentityMismatch("7656", "Alaric", "Bram"));
        // FBUFF9: the mismatch line no longer claims to drop anything. The deletion is its own
        // line, from the reconcile only, and the unprovable case is a third line that says the
        // row was kept.
        Assert.Equal(
            "grant identity unknown character=7656 recorded=Alaric live=(none) "
                + "(identity unreadable this pass; row kept, no match and no mismatch, "
                + "Immortal left alone)",
            DebugBuff.FormatGrantIdentityUnknown("7656", "Alaric", null));
        Assert.Equal(
            "grant dropped character=7656 recorded=Alaric live=Bram "
                + "(reconcile decided Foreign for a recreated character)",
            DebugBuff.FormatGrantDropped("7656", "Alaric", "Bram"));
        Assert.Equal(
            "grant refused character=7656 (no readable character identity; "
                + "a row that cannot be matched could never be removed)",
            DebugBuff.FormatGrantRefused("7656"));
        Assert.Equal(
            "grant replaced character=7656 stale=Alaric identity=Bram preExisting=False",
            DebugBuff.FormatGrantReplaced("7656", "Alaric", "Bram", preExisting: false));
        Assert.Equal("grant forgotten character=7656", DebugBuff.FormatGrantForgotten("7656"));
        Assert.Equal(
            "character=7656 decision=Remove",
            DebugBuff.FormatImmortalDecision("7656", DebugBuff.ImmortalPlan.Remove));
        Assert.Equal(
            "leaving foreign Immortal alone character=7656 (no progenitor.debug grant recorded)",
            DebugBuff.FormatForeignImmortal("7656"));
    }

    // ------------------------------------------------------------------
    // R27 finding 3 — the debug commands are admin-gated
    // ------------------------------------------------------------------

    /// <summary>
    /// Every Sample command that grants a buff or spawns an entity carries
    /// <c>adminOnly: true</c>. Read out of the attribute argument list per command, so it
    /// cannot pass on the word appearing somewhere else in the file.
    ///
    /// Fails on the pre-FZ code: none of the three attributes carried the argument.
    /// <c>SampleCommands.cs</c> references ProjectM / Unity.Entities and cannot be compiled
    /// into this project, so the gate's <em>behavior</em> is pinned separately by
    /// <c>CommandRegistryTests</c> against <c>CommandAttribute</c> / <c>VisibleTo</c>.
    /// </summary>
    [Theory]
    [InlineData("beam")]
    [InlineData("rat")]
    public void Debug_commands_are_admin_only(string command)
    {
        Assert.Contains("adminOnly: true", CommandAttributeArgs(command), StringComparison.Ordinal);
    }

    /// <summary>
    /// The read-only / self-service commands stay open, so the gate is a decision and not a
    /// blanket. <c>.fx</c> only sends a cosmetic packet to the caller's own client.
    /// </summary>
    [Theory]
    [InlineData("joins")]
    [InlineData("order")]
    [InlineData("fx")]
    public void Informational_commands_are_not_gated(string command)
    {
        Assert.DoesNotContain("adminOnly", CommandAttributeArgs(command), StringComparison.Ordinal);
    }

    /// <summary>The argument list of <c>[Command("name", ...)]</c> in SampleCommands.cs.</summary>
    private static string CommandAttributeArgs(string command)
    {
        var src = ReadSampleFile("SampleCommands.cs");
        string marker = "[Command(\"" + command + "\"";
        int start = src.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "no [Command(\"" + command + "\"...)] in SampleCommands.cs");
        int end = src.IndexOf(")]", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated [Command] attribute for " + command);
        return src.Substring(start, end - start);
    }

    /// <summary>
    /// The all-clear line is only reachable when every one of the five protections reads on and
    /// the kill-path prefixes are all installed. This is the line the operator trusts before
    /// walking into daylight, so it gets its own test.
    /// </summary>
    [Fact]
    public void FormatStatusLines_says_all_five_are_live_when_they_are()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "328855:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Equal(DebugBuff.StatusHeader, lines[0]);
        Assert.Contains(lines, l => l.Contains("all five protections are live"));
        Assert.DoesNotContain(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
        Assert.DoesNotContain(lines, l => l.Contains("OFF"));
        Assert.Contains(lines, l => l.Contains("328855:1"));
    }

    /// <summary>Each protection can fail on its own, and each failure must reach the verdict.</summary>
    [Theory]
    [InlineData("drain")]
    [InlineData("aggro")]
    [InlineData("persists")]
    [InlineData("immortal")]
    [InlineData("guard")]
    [InlineData("prefixes")]
    public void FormatStatusLines_warns_when_any_single_protection_is_missing(string missing)
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "1:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: missing != "immortal",
            guardReadable: true,
            guarded: missing != "guard",
            killPathPatches: missing == "prefixes" ? 2 : 3,
            requiredPatches: 3,
            noDrain: missing != "drain",
            noAggro: missing != "aggro",
            persists: missing != "persists",
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
        Assert.DoesNotContain(lines, l => l.Contains("all five protections are live"));
    }

    /// <summary>
    /// A missing flag bit is a missing protection even though all three share one field, so the
    /// line names each bit separately.
    /// </summary>
    [Fact]
    public void FormatStatusLines_reports_each_flag_bit_separately()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "1:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags & ~DebugBuff.ImmuneToSunFlag,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("invulnerable: on") && l.Contains("sun-immune: OFF"));
        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
    }

    /// <summary>
    /// No buff at all is the state the player was in when they died the first time, and it must
    /// say so in the first line rather than only in the verdict.
    /// </summary>
    [Fact]
    public void FormatStatusLines_names_the_missing_buff_first()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: false,
            buffLabel: "",
            flagsReadable: true,
            flagsPresent: true,
            flags: 0,
            immortalReadable: true,
            immortal: false,
            guardReadable: true,
            guarded: false,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: false,
            noAggro: false,
            persists: false,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains("OFF", lines[1]);
        Assert.Contains(".debug on", lines[1]);
        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
    }

    /// <summary>
    /// "I could not read it" is not "it is off". A read that failed says UNREADABLE, because the
    /// two answers call for different behavior from the person reading them.
    /// </summary>
    [Fact]
    public void FormatStatusLines_distinguishes_unreadable_from_off()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "1:1",
            flagsReadable: false,
            flagsPresent: false,
            flags: 0,
            immortalReadable: false,
            immortal: false,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("BuffableFlagState did not read"));
        Assert.Contains(lines, l => l.Contains("Immortal did not read"));
        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
    }

    /// <summary>
    /// Flags reading a real zero is a genuine answer, not the unreadable sentinel: it must show
    /// the bits as off and the value as 0, never as UNREADABLE.
    /// </summary>
    [Fact]
    public void FormatStatusLines_treats_zero_flags_as_readable()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "1:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: 0,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("BuffableFlagState=0"));
        Assert.DoesNotContain(lines, l => l.Contains("did not read"));
    }

    /// <summary>Buff components are only reported on when there is a buff to carry them.</summary>
    [Fact]
    public void FormatStatusLines_does_not_credit_buff_components_without_a_buff()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: false,
            buffLabel: "",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("no blood drain: OFF"));
        Assert.Contains(lines, l => l.Contains("ignored by foes: OFF"));
        Assert.Contains(lines, l => l.Contains("survives death: OFF"));
    }

    /// <summary>
    /// R43 finding 7, the case that matters. When the god-mode read throws, <c>Status</c> hands
    /// down its zero-initialized locals: guard false, 0 prefixes required, 0 installed. Under
    /// the old shape `0 == 0` satisfied the strictest term of the all-clear, so the readout
    /// could print "all five protections are live" over god-mode state it never read — which is
    /// the `prefixes=0` state evidence 162 found, under the sentence an admin trusted before
    /// dying. An unreadable group must never be able to produce the all-clear.
    /// </summary>
    [Fact]
    public void FormatStatusLines_cannot_all_clear_when_the_god_mode_read_failed()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "328855:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: true,
            // Exactly what the catch path leaves behind: nothing was read, so nothing is known.
            guardReadable: false,
            guarded: false,
            killPathPatches: 0,
            requiredPatches: 0,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.DoesNotContain(lines, l => l.Contains("all five protections are live"));
        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
        Assert.Contains(lines, l => l.Contains("kill-path guard: UNREADABLE"));
    }

    /// <summary>
    /// The other half of finding 7: a *readable* zero-of-zero is not an all-clear either. Zero
    /// required prefixes is what a policy that did not answer looks like, and "0/0 installed" is
    /// the one arithmetic that makes a total absence of protection pass an equality test.
    /// </summary>
    [Fact]
    public void FormatStatusLines_never_all_clears_on_zero_of_zero_prefixes()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "328855:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 0,
            requiredPatches: 0,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.DoesNotContain(lines, l => l.Contains("all five protections are live"));
        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
    }

    /// <summary>
    /// An unreadable god-mode group says UNREADABLE, not OFF — the same rule the flags and
    /// <c>Immortal</c> groups already followed. "I could not tell" and "it is not there" send
    /// the operator to different places.
    /// </summary>
    [Fact]
    public void FormatStatusLines_reports_an_unreadable_guard_as_unreadable_not_off()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "1:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: true,
            guardReadable: false,
            guarded: false,
            killPathPatches: 0,
            requiredPatches: 0,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("kill-path guard: UNREADABLE - Core god-mode state did not read"));
        Assert.DoesNotContain(lines, l => l.Contains("kill-path guard: OFF"));
        // The fabricated counts must not be printed as if they were read.
        Assert.DoesNotContain(lines, l => l.Contains("0/0 installed"));
    }

    /// <summary>
    /// R43 finding 8. A character with no <c>BuffableFlagState</c> component at all is not a
    /// failed read — it is the strongest possible answer, that all three bits are definitively
    /// off. Reporting it as UNREADABLE replaced the strongest statement with the weakest and
    /// sent the operator to the interop instead of to the buff.
    /// </summary>
    [Fact]
    public void FormatStatusLines_reports_an_absent_BuffableFlagState_as_off_not_unreadable()
    {
        var lines = DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "1:1",
            flagsReadable: true,
            flagsPresent: false,
            flags: 0,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: null,
            reconcileThrowingPasses: 0);

        Assert.Contains(lines, l => l.Contains("invulnerable / sun / hazards: OFF")
                                    && l.Contains("no BuffableFlagState component"));
        Assert.DoesNotContain(lines, l => l.Contains("did not read"));
        // Absent is a definite answer, and the answer is that three protections are missing.
        Assert.DoesNotContain(lines, l => l.Contains("all five protections are live"));
        Assert.Contains(lines, l => l.Contains("SOMETHING ABOVE IS OFF"));
    }

    /// <summary>
    /// R43 finding 9. `.nodrain on` over an existing buff re-runs the character pass, which
    /// returns void and swallows every failure — <c>WriteImmortal</c> has three paths that reach
    /// neither <c>GodMode.Add</c> nor any component write. The reply must be worded from a
    /// read-back of the guard, never from the fact that the pass was called: evidence 162 closes
    /// with "a reply that says what a command intended is not evidence".
    /// </summary>
    [Fact]
    public void FormatAlreadyOnReply_does_not_claim_a_re_arm_the_guard_read_denies()
    {
        string held = DebugBuff.FormatAlreadyOnReply("328855:1", guardReadable: true, guarded: true);
        Assert.Contains("re-armed the process-lifetime guard", held);
        Assert.Contains("328855:1", held);

        string refused = DebugBuff.FormatAlreadyOnReply("328855:1", guardReadable: true, guarded: false);
        Assert.DoesNotContain("re-armed the process-lifetime guard", refused);
        Assert.Contains("still OFF", refused);
        Assert.Contains("read the status below", refused);

        string unknown = DebugBuff.FormatAlreadyOnReply("328855:1", guardReadable: false, guarded: false);
        Assert.DoesNotContain("re-armed the process-lifetime guard", unknown);
        // Unreadable is not OFF here either: it does not assert the guard is missing.
        Assert.DoesNotContain("still OFF", unknown);
        Assert.Contains("could not read", unknown);
        Assert.Contains("read the status below", unknown);
    }


    /// <summary>
    /// Evidence 166. WorldReady fires on a server boot before any player has connected, so every
    /// granted character is unresolvable at exactly the moment the reconcile runs. Latching there
    /// meant the god-mode guard the pass exists to re-take was never taken, and the wearer kept
    /// the buff icon with no direct-kill protection until something ran `.nodrain on` again.
    /// </summary>
    [Fact]
    public void An_unresolved_grant_does_not_end_the_reconcile()
    {
        Assert.False(DebugBuff.ReconcileMayLatch(1));
        Assert.False(DebugBuff.ReconcileMayLatch(7));
    }

    /// <summary>Nothing left to resolve is the only reason to stop retrying.</summary>
    [Fact]
    public void A_fully_resolved_reconcile_latches()
    {
        Assert.True(DebugBuff.ReconcileMayLatch(0));

        // Defensive: a negative count is nonsense, and treating it as "keep retrying forever"
        // would be worse than treating it as done.
        Assert.True(DebugBuff.ReconcileMayLatch(-1));
    }


    // ------------------------------------------------------------------
    // FBUFF17 — a success verdict is derived from a read-back, not from the
    // absence of a thrown exception (R45 findings 3, 18, 29)
    // ------------------------------------------------------------------

    /// <summary>
    /// The rule itself. <c>Unreadable</c> is a third state precisely so that it verifies
    /// neither direction: code holding only a bool has to pick one of "on" or "off" as the
    /// failure default and is wrong half the time.
    /// </summary>
    [Fact]
    public void An_unreadable_read_back_verifies_neither_direction()
    {
        Assert.True(DebugBuff.Verified(DebugBuff.ReadBack.Yes, expected: true));
        Assert.True(DebugBuff.Verified(DebugBuff.ReadBack.No, expected: false));

        Assert.False(DebugBuff.Verified(DebugBuff.ReadBack.No, expected: true));
        Assert.False(DebugBuff.Verified(DebugBuff.ReadBack.Yes, expected: false));

        // The row the whole job is about.
        Assert.False(DebugBuff.Verified(DebugBuff.ReadBack.Unreadable, expected: true));
        Assert.False(DebugBuff.Verified(DebugBuff.ReadBack.Unreadable, expected: false));

        Assert.Equal(DebugBuff.ReadBack.Unreadable, DebugBuff.ToReadBack(readable: false, value: true));
        Assert.Equal(DebugBuff.ReadBack.Unreadable, DebugBuff.ToReadBack(readable: false, value: false));
        Assert.Equal(DebugBuff.ReadBack.Yes, DebugBuff.ToReadBack(readable: true, value: true));
        Assert.Equal(DebugBuff.ReadBack.No, DebugBuff.ToReadBack(readable: true, value: false));

        Assert.Equal("?", DebugBuff.Show(DebugBuff.ReadBack.Unreadable));
        Assert.Equal("True", DebugBuff.Show(DebugBuff.ReadBack.Yes));
        Assert.Equal("False", DebugBuff.Show(DebugBuff.ReadBack.No));
    }

    /// <summary>
    /// R45 finding 3, the headline. A swallowed apply failure must not produce a success verdict
    /// and must not close the latch. <c>69d528e</c> moved the latch onto "nothing unresolved" and
    /// left <c>adopted++</c> running unconditionally after a <c>void</c>, everything-swallowing
    /// <c>RunCharacterSteps</c> — so a Keep row whose <c>GodMode.Add</c> never ran (the FBUFF9
    /// refusal, or a throw inside <c>WriteImmortal</c>) was counted as adopted, left
    /// <c>unresolved</c> at zero, closed the latch for the rest of the generation, and printed
    /// <c>adopted 1</c> over evidence 162's exact state.
    /// </summary>
    [Fact]
    public void A_swallowed_apply_failure_is_not_adopted_and_does_not_close_the_latch()
    {
        // The guard read back as not held: GodMode.Add never ran.
        Assert.Equal(
            DebugBuff.ReconcileTally.Unresolved,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Keep, DebugBuff.ReadBack.No, dropRow: false));

        // The read-back itself threw: still not evidence of an adopt. FBUFF18 (R46 finding 1)
        // gave that its own tally so the retry can be bounded, and the row still holds the latch
        // open, which is the half FBUFF17 is about.
        Assert.Equal(
            DebugBuff.ReconcileTally.Unreadable,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Keep, DebugBuff.ReadBack.Unreadable, dropRow: false));

        // ... and the latch is computed from the count those rows land in, so it stays open and
        // the watchdog retries. This is the whole chain the finding is about, end to end.
        int unresolved = 0;
        foreach (var readBack in new[] { DebugBuff.ReadBack.No, DebugBuff.ReadBack.Unreadable })
            if (DebugBuff.HoldsLatchOpen(
                    DebugBuff.TallyReconcileRow(DebugBuff.ImmortalPlan.Keep, readBack, dropRow: false)))
                unresolved++;
        Assert.Equal(2, unresolved);
        Assert.False(DebugBuff.ReconcileMayLatch(unresolved));

        // Only a guard actually read back as held is an adopt, and only then does the latch close.
        Assert.Equal(
            DebugBuff.ReconcileTally.Adopted,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Keep, DebugBuff.ReadBack.Yes, dropRow: false));
        Assert.True(DebugBuff.ReconcileMayLatch(0));
    }

    /// <summary>
    /// The removal half of the same rule. <c>RunCharacterSteps(CharacterRemoveSteps)</c> is the
    /// same void call, and a character whose guard was not dropped is one Core's kill prefixes
    /// keep alive — so <c>removed</c> and <c>pre-existing kept</c> are verdicts too and may only
    /// be counted for a guard read back as dropped. <c>Foreign</c> touches nothing on the
    /// character, so its evidence is the identity that proved the row is about somebody else and
    /// it takes no read-back at all.
    /// </summary>
    [Fact]
    public void The_other_reconcile_counters_are_also_derived_from_the_read_back()
    {
        Assert.Equal(
            DebugBuff.ReconcileTally.Removed,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Remove, DebugBuff.ReadBack.No, dropRow: false));
        Assert.Equal(
            DebugBuff.ReconcileTally.Unresolved,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Remove, DebugBuff.ReadBack.Yes, dropRow: false));
        Assert.Equal(
            DebugBuff.ReconcileTally.Unreadable,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Remove, DebugBuff.ReadBack.Unreadable, dropRow: false));

        Assert.Equal(
            DebugBuff.ReconcileTally.ForeignKept,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.DropGuardOnly, DebugBuff.ReadBack.No, dropRow: false));
        Assert.Equal(
            DebugBuff.ReconcileTally.Unreadable,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.DropGuardOnly, DebugBuff.ReadBack.Unreadable, dropRow: false));

        // Foreign: the read-back is ignored in both directions, and dropRow decides.
        Assert.Equal(
            DebugBuff.ReconcileTally.Dropped,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Foreign, DebugBuff.ReadBack.Unreadable, dropRow: true));
        Assert.Equal(
            DebugBuff.ReconcileTally.Unresolved,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Foreign, DebugBuff.ReadBack.Yes, dropRow: false));
    }

    /// <summary>
    /// R45 finding 18. <c>.nodrain off</c>'s recovery path reads three unguarded IL2CPP calls
    /// under one <c>try</c> with both booleans initialized to "nothing here", then prints a
    /// player sentence and an operator line over them. A throw in any of the three used to read
    /// as "The debug buff is not on." while the clear it just ran may have taken a real guard away.
    /// </summary>
    [Fact]
    public void The_off_recovery_path_cannot_all_clear_over_a_read_that_threw()
    {
        Assert.Equal(
            "off: buff not present, immortal=? guard=? (clearing anyway)",
            DebugBuff.FormatOffNotPresent(stateReadable: false, hadImmortal: false, hadGuard: false));

        string unreadable = DebugBuff.FormatOffNotPresentReply(
            stateReadable: false, hadImmortal: false, hadGuard: false);
        Assert.Equal(DebugBuff.OffStateUnreadableReply, unreadable);
        Assert.NotEqual(DebugBuff.OffNotPresentReply, unreadable);
        Assert.NotEqual(DebugBuff.OffRecoveredReply, unreadable);
        Assert.Contains("could not read", unreadable);
        Assert.Contains("read the status below", unreadable);

        // The two genuine answers are unchanged.
        Assert.Equal(
            DebugBuff.OffNotPresentReply,
            DebugBuff.FormatOffNotPresentReply(
                stateReadable: true, hadImmortal: false, hadGuard: false));
        Assert.Equal(
            DebugBuff.OffRecoveredReply,
            DebugBuff.FormatOffNotPresentReply(
                stateReadable: true, hadImmortal: true, hadGuard: false));
        Assert.Equal(
            DebugBuff.OffRecoveredReply,
            DebugBuff.FormatOffNotPresentReply(
                stateReadable: true, hadImmortal: false, hadGuard: true));
    }

    /// <summary>
    /// R45 finding 29. <c>.nodrain off</c> replied a flat "Debug off." after a void call with
    /// three silent-failure paths and an outer catch that can skip <c>GodMode.Remove</c>
    /// entirely. Only a guard read back as dropped may say it plainly.
    /// </summary>
    [Fact]
    public void The_off_reply_is_worded_from_the_guard_read_back()
    {
        Assert.Equal("Debug off.", DebugBuff.FormatOffReply(DebugBuff.ReadBack.No));

        string held = DebugBuff.FormatOffReply(DebugBuff.ReadBack.Yes);
        Assert.NotEqual("Debug off.", held);
        Assert.Contains("still held", held);
        Assert.Contains("read the status below", held);

        string unknown = DebugBuff.FormatOffReply(DebugBuff.ReadBack.Unreadable);
        Assert.NotEqual("Debug off.", unknown);
        Assert.DoesNotContain("still held", unknown);
        Assert.Contains("could not read", unknown);
        Assert.Contains("read the status below", unknown);
    }

    /// <summary>
    /// The already-on overload folds into the same three states, so the module has one read-back
    /// vocabulary rather than a bool pair per call site — which is how the reconcile's adopt came
    /// to have no read-back at all while its sibling three lines away had one.
    /// </summary>
    [Fact]
    public void The_already_on_reply_takes_the_same_three_states()
    {
        Assert.Equal(
            DebugBuff.FormatAlreadyOnReply("1:1", guardReadable: true, guarded: true),
            DebugBuff.FormatAlreadyOnReply("1:1", DebugBuff.ReadBack.Yes));
        Assert.Equal(
            DebugBuff.FormatAlreadyOnReply("1:1", guardReadable: true, guarded: false),
            DebugBuff.FormatAlreadyOnReply("1:1", DebugBuff.ReadBack.No));
        Assert.Equal(
            DebugBuff.FormatAlreadyOnReply("1:1", guardReadable: false, guarded: false),
            DebugBuff.FormatAlreadyOnReply("1:1", DebugBuff.ReadBack.Unreadable));
    }

    /// <summary>
    /// Source pin for the three call sites the tests above cannot reach, because each one needs
    /// an <c>EntityManager</c> and a live <c>GodMode</c>: the reconcile counts through
    /// <see cref="DebugBuff.TallyReconcileRow"/> after a read-back rather than incrementing
    /// beside the pass, and both <c>.nodrain off</c> paths end in the live readout. R45 finding
    /// 13's lesson — a fix that lives only at an untested call site is a fix that can be
    /// reverted with every test still green.
    /// </summary>
    [Fact]
    public void The_reconcile_and_off_call_sites_are_wired_to_the_read_back()
    {
        var src = System.IO.File.ReadAllText(FindDebugBuff()).Replace("\r\n", "\n");

        int rec = src.IndexOf(
            "private static void ReconcileImmortalOnWorldReady()", StringComparison.Ordinal);
        Assert.True(rec > 0, "ReconcileImmortalOnWorldReady not found");
        int recEnd = src.IndexOf("\n    /// <summary>\n    /// Characters this generation wrote", rec, StringComparison.Ordinal);
        Assert.True(recEnd > rec, "end of ReconcileImmortalOnWorldReady not found");
        string recBody = src.Substring(rec, recEnd - rec);

        // The defect: an increment sitting beside the void pass. The tally is the ONLY thing the
        // counting switch is allowed to read — a ternary in front of it would be the same
        // "count it anyway for this plan" the finding is about, wearing the fix as a sleeve.
        Assert.DoesNotContain("CharacterApplySteps);\n                        adopted++;", recBody, StringComparison.Ordinal);
        // FBUFF18 widened the read-back call with the pass's log sink. AssertStatementExactly, not
        // Assert.Contains: the pinned call commented out, or folded into a ternary that never
        // takes this half, satisfies a substring search and proves nothing (jobs FUI26, FDOC5).
        SourcePin.AssertStatementExactly(recBody, "guardAfter = ReadGuardBack(character, report);");
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                recBody, @"guardAfter = ReadGuardBack\(character, report\);").Count);
        int tally = recBody.IndexOf(
            "var tally = TallyReconcileRow(plan, guardAfter, dropRow);\n", StringComparison.Ordinal);
        Assert.True(tally > 0, "the counting switch must read TallyReconcileRow and nothing else");
        SourcePin.AssertStatementExactly(
            recBody, "var tally = TallyReconcileRow(plan, guardAfter, dropRow);");

        // Every counter the summary prints is incremented after that switch head, never in the
        // block that runs the passes. `unresolved++` legitimately appears twice: once in the
        // tally, and once for a grant id that named no live character, which is decided before
        // any pass runs and so sits ahead of the switch.
        foreach (var counter in new[] { "adopted++", "removed++", "foreignKept++", "dropped++", "unreadable++" })
        {
            var hits = System.Text.RegularExpressions.Regex.Matches(
                recBody, System.Text.RegularExpressions.Regex.Escape(counter));
            Assert.Equal(1, hits.Count);
            Assert.True(hits[0].Index > tally, counter + " must be reached through the tally");
        }

        // `unresolved++` legitimately appears three times, and all three are before-the-pass or
        // through-the-tally: the grant that has given up (FBUFF19 — skipped before any mutating
        // step and still counted unresolved, because giving up resolved nothing), the grant id
        // that named no live character, and the tally. A fourth would be a row counted without a
        // read-back, which is FBUFF17's rule.
        var unresolvedHits = System.Text.RegularExpressions.Regex.Matches(recBody, @"unresolved\+\+");
        Assert.Equal(3, unresolvedHits.Count);
        Assert.True(unresolvedHits[0].Index < tally, "the gave-up skip runs before any pass");
        Assert.True(unresolvedHits[1].Index < tally, "the unresolvable-id increment runs before any pass");
        Assert.True(unresolvedHits[2].Index > tally, "the tally owns every other unresolved row");

        // FBUFF19: the gave-up rows are counted alongside them, and only in those two places —
        // the skip, and the call that decides a grant has just given up.
        var gaveUpHits = System.Text.RegularExpressions.Regex.Matches(recBody, @"gaveUp\+\+");
        Assert.Equal(2, gaveUpHits.Count);
        Assert.True(gaveUpHits[0].Index < unresolvedHits[0].Index + 80, "the skip counts both");

        // FBUFF18 (R46 finding 1). The latch count is fed from HoldsLatchOpen, so the failure
        // column splitting in two cannot become a way for the unreadable half to latch: the
        // second `unresolved++` is inside that branch and nowhere else.
        SourcePin.AssertStatementExactly(recBody, "if (HoldsLatchOpen(tally))");
        Assert.True(
            recBody.IndexOf("if (HoldsLatchOpen(tally))", StringComparison.Ordinal)
                < unresolvedHits[2].Index,
            "the tally's unresolved increment must sit inside the HoldsLatchOpen branch");

        int off = src.IndexOf("private static void Off(Target t)", StringComparison.Ordinal);
        Assert.True(off > 0, "Off(Target) not found");
        int offEnd = src.IndexOf("private static void OnCloneApplied", off, StringComparison.Ordinal);
        string offBody = src.Substring(off, offEnd - off);

        // R45 finding 29: Off was the one command that did not end in a live readout.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(offBody, @"Status\(t\);").Count);
        SourcePin.AssertUncommented(offBody, "FormatOffReply(guardAfterClear)");
        SourcePin.AssertUncommented(offBody, "ReadGuardBack(t.Character)");
        // R45 finding 18: the ternary that asserted a state nothing measured is gone.
        Assert.DoesNotContain(
            "hadImmortal || hadGuard ? OffRecoveredReply : OffNotPresentReply",
            offBody,
            StringComparison.Ordinal);
        SourcePin.AssertStatement(offBody, "stateReadable = true;");
        Assert.DoesNotContain("t.Reply(\"Debug off.\");", offBody, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // FBUFF18 — a retry may only be spent on evidence it can actually get
    // (R46 finding 1)
    // ------------------------------------------------------------------

    /// <summary>
    /// The split FBUFF18 introduced, and the boundary it may not move.
    ///
    /// <para>FBUFF17 mapped every unverified row to one <c>Unresolved</c> bucket, which is right
    /// for the latch and wrong for the retry: "the guard read back as not held" and "the read of
    /// the guard threw" are different facts and only the first can be improved by running the
    /// pass again. So they get separate tallies — and both still hold the latch open, because a
    /// count that gates a latch may only be incremented for work that was read back, and an
    /// unreadable row was not.</para>
    ///
    /// <para>Fails with <c>UnresolvedKind</c> reverted to a bare
    /// <c>ReconcileTally.Unresolved</c>: the three <c>Unreadable</c> assertions go first.</para>
    /// </summary>
    [Fact]
    public void An_unreadable_read_back_is_tallied_apart_from_one_that_answered()
    {
        foreach (var plan in new[]
                 {
                     DebugBuff.ImmortalPlan.Keep,
                     DebugBuff.ImmortalPlan.Remove,
                     DebugBuff.ImmortalPlan.DropGuardOnly,
                 })
        {
            Assert.Equal(
                DebugBuff.ReconcileTally.Unreadable,
                DebugBuff.TallyReconcileRow(plan, DebugBuff.ReadBack.Unreadable, dropRow: false));
        }

        // The answering-but-wrong direction stays Unresolved for each of the three.
        Assert.Equal(
            DebugBuff.ReconcileTally.Unresolved,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Keep, DebugBuff.ReadBack.No, dropRow: false));
        Assert.Equal(
            DebugBuff.ReconcileTally.Unresolved,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.Remove, DebugBuff.ReadBack.Yes, dropRow: false));
        Assert.Equal(
            DebugBuff.ReconcileTally.Unresolved,
            DebugBuff.TallyReconcileRow(
                DebugBuff.ImmortalPlan.DropGuardOnly, DebugBuff.ReadBack.Yes, dropRow: false));

        // Foreign takes no read-back at all, so it can never be Unreadable however the ignored
        // argument reads: its evidence is the identity, and dropRow carries it.
        foreach (var readBack in new[]
                 {
                     DebugBuff.ReadBack.Unreadable,
                     DebugBuff.ReadBack.No,
                     DebugBuff.ReadBack.Yes,
                 })
        {
            Assert.Equal(
                DebugBuff.ReconcileTally.Unresolved,
                DebugBuff.TallyReconcileRow(
                    DebugBuff.ImmortalPlan.Foreign, readBack, dropRow: false));
            Assert.Equal(
                DebugBuff.ReconcileTally.Dropped,
                DebugBuff.TallyReconcileRow(
                    DebugBuff.ImmortalPlan.Foreign, readBack, dropRow: true));
        }

        // The boundary FBUFF17 drew and FBUFF18 may not move: both failures hold the latch open,
        // and neither of the three verdicts does.
        Assert.True(DebugBuff.HoldsLatchOpen(DebugBuff.ReconcileTally.Unresolved));
        Assert.True(DebugBuff.HoldsLatchOpen(DebugBuff.ReconcileTally.Unreadable));
        Assert.False(DebugBuff.HoldsLatchOpen(DebugBuff.ReconcileTally.Adopted));
        Assert.False(DebugBuff.HoldsLatchOpen(DebugBuff.ReconcileTally.Removed));
        Assert.False(DebugBuff.HoldsLatchOpen(DebugBuff.ReconcileTally.ForeignKept));
        Assert.False(DebugBuff.HoldsLatchOpen(DebugBuff.ReconcileTally.Dropped));
    }

    /// <summary>
    /// A game-free model of the production reconcile's retry bookkeeping (job FBUFF19, R47
    /// finding 1), composed out of the same functions the production loop calls, in the same
    /// order. The behavioral tests below drive this over thousands of watchdog ticks; the source
    /// pin <see cref="The_reconcile_loop_is_the_composition_this_test_drives"/> is what makes that
    /// evidence about production — a test that models a loop proves nothing about a production
    /// loop that models it differently, which is R45 finding 13's lesson.
    ///
    /// <para>FBUFF18's version of this model was a handful of locals, which is why its test could
    /// not see the defect: with one grant, a per-pass budget and a per-grant budget are the same
    /// thing. The model is keyed by grant here for the same reason the fix is.</para>
    /// </summary>
    private sealed class ReconcileModel
    {
        private readonly Dictionary<string, int> _budget = new(StringComparer.Ordinal);
        private readonly List<string> _gaveUp = new();

        /// <summary>Rows whose mutating pass ran, per grant. The cost the budget exists to bound.</summary>
        internal Dictionary<string, int> MutatingPasses { get; } = new(StringComparer.Ordinal);

        /// <summary>Rows skipped because that grant had already given up, per grant.</summary>
        internal Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);

        internal List<string> GiveUpLines { get; } = new();
        internal List<string> AllStoppedLines { get; } = new();
        internal List<string> PassGaveUpLines { get; } = new();
        internal IReadOnlyList<string> GaveUpGrants => _gaveUp;

        internal bool Reconciled { get; private set; }
        internal bool EveryGrantSettled { get; private set; }
        internal bool PassGaveUp { get; private set; }
        internal int ThrowingPasses { get; private set; }
        internal int PassesRun { get; private set; }

        internal bool MayRun =>
            DebugBuff.ReconcileMayRun(Reconciled, EveryGrantSettled, PassGaveUp);

        /// <summary>
        /// The plan every modeled row takes. <c>Keep</c> is the reconcile's ordinary re-arm — the
        /// one evidence 162 is about — and the removal plans are here so the "an apply that keeps
        /// failing" case can be driven from the failing direction of either.
        /// </summary>
        internal DebugBuff.ImmortalPlan Plan { get; set; } = DebugBuff.ImmortalPlan.Keep;

        /// <summary>
        /// One watchdog tick. <paramref name="readBack"/> answers what a grant's guard read back
        /// after its mutating pass, or <c>null</c> for a grant whose character has not streamed in
        /// — evidence 166's case, where no mutating pass runs for that grant at all.
        /// </summary>
        internal void Tick(
            IReadOnlyList<string> ids,
            Func<string, DebugBuff.ReadBack?> readBack,
            bool passThrows = false)
        {
            if (!MayRun) return;
            PassesRun++;

            bool threw = false;
            try
            {
                if (passThrows) throw new InvalidOperationException("modeled mid-loop throw");

                int unresolved = 0, gaveUp = 0;
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (_gaveUp.Contains(id))
                    {
                        gaveUp++;
                        unresolved++;
                        Skipped[id] = Skipped.TryGetValue(id, out int s) ? s + 1 : 1;
                        continue;
                    }

                    var read = readBack(id);
                    if (read == null)
                    {
                        unresolved++;
                        Advance(id, rowUnreadable: false);
                        continue;
                    }

                    MutatingPasses[id] = MutatingPasses.TryGetValue(id, out int m) ? m + 1 : 1;
                    var tally = DebugBuff.TallyReconcileRow(Plan, read.Value, dropRow: false);
                    if (DebugBuff.HoldsLatchOpen(tally)) unresolved++;
                    if (Advance(id, tally == DebugBuff.ReconcileTally.Unreadable)) gaveUp++;
                }

                if (DebugBuff.ReconcileMayLatch(unresolved)) Reconciled = true;
                if (DebugBuff.ReconcileEveryGrantSettled(unresolved, gaveUp) && !EveryGrantSettled)
                {
                    EveryGrantSettled = true;
                    AllStoppedLines.Add(DebugBuff.FormatReconcileAllStopped(_gaveUp));
                }
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            finally
            {
                ThrowingPasses = DebugBuff.NextThrowingPasses(ThrowingPasses, threw);
                if (DebugBuff.ReconcilePassGaveUp(ThrowingPasses) && !PassGaveUp)
                {
                    PassGaveUp = true;
                    PassGaveUpLines.Add(DebugBuff.FormatReconcilePassGaveUp(ThrowingPasses));
                }
            }
        }

        private bool Advance(string id, bool rowUnreadable)
        {
            int prior = _budget.TryGetValue(id, out int p) ? p : 0;
            int next = DebugBuff.NextUnreadablePasses(prior, rowUnreadable ? 1 : 0);
            if (next == 0) _budget.Remove(id);
            else _budget[id] = next;

            if (!DebugBuff.ReconcileReadBackGaveUp(next)) return false;
            _gaveUp.Add(id);
            GiveUpLines.Add(DebugBuff.FormatReconcileGaveUp(id, next));
            return true;
        }

        internal int MutatingPassesFor(string id) =>
            MutatingPasses.TryGetValue(id, out int n) ? n : 0;
    }

    /// <summary>
    /// R46 finding 1, the headline: <b>a read-back that always throws must not produce an
    /// unbounded sequence of mutating passes.</b>
    ///
    /// <para>This drives the functions the watchdog composes, in the order it composes them, over
    /// ten thousand watchdog ticks with one granted character whose apply pass runs and whose
    /// guard read-back throws every single time. Before FBUFF18 this loop ran a mutating
    /// <c>RunCharacterSteps(CharacterApplySteps)</c> and wrote three log lines on every one of the
    /// ten thousand ticks; after it, three passes and one give-up line — and after FBUFF19 the
    /// same three passes, because with a single grant a per-pass budget and a per-grant budget
    /// spend at the same rate. That is exactly why this test could not see R47 finding 1, and why
    /// <see cref="One_grants_give_up_does_not_stop_another_grants_retry"/> exists.</para>
    ///
    /// <para>Fails with <c>ReconcileMayRun</c> reverted to <c>!reconciled</c>: the loop runs
    /// 10,000 mutating passes instead of 3.</para>
    /// </summary>
    [Fact]
    public void A_read_back_that_always_throws_does_not_retry_the_mutating_pass_forever()
    {
        var model = new ReconcileModel();
        var ids = new[] { "alone" };

        for (int tick = 0; tick < 10_000; tick++)
            model.Tick(ids, _ => DebugBuff.ReadBack.Unreadable);

        Assert.Equal(DebugBuff.MaxUnreadableReconcilePasses, model.MutatingPassesFor("alone"));
        Assert.True(
            model.MutatingPassesFor("alone") < 10,
            "the mutating pass must be bounded, not merely throttled");

        // Stopped, not finished. The latch never closed, so nothing anywhere claims the grant was
        // resolved — that is the whole difference between this and latching to stop the noise.
        Assert.False(model.Reconciled);
        Assert.Equal(new[] { "alone" }, model.GaveUpGrants);

        // The whole ledger is one grant, so the module stops too — but through "every grant is
        // settled", which is the derivation that also leaves a second grant alone.
        Assert.True(model.EveryGrantSettled);

        // And the operator is told once, not once per pass: once that this grant gave up, once
        // that there is nothing left for a pass to do.
        Assert.Single(model.GiveUpLines);
        Assert.Single(model.AllStoppedLines);
        Assert.False(model.PassGaveUp);
    }

    /// <summary>
    /// <b>R47 finding 1, the required test.</b> One grant whose read-back always throws must not
    /// stop the retry of any other grant.
    ///
    /// <para>Two grants over ten thousand ticks. <c>broken</c>'s guard read-back throws every
    /// pass; <c>waiting</c> is evidence 166's case — a character who has not streamed in, so no
    /// mutating pass runs for it at all and its retry is unbounded by design (the doc on
    /// <c>MaxUnreadableReconcilePasses</c> promises exactly that). FBUFF18 counted the budget per
    /// pass and stopped the reconcile per module, so after three passes <c>waiting</c> stopped
    /// being retried as well — and because this reconcile is the only automatic re-arm of the
    /// god-mode guard after a restart, the player behind <c>waiting</c> would have connected into
    /// a world with no kill-path guard and nothing saying so.</para>
    ///
    /// <para>Fails with <c>ReconcileEveryGrantSettled</c> mutated to <c>gaveUp &gt; 0</c>, which is
    /// FBUFF18's semantics exactly: <c>waiting</c> then gets 3 passes instead of 10,000 and the
    /// module stops with a retryable grant outstanding.</para>
    /// </summary>
    [Fact]
    public void One_grants_give_up_does_not_stop_another_grants_retry()
    {
        var model = new ReconcileModel();
        var ids = new[] { "broken", "waiting" };

        for (int tick = 0; tick < 10_000; tick++)
            model.Tick(ids, id => id == "broken" ? DebugBuff.ReadBack.Unreadable : (DebugBuff.ReadBack?)null);

        // The broken grant is bounded and named, and it stops being touched at all.
        Assert.Equal(DebugBuff.MaxUnreadableReconcilePasses, model.MutatingPassesFor("broken"));
        Assert.Equal(new[] { "broken" }, model.GaveUpGrants);
        Assert.Equal(
            10_000 - DebugBuff.MaxUnreadableReconcilePasses,
            model.Skipped["broken"]);

        // The finding: the other grant's unbounded retry is untouched. It runs no mutating pass
        // (its character never streams in), and the pass keeps running for it forever.
        Assert.Equal(0, model.MutatingPassesFor("waiting"));
        Assert.Equal(10_000, model.PassesRun);
        Assert.False(model.EveryGrantSettled);
        Assert.False(model.Reconciled);

        // Nothing claims the module stopped, because it did not.
        Assert.Empty(model.AllStoppedLines);
        Assert.Single(model.GiveUpLines);
        Assert.Contains("broken", model.GiveUpLines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The same shape with the second grant on the <em>other</em> unbounded retry: an apply whose
    /// guard reads back as <c>No</c>, which runs a mutating pass every period on purpose because
    /// the apply failed and the next one may not (job FBUFF19, R47 finding 1).
    ///
    /// <para>Fails with <c>ReconcileEveryGrantSettled</c> mutated to <c>gaveUp &gt; 0</c>: the
    /// 5,000 becomes 3.</para>
    /// </summary>
    [Fact]
    public void A_give_up_does_not_stop_the_retry_of_an_apply_that_keeps_failing()
    {
        var model = new ReconcileModel();
        var ids = new[] { "broken", "failing" };

        for (int tick = 0; tick < 5_000; tick++)
            model.Tick(ids, id => id == "broken" ? DebugBuff.ReadBack.Unreadable : DebugBuff.ReadBack.No);

        Assert.Equal(DebugBuff.MaxUnreadableReconcilePasses, model.MutatingPassesFor("broken"));
        Assert.Equal(5_000, model.MutatingPassesFor("failing"));
        Assert.Equal(new[] { "broken" }, model.GaveUpGrants);
        Assert.False(model.EveryGrantSettled);
    }

    /// <summary>
    /// The module does stop, but only when there is genuinely nothing left for a pass to do:
    /// every grant is either resolved or has given up (job FBUFF19, R47 finding 1). The line that
    /// says so names the characters, because "the reconcile stopped" is a per-row fact now and an
    /// operator reading only that line still has to be able to tell which players are unknown.
    ///
    /// <para>Fails with <c>ReconcileEveryGrantSettled</c> mutated to <c>unresolved &lt;= 0</c> (it
    /// never fires, so the pass runs 500 times) and with it mutated to <c>gaveUp &gt; 0</c> (it
    /// fires while <c>resolves</c> is still outstanding, so the resolved grant never gets its
    /// pass).</para>
    /// </summary>
    [Fact]
    public void The_pass_stops_only_when_every_grant_is_resolved_or_gave_up()
    {
        var model = new ReconcileModel();
        var ids = new[] { "broken", "late" };

        // "late" streams in on tick 6 and resolves; "broken" throws throughout.
        int tick = 0;
        for (; tick < 500; tick++)
        {
            int now = tick;
            model.Tick(ids, id => id == "broken"
                ? DebugBuff.ReadBack.Unreadable
                : now < 6 ? (DebugBuff.ReadBack?)null : DebugBuff.ReadBack.Yes);
        }

        // "broken" gave up on pass 3; "late" resolved on pass 7; the pass stopped there.
        Assert.Equal(new[] { "broken" }, model.GaveUpGrants);
        Assert.Equal(1, model.MutatingPassesFor("late"));
        Assert.Equal(7, model.PassesRun);
        Assert.True(model.EveryGrantSettled);

        // Not latched: "broken" was never resolved, so nothing may claim the pass finished.
        Assert.False(model.Reconciled);

        var stopped = Assert.Single(model.AllStoppedLines);
        Assert.Contains("reconcile STOPPED entirely", stopped, StringComparison.Ordinal);
        Assert.Contains("broken", stopped, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", stopped, StringComparison.Ordinal);
        Assert.DoesNotContain("late", stopped, StringComparison.Ordinal);
    }

    /// <summary>
    /// FBUFF18's declared residual, closed by job FBUFF19 (R47 finding 1) with its own state and
    /// its own sentence: a pass that throws before finishing has already run mutating steps for
    /// the rows it got through, and repeating it every watchdog period forever is the same defect
    /// one level up.
    ///
    /// <para>The two give-ups are kept apart, and this test is where that is checked: the
    /// pass-throw line claims none of the read-back line's facts, because a throw from the store,
    /// the resolve or a buffer walk is not a guard read-back and the rows it touched are not
    /// recorded.</para>
    ///
    /// <para>Fails with the <c>finally</c>'s budget removed (the pass runs 1,000 times) and with
    /// <c>NextThrowingPasses</c> mutated to <c>priorPasses + 1</c> (the refill assertion below
    /// goes, because a pass that completes would still spend the budget).</para>
    /// </summary>
    [Fact]
    public void A_pass_that_keeps_throwing_stops_with_its_own_state_and_its_own_sentence()
    {
        var model = new ReconcileModel();
        var ids = new[] { "any" };

        for (int tick = 0; tick < 1_000; tick++)
            model.Tick(ids, _ => DebugBuff.ReadBack.Yes, passThrows: true);

        Assert.Equal(DebugBuff.MaxThrowingReconcilePasses, model.PassesRun);
        Assert.True(model.PassGaveUp);
        Assert.False(model.Reconciled);
        Assert.False(model.EveryGrantSettled);
        Assert.Empty(model.GaveUpGrants);

        // Its own sentence. It says the pass threw, says the mutating steps it had already run are
        // not recorded, and says UNKNOWN — and it claims none of the per-grant line's facts.
        string line = Assert.Single(model.PassGaveUpLines);
        Assert.Contains("reconcile STOPPED: the pass threw", line, StringComparison.Ordinal);
        Assert.Contains("not recorded", line, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", line, StringComparison.Ordinal);
        Assert.Contains("NOT a success", line, StringComparison.Ordinal);
        Assert.DoesNotContain("character=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("read-back threw", line, StringComparison.Ordinal);

        // A completing pass refills it, so a transient throw costs nothing.
        var refilled = new ReconcileModel();
        for (int tick = 0; tick < 1_000; tick++)
            refilled.Tick(ids, _ => (DebugBuff.ReadBack?)null, passThrows: tick % 2 == 0);

        Assert.False(refilled.PassGaveUp);
        Assert.Equal(1_000, refilled.PassesRun);

        // The bound itself, from both sides.
        Assert.False(DebugBuff.ReconcilePassGaveUp(DebugBuff.MaxThrowingReconcilePasses - 1));
        Assert.True(DebugBuff.ReconcilePassGaveUp(DebugBuff.MaxThrowingReconcilePasses));
        Assert.Equal(0, DebugBuff.NextThrowingPasses(9, passThrew: false));
        Assert.Equal(10, DebugBuff.NextThrowingPasses(9, passThrew: true));
    }

    /// <summary>
    /// The budget is for a read-back that is <em>persistently</em> broken <b>for one grant</b>.
    /// One readable pass refills it, so a hiccup while the world is still coming up does not spend
    /// the retry that evidence 166 needs — and, since FBUFF19, a pass in which that grant's
    /// character was not resolvable at all refills it too, because the counter is a count of
    /// passes on which <em>this grant</em> produced an unreadable row.
    ///
    /// <para>Fails with <c>NextUnreadablePasses</c> reverted to <c>priorPasses + 1</c>: the
    /// refill assertions go. The per-grant half fails with the model's <c>Advance</c> skipped on
    /// the not-streamed-in branch — which is why the production loop pins that call.</para>
    /// </summary>
    [Fact]
    public void A_single_readable_pass_refills_the_retry_budget()
    {
        Assert.Equal(1, DebugBuff.NextUnreadablePasses(0, unreadableRows: 1));
        Assert.Equal(2, DebugBuff.NextUnreadablePasses(1, unreadableRows: 4));
        Assert.Equal(0, DebugBuff.NextUnreadablePasses(2, unreadableRows: 0));
        Assert.Equal(0, DebugBuff.NextUnreadablePasses(99, unreadableRows: 0));

        // Alternating throw / no-throw never reaches the bound, however long it runs.
        int passes = 0;
        for (int tick = 0; tick < 1_000; tick++)
        {
            passes = DebugBuff.NextUnreadablePasses(passes, unreadableRows: tick % 2);
            Assert.False(DebugBuff.ReconcileReadBackGaveUp(passes));
        }

        // The bound itself: below it the retry runs, at it and past it the retry stops.
        Assert.False(DebugBuff.ReconcileReadBackGaveUp(DebugBuff.MaxUnreadableReconcilePasses - 1));
        Assert.True(DebugBuff.ReconcileReadBackGaveUp(DebugBuff.MaxUnreadableReconcilePasses));
        Assert.True(DebugBuff.ReconcileReadBackGaveUp(DebugBuff.MaxUnreadableReconcilePasses + 1));

        // FBUFF19: the refill is per grant, over the loop that spends it. Two throwing passes,
        // then a pass on which this grant's character is not resolvable, then two more throwing
        // passes: the grant has still never reached the bound and no mutating pass was spared.
        var model = new ReconcileModel();
        var ids = new[] { "flaky" };
        for (int tick = 0; tick < 500; tick++)
            model.Tick(ids, _ => tick % 3 == 2 ? (DebugBuff.ReadBack?)null : DebugBuff.ReadBack.Unreadable);

        Assert.Empty(model.GaveUpGrants);
        Assert.False(model.EveryGrantSettled);
        Assert.Equal(500, model.PassesRun);
    }

    /// <summary>
    /// The asymmetry that makes this a semantic change rather than a retry cap. Evidence 166's
    /// retry is unbounded on purpose — at WorldReady on a server boot nobody is connected and
    /// every granted character is unresolvable — and it stays unbounded for both cases where the
    /// next pass can genuinely decide differently: a grant that resolved to no live character
    /// (no mutating pass runs for it at all) and an apply whose guard read back as
    /// <c>No</c>.
    ///
    /// <para>Fails if the bound is applied to the answering failures too: the loop then stops
    /// after three passes and the two <c>Assert.Equal(5_000, ...)</c> go.</para>
    /// </summary>
    [Fact]
    public void An_answering_read_back_keeps_retrying_without_a_bound()
    {
        foreach (var readBack in new[] { DebugBuff.ReadBack.No, DebugBuff.ReadBack.Yes })
        {
            // Keep + No is a failed apply; Remove + Yes is a failed removal. Both answered.
            var plan = readBack == DebugBuff.ReadBack.No
                ? DebugBuff.ImmortalPlan.Keep
                : DebugBuff.ImmortalPlan.Remove;

            // The tally is the thing the budget is keyed off, so state it before driving the loop.
            Assert.Equal(
                DebugBuff.ReconcileTally.Unresolved,
                DebugBuff.TallyReconcileRow(plan, readBack, dropRow: false));

            var model = new ReconcileModel { Plan = plan };
            var ids = new[] { "answering" };
            for (int tick = 0; tick < 5_000; tick++)
                model.Tick(ids, _ => readBack);

            Assert.Equal(5_000, model.MutatingPassesFor("answering"));
            Assert.Empty(model.GaveUpGrants);
            Assert.False(model.EveryGrantSettled);
            Assert.False(model.PassGaveUp);
        }
    }

    /// <summary>
    /// The standing rule applied to the new state: <b>"we stopped retrying" must not be reported
    /// as "it is fine"</b>. The give-up line says the retry stopped, says why, and says that what
    /// is on those characters is unknown — it borrows none of the summary's success vocabulary,
    /// and giving up closes no latch.
    ///
    /// <para>Fails if the give-up line is worded as a completion, or if giving up is folded into
    /// <c>ReconcileMayLatch</c>.</para>
    /// </summary>
    [Fact]
    public void Giving_up_is_reported_as_unknown_and_never_as_success()
    {
        string gaveUp = DebugBuff.FormatReconcileGaveUp("Nihil", passes: 3);

        Assert.Contains("reconcile STOPPED for character=Nihil", gaveUp, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", gaveUp, StringComparison.Ordinal);
        Assert.Contains("NOT a success", gaveUp, StringComparison.Ordinal);
        Assert.Contains("3", gaveUp, StringComparison.Ordinal);

        // FBUFF19: it names the character, because the give-up is a fact about one row now, and
        // it says out loud that the other grants are unaffected — which is the property the fix
        // establishes and the reason the line may say it.
        Assert.Contains("Other grants are unaffected", gaveUp, StringComparison.Ordinal);

        // It must not read as the summary's happy path, and it must not claim anything was done.
        Assert.DoesNotContain("adopted", gaveUp, StringComparison.Ordinal);
        Assert.DoesNotContain("all five protections", gaveUp, StringComparison.Ordinal);
        Assert.DoesNotContain("reconcile complete", gaveUp, StringComparison.Ordinal);

        // It names a way forward rather than leaving the operator with a dead end.
        Assert.Contains("swap Core", gaveUp, StringComparison.Ordinal);
        Assert.Contains(".debug on", gaveUp, StringComparison.Ordinal);

        // The skip line the following passes write is the same claim, and no stronger.
        string skipped = DebugBuff.FormatReconcileGrantSkipped("Nihil");
        Assert.Contains("reconcile skipping character=Nihil", skipped, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", skipped, StringComparison.Ordinal);

        // The module-level line is a different fact and states its own evidence. It names the
        // characters and it never claims the ones it did not name are fine.
        string all = DebugBuff.FormatReconcileAllStopped(new[] { "Nihil", "Vera" });
        Assert.Contains("reconcile STOPPED entirely", all, StringComparison.Ordinal);
        Assert.Contains("2 character(s): Nihil, Vera", all, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", all, StringComparison.Ordinal);
        Assert.Contains("NOT a success", all, StringComparison.Ordinal);
        Assert.DoesNotContain("all five protections", all, StringComparison.Ordinal);

        // An empty or absent set renders as (none), never as an empty gap that reads like a set.
        Assert.Equal("(none)", DebugBuff.JoinCharacterIds(null));
        Assert.Equal("(none)", DebugBuff.JoinCharacterIds(new string[0]));
        Assert.Contains("0 character(s): (none)", DebugBuff.FormatReconcileAllStopped(null),
            StringComparison.Ordinal);

        // Giving up is not latching. An unresolved count is unresolved whatever the retry did.
        Assert.False(DebugBuff.ReconcileMayLatch(1));
        Assert.True(DebugBuff.ReconcileReadBackGaveUp(DebugBuff.MaxUnreadableReconcilePasses));

        // And the module stop is not the per-grant stop: one grant giving up while another is
        // still outstanding settles nothing, which is R47 finding 1 in one assertion.
        Assert.False(DebugBuff.ReconcileEveryGrantSettled(unresolved: 2, gaveUp: 1));
        Assert.True(DebugBuff.ReconcileEveryGrantSettled(unresolved: 1, gaveUp: 1));
        Assert.False(DebugBuff.ReconcileEveryGrantSettled(unresolved: 0, gaveUp: 0));
        Assert.False(DebugBuff.ReconcileMayRun(false, everyGrantSettled: true, passGaveUp: false));
        Assert.False(DebugBuff.ReconcileMayRun(false, everyGrantSettled: false, passGaveUp: true));
        Assert.True(DebugBuff.ReconcileMayRun(false, everyGrantSettled: false, passGaveUp: false));
    }

    /// <summary>
    /// The other half of the reporting rule: <c>.nodrain status</c> says it too, so an operator
    /// who is not reading the server log still learns that the background pass stopped without
    /// knowing. It is stated beside the verdict and not folded into it — the all-clear is derived
    /// from this character's own fresh reads, which a give-up elsewhere neither confirms nor
    /// denies.
    ///
    /// <para>Fails with the <c>ReconcileReadBackGaveUp</c> block removed from
    /// <c>FormatStatusLines</c>: the STOPPED line is then absent at the bound.</para>
    /// </summary>
    [Fact]
    public void The_status_readout_reports_a_reconcile_that_gave_up()
    {
        System.Collections.Generic.IReadOnlyList<string> At(
            System.Collections.Generic.IReadOnlyList<string>? gaveUp, int throwingPasses) =>
            DebugBuff.FormatStatusLines(
            buffPresent: true,
            buffLabel: "328855:1",
            flagsReadable: true,
            flagsPresent: true,
            flags: DebugBuff.ModificationFlags,
            immortalReadable: true,
            immortal: true,
            guardReadable: true,
            guarded: true,
            killPathPatches: 3,
            requiredPatches: 3,
            noDrain: true,
            noAggro: true,
            persists: true,
            reconcileGaveUpCharacters: gaveUp,
            reconcileThrowingPasses: throwingPasses);

        // Nothing has given up: nothing to report to a player, so the readout is untouched. The
        // second case is the one FBUFF19 changed — a grant partway through its budget is still
        // being retried, and a count below the bound is not a give-up.
        foreach (int throwing in new[] { 0, DebugBuff.MaxThrowingReconcilePasses - 1 })
            Assert.DoesNotContain(At(null, throwing), l => l.Contains("background reconcile"));
        Assert.DoesNotContain(At(new string[0], 0), l => l.Contains("background reconcile"));

        var stopped = At(new[] { "Nihil", "Vera" }, 0);
        Assert.Contains(stopped, l => l.Contains("background reconcile: STOPPED for 2 grant(s)"));
        // FBUFF19: which characters, because the give-up is per grant now and a player reading
        // this has to be able to tell whether it is about them.
        Assert.Contains(stopped, l => l.Contains("Nihil, Vera"));
        Assert.Contains(stopped, l => l.Contains("UNKNOWN"));
        Assert.Contains(stopped, l => l.Contains("not fine"));

        // The verdict over this character's own reads is unchanged: those reads all succeeded a
        // few lines above and a give-up elsewhere is not evidence against them.
        Assert.Contains(stopped, l => l.Contains("all five protections are live"));
        Assert.DoesNotContain(stopped, l => l.Contains("SOMETHING ABOVE IS OFF"));

        // ... and it does not displace the verdict: STOPPED comes after it, as a separate fact.
        int verdict = IndexOfLine(stopped, "all five protections are live");
        int note = IndexOfLine(stopped, "background reconcile: STOPPED");
        Assert.True(note > verdict, "the reconcile note is stated after the verdict, not instead of it");

        // The pass-throw give-up is its own line and never merged with the one above: it names no
        // character, because the code did not establish one.
        var threw = At(null, DebugBuff.MaxThrowingReconcilePasses);
        string passLine = Assert.Single(threw, l => l.Contains("background reconcile: STOPPED"));
        Assert.Contains("the pass threw before finishing", passLine, StringComparison.Ordinal);
        Assert.Contains("not recorded", passLine, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", passLine, StringComparison.Ordinal);
        Assert.DoesNotContain("grant(s) whose god-mode guard", passLine, StringComparison.Ordinal);
        Assert.Contains(threw, l => l.Contains("all five protections are live"));

        // Both at once: two lines, two facts, both after the verdict.
        var both = At(new[] { "Nihil" }, DebugBuff.MaxThrowingReconcilePasses);
        Assert.Equal(2, both.Count(l => l.Contains("background reconcile: STOPPED")));
        Assert.True(
            IndexOfLine(both, "background reconcile: STOPPED for 1 grant(s)")
                > IndexOfLine(both, "all five protections are live"),
            "both notes are stated after the verdict");
    }

    private static int IndexOfLine(System.Collections.Generic.IReadOnlyList<string> lines, string needle)
    {
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// Source pin for the two things the game-free tests above cannot reach: that the production
    /// watchdog composes those functions the way
    /// <see cref="A_read_back_that_always_throws_does_not_retry_the_mutating_pass_forever"/>
    /// drives them, and that every reconcile line which repeats at watchdog cadence goes through
    /// the one de-duplicated report rather than straight to the log.
    ///
    /// <para>R46 finding 1's second half: FBUFF17 added two per-row lines and a new way to stay
    /// unresolved into the loop whose summary line had already been de-duplicated for exactly
    /// this reason. The pin is that the pass's lines are <c>report.Add</c>, and that the only
    /// thing which writes them is <c>LogReconcileReport</c>, from a <c>finally</c> so the early
    /// returns and the outer catch are covered too.</para>
    ///
    /// <para>Every pin here is <see cref="SourcePin.AssertStatementExactly"/>: a substring search
    /// is satisfied by the pinned line commented out (job FUI26) and by the pinned call wrapped
    /// in a ternary that never takes it (job FDOC5).</para>
    /// </summary>
    [Fact]
    public void The_reconcile_loop_is_the_composition_this_test_drives()
    {
        var src = System.IO.File.ReadAllText(FindDebugBuff()).Replace("\r\n", "\n");

        // The watchdog gate: every stop condition, through the one derivation. FBUFF19 (R47
        // finding 1) is the second argument — "every grant is settled", not "some grant gave up".
        string dogBody = SourcePin.MethodBody(
            src, "private static void ImmortalWatchdog(Progenitor.Systems.VirtualSystemContext vs)");
        SourcePin.AssertStatementExactly(
            dogBody,
            "if (ReconcileMayRun(_reconciled, _everyGrantSettled, _passGaveUp)) ReconcileImmortalOnWorldReady();");
        // The defect: a periodic caller gated on the latch alone.
        Assert.DoesNotContain("if (!_reconciled) ReconcileImmortalOnWorldReady();", dogBody, StringComparison.Ordinal);
        // R47 finding 1's defect: gated on one grant's give-up, which stops the retry the rest of
        // the ledger is waiting for. Named here so the exact reverted form fails rather than only
        // the exact-line pin above.
        Assert.DoesNotContain("ReconcileMayRun(_reconciled, _readBackGaveUp)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_readBackGaveUp", src, StringComparison.Ordinal);

        string recBody = SourcePin.MethodBody(src, "private static void ReconcileImmortalOnWorldReady()");

        // FBUFF19: the budget is spent per GRANT, keyed by the ledger's key, through the one
        // helper — the row loop's every branch reaches it and the branch that runs no mutating
        // pass refills it, which is the case a per-pass counter could not represent.
        SourcePin.AssertStatementExactly(
            recBody, "AdvanceGrantReadBackBudget(id, rowUnreadable: false, report);");
        SourcePin.AssertStatementExactly(
            recBody, "if (AdvanceGrantReadBackBudget(id, tally == ReconcileTally.Unreadable, report))");
        // ... and the defect it replaces: one counter for the whole pass.
        Assert.DoesNotContain("_unreadablePasses", src, StringComparison.Ordinal);

        string budgetBody = SourcePin.MethodBody(
            src,
            "private static bool AdvanceGrantReadBackBudget(string id, bool rowUnreadable, List<string> report)");
        SourcePin.AssertStatementExactly(
            budgetBody, "int next = NextUnreadablePasses(prior, rowUnreadable ? 1 : 0);");
        SourcePin.AssertStatementExactly(
            budgetBody, "if (!ReconcileReadBackGaveUp(next)) return false;");
        SourcePin.AssertStatementExactly(budgetBody, "GaveUpGrants.Add(id);");
        SourcePin.AssertStatementExactly(
            budgetBody, "report.Add(FormatReconcileGaveUp(id, next));");
        // The refill has to remove the entry, or the map grows one row per grant for ever.
        SourcePin.AssertStatementExactly(budgetBody, "if (next == 0) UnreadablePassesByGrant.Remove(id);");

        // A grant that gave up is skipped BEFORE any mutating step, and only that grant.
        SourcePin.AssertStatementExactly(recBody, "if (GaveUpGrants.Contains(id))");
        SourcePin.AssertStatementExactly(recBody, "report.Add(FormatReconcileGrantSkipped(id));");

        // The module stop: every grant settled, never "some grant gave up".
        SourcePin.AssertStatementExactly(
            recBody, "if (ReconcileEveryGrantSettled(unresolved, gaveUp) && !_everyGrantSettled)");
        SourcePin.AssertStatementExactly(recBody, "_everyGrantSettled = true;");
        SourcePin.AssertStatementExactly(
            recBody, "report.Add(FormatReconcileAllStopped(GaveUpGrants));");
        // Giving up must not latch: that is the difference between "stopped" and "fine".
        Assert.DoesNotContain("_reconciled = true;\n                report.Add(FormatReconcileAllStopped", recBody, StringComparison.Ordinal);

        // FBUFF18's residual, closed by FBUFF19 with its own state, bound and sentence, in the
        // finally so the throw that sets it is the only thing that can.
        SourcePin.AssertStatementExactly(recBody, "passThrew = true;");
        SourcePin.AssertStatementExactly(
            recBody, "_throwingPasses = NextThrowingPasses(_throwingPasses, passThrew);");
        SourcePin.AssertStatementExactly(
            recBody, "if (ReconcilePassGaveUp(_throwingPasses) && !_passGaveUp)");
        SourcePin.AssertStatementExactly(recBody, "_passGaveUp = true;");
        SourcePin.AssertStatementExactly(
            recBody, "report.Add(FormatReconcilePassGaveUp(_throwingPasses));");

        // Retry state is keyed by the ledger's key, so it is trimmed to the ledger every pass.
        SourcePin.AssertStatementExactly(recBody, "PruneReconcileRetryState(ids);");

        // Log volume. Every line the pass writes is composed into the report, and the report is
        // the only thing that logs — from a finally, so the two early returns and the outer catch
        // go through it as well.
        SourcePin.AssertStatementExactly(recBody, "LogReconcileReport(report);");
        Assert.True(
            recBody.IndexOf("finally\n", StringComparison.Ordinal)
                < recBody.IndexOf("LogReconcileReport(report);", StringComparison.Ordinal),
            "the flush must run from the finally, so the early returns and the catch are covered");

        // No line inside the pass may go straight to the log: that is the defect R46 found, where
        // three unlatched lines per grant per watchdog period reached a live server's log forever.
        foreach (string line in recBody.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("Log(", trimmed, StringComparison.Ordinal);
        }

        // The four repeating lines the finding names, each of them now a report line.
        foreach (string pinned in new[]
                 {
                     "report.Add(ReconcileNoWorldLog);",
                     "report.Add(FormatReconcileUnverified(id, plan, guardAfter));",
                     "report.Add(\"world-ready reconcile threw: \" + e.Message);",
                 })
        {
            SourcePin.AssertStatementExactly(recBody, pinned);
        }
        SourcePin.AssertStatement(
            recBody, "report.Add(FormatImmortalDecision(id, ImmortalPlan.Keep) + \" (unresolved; grant kept)\");");
        SourcePin.AssertStatement(recBody, "report.Add(FormatImmortalDecision(id, plan)");

        // The guard read-back's own throw line joins the report rather than logging per pass, and
        // it does so through the shared helper's sink rather than a second copy of the read.
        string readBody = SourcePin.MethodBody(
            src, "private static ReadBack ReadGuardBack(Entity character, List<string>? sink = null)");
        SourcePin.AssertStatementExactly(readBody, "if (sink != null) sink.Add(line);");
        SourcePin.AssertStatementExactly(readBody, "else Log(line);");

        // The de-duplication is the summary's own comparison, widened — one mechanism, not two.
        string flushBody = SourcePin.MethodBody(src, "private static void LogReconcileReport(List<string> lines)");
        SourcePin.AssertStatementExactly(
            flushBody,
            "if (string.Equals(composed, _lastReconcileReport, StringComparison.Ordinal)) return;");
        Assert.DoesNotContain("_lastReconcileSummary", src, StringComparison.Ordinal);

        // A new generation and a new world both start the budget over: a Core swap or a redeploy
        // is exactly the kind of change that can fix a read-back which was throwing. FBUFF19 puts
        // both through ONE clear, so a state added later cannot be reset on one path and forgotten
        // on the other — which is a smaller version of the defect this job is fixing.
        string resetBody = SourcePin.MethodBody(src, "internal static void Reset(PluginUnloadReason reason)");
        SourcePin.AssertStatementExactly(resetBody, "ResetReconcileRetryState();");
        string worldReadyBody = SourcePin.MethodBody(
            src, "private static void OnWorldReadyReconcile(Progenitor.Events.WorldReady ev)");
        SourcePin.AssertStatementExactly(worldReadyBody, "ResetReconcileRetryState();");

        // ... and that clear covers every field the pass carries between passes. A field added to
        // the block above and not to the clear is what this enumerates.
        string clearBody = SourcePin.MethodBody(src, "private static void ResetReconcileRetryState()");
        foreach (string pinned in new[]
                 {
                     "UnreadablePassesByGrant.Clear();",
                     "GaveUpGrants.Clear();",
                     "_everyGrantSettled = false;",
                     "_throwingPasses = 0;",
                     "_passGaveUp = false;",
                 })
        {
            SourcePin.AssertStatementExactly(clearBody, pinned);
        }

        // And the status readout reads the live state rather than a constant, in both halves.
        string statusBody = SourcePin.MethodBody(src, "private static void Status(Target t)");
        SourcePin.AssertStatementExactly(
            statusBody, "ReconcileGaveUpCharacters, ReconcileThrowingPasses);");
    }

    private static string ReadSampleFile(string fileName)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "samples", "Progenitor.Sample", fileName);
            if (System.IO.File.Exists(candidate))
                return System.IO.File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException("samples/Progenitor.Sample/" + fileName);
    }
}
