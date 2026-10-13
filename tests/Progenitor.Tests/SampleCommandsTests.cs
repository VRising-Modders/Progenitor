using System;
using System.IO;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Gating contract for the Sample's chat commands.
///
/// <c>SampleCommands.cs</c> is not compiled into this project: it calls
/// <c>InventoryUtilitiesServer</c>, <c>GameDataSystem</c> and <c>Unity.Entities</c> directly, and
/// this project must never reference the game (see <c>ForbidGameReferences</c> in the csproj).
/// The <c>[Command]</c> attribute itself is therefore read out of the source, but the assertions
/// are on the attribute's own arguments rather than on prose: a command that changes world state
/// and is not marked <c>adminOnly: true</c> fails here, whatever the file around it looks like.
/// The dispatcher's enforcement of that flag is covered by <c>CommandRegistryTests</c>.
/// </summary>
public class SampleCommandsTests
{
    /// <summary>
    /// R28 finding 27. <c>.ember</c> was the one Sample command left open by R27 finding 3's
    /// pass, and its only guard was <c>count &lt;= 0</c>: <c>.ember 2147483647</c> went straight
    /// into <c>InventoryUtilitiesServer.TryAddItem</c> from any connected non-admin. Ember Shard
    /// is a cloned Crystal — a real resource — so on any server running the Sample (the
    /// framework's shop window, loaded by operators who did not write it) an open <c>.ember</c>
    /// is unlimited item creation for every player, which a per-call clamp bounds but does not
    /// remove. Gated, and clamped as well so an admin typo cannot ask for int.MaxValue.
    ///
    /// Fails on the pre-FZ2 code: the attribute was
    /// <c>[Command("ember", usage: ".ember [count]", …)]</c> with no <c>adminOnly</c> argument
    /// and the method had no upper bound on <c>count</c>.
    /// </summary>
    [Fact]
    public void Ember_is_admin_only_and_clamps_its_count()
    {
        string attribute = CommandAttribute("ember");
        Assert.Contains("adminOnly: true", attribute, StringComparison.Ordinal);

        string body = MethodBody("public static void Ember(");
        Assert.Contains("EmberMaxPerCommand", body, StringComparison.Ordinal);
        Assert.Contains("count > EmberMaxPerCommand", body, StringComparison.Ordinal);
        Assert.Contains("count = EmberMaxPerCommand;", body, StringComparison.Ordinal);
        Assert.Contains("count <= 0", body, StringComparison.Ordinal);

        // The documented ceiling. A literal, so raising it is a deliberate edit here too.
        SourcePin.AssertStatement(Source(), "internal const int EmberMaxPerCommand = 50;");
    }

    /// <summary>
    /// The gate is the whole point of R27 finding 3 and R28 finding 27: every Sample command
    /// that writes to the world server-side carries the flag, and the read-only ones do not
    /// (so the acceptance loop still works for a plain player).
    /// </summary>
    [Theory]
    [InlineData("ember", true)]     // grants items into an inventory
    [InlineData("rat", true)]       // spawns and steers replicated units
    [InlineData("beam", true)]      // spawns replicated NetworkedSequence entities
    [InlineData("time", true)]      // moves or freezes the world clock for every player
    [InlineData("items", true)]     // grants into an inventory and equips a bag slot
    [InlineData("protect", true)]   // applies ctx.Debug protection
    [InlineData("players", true)]   // prints platform ids
    [InlineData("form", true)]      // asks Bootstrap to take a form, even when the call is refused
    [InlineData("joins", false)]    // reads the per-save counter
    [InlineData("order", false)]    // prints handler order
    [InlineData("fx", false)]       // sends a cosmetic packet to the caller's own client
    [InlineData("ui", false)]       // opens a panel on the caller's own client
    [InlineData("mods", false)]     // prints the pending catalog on the caller's own client
    public void World_writing_sample_commands_are_admin_gated(string name, bool adminOnly)
    {
        string attribute = CommandAttribute(name);
        Assert.Equal(adminOnly, attribute.Contains("adminOnly: true", StringComparison.Ordinal));
    }

    /// <summary>The text of one <c>[Command("name" … )]</c> attribute, brackets excluded.</summary>
    private static string CommandAttribute(string name)
    {
        string src = Source();
        string marker = "[Command(\"" + name + "\"";
        int start = src.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "no [Command(\"" + name + "\" …)] in SampleCommands.cs");
        int end = src.IndexOf(")]", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated [Command] attribute for '" + name + "'");
        return src.Substring(start, end - start);
    }

    /// <summary>Everything from a method signature to the next method signature.</summary>
    private static string MethodBody(string signature)
    {
        string src = Source();
        int start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no '" + signature + "' in SampleCommands.cs");
        int end = src.IndexOf("\n    [Command(", start, StringComparison.Ordinal);
        if (end < 0) end = src.Length;
        return src.Substring(start, end - start);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleCommands.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }
        throw new FileNotFoundException("samples/Progenitor.Sample/SampleCommands.cs");
    }
}
