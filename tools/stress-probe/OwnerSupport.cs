#if STRESS_OWNERS
// Harness-only adapters for the linked Dev owner helper. These do not replace loaded Core/Dev.
namespace Progenitor.Dev.Ops;

internal static class Plugin
{
    internal static BepInEx.Logging.ManualLogSource? Logger;
}

internal static class Layout
{
    // Same constants as the reviewed owner-helper branch and matching server dump.
    internal const int TeamReferenceValueOffset = 0;
    internal const int ModifiableEntityValueOffset = 0;
    internal const int UserTeamUserEntityOffset = 0;
    internal const int ServerRootPrefabCollectionPlayerTeamPrefabGuidOffset = 0x70;
    internal const int TeamAlliesStride = 8;
    internal const int TeamAlliesValueOffset = 0;
}

internal static class ExtraInstanceAuth
{
    internal static bool IsOwnPlatformId(ulong id) => id >= 90000000000020000UL && id <= 90000000000020099UL;
}

#endif
