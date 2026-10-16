#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.UI;
using static Progenitor.Ui.GameArt.GameArtUnity;
using Object = UnityEngine.Object;

namespace Progenitor.Ui.GameArt;

/// <summary>
/// MENUKIT2, the Unity side of <see cref="SettingsArtLadder"/>: borrows the game's Options page controls
/// for the Mods settings page. Donors are only read (never cloned, modified or instantiated).
///
/// <para><b>Donors</b> (client dump; live probe M1, 2026-09-30): <c>ProjectM.UI.OptionsMenu</c> (446623)
/// — its <c>GeneralPanel</c>/<c>ControlsPanel</c>/<c>GraphicsPanel</c>/<c>SoundPanel</c> and
/// <c>DetailsDivider</c> — and the <c>OptionsPanel_Interface/Controls/Graphics/Sound/Accessibility</c>
/// types directly. Each panel's <c>CheckboxPrefab</c>, <c>SliderPrefab</c>, <c>DropdownPrefab</c>,
/// <c>HeaderPrefab</c>, <c>ButtonPrefab</c> and <c>ScrollRect.verticalScrollbar</c> are walked; every
/// <c>Image</c> sprite and every selectable sprite state (<c>Selectable.spriteState</c>, and each
/// <c>SimpleStunSelectable.TargetSettings[i].SpriteState</c>) is matched by name to a part
/// (<see cref="SettingsArtLadder.PartOf"/>). M1 found every donor resident at the title and none live.</para>
///
/// <para><b>Rungs</b> per part: live → owned copy → held → kept (a copy an earlier Core generation left in
/// Bootstrap's process-lifetime table, <see cref="GameArtKept"/>) → resident prefab or a loaded sprite of the
/// part's primary name → kit, exactly as the Keybinds tab (<see cref="GameArtLadder"/>). Held sprites and
/// owned copies are the shared <see cref="OwnedSprites"/> store; textures found at the title are released on a
/// world load (K8), so the owned copy is what a world draws. BUTTONART1: a button part is taken only when it
/// has a game button's shape (<see cref="SettingsArtLadder.ShapeFits"/>), and the loaded-sprite rung matches
/// primary names only (<see cref="SettingsArtLadder.PrimaryPartOf"/>).</para>
/// </summary>
internal static class SettingsArtDonors
{
    internal const string HolderName = UiOwnership.Prefix + "core-settings-art";

    /// <summary>The owned copies' name tag (<c>progenitor-ui-settings-art-{sprite}</c>).</summary>
    internal const string AssetTag = "settings-art-";

    private const int PartCount = SettingsArtLadder.PartCount;

    private static readonly string[] PartNames = BuildPartNames();

    private static readonly OwnedSprites Art = new(HolderName, AssetTag, SettingsArtLadder.LogPrefix, PartNames);

    private static readonly Sprite?[] HeldSource = new Sprite?[PartCount];
    private static readonly string[] HeldDonor = new string[PartCount];
    private static readonly string[] HeldName = new string[PartCount];
    private static readonly GameArtSource[] Sources = new GameArtSource[PartCount];
    private static List<string> _donorSprites = new();
    private static int _epoch = int.MinValue;
    private static int _liveScanEpoch = int.MinValue;
    private static int _liveScanFrame = int.MinValue;
    private static int _residentScans;
    private static int _residentScanFrame = int.MinValue;
    private static int _preparedFrame = int.MinValue;
    private static readonly HashSet<string> LoggedFallThrough = new(StringComparer.Ordinal);

    private sealed class Found
    {
        internal readonly Sprite?[] Parts = new Sprite?[PartCount];
        internal readonly string[] Donor = new string[PartCount];
        internal readonly List<string> Names = new();
        internal bool AnyMenu;

        /// <summary>BUTTONART1: copies an earlier generation kept in Bootstrap's process-lifetime table.</summary>
        internal readonly Sprite?[] Kept = new Sprite?[PartCount];
        internal readonly GameArtKept.Entry[] KeptEntry = new GameArtKept.Entry[PartCount];
    }

    private static string[] BuildPartNames()
    {
        var names = new string[PartCount];
        for (int i = 0; i < PartCount; i++) names[i] = SettingsArtLadder.PartName((SettingsArtPart)i);
        return names;
    }

    /// <summary>
    /// Resolve every part from what is ready, holding newly found sprites without copying them (the
    /// copies are made on idle frames, <see cref="IdleTick"/>). The hook of <see cref="SettingsArtLadder"/>.
    /// </summary>
    internal static void Prepare() => PrepareCore(copyNow: false);

    // ---- MENUKIT2 round C: preparation on idle frames, off the workspace's open path ------------------

    /// <summary>Frames a scene (the title, or a world after its load) must run before the scan.</summary>
    internal const int SettleFrames = 180;

    /// <summary>GPU copies made per idle frame.</summary>
    internal const int CopiesPerFrame = 2;

    /// <summary>A frame slower than this is not idle (a load hitch): the preparation waits.</summary>
    internal const float BusyFrameSeconds = 0.1f;

    private static bool _idleArmed;
    private static int _idleEpoch = int.MinValue;
    private static int _idleFrames;
    private static int _idleStage;
    private static long _idleWorkTicks;
    private static long _idleStartTicks;

    /// <summary>
    /// The key cap's preparation waits for the frame after the donor scan: each is a type scan of every loaded
    /// object, and together they made one 130-400 ms frame after each world load (client logs 2026-10-06).
    /// </summary>
    private static bool _keyCapPending;

    /// <summary>Start watching for idle frames (<c>UiKeybindHost.Install</c>).</summary>
    internal static void StartIdle()
    {
        if (_idleArmed) return;
        _idleArmed = true;
        _idleEpoch = int.MinValue;
        Plugin.Frame += IdleTick;
        // Key-conflict dialogs wait (capped) for the owned-copy art instead of drawing the kit look.
        KeybindConflictPrompt.DialogArtReady = IdlePrepared;
    }

    /// <summary>True once this world's idle preparation finished (or gave up), or when idle preparation is off.</summary>
    private static bool IdlePrepared() => !_idleArmed || _idleStage >= 3;

    internal static void StopIdle()
    {
        if (!_idleArmed) return;
        _idleArmed = false;
        Plugin.Frame -= IdleTick;
        if (KeybindConflictPrompt.DialogArtReady == (Func<bool>)IdlePrepared) KeybindConflictPrompt.DialogArtReady = () => true;
    }

    /// <summary>
    /// Per frame, cheap until there is work: after <see cref="SettleFrames"/> quiet frames of a scene, one
    /// frame scans and holds the donors' sprites, then each frame makes at most <see cref="CopiesPerFrame"/>
    /// owned copies, then the parts are resolved again and the time is logged
    /// (<c>Settings kit: prepared in N ms (owned-copy=K)</c>). Runs again after each world change.
    /// </summary>
    private static void IdleTick()
    {
        try
        {
            int epoch = CurrentEpoch();
            if (epoch != _idleEpoch)
            {
                _idleEpoch = epoch;
                _idleFrames = 0;
                _idleStage = 0;
                _idleWorkTicks = 0;
                _keyCapPending = false;
            }

            if (_idleStage >= 3) return;
            if (_idleStage == 0)
            {
                if (++_idleFrames < SettleFrames) return;
                float dt = 0f;
                try { dt = Time.unscaledDeltaTime; } catch { dt = 0f; }
                if (dt > BusyFrameSeconds) return;
                _idleStage = 1;
                _idleStartTicks = Stopwatch.GetTimestamp();
            }

            long start = Stopwatch.GetTimestamp();
            if (_idleStage == 1)
            {
                _preparedFrame = int.MinValue;
                PrepareCore(copyNow: false);
                _keyCapPending = true;
                _idleStage = 2;
            }
            else if (_keyCapPending)
            {
                // Round D: the game's standard button is the Keybinds tab's owned key cap
                // (SimpleMenuButton_Normal); prepare it here too, so every settings page and the
                // Configuration tab have it without the Keybinds tab being opened first. Its own frame.
                _keyCapPending = false;
                try { KeybindArtLadder.Prepare(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key cap art preparation threw: " + e.Message); }
            }
            else
            {
                int copies = 0;
                for (int i = 0; i < PartCount && copies < CopiesPerFrame; i++)
                    if (Art.CopyPending(i)) copies++;
                if (copies == 0)
                {
                    _preparedFrame = int.MinValue;
                    PrepareCore(copyNow: false);
                    _idleStage = 3;
                    _idleWorkTicks += Stopwatch.GetTimestamp() - start;
                    LogPrepared();
                    return;
                }
            }

            _idleWorkTicks += Stopwatch.GetTimestamp() - start;
        }
        catch (Exception e)
        {
            _idleStage = 3;
            Plugin.Logger?.LogWarning("Ui: settings art idle preparation threw: " + e.Message + "; the kit look is used where art is missing.");
        }
    }

    private static void LogPrepared()
    {
        int owned = 0;
        for (int i = 0; i < PartCount; i++) if (Sources[i] is GameArtSource.OwnedCopy or GameArtSource.Kept) owned++;
        double work = _idleWorkTicks * 1000.0 / Stopwatch.Frequency;
        double wall = (Stopwatch.GetTimestamp() - _idleStartTicks) * 1000.0 / Stopwatch.Frequency;
        try
        {
            Plugin.Logger?.LogDiag(SettingsArtLadder.PreparedLine(work, wall, owned));
        }
        catch { /* */ }
    }

    /// <summary>Resolve every part. <paramref name="copyNow"/> false holds a new sprite and leaves its copy to an idle frame.</summary>
    private static void PrepareCore(bool copyNow)
    {
        int frame = CurrentFrame();
        if (frame == _preparedFrame && SettingsArtLadder.Current != SettingsArt.Unresolved) return;
        _preparedFrame = frame;
        int epoch = CurrentEpoch();
        if (epoch != _epoch)
        {
            _epoch = epoch;
            _residentScans = 0;
            _residentScanFrame = int.MinValue;
        }

        bool allHeld = true;
        // WINDOWS1: an optional part (the window title ornament) that the game never loads does not keep the scans going.
        for (int i = 0; i < PartCount; i++) allHeld &= SettingsArtLadder.Optional((SettingsArtPart)i) || Art.OwnedUsable(i) || Art.HeldUsable(i);
        Found? live = null;
        if (SettingsArtLadder.ShouldScanLive(allHeld, _liveScanEpoch, epoch, _liveScanFrame, frame))
        {
            _liveScanEpoch = epoch;
            _liveScanFrame = frame;
            live = new Found();
            try { ReadMenus(live, resident: false); }
            catch { /* a failed read is a miss */ }
        }

        Found? resident = null;
        string residentWhy = "";
        Found? Resident()
        {
            if (resident != null || residentWhy.Length > 0) return resident;
            if (!SettingsArtLadder.MayScanResident(_residentScans, _residentScanFrame, frame))
            {
                residentWhy = "scan budget spent this world";
                return null;
            }

            _residentScans++;
            _residentScanFrame = frame;
            resident = new Found();
            try { ReadMenus(resident, resident: true); }
            catch { /* a miss */ }
            try { ReadLoadedSprites(resident); }
            catch { /* a miss */ }
            return resident;
        }

        var parts = new GameArtPart[PartCount];
        for (int i = 0; i < PartCount; i++)
        {
            var liveHit = live?.Parts[i];
            bool liveFound = AliveSprite(liveHit);
            string liveWhy = live == null ? "not rescanned (kept from this world)"
                : liveFound ? "" : live.AnyMenu ? "menu found, no " + SettingsArtLadder.SpriteName((SettingsArtPart)i) : "no donor menu in the scene";
            bool owned = !liveFound && Art.OwnedUsable(i);
            bool cached = !liveFound && !owned && Art.HeldUsable(i);
            string cachedWhy = liveFound || owned || cached ? "" : HeldSource[i] == null ? "none kept" : "kept sprite unloaded";
            Sprite? residentHit = null;
            string residentDonor = "";
            string resWhy = "";
            Found? r = null;
            if (!liveFound && !owned && !cached)
            {
                r = Resident();
                residentHit = r?.Parts[i];
                residentDonor = r?.Donor[i] ?? "";
                if (!AliveSprite(residentHit))
                {
                    residentHit = null;
                    resWhy = r == null ? residentWhy : r.AnyMenu ? "prefab found, no " + SettingsArtLadder.SpriteName((SettingsArtPart)i) : "no donor prefab or named sprite loaded";
                }
            }

            // BUTTONART1: a copy kept by an earlier generation beats a name match.
            bool kept = r != null && AliveSprite(r.Kept[i]);
            var source = GameArtLadder.Next(liveFound, owned, cached, kept, residentHit != null);
            if (source == GameArtSource.Kept && !Adopt(i, r!.Kept[i], r.KeptEntry[i]))
                source = GameArtLadder.Next(false, false, false, false, residentHit != null);
            if (source == GameArtSource.Live) Hold(i, liveHit!, live!.Donor[i], copyNow);
            else if (source == GameArtSource.Resident) Hold(i, residentHit!, residentDonor, copyNow);
            Sources[i] = source;
            parts[i] = PartOf(i, source);
            if (source != GameArtSource.Live && live != null || source == GameArtSource.Kit)
                LogFallThrough(SettingsArtLadder.FallThrough(PartNames[i], liveWhy, cachedWhy, resWhy, source));
        }

        if (live != null && live.Names.Count > 0) _donorSprites = live.Names;
        else if (resident != null && resident.Names.Count > 0) _donorSprites = resident.Names;
        SettingsArtLadder.Use(new SettingsArt(parts, _donorSprites));
    }

    /// <summary>
    /// The sprite to draw for <paramref name="part"/>, how (sliced or simple) and its pixels-per-unit
    /// multiplier; false for the kit's own drawing. The color is the caller's (<see cref="GameSettingsLook"/>).
    /// </summary>
    internal static bool TryPart(SettingsArtPart part, out Sprite? sprite, out Image.Type type, out float ppuMultiplier)
    {
        int i = (int)part;
        sprite = null;
        type = SettingsArtLadder.Sliced(part) ? Image.Type.Sliced : Image.Type.Simple;
        ppuMultiplier = SettingsArtLadder.PpuMultiplier(part);
        if (i < 0 || i >= PartCount) return false;
        if (Sources[i] is GameArtSource.Kit or GameArtSource.Unresolved) return false;
        if (Art.OwnedUsable(i)) sprite = Art.Owned(i);
        else if (Art.HeldUsable(i))
        {
            try { sprite = Art.Held(i)!.sprite; }
            catch { return false; }
        }
        else return false;
        return sprite != null;
    }

    /// <summary>Core unload: destroy the holder and every owned copy, forget every reference and reset the budgets.</summary>
    internal static void Release()
    {
        Art.Release();
        for (int i = 0; i < PartCount; i++)
        {
            HeldSource[i] = null;
            HeldDonor[i] = "";
            HeldName[i] = "";
            Sources[i] = GameArtSource.Unresolved;
        }

        _donorSprites = new List<string>();
        _epoch = int.MinValue;
        _liveScanEpoch = int.MinValue;
        _liveScanFrame = int.MinValue;
        _residentScans = 0;
        _residentScanFrame = int.MinValue;
        _preparedFrame = int.MinValue;
        LoggedFallThrough.Clear();
        _idleEpoch = int.MinValue;
        _idleStage = 0;
        SettingsArtLadder.Reset();
    }

    private static void Hold(int i, Sprite sprite, string donor, bool copyNow)
    {
        HeldSource[i] = sprite;
        HeldDonor[i] = donor ?? "";
        HeldName[i] = SafeName(sprite);
        // The look a later generation adopts the kept copy with: the part's own image type, no tint.
        Art.SetKeepLook(i, SettingsArtLadder.Sliced((SettingsArtPart)i), Color.white);
        Art.Hold(i, sprite, copyNow);
    }

    /// <summary>BUTTONART1: draw part <paramref name="i"/> from a copy an earlier generation kept (no donor held).</summary>
    private static bool Adopt(int i, Sprite? kept, in GameArtKept.Entry entry)
    {
        if (!Art.Adopt(i, kept, entry)) return false;
        HeldSource[i] = null;
        HeldDonor[i] = GameArtKept.DonorKept;
        HeldName[i] = entry.Sprite;
        return true;
    }

    private static GameArtPart PartOf(int i, GameArtSource source)
    {
        if (source is GameArtSource.Kit or GameArtSource.Unresolved) return GameArtPart.Kit;
        if (HeldSource[i] == null && !Art.OwnedUsable(i)) return GameArtPart.Kit;
        Sprite? drawn = null;
        try { drawn = Art.OwnedUsable(i) ? Art.Owned(i) : Art.Held(i)?.sprite; } catch { /* */ }
        string name = HeldSource[i] != null ? SafeName(HeldSource[i]!) : HeldName[i] ?? "";
        return new GameArtPart(source, HeldDonor[i] ?? "", name, drawn, Art.CopyNote(i));
    }

    private static void LogFallThrough(string line)
    {
        if (!LoggedFallThrough.Add(line)) return;
        try { Plugin.Logger?.LogDiag(line); } catch { /* */ }
    }

    // ---- reads -------------------------------------------------------------------------------

    private static void ReadMenus(Found found, bool resident)
    {
        var menus = resident ? Loaded<OptionsMenu>() : Scene<OptionsMenu>();
        var panels = new List<(Object Panel, string Donor)>();
        var seen = new HashSet<IntPtr>();
        void AddPanel(Object? panel, string donor)
        {
            if (panel == null || !AliveObject(panel)) return;
            try { if (!seen.Add(panel.Pointer)) return; } catch { return; }
            panels.Add((panel, donor));
        }

        foreach (var menu in menus)
        {
            try { AddPanel(menu.GeneralPanel, SettingsArtLadder.DonorOptionsMenu); } catch { /* */ }
            try { AddPanel(menu.ControlsPanel, SettingsArtLadder.DonorOptionsMenu); } catch { /* */ }
            try { AddPanel(menu.GraphicsPanel, SettingsArtLadder.DonorOptionsMenu); } catch { /* */ }
            try { AddPanel(menu.SoundPanel, SettingsArtLadder.DonorOptionsMenu); } catch { /* */ }
            try { Collect(found, menu.DetailsDivider, SettingsArtLadder.DonorOptionsMenu); } catch { /* */ }
            // Round D: the Options page's Back button (Button_Normal), a second donor of the standard button.
            try { Collect(found, menu.BackButton != null ? menu.BackButton.gameObject : null, SettingsArtLadder.DonorOptionsMenu); } catch { /* */ }
        }

        foreach (var p in resident ? Loaded<OptionsPanel_Interface>() : Scene<OptionsPanel_Interface>()) AddPanel(p, SettingsArtLadder.DonorOptionsPanel);
        foreach (var p in resident ? Loaded<OptionsPanel_Controls>() : Scene<OptionsPanel_Controls>()) AddPanel(p, SettingsArtLadder.DonorOptionsPanel);
        foreach (var p in resident ? Loaded<OptionsPanel_Graphics>() : Scene<OptionsPanel_Graphics>()) AddPanel(p, SettingsArtLadder.DonorOptionsPanel);
        foreach (var p in resident ? Loaded<OptionsPanel_Sound>() : Scene<OptionsPanel_Sound>()) AddPanel(p, SettingsArtLadder.DonorOptionsPanel);
        foreach (var p in resident ? Loaded<OptionsPanel_Accessibility>() : Scene<OptionsPanel_Accessibility>()) AddPanel(p, SettingsArtLadder.DonorOptionsPanel);

        // DIALOGS2: the game's own popups — every CommonPopup (the confirm, resident and live at the title
        // and in a world, probe D1) and the main-menu canvas's popup prefab.
        var popups = resident ? Loaded<global::CommonPopup>() : Scene<global::CommonPopup>();
        foreach (var popup in popups)
        {
            try { Collect(found, popup.gameObject, SettingsArtLadder.DonorPopup); } catch { /* */ }
        }

        found.AnyMenu = menus.Count > 0 || panels.Count > 0 || popups.Count > 0;
        var roots = new HashSet<IntPtr>();
        foreach (var (panel, donor) in panels)
        {
            foreach (var root in PrefabRoots(panel))
            {
                if (root == null || !AliveObject(root)) continue;
                try { if (!roots.Add(root.Pointer)) continue; } catch { continue; }
                Collect(found, root, donor);
            }
        }
    }

    /// <summary>A panel's prefab roots (every <c>OptionsPanel_*</c> type has its own fields; client dump 447114-448263).</summary>
    private static List<GameObject?> PrefabRoots(Object panel)
    {
        var list = new List<GameObject?>(6);
        GameObject? Go(Func<Component?> read)
        {
            try { var c = read(); return c == null ? null : c.gameObject; }
            catch { return null; }
        }

        GameObject? Bar(Func<ScrollRect?> read)
        {
            try { var s = read(); return s == null || s.verticalScrollbar == null ? null : s.verticalScrollbar.gameObject; }
            catch { return null; }
        }

        if (panel.TryCast<OptionsPanel_Interface>() is { } pi)
        {
            list.Add(Go(() => pi.CheckboxPrefab)); list.Add(Go(() => pi.HeaderPrefab)); list.Add(Go(() => pi.DropdownPrefab));
            list.Add(Go(() => pi.SliderPrefab)); list.Add(Bar(() => pi.ScrollRect));
        }
        else if (panel.TryCast<OptionsPanel_Controls>() is { } pc)
        {
            list.Add(Go(() => pc.CheckboxPrefab)); list.Add(Go(() => pc.HeaderPrefab)); list.Add(Go(() => pc.ButtonPrefab));
            list.Add(Go(() => pc.SliderPrefab)); list.Add(Go(() => pc.DropdownPrefab)); list.Add(Bar(() => pc.ScrollRect));
        }
        else if (panel.TryCast<OptionsPanel_Graphics>() is { } pg)
        {
            list.Add(Go(() => pg.CheckboxPrefab)); list.Add(Go(() => pg.HeaderPrefab)); list.Add(Go(() => pg.DropdownPrefab));
            list.Add(Go(() => pg.SliderPrefab)); list.Add(Go(() => pg.ButtonPrefab)); list.Add(Bar(() => pg.ScrollRect));
        }
        else if (panel.TryCast<OptionsPanel_Sound>() is { } ps)
        {
            list.Add(Go(() => ps.CheckboxPrefab)); list.Add(Go(() => ps.HeaderPrefab)); list.Add(Go(() => ps.DropdownPrefab));
            list.Add(Go(() => ps.SliderPrefab)); list.Add(Bar(() => ps.ScrollRect));
        }
        else if (panel.TryCast<OptionsPanel_Accessibility>() is { } pa)
        {
            list.Add(Go(() => pa.HeaderPrefab)); list.Add(Go(() => pa.CheckboxPrefab)); list.Add(Go(() => pa.SliderPrefab));
            list.Add(Bar(() => pa.ScrollRect));
        }

        return list;
    }

    /// <summary>Every image sprite and selectable sprite state under <paramref name="root"/>, matched to parts by name.</summary>
    private static void Collect(Found found, GameObject? root, string donor)
    {
        if (root == null || !AliveObject(root)) return;
        try
        {
            var images = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; images != null && i < images.Length; i++)
            {
                try { Offer(found, images[i] == null ? null : images[i].sprite, donor); }
                catch { /* next image */ }
            }
        }
        catch { /* no images */ }

        try
        {
            var selectables = root.GetComponentsInChildren<Selectable>(true);
            for (int s = 0; selectables != null && s < selectables.Length; s++)
            {
                var sel = selectables[s];
                if (sel == null) continue;
                try { OfferState(found, sel.spriteState, donor); }
                catch { /* a Unity struct read that failed: skip */ }
                try
                {
                    var stun = sel.TryCast<SimpleStunSelectable>();
                    var targets = stun?.TargetSettings;
                    int n = targets == null ? 0 : targets.Length;
                    for (int t = 0; t < n; t++)
                    {
                        try { if (targets![t] != null) OfferState(found, targets[t].SpriteState, donor); }
                        catch { /* next target */ }
                    }
                }
                catch { /* not a stun selectable */ }
            }
        }
        catch { /* no selectables */ }
    }

    private static void OfferState(Found found, SpriteState state, string donor)
    {
        try { Offer(found, state.highlightedSprite, donor); } catch { /* */ }
        try { Offer(found, state.pressedSprite, donor); } catch { /* */ }
        try { Offer(found, state.selectedSprite, donor); } catch { /* */ }
        try { Offer(found, state.disabledSprite, donor); } catch { /* */ }
    }

    private static void Offer(Found found, Sprite? sprite, string donor)
    {
        if (sprite == null || !AliveObject(sprite)) return;
        string name = SafeName(sprite);
        if (name.Length == 0) return;
        if (found.Names.Count < 96 && !found.Names.Contains(name)) found.Names.Add(name);
        if (SettingsArtLadder.PartOf(name) is not { } part) return;
        int i = (int)part;
        if (found.Parts[i] != null || !AliveSprite(sprite) || !ShapeFits(part, sprite)) return;
        found.Parts[i] = sprite;
        found.Donor[i] = donor;
    }

    /// <summary>BUTTONART1: a button part must have a game button's shape (<see cref="SettingsArtLadder.ShapeFits"/>).</summary>
    private static bool ShapeFits(SettingsArtPart part, Sprite sprite)
    {
        if (!SettingsArtLadder.IsButton(part)) return true;
        try
        {
            var rect = sprite.rect;
            var b = sprite.border;
            return SettingsArtLadder.ShapeFits(part, rect.width, rect.height, (b.x, b.y, b.z, b.w));
        }
        catch { return false; }
    }

    /// <summary>
    /// In a world the prefabs may be gone: loaded sprites of the parts' primary names, and the copies an
    /// earlier Core generation kept in Bootstrap's process-lifetime table (<see cref="GameArtKept"/>), both
    /// from one <c>FindObjectsOfTypeAll(Sprite)</c>. BUTTONART1: an alternate name is never matched here
    /// (the HUD's own <c>Button_Normal</c> is a flat red square), and a button must have a button's shape.
    /// </summary>
    private static void ReadLoadedSprites(Found found)
    {
        bool want = false;
        for (int i = 0; i < PartCount && !want; i++) want = found.Parts[i] == null && !SettingsArtLadder.Optional((SettingsArtPart)i);
        if (!want) return;
        var type = Il2CppType.Of<Sprite>();
        if (type == null) return;
        var all = Resources.FindObjectsOfTypeAll(type);
        var haveKept = new bool[PartCount];
        for (int i = 0; all != null && i < all.Length; i++)
        {
            Sprite? sprite;
            try { sprite = all[i] == null ? null : all[i].TryCast<Sprite>(); }
            catch { continue; }
            if (sprite == null || !AliveSprite(sprite)) continue;
            string name = SafeName(sprite);
            if (GameArtKept.TryParse(name, out var entry))
            {
                for (int k = 0; k < PartCount; k++)
                {
                    if (!GameArtKept.IsFor(entry, AssetTag, PartNames[k])) continue;
                    if (!GameArtKept.Prefer(haveKept[k], found.KeptEntry[k].Sprite, entry.Sprite, SettingsArtLadder.SpriteName((SettingsArtPart)k)))
                        break;
                    haveKept[k] = true;
                    found.Kept[k] = sprite;
                    found.KeptEntry[k] = entry;
                    break;
                }

                continue;
            }

            if (SettingsArtLadder.PrimaryPartOf(name) is not { } part) continue;
            int p = (int)part;
            if (found.Parts[p] != null || !ShapeFits(part, sprite)) continue;
            found.Parts[p] = sprite;
            found.Donor[p] = SettingsArtLadder.DonorLoadedSprite;
        }
    }
}
#endif
