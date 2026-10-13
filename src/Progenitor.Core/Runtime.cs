using System;
using System.Reflection;
using BepInEx.Logging;
using ProjectM.Network;
using Progenitor.Content;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor;

/// <summary>
/// Static entry Bootstrap invokes after <c>LoadFromStream</c> of this assembly into a collectible
/// ALC. The shape matches <c>Progenitor.Bootstrap.ICoreRuntime</c> without a compile-time
/// reference to Bootstrap. <paramref name="host"/> is Bootstrap's <c>ICoreHost</c>; Core reads
/// <c>Log</c> and <c>ContentMemory</c> by property name.
/// </summary>
public static class Runtime
{
    private static object? _host;

    /// <summary>
    /// Install Core modules and attach to Bootstrap. Safe to call once; a second call is a
    /// no-op and does not rehydrate content memory (that would ImportProcessMemory into a
    /// generation that was never Uninstall'd).
    /// </summary>
    public static void Install(object? host)
    {
        _host = host;
        try
        {
            bool fresh = Plugin.Install(ReadLog(host));
            if (fresh)
                TryRehydrate(host);
        }
        catch (Exception e)
        {
            (ReadLog(host) ?? Plugin.Logger)?.LogError($"Progenitor.Runtime.Install threw: {e}");
            throw;
        }
    }

    /// <summary>
    /// Log every known pin candidate this generation still holds. Called by Bootstrap after
    /// plugin sweep and before ALC unload so the collect-failure dump is readable without
    /// a debugger.
    /// </summary>
    public static void DiagnosePins()
    {
        try { Plugin.DiagnosePins(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Runtime.DiagnosePins: {e.Message}");
        }
    }

    /// <summary>Reverse <see cref="Install"/>. Native receive detours stay with Bootstrap.</summary>
    public static void Uninstall()
    {
        try
        {
            SnapshotContent(_host);
            if (!Plugin.Uninstall())
                throw new InvalidOperationException(
                    "Core Uninstall refused: Hosted StunProcess.Start is in flight; retry after the child spawn finishes.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Progenitor.Runtime.Uninstall threw: {e}");
            throw;
        }
    }

    /// <summary>
    /// Copy clone / appended / tombstone sets and ItemKeyLookup indices into Bootstrap's
    /// process-lifetime store. Must run after plugin unregister (tombstones from that sweep
    /// included) and before the collectible ALC unloads. Throws on failure so the cascade
    /// aborts before Uninstall (empty memory would refuse Ember Shard as a foreign GUID).
    /// </summary>
    public static void SnapshotContent(object? host)
    {
        host ??= _host;
        PrefabRegistry.ExportProcessMemory(
            out var cloned, out var appended, out var appendedIndices,
            out var hashes, out var ids, out var tombIndices);
        if (!TryGetContentMemory(host, out var memory, out var memType) || memory == null || memType == null)
            throw new InvalidOperationException("ContentMemory missing; abort Core swap.");
        var replace = FindReplace(memType);
        if (replace == null)
            throw new InvalidOperationException("ContentMemory.Replace was not found; abort Core swap.");
        replace.Invoke(memory, BuildReplaceArgs(replace, cloned, appended, appendedIndices, hashes, ids, tombIndices));
        Plugin.Logger?.LogInfo(
            $"PrefabRegistry: snapshot cloned={cloned.Length} appended={appended.Length} tombs={hashes.Length}.");
    }

    /// <summary>
    /// Mid-session Core swap: the world already exists, there is no new deserialize and no
    /// new join packet. Open the apply gate so local re-Registers land instead of waiting
    /// 30 s (server) or forever (client). Key-table placeholders wait until plugins
    /// re-Register (<see cref="ApplyKeyPlaceholdersAfterPlugins"/>).
    /// </summary>
    public static void NotifyLiveWorldAfterCoreSwap()
    {
        try
        {
            PrefabRegistry.NotifyLiveWorldAfterCoreSwap();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Runtime.NotifyLiveWorldAfterCoreSwap threw: {e}");
        }
    }

    /// <summary>
    /// After plugins re-Require/Provide into a new Core ALC, the server re-asks connected
    /// users through the existing join handler. A client-only swap requests a fresh server
    /// list on the current connection. Same-ALC restore must not call this.
    /// </summary>
    public static void ReaskConnectedHandshake()
    {
        try
        {
            if (!Plugin.IsServerProcess)
            {
                Networking.Handshake.RequestRefreshAfterCoreSwap();
                return;
            }
            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogInfo("Handshake: no Server world; connected users are not re-asked.");
                return;
            }
            var em = world.EntityManager;
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<User>());
            var entities = query.ToEntityArray(Allocator.Temp);
            int n = 0;
            // REASK1: these PlayerConnected events are re-asks, not joins: a user who answered this session is never
            // disconnected for a missing answer (Handshake.BeginReask).
            var reask = Networking.Handshake.BeginReask();
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!em.HasComponent<User>(entity)) continue;
                    var user = em.GetComponentData<User>(entity);
                    if (!user.IsConnected) continue;
                    Events.EventBus.Publish(new Events.PlayerConnected(world, entity, user));
                    n++;
                }
            }
            finally
            {
                reask.Dispose();
                entities.Dispose();
                query.Dispose();
            }
            Plugin.Logger?.LogInfo("Handshake: re-asked " + n + " connected user(s) after a Core generation change.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Runtime.ReaskConnectedHandshake: {e.Message}");
        }
    }

    /// <summary>
    /// After plugins re-Register, append ItemKeyLookup placeholders only for tombstoned GUIDs
    /// still missing at their saved index. Called from Bootstrap after ReloadAll.
    /// </summary>
    public static void ApplyKeyPlaceholdersAfterPlugins()
    {
        try
        {
            PrefabRegistry.ApplyKeyPlaceholdersAfterPlugins();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Runtime.ApplyKeyPlaceholdersAfterPlugins threw: {e}");
        }
    }

    private static void TryRehydrate(object? host)
    {
        try
        {
            if (!TryGetContentMemory(host, out var memory, out var memType) || memory == null || memType == null)
                return;
            var take = memType.GetMethod("Take", Type.EmptyTypes)?.Invoke(memory, null);
            if (take == null) return;
            var t = take.GetType();
            var cloned = t.GetProperty("ClonedGuids")?.GetValue(take) as int[] ?? Array.Empty<int>();
            var appended = t.GetProperty("AppendedGuids")?.GetValue(take) as int[] ?? Array.Empty<int>();
            var appendedIndices = t.GetProperty("AppendedIndices")?.GetValue(take) as int[] ?? Array.Empty<int>();
            var hashes = t.GetProperty("TombstoneHashes")?.GetValue(take) as int[] ?? Array.Empty<int>();
            var ids = t.GetProperty("TombstoneIds")?.GetValue(take) as string[] ?? Array.Empty<string>();
            var tombIndices = t.GetProperty("TombstoneIndices")?.GetValue(take) as int[] ?? Array.Empty<int>();
            PrefabRegistry.ImportProcessMemory(cloned, appended, appendedIndices, hashes, ids, tombIndices);
            Plugin.Logger?.LogInfo(
                $"PrefabRegistry: rehydrated cloned={cloned.Length} appended={appended.Length} tombs={hashes.Length}.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Runtime rehydrate: {e.Message}");
        }
    }

    private static MethodInfo? FindReplace(Type memType)
    {
        MethodInfo? six = null;
        MethodInfo? four = null;
        foreach (var m in memType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!string.Equals(m.Name, "Replace", StringComparison.Ordinal)) continue;
            int n = m.GetParameters().Length;
            if (n == 6) six = m;
            else if (n == 4) four = m;
        }
        return six ?? four;
    }

    private static object?[] BuildReplaceArgs(
        MethodInfo replace,
        int[] cloned,
        int[] appended,
        int[] appendedIndices,
        int[] hashes,
        string[] ids,
        int[] tombIndices)
    {
        int n = replace.GetParameters().Length;
        if (n >= 6)
            return new object?[] { cloned, appended, appendedIndices, hashes, ids, tombIndices };
        return new object?[] { cloned, appended, hashes, ids };
    }

    /// <summary>
    /// Bootstrap ContentMemory from the host passed to <see cref="Install"/>. Fallback
    /// when PrefabRegistry's ProcessHost.Content scan misses; same object as
    /// <see cref="SnapshotContent"/>.
    /// </summary>
    internal static object? HostContentMemory()
    {
        return TryGetContentMemory(_host, out var memory, out _) ? memory : null;
    }

    private static bool TryGetContentMemory(object? host, out object? memory, out Type? memType)
    {
        memory = null;
        memType = null;
        if (host == null) return false;
        memory = host.GetType().GetProperty("ContentMemory")?.GetValue(host);
        if (memory == null) return false;
        memType = memory.GetType();
        return true;
    }

    private static ManualLogSource? ReadLog(object? host)
    {
        if (host == null) return null;
        try
        {
            return host.GetType().GetProperty("Log")?.GetValue(host) as ManualLogSource;
        }
        catch
        {
            return null;
        }
    }
}
