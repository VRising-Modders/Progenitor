using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public sealed partial class PluginHud
{
    private readonly HashSet<string> _noticeDropLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Show <paramref name="text"/> (and an optional second, muted <paramref name="detail"/> line) as a notification in
    /// the game's own style: on the game's center announcement banner when it is on screen, otherwise on a framework band
    /// in the same place and style (main menu, HUD hidden). One notice shows at a time, first posted first shown, across
    /// every plugin; the same text again within a few seconds is dropped as a duplicate, a plugin may post at most six
    /// in ten seconds, and at most eight wait (the oldest waiting one is dropped).
    ///
    /// <para><paramref name="kind"/> tints the framework band's rules (gold, green, amber, red); the game's banner has
    /// one style and shows every kind alike. <paramref name="seconds"/> 0 is the game's default (the banner's own
    /// animation length; four seconds on the band); on the banner a longer value holds the slot longer but cannot
    /// lengthen the animation itself. Client only; on the server it logs once and returns a dropped notice.</para>
    ///
    /// <para><b>Status: experimental</b> (built and unit-tested; not yet verified live).</para>
    /// </summary>
    public IUiNotice Notify(string text, string detail = "", UiNoticeKind kind = UiNoticeKind.Info, double seconds = 0)
    {
        if (ServerSide("Notify")) return UiNoticeHandle.Inert(text ?? "", detail ?? "", kind);
        var notice = UnityUiRuntime.HudNotify(_ctx.Owner, text ?? "", detail ?? "", kind, seconds);
        if (notice.State == UiNoticeState.Dropped && notice.DropReason == "rate-limited"
            && _noticeDropLogged.Add(notice.DropReason))
        {
            _ctx.Log.LogWarning("Ui: ctx.Hud.Notify is over its rate limit (" + UiNoticeRules.RateLimitCount
                + " notices in " + UiNoticeRules.RateLimitWindowSeconds + " s); extra notices are dropped. Logged once.");
        }

        return notice;
    }

    static partial void DropNotices(object owner)
    {
        try { UnityUiRuntime.HudDropNotices(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ctx.Hud notices of an unloading plugin were not dropped: " + e.Message); }
    }
}
