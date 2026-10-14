#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using Stunlock.Localization;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;
using ModStrings = Progenitor.Localization.Localization;

namespace Progenitor.Ui;

/// <summary>
/// P7 <c>ctx.Hud.Notify</c>, the Unity half. A notice goes on the game's own center announcement banner when it is on
/// screen: <c>AnnouncementHUDSystem._AnnouncementParent.ShowBasicAnnouncement(header, subHeader)</c>, the same call
/// the client's <c>BasicAnnouncement</c> component drives, with the text registered as two mod localization keys.
/// That call is an ordinary instance method taking <c>LocalizationKey</c> by value; the interop wrapper passes each
/// argument's address through <c>il2cpp_runtime_invoke</c> (the standard by-value path, no by-ref struct). In the
/// game's metadata it restarts the achievement banner's animation and overwrites its two texts (an all-zero key hides
/// that line); it keeps no queue, so this side keeps one (<see cref="UiNoticeQueue"/>) and waits while the game's own
/// banner is busy or the player has hidden the HUD (then nothing is drawn; a notice waits at most 30 s). Without a
/// banner (main menu, no client world, the HUD not built) the notice is drawn on a
/// framework band in the same place and the same style. Which path is in use is logged when it changes.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    private static readonly UiNoticeQueue NoticeQueue = new();
    private static readonly object NoticeKeysOwner = new();
    private static readonly List<IDisposable> NoticeKeys = new();
    private static bool _noticeTicking;
    private static bool _noticeHooked;
    private static string _noticePath = "";
    private static bool _hudHidden;
    private static AnnouncementHUDParent? _hudCanvasOf;
    private static Canvas? _hudCanvas;

    // The game's banner, cached per world epoch (a new client world builds a new HUD).
    private static AnnouncementHUDSystem? _announceSystem;
    private static AnnouncementHUDParent? _announceParent;
    private static int _announceEpoch = -1;
    private static double _announceRetryAt;

    // The framework band.
    private static GameObject? _bandCanvas, _band;
    private static CanvasGroup? _bandGroup;
    private static Image? _bandRuleTop, _bandRuleBottom;
    private static TextMeshProUGUI? _bandHeader, _bandDetail;
    private static Sprite? _bandSprite;
    private static Texture2D? _bandTexture;
    private static UiNoticeHandle? _bandNotice;

    /// <summary>The process-wide queue (the bridge and tests read it).</summary>
    internal static UiNoticeQueue HudNotices => NoticeQueue;

    /// <summary>Post a notice for <paramref name="owner"/>; starts the frame tick while there is work.</summary>
    internal static UiNoticeHandle HudNotify(object owner, string text, string detail, UiNoticeKind kind, double seconds)
    {
        if (!_noticeHooked)
        {
            _noticeHooked = true;
            NoticeQueue.Withdrawn += OnNoticeWithdrawn;
            NoticeQueue.Expired += OnNoticeExpired;
            UiWidgetLifetime.Register("hud-notices", TeardownNotices);
        }

        var notice = NoticeQueue.Enqueue(owner, text, detail, kind, seconds, HudNow(),
            Progenitor.WorldState.WorldEpoch.Current);
        if (notice.State == UiNoticeState.Queued) EnsureNoticeTick();
        return notice;
    }

    /// <summary>An unloading plugin's notices: queued ones dropped, its showing one ended.</summary>
    internal static int HudDropNotices(object owner) => NoticeQueue.DropOwner(owner);

    private static void EnsureNoticeTick()
    {
        if (_noticeTicking) return;
        _noticeTicking = true;
        Plugin.Frame += NoticeTick;
    }

    private static void StopNoticeTick()
    {
        if (!_noticeTicking) return;
        _noticeTicking = false;
        Plugin.Frame -= NoticeTick;
    }

    private static void NoticeTick()
    {
        try
        {
            double now = HudNow();
            bool wait = NoticeHostWait(now);
            // P7b: the game's full-screen menu hides the HUD; the notice waits for it to close, and that wait does not
            // count toward its expiry (UiNoticeRules.MaxHeldSeconds bounds it).
            bool menuHold = wait && _hudHidden && GameMenuOpenNow();
            NoticeQueue.Next(now, wait, PresentNotice, Progenitor.WorldState.WorldEpoch.Current, menuHold);
            UpdateBand(now);
            if (!NoticeQueue.HasWork && _bandNotice == null) StopNoticeTick();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud.Notify tick failed: " + e.Message + "; the queue is cleared.");
            NoticeQueue.Clear();
            HideBand();
            StopNoticeTick();
        }
    }

    /// <summary>Whether the game's full-screen menu is open (the HUD is hidden by the menu, not by the player).</summary>
    private static bool GameMenuOpenNow()
    {
        try { return GameMenuHistory.Probe.MenuOpen; }
        catch { return false; }
    }

    private static void OnNoticeExpired(UiNoticeHandle notice)
    {
        Plugin.Logger?.LogInfo("Ui: ctx.Hud.Notify dropped a notice unshown (" + notice.DropReason
            + (notice.DropReason == "expired"
                ? ": the game's banner stayed busy or the HUD stayed hidden for " + UiNoticeRules.MaxWaitSeconds + " s"
                : ": the world it was posted in is gone")
            + "): \"" + notice.Header + "\".");
    }

    /// <summary>Present <paramref name="notice"/>; returns the seconds it holds the screen (0: could not).</summary>
    private static double PresentNotice(UiNoticeHandle notice)
    {
        var parent = AnnouncementParent(HudNow(), out string why);
        if (parent == null && _hudHidden) return 0; // the player hid the HUD: never draw over that choice
        if (parent != null)
        {
            double length = ShowOnBanner(parent, notice);
            if (length > 0)
            {
                LogNoticePath("banner", "Ui: ctx.Hud.Notify shows notices on the game's announcement banner "
                    + "(AnnouncementHUDParent.ShowBasicAnnouncement); its animation is " + length.ToString("0.0") + " s.");
                // On the banner the caller's seconds can only lengthen the slot: the animation is the game's.
                return Math.Max(length, notice.RequestedSeconds > 0 ? UiNoticeRules.Seconds(notice.RequestedSeconds, length) : length);
            }

            why = "the banner call failed";
        }

        if (!ShowOnBand(notice)) return 0;
        LogNoticePath("band", "Ui: ctx.Hud.Notify draws notices on the framework band (" + why + ").");
        return UiNoticeRules.Seconds(notice.RequestedSeconds, UiNoticeRules.DefaultSeconds);
    }

    private static void LogNoticePath(string path, string line)
    {
        if (string.Equals(_noticePath, path, StringComparison.Ordinal)) return;
        _noticePath = path;
        Plugin.Logger?.LogInfo(line);
    }

    // ---- the game's banner ----

    /// <summary>The game's announcement parent when it can show a notice now, or null with the reason.</summary>
    private static AnnouncementHUDParent? AnnouncementParent(double now, out string why)
    {
        why = "";
        _hudHidden = false;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (epoch != _announceEpoch)
        {
            _announceEpoch = epoch;
            _announceSystem = null;
            _announceParent = null;
            _announceRetryAt = 0;
        }

        if (_announceParent == null || !Alive(_announceParent))
        {
            _announceParent = null;
            if (now < _announceRetryAt)
            {
                why = "the game's banner is not built";
                return null;
            }

            _announceRetryAt = now + 2.0;
            var world = Worlds.Client;
            if (world == null)
            {
                why = "no client world";
                return null;
            }

            try
            {
                _announceSystem = world.GetExistingSystemManaged<AnnouncementHUDSystem>();
                _announceParent = _announceSystem?._AnnouncementParent;
            }
            catch (Exception e)
            {
                why = "the game's banner could not be read: " + e.Message;
                _announceSystem = null;
                _announceParent = null;
                return null;
            }

            if (_announceParent == null || !Alive(_announceParent))
            {
                _announceParent = null;
                why = "the game's banner is not built";
                return null;
            }
        }

        try
        {
            if (HudHidden(_announceParent))
            {
                why = "the HUD is hidden";
                _hudHidden = true;
                return null;
            }

            if (_announceParent.AnnouncementAchievement == null)
            {
                why = "the banner has no basic-announcement slot";
                return null;
            }
        }
        catch (Exception e)
        {
            why = "the game's banner could not be read: " + e.Message;
            return null;
        }

        return _announceParent;
    }

    /// <summary>
    /// True when the HUD's banner holder is inactive or its root canvas is disabled: the player (or a fullscreen game
    /// menu) has hidden the HUD. The root canvas is looked up once per banner object.
    /// </summary>
    private static bool HudHidden(AnnouncementHUDParent parent)
    {
        if (!parent.gameObject.activeInHierarchy) return true;
        if (!ReferenceEquals(_hudCanvasOf, parent) || !Alive(_hudCanvas))
        {
            _hudCanvasOf = parent;
            Canvas? canvas = null;
            try { canvas = parent.GetComponentInParent<Canvas>(); } catch { /* no canvas: treat as shown */ }
            try { if (canvas != null) canvas = canvas.rootCanvas; } catch { /* keep the nearest */ }
            _hudCanvas = canvas;
        }

        try { return _hudCanvas != null && !_hudCanvas.isActiveAndEnabled; }
        catch { return false; }
    }

    /// <summary>
    /// True while the queue must wait: the player has hidden the HUD (nothing is drawn, not even the band), or the
    /// game's own banner is playing something: a day/night announcement (<c>AnnouncementHUDSystem._IsShowingAnnouncement</c>)
    /// or the achievement banner's animation, whose object the motion deactivates when it finishes (unless
    /// <c>DontDisableOnFinish</c>). A notice is never shown over either; it waits and expires after
    /// <see cref="UiNoticeRules.MaxWaitSeconds"/>. Read-only.
    /// </summary>
    private static bool NoticeHostWait(double now)
    {
        if (NoticeQueue.QueuedCount == 0) return false;
        var parent = AnnouncementParent(now, out _);
        if (_hudHidden) return true;
        if (parent == null) return false;
        try
        {
            if (_announceSystem != null && _announceSystem._IsShowingAnnouncement) return true;
            var motion = parent.AnnouncementAchievement;
            if (motion == null || motion.DontDisableOnFinish) return false;
            return motion.gameObject.activeSelf;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Show on the game's banner; returns the animation's length in seconds, or 0 on failure.</summary>
    private static double ShowOnBanner(AnnouncementHUDParent parent, UiNoticeHandle notice)
    {
        try
        {
            string headerKey = "progenitor.hud.notice." + notice.Serial + ".header";
            KeepNoticeKey(ModStrings.Register(headerKey, notice.Header, "English", NoticeKeysOwner));
            LocalizationKey header = ModStrings.KeyFor(headerKey);
            LocalizationKey sub = default;
            if (notice.Detail.Length > 0)
            {
                string detailKey = "progenitor.hud.notice." + notice.Serial + ".detail";
                KeepNoticeKey(ModStrings.Register(detailKey, notice.Detail, "English", NoticeKeysOwner));
                sub = ModStrings.KeyFor(detailKey);
            }

            parent.ShowBasicAnnouncement(header, sub);

            double length = 0;
            try
            {
                var motion = parent.AnnouncementAchievement;
                if (motion != null) length = Math.Max(0f, motion.Delay) + Math.Max(0f, motion.Duration);
            }
            catch { /* the length is a nicety */ }

            return length > 0.5 && length < 60 ? length : UiNoticeRules.DefaultSeconds;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud.Notify: the game's banner refused a notice (" + e.Message + "); using the framework band.");
            _announceParent = null;
            _announceRetryAt = HudNow() + 30.0;
            return 0;
        }
    }

    /// <summary>
    /// Keep the last few notice strings in the game's table (a banner may still be fading out the previous one); older
    /// ones are removed.
    /// </summary>
    private static void KeepNoticeKey(IDisposable registration)
    {
        NoticeKeys.Add(registration);
        while (NoticeKeys.Count > 4)
        {
            try { NoticeKeys[0].Dispose(); } catch { /* */ }
            NoticeKeys.RemoveAt(0);
        }
    }

    // ---- the framework band ----

    private static bool ShowOnBand(UiNoticeHandle notice)
    {
        if (!EnsureBand()) return false;
        try
        {
            var accent = ToColor(UiNoticeBand.RuleColor(notice.Kind));
            if (_bandRuleTop != null) _bandRuleTop.color = accent;
            if (_bandRuleBottom != null) _bandRuleBottom.color = accent;
            bool hasDetail = notice.Detail.Length > 0;
            float headerWidth = 0f, detailWidth = 0f;
            if (_bandHeader != null)
            {
                _bandHeader.text = notice.Header;
                headerWidth = _bandHeader.GetPreferredValues(notice.Header, float.PositiveInfinity, float.PositiveInfinity).x;
            }

            if (_bandDetail != null)
            {
                _bandDetail.text = notice.Detail;
                _bandDetail.gameObject.SetActive(hasDetail);
                if (hasDetail)
                    detailWidth = _bandDetail.GetPreferredValues(notice.Detail, float.PositiveInfinity, float.PositiveInfinity).x;
            }

            float width = UiNoticeBand.Width(headerWidth, detailWidth);
            float height = UiNoticeBand.Height(hasDetail);
            var rt = _band!.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, height);
            float inner = width - 2f * UiNoticeBand.PadX;
            if (_bandHeader != null)
            {
                var hr = _bandHeader.rectTransform;
                hr.sizeDelta = new Vector2(inner, UiNoticeBand.HeaderHeight);
                hr.anchoredPosition = new Vector2(0f, -UiNoticeBand.PadY);
            }

            if (_bandDetail != null)
            {
                var dr = _bandDetail.rectTransform;
                dr.sizeDelta = new Vector2(inner, UiNoticeBand.DetailHeight);
                dr.anchoredPosition = new Vector2(0f, -UiNoticeBand.PadY - UiNoticeBand.HeaderHeight);
            }

            if (_bandGroup != null) _bandGroup.alpha = 0f;
            _bandCanvas!.SetActive(true);
            _bandNotice = notice;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud.Notify: the framework band failed (" + e.Message + ").");
            HideBand();
            return false;
        }
    }

    /// <summary>Fade the band in and out with its notice; hide it when its notice is no longer the one showing.</summary>
    private static void UpdateBand(double now)
    {
        var shown = _bandNotice;
        if (shown == null) return;
        if (!ReferenceEquals(NoticeQueue.Showing, shown) || shown.State != UiNoticeState.Showing)
        {
            HideBand();
            return;
        }

        try { if (_bandGroup != null) _bandGroup.alpha = UiNoticeBand.Alpha(now, shown.ShownAt, shown.EndsAt); }
        catch { HideBand(); }
    }

    private static void HideBand()
    {
        _bandNotice = null;
        try { if (Alive(_bandCanvas)) _bandCanvas!.SetActive(false); } catch { /* */ }
    }

    private static void OnNoticeWithdrawn(UiNoticeHandle notice)
    {
        if (ReferenceEquals(_bandNotice, notice)) HideBand();
    }

    private static bool EnsureBand()
    {
        if (Alive(_bandCanvas) && Alive(_band)) return true;
        DestroyBand();
        var canvas = CreateHudCanvas("hud-notice", UiOwnership.OverlaySorting + 20);
        if (canvas == null) return false;
        try
        {
            if (!Alive(_bandSprite))
                _bandSprite = HudSprite("hud-notice-band", UiNoticeBandGlyph.Width, 4, (x, _) => UiNoticeBandGlyph.Alpha(x), out _bandTexture);

            var band = HudChild(canvas, "band", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(UiNoticeBand.MinWidth, UiNoticeBand.Height(true)));
            band.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, -UiNoticeBand.Top);
            _bandGroup = TryAdd<CanvasGroup>(band);
            if (_bandGroup != null)
            {
                _bandGroup.interactable = false;
                _bandGroup.blocksRaycasts = false;
            }

            var ground = HudChild(band, "ground", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero);
            Stretch(ground);
            HudImage(ground, _bandSprite, ToColor(UiNoticeBand.Ground));

            _bandRuleTop = Rule(band, "rule-top", top: true);
            _bandRuleBottom = Rule(band, "rule-bottom", top: false);

            var header = HudChild(band, "header", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(UiNoticeBand.MinWidth, UiNoticeBand.HeaderHeight));
            _bandHeader = HudText(header, UiNoticeBand.HeaderSize, ToColor(MenuPalette.Text));
            var detail = HudChild(band, "detail", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(UiNoticeBand.MinWidth, UiNoticeBand.DetailHeight));
            _bandDetail = HudText(detail, UiNoticeBand.DetailSize, ToColor(MenuPalette.Muted));

            _bandCanvas = canvas;
            _band = band;
            canvas.SetActive(false);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud.Notify: the framework band could not be built (" + e.Message + ").");
            TryDestroyImmediate(canvas);
            DestroyBand();
            return false;
        }

        Image? Rule(GameObject band, string name, bool top)
        {
            var anchor = new Vector2(0.5f, top ? 1f : 0f);
            var go = HudChild(band, name, anchor, anchor, new Vector2(0f, UiNoticeBand.RuleThickness));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, top ? 1f : 0f);
            rt.anchorMax = new Vector2(1f, top ? 1f : 0f);
            rt.sizeDelta = new Vector2(0f, UiNoticeBand.RuleThickness);
            // The rules fade out at both ends like the ground (the same gradient strip, tinted).
            return HudImage(go, _bandSprite, ToColor(MenuPalette.Gold.WithAlpha(UiNoticeBand.RuleAlpha)));
        }
    }

    private static void DestroyBand()
    {
        if (Alive(_bandCanvas)) TryDestroy(_bandCanvas, immediate: false);
        _bandCanvas = _band = null;
        _bandGroup = null;
        _bandRuleTop = _bandRuleBottom = null;
        _bandHeader = _bandDetail = null;
        _bandNotice = null;
    }

    /// <summary>Core teardown (every unload path, via <see cref="UiWidgetLifetime"/>): queue, band, strings, art.</summary>
    private static void TeardownNotices()
    {
        StopNoticeTick();
        NoticeQueue.Withdrawn -= OnNoticeWithdrawn;
        NoticeQueue.Expired -= OnNoticeExpired;
        _hudCanvasOf = null;
        _hudCanvas = null;
        _hudHidden = false;
        _noticeHooked = false;
        NoticeQueue.Clear();
        DestroyBand();
        FreeHudAsset(_bandSprite);
        FreeHudAsset(_bandTexture);
        _bandSprite = null;
        _bandTexture = null;
        foreach (var key in NoticeKeys)
        {
            try { key.Dispose(); } catch { /* */ }
        }

        NoticeKeys.Clear();
        try { ModStrings.RemoveAll(NoticeKeysOwner); } catch { /* */ }
        _announceSystem = null;
        _announceParent = null;
        _announceEpoch = -1;
        _noticePath = "";
    }
}
#endif
