using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free half of the client <c>mods_ui</c> op: argument policy and the
/// <c>{ok, action, before, after, detail, elapsedMs}</c> envelope. Live Unity
/// (owned-button invoke, native tab pointer path, <c>Screen.SetResolution</c>)
/// needs a running client and is not exercised here.
/// </summary>
public class ModsUiOpsTests
{
    [Fact]
    public void Parse_requires_action()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{}")));
        Assert.Contains("args.action is required", missing.Message);

        var empty = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"\"}")));
        Assert.Contains("args.action is required", empty.Message);
    }

    [Theory]
    [InlineData("{\"action\":\"spawn\"}")]
    [InlineData("{\"action\":\"mods_open\"}")]
    [InlineData("{\"action\":\"toggle\"}")]
    public void Parse_refuses_an_unknown_action(string json)
    {
        var e = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json(json)));
        Assert.Contains("args.action must be", e.Message);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("open")]
    [InlineData("close")]
    [InlineData("back")]
    [InlineData("select")]
    [InlineData("select_tab")]
    [InlineData("search")]
    [InlineData("scroll")]
    [InlineData("widget_read")]
    [InlineData("widget_click")]
    [InlineData("native_tab")]
    [InlineData("resolution")]
    [InlineData("rects")]
    [InlineData("window")]
    [InlineData("scene")]
    [InlineData("menu")]
    [InlineData("hit")]
    [InlineData("tabs")]
    public void Allowed_actions_are_the_documented_set(string action)
    {
        Assert.Contains(action, ModsUiArgs.Actions);
    }

    [Fact]
    public void Close_and_back_are_separate_actions_and_both_defer_readback()
    {
        var close = ModsUiArgs.Parse(Json("{\"action\":\"close\"}"));
        Assert.Equal("close", close.Action);
        Assert.True(close.NeedsNextDrain);

        var back = ModsUiArgs.Parse(Json("{\"action\":\"back\"}"));
        Assert.Equal("back", back.Action);
        Assert.True(back.NeedsNextDrain);
    }

    [Theory]
    [InlineData("select", true)]
    [InlineData("select_tab", true)]
    [InlineData("search", true)]
    [InlineData("scroll", true)]
    [InlineData("widget_read", true)]
    [InlineData("widget_click", true)]
    [InlineData("status", false)]
    [InlineData("open", false)]
    [InlineData("close", false)]
    [InlineData("back", false)]
    [InlineData("native_tab", false)]
    [InlineData("resolution", false)]
    [InlineData("rects", false)]
    [InlineData("window", false)]
    [InlineData("scene", false)]
    [InlineData("hit", false)]
    [InlineData("tabs", false)]
    public void Closed_workspace_refusals_cover_select_search_scroll_and_widgets(string action, bool required)
    {
        Assert.Equal(required, ModsUiArgs.RequiresOpenWorkspace(action));
        Assert.Equal("workspace closed", ModsUiArgs.WorkspaceClosedError);
    }

    [Fact]
    public void Same_drain_after_open_without_curtain_must_defer()
    {
        var openNoCurtain = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: false, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 0);
        Assert.True(ModsUiArgs.SameDrainOpenWithoutCurtain(openNoCurtain));

        var openWithCurtain = ModsUiResult.Status(
            open: true, site: "top-tab", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 4);
        Assert.False(ModsUiArgs.SameDrainOpenWithoutCurtain(openWithCurtain));

        Assert.False(ModsUiArgs.SameDrainOpenWithoutCurtain(ModsUiResult.EmptyStatus()));

        Assert.False(ModsUiArgs.CurtainPresent(openNoCurtain));
        Assert.True(ModsUiArgs.CurtainPresent(openWithCurtain));
        var curtain = Assert.IsType<Dictionary<string, object?>>(openWithCurtain["curtain"]);
        Assert.Equal(true, curtain["present"]);
        Assert.Equal(true, curtain["raycastTarget"]);
        var emptyCurtain = Assert.IsType<Dictionary<string, object?>>(ModsUiResult.EmptyStatus()["curtain"]);
        Assert.Equal(false, emptyCurtain["present"]);
        Assert.Equal(false, emptyCurtain["raycastTarget"]);
    }

    /// <summary>
    /// POLISH3 item B: <c>status.dialog {open, key, kind}</c> is in EVERY status shape this op can
    /// answer with — the full one the workspace described, the full one with no dialog reported, and
    /// the minimal caught-throw one (<c>EmptyStatus</c>) — and the <c>status</c> action's own
    /// same-drain <c>detail</c> never stands in for it. Live (2026-09-13, deploy #74) a
    /// <c>mods_ui status</c> taken while the Install tab's modal was up came back with no
    /// <c>dialog</c> key at all and a <c>detail</c> of <c>{"readback":"same-drain"}</c>.
    /// <para>There is no status short-circuit: the "same-drain" note is the <em>detail</em> of a
    /// status call, and the envelope's <c>after</c> is a full <c>ReadStatus</c> either way. What this
    /// test pins is the shape, so a reader may always ask for <c>status.dialog.open</c>.</para>
    /// What this would catch: the key going back behind an <c>if (dialog != null)</c>, the
    /// caught-throw status dropping it (a missing key and "nothing is open" are different answers),
    /// or the envelope carrying the same-drain detail as its status.
    /// </summary>
    [Fact]
    public void Every_status_shape_carries_the_dialog_row()
    {
        var closed = ModsUiResult.Status(
            open: true, site: "top-tab", plugin: null, page: "install", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 3840, height: 1080,
            mode: "window", panes: 2, observers: 0);
        var closedDialog = Assert.IsType<Dictionary<string, object?>>(closed["dialog"]);
        Assert.Equal(false, closedDialog["open"]);
        Assert.Equal("", closedDialog["key"]);
        Assert.Equal("none", closedDialog["kind"]);
        Assert.Equal(ModsUiResult.DialogClosed(), closedDialog);

        // The workspace reported an open uninstall confirmation: the same three keys, filled in.
        var open = ModsUiResult.Status(
            open: true, site: "top-tab", plugin: null, page: "install", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 3840, height: 1080,
            mode: "window", panes: 2, observers: 0,
            dialog: new Dictionary<string, object?>
            {
                ["open"] = true,
                ["key"] = "Progenitor-HotDemo",
                ["kind"] = "uninstall",
            });
        var openDialog = Assert.IsType<Dictionary<string, object?>>(open["dialog"]);
        Assert.Equal(true, openDialog["open"]);
        Assert.Equal("Progenitor-HotDemo", openDialog["key"]);
        Assert.Equal("uninstall", openDialog["kind"]);

        // The caught-throw shape has it too, reading closed rather than missing.
        var empty = ModsUiResult.EmptyStatus();
        Assert.True(empty.ContainsKey("dialog"), "the minimal status dropped the dialog row");
        Assert.Equal(ModsUiResult.DialogClosed(), Assert.IsType<Dictionary<string, object?>>(empty["dialog"]));

        // The envelope's `after` is the status; the same-drain note is only the detail.
        var envelope = ModsUiResult.Envelope(
            true, "status", closed, open, new Dictionary<string, object?> { ["readback"] = "same-drain" }, 1.5);
        var after = Assert.IsType<Dictionary<string, object?>>(envelope["after"]);
        Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(after["dialog"])["open"]);
        var detail = Assert.IsType<Dictionary<string, object?>>(envelope["detail"]);
        Assert.Equal("same-drain", detail["readback"]);
        Assert.False(detail.ContainsKey("dialog"), "the detail is not where a reader looks for the dialog");
        // Revert: making `dialog` conditional in ModsUiResult.Status, or leaving it out of
        // EmptyStatus, fails this.
    }

    [Fact]
    public void Curtain_status_and_rect_rows_have_the_readback_shape()
    {
        NativeModsEntry.SetOwnedForTests("top-tab", curtain: true, observers: 1);
        var status = ModsUiResult.Status(
            open: false, site: "top-tab", plugin: null, page: null, pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: false, width: 1920, height: 1080,
            mode: "window", panes: 0, observers: 1, curtainRaycast: true);
        var curtain = Assert.IsType<Dictionary<string, object?>>(status["curtain"]);
        Assert.Equal(true, curtain["present"]);
        Assert.Equal(true, curtain["raycastTarget"]);
        var rect = NativeModsEntry.DescribeCurtainRectLive();
        Assert.Equal(true, rect["present"]);
        Assert.Equal(true, rect["active"]);
        Assert.Equal(true, rect["raycastTarget"]);
        Assert.True(rect.ContainsKey("alpha"));
        Assert.True(rect.ContainsKey("sortingOrder"));
        Assert.True(rect.ContainsKey("x"));
        Assert.True(rect.ContainsKey("y"));
        Assert.True(rect.ContainsKey("width"));
        Assert.True(rect.ContainsKey("height"));
        NativeModsEntry.SetOwnedForTests("none", curtain: false, observers: 0);
    }

    [Fact]
    public void Status_parses_with_no_other_args()
    {
        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"status\"}"));
        Assert.Equal("status", parsed.Action);
        Assert.False(parsed.NeedsNextDrain);
        Assert.False(parsed.Fonts);
    }

    [Fact]
    public void Status_fonts_is_opt_in()
    {
        Assert.True(ModsUiArgs.Parse(Json("{\"action\":\"status\",\"fonts\":true}")).Fonts);
        Assert.False(ModsUiArgs.Parse(Json("{\"action\":\"status\",\"fonts\":false}")).Fonts);
        Assert.True(ModsUiArgs.Parse(Json("{\"action\":\"status\",\"fonts\":\"true\"}")).Fonts);
        // Revert: enumerating TMP_FontAsset on every status is a main-thread stall.
    }

    [Theory]
    [InlineData("main-menu")]
    [InlineData("top-tab")]
    [InlineData("shortcut")]
    [InlineData("MAIN-MENU")]
    public void Open_accepts_the_three_sites(string site)
    {
        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"open\",\"site\":\"" + site + "\"}"));
        Assert.Equal("open", parsed.Action);
        Assert.Equal(site.ToLowerInvariant(), parsed.Site);
        Assert.True(parsed.NeedsNextDrain);
    }

    [Fact]
    public void Open_requires_a_known_site()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"open\"}")));
        Assert.Contains("args.site is required", missing.Message);

        var bad = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"open\",\"site\":\"escape\"}")));
        Assert.Contains("main-menu, top-tab, shortcut, or standalone", bad.Message);

        var standalone = ModsUiArgs.Parse(Json("{\"action\":\"open\",\"site\":\"standalone\"}"));
        Assert.Equal("standalone", standalone.Site);
    }

    [Fact]
    public void Select_requires_a_known_page()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"select\",\"plugin\":\"progenitor.sample\"}")));
        Assert.Contains("args.page is required", missing.Message);

        var bad = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"select\",\"page\":\"inventory\"}")));
        Assert.Contains("overview, settings, or custom", bad.Message);
    }

    [Fact]
    public void Select_parses_plugin_page_and_group()
    {
        var parsed = ModsUiArgs.Parse(Json(
            "{\"action\":\"select\",\"plugin\":\"progenitor.sample\",\"page\":\"settings\",\"group\":\"Server\"}"));
        Assert.Equal("select", parsed.Action);
        Assert.Equal("progenitor.sample", parsed.Plugin);
        Assert.Equal("settings", parsed.Page);
        Assert.Equal("Server", parsed.Group);
    }

    [Fact]
    public void Search_requires_text_and_empty_clears()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"search\"}")));
        Assert.Contains("args.text is required", missing.Message);

        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"search\",\"text\":\"\"}"));
        Assert.Equal("", parsed.Text);
    }

    [Theory]
    [InlineData("{\"action\":\"scroll\",\"pane\":\"widgets\",\"to\":\"top\"}")]
    [InlineData("{\"action\":\"scroll\",\"to\":\"top\"}")]
    [InlineData("{\"action\":\"widget_read\",\"pane\":\"sidebar\"}")]
    [InlineData("{\"action\":\"widget_click\",\"pane\":\"both\",\"label\":\"x\"}")]
    public void Unknown_or_missing_pane_is_rejected(string json)
    {
        var e = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json(json)));
        Assert.Contains("args.pane", e.Message);
    }

    [Theory]
    [InlineData("list", "top", null)]
    [InlineData("content", "bottom", null)]
    [InlineData("list", "number", 0.25)]
    public void Scroll_parses_pane_and_to(string pane, string to, double? number)
    {
        string json = number == null
            ? "{\"action\":\"scroll\",\"pane\":\"" + pane + "\",\"to\":\"" + to + "\"}"
            : "{\"action\":\"scroll\",\"pane\":\"" + pane + "\",\"to\":" + number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        var parsed = ModsUiArgs.Parse(Json(json));
        Assert.Equal(pane, parsed.Pane);
        Assert.Equal(to == "number" ? "number" : to, parsed.To);
        if (number != null) Assert.Equal(number.Value, parsed.ToNumber);
    }

    [Fact]
    public void Widget_click_requires_label_or_index()
    {
        var e = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"widget_click\"}")));
        Assert.Contains("args.label or args.index", e.Message);
    }

    [Fact]
    public void Widget_click_parses_label_index_and_value()
    {
        var parsed = ModsUiArgs.Parse(Json(
            "{\"action\":\"widget_click\",\"pane\":\"content\",\"label\":\"Try an action\",\"value\":\"on\"}"));
        Assert.Equal("content", parsed.Pane);
        Assert.Equal("Try an action", parsed.Label);
        Assert.Equal("on", parsed.Value);
    }

    [Fact]
    public void Native_tab_requires_tab_unless_same()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"native_tab\"}")));
        Assert.Contains("args.tab", missing.Message);

        var same = ModsUiArgs.Parse(Json("{\"action\":\"native_tab\",\"same\":true}"));
        Assert.True(same.Same);
        Assert.True(same.NeedsNextDrain);

        var byIndex = ModsUiArgs.Parse(Json("{\"action\":\"native_tab\",\"tab\":\"2\"}"));
        Assert.Equal(2, byIndex.TabIndex);
    }

    [Theory]
    [InlineData("{\"action\":\"resolution\"}")]
    [InlineData("{\"action\":\"resolution\",\"width\":0,\"height\":720,\"mode\":\"window\"}")]
    [InlineData("{\"action\":\"resolution\",\"width\":1280,\"height\":-1,\"mode\":\"window\"}")]
    [InlineData("{\"action\":\"resolution\",\"width\":1280,\"height\":720}")]
    [InlineData("{\"action\":\"resolution\",\"width\":1280,\"height\":720,\"mode\":\"borderless\"}")]
    public void Resolution_refuses_missing_or_invalid_size_and_mode(string json)
    {
        Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json(json)));
    }

    [Theory]
    [InlineData("window")]
    [InlineData("windowed")]
    [InlineData("fullscreenwindow")]
    [InlineData("exclusive")]
    public void Resolution_parses_a_positive_size_and_known_mode(string mode)
    {
        var parsed = ModsUiArgs.Parse(Json(
            "{\"action\":\"resolution\",\"width\":1280,\"height\":720,\"mode\":\"" + mode + "\"}"));
        Assert.Equal(1280, parsed.Width);
        Assert.Equal(720, parsed.Height);
        Assert.Equal(mode, parsed.Mode);
        Assert.True(parsed.NeedsNextDrain);
    }

    [Fact]
    public void Status_shape_has_the_documented_keys()
    {
        var status = ModsUiResult.Status(
            open: true,
            site: "main-menu",
            plugin: "progenitor.sample",
            page: "settings",
            pageId: "Server",
            search: "",
            entries: new[] { new { id = "progenitor.sample", name = "Progenitor Sample", status = "Loaded on this client" } },
            curtain: true,
            capture: true,
            width: 3840,
            height: 1080,
            mode: "fullscreenwindow",
            panes: 2,
            observers: 4);

        foreach (var key in new[] { "open", "tab", "site", "plugin", "page", "pageId", "search", "entries", "visibleRows", "totalRows", "listScroll", "contentScroll", "curtain", "modsCapture", "inputCapture", "capture", "width", "height", "mode", "panes", "observers", "serverList", "serverListEpoch", "statusMs", "suspended", "suspendReason", "shortcut", "fontDonor" })
            Assert.True(status.ContainsKey(key), key);

        Assert.Equal(true, status["open"]);
        Assert.Null(status["tab"]);
        Assert.Equal("main-menu", status["site"]);
        Assert.Equal("progenitor.sample", status["plugin"]);
        Assert.Equal("settings", status["page"]);
        Assert.Equal(3840, status["width"]);
        Assert.Equal("fullscreenwindow", status["mode"]);
        Assert.Equal(true, status["modsCapture"]);
        Assert.Equal(true, status["inputCapture"]);
        Assert.Equal(status["inputCapture"], status["capture"]);
        Assert.Equal(false, status["suspended"]);
        Assert.Null(status["suspendReason"]);
        Assert.Null(status["shortcut"]);
        Assert.Null(status["fontDonor"]);
        Assert.False(status.ContainsKey("fontAssets"));
        Assert.Equal(0, status["visibleRows"]);
        Assert.Equal(0, status["totalRows"]);
        var listScroll = Assert.IsType<Dictionary<string, object?>>(status["listScroll"]);
        Assert.Equal(1d, Convert.ToDouble(listScroll["position"]));
        Assert.Equal(false, listScroll["overflow"]);
        var contentScroll = Assert.IsType<Dictionary<string, object?>>(status["contentScroll"]);
        Assert.Equal(1d, Convert.ToDouble(contentScroll["position"]));
        Assert.Equal(false, contentScroll["overflow"]);
    }

    /// <summary>
    /// MODS2: <c>status.mods</c> is <c>{shown, required, server}</c> on every status, closed
    /// workspace included, so a reader never has to tell a missing field from a zero. MODS1's
    /// <c>hiddenDeveloper</c>/<c>showDeveloper</c> pair went with the developer toggle.
    /// What this would catch: dropping the block from the closed-workspace status, renaming a
    /// count, or the retired toggle fields coming back.
    /// </summary>
    [Fact]
    public void Mods_counts_are_on_every_status()
    {
        var status = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: false, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 0,
            mods: ModsUiResult.ModsCounts(5, 7, 2));
        var mods = Assert.IsType<Dictionary<string, object?>>(status["mods"]);
        Assert.Equal(5, mods["shown"]);
        Assert.Equal(7, mods["required"]);
        Assert.Equal(2, mods["server"]);
        Assert.False(mods.ContainsKey("hiddenDeveloper"));
        Assert.False(mods.ContainsKey("showDeveloper"));

        var empty = Assert.IsType<Dictionary<string, object?>>(ModsUiResult.EmptyStatus()["mods"]);
        Assert.Equal(0, empty["shown"]);
        Assert.Equal(0, empty["required"]);
        Assert.Equal(0, empty["server"]);
        Assert.False(empty.ContainsKey("showDeveloper"));
    }

    [Fact]
    public void ListScroll_shape_is_position_and_overflow()
    {
        var scroll = ModsUiResult.ListScroll(0.25, true);
        Assert.Equal(0.25, scroll["position"]);
        Assert.Equal(true, scroll["overflow"]);
        var status = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: "beta.mod", page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: false, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 0,
            listScroll: ModsUiResult.ListScroll(0.728, true),
            contentScroll: ModsUiResult.ListScroll(0, false));
        var list = Assert.IsType<Dictionary<string, object?>>(status["listScroll"]);
        var content = Assert.IsType<Dictionary<string, object?>>(status["contentScroll"]);
        Assert.Equal(0.728, Convert.ToDouble(list["position"]));
        Assert.Equal(0d, Convert.ToDouble(content["position"]));
        Assert.NotEqual(Convert.ToDouble(list["position"]), Convert.ToDouble(content["position"]));
        var empty = ModsUiResult.EmptyStatus();
        Assert.True(empty.ContainsKey("visibleRows"));
        Assert.True(empty.ContainsKey("totalRows"));
        Assert.True(empty.ContainsKey("listScroll"));
        Assert.True(empty.ContainsKey("contentScroll"));
    }

    [Fact]
    public void Capture_is_an_alias_of_inputCapture_and_modsCapture_defaults_to_open()
    {
        var closed = ModsUiResult.Status(
            open: false, site: "none", plugin: null, page: null, pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: false, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 0, observers: 7);
        Assert.Equal(false, closed["open"]);
        Assert.Equal(false, closed["modsCapture"]);
        Assert.Equal(true, closed["inputCapture"]);
        Assert.Equal(true, closed["capture"]);

        var openNoModsCapture = ModsUiResult.Status(
            open: true, site: "top-tab", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 7, modsCapture: false);
        Assert.Equal(false, openNoModsCapture["modsCapture"]);
        Assert.Equal(true, openNoModsCapture["inputCapture"]);
        Assert.Equal(true, openNoModsCapture["capture"]);
    }

    [Fact]
    public void Top_tab_records_how_FullscreenMenu_was_brought_up()
    {
        Assert.Equal("HUDMenuManager.GoToMenu", ModsUiArgs.TopTabMenuOpenedBy(requestedMenu: true));
        Assert.Equal("already-up", ModsUiArgs.TopTabMenuOpenedBy(requestedMenu: false));
        Assert.Equal("top-tab button not attached", ModsUiArgs.TopTabButtonNotAttached);
        Assert.Equal(NativeModsEntry.FullscreenMenuAttachFrames, ModsUiArgs.TopTabAttachFrames);
        Assert.Equal(120, ModsUiArgs.TopTabAttachFrames);
    }

    [Fact]
    public void Pending_top_tab_attach_is_a_dictionary_flag()
    {
        var pending = new Dictionary<string, object?>
        {
            ["path"] = "top-tab",
            ["menuOpenedBy"] = ModsUiArgs.MenuOpenedByGoToMenu,
            [ModsUiArgs.PendingTopTabKey] = true,
            [ModsUiArgs.RemainingTopTabKey] = ModsUiArgs.TopTabAttachFrames,
        };
        Assert.True(ModsUiArgs.IsPendingTopTab(pending));
        Assert.Equal(ModsUiArgs.TopTabAttachFrames, ModsUiArgs.TopTabRemainingFrames(pending));
        Assert.False(ModsUiArgs.IsPendingTopTab(new Dictionary<string, object?> { ["path"] = "top-tab" }));
        Assert.False(ModsUiArgs.IsPendingTopTab(new { path = "top-tab" }));
    }

    [Fact]
    public void Top_tab_attach_polls_the_NativeModsEntry_frame_budget_and_restores_only_if_it_opened_the_menu()
    {
        int remaining = ModsUiArgs.TopTabAttachFrames;
        int waits = 0;
        while (ModsUiArgs.ShouldKeepWaitingTopTab(ModsUiArgs.ConsumeTopTabFrame(remaining)))
        {
            remaining = ModsUiArgs.ConsumeTopTabFrame(remaining);
            waits++;
        }
        remaining = ModsUiArgs.ConsumeTopTabFrame(remaining);
        Assert.Equal(ModsUiArgs.TopTabAttachFrames - 1, waits);
        Assert.Equal(0, remaining);
        Assert.False(ModsUiArgs.ShouldKeepWaitingTopTab(remaining));

        Assert.True(ModsUiArgs.ShouldRestoreMenuOnAttachFailure(ModsUiArgs.MenuOpenedByGoToMenu));
        Assert.False(ModsUiArgs.ShouldRestoreMenuOnAttachFailure(ModsUiArgs.MenuOpenedByAlreadyUp));

        Assert.True(ModsUiArgs.ShouldInvokeOwnedButton(buttonAttached: true, alreadyInvoked: false));
        Assert.False(ModsUiArgs.ShouldInvokeOwnedButton(buttonAttached: true, alreadyInvoked: true));
        Assert.False(ModsUiArgs.ShouldInvokeOwnedButton(buttonAttached: false, alreadyInvoked: false));

        var missed = ModsUiArgs.TopTabNotAttachedDetail(
            ModsUiArgs.MenuOpenedByGoToMenu, "FullscreenMenu", menuRestored: true);
        Assert.Equal(true, missed[ModsUiArgs.MenuRestoredKey]);
        Assert.Equal(ModsUiArgs.TopTabButtonNotAttached, missed["error"]);
    }

    [Fact]
    public void Envelope_serializes_camelCase_ok_action_before_after_detail_elapsedMs()
    {
        var before = ModsUiResult.EmptyStatus();
        var after = ModsUiResult.Status(true, "top-tab", "progenitor.sample", "custom", "Example", "", Array.Empty<object>(), true, true, 1920, 1080, "window", 2, 1);
        var envelope = ModsUiResult.Envelope(true, "open", before, after, new { path = "top-tab", readback = "next-drain" }, 1.25);

        string json = JsonSerializer.Serialize(envelope, ModsUiResult.JsonOptions);
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"action\":\"open\"", json);
        Assert.Contains("\"before\":", json);
        Assert.Contains("\"after\":", json);
        Assert.Contains("\"detail\":", json);
        Assert.Contains("\"elapsedMs\":1.25", json);
        Assert.Contains("\"path\":\"top-tab\"", json);
        Assert.Contains("\"readback\":\"next-drain\"", json);
        Assert.Contains("\"site\":\"none\"", json);
        Assert.Contains("\"site\":\"top-tab\"", json);
    }

    [Fact]
    public void Empty_status_is_the_failed_read_fallback()
    {
        var status = ModsUiResult.EmptyStatus();
        Assert.Equal(false, status["open"]);
        Assert.Null(status["tab"]);
        Assert.Equal("none", status["site"]);
        Assert.Null(status["plugin"]);
        Assert.Equal(0, status["panes"]);
        Assert.Equal(0, status["observers"]);
        Assert.Equal(0.0, status["statusMs"]);
        Assert.Equal(false, status["modsCapture"]);
        Assert.Equal(false, status["inputCapture"]);
        Assert.Equal(false, status["capture"]);
        Assert.Equal(false, status["suspended"]);
        Assert.Null(status["suspendReason"]);
        Assert.True(status.ContainsKey("shortcut"));
        Assert.Null(status["shortcut"]);
        var unknown = Assert.IsType<Dictionary<string, object?>>(status["serverList"]);
        Assert.Equal("unknown", unknown["state"]);
        Assert.Equal(0, status["serverListEpoch"]);
    }

    [Fact]
    public void Status_reports_unavailable_after_a_clear_and_available_after_restore()
    {
        var unavailable = ModsUiResult.Status(
            open: false, site: "main-menu", plugin: null, page: null, pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: false, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 0, observers: 0, serverList: ModsUiResult.ServerList("unavailable"),
            serverListEpoch: 12);
        var cleared = Assert.IsType<Dictionary<string, object?>>(unavailable["serverList"]);
        Assert.Equal("unavailable", cleared["state"]);
        Assert.False(cleared.ContainsKey("count"));
        Assert.Equal(12, unavailable["serverListEpoch"]);

        var available = ModsUiResult.Status(
            open: true, site: "top-tab", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 7, serverList: ModsUiResult.ServerList("available", 5),
            serverListEpoch: 13);
        var received = Assert.IsType<Dictionary<string, object?>>(available["serverList"]);
        Assert.Equal("available", received["state"]);
        Assert.Equal(5, received["count"]);
        Assert.Equal(13, available["serverListEpoch"]);
    }

    [Fact]
    public void Next_drain_readback_does_not_block_the_calling_frame()
    {
        Assert.Equal(0d, ModsUiArgs.NextDrainDelaySeconds);
        Assert.True(ModsUiArgs.NextDrainWaitMs > 0);
    }

    [Fact]
    public void Client_only_refusal_string_is_stable()
    {
        Assert.Equal("mods_ui runs on the client side only", ModsUiArgs.ClientOnlyMessage);
    }

    [Fact]
    public void Rects_and_window_parse_with_no_other_args_and_do_not_defer()
    {
        var rects = ModsUiArgs.Parse(Json("{\"action\":\"rects\"}"));
        Assert.Equal("rects", rects.Action);
        Assert.False(rects.NeedsNextDrain);

        var window = ModsUiArgs.Parse(Json("{\"action\":\"window\"}"));
        Assert.Equal("window", window.Action);
        Assert.False(window.NeedsNextDrain);
    }

    [Theory]
    [InlineData("mods")]
    [InlineData("install")]
    [InlineData("configuration")]
    [InlineData("INSTALL")]
    [InlineData("config")]
    [InlineData("preferences")]
    [InlineData("server")]
    public void Select_tab_parses_canonical_names_and_defers_readback(string tab)
    {
        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"select_tab\",\"tab\":\"" + tab + "\"}"));
        Assert.Equal("select_tab", parsed.Action);
        Assert.True(parsed.NeedsNextDrain);
        Assert.True(ModsMenu.TryParseWorkspaceTab(parsed.Tab, out var name));
        // Three homes: "configuration" and "config" are aliases of the Preferences tab.
        Assert.True(name is "mods" or "install" or "preferences" or "server");
        Assert.True(ModsUiArgs.RequiresOpenWorkspace("select_tab"));
        // Revert: omitting NeedsNextDrain or dropping select_tab from RequiresOpenWorkspace fails this.
    }

    [Fact]
    public void Select_tab_refuses_a_missing_or_unknown_tab_including_consent()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"select_tab\"}")));
        Assert.Contains("args.tab is required", missing.Message);

        var consent = Assert.Throws<ArgumentException>(() =>
            ModsUiArgs.Parse(Json("{\"action\":\"select_tab\",\"tab\":\"consent\"}")));
        Assert.Contains("mods, install, server, keybinds, or preferences", consent.Message);

        var native = Assert.Throws<ArgumentException>(() =>
            ModsUiArgs.Parse(Json("{\"action\":\"select_tab\",\"tab\":\"Spellbook\"}")));
        Assert.Contains("mods, install, server, keybinds, or preferences", native.Message);
        // Revert: treating consent or a native outer tab as args.tab makes TryParse succeed.
    }

    [Fact]
    public void Status_tab_values_are_mods_install_configuration_or_null()
    {
        var closed = ModsUiResult.EmptyStatus();
        Assert.Null(closed["tab"]);

        var mods = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 3, observers: 0, tab: "mods");
        Assert.Equal("mods", mods["tab"]);

        var install = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 3, observers: 0, tab: "install");
        Assert.Equal("install", install["tab"]);

        var configuration = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 3, observers: 0, tab: "configuration");
        Assert.Equal("configuration", configuration["tab"]);
        // Revert: dropping the tab key from Status, or mapping consent as a tab, fails this.
    }

    [Fact]
    public void Parse_scene_with_no_other_args_and_does_not_defer()
    {
        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"scene\"}"));
        Assert.Equal("scene", parsed.Action);
        Assert.False(parsed.NeedsNextDrain);
        Assert.False(ModsUiArgs.RequiresOpenWorkspace("scene"));
    }

    [Fact]
    public void Parse_menu_with_no_other_args_and_does_not_defer()
    {
        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"menu\"}"));
        Assert.Equal("menu", parsed.Action);
        Assert.False(parsed.NeedsNextDrain);
        Assert.False(ModsUiArgs.RequiresOpenWorkspace("menu"));
        Assert.Contains("menu", ModsUiArgs.Actions);
        // Revert: dropping menu from Actions hides the FullscreenMenu footer probe.
    }

    [Fact]
    public void Parse_hit_requires_x_and_y_and_does_not_defer()
    {
        var missing = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"hit\"}")));
        Assert.Contains("args.x is required", missing.Message);

        var missingY = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json("{\"action\":\"hit\",\"x\":1360}")));
        Assert.Contains("args.y is required", missingY.Message);

        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"hit\",\"x\":1360,\"y\":92}"));
        Assert.Equal("hit", parsed.Action);
        Assert.Equal(1360f, parsed.X);
        Assert.Equal(92f, parsed.Y);
        Assert.False(parsed.NeedsNextDrain);
        Assert.False(ModsUiArgs.RequiresOpenWorkspace("hit"));
    }

    [Fact]
    public void Parse_tabs_with_no_other_args_and_does_not_defer()
    {
        var parsed = ModsUiArgs.Parse(Json("{\"action\":\"tabs\"}"));
        Assert.Equal("tabs", parsed.Action);
        Assert.False(parsed.NeedsNextDrain);
        Assert.False(ModsUiArgs.RequiresOpenWorkspace("tabs"));
        Assert.Equal("graphics", ModsUiArgs.TabsGraphicsField);
        Assert.Equal("interactable", ModsUiArgs.TabsInteractableField);
        Assert.Equal("isHovered", ModsUiArgs.TabsIsHoveredField);
        Assert.Equal("raycastTarget", ModsUiArgs.TabsRaycastTargetField);
        Assert.Equal("crossFadeAlpha", ModsUiArgs.TabsCrossFadeAlphaField);
        Assert.Equal("canvasRendererColor", ModsUiArgs.TabsCanvasRendererColorField);
        Assert.Equal("rect", ModsUiArgs.TabsGraphicRectField);
        Assert.Equal("siblingIndex", ModsUiArgs.TabsSiblingIndexField);
    }

    [Fact]
    public void Resolution_windowed_is_FullScreenMode_Windowed()
    {
        var parsed = ModsUiArgs.Parse(Json(
            "{\"action\":\"resolution\",\"width\":1280,\"height\":720,\"mode\":\"windowed\"}"));
        Assert.Equal("windowed", parsed.Mode);
        Assert.Equal(1280, parsed.Width);
        Assert.Equal(720, parsed.Height);
        Assert.True(parsed.NeedsNextDrain);
    }

    [Fact]
    public void Unity_screen_bottom_left_converts_to_client_top_left_at_scale_one()
    {
        // Unity (10, 20, 200, 48) on a 1280x720 client: Y flips about the top edge.
        var rect = ModsUiGeometry.FromUnityScreen(10, 20, 200, 48, screenHeight: 720, canvasScale: 1f);
        Assert.Equal(10, rect["x"]);
        Assert.Equal(720 - 20 - 48, rect["y"]);
        Assert.Equal(200, rect["width"]);
        Assert.Equal(48, rect["height"]);
        Assert.Equal(true, rect["visible"]);
        Assert.Equal(true, rect["interactable"]);
        Assert.Equal(0, rect["sortingOrder"]);
        Assert.Null(rect["occludedBy"]);
        Assert.Null(rect["occluderOrder"]);
        Assert.Equal(1f, rect["canvasScale"]);
        var unity = Assert.IsType<Dictionary<string, object?>>(rect["unity"]);
        Assert.Equal(10f, unity["x"]);
        Assert.Equal(20f, unity["y"]);
        Assert.Equal(200f, unity["width"]);
        Assert.Equal(48f, unity["height"]);
    }

    [Fact]
    public void A_row_scrolled_above_the_list_viewport_is_not_visible_in_rects()
    {
        var above = ModsUiGeometry.FromUnityScreen(1302, 1080, 320, 40, screenHeight: 1080, canvasScale: 1f);
        Assert.True((int)above["y"]! < 0);
        ModsUiGeometry.ClipToViewport(above, vx: 1302, vy: 338, vw: 320, vh: 568);
        Assert.Equal(false, above["visible"]);
        Assert.Equal(false, above["interactable"]);

        var inView = ModsUiGeometry.FromUnityScreen(1302, 640, 320, 40, screenHeight: 1080, canvasScale: 1f);
        Assert.InRange((int)inView["y"]!, 338, 338 + 568);
        ModsUiGeometry.ClipToViewport(inView, vx: 1302, vy: 338, vw: 320, vh: 568);
        Assert.Equal(true, inView["visible"]);
    }

    [Fact]
    public void Native_background_row_keeps_found_and_why()
    {
        var row = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromUnityScreen(1088, 72, 1664, 914, 1080, 1f),
            "native-background", "image", "Background");
        ModsUiGeometry.WriteFoundWhy(row, found: true, ModsWorkspaceFrame.BackgroundWhyLive);
        Assert.Equal(true, row["found"]);
        Assert.Equal(ModsWorkspaceFrame.BackgroundWhyLive, row["why"]);
        Assert.Equal(1088, row["x"]);
        Assert.Equal(914, row["height"]);
        var miss = ModsUiGeometry.Annotate(
            ModsUiGeometry.MissingRect(),
            "native-background", "image", "BackgroundImage");
        ModsUiGeometry.WriteFoundWhy(miss, found: false, ModsWorkspaceFrame.BackgroundWhyNoHosted);
        Assert.Equal(false, miss["found"]);
        Assert.Equal(ModsWorkspaceFrame.BackgroundWhyNoHosted, miss["why"]);
        var frame = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromUnityScreen(1088, 72, 1664, 914, 1080, 1f),
            "workspace-frame", "frame", "Mods workspace");
        ModsUiGeometry.CopyClientRect(miss, frame);
        Assert.Equal(1088, miss["x"]);
        Assert.Equal(914, miss["height"]);
        // Revert: dropping native-background / nativeBackgroundWhy hides the
        // overhang the in-game frame is supposed to match.
    }

    [Fact]
    public void Backdrop_rects_report_the_clipped_visible_rect_not_the_pre_mask_image()
    {
        var frame = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromUnityScreen(1282, 70, 1276, 800, 1080, 1f),
            "workspace-frame", "frame", "Mods workspace");
        var backdrop = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromUnityScreen(1209, 70, 1421, 800, 1080, 1f),
            "backdrop", "image", "mods-background");
        Assert.Equal(1421, backdrop["width"]);
        ModsUiGeometry.ClipRectTo(backdrop, frame);
        Assert.Equal(1282, backdrop["x"]);
        Assert.Equal(1276, backdrop["width"]);
        Assert.NotEqual(1421, backdrop["width"]);
        Assert.NotEqual(916, backdrop["width"]);
        Assert.Equal(ModsCanvasBand.Blocker, NativeModsEntry.CurtainCanvasSortingOrder);
        Assert.True(ModsCanvasBand.LayerOrderIsResolved(
            ModsCanvasBand.Blocker, ModsCanvasBand.Backdrop, ModsCanvasBand.Panels, ModsCanvasBand.Overlay));
    }

    [Fact]
    public void Rects_layer_fields_name_the_band_and_equal_pane_alphas()
    {
        var geometry = ModsMenu.DescribeGeometry();
        string json = JsonSerializer.Serialize(geometry, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(ModsCanvasBand.Blocker, doc.RootElement.GetProperty("blockerSorting").GetInt32());
        Assert.Equal(ModsCanvasBand.Backdrop, doc.RootElement.GetProperty("backdropSorting").GetInt32());
        Assert.Equal(ModsCanvasBand.Panels, doc.RootElement.GetProperty("panelsSorting").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("consentSorting", out _));
        Assert.Equal(ModsWorkspaceFrame.PanelFillAlpha, doc.RootElement.GetProperty("sidebarAlpha").GetSingle());
        Assert.Equal(ModsWorkspaceFrame.PanelFillAlpha, doc.RootElement.GetProperty("detailAlpha").GetSingle());
        Assert.Equal(
            doc.RootElement.GetProperty("sidebarAlpha").GetSingle(),
            doc.RootElement.GetProperty("detailAlpha").GetSingle());
        Assert.True(doc.RootElement.TryGetProperty("nativeWhy", out var whyEl));
        Assert.Equal(JsonValueKind.String, whyEl.ValueKind);
        Assert.False(string.IsNullOrEmpty(whyEl.GetString()));
        var (bgFound, bgWhy, _, _, _, _) = ModsMenu.WorkspaceNativeBackground();
        Assert.False(bgFound);
        Assert.NotNull(bgWhy);
        Assert.True(doc.RootElement.TryGetProperty("hostedViewHidden", out var hostedEl));
        Assert.Equal(JsonValueKind.False, hostedEl.ValueKind);
        Assert.False(ModsMenu.HostedViewIsHidden);
        Assert.Null(ModsMenu.TitleVideoPlaying);
        string ops = File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Dev", "Ops", "ModsUiOps.cs"));
        Assert.Contains("[\"titleVideoPlaying\"]", ops, StringComparison.Ordinal);
        Assert.Contains("[\"nativeBackgroundWhy\"]", ops, StringComparison.Ordinal);
        Assert.Contains("[\"frameExtentWhy\"]", ops, StringComparison.Ordinal);
        Assert.Contains("\"native-background\"", ops, StringComparison.Ordinal);
        Assert.Equal("", ModsMenu.WorkspaceFrameExtentWhy());
        Assert.True(doc.RootElement.TryGetProperty("frameExtentWhy", out var extentEl));
        Assert.Equal(JsonValueKind.String, extentEl.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("outerTabVisual", out var visEl));
        Assert.Equal(JsonValueKind.String, visEl.ValueKind);
        Assert.Equal("", visEl.GetString());
        Assert.Equal("", ModsMenu.OuterTabVisual);
        Assert.True(doc.RootElement.TryGetProperty("ownedTopTabCount", out var ownedEl));
        Assert.Equal(JsonValueKind.Number, ownedEl.ValueKind);
        Assert.Equal(0, ownedEl.GetInt32());
        Assert.Equal(0, ModsMenu.OwnedTopTabCount);
        Assert.Contains("[\"ownedTopTabCount\"]", ops, StringComparison.Ordinal);
        Assert.False(ModsCanvasBand.CountTowardNativeMax("overlay-1", new[] { "progenitor-ui-core" }));
        Assert.True(doc.RootElement.TryGetProperty("firstShowFrame", out var firstEl));
        Assert.Equal(JsonValueKind.Number, firstEl.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("layoutBeforeShow", out var beforeEl));
        Assert.Equal(JsonValueKind.Number, beforeEl.ValueKind);
        Assert.Contains("[\"firstShowFrame\"]", ops, StringComparison.Ordinal);
        Assert.Contains("[\"layoutBeforeShow\"]", ops, StringComparison.Ordinal);
        Assert.True(doc.RootElement.TryGetProperty("playBackdrop", out var playEl));
        Assert.False(playEl.GetProperty("present").GetBoolean());
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceNone, playEl.GetProperty("source").GetString());
        var play = Assert.IsType<Dictionary<string, object?>>(ModsMenu.DescribePlayBackdrop());
        Assert.Equal(false, play["present"]);
        Assert.Equal(NativeModsEntry.PlayBackdropHold.SourceNone, play["source"]);
        Assert.True(play.ContainsKey("sprite"));
        Assert.True(play.ContainsKey("material"));
        Assert.True(play.ContainsKey("shader"));
    }

    [Fact]
    public void Canvas_scale_is_applied_before_the_Y_flip()
    {
        // Canvas-local (100, 40, 200, 48) at scale 2 on 1920x1080.
        // Unity screen: (200, 80, 400, 96). Client y = 1080 - 80 - 96 = 904.
        var rect = ModsUiGeometry.FromCanvasLocal(100, 40, 200, 48, screenHeight: 1080, canvasScale: 2f);
        Assert.Equal(200, rect["x"]);
        Assert.Equal(904, rect["y"]);
        Assert.Equal(400, rect["width"]);
        Assert.Equal(96, rect["height"]);
        Assert.Equal(2f, rect["canvasScale"]);
        var unity = Assert.IsType<Dictionary<string, object?>>(rect["unity"]);
        Assert.Equal(200f, unity["x"]);
        Assert.Equal(80f, unity["y"]);
        Assert.Equal(400f, unity["width"]);
        Assert.Equal(96f, unity["height"]);
    }

    [Fact]
    public void Rect_json_shape_is_client_pixels_plus_unity_rect_and_canvas_scale()
    {
        var annotated = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromCanvasLocal(0, 0, 240, 48, 720, 1f),
            "main-menu", "button", "Mods");
        string json = JsonSerializer.Serialize(annotated, ModsUiResult.JsonOptions);
        Assert.Contains("\"id\":\"main-menu\"", json);
        Assert.Contains("\"kind\":\"button\"", json);
        Assert.Contains("\"label\":\"Mods\"", json);
        Assert.Contains("\"x\":0", json);
        Assert.Contains("\"y\":672", json);
        Assert.Contains("\"width\":240", json);
        Assert.Contains("\"height\":48", json);
        Assert.Contains("\"unity\":{", json);
        Assert.Contains("\"canvasScale\":1", json);
        Assert.Contains("\"visible\":true", json);
        Assert.Contains("\"interactable\":true", json);
        Assert.Contains("\"sortingOrder\":0", json);
        Assert.Contains("\"occludedBy\":null", json);
        Assert.Contains("\"occluderOrder\":null", json);
        Assert.Contains("\"align\":\"centre\"", json);
    }

    [Fact]
    public void Rects_rows_carry_align_left_or_centre()
    {
        var centre = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromCanvasLocal(0, 0, 10, 10, 720, 1f),
            "widget", "label", "Overview");
        Assert.Equal(SettingsAlign.Centre, centre["align"]);
        ModsUiGeometry.WriteAlign(centre, "left");
        Assert.Equal(SettingsAlign.Left, centre["align"]);
        ModsUiGeometry.WriteAlign(centre, "CENTER");
        Assert.Equal(SettingsAlign.Centre, centre["align"]);
        // Revert: dropping the align field from Annotate leaves mods_ui rects unable to confirm layout.
    }

    [Fact]
    public void Occlusion_requires_the_overlay_to_render_above_and_overlap()
    {
        var curtain = ModsUiGeometry.OverlayLayer.Of(
            ModsUiGeometry.OccludedByCurtain, 0, 52, 3840, 1028, NativeModsEntry.CurtainCanvasSortingOrder);
        int workspace = UiOwnership.OverlaySorting;

        Assert.Equal(ModsCanvasBand.Blocker, NativeModsEntry.CurtainCanvasSortingOrder);
        Assert.Equal(5000, workspace);
        Assert.True(ModsCanvasBand.LayerOrderIsResolved(
            NativeModsEntry.CurtainCanvasSortingOrder,
            ModsCanvasBand.Backdrop,
            ModsCanvasBand.Panels,
            workspace));
        Assert.True(curtain.RendersAbove(0, 0));
        Assert.False(curtain.RendersAbove(workspace, 0));

        var below = ModsUiGeometry.Occlusion(1302, 914, 320, 40, workspace, 0, curtain);
        Assert.Null(below.occludedBy);
        Assert.Null(below.occluderOrder);

        var native = ModsUiGeometry.Occlusion(1702, 100, 145, 48, sortingOrder: 0, siblingOrder: 0, curtain);
        Assert.Equal(ModsUiGeometry.OccludedByCurtain, native.occludedBy);
        Assert.Equal(NativeModsEntry.CurtainCanvasSortingOrder, native.occluderOrder);

        var insetTab = ModsUiGeometry.Occlusion(1702, 0, 145, 48, sortingOrder: 0, siblingOrder: 0, curtain);
        Assert.Null(insetTab.occludedBy);

        var self = ModsUiGeometry.Occlusion(1702, 100, 145, 48, 0, 0, curtain, selfIsCurtain: true);
        Assert.Null(self.occludedBy);

        Assert.True(ModsUiGeometry.Overlaps(0, 0, 100, 100, 50, 50, 10, 10));
        Assert.False(ModsUiGeometry.Overlaps(0, 0, 10, 10, 20, 20, 10, 10));
        Assert.False(ModsUiGeometry.IsInteractable(true, true, ModsUiGeometry.OccludedByCurtain));
        Assert.True(ModsUiGeometry.IsInteractable(true, true, null));
        Assert.False(ModsUiGeometry.IsInteractable(true, false, null));
    }

    [Fact]
    public void Window_json_shape_is_os_client_area_plus_screen_and_primary_display()
    {
        var window = ModsUiResult.Window(
            "0x530942", 1280, 720, 100, 80, 1280, 720, "Windowed", "windowed", 3840, 1080);
        string json = JsonSerializer.Serialize(window, ModsUiResult.JsonOptions);
        Assert.Contains("\"hwnd\":\"0x530942\"", json);
        Assert.Contains("\"client\":{", json);
        Assert.Contains("\"width\":1280", json);
        Assert.Contains("\"height\":720", json);
        Assert.Contains("\"screenX\":100", json);
        Assert.Contains("\"screenY\":80", json);
        Assert.Contains("\"fullScreenMode\":\"Windowed\"", json);
        Assert.Contains("\"mode\":\"windowed\"", json);
        Assert.Contains("\"primaryDisplay\":{", json);
        Assert.Contains("\"width\":3840", json);
        Assert.Contains("\"height\":1080", json);

        var envelope = ModsUiResult.Envelope(
            true, "window", ModsUiResult.EmptyStatus(), ModsUiResult.EmptyStatus(), window, 1);
        string wrapped = JsonSerializer.Serialize(envelope, ModsUiResult.JsonOptions);
        Assert.Contains("\"action\":\"window\"", wrapped);
        Assert.Contains("\"hwnd\":\"0x530942\"", wrapped);
    }

    [Fact]
    public void Rects_detail_shape_names_origin_spaces_and_a_rect_list()
    {
        var rect = ModsUiGeometry.Annotate(
            ModsUiGeometry.FromUnityScreen(0, 672, 112, 48, 720, 1f),
            "top-tab", "button", "Mods");
        var detail = new Dictionary<string, object?>
        {
            ["readback"] = "same-drain",
            ["origin"] = ModsUiGeometry.ClientOrigin,
            ["unityOrigin"] = ModsUiGeometry.UnityOrigin,
            ["screenWidth"] = 1280,
            ["screenHeight"] = 720,
            ["canvasScale"] = 1f,
            ["shortcut"] = ModsUiResult.FormatShortcut(new KeyChord(UiKey.F10)),
            ["rects"] = new[] { rect },
        };
        string json = JsonSerializer.Serialize(detail, ModsUiResult.JsonOptions);
        Assert.Contains("\"origin\":\"client-top-left\"", json);
        Assert.Contains("\"unityOrigin\":\"screen-bottom-left\"", json);
        Assert.Contains("\"id\":\"top-tab\"", json);
        Assert.Contains("\"shortcut\":\"F10\"", json);
        Assert.Equal(0, rect["y"]);
    }

    [Theory]
    [InlineData(UiKey.F10, KeyModifiers.None, "F10")]
    [InlineData(UiKey.F10, KeyModifiers.Ctrl, "F10 + Ctrl")]
    [InlineData(UiKey.K, KeyModifiers.Ctrl | KeyModifiers.Shift, "K + Ctrl + Shift")]
    [InlineData(UiKey.F5, KeyModifiers.Alt, "F5 + Alt")]
    public void Shortcut_is_the_configured_chord_as_KeyChord_formats_it(UiKey key, KeyModifiers modifiers, string formatted)
    {
        Assert.Equal(formatted, new KeyChord(key, modifiers).ToString());
        Assert.Equal(formatted, ModsUiResult.FormatShortcut(new KeyChord(key, modifiers)));
    }

    [Fact]
    public void Shortcut_is_null_when_none_is_bound()
    {
        Assert.Null(ModsUiResult.FormatShortcut(KeyChord.None));
        Assert.Null(ModsUiResult.FormatShortcut(default));
        Assert.Null(ModsUiResult.ShortcutFrom(null));
        Assert.Null(ModsUiResult.ShortcutFrom(Array.Empty<IKeybindInfo>()));
        Assert.Null(ModsUiResult.ShortcutFrom(new IKeybindInfo[]
        {
            new FakeBind("other.mod", SettingsPanel.OpenKeybindId, new KeyChord(UiKey.F11)),
            new FakeBind(SettingsPanel.PluginId, "OtherBind", new KeyChord(UiKey.F12)),
            new FakeBind(SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, KeyChord.None),
        }));
    }

    [Fact]
    public void Shortcut_from_keybind_snapshot_picks_the_Mods_opener()
    {
        var opener = new FakeBind(SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, new KeyChord(UiKey.F10));
        var rebound = new FakeBind(SettingsPanel.PluginId, SettingsPanel.OpenKeybindId, new KeyChord(UiKey.F8, KeyModifiers.Ctrl));
        var other = new FakeBind("other.mod", SettingsPanel.OpenKeybindId, new KeyChord(UiKey.F11));

        Assert.Equal("F10", ModsUiResult.ShortcutFrom(new IKeybindInfo[] { other, opener }));
        Assert.Equal("F8 + Ctrl", ModsUiResult.ShortcutFrom(new IKeybindInfo[] { rebound }));
        Assert.Equal("F10", ModsUiResult.ShortcutFrom(new IKeybindInfo[]
        {
            new ThrowingBind(),
            opener,
        }));
        Assert.Null(ModsUiResult.ShortcutFrom(new ThrowingBindList()));
    }

    [Fact]
    public void Status_and_rects_detail_carry_the_formatted_shortcut()
    {
        var with = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 0, shortcut: "F10");
        Assert.Equal("F10", with["shortcut"]);
        string statusJson = JsonSerializer.Serialize(with, ModsUiResult.JsonOptions);
        Assert.Contains("\"shortcut\":\"F10\"", statusJson);

        var empty = ModsUiResult.EmptyStatus();
        Assert.True(empty.ContainsKey("shortcut"));
        Assert.Null(empty["shortcut"]);
        string emptyJson = JsonSerializer.Serialize(empty, ModsUiResult.JsonOptions);
        Assert.Contains("\"shortcut\":null", emptyJson);

        var rectsDetail = new Dictionary<string, object?>
        {
            ["readback"] = "same-drain",
            ["shortcut"] = ModsUiResult.FormatShortcut(new KeyChord(UiKey.F10)),
            ["rects"] = Array.Empty<object>(),
        };
        string detailJson = JsonSerializer.Serialize(rectsDetail, ModsUiResult.JsonOptions);
        Assert.Contains("\"shortcut\":\"F10\"", detailJson);
    }

    [Fact]
    public void Configured_shortcut_read_is_exception_safe()
    {
        string? shortcut = ModsUiResult.ReadConfiguredShortcut();
        if (shortcut != null)
            Assert.False(string.IsNullOrWhiteSpace(shortcut));
    }

    // ---- replayed rects readbacks --------------------------------------------------------
    //
    // THESE FIXTURES ARE SYNTHETIC. They are hand-written `mods_ui rects` readbacks in the shape
    // ModsUiGeometry writes — one row per control, screen 3840x1080 at canvasScale 1 — and the
    // tests below REPLAY them through the occlusion arithmetic. A replay is evidence about the
    // arithmetic and nothing else: it does not observe a client, and a passing replay is not a
    // live qualification of the Mods workspace.
    //
    // They replace five tests that read captured bridge reports out of the research tree beside
    // this repository (internal research notes, mods-UI usability review). Those
    // captures are live evidence and stay outside the repository, so the tests reading them could
    // only pass on the machine that recorded them: on CI — and in any worktree, whose parent is
    // not the research folder's parent — all five failed at "live rects directory missing" without
    // asserting anything about the geometry. The invariants they asserted are all kept here; the
    // coordinates are chosen to state each one plainly rather than copied from a capture.
    //
    // The curtain band is what makes the numbers below meaningful: NativeModsEntry's top-bar
    // inset (52 px at scale 1) leaves the native tab strip at y 0..48 ABOVE the curtain, and the
    // workspace's own controls sort above the curtain's canvas, so neither is covered while the
    // curtain hides the HUD between them.

    private const string WorkspaceOpenRects = """
        {
          "screenWidth": 3840, "screenHeight": 1080, "canvasScale": 1,
          "rects": [
            { "id": "native-tab", "kind": "tab", "label": "Spellbook", "index": 2,
              "x": 1702, "y": 0, "width": 145, "height": 48, "canvasScale": 1,
              "sortingOrder": 1, "visible": true, "interactable": false,
              "occludedBy": "curtain", "occluderOrder": 4999 },
            { "id": "close-back", "kind": "button", "label": "Close Mods",
              "x": 2600, "y": 120, "width": 180, "height": 44, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null },
            { "id": "search", "kind": "input", "label": "Search",
              "x": 1040, "y": 120, "width": 320, "height": 40, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null },
            { "id": "sidebar-row", "kind": "button", "label": "Progenitor Sample",
              "x": 1040, "y": 220, "width": 360, "height": 48, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null },
            { "id": "list-scrollbar-handle", "kind": "scrollbar-handle", "label": "list",
              "x": 1412, "y": 240, "width": 8, "height": 545, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null },
            { "id": "content-scrollbar-handle", "kind": "scrollbar-handle", "label": "content",
              "x": 2228, "y": 355, "width": 8, "height": 545, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null }
          ]
        }
        """;

    /// <summary>A settings page: one widget with a nested input, and its Apply button.</summary>
    private const string WorkspaceSettingsRects = """
        {
          "screenWidth": 3840, "screenHeight": 1080, "canvasScale": 1,
          "rects": [
            { "id": "widget", "kind": "setting", "label": "ExampleLimit",
              "x": 1520, "y": 300, "width": 640, "height": 56, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null,
              "controls": [
                { "id": "input", "kind": "input", "label": "input",
                  "x": 1960, "y": 314, "width": 139, "height": 28, "canvasScale": 1,
                  "sortingOrder": 5000, "visible": true, "interactable": true,
                  "occludedBy": null, "occluderOrder": null }
              ] },
            { "id": "widget", "kind": "button", "label": "Apply ExampleLimit",
              "x": 1520, "y": 368, "width": 240, "height": 40, "canvasScale": 1,
              "sortingOrder": 5000, "visible": true, "interactable": true,
              "occludedBy": null, "occluderOrder": null }
          ]
        }
        """;

    [Fact]
    public void Replayed_open_workspace_controls_are_not_occluded_by_the_curtain()
    {
        var (screenWidth, screenHeight, canvasScale, rects) = LoadRects(WorkspaceOpenRects);
        var curtain = InsetCurtain(screenWidth, screenHeight, canvasScale);
        var settings = LoadRects(WorkspaceSettingsRects);

        RecomputeAll(rects, curtain);
        RecomputeAll(settings.Rects, InsetCurtain(settings.Width, settings.Height, settings.Scale));

        AssertInteractable(rects, "close-back", "Close Mods");
        AssertInteractable(rects, "search", "Search");
        AssertInteractable(rects, "sidebar-row", "Progenitor Sample");
        AssertVisibleNotOccluded(rects, "list-scrollbar-handle");
        AssertVisibleNotOccluded(rects, "content-scrollbar-handle");
        AssertInteractable(settings.Rects, "widget", "ExampleLimit");
        AssertNestedInteractable(settings.Rects, "widget", "ExampleLimit", "input");
        AssertInteractable(settings.Rects, "widget", "Apply ExampleLimit");
        // Revert: giving workspace controls a sorting order at or below the curtain's, or
        // extending the curtain over the whole screen, occludes every row asserted here.
    }

    [Fact]
    public void Replayed_native_tabs_under_the_curtain_are_occluded_while_Mods_is_open()
    {
        var (screenWidth, screenHeight, canvasScale, rects) = LoadRects(WorkspaceOpenRects);
        var inset = InsetCurtain(screenWidth, screenHeight, canvasScale);
        var covering = ModsUiGeometry.OverlayLayer.Of(
            ModsUiGeometry.OccludedByCurtain, 0, 0, screenWidth, screenHeight,
            NativeModsEntry.CurtainCanvasSortingOrder);

        var spellbook = FindRect(rects, "native-tab", "Spellbook");
        Recompute(spellbook, inset);
        Assert.Equal(true, spellbook["visible"]);
        Assert.Null(spellbook["occludedBy"]);
        Assert.Equal(true, spellbook["interactable"]);

        Recompute(spellbook, covering);
        Assert.Equal(ModsUiGeometry.OccludedByCurtain, spellbook["occludedBy"]);
        Assert.Equal(NativeModsEntry.CurtainCanvasSortingOrder, spellbook["occluderOrder"]);
        Assert.Equal(false, spellbook["interactable"]);

        int parkedY = inset.Y + 8;
        var parked = ModsUiGeometry.Occlusion(
            RectInt(spellbook, "x"), parkedY, RectInt(spellbook, "width"), RectInt(spellbook, "height"),
            sortingOrder: 0, siblingOrder: 0,
            inset);
        Assert.Equal(ModsUiGeometry.OccludedByCurtain, parked.occludedBy);
        Assert.Equal(NativeModsEntry.CurtainCanvasSortingOrder, parked.occluderOrder);
    }

    [Fact]
    public void Workspace_control_ids_are_the_workspace_rows_only()
    {
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("content-scrollbar-handle"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("list-scrollbar-track"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("sidebar-row"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("tab-strip"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("header"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("viewport"));
        Assert.False(ModsUiGeometry.IsWorkspaceControlId("native-tab"));
        Assert.False(ModsUiGeometry.IsWorkspaceControlId("top-tab"));
    }

    [Fact]
    public void Tab_strip_rects_are_three_named_rows_with_exactly_one_selected()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var strip = MenuPanelLayout.ForStrip(frame);
        var names = new[] { "Mods", "Install mods", "Configuration" };
        var rects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < names.Length; i++)
        {
            var button = MenuPanelLayout.StripButton(strip, i, names.Length);
            var rect = ModsUiGeometry.FromCanvasLocal(
                button.X - button.Width / 2f, button.Y - button.Height / 2f,
                button.Width, button.Height, 1080, 1f, visible: true, raycastTarget: true);
            rects.Add(ModsUiGeometry.TabStripRow(rect, names[i], selected: i == 0));
        }

        Assert.Equal(3, rects.Count);
        int selected = 0;
        for (int i = 0; i < rects.Count; i++)
        {
            Assert.Equal("tab-strip", rects[i]["id"]);
            Assert.Equal("tab-strip", rects[i]["kind"]);
            Assert.Equal(names[i], rects[i]["name"]);
            Assert.Equal(names[i], rects[i]["label"]);
            Assert.Equal(true, rects[i]["visible"]);
            if (rects[i]["selected"] is true) selected++;
            Assert.True(Convert.ToInt32(rects[i]["width"]) > 1);
            Assert.True(Convert.ToInt32(rects[i]["height"]) > 1);
        }

        Assert.Equal(1, selected);
        Assert.Equal(true, rects[0]["selected"]);
        Assert.Equal(false, rects[1]["selected"]);
        Assert.True(MenuPanelLayout.StripLiesInTopInset(frame, strip));

        // Revert: dropping AddTabStrip from DoRects, or omitting tab-strip from IsWorkspaceControlId, fails this.
    }

    [Fact]
    public void Listing_header_and_viewport_rects_do_not_overlap()
    {
        var frame = ModsWorkspaceFrame.Fallback(3840f, 1080f);
        var pane = MenuPanelLayout.ForFrame(frame, sidebar: false);
        MenuPanelLayout.SplitPinnedHeader(pane, MenuPanelLayout.PinnedHeaderHeight(), out var header, out var viewport);
        var rects = new List<Dictionary<string, object?>>();
        var headerRect = ModsUiGeometry.FromCanvasLocal(
            header.X - header.Width / 2f, header.Y - header.Height / 2f,
            header.Width, header.Height, 1080, 1f, visible: true, raycastTarget: true);
        rects.Add(ModsUiGeometry.Annotate(headerRect, "header", "header", "header"));
        var viewRect = ModsUiGeometry.FromCanvasLocal(
            viewport.X - viewport.Width / 2f, viewport.Y - viewport.Height / 2f,
            viewport.Width, viewport.Height, 1080, 1f, visible: true, raycastTarget: false);
        rects.Add(ModsUiGeometry.Annotate(viewRect, "viewport", "viewport", "viewport"));
        Assert.Equal("header", rects[0]["kind"]);
        Assert.Equal("viewport", rects[1]["kind"]);
        int hx = Convert.ToInt32(rects[0]["x"]);
        int hy = Convert.ToInt32(rects[0]["y"]);
        int hw = Convert.ToInt32(rects[0]["width"]);
        int hh = Convert.ToInt32(rects[0]["height"]);
        int vx = Convert.ToInt32(rects[1]["x"]);
        int vy = Convert.ToInt32(rects[1]["y"]);
        int vw = Convert.ToInt32(rects[1]["width"]);
        int vh = Convert.ToInt32(rects[1]["height"]);
        Assert.False(ModsUiGeometry.Overlaps(hx, hy, hw, hh, vx, vy, vw, vh));
        Assert.True(hy + hh <= vy + 1);
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("header"));
        Assert.True(ModsUiGeometry.IsWorkspaceControlId("viewport"));
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
        SourcePin.AssertUncommented(ops, "AddListingHeader(");
        // Revert: omitting header/viewport rect rows, or letting them overlap, fails this.
    }

    [Fact]
    public void Status_fontDonor_and_rects_tmp_pass_through()
    {
        var donor = ModsUiResult.FontDonor(
            "BORUTTA GROUP - Nocturne Serif Regular SDF", "", UiFontDonor.SourceAssetList);
        var assets = new[]
        {
            FontAssetReadback.Shape(
                "BORUTTA GROUP - Nocturne Serif Regular SDF", 51, 1f, 5, 28.4f, 11.2f, null),
            FontAssetReadback.Shape(
                "BORUTTA GROUP - Nocturne Serif Regular MainMenu", 51, 1f, 5, 40.1f, 11.2f, null),
        };
        var status = ModsUiResult.Status(
            open: true, site: "shortcut", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 1920, height: 1080,
            mode: "window", panes: 2, observers: 0, fontDonor: donor, fontAssets: assets);
        var read = Assert.IsType<Dictionary<string, object?>>(status["fontDonor"]);
        Assert.Equal("BORUTTA GROUP - Nocturne Serif Regular SDF", read["font"]);
        Assert.Equal("", read["path"]);
        Assert.Equal(UiFontDonor.SourceAssetList, read["source"]);
        var listed = Assert.IsType<Dictionary<string, object?>[]>(status["fontAssets"]);
        Assert.Equal(2, listed.Length);
        Assert.True((float)listed[1]["advanceN"]! > (float)listed[0]["advanceN"]!);
        string json = JsonSerializer.Serialize(status, ModsUiResult.JsonOptions);
        Assert.Contains("\"fontDonor\":", json);
        Assert.Contains("\"source\":\"asset-list\"", json);
        Assert.Contains("\"fontAssets\":", json);
        Assert.Contains("\"advanceN\":", json);

        var tmp = TmpReadback.LabelDefaults(UiFontDonor.BodyFontName);
        var row = new Dictionary<string, object?> { ["kind"] = "label", ["label"] = "Mods" };
        ModsUiResult.AttachTmp(row, tmp);
        Assert.Same(tmp, row["tmp"]);
        string rowJson = JsonSerializer.Serialize(row, ModsUiResult.JsonOptions);
        Assert.Contains("\"characterSpacing\":0", rowJson);
        using var doc = JsonDocument.Parse(rowJson);
        var parsed = ModsUiResult.ReadTmpElement(doc.RootElement);
        Assert.NotNull(parsed);
        // Revert: dropping fontDonor or AttachTmp hides the live TMP readback.
    }

    [Fact]
    public void DescribeGeometry_is_read_only_and_never_throws()
    {
        var described = ModsMenu.DescribeGeometry();
        string json = JsonSerializer.Serialize(described);
        Assert.Contains("\"open\":", json);
        Assert.Contains("\"sidebar\":", json);
        Assert.Contains("\"listWidgets\":", json);
        Assert.Contains("\"contentWidgets\":", json);
        Assert.Contains("\"listOpen\":", json);
        Assert.Contains("\"contentOpen\":", json);
        Assert.Contains("\"playBackdrop\":", json);
        Assert.Contains("\"ownedTopTabCount\":", json);
        Assert.Contains("\"firstShowFrame\":", json);
        Assert.Contains("\"layoutBeforeShow\":", json);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>
    /// Parse one <c>mods_ui rects</c> readback: the <c>detail</c> object's
    /// <c>{screenWidth, screenHeight, canvasScale, rects[]}</c>, in the shape
    /// <see cref="ModsUiGeometry.FromUnityScreen"/> writes and the bridge prints.
    /// </summary>
    private static (int Width, int Height, float Scale, List<Dictionary<string, object?>> Rects) LoadRects(string fixture)
    {
        using var doc = JsonDocument.Parse(fixture);
        JsonElement root = doc.RootElement;
        int width = root.GetProperty("screenWidth").GetInt32();
        int height = root.GetProperty("screenHeight").GetInt32();
        float scale = root.GetProperty("canvasScale").GetSingle();
        var rects = new List<Dictionary<string, object?>>();
        foreach (var el in root.GetProperty("rects").EnumerateArray())
            rects.Add(RectFromJson(el));
        Assert.NotEmpty(rects);
        return (width, height, scale, rects);
    }

    private static Dictionary<string, object?> RectFromJson(JsonElement el)
    {
        var rect = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            rect[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number when prop.Value.TryGetInt32(out var i) => i,
                JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Array when prop.Name == "controls" => ControlsFromJson(prop.Value),
                _ => prop.Value.Clone(),
            };
        }

        return rect;
    }

    private static List<Dictionary<string, object?>> ControlsFromJson(JsonElement arr)
    {
        var list = new List<Dictionary<string, object?>>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in arr.EnumerateArray())
            list.Add(RectFromJson(el));
        return list;
    }

    private static ModsUiGeometry.OverlayLayer InsetCurtain(int screenWidth, int screenHeight, float canvasScale)
    {
        int inset = (int)Math.Round(
            -NativeModsEntry.FullscreenMenuTopBarInset * ModsUiGeometry.EffectiveScale(canvasScale));
        if (inset < 0) inset = 0;
        if (inset > screenHeight) inset = screenHeight;
        return ModsUiGeometry.OverlayLayer.Of(
            ModsUiGeometry.OccludedByCurtain,
            0, inset, screenWidth, Math.Max(0, screenHeight - inset),
            NativeModsEntry.CurtainCanvasSortingOrder);
    }

    private static void RecomputeAll(
        List<Dictionary<string, object?>> rects,
        ModsUiGeometry.OverlayLayer curtain)
    {
        foreach (var rect in rects)
            Recompute(rect, curtain);
    }

    private static void Recompute(
        Dictionary<string, object?> rect,
        ModsUiGeometry.OverlayLayer curtain)
    {
        string id = rect.GetValueOrDefault("id") as string ?? "";
        int sorting = SortingFor(id);
        rect["sortingOrder"] = sorting;
        bool visible = rect.GetValueOrDefault("visible") is true;
        string? oldOcc = rect.GetValueOrDefault("occludedBy") as string;
        bool oldInt = rect.GetValueOrDefault("interactable") is true;
        bool raycast = visible && (oldInt || oldOcc != null);
        var (occludedBy, occluderOrder) = ModsUiGeometry.Occlusion(
            RectInt(rect, "x"), RectInt(rect, "y"), RectInt(rect, "width"), RectInt(rect, "height"),
            sorting, 0, curtain, selfIsCurtain: false);
        rect["occludedBy"] = occludedBy;
        rect["occluderOrder"] = occludedBy == null ? null : occluderOrder;
        rect["interactable"] = ModsUiGeometry.IsInteractable(visible, raycast, occludedBy);
        if (rect.GetValueOrDefault("controls") is List<Dictionary<string, object?>> controls)
        {
            foreach (var child in controls)
                Recompute(child, curtain);
        }
    }

    private static int SortingFor(string id) => id switch
    {
        "native-tab" or "top-tab" or "main-menu" => 0,
        _ => UiOwnership.OverlaySorting,
    };

    private static Dictionary<string, object?> FindRect(
        List<Dictionary<string, object?>> rects, string id, string? label = null)
    {
        foreach (var rect in rects)
        {
            if (!string.Equals(rect.GetValueOrDefault("id") as string, id, StringComparison.Ordinal))
                continue;
            if (label != null
                && !string.Equals(rect.GetValueOrDefault("label") as string, label, StringComparison.Ordinal))
                continue;
            return rect;
        }

        Assert.Fail("no rect id=" + id + (label == null ? "" : " label=" + label));
        return new Dictionary<string, object?>();
    }

    private static void AssertInteractable(List<Dictionary<string, object?>> rects, string id, string label)
    {
        var rect = FindRect(rects, id, label);
        Assert.Equal(true, rect["visible"]);
        Assert.Null(rect["occludedBy"]);
        Assert.Null(rect["occluderOrder"]);
        Assert.Equal(true, rect["interactable"]);
        Assert.Equal(UiOwnership.OverlaySorting, rect["sortingOrder"]);
    }

    private static void AssertNestedInteractable(
        List<Dictionary<string, object?>> rects, string id, string label, string nestedId)
    {
        var rect = FindRect(rects, id, label);
        var controls = Assert.IsType<List<Dictionary<string, object?>>>(rect["controls"]);
        var nested = FindRect(controls, nestedId);
        Assert.Equal(true, nested["visible"]);
        Assert.Null(nested["occludedBy"]);
        Assert.Equal(true, nested["interactable"]);
    }

    private static void AssertVisibleNotOccluded(List<Dictionary<string, object?>> rects, string id)
    {
        var rect = FindRect(rects, id);
        Assert.Null(rect["occludedBy"]);
        Assert.Null(rect["occluderOrder"]);
    }

    private static int RectInt(Dictionary<string, object?> rect, string key)
    {
        if (!rect.TryGetValue(key, out var raw) || raw == null) return 0;
        return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeBind : IKeybindInfo
    {
        public FakeBind(string pluginId, string id, KeyChord chord)
        {
            PluginId = pluginId;
            Id = id;
            Chord = chord;
            Description = "";
            DefaultChord = chord;
        }

        public string PluginId { get; }
        public string Id { get; }
        public string Description { get; }
        public KeyChord Chord { get; }
        public KeyChord DefaultChord { get; }
    }

    private sealed class ThrowingBind : IKeybindInfo
    {
        public string PluginId => throw new InvalidOperationException("test bind");
        public string Id => throw new InvalidOperationException("test bind");
        public string Description => throw new InvalidOperationException("test bind");
        public KeyChord Chord => throw new InvalidOperationException("test bind");
        public KeyChord DefaultChord => throw new InvalidOperationException("test bind");
    }

    private sealed class ThrowingBindList : IReadOnlyList<IKeybindInfo>
    {
        public IKeybindInfo this[int index] => throw new InvalidOperationException("test list");
        public int Count => throw new InvalidOperationException("test list");
        public IEnumerator<IKeybindInfo> GetEnumerator() => throw new InvalidOperationException("test list");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
