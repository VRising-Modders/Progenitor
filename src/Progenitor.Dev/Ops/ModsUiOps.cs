using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Ui;

#if !PROGENITOR_TESTS
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;
using UiApi = Progenitor.Ui.Ui;
#endif

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument policy and result shape for the client <c>mods_ui</c> op. Live Unity
/// work lives in <see cref="ModsUiOps"/> behind <c>!PROGENITOR_TESTS</c> so this file
/// compiles into the test project without the game.
/// </summary>
internal static class ModsUiArgs
{
    internal const string ClientOnlyMessage = "mods_ui runs on the client side only";

    /// <summary>
    /// Delay until the follow-up Drain that reads curtain / capture / <c>Screen</c> after a
    /// mutation. <c>0</c> is the next Drain: the current Drain has already flushed
    /// <c>PostDelayed</c> at its start, so a due-now item runs on the following frame
    /// rather than re-entering this one. The socket waits on <c>DeferredResult</c>; the
    /// game thread is never slept.
    /// </summary>
    internal const double NextDrainDelaySeconds = 0;

    /// <summary>
    /// Waiter budget for a next-Drain readback, including <c>open site=top-tab</c> polling
    /// up to <see cref="TopTabAttachFrames"/> Drains. The bridge adds 5 s slack on top.
    /// </summary>
    internal const int NextDrainWaitMs = 10000;

    internal const string WorkspaceClosedError = "workspace closed";

    /// <summary>
    /// <c>open site=top-tab</c> waited a Drain for the owned button after requesting
    /// FullscreenMenu, and it never attached.
    /// </summary>
    internal const string TopTabButtonNotAttached = "top-tab button not attached";

    /// <summary>
    /// Same bound NativeModsEntry uses after <c>HUDMenuManager.GoToMenu(FullscreenMenu)</c>.
    /// Keep equal to <c>NativeModsEntry.FullscreenMenuAttachFrames</c>.
    /// </summary>
    internal const int TopTabAttachFrames = 120;

    internal const string MenuOpenedByGoToMenu = "HUDMenuManager.GoToMenu";
    internal const string MenuOpenedByAlreadyUp = "already-up";
    internal const string PendingTopTabKey = "pendingTopTab";
    internal const string RemainingTopTabKey = "remainingFrames";
    internal const string MenuRestoredKey = "menuRestored";

    /// <summary>
    /// How <c>open site=top-tab</c> brought up FullscreenMenu. <paramref name="requestedMenu"/>
    /// is the return of <c>ModsMenu.TryRequestFullscreenMenu</c>.
    /// </summary>
    internal static string TopTabMenuOpenedBy(bool requestedMenu) =>
        requestedMenu ? MenuOpenedByGoToMenu : MenuOpenedByAlreadyUp;

    /// <summary>True when this op requested FullscreenMenu and must undo that on attach failure.</summary>
    internal static bool ShouldRestoreMenuOnAttachFailure(string? menuOpenedBy) =>
        string.Equals(menuOpenedBy, MenuOpenedByGoToMenu, StringComparison.Ordinal);

    /// <summary>Handler names reported by <c>open site=top-tab</c> and <c>close</c> per route (job NATIVE3 §2).</summary>
    internal const string TopTabOnClickHandler = "progenitor-ui-core-mods-tab.onClick";
    internal const string TopTabNativeHandler = "NativeOuterTabRegistration.TrySelectOwned+ModsMenu.Open";
    internal const string CloseHandler = "ModsMenu.Close";
    internal const string CloseNativeHandler = "ModsMenu.Close+NativeOuterTabRegistration.ReturnSelection";
    internal const string NativeTabNoMenuError = "native_tab is a no-op without the FullscreenMenu up";
    internal const string NativeTabNoMenuReason = "no-menu";

    /// <summary>
    /// On the native route the owned tab has no uGUI <c>onClick</c> (its click is the game's own
    /// <c>OnLeftClicked</c> field through the Bootstrap trampoline), so the op fires the same
    /// path a click does through <c>ModsMenu.TryOpenNativeRoute</c>; the imitation route keeps
    /// the owned button's <c>onClick</c>.
    /// </summary>
    internal static bool ShouldOpenThroughNativeRoute(string site, bool routeIsNative) =>
        routeIsNative && string.Equals(site, "top-tab", StringComparison.Ordinal);

    internal static string TopTabHandler(bool routeIsNative) =>
        routeIsNative ? TopTabNativeHandler : TopTabOnClickHandler;

    /// <summary>
    /// <c>native_tab</c> needs the FullscreenMenu up: without it there is no outer tab to click,
    /// and the mapper's remembered menu is not a live target. A no-op with a reason.
    /// </summary>
    internal static bool NativeTabRequiresMenu(bool fullscreenMenuUp) => !fullscreenMenuUp;

    internal static Dictionary<string, object?> NativeTabNoMenuDetail(string menu) => new()
    {
        ["error"] = NativeTabNoMenuError,
        ["reason"] = NativeTabNoMenuReason,
        ["noop"] = true,
        ["menu"] = menu,
        ["readback"] = "next-drain",
    };

    /// <summary>Keep polling while frames remain. Zero is the miss that restores or fails.</summary>
    internal static bool ShouldKeepWaitingTopTab(int remainingFrames) => remainingFrames > 0;

    /// <summary>The owned top-tab button is invoked once, when it first attaches.</summary>
    internal static bool ShouldInvokeOwnedButton(bool buttonAttached, bool alreadyInvoked) =>
        buttonAttached && !alreadyInvoked;

    internal static int ConsumeTopTabFrame(int remainingFrames) =>
        remainingFrames > 0 ? remainingFrames - 1 : 0;

    internal static bool IsPendingTopTab(object? detail) =>
        detail is Dictionary<string, object?> d
        && d.TryGetValue(PendingTopTabKey, out var v)
        && v is bool pending
        && pending;

    internal static int TopTabRemainingFrames(object? detail)
    {
        if (detail is Dictionary<string, object?> d
            && d.TryGetValue(RemainingTopTabKey, out var raw)
            && raw is int n)
            return n;
        return 0;
    }

    internal static Dictionary<string, object?> TopTabNotAttachedDetail(
        string menuOpenedBy,
        string menu,
        bool? menuRestored = null)
    {
        var detail = new Dictionary<string, object?>
        {
            ["path"] = "top-tab",
            ["error"] = TopTabButtonNotAttached,
            ["menuOpenedBy"] = menuOpenedBy,
            ["menu"] = menu,
            ["readback"] = "next-drain",
        };
        if (menuRestored != null) detail[MenuRestoredKey] = menuRestored.Value;
        return detail;
    }

    internal static readonly string[] Actions =
    {
        "status", "open", "close", "back", "select", "select_tab", "search", "scroll",
        "widget_read", "widget_click", "native_tab", "resolution",
        "rects", "window", "scene", "menu", "hit", "tabs", "keys",
        "nav_back", "nav_forward", "nav_state", "menu_tab_state", "dialog_choose", "dialog_type",
        // TEXTTEST1: focus-free text-field tests through the real input paths.
        "field_focus", "field_blur", "field_submit", "type", "paste", "perf",
        // TEXTEXPAND1 / TEXTLIMIT1: the expanded editor, wheel scrolling, Escape, and an admin's Apply.
        "field_expand", "field_scroll", "field_cancel", "field_apply",
        // JOINFLOW1: the date-time picker (ModsUiOps.DateTime.cs).
        "datetime_open", "datetime_read", "datetime_month", "datetime_day", "datetime_time", "datetime_done", "datetime_cancel",
        // Settings organization: every placed setting of Progenitor's own, its home, its places and its stored value.
        "settings_places",
        // Three homes: a left-list group jump, and a settings drawer's toggle.
        "section", "drawer",
        // DROPDOWN1: pick an option from a dropdown's list (the list opens as a click opens it).
        "dropdown_choose",
        // RADIO1: choose one line of a radio list (as a click on that line: one write, never a toggle).
        "radio_choose",
        // OPTIONHOVER1: the pointer over a dropdown's or radio list's option (the details pane describes it), or leaving it.
        "hover",
        // INSTALLRESTART1: the pending-restart bar: read it, press it, answer the confirmation (dryRun), pick up after a restart.
        "restart",
        // RADIOCLICK1: a click through the event system raycast and the hit object's own handler (a pointer click, no OS input).
        "click_at",
    };

    /// <summary>JOINFLOW1: the date-time picker actions (<c>ModsUiOps.DateTime.cs</c>).</summary>
    internal static bool IsDateTimeAction(string action) => action.StartsWith("datetime_", StringComparison.Ordinal);

    /// <summary>TEXTTEST1: <c>type</c>'s default and largest items per frame.</summary>
    internal const int DefaultPerFrame = 1;
    internal const int MaxPerFrame = 1000;

    /// <summary>TEXTTEST1: <c>perf</c>'s default and largest frame count.</summary>
    internal const int DefaultPerfFrames = 120;
    internal const int MaxPerfFrames = 3000;

    /// <summary>
    /// TEXTTEST1: how long a deferred text test may run in game time before it reports what it
    /// has; under the bridge's 30 s gate with its 5 s slack.
    /// </summary>
    internal const int TextTestBudgetMs = 20_000;

    /// <summary>TEXTTEST1: the text ops that act on (or need) a focused Mods workspace field.</summary>
    internal static bool IsTextFieldAction(string action) =>
        action is "field_focus" or "field_blur" or "field_submit" or "type" or "paste"
            or "field_expand" or "field_scroll" or "field_cancel" or "field_apply";

    /// <summary>TEXTEXPAND1 <c>field_scroll</c>: the most notches one call applies.</summary>
    internal const int MaxScrollNotches = 10_000;

    /// <summary>
    /// Per-tab field on <c>mods_ui tabs</c>. Filled by
    /// <c>NativeModsEntry.DescribeOneOuterTab</c> (on-demand
    /// <c>GetComponentsInChildren&lt;Graphic&gt;</c>, never per frame).
    /// Each row is <c>{path, type, enabled, activeInHierarchy, rgba, sprite,
    /// crossFadeAlpha, canvasRendererColor, rect, siblingIndex}</c>
    /// plus <c>fontAsset</c> / <c>material</c> on TMP.
    /// </summary>
    internal const string TabsGraphicsField = "graphics";

    /// <summary>Per native/owned row: <c>Selectable.interactable</c>.</summary>
    internal const string TabsInteractableField = "interactable";

    /// <summary>Per native/owned row: <c>SimpleStunButton.IsHovered</c>.</summary>
    internal const string TabsIsHoveredField = "isHovered";

    /// <summary>Per native/owned row: root Graphic <c>raycastTarget</c>.</summary>
    internal const string TabsRaycastTargetField = "raycastTarget";

    /// <summary>Per graphics row: <c>canvasRenderer.GetColor().a</c>.</summary>
    internal const string TabsCrossFadeAlphaField = "crossFadeAlpha";

    /// <summary>Per graphics row: <c>canvasRenderer.GetColor()</c> rgba.</summary>
    internal const string TabsCanvasRendererColorField = "canvasRendererColor";

    /// <summary>
    /// Per graphics row: screen-pixel <c>{x,y,w,h}</c> from
    /// <c>TransformPoint</c> of two local corners.
    /// </summary>
    internal const string TabsGraphicRectField = "rect";

    /// <summary>Per graphics row: <c>transform.GetSiblingIndex()</c>.</summary>
    internal const string TabsSiblingIndexField = "siblingIndex";

    /// <summary>
    /// These actions only exist on an open two-pane workspace. Closed, they must not
    /// host settings without a sidebar or otherwise mutate state the real entry never produces.
    /// </summary>
    internal static bool RequiresOpenWorkspace(string action, string? op = null) =>
        action is "select" or "select_tab" or "search" or "scroll" or "widget_read" or "widget_click" or "section" or "drawer"
            or "dropdown_choose" or "radio_choose" or "hover"
        || IsTextFieldAction(action)
        || (action is "keys" && !IsKeyConflictOp(op));

    /// <summary>
    /// A <c>widget_click</c> that names a dialog's control by its label alone (no pane, no index) goes to the open
    /// dialog first, so it is allowed with the workspace closed while a dialog is open over the HUD (a chat tab's
    /// editor, a key-conflict prompt). Anything else still needs the open workspace.
    /// </summary>
    internal static bool RefusedWithWorkspaceClosed(Request request, bool dialogOpen)
    {
        if (!RequiresOpenWorkspace(request.Action, request.Op)) return false;
        return !(dialogOpen && TargetsDialog(request));
    }

    /// <summary>A label-only <c>widget_click</c>: the form <see cref="ModsMenu.ClickVisibleWidget"/> sends to the open dialog.</summary>
    internal static bool TargetsDialog(Request request) =>
        request.Action == "widget_click" && request.Pane == null && request.Index == null && !string.IsNullOrWhiteSpace(request.Label);

    /// <summary>
    /// KEYS4: <c>keys op=conflict</c> / <c>conflict-choose</c> read and answer the key-conflict dialog,
    /// which can be on screen over the HUD with the workspace closed.
    /// </summary>
    internal static bool IsKeyConflictOp(string? op) =>
        (op ?? "").Trim().StartsWith("conflict", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same-Drain <c>after.open</c> with no curtain is a lie: the curtain is created on
    /// <c>NativeModsEntry.Tick</c> after Drain. Defer that readback.
    /// </summary>
    internal static bool SameDrainOpenWithoutCurtain(IReadOnlyDictionary<string, object?> status)
    {
        bool open = status.TryGetValue("open", out var o) && o is bool ob && ob;
        return open && !CurtainPresent(status);
    }

    /// <summary>
    /// <c>status.curtain</c> is <c>{present, raycastTarget}</c>. Older bool
    /// payloads still count as present.
    /// </summary>
    internal static bool CurtainPresent(IReadOnlyDictionary<string, object?> status)
    {
        if (status == null || !status.TryGetValue("curtain", out var c) || c == null)
            return false;
        if (c is bool b) return b;
        if (c is IReadOnlyDictionary<string, object?> d)
            return d.TryGetValue("present", out var p) && p is bool pb && pb;
        if (c is Dictionary<string, object?> dict)
            return dict.TryGetValue("present", out var p2) && p2 is bool pb2 && pb2;
        return false;
    }

    internal sealed class Request
    {
        internal string Action { get; init; } = "";
        internal string? Site { get; init; }
        internal string? Plugin { get; init; }
        internal string? Page { get; init; }
        internal string? Group { get; init; }
        internal string? Text { get; init; }
        internal string? Pane { get; init; }
        internal string? To { get; init; }
        internal double? ToNumber { get; init; }
        internal string? Label { get; init; }
        internal int? Index { get; init; }
        internal string? Value { get; init; }

        /// <summary>BRIDGEUI1 <c>widget_click</c>: Shift (1) and Ctrl (2) held for the click (multi-key sort, range select).</summary>
        internal int Modifiers { get; init; }
        internal string? Tab { get; init; }
        internal int? TabIndex { get; init; }
        internal bool Same { get; init; }
        internal int Width { get; init; }
        internal int Height { get; init; }
        internal string? Mode { get; init; }
        internal float? X { get; init; }
        internal float? Y { get; init; }
        internal bool NeedsNextDrain { get; init; }

        /// <summary>Three homes <c>drawer</c>: open (true), close (false), or toggle (null).</summary>
        internal bool? Open { get; init; }

        /// <summary>OPTIONHOVER1 <c>hover</c>: the option (its value or label) the pointer goes over.</summary>
        internal string? Option { get; init; }

        /// <summary>OPTIONHOVER1 <c>hover</c>: the pointer leaves the options instead.</summary>
        internal bool Leave { get; init; }

        /// <summary>C5 <c>hover</c>: a hover cell's key on the cell row <see cref="Label"/> names (the list icon's <c>across</c>).</summary>
        internal string? Cell { get; init; }

        /// <summary>
        /// <c>args.withStatus</c>: a light read (<see cref="ModsUiArgs.IsLightRead"/>) also takes the full
        /// before/after workspace status. Ignored by every other op, which always takes it.
        /// </summary>
        internal bool WithStatus { get; set; }
        internal bool Fonts { get; init; }

        /// <summary>KEYS1 <c>keys</c>: the operation, its target (a section, row id or plugin id) and a chord.</summary>
        internal string? Op { get; init; }

        /// <summary>INSTALLRESTART1 <c>restart</c>: answer the confirmation without leaving or quitting.</summary>
        internal bool DryRun { get; init; }
        internal string? Target { get; init; }
        internal string? Chord { get; init; }

        /// <summary>DIALOGS3 <c>dialog_choose</c>: a button's caption, <c>yes</c>, <c>no</c>, <c>cancel</c> or an index.</summary>
        internal string? Choice { get; init; }

        /// <summary>TEXTTEST1 <c>type</c>: <c>os-text</c> (default) or <c>keys</c>.</summary>
        internal string Via { get; init; } = "os-text";

        /// <summary>TEXTTEST1 <c>type via=keys</c>: chord names (<c>H</c>, <c>Shift+1</c>, <c>Ctrl+A</c>, <c>Enter</c>).</summary>
        internal string[]? Keys { get; init; }

        /// <summary>TEXTTEST1: items injected per frame (<c>type</c>).</summary>
        internal int PerFrame { get; init; } = DefaultPerFrame;

        /// <summary>TEXTTEST1 <c>paste</c>: repeat <see cref="Text"/> this many times.</summary>
        internal int Repeat { get; init; } = 1;

        /// <summary>TEXTTEST1 <c>paste</c>: generate this many characters instead of <see cref="Text"/>.</summary>
        internal int Length { get; init; }

        /// <summary>TEXTTEST1 <c>paste</c>: the generated text's seed.</summary>
        internal int Seed { get; init; } = 1;

        /// <summary>TEXTTEST1 <c>perf</c> frames, or <c>paste</c>'s <c>measureFrames</c> (0 = none).</summary>
        internal int Frames { get; init; }

        /// <summary>JOINFLOW1 <c>datetime_*</c>: the arguments, read by <c>ModsUiOps.DateTime.cs</c>.</summary>
        internal JsonElement Raw { get; init; }
    }

    private static Request ParseFieldFocus(JsonElement args, string action)
    {
        string? label = OptionalString(args, "label");
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label is required: the row caption of the field (or \"search\", \"dialog\")");
        string? pane = OptionalString(args, "pane");
        if (pane != null && pane.Trim().Length > 0
            && !(pane.Trim().ToLowerInvariant() is "content" or "sidebar" or "dialog"))
            throw new ArgumentException("args.pane must be content, sidebar or dialog (got '" + pane + "')");
        return new Request { Action = action, Label = label!.Trim(), Pane = string.IsNullOrWhiteSpace(pane) ? null : pane!.Trim().ToLowerInvariant() };
    }

    private static Request ParseType(JsonElement args, string action)
    {
        string via = (OptionalString(args, "via") ?? "os-text").Trim().ToLowerInvariant();
        if (via is not ("os-text" or "keys")) throw new ArgumentException("args.via must be os-text or keys (got '" + via + "')");
        int perFrame = OptionalInt(args, "perFrame") ?? DefaultPerFrame;
        if (perFrame < 1 || perFrame > MaxPerFrame) throw new ArgumentException("args.perFrame must be 1.." + MaxPerFrame);
        string? text = OptionalString(args, "text");
        string[]? keys = null;
        if (via == "keys")
        {
            keys = StringArray(args, "keys");
            if (keys == null || keys.Length == 0) throw new ArgumentException("args.keys is required for via=keys: an array of chord names, e.g. [\"H\",\"Shift+1\",\"Enter\"]");
        }
        else if (string.IsNullOrEmpty(text)) throw new ArgumentException("args.text is required for via=os-text");
        return new Request { Action = action, Via = via, Text = text, Keys = keys, PerFrame = perFrame };
    }

    private static Request ParsePaste(JsonElement args, string action)
    {
        string? text = OptionalString(args, "text");
        int repeat = OptionalInt(args, "repeat") ?? 1;
        int length = OptionalInt(args, "length") ?? 0;
        int seed = OptionalInt(args, "seed") ?? 1;
        int measure = OptionalInt(args, "measureFrames") ?? 0;
        if (repeat < 1) throw new ArgumentException("args.repeat must be at least 1");
        if (length < 0 || length > MaxPasteLength)
            throw new ArgumentException("args.length must be 0.." + MaxPasteLength);
        if (length == 0 && string.IsNullOrEmpty(text)) throw new ArgumentException("args.text or args.length is required");
        if (measure < 0 || measure > MaxPerfFrames) throw new ArgumentException("args.measureFrames must be 0.." + MaxPerfFrames);
        return new Request { Action = action, Text = text, Repeat = repeat, Length = length, Seed = seed, Frames = measure };
    }

    /// <summary>TEXTEXPAND1 <c>field_expand</c> / TEXTLIMIT1 <c>field_apply</c>: the settings row's caption.</summary>
    private static Request ParseRowLabel(JsonElement args, string action)
    {
        string? label = OptionalString(args, "label");
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label is required: the settings row's caption (its display name)");
        return new Request { Action = action, Label = label!.Trim() };
    }

    /// <summary>
    /// FIELDAPPLY1 <c>field_apply {label, text?}</c>: <c>text</c>, when given, goes in the field as typing would, then the
    /// row's Apply button is pressed (the same path: a wipe row opens its confirmation and writes nothing until it is
    /// answered). Live 2026-10-06: <c>text</c> was ignored, so Apply sent the unchanged value and nothing happened.
    /// </summary>
    internal static Request ParseFieldApply(JsonElement args, string action)
    {
        var r = ParseRowLabel(args, action);
        return new Request { Action = r.Action, Label = r.Label, Value = OptionalString(args, "text") };
    }

    /// <summary>TEXTEXPAND1 <c>field_scroll</c>: <c>notches</c> (positive toward the end) or <c>to</c> (<c>top</c> / <c>end</c>).</summary>
    private static Request ParseFieldScroll(JsonElement args, string action)
    {
        string? to = OptionalString(args, "to")?.Trim().ToLowerInvariant();
        int? notches = OptionalInt(args, "notches");
        if (!string.IsNullOrEmpty(to))
        {
            if (to is not ("top" or "end")) throw new ArgumentException("args.to must be top or end (got '" + to + "')");
            return new Request { Action = action, To = to };
        }
        if (notches is not int n || n == 0 || n < -MaxScrollNotches || n > MaxScrollNotches)
            throw new ArgumentException("args.notches (non-zero, ±" + MaxScrollNotches + "; positive scrolls toward the end) or args.to (top/end) is required");
        return new Request { Action = action, Index = n };
    }

    private static Request ParsePerf(JsonElement args, string action)
    {
        int frames = OptionalInt(args, "frames") ?? DefaultPerfFrames;
        if (frames < 1 || frames > MaxPerfFrames) throw new ArgumentException("args.frames must be 1.." + MaxPerfFrames);
        return new Request { Action = action, Frames = frames };
    }

    /// <summary>
    /// TEXTTEST1 <c>paste</c>: the longest generated or repeated text. Equal to Core's
    /// <c>UiTextTestInput.MaxGeneratedLength</c> (Core's type is internal; Core generates the text).
    /// </summary>
    internal const int MaxPasteLength = 1_000_000;

    private static string[]? StringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.String) return new[] { el.GetString() ?? "" };
        if (el.ValueKind != JsonValueKind.Array) throw new ArgumentException("args." + name + " must be an array of strings");
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new ArgumentException("args." + name + " must be an array of strings");
            list.Add(item.GetString() ?? "");
        }
        return list.ToArray();
    }

    internal static Request Parse(JsonElement args)
    {
        var request = ParseAction(args);
        request.WithStatus = OptionalBool(args, "withStatus") ?? false;
        return request;
    }

    /// <summary>
    /// Bridge cost (live 2026-09-30: polling <c>menu_tab_state</c> and <c>nav_state</c> once a second made the
    /// game stutter once a second): the full workspace status every op wraps its result in costs about 90 ms on
    /// the main thread (<c>readback-status</c>). These ops only read and change nothing, so they skip it unless
    /// <c>withStatus: true</c>: <c>menu_tab_state</c>, <c>nav_state</c>, <c>rects</c>, <c>window</c>,
    /// <c>scene</c>, <c>tabs</c>, and <c>keys</c> with <c>op</c> <c>read</c> / <c>conflict</c> /
    /// <c>conflict-read</c>. <c>status</c> is the status itself and stays full.
    /// </summary>
    internal static bool IsLightRead(string action, string? op)
    {
        switch (action)
        {
            case "menu_tab_state":
            case "nav_state":
            case "rects":
            case "window":
            case "scene":
            case "tabs":
            // TEXTTEST1: perf only samples; a 90 ms status read would be measured as a hitch.
            case "perf":
            // Settings organization: one store read per setting, nothing changed.
            case "settings_places":
                return true;
            case "restart":
                // INSTALLRESTART1: reading the bar changes nothing.
                return (op ?? "read").Trim().ToLowerInvariant() == "read";
            case "keys":
                string o = (op ?? "read").Trim().ToLowerInvariant();
                return o is "read" or "conflict" or "conflict-read";
            case "menu":
                // GAMEMENU1: the state read is light (it carries workspaceOpen itself); the legacy
                // no-op probe and open / close keep the full status.
                return op == GameMenuPlan.OpStatus;
            default:
                return false;
        }
    }

    /// <summary>True when this request skips the before/after workspace status (a light read without <c>withStatus</c>).</summary>
    internal static bool SkipsStatus(Request request) => IsLightRead(request.Action, request.Op) && !request.WithStatus;

    /// <summary>What a light read reports instead of the status: a marker and whether the workspace is open (no build).</summary>
    internal static Dictionary<string, object?> SkippedStatus(bool open) => new(StringComparer.Ordinal)
    {
        ["skipped"] = true,
        ["reason"] = "light read; pass withStatus: true for the full status",
        ["open"] = open,
    };

    private static Request ParseAction(JsonElement args)
    {
        string action = RequiredAction(args);
        return action switch
        {
            "status" => ParseStatus(args, action),
            "open" => ParseOpen(args, action),
            "close" => new Request { Action = action, NeedsNextDrain = true },
            "back" => new Request { Action = action, NeedsNextDrain = true },
            "select" => ParseSelect(args, action),
            "select_tab" => ParseSelectTab(args, action),
            "search" => ParseSearch(args, action),
            "scroll" => ParseScroll(args, action),
            "widget_read" => ParseWidgetRead(args, action),
            "widget_click" => ParseWidgetClick(args, action),
            "dropdown_choose" => ParseDropdownChoose(args, action),
            "radio_choose" => ParseRadioChoose(args, action),
            "hover" => ParseHover(args, action),
            "dialog_choose" => ParseDialogChoose(args, action),
            "dialog_type" => new Request { Action = action, Value = OptionalString(args, "text") ?? "" },
            "native_tab" => ParseNativeTab(args, action),
            "resolution" => ParseResolution(args, action),
            "rects" => new Request { Action = action },
            "window" => new Request { Action = action },
            "scene" => new Request { Action = action },
            "menu" => ParseMenu(args, action),
            "hit" => ParseHit(args, action),
            "click_at" => ParseClickAt(args, action),
            "tabs" => new Request { Action = action },
            "keys" => ParseKeys(args, action),
            // KEYS4: back/forward through the navigation keybinds (the same path a mouse button takes).
            "nav_back" or "nav_forward" => new Request { Action = action, NeedsNextDrain = true },
            "nav_state" => new Request { Action = action },
            // KEYS7: the game's full-screen menu tab (and inner tab) as the back/forward history reads it.
            "menu_tab_state" => new Request { Action = action },
            "settings_places" => new Request { Action = action },
            "section" => ParseSection(args, action),
            "drawer" => ParseDrawer(args, action),
            "restart" => new Request { Action = action, Op = OptionalString(args, "op")?.Trim().ToLowerInvariant(), DryRun = OptionalBool(args, "dryRun") ?? false },
            "field_focus" => ParseFieldFocus(args, action),
            "field_blur" or "field_submit" => new Request { Action = action },
            "type" => ParseType(args, action),
            "paste" => ParsePaste(args, action),
            "perf" => ParsePerf(args, action),
            "field_expand" => ParseRowLabel(args, action),
            // FIELDAPPLY1: an optional text is put in the field (as typing would) before the Apply button's own click.
            "field_apply" => ParseFieldApply(args, action),
            "field_scroll" => ParseFieldScroll(args, action),
            "field_cancel" => new Request { Action = action },
            _ when IsDateTimeAction(action) => new Request { Action = action, Label = OptionalString(args, "label")?.Trim(), Raw = args.Clone() },
            _ => throw new ArgumentException($"args.action must be {string.Join(", ", Actions)} (got '{action}')"),
        };
    }

    private static string RequiredAction(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("action", out var el)
            || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException("args.action is required: " + string.Join(", ", Actions));
        string? action = el.GetString();
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("args.action is required: " + string.Join(", ", Actions));
        action = action.Trim().ToLowerInvariant();
        foreach (var allowed in Actions)
            if (allowed == action) return action;
        throw new ArgumentException($"args.action must be {string.Join(", ", Actions)} (got '{action}')");
    }

    private static Request ParseOpen(JsonElement args, string action)
    {
        string? site = OptionalString(args, "site");
        if (string.IsNullOrWhiteSpace(site))
            throw new ArgumentException("args.site is required: main-menu, top-tab, shortcut, or standalone");
        site = site.Trim().ToLowerInvariant();
        if (site is not "main-menu" and not "top-tab" and not "shortcut" and not "standalone")
            throw new ArgumentException($"args.site must be main-menu, top-tab, shortcut, or standalone (got '{site}')");
        return new Request { Action = action, Site = site, NeedsNextDrain = true };
    }

    /// <summary>
    /// GAMEMENU1: <c>menu</c> with no <c>op</c> is the read-only FullscreenMenu probe it always was.
    /// <c>op</c> <c>status</c> reads the game menu state; <c>open</c> raises the game's full-screen
    /// menu, optionally on <c>tab</c> (a native tab, or <c>mods</c> through the shortcut route);
    /// <c>close</c> returns to gameplay the way Escape does. <c>tab</c> is only for <c>open</c>.
    /// </summary>
    private static Request ParseMenu(JsonElement args, string action)
    {
        string? op = OptionalString(args, "op");
        string? tab = OptionalString(args, "tab");
        if (op == null)
        {
            if (tab != null) throw new ArgumentException(GameMenuPlan.TabNeedsOpen);
            return new Request { Action = action };
        }
        op = op.Trim().ToLowerInvariant();
        if (op is not (GameMenuPlan.OpStatus or GameMenuPlan.OpOpen or GameMenuPlan.OpClose))
            throw new ArgumentException(GameMenuPlan.OpUsage + " (got '" + op + "')");
        string? canonical = null;
        if (tab != null)
        {
            if (op != GameMenuPlan.OpOpen) throw new ArgumentException(GameMenuPlan.TabNeedsOpen);
            if (!GameMenuPlan.TryParseTab(tab, out canonical, out _))
                throw new ArgumentException(GameMenuPlan.TabUsage + " (got '" + tab + "')");
        }
        return new Request { Action = action, Op = op, Tab = canonical };
    }

    private static Request ParseStatus(JsonElement args, string action) =>
        new() { Action = action, Fonts = OptionalBool(args, "fonts") == true };

    private static Request ParseSelect(JsonElement args, string action)
    {
        string? page = OptionalString(args, "page");
        if (string.IsNullOrWhiteSpace(page))
            throw new ArgumentException("args.page is required: overview, settings, or custom");
        page = page.Trim().ToLowerInvariant();
        if (page is not "overview" and not "settings" and not "custom")
            throw new ArgumentException($"args.page must be overview, settings, or custom (got '{page}')");
        return new Request
        {
            Action = action,
            Plugin = OptionalString(args, "plugin"),
            Page = page,
            Group = OptionalString(args, "group"),
        };
    }

    private static Request ParseSelectTab(JsonElement args, string action)
    {
        string? tab = OptionalString(args, "tab");
        if (!ModsMenu.TryParseWorkspaceTab(tab, out var name))
            throw new ArgumentException("args.tab is required: mods, install, server, keybinds, or preferences (configuration is an alias)");
        return new Request { Action = action, Tab = name, NeedsNextDrain = true };
    }

    /// <summary>Three homes: <c>section {group, tab?}</c> — the left-list click (by caption or slug) on the tab shown or <c>tab</c>.</summary>
    private static Request ParseSection(JsonElement args, string action)
    {
        string? group = OptionalString(args, "group");
        if (string.IsNullOrWhiteSpace(group))
            throw new ArgumentException("args.group is required: a left-list group of the tab, by caption or slug (Sources and downloads, server-rules)");
        string? tab = OptionalString(args, "tab");
        if (tab != null && !ModsMenu.TryParseWorkspaceTab(tab, out _))
            throw new ArgumentException("args.tab must be mods, server or preferences (got '" + tab + "')");
        return new Request { Action = action, Group = group.Trim(), Tab = tab, NeedsNextDrain = true };
    }

    /// <summary>Three homes: <c>drawer {group, open?}</c> — toggle a drawer of the tab shown, or set it open or closed.</summary>
    private static Request ParseDrawer(JsonElement args, string action)
    {
        string? group = OptionalString(args, "group");
        if (string.IsNullOrWhiteSpace(group))
            throw new ArgumentException("args.group is required: a drawer of the tab shown, by caption or slug (Operator, sources-and-downloads)");
        return new Request { Action = action, Group = group.Trim(), Open = OptionalBool(args, "open"), NeedsNextDrain = true };
    }

    /// <summary>
    /// KEYS1: <c>keys {op, target?, chord?}</c> drives the Keybinds tab (docs/mods-menu.md#keybinds-tab).
    /// op: read, jump, filter, search, edit, capture, save, unbind, cancel, reset, clear, swap, hover, select, press-key, press-key2, reset-all,
    /// open-page, open-keybinds.
    /// </summary>
    private static Request ParseKeys(JsonElement args, string action)
    {
        string op = (OptionalString(args, "op") ?? "read").Trim().ToLowerInvariant();
        bool changes = op is not ("read" or "filter" or "search" or "edit" or "capture" or "cancel" or "conflict" or "conflict-read");
        return new Request
        {
            Action = action,
            Op = op,
            Target = OptionalString(args, "target"),
            Chord = OptionalString(args, "chord"),
            NeedsNextDrain = changes,
        };
    }

    private static Request ParseSearch(JsonElement args, string action)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("text", out var el))
            throw new ArgumentException("args.text is required (empty string clears)");
        string text = el.ValueKind == JsonValueKind.Null ? "" : el.GetString() ?? "";
        return new Request { Action = action, Text = text };
    }

    private static Request ParseScroll(JsonElement args, string action)
    {
        string pane = RequiredPane(args);
        if (!args.TryGetProperty("to", out var el))
            throw new ArgumentException("args.to is required: top, bottom, or a number");
        string? to;
        double? number = null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n))
        {
            to = "number";
            number = n;
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            to = (el.GetString() ?? "").Trim().ToLowerInvariant();
            if (to is "top" or "bottom") { /* ok */ }
            else if (double.TryParse(to, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            {
                to = "number";
                number = n;
            }
            else throw new ArgumentException($"args.to must be top, bottom, or a number (got '{el.GetString()}')");
        }
        else throw new ArgumentException("args.to is required: top, bottom, or a number");
        return new Request { Action = action, Pane = pane, To = to, ToNumber = number };
    }

    private static Request ParseWidgetRead(JsonElement args, string action)
    {
        string? pane = OptionalString(args, "pane");
        if (!string.IsNullOrWhiteSpace(pane)) pane = RequiredPane(args);
        else pane = null;
        return new Request { Action = action, Pane = pane };
    }

    private static Request ParseWidgetClick(JsonElement args, string action)
    {
        string? pane = OptionalString(args, "pane");
        if (!string.IsNullOrWhiteSpace(pane)) pane = RequiredPane(args);
        else pane = null;
        string? label = OptionalString(args, "label");
        int? index = OptionalInt(args, "index");
        if (string.IsNullOrWhiteSpace(label) && index == null)
            throw new ArgumentException("args.label or args.index is required");
        return new Request
        {
            Action = action,
            Pane = pane,
            Label = label,
            Index = index,
            Value = OptionalString(args, "value"),
            Modifiers = UiInputPlan.ParseModifiers(args),
        };
    }

    /// <summary>RADIO1: <c>radio_choose {label, value, pane?}</c>: the radio list's caption and an option's value or label.</summary>
    private static Request ParseRadioChoose(JsonElement args, string action)
    {
        string? pane = OptionalString(args, "pane");
        if (!string.IsNullOrWhiteSpace(pane)) pane = RequiredPane(args);
        else pane = null;
        string? label = OptionalString(args, "label");
        string? value = OptionalString(args, "value");
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the radio list's caption) is required");
        if (value == null) throw new ArgumentException("args.value (an option's value or label) is required");
        return new Request { Action = action, Pane = pane, Label = label, Value = value };
    }

    /// <summary>
    /// OPTIONHOVER1: <c>hover {label, option, pane?}</c> puts the pointer over an option (its value or label) of the dropdown or
    /// radio list captioned <c>label</c>, opening a closed dropdown's list first; <c>hover {label, leave: true}</c> moves it off
    /// the options.
    /// </summary>
    private static Request ParseHover(JsonElement args, string action)
    {
        string? pane = OptionalString(args, "pane");
        if (!string.IsNullOrWhiteSpace(pane)) pane = RequiredPane(args);
        else pane = null;
        string? label = OptionalString(args, "label");
        string? option = OptionalString(args, "option");
        string? cell = OptionalString(args, "cell");
        bool leave = OptionalBool(args, "leave") ?? false;
        if (!string.IsNullOrWhiteSpace(cell))
        {
            // C5: a hover cell of a cell row (label: the row's key).
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the cell row's key) is required");
            return new Request { Action = action, Pane = pane, Label = label, Cell = cell, Leave = leave };
        }

        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the dropdown's or radio list's caption) is required");
        if (!leave && option == null) throw new ArgumentException("args.option (an option's value or label) is required, or args.leave: true");
        return new Request { Action = action, Pane = pane, Label = label, Option = option, Leave = leave };
    }

    /// <summary>DROPDOWN1: <c>dropdown_choose {label, value, pane?}</c>: the dropdown's caption and an option's value or caption.</summary>
    private static Request ParseDropdownChoose(JsonElement args, string action)
    {
        string? pane = OptionalString(args, "pane");
        if (!string.IsNullOrWhiteSpace(pane)) pane = RequiredPane(args);
        else pane = null;
        string? label = OptionalString(args, "label");
        string? value = OptionalString(args, "value");
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("args.label (the dropdown's caption) is required");
        if (value == null) throw new ArgumentException("args.value (an option's value or caption) is required");
        return new Request { Action = action, Pane = pane, Label = label, Value = value };
    }

    /// <summary>
    /// DIALOGS3: <c>dialog_choose {choice}</c>. <c>choice</c> is a string (a button's caption such as <c>OK</c>,
    /// <c>yes</c> / <c>no</c> / <c>cancel</c>, <c>"2"</c>) or a JSON number (an index).
    /// </summary>
    private static Request ParseDialogChoose(JsonElement args, string action)
    {
        string? choice = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("choice", out var raw))
        {
            if (raw.ValueKind == JsonValueKind.String) choice = raw.GetString();
            else if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out int n))
                choice = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // A button's caption (OK, Restore, ...) is a choice too: the open dialog decides what matches, and its error
        // lists the choices it has. Only a negative index can be refused here.
        choice = choice?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(choice))
            throw new ArgumentException("args.choice is required (a button's caption, yes, no, cancel or an index)");
        if (choice.StartsWith("-", StringComparison.Ordinal)
            && int.TryParse(choice, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("args.choice index must be 0 or more, not " + choice);
        return new Request { Action = action, Choice = choice };
    }

    private static Request ParseNativeTab(JsonElement args, string action)
    {
        bool same = OptionalBool(args, "same") ?? false;
        string? tab = OptionalString(args, "tab");
        int? tabIndex = null;
        if (!string.IsNullOrWhiteSpace(tab) && int.TryParse(tab.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            tabIndex = n;
        if (!same && string.IsNullOrWhiteSpace(tab) && tabIndex == null)
            throw new ArgumentException("args.tab (name or index) is required when args.same is not true");
        return new Request
        {
            Action = action,
            Tab = tab,
            TabIndex = tabIndex,
            Same = same,
            NeedsNextDrain = true,
        };
    }

    /// <summary>
    /// RADIOCLICK1 <c>click_at {x, y}</c> (client pixels, as <c>hit</c>) or <c>click_at {row}</c> (a visible cell row's
    /// key; the click lands on the row's center).
    /// </summary>
    internal static Request ParseClickAt(JsonElement args, string action)
    {
        string? row = OptionalString(args, "row")?.Trim();
        if (!string.IsNullOrEmpty(row)) return new Request { Action = action, Label = row };
        bool hasX = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("x", out _);
        bool hasY = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("y", out _);
        if (!hasX || !hasY) throw new ArgumentException("args.row (a cell row key), or args.x and args.y (client pixels), is required");
        return new Request { Action = action, X = RequiredNumber(args, "x"), Y = RequiredNumber(args, "y") };
    }

    private static Request ParseHit(JsonElement args, string action)
    {
        float x = RequiredNumber(args, "x");
        float y = RequiredNumber(args, "y");
        return new Request { Action = action, X = x, Y = y };
    }

    private static Request ParseResolution(JsonElement args, string action)
    {
        int width = RequiredPositiveInt(args, "width");
        int height = RequiredPositiveInt(args, "height");
        string? mode = OptionalString(args, "mode");
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("args.mode is required: window, windowed, fullscreenwindow, or exclusive");
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not "window" and not "windowed" and not "fullscreenwindow" and not "exclusive")
            throw new ArgumentException($"args.mode must be window, windowed, fullscreenwindow, or exclusive (got '{mode}')");
        return new Request
        {
            Action = action,
            Width = width,
            Height = height,
            Mode = mode,
            NeedsNextDrain = true,
        };
    }

    internal static string RequiredPane(JsonElement args)
    {
        string? pane = OptionalString(args, "pane");
        if (string.IsNullOrWhiteSpace(pane))
            throw new ArgumentException("args.pane is required: list or content");
        pane = pane.Trim().ToLowerInvariant();
        if (pane is not "list" and not "content")
            throw new ArgumentException($"args.pane must be list or content (got '{pane}')");
        return pane;
    }

    private static string? OptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
        return el.ToString();
    }

    private static int? OptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n;
        return null;
    }

    private static bool? OptionalBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String
            && bool.TryParse(el.GetString(), out var b))
            return b;
        return null;
    }

    private static float RequiredNumber(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            throw new ArgumentException($"args.{name} is required");
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n))
            return (float)n;
        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            return (float)n;
        throw new ArgumentException($"args.{name} is required");
    }

    private static int RequiredPositiveInt(JsonElement args, string name)
    {
        int? n = OptionalInt(args, name);
        if (n == null)
            throw new ArgumentException($"args.{name} is required");
        if (n.Value <= 0)
            throw new ArgumentException($"args.{name} must be a positive integer (got {n.Value})");
        return n.Value;
    }
}

/// <summary>Envelope and status dictionaries the bridge serializes (camelCase).</summary>
/// <summary>
/// GAMEMENU1 (user, 2026-10-03: the bridge could not close the game's own menu — "seems like an
/// odd gap. Fix it?"): the game-free plan for <c>mods_ui menu {op: status|open|close, tab?}</c>.
/// The live side uses the game's own paths: <c>HUDMenuManager.GoToMenu(FullscreenMenu)</c> to
/// raise the menu and <c>GoToMenu(None)</c> to dismiss it (the call this framework's Escape
/// handling already makes), the native outer tab's own click handler to pick a tab, and the Mods
/// shortcut route (the F10 path) for <c>mods</c>. No OS input is synthesized. The reply is
/// verified: the op waits until the state it asked for has held for <see cref="StableFrames"/>
/// frames, or reports what it saw after <see cref="VerifyFrames"/>.
/// </summary>
internal static class GameMenuPlan
{
    internal const string OpStatus = "status";
    internal const string OpOpen = "open";
    internal const string OpClose = "close";
    internal const string OpUsage = "args.op must be status, open or close (omit it for the read-only menu probe)";
    internal const string TabNeedsOpen = "args.tab is only for op open";
    internal const string TabUsage = "args.tab must be emotes, vampire-powers, spellbook, map, vblood, social, system or mods";

    /// <summary>The close reason logged when this op closes the workspace on its way out.</summary>
    internal const string ReasonClose = "bridge:menu-close";

    /// <summary>The reason a routed MODS open carries (the shortcut route logs it).</summary>
    internal const string ReasonOpen = "bridge:menu-open";

    /// <summary>The owned MODS outer tab's state (the eighth slot after the game's seven).</summary>
    internal const int ModsState = 7;

    /// <summary>Most frames an open or close waits for its state before replying with what it saw.</summary>
    internal const int VerifyFrames = 180;

    /// <summary>
    /// Frames the reached state must hold before the reply. Longer than the frames in which a
    /// freshly raised menu still picks its remembered tab (F10SETTLE saw it on frame 0-2).
    /// </summary>
    internal const int StableFrames = 8;

    /// <summary>Frames after the menu's tab bar is ready (or after a click) before a tab is (re)clicked.</summary>
    internal const int SelectDelayFrames = 6;

    /// <summary>Most clicks one open makes on the requested native tab.</summary>
    internal const int MaxSelectAttempts = 3;

    internal const string WhyTitle = "title-screen: there is no game menu outside a world";
    internal const string WhyNoMenu = "no-menu: the game menu is not up";
    internal const string WhyAlready = "already: the game menu is already in that state";
    internal const string WhyWorkspaceElsewhere = "workspace-open-outside-menu: the Mods window is open without the game menu; close it first";

    private static readonly (string Name, int State, string Enum, string[] Aliases)[] Tabs =
    {
        ("emotes", 0, "Emotes", new[] { "emote" }),
        ("vampire-powers", 1, "VampirePowers", new[] { "vampirepowers", "powers" }),
        ("spellbook", 2, "SpellBook", new[] { "spell-book", "spells" }),
        ("map", 3, "Map", Array.Empty<string>()),
        ("vblood", 4, "VBlood", new[] { "v-blood" }),
        ("social", 5, "Social", Array.Empty<string>()),
        ("system", 6, "System", Array.Empty<string>()),
        ("mods", ModsState, "Mods", Array.Empty<string>()),
    };

    /// <summary>Parse a tab name (case, spaces, dashes and underscores ignored) to its canonical name and state.</summary>
    internal static bool TryParseTab(string? text, out string name, out int state)
    {
        name = "";
        state = -1;
        string key = Squash(text);
        if (key.Length == 0) return false;
        foreach (var tab in Tabs)
        {
            bool hit = Squash(tab.Name) == key || Squash(tab.Enum) == key;
            foreach (var alias in tab.Aliases) hit |= Squash(alias) == key;
            if (!hit) continue;
            name = tab.Name;
            state = tab.State;
            return true;
        }
        return false;
    }

    /// <summary>The canonical name of a menu state, or null when unknown.</summary>
    internal static string? TabName(int? state)
    {
        if (state == null) return null;
        foreach (var tab in Tabs) if (tab.State == state.Value) return tab.Name;
        return null;
    }

    /// <summary>The game's <c>FullscreenMenuState</c> name of a native tab (the outer tab entry's <c>State</c>).</summary>
    internal static string? NativeEnumName(int state)
    {
        foreach (var tab in Tabs) if (tab.State == state && tab.State != ModsState) return tab.Enum;
        return null;
    }

    private static string Squash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = new List<char>(text!.Length);
        foreach (char c in text) if (c != '-' && c != '_' && c != ' ') chars.Add(char.ToLowerInvariant(c));
        return new string(chars.ToArray());
    }

    /// <summary>One read of the game menu.</summary>
    internal readonly struct State
    {
        internal State(bool inWorld, string hudMenu, bool menuUp, int? tab, bool workspaceOpen, bool menuReady = true)
        {
            InWorld = inWorld;
            HudMenu = hudMenu ?? "";
            MenuUp = menuUp;
            Tab = menuUp ? tab : null;
            WorkspaceOpen = workspaceOpen;
            MenuReady = menuUp && menuReady;
        }

        internal bool InWorld { get; }
        internal string HudMenu { get; }
        internal bool MenuUp { get; }
        internal int? Tab { get; }
        internal bool WorkspaceOpen { get; }
        internal bool MenuReady { get; }

        internal Dictionary<string, object?> ToReply() => new(StringComparer.Ordinal)
        {
            ["inWorld"] = InWorld,
            ["hudMenu"] = HudMenu,
            ["menuOpen"] = MenuUp,
            ["tab"] = TabName(Tab),
            ["tabState"] = Tab,
            ["workspaceOpen"] = WorkspaceOpen,
        };
    }

    internal enum Step
    {
        /// <summary>No world: there is no game menu (title screen).</summary>
        NoWorld = 0,
        /// <summary>Nothing to do; the reply says why.</summary>
        NoOp = 1,
        /// <summary><c>GoToMenu(None)</c>.</summary>
        Dismiss = 2,
        /// <summary>Close the workspace with <see cref="ReasonClose"/>, then <c>GoToMenu(None)</c>.</summary>
        CloseWorkspaceThenDismiss = 3,
        /// <summary><c>GoToMenu(FullscreenMenu)</c> and take whatever tab the game picks.</summary>
        Raise = 4,
        /// <summary><c>GoToMenu(FullscreenMenu)</c>, wait for the tab bar, then click the native tab.</summary>
        RaiseThenSelect = 5,
        /// <summary>The menu is up: click the native tab.</summary>
        Select = 6,
        /// <summary>The Mods shortcut route (the F10 path): raises the menu when needed and opens on MODS.</summary>
        OpenMods = 7,
        /// <summary>Refused: the reply says why.</summary>
        Refuse = 8,
    }

    internal static (Step Step, string Why) DecideClose(State now)
    {
        if (!now.InWorld) return (Step.NoOp, WhyTitle);
        if (!now.MenuUp) return (Step.NoOp, WhyNoMenu);
        return now.WorkspaceOpen ? (Step.CloseWorkspaceThenDismiss, "") : (Step.Dismiss, "");
    }

    internal static (Step Step, string Why) DecideOpen(State now, int? want)
    {
        if (!now.InWorld) return (Step.NoWorld, WhyTitle);
        if (want == null) return now.MenuUp ? (Step.NoOp, WhyAlready) : (Step.Raise, "");
        if (want.Value == ModsState)
        {
            if (now.WorkspaceOpen && now.MenuUp && now.Tab == ModsState) return (Step.NoOp, WhyAlready);
            if (now.WorkspaceOpen) return (Step.Refuse, WhyWorkspaceElsewhere);
            return (Step.OpenMods, "");
        }
        if (now.MenuUp && now.Tab == want && !now.WorkspaceOpen) return (Step.NoOp, WhyAlready);
        return now.MenuUp ? (Step.Select, "") : (Step.RaiseThenSelect, "");
    }

    /// <summary>The state an open or close is waiting for.</summary>
    internal static bool Reached(string op, int? want, State now)
    {
        if (op == OpClose) return !now.MenuUp && !now.WorkspaceOpen;
        if (!now.MenuUp) return false;
        if (want == null) return now.MenuReady;
        if (want.Value == ModsState) return now.Tab == ModsState && now.WorkspaceOpen;
        return now.Tab == want && !now.WorkspaceOpen;
    }

    /// <summary>
    /// Whether this frame clicks the requested native tab: the tab bar is ready, the menu is not on
    /// it, the attempts are not used up, and <see cref="SelectDelayFrames"/> frames have passed since
    /// the bar became ready or since the last click (so the menu's own pick lands first).
    /// </summary>
    internal static bool ShouldClick(int? want, State now, int attempts, int framesSinceReadyOrClick) =>
        want != null && want.Value != ModsState && now.MenuReady && now.Tab != want
        && attempts < MaxSelectAttempts && framesSinceReadyOrClick >= SelectDelayFrames;
}

internal static class ModsUiResult
{
    /// <summary>
    /// The readback's serializer. <b>POLISH1 defect F: the encoder is named on purpose.</b>
    /// <see cref="JavaScriptEncoder.Default"/> escapes every non-ASCII character as <c>\uXXXX</c>, so
    /// every string this op puts on the bridge — the row captions, the KIT1 <c>Describe()</c> parts
    /// (<c>glyph</c>, <c>marker</c>, <c>tooltip</c>) and the merged page-row dictionaries — is pure
    /// ASCII on the wire and no reader's encoding choice can turn <c>»</c> into <c>Â»</c>. Switching
    /// this to <c>UnsafeRelaxedJsonEscaping</c> (or to any encoder that passes non-ASCII through) would
    /// put raw UTF-8 bytes on the wire and hand that decision back to whatever reads them;
    /// <c>ReadbackTests</c> pins it.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
    };

    internal static Dictionary<string, object?> ServerList(string state, int? count = null)
    {
        var list = new Dictionary<string, object?> { ["state"] = state };
        if (string.Equals(state, "available", StringComparison.Ordinal))
            list["count"] = count ?? 0;
        return list;
    }

    internal static Dictionary<string, object?> Status(
        bool open,
        string site,
        string? plugin,
        string? page,
        string pageId,
        string search,
        object entries,
        bool curtain,
        bool capture,
        int width,
        int height,
        string mode,
        int panes,
        int observers,
        double statusMs = 0,
        bool? modsCapture = null,
        object? serverList = null,
        int serverListEpoch = 0,
        bool suspended = false,
        string? suspendReason = null,
        string? shortcut = null,
        int visibleRows = 0,
        int totalRows = 0,
        object? listScroll = null,
        object? contentScroll = null,
        string? tab = null,
        object? fontDonor = null,
        object? fontAssets = null,
        bool? curtainRaycast = null,
        object? mods = null,
        object? dialog = null,
        string? footer = null)
    {
        var status = new Dictionary<string, object?>
        {
            ["open"] = open,
            ["tab"] = tab,
            ["site"] = site,
            ["plugin"] = plugin,
            ["page"] = page,
            ["pageId"] = pageId,
            ["search"] = search,
            ["entries"] = entries,
            // visibleRows is the rendered subset (rows intersecting the list viewport),
            // not the filtered catalog count. totalRows is the catalog size.
            ["visibleRows"] = visibleRows,
            ["totalRows"] = totalRows,
            ["listScroll"] = listScroll ?? ListScroll(1f, false),
            ["contentScroll"] = contentScroll ?? ListScroll(1f, false),
            ["curtain"] = new Dictionary<string, object?>
            {
                ["present"] = curtain,
                ["raycastTarget"] = curtainRaycast ?? curtain,
            },
            ["modsCapture"] = modsCapture ?? open,
            ["inputCapture"] = capture,
            ["capture"] = capture,
            ["width"] = width,
            ["height"] = height,
            ["mode"] = mode,
            ["panes"] = panes,
            ["observers"] = observers,
            ["serverList"] = serverList ?? ServerList("unknown"),
            ["serverListEpoch"] = serverListEpoch,
            ["statusMs"] = Math.Round(statusMs, 3),
            ["suspended"] = suspended,
            ["suspendReason"] = suspendReason,
            ["shortcut"] = shortcut,
            ["fontDonor"] = fontDonor,
            // MODS1/MODS2: the Mods tab's decided sidebar — the default list, the server-only
            // group, and the required group (the loader plus Progenitor's own plugins).
            ["mods"] = mods ?? ModsCounts(0, 0, 0),
            // INSTALLUX2 item 1: the one modal confirmation dialog — {open, key, kind}. A
            // workspace that reported nothing (an older Core, or the catch path) reads as closed.
            ["dialog"] = dialog ?? DialogClosed(),
            // The Mods workspace's version line (`Progenitor <core> · Bootstrap <b>[ · Dev <d>]`),
            // as the main pane draws it; empty while the workspace is closed or on an older Core.
            ["footer"] = footer ?? "",
        };
        if (fontAssets != null)
            status["fontAssets"] = fontAssets;
        return status;
    }

    /// <summary>INSTALLUX2: <c>status.dialog</c> when nothing is open.</summary>
    internal static Dictionary<string, object?> DialogClosed() =>
        new()
        {
            ["open"] = false,
            ["key"] = "",
            ["kind"] = "none",
            ["footer"] = "",
            ["title"] = "",
            ["error"] = "",
            ["queued"] = 0,
        };

    /// <summary>MODS2 <c>status.mods</c>: <c>{shown, required, server}</c>. MODS1's
    /// <c>hiddenDeveloper</c>/<c>showDeveloper</c> pair went with the developer toggle.</summary>
    internal static Dictionary<string, object?> ModsCounts(int shown, int required, int server) =>
        new()
        {
            ["shown"] = shown,
            ["required"] = required,
            ["server"] = server,
        };

    /// <summary>
    /// List pane scroll readback: Unity <c>ScrollRect.verticalNormalizedPosition</c>
    /// (1 = top, 0 = bottom) and whether the content overflows the viewport.
    /// </summary>
    internal static Dictionary<string, object?> ListScroll(double position, bool overflow)
        => new()
        {
            ["position"] = position,
            ["overflow"] = overflow,
        };

    internal static Dictionary<string, object?> FontDonor(string font, string path, string? source = null)
        => new()
        {
            ["font"] = font ?? "",
            ["path"] = path ?? "",
            ["source"] = source ?? "",
        };

    internal static void AttachTmp(Dictionary<string, object?> row, object? tmp)
    {
        if (row == null || tmp == null) return;
        row["tmp"] = tmp;
    }

    internal static object? ReadTmpElement(JsonElement row)
    {
        if (!row.TryGetProperty("tmp", out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText());
    }

    /// <summary>
    /// The Mods opener chord as <see cref="KeyChord"/> formats it (<c>F10</c>,
    /// <c>F10 + Ctrl</c>), or <c>null</c> when unbound / missing.
    /// </summary>
    internal static string? FormatShortcut(KeyChord chord) =>
        chord.IsBound ? chord.ToString() : null;

    /// <summary>
    /// The configured Mods opener from a keybind snapshot. Never throws: a
    /// missing, unbound, or unreadable bind is <c>null</c>.
    /// </summary>
    internal static string? ShortcutFrom(IReadOnlyList<IKeybindInfo>? binds)
    {
        if (binds == null) return null;
        try
        {
            int count = binds.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var bind = binds[i];
                    if (bind == null) continue;
                    if (!string.Equals(bind.PluginId, SettingsPanel.PluginId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(bind.Id, SettingsPanel.OpenKeybindId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return FormatShortcut(bind.Chord);
                }
                catch { /* one unreadable bind must not fail the status */ }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    internal static string? ReadConfiguredShortcut()
    {
        try { return ShortcutFrom(Keybinds.Snapshot()); }
        catch { return null; }
    }

    internal static Dictionary<string, object?> Envelope(
        bool ok,
        string action,
        object? before,
        object? after,
        object? detail,
        double elapsedMs)
        => new()
        {
            ["ok"] = ok,
            ["action"] = action,
            ["before"] = before,
            ["after"] = after,
            ["detail"] = detail ?? new Dictionary<string, object?>(),
            ["elapsedMs"] = Math.Round(elapsedMs, 3),
        };

    internal static Dictionary<string, object?> EmptyStatus(double statusMs = 0)
        => Status(false, "none", null, null, "", "", Array.Empty<object>(), false, false, 0, 0, "window", 0, 0, statusMs);

    /// <summary>
    /// Job NATIVE1. <c>route</c> is <c>native</c> while an owned eighth outer tab is registered
    /// with the game's <c>FullscreenMenuMapper</c>, <c>imitation</c> otherwise; <c>nativeRoute</c>
    /// is <c>{state, viewsIndex, listIndex, reason, callback, selected, mounted, adapter, attempts,
    /// pruned, staleReason}</c> — job NATIVE4 added <c>pruned</c>, the number of stale owned state-7
    /// entries the current attach removed, and <c>staleReason</c>, how they were recognized.
    /// Same shape on <c>status</c> and <c>rects.detail</c>. Missing or malformed input reads as
    /// the imitation route with the reason preserved, never as a throw on a status read.
    /// </summary>
    /// <summary>
    /// Job KEY2. <c>status.focus</c> — <c>{field, hotkeysBlocked, blockedActions[]}</c>: which
    /// Progenitor search field has owned focus and whether the fullscreen menu's letter shortcuts
    /// are being taken from it this frame. Added to the status on <b>both</b> exits of
    /// <c>ReadStatus</c>, the good one and the caught-throw one, so a client where the read fell
    /// over reports a focus row that says <c>none</c> rather than no row at all — a missing key
    /// and "nothing is focused" are different answers and only one of them is true after a throw,
    /// but a caller reading <c>status.focus.field</c> must not have to handle both shapes.
    /// Never throws: a status read is a diagnostic and must not fail for its own diagnostics.
    /// </summary>
    /// <summary>
    /// HOSTS1 round 7: the workspace hosts readback, copied whole from Core
    /// (<c>ModsMenu.DescribeHostsStatus</c>) as <c>status.hosts</c>, with <c>sidebarSource</c> and
    /// <c>sidebarParked</c> also at the top level. Later Core fields reach the status without a Dev change.
    /// </summary>
    internal static Dictionary<string, object?> WithHosts(Dictionary<string, object?> target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        try
        {
            var hosts = ModsMenu.DescribeHostsStatus();
            target["hosts"] = hosts;
            if (hosts.TryGetValue("sidebarSource", out var source)) target["sidebarSource"] = source;
            if (hosts.TryGetValue("sidebarParked", out var parked)) target["sidebarParked"] = parked;
        }
        catch (Exception e) { target["hosts"] = new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
        return target;
    }

    internal static Dictionary<string, object?> WithFocus(Dictionary<string, object?> target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        try { target["focus"] = ModsMenu.DescribeFocusStatus(); }
        catch
        {
            target["focus"] = new Dictionary<string, object?>
            {
                ["field"] = "none",
                ["hotkeysBlocked"] = false,
                ["blockedActions"] = Array.Empty<object>(),

                // Job KEY3: the guard row is on this exit too, for the reason the summary gives —
                // a caller reading status.focus.guard.registered must not have to tell a missing
                // key from a false one. registered:false is the honest answer after a throw; the
                // order is a compile-time constant and is stated rather than omitted, so the shape
                // is identical to the good exit's.
                ["guard"] = new Dictionary<string, object?>
                {
                    ["registered"] = false,
                    ["order"] = ModsMenu.MenuHotkeyGuardOrder,
                    ["masked"] = 0,
                },
            };
        }
        return target;
    }

    internal static Dictionary<string, object?> WithRoute(
        Dictionary<string, object?> target, string? route, Dictionary<string, object?>? nativeRoute)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        string name = string.Equals(route, "native", StringComparison.Ordinal) ? "native" : "imitation";
        target["route"] = name;
        target["nativeRoute"] = nativeRoute ?? new Dictionary<string, object?>
        {
            ["state"] = 7,
            ["viewsIndex"] = -1,
            ["listIndex"] = -1,
            ["reason"] = "unavailable",
            ["callback"] = "none",
            ["selected"] = false,
            ["mounted"] = false,
            ["adapter"] = 0,
            ["attempts"] = 0,
            ["pruned"] = 0,
            ["staleReason"] = "",
            // NATIVE5: whether a further native attach attempt is due, and when.
            ["retryable"] = false,
            ["nextAttemptOn"] = "none",
        };
        return target;
    }

    /// <summary>
    /// OS client-area readback for <c>mods_ui window</c> and the next-Drain
    /// <c>resolution</c> payload. Coordinates match observe.ps1: client origin is
    /// top-left of the drawable area; <c>screenX</c>/<c>screenY</c> are
    /// <c>ClientToScreen</c> of that origin.
    /// </summary>
    internal static Dictionary<string, object?> Window(
        string hwnd,
        int clientWidth,
        int clientHeight,
        int screenX,
        int screenY,
        int screenWidth,
        int screenHeight,
        string fullScreenMode,
        string mode,
        int primaryWidth,
        int primaryHeight)
        => new()
        {
            ["hwnd"] = hwnd,
            ["client"] = new Dictionary<string, object?>
            {
                ["x"] = 0,
                ["y"] = 0,
                ["width"] = clientWidth,
                ["height"] = clientHeight,
                ["screenX"] = screenX,
                ["screenY"] = screenY,
            },
            ["screen"] = new Dictionary<string, object?>
            {
                ["width"] = screenWidth,
                ["height"] = screenHeight,
            },
            ["fullScreenMode"] = fullScreenMode,
            ["mode"] = mode,
            ["primaryDisplay"] = new Dictionary<string, object?>
            {
                ["width"] = primaryWidth,
                ["height"] = primaryHeight,
            },
        };

    internal static Dictionary<string, object?> EmptyWindow()
        => Window("0x0", 0, 0, 0, 0, 0, 0, "Unknown", "window", 0, 0);
}

/// <summary>
/// Game-free conversion from Unity screen space (origin bottom-left, Y up) to
/// Windows client pixels (origin top-left, Y down) — the space observe.ps1
/// <c>click</c>/<c>move</c> take relative to the client area. Canvas scale maps
/// canvas-local units onto Unity screen pixels before the Y-flip.
/// </summary>
internal static class ModsUiGeometry
{
    internal const string ClientOrigin = "client-top-left";
    internal const string UnityOrigin = "screen-bottom-left";
    internal const string OccludedByCurtain = "curtain";

    internal static float EffectiveScale(float canvasScale) => canvasScale > 0f ? canvasScale : 1f;

    /// <summary>Canvas-local units to Unity screen pixels (bottom-left origin).</summary>
    internal static void ScaleCanvasLocal(
        float x, float y, float width, float height, float canvasScale,
        out float unityX, out float unityY, out float unityWidth, out float unityHeight)
    {
        float scale = EffectiveScale(canvasScale);
        unityX = x * scale;
        unityY = y * scale;
        unityWidth = width * scale;
        unityHeight = height * scale;
    }

    /// <summary>
    /// Unity screen pixels (origin bottom-left) to Windows client pixels (origin top-left).
    /// <paramref name="screenHeight"/> is the client/Unity screen height in pixels.
    /// </summary>
    internal static void UnityScreenToClient(
        float unityX, float unityY, float unityWidth, float unityHeight, int screenHeight,
        out int x, out int y, out int width, out int height)
    {
        width = (int)Math.Round(unityWidth);
        height = (int)Math.Round(unityHeight);
        x = (int)Math.Round(unityX);
        y = (int)Math.Round(screenHeight - unityY - unityHeight);
    }

    internal static Dictionary<string, object?> UnityRect(float x, float y, float width, float height)
        => new()
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
        };

    /// <summary>
    /// One owned overlay this op can enumerate: the HUD curtain.
    /// <see cref="SiblingOrder"/> is the overlay canvas's sibling index; it breaks ties
    /// when two overlays share a <see cref="SortingOrder"/>.
    /// </summary>
    internal readonly struct OverlayLayer
    {
        internal bool Present { get; init; }
        internal string Kind { get; init; }
        internal int X { get; init; }
        internal int Y { get; init; }
        internal int Width { get; init; }
        internal int Height { get; init; }
        internal int SortingOrder { get; init; }
        internal int SiblingOrder { get; init; }

        internal static OverlayLayer None => default;

        internal static OverlayLayer Of(
            string kind, int x, int y, int width, int height, int sortingOrder, int siblingOrder = 0)
            => new()
            {
                Present = true,
                Kind = kind,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                SortingOrder = sortingOrder,
                SiblingOrder = siblingOrder,
            };

        internal bool Covers(int x, int y, int width, int height)
            => Present && Overlaps(x, y, width, height, X, Y, Width, Height);

        /// <summary>
        /// True when this overlay is drawn above a control at
        /// <paramref name="sortingOrder"/> / <paramref name="siblingOrder"/>.
        /// Sorting order first; later sibling wins a tie.
        /// </summary>
        internal bool RendersAbove(int sortingOrder, int siblingOrder)
        {
            if (SortingOrder != sortingOrder) return SortingOrder > sortingOrder;
            return SiblingOrder > siblingOrder;
        }
    }

    /// <summary>
    /// Canvas-local rect plus canvas scale to the documented <c>rects</c> shape:
    /// client-area pixels, the Unity screen-space rect, and the scale used.
    /// </summary>
    internal static Dictionary<string, object?> FromCanvasLocal(
        float canvasX, float canvasY, float canvasWidth, float canvasHeight,
        int screenHeight, float canvasScale,
        bool visible = true, bool raycastTarget = true, string? occludedBy = null,
        int sortingOrder = 0, int? occluderOrder = null)
    {
        ScaleCanvasLocal(canvasX, canvasY, canvasWidth, canvasHeight, canvasScale,
            out float ux, out float uy, out float uw, out float uh);
        return FromUnityScreen(ux, uy, uw, uh, screenHeight, canvasScale, visible, raycastTarget,
            occludedBy, sortingOrder, occluderOrder);
    }

    internal static Dictionary<string, object?> FromUnityScreen(
        float unityX, float unityY, float unityWidth, float unityHeight,
        int screenHeight, float canvasScale,
        bool visible = true, bool raycastTarget = true, string? occludedBy = null,
        int sortingOrder = 0, int? occluderOrder = null)
    {
        UnityScreenToClient(unityX, unityY, unityWidth, unityHeight, screenHeight,
            out int x, out int y, out int width, out int height);
        bool interactable = IsInteractable(visible, raycastTarget, occludedBy);
        return new Dictionary<string, object?>
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["unity"] = UnityRect(unityX, unityY, unityWidth, unityHeight),
            ["canvasScale"] = EffectiveScale(canvasScale),
            ["sortingOrder"] = sortingOrder,
            ["visible"] = visible,
            ["interactable"] = interactable,
            ["occludedBy"] = occludedBy,
            ["occluderOrder"] = occludedBy == null ? null : occluderOrder,
        };
    }

    internal static Dictionary<string, object?> MissingRect(float canvasScale = 1f)
        => FromUnityScreen(0, 0, 0, 0, 0, canvasScale, visible: false, raycastTarget: false);

    internal static bool Overlaps(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh)
        => aw > 0 && ah > 0 && bw > 0 && bh > 0
        && ax < bx + bw && ax + aw > bx
        && ay < by + bh && ay + ah > by;

    /// <summary>
    /// Mark a measured rect as not visible when it does not intersect the list viewport.
    /// <c>activeInHierarchy</c> stays true for rows scrolled above the search field; this is
    /// the flag <c>mods_ui rects</c> must agree with <c>status.visibleRows</c> on.
    /// </summary>
    internal static void ClipToViewport(Dictionary<string, object?> rect, int vx, int vy, int vw, int vh)
    {
        if (rect == null) return;
        int x = ToInt(rect, "x");
        int y = ToInt(rect, "y");
        int w = ToInt(rect, "width");
        int h = ToInt(rect, "height");
        if (Overlaps(x, y, w, h, vx, vy, vw, vh)) return;
        rect["visible"] = false;
        string? occludedBy = rect.TryGetValue("occludedBy", out var occ) ? occ as string : null;
        rect["interactable"] = IsInteractable(false, false, occludedBy);
    }

    /// <summary>
    /// Overwrite <paramref name="rect"/> with its intersection against
    /// <paramref name="frame"/>. Used so <c>backdrop</c> reports the clipped
    /// visible pixels, not the pre-mask Image layout.
    /// </summary>
    internal static void ClipRectTo(Dictionary<string, object?> rect, Dictionary<string, object?> frame)
    {
        if (rect == null || frame == null) return;
        int x = ToInt(rect, "x");
        int y = ToInt(rect, "y");
        int w = ToInt(rect, "width");
        int h = ToInt(rect, "height");
        int fx = ToInt(frame, "x");
        int fy = ToInt(frame, "y");
        int fw = ToInt(frame, "width");
        int fh = ToInt(frame, "height");
        ModsMenu.WorkspaceClippedVisibleRect(x, y, w, h, fx, fy, fw, fh,
            out float visX, out float visY, out float visW, out float visH);
        rect["x"] = (int)Math.Round(visX);
        rect["y"] = (int)Math.Round(visY);
        rect["width"] = (int)Math.Round(visW);
        rect["height"] = (int)Math.Round(visH);
        if (rect.TryGetValue("unity", out var unity) && unity is Dictionary<string, object?> u)
        {
            u["x"] = visX;
            u["y"] = visY;
            u["width"] = visW;
            u["height"] = visH;
        }
    }

    private static int ToInt(Dictionary<string, object?> rect, string key)
    {
        if (!rect.TryGetValue(key, out var raw) || raw == null) return 0;
        try { return Convert.ToInt32(raw, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    /// <summary>
    /// Occlusion against owned overlays only: the HUD curtain. An overlay occludes a control only when it overlaps the
    /// rect and renders above it (canvas sorting order, then sibling order on a tie). A control that is itself that
    /// overlay is not marked covered by it. (The consent prompt's layer went with server distribution, 2026-10-05.)
    /// </summary>
    internal static (string? occludedBy, int? occluderOrder) Occlusion(
        int x, int y, int width, int height,
        int sortingOrder, int siblingOrder,
        OverlayLayer curtain,
        bool selfIsCurtain = false)
    {
        if (!selfIsCurtain && curtain.Covers(x, y, width, height)
            && curtain.RendersAbove(sortingOrder, siblingOrder))
            return (OccludedByCurtain, curtain.SortingOrder);

        return (null, null);
    }

    internal static bool IsInteractable(bool visible, bool raycastTarget, string? occludedBy)
        => visible && raycastTarget && occludedBy == null;

    /// <summary>
    /// Workspace chrome, scrollbars and content widgets. Native tabs, entry
    /// buttons are not workspace controls.
    /// </summary>
    internal static bool IsWorkspaceControlId(string? id) =>
        id is "search" or "close-back" or "sidebar-row"
            or "list-scrollbar-track" or "list-scrollbar-handle"
            or "content-scrollbar-track" or "content-scrollbar-handle"
            or "widget" or "tab-strip" or "header" or "viewport"
            or "header-title" or "header-reason" or "action";

    internal const string TabStripId = "tab-strip";
    internal const string TabStripKind = "tab-strip";

    /// <summary>
    /// One <c>mods_ui rects</c> row for a workspace tab button. <c>name</c> is
    /// the caption; <c>selected</c> is the gold tab.
    /// </summary>
    internal static Dictionary<string, object?> TabStripRow(
        Dictionary<string, object?> rect, string name, bool selected)
    {
        Annotate(rect, TabStripId, TabStripKind, name);
        rect["name"] = name;
        rect["selected"] = selected;
        return rect;
    }

    internal static Dictionary<string, object?> Annotate(
        Dictionary<string, object?> rect,
        string id,
        string kind,
        string? label = null,
        string? plugin = null,
        int? index = null,
        object? controls = null)
    {
        rect["id"] = id;
        rect["kind"] = kind;
        if (label != null) rect["label"] = label;
        if (plugin != null) rect["plugin"] = plugin;
        if (index != null) rect["index"] = index.Value;
        if (controls != null) rect["controls"] = controls;
        if (!rect.ContainsKey("align"))
            rect["align"] = AlignCentre;
        return rect;
    }

    internal const string AlignLeft = "left";
    internal const string AlignCentre = "centre";

    internal static string NormalizeAlign(string? value) =>
        string.Equals(value, AlignLeft, StringComparison.OrdinalIgnoreCase) ? AlignLeft : AlignCentre;

    internal static void WriteAlign(Dictionary<string, object?> rect, string? align)
    {
        if (rect == null) return;
        rect["align"] = NormalizeAlign(align);
    }

    /// <summary>
    /// <c>native-container</c> / <c>native-background</c> found flag and why.
    /// Why stays populated when found (<c>live</c> / <c>cached</c>).
    /// </summary>
    internal static void WriteFoundWhy(Dictionary<string, object?> row, bool found, string? why)
    {
        if (row == null) return;
        row["found"] = found;
        row["why"] = why ?? "";
    }

    internal static void CopyClientRect(Dictionary<string, object?> dest, Dictionary<string, object?> source)
    {
        if (dest == null || source == null) return;
        foreach (string key in new[] { "x", "y", "width", "height" })
        {
            if (source.TryGetValue(key, out var value)) dest[key] = value;
        }

        if (source.TryGetValue("unity", out var unity) && unity is Dictionary<string, object?> srcU)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var kv in srcU) copy[kv.Key] = kv.Value;
            dest["unity"] = copy;
        }
    }
}

/// <summary>
/// Client Dev op <c>mods_ui</c>: focus-free open / close / select / widget / native-tab /
/// resolution of the Mods workspace. Main thread. Refuses on the server.
/// </summary>
internal static partial class ModsUiOps
{
    /// <summary>
    /// <c>{"action":"..."}</c>. Parse is game-free; the Unity half is compiled out of the
    /// test project. Actions that need a frame (open, close, back, native_tab, resolution)
    /// return a <see cref="DeferredResult"/> scheduled with
    /// <see cref="ModsUiArgs.NextDrainDelaySeconds"/> so the after-status is read on the
    /// next Drain rather than blocking this one.
    /// </summary>
    public static object Run(JsonElement args)
    {
        var request = ModsUiArgs.Parse(args);
#if PROGENITOR_TESTS
        throw new InvalidOperationException(ModsUiArgs.ClientOnlyMessage);
#else
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException(ModsUiArgs.ClientOnlyMessage);
        return Execute(request);
#endif
    }

#if !PROGENITOR_TESTS
    private static object Execute(ModsUiArgs.Request request)
    {
        var clock = Stopwatch.StartNew();
        // Bridge cost: a light read skips the ~90 ms workspace status before and after (ModsUiArgs.IsLightRead).
        bool light = ModsUiArgs.SkipsStatus(request);
        Func<bool, Dictionary<string, object?>> status = fonts =>
            light ? ModsUiArgs.SkippedStatus(ModsMenu.IsOpen) : ReadStatus(fonts);
        var before = status(false);
        object? detail;
        bool ok;
        try
        {
            if (!ModsMenu.IsOpen && ModsUiArgs.RefusedWithWorkspaceClosed(request, ModsMenu.DialogOpen))
            {
                return ModsUiResult.Envelope(
                    false, request.Action, before, status(false),
                    new { error = ModsUiArgs.WorkspaceClosedError },
                    clock.Elapsed.TotalMilliseconds);
            }

            if (request.Action == "menu" && request.Op != null)
                return ExecuteGameMenu(request, before, light, clock);

            // TEXTTEST1: focus-free text-field tests (ModsUiOps.TextTest.cs).
            if (ModsUiArgs.IsTextFieldAction(request.Action) || request.Action == "perf")
                return ExecuteTextTest(request, before, light, clock);

            // JOINFLOW1: the date-time picker (ModsUiOps.DateTime.cs).
            if (ModsUiArgs.IsDateTimeAction(request.Action))
                return ExecuteDateTime(request, before, light, clock);

            (ok, detail) = request.Action switch
            {
                "status" => (true, (object)new { readback = "same-drain" }),
                "open" => DoOpen(request),
                "close" => DoClose(),
                "back" => DoBack(),
                "select" => DoSelect(request),
                "select_tab" => DoSelectTab(request),
                "search" => DoSearch(request),
                "scroll" => DoScroll(request),
                "widget_read" => DoWidgetRead(request),
                "widget_click" => DoWidgetClick(request),
                "dialog_choose" => (true, ModsMenu.ChooseDialog(request.Choice)),
                "dialog_type" => (true, ModsMenu.TypeDialog(request.Value)),
                "dropdown_choose" => (true, ModsMenu.ChooseDropdown(request.Pane, request.Label, request.Value)),
                "radio_choose" => (true, ModsMenu.ChooseRadio(request.Pane, request.Label, request.Value)),
                "hover" => request.Cell != null
                    ? (true, ModsMenu.HoverCell(request.Label, request.Cell, request.Leave))
                    : (true, ModsMenu.HoverOption(request.Pane, request.Label, request.Option, request.Leave)),
                "keys" => (true, ModsMenu.KeysOp(request.Op, request.Target, request.Chord)),
                "nav_back" => (true, ModsMenu.NavigateOp(back: true)),
                "nav_forward" => (true, ModsMenu.NavigateOp(back: false)),
                "nav_state" => (true, (object)ModsMenu.NavState()),
                "menu_tab_state" => (true, (object)ModsMenu.MenuTabState()),
                "settings_places" => (true, (object)ModsMenu.SettingsPlacesReadback()),
                "section" => (true, ModsMenu.SectionOp(request.Tab, request.Group)),
                "drawer" => (true, ModsMenu.DrawerOp(request.Group, request.Open)),
                "restart" => (true, ModsMenu.RestartOp(request.Op, request.DryRun)),
                "native_tab" => DoNativeTab(request),
                "resolution" => DoResolution(request),
                "rects" => DoRects(),
                "window" => DoWindow(),
                "scene" => DoScene(),
                "menu" => DoMenu(),
                "hit" => DoHit(request),
                "click_at" => DoClickAt(request),
                "tabs" => DoTabs(),
                _ => throw new ArgumentException($"args.action must be {string.Join(", ", ModsUiArgs.Actions)} (got '{request.Action}')"),
            };
        }
        catch (ArgumentException e)
        {
            return ModsUiResult.Envelope(false, request.Action, before, status(false), new { error = e.Message }, clock.Elapsed.TotalMilliseconds);
        }
        catch (InvalidOperationException e)
        {
            return ModsUiResult.Envelope(false, request.Action, before, status(false), new { error = e.Message }, clock.Elapsed.TotalMilliseconds);
        }

        if (ModsUiArgs.IsPendingTopTab(detail))
            return DeferOpenTopTab(request.Action, before, detail, clock);

        if (request.NeedsNextDrain)
            return DeferAfter(request.Action, before, detail, ok, clock);

        if (light)
            return ModsUiResult.Envelope(ok, request.Action, before, status(false), detail, clock.Elapsed.TotalMilliseconds);

        var after = ReadStatus(request.Fonts);
        if (ModsUiArgs.SameDrainOpenWithoutCurtain(after))
            return DeferAfter(request.Action, before, detail, ok, clock);

        return ModsUiResult.Envelope(ok, request.Action, before, after, detail, clock.Elapsed.TotalMilliseconds);
    }

    private static object DeferAfter(string action, object before, object? detail, bool ok, Stopwatch clock)
    {
        detail = WithReadback(detail, "next-drain");
        var deferred = new DeferredResult(ModsUiArgs.NextDrainWaitMs);
        try
        {
            MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                        return;
                    object published = action == "resolution" ? WithWindow(detail) : detail;
                    deferred.SetResult(ModsUiResult.Envelope(
                        ok, action, before, ReadStatus(), published, clock.Elapsed.TotalMilliseconds));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("mods_ui readback: " + e.Message);
                    if (!deferred.Completion.Task.IsCompleted)
                        deferred.SetResult(ModsUiResult.Envelope(
                            false, action, before, ModsUiResult.EmptyStatus(),
                            new { error = e.Message, readback = "next-drain" },
                            clock.Elapsed.TotalMilliseconds));
                }
            }, deferred);
        }
        catch
        {
            deferred.SetException(new InvalidOperationException("mods_ui could not schedule a next-Drain readback"));
            throw;
        }
        return deferred;
    }

    // ---- GAMEMENU1: focus-free control of the game's full-screen menu -------------------------

    /// <summary>The game menu as the HUD reports it now (no scene walk: the HUD's typed menu read).</summary>
    private static GameMenuPlan.State ReadGameMenu()
    {
        bool inWorld = false, menuUp = false, ready = false;
        string hudMenu = "";
        int? tab = null;
        try
        {
            var world = Progenitor.Worlds.Client;
            var hud = world != null && world.IsCreated ? OpenHUDMenuSystem.Get(world) : null;
            inWorld = hud != null;
            if (hud != null)
            {
                hudMenu = hud.CurrentMenuType.ToString();
                menuUp = hud.CurrentMenuType == HUDMenuType.FullscreenMenu;
                var menu = menuUp ? hud.CurrentMenu?.TryCast<FullscreenMenu>() : null;
                if (menu != null)
                {
                    try { if (menu.CurrentView != null) tab = (int)menu.CurrentView.State; } catch { tab = null; }
                    try
                    {
                        var group = menu.OuterTabGroup;
                        ready = group != null && group.gameObject.activeInHierarchy;
                    }
                    catch { ready = false; }
                }
            }
        }
        catch { /* reads as no world */ }
        return new GameMenuPlan.State(inWorld, hudMenu, menuUp, tab, ModsMenu.IsOpen, ready);
    }

    private static object ExecuteGameMenu(ModsUiArgs.Request request, object before, bool light, Stopwatch clock)
    {
        string op = request.Op!;
        var start = ReadGameMenu();
        Func<object> after = () => light ? ModsUiArgs.SkippedStatus(ModsMenu.IsOpen) : ReadStatus();
        if (op == GameMenuPlan.OpStatus)
        {
            var reply = start.ToReply();
            reply["readback"] = "same-drain";
            return ModsUiResult.Envelope(true, request.Action, before, after(), reply, clock.Elapsed.TotalMilliseconds);
        }

        int? want = null;
        if (request.Tab != null && GameMenuPlan.TryParseTab(request.Tab, out _, out int state)) want = state;
        var (step, why) = op == GameMenuPlan.OpClose ? GameMenuPlan.DecideClose(start) : GameMenuPlan.DecideOpen(start, want);

        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["op"] = op,
            ["tab"] = request.Tab,
            ["step"] = step.ToString(),
            ["menuBefore"] = start.ToReply(),
        };
        if (step is GameMenuPlan.Step.NoOp or GameMenuPlan.Step.NoWorld or GameMenuPlan.Step.Refuse)
        {
            bool noOpOk = step == GameMenuPlan.Step.NoOp;
            detail["noop"] = true;
            detail["why"] = why;
            detail["menuAfter"] = start.ToReply();
            detail["readback"] = "same-drain";
            if (!noOpOk) detail["error"] = why;
            return ModsUiResult.Envelope(noOpOk, request.Action, before, after(), detail, clock.Elapsed.TotalMilliseconds);
        }

        // Start the change through the game's own paths.
        int clicks = 0;
        switch (step)
        {
            case GameMenuPlan.Step.CloseWorkspaceThenDismiss:
                ModsMenu.Close(GameMenuPlan.ReasonClose);
                detail["handler"] = "ModsMenu.Close(" + GameMenuPlan.ReasonClose + ")+HUDMenuManager.GoToMenu(None)";
                detail["dismissed"] = ModsMenu.TryDismissFullscreenMenu();
                break;
            case GameMenuPlan.Step.Dismiss:
                detail["handler"] = "HUDMenuManager.GoToMenu(None)";
                detail["dismissed"] = ModsMenu.TryDismissFullscreenMenu();
                break;
            case GameMenuPlan.Step.Raise:
            case GameMenuPlan.Step.RaiseThenSelect:
                detail["handler"] = step == GameMenuPlan.Step.Raise
                    ? "HUDMenuManager.GoToMenu(FullscreenMenu)"
                    : "HUDMenuManager.GoToMenu(FullscreenMenu)+OuterTabEntry.OnPointerClick";
                detail["raised"] = ModsMenu.TryRequestFullscreenMenu();
                break;
            case GameMenuPlan.Step.Select:
                detail["handler"] = "OuterTabEntry.OnPointerClick";
                break;
            case GameMenuPlan.Step.OpenMods:
                detail["handler"] = "ModsMenu.Toggle(" + GameMenuPlan.ReasonOpen + ") (the shortcut route)";
                ModsMenu.ToggleFrom(GameMenuPlan.ReasonOpen);
                break;
        }

        var deferred = new DeferredResult(ModsUiArgs.NextDrainWaitMs);
        int frames = 0, stable = 0, sinceReadyOrClick = 0;
        bool sawReady = false;
        var clickLog = new List<object?>();
        void Finish(bool ok, GameMenuPlan.State now, string? error)
        {
            detail["menuAfter"] = now.ToReply();
            detail["framesWaited"] = frames;
            detail["clicks"] = clicks;
            if (clickLog.Count > 0) detail["clickResults"] = clickLog;
            detail["readback"] = "verified-after-frames";
            if (error != null) detail["error"] = error;
            deferred.SetResult(ModsUiResult.Envelope(ok, request.Action, before, ReadStatus(), detail, clock.Elapsed.TotalMilliseconds));
        }
        void Poll()
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                frames++;
                var now = ReadGameMenu();
                if (now.MenuReady && !sawReady) { sawReady = true; sinceReadyOrClick = 0; }
                else sinceReadyOrClick++;
                if (GameMenuPlan.ShouldClick(want, now, clicks, sinceReadyOrClick))
                {
                    clicks++;
                    sinceReadyOrClick = 0;
                    stable = 0;
                    var (clickOk, clickDetail) = DoNativeTab(new ModsUiArgs.Request
                    {
                        Action = "native_tab",
                        Tab = GameMenuPlan.NativeEnumName(want!.Value),
                    });
                    clickLog.Add(new { frame = frames, ok = clickOk, detail = clickDetail });
                }
                else if (GameMenuPlan.Reached(op, want, now))
                {
                    if (++stable >= GameMenuPlan.StableFrames)
                    {
                        Finish(true, now, null);
                        return;
                    }
                }
                else stable = 0;

                if (frames >= GameMenuPlan.VerifyFrames)
                {
                    Finish(false, now, "the game menu did not reach the requested state within " + GameMenuPlan.VerifyFrames + " frames");
                    return;
                }
                MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("mods_ui menu " + op + ": " + e.Message);
                if (!deferred.Completion.Task.IsCompleted) Finish(false, ReadGameMenu(), e.Message);
            }
        }
        try { MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, Poll, deferred); }
        catch
        {
            deferred.SetException(new InvalidOperationException("mods_ui could not schedule the game menu readback"));
            throw;
        }
        Plugin.Logger?.LogInfo("mods_ui menu " + op + (request.Tab != null ? " tab=" + request.Tab : "") + " step=" + step);
        return deferred;
    }

    private static object DeferOpenTopTab(string action, object before, object? detail, Stopwatch clock)
    {
        string menuOpenedBy = ModsUiArgs.MenuOpenedByAlreadyUp;
        int remaining = ModsUiArgs.TopTabAttachFrames;
        if (detail is Dictionary<string, object?> d)
        {
            if (d.TryGetValue("menuOpenedBy", out var raw)
                && raw is string named
                && !string.IsNullOrEmpty(named))
                menuOpenedBy = named;
            if (d.TryGetValue(ModsUiArgs.RemainingTopTabKey, out var frames)
                && frames is int n
                && n > 0)
                remaining = n;
        }

        var deferred = new DeferredResult(ModsUiArgs.NextDrainWaitMs);
        try
        {
            MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                        return;
                    CompleteTopTabAttach(action, before, menuOpenedBy, remaining, clock, deferred);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("mods_ui top-tab attach: " + e.Message);
                    if (!deferred.Completion.Task.IsCompleted)
                        deferred.SetResult(ModsUiResult.Envelope(
                            false, action, before, ModsUiResult.EmptyStatus(),
                            FailTopTabAttach(menuOpenedBy, "error"),
                            clock.Elapsed.TotalMilliseconds));
                }
            }, deferred);
        }
        catch
        {
            deferred.SetException(new InvalidOperationException("mods_ui could not schedule a next-Drain readback"));
            throw;
        }
        return deferred;
    }

    private static void CompleteTopTabAttach(
        string action,
        object before,
        string menuOpenedBy,
        int remaining,
        Stopwatch clock,
        DeferredResult deferred)
    {
        var go = FindNamed("progenitor-ui-core-mods-tab");
        if (go == null)
        {
            int next = ModsUiArgs.ConsumeTopTabFrame(remaining);
            if (ModsUiArgs.ShouldKeepWaitingTopTab(next))
            {
                MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, () =>
                {
                    try
                    {
                        if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                            return;
                        CompleteTopTabAttach(action, before, menuOpenedBy, next, clock, deferred);
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning("mods_ui top-tab attach: " + e.Message);
                        if (!deferred.Completion.Task.IsCompleted)
                            deferred.SetResult(ModsUiResult.Envelope(
                                false, action, before, ModsUiResult.EmptyStatus(),
                                FailTopTabAttach(menuOpenedBy, "error"),
                                clock.Elapsed.TotalMilliseconds));
                    }
                }, deferred);
                return;
            }

            deferred.SetResult(ModsUiResult.Envelope(
                false, action, before, ReadStatus(),
                FailTopTabAttach(menuOpenedBy, ModsMenu.DescribeHudMenuType()),
                clock.Elapsed.TotalMilliseconds));
            return;
        }

        if (!ModsUiArgs.ShouldInvokeOwnedButton(buttonAttached: true, alreadyInvoked: false))
        {
            deferred.SetResult(ModsUiResult.Envelope(
                false, action, before, ReadStatus(),
                FailTopTabAttach(menuOpenedBy, ModsMenu.DescribeHudMenuType()),
                clock.Elapsed.TotalMilliseconds));
            return;
        }

        var (ok, invoked) = InvokeOwnedButton(go, "top-tab", menuOpenedBy);
        var after = ReadStatus();
        object published = WithReadback(invoked, "next-drain");
        if (ok && ModsUiArgs.SameDrainOpenWithoutCurtain(after))
        {
            MainThread.PostDelayed(ModsUiArgs.NextDrainDelaySeconds, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                        return;
                    deferred.SetResult(ModsUiResult.Envelope(
                        ok, action, before, ReadStatus(), published, clock.Elapsed.TotalMilliseconds));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("mods_ui top-tab curtain: " + e.Message);
                    if (!deferred.Completion.Task.IsCompleted)
                        deferred.SetResult(ModsUiResult.Envelope(
                            false, action, before, ModsUiResult.EmptyStatus(),
                            new { error = e.Message, readback = "next-drain" },
                            clock.Elapsed.TotalMilliseconds));
                }
            }, deferred);
            return;
        }

        deferred.SetResult(ModsUiResult.Envelope(
            ok, action, before, after, published, clock.Elapsed.TotalMilliseconds));
    }

    private static Dictionary<string, object?> FailTopTabAttach(string menuOpenedBy, string menu)
    {
        bool? restored = null;
        if (ModsUiArgs.ShouldRestoreMenuOnAttachFailure(menuOpenedBy))
            restored = TryRestoreOpenedFullscreenMenu();
        return ModsUiArgs.TopTabNotAttachedDetail(menuOpenedBy, menu, restored);
    }

    /// <summary>
    /// Same <c>HUDMenuManager.GoToMenu</c> NativeModsEntry used to bring FullscreenMenu up,
    /// back to <c>None</c>.
    /// </summary>
    private static bool TryRestoreOpenedFullscreenMenu()
    {
        try
        {
            var world = Progenitor.Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var hud = OpenHUDMenuSystem.Get(world);
            if (hud == null || hud.CurrentMenuType != HUDMenuType.FullscreenMenu) return false;
            HUDMenuManager.GoToMenu(world, HUDMenuType.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object WithReadback(object? detail, string readback)
    {
        Dictionary<string, object?> dict;
        try
        {
            if (detail is Dictionary<string, object?> existing)
                dict = new Dictionary<string, object?>(existing);
            else
            {
                var json = JsonSerializer.Serialize(detail ?? new Dictionary<string, object?>(), ModsUiResult.JsonOptions);
                dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
            }
        }
        catch
        {
            dict = new Dictionary<string, object?>();
        }
        dict["readback"] = readback;
        return dict;
    }

    private static object WithWindow(object? detail)
    {
        Dictionary<string, object?> dict;
        try
        {
            if (detail is Dictionary<string, object?> existing)
                dict = new Dictionary<string, object?>(existing);
            else
            {
                var json = JsonSerializer.Serialize(detail ?? new Dictionary<string, object?>(), ModsUiResult.JsonOptions);
                dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
            }
        }
        catch
        {
            dict = new Dictionary<string, object?>();
        }
        try { dict["window"] = ReadWindowPayload(); }
        catch (Exception e)
        {
            dict["window"] = ModsUiResult.EmptyWindow();
            dict["windowError"] = e.Message;
        }
        return dict;
    }

    private static Dictionary<string, object?> ReadStatus(bool includeFonts = false)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            bool open = false;
            string? plugin = null;
            string? page = null;
            string pageId = "";
            string search = "";
            object entries = Array.Empty<object>();
            int panes = 0;
            string site = "none";
            bool curtain = false;
            bool curtainRaycast = false;
            bool modsCapture = false;
            int observers = 0;
            object serverList = ModsUiResult.ServerList("unknown");
            int serverListEpoch = 0;
            bool suspended = false;
            string? suspendReason = null;
            int visibleRows = 0;
            int totalRows = 0;
            object listScroll = ModsUiResult.ListScroll(1f, false);
            object contentScroll = ModsUiResult.ListScroll(1f, false);
            string? tab = null;
            object? fontDonor = null;
            object? fontAssets = null;
            object? mods = null;
            object? dialog = null;
            string footer = "";
            try
            {
                var described = ModsMenu.DescribeWorkspace();
                MergeWorkspace(described, ref open, ref plugin, ref page, ref pageId, ref search, ref entries, ref panes, ref site, ref curtain, ref modsCapture, ref observers, ref serverList, ref serverListEpoch, ref suspended, ref suspendReason, ref visibleRows, ref totalRows, ref listScroll, ref contentScroll, ref tab, ref fontDonor, ref mods, ref dialog, ref footer);
            }
            catch { }
            try
            {
                if (ModsMenu.DescribeCurtainStatus() is Dictionary<string, object?> curtainStatus)
                {
                    if (curtainStatus.TryGetValue("present", out var present) && present is bool pb)
                        curtain = pb;
                    if (curtainStatus.TryGetValue("raycastTarget", out var ray) && ray is bool rb)
                        curtainRaycast = rb;
                    else
                        curtainRaycast = curtain;
                }
                else
                    curtainRaycast = curtain;
            }
            catch { curtainRaycast = curtain; }

            bool capture = false;
            int width = 0, height = 0;
            string mode = "window";
            try { capture = UiApi.IsCapturingInput; } catch { }
            try
            {
                width = Screen.width;
                height = Screen.height;
                mode = ReadMode(Screen.fullScreenMode);
            }
            catch { }

            string? shortcut = null;
            try { shortcut = ModsUiResult.ReadConfiguredShortcut(); } catch { }

            if (includeFonts)
            {
                try { fontAssets = ModsMenu.DescribeLoadedFontAssets(); }
                catch { fontAssets = Array.Empty<object>(); }
            }

            double statusMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            var status = ModsUiResult.Status(open, site, plugin, page, pageId, search, entries, curtain, capture, width, height, mode, panes, observers, statusMs, modsCapture, serverList, serverListEpoch, suspended, suspendReason, shortcut, visibleRows, totalRows, listScroll, contentScroll, tab, fontDonor, fontAssets, curtainRaycast, mods, dialog, footer);
            ReadRoute(out var route, out var nativeRoute);
            return ModsUiResult.WithHosts(ModsUiResult.WithFocus(ModsUiResult.WithRoute(status, route, nativeRoute)));
        }
        catch
        {
            double statusMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return ModsUiResult.WithFocus(ModsUiResult.WithRoute(ModsUiResult.EmptyStatus(statusMs), "imitation", null));
        }
    }

    private static void MergeWorkspace(
        object described,
        ref bool open,
        ref string? plugin,
        ref string? page,
        ref string pageId,
        ref string search,
        ref object entries,
        ref int panes,
        ref string site,
        ref bool curtain,
        ref bool modsCapture,
        ref int observers,
        ref object serverList,
        ref int serverListEpoch,
        ref bool suspended,
        ref string? suspendReason,
        ref int visibleRows,
        ref int totalRows,
        ref object listScroll,
        ref object contentScroll,
        ref string? tab,
        ref object? fontDonor,
        ref object? mods,
        ref object? dialog,
        ref string footer)
    {
        var json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("open", out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            open = el.GetBoolean();
        if (root.TryGetProperty("tab", out el))
            tab = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (root.TryGetProperty("plugin", out el) && el.ValueKind == JsonValueKind.String)
            plugin = el.GetString();
        if (root.TryGetProperty("page", out el) && el.ValueKind == JsonValueKind.String)
            page = el.GetString();
        if (root.TryGetProperty("pageId", out el) && el.ValueKind == JsonValueKind.String)
            pageId = el.GetString() ?? "";
        if (root.TryGetProperty("search", out el) && el.ValueKind == JsonValueKind.String)
            search = el.GetString() ?? "";
        if (root.TryGetProperty("entries", out el) && el.ValueKind == JsonValueKind.Array)
            entries = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(el.GetRawText()) ?? new List<Dictionary<string, object?>>();
        if (root.TryGetProperty("panes", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            panes = n;
        if (root.TryGetProperty("site", out el) && el.ValueKind == JsonValueKind.String)
            site = el.GetString() ?? "none";
        if (root.TryGetProperty("curtain", out el))
        {
            if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
                curtain = el.GetBoolean();
            else if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("present", out var presentEl)
                && (presentEl.ValueKind == JsonValueKind.True || presentEl.ValueKind == JsonValueKind.False))
                curtain = presentEl.GetBoolean();
        }
        if (root.TryGetProperty("modsCapture", out el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            modsCapture = el.GetBoolean();
        else
            modsCapture = open;
        if (root.TryGetProperty("observers", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out n))
            observers = n;
        if (root.TryGetProperty("serverList", out el) && el.ValueKind == JsonValueKind.Object)
            serverList = ReadServerList(el);
        if (root.TryGetProperty("serverListEpoch", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out n))
            serverListEpoch = n;
        if (root.TryGetProperty("suspended", out el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            suspended = el.GetBoolean();
        if (root.TryGetProperty("suspendReason", out el))
            suspendReason = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        if (root.TryGetProperty("visibleRows", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out n))
            visibleRows = n;
        if (root.TryGetProperty("totalRows", out el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out n))
            totalRows = n;
        if (root.TryGetProperty("listScroll", out el) && el.ValueKind == JsonValueKind.Object)
            listScroll = ReadScrollField(el);
        if (root.TryGetProperty("contentScroll", out el) && el.ValueKind == JsonValueKind.Object)
            contentScroll = ReadScrollField(el);
        if (root.TryGetProperty("fontDonor", out el) && el.ValueKind == JsonValueKind.Object)
            fontDonor = ReadFontDonor(el);
        if (root.TryGetProperty("mods", out el) && el.ValueKind == JsonValueKind.Object)
            mods = ReadModsCounts(el);
        // INSTALLUX2 item 1: `dialog {open, key, kind}` as ModsMenu reported it. A workspace that
        // does not carry the field (an older Core) leaves the closed default in place.
        if (root.TryGetProperty("dialog", out el) && el.ValueKind == JsonValueKind.Object)
            dialog = ReadDialog(el);
        if (root.TryGetProperty("footer", out el) && el.ValueKind == JsonValueKind.String)
            footer = el.GetString() ?? "";
    }

    /// <summary>INSTALLUX2: <c>status.dialog</c> as the workspace reported it. Missing fields read as closed.</summary>
    private static Dictionary<string, object?> ReadDialog(JsonElement el)
    {
        bool open = false;
        string key = "", kind = "none", footer = "", title = "", error = "";
        int queued = 0;
        if (el.TryGetProperty("open", out var o) && (o.ValueKind == JsonValueKind.True || o.ValueKind == JsonValueKind.False))
            open = o.GetBoolean();
        if (el.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
            key = k.GetString() ?? "";
        if (el.TryGetProperty("kind", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(d.GetString()))
            kind = d.GetString()!;
        if (el.TryGetProperty("footer", out var f) && f.ValueKind == JsonValueKind.String)
            footer = f.GetString() ?? "";
        // DIALOGS1: the open dialog's title, a Prompt's error line and how many dialogs wait.
        if (el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            title = t.GetString() ?? "";
        if (el.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            error = e.GetString() ?? "";
        if (el.TryGetProperty("queued", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out int n))
            queued = n;
        return new Dictionary<string, object?>
        {
            ["open"] = open,
            ["key"] = key,
            ["kind"] = kind,
            ["footer"] = footer,
            ["title"] = title,
            ["error"] = error,
            ["queued"] = queued,
        };
    }

    /// <summary>MODS2: <c>status.mods</c> as the Mods tab reported it. A missing field reads as zero.</summary>
    private static Dictionary<string, object?> ReadModsCounts(JsonElement el)
    {
        int shown = 0, required = 0, server = 0;
        if (el.TryGetProperty("shown", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n))
            shown = n;
        if (el.TryGetProperty("required", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out n))
            required = n;
        if (el.TryGetProperty("server", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n))
            server = n;
        return ModsUiResult.ModsCounts(shown, required, server);
    }

    private static Dictionary<string, object?> ReadFontDonor(JsonElement el)
    {
        string font = "";
        string path = "";
        string source = "";
        if (el.TryGetProperty("font", out var f) && f.ValueKind == JsonValueKind.String)
            font = f.GetString() ?? "";
        if (el.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            path = p.GetString() ?? "";
        if (el.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
            source = s.GetString() ?? "";
        return ModsUiResult.FontDonor(font, path, source);
    }

    private static Dictionary<string, object?> ReadScrollField(JsonElement el)
    {
        double position = 1d;
        bool overflow = false;
        if (el.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number)
            position = pos.GetDouble();
        if (el.TryGetProperty("overflow", out var ov)
            && (ov.ValueKind == JsonValueKind.True || ov.ValueKind == JsonValueKind.False))
            overflow = ov.GetBoolean();
        return ModsUiResult.ListScroll(position, overflow);
    }

    private static Dictionary<string, object?> ReadServerList(JsonElement el)
    {
        string state = "unknown";
        if (el.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
            state = st.GetString() ?? "unknown";
        int? count = null;
        if (el.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n))
            count = n;
        return ModsUiResult.ServerList(state, count);
    }

    private static string ReadMode(FullScreenMode mode) => mode switch
    {
        FullScreenMode.ExclusiveFullScreen => "exclusive",
        FullScreenMode.FullScreenWindow => "fullscreenwindow",
        _ => "window",
    };

    private static FullScreenMode WriteMode(string mode) => mode switch
    {
        "exclusive" => FullScreenMode.ExclusiveFullScreen,
        "fullscreenwindow" => FullScreenMode.FullScreenWindow,
        "windowed" => FullScreenMode.Windowed,
        _ => FullScreenMode.Windowed,
    };

    /// <summary><c>window</c> payload mode: <c>windowed</c> is <see cref="FullScreenMode.Windowed"/>.</summary>
    private static string WindowModeName(FullScreenMode mode) => mode switch
    {
        FullScreenMode.ExclusiveFullScreen => "exclusive",
        FullScreenMode.FullScreenWindow => "fullscreenwindow",
        FullScreenMode.Windowed => "windowed",
        _ => "window",
    };

    /// <summary>
    /// <c>NativeModsEntry.RouteName</c> / <c>DescribeNativeRoute</c> are internal to Core (no
    /// InternalsVisibleTo for Dev, same as <c>PingIdentity</c>): invoked by reflection on the
    /// Core assembly Dev is bound to. Any miss reads as the imitation route.
    /// </summary>
    private static void ReadRoute(out string route, out Dictionary<string, object?>? nativeRoute)
    {
        route = "imitation";
        nativeRoute = null;
        try
        {
            var type = typeof(ModsMenu).Assembly.GetType("Progenitor.Ui.NativeModsEntry");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var name = type?.GetMethod("RouteName", flags)?.Invoke(null, null) as string;
            if (!string.IsNullOrEmpty(name)) route = name!;
            nativeRoute = type?.GetMethod("DescribeNativeRoute", flags)?.Invoke(null, null) as Dictionary<string, object?>;
        }
        catch
        {
            route = "imitation";
            nativeRoute = null;
        }
    }

    private static GameObject? FindNamed(string name)
    {
        try
        {
            var all = UnityObject.FindObjectsOfType<GameObject>(true);
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].name == name) return all[i];
        }
        catch { }
        try { return GameObject.Find(name); }
        catch { return null; }
    }

    /// <summary>
    /// The owned title-list MODS button, including when <c>MainMenuNewView</c> is
    /// inactive (we hid it) or immediately after restore. Scene-wide
    /// <c>FindObjectsOfType&lt;GameObject&gt;</c> can miss it; walking the view
    /// type with <c>includeInactive</c> does not.
    /// </summary>
    private static GameObject? FindMainMenuButton()
    {
        try
        {
            var views = UnityObject.FindObjectsOfType<MainMenuNewView>(true);
            if (views != null)
            {
                for (int i = 0; i < views.Length; i++)
                {
                    var view = views[i];
                    if (view == null) continue;
                    var found = FindChildNamedRecursive(view.transform, "progenitor-ui-core-main-mods");
                    if (found != null) return found;
                }
            }
        }
        catch { }

        return FindNamed("progenitor-ui-core-main-mods");
    }

    private static GameObject? FindChildNamedRecursive(Transform parent, string name)
    {
        if (parent == null) return null;
        try
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                if (child.name == name) return child.gameObject;
                var nested = FindChildNamedRecursive(child, name);
                if (nested != null) return nested;
            }
        }
        catch { }

        return null;
    }

    private static (bool ok, object detail) DoOpen(ModsUiArgs.Request request)
    {
        string site = request.Site!;
        if (ModsMenu.IsOpen)
        {
            var already = new Dictionary<string, object?>
            {
                ["path"] = site,
                ["alreadyOpen"] = true,
                ["readback"] = "next-drain",
            };
            if (site == "top-tab")
                already["menuOpenedBy"] = ModsUiArgs.MenuOpenedByAlreadyUp;
            return (true, already);
        }

        if (site == "shortcut")
        {
            ModsMenu.Toggle();
            // TABS2: in a world the shortcut opens through the FullscreenMenu a few frames later.
            bool routed = ModsMenu.ShortcutOpenPending;
            return (ModsMenu.IsOpen || routed, new Dictionary<string, object?>
            {
                ["path"] = "shortcut",
                ["handler"] = "ModsMenu.Toggle",
                ["routedThroughMenu"] = routed,
                ["readback"] = "next-drain",
            });
        }

        if (site == "top-tab")
            return DoOpenTopTab();

        if (site == "standalone")
        {
            // TABS2: the workspace window without the FullscreenMenu route (the shortcut's fallback
            // window), in a world or at the title. The in-world menu request is suppressed for it.
            ModsMenu.OpenStandalone();
            return (ModsMenu.IsOpen, new Dictionary<string, object?>
            {
                ["path"] = "standalone",
                ["handler"] = "ModsMenu.OpenStandalone",
                ["readback"] = "next-drain",
            });
        }

        return InvokeOwnedButton(FindMainMenuButton(), "main-menu", menuOpenedBy: null);
    }

    private static (bool ok, object detail) DoOpenTopTab()
    {
        var go = FindNamed("progenitor-ui-core-mods-tab");
        if (go != null)
            return InvokeOwnedButton(go, "top-tab", ModsUiArgs.MenuOpenedByAlreadyUp);

        bool requested = ModsMenu.TryRequestFullscreenMenu();
        return (false, new Dictionary<string, object?>
        {
            ["path"] = "top-tab",
            ["menuOpenedBy"] = ModsUiArgs.TopTabMenuOpenedBy(requested),
            [ModsUiArgs.PendingTopTabKey] = true,
            [ModsUiArgs.RemainingTopTabKey] = ModsUiArgs.TopTabAttachFrames,
            ["readback"] = "next-drain",
        });
    }

    private static (bool ok, object detail) InvokeOwnedButton(GameObject? go, string site, string? menuOpenedBy)
    {
        string goName = site == "main-menu" ? "progenitor-ui-core-main-mods" : "progenitor-ui-core-mods-tab";
        if (go == null)
            return (false, ButtonDetail(site, menuOpenedBy, error: "owned " + site + " button '" + goName + "' is not attached"));
        if (ModsUiArgs.ShouldOpenThroughNativeRoute(site, ModsMenu.RouteIsNative))
        {
            // NATIVE3 §2: the owned clone carries the game's OnLeftClicked field, not a uGUI
            // onClick; fire the same path a click does (typed SetState(7) + the stored open).
            bool opened = ModsMenu.TryOpenNativeRoute(out string why, out string steps);
            var detail = ButtonDetail(site, menuOpenedBy,
                handler: ModsUiArgs.TopTabHandler(routeIsNative: true),
                error: opened ? null : "native route open refused: " + why);
            detail["route"] = "native";
            detail["steps"] = steps;
            return (opened, detail);
        }
        var button = go.GetComponent<Button>();
        if (button == null || button.onClick == null)
            return (false, ButtonDetail(site, menuOpenedBy, error: "owned " + site + " button has no onClick"));
        // A grayed button takes no click: onClick.Invoke skips uGUI's interactable check, so refuse here.
        if (!button.interactable)
            return (false, ButtonDetail(site, menuOpenedBy, error: "owned " + site + " button is disabled (not interactable); not clicked"));
        button.onClick.Invoke();
        var invoked = ButtonDetail(site, menuOpenedBy, handler: goName + ".onClick");
        if (site == "top-tab") invoked["route"] = "imitation";
        return (true, invoked);
    }

    private static Dictionary<string, object?> ButtonDetail(string site, string? menuOpenedBy, string? handler = null, string? error = null)
    {
        var detail = new Dictionary<string, object?>
        {
            ["path"] = site,
            ["readback"] = "next-drain",
        };
        if (handler != null) detail["handler"] = handler;
        if (error != null) detail["error"] = error;
        if (menuOpenedBy != null) detail["menuOpenedBy"] = menuOpenedBy;
        return detail;
    }

    private static (bool ok, object detail) DoClose()
    {
        bool alreadyClosed = !ModsMenu.IsOpen;
        if (ModsMenu.RouteIsNative)
        {
            // NATIVE3 §2: the owned Close, then the typed return transition to the saved native
            // page while the mapper still selects the owned one, so the next-Drain readback
            // already shows that page (the tick retries a failed return).
            bool ok = ModsMenu.TryCloseNativeRoute(out string why, out string steps);
            return (ok, new
            {
                path = "close",
                handler = ModsUiArgs.CloseNativeHandler,
                route = "native",
                steps,
                nativeReturn = why,
                alreadyClosed,
                readback = "next-drain",
            });
        }
        ModsMenu.Close();
        return (true, new { path = "close", handler = ModsUiArgs.CloseHandler, alreadyClosed, readback = "next-drain" });
    }

    private static (bool ok, object detail) DoBack()
    {
        string path = ModsMenu.CloseOrBack();
        return (true, new { path, handler = "ModsMenu.CloseOrBack", readback = "next-drain" });
    }

    private static (bool ok, object detail) DoSelect(ModsUiArgs.Request request)
    {
        var selected = ModsMenu.Select(request.Plugin, request.Page!, request.Group);
        return (true, selected);
    }

    private static (bool ok, object detail) DoSelectTab(ModsUiArgs.Request request)
    {
        var selected = ModsMenu.SelectTab(request.Tab);
        return (true, selected);
    }

    private static (bool ok, object detail) DoSearch(ModsUiArgs.Request request)
    {
        var report = ModsMenu.ApplySearchReport(request.Text);
        report["text"] = request.Text ?? "";
        return (true, report);
    }

    private static (bool ok, object detail) DoScroll(ModsUiArgs.Request request)
    {
        var scroll = FindPaneScroll(request.Pane!);
        if (scroll == null)
            return (false, new { pane = request.Pane, error = "no ScrollRect on pane '" + request.Pane + "'" });
        float normalized = request.To switch
        {
            "top" => 1f,
            "bottom" => 0f,
            _ => (float)(request.ToNumber ?? 1d),
        };
        scroll.verticalNormalizedPosition = normalized;
        return (true, new { pane = request.Pane, to = request.To, normalized = scroll.verticalNormalizedPosition });
    }

    private static ScrollRect? FindPaneScroll(string pane)
    {
        bool sidebar = pane == "list";
        try { return ModsMenu.ShownWorkspaceScroll(sidebar); }
        catch { return null; }
    }

    private static bool IsOwnedUi(Transform transform)
    {
        for (var t = transform; t != null; t = t.parent)
        {
            string name = t.name ?? "";
            if (name.StartsWith("progenitor-ui-", StringComparison.Ordinal)
                || name == "progenitor-ui-core-mods-background")
                return true;
        }
        return false;
    }

    private static (bool ok, object detail) DoWidgetRead(ModsUiArgs.Request request)
    {
        var rows = ModsMenu.ReadVisibleWidgets(request.Pane);
        var substituted = SubstituteCellRows(rows);
        // Job KEY2: the Install toolbar's input cell carries `focused`, so a reader can tell that
        // the field taking the keystrokes is the one whose focus is suppressing the menu hotkeys.
        // Stamped against the expected id rather than against "anything is focused", because with
        // two fields armed the sidebar search being focused must not make this row read true.
        for (int i = 0; i < substituted.Length; i++)
            StampInputFocus(substituted[i], ModsMenu.MenuHotkeyInstallSearch);
        // OPTIONHOVER1: the details pane's text now (the hovered option, row, selection or first row), or null.
        // SEARCHCLEAR1: the shown search box and its clear button (`{field, text, focused, shown, glyph, rect, tint}`).
        return (true, new { pane = request.Pane, count = rows.Length, rows = substituted, details = ModsMenu.DetailsPane(), searchClear = ModsMenu.DescribeSearchClear() });
    }

    /// <summary>
    /// Job KEY2. Write <c>focused</c> onto every <c>kind: "input"</c> dictionary reachable in
    /// <paramref name="node"/>, true when <c>status.focus.field</c> is
    /// <paramref name="expectedField"/>. A readback stamp only: the value comes from the same
    /// <c>ModsMenu.DescribeFocusStatus</c> the status row reports, so the two cannot disagree, and
    /// a row whose shape is not a mutable dictionary is left exactly as it was rather than being
    /// rebuilt. Depth-bounded and exception-safe for the reason every <c>mods_ui</c> read is: a
    /// diagnostic must not throw for its own diagnostics.
    /// </summary>
    private static void StampInputFocus(object? node, string expectedField, int depth = 0)
    {
        if (node == null || depth > 6) return;
        try
        {
            if (node is IDictionary<string, object?> dict)
            {
                if (dict.TryGetValue("kind", out var kind)
                    && kind is string name
                    && string.Equals(name, "input", StringComparison.Ordinal))
                {
                    dict["focused"] = FocusedFieldIs(expectedField);
                }

                var values = new List<object?>(dict.Count);
                foreach (var pair in dict) values.Add(pair.Value);
                for (int i = 0; i < values.Count; i++) StampInputFocus(values[i], expectedField, depth + 1);
                return;
            }

            if (node is System.Collections.IEnumerable list && node is not string)
            {
                foreach (var item in list) StampInputFocus(item, expectedField, depth + 1);
            }
        }
        catch { /* the unstamped row stands */ }
    }

    /// <summary>
    /// Whether <c>status.focus.field</c> is <paramref name="expectedField"/> right now. One reader,
    /// so <c>status.focus</c> and the per-row <c>focused</c> keys are the same measurement.
    /// </summary>
    private static bool FocusedFieldIs(string expectedField)
    {
        try
        {
            var focus = ModsMenu.DescribeFocusStatus();
            return focus.TryGetValue("field", out var field)
                && field is string name
                && string.Equals(name, expectedField, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// LIST1 pass-through: a cell row is described by the menu as a plain <c>widget</c> whose
    /// label is the row key; swap in the row's own readback (<c>kind: "cellrow"</c> /
    /// <c>"cellheader"</c>, <c>cells[{key, kind, text, tone, visible, rect}]</c>, <c>expanded</c>,
    /// <c>selected</c>). Rows of every other kind pass unchanged.
    /// </summary>
    internal static object[] SubstituteCellRows(object[] rows)
    {
        if (rows == null || rows.Length == 0) return rows ?? Array.Empty<object>();
        var result = new object[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            result[i] = rows[i];
            try
            {
                var element = JsonSerializer.SerializeToElement(rows[i], ModsUiResult.JsonOptions);
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(kind.GetString(), "widget", StringComparison.Ordinal)) continue;
                if (!element.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String) continue;
                var described = CellRowDescribe(label.GetString());
                if (described != null) result[i] = described;
            }
            catch { /* the plain row stands */ }
        }
        return result;
    }

    /// <summary>
    /// <c>UiCellRowHandle.DescribeVisible</c> / <c>TryInvoke</c> are internal to Core (no
    /// InternalsVisibleTo for Dev, the same seam as <see cref="ReadRoute"/>): invoked by
    /// reflection on the Core assembly Dev is bound to. A miss reads as "not a cell row".
    /// </summary>
    private static Dictionary<string, object?>? CellRowDescribe(string? key)
    {
        try
        {
            var type = typeof(ModsMenu).Assembly.GetType("Progenitor.Ui.UiCellRowHandle");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            return type?.GetMethod("DescribeVisible", flags)?.Invoke(null, new object?[] { key }) as Dictionary<string, object?>;
        }
        catch { return null; }
    }

    private static bool CellRowInvoke(string? label, out Dictionary<string, object?> detail)
    {
        detail = new Dictionary<string, object?>();
        try
        {
            var type = typeof(ModsMenu).Assembly.GetType("Progenitor.Ui.UiCellRowHandle");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            var method = type?.GetMethod("TryInvoke", flags);
            if (method == null) return false;
            var args = new object?[] { label, null };
            bool ok = method.Invoke(null, args) is true;
            if (args[1] is Dictionary<string, object?> filled) detail = filled;
            return ok;
        }
        catch { return false; }
    }

    /// <summary>
    /// The reason Core's click gate refused a grayed control (<c>UiClickGate</c>: the detail's <c>refused</c>, or an
    /// exception whose <c>Data</c> carries it), or null. A refused click is reported as such and never retried by caption.
    /// </summary>
    internal static string? ClickRefusal(Dictionary<string, object?>? detail) =>
        detail != null && detail.TryGetValue(ClickRefusedKey, out var value) && value is string s && s.Length > 0 ? s : null;

    internal static bool IsClickRefusal(Exception e) => e.Data.Contains(ClickRefusedKey);

    private static ArgumentException RefusedClick(string message)
    {
        var e = new ArgumentException(message);
        e.Data[ClickRefusedKey] = true;
        return e;
    }

    /// <summary>Core's <c>UiClickGate.RefusedKey</c> (Core is not visible to Dev).</summary>
    internal const string ClickRefusedKey = "refused";

    private static (bool ok, object detail) DoWidgetClick(ModsUiArgs.Request request)
    {
        if (request.Modifiers == 0) return DoWidgetClickCore(request);
        // BRIDGEUI1: the click's Shift / Ctrl come from the framework's input layer (HeldClickModifiers), which reads the
        // synthetic input's modifiers while it is held; set them for this one dispatch.
        int token = UiInputOps.Bridge("BeginClickModifiers", request.Modifiers) is int t ? t : -2;
        if (token == -2) throw new InvalidOperationException("widget_click: the click modifiers could not be set (another holder has the synthetic input)");
        try
        {
            var (ok, detail) = DoWidgetClickCore(request);
            if (detail is Dictionary<string, object?> d) d["modifiers"] = UiInputPlan.ModifierNames(request.Modifiers);
            return (ok, detail);
        }
        finally { UiInputOps.Bridge("EndClickModifiers", token); }
    }

    private static (bool ok, object detail) DoWidgetClickCore(ModsUiArgs.Request request)
    {
        string? refusal = null;
        try
        {
            var clicked = ModsMenu.ClickVisibleWidget(request.Pane, request.Label, request.Index, request.Value);
            // LIST1: the menu finds a cell row by its key but has no dispatch for the kind
            // (invoked 0); the row registry fires the row's own slot.
            try
            {
                var element = JsonSerializer.SerializeToElement(clicked, ModsUiResult.JsonOptions);
                if (element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    && string.Equals(kind.GetString(), "widget", StringComparison.Ordinal)
                    && element.TryGetProperty("invoked", out var invoked) && invoked.ValueKind == JsonValueKind.Number && invoked.GetInt32() == 0
                    && element.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                {
                    if (CellRowInvoke(label.GetString(), out var rowDetail))
                    {
                        rowDetail["index"] = element.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : -1;
                        rowDetail["value"] = "";
                        return (true, rowDetail);
                    }
                    if (ClickRefusal(rowDetail) is { } rowRefusal) refusal = rowRefusal;
                }
            }
            catch { /* the menu's answer stands */ }
            // A grayed control is refused (ok:false with the reason), never reported as clicked.
            if (refusal != null) throw RefusedClick(refusal);
            return (true, clicked);
        }
        catch (ArgumentException e) when (!string.IsNullOrWhiteSpace(request.Label) && request.Index == null && !IsClickRefusal(e))
        {
            // LIST1: a cell row's button (`<rowKey>/<cellKey>` or its caption) is not a panel
            // child the menu can see; the row registry dispatches its slot instead.
            if (CellRowInvoke(request.Label, out var detail))
            {
                detail["index"] = -1;
                detail["value"] = "";
                return (true, detail);
            }
            if (ClickRefusal(detail) is { } cellRefusal) throw RefusedClick(cellRefusal);
            throw;
        }
    }

    private static (bool ok, object detail) DoNativeTab(ModsUiArgs.Request request)
    {
        // NATIVE3 §3: without the FullscreenMenu up there is no outer tab to click; the mapper's
        // remembered menu (TryFullscreenMenu's fallback) is not a live target. No-op with a reason.
        string hudMenu = ModsMenu.DescribeHudMenuType();
        if (ModsUiArgs.NativeTabRequiresMenu(string.Equals(hudMenu, HUDMenuType.FullscreenMenu.ToString(), StringComparison.Ordinal)))
            return (false, ModsUiArgs.NativeTabNoMenuDetail(hudMenu));
        var menu = TryFullscreenMenu();
        if (menu == null)
            return (false, new { error = "no FullscreenMenu (native tabs exist only in the connected HUD)", readback = "next-drain" });
        var group = menu.OuterTabGroup;
        if (group == null)
            return (false, new { error = "FullscreenMenu.OuterTabGroup is missing", readback = "next-drain" });
        var tabs = group.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true);
        if (tabs == null || tabs.Length == 0)
            return (false, new { error = "no FullscreenMenu_OuterTabEntry children", readback = "next-drain" });

        FullscreenMenuState? current = null;
        try { current = menu.CurrentView.State; } catch { }

        FullscreenMenu_OuterTabEntry? target = null;
        if (request.Same)
        {
            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] == null) continue;
                if (current != null && tabs[i].State != current.Value) continue;
                target = tabs[i];
                break;
            }
        }
        else if (request.TabIndex != null)
        {
            if (request.TabIndex.Value < 0 || request.TabIndex.Value >= tabs.Length)
                return (false, new { error = "args.tab index " + request.TabIndex.Value + " is out of range (0.." + (tabs.Length - 1) + ")", readback = "next-drain" });
            target = tabs[request.TabIndex.Value];
        }
        else
        {
            string want = (request.Tab ?? "").Trim();
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == null) continue;
                if (request.Same == false && current != null && tab.State == current.Value) continue;
                if (TabMatches(tab, want)) { target = tab; break; }
            }
            if (target == null)
            {
                for (int i = 0; i < tabs.Length; i++)
                {
                    if (tabs[i] != null && TabMatches(tabs[i], want)) { target = tabs[i]; break; }
                }
            }
        }

        if (target == null)
            return (false, new { error = "native tab not found", same = request.Same, tab = request.Tab, readback = "next-drain" });

        var es = EventSystem.current;
        if (es == null)
            return (false, new { error = "EventSystem.current is null; native pointer path unavailable", readback = "next-drain" });
        var ev = new PointerEventData(es) { button = PointerEventData.InputButton.Left };
        target.OnPointerClick(ev);
        var trigger = target.GetComponent<EventTrigger>();
        trigger?.OnPointerClick(ev);
        return (true, new
        {
            tab = target.State.ToString(),
            same = request.Same,
            handler = "OuterTabEntry.OnPointerClick+EventTrigger.OnPointerClick",
            readback = "next-drain",
        });
    }

    private static bool TabMatches(FullscreenMenu_OuterTabEntry tab, string want)
    {
        if (string.IsNullOrEmpty(want)) return false;
        if (string.Equals(tab.State.ToString(), want, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var tmp = tab.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)
                && tmp.text.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch { }
        return string.Equals(tab.gameObject.name, want, StringComparison.OrdinalIgnoreCase);
    }

    private static FullscreenMenu? TryFullscreenMenu()
    {
        try
        {
            var world = Progenitor.Worlds.Client;
            if (world != null && world.IsCreated)
            {
                var hud = OpenHUDMenuSystem.Get(world);
                var found = hud?.CurrentMenuType == HUDMenuType.FullscreenMenu
                    ? hud.CurrentMenu?.TryCast<FullscreenMenu>() : null;
                if (found != null) return found;
                var mapper = world.GetExistingSystemManaged<FullscreenMenuMapper>();
                if (mapper != null && mapper._Menu != null) return mapper._Menu;
            }
        }
        catch { }
        return null;
    }

    private static (bool ok, object detail) DoResolution(ModsUiArgs.Request request)
    {
        Screen.SetResolution(request.Width, request.Height, WriteMode(request.Mode!));
        return (true, new Dictionary<string, object?>
        {
            ["requestedWidth"] = request.Width,
            ["requestedHeight"] = request.Height,
            ["requestedMode"] = request.Mode,
            ["readback"] = "next-drain",
            ["note"] = "after.width/height/mode are Screen readback on the next Drain; detail.window is the OS client-area readback; the caller restores the captured baseline",
        });
    }

    private static (bool ok, object detail) DoWindow()
    {
        try
        {
            var window = ReadWindowPayload();
            window["readback"] = "same-drain";
            return (true, window);
        }
        catch (Exception e)
        {
            var empty = ModsUiResult.EmptyWindow();
            empty["readback"] = "same-drain";
            empty["error"] = e.Message;
            return (false, empty);
        }
    }

    /// <summary>
    /// Read-only pointer probe. Never clicks, focuses, or writes Unity.
    /// </summary>
    /// <summary>RADIOCLICK1: <see cref="ModsMenu.ClickAt"/>; ok only when a handler took the click.</summary>
    private static (bool ok, object detail) DoClickAt(ModsUiArgs.Request request)
    {
        var described = ModsMenu.ClickAt(request.X ?? 0f, request.Y ?? 0f, request.Label);
        var detail = described as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        detail["readback"] = "same-drain";
        bool ok = detail.TryGetValue("invoked", out var invoked) && invoked is true;
        return (ok, detail);
    }

    private static (bool ok, object detail) DoHit(ModsUiArgs.Request request)
    {
        try
        {
            float x = request.X ?? 0f;
            float y = request.Y ?? 0f;
            var described = ModsMenu.DescribePointerHit(x, y);
            var detail = described as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            if (described != null && detail.Count == 0)
            {
                try
                {
                    var json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
                    detail = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                        ?? new Dictionary<string, object?>();
                }
                catch { /* keep empty and still stamp readback */ }
            }
            detail["readback"] = "same-drain";
            bool ok = !detail.TryGetValue("error", out var err) || err is null or "";
            return (ok, detail);
        }
        catch (Exception e)
        {
            var failed = ModsMenu.DescribePointerHit(request.X ?? 0f, request.Y ?? 0f) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
            failed["readback"] = "same-drain";
            failed["error"] = e.Message;
            return (false, failed);
        }
    }

    /// <summary>
    /// Read-only native outer-tab visual probe. Never drives HighlightImage / ForceSelect.
    /// Each native / owned row includes <see cref="ModsUiArgs.TabsGraphicsField"/>.
    /// </summary>
    private static (bool ok, object detail) DoTabs()
    {
        try
        {
            var described = ModsMenu.DescribeOuterTabs();
            var detail = described as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            if (described != null && detail.Count == 0)
            {
                try
                {
                    var json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
                    detail = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                        ?? new Dictionary<string, object?>();
                }
                catch { /* keep empty and still stamp readback */ }
            }
            detail["readback"] = "same-drain";
            bool ok = !detail.TryGetValue("error", out var err) || err is null or "";
            return (ok, detail);
        }
        catch (Exception e)
        {
            return (false, new Dictionary<string, object?>
            {
                ["readback"] = "same-drain",
                ["error"] = e.Message,
            });
        }
    }

    /// <summary>
    /// Read-only FullscreenMenu graphic probe. Never <c>SetActive</c>s.
    /// Same-Drain, depth ≤ 6, 1024-object cap.
    /// </summary>
    private static (bool ok, object detail) DoMenu()
    {
        try
        {
            var described = ModsMenu.DescribeFullscreenMenu();
            var detail = described as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            if (described != null && detail.Count == 0)
            {
                try
                {
                    var json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
                    detail = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                        ?? new Dictionary<string, object?>();
                }
                catch { /* keep empty and still stamp readback */ }
            }
            detail["readback"] = "same-drain";
            bool ok = !detail.TryGetValue("error", out var err) || err is null or "";
            return (ok, detail);
        }
        catch (Exception e)
        {
            return (false, new Dictionary<string, object?>
            {
                ["readback"] = "same-drain",
                ["error"] = e.Message,
            });
        }
    }

    /// <summary>
    /// Read-only title/Play scene probe. Never <c>SetActive</c>s or <c>GoToView</c>s.
    /// </summary>
    private static (bool ok, object detail) DoScene()
    {
        try
        {
            var described = ModsMenu.DescribeMenuScene();
            var detail = described as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            if (described != null && detail.Count == 0)
            {
                try
                {
                    var json = JsonSerializer.Serialize(described, ModsUiResult.JsonOptions);
                    detail = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                        ?? new Dictionary<string, object?>();
                }
                catch { /* keep empty and still stamp readback */ }
            }
            detail["readback"] = "same-drain";
            bool ok = !detail.TryGetValue("error", out var err) || err is null or "";
            return (ok, detail);
        }
        catch (Exception e)
        {
            return (false, new Dictionary<string, object?>
            {
                ["readback"] = "same-drain",
                ["error"] = e.Message,
            });
        }
    }

    private static object ReadHostsForRects()
    {
        try { return ModsMenu.DescribeHostsStatus(); }
        catch (Exception e) { return new Dictionary<string, object?> { ["why"] = "read-threw:" + e.Message }; }
    }

    private static (bool ok, object detail) DoRects()
    {
        try
        {
            int screenWidth = 0, screenHeight = 0;
            try { screenWidth = Screen.width; screenHeight = Screen.height; } catch { }
            var overlays = ReadOwnedOverlays(screenHeight);
            var rects = new List<Dictionary<string, object?>>();

            AddMeasured(rects, "main-menu", "button", "Mods", FindMainMenuButton(), overlays, screenHeight);
            AddMeasured(rects, "top-tab", "button", "Mods", FindNamed("progenitor-ui-core-mods-tab"), overlays, screenHeight);
            AddNativeTabs(rects, overlays, screenHeight);
            AddWorkspaceFrameAndBackdrop(rects, overlays, screenHeight);
            try
            {
                var curtainRow = ModsMenu.DescribeCurtainRect();
                curtainRow["id"] = "curtain";
                curtainRow["kind"] = "curtain";
                rects.Add(curtainRow);
            }
            catch { /* curtain row is best-effort; owned status still reports it */ }
            ClipBackdropToWorkspaceFrame(rects);
            AddTabStrip(rects, overlays, screenHeight);
            AddListingHeader(rects, overlays, screenHeight);
            AddWorkspaceChrome(rects, overlays, screenHeight);
            AddScrollbars(rects, overlays, screenHeight);
            AddContentWidgets(rects, overlays, screenHeight);

            float canvasScale = 1f;
            for (int i = 0; i < rects.Count; i++)
            {
                if (rects[i].TryGetValue("visible", out var vis) && vis is true
                    && rects[i].TryGetValue("canvasScale", out var raw) && raw is float f && f > 0f)
                {
                    canvasScale = f;
                    break;
                }
            }

            string? shortcut = null;
            try { shortcut = ModsUiResult.ReadConfiguredShortcut(); } catch { }

            bool backdropDrawable = false;
            string framePath = "";
            bool nativeFound = false;
            bool titleViewHidden = false;
            bool? titleVideoPlaying = null;
            bool hostedViewHidden = false;
            string outerTabVisual = "";
            int ownedTopTabCount = 0;
            string nativeWhy = "";
            string nativeBackgroundWhy = "";
            string frameExtentWhy = "";
            float sidebarAlpha = 0f, detailAlpha = 0f;
            int firstShowFrame = 0, layoutBeforeShow = 0;
            int nativeCanvasMax = 0;
            // LIST3 item 6: the content pane's layout ("split" | "full-width") and its width.
            string layout = "split";
            float contentWidth = 0f;
            object playBackdrop;
            try { playBackdrop = ModsMenu.DescribePlayBackdrop(); }
            catch { playBackdrop = new Dictionary<string, object?> { ["present"] = false, ["source"] = "none", ["sprite"] = "", ["material"] = "", ["shader"] = "" }; }
            try
            {
                var geometry = ModsMenu.DescribeGeometry();
                var json = JsonSerializer.Serialize(geometry, ModsUiResult.JsonOptions);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("framePath", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                    framePath = pathEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("nativeFound", out var foundEl) && foundEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    nativeFound = foundEl.GetBoolean();
                if (doc.RootElement.TryGetProperty("nativeWhy", out var whyEl) && whyEl.ValueKind == JsonValueKind.String)
                    nativeWhy = whyEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("titleViewHidden", out var hidEl) && hidEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    titleViewHidden = hidEl.GetBoolean();
                if (doc.RootElement.TryGetProperty("hostedViewHidden", out var hostEl) && hostEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    hostedViewHidden = hostEl.GetBoolean();
                if (doc.RootElement.TryGetProperty("outerTabVisual", out var visEl) && visEl.ValueKind == JsonValueKind.String)
                    outerTabVisual = visEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("sidebarAlpha", out var sa) && sa.TryGetSingle(out var sav))
                    sidebarAlpha = sav;
                if (doc.RootElement.TryGetProperty("detailAlpha", out var da) && da.TryGetSingle(out var dav))
                    detailAlpha = dav;
                if (doc.RootElement.TryGetProperty("layout", out var layoutEl) && layoutEl.ValueKind == JsonValueKind.String)
                    layout = layoutEl.GetString() ?? "split";
                if (doc.RootElement.TryGetProperty("contentWidth", out var cw) && cw.TryGetSingle(out var cwv))
                    contentWidth = cwv;
                if (doc.RootElement.TryGetProperty("firstShowFrame", out var fs) && fs.TryGetInt32(out var fsv))
                    firstShowFrame = fsv;
                if (doc.RootElement.TryGetProperty("layoutBeforeShow", out var lb) && lb.TryGetInt32(out var lbv))
                    layoutBeforeShow = lbv;
            }
            catch { }
            try
            {
                var bg = ModsMenu.WorkspaceNativeBackground();
                nativeBackgroundWhy = bg.Why ?? "";
            }
            catch { }
            try { frameExtentWhy = ModsMenu.WorkspaceFrameExtentWhy() ?? ""; }
            catch { }
            try { titleViewHidden = ModsMenu.TitleViewIsHidden; } catch { }
            try { titleVideoPlaying = ModsMenu.TitleVideoPlaying; } catch { }
            try { hostedViewHidden = ModsMenu.HostedViewIsHidden; } catch { }
            try { outerTabVisual = ModsMenu.OuterTabVisual ?? ""; } catch { }
            try { ownedTopTabCount = ModsMenu.OwnedTopTabCount; } catch { }
            try { nativeCanvasMax = ModsMenu.NativeCanvasMaxBelowConsent(); } catch { }
            ReadRoute(out var route, out var nativeRoute);
            nativeRoute ??= ModsUiResult.WithRoute(new Dictionary<string, object?>(), route, null)["nativeRoute"] as Dictionary<string, object?>;
            try
            {
                for (int i = 0; i < rects.Count; i++)
                {
                    if (rects[i].TryGetValue("id", out var id) && id is "backdrop"
                        && rects[i].TryGetValue("drawable", out var draw) && draw is true)
                    {
                        backdropDrawable = true;
                        break;
                    }
                }
            }
            catch { }

            return (true, new Dictionary<string, object?>
            {
                ["readback"] = "same-drain",
                ["origin"] = ModsUiGeometry.ClientOrigin,
                ["unityOrigin"] = ModsUiGeometry.UnityOrigin,
                ["screenWidth"] = screenWidth,
                ["screenHeight"] = screenHeight,
                ["canvasScale"] = canvasScale,
                ["shortcut"] = shortcut,
                ["framePath"] = framePath,
                ["nativeFound"] = nativeFound,
                ["backdropDrawable"] = backdropDrawable,
                ["blockerSorting"] = ModsMenu.BlockerSorting,
                ["backdropSorting"] = ModsMenu.BackdropSorting,
                ["panelsSorting"] = ModsMenu.PanelsSorting,
                ["titleViewHidden"] = titleViewHidden,
                ["titleVideoPlaying"] = titleVideoPlaying,
                ["hostedViewHidden"] = hostedViewHidden,
                ["outerTabVisual"] = outerTabVisual,
                ["ownedTopTabCount"] = ownedTopTabCount,
                ["route"] = route,
                ["nativeRoute"] = nativeRoute,
                ["nativeWhy"] = nativeWhy,
                ["nativeBackgroundWhy"] = nativeBackgroundWhy,
                ["frameExtentWhy"] = frameExtentWhy,
                ["sidebarAlpha"] = sidebarAlpha,
                ["detailAlpha"] = detailAlpha,
                ["layout"] = layout,
                ["contentWidth"] = contentWidth,
                ["firstShowFrame"] = firstShowFrame,
                ["layoutBeforeShow"] = layoutBeforeShow,
                ["nativeCanvasMax"] = nativeCanvasMax,
                ["playBackdrop"] = playBackdrop,
                // HOSTS1 round 8: windowRect / tabBandRect / vanilla window in screen pixels (hosts.windowRects).
                ["hosts"] = ReadHostsForRects(),
                ["rects"] = rects,
            });
        }
        catch (Exception e)
        {
            string? shortcut = null;
            try { shortcut = ModsUiResult.ReadConfiguredShortcut(); } catch { }
            object playBackdropFail;
            try { playBackdropFail = ModsMenu.DescribePlayBackdrop(); }
            catch
            {
                playBackdropFail = new Dictionary<string, object?>
                {
                    ["present"] = false,
                    ["source"] = "none",
                    ["sprite"] = "",
                    ["material"] = "",
                    ["shader"] = "",
                };
            }
            return (false, new Dictionary<string, object?>
            {
                ["readback"] = "same-drain",
                ["origin"] = ModsUiGeometry.ClientOrigin,
                ["unityOrigin"] = ModsUiGeometry.UnityOrigin,
                ["shortcut"] = shortcut,
                ["error"] = e.Message,
                ["playBackdrop"] = playBackdropFail,
                ["rects"] = Array.Empty<object>(),
            });
        }
    }

    private sealed class OverlayHit
    {
        internal bool Present;
        internal int X, Y, Width, Height;
        internal int SortingOrder;
        internal int SiblingOrder;

        internal ModsUiGeometry.OverlayLayer AsLayer(string kind) => Present
            ? ModsUiGeometry.OverlayLayer.Of(kind, X, Y, Width, Height, SortingOrder, SiblingOrder)
            : ModsUiGeometry.OverlayLayer.None;
    }

    private sealed class OwnedOverlays
    {
        internal OverlayHit Curtain { get; init; } = new();
    }

    private static OwnedOverlays ReadOwnedOverlays(int screenHeight)
    {
        var overlays = new OwnedOverlays();
        try
        {
            var backdrop = FindNamed("progenitor-ui-core-mods-background");
            var curtain = backdrop == null ? null : FindChildNamed(backdrop.transform, "curtain");
            FillOverlay(overlays.Curtain, curtain, screenHeight);
        }
        catch { }
        return overlays;
    }

    private static void FillOverlay(OverlayHit hit, GameObject? go, int screenHeight)
    {
        if (go == null || !go.activeInHierarchy) return;
        var measured = MeasureTransform(go, screenHeight, selfIsCurtain: false, new OwnedOverlays());
        if (measured.TryGetValue("visible", out var vis) && vis is true)
        {
            hit.Present = true;
            hit.X = (int)(measured["x"] ?? 0);
            hit.Y = (int)(measured["y"] ?? 0);
            hit.Width = (int)(measured["width"] ?? 0);
            hit.Height = (int)(measured["height"] ?? 0);
            ReadCanvasOrder(go, out hit.SortingOrder, out hit.SiblingOrder);
        }
    }

    private static GameObject? FindChildNamed(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child != null && child.name == name) return child.gameObject;
        }
        return null;
    }

    private static void AddMeasured(
        List<Dictionary<string, object?>> rects,
        string id,
        string kind,
        string? label,
        GameObject? go,
        OwnedOverlays overlays,
        int screenHeight,
        string? plugin = null,
        int? index = null,
        bool selfIsCurtain = false,
        bool includeControls = false,
        string? align = null)
    {
        try
        {
            var rect = MeasureTransform(go, screenHeight, selfIsCurtain, overlays);
            object? controls = null;
            if (includeControls && go != null)
                controls = ReadSubControls(go, overlays, screenHeight);
            var annotated = ModsUiGeometry.Annotate(rect, id, kind, label, plugin, index, controls);
            ModsUiGeometry.WriteAlign(annotated, align);
            ModsUiResult.AttachTmp(annotated, ModsMenu.ReadGameObjectTmp(go));
            rects.Add(annotated);
        }
        catch
        {
            var missing = ModsUiGeometry.Annotate(ModsUiGeometry.MissingRect(), id, kind, label, plugin, index);
            ModsUiGeometry.WriteAlign(missing, align);
            ModsUiResult.AttachTmp(missing, ModsMenu.ReadGameObjectTmp(go));
            rects.Add(missing);
        }
    }

    private static List<Dictionary<string, object?>> ReadSubControls(
        GameObject go, OwnedOverlays overlays, int screenHeight)
    {
        var controls = new List<Dictionary<string, object?>>();
        try
        {
            var input = go.GetComponentInChildren<TMP_InputField>(true);
            if (input != null && input.gameObject != go)
                controls.Add(ModsUiGeometry.Annotate(
                    MeasureTransform(input.gameObject, screenHeight, false, overlays),
                    "input", "input", input.name));
        }
        catch { }
        try
        {
            var toggle = go.GetComponentInChildren<Toggle>(true);
            if (toggle != null && toggle.gameObject != go)
                controls.Add(ModsUiGeometry.Annotate(
                    MeasureTransform(toggle.gameObject, screenHeight, false, overlays),
                    "toggle", "toggle", toggle.name));
        }
        catch { }
        try
        {
            var buttons = go.GetComponentsInChildren<Button>(true);
            if (buttons != null)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    var button = buttons[i];
                    if (button == null || button.gameObject == go) continue;
                    string text = ReadText(button.gameObject);
                    if (text.StartsWith("Apply ", StringComparison.Ordinal))
                        controls.Add(ModsUiGeometry.Annotate(
                            MeasureTransform(button.gameObject, screenHeight, false, overlays),
                            "apply", "button", text));
                }
            }
        }
        catch { }
        return controls;
    }

    private static Dictionary<string, object?> MeasureTransform(
        GameObject? go, int screenHeight, bool selfIsCurtain, OwnedOverlays overlays)
    {
        if (go == null)
            return ModsUiGeometry.MissingRect();
        try
        {
            bool visible = go.activeInHierarchy;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null)
                return ModsUiGeometry.FromUnityScreen(0, 0, 0, 0, screenHeight, 1f, visible, raycastTarget: false);

            float canvasScale = 1f;
            try
            {
                var canvas = go.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.scaleFactor > 0f) canvasScale = canvas.scaleFactor;
            }
            catch { }

            Rect local = rt.rect;
            Vector3 worldMin = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
            Vector3 worldMax = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
            Vector2 a = RectTransformUtility.WorldToScreenPoint(null, worldMin);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(null, worldMax);
            float ux = Math.Min(a.x, b.x);
            float uy = Math.Min(a.y, b.y);
            float uw = Math.Abs(a.x - b.x);
            float uh = Math.Abs(a.y - b.y);

            ModsUiGeometry.UnityScreenToClient(ux, uy, uw, uh, screenHeight, out int x, out int y, out int w, out int h);
            ReadCanvasOrder(go, out int sortingOrder, out int siblingOrder);
            var (occludedBy, occluderOrder) = ModsUiGeometry.Occlusion(
                x, y, w, h, sortingOrder, siblingOrder,
                overlays.Curtain.AsLayer(ModsUiGeometry.OccludedByCurtain),
                selfIsCurtain);
            bool raycast = visible && IsRaycastTarget(go);
            return ModsUiGeometry.FromUnityScreen(
                ux, uy, uw, uh, screenHeight, canvasScale, visible, raycast, occludedBy, sortingOrder, occluderOrder);
        }
        catch
        {
            return ModsUiGeometry.MissingRect();
        }
    }

    private static void ReadCanvasOrder(GameObject go, out int sortingOrder, out int siblingOrder)
    {
        sortingOrder = 0;
        siblingOrder = 0;
        try
        {
            var canvas = go.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            sortingOrder = canvas.sortingOrder;
            siblingOrder = canvas.transform.GetSiblingIndex();
        }
        catch { }
    }

    private static bool IsRaycastTarget(GameObject go)
    {
        try
        {
            var graphic = go.GetComponent<Graphic>();
            if (graphic == null) graphic = go.GetComponentInChildren<Graphic>(true);
            if (graphic == null || !graphic.raycastTarget || !graphic.enabled) return false;
            var selectable = go.GetComponent<Selectable>();
            if (selectable == null) selectable = go.GetComponentInChildren<Selectable>(true);
            if (selectable != null && !selectable.interactable) return false;
            var group = go.GetComponentInParent<CanvasGroup>();
            if (group != null && !group.blocksRaycasts) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddNativeTabs(List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        try
        {
            var menu = TryFullscreenMenu();
            var group = menu?.OuterTabGroup;
            var tabs = group == null ? null : group.GetComponentsInChildren<FullscreenMenu_OuterTabEntry>(true);
            if (tabs == null) return;
            for (int i = 0; i < tabs.Length; i++)
            {
                var tab = tabs[i];
                if (tab == null) continue;
                string name = tab.State.ToString();
                try
                {
                    var tmp = tab.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (tmp != null && !string.IsNullOrEmpty(tmp.text)) name = tmp.text;
                }
                catch { }
                AddMeasured(rects, "native-tab", "tab", name, tab.gameObject, overlays, screenHeight, index: i);
            }
        }
        catch { }
    }

    private static void AddWorkspaceFrameAndBackdrop(
        List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        var root = FindNamed(ModsMenu.WorkspaceRootName);
        var holder = root == null ? null : FindChildNamed(root.transform, ModsMenu.WorkspaceHolderName);
        var imageGo = holder == null ? null : FindChildNamed(holder.transform, ModsMenu.WorkspaceBackdropName);
        AddMeasured(rects, "workspace-frame", "frame", "Mods workspace", holder, overlays, screenHeight);
        AddMeasured(rects, "backdrop", "image", "mods-background", imageGo, overlays, screenHeight);
        if (rects.Count >= 2)
            WriteBackdropDrawability(rects[rects.Count - 1], imageGo, holder);

        GameObject? nativeGo = null;
        try
        {
            var menu = TryFullscreenMenu();
            nativeGo = menu?.ViewParent == null ? null : menu.ViewParent.gameObject;
        }
        catch { }
        AddMeasured(rects, "native-container", "container", "ViewParent", nativeGo, overlays, screenHeight);
        if (rects.Count > 0)
        {
            var row = rects[rects.Count - 1];
            bool found = nativeGo != null;
            try { found = nativeGo != null && nativeGo.activeInHierarchy; } catch { }
            row["found"] = found;
            if (!found)
            {
                string why = "no-viewparent";
                try
                {
                    var geometry = ModsMenu.DescribeGeometry();
                    var json = JsonSerializer.Serialize(geometry, ModsUiResult.JsonOptions);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("nativeWhy", out var whyEl)
                        && whyEl.ValueKind == JsonValueKind.String)
                    {
                        string reported = whyEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(reported)) why = reported;
                    }
                }
                catch { }
                row["why"] = why;
            }
            else row["why"] = "";
        }

        AddNativeBackground(rects, overlays, screenHeight);
    }

    private static void AddNativeBackground(
        List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        GameObject? bgGo = null;
        string label = "BackgroundImage";
        try
        {
            bgGo = FindNativeBackgroundImage(out label);
        }
        catch { }
        AddMeasured(rects, "native-background", "image", label, bgGo, overlays, screenHeight);
        if (rects.Count == 0) return;
        var row = rects[rects.Count - 1];
        bool found = false;
        string why = "no-background";
        try
        {
            var bg = ModsMenu.WorkspaceNativeBackground();
            found = bg.Found;
            why = string.IsNullOrEmpty(bg.Why) ? why : bg.Why;
        }
        catch { }
        ModsUiGeometry.WriteFoundWhy(row, found, why);
        if (bgGo != null || !found) return;
        Dictionary<string, object?>? frame = null;
        for (int i = 0; i < rects.Count; i++)
        {
            if (rects[i].TryGetValue("id", out var id) && id is "workspace-frame")
            {
                frame = rects[i];
                break;
            }
        }

        if (frame != null)
            ModsUiGeometry.CopyClientRect(row, frame);
    }

    private static GameObject? FindNativeBackgroundImage(out string label)
    {
        label = "BackgroundImage";
        var menu = TryFullscreenMenu();
        if (menu == null) return null;
        FullscreenMenuView? view = null;
        try { view = menu.CurrentView?.Instance; } catch { view = null; }
        GameObject? typed = ImageFromTypedView(view, out label);
        if (typed != null) return typed;
        try
        {
            if (view != null && view.gameObject != null)
            {
                var named = FindNamedBackground(view.gameObject.transform);
                if (named != null)
                {
                    label = named.name ?? label;
                    return named;
                }
            }
        }
        catch { }
        try
        {
            if (menu.ViewParent != null)
            {
                var under = FindNamedBackground(menu.ViewParent);
                if (under != null)
                {
                    label = under.name ?? label;
                    return under;
                }
            }
        }
        catch { }

        return null;
    }

    private static GameObject? ImageFromTypedView(FullscreenMenuView? view, out string label)
    {
        label = "BackgroundImage";
        if (view == null) return null;
        try
        {
            var spell = view.TryCast<SpellbookMenu>();
            if (spell != null && spell.BackgroundImage != null)
            {
                label = "BackgroundImage";
                return spell.BackgroundImage.gameObject;
            }
        }
        catch { }

        try
        {
            var power = view.TryCast<PowerSelectionMenu>();
            if (power != null && power.Background != null)
            {
                label = "Background";
                var go = ImageFromFadeSwap(power.Background);
                if (go != null) return go;
            }
        }
        catch { }

        try
        {
            var vblood = view.TryCast<VBloodTrackingMenu>();
            if (vblood != null && vblood.BackgroundImage != null)
            {
                label = "BackgroundImage";
                var go = ImageFromFadeSwap(vblood.BackgroundImage);
                if (go != null) return go;
            }
        }
        catch { }

        return null;
    }

    private static GameObject? ImageFromFadeSwap(ImageFadeSwap? swap)
    {
        if (swap == null) return null;
        try { if (swap.CurrentImage != null) return swap.CurrentImage.gameObject; } catch { }
        try { if (swap.PreviousImage != null) return swap.PreviousImage.gameObject; } catch { }
        try { return swap.gameObject; } catch { return null; }
    }

    private static GameObject? FindNamedBackground(Transform? root)
    {
        if (root == null) return null;
        GameObject? found = null;
        WalkNamedBackground(root, 0, ref found);
        return found;
    }

    private static void WalkNamedBackground(Transform t, int depth, ref GameObject? found)
    {
        if (found != null || t == null || depth > 6) return;
        string name = "";
        try { name = t.name ?? ""; } catch { }
        if (string.Equals(name, "Background", StringComparison.Ordinal)
            || string.Equals(name, "BackgroundImage", StringComparison.Ordinal))
        {
            found = t.gameObject;
            return;
        }

        int n = 0;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n && found == null; i++)
        {
            Transform? child = null;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            WalkNamedBackground(child, depth + 1, ref found);
        }
    }

    private static void ClipBackdropToWorkspaceFrame(List<Dictionary<string, object?>> rects)
    {
        Dictionary<string, object?>? frame = null;
        Dictionary<string, object?>? backdrop = null;
        for (int i = 0; i < rects.Count; i++)
        {
            if (rects[i].TryGetValue("id", out var id) && id is string s)
            {
                if (s == "workspace-frame") frame = rects[i];
                else if (s == "backdrop") backdrop = rects[i];
            }
        }
        if (frame == null || backdrop == null) return;
        ModsUiGeometry.ClipRectTo(backdrop, frame);
    }

    private static void WriteBackdropDrawability(
        Dictionary<string, object?> dest, GameObject? imageGo, GameObject? holder)
    {
        bool assigned = false, active = false, enabled = false;
        float alpha = 0f, rw = 0f, rh = 0f;
        int mesh = 0;
        try
        {
            if (imageGo != null)
            {
                assigned = true;
                try { active = imageGo.activeInHierarchy; } catch { }
                var image = imageGo.GetComponent<Image>();
                if (image != null)
                {
                    try { enabled = image.enabled; } catch { }
                    try { alpha = image.color.a; } catch { }
                    try { mesh = image.sprite != null ? 1 : 0; } catch { }
                }
                var rt = imageGo.GetComponent<RectTransform>();
                float ix = 0f, iy = 0f, iw = 0f, ih = 0f;
                if (rt != null)
                {
                    try
                    {
                        var r = rt.rect;
                        ix = r.x;
                        iy = r.y;
                        iw = r.width;
                        ih = r.height;
                    }
                    catch { }
                }
                float hx = 0f, hy = 0f, hw = iw, hh = ih;
                if (holder != null)
                {
                    try
                    {
                        var ht = holder.GetComponent<RectTransform>();
                        if (ht != null)
                        {
                            var hr = ht.rect;
                            hx = hr.x;
                            hy = hr.y;
                            hw = hr.width;
                            hh = hr.height;
                        }
                    }
                    catch { }
                }
                ModsMenu.WorkspaceClippedVisibleRect(ix, iy, iw, ih, hx, hy, hw, hh, out _, out _, out rw, out rh);
            }
        }
        catch { }
        bool drawable = ModsMenu.WorkspaceBackdropIsDrawable(
            assigned, active, enabled, alpha, rw, rh,
            ModsMenu.BackdropSorting, ModsMenu.PanelsSorting, mesh);
        dest["drawable"] = drawable;
        dest["assigned"] = assigned;
        dest["graphicEnabled"] = enabled;
        dest["alpha"] = alpha;
        dest["meshCount"] = mesh;
        dest["behindPanels"] = drawable;
        dest["sortingOrder"] = ModsMenu.BackdropSorting;
    }

    /// <summary>
    /// Listing header rows, measured from the live objects through
    /// <see cref="AddMeasured"/> like every other rect (client pixels, not the
    /// canvas-space numbers gen 39 reported): one <c>header</c> band, one
    /// <c>viewport</c>, the title as <c>kind: label</c>, and the action button(s)
    /// as <c>id: action</c> / <c>kind: button</c> labeled with the caption
    /// (index 0 primary, 1 the secondary Remove) plus the reason note.
    /// </summary>
    private static void AddListingHeader(List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        Dictionary<string, GameObject?> objects;
        try { objects = ModsMenu.ListingHeaderObjects(); }
        catch { return; }
        if (objects == null || objects.Count == 0) return;
        objects.TryGetValue("band", out var band);
        objects.TryGetValue("viewport", out var viewport);
        objects.TryGetValue("title", out var title);
        objects.TryGetValue("action", out var action);
        objects.TryGetValue("remove", out var remove);
        objects.TryGetValue("reason", out var reason);
        AddMeasured(rects, "header", "header", "header", band, overlays, screenHeight);
        AddMeasured(rects, "viewport", "viewport", "viewport", viewport, overlays, screenHeight);
        AddMeasured(rects, "header-title", "label", title == null ? "" : ReadText(title), title, overlays, screenHeight);
        AddMeasured(rects, "action", "button", action == null ? "" : ReadText(action), action, overlays, screenHeight, index: 0);
        if (remove != null)
            AddMeasured(rects, "action", "button", ReadText(remove), remove, overlays, screenHeight, index: 1);
        if (reason != null)
            AddMeasured(rects, "header-reason", "note", ReadText(reason), reason, overlays, screenHeight);
    }

    private static void AddTabStrip(List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        (string Name, bool Selected)[] rows;
        try { rows = ModsMenu.WorkspaceStripButtons(); }
        catch { rows = Array.Empty<(string Name, bool Selected)>(); }
        if (rows.Length == 0) return;

        bool shown = false;
        try { shown = ModsMenu.WorkspaceStripIsShown; } catch { }

        for (int i = 0; i < rows.Length; i++)
        {
            string name = rows[i].Name;
            bool selected = rows[i].Selected;
            var go = FindStripButton(name);
            AddMeasured(rects, ModsUiGeometry.TabStripId, ModsUiGeometry.TabStripKind, name, go, overlays, screenHeight, index: i);
            if (rects.Count == 0) continue;
            var row = ModsUiGeometry.TabStripRow(rects[rects.Count - 1], name, selected);
            if (!shown)
            {
                row["visible"] = false;
                row["interactable"] = false;
            }
        }
    }

    private static GameObject? FindStripButton(string caption)
    {
        if (string.IsNullOrEmpty(caption)) return null;
        try
        {
            var tmps = UnityObject.FindObjectsOfType<TextMeshProUGUI>(true);
            if (tmps == null) return null;
            for (int i = 0; i < tmps.Length; i++)
            {
                var tmp = tmps[i];
                if (tmp == null || !string.Equals(tmp.text, caption, StringComparison.Ordinal)) continue;
                if (!IsOwnedUi(tmp.transform) || !IsUnderNamed(tmp.transform, ModsMenu.StripRootName))
                    continue;
                return HitTarget(tmp.gameObject);
            }
        }
        catch { }
        return null;
    }

    private static bool IsUnderNamed(Transform transform, string name)
    {
        for (var t = transform; t != null; t = t.parent)
        {
            if (t != null && t.name == name) return true;
        }

        return false;
    }

    private static void AddWorkspaceChrome(List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        object geometry;
        try { geometry = ModsMenu.DescribeGeometry(); }
        catch { geometry = new { }; }

        string search = ReadGeometryString(geometry, "search") ?? "";
        var searchGo = FindOwnedControl(ModsMenu.SearchPlaceholder, wantInput: true)
            ?? FindOwnedControl("Search", wantInput: true);
        string renderedWord = ReadNamedTmp(searchGo, ModsMenu.SearchHintWordName);
        string renderedGlyph = ReadNamedTmp(searchGo, ModsMenu.SearchHintGlyphName);
        if (string.IsNullOrEmpty(renderedGlyph) && HasNamedChild(searchGo, ModsMenu.SearchHintGlyphName))
            renderedGlyph = "(icon)";
        // Label is the typed value when present, otherwise the hint TMP's own text — never a
        // constant substituted when that TMP is blank (that is how an empty box went green).
        string searchLabel = !string.IsNullOrEmpty(search) ? search : renderedWord;
        AddMeasured(rects, "search", "input", searchLabel, searchGo, overlays, screenHeight, includeControls: true);
        if (rects.Count > 0)
        {
            var row = rects[rects.Count - 1];
            row["renderedPlaceholder"] = renderedWord;
            row["renderedGlyph"] = renderedGlyph;
            WriteHintDrawability(row, searchGo, ModsMenu.SearchHintWordName, "placeholder",
                assigned: !string.IsNullOrEmpty(renderedWord));
            WriteHintDrawability(row, searchGo, ModsMenu.SearchHintGlyphName, "glyph",
                assigned: !string.IsNullOrEmpty(renderedGlyph));
            WriteCaretDrawability(row, searchGo, overlays, screenHeight);
            // Job KEY2. Owned focus, the same answer status.focus reports — not TMP's isFocused,
            // which a readOnly field may never set (that is how the Install search took keys with
            // no field). caretFocused above is the caret's question and stays as it was.
            row["focused"] = FocusedFieldIs(ModsMenu.MenuHotkeyModsSearch);
        }

        var closeGo = FindOwnedControl("Close Mods", wantInput: false);
        var backGo = FindOwnedPrefixed("Back to ");
        if (backGo != null)
            AddMeasured(rects, "close-back", "button", ReadText(backGo), backGo, overlays, screenHeight);
        else
            AddMeasured(rects, "close-back", "button", "Close Mods", closeGo, overlays, screenHeight);

        bool haveViewport = TryMeasureListViewport(screenHeight, overlays, out int vx, out int vy, out int vw, out int vh);
        try
        {
            var json = JsonSerializer.Serialize(geometry, ModsUiResult.JsonOptions);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sidebar", out var sidebar) && sidebar.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var row in sidebar.EnumerateArray())
                {
                    string id = row.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                    string name = row.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() ?? "" : "";
                    string caption = row.TryGetProperty("caption", out var capEl) && capEl.ValueKind == JsonValueKind.String
                        ? capEl.GetString() ?? name : name;
                    var go = FindOwnedControl(name, wantInput: false)
                        ?? FindOwnedControl(caption, wantInput: false);
                    AddMeasured(rects, "sidebar-row", "button", name, go, overlays, screenHeight, plugin: id, index: index);
                    if (haveViewport && rects.Count > 0)
                        ModsUiGeometry.ClipToViewport(rects[rects.Count - 1], vx, vy, vw, vh);
                    index++;
                }
            }
        }
        catch { }
    }

    private static bool TryMeasureListViewport(
        int screenHeight, OwnedOverlays overlays, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        try
        {
            var scroll = FindPaneScroll("list");
            var viewport = scroll == null ? null : scroll.viewport;
            var go = viewport == null ? null : viewport.gameObject;
            if (go == null) return false;
            var measured = MeasureTransform(go, screenHeight, false, overlays);
            if (measured.TryGetValue("visible", out var vis) && vis is false) return false;
            x = Convert.ToInt32(measured["x"] ?? 0, CultureInfo.InvariantCulture);
            y = Convert.ToInt32(measured["y"] ?? 0, CultureInfo.InvariantCulture);
            width = Convert.ToInt32(measured["width"] ?? 0, CultureInfo.InvariantCulture);
            height = Convert.ToInt32(measured["height"] ?? 0, CultureInfo.InvariantCulture);
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadGeometryString(object geometry, string name)
    {
        try
        {
            var json = JsonSerializer.Serialize(geometry, ModsUiResult.JsonOptions);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }

    private static void AddScrollbars(List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        AddScrollbar(rects, "list", overlays, screenHeight);
        AddScrollbar(rects, "content", overlays, screenHeight);
    }

    private static void AddScrollbar(List<Dictionary<string, object?>> rects, string pane, OwnedOverlays overlays, int screenHeight)
    {
        ScrollRect? scroll = null;
        try { scroll = FindPaneScroll(pane); } catch { }
        Scrollbar? bar = null;
        try { bar = scroll == null ? null : scroll.verticalScrollbar; } catch { }
        AddMeasured(rects, pane + "-scrollbar-track", "scrollbar-track", pane, bar == null ? null : bar.gameObject, overlays, screenHeight, selfIsCurtain: false);
        RectTransform? handle = null;
        try { handle = bar == null ? null : bar.handleRect; } catch { }
        AddMeasured(rects, pane + "-scrollbar-handle", "scrollbar-handle", pane, handle == null ? null : handle.gameObject, overlays, screenHeight);
    }

    private static void AddContentWidgets(List<Dictionary<string, object?>> rects, OwnedOverlays overlays, int screenHeight)
    {
        try
        {
            if (!ModsMenu.IsOpen) return;
            var rows = ModsMenu.ReadVisibleWidgets("content");
            var json = JsonSerializer.Serialize(rows, ModsUiResult.JsonOptions);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            int index = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                string kind = row.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "widget" : "widget";
                // The listing title is measured by AddListingHeader as kind: label; a second
                // kind: header row here was the gen 39 duplicate.
                if (kind == "header") { index++; continue; }
                string label = row.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
                string align = row.TryGetProperty("align", out var a) && a.ValueKind == JsonValueKind.String
                    ? ModsUiGeometry.NormalizeAlign(a.GetString())
                    : ModsUiGeometry.AlignCentre;
                var go = FindOwnedControl(label, wantInput: kind == "input");
                AddMeasured(rects, "widget", kind, label, go, overlays, screenHeight, index: index, includeControls: true, align: align);
                if (rects.Count > 0 && !rects[rects.Count - 1].ContainsKey("tmp"))
                    ModsUiResult.AttachTmp(rects[rects.Count - 1], ModsUiResult.ReadTmpElement(row));
                index++;
            }
        }
        catch { }
    }

    private static bool IsUnder(Transform transform, Transform ancestor)
    {
        for (var t = transform; t != null; t = t.parent)
        {
            if (ReferenceEquals(t, ancestor)) return true;
        }
        return false;
    }

    private static GameObject? FindOwnedControl(string text, bool wantInput)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            if (wantInput)
            {
                var fields = UnityObject.FindObjectsOfType<TMP_InputField>(true);
                if (fields != null)
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];
                        if (field == null || !IsOwnedUi(field.transform)) continue;
                        var parent = field.transform.parent;
                        if (parent != null
                            && string.Equals(parent.name, ModsMenu.SearchRowName, StringComparison.Ordinal)
                            && (string.Equals(text, ModsMenu.SearchPlaceholder, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(text, "Search", StringComparison.OrdinalIgnoreCase)))
                            return parent.gameObject;
                        if (ReadText(field.gameObject).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                            && ReadText(parent == null ? field.gameObject : parent.gameObject)
                                .IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        return parent != null && (parent.name == "input-row" || parent.name == ModsMenu.SearchRowName)
                            ? parent.gameObject
                            : field.gameObject;
                    }
                }
            }

            var tmps = UnityObject.FindObjectsOfType<TextMeshProUGUI>(true);
            if (tmps == null) return null;
            for (int i = 0; i < tmps.Length; i++)
            {
                var tmp = tmps[i];
                if (tmp == null || string.IsNullOrEmpty(tmp.text) || !IsOwnedUi(tmp.transform)) continue;
                if (!string.Equals(tmp.text, text, StringComparison.Ordinal)
                    && tmp.text.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                return HitTarget(tmp.gameObject);
            }
        }
        catch { }
        return null;
    }

    private static GameObject? FindOwnedPrefixed(string prefix)
    {
        try
        {
            var tmps = UnityObject.FindObjectsOfType<TextMeshProUGUI>(true);
            if (tmps == null) return null;
            for (int i = 0; i < tmps.Length; i++)
            {
                var tmp = tmps[i];
                if (tmp == null || string.IsNullOrEmpty(tmp.text) || !IsOwnedUi(tmp.transform)) continue;
                if (!tmp.text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                return HitTarget(tmp.gameObject);
            }
        }
        catch { }
        return null;
    }

    private static GameObject HitTarget(GameObject textGo)
    {
        for (var t = textGo.transform; t != null; t = t.parent)
        {
            if (t.GetComponent<Button>() != null) return t.gameObject;
            if (t.GetComponent<Toggle>() != null) return t.gameObject;
            if (t.GetComponent<Slider>() != null) return t.gameObject;
            if (t.GetComponent<TMP_InputField>() != null) return t.gameObject;
            string name = t.name ?? "";
            if (name is "input-row" or "toggle-row" or "slider-row" or "button") return t.gameObject;
        }
        return textGo;
    }

    private static string ReadText(GameObject go)
    {
        try
        {
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
        }
        catch { }
        return go.name ?? "";
    }

    private static string ReadNamedTmp(GameObject? root, string childName)
    {
        if (root == null || string.IsNullOrEmpty(childName)) return "";
        try
        {
            var tmps = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (tmps == null) return "";
            for (int i = 0; i < tmps.Length; i++)
            {
                var tmp = tmps[i];
                if (tmp == null) continue;
                try
                {
                    if (!string.Equals(tmp.gameObject.name, childName, StringComparison.Ordinal))
                        continue;
                    return tmp.text ?? "";
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    private static bool HasNamedChild(GameObject? root, string childName)
    {
        if (root == null || string.IsNullOrEmpty(childName)) return false;
        try
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms == null) return false;
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null) continue;
                try
                {
                    if (string.Equals(t.name, childName, StringComparison.Ordinal))
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Drawability of a named hint child. <c>renderedPlaceholder</c> is the TMP
    /// <c>text</c> and is not proof it paints. A live run should read
    /// <c>placeholderDrawable</c> / <c>glyphDrawable</c>: true means assigned,
    /// active, enabled, alpha ≥ <see cref="ModsMenu.SearchHintMinAlpha"/>, a
    /// non-zero rect, in front of the fill, and a non-empty mesh.
    /// </summary>
    private static void WriteHintDrawability(
        Dictionary<string, object?> dest,
        GameObject? searchGo,
        string childName,
        string prefix,
        bool assigned)
    {
        var go = FindNamedChild(searchGo, childName);
        bool active = false;
        bool enabled = false;
        float alpha = 0f;
        float rectW = 0f;
        float rectH = 0f;
        int hintSibling = -1;
        int fillSibling = -1;
        int meshCount = 0;
        string color = "";
        try
        {
            if (go != null)
            {
                try { active = go.activeInHierarchy; } catch { active = false; }
                ReadHintGraphic(go, out enabled, out alpha, out rectW, out rectH, out meshCount, out color);
                ReadHintDrawOrder(go, out hintSibling, out fillSibling);
            }
        }
        catch { }

        bool drawable = ModsMenu.SearchHintIsDrawable(
            assigned, active, enabled, alpha, rectW, rectH, hintSibling, fillSibling, meshCount);
        dest[prefix + "Alpha"] = alpha;
        dest[prefix + "Active"] = active && enabled;
        dest[prefix + "RectW"] = rectW;
        dest[prefix + "RectH"] = rectH;
        dest[prefix + "InFrontOfFill"] = ModsMenu.SearchHintIsNotBehindFill(hintSibling, fillSibling);
        dest[prefix + "MeshCount"] = meshCount;
        dest[prefix + "Color"] = color;
        dest[prefix + "Drawable"] = drawable;
    }

    /// <summary>
    /// Owned caret Graphic. <c>caretDrawable</c> true means focused, assigned,
    /// active, enabled, alpha ≥ the hint floor, a non-zero rect, in front of
    /// the fill, and a non-empty mesh. A still capture of the blink off-phase
    /// reports <c>caretBlinkOn</c> false and <c>caretDrawable</c> false with
    /// the rect still sized. <c>caretX</c> is the Image's anchored x, which
    /// advances with the owned index. <c>caretClientY</c> / <c>caretClientH</c>
    /// and <c>fieldClientY</c> / <c>fieldClientH</c> are the same client-pixel
    /// space as this row's <c>y</c> / <c>height</c>; <c>caretContainedVertically</c>
    /// is false when the bar hangs off the field, which drawable-only missed.
    /// </summary>
    private static void WriteCaretDrawability(
        Dictionary<string, object?> dest, GameObject? searchGo, OwnedOverlays overlays, int screenHeight)
    {
        var go = FindNamedChild(searchGo, ModsMenu.OwnedCaretName);
        bool focused = false;
        int caretIndex = 0;
        bool blinkOn = false;
        bool assigned = go != null;
        bool active = false;
        bool enabled = false;
        float alpha = 0f;
        float rectW = 0f;
        float rectH = 0f;
        float caretX = 0f;
        float caretY = 0f;
        int sibling = -1;
        int fillSibling = -1;
        int meshCount = 0;
        string color = "";
        int caretClientY = 0;
        int caretClientH = 0;
        int fieldClientY = 0;
        int fieldClientH = 0;
        try
        {
            fieldClientY = Convert.ToInt32(dest["y"] ?? 0, CultureInfo.InvariantCulture);
            fieldClientH = Convert.ToInt32(dest["height"] ?? 0, CultureInfo.InvariantCulture);
        }
        catch { }
        try
        {
            var field = FindSearchInput(searchGo);
            if (field != null)
                ModsMenu.TryDescribeOwnedCaret(field, out caretIndex, out focused, out blinkOn);
        }
        catch { }

        try
        {
            if (go != null)
            {
                try { active = go.activeInHierarchy; } catch { active = false; }
                ReadHintGraphic(go, out enabled, out alpha, out rectW, out rectH, out meshCount, out color);
                ReadCaretDrawOrder(go, out sibling, out fillSibling);
                try
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        var pos = rt.anchoredPosition;
                        caretX = pos.x;
                        caretY = pos.y;
                    }
                }
                catch { }
                try
                {
                    var measured = MeasureTransform(go, screenHeight, false, overlays);
                    caretClientY = Convert.ToInt32(measured["y"] ?? 0, CultureInfo.InvariantCulture);
                    caretClientH = Convert.ToInt32(measured["height"] ?? 0, CultureInfo.InvariantCulture);
                }
                catch { }
            }
        }
        catch { }

        dest["caretIndex"] = caretIndex;
        dest["caretFocused"] = focused;
        dest["caretBlinkOn"] = blinkOn;
        dest["caretX"] = caretX;
        dest["caretY"] = caretY;
        dest["caretAlpha"] = alpha;
        dest["caretActive"] = active && enabled;
        dest["caretRectW"] = rectW;
        dest["caretRectH"] = rectH;
        dest["caretClientY"] = caretClientY;
        dest["caretClientH"] = caretClientH;
        dest["fieldClientY"] = fieldClientY;
        dest["fieldClientH"] = fieldClientH;
        dest["caretContainedVertically"] = ModsMenu.OwnedCaretContainedVertically(
            caretClientY, caretClientH, fieldClientY, fieldClientH);
        dest["caretOnTextLine"] = ModsMenu.OwnedCaretOnTextLine(
            caretClientY, caretClientH, fieldClientY + fieldClientH * 0.5f);
        dest["caretInFrontOfFill"] = ModsMenu.SearchHintIsNotBehindFill(sibling, fillSibling);
        dest["caretMeshCount"] = meshCount;
        dest["caretColor"] = color;
        dest["caretDrawable"] = ModsMenu.OwnedCaretIsDrawable(
            focused, assigned, active, enabled, alpha, rectW, rectH, sibling, fillSibling, meshCount);
    }

    private static TMP_InputField? FindSearchInput(GameObject? searchGo)
    {
        if (searchGo == null) return null;
        try { return searchGo.GetComponentInChildren<TMP_InputField>(true); }
        catch { return null; }
    }

    private static void ReadCaretDrawOrder(GameObject go, out int caretSibling, out int fillSibling)
    {
        caretSibling = -1;
        fillSibling = -1;
        try
        {
            var t = go.transform;
            caretSibling = t.GetSiblingIndex();
            var parent = t.parent;
            if (parent == null) return;
            Image? selfFill = null;
            try { selfFill = parent.GetComponent<Image>(); } catch { selfFill = null; }
            if (selfFill != null)
            {
                fillSibling = -1;
                return;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null) continue;
                Image? img = null;
                try { img = child.GetComponent<Image>(); } catch { img = null; }
                if (img != null) fillSibling = i;
            }
        }
        catch { }
    }

    private static GameObject? FindNamedChild(GameObject? root, string childName)
    {
        if (root == null || string.IsNullOrEmpty(childName)) return null;
        try
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms == null) return null;
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null) continue;
                try
                {
                    if (string.Equals(t.name, childName, StringComparison.Ordinal))
                        return t.gameObject;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static void ReadHintGraphic(
        GameObject go,
        out bool enabled,
        out float alpha,
        out float rectW,
        out float rectH,
        out int meshCount,
        out string color)
    {
        enabled = false;
        alpha = 0f;
        rectW = 0f;
        rectH = 0f;
        meshCount = 0;
        color = "";
        Graphic? graphic = null;
        try { graphic = go.GetComponent<Graphic>(); } catch { graphic = null; }
        if (graphic == null)
        {
            try { graphic = go.GetComponentInChildren<Graphic>(true); } catch { graphic = null; }
        }
        if (graphic == null) return;
        try { enabled = graphic.enabled; } catch { enabled = false; }
        try
        {
            var c = graphic.color;
            alpha = c.a;
            color = string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.###}", c.r, c.g, c.b, c.a);
        }
        catch { }
        try
        {
            var cr = graphic.canvasRenderer;
            if (cr != null) alpha = cr.GetAlpha();
        }
        catch { }
        try
        {
            var rt = graphic.rectTransform;
            if (rt != null)
            {
                var rect = rt.rect;
                rectW = rect.width;
                rectH = rect.height;
            }
        }
        catch { }

        try
        {
            var tmp = go.GetComponent<TextMeshProUGUI>() ?? go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && tmp.textInfo != null)
                meshCount = tmp.textInfo.characterCount;
            else if (graphic.TryCast<Image>() != null)
                meshCount = 1;
        }
        catch
        {
            try { if (graphic.TryCast<Image>() != null) meshCount = 1; } catch { /* */ }
        }
    }

    private static void ReadHintDrawOrder(GameObject go, out int hintSibling, out int fillSibling)
    {
        hintSibling = -1;
        fillSibling = -1;
        try
        {
            var hintRoot = go.transform;
            while (hintRoot != null && hintRoot.name != ModsMenu.SearchHintName)
                hintRoot = hintRoot.parent;
            if (hintRoot == null) hintRoot = go.transform.parent;
            if (hintRoot == null) return;
            hintSibling = hintRoot.GetSiblingIndex();
            var field = hintRoot.parent;
            if (field == null) return;
            // Fill Image lives on the field GO itself (not a sibling): -1 means behind every child.
            Image? selfFill = null;
            try { selfFill = field.GetComponent<Image>(); } catch { selfFill = null; }
            if (selfFill != null)
            {
                fillSibling = -1;
                return;
            }
            for (int i = 0; i < field.childCount; i++)
            {
                var child = field.GetChild(i);
                if (child == null) continue;
                Image? img = null;
                try { img = child.GetComponent<Image>(); } catch { img = null; }
                if (img != null) fillSibling = i;
            }
        }
        catch { }
    }

    private static Dictionary<string, object?> ReadWindowPayload()
    {
        int screenWidth = 0, screenHeight = 0;
        string fullScreenMode = "Unknown";
        string mode = "window";
        try
        {
            screenWidth = Screen.width;
            screenHeight = Screen.height;
            fullScreenMode = Screen.fullScreenMode.ToString();
            mode = WindowModeName(Screen.fullScreenMode);
        }
        catch { }

        int primaryWidth = 0, primaryHeight = 0;
        try
        {
            var display = Display.main;
            if (display != null)
            {
                primaryWidth = display.systemWidth;
                primaryHeight = display.systemHeight;
            }
        }
        catch { }
        if (primaryWidth <= 0 || primaryHeight <= 0)
        {
            try
            {
                primaryWidth = GetSystemMetrics(0);
                primaryHeight = GetSystemMetrics(1);
            }
            catch { }
        }

        IntPtr hwnd = IntPtr.Zero;
        try { hwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { }
        if (hwnd == IntPtr.Zero)
        {
            try
            {
                var fg = GetForegroundWindow();
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == (uint)Process.GetCurrentProcess().Id) hwnd = fg;
            }
            catch { }
        }

        int clientWidth = 0, clientHeight = 0, screenX = 0, screenY = 0;
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                if (GetClientRect(hwnd, out var client) && client.Right > client.Left && client.Bottom > client.Top)
                {
                    clientWidth = client.Right - client.Left;
                    clientHeight = client.Bottom - client.Top;
                    var origin = new Win32Point { X = 0, Y = 0 };
                    if (ClientToScreen(hwnd, ref origin))
                    {
                        screenX = origin.X;
                        screenY = origin.Y;
                    }
                }
            }
            catch { }
        }

        return ModsUiResult.Window(
            hwnd == IntPtr.Zero ? "0x0" : "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture),
            clientWidth, clientHeight, screenX, screenY,
            screenWidth, screenHeight, fullScreenMode, mode,
            primaryWidth, primaryHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
#endif
}
