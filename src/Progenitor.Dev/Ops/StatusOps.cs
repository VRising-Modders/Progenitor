using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Progenitor.Content;
using Progenitor.Hosted;
using Progenitor.Persistence;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Read-only (and one write) debug-bridge ops over Core modules that do not need an entity id:
/// framework status, a forced <see cref="ModSave.SaveAll"/>, and the per-mod JSON store.
/// The bridge already marshals these onto the main thread.
/// </summary>
public static class StatusOps
{
    /// <summary>
    /// Snapshot of Core version, process side, uptime, and one-line module dumps. Prefab and
    /// hosted <c>Describe</c> are the diagnostic strings those modules already expose; persistence
    /// has no public store list, so this is the current save directory only.
    /// </summary>
    public static object Status()
    {
        try
        {
            return new
            {
                version = Progenitor.Plugin.Version,
                side = Progenitor.Plugin.IsServerProcess ? "server" : "client",
                uptimeSeconds = (int)(DateTime.UtcNow - Progenitor.Plugin.StartedUtc).TotalSeconds,
                modules = new
                {
                    prefabs = DescribePrefabs(),
                    hosted = DescribeHosted(),
                    persistence = DescribePersistence(),
                    systems = SafeDescribe(() => Progenitor.Systems.VirtualSystems.Describe()),
                },
            };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"status op threw: {e}");
            throw;
        }
    }

    /// <summary>
    /// Server only. Flushes every in-memory <see cref="ModSave"/> store to disk the same way the
    /// game's save trigger does, without waiting for autosave. Returns the directory written to
    /// (null when the save folder has not been resolved yet).
    /// </summary>
    public static object Save()
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("save runs on the server side only");
        try
        {
            ModSave.SaveAll();
            return new { ok = true, dir = ModSave.CurrentSaveDirectory };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"save op threw: {e}");
            throw;
        }
    }

    /// <summary>
    /// Peek or poke one mod's <see cref="IModStore"/>. No <c>key</c> lists keys; <c>key</c> without
    /// <c>value</c> returns the JSON node (or null if missing); both set and return <c>ok</c>.
    /// </summary>
    public static object Store(JsonElement args)
    {
        try
        {
            var mod = GetString(args, "mod") ?? throw new ArgumentException("args.mod is required");
            var key = GetString(args, "key");
            bool hasValue = TryGetProperty(args, "value", out var valueEl);

            if (hasValue && string.IsNullOrEmpty(key))
                throw new ArgumentException("args.key is required when value is set");

            // "scope": "server" is the wipe-surviving ServerSave; anything else is the world store (ModSave).
            bool server = string.Equals(GetString(args, "scope"), "server", StringComparison.OrdinalIgnoreCase);
            var store = server ? ServerSave.Store(mod) : ModSave.Store(mod);

            if (string.IsNullOrEmpty(key))
                return new { mod, scope = server ? "server" : "world", keys = store.Keys };

            if (!hasValue)
            {
                var node = store.Get<JsonNode>(key);
                if (node != null)
                    return new { mod, key, value = node };
                var raw = store.Get<string>(key);
                return new { mod, key, value = (object?)raw };
            }

            store.Set(key, ToNode(valueEl));
            return new { ok = true, mod, scope = server ? "server" : "world", key };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"store op threw: {e}");
            throw;
        }
    }

    // ---- module dumps -------------------------------------------------------------------------

    private static string SafeDescribe(Func<string> f)
    {
        try { return f() ?? ""; }
        catch (Exception e) { return $"(describe failed: {e.Message})"; }
    }

    private static string DescribePrefabs()
    {
        try
        {
            // Registry summary (registered + tombstones), then Describe(world, guid) per item when a world exists.
            var defs = PrefabRegistry.Definitions;
            var world = Progenitor.Plugin.IsServerProcess ? Progenitor.Worlds.Server : Progenitor.Worlds.Client;
            var sb = new StringBuilder();
            sb.Append(PrefabRegistry.Describe());
            if (world == null || !world.IsCreated)
            {
                foreach (var d in defs)
                {
                    sb.Append("; ").Append(d.Id)
                        .Append(" guid=").Append(d.Guid.GuidHash)
                        .Append(" source=").Append(d.SourceName ?? d.Source.GuidHash.ToString());
                }
                sb.Append(" (no world)");
                return sb.ToString();
            }

            foreach (var d in defs)
            {
                sb.Append("; ");
                sb.Append(PrefabRegistry.Describe(world, d.Guid));
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"PrefabRegistry.Describe threw: {e.Message}";
        }
    }

    private static string DescribeHosted()
    {
        try
        {
            return HostedServerLauncher.Describe();
        }
        catch (Exception e)
        {
            return $"HostedServerLauncher.Describe threw: {e.Message}";
        }
    }

    private static string DescribePersistence()
    {
        try
        {
            var dir = ModSave.CurrentSaveDirectory;
            return dir ?? "(directory not resolved yet)";
        }
        catch (Exception e)
        {
            return $"ModSave.CurrentSaveDirectory threw: {e.Message}";
        }
    }

    // ---- args ---------------------------------------------------------------------------------

    private static string? GetString(JsonElement args, string name)
    {
        if (!TryGetProperty(args, name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        try { return el.GetString(); }
        catch { return null; }
    }

    private static bool TryGetProperty(JsonElement args, string name, out JsonElement el)
    {
        el = default;
        if (args.ValueKind != JsonValueKind.Object) return false;
        return args.TryGetProperty(name, out el) && el.ValueKind != JsonValueKind.Undefined;
    }

    private static JsonNode? ToNode(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        try
        {
            return JsonNode.Parse(el.GetRawText());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"store value is not JSON: {e.Message}");
            return el.ValueKind == JsonValueKind.String ? JsonValue.Create(el.GetString()) : JsonValue.Create(el.GetRawText());
        }
    }
}
