namespace BepInEx;

/// <summary>
/// Minimal stand-in for BepInEx's <c>Paths</c> so Core's <c>ReloadAllowStore</c> — the real
/// allowlist writer an "Always" install calls (job FMAN3) — compiles in this assembly. Same idea
/// as <c>ConfigFileShims.cs</c>: the project must not reference BepInEx (see the csproj header),
/// and the FMAN3 tests want the production writer rather than a second copy of it that could
/// drift from it.
///
/// <para>Only the caching read path (<c>ReloadAllowStore.Current</c>) consults these; every write
/// takes its path as a parameter, which is why <c>TryWriteAllowRow</c> is game-free and can be
/// mirrored into Bootstrap byte for byte.</para>
/// </summary>
internal static class Paths
{
    public static string ConfigPath { get; set; } = "";

    public static string BepInExRootPath { get; set; } = "";
}
