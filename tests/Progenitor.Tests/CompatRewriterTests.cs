using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Progenitor.Compat;
using Progenitor.DriftMapTool;
using Xunit;
using Boot = Progenitor.Bootstrap;

namespace Progenitor.Tests;

/// <summary>
/// M4 item 2, the compatibility rewriter (job FCOMPAT2).
///
/// The spike plan's S1-S3 run here, offline, against <b>synthetic</b> assemblies this fixture
/// builds with Cecil — no V Rising install, no interop copy, no game process. That is stronger
/// than the research note's S1 in one way that matters: because the "game" assemblies are ours,
/// the rewritten consumer can actually be <b>loaded and invoked</b> in a collectible ALC, so S3's
/// "the reference binds" is asserted by running the code rather than by reading tokens.
///
/// The synthetic drift is exactly the design's claim set:
/// <list type="bullet">
/// <item><c>ProjectM.Alpha.Ping(int)</c> renamed to <c>Pong</c> — a memberRename;</item>
/// <item><c>ProjectM.Mover</c> moved from <c>ProjectM</c> to <c>ProjectM.Shared</c> — a typeMove;</item>
/// <item><c>ProjectM.Flip.Value</c>, a field, becomes a property — a fieldToProperty;</item>
/// <item><c>ProjectM.Layout</c>'s <c>[FieldOffset]</c> moves from 4 to 8, <b>the negative
/// control</b>: §1.2 says interop regeneration already absorbs a layout change and the plugin's
/// IL holds no number for it, so a generator that emits a rule for this is wrong.</item>
/// </list>
/// </summary>
public class CompatRewriterTests
{
    private const string Side = "client";
    private const string Build = "248100992-6a147771-ec12531-66fe4b61f7a0d186";

    // ---- S1: the generator, and the negative control -------------------------------------

    [Fact]
    public void S1_the_generator_emits_exactly_the_three_rewritable_drifts()
    {
        using var world = new SyntheticWorld();
        var diff = world.Diff();

        Assert.Equal(3, diff.Rules.Count);

        var rename = Single(diff.Rules, DriftKind.MemberRename);
        Assert.Equal(DriftConfidence.High, rename.Confidence);
        Assert.Equal(DriftRule.Key("ProjectM", "ProjectM.Alpha", "Ping", "System.Void(System.Int32)"), rename.From);
        Assert.Equal(DriftRule.Key("ProjectM", "ProjectM.Alpha", "Pong", "System.Void(System.Int32)"), rename.To);

        var move = Single(diff.Rules, DriftKind.TypeMove);
        Assert.Equal(DriftConfidence.Exact, move.Confidence);
        Assert.Equal(DriftRule.TypeKey("ProjectM", "ProjectM.Mover"), move.From);
        Assert.Equal(DriftRule.TypeKey("ProjectM.Shared", "ProjectM.Mover"), move.To);

        var flip = Single(diff.Rules, DriftKind.FieldToProperty);
        Assert.Equal(DriftConfidence.High, flip.Confidence);
        Assert.Equal(DriftRule.Key("ProjectM", "ProjectM.Flip", "Value", "System.Int32"), flip.From);

        // The negative control: nothing at all for the type whose [FieldOffset] moved.
        Assert.DoesNotContain(diff.Rules, r => r.From.Contains("ProjectM.Layout", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Rules, r => r.To.Contains("ProjectM.Layout", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Notes, n => n.Contains("Layout", StringComparison.Ordinal));

        // And it is not a rename in disguise: the property's backing field must not have been
        // matched one-to-one against the field it replaced.
        Assert.DoesNotContain(diff.Rules, r => r.ToName.Contains("k__BackingField", StringComparison.Ordinal));
    }

    [Fact]
    public void S1_the_map_round_trips_through_json_with_the_hand_block_last()
    {
        using var world = new SyntheticWorld();
        var rules = new List<DriftRule>(world.Diff().Rules)
        {
            new DriftRule(
                DriftRule.Key("ProjectM", "ProjectM.Alpha", "Ping", "System.Void(System.Int32)"),
                DriftRule.Key("ProjectM", "ProjectM.Alpha", "HandWins", "System.Void(System.Int32)"),
                DriftKind.MemberRename, DriftConfidence.High, DriftSource.Hand, "operator decision"),
        };
        var map = DriftMap.Create(Side, Build, rules);
        var reread = DriftMap.Parse(map.ToJson(), out var error, out var refusals);

        Assert.Equal("", error);
        Assert.Empty(refusals);
        Assert.NotNull(reread);
        Assert.Equal(Side, reread!.Side);
        Assert.Equal(Build, reread.Build);
        Assert.Equal(1, reread.HandCount);
        // A hand rule wins the key collision with the generated rule it shadows.
        var winner = reread.Find(DriftRule.Key("ProjectM", "ProjectM.Alpha", "Ping", "System.Void(System.Int32)"));
        Assert.NotNull(winner);
        Assert.Equal(DriftSource.Hand, winner!.Source);
        Assert.Equal("HandWins", winner.ToName);
    }

    [Fact]
    public void A_rule_that_redirects_outside_the_game_surface_is_refused_at_parse_time()
    {
        // §4.7: the remit is game-surface drift. A map is an arbitrary-call-redirection
        // primitive, so a rule pointing into System.* or into a plugin is refused, not applied.
        // Written as raw JSON on purpose: DriftMap.Create applies the same refusal, so a map
        // built through the API could never carry this rule to begin with. This is the
        // hand-edited-file case, which is the one that matters.
        string json =
            "{\"side\":\"" + Side + "\",\"build\":\"" + Build + "\",\"generated\":[],\"hand\":[{" +
            "\"from\":\"ProjectM|ProjectM.Alpha|Ping|System.Void(System.Int32)\"," +
            "\"to\":\"Evil.Plugin|Evil.Backdoor|Run|System.Void(System.Int32)\"," +
            "\"kind\":\"memberRename\",\"confidence\":\"high\",\"source\":\"hand\"}]}";
        var map = DriftMap.Parse(json, out _, out var refusals);
        Assert.NotNull(map);
        Assert.Empty(map!.Rules);
        Assert.Contains(refusals, r => r.Contains("non-game assembly", StringComparison.Ordinal));
    }

    // ---- S2 / S3: rewrite the consumer, then load and run it ------------------------------

    [Fact]
    public void S2_S3_the_rewritten_consumer_binds_and_runs_against_the_new_assemblies()
    {
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, world.Diff().Rules);
        byte[] original = File.ReadAllBytes(world.ConsumerPath);

        // Before: the consumer names members the new assemblies do not have.
        Assert.Throws<MissingMethodException>(() => world.RunConsumer(original));

        byte[] rewritten = PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Apply, out var report,
            world.NewAssemblyNames);

        Assert.Equal("", report.Failure);
        Assert.True(report.Applied);
        Assert.NotSame(original, rewritten);
        Assert.Equal(3, report.Hits.Count);
        Assert.Equal(PluginRewriter.Sha256Hex(original), report.OriginalSha);
        Assert.Equal(PluginRewriter.Sha256Hex(rewritten), report.LoadedSha);
        Assert.NotEqual(report.OriginalSha, report.LoadedSha);

        // S3: load the rewritten image in a collectible ALC and CALL it. If any of the three
        // retargets were wrong this throws instead of returning the expected value.
        Assert.Equal(1 + 7 + 41, world.RunConsumer(rewritten));
    }

    [Fact]
    public void S3_the_rewrite_does_not_pin_the_collectible_alc()
    {
        // §4.2: Cecil lives in the non-collectible default ALC, so a cached ModuleDefinition or
        // a resolver holding the rewritten module would pin the plugin's LoaderAllocator - the
        // Harmony pin with different nouns. Only a byte[] leaves Rewrite; this asserts it.
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, world.Diff().Rules);
        byte[] rewritten = PluginRewriter.Rewrite("Consumer.dll", File.ReadAllBytes(world.ConsumerPath),
            map, RewriteMode.Apply, out _, world.NewAssemblyNames);

        var weak = world.LoadAndForget(rewritten);
        for (int i = 0; i < 15 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "the plugin ALC did not collect after a rewrite");
    }

    [Fact]
    public void A_dry_run_reports_everything_and_returns_the_original_array()
    {
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, world.Diff().Rules);
        byte[] original = File.ReadAllBytes(world.ConsumerPath);

        byte[] loaded = PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.DryRun, out var report,
            world.NewAssemblyNames);

        Assert.Same(original, loaded);          // the same array, not an equivalent re-serialization
        Assert.False(report.Applied);
        Assert.Equal(3, report.Hits.Count);     // the full analysis still ran
        Assert.Equal(report.OriginalSha, report.LoadedSha);
        var lines = report.Lines();
        Assert.Contains(lines, l => l.Contains("would apply", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("(original; dry run)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("rewritten in memory only", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_computes_nothing_and_a_missing_map_is_a_byte_for_byte_no_op()
    {
        using var world = new SyntheticWorld();
        byte[] original = File.ReadAllBytes(world.ConsumerPath);
        var map = DriftMap.Create(Side, Build, world.Diff().Rules);

        Assert.Same(original, PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Off, out var off,
            world.NewAssemblyNames));
        Assert.Empty(off.Hits);
        Assert.Same(original, PluginRewriter.Rewrite("Consumer.dll", original, null, RewriteMode.Apply, out var none));
        Assert.Empty(none.Hits);
        Assert.Equal(PluginRewriter.Sha256Hex(original), none.OriginalSha);
    }

    [Fact]
    public void A_cecil_failure_returns_the_original_bytes_with_one_line()
    {
        // §2.6: every failure is "load the original". A truncated image is the cheapest way to
        // make Cecil throw from inside the pass.
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, world.Diff().Rules);
        byte[] junk = File.ReadAllBytes(world.ConsumerPath).Take(512).ToArray();

        byte[] loaded = PluginRewriter.Rewrite("Broken.dll", junk, map, RewriteMode.Apply, out var report,
            world.NewAssemblyNames);

        Assert.Same(junk, loaded);
        Assert.False(report.Applied);
        Assert.NotEqual("", report.Failure);
        Assert.Contains(report.Lines(), l => l.Contains("loaded the original bytes", StringComparison.Ordinal));
    }

    // ---- the store, the mode and the pipeline seam ----------------------------------------

    [Fact]
    public void A_map_labelled_for_another_build_is_refused()
    {
        // S4's second half: the map name matches but the fields inside do not (§4.5).
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, DriftMapStore.FileName(Build)),
                DriftMap.Create("server", "some-other-build", Array.Empty<DriftRule>()).ToJson());

            var map = DriftMapStore.TryLoad(dir, Side, Build, out var status);

            Assert.Null(map);
            Assert.Contains("refused", status, StringComparison.Ordinal);
            Assert.Contains("labeled server-some-other-build", status, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void No_map_for_this_build_is_not_an_error_and_bepinex_plugins_is_never_read()
    {
        string dir = NewTempDir();
        try
        {
            Assert.Null(DriftMapStore.TryLoad(dir, Side, Build, out var absent));
            Assert.Contains("no map for build", absent, StringComparison.Ordinal);

            string plugins = Path.Combine(dir, "plugins");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, DriftMapStore.FileName(Build)),
                DriftMap.Create(Side, Build, Array.Empty<DriftRule>()).ToJson());
            Assert.Null(DriftMapStore.TryLoad(plugins, Side, Build, out var refused));
            Assert.Contains("never read from BepInEx/plugins", refused, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    [Fact]
    public void The_mode_defaults_to_dry_run_and_parses_the_three_values()
    {
        Assert.Equal(RewriteMode.DryRun, CompatRewritePolicy.DefaultMode);
        Assert.Equal(RewriteMode.Off, CompatRewritePolicy.Parse("Off"));
        Assert.Equal(RewriteMode.DryRun, CompatRewritePolicy.Parse("dryrun"));
        Assert.Equal(RewriteMode.Apply, CompatRewritePolicy.Parse(" apply "));
        Assert.Equal(RewriteMode.DryRun, CompatRewritePolicy.Parse("nonsense"));
        Assert.Equal(RewriteMode.DryRun, CompatRewritePolicy.Parse(null));
    }

    [Fact]
    public void The_mode_is_read_out_of_the_bepinex_config_file()
    {
        string root = NewTempDir();
        try
        {
            Assert.Equal(RewriteMode.DryRun, CompatRewriteHost.ReadModeFromConfig(root));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", CompatRewriteHost.ConfigFileName),
                "[Reload]\nWatchCore = true\n\n[Compat]\n## comment\nRewriteMode = Apply\n");
            Assert.Equal(RewriteMode.Apply, CompatRewriteHost.ReadModeFromConfig(root));

            File.WriteAllText(Path.Combine(root, "config", CompatRewriteHost.ConfigFileName),
                "[Other]\nRewriteMode = Apply\n");
            // The key only counts inside its own section.
            Assert.Equal(RewriteMode.DryRun, CompatRewriteHost.ReadModeFromConfig(root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void The_load_path_hunk_reports_once_and_hands_the_bytes_through()
    {
        using var world = new SyntheticWorld();
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, DriftMapStore.FileName(Build)),
                DriftMap.Create(Side, Build, world.Diff().Rules).ToJson());
            byte[] original = File.ReadAllBytes(world.ConsumerPath);
            var log = new List<string>();

            ResetHost();
            CompatRewriteHost.AllowTokenReader = (_, _) => CompatRewritePolicy.AllowTokenListed;
            CompatRewriteHost.InteropAssemblies = world.NewAssemblyNames;
            byte[] first = CompatRewriteHost.RewriteForLoad(dir, Side, Build, "Consumer.dll",
                original, RewriteMode.Apply, log.Add);
            byte[] second = CompatRewriteHost.RewriteForLoad(dir, Side, Build, "Consumer.dll",
                original, RewriteMode.Apply, log.Add);

            Assert.NotSame(original, first);
            Assert.Equal(first, second);
            // The map line appears once, not once per plugin load.
            Assert.Equal(1, log.Count(l => l.StartsWith("Rewrite: " + DriftMapStore.FileName(Build), StringComparison.Ordinal)));
            Assert.Contains(log, l => l.Contains("applied", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("rewritten in memory only", StringComparison.Ordinal));
        }
        finally
        {
            ResetHost();
            Delete(dir);
        }
    }

    [Fact]
    public void The_build_id_matches_the_address_library_id_for_the_same_bytes()
    {
        // The drift map is named after the same build id the address tables are, so a Bootstrap
        // copy that cannot see AddressId must still produce that exact string.
        var prefix = new byte[4096];
        new Random(20260904).NextBytes(prefix);
        prefix[0] = (byte)'M'; prefix[1] = (byte)'Z';

        Assert.Equal(
            Progenitor.Native.AddressId.ComputeBuildIdFromPrefix(prefix, 4096),
            CompatRewriteHost.ComputeBuildIdFromPrefix(prefix, 4096));
        Assert.Equal(
            Progenitor.Native.AddressId.ComputeBuildIdFromPrefix(prefix, 4096),
            Boot.CompatRewriteHost.ComputeBuildIdFromPrefix(prefix, 4096));
    }

    // ---- R33 findings 2, 4, 7, 8 -----------------------------------------------------------

    [Theory]
    // The names the prefix test used to admit. A prefix that runs on into other letters is not
    // the game, however much it looks like it.
    [InlineData("ProjectMEvil", false)]
    [InlineData("ProjectMBackdoor", false)]
    [InlineData("UnityHax", false)]
    [InlineData("Stunlocked", false)]
    [InlineData("Evil.Plugin", false)]
    // The real surface still passes: the root itself, and dotted children of it.
    [InlineData("ProjectM", true)]
    [InlineData("ProjectM.Shared", true)]
    [InlineData("Unity.Entities", true)]
    [InlineData("UnityEngine.CoreModule", true)]
    [InlineData("Stunlock.Core", true)]
    public void The_game_surface_test_matches_name_segments_and_not_prefixes(string name, bool game)
    {
        Assert.Equal(game, DriftMap.IsGameAssemblyName(name));
        Assert.Equal(game, Boot.DriftMap.IsGameAssemblyName(name));
    }

    [Fact]
    public void A_rule_naming_a_prefix_lookalike_assembly_is_refused_at_parse_time()
    {
        // R33 finding 2, the parse half: `ProjectMEvil` used to pass the one check §4.7 says
        // keeps the rewriter inside its remit.
        string json =
            "{\"side\":\"" + Side + "\",\"build\":\"" + Build + "\",\"generated\":[],\"hand\":[{" +
            "\"from\":\"ProjectM|ProjectM.Mover||\"," +
            "\"to\":\"ProjectMEvil|ProjectM.Mover||\"," +
            "\"kind\":\"typeMove\",\"confidence\":\"exact\",\"source\":\"hand\"}]}";
        var map = DriftMap.Parse(json, out _, out var refusals);

        Assert.NotNull(map);
        Assert.Empty(map!.Rules);
        Assert.Contains(refusals, r => r.Contains("non-game assembly", StringComparison.Ordinal)
                                       && r.Contains("ProjectMEvil", StringComparison.Ordinal));
    }

    [Fact]
    public void A_type_move_to_an_assembly_this_install_does_not_have_is_refused_not_minted()
    {
        // R33 finding 2, the apply half. `ProjectM.Shim` is a legal game-surface NAME, so the
        // parse gate admits it; the rewriter must still refuse to invent the AssemblyRef, or an
        // attacker who can write the map can write ProjectM.Shim.dll beside it and own the type
        // graph of an allowlisted plugin.
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, new[]
        {
            new DriftRule(
                DriftRule.TypeKey("ProjectM", "ProjectM.Mover"),
                DriftRule.TypeKey("ProjectM.Shim", "ProjectM.Mover"),
                DriftKind.TypeMove, DriftConfidence.Exact, DriftSource.Hand),
        });
        byte[] original = File.ReadAllBytes(world.ConsumerPath);

        byte[] loaded = PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames);

        Assert.Same(original, loaded);
        Assert.Empty(report.Hits);
        Assert.Contains(report.Skipped, s => s.Contains("ProjectM.Shim", StringComparison.Ordinal)
                                             && s.Contains("neither referenced by this image nor an interop assembly",
                                                 StringComparison.Ordinal));

        // With no interop set supplied at all, the same rule is refused for the same reason: an
        // unanswered "does this install have it" is not a yes.
        Assert.Same(original, PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Apply, out var blind));
        Assert.Empty(blind.Hits);
    }

    [Fact]
    public void A_type_reached_by_a_patch_registration_is_not_rewritten_at_all()
    {
        // R33 finding 4: recording a skip was not enough. Cecil hands out one TypeReference per
        // TypeRef row, so ApplyTypeRules rewrote the ldtoken operand anyway and the report said
        // both "left alone" and "applied" about the same type. The row is frozen now, and the
        // rule is refused for the whole module.
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, world.Diff().Rules);
        byte[] original = world.BuildPatchRegistrar();

        byte[] loaded = PluginRewriter.Rewrite("Registrar.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames);

        // ProjectM.Mover is the registered type, and it is the only rule this image could match.
        Assert.Same(original, loaded);
        Assert.False(report.Applied);
        Assert.Empty(report.Hits);
        Assert.Contains(report.Skipped, s => s.Contains("both halves left alone", StringComparison.Ordinal));
        Assert.Contains(report.Skipped, s => s.Contains("refused for the whole module", StringComparison.Ordinal));
        // The report may not claim a fix and a refusal for the same rule in one block.
        Assert.DoesNotContain(report.Lines(), l => l.Contains("applied     ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hand_rule_that_renames_a_field_and_retypes_it_is_refused()
    {
        // R33 finding 8: a field's key signature IS its type, so this is a rename AND a retype.
        // The method branch always refused it; the field branch renamed the operand and left the
        // old type on the MemberRef, so every use threw MissingFieldException at JIT time while
        // the report claimed a successful fix.
        using var world = new SyntheticWorld();
        var map = DriftMap.Create(Side, Build, new[]
        {
            new DriftRule(
                DriftRule.Key("ProjectM", "ProjectM.Flip", "Value", "System.Int32"),
                DriftRule.Key("ProjectM", "ProjectM.Flip", "Amount", "System.Single"),
                DriftKind.MemberRename, DriftConfidence.High, DriftSource.Hand, "renamed and retyped"),
        });
        byte[] original = File.ReadAllBytes(world.ConsumerPath);

        byte[] loaded = PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames);

        Assert.Same(original, loaded);
        Assert.Empty(report.Hits);
        Assert.Contains(report.Skipped, s => s.Contains("a rename may not change the signature", StringComparison.Ordinal));
    }

    [Fact]
    public void The_generator_will_not_call_an_unrelated_addition_a_high_confidence_rename()
    {
        // R33 finding 7, the exact scenario: one removal plus one unrelated addition with the
        // same signature in one type. Still emitted — as Heuristic, which is reported and never
        // applied — and an operator can promote it to a hand rule after looking at it.
        Assert.False(DriftDiff.NamesLookLikeARename("EnableDebugDraw", "SetSpawnBudget"));
        // And the shapes a real rename has.
        Assert.True(DriftDiff.NamesLookLikeARename("Ping", "Pong"));
        Assert.True(DriftDiff.NamesLookLikeARename("OnUpdate", "OnUpdateInternal"));
        Assert.True(DriftDiff.NamesLookLikeARename("GetPrefabLookupMap", "GetPrefabLookupTable"));

        using var world = new SyntheticWorld();
        var diff = world.RenameDiff("EnableDebugDraw", "SetSpawnBudget");
        var rule = Single(diff.Rules, DriftKind.MemberRename);

        Assert.Equal(DriftConfidence.Heuristic, rule.Confidence);
        Assert.False(rule.IsApplicable);
        Assert.Contains(diff.Notes, n => n.Contains("demoted to heuristic", StringComparison.Ordinal));

        // A Heuristic rule reaches the map and the report, and changes nothing in the image.
        var map = DriftMap.Create(Side, Build, diff.Rules);
        byte[] original = File.ReadAllBytes(world.ConsumerPath);
        Assert.Same(original, PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames));
        Assert.Empty(report.Hits);
    }

    // ---- the map is gated like code (job FCOMPAT3, §4.5) ----------------------------------

    [Theory]
    // The map's own sha256 is on the allowlist: admitted. The ONLY verdict that is.
    [InlineData(CompatRewritePolicy.AllowTokenListed, true)]
    // R34 finding 2: no allowlist file at all is the dev-machine default and the default for
    // every operator who never wrote one, and it used to mean Apply. For a plugin DLL "there is
    // no list" is the operator declining to gate anything; for the drift map it is not, because
    // the map is not a file they chose to run — it reaches inside the ones they did. Fail closed.
    [InlineData(CompatRewritePolicy.AllowTokenNoList, false)]
    // An allowlist exists and the map is not on it. The map is still parsed and still reported;
    // it may not drive Apply. Fail closed to DryRun, never to a refused plugin.
    [InlineData("unlisted", false)]
    [InlineData("malformed", false)]
    public void The_drift_map_must_be_on_the_allowlist_before_it_may_drive_apply(string token, bool applies)
    {
        using var world = new SyntheticWorld();
        string dir = NewTempDir();
        try
        {
            WriteMap(dir, world.Diff().Rules);
            byte[] original = File.ReadAllBytes(world.ConsumerPath);
            var log = new List<string>();

            ResetHost();
            CompatRewriteHost.AllowTokenReader = (_, _) => token;
            CompatRewriteHost.InteropAssemblies = world.NewAssemblyNames;
            byte[] loaded = CompatRewriteHost.RewriteForLoad(dir, Side, Build, "Consumer.dll",
                original, RewriteMode.Apply, log.Add);

            if (applies)
            {
                Assert.NotSame(original, loaded);
                Assert.Equal(RewriteMode.Apply, CompatRewriteHost.Ceiling);
                Assert.Equal("", CompatRewriteHost.GateLine);
                Assert.DoesNotContain(log, l => l.Contains("refusing Apply", StringComparison.Ordinal));
            }
            else
            {
                Assert.Same(original, loaded);
                Assert.Equal(RewriteMode.DryRun, CompatRewriteHost.Ceiling);
                if (token == CompatRewritePolicy.AllowTokenNoList)
                {
                    // The refusal has to be actionable: it names the file that is missing and
                    // says the map needs a row in it even though nothing else does.
                    Assert.Contains(CompatRewritePolicy.AllowListFileName, CompatRewriteHost.GateLine,
                        StringComparison.Ordinal);
                    Assert.Contains("needs a row in it even when nothing else does",
                        CompatRewriteHost.GateLine, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Contains("is not on the allowlist", CompatRewriteHost.GateLine, StringComparison.Ordinal);
                }
                Assert.Contains(log, l => l.Contains("refusing Apply", StringComparison.Ordinal)
                                          && l.Contains("falls back to DryRun", StringComparison.Ordinal));
                // The analysis still ran: this is a report, not a silent Off.
                Assert.Contains(log, l => l.Contains("would apply", StringComparison.Ordinal));
            }
        }
        finally { ResetHost(); Delete(dir); }
    }

    [Fact]
    public void With_no_allowlist_gate_wired_at_all_the_map_may_not_drive_apply()
    {
        // Null is not "allow": an unwired gate is an unanswered question. The rewriter can only
        // ever be wired by a reloader, and a reloader that forgot must not silently mean Apply.
        using var world = new SyntheticWorld();
        string dir = NewTempDir();
        try
        {
            WriteMap(dir, world.Diff().Rules);
            byte[] original = File.ReadAllBytes(world.ConsumerPath);
            var log = new List<string>();

            ResetHost();
            CompatRewriteHost.InteropAssemblies = world.NewAssemblyNames;
            byte[] loaded = CompatRewriteHost.RewriteForLoad(dir, Side, Build, "Consumer.dll",
                original, RewriteMode.Apply, log.Add);

            Assert.Same(original, loaded);
            Assert.Equal(RewriteMode.DryRun, CompatRewriteHost.Ceiling);
            Assert.Contains("no allowlist gate is wired", CompatRewriteHost.GateLine, StringComparison.Ordinal);
        }
        finally { ResetHost(); Delete(dir); }
    }

    [Fact]
    public void The_maps_own_hash_is_computed_from_the_bytes_that_were_parsed_and_logged_every_load()
    {
        using var world = new SyntheticWorld();
        string dir = NewTempDir();
        try
        {
            string path = WriteMap(dir, world.Diff().Rules);
            string expected = PluginRewriter.Sha256Hex(File.ReadAllBytes(path));

            var map = DriftMapStore.TryLoad(dir, Side, Build, out var status);

            Assert.NotNull(map);
            Assert.Equal(expected, map!.Sha256);
            Assert.Contains("sha256 " + expected.Substring(0, 16), status, StringComparison.Ordinal);
            // And the same identity reaches the per-plugin block's map line.
            PluginRewriter.Rewrite("Consumer.dll", File.ReadAllBytes(world.ConsumerPath), map,
                RewriteMode.DryRun, out var report, world.NewAssemblyNames);
            Assert.Contains("sha256 " + expected.Substring(0, 16), report.MapLine, StringComparison.Ordinal);
        }
        finally { Delete(dir); }
    }

    // ---- plugin-local dependency DLLs (job FCOMPAT3) ---------------------------------------

    [Fact]
    public void A_plugins_local_dependency_is_rewritten_leaf_first_and_the_plugin_then_runs()
    {
        using var world = new SyntheticWorld();
        using var install = new FakeInstall(world, RewriteMode.Apply);
        try
        {
            world.BuildPluginWithDependency(install.PluginFolder);
            byte[] plugin = File.ReadAllBytes(install.PluginPath);
            var log = new List<string>();

            ResetHost();
            // The map is listed (R34 finding 2: it has to be, or the ceiling is DryRun); the
            // dependency is not, and for a DLL "no list" still means the operator is not gating.
            CompatRewriteHost.AllowTokenReader = (name, _) =>
                name.StartsWith("driftmap-", StringComparison.Ordinal)
                    ? CompatRewritePolicy.AllowTokenListed
                    : CompatRewritePolicy.AllowTokenNoList;
            var bundle = CompatRewriteHost.RewriteForLoad(install.BepInExRoot, install.PluginPath, plugin, log.Add);

            // Plugin.dll only names Dep, which did not move, so its own bytes are untouched.
            Assert.Same(plugin, bundle.Image);
            // Dep.dll names ProjectM.Alpha.Ping, which did: it is the rewritten one.
            var dependency = Assert.Single(bundle.Dependencies);
            Assert.Equal("Dep.dll", dependency.FileName);
            Assert.Equal("Dep", dependency.AssemblyName);
            Assert.NotEqual(PluginRewriter.Sha256Hex(File.ReadAllBytes(install.DependencyPath)),
                PluginRewriter.Sha256Hex(dependency.Image));

            // Per file, by name, with the allowlist verdict on the ORIGINAL bytes.
            Assert.Contains(log, l => l.StartsWith("Rewrite: dependency Dep.dll sha256 ", StringComparison.Ordinal)
                                      && l.EndsWith("allowlist=none", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("Rewrite: Dep.dll", StringComparison.Ordinal));

            // And it actually binds: preload the rewritten dependency into the plugin's context,
            // leaf-first, then load and invoke the plugin against the NEW assemblies.
            Assert.Equal(5, world.RunPluginWithDependency(bundle));
        }
        finally { ResetHost(); }
    }

    [Fact]
    public void A_dependency_the_allowlist_refuses_is_left_entirely_alone()
    {
        using var world = new SyntheticWorld();
        using var install = new FakeInstall(world, RewriteMode.Apply);
        try
        {
            world.BuildPluginWithDependency(install.PluginFolder);
            var log = new List<string>();

            ResetHost();
            // The map is listed; the dependency is not. The gate is per file, on original bytes.
            CompatRewriteHost.AllowTokenReader = (name, _) =>
                name.StartsWith("driftmap-", StringComparison.Ordinal)
                    ? CompatRewritePolicy.AllowTokenListed
                    : "unlisted";
            var bundle = CompatRewriteHost.RewriteForLoad(install.BepInExRoot, install.PluginPath,
                File.ReadAllBytes(install.PluginPath), log.Add);

            Assert.Empty(bundle.Dependencies);
            Assert.Contains(log, l => l.Contains("Rewrite: dependency Dep.dll", StringComparison.Ordinal)
                                      && l.Contains("allowlist=unlisted", StringComparison.Ordinal)
                                      && l.Contains("it loads from disk as before", StringComparison.Ordinal));
        }
        finally { ResetHost(); }
    }

    [Fact]
    public void The_dependency_walk_never_leaves_the_plugins_own_folder_and_never_touches_core_or_interop()
    {
        // The name half: Progenitor itself, the loader stack and the whole game/interop surface
        // are refused as dependency names even when a file of that name sits in the folder.
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("Progenitor"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("Progenitor.Bootstrap"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("0Harmony"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("Mono.Cecil"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("BepInEx.Core"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("Il2Cppmscorlib"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("System.Text.Json"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("ProjectM"));
        Assert.False(CompatRewriteHost.IsRewritableDependencyName("Unity.Entities"));
        Assert.True(CompatRewriteHost.IsRewritableDependencyName("MyMod.Shared"));

        // The folder half: a plugin sitting in a BepInEx-owned folder gets no walk at all.
        Assert.True(CompatRewriteHost.IsRefusedDependencyFolder(Path.Combine("x", "BepInEx", "core")));
        Assert.True(CompatRewriteHost.IsRefusedDependencyFolder(Path.Combine("x", "BepInEx", "interop")));
        Assert.True(CompatRewriteHost.IsRefusedDependencyFolder(Path.Combine("x", "BepInEx", "plugins")));
        Assert.True(CompatRewriteHost.IsRefusedDependencyFolder(Path.Combine("x", "BepInEx", "progenitor-core")));
        Assert.False(CompatRewriteHost.IsRefusedDependencyFolder(Path.Combine("x", "BepInEx", "progenitor-reload")));
    }

    [Fact]
    public void A_dry_run_rewrites_no_dependency_but_still_reports_every_one_of_them()
    {
        using var world = new SyntheticWorld();
        using var install = new FakeInstall(world, RewriteMode.DryRun);
        try
        {
            world.BuildPluginWithDependency(install.PluginFolder);
            byte[] before = File.ReadAllBytes(install.DependencyPath);
            var log = new List<string>();

            ResetHost();
            CompatRewriteHost.AllowTokenReader = (_, _) => CompatRewritePolicy.AllowTokenNoList;
            var bundle = CompatRewriteHost.RewriteForLoad(install.BepInExRoot, install.PluginPath,
                File.ReadAllBytes(install.PluginPath), log.Add);

            Assert.Empty(bundle.Dependencies);
            Assert.Contains(log, l => l.Contains("Rewrite: dependency Dep.dll", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("would apply", StringComparison.Ordinal));
            // Nothing on disk moved, in any mode.
            Assert.Equal(before, File.ReadAllBytes(install.DependencyPath));
        }
        finally { ResetHost(); }
    }

    // ---- the bound mode --------------------------------------------------------------------

    [Fact]
    public void The_bound_value_is_latched_for_the_generation_and_beats_the_reader_seam()
    {
        try
        {
            CompatRewritePolicy.ModeReader = () => "Apply";
            Assert.Equal(RewriteMode.Apply, CompatRewritePolicy.Mode);

            // Plugin.Install binds the entry and adopts its value once, here.
            Assert.Equal(RewriteMode.Off, CompatRewritePolicy.Adopt("Off"));
            Assert.Equal(RewriteMode.Off, CompatRewritePolicy.Mode);

            // R34 finding 12: the latch must stay VISIBLE on the seam the load path reads.
            // Adopt used to null ModeReader, and RewriteForLoad only consults Mode when a
            // reader is set — so the bound value was dead and every load re-read the .cfg.
            Assert.NotNull(CompatRewritePolicy.ModeReader);
            Assert.Equal("Off", CompatRewritePolicy.ModeReader!());

            // The same three values the cfg reader parses, and the same fallback.
            Assert.Equal(RewriteMode.DryRun, CompatRewritePolicy.Adopt("nonsense"));
            Assert.Equal(RewriteMode.DryRun, CompatRewritePolicy.Adopt(null));
        }
        finally
        {
            CompatRewritePolicy.ResetMode();
        }
    }

    [Fact]
    public void The_adopted_mode_reaches_the_load_path_and_the_cfg_is_parsed_once()
    {
        // R34 finding 12, the whole of it. Two claims:
        //   1. what Plugin.Install adopted is what RewriteForLoad runs under, and
        //   2. with nothing adopted the .cfg is read ONCE per generation, not per plugin load,
        //      so two plugins in one process cannot disagree about what ran.
        using var world = new SyntheticWorld();
        using var install = new FakeInstall(world, RewriteMode.Apply);
        try
        {
            world.BuildPluginWithDependency(install.PluginFolder);
            string cfg = Path.Combine(install.BepInExRoot, "config", CompatRewriteHost.ConfigFileName);

            ResetHost();
            // Nothing adopted: the file says Apply, and the file is what is used.
            Assert.Equal(RewriteMode.Apply, CompatRewriteHost.ModeForLoad(install.BepInExRoot));

            // The operator (or BepInEx's own config writer) rewrites the file mid-session. The
            // generation already decided; plugin B may not load under a different mode than A.
            File.WriteAllText(cfg, "[" + CompatRewritePolicy.ConfigSection + "]\n"
                                   + CompatRewritePolicy.ConfigKey + " = Off\n");
            Assert.Equal(RewriteMode.Apply, CompatRewriteHost.ModeForLoad(install.BepInExRoot));
            // And the read is latched, not re-taken: deleting the file changes nothing either.
            File.Delete(cfg);
            Assert.Equal(RewriteMode.Apply, CompatRewriteHost.ModeForLoad(install.BepInExRoot));

            // Now Core's Plugin.Install binds and adopts. That value wins from here on — this is
            // the half that used to be dead, because Adopt cleared the seam the load path reads.
            CompatRewritePolicy.Adopt("Off");
            Assert.Equal(RewriteMode.Off, CompatRewriteHost.ModeForLoad(install.BepInExRoot));

            // End to end: Off through the production overload means the input array back and no
            // dependency work at all, even though the map, the build and the plugin all exist.
            byte[] plugin = File.ReadAllBytes(install.PluginPath);
            var bundle = CompatRewriteHost.RewriteForLoad(install.BepInExRoot, install.PluginPath, plugin, null);
            Assert.Same(plugin, bundle.Image);
            Assert.Empty(bundle.Dependencies);

            // Re-latching (a Core swap re-binds) is what changes it.
            CompatRewritePolicy.Adopt("Apply");
            Assert.Equal(RewriteMode.Apply, CompatRewriteHost.ModeForLoad(install.BepInExRoot));
        }
        finally
        {
            ResetHost();
        }
    }

    [Fact]
    public void The_config_description_names_the_section_key_and_every_value()
    {
        // The description Plugin.Install passes to Config.Bind is what an operator reads in the
        // .cfg. If it stops naming a mode, the key stops being self-explanatory.
        Assert.Equal("Compat", CompatRewritePolicy.ConfigSection);
        Assert.Equal("RewriteMode", CompatRewritePolicy.ConfigKey);
        foreach (string value in new[] { "Off", "DryRun", "Apply" })
            Assert.Contains(value, CompatRewritePolicy.ConfigDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_install_binds_the_mode_and_bootstrap_reads_the_same_entry_by_hand()
    {
        // Core's Plugin.Install goes through ConfigHelper like every other section; Bootstrap's
        // mirror cannot (it has no Core ConfigFile on the load path) and reads the same section
        // and key out of the same file instead. Pin both halves as source, since Plugin.cs is
        // not compiled by this project.
        string plugin = ReadRepoFile("src", "Progenitor.Core", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "ConfigHelper.BindEnum(");
        SourcePin.AssertUncommented(plugin, "CompatRewritePolicy.ConfigSection");
        SourcePin.AssertUncommented(plugin, "CompatRewritePolicy.ConfigKey");
        SourcePin.AssertUncommented(plugin, "CompatRewritePolicy.ConfigDescription");
        SourcePin.AssertUncommented(plugin, "CompatRewritePolicy.Adopt(");

        string root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", CompatRewriteHost.ConfigFileName),
                "[" + CompatRewritePolicy.ConfigSection + "]\n"
                + CompatRewritePolicy.ConfigKey + " = Apply\n");
            Assert.Equal(RewriteMode.Apply, CompatRewriteHost.ReadModeFromConfig(root));
            Assert.Equal(Boot.RewriteMode.Apply, Boot.CompatRewriteHost.ReadModeFromConfig(root));
        }
        finally { Delete(root); }
    }

    // ---- job FCOMPAT4: R34 findings 3, 4 and 10 --------------------------------------------

    [Fact]
    public void One_rule_the_pass_refused_does_not_discard_the_rules_that_worked()
    {
        // R34 finding 3. The pass has five paths that leave a rule's old key in the image ON
        // PURPOSE, and SelfCheck used to read any surviving key as proof the rewrite failed - so
        // the two R33 fixes that made the rewriter safer were also the two that made it useless.
        // Here: two plain rules that apply, plus a typeMove whose destination assembly this
        // install does not have, which the pass must refuse and the self-check must ignore.
        using var world = new SyntheticWorld();
        var rules = new List<DriftRule>();
        foreach (var rule in world.Diff().Rules)
            if (rule.Kind != DriftKind.TypeMove) rules.Add(rule);
        Assert.Equal(2, rules.Count);
        rules.Add(new DriftRule(
            DriftRule.TypeKey("ProjectM", "ProjectM.Mover"),
            DriftRule.TypeKey("ProjectM.Shim", "ProjectM.Mover"),
            DriftKind.TypeMove, DriftConfidence.Exact, DriftSource.Hand));
        var map = DriftMap.Create(Side, Build, rules);
        byte[] original = File.ReadAllBytes(world.ConsumerPath);

        byte[] loaded = PluginRewriter.Rewrite("Consumer.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames);

        // The refusal happened, and it is reported.
        Assert.Contains(report.Skipped, l => l.Contains("ProjectM.Shim", StringComparison.Ordinal));
        Assert.Contains(report.RefusedSites, k => k.Contains("ProjectM.Mover", StringComparison.Ordinal));

        // And the two rules that worked survived it. Before the fix this was the original array,
        // report.Failure = "self-check failed: typeMove ... still references the old member",
        // and the plugin loaded unrewritten with nothing the operator could do about it.
        Assert.Equal("", report.Failure);
        Assert.True(report.Applied);
        Assert.NotSame(original, loaded);
        Assert.Equal(2, report.Hits.Count);
        Assert.DoesNotContain(report.Lines(), l => l.Contains("self-check failed", StringComparison.Ordinal));
    }

    // ---- job FCOMPAT5: R35 finding 6 ------------------------------------------------------

    [Fact]
    public void A_refusal_at_one_site_still_verifies_the_other_sites_of_the_same_rule()
    {
        // R35 finding 6. The fieldToProperty rule for ProjectM.Flip.Value covers three sites in
        // this image: a store, a load, and one ldflda that a property cannot answer. The pass
        // must refuse the ldflda ALONE - the two rewritable sites still apply, and the self-check
        // still inspects them. FCOMPAT4 keyed the skip on the rule, so this shape switched the
        // check off for all three.
        using var world = new SyntheticWorld();
        var rules = new List<DriftRule>();
        foreach (var rule in world.Diff().Rules)
            if (rule.Kind == DriftKind.FieldToProperty) rules.Add(rule);
        Assert.Single(rules);
        var map = DriftMap.Create(Side, Build, rules);
        byte[] original = world.BuildFieldSites(withDriftedRow: false);

        byte[] loaded = PluginRewriter.Rewrite("Sites.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames);

        Assert.Equal("", report.Failure);
        Assert.True(report.Applied);
        Assert.NotSame(original, loaded);

        // Two sites applied, one refused, and the refusal is recorded as a SITE - a method and an
        // instruction offset - not as the rule's key.
        Assert.Equal(2, Assert.Single(report.Hits).Sites);
        Assert.Contains(report.Skipped, l => l.Contains("ldflda has no property equivalent", StringComparison.Ordinal));
        string site = Assert.Single(report.RefusedSites);
        Assert.Contains("Sites.Entry::Address", site, StringComparison.Ordinal);
        Assert.Contains("@IL_", site, StringComparison.Ordinal);
        Assert.DoesNotContain(DriftRule.Key("ProjectM", "ProjectM.Flip", "Value", "System.Int32"),
            report.RefusedSites);

        // And the two that applied really were rewritten: only the refused ldflda still names the
        // old field.
        Assert.Equal(1, CountFieldUses(loaded, "Value"));
        Assert.Equal(2, CountMethodUses(loaded, "get_Value") + CountMethodUses(loaded, "set_Value"));
    }

    [Fact]
    public void An_old_reference_left_at_a_site_nobody_refused_still_fails_the_self_check()
    {
        // The other half of R35 finding 6: the check has to keep biting for a rule that was
        // refused SOMEWHERE. Same three sites as above (one ldflda refused), plus a typeRename
        // that points ProjectM.Other at ProjectM.Flip. That rename is applied, and it thereby
        // MANUFACTURES a fourth `ldfld ProjectM.Flip::Value` in the written image - the exact old
        // key the fieldToProperty rule exists to remove, at a site the pass never touched and
        // never refused. The plugin would MissingFieldException on that method at JIT time.
        //
        // Under the per-rule skip list this passed: the ldflda had put the rule's key in the
        // refused set, so the self-check ignored every surviving use of it. Per site, the
        // manufactured one is not exempt, the check fails, and the ORIGINAL bytes load.
        using var world = new SyntheticWorld();
        var rules = new List<DriftRule>();
        foreach (var rule in world.Diff().Rules)
            if (rule.Kind == DriftKind.FieldToProperty) rules.Add(rule);
        Assert.Single(rules);
        rules.Add(new DriftRule(
            DriftRule.TypeKey("ProjectM", "ProjectM.Other"),
            DriftRule.TypeKey("ProjectM", "ProjectM.Flip"),
            DriftKind.TypeRename, DriftConfidence.Exact, DriftSource.Hand));
        var map = DriftMap.Create(Side, Build, rules);
        byte[] original = world.BuildFieldSites(withDriftedRow: true);

        byte[] loaded = PluginRewriter.Rewrite("Sites.dll", original, map, RewriteMode.Apply,
            out var report, world.NewAssemblyNames);

        Assert.Contains("self-check failed", report.Failure, StringComparison.Ordinal);
        Assert.Contains("Sites.Entry::Drifted", report.Failure, StringComparison.Ordinal);
        Assert.False(report.Applied);
        Assert.Same(original, loaded);
        Assert.Equal(report.OriginalSha, report.LoadedSha);
    }

    /// <summary>How many field operands in <paramref name="image"/> name <paramref name="name"/>.</summary>
    private static int CountFieldUses(byte[] image, string name) =>
        CountOperands(image, o => o is FieldReference f && f.Name == name);

    /// <summary>How many method operands in <paramref name="image"/> name <paramref name="name"/>.</summary>
    private static int CountMethodUses(byte[] image, string name) =>
        CountOperands(image, o => o is MethodReference m && m.Name == name);

    private static int CountOperands(byte[] image, Func<object?, bool> match)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var assembly = AssemblyDefinition.ReadAssembly(stream,
            new ReaderParameters { InMemory = true, AssemblyResolver = new EmptyResolver() });
        int n = 0;
        foreach (var type in assembly.MainModule.GetTypes())
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                    if (match(instruction.Operand)) n++;
            }
        return n;
    }

    [Fact]
    public void A_handler_registered_from_a_rewritten_dependency_is_swept_with_its_plugin()
    {
        // R34 finding 4. FCOMPAT3 moved a plugin's helper DLL out of the non-collectible default
        // context and into the plugin's own collectible one - the right place for it - but every
        // unload sweep still took plugin.GetType().Assembly and only that. EventBus filters on
        // the DECLARING assembly, so a handler declared in Shared.dll was never swept: the
        // delegate held the LoaderAllocator, the ALC never collected, and CoreLoader's
        // PluginAlcsAlive refusal then blocked Core swaps for the rest of the session.
        using var world = new SyntheticWorld();
        string dir = NewTempDir();
        var alc = new AssemblyLoadContext("progenitor-reload:sweep-probe:1", isCollectible: true);
        alc.Resolving += (_, name) =>
            string.Equals(name.Name, typeof(CompatRewriterTests).Assembly.GetName().Name, StringComparison.Ordinal)
                ? typeof(CompatRewriterTests).Assembly
                : null;
        try
        {
            world.BuildEventDependency(dir);
            Assembly dependency, plugin;
            using (var stream = new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "Dep.dll")), writable: false))
                dependency = alc.LoadFromStream(stream);
            using (var stream = new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "Plugin.dll")), writable: false))
                plugin = alc.LoadFromStream(stream);

            // The plugin's owned set is the plugin plus everything else in its context - which is
            // where the rewriter preloads each rewritten dependency image.
            var owned = PluginOwnedAssemblies.Of(plugin, alc);
            Assert.Contains(plugin, owned);
            Assert.Contains(dependency, owned);
            Assert.Same(plugin, owned[0]);

            // One handler DECLARED in the dependency (owner: null, the case RemoveAll cannot
            // reach) and one whose OWNER object is a dependency type.
            var handler = (Action<SweepProbe>)Delegate.CreateDelegate(typeof(Action<SweepProbe>),
                dependency.GetType("Dep.Entry")!.GetMethod("Handle", BindingFlags.Public | BindingFlags.Static)!);
            Progenitor.Events.EventBus.On(handler, owner: null);
            Progenitor.Events.EventBus.On<SweepProbe>(
                _ => { }, owner: Activator.CreateInstance(dependency.GetType("Dep.Owner")!));
            Assert.Equal(2, Progenitor.Events.EventBus.SubscriberCount<SweepProbe>());

            // Sweeping the plugin assembly alone leaves both behind: that is the bug.
            Assert.Equal(0, PluginOwnedAssemblies.RemoveEventHandlers(new[] { plugin }));
            Assert.Equal(2, Progenitor.Events.EventBus.SubscriberCount<SweepProbe>());

            // Sweeping what the plugin OWNS takes them with it.
            Assert.Equal(2, PluginOwnedAssemblies.RemoveEventHandlers(owned));
            Assert.Equal(0, Progenitor.Events.EventBus.SubscriberCount<SweepProbe>());
        }
        finally
        {
            Progenitor.Events.EventBus.RemoveDeclaredIn(typeof(CompatRewriterTests).Assembly);
            alc.Unload();
            Delete(dir);
        }
    }

    [Fact]
    public void The_default_context_is_never_treated_as_a_plugins_owned_set()
    {
        // The other direction of the same rule: Core itself lives in a non-collectible context
        // holding every assembly in the process, and sweeping "declared in" across that would
        // drop Core's own registrations. Only a collectible plugin ALC is owned.
        var owned = PluginOwnedAssemblies.Of(typeof(CompatRewriterTests).Assembly);
        Assert.Single(owned);
        Assert.Same(typeof(CompatRewriterTests).Assembly, owned[0]);
        Assert.Empty(PluginOwnedAssemblies.Of(null));
    }

    /// <summary>An event type only this test publishes, so the sweep counts are unambiguous.</summary>
    public sealed class SweepProbe
    {
    }

    [Fact]
    public void The_scan_and_the_rewriter_key_a_generic_declaring_type_the_same_way()
    {
        // R34 finding 10. The scan reduces a TypeSpec MemberRef parent to the open type it
        // instantiates (List`1); Cecil hands the rewriter the instantiated GenericInstanceType
        // (List`1<System.Int32>). DriftMap._byFrom is ordinal, so the two never met - while
        // ResilienceDiff joined on the SCAN's key and happily printed "[fixable by rewrite]"
        // beside a row the rewriter could never find. One shared reduction now, on both sides.
        string path = typeof(CompatRewriterTests).Assembly.Location;

        // The scan's key for List`1.Add, read out of this assembly's real metadata.
        var rows = Progenitor.Resilience.MetadataScan.ScanReferences(path, out var error, new[] { "System" });
        Assert.Null(error);
        var scanned = rows.Single(r =>
            r.Type == "System.Collections.Generic.List`1" && r.Name == "Add"
            && r.Kind == Progenitor.Resilience.MemberKind.Method);

        // The rewriter's key for the same call site, read out of the same file with Cecil.
        using var module = ModuleDefinition.ReadModule(path, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
            AssemblyResolver = new EmptyResolver(),
        });
        var probe = module.GetType(typeof(CompatRewriterTests).FullName!.Replace('+', '/'))
            .Methods.Single(m => m.Name == nameof(GenericKeyProbe));
        var call = probe.Body.Instructions.Single(i =>
            i.Operand is MethodReference m && m.Name == "Add"
            && m.DeclaringType is GenericInstanceType g
            && g.ElementType.FullName == "System.Collections.Generic.List`1");
        string rewriterKey = PluginRewriter.KeyOf((MethodReference)call.Operand);

        Assert.Equal(scanned.Key, rewriterKey);
        Assert.Contains("System.Collections.Generic.List`1|Add|", rewriterKey, StringComparison.Ordinal);
        Assert.DoesNotContain("<", rewriterKey, StringComparison.Ordinal);

        // And a map row written in either form joins the other, because the rule normalizes its
        // own keys too — a hand-written map that pasted the instantiated form still matches the
        // scan's row, and the report and the rewriter agree about what is fixable.
        const string instantiated = "Unity.Collections.NativeArray`1<ProjectM.Entity>";
        const string open = "Unity.Collections.NativeArray`1";
        var map = DriftMap.Create(Side, Build, new[]
        {
            new DriftRule(
                DriftRule.Key("Unity.Collections", instantiated, "Length", "System.Int32"),
                DriftRule.Key("Unity.Collections", open, "Count", "System.Int32"),
                DriftKind.MemberRename, DriftConfidence.High, DriftSource.Hand),
        });
        Assert.NotNull(map.Find(DriftRule.Key("Unity.Collections", open, "Length", "System.Int32")));
        Assert.NotNull(map.Find(DriftRule.Key("Unity.Collections", instantiated, "Length", "System.Int32")));

        // The signature half is NOT reduced: both sides render those instantiated, and that is
        // the identity a rule needs to tell two overloads apart.
        Assert.Equal("System.Void(!0)", scanned.Signature);
    }

    private static int GenericKeyProbe()
    {
        var list = new List<int>();
        list.Add(7);
        return list.Count;
    }

    /// <summary>A Cecil resolver that resolves nothing, for the read-only key comparison above.</summary>
    private sealed class EmptyResolver : IAssemblyResolver
    {
        public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;

        public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;

        public void Dispose() { }
    }

    [Fact]
    public void The_rewriters_allowlist_file_name_is_the_reloaders()
    {
        // The refusal line names a file; the mirror may not reference ReloadAllowPolicy (it is in
        // a different namespace on each side), so the string is a literal. This is its pin.
        Assert.Equal(Progenitor.Reload.ReloadAllowPolicy.FileName, CompatRewritePolicy.AllowListFileName);
        Assert.Equal(Boot.ReloadAllowPolicy.FileName, Boot.CompatRewritePolicy.AllowListFileName);
    }

    // ---- the mirror ------------------------------------------------------------------------

    [Theory]
    [InlineData("DriftMap.cs")]
    [InlineData("PluginRewriter.cs")]
    [InlineData("CompatRewriteHost.cs")]
    public void The_two_rewriter_copies_are_the_same_code(string file)
    {
        // Source pin, and the one that earns it: ReloaderHost is the reloader that runs in
        // production and cannot reference Core, so the whole rewriter exists twice. Both copies
        // are compiled by this project, so the behavioral tests above cover what they do.
        string boot = StripLineComments(ReadRepoFile("src", "Progenitor.Bootstrap", file))
            .Replace("namespace Progenitor.Bootstrap;", "namespace X;");
        string core = StripLineComments(ReadRepoFile("src", "Progenitor.Core", "Compat", file))
            .Replace("namespace Progenitor.Compat;", "namespace X;");
        Assert.Equal(boot, core);
    }

    [Fact]
    public void Both_reloaders_call_the_rewriter_between_the_allow_gate_and_load_from_stream()
    {
        foreach (var (project, name) in new[]
                 {
                     ("Progenitor.Core", Path.Combine("Reload", "PluginReloader.cs")),
                     ("Progenitor.Bootstrap", "ReloaderHost.cs"),
                 })
        {
            string text = ReadRepoFile("src", project, name).Replace("\r\n", "\n");
            int gate = text.IndexOf("ReloadAllowPolicy.MayLoad(allow)", StringComparison.Ordinal);
            int hunk = text.IndexOf("CompatRewriteHost.RewriteForLoad(", StringComparison.Ordinal);
            int load = text.IndexOf("context.LoadFromStream(stream)", StringComparison.Ordinal);
            Assert.True(gate > 0, project + ": no allow gate");
            Assert.True(hunk > gate, project + ": the rewriter must run AFTER the allow gate, so a "
                                     + "refused file is never parsed by Cecil");
            Assert.True(load > hunk, project + ": the rewriter must run BEFORE LoadFromStream");
        }
    }

    // ---- fixture ---------------------------------------------------------------------------

    private static DriftRule Single(IEnumerable<DriftRule> rules, DriftKind kind)
    {
        var matches = rules.Where(r => r.Kind == kind).ToList();
        Assert.True(matches.Count == 1, "expected exactly one " + DriftRule.KindText(kind)
                                        + " rule, got " + matches.Count);
        return matches[0];
    }

    /// <summary>Write the map for this fixture's build into <paramref name="folder"/>.</summary>
    private static string WriteMap(string folder, IEnumerable<DriftRule> rules)
    {
        string path = Path.Combine(folder, DriftMapStore.FileName(Build));
        File.WriteAllText(path, DriftMap.Create(Side, Build, rules).ToJson());
        return path;
    }

    /// <summary>Clear every per-process latch the host keeps, so one test cannot leak into another.</summary>
    private static void ResetHost()
    {
        CompatRewriteHost.Reset();
        CompatRewriteHost.AllowTokenReader = null;
        CompatRewriteHost.InteropAssemblies = null;
        CompatRewritePolicy.ResetMode();
    }

    /// <summary>
    /// The smallest thing that looks like a V Rising install to <c>RewriteForLoad</c>'s
    /// production overload: a game root with a <c>GameAssembly.dll</c> whose build id names the
    /// map, a <c>BepInEx</c> root with a config file, an <c>interop</c> folder holding the "new"
    /// assemblies, <c>progenitor-core</c> holding the map, and a plugin folder.
    /// </summary>
    private sealed class FakeInstall : IDisposable
    {
        private readonly string _root;

        public FakeInstall(SyntheticWorld world, RewriteMode mode)
        {
            _root = NewTempDir();
            BepInExRoot = Path.Combine(_root, "BepInEx");
            PluginFolder = Path.Combine(BepInExRoot, "progenitor-reload");
            string core = Path.Combine(BepInExRoot, CompatRewriteHost.CoreFolderName);
            string interop = Path.Combine(BepInExRoot, CompatRewriteHost.InteropFolderName);
            string config = Path.Combine(BepInExRoot, "config");
            Directory.CreateDirectory(PluginFolder);
            Directory.CreateDirectory(core);
            Directory.CreateDirectory(interop);
            Directory.CreateDirectory(config);

            // A GameAssembly.dll whose bytes give the fixture's build id. Written first, then the
            // map is named after whatever id it actually produced — the same rule the game uses.
            string gameAssembly = Path.Combine(_root, CompatRewriteHost.GameAssemblyFileName);
            var bytes = new byte[8192];
            new Random(33033).NextBytes(bytes);
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            File.WriteAllBytes(gameAssembly, bytes);
            BuildId = CompatRewriteHost.TryComputeBuildId(gameAssembly);
            Assert.NotEqual("", BuildId);

            foreach (string dll in Directory.GetFiles(world.NewDir, "*.dll"))
                File.Copy(dll, Path.Combine(interop, Path.GetFileName(dll)));

            File.WriteAllText(Path.Combine(core, DriftMapStore.FileName(BuildId)),
                DriftMap.Create(CompatRewriteHost.DetectSide(), BuildId, world.Diff().Rules).ToJson());
            File.WriteAllText(Path.Combine(config, CompatRewriteHost.ConfigFileName),
                "[" + CompatRewritePolicy.ConfigSection + "]\n"
                + CompatRewritePolicy.ConfigKey + " = " + CompatRewritePolicy.Text(mode) + "\n");
        }

        public string BepInExRoot { get; }
        public string PluginFolder { get; }
        public string BuildId { get; }
        public string PluginPath => Path.Combine(PluginFolder, "Plugin.dll");
        public string DependencyPath => Path.Combine(PluginFolder, "Dep.dll");

        public void Dispose() => Delete(_root);
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    /// <summary>Drops <c>//</c> line comments (never <c>///</c>) so the mirror header is not drift.</summary>
    private static string StripLineComments(string text)
    {
        var kept = new List<string>();
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("///", StringComparison.Ordinal))
                continue;
            string trimmed = line.TrimEnd();
            if (trimmed.Length == 0 && kept.Count > 0 && kept[kept.Count - 1].Length == 0) continue;
            kept.Add(trimmed);
        }
        return string.Join("\n", kept).Trim();
    }

    /// <summary>
    /// The whole S1 fixture on disk: an "old" and a "new" interop set plus one consumer built
    /// against the old one, all synthetic and all thrown away with the temp directory.
    /// </summary>
    private sealed class SyntheticWorld : IDisposable
    {
        private readonly string _root;

        public SyntheticWorld()
        {
            _root = NewTempDir();
            OldDir = Path.Combine(_root, "old");
            NewDir = Path.Combine(_root, "new");
            Directory.CreateDirectory(OldDir);
            Directory.CreateDirectory(NewDir);
            BuildOld();
            BuildNew();
            BuildConsumer();
        }

        public string OldDir { get; }
        public string NewDir { get; }
        public string ConsumerPath => Path.Combine(_root, "Consumer.dll");

        /// <summary>
        /// The "interop set" of this install: the simple names of the NEW assemblies. A typeMove
        /// may only aim at one of these or at something the image already references (R33 #2).
        /// </summary>
        public IReadOnlyCollection<string> NewAssemblyNames
        {
            get
            {
                var names = new List<string>();
                foreach (string dll in Directory.GetFiles(NewDir, "*.dll"))
                    names.Add(Path.GetFileNameWithoutExtension(dll));
                return names;
            }
        }

        /// <summary>
        /// A two-DLL plugin in <paramref name="folder"/>: <c>Dep.dll</c> calls
        /// <c>ProjectM.Alpha.Ping</c> (the member that gets renamed) and <c>Plugin.dll</c> calls
        /// only <c>Dep.Entry.Go</c>. So the plugin itself needs no rewrite and its dependency
        /// does — which is exactly the shape "rewrite dependencies leaf-first" exists for.
        /// </summary>
        public void BuildPluginWithDependency(string folder)
        {
            var resolver = new DirectoryResolver(OldDir);
            using var projectM = resolver.Resolve(new AssemblyNameReference("ProjectM", new Version(1, 0, 0, 0)))!;
            var alpha = projectM.MainModule.GetType("ProjectM.Alpha");

            var dep = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Dep", new Version(1, 0, 0, 0)), "Dep", ModuleKind.Dll);
            var depModule = dep.MainModule;
            var depType = new TypeDefinition("Dep", "Entry",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                | Mono.Cecil.TypeAttributes.Sealed, depModule.TypeSystem.Object);
            depModule.Types.Add(depType);
            var go = new MethodDefinition("Go",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                depModule.TypeSystem.Int32);
            depType.Methods.Add(go);
            var dil = go.Body.GetILProcessor();
            dil.Emit(OpCodes.Ldc_I4, 5);
            dil.Emit(OpCodes.Call, depModule.ImportReference(Method(alpha, "Ping")));
            dil.Emit(OpCodes.Call, depModule.ImportReference(Method(alpha, "get_Total")));
            dil.Emit(OpCodes.Ret);
            go.Body.MaxStackSize = 8;
            string depPath = Path.Combine(folder, "Dep.dll");
            dep.Write(depPath);

            var plugin = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Plugin", new Version(1, 0, 0, 0)), "Plugin", ModuleKind.Dll);
            var pluginModule = plugin.MainModule;
            var pluginType = new TypeDefinition("Plugin", "Entry",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                | Mono.Cecil.TypeAttributes.Sealed, pluginModule.TypeSystem.Object);
            pluginModule.Types.Add(pluginType);
            var run = new MethodDefinition("Run",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                pluginModule.TypeSystem.Int32);
            pluginType.Methods.Add(run);
            var pil = run.Body.GetILProcessor();
            pil.Emit(OpCodes.Call, pluginModule.ImportReference(go));
            pil.Emit(OpCodes.Ret);
            run.Body.MaxStackSize = 8;
            plugin.Write(Path.Combine(folder, "Plugin.dll"));
        }

        /// <summary>
        /// A two-DLL plugin whose <c>Dep.dll</c> owns an <c>EventBus</c> registration: a static
        /// handler and an owner type. Nothing here is rewritten — the point is what the UNLOAD
        /// sweep has to reach once a dependency lives in the plugin's collectible context
        /// (R34 finding 4). <c>Plugin.dll</c> references <c>Dep</c> and declares nothing.
        /// </summary>
        public void BuildEventDependency(string folder)
        {
            var tests = AssemblyDefinition.ReadAssembly(typeof(CompatRewriterTests).Assembly.Location,
                new ReaderParameters { InMemory = true, AssemblyResolver = new EmptyResolver() });
            using (tests)
            {
                var probe = tests.MainModule.GetType("Progenitor.Tests.CompatRewriterTests/SweepProbe");
                Assert.NotNull(probe);

                var dep = AssemblyDefinition.CreateAssembly(
                    new AssemblyNameDefinition("Dep", new Version(1, 0, 0, 0)), "Dep", ModuleKind.Dll);
                var depModule = dep.MainModule;

                var entry = new TypeDefinition("Dep", "Entry",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                    | Mono.Cecil.TypeAttributes.Sealed, depModule.TypeSystem.Object);
                depModule.Types.Add(entry);
                var handle = new MethodDefinition("Handle",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                    depModule.TypeSystem.Void);
                handle.Parameters.Add(new ParameterDefinition(depModule.ImportReference(probe)));
                handle.Body.GetILProcessor().Emit(OpCodes.Ret);
                entry.Methods.Add(handle);

                // A plain public class with a default ctor, so the test can make one and pass it
                // as the registration's owner.
                var owner = new TypeDefinition("Dep", "Owner",
                    Mono.Cecil.TypeAttributes.Public, depModule.TypeSystem.Object);
                depModule.Types.Add(owner);
                var ctor = new MethodDefinition(".ctor",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig
                    | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                    depModule.TypeSystem.Void);
                var cil = ctor.Body.GetILProcessor();
                cil.Emit(OpCodes.Ldarg_0);
                cil.Emit(OpCodes.Call, depModule.ImportReference(
                    new MethodReference(".ctor", depModule.TypeSystem.Void, depModule.TypeSystem.Object)
                    { HasThis = true }));
                cil.Emit(OpCodes.Ret);
                owner.Methods.Add(ctor);
                dep.Write(Path.Combine(folder, "Dep.dll"));

                var plugin = AssemblyDefinition.CreateAssembly(
                    new AssemblyNameDefinition("Plugin", new Version(1, 0, 0, 0)), "Plugin", ModuleKind.Dll);
                var pluginModule = plugin.MainModule;
                var pluginType = new TypeDefinition("Plugin", "Entry",
                    Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                    | Mono.Cecil.TypeAttributes.Sealed, pluginModule.TypeSystem.Object);
                pluginModule.Types.Add(pluginType);
                var run = new MethodDefinition("Run",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                    pluginModule.TypeSystem.Void);
                var ril = run.Body.GetILProcessor();
                ril.Emit(OpCodes.Ldnull);
                ril.Emit(OpCodes.Call, pluginModule.ImportReference(handle));
                ril.Emit(OpCodes.Ret);
                run.Body.MaxStackSize = 8;
                pluginType.Methods.Add(run);
                plugin.Write(Path.Combine(folder, "Plugin.dll"));
            }
        }

        /// <summary>
        /// Preload the bundle's rewritten dependencies into one collectible context, leaf-first,
        /// then load the plugin into the same context and call <c>Plugin.Entry.Run</c> — the
        /// reloader's hunk, in miniature, against the NEW assemblies.
        /// </summary>
        public int RunPluginWithDependency(RewriteBundle bundle)
        {
            var alc = new NewSetContext(NewDir);
            try
            {
                bundle.PreloadInto(
                    (_, image) =>
                    {
                        using var dependency = new MemoryStream(image, writable: false);
                        alc.LoadFromStream(dependency);
                    },
                    null);
                using var stream = new MemoryStream(bundle.Image, writable: false);
                var assembly = alc.LoadFromStream(stream);
                var method = assembly.GetType("Plugin.Entry")!.GetMethod("Run",
                    BindingFlags.Public | BindingFlags.Static)!;
                try { return (int)method.Invoke(null, null)!; }
                catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
            }
            finally
            {
                alc.Unload();
            }
        }

        public DriftDiff.Result Diff()
        {
            var notes = new List<string>();
            var before = AssemblySurface.Read(OldDir, DriftMap.GameAssemblyPrefixes, notes);
            var after = AssemblySurface.Read(NewDir, DriftMap.GameAssemblyPrefixes, notes);
            Assert.Empty(notes);
            return DriftDiff.Compare(before, after);
        }

        public void Dispose() => Delete(_root);

        // ProjectM.dll (old): Alpha.Ping(int), Mover (moved later), Flip.Value (field),
        // Layout with [FieldOffset(4)]. ProjectM.Shared.dll (old): one unrelated type, so the
        // assembly exists on both sides and the diff is about what moved into it.
        private void BuildOld()
        {
            var shared = NewAssembly("ProjectM.Shared");
            AddMarker(shared, "ProjectM.Shared.Anchor");
            shared.Write(Path.Combine(OldDir, "ProjectM.Shared.dll"));

            var main = NewAssembly("ProjectM");
            AddCounter(main, "ProjectM.Alpha", "Ping");
            AddCounter(main, "ProjectM.Mover", "Step");
            AddValueField(main, "ProjectM.Flip");
            AddExplicitLayout(main, "ProjectM.Layout", offset: 4);
            main.Write(Path.Combine(OldDir, "ProjectM.dll"));
        }

        // The "new build": the same set with the four drifts applied.
        private void BuildNew()
        {
            var shared = NewAssembly("ProjectM.Shared");
            AddMarker(shared, "ProjectM.Shared.Anchor");
            AddCounter(shared, "ProjectM.Mover", "Step");     // the typeMove destination
            shared.Write(Path.Combine(NewDir, "ProjectM.Shared.dll"));

            var main = NewAssembly("ProjectM");
            AddCounter(main, "ProjectM.Alpha", "Pong");        // memberRename
            AddValueProperty(main, "ProjectM.Flip");           // fieldToProperty
            AddExplicitLayout(main, "ProjectM.Layout", offset: 8); // the negative control
            main.Write(Path.Combine(NewDir, "ProjectM.dll"));
        }

        /// <summary>
        /// One consumer method built against the OLD assemblies:
        /// <c>Alpha.Ping(1); Mover.Step(7); Flip.Value = 41;</c> then returns the sum the three
        /// types accumulated. It is a plugin's IL in miniature — a call, a newobj across an
        /// assembly boundary, and a field access.
        /// </summary>
        private void BuildConsumer()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Consumer", new Version(1, 0, 0, 0)), "Consumer", ModuleKind.Dll);
            var module = assembly.MainModule;
            var resolver = new DirectoryResolver(OldDir);
            using var projectM = resolver.Resolve(new AssemblyNameReference("ProjectM", new Version(1, 0, 0, 0)))!;

            var alpha = projectM.MainModule.GetType("ProjectM.Alpha");
            var mover = projectM.MainModule.GetType("ProjectM.Mover");
            var flip = projectM.MainModule.GetType("ProjectM.Flip");

            var type = new TypeDefinition("Consumer", "Entry",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);

            var run = new MethodDefinition("Run",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            type.Methods.Add(run);
            var il = run.Body.GetILProcessor();

            // Alpha.Ping(1)
            il.Emit(OpCodes.Ldc_I4, 1);
            il.Emit(OpCodes.Call, module.ImportReference(Method(alpha, "Ping")));
            // Mover.Step(7) - the type that moves to ProjectM.Shared
            il.Emit(OpCodes.Ldc_I4, 7);
            il.Emit(OpCodes.Call, module.ImportReference(Method(mover, "Step")));
            // new Flip().Value = 41, then read it back - a store and a load of the flipped member
            var local = new VariableDefinition(module.ImportReference(flip));
            run.Body.Variables.Add(local);
            il.Emit(OpCodes.Newobj, module.ImportReference(Method(flip, ".ctor")));
            il.Emit(OpCodes.Stloc, local);
            il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Ldc_I4, 41);
            il.Emit(OpCodes.Stfld, module.ImportReference(Field(flip, "Value")));
            il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Ldfld, module.ImportReference(Field(flip, "Value")));
            // + Alpha.Total + Mover.Total
            il.Emit(OpCodes.Call, module.ImportReference(Method(alpha, "get_Total")));
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Call, module.ImportReference(Method(mover, "get_Total")));
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);
            run.Body.MaxStackSize = 8;

            assembly.Write(ConsumerPath);
        }

        /// <summary>
        /// Three sites of the ONE <c>fieldToProperty</c> rule, in three methods:
        /// <c>Store</c> (<c>stfld</c>), <c>Read</c> (<c>ldfld</c>) and <c>Address</c>
        /// (<c>ldflda</c>, the one the pass must refuse). R35 finding 6 shape, minus seven.
        ///
        /// With <paramref name="withDriftedRow"/> the image also gets a <c>Drifted</c> method
        /// reading <c>ProjectM.Other::Value</c>. That key matches no rule when the pass collects
        /// its sites, so it is neither rewritten nor refused - and a <c>typeRename</c> of the
        /// <c>Other</c> row to <c>ProjectM.Flip</c> then turns it into a live use of the old
        /// field key in the written image. It is never executed; the test reads the report.
        /// </summary>
        public byte[] BuildFieldSites(bool withDriftedRow)
        {
            var resolver = new DirectoryResolver(OldDir);
            using var projectM = resolver.Resolve(new AssemblyNameReference("ProjectM", new Version(1, 0, 0, 0)))!;
            var flip = projectM.MainModule.GetType("ProjectM.Flip");

            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Sites", new Version(1, 0, 0, 0)), "Sites", ModuleKind.Dll);
            var module = assembly.MainModule;
            var type = new TypeDefinition("Sites", "Entry",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);

            var flipRef = module.ImportReference(flip);
            var ctor = module.ImportReference(Method(flip, ".ctor"));
            var value = module.ImportReference(Field(flip, "Value"));

            var store = new MethodDefinition("Store",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Void);
            type.Methods.Add(store);
            var sil = store.Body.GetILProcessor();
            sil.Emit(OpCodes.Newobj, ctor);
            sil.Emit(OpCodes.Ldc_I4, 41);
            sil.Emit(OpCodes.Stfld, value);
            sil.Emit(OpCodes.Ret);
            store.Body.MaxStackSize = 8;

            var read = new MethodDefinition("Read",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            type.Methods.Add(read);
            var ril = read.Body.GetILProcessor();
            ril.Emit(OpCodes.Newobj, ctor);
            ril.Emit(OpCodes.Ldfld, value);
            ril.Emit(OpCodes.Ret);
            read.Body.MaxStackSize = 8;

            // The refusal: a property has no address, so this one instruction is left alone.
            var address = new MethodDefinition("Address",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            type.Methods.Add(address);
            var ail = address.Body.GetILProcessor();
            ail.Emit(OpCodes.Newobj, ctor);
            ail.Emit(OpCodes.Ldflda, value);
            ail.Emit(OpCodes.Ldind_I4);
            ail.Emit(OpCodes.Ret);
            address.Body.MaxStackSize = 8;

            if (withDriftedRow)
            {
                var other = new TypeReference("ProjectM", "Other", module, flipRef.Scope);
                var otherValue = new FieldReference("Value", module.TypeSystem.Int32, other);
                var drifted = new MethodDefinition("Drifted",
                    Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                    module.TypeSystem.Int32);
                type.Methods.Add(drifted);
                var dil = drifted.Body.GetILProcessor();
                dil.Emit(OpCodes.Ldnull);
                dil.Emit(OpCodes.Ldfld, otherValue);
                dil.Emit(OpCodes.Ret);
                drifted.Body.MaxStackSize = 8;
            }

            using var output = new MemoryStream();
            assembly.Write(output);
            return output.ToArray();
        }

        /// <summary>
        /// An image whose only game reference is a Harmony-shaped patch registration:
        /// <c>ldtoken ProjectM.Mover; ldstr "Step"; call Register(RuntimeTypeHandle, string)</c>.
        /// The type half is rewritable and the name half is an <c>ldstr</c> nothing can follow,
        /// which is the pairing §4.6 refuses (R33 finding 4).
        /// </summary>
        public byte[] BuildPatchRegistrar()
        {
            var resolver = new DirectoryResolver(OldDir);
            using var projectM = resolver.Resolve(new AssemblyNameReference("ProjectM", new Version(1, 0, 0, 0)))!;
            var mover = projectM.MainModule.GetType("ProjectM.Mover");

            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("Registrar", new Version(1, 0, 0, 0)), "Registrar", ModuleKind.Dll);
            var module = assembly.MainModule;
            var type = new TypeDefinition("Registrar", "Entry",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
                | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(type);

            var register = new MethodDefinition("Register",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Void);
            register.Parameters.Add(new ParameterDefinition("handle", Mono.Cecil.ParameterAttributes.None,
                module.ImportReference(typeof(RuntimeTypeHandle))));
            register.Parameters.Add(new ParameterDefinition("member", Mono.Cecil.ParameterAttributes.None,
                module.TypeSystem.String));
            register.Body.GetILProcessor().Emit(OpCodes.Ret);
            register.Body.MaxStackSize = 8;
            type.Methods.Add(register);

            var patch = new MethodDefinition("Patch",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(patch);
            var il = patch.Body.GetILProcessor();
            il.Emit(OpCodes.Ldtoken, module.ImportReference(mover));
            il.Emit(OpCodes.Ldstr, "Step");
            il.Emit(OpCodes.Call, register);
            il.Emit(OpCodes.Ret);
            patch.Body.MaxStackSize = 8;

            using var output = new MemoryStream();
            assembly.Write(output);
            return output.ToArray();
        }

        /// <summary>
        /// A one-member diff: <c>ProjectM.Alpha.<paramref name="before"/>(int)</c> goes and
        /// <c>ProjectM.Alpha.<paramref name="after"/>(int)</c> arrives, with nothing else moving.
        /// The generator sees one removal and one addition of the same signature in one type —
        /// R33 finding 7's exact shape.
        /// </summary>
        public DriftDiff.Result RenameDiff(string before, string after)
        {
            string dir = NewTempDir();
            try
            {
                string oldDir = Path.Combine(dir, "old");
                string newDir = Path.Combine(dir, "new");
                Directory.CreateDirectory(oldDir);
                Directory.CreateDirectory(newDir);

                var oldMain = NewAssembly("ProjectM");
                AddCounter(oldMain, "ProjectM.Alpha", before);
                oldMain.Write(Path.Combine(oldDir, "ProjectM.dll"));

                var newMain = NewAssembly("ProjectM");
                AddCounter(newMain, "ProjectM.Alpha", after);
                newMain.Write(Path.Combine(newDir, "ProjectM.dll"));

                var notes = new List<string>();
                return DriftDiff.Compare(
                    AssemblySurface.Read(oldDir, DriftMap.GameAssemblyPrefixes, notes),
                    AssemblySurface.Read(newDir, DriftMap.GameAssemblyPrefixes, notes));
            }
            finally { Delete(dir); }
        }

        /// <summary>Load <paramref name="image"/> beside the NEW assemblies and call Entry.Run.</summary>
        public int RunConsumer(byte[] image)
        {
            var alc = new NewSetContext(NewDir);
            try
            {
                using var stream = new MemoryStream(image, writable: false);
                var assembly = alc.LoadFromStream(stream);
                var method = assembly.GetType("Consumer.Entry")!.GetMethod("Run",
                    BindingFlags.Public | BindingFlags.Static)!;
                try { return (int)method.Invoke(null, null)!; }
                catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
            }
            finally
            {
                alc.Unload();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public WeakReference LoadAndForget(byte[] image)
        {
            var alc = new NewSetContext(NewDir);
            using (var stream = new MemoryStream(image, writable: false))
            {
                var assembly = alc.LoadFromStream(stream);
                assembly.GetType("Consumer.Entry")!.GetMethod("Run",
                    BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
            }
            var weak = new WeakReference(alc);
            alc.Unload();
            return weak;
        }

        private sealed class NewSetContext : AssemblyLoadContext
        {
            private readonly string _dir;

            public NewSetContext(string dir) : base("progenitor-compat-test", isCollectible: true) => _dir = dir;

            protected override Assembly? Load(AssemblyName name)
            {
                string path = Path.Combine(_dir, (name.Name ?? "") + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }

        // ---- Cecil builders ----

        private static AssemblyDefinition NewAssembly(string name) =>
            AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);

        private static TypeDefinition NewType(AssemblyDefinition assembly, string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            var type = new TypeDefinition(
                dot > 0 ? fullName.Substring(0, dot) : "",
                dot > 0 ? fullName.Substring(dot + 1) : fullName,
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class
                | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                assembly.MainModule.TypeSystem.Object);
            assembly.MainModule.Types.Add(type);
            return type;
        }

        private static void AddMarker(AssemblyDefinition assembly, string fullName) => NewType(assembly, fullName);

        /// <summary>A type with a static <c>int Total</c>, a <c>get_Total</c>, and a void adder.</summary>
        private static void AddCounter(AssemblyDefinition assembly, string fullName, string adder)
        {
            var module = assembly.MainModule;
            var type = NewType(assembly, fullName);

            var total = new FieldDefinition("_total",
                Mono.Cecil.FieldAttributes.Private | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
            type.Fields.Add(total);

            var add = new MethodDefinition(adder,
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Void);
            add.Parameters.Add(new ParameterDefinition("n", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            var il = add.Body.GetILProcessor();
            il.Emit(OpCodes.Ldsfld, total);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stsfld, total);
            il.Emit(OpCodes.Ret);
            add.Body.MaxStackSize = 8;
            type.Methods.Add(add);

            var get = new MethodDefinition("get_Total",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            var gil = get.Body.GetILProcessor();
            gil.Emit(OpCodes.Ldsfld, total);
            gil.Emit(OpCodes.Ret);
            get.Body.MaxStackSize = 8;
            type.Methods.Add(get);
        }

        /// <summary>A class with a public instance field <c>int Value</c> and a default ctor.</summary>
        private static void AddValueField(AssemblyDefinition assembly, string fullName)
        {
            var type = NewType(assembly, fullName);
            type.Fields.Add(new FieldDefinition("Value", Mono.Cecil.FieldAttributes.Public,
                assembly.MainModule.TypeSystem.Int32));
            AddDefaultConstructor(assembly, type);
        }

        /// <summary>The same class after the flip: a property over a compiler-named backing field.</summary>
        private static void AddValueProperty(AssemblyDefinition assembly, string fullName)
        {
            var module = assembly.MainModule;
            var type = NewType(assembly, fullName);
            var backing = new FieldDefinition("<Value>k__BackingField",
                Mono.Cecil.FieldAttributes.Private, module.TypeSystem.Int32);
            type.Fields.Add(backing);

            var get = new MethodDefinition("get_Value",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.SpecialName, module.TypeSystem.Int32);
            var gil = get.Body.GetILProcessor();
            gil.Emit(OpCodes.Ldarg_0);
            gil.Emit(OpCodes.Ldfld, backing);
            gil.Emit(OpCodes.Ret);
            get.Body.MaxStackSize = 8;
            type.Methods.Add(get);

            var set = new MethodDefinition("set_Value",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.SpecialName, module.TypeSystem.Void);
            set.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            var sil = set.Body.GetILProcessor();
            sil.Emit(OpCodes.Ldarg_0);
            sil.Emit(OpCodes.Ldarg_1);
            sil.Emit(OpCodes.Stfld, backing);
            sil.Emit(OpCodes.Ret);
            set.Body.MaxStackSize = 8;
            type.Methods.Add(set);

            var property = new PropertyDefinition("Value", Mono.Cecil.PropertyAttributes.None, module.TypeSystem.Int32)
            {
                GetMethod = get,
                SetMethod = set,
            };
            type.Properties.Add(property);
            AddDefaultConstructor(assembly, type);
        }

        /// <summary>The negative control: an explicit-layout struct whose second field moves.</summary>
        private static void AddExplicitLayout(AssemblyDefinition assembly, string fullName, int offset)
        {
            var module = assembly.MainModule;
            int dot = fullName.LastIndexOf('.');
            var type = new TypeDefinition(
                fullName.Substring(0, dot), fullName.Substring(dot + 1),
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed
                | Mono.Cecil.TypeAttributes.ExplicitLayout | Mono.Cecil.TypeAttributes.SequentialLayout,
                module.ImportReference(typeof(ValueType)));
            type.Attributes &= ~Mono.Cecil.TypeAttributes.SequentialLayout;
            type.Attributes |= Mono.Cecil.TypeAttributes.ExplicitLayout;
            type.ClassSize = 16;
            type.PackingSize = 0;
            module.Types.Add(type);
            type.Fields.Add(new FieldDefinition("First", Mono.Cecil.FieldAttributes.Public, module.TypeSystem.Int32) { Offset = 0 });
            type.Fields.Add(new FieldDefinition("Second", Mono.Cecil.FieldAttributes.Public, module.TypeSystem.Int32) { Offset = offset });
        }

        private static void AddDefaultConstructor(AssemblyDefinition assembly, TypeDefinition type)
        {
            var module = assembly.MainModule;
            var ctor = new MethodDefinition(".ctor",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(
                new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true }));
            il.Emit(OpCodes.Ret);
            ctor.Body.MaxStackSize = 8;
            type.Methods.Add(ctor);
        }

        private static MethodDefinition Method(TypeDefinition type, string name) =>
            type.Methods.First(m => m.Name == name);

        private static FieldDefinition Field(TypeDefinition type, string name) =>
            type.Fields.First(f => f.Name == name);

        private sealed class DirectoryResolver : BaseAssemblyResolver
        {
            private readonly string _dir;

            public DirectoryResolver(string dir)
            {
                _dir = dir;
                AddSearchDirectory(dir);
            }

            public override AssemblyDefinition Resolve(AssemblyNameReference name)
            {
                string path = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(path))
                    return AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, AssemblyResolver = this });
                return base.Resolve(name);
            }
        }
    }
}
