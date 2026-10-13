#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Progenitor.WorldState;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Progenitor.Ui.GameArt;

/// <summary>
/// The Unity reads shared by the borrowed-art ladders (<see cref="GameArtLadder"/>): donor scans through
/// the non-generic <c>FindObjectsOfType(Il2CppType, true)</c> / <c>Resources.FindObjectsOfTypeAll(Il2CppType)</c>
/// plus <c>TryCast</c>, liveness checks, and the owned-object teardown. Every call is guarded; nothing here
/// throws.
/// </summary>
internal static class GameArtUnity
{
    /// <summary>Scene objects, active and inactive (the non-generic overload); Progenitor's own are skipped.</summary>
    internal static List<T> Scene<T>() where T : Object
    {
        var list = new List<T>();
        try
        {
            var type = Il2CppType.Of<T>();
            if (type == null) return list;
            var all = Object.FindObjectsOfType(type, true);
            for (int i = 0; all != null && i < all.Length; i++)
            {
                try
                {
                    var t = all[i] == null ? null : all[i].TryCast<T>();
                    if (t != null && AliveObject(t) && !IsOwned(t)) list.Add(t);
                }
                catch { /* skip */ }
            }
        }
        catch { /* a miss */ }
        return list;
    }

    /// <summary>Everything loaded, prefabs included (the non-generic overload); Progenitor's own are skipped.</summary>
    internal static List<T> Loaded<T>() where T : Object
    {
        var list = new List<T>();
        try
        {
            var type = Il2CppType.Of<T>();
            if (type == null) return list;
            var all = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; all != null && i < all.Length; i++)
            {
                try
                {
                    var t = all[i] == null ? null : all[i].TryCast<T>();
                    if (t != null && AliveObject(t) && !IsOwned(t)) list.Add(t);
                }
                catch { /* skip */ }
            }
        }
        catch { /* a miss */ }
        return list;
    }

    /// <summary>The transform path from <paramref name="root"/> (exclusive) to <paramref name="t"/>, or <c>.</c>.</summary>
    internal static string PathUnder(Transform t, Transform root)
    {
        var parts = new List<string>();
        try
        {
            var cur = t;
            int guard = 0;
            while (cur != null && cur.Pointer != root.Pointer && guard++ < 16)
            {
                parts.Insert(0, cur.gameObject.name ?? "");
                cur = cur.parent;
            }
        }
        catch { /* */ }
        return parts.Count == 0 ? "." : string.Join("/", parts);
    }

    /// <summary>A component under a Progenitor-owned root (by <see cref="UiOwnership.IsOwned(string)"/>).</summary>
    internal static bool IsOwned(Object obj)
    {
        try
        {
            if (obj.TryCast<Component>() is not { } c) return false;
            var t = c.transform;
            int guard = 0;
            while (t != null && guard++ < 16)
            {
                if (UiOwnership.IsOwned(t.gameObject.name ?? "")) return true;
                t = t.parent;
            }
        }
        catch { /* */ }
        return false;
    }

    internal static string SafeName(Object obj)
    {
        try { return obj.name ?? ""; }
        catch { return ""; }
    }

    internal static bool HasBorder(Sprite sprite)
    {
        try { return sprite.border != Vector4.zero; }
        catch { return false; }
    }

    internal static bool AliveObject(Object? obj)
    {
        try { return obj != null && obj.Pointer != IntPtr.Zero; }
        catch { return false; }
    }

    /// <summary>A sprite that can still draw: the object and its texture are loaded.</summary>
    internal static bool AliveSprite(Sprite? sprite) => AliveObject(sprite) && TextureAlive(sprite);

    internal static bool TextureAlive(Sprite? sprite)
    {
        try { return sprite != null && AliveObject(sprite.texture); }
        catch { return false; }
    }

    internal static int CurrentFrame()
    {
        try { return Time.frameCount; }
        catch { return int.MinValue + 1; }
    }

    internal static int CurrentEpoch()
    {
        try { return WorldEpoch.Current; }
        catch { return 0; }
    }

    /// <summary>Destroy an owned asset (texture, sprite, material); DestroyImmediate when Destroy throws.</summary>
    internal static void DestroyAsset(Object? obj)
    {
        if (!AliveObject(obj)) return;
        try { Object.Destroy(obj); }
        catch
        {
            try { Object.DestroyImmediate(obj); } catch { /* */ }
        }
    }

    /// <summary>Deactivate and destroy an owned GameObject (immediately when Bootstrap allows it).</summary>
    internal static void DestroyOwned(GameObject? go)
    {
        if (!AliveObject(go)) return;
        try { go!.SetActive(false); } catch { /* */ }
        if (BootstrapLink.MayDestroyUiImmediate())
        {
            try { Object.DestroyImmediate(go); return; }
            catch { /* */ }
        }

        try { Object.Destroy(go); } catch { /* */ }
    }
}
#endif
