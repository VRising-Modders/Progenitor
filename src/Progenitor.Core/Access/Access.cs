namespace Progenitor.Access;

/// <summary>
/// The generated access layer: typed accessors for game members the Il2CppInterop stubs do not
/// hand you directly.
///
/// Nothing is hand-written in this class. The members live in
/// <c>Access/Generated/Access.Generated.cs</c>, emitted by <c>tools/accessgen</c> from
/// <c>Access/access.txt</c>:
///
/// <code>
/// dotnet run --project tools/accessgen -- \
///   --declarations src/Progenitor.Core/Access/access.txt \
///   --out src/Progenitor.Core/Access/Generated \
///   --interop "&lt;game&gt;\BepInEx\interop"
/// </code>
///
/// The interop folder is required whenever the declarations carry a field line — it is what checks
/// the <c>ref</c> modifiers — so <c>--interop</c>, <c>VRISING_INTEROP_DIR</c> or
/// <c>VRISING_GAME_DIR</c> has to name it. Without one the tool refuses (exit 1) rather than
/// quietly emitting an unverified file; <c>--unverified</c> is the explicit opt-out and stamps
/// <c>verified: false</c> into the header, where <c>--check</c> and <c>Describe()</c> both see it.
///
/// The generated file is checked in, is plain C# (no source generator, no build-time step, no
/// bytecode transform), and is byte-identical for the same declarations. A plugin does the same
/// thing with its own <c>access.txt</c> and its own output folder — see
/// <c>docs/tutorial.md</c> "Reaching a private member".
///
/// A declared member that is not on the build that is running throws
/// <see cref="AccessException"/> at first use, naming the member; the resilience report
/// (<c>tools/resilience-report.ps1</c>) lists the same declaration ahead of time, because
/// <c>tools/accessgen</c> stamps every declaration into the assembly as an
/// <see cref="AccessMemberAttribute"/> and <c>Resilience/MetadataScan.cs</c> reads them.
/// </summary>
public static partial class Access
{
    /// <summary>
    /// Drop every resolved pointer. The cache is static state of this Core assembly, so it cannot
    /// outlive a Core generation on its own; this empties it early, before the ALC unloads.
    /// </summary>
    public static void Reset() => AccessResolver.Reset();

    // Describe() is NOT here. It is emitted into the generated half beside DeclaredCount and
    // InteropVerified, because a plugin's facade needs the same line about its own artifact and a
    // hand-written one in Core could only ever report Core's answer (R42 finding 3). Core reads
    // it the same way any consumer does: Progenitor.Access.Access.Describe().
}
