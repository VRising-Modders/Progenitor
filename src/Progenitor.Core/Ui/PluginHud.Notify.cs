using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

public sealed partial class PluginHud
{
    private readonly HashSet<string> _noticeDropLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Show <paramref name="text"/> (and an optional second, muted <paramref name="detail"/> line) as a notification in
    /// the game's own style: on the game's center announcement banner when it is on screen, otherwise on a framework band
    /// in the same place and style (main menu, HUD not built). While the game's banner is playing or the player has
    /// hidden the HUD a notice waits (never overwriting it, never drawn over a hidden HUD) and is dropped unshown after
    /// 30 s; a notice still waiting when the world changes is dropped. One notice shows at a time, first posted first shown, across
    /// every plugin; the same text again within a few seconds is dropped as a duplicate, a plugin may post at most six
    /// in ten seconds, and at most eight wait (the oldest waiting one is dropped).
    ///
    /// <para><paramref name="kind"/> tints the framework band's rules (gold, green, amber, red); the game's banner has
    /// one style and shows every kind alike. <paramref name="seconds"/> 0 is the game's default (the banner's own
    /// animation length; four seconds on the band); on the banner a longer value holds the slot longer but cannot
    /// lengthen the animation itself. Client only; on the server it logs once and returns a dropped notice.</para>
    ///
    /// <para><b>The mod's name in front.</b> WINDOWS1: <paramref name="text"/> is shown as <c>&lt;Mod Name&gt;: text</c>
    /// (the plugin's BepInEx name), so the player can tell which mod spoke; a text that already starts with the name is
    /// left as it is. Pass <paramref name="prefix"/> false for a notice that names itself another way.</para>
    ///
    /// <para><b>Status: experimental</b> (built and unit-tested; not yet verified live).</para>
    /// </summary>
    public IUiNotice Notify(string text, string detail = "", UiNoticeKind kind = UiNoticeKind.Info, double seconds = 0, bool prefix = true)
    {
        text = ModMessagePrefix.Apply(_ctx.Name, text, prefix);
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

    /// <summary>
    /// The four-argument form plugins built before WINDOWS1 were compiled against: the same as
    /// <see cref="Notify(string, string, UiNoticeKind, double, bool)"/> with the prefix on. Kept so they load unchanged;
    /// new code calls the form with <c>prefix</c>.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public IUiNotice Notify(string text, string detail, UiNoticeKind kind, double seconds) =>
        Notify(text, detail, kind, seconds, prefix: true);

    static partial void DropNotices(object owner)
    {
        try { UnityUiRuntime.HudDropNotices(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ctx.Hud notices of an unloading plugin were not dropped: " + e.Message); }
    }
}
