#if !PROGENITOR_TESTS
using System;
using System.Globalization;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace Progenitor.Networking;

/// <summary>
/// Config binding for the two remote-input budgets that have no other owner: the per-sender
/// token bucket on the server receive path (<see cref="ReceiveGate"/>, threat model surface 1;
/// the server-wide backstop is derived from this value and the connected population —
/// <see cref="ReceiveGate.GlobalCapacity"/> — and is not separately configurable) and
/// the per-join cap on server-sent prefab definitions the client accepts
/// (<see cref="Progenitor.Content.PrefabSyncBudget"/>, surface 6).
///
/// Values are read once at Install and copied into plain ints — no live <c>ConfigEntry</c> is
/// held, so nothing here can pin a collectible Core generation (the Core config policy in
/// <c>Config/ConfigHelper.cs</c>). Both keys fail safe: an unreadable or non-positive value keeps
/// the compiled-in default.
/// </summary>
internal static class ReceiveLimits
{
    /// <summary>Bind now, or once the shared file resolves; the defaults hold until then.</summary>
    internal static void Bind() =>
        Config.SharedConfig.BindWhenAvailable("[Networking] receive limits", BindFrom);

    private static void BindFrom(BepInEx.Configuration.ConfigFile file)
    {
        try
        {
            var burst = Config.ConfigHelper.Bind(
                file,
                "Networking",
                "ReceiveBurstPackets",
                ReceiveGate.DefaultBurst,
                "Progenitor custom-event packets one connection may burst on the server before the per-connection rate limit drops them.");
            var perSecond = Config.ConfigHelper.Bind(
                file,
                "Networking",
                "ReceivePacketsPerSecond",
                ReceiveGate.DefaultPerSecond,
                "Steady-state Progenitor custom-event packets per second per connection on the server.");
            var prefabs = Config.ConfigHelper.Bind(
                file,
                "Networking",
                "MaxSyncedPrefabsPerJoin",
                Content.PrefabSyncBudget.DefaultMaxPerJoin,
                "Distinct prefab identities allowed per join and during server pack admission (1-16384; default 256). Set 1024 on both peers for the extended profile, or 16384 for the experimental scale profile. Native key-table capacity and packet-rate limits still apply.");

            if (burst != null && burst.Value > 0) CustomEvents.ServerReceiveGate.Burst = burst.Value;
            if (perSecond != null && perSecond.Value > 0) CustomEvents.ServerReceiveGate.PerSecond = perSecond.Value;
            if (prefabs != null && prefabs.Value > 0) Content.PrefabSyncBudget.Cap = prefabs.Value;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Networking: binding receive limits threw: " + e.Message);
        }
    }
}
#endif
