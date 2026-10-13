using System;
using Il2CppInterop.Runtime;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Progenitor.Ui;

/// <summary>
/// TABS2 part B, the Unity side of <see cref="WorkspaceTabLadder"/>: the two donors that let a
/// strip built without a FullscreenMenu (the title screen) still clone the game's own inner tab.
///
/// <list type="bullet">
/// <item><b>Cached template.</b> One hidden, inactive, <c>DontDestroyOnLoad</c> copy of an
/// already-silenced strip clone (native callbacks removed, school icon and new-tag switched off),
/// taken while the in-game menu is up. Re-validated before each use: every <c>Image.sprite</c> it
/// draws must still be alive, else it is destroyed and the ladder moves on.</item>
/// <item><b>Resident prefab.</b> A <c>FullscreenMenu_InnerTabEntry</c> already loaded in memory,
/// found with the non-generic <c>Resources.FindObjectsOfTypeAll(Il2CppType)</c> (the generic has
/// no native instantiation). Bounded to <see cref="WorkspaceTabLadder.MaxResidentScans"/> scans
/// per Core generation and only ever cloned, never modified.</item>
/// </list>
///
/// <para>Both are released on Core unload (<see cref="Release"/>); the template's name carries the
/// owned prefix, so the Core-unload DontDestroyOnLoad sweep also removes a leftover.</para>
/// </summary>
internal static class WorkspaceTabDonors
{
    internal const string TemplateName = UiOwnership.Prefix + "core-tab-donor-template";
    internal const string PrimeHolderName = UiOwnership.Prefix + "core-tab-donor-prime";

    private static GameObject? _template;
    private static int _templateCaptures;
    private static GameObject? _resident;
    private static Image? _residentIcon;
    private static Image? _residentNewTag;
    private static int _residentScans;
    private static string _residentKind = "";

    /// <summary>
    /// TABCAPS1 readback: which resident donor the last scan took — <c>menu-prefab</c> (the
    /// FullscreenMenu's own inner-tab prefab, the in-game donor's twin) or <c>any-entry</c> (the first
    /// inner-tab entry found, which can be another menu's tab), empty before a scan found one.
    /// </summary>
    internal static string ResidentKind => _residentKind;

    internal const string ResidentMenuPrefab = "menu-prefab";
    internal const string ResidentAnyEntry = "any-entry";
    private static int _residentMissFrame = int.MinValue;

    internal static bool TemplateAlive => Alive(_template);

    /// <summary>Whether a template capture is still worth attempting this generation.</summary>
    internal static bool MayCaptureTemplate() =>
        WorkspaceTabLadder.MayCaptureTemplate(Alive(_template), _templateCaptures);

    /// <summary>
    /// Spend one of the generation's capture attempts; false when there is a live template already or
    /// the budget is gone. Counted before the clone so a donor that never clones cannot retry per frame.
    /// </summary>
    internal static bool TryBeginCapture()
    {
        if (!MayCaptureTemplate()) return false;
        _templateCaptures++;
        return true;
    }

    /// <summary>
    /// Keep one hidden copy of <paramref name="silencedClone"/> — a strip clone that has already
    /// had its callbacks and decorations removed — so a later title strip gets the same framing.
    /// </summary>
    internal static void CaptureTemplate(GameObject? silencedClone)
    {
        if (Alive(_template) || !Alive(silencedClone)) return;
        GameObject? made = null;
        try
        {
            made = Object.Instantiate(silencedClone!);
            if (!Alive(made)) return;
            made!.transform.SetParent(null, false);
            Object.DontDestroyOnLoad(made);
            made.name = TemplateName;
            made.SetActive(false);
            _template = made;
        }
        catch
        {
            DestroyOwned(made);
        }
    }

    /// <summary>The template when it is alive and every sprite it draws is still loaded; else null (and dropped).</summary>
    internal static GameObject? UsableTemplate()
    {
        if (!Alive(_template)) return null;
        if (WorkspaceTabLadder.TemplateUsable(
                alive: true,
                spritesResident: SpritesResident(_template!),
                fontsResident: FontsResident(_template!),
                materialsResident: MaterialsResident(_template!)))
            return _template;
        DestroyOwned(_template);
        _template = null;
        return null;
    }

    /// <summary>
    /// The resident inner-tab button, or null. A found donor is kept (weakly: it is re-checked for
    /// life on every call); a miss costs one of <see cref="WorkspaceTabLadder.MaxResidentScans"/>.
    /// </summary>
    internal static GameObject? ResidentDonor()
    {
        if (Alive(_resident)) return _resident;
        _resident = null;
        int frame = CurrentFrame();
        if (!WorkspaceTabLadder.MayScanResident(_residentScans, _residentMissFrame, frame)) return null;
        _residentScans++;
        _residentMissFrame = frame;
        // TABCAPS1: live 2026-10-03 (generation 32, after a Core swap in a world with the menu down)
        // the resident scan took an inner tab whose highlighted sprite is OrnamentedTab_Passives_Active
        // — another menu's tab, not the Spellbook tab every in-game strip clones (Tab_Active) — and
        // that strip is the one the user saw without captions. Prefer the FullscreenMenu's own
        // inner-tab prefab, found the same way, so the resident rung clones what rung 1 clones.
        try
        {
            var menuType = Il2CppType.Of<FullscreenMenu>();
            var menus = menuType == null ? null : Resources.FindObjectsOfTypeAll(menuType);
            if (menus != null)
            {
                for (int i = 0; i < menus.Length; i++)
                {
                    FullscreenMenu? menu = null;
                    try { menu = menus[i] == null ? null : menus[i].TryCast<FullscreenMenu>(); }
                    catch { continue; }
                    if (menu == null) continue;
                    try
                    {
                        var entry = menu.InnerTabGroup?.EntryPrefab;
                        var button = entry?.Button;
                        if (entry == null || !Alive(button)) continue;
                        var go = button!.gameObject;
                        if (!Alive(go) || IsOwned(go)) continue;
                        _resident = go;
                        _residentKind = ResidentMenuPrefab;
                        _residentMissFrame = int.MinValue;
                        _residentIcon = Alive(entry.IconImage) ? entry.IconImage : null;
                        _residentNewTag = Alive(entry.NewTagImage) ? entry.NewTagImage : null;
                        return go;
                    }
                    catch { /* keep scanning */ }
                }
            }
        }
        catch { /* fall back to any inner-tab entry */ }

        try
        {
            var type = Il2CppType.Of<FullscreenMenu_InnerTabEntry>();
            if (type == null) return null;
            var all = Resources.FindObjectsOfTypeAll(type);
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                FullscreenMenu_InnerTabEntry? entry = null;
                try { entry = all[i] == null ? null : all[i].TryCast<FullscreenMenu_InnerTabEntry>(); }
                catch { continue; }
                if (entry == null) continue;
                try
                {
                    if (entry.Pointer == IntPtr.Zero) continue;
                    var button = entry.Button;
                    if (!Alive(button)) continue;
                    var go = button!.gameObject;
                    if (!Alive(go) || IsOwned(go)) continue;
                    _resident = go;
                    _residentKind = ResidentAnyEntry;
                    _residentMissFrame = int.MinValue;
                    _residentIcon = Alive(entry.IconImage) ? entry.IconImage : null;
                    _residentNewTag = Alive(entry.NewTagImage) ? entry.NewTagImage : null;
                    return go;
                }
                catch { /* keep scanning */ }
            }
        }
        catch { /* a failed scan is a miss, not a defect */ }

        return null;
    }

    /// <summary>Icon / new-tag sprites of the resident entry, so its clone can switch them off.</summary>
    internal static void ResidentDecorations(out Sprite? icon, out Sprite? newTag)
    {
        icon = null;
        newTag = null;
        try { if (Alive(_residentIcon)) icon = _residentIcon!.sprite; } catch { /* */ }
        try { if (Alive(_residentNewTag)) newTag = _residentNewTag!.sprite; } catch { /* */ }
    }

    /// <summary>Record the rung a strip tab came from and log once per change.</summary>
    internal static void Note(WorkspaceTabSource source)
    {
        string? line = WorkspaceTabLadder.Note(source);
        if (line == null) return;
        try { Plugin.Logger?.LogDiag(line); } catch { /* */ }
    }

    /// <summary>Core unload: destroy the template, forget the resident donor, reset the counters.</summary>
    internal static void Release()
    {
        DestroyOwned(_template);
        _template = null;
        _templateCaptures = 0;
        _resident = null;
        _residentKind = "";
        _residentIcon = null;
        _residentNewTag = null;
        _residentScans = 0;
        _residentMissFrame = int.MinValue;
        WorkspaceTabLadder.Reset();
    }

    private static bool SpritesResident(GameObject template)
    {
        try
        {
            var images = template.GetComponentsInChildren<Image>(true);
            if (images == null || images.Length == 0) return false;
            bool any = false;
            for (int i = 0; i < images.Length; i++)
            {
                var image = images[i];
                if (image == null) continue;
                Sprite? sprite;
                try { sprite = image.sprite; } catch { return false; }
                if (sprite == null) continue;
                if (!Alive(sprite)) return false;
                any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every caption's TMP font asset (and its atlas material) is still loaded.</summary>
    private static bool FontsResident(GameObject template)
    {
        try
        {
            var texts = template.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
            if (texts == null) return true;
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null) continue;
                TMPro.TMP_FontAsset? font;
                try { font = text.font; } catch { return false; }
                if (font == null || !Alive(font)) return false;
                Material? shared;
                try { shared = text.fontSharedMaterial; } catch { return false; }
                if (shared != null && !Alive(shared)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every Image's explicitly assigned material (not the default UI material) is still loaded.</summary>
    private static bool MaterialsResident(GameObject template)
    {
        try
        {
            var images = template.GetComponentsInChildren<Image>(true);
            if (images == null) return true;
            for (int i = 0; i < images.Length; i++)
            {
                var image = images[i];
                if (image == null) continue;
                Material? material;
                try { material = image.material; } catch { return false; }
                if (material != null && !Alive(material)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int CurrentFrame()
    {
        try { return Time.frameCount; }
        catch { return int.MinValue + 1; }
    }

    private static bool IsOwned(GameObject go)
    {
        try
        {
            var t = go.transform;
            int guard = 0;
            while (t != null && guard++ < 16)
            {
                try { if (UiOwnership.IsOwned(t.gameObject?.name ?? "")) return true; }
                catch { /* */ }
                try { t = t.parent; } catch { break; }
            }
        }
        catch { /* */ }

        return false;
    }

    private static bool Alive(Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static void DestroyOwned(GameObject? go)
    {
        if (!Alive(go)) return;
        try { go!.SetActive(false); } catch { /* */ }
        if (BootstrapLink.MayDestroyUiImmediate())
        {
            try { Object.DestroyImmediate(go); return; }
            catch { /* */ }
        }

        try { Object.Destroy(go); } catch { /* */ }
    }
}
