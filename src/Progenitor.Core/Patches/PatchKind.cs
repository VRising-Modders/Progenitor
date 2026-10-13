namespace Progenitor.Patches;

/// <summary>
/// Which Harmony slot a registered patch occupies. Deliberately not HarmonyLib's
/// <c>HarmonyPatchType</c>: this file is compiled into the game-free test assembly, which is
/// forbidden to reference HarmonyLib (see <c>tests/Progenitor.Tests/Progenitor.Tests.csproj</c>,
/// target <c>ForbidGameReferences</c>). <see cref="Progenitor.Patches.HarmonyPatchApplier"/> is
/// the only file that maps these onto Harmony.
/// </summary>
public enum PatchKind
{
    /// <summary>Runs before the original. A prefix returning <c>false</c> skips the original.</summary>
    Prefix = 0,

    /// <summary>Runs after the original.</summary>
    Postfix = 1,

    /// <summary>Runs after the original and after any postfix, and sees/absorbs exceptions.</summary>
    Finalizer = 2,
}
