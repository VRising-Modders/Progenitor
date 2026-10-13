using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Progenitor.Ecs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-02 (client, Core swap at the title screen): <c>PrefabApplicator.UninstallQueries</c>
/// disposed an item live-query built in a client world that had already been destroyed when the
/// player left. <c>EntityQueryImpl.Dispose</c> read a null <c>_Access</c> and faulted inside
/// GameAssembly; CoreCLR turned the access violation into a fatal error (crash-dump evidence,
/// 2026-10-02). A cached query may be disposed only while its
/// own world is still created and still that world; otherwise it is forgotten.
/// </summary>
public class CachedQueryWorldTests
{
    [Fact]
    public void Release_disposes_only_while_the_owning_world_lives()
    {
        Assert.Equal(CachedQueryRelease.Dispose, WorldQueryPolicy.Release(true, 7, ownerAlive: true, ownerSeqNow: 7));
    }

    [Fact]
    public void Release_forgets_a_query_whose_world_was_destroyed()
    {
        // The crash: built in Client_0, the player left to the title, then the Core swap released it.
        Assert.Equal(CachedQueryRelease.Forget, WorldQueryPolicy.Release(true, 7, ownerAlive: false, ownerSeqNow: null));
    }

    [Fact]
    public void Release_forgets_when_the_world_identity_is_unknown_or_different()
    {
        Assert.Equal(CachedQueryRelease.Forget, WorldQueryPolicy.Release(true, null, ownerAlive: true, ownerSeqNow: 7));
        Assert.Equal(CachedQueryRelease.Forget, WorldQueryPolicy.Release(true, 7, ownerAlive: true, ownerSeqNow: null));
        Assert.Equal(CachedQueryRelease.Forget, WorldQueryPolicy.Release(true, 7, ownerAlive: true, ownerSeqNow: 8));
    }

    [Fact]
    public void Release_of_nothing_is_none()
    {
        Assert.Equal(CachedQueryRelease.None, WorldQueryPolicy.Release(false, null, ownerAlive: false, ownerSeqNow: null));
        Assert.Equal(CachedQueryRelease.None, WorldQueryPolicy.Release(false, 7, ownerAlive: true, ownerSeqNow: 7));
    }

    [Fact]
    public void Reuse_needs_the_same_readable_world()
    {
        Assert.True(WorldQueryPolicy.CanReuse(true, 7, 7));
        Assert.False(WorldQueryPolicy.CanReuse(true, 7, 8));      // rejoin: a new world
        Assert.False(WorldQueryPolicy.CanReuse(true, null, 7));
        Assert.False(WorldQueryPolicy.CanReuse(true, 7, null));
        Assert.False(WorldQueryPolicy.CanReuse(false, 7, 7));
    }

    /// <summary>Files reviewed to keep an <c>EntityQuery</c> in a field with a world-checked release.</summary>
    private static readonly HashSet<string> ReviewedQueryFields = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("Progenitor.Core", "Ecs", "WorldBoundQuery.cs"),          // the helper itself
        Path.Combine("Progenitor.Core", "Events", "EventHooks.cs"),            // ChatHook: seq identity, disposes only while IsCreated
        Path.Combine("Progenitor.Core", "Effects", "ServerBeamRuntime.cs"),    // ClientBeamPoll + WorldQueryPolicy on Shutdown
        Path.Combine("Progenitor.Dev", "Ops", "CaptureOps.cs"),                // owner world + seq checked before use and dispose
    };

    private static readonly Regex QueryField = new(
        @"^(private|internal|public|protected)\s+(static\s+)?(readonly\s+)?EntityQuery\??\s+\w+\s*(;|=)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Cached_EntityQuery_fields_go_through_WorldBoundQuery_outside_reviewed_files()
    {
        string root = Path.Combine(SourcePin.RepoRoot(), "src");
        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = path.Substring(root.Length + 1);
            string sep = Path.DirectorySeparatorChar.ToString();
            if (relative.Contains(sep + "bin" + sep, StringComparison.Ordinal)
                || relative.Contains(sep + "obj" + sep, StringComparison.Ordinal))
                continue;
            if (ReviewedQueryFields.Contains(relative)) continue;

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (QueryField.IsMatch(trimmed))
                    offenders.Add(relative + ":" + (i + 1) + ": " + trimmed);
            }
        }

        Assert.True(offenders.Count == 0,
            "Keep a cached EntityQuery in Ecs.WorldBoundQuery (disposing one after its world is destroyed "
            + "is a native crash):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void PrefabApplicator_and_combat_hold_their_queries_in_WorldBoundQuery()
    {
        string applicator = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabApplicator.cs");
        SourcePin.AssertUncommented(applicator, "new(\"PrefabApplicator buff live-query\")");
        SourcePin.AssertUncommented(applicator, "new(\"PrefabApplicator item live-query\")");
        string uninstall = SourcePin.MethodBody(applicator, "internal static void UninstallQueries()");
        SourcePin.AssertNoStatementContaining(uninstall, ".Dispose()");

        string combat = SourcePin.ReadFile("src", "Progenitor.Core", "Combat", "CombatHooks.cs");
        SourcePin.AssertUncommented(combat, "new(\"combat damage\")");
        SourcePin.AssertUncommented(combat, "new(\"combat death-event\")");
        SourcePin.AssertNoStatementContaining(combat, "Query.Dispose()");
    }

    [Fact]
    public void ServerBeam_shutdown_release_checks_the_owning_world()
    {
        string beam = SourcePin.ReadFile("src", "Progenitor.Core", "Effects", "ServerBeamRuntime.cs");
        string release = SourcePin.MethodBody(beam, "private void ReleaseClientQuery(bool disposeQuery = true)");
        SourcePin.AssertUncommented(release, "WorldQueryPolicy.Release(true, builtSeq, alive, seqNow) != CachedQueryRelease.Dispose");
    }
}
