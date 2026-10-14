#if !PROGENITOR_TESTS
namespace Progenitor.Materials;

/// <summary>
/// Binds Core's availability and payment handlers to Bootstrap's opt-in material seams.
/// Registration layer only in this revision: no seam is bound, so <see cref="HookState"/> reports
/// the seams as not installed and the game's own rules apply.
/// </summary>
internal static class MaterialSeamRuntime
{
    private static MaterialSourceRegistry? _registry;

    internal static (bool installed, string reason) HookState() =>
        (false, "native seams not bound in this Core");

    internal static void Install(MaterialSourceRegistry registry) => _registry = registry;

    internal static void Uninstall() => _registry = null;

    internal static void OnRegistrationChanged() { }
}
#endif
