using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Client-side vanilla HUD cloning: a factory that styles Unity UI with vanilla sprites/fonts,
/// a mod-owned overlay canvas, and an explicit input-capture contract. We Instantiate live
/// <c>SimpleStunButton</c> / <c>TextMeshProUGUI</c> / Toggle / slider / <c>TMP_InputField</c>
/// rather than rebuilding uGUI from sprites. Every call is safe on the
/// server: it returns a disabled handle and one log line so a shared plugin does not need
/// a side check. Reload-safe: handles are <see cref="IDisposable"/> and
/// <see cref="RemoveAll"/> destroys that plugin's exact <c>progenitor-ui-&lt;id&gt;</c> root.
/// </summary>
public static partial class Ui
{
    /// <summary>
    /// SkipCoreModules token. Same string as <see cref="HarmonySlotHandoff.UiModule"/>.
    /// </summary>
    public const string ModuleName = HarmonySlotHandoff.UiModule;

    private static readonly object Gate = new();
    private static readonly List<UiHandle> Live = new();

    /// <summary>
    /// Per-plugin capture-mode overrides, keyed by the same sanitized plugin id a handle carries
    /// in <see cref="IUiHandle.PluginId"/> (job FUI19). Dropped with the Core generation, like
    /// every other entry in this class.
    /// </summary>
    private static readonly Dictionary<string, UiCaptureMode> PluginModes = new(StringComparer.Ordinal);

    /// <summary>
    /// The framework-wide default. <see cref="UiCaptureModes.Default"/> unless a plugin or the
    /// client's <c>Ui.CaptureMode</c> config entry moved it.
    /// </summary>
    private static UiCaptureMode _defaultMode = UiCaptureModes.Default;

    private static IUiRuntime? _runtime;
    private static bool _installed;
    private static bool _skipped;
    private static int _shown;
    private static bool _keySlotFilled;

    /// <summary>The 17 (Back) of a press whose 27 (Escape) a capture or a dialog took.</summary>
    private static bool _swallowBack;

    /// <summary>Configure a workspace pane before its rows are added. Ownership stays on the handle.</summary>
    /// <summary>HOSTS1 round 5: see <see cref="IUiRuntime.SetSoftHide"/>.</summary>
    internal static void SetSoftHide(IUiPanel panel, bool soft)
    {
        if (panel is not UiPanelHandle { IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().SetSoftHide(runtimePanel, soft); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: soft hide: " + e.Message); }
    }

    internal static void ConfigureMenuPanel(IUiPanel panel, bool sidebar) =>
        ConfigureMenuPanel(panel, sidebar, MenuPaneMode.Split);

    /// <summary>As above with the pane's <see cref="MenuPaneMode"/> (LIST3: the Install tab is <see cref="MenuPaneMode.FullWidth"/>).</summary>
    internal static void ConfigureMenuPanel(IUiPanel panel, bool sidebar, MenuPaneMode mode)
    {
        // WINDOWS1 live fix: a workspace pane is never a floating panel in the window row, even after it is parked (a
        // parked list is no longer ModsMenu's live pane but stays shown, and was counted in the row: the Header window
        // jumped while the search was typed).
        if (panel is UiPanelHandle pane) pane.Flows = false;
        // The Mods workspace carries one footer — the component versions, on its main pane — and
        // the sidebar none, whatever the Core-owned default put there when the pane was created.
        // KEYS3 live 2026-09-29: the Keybinds tab's description column (Aside) is a second pane too;
        // with its own footer the workspace drew the versions twice.
        try { panel.Footer = sidebar || mode == MenuPaneMode.Aside ? "" : FrameworkVersions.CurrentWorkspaceFooter(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: workspace footer: " + e.Message); }
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().ConfigureMenuPanel(runtimePanel, sidebar, mode); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: menu pane configuration: " + e.Message); }
    }

    /// <summary>Parent subsequent rows to the scrolling content or to pinned chrome.</summary>
    /// <summary>SETTINGS1 stage 3: copy to the system clipboard; false when this runtime cannot.</summary>
    internal static bool TryCopyToClipboard(string text)
    {
        try { return CurrentRuntime().TryCopyToClipboard(text ?? ""); }
        catch { return false; }
    }

    /// <summary>SETTINGS1 stage 3: whether this runtime has a clipboard to copy to.</summary>
    internal static bool CanCopyToClipboard
    {
        get
        {
            try { return CurrentRuntime().CanCopyToClipboard; }
            catch { return false; }
        }
    }

    internal static void PinMenuRows(IUiPanel panel, MenuRowPin pin)
    {
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().PinMenuRows(runtimePanel, pin); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: menu row pin: " + e.Message); }
    }

    /// <summary>Visual treatment of the next sidebar button (catalog, selected, or chrome).</summary>
    internal static void StyleMenuRow(IUiPanel panel, MenuRowTone tone)
    {
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().StyleMenuRow(runtimePanel, tone); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: menu row style: " + e.Message); }
    }

    /// <summary>
    /// KIT1 gen 1: drop this pane's centered title band because the page draws its own name — the
    /// mod page's head block does, and live gen 1 showed the mod's name twice. Ask while the pane is
    /// being built; a pane that wants its title does not ask.
    /// </summary>
    internal static void HidePaneTitle(IUiPanel panel)
    {
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().HidePaneTitle(runtimePanel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: hide pane title: " + e.Message); }
    }

    /// <summary>
    /// CONSENT2: this panel owns its own frame rect — the toolkit's layout pass and its re-flow
    /// leave the size and the position alone. Asked for by the consent prompt, which is a centered
    /// modal and not one of a flowed set. See <see cref="IUiRuntime.SelfPlacePanel"/>.
    /// </summary>
    internal static void NoScrollPanel(IUiPanel panel)
    {
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().NoScrollPanel(runtimePanel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: no-scroll panel: " + e.Message); }
    }

    /// <summary>Keep a transient panel centered at the top of the screen, inside a 24-unit margin.</summary>
    public static void TopPanel(IUiPanel panel, bool top = true)
    {
        if (panel is UiPanelHandle tagged) tagged.Flows = !top;
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        CurrentRuntime().TopPanel(runtimePanel, top);
    }

    internal static void SelfPlacePanel(IUiPanel panel, bool selfPlaced = true)
    {
        if (panel is UiPanelHandle tagged) tagged.Flows = !selfPlaced;
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().SelfPlacePanel(runtimePanel, selfPlaced); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: self-placed panel: " + e.Message); }
    }

    /// <summary>
    /// INSTALLUX2 item 1: make this panel a modal dialog — opaque frame, raycast-blocking backdrop,
    /// content-sized and centered, revealed after the runtime's layout pass. See
    /// <see cref="IUiRuntime.DialogChrome"/>.
    /// </summary>
    internal static void DialogChrome(IUiPanel panel)
    {
        if (panel is UiPanelHandle tagged) tagged.Flows = false;
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().DialogChrome(runtimePanel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog chrome: " + e.Message); }
    }

    /// <summary>WIDEDIALOG1: ask the runtime for a wider dialog frame (<see cref="DialogLayout.FrameWidthFor"/> caps it).</summary>
    internal static void SetDialogWidth(IUiPanel panel, float frameWidth)
    {
        // A dialog-wide frame is centered by the runtime, never flowed.
        if (panel is UiPanelHandle tagged && frameWidth > UnityUiRuntimeMetrics.PanelWidth) tagged.Flows = false;
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().SetDialogWidth(runtimePanel, frameWidth); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog width: " + e.Message); }
    }

    /// <summary>
    /// INSTALLUX2 item 1: open the one modal confirmation dialog — title, body and the button pair,
    /// in the middle of the screen, over a backdrop nothing can be clicked through. DIALOGS1: drawn
    /// with the shared <see cref="DialogLayout"/> (the pair centered, No left, Yes right). A second
    /// call replaces a framework dialog on screen and puts a mod's back in the queue; Escape answers
    /// the open one with No and Enter with Yes. The caller binds the decision with
    /// <see cref="UiDialog.OnYes"/> / <see cref="UiDialog.OnNo"/>.
    /// </summary>
    internal static UiDialog Dialog(
        string heading,
        string body,
        string yesCaption,
        LabelTone yesTone,
        string noCaption,
        string tag = "dialog",
        string kind = "",
        string key = "",
        object? owner = null,
        string? pluginId = null,
        string? rowKey = null,
        string? yesKey = null,
        string? noKey = null) =>
        UiDialog.Open(
            title => Panel(title, owner, pluginId),
            tag, kind, key, heading, body, yesCaption, yesTone, noCaption, rowKey, yesKey, noKey);

    /// <summary>
    /// Clone a vanilla panel (PassiveFrame_Normal chrome, title label) under a mod-owned
    /// overlay canvas (sorting 5000). Pass <paramref name="owner"/> (the BepInEx plugin)
    /// and <paramref name="pluginId"/> (its GUID) so unload can sweep the root.
    /// </summary>
    public static IUiPanel Panel(string title, object? owner = null, string? pluginId = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        string id = string.IsNullOrWhiteSpace(pluginId)
            ? InferPluginId(owner)
            : pluginId.Trim();
        string root = UiOwnership.RootName(id);

        if (_skipped)
        {
            Plugin.Logger?.LogInfo("Ui: Panel('" + title + "') skipped; returning a disabled handle.");
            return WithDefaultFooter(Track(new UiPanelHandle(id, root, owner, CurrentRuntime(), panel: null, enabled: false)));
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsClient)
        {
            Plugin.Logger?.LogInfo("Ui: Panel('" + title + "') is client-only; returning a disabled handle.");
            return WithDefaultFooter(Track(new UiPanelHandle(id, root, owner, runtime, panel: null, enabled: false)));
        }

        IRuntimePanel? panel = null;
        try { panel = runtime.CreatePanel(title, root); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ui: Panel('" + title + "') threw: " + e);
        }

        bool enabled = panel != null;
        if (!enabled)
            Plugin.Logger?.LogInfo("Ui: could not build panel '" + title + "'; returning a disabled handle.");
        else
            Plugin.Logger?.LogDiag("Ui: panel '" + title + "' under " + root);

        return WithDefaultFooter(Track(new UiPanelHandle(id, root, owner, runtime, panel, enabled)));
    }

    /// <summary>
    /// WINDOWS1: a standard window (<see cref="UiWindowPreset"/>): the frame art, title, version stamp, input mode and
    /// Escape behavior fixed by the preset (docs/ui.md "Standard windows"). The stamp names the plugin from
    /// <paramref name="owner"/>'s <c>[BepInPlugin]</c> name and version; inside a plugin prefer <c>ctx.Windows.Create</c>,
    /// which also closes the window on unload. Disabled but valid on the server, like <see cref="Panel"/>. Throws for an
    /// empty title, more than six tabs or toolbar buttons, or a toolbar caption longer than two short words.
    /// </summary>
    public static IUiWindow Window(
        UiWindowPreset preset, string title, object? owner = null, string? pluginId = null, UiWindowOptions? options = null)
    {
        var (name, version) = ModMessagePrefix.PluginIdentity(owner);
        return new UiWindowHandle(preset, title, owner, pluginId, name, version, options);
    }

    /// <summary>WINDOWS1: as <see cref="Window"/> with the stamp's mod name and version given (<c>ctx.Windows</c>).</summary>
    internal static IUiWindow Window(
        UiWindowPreset preset, string title, object? owner, string? pluginId, string modName, string modVersion,
        UiWindowOptions? options) =>
        new UiWindowHandle(preset, title, owner, pluginId, modName, modVersion, options);

    /// <summary>WINDOWS1: the preset's frame on a game window (<see cref="IUiRuntime.WindowChrome"/>).</summary>
    internal static void WindowChrome(IUiPanel panel, UiWindowSpec spec)
    {
        // The re-flow lays the standard windows out side by side (UiWindowLayout.Spots / Place) from their asked-for widths.
        if (panel is UiPanelHandle owned) owned.WindowWidth = spec.FrameWidth;
        if (panel is not UiPanelHandle { Enabled: true, IsDisposed: false } handle
            || handle.Runtime is not IRuntimePanel runtimePanel) return;
        try { CurrentRuntime().WindowChrome(runtimePanel, spec); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: window chrome: " + e.Message); }
    }

    /// <summary>
    /// The running Progenitor Core's version as docs/versioning.md defines it — three numbers for a
    /// release (<c>2026.9.58</c>), four for an unmerged build (<c>2026.9.58.3</c>) — read from the
    /// Core assembly's informational version with its <c>+&lt;commit&gt;</c> metadata removed.
    /// </summary>
    public static string FrameworkVersion => FrameworkVersions.Core;

    /// <summary>
    /// <c>Progenitor &lt;version&gt;</c>: the line every prompt Progenitor shows carries in its
    /// <see cref="IUiPanel.Footer"/>. A plugin opts its own panel in with
    /// <c>panel.Footer = Ui.FrameworkFooter</c>.
    /// </summary>
    public static string FrameworkFooter => FrameworkVersions.CurrentPromptFooter();

    /// <summary>
    /// Core-owned panels (plugin id <see cref="FrameworkVersions.CorePluginId"/>) carry the framework
    /// footer from the moment they are created; every other panel starts without one. The Mods
    /// workspace panes replace it in <see cref="ConfigureMenuPanel(IUiPanel, bool, MenuPaneMode)"/>.
    /// </summary>
    private static IUiPanel WithDefaultFooter(IUiPanel handle)
    {
        if (string.Equals(handle.PluginId, FrameworkVersions.CorePluginId, StringComparison.Ordinal))
        {
            try { handle.Footer = FrameworkFooter; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: framework footer: " + e.Message); }
        }
        return handle;
    }

    /// <summary>
    /// Host mod rows beside a vanilla widget (job FUI36). Returns a handle that is
    /// <b>never null</b>: on the server, with the module skipped, or for an undefined site it is
    /// disabled but valid, exactly as <see cref="Panel"/> is.
    ///
    /// <para>
    /// <paramref name="site"/> is a <b>closed enum</b> and not a transform path. That is the
    /// design decision the whole capability rests on: a plugin may not name an arbitrary vanilla
    /// object, so Progenitor is answerable only for sites Core has read, spiked and written down.
    /// See <see cref="VanillaUiSite"/>.
    /// </para>
    ///
    /// <para>
    /// The rows are our own GameObjects in a container that is a <i>sibling</i> of the widget.
    /// Nothing vanilla is written, read-back or reordered, and disposal is one destroy with
    /// nothing to restore. The container is refreshed from the site's own managed system update,
    /// so it survives the map being closed and reopened — and because it is outside the plugin's
    /// DontDestroyOnLoad root, it is swept by <see cref="RemoveAll"/> and Core Uninstall through
    /// <see cref="VanillaAttachRegistry"/> rather than by a name sweep.
    /// </para>
    /// </summary>
    public static IVanillaAttachHandle AttachToVanilla(
        VanillaUiSite site, object? owner = null, string? pluginId = null) =>
        AttachToVanilla(site, VanillaPanelAnchor.Default, owner, pluginId);

    /// <summary>
    /// P6: attach to a vanilla panel at a chosen <paramref name="anchor"/> (see
    /// <see cref="VanillaPanelAnchor"/>; each <see cref="VanillaUiSite"/> lists the anchors it
    /// accepts, and <see cref="VanillaPanelAnchor.Default"/> picks the site's first). The handle
    /// reports the panel's open and close and which entity it shows (<see cref="IVanillaAttachHandle.OnPanel"/>,
    /// <see cref="IVanillaAttachHandle.Target"/>) and hosts buttons and rows in the game's own look.
    /// An anchor the site does not accept gives a disabled handle and one log line, like an
    /// undefined site. Several attachments on one site (several plugins, or several anchors) share
    /// one frame watch.
    /// </summary>
    public static IVanillaAttachHandle AttachToVanilla(
        VanillaUiSite site, VanillaPanelAnchor anchor, object? owner = null, string? pluginId = null)
    {
        string id = string.IsNullOrWhiteSpace(pluginId) ? InferPluginId(owner) : pluginId.Trim();
        string key = VanillaUiSites.Key(site);
        bool anchorOk = VanillaUiSites.TryResolveAnchor(site, anchor, out var resolved);
        string suffix = VanillaUiSites.AnchorKey(resolved);
        string container = UiOwnership.AttachName(id, suffix.Length == 0 ? key : key + "-" + suffix);

        string? refusal =
            !VanillaUiSites.IsDefined(site) ? "site " + (int)site + " is not a site Core knows"
            : !anchorOk ? "anchor " + anchor + " is not an anchor this site accepts"
            : _skipped ? "the module is skipped"
            : !CurrentRuntime().IsClient ? "it is client-only"
            : null;

        var runtime = CurrentRuntime();
        if (refusal != null)
        {
            Plugin.Logger?.LogInfo(
                "Ui: attach " + key + " " + refusal + "; returning a disabled handle.");
            var off = new VanillaAttachHandle(site, id, container, owner, runtime, enabled: false, resolved);
            VanillaAttachRegistry.Add(off, installWatch: null);
            return off;
        }

        var handle = new VanillaAttachHandle(site, id, container, owner, runtime, enabled: true, resolved);
        VanillaAttachRegistry.Add(handle, InstallSiteWatch);
        Plugin.Logger?.LogInfo("Ui: attach " + key + " requested by " + id + " as " + container);
        return handle;
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Watch pointer Down, Up and Exit on a game UI object (a swatch, a slot, a button the game
    /// built) without replacing anything the game wired. Adds owned rows to the object's
    /// <c>EventTrigger</c> (adding the component when it has none), each routed through a
    /// Bootstrap trampoline, so a hot-reloadable plugin can react to a press on vanilla UI.
    /// <paramref name="handler"/> runs on the main thread inside the game's event dispatch; it gets
    /// the kind and whether the primary (left) button was held (<see cref="UiPointerEvent"/>).
    ///
    /// <para>Returns a handle that is never null. On the server, with the module skipped, for a
    /// destroyed target or when wiring fails, the handle is inert and the reason is logged. Pass
    /// <paramref name="owner"/> (<c>ctx.Owner</c>) and track the handle (<c>ctx.Track</c>);
    /// <see cref="RemoveAll"/> and Core Uninstall also dispose it. Disposing removes our rows and
    /// destroys a trigger this watch added once it is empty. An object that gains an
    /// <c>EventTrigger</c> stops passing unhandled scroll and drag events to its parent.</para>
    ///
    /// <para>This is UI interaction on a specific object, not a key read: to act on a key, register
    /// it through <c>ctx.Keybinds</c>.</para>
    /// </summary>
    public static IDisposable WatchPointer(UnityEngine.GameObject target, Action<UiPointerEvent> handler, object? owner = null)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (_skipped || !CurrentRuntime().IsClient) return InertWatch.Instance;
        if (target == null || target.Pointer == IntPtr.Zero)
        {
            Plugin.Logger?.LogInfo("Ui: WatchPointer target is gone; returning an inert handle.");
            return InertWatch.Instance;
        }

        try
        {
            var watch = UiPointerWatch.Create(target, handler, owner);
            UiPointerWatchRegistry.Add(owner, watch, handler);
            return watch;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: WatchPointer on '" + SafeName(target) + "' failed: " + e.Message);
            return InertWatch.Instance;
        }
    }

    private static string SafeName(UnityEngine.GameObject go)
    {
        try { return go.name; }
        catch { return "?"; }
    }
#endif

    private sealed class InertWatch : IDisposable
    {
        internal static readonly InertWatch Instance = new();

        public void Dispose() { }
    }

    /// <summary>
    /// The one place a site's frame watch is created. Kept beside the API rather than inside the
    /// registry so the registry stays game-free: this closes over <see cref="CurrentRuntime"/>,
    /// whose Unity implementation is the half that knows what a <c>MapMenuMapper</c> is.
    /// </summary>
    private static IDisposable? InstallSiteWatch(VanillaUiSite site)
    {
        var runtime = CurrentRuntime();
        return runtime.WatchSite(site, sample => VanillaAttachRegistry.Dispatch(site, sample));
    }

    /// <summary>Unlock the cursor and show <paramref name="panel"/>. No-op when disabled or disposed.</summary>
    public static void Show(IUiPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        panel.Show();
    }

    /// <summary>Hide <paramref name="panel"/> and release capture when no other panel is shown.</summary>
    public static void Hide(IUiPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        panel.Hide();
    }

    /// <summary>
    /// Opt out of (or back into) gameplay input capture. On by default: showing a panel
    /// registers one mod-owned input context at <see cref="UiInputCapture.Order"/> (599, one
    /// below <c>InputContextOrder.MenuInput</c>) that applies chat's mask — every button blocked
    /// except UI navigation, analog Move/Aim blocked, camera rotation kept on keyboard and
    /// mouse, and <c>Back</c> / <c>Menu_Close</c> taken so Escape closes the panel without also
    /// opening the vanilla system menu — and hiding the last panel removes it again. Call
    /// <c>Ui.CaptureInput(false)</c> for a HUD-style overlay that must let the player keep
    /// playing underneath; the cursor unlock and Escape forwarding are unaffected.
    /// The context is registered once no matter how many panels are open, is never added or
    /// removed from inside an input callback (a change requested while
    /// <c>InputActionSystem._DenyModificationsOfInputContexts</c> is set is deferred to the
    /// next frame), and is removed on panel dispose, <see cref="RemoveAll"/>, plugin unload
    /// and Core Uninstall.
    ///
    /// <para>
    /// <b>Two caveats this doc used to promise its way past</b> (job FUI23, R44 findings 3 and 6).
    /// The mask above is applied by a native detour on <c>RotateCameraBlocker.GetConsumedInputs</c>,
    /// and a game update that moves that address is non-fatal by design: the context is still
    /// registered, but it then blocks camera rotation only and <b>Escape is not consumed</b>. The
    /// <c>Ui: input context registered … mask=live</c> / <c>mask=absent</c> line reports which of
    /// the two you have. And with <c>Ui.CaptureInput(false)</c> — or a
    /// <see cref="UiCaptureMode.Never"/> panel — nothing is registered at all, so Escape closes the
    /// panel <i>and</i> opens the vanilla system menu. That is what opting out of capture means; it
    /// is stated here because the sentence above promises the recipe unconditionally and the
    /// recipe has two ways of not being there.
    /// </para>
    /// </summary>
    public static void CaptureInput(bool capture) => UiInputCapture.SetEnabled(capture);

    /// <summary>
    /// Choose <i>when</i> an open panel blocks gameplay input, rather than whether it ever does
    /// (job FUI19). The framework default is <see cref="UiCaptureMode.WhenHovered"/>: the wheel
    /// scrolls the panel under the pointer and zooms the camera everywhere else, which is what a
    /// player expects of every other windowed game UI.
    ///
    /// <para>
    /// With neither <paramref name="owner"/> nor <paramref name="pluginId"/> this moves the
    /// framework-wide default for the process — the same thing the client's <c>Ui.CaptureMode</c>
    /// config entry does at install. Passing either scopes the change to panels that plugin owns,
    /// so one mod choosing <see cref="UiCaptureMode.WhileShown"/> for a modal editor does not
    /// change how every other mod's panels behave. A single panel can override both through
    /// <see cref="IUiPanel.CaptureMode"/>.
    /// </para>
    ///
    /// <para>
    /// The two identifying parameters mirror <see cref="Panel"/> exactly, and they have to:
    /// <c>Panel</c> lets a plugin state its GUID explicitly rather than have it inferred from the
    /// owner's type name, and a default keyed the other way round would silently never match the
    /// panels it was meant for.
    /// </para>
    ///
    /// <para>
    /// This composes with, and does not replace, <see cref="CaptureInput(bool)"/>. That one is
    /// still the master switch: <c>CaptureInput(false)</c> removes the context now and keeps it
    /// off whatever mode any panel asks for, and it is unchanged for existing callers.
    /// <c>CaptureInput(UiCaptureMode.Never)</c> is the softer form of the same wish — panels stop
    /// asking for the block — and leaves the switch alone.
    /// </para>
    ///
    /// <para>
    /// A mode chosen here applies to the <b>next</b> Show of a panel; a panel already open keeps
    /// the mode it was shown with, so the block cannot change under a player mid-interaction
    /// because an unrelated plugin loaded.
    /// </para>
    /// </summary>
    public static void CaptureInput(UiCaptureMode mode, object? owner = null, string? pluginId = null)
    {
        if (!IsDefinedMode(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown UiCaptureMode.");

        if (owner == null && string.IsNullOrWhiteSpace(pluginId))
        {
            lock (Gate)
                _defaultMode = mode;
            Plugin.Logger?.LogInfo("Ui: default capture mode is now " + mode + " (framework-wide).");
            return;
        }

        string id = string.IsNullOrWhiteSpace(pluginId)
            ? InferPluginId(owner)
            : pluginId!.Trim();
        lock (Gate)
            PluginModes[id] = mode;
        Plugin.Logger?.LogInfo("Ui: default capture mode is now " + mode + " for '" + id + "'.");
    }

    /// <summary>
    /// The mode a panel of <paramref name="pluginId"/> gets when it does not choose one itself:
    /// that plugin's own default if it set one, otherwise the framework-wide default.
    /// </summary>
    public static UiCaptureMode CaptureMode(string? pluginId)
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(pluginId)
                && PluginModes.TryGetValue(pluginId!, out var mine))
                return mine;
            return _defaultMode;
        }
    }

    /// <summary>True while a mod input context is registered in the game's context list.</summary>
    public static bool IsCapturingInput => UiInputCapture.IsRegistered;

    /// <summary>
    /// Dispose every handle recorded for <paramref name="owner"/> and destroy that plugin's
    /// exact <c>progenitor-ui-&lt;pluginId&gt;</c> root (not a prefix match). Core Uninstall
    /// is the only path that sweeps every <c>progenitor-ui-*</c> root.
    /// Returns how many handles were disposed, so the reload sweep can log a real count
    /// (R27 finding 25).
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        VanillaPanelTabs.Drop(owner);
        // DIALOGS1: the owner's dialogs, on screen and queued, go first and run no callback — the
        // second line behind the tracked ctx.Dialogs registration.
        try { UiDialog.DropOwner(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll dialogs threw: " + e.Message); }
        // WINDOWS1 review: after its dialogs (the match reads them), the OnRename handlers it left on ownerless windows.
        try { UiWindowHandle.DropHandlersOf(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll window handlers threw: " + e.Message); }
        // P7b help: the owner's marked terms, marker change handlers and tip cards (every plugin, ctx.Hud or not).
        try { UiTerms.DropOwner(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll terms threw: " + e.Message); }
        try { UiTips.DropOwner(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll tips threw: " + e.Message); }
        UiHandle[] snapshot;
        lock (Gate)
        {
            var list = new List<UiHandle>();
            for (int i = 0; i < Live.Count; i++)
            {
                if (ReferenceEquals(Live[i].Owner, owner))
                    list.Add(Live[i]);
            }

            snapshot = list.ToArray();
        }

        string root = snapshot.Length > 0
            ? snapshot[0].RootName
            : UiOwnership.RootName(InferPluginId(owner));

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll dispose threw: " + e.Message); }
        }

        // Vanilla attachments are owner-tagged too, and they are the one thing here that the
        // root destroy below cannot reach: the container is parented beside a vanilla widget, not
        // under progenitor-ui-<pluginId>. It goes first, so an unloading plugin's rows leave the
        // screen on the same frame its panels do (job FUI36; research war-events.md §4.4).
        int attachments = 0;
        try { attachments = VanillaAttachRegistry.RemoveAll(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll attachments threw: " + e.Message); }

        // Pointer watches sit on game objects too, outside the plugin root (Ui.WatchPointer).
        try { attachments += UiPointerWatchRegistry.RemoveAll(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll pointer watches threw: " + e.Message); }

        // Keybinds and settings sources are owner-tagged the same way handles are, so an unloading
        // plugin's binds stop firing on the same frame its panels are destroyed.
        try { Keybinds.RemoveAll(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll keybinds threw: " + e.Message); }
        try { ModsMenu.RemoveAll(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll mod menu threw: " + e.Message); }
        try { SettingsCatalog.RemoveAll(owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll settings sources threw: " + e.Message); }

        ushort[] ids = UiSlotTable.ReleaseOwner(owner);
        try { CurrentRuntime().UnwireMany(ids); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll UnwireMany threw: " + e.Message); }

        try { CurrentRuntime().DestroyRoot(root); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: RemoveAll sweep threw: " + e.Message); }

        // Attachments are counted with the panels, not reported separately: the caller is the
        // reload sweep, whose log line is "how many things did this plugin still own", and a
        // capability left out of that number is the ServerBeam defect (R27 finding 4) repeated.
        return snapshot.Length + attachments;
    }

    internal static void Install()
    {
        lock (Gate)
        {
            if (IsModuleSkipped())
            {
                _skipped = true;
                _installed = true;
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(ModuleName));
                return;
            }

            _skipped = false;
            _installed = true;
            _runtime ??= CreateDefaultRuntime();
            UiInputCapture.Use(_runtime.IsClient ? CreateDefaultBlocker() : NullUiInputBlocker.Instance);
        }

        TryFillBootstrapSlots();

#if !PROGENITOR_TESTS
        // Keybinds and the settings panel are client-only, and the key source is the one place
        // Core reads the keyboard. A server keeps
        // NullKeyInputSource, so ctx.Keybinds.Register there is a registration that never fires.
        if (_runtime is { IsClient: true })
        {
            try { UiKeybindHost.Install(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind host install threw: " + e.Message); }
            NativeModsEntry.Install(reason => ModsMenu.Open(reason, "NativeModsEntry"));
        }
#endif

        Plugin.Logger?.LogInfo(_runtime is { IsClient: true }
            ? "Ui: installed (client)"
            : "Ui: installed (server no-op)");
    }

    internal static void Uninstall()
    {
        VanillaPanelTabs.Drop(null);
        // Dialogs first, every one of them (framework and mod, on screen and queued), and latched
        // shut: every later step here disposes panels, and a dialog panel leaving the screen would
        // otherwise show the next queued dialog on a fresh overlay that outlives this generation.
        int dialogs = 0;
        try { dialogs = UiDialog.CloseAllForUnload(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog unload threw: " + e.Message); }

        // P7: the widget backends' process-wide state (tooltip, HUD notices, Locate pings, a world pick, a drag).
        try { UiWidgetLifetime.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget teardown threw: " + e.Message); }

        // NAV1: the focus marker comes off before the panels it is drawn on go.
        try { UiNav.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: navigation reset threw: " + e.Message); }

        ModsMenu.Uninstall();
        // The panel and its keybinds go first: their handlers live in this Core generation, and a
        // frame subscription or a settings row that outlived it would call into a dead ALC.
        try { SettingsPanel.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings panel uninstall threw: " + e.Message); }
#if !PROGENITOR_TESTS
        // Guarded like every other step: an escape here skipped the whole handle, slot, input and
        // prefix teardown below (live 2026-10-02, a TypeLoadException from the play backdrop).
        try { NativeModsEntry.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: native Mods entry uninstall threw: " + e.Message); }
        try { UiKeybindHost.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind host uninstall threw: " + e.Message); }
#endif
        try { Keybinds.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds uninstall threw: " + e.Message); }
        try { SettingsCatalog.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings catalog uninstall threw: " + e.Message); }

        // Job FUI36. Attachments go here and not with the handle sweep below, because the prefix
        // sweep at the end of this method cannot reach them: DestroyByPrefix walks
        // DontDestroyOnLoad roots and an attachment container lives under the map menu. An
        // attachment missed here survives the Core swap as a frozen GameObject with a dead text
        // writer, and its SystemHooks subscription pins the generation.
        int attachments = 0;
        try { attachments = VanillaAttachRegistry.Clear(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: attachment sweep threw: " + e.Message); }
        VanillaSiteHealth.Clear();

        // Pointer watches before the slot table is cleared below: each one unwires its own ids and
        // takes its rows off the game object's EventTrigger while that object still exists.
        try { attachments += UiPointerWatchRegistry.Clear(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: pointer watch sweep threw: " + e.Message); }

        UiHandle[] snapshot;
        ushort[] ids;
        lock (Gate)
        {
            snapshot = Live.ToArray();
            Live.Clear();
            _installed = false;
            _skipped = false;
            _shown = 0;
            _keySlotFilled = false;
            ids = UiSlotTable.SnapshotIds();
            UiSlotTable.ClearAll();
        }

        // Unwire every EventTrigger / Button BEFORE DestroyByPrefix so UnityEvents
        // drop Bootstrap listeners while the GameObjects still exist. The cascade
        // then yields a FrameTicker tick before ALC.Unload (client-hot-swap-crash.md).
        try { CurrentRuntime().UnwireMany(ids); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Uninstall UnwireMany threw: " + e.Message); }

#if !PROGENITOR_TESTS
        try { BootstrapLink.ClearAllUiSlots(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ClearAllUiSlots: " + e.Message); }
        try { BootstrapLink.ClearUiDispatch(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ClearUiDispatch: " + e.Message); }
        try { BootstrapLink.ClearUiKeys(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ClearUiKeys: " + e.Message); }
        try { BootstrapLink.ClearUiCapture(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ClearUiCapture: " + e.Message); }
#endif

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Uninstall dispose threw: " + e.Message); }
        }

        // Anything a dispose above built on its way out (a close handler that opened a panel) is
        // this generation's too: dispose it now rather than leave it under a root the next
        // generation adopts. Reported, because it means some close path still opens UI.
        int survivors = DisposeLateHandles();

        // The hover gate goes before the capture teardown, not after: it is what still holds
        // per-panel state and a Plugin.Frame subscription, and a pass that ran after
        // UiInputCapture.Uninstall would drive a module that has already handed its blocker back.
        try { UiHoverGate.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: hover gate reset threw: " + e.Message); }

        // Job FUI20: the cascade slots go with the handles that held them. A next generation must
        // start its first panel at the center rather than one step down from slots nobody owns.
        try { UiPanelSlots.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel slot reset threw: " + e.Message); }

        // Job FUI33 (R48 finding 6). The deferred re-measure was the one Plugin.Frame subscriber in
        // this module with no teardown: its pump unsubscribed only from inside itself, on a later
        // frame, when the owed list emptied. The leftover subscription is not the hazard —
        // Plugin.Frame is a Core static and Plugin.Uninstall nulls it — the pump-armed latch is:
        // it survives an uninstall in the same ALC, and a latch standing over a subscription that
        // was nulled means no later panel ever subscribes and every one of them prints
        // settle=armed over a pump that is not running.
        try { UiSettleGate.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel settle reset threw: " + e.Message); }

        try { UiFontLookup.Invalidate(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: font lookup reset threw: " + e.Message); }

        lock (Gate)
        {
            PluginModes.Clear();
            _defaultMode = UiCaptureModes.Default;
        }

        // After the handles are disposed (each Hide already released its count) force the
        // context out and drop the detour. A Core swap must not leave an input context
        // registered: the next generation would have no owner for it, and the player would
        // be left with a mask nobody can remove.
        try { UiInputCapture.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input capture uninstall threw: " + e.Message); }
#if !PROGENITOR_TESTS
        try { UnityUiRuntime.ReleaseInputPumps(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input pump release threw: " + e.Message); }
#endif

        int leftover = 0;
        try
        {
            var runtime = CurrentRuntime();
            leftover = runtime.DestroyByPrefix(UiOwnership.Prefix);
            runtime.SetCapture(false);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Uninstall sweep threw: " + e.Message);
        }

        lock (Gate)
            _runtime = null;

        Plugin.Logger?.LogInfo("Ui: uninstalled leftover=" + leftover + " attachments=" + attachments
            + " dialogs=" + dialogs + " late=" + survivors + " held=" + UiDialog.HeldCount);
    }

    /// <summary>
    /// Dispose handles registered after <see cref="Uninstall"/> took its snapshot, a bounded number
    /// of rounds (a dispose can register another). Returns how many it disposed.
    /// </summary>
    internal static int DisposeLateHandles()
    {
        int disposed = 0;
        for (int round = 0; round < 4; round++)
        {
            UiHandle[] late;
            lock (Gate)
            {
                if (Live.Count == 0) break;
                late = Live.ToArray();
                Live.Clear();
            }

            for (int i = 0; i < late.Length; i++)
            {
                try { late[i].Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: late dispose threw: " + e.Message); }
                disposed++;
            }
        }

        if (disposed > 0)
            Plugin.Logger?.LogWarning("Ui: Uninstall disposed " + disposed + " panel(s) built during teardown.");
        return disposed;
    }

    internal static string DescribePins()
    {
        if (_skipped)
            return HarmonySlotHandoff.FormatModulePinsSkipped(ModuleName);

        var holds = new List<string>();
        lock (Gate)
        {
            if (Live.Count > 0)
                holds.Add("handles=" + Live.Count);
            if (_shown > 0)
                holds.Add("shown=" + _shown);
            if (_installed)
                holds.Add("installed");
            if (UiSlotTable.LiveCount > 0)
                holds.Add("slots=" + UiSlotTable.LiveCount);
            if (_keySlotFilled)
                holds.Add("keys");
        }

        // A dialog still held after unload is a panel and callbacks of this generation.
        if (UiDialog.HeldCount > 0)
            holds.Add("dialogs=" + UiDialog.HeldCount);

        if (UiInputCapture.IsRegistered || UiInputCapture.Count > 0)
            holds.Add(UiInputCapture.Describe());

        // The hover gate holds a Plugin.Frame subscription while a hovered panel is shown, so it
        // is a pin in exactly the way every other frame subscriber in this file is.
        if (UiHoverGate.Count > 0)
            holds.Add(UiHoverGate.Describe());

        // Job FUI20: a slot held by a panel that is no longer shown is a leak with a visible
        // symptom (every later panel one step further down the cascade), so it is a pin.
        if (UiPanelSlots.Count > 0)
            holds.Add(UiPanelSlots.Describe());

        // Job FUI33: the deferred re-measure holds a Plugin.Frame subscription for as long as any
        // panel is still waiting for its canvas, which is a pin in exactly the way the hover gate
        // above is — and the latch is reported even with nothing owed, because a pump armed over
        // an empty list is the state that means an earlier teardown left it standing.
        if (UiSettleGate.Count > 0 || UiSettleGate.PumpArmed)
            holds.Add(UiSettleGate.Describe());

        // Job FUI36: an attachment is a pin twice over — a GameObject the name sweeps cannot see,
        // and a SystemHooks subscription holding a Core-owned delegate. "Who holds this
        // generation" must be answerable from the log before anyone reaches for a process dump.
        // The repair tick is a third one, and it is reported even with nothing attached for the
        // same reason the settle pump above is: a tick over an empty table is the state that means
        // an earlier teardown left one standing (R63 finding 1).
        if (VanillaAttachRegistry.Count > 0 || VanillaAttachRegistry.WatchCount > 0
            || VanillaAttachRegistry.HasHeartbeat)
            holds.Add(VanillaAttachRegistry.Describe());
        var missingSites = VanillaSiteHealth.Describe();
        if (missingSites.Length > 0) holds.Add(missingSites);

        if (Keybinds.Count > 0)
            holds.Add(Keybinds.Describe());
        if (SettingsCatalog.SourceCount > 0)
            holds.Add(SettingsCatalog.Describe());
        if (SettingsPanel.IsOpen)
            holds.Add("settingsPanel");

        return HarmonySlotHandoff.FormatModulePins(ModuleName, holds);
    }

    internal static void Forget(UiHandle handle)
    {
        lock (Gate)
            Live.Remove(handle);
    }

    internal static void TrackChild(UiHandle handle)
    {
        lock (Gate)
        {
            if (!Live.Contains(handle))
                Live.Add(handle);
        }
    }

    /// <summary>
    /// Lay every shown panel out for the set they are now in (job FUI21). Called when a panel is
    /// shown and when one is hidden, because that is when the set changes and therefore when the
    /// answer changes: one panel is centered, two sit side by side, and neither of those is a fact
    /// about a single panel.
    ///
    /// <para>
    /// <b>Why it is here.</b> A plugin knows where <i>its</i> panel should go and cannot know what
    /// else is open; a panel handle knows its own state and not its siblings'. <see cref="Ui"/> is
    /// the one layer that holds every live handle, so it is the only layer that can count the set
    /// — the same argument job FUI20 made for placing panels in the framework at all, applied one
    /// level further.
    /// </para>
    ///
    /// <para>
    /// Ordered by slot, not by handle order: <see cref="UiPanelSlots"/> hands out the lowest free
    /// index, so the longest-open panel keeps the lowest slot and therefore the leftmost column,
    /// and a panel opening beside it does not make it swap sides.
    /// </para>
    ///
    /// <para>
    /// Runs outside <c>Gate</c>. Placement calls into the runtime and therefore into Unity, and
    /// holding the lock across it would put the UI module's own lock underneath every layout pass.
    /// </para>
    /// </summary>
    internal static void ReflowPanels()
    {
        try
        {
            UiHandle[] snapshot;
            lock (Gate)
                snapshot = Live.ToArray();

            var shown = new List<UiPanelHandle>();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] is UiPanelHandle panel && panel.IsShownPanel)
                    shown.Add(panel);
            }

            // WINDOWS1: the workspace is dimmed behind a window that takes the input while shown (UiWindowLayout.ShadeWorkspace).
            bool workspaceOpen = false;
            var modes = new List<UiCaptureMode>();
            foreach (var p in shown)
            {
                if (ModsMenu.IsWorkspacePanel(p)) workspaceOpen = true;
                else if (p.WindowWidth > 0f) modes.Add(p.CaptureMode);
            }

            bool shade = UiWindowLayout.ShadeWorkspace(workspaceOpen, modes);
            if (shade != _windowShade)
            {
                _windowShade = shade;
                try { CurrentRuntime().WindowShade(shade); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: window shade threw: " + e.Message); }
            }

            if (shown.Count == 0) return;
            shown.Sort(static (a, b) => a.PlacementSlot.CompareTo(b.PlacementSlot));

            foreach (var (panel, spot) in UiWindowLayout.Spots(shown, p => p.WindowWidth, ModsMenu.IsWorkspacePanel, p => p.Flows, p => p.ShownSequence))
                panel.PlaceAt(spot);
        }
        catch (Exception e)
        {
            // A re-flow that failed leaves every panel where it was, which is readable-but-stale
            // rather than half a layout.
            Plugin.Logger?.LogWarning("Ui: panel re-flow threw: " + e.Message);
        }
    }

    /// <summary>WINDOWS1: whether the runtime was last asked to dim the workspace behind the windows.</summary>
    private static bool _windowShade;

    /// <summary>WINDOWS1 review: a shown window's input mode changed; the dim (and nothing else) may change with it.</summary>
    internal static void ReflowAfterModeChange() => ReflowPanels();

    internal static void NotifyShown(int delta)
    {
        lock (Gate)
        {
            _shown += delta;
            if (_shown < 0) _shown = 0;
        }
    }

    internal static IUiRuntime CurrentRuntime()
    {
        lock (Gate)
        {
            if (_runtime != null) return _runtime;
            _runtime = CreateDefaultRuntime();
            return _runtime;
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        VanillaPanelTabs.Drop(null);
        UiNav.Reset();
        lock (Gate)
        {
            Live.Clear();
            _swallowBack = false;
            _windowShade = false;
            _runtime = null;
            _installed = false;
            _skipped = false;
            _shown = 0;
            _keySlotFilled = false;
            PluginModes.Clear();
            _defaultMode = UiCaptureModes.Default;
        }

        UiDialog.ForgetForTests();
        VanillaAttachRegistry.ResetForTests();
        VanillaSiteHealth.Clear();
        UiSlotTable.ClearAll();
        UiPanelSlots.Reset();
        UiHoverGate.ResetForTests();
        UiInputCapture.ResetForTests();
        SettingsPanel.Uninstall();
        Keybinds.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModsMenu.Uninstall();
        UiFontLookup.Invalidate();
    }

    /// <summary>
    /// Drive the Bootstrap key dispatch exactly as the client does, so a test of "Escape hid my
    /// panel and my module still says it is open" runs the real sweep rather than a stand-in for
    /// it. The pre-FUI29 test for the settings panel called <c>panel.Hide()</c> with a comment
    /// saying that is what Escape does, which is a test of the comment.
    /// </summary>
    internal static void PressKeyForTests(int keyCode, bool down) => OnBootstrapKey(keyCode, down);

    internal static void SetKeyInputSourceForTests(IKeyInputSource? source) =>
        Keybinds.SetSourceForTests(source);

    internal static void SetInputBlockerForTests(IUiInputBlocker blocker) =>
        UiInputCapture.Use(blocker);

    internal static void SetRuntimeForTests(IUiRuntime runtime)
    {
        lock (Gate)
            _runtime = runtime ?? NullUiRuntime.Instance;
    }

    internal static void SetSkippedForTests(bool skipped)
    {
        lock (Gate)
            _skipped = skipped;
    }
#endif

    /// <summary>
    /// A cast integer is a legal <c>enum</c> value in C# and would otherwise reach the router as a
    /// mode it has no rule for — which the router's <c>default</c> arm reads as
    /// <see cref="UiCaptureMode.Never"/>, i.e. silently no capture at all. Refuse at the door.
    /// </summary>
    private static bool IsDefinedMode(UiCaptureMode mode) =>
        mode is UiCaptureMode.WhenHovered or UiCaptureMode.WhileShown or UiCaptureMode.Never;

    private static IUiPanel Track(UiPanelHandle handle)
    {
        lock (Gate)
            Live.Add(handle);
        return handle;
    }

    private static IUiRuntime CreateDefaultRuntime()
    {
#if PROGENITOR_TESTS
        return NullUiRuntime.Instance;
#else
        return new UnityUiRuntime();
#endif
    }

    private static IUiInputBlocker CreateDefaultBlocker()
    {
#if PROGENITOR_TESTS
        return NullUiInputBlocker.Instance;
#else
        return new GameInputBlocker();
#endif
    }

    private static void TryFillBootstrapSlots()
    {
#if PROGENITOR_TESTS
        _keySlotFilled = true;
#else
        try
        {
            if (BootstrapLink.TrySetUiDispatch((BootstrapLink.UiDispatch)UiSlotTable.BootstrapDispatch))
                Plugin.Logger?.LogInfo("Ui: Bootstrap click slot filled.");
            else
                Plugin.Logger?.LogInfo("Ui: Bootstrap UiSlots absent; click handlers stay in-process (tests / no Bootstrap).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: TrySetUiDispatch threw: " + e.Message);
        }

        try
        {
            if (BootstrapLink.TrySetUiKeys((BootstrapLink.UiKeyDispatch)OnBootstrapKey))
                _keySlotFilled = true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: TrySetUiKeys threw: " + e.Message);
        }
#endif
    }

    /// <summary>
    /// TEXTEXPAND1 (dev bridge <c>field_cancel</c>): one physical Escape as Bootstrap delivers it — 27,
    /// then the paired <c>BackAction</c> 17 — through the same hook, so the dialog policy, the sweep and
    /// the pairing rule all run as they do for the player's key.
    /// </summary>
    internal static void PressEscapeForBridge()
    {
        OnBootstrapKey(27, true);
        OnBootstrapKey(17, true);
    }

    /// <summary>
    /// Escape (27) and the Bootstrap <c>BackAction</c> (17): after a key capture, a dialog's answer, a screen's Back and a
    /// pending Mods search clear, the topmost standard window or input-taking floating panel closes, one per press
    /// (WINDOWS1, <see cref="TryCloseTopWindow"/>); with none of those, the sweep hides every shown panel — including
    /// panels this sweep does not own and whose module it does not know. Other modules find out
    /// that a panel left the screen by reading the handle (<see cref="OwnedPanel"/>). The Mods
    /// workspace is the exception: <see cref="ModsMenu.NotifyHiddenByEscape"/> logs
    /// <c>hide reason=escape</c> so that edge is not silent. Close and Back stay the explicit
    /// dispose / back-to-mod paths.
    /// </summary>
    private static void OnBootstrapKey(int keyCode, bool down)
    {
        if (!down) return;
        if (keyCode != 27 && keyCode != 17) return;
        // One physical Escape arrives as 27 then 17 (Bootstrap UiInputContext). When 27 was taken by a
        // capture or a dialog, the 17 of the same press is too.
        if (keyCode == 17 && _swallowBack)
        {
            _swallowBack = false;
            return;
        }

        _swallowBack = false;
        // HOSTS1: one policy for both hosts (capture > dialog > search > close). The search stage is
        // the workspace's own (TryConsumeEscapeHide, inside the sweep), so this hook passes no search.
        var outcome = WorkspaceInputPolicy.Escape(
            // DROPDOWN1: an open dropdown list takes Escape like a dialog (its handler below dismisses it).
            WorkspaceHost.Standalone, Keybinds.EscapeBelongsToCapture, UiDialog.Current != null || UiChoiceHandle.AnyOpen,
            searchFocused: false, searchText: "", keyCode: keyCode);
        // KEYS3 polish (user report 2026-09-30): Escape during a key capture cancels the capture only.
        // It never hides the workspace (or any panel), whichever of the capture's pump and this hook
        // sees the press first.
        if (outcome.Stage == EscapeStage.CancelCapture)
        {
            _swallowBack = outcome.SwallowPairedBack;
            WorkspaceInputPolicy.NotePressTaken(outcome.Stage);
            Plugin.Logger?.LogDiag("Ui: key " + keyCode + " left to the key capture; no panel hidden.");
            return;
        }

        // INSTALLUX2 item 1: Escape = No. The open dialog is answered (and closed, and its owner
        // told) before the sweep below hides it, because a hidden-but-open confirmation is the
        // FUI29 failure: the box leaves the screen with no decision and nobody finds out.
        // DIALOGS1: the queue is held across the sweep, so the dialog waiting behind the one Escape
        // answers is shown after it — not shown and then hidden by this same press.
        UiDialog.BeginEscape();
        int hidden = 0;
        try
        {
            bool answered = false;
            if (outcome.Stage == EscapeStage.AnswerDialog)
            {
                try { answered = UiDialog.NotifyEscape(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog escape threw: " + e.Message); }
            }

            // KEYS3 polish: Escape on an open dialog answers the dialog and nothing else; the panels
            // behind it (the Mods workspace) stay. The next Escape, with no dialog, is the sweep.
            if (answered)
            {
                _swallowBack = outcome.SwallowPairedBack;
                WorkspaceInputPolicy.NotePressTaken(outcome.Stage);
                Plugin.Logger?.LogDiag("Ui: key " + keyCode + " answered the open dialog; no panel hidden.");
                return;
            }

            // JOINFLOW1: a framework screen with its own Back (the join screens, the date-time picker) takes
            // Escape as Back or Close and is told, instead of being hidden behind its owner's back. The
            // newest handler first; a handler that returns false passes the press on.
            if (TryEscapeHandlers())
            {
                _swallowBack = keyCode == 27;
                Plugin.Logger?.LogDiag("Ui: key " + keyCode + " taken by a screen's Back; no panel hidden.");
                return;
            }

            UiHandle[] snapshot;
            lock (Gate)
                snapshot = Live.ToArray();

            // WINDOWS1 live fix: one press closes one standard window, the topmost (shown last); the next press goes on to
            // the next window and only then to the sweep below (the Mods workspace). The sweep hid every window and the
            // workspace on one press.
            // HOSTS1 order (capture > dialog > search > close): a pending Mods search clear (or leave) takes this press
            // before a window closes; one Escape does exactly one thing.
            if (HasShownWindow(snapshot) && ModsMenu.TryConsumeEscapeHide())
            {
                _swallowBack = keyCode == 27;
                Plugin.Logger?.LogDiag("Ui: key " + keyCode + " cleared the Mods search; the windows stay.");
                return;
            }

            if (TryCloseTopWindow(snapshot))
            {
                _swallowBack = keyCode == 27;
                WorkspaceInputPolicy.NotePressTaken(EscapeStage.CloseWindow);
                Plugin.Logger?.LogDiag("Ui: key " + keyCode + " closed the topmost window; nothing else hidden.");
                return;
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] is UiPanelHandle panel && panel.State == UiHandleState.Shown)
                {
                    try { panel.Hide(); hidden++; }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Escape hide threw: " + e.Message); }
                }
            }
        }
        finally
        {
            try { UiDialog.EndEscape(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog queue threw: " + e.Message); }
        }

        // The log used to carry only HideCore's `Ui: hide '<title>'`, which says a panel was
        // hidden and not what hid it. Both live diagnoses of FUI29 had to infer Escape from the
        // absence of the owner's own close line; this says it. ModsMenu is told separately so
        // the workspace log can carry reason=escape; other owners still read the handle.
        if (hidden > 0)
        {
            // P7b: one physical Escape is one action. Its 27 swept the panels; the paired BackAction 17 of the same
            // press must not reach the Escape handlers next, or a handler that stood back because a panel was over it
            // (a world pick, while the Mods workspace was open) would take the same press once the sweep cleared the
            // way — the workspace closed and the pick ended on one press.
            if (keyCode == 27) _swallowBack = true;
            try { ModsMenu.NotifyHiddenByEscape(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Mods escape hook threw: " + e.Message); }
            Plugin.Logger?.LogDiag("Ui: key " + keyCode + " hid " + hidden + " panel(s); owners are not told.");
        }
    }

    /// <summary>
    /// The panels Escape closes one at a time, topmost first (by show order, which is their draw order): the shown
    /// standard windows, and the floating panels that take the input (<see cref="UiCaptureMode.WhileShown"/>, or
    /// <see cref="UiCaptureMode.WhenHovered"/> while the pointer is over them). WINDOWS1 live round 4: a floating panel and
    /// the Mods workspace closed together on one press. A passive panel (<see cref="UiCaptureMode.Never"/>, or not under
    /// the pointer) is left to the sweep, so a tracker on screen never takes an Escape and a panel that shows itself again
    /// cannot keep Escape from reaching the workspace. Workspace panes, dialogs, self-placed, top-pinned and mounted panels
    /// are never among them; when none is shown, the press goes on to the sweep (the workspace and the rest).
    /// </summary>
    private static List<UiPanelHandle> ShownWindows(UiHandle[] snapshot)
    {
        var shown = new List<UiPanelHandle>();
        foreach (var h in snapshot)
        {
            if (h is not UiPanelHandle p || p.IsDisposed || p.State != UiHandleState.Shown || ModsMenu.IsWorkspacePanel(p)) continue;
            if (p.WindowWidth > 0f || (p.Flows && TakesInput(p))) shown.Add(p);
        }

        return shown;
    }

    /// <summary>A floating panel that takes the input now: shown WhileShown, or WhenHovered with the pointer over it.</summary>
    private static bool TakesInput(UiPanelHandle p)
    {
        var mode = p.ShownCaptureMode;
        if (mode == UiCaptureMode.WhileShown) return true;
        if (mode != UiCaptureMode.WhenHovered || p.Runtime is not IRuntimePanel runtime) return false;
        try { return CurrentRuntime().PanelUnderPointer(runtime); }
        catch { return false; }
    }

    private static bool HasShownWindow(UiHandle[] snapshot) => ShownWindows(snapshot).Count > 0;

    /// <summary>
    /// WINDOWS1: hide the topmost shown standard window (<see cref="UiWindowLayout.EscapeTarget"/>). False when none is
    /// shown, or when the window is still shown after the hide (the press then falls through to the sweep).
    /// </summary>
    private static bool TryCloseTopWindow(UiHandle[] snapshot)
    {
        var top = UiWindowLayout.EscapeTarget(ShownWindows(snapshot), _ => true, p => p.ShownSequence);
        if (top == null) return false;
        try { top.ForceHide(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Escape window hide threw: " + e.Message);
            return false;
        }

        return top.State != UiHandleState.Shown;
    }

    private static readonly List<Func<bool>> EscapeHandlers = new();

    /// <summary>NAV1: the panels on screen now, in no order (<see cref="UiPanelHandle.ShownSequence"/> orders them).</summary>
    internal static List<UiPanelHandle> ShownPanelsSnapshot()
    {
        var list = new List<UiPanelHandle>();
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
                if (Live[i] is UiPanelHandle panel && !panel.IsDisposed && panel.IsShownNow) list.Add(panel);
        }

        return list;
    }

    /// <summary>NAV1: a controller's B: the Escape key's path (a dialog's answer, a screen's Back, else hide).</summary>
    internal static void PressEscapeForNav()
    {
        OnBootstrapKey(27, true);
        OnBootstrapKey(17, true);
    }

    /// <summary>
    /// JOINFLOW1: take Escape while a framework screen with its own Back is open. <paramref name="handler"/>
    /// runs on an Escape that no dialog answered, newest first, and returns true when it acted (went back,
    /// closed). Dispose the lease when the screen closes. Core-internal: the handler lives in Core only.
    /// </summary>
    internal static IDisposable PushEscape(Func<bool> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (Gate) EscapeHandlers.Add(handler);
        return new EscapeLease(handler);
    }

    /// <summary>Run the Escape handlers, newest first; true when one acted.</summary>
    internal static bool TryEscapeHandlers()
    {
        Func<bool>[] handlers;
        lock (Gate) handlers = EscapeHandlers.ToArray();
        for (int i = handlers.Length - 1; i >= 0; i--)
        {
            try { if (handlers[i]()) return true; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: escape handler threw: " + e.Message); }
        }

        return false;
    }

    private sealed class EscapeLease : IDisposable
    {
        private Func<bool>? _handler;

        internal EscapeLease(Func<bool> handler) => _handler = handler;

        public void Dispose()
        {
            var h = _handler;
            _handler = null;
            if (h == null) return;
            lock (Gate) EscapeHandlers.Remove(h);
        }
    }

    private static bool IsModuleSkipped()
    {
        if (HarmonySlotHandoff.SkipModulesRefusedLiveWorld) return false;
        if (HarmonySlotHandoff.ShouldSkipModule(ModuleName)) return true;
#if !PROGENITOR_TESTS
        string csv = BootstrapLink.SkipCoreModules;
        if (string.IsNullOrWhiteSpace(csv)) return false;
        var parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i].Trim(), ModuleName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
#endif
        return false;
    }

    private static string InferPluginId(object? owner)
    {
        if (owner == null) return UiOwnership.UnownedId;
        try
        {
            var type = owner.GetType();
            string? full = type.FullName;
            if (!string.IsNullOrEmpty(full)) return UiOwnership.Sanitize(full);
        }
        catch
        {
            // owner.GetType can throw from a collectible ALC that has already unloaded.
        }

        return UiOwnership.UnownedId;
    }
}

internal sealed class NullUiRuntime : IUiRuntime
{
    public static readonly NullUiRuntime Instance = new();

    public bool IsClient => false;

    public IRuntimePanel? CreatePanel(string title, string rootName) => null;

    public IRuntimeWidget? AddButton(IRuntimePanel panel, string text) => null;

    public IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial) => null;

    public IRuntimeWidget? AddLabel(IRuntimePanel panel, string text) => null;

    public IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial) => null;

    public IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial) => null;

    public void PlacePanel(IRuntimePanel panel, PanelSpot spot) { }

    public void Show(IRuntimePanel panel) { }

    public void Hide(IRuntimePanel panel) { }

    public bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget) => false;

    public void Unwire(ushort widgetId) { }

    public void UnwireMany(IReadOnlyList<ushort> ids) { }

    public int DestroyRoot(string rootName) => 0;

    public int DestroyByPrefix(string prefix) => 0;

    public void SetCapture(bool capture) { }

    /// <summary>
    /// No Unity, so no transform to read. Unknown rather than an empty rect: the router must not
    /// mistake "there is no runtime" for "the pointer is not over this panel" (job FUI19).
    /// </summary>
    public ScreenRect PanelRect(IRuntimePanel panel) => ScreenRect.Unknown;

    /// <summary>No mouse in a server process or a test. Fail closed, exactly as before FUI19.</summary>
    public PointerSample ReadPointer() => PointerSample.Unreadable;
}
