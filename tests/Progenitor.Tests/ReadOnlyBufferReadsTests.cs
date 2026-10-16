using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>EntityManager.GetBuffer&lt;T&gt;(entity)</c> defaults to <c>isReadOnly = false</c>: every
/// call takes the buffer read-write and stamps the entity's chunk with the current change version,
/// even when the caller only reads. That made each <c>ctx.Inventory.Read</c> look like an inventory
/// write to anything comparing change versions (<c>ctx.Inventory.ChangeStamp</c>, the game's own
/// change filters). These pins keep the read-only calls read-only: same generic instantiation,
/// second argument <c>true</c>.
/// </summary>
public class ReadOnlyBufferReadsTests
{
    private static readonly Regex InventoryBufferCall = new(@"GetBuffer<InventoryBuffer>\(([^()]|\([^()]*\))*\)", RegexOptions.Compiled);

    /// <summary>
    /// Progenitor never writes <c>InventoryBuffer</c> through a <c>DynamicBuffer</c>: writes go
    /// through the game's add, remove and move. So every <c>GetBuffer&lt;InventoryBuffer&gt;</c>
    /// in the tree must pass <c>true</c>.
    /// </summary>
    [Fact]
    public void Every_InventoryBuffer_GetBuffer_is_read_only()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        int seen = 0;
        foreach (var dir in new[] { "src", "samples" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = CodePart(lines[i]);
                    foreach (Match m in InventoryBufferCall.Matches(code))
                    {
                        seen++;
                        if (!m.Value.EndsWith(", true)", StringComparison.Ordinal))
                            offenders.Add(Path.GetRelativePath(root, file) + ":" + (i + 1) + ": " + lines[i].Trim());
                    }
                }
            }
        }

        Assert.True(seen >= 12, "the scan found only " + seen + " GetBuffer<InventoryBuffer> calls; the pattern is wrong");
        Assert.True(offenders.Count == 0,
            "GetBuffer<InventoryBuffer> without isReadOnly=true bumps the chunk change version:\n" + string.Join("\n", offenders));
    }

    /// <summary>The other buffers the framework only reads, file by file.</summary>
    [Theory]
    [InlineData("src/Progenitor.Core/Abilities/AbilitySlots.cs", "em.GetBuffer<BuffBuffer>(character, true)")]
    [InlineData("src/Progenitor.Core/Ai/AiRuntime.cs", "em.GetBuffer<BuffBuffer>(unit, true)")]
    [InlineData("src/Progenitor.Core/Buffs/DebugProtection.cs", "em.GetBuffer<BuffBuffer>(character, true)")]
    [InlineData("src/Progenitor.Core/Buffs/UnityBuffRuntime.cs", "em.GetBuffer<BuffBuffer>(entity, true)")]
    [InlineData("src/Progenitor.Core/Combat/CombatMusicRepairServer.cs", "em.GetBuffer<InverseAggroBufferElement>(player, true)")]
    [InlineData("src/Progenitor.Core/Debugging/DebugBuff.cs", "em.GetBuffer<BuffBuffer>(target, true)")]
    [InlineData("src/Progenitor.Core/Units/UnitTeams.cs", "em.GetBuffer<BuffBuffer>(unit, true)")]
    [InlineData("src/Progenitor.Dev/Ops/BuffOps.cs", "em.GetBuffer<BuffBuffer>(target, true)")]
    [InlineData("src/Progenitor.Dev/Ops/HealOps.cs", "em.GetBuffer<BuffBuffer>(character, true)")]
    [InlineData("src/Progenitor.Dev/Ops/LifeOps.cs", "em.GetBuffer<BuffBuffer>(character, true)")]
    [InlineData("samples/Progenitor.NecroArmyExample/NecroCamera.cs", "em.GetBuffer<LinkedEntityGroup>(pillar, true)")]
    public void Read_only_buffer_walks_stay_read_only(string path, string call)
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), path));
        SourcePin.AssertUncommented(src, call);
        string readWrite = call.Replace(", true)", ")", StringComparison.Ordinal);
        Assert.DoesNotContain(readWrite, src, StringComparison.Ordinal);
    }

    private static string CodePart(string line)
    {
        int c = line.IndexOf("//", StringComparison.Ordinal);
        return c < 0 ? line : line.Substring(0, c);
    }

    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
