using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using Unity.Entities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Progenitor.Content;

/// <summary>
/// Client-only: load a plugin PNG and write it onto every
/// <c>ShowOnBuffBarData.Entries[i].Icon</c> of a cloned buff or <c>ManagedItemData.Icon</c>
/// of a cloned item. The path lives on the local
/// <see cref="PrefabDefinition"/> (same plugin DLL on the client), not on the wire.
/// Sprite + Texture2D live in Bootstrap <c>IconCache</c> (process-lifetime,
/// <c>hideFlags = HideAndDontSave</c>). This type only holds leases: <see cref="Uninstall"/>
/// drops Core-side references and never <c>Destroy</c>s a cached pair (evidence 112 / 122).
/// </summary>
internal static class BuffIconApply
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, IconLease> Leases = new();

    /// <summary>Definitions whose cache self-check already logged in this Core generation.</summary>
    private static readonly HashSet<int> Checked = new();

    /// <summary>
    /// Write the already-loaded lease sprite onto an existing instance's
    /// <c>ShowOnBuffBarData</c>. Does not decode a new PNG (that would
    /// <c>IconLease.Replace</c> and destroy the clone's sprite — evidence 112). Never
    /// removes buffers. No-op when there is no lease or no HUD data.
    /// </summary>
    public static void TryNudge(EntityManager em, Entity instance, PrefabDefinition def)
    {
        try
        {
            if (!PrefabIconPath.ShouldApply(Plugin.IsServerProcess, def.Kind, def.IconPath))
                return;
            Sprite? sprite = null;
            lock (Gate)
            {
                if (Leases.TryGetValue(def.Guid.GuidHash, out var lease) && lease != null && !lease.Disposed)
                    sprite = lease.CurrentSprite;
            }
            if (sprite == null)
            {
                string pluginId = PluginIdOf(def.Owner);
                string path = PrefabIconPath.Normalize(def.IconPath ?? "");
                if (!BootstrapLink.TryGetCachedSprite(pluginId, path, out sprite) || sprite == null)
                    return;
            }
            // Unity fake-null: writing a destroyed Sprite (or one over a destroyed texture)
            // is worse than leaving the instance alone — it is the black / white square of
            // evidence 112 / 122. Never write a dead pair onto a live HUD.
            if (!IsAlive(sprite))
            {
                Plugin.Logger?.LogWarning(ContentIconOwnership.FormatIconCacheCheck(
                    def.Id, InstanceIdOf(sprite), TextureInstanceIdOf(sprite), alive: false));
                return;
            }
            if (!BuffBarDataAccess.TryGet(em, instance, out var data, out _) || data == null)
                return;
            // Write Icon in place: never replace, clear, or re-create the Entries list — the
            // instance's managed ShowOnBuffBarData copy is the only thing the HUD reads.
            var entries = data.Entries;
            int count = entries.Count;
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                entry.Icon = sprite;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: icon nudge threw: " + e.Message);
        }
    }

    /// <summary>
    /// No-op on the server, for items, or when <see cref="PrefabDefinition.IconPath"/> is
    /// empty. Failures log one line and leave the clone applied (the haste source icon stays).
    /// </summary>
    public static void TryApply(EntityManager em, Entity clone, PrefabDefinition def)
    {
        try
        {
            if (!PrefabIconPath.ShouldApply(Plugin.IsServerProcess, def.Kind, def.IconPath))
                return;
            Apply(em, clone, def);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                PrefabIconPath.FormatFailed(def.IconPath ?? "", PrefabIconPath.DecodeFailedReason, def.Id)
                + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// Apply to the privately cloned managed item data, never the donor. Uses the same
    /// process-owned cache as buff icons so inventory sprites survive a Core swap.
    /// Missing assets or an unavailable cache leave the donor icon intact.
    /// </summary>
    public static void TryApplyItem(ManagedItemData data, PrefabDefinition def)
    {
        if (!PrefabIconPath.ShouldApplyItem(Plugin.IsServerProcess, def.Kind, def.IconPath)) return;
        string path = PrefabIconPath.Normalize(def.IconPath!);
        try
        {
            string pluginId = PluginIdOf(def.Owner);
            Sprite? sprite;
            bool fromCache = false;
            if (TryReadPng(def, path, out var png, out var reason))
            {
                if (!BootstrapLink.TryGetOrCreateSprite(PrefabIconPath.FormatCacheKey(pluginId, path, png), png, out sprite)
                    || !IsAlive(sprite))
                {
                    Plugin.Logger?.LogWarning(PrefabIconPath.FormatFailed(path, PrefabIconPath.DecodeFailedReason, def.Id));
                    return;
                }
            }
            else if (BootstrapLink.TryGetCachedSprite(pluginId, path, out sprite) && IsAlive(sprite))
                fromCache = true;
            else
            {
                Plugin.Logger?.LogWarning(PrefabIconPath.FormatFailed(path, reason, def.Id));
                return;
            }
            data.Icon = sprite;
            GetOrCreateLease(def, pluginId).Replace(sprite!, null, pluginId, cached: true);
            if (fromCache) Plugin.Logger?.LogInfo(PrefabIconPath.FormatFromCache(path, def.Id));
            Plugin.Logger?.LogInfo("PrefabRegistry: icon '" + path + "' -> ManagedItemData.Icon on '" + def.Id + "'");
            LogCacheCheck(def, sprite);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: item icon '" + path + "' on '" + def.Id + "' threw: " + e.Message);
        }
    }

    /// <summary>
    /// Drop Core-side leases only. Cached Sprite/Texture2D pairs stay in Bootstrap
    /// <c>IconCache</c> so the next generation can apply-from-cache before the plugin
    /// file reloads. Called from <see cref="PrefabRegistry.Uninstall"/>.
    /// </summary>
    public static void Uninstall()
    {
        IconLease[] snapshot;
        lock (Gate)
        {
            snapshot = new IconLease[Leases.Count];
            Leases.Values.CopyTo(snapshot, 0);
            Leases.Clear();
            // Next generation's first apply logs the cache self-check again.
            Checked.Clear();
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].ReleaseReference(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("PrefabRegistry: icon lease uninstall threw: " + e.Message); }
        }
    }

    private static void Apply(EntityManager em, Entity clone, PrefabDefinition def)
    {
        string path = PrefabIconPath.Normalize(def.IconPath!);
        if (!BuffBarDataAccess.TryGet(em, clone, out var data, out var missReason) || data == null)
        {
            Plugin.Logger?.LogWarning(PrefabIconPath.FormatFailed(path,
                string.IsNullOrEmpty(missReason) ? PrefabIconPath.NoBuffBarDataReason : missReason,
                def.Id));
            return;
        }

        var entries = data.Entries;
        string pluginId = PluginIdOf(def.Owner);

        Sprite? sprite = null;
        Texture2D? tex = null;
        bool cached = false;
        bool fromCache = false;

        if (TryReadPng(def, path, out var png, out var readReason))
        {
            string key = PrefabIconPath.FormatCacheKey(pluginId, path, png);
            if (BootstrapLink.TryGetOrCreateSprite(key, png, out sprite) && sprite != null)
            {
                cached = true;
            }
            else if (!TryDecodeSprite(png, path, out sprite, out tex) || sprite == null)
            {
                DestroyUnity(sprite);
                DestroyUnity(tex);
                Plugin.Logger?.LogWarning(PrefabIconPath.FormatFailed(path, PrefabIconPath.DecodeFailedReason, def.Id));
                return;
            }
        }
        else if (BootstrapLink.TryGetCachedSprite(pluginId, path, out sprite) && sprite != null)
        {
            cached = true;
            fromCache = true;
        }
        else
        {
            Plugin.Logger?.LogWarning(PrefabIconPath.FormatFailed(path, readReason, def.Id));
            return;
        }

        int written = 0;
        try
        {
            int count = entries.Count;
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                entry.Icon = sprite;
                written++;
            }
        }
        catch (Exception e)
        {
            if (!cached)
            {
                DestroyUnity(sprite);
                DestroyUnity(tex);
            }
            Plugin.Logger?.LogWarning(
                PrefabIconPath.FormatFailed(path, PrefabIconPath.NoBuffBarDataReason, def.Id)
                + " threw: " + e.Message);
            return;
        }

        if (written == 0)
        {
            if (!cached)
            {
                DestroyUnity(sprite);
                DestroyUnity(tex);
            }
            Plugin.Logger?.LogWarning(PrefabIconPath.FormatFailed(path, PrefabIconPath.NoBuffBarDataReason, def.Id));
            return;
        }

        var lease = GetOrCreateLease(def, pluginId);
        lease.Replace(sprite, tex, pluginId, cached);
        if (fromCache)
            Plugin.Logger?.LogInfo(PrefabIconPath.FormatFromCache(path, def.Id));
        Plugin.Logger?.LogInfo(PrefabIconPath.FormatApplied(path, written, def.Id));
        LogCacheCheck(def, sprite);
        PrefabLiveRefreshApply.AfterIconApplied(em, clone, def);
    }

    /// <summary>
    /// One <c>PrefabRegistry: icon cache check '&lt;id&gt;' sprite=N texture=N alive=B</c> per
    /// definition per Core generation (<see cref="Uninstall"/> clears the set, so the first
    /// apply after a swap logs it again). Compare the two ids across generations: they must
    /// not move, or every live instance is drawing a sprite the cache has replaced.
    /// </summary>
    private static void LogCacheCheck(PrefabDefinition def, Sprite? sprite)
    {
        try
        {
            lock (Gate)
            {
                if (!Checked.Add(def.Guid.GuidHash)) return;
            }

            bool alive = IsAlive(sprite);
            string line = ContentIconOwnership.FormatIconCacheCheck(
                def.Id, InstanceIdOf(sprite), TextureInstanceIdOf(sprite), alive);
            if (alive) Plugin.Logger?.LogDiag(line);
            else Plugin.Logger?.LogWarning(line);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: icon cache check threw: " + e.Message);
        }
    }

    /// <summary>Unity fake-null on the Sprite AND its Texture2D (a live sprite over a dead texture is the white square).</summary>
    private static bool IsAlive(Sprite? sprite)
    {
        try
        {
            if (sprite == null) return false;
            var tex = sprite.texture;
            return tex != null;
        }
        catch { return false; }
    }

    private static int InstanceIdOf(Object? obj)
    {
        try { return obj == null ? 0 : obj.GetInstanceID(); }
        catch { return 0; }
    }

    private static int TextureInstanceIdOf(Sprite? sprite)
    {
        try { return sprite == null ? 0 : InstanceIdOf(sprite.texture); }
        catch { return 0; }
    }

    private static bool TryReadPng(PrefabDefinition def, string path, out byte[] png, out string reason)
    {
        png = Array.Empty<byte>();
        reason = PrefabIconPath.FileMissingReason;
        Assembly? asm = null;
        try { asm = def.Owner?.GetType().Assembly; }
        catch { /* */ }
        if (asm == null)
        {
            reason = PrefabIconPath.AssetRootMissingReason;
            return false;
        }

        if (asm == typeof(GuideOutfit).Assembly)
        {
            try
            {
                // Core-owned definitions have no assets folder: their icons are embedded resources,
                // the Guide outfit's under progenitor.guide.*, every other Core clone's (the debug
                // buff) under progenitor.content.*.
                string logical = path.Replace('/', '.');
                using var resource = asm.GetManifestResourceStream("progenitor.guide." + logical)
                    ?? asm.GetManifestResourceStream("progenitor.content." + logical);
                if (resource != null)
                {
                    using var bytes = new MemoryStream();
                    resource.CopyTo(bytes);
                    png = bytes.ToArray();
                    return png.Length > 0;
                }
            }
            catch (Exception e)
            {
                reason = PrefabIconPath.FileMissingReason + " (embedded icon: " + e.Message + ")";
                return false;
            }
        }

        string? root;
        try { root = PluginAssetRoot.Resolve(asm); }
        catch (Exception e)
        {
            reason = PrefabIconPath.AssetRootMissingReason + " (" + e.Message + ")";
            return false;
        }
        if (string.IsNullOrEmpty(root))
        {
            reason = PrefabIconPath.AssetRootMissingReason;
            return false;
        }

        if (!PrefabIconPath.TryCombine(root, path, out var full, out var combineError))
        {
            reason = string.IsNullOrEmpty(combineError) ? PrefabIconPath.FileMissingReason : combineError;
            return false;
        }
        if (!File.Exists(full))
        {
            reason = PrefabIconPath.FileMissingReason;
            return false;
        }

        try
        {
            png = File.ReadAllBytes(full);
            return png.Length > 0;
        }
        catch (Exception e)
        {
            reason = PrefabIconPath.FileMissingReason + " (" + e.Message + ")";
            return false;
        }
    }

    /// <summary>
    /// PNG bytes → Texture2D via <c>ImageConversion.LoadImage</c>. The interop takes an
    /// Il2Cpp byte array (managed <c>byte[]</c> is the wrong object header); copy into
    /// <see cref="Il2CppStructArray{T}"/> the same way other Core code hands native APIs a
    /// blittable buffer. Pivot is center (0.5, 0.5); rect is the full decoded size.
    /// </summary>
    private static bool TryDecodeSprite(byte[] png, string path, out Sprite? sprite, out Texture2D? tex)
    {
        sprite = null;
        tex = null;
        Texture2D? created = null;
        try
        {
            created = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "progenitor-icon-" + Path.GetFileNameWithoutExtension(path),
                hideFlags = HideFlags.HideAndDontSave,
            };
            var native = new Il2CppStructArray<byte>(png.Length);
            for (int i = 0; i < png.Length; i++)
                native[i] = png[i];
            if (!ImageConversion.LoadImage(created, native))
            {
                DestroyUnity(created);
                return false;
            }

            var rect = new Rect(0f, 0f, created.width, created.height);
            var pivot = new Vector2(0.5f, 0.5f);
            var createdSprite = Sprite.Create(created, rect, pivot);
            if (createdSprite == null)
            {
                DestroyUnity(created);
                return false;
            }
            createdSprite.name = created.name;
            createdSprite.hideFlags = HideFlags.HideAndDontSave;
            tex = created;
            sprite = createdSprite;
            return true;
        }
        catch (Exception e)
        {
            DestroyUnity(sprite);
            DestroyUnity(created);
            sprite = null;
            tex = null;
            Plugin.Logger?.LogWarning("PrefabRegistry: icon decode threw: " + e.Message);
            return false;
        }
    }

    private static IconLease GetOrCreateLease(PrefabDefinition def, string pluginId)
    {
        int hash = def.Guid.GuidHash;
        lock (Gate)
        {
            if (Leases.TryGetValue(hash, out var existing) && existing != null && !existing.Disposed)
                return existing;
            var lease = new IconLease(hash, pluginId);
            Leases[hash] = lease;
            def.RecordUndo(lease);
            return lease;
        }
    }

    private static string PluginIdOf(object? owner)
    {
        if (owner == null) return ContentIconOwnership.UnownedId;
        try
        {
            if (owner is BasePlugin plugin)
            {
                var meta = MetadataHelper.GetMetadata(plugin);
                if (meta != null && !string.IsNullOrEmpty(meta.GUID))
                    return ContentIconOwnership.OwnerId(meta.GUID, null);
            }
        }
        catch { /* */ }

        try
        {
            var attr = owner.GetType().GetCustomAttribute<BepInPlugin>(inherit: true);
            if (attr != null && !string.IsNullOrEmpty(attr.GUID))
                return ContentIconOwnership.OwnerId(attr.GUID, null);
        }
        catch { /* */ }

        string? assemblyName = null;
        try { assemblyName = owner.GetType().Assembly.GetName().Name; }
        catch { /* */ }
        return ContentIconOwnership.OwnerId(null, assemblyName);
    }

    private static void Forget(int guidHash, IconLease lease)
    {
        lock (Gate)
        {
            if (Leases.TryGetValue(guidHash, out var current) && ReferenceEquals(current, lease))
                Leases.Remove(guidHash);
        }
    }

    private static void DestroyUnity(Object? obj)
    {
        if (obj == null) return;
        try { Object.DestroyImmediate(obj); }
        catch
        {
            try { Object.Destroy(obj); }
            catch { /* */ }
        }
    }

    /// <summary>
    /// One definition's Sprite reference. Cached pairs are owned by Bootstrap
    /// <c>IconCache</c>; this lease must not <c>Destroy</c> them. Fallback-decoded
    /// sprites (Bootstrap absent) are still destroyed on Dispose so tests without
    /// the cache do not leak.
    /// </summary>
    private sealed class IconLease : IDisposable
    {
        private readonly int _guidHash;
        private Sprite? _sprite;
        private Texture2D? _tex;
        private bool _cached;
        private int _disposed;

        public IconLease(int guidHash, string pluginId)
        {
            _guidHash = guidHash;
            _ = pluginId;
        }

        public bool Disposed => _disposed != 0;

        public Sprite? CurrentSprite => _sprite;

        public void Replace(Sprite sprite, Texture2D? tex, string pluginId, bool cached)
        {
            if (Disposed)
            {
                if (!cached)
                {
                    DestroyUnity(sprite);
                    DestroyUnity(tex);
                }
                return;
            }

            var previousSprite = _sprite;
            var previousTex = _tex;
            bool previousCached = _cached;
            _sprite = sprite;
            _tex = tex;
            _ = pluginId;
            _cached = cached;
            if (!previousCached)
            {
                DestroyUnity(previousSprite);
                if (!ReferenceEquals(previousTex, tex))
                    DestroyUnity(previousTex);
            }
        }

        /// <summary>Core Uninstall: drop the reference, never Destroy a cached pair.</summary>
        public void ReleaseReference()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Forget(_guidHash, this);
            _sprite = null;
            _tex = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Forget(_guidHash, this);
            if (!_cached)
            {
                DestroyUnity(_sprite);
                DestroyUnity(_tex);
            }
            _sprite = null;
            _tex = null;
        }
    }
}
