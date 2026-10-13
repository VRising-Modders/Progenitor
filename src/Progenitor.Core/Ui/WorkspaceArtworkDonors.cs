using System;
using Il2CppInterop.Runtime;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Progenitor.Ui;

/// <summary>
/// Unity side of <see cref="WorkspaceArtworkLadder"/>'s rungs 2 and 3: a copy of the Esc menu's
/// <c>JournalBackgroundSmoke</c> material (plus a reference to its <c>Divider_Big_04</c> trim
/// sprite) taken the first time the menu is seen this Core generation, and a bounded resident
/// lookup for both at the title. The copy is our own <c>new Material</c> (never a native object),
/// re-validated before use and destroyed on Core unload. Rung 1 (live menu) and rung 4 (Play
/// backdrop) stay in <c>ModsWorkspaceChrome</c>, which owns the pane.
/// </summary>
internal static class WorkspaceArtworkDonors
{
    private static Material? _smoke;
    private static Sprite? _trim;
    private static Image.Type _trimType = Image.Type.Simple;
    private static Color _trimColor = Color.white;
    private static int _captures;
    private static Material? _residentSmoke;
    private static Sprite? _residentTrim;
    private static int _residentScans;
    private static int _residentScanFrame = int.MinValue;

    internal static bool MayCapture() =>
        WorkspaceArtworkLadder.MayCapture(Alive(_smoke) && Alive(_trim), _captures);

    /// <summary>
    /// Remember the menu's smoke material (as an owned copy) and trim sprite. Counted before the read,
    /// only when the menu has an Image to read, so a half-built menu cannot spend the budget per frame.
    /// </summary>
    internal static void Capture(FullscreenMenu? menu)
    {
        if (menu == null || !MayCapture()) return;
        Image[]? images;
        try { images = menu.GetComponentsInChildren<Image>(true); }
        catch { return; }
        if (images == null || images.Length == 0) return;
        _captures++;
        for (int i = 0; i < images.Length; i++)
        {
            var donor = images[i];
            if (donor == null) continue;
            try
            {
                if (!Alive(_smoke))
                {
                    var material = donor.material;
                    if (IsSmoke(material))
                    {
                        var copy = new Material(material!);
                        copy.name = UiOwnership.Prefix + "core-smoke-cache";
                        try { copy.hideFlags = HideFlags.DontUnloadUnusedAsset; } catch { /* */ }
                        _smoke = copy;
                    }
                }
                if (!Alive(_trim) && donor.sprite != null && donor.sprite.name == WorkspaceArtworkLadder.TrimSpriteName)
                {
                    _trim = donor.sprite;
                    _trimType = donor.type;
                    _trimColor = donor.color;
                }
            }
            catch { /* keep reading */ }
        }
    }

    /// <summary>The cached smoke copy when it and every texture it samples are still alive; else null (and dropped).</summary>
    internal static Material? CachedSmoke()
    {
        if (!Alive(_smoke)) return null;
        if (WorkspaceArtworkLadder.CachedUsable(alive: true, texturesResident: TexturesResident(_smoke!)))
            return _smoke;
        DestroyUnity(_smoke);
        _smoke = null;
        return null;
    }

    internal static Sprite? CachedTrim(out Image.Type type, out Color color)
    {
        type = _trimType;
        color = _trimColor;
        if (Alive(_trim)) return _trim;
        _trim = null;
        return null;
    }

    /// <summary>Resident <c>JournalBackgroundSmoke</c>, scanned at most once per pane build (shared with the trim).</summary>
    internal static Material? ResidentSmoke()
    {
        if (Alive(_residentSmoke) && TexturesResident(_residentSmoke!)) return _residentSmoke;
        _residentSmoke = null;
        ScanResidentOnce();
        return Alive(_residentSmoke) ? _residentSmoke : null;
    }

    internal static Sprite? ResidentTrim()
    {
        if (Alive(_residentTrim)) return _residentTrim;
        _residentTrim = null;
        ScanResidentOnce();
        return Alive(_residentTrim) ? _residentTrim : null;
    }

    /// <summary>Core unload: destroy the owned copy, forget every reference, reset the budgets.</summary>
    internal static void Release()
    {
        DestroyUnity(_smoke);
        _smoke = null;
        _trim = null;
        _captures = 0;
        _residentSmoke = null;
        _residentTrim = null;
        _residentScans = 0;
        _residentScanFrame = int.MinValue;
        WorkspaceArtworkLadder.Reset();
    }

    internal static bool IsSmoke(Material? material)
    {
        try
        {
            return material != null && Alive(material)
                && material.name == WorkspaceArtworkLadder.SmokeMaterialName
                && material.shader != null && material.shader.name == WorkspaceArtworkLadder.FlowmapShaderName;
        }
        catch { return false; }
    }

    internal static bool IsFlowmap(Material? material)
    {
        try
        {
            return material != null && Alive(material)
                && material.shader != null && material.shader.name == WorkspaceArtworkLadder.FlowmapShaderName;
        }
        catch { return false; }
    }

    /// <summary>Main texture and every flowmap overlay texture the material samples are still loaded.</summary>
    internal static bool TexturesResident(Material material)
    {
        try
        {
            var main = material.mainTexture;
            if (main != null && !Alive(main)) return false;
            var names = WorkspaceArtworkLadder.OverlayScaleProperties;
            for (int i = 0; i < names.Length; i++)
            {
                if (!material.HasProperty(names[i])) continue;
                var tex = material.GetTexture(names[i]);
                if (tex != null && !Alive(tex)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Read-only description of a material for <c>mods_ui tabs.artworkMaterials</c>: name, shader,
    /// render queue and every shader property's current value (floats/ranges, colours/vectors,
    /// textures with name, scale and offset). On demand only, never per frame. Used to compare the
    /// pane's copy with the live donor so a slower smoke can be traced to a parameter difference.
    /// </summary>
    internal static System.Collections.Generic.Dictionary<string, object?>? DescribeMaterial(Material? material)
    {
        if (!Alive(material)) return null;
        var d = new System.Collections.Generic.Dictionary<string, object?>();
        try { d["name"] = material!.name ?? ""; } catch { d["name"] = ""; }
        try { d["shader"] = material!.shader != null ? material.shader.name ?? "" : ""; } catch { d["shader"] = ""; }
        try { d["renderQueue"] = material!.renderQueue; } catch { /* */ }
        var props = new System.Collections.Generic.List<object>();
        try
        {
            var shader = material!.shader;
            int count = shader != null ? shader.GetPropertyCount() : 0;
            for (int i = 0; i < count && i < 64; i++)
            {
                string name;
                int type;
                try { name = shader!.GetPropertyName(i) ?? ""; type = (int)shader.GetPropertyType(i); }
                catch { continue; }
                var row = new System.Collections.Generic.Dictionary<string, object?> { ["name"] = name, ["type"] = type };
                try
                {
                    switch (type)
                    {
                        case 0: // Color
                        case 1: // Vector
                        {
                            var v = material.GetVector(name);
                            row["value"] = new[] { Round(v.x), Round(v.y), Round(v.z), Round(v.w) };
                            break;
                        }
                        case 4: // Texture
                        {
                            var tex = material.GetTexture(name);
                            row["texture"] = tex != null ? tex.name ?? "" : null;
                            row["alive"] = tex == null || Alive(tex);
                            var sc = material.GetTextureScale(name);
                            var of = material.GetTextureOffset(name);
                            row["scale"] = new[] { Round(sc.x), Round(sc.y) };
                            row["offset"] = new[] { Round(of.x), Round(of.y) };
                            break;
                        }
                        default: // Float, Range, Int
                            row["value"] = Round(material.GetFloat(name));
                            break;
                    }
                }
                catch (Exception e) { row["why"] = "read-threw:" + e.Message; }
                props.Add(row);
            }
        }
        catch (Exception e) { d["why"] = "read-threw:" + e.Message; }
        d["properties"] = props;
        return d;
    }

    private static float Round(float v) => (float)Math.Round(v, 4);

    private static void ScanResidentOnce()
    {
        int frame;
        try { frame = Time.frameCount; } catch { frame = int.MinValue + 1; }
        if (!WorkspaceArtworkLadder.MayScanResident(_residentScans, _residentScanFrame, frame)) return;
        _residentScans++;
        _residentScanFrame = frame;
        if (!Alive(_residentSmoke))
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    Material? m = null;
                    try { m = all[i] == null ? null : all[i].TryCast<Material>(); }
                    catch { continue; }
                    if (!IsSmoke(m) || IsOwnedName(m!.name)) continue;
                    if (!TexturesResident(m)) continue;
                    _residentSmoke = m;
                    break;
                }
            }
            catch { /* a failed scan is a miss */ }
        }
        if (!Alive(_residentTrim))
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    Sprite? s = null;
                    try { s = all[i] == null ? null : all[i].TryCast<Sprite>(); }
                    catch { continue; }
                    if (s == null) continue;
                    string name;
                    try { name = s.name ?? ""; } catch { continue; }
                    if (!string.Equals(name, WorkspaceArtworkLadder.TrimSpriteName, StringComparison.Ordinal)) continue;
                    _residentTrim = s;
                    break;
                }
            }
            catch { /* a failed scan is a miss */ }
        }
    }

    private static bool IsOwnedName(string? name)
    {
        try { return UiOwnership.IsOwned(name ?? "") || string.Equals(name, "progenitor-workspace-smoke", StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool Alive(Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static void DestroyUnity(Object? obj)
    {
        if (!Alive(obj)) return;
        try { Object.DestroyImmediate(obj); }
        catch
        {
            try { Object.Destroy(obj); } catch { /* */ }
        }
    }
}
