using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.SampleTests;

/// <summary>One message a sample build sends, as its fixture records it.</summary>
internal sealed record SampleMessage(string ModVersion, object Message, string Direction);

/// <summary>
/// The samples' checked-in fixtures. They cannot be recorded live here, so a test writes them with
/// <see cref="MessageFixtures.Save{T}"/> (their sidecars say <c>source: test</c>) when the environment variable
/// <see cref="WriteVariable"/> is <c>1</c>, into the checked-in <c>Samples/&lt;sample&gt;/Fixtures</c> folder of this project,
/// only for a version that has no folder yet (a released version's fixtures are frozen and never rewritten):
/// <c>PROGENITOR_WRITE_FIXTURES=1 dotnet test tests/Progenitor.Testing.Tests --filter "FullyQualifiedName~Progenitor.SampleTests"</c>.
/// Otherwise it reads the copies in the test output and checks that the current version's fixtures are exactly
/// what today's message types encode: a changed field fails here until the fixtures are written again (and a
/// change an older peer cannot read fails the replay tests).
/// </summary>
internal static class SampleFixtures
{
    internal const string WriteVariable = "PROGENITOR_WRITE_FIXTURES";

    internal static bool Writing => Environment.GetEnvironmentVariable(WriteVariable) == "1";

    /// <summary>
    /// The fixtures of <paramref name="mod"/> for the sample whose tests live in <c>Samples/&lt;sample&gt;/</c>:
    /// written first when <see cref="Writing"/>, otherwise verified for <paramref name="currentVersion"/>.
    /// <paramref name="subfolder"/> separates two mods of one sample (<c>Fixtures/&lt;subfolder&gt;/…</c>).
    /// </summary>
    internal static MessageFixtures For(string sample, string mod, string currentVersion, IReadOnlyList<SampleMessage> sent,
        string? subfolder = null)
    {
        if (Writing)
        {
            string source = Path.Combine(RepoRoot(), "tests", "Progenitor.Testing.Tests", "Samples", sample, "Fixtures");
            if (subfolder != null) source = Path.Combine(source, subfolder);
            // A version whose folder exists is frozen: its fixtures are the baseline later builds are checked against, so
            // they are never rewritten or deleted (MessageFixtures.SaveRelease refuses). Only a version with no folder yet
            // is written. A changed message is a new version.
            foreach (var group in sent.GroupBy(m => m.ModVersion))
            {
                if (Directory.Exists(Path.Combine(source, group.Key))) continue;
                var directions = group.ToDictionary(m => m.Message, m => m.Direction, ReferenceEqualityComparer.Instance);
                MessageFixtures.SaveRelease(source, mod, group.Key, group.Select(m => m.Message), m => directions[m]);
            }

            var written = MessageFixtures.Load(source, mod);
            AssertCurrent(written, currentVersion, sent);
            return written;
        }

        string output = Path.Combine(AppContext.BaseDirectory, "Samples", sample, "Fixtures");
        if (subfolder != null) output = Path.Combine(output, subfolder);
        var fixtures = MessageFixtures.Load(output, mod);
        AssertCurrent(fixtures, currentVersion, sent);
        return fixtures;
    }

    /// <summary>The current version's fixtures are exactly the payloads today's types encode for <paramref name="sent"/>.</summary>
    private static void AssertCurrent(MessageFixtures fixtures, string currentVersion, IReadOnlyList<SampleMessage> sent)
    {
        var expected = sent
            .Where(m => m.ModVersion == currentVersion)
            .Select(m => MessageCompat.NameOf(m.Message.GetType()) + " " + Convert.ToHexString(MessageCompat.Encode(m.Message)))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var recorded = fixtures.All
            .Where(f => f.ModVersion == currentVersion)
            .Select(f => f.Message + " " + Convert.ToHexString(f.Payload))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(expected.Count > 0, "no message of " + currentVersion + " to check");
        Assert.True(expected.SequenceEqual(recorded),
            "The checked-in " + currentVersion + " fixtures differ from what today's messages encode. The fixtures of a released version are frozen: "
            + "a changed message is a new version (raise the sample's version, then run the tests with " + WriteVariable + "=1 and check the new "
            + "folder in); delete the folder by hand only if this version was never released.\nExpected:\n  " + string.Join("\n  ", expected) + "\nRecorded:\n  " + string.Join("\n  ", recorded));
    }

    /// <summary>A file of the repository (for pins on the samples' declarations).</summary>
    internal static string RepoFile(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>The repository root: the first folder above the test output that holds <c>Progenitor.sln</c>.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "Progenitor.sln not found above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
