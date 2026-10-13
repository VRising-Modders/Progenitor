using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime Sprite + Texture2D table. Bootstrap is non-collectible, so these
/// Unity objects outlive a Core ALC swap; Core only holds leases (no <c>Destroy</c> on
/// Uninstall). A pair is destroyed together only on plugin-file removal
/// (<see cref="ReleasePlugin"/>) or process exit (<see cref="ClearAll"/>).
///
/// Key = plugin id + normalized relative path + SHA-256 of the PNG bytes
/// (<see cref="IconCachePolicy.FormatKey"/>). When the plugin asset root is missing
/// at re-apply, Core looks up by plugin+path and the stored bytes rebuild a dead
/// texture (evidence 112 / 122: black then white squares after a client Core swap).
/// </summary>
public static class IconCache
{
    private static readonly object Gate = new();
    private static readonly IconCacheTable Table = new();
    private static readonly Dictionary<string, Live> Unity = new(StringComparer.Ordinal);

    /// <summary>
    /// Pairs superseded by a Replace while a live clone still referenced them. They are kept
    /// alive (never destroyed under a HUD that is drawing them), and now they also leave:
    /// <see cref="Release"/> destroys one as soon as its last reference goes, and the table
    /// caps itself at <see cref="IconCachePolicy.MaxRetiredPairs"/> by destroying the oldest
    /// rows <em>that nothing references</em>; if every row is still referenced it exceeds the
    /// cap and logs once rather than destroying a sprite under the HUD (R28 finding 4a).
    /// Before FZ this was a plain list with no exit but <see cref="ClearAll"/>, so every save
    /// of an icon PNG (new SHA-256 → new key → Replace) leaked one more
    /// <c>HideAndDontSave</c> Sprite+Texture2D pair for the life of the process
    /// (R27 finding 14a). Skipping referenced rows then made the cap a report rather than a
    /// bound, because in a dev loop every row is referenced; the bound now comes from
    /// <see cref="RetiredIconTable.RetireReplaced"/> handing the superseded holder on, so one
    /// icon re-saved N times holds one retired row rather than N (R29 finding 10).
    /// </summary>
    private static readonly RetiredIconTable Retired = new();

    private struct Live
    {
        public Sprite Sprite;
        public Texture2D Texture;
    }

    /// <summary>How many cached pairs are in this process. Pin-diag greps this via <see cref="DescribePins"/>.</summary>
    public static int Count
    {
        get { lock (Gate) return Table.Count; }
    }

    /// <summary>Log that the table is live. Called once from Bootstrap <c>Plugin.Load</c>.</summary>
    public static void Install() =>
        ProcessHost.Log?.LogInfo("IconCache: ready (process-lifetime Sprite/Texture2D table).");

    /// <summary>How many superseded pairs are still held alive for live clones.</summary>
    public static int RetiredCount
    {
        get { lock (Gate) return Retired.Count; }
    }

    /// <summary><c>pin-diag IconCache count=N retired=M</c>.</summary>
    public static string DescribePins()
    {
        lock (Gate) return IconCachePolicy.FormatPinDiag(Table.Count, Retired.Count);
    }

    /// <summary>
    /// Return a live Sprite for <paramref name="key"/>, creating the Sprite+Texture2D pair
    /// from <paramref name="bytes"/> on miss. Recreates when the cached texture is Unity-null.
    /// </summary>
    public static bool TryGetOrCreate(string key, byte[] bytes, out Sprite? sprite)
    {
        sprite = null;
        try
        {
            if (!IconCachePolicy.TryParseKey(key, out var pluginId, out var path, out _))
                return false;
            if (!IconCachePolicy.CanCreate(bytes))
                return false;

            lock (Gate)
            {
                var resolve = Table.TryGetOrCreate(pluginId, path, bytes, out var canonical);
                if (resolve == IconCachePolicy.Resolve.Miss)
                    return false;

                // The ONLY retire seam in this type. LastReplacedKey is set exactly when a new
                // PNG for the same plugin+path hashed to a different canonical key, so retiring
                // is a replacement rule and never a generation rule: a Core swap re-applying an
                // unchanged PNG resolves to Hit / Recreate, LastReplacedKey stays null, and the
                // same Sprite and Texture2D objects are handed to the new generation
                // (IconCachePolicy.ShouldRetireOnReplace, R29 finding 10).
                if (!string.IsNullOrEmpty(Table.LastReplacedKey))
                {
                    // Never destroy a pair a live clone / buff instance is still drawing:
                    // that is the black square of evidence 112 / 131. Retire it instead.
                    if (IconCachePolicy.ShouldDestroyReplaced(Table.LastReplacedRefs))
                    {
                        DestroyPair(Table.LastReplacedKey);
                    }
                    else if (IconCachePolicy.ShouldRetireOnReplace(liveRowReplaced: true, Table.LastReplacedRefs))
                    {
                        RetirePair(Table.LastReplacedKey, Table.LastReplacedRefs);
                        ProcessHost.Log?.LogInfo(
                            IconCachePolicy.FormatReplacedKept(Table.LastReplacedKey!, Table.LastReplacedRefs));
                        SweepRetiredNoLock();
                    }
                }

                // Identity rule: for one canonical key the Sprite and Texture2D objects are
                // the same across every Core generation. Any resolve but Recreate reuses the
                // live pair, so a Create after a row was dropped never mints a second sprite
                // over a HUD that still points at the first.
                if (resolve != IconCachePolicy.Resolve.Recreate
                    && Unity.TryGetValue(canonical, out var hit)
                    && !IsUnityDead(hit.Sprite)
                    && !IsUnityDead(hit.Texture))
                {
                    if (Table.RefsOf(canonical) == 0)
                        Table.Retain(canonical);
                    sprite = hit.Sprite;
                    return true;
                }

                if ((resolve == IconCachePolicy.Resolve.Hit || resolve == IconCachePolicy.Resolve.Recreate)
                    && Unity.TryGetValue(canonical, out var stale)
                    && (IsUnityDead(stale.Sprite) || IsUnityDead(stale.Texture)))
                {
                    ProcessHost.Log?.LogWarning(IconCachePolicy.FormatDeadRecreated(canonical));
                    DestroyPair(canonical);
                    resolve = IconCachePolicy.Resolve.Recreate;
                }

                if (!TryCreatePair(bytes, path, out var createdSprite, out var createdTex)
                    || createdSprite == null
                    || createdTex == null)
                {
                    Table.Release(canonical);
                    return false;
                }

                Unity[canonical] = new Live { Sprite = createdSprite, Texture = createdTex };
                if (Table.RefsOf(canonical) == 0)
                    Table.Retain(canonical);
                sprite = createdSprite;
                _ = resolve;
                return true;
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("IconCache.TryGetOrCreate: " + e.Message);
            sprite = null;
            return false;
        }
    }

    /// <summary>
    /// Lookup by plugin + path when PNG bytes are not available (asset root missing).
    /// Recreates from stored bytes when the texture is dead.
    /// </summary>
    public static bool TryGet(string pluginId, string relativePath, out Sprite? sprite)
    {
        sprite = null;
        try
        {
            lock (Gate)
            {
                if (!Table.TryGetWithoutBytes(pluginId, relativePath, out var key, out var bytes, out _))
                    return false;

                if (Unity.TryGetValue(key, out var live)
                    && !IsUnityDead(live.Sprite)
                    && !IsUnityDead(live.Texture))
                {
                    if (Table.RefsOf(key) == 0)
                        Table.Retain(key);
                    sprite = live.Sprite;
                    return true;
                }

                if (!IconCachePolicy.CanCreate(bytes))
                    return false;

                ProcessHost.Log?.LogWarning(IconCachePolicy.FormatDeadRecreated(key));
                DestroyPair(key);
                if (!TryCreatePair(bytes, IconCachePolicy.NormalizePath(relativePath), out var createdSprite, out var createdTex)
                    || createdSprite == null
                    || createdTex == null)
                    return false;

                Unity[key] = new Live { Sprite = createdSprite, Texture = createdTex };
                if (Table.RefsOf(key) == 0)
                    Table.Retain(key);
                sprite = createdSprite;
                return true;
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("IconCache.TryGet: " + e.Message);
            sprite = null;
            return false;
        }
    }

    /// <summary>
    /// Drop one Content retain. Destroys the pair only when the row is pending-destroy
    /// (plugin file already removed) and no clone still holds it. A key that names a
    /// <em>retired</em> pair (superseded by a Replace while a clone was drawing it) is
    /// released there instead: that is the refcount exit R27 finding 14a asked for.
    /// </summary>
    public static bool Release(string key)
    {
        try
        {
            lock (Gate)
            {
                // A key can be live *and* retired at once (revert an icon PNG to bytes that were
                // already cached: the canonical key comes back while the earlier pair is still
                // retired under it). One Content release is one reference, so it must land on
                // exactly one of the two rows — the live one wins, and the retired row drains
                // only once no live row owns the key (R28 finding 4b).
                bool liveRow = Table.Contains(key);
                bool dropped = Table.ReleaseRef(key);
                if (dropped)
                {
                    DestroyPair(key);
                    return true;
                }

                if (!liveRow && Retired.ReleaseRef(key, out var freed))
                {
                    DestroyRetired(freed);
                    ProcessHost.Log?.LogInfo(IconCachePolicy.FormatRetiredFreed(key));
                    return true;
                }
                return false;
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("IconCache.Release: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Content apply: the clone now references this cached pair. No-op when the key is missing.
    /// </summary>
    public static bool Retain(string key)
    {
        try
        {
            lock (Gate) return Table.Retain(key);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("IconCache.Retain: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// File-removed: destroy unreferenced pairs; keep pairs a live clone still holds
    /// (pending destroy until Content <see cref="Release"/>s the last retain). Not called
    /// on a Core-swap Unload.
    /// </summary>
    public static int ReleasePlugin(string pluginId)
    {
        try
        {
            lock (Gate)
            {
                string id = IconCachePolicy.NormalizePluginId(pluginId);
                var keys = new List<string>();
                foreach (var kv in Unity)
                {
                    if (!IconCachePolicy.TryParseKey(kv.Key, out var parsed, out _, out _))
                        continue;
                    // Normalized both sides: a file removal reported with different casing used
                    // to match nothing here and mark nothing pending (R27 finding 14b).
                    if (string.Equals(IconCachePolicy.NormalizePluginId(parsed), id, StringComparison.Ordinal))
                        keys.Add(kv.Key);
                }

                int dropped = 0;
                for (int i = 0; i < keys.Count; i++)
                {
                    if (IconCachePolicy.ShouldDestroyOnPluginRelease(Table.RefsOf(keys[i])))
                    {
                        DestroyPair(keys[i]);
                        dropped++;
                    }
                }

                Table.ReleasePlugin(pluginId);
                return dropped;
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("IconCache.ReleasePlugin: " + e.Message);
            return 0;
        }
    }

    /// <summary>Destroy every pair. Process exit / Bootstrap Unload.</summary>
    public static void ClearAll()
    {
        try
        {
            lock (Gate)
            {
                var keys = new List<string>(Unity.Keys);
                for (int i = 0; i < keys.Count; i++)
                    DestroyPair(keys[i]);
                DestroyRetired(Retired.TakeAll());
                Table.Clear();
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("IconCache.ClearAll: " + e.Message);
        }
    }

    private static bool TryCreatePair(byte[] png, string path, out Sprite? sprite, out Texture2D? tex)
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
            ProcessHost.Log?.LogWarning("IconCache: decode threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Move a pair out of the key table without destroying it. Used when a Replace supersedes
    /// a pair a live clone still references. <paramref name="refs"/> is the retain count the
    /// row had when it was dropped, so <see cref="Release"/> can destroy the pair once the
    /// last of those references goes away.
    ///
    /// The replacement also hands the superseded holder on: whatever was drawing the pair
    /// retired by the <em>previous</em> replacement of this plugin+path is exactly what is now
    /// being re-pointed at the new sprite, so that row drops one reference and is destroyed the
    /// moment its count reaches zero. Without it the retired table was a per-replacement
    /// accumulator whose rows nothing ever released — the ceiling
    /// (<see cref="IconCachePolicy.MaxRetiredPairs"/>) could free nothing because every row was
    /// still "referenced" by a Core generation that had unloaded (R29 finding 10).
    /// </summary>
    private static void RetirePair(string? key, int refs)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!Unity.TryGetValue(key!, out var live)) return;
        Unity.Remove(key!);
        string index = IconCachePolicy.TryParseKey(key, out var pluginId, out var path, out _)
            ? IconCachePolicy.PluginPathIndex(pluginId, path)
            : "";
        bool retired = Retired.RetireReplaced(index, key, refs, live, out var freed, out bool handedOn);
        if (freed.Count > 0)
            DestroyRetired(freed);
        if (handedOn)
            ProcessHost.Log?.LogInfo(IconCachePolicy.FormatRetiredHandedOn(index, freed.Count > 0));
        if (retired) return;
        // Nothing referenced it after all: destroy rather than park it forever.
        DestroyUnity(live.Sprite);
        DestroyUnity(live.Texture);
    }

    /// <summary>
    /// Enforce the retired-list cap. Only rows nothing references any more are destroyed: a
    /// retired row exists precisely because a live clone was drawing that pair, so popping the
    /// oldest regardless of its refcount destroyed a sprite under the HUD — the black square
    /// retire-not-destroy exists to prevent, reintroduced at a bound (R28 finding 4a). When
    /// every row over the cap is still referenced the table is allowed to grow and says so once.
    /// </summary>
    private static void SweepRetiredNoLock()
    {
        var overflow = Retired.TakeOverflow();
        if (overflow.Count > 0)
        {
            DestroyRetired(overflow);
            ProcessHost.Log?.LogWarning(
                IconCachePolicy.FormatRetiredCapped(overflow.Count, IconCachePolicy.MaxRetiredPairs));
        }

        if (Retired.NoteCapExceeded())
        {
            ProcessHost.Log?.LogWarning(
                IconCachePolicy.FormatRetiredOverCap(Retired.Count, IconCachePolicy.MaxRetiredPairs));
        }
    }

    private static void DestroyRetired(List<object?> pairs)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i] is not Live live) continue;
            DestroyUnity(live.Sprite);
            DestroyUnity(live.Texture);
        }
    }

    private static void DestroyPair(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!Unity.TryGetValue(key, out var live)) return;
        Unity.Remove(key);
        DestroyUnity(live.Sprite);
        DestroyUnity(live.Texture);
    }

    /// <summary>
    /// Unity fake-null: a destroyed Texture2D still has a non-null managed wrapper.
    /// <c>== null</c> uses the overloaded operator (evidence 122: white square).
    /// </summary>
    private static bool IsUnityDead(Object? obj)
    {
        try
        {
            return obj == null;
        }
        catch
        {
            return true;
        }
    }

    private static void DestroyUnity(Object? obj)
    {
        if (obj == null) return;
        try { Object.DestroyImmediate(obj); }
        catch
        {
            try { Object.Destroy(obj); }
            catch { /* process teardown */ }
        }
    }
}
