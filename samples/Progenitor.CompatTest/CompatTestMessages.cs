using Progenitor.Networking;

namespace Progenitor.CompatTest;

// The test plugins' versioned messages. Game-free on purpose, with no using but Progenitor.Networking: the replay tests
// (tests/Progenitor.Testing.Tests/Samples/Progenitor.CompatTest) link this file as source. COMPATTEST_V2 is the 1.1.0
// build's shape (field 3); the default build (1.0.0) has fields 1 and 2.

/// <summary>The message versions of the build.</summary>
internal static class CompatTestMessages
{
#if COMPATTEST_V2
    /// <summary><see cref="Hello"/>'s version in this build: 2 adds field 3 (1.1.0).</summary>
    internal const int HelloVersion = 2;
#else
    /// <summary><see cref="Hello"/>'s version in this build: 1 (1.0.0).</summary>
    internal const int HelloVersion = 1;
#endif
}

/// <summary>The versioned test message. 1.1.0 adds field 3; 1.0.0 skips it, and 1.1.0 reads 1.0.0's message with the default.</summary>
[ModMessage("hello", Version = CompatTestMessages.HelloVersion)]
public sealed class Hello
{
    [Field(1)] public int Seq;
    [Field(2)] public string Text = "";
#if COMPATTEST_V2
    [Field(3, Since = 2)] public string Note = "(no note: the sender predates field 3)";
#endif
}

/// <summary>The second test plugin's message (<c>-p:CompatTestVariant=Other</c>).</summary>
[ModMessage("ping")]
public sealed class Ping
{
    [Field(1)] public int N;
}
