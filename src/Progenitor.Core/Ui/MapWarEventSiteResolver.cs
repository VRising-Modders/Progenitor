#if !PROGENITOR_TESTS
using System;
using Progenitor.Hooks;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// The Unity half of <c>Ui.AttachToVanilla</c>: how each <see cref="VanillaUiSite"/> is found,
/// how its container is built, and how a row is cloned from its donor (job FUI36).
///
/// <para><b>The seam.</b> <c>SystemHooks.After&lt;MapMenuMapper&gt;</c> — the managed
/// <c>SystemBase.Update</c>, verified live for managed systems on 2026-09-02. This is not a
/// Harmony patch of ours at all, which makes the "never patch a Burst <c>LambdaJob_*</c>" rule
/// (learned the hard way in earlier map overlays) unbreakable by construction rather
/// than by discipline.</para>
///
/// <para><b>The address.</b> <c>MapMenuMapper.GetMapMenu()?.WarEventInfo</c> and nothing else.
/// There are two instances of <c>MapWarEventInfoEntry</c> in the process — the map card and the
/// transient HUD popup <c>AlertsParent.WarEventNode</c> — so a type sweep would be a coin flip
/// that changes between sessions. <c>GetMapMenu()</c> is an entity query behind a method call and
/// returns null as a normal state; every frame null-checks it.</para>
///
/// <para><b>What is decided here and what is decided offline.</b> This file only reads and
/// builds; every "should we" is <see cref="VanillaAttachPlan"/>, which the test project compiles
/// and this file it does not. Two facts the offline research could not settle — the hierarchy
/// under the widget, and whether a <c>LayoutGroup</c> governs the parent — are read at runtime
/// and reported in the attach line's <c>layout=</c> field, so the first client spike answers them
/// from the log rather than from a rewrite.</para>
/// </summary>
internal static class VanillaSiteRuntime
{
    /// <summary>
    /// The widget the most recent sample resolved. Held so the builders do not each re-run the
    /// query.
    ///
    /// <para><b>Who calls them, exactly</b> (R60 finding 8 corrected an earlier comment here that
    /// claimed both). <see cref="BuildContainer"/> runs inside the frame's dispatch, immediately
    /// after the sample that found the widget. <see cref="CloneRow"/> does <b>not</b>: a plugin's
    /// <c>Row(...)</c> or <c>SetRows(...)</c> that grows the row list clones one right there, on
    /// the plugin's own frame and thread, against whatever this field held from the most recent
    /// dispatch — and the field is only nulled by a sample that failed, so it can be a widget
    /// from a map that has since closed. That is why every read of it re-checks
    /// <see cref="Alive"/>, and why nothing here may cache anything derived from it.</para>
    /// </summary>
    private static MapWarEventInfoEntry? _mapWidget;

    private static bool _loggedHierarchy;

    internal static IDisposable? Watch(VanillaUiSite site, Action<VanillaSiteSample> onFrame)
    {
        // Before every guard below, because the registry's watch repair must have a frame even
        // for a site whose watch could not be built (R62 finding 2). This is the one line in the
        // module that names Plugin.Frame: the registry stays game-free and the test project
        // compiles neither this file nor Core's Plugin.
        VanillaAttachRegistry.UseHeartbeat(FrameHeartbeat.Install);

        if (onFrame == null || !VanillaUiSites.IsDefined(site)) return null;

        // P6: the panel sites have their own resolver (VanillaPanelSiteResolver.cs) on their own
        // mappers; the heartbeat above is shared.
        if (VanillaUiSites.IsPanel(site))
        {
            try { return UnityUiRuntime.WatchPanelSite(site, onFrame); }
            catch (Exception e)
            {
                // A mapper type the game no longer has fails here, once, at install.
                if (VanillaSiteHealth.ReportMissing(site, "watch: " + e.GetType().Name))
                    Plugin.Logger?.LogWarning(VanillaSiteHealth.Line(site, "watch: " + e.GetType().Name + ": " + e.Message));
                return null;
            }
        }

        if (site != VanillaUiSite.MapWarEventInfo) return null;

        try
        {
            return SystemHooks.After<MapMenuMapper>(ctx =>
            {
                VanillaSiteSample sample;
                try { sample = SampleMapWarEvent(ctx); }
                catch (Exception e)
                {
                    // A read that throws is "not there this frame", never an exception into a
                    // system hook. The attachment degrades and the next frame tries again.
                    _mapWidget = null;
                    Plugin.Logger?.LogWarning("Ui: attach MapWarEventInfo read threw: " + e.Message);
                    sample = VanillaSiteSample.Absent;
                }

                onFrame(sample);
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach MapWarEventInfo hook install threw: " + e.Message);
            return null;
        }
    }

    private static VanillaSiteSample SampleMapWarEvent(SystemHookContext ctx)
    {
        var world = ctx?.World;
        if (world == null) { _mapWidget = null; return VanillaSiteSample.Absent; }

        // A SystemBase is an Il2CppObjectBase, not a UnityEngine.Object: it has no lifetime
        // operator and Alive() does not apply to it. A null pointer is the whole check.
        var mapper = world.GetExistingSystemManaged<MapMenuMapper>();
        if (mapper == null || mapper.Pointer == IntPtr.Zero)
        {
            _mapWidget = null;
            return VanillaSiteSample.Absent;
        }

        var menu = mapper!.GetMapMenu();
        if (!Alive(menu)) { _mapWidget = null; return VanillaSiteSample.Absent; }

        var widget = menu!.WarEventInfo;
        if (!Alive(widget)) { _mapWidget = null; return VanillaSiteSample.Absent; }

        var go = widget!.gameObject;
        if (!Alive(go)) { _mapWidget = null; return VanillaSiteSample.Absent; }

        _mapWidget = widget;

        var parent = go!.transform != null ? go.transform.parent : null;
        bool donor = Alive(widget.TimerText) && Alive(widget.TimerText.gameObject);

        LogHierarchyOnce(widget, go, parent);

        return new VanillaSiteSample(
            resolved: true,
            instanceId: widget.GetInstanceID(),
            donorPresent: donor,
            parentLayoutGroup: HasLayoutGroup(parent),
            parentName: parent != null ? SafeName(parent.gameObject) : "",
            parentSizeFitter: HasSizeFitter(parent));
    }

    /// <summary>
    /// Build the mod container. It is a <b>sibling</b> of the widget root — a child of the
    /// widget's parent, never a child of the widget and never a child of <c>TextGroup</c> —
    /// for three reasons, in order of what they cost when ignored: <c>TextGroup</c> is the
    /// <c>CanvasGroup</c> whose alpha the vanilla per-frame <c>SetData</c> rewrites, so rows
    /// under it fade and can vanish with the vanilla text; <c>Hide()</c> may deactivate the
    /// widget root when nothing is scheduled, which is exactly when a mod row is the only useful
    /// thing on screen; and adding a child to a vanilla transform is already a mutation of the
    /// vanilla hierarchy — it changes <c>childCount</c>, and anything the game does with sibling
    /// indices then sees us.
    /// </summary>
    internal static IRuntimeWidget? BuildContainer(
        VanillaUiSite site, string containerName, VanillaAttachParent parent, bool ignoreLayout)
    {
        if (site != VanillaUiSite.MapWarEventInfo) return null;
        var widget = _mapWidget;
        if (!Alive(widget)) return null;
        var widgetGo = widget!.gameObject;
        if (!Alive(widgetGo)) return null;

        GameObject? go = null;
        try
        {
            var widgetRt = widgetGo!.GetComponent<RectTransform>();
            Transform? host = parent == VanillaAttachParent.CanvasRoot
                ? CanvasRootOf(widgetGo)
                : (widgetGo.transform != null ? widgetGo.transform.parent : null);
            if (host == null) host = widgetGo.transform != null ? widgetGo.transform.parent : null;
            if (host == null) return null;

            go = new GameObject(containerName);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(host, worldPositionStays: false);

            // Anchored to the widget's own rect: our top edge on their bottom edge, so the rows
            // track the card without being inside it. When the widget's rect cannot be read the
            // container still exists at the parent's pivot, which is visibly wrong rather than
            // invisibly absent — the failure the spike can see.
            if (widgetRt != null)
            {
                rt.anchorMin = new Vector2(widgetRt.anchorMin.x, widgetRt.anchorMin.y);
                rt.anchorMax = new Vector2(widgetRt.anchorMin.x, widgetRt.anchorMin.y);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = widgetRt.anchoredPosition
                    - new Vector2(0f, widgetRt.rect.height * (1f - widgetRt.pivot.y) + RowGap);
                rt.sizeDelta = new Vector2(Mathf.Max(widgetRt.rect.width, MinWidth), 0f);
            }
            else
            {
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(MinWidth, 0f);
            }

            // Inside our own container a layout group of ours is fine and better than hand-placed
            // offsets, because the rows are ours end to end.
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 2f;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // A laid-out sibling would make the vanilla card move to make room, which is a
            // regression the player did not ask for. ignoreLayout is the cheap answer; disabling
            // the vanilla layout component is the last resort and is not taken here, because a
            // vanilla group left disabled after an unload is a broken vanilla screen.
            if (ignoreLayout)
            {
                var element = go.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
            }

            return new VanillaContainerWidget(go);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach MapWarEventInfo container build threw: " + e.Message);
            TryDestroy(go);
            return null;
        }
    }

    /// <summary>
    /// Clone the site's donor (<c>TimerText</c>'s GameObject) into the container. The clone
    /// carries the game's own font, size, color, material and outline — nothing is shipped,
    /// which is the M3 principle — and then loses everything that would let the game drive it:
    /// its <c>LocalizedText</c> (which re-applies its key's string on a locale change or a
    /// <c>LocalizationVersion</c> bump and would blank the row), every inherited persistent
    /// <c>UnityEvent</c> call, and its raycast target.
    /// </summary>
    internal static IRuntimeWidget? CloneRow(VanillaUiSite site, IRuntimeWidget container, string text)
    {
        if (site != VanillaUiSite.MapWarEventInfo) return null;
        if (container is not VanillaContainerWidget host || !host.Alive) return null;
        var widget = _mapWidget;
        if (!Alive(widget) || !Alive(widget!.TimerText)) return null;
        var donor = widget.TimerText.gameObject;
        if (!Alive(donor)) return null;

        GameObject? clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(donor);
            clone.name = host.Name + "-row";
            clone.transform.SetParent(host.Transform, worldPositionStays: false);

            // The wrapper goes, the TMP stays. LocalizedText.ForceSet is deliberately not the
            // write path: it is a vanilla-behavior method with _IsForceSet / _TextHasBeenModified
            // state we would then be depending on.
            foreach (var localized in clone.GetComponentsInChildren<LocalizedText>(true))
            {
                try { UnityEngine.Object.DestroyImmediate(localized); } catch { /* */ }
            }

            var tmp = clone.GetComponent<TextMeshProUGUI>() ?? clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null)
            {
                // A row that came out without the game's text component would have to be drawn by
                // us, in a font we chose, beside the game's own card. No row is better.
                Plugin.Logger?.LogWarning(
                    "Ui: attach MapWarEventInfo donor clone came out without a TextMeshProUGUI; the row is dropped.");
                TryDestroy(clone);
                return null;
            }

            // A mod row must never eat a click meant for the map.
            try { tmp.raycastTarget = false; } catch { /* */ }
            try { tmp.text = text ?? ""; } catch { /* */ }

            SilenceInherited(clone);
            clone.SetActive(true);
            return new VanillaRowWidget(clone, tmp);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach MapWarEventInfo row clone threw: " + e.Message);
            TryDestroy(clone);
            return null;
        }
    }

    private const float RowGap = 4f;
    private const float MinWidth = 240f;

    /// <summary>
    /// One line, once per process, naming what is actually there. The hierarchy under the widget
    /// and whether a layout component governs it are prefab serialized data: nothing in
    /// the game's metadata can answer them, so they are read here and printed rather than assumed.
    /// This is the log line the first client spike (P3) reads.
    ///
    /// <para><b>A group and a fitter are two answers, so they are two fields</b> (R60 finding 2).
    /// The spike is sent to find out which component governs the parent, and a merged
    /// <c>parentLayout=True</c> would tell it neither: a <c>LayoutGroup</c> moves the vanilla card
    /// and is the reason <c>ignoreLayout</c> exists, a <c>ContentSizeFitter</c> lays no child out
    /// at all and <c>ignoreLayout</c> says nothing to it.</para>
    /// </summary>
    private static void LogHierarchyOnce(MapWarEventInfoEntry widget, GameObject go, Transform? parent)
    {
        if (_loggedHierarchy) return;
        _loggedHierarchy = true;
        try
        {
            var textGroup = widget.TextGroup;
            var groupTransform = Alive(textGroup) ? textGroup!.transform : null;
            Plugin.Logger?.LogInfo(
                "Ui: site MapWarEventInfo root=" + SafeName(go)
                + " instance=" + widget.GetInstanceID()
                + " parent=" + (parent != null ? SafeName(parent.gameObject) : "<none>")
                + " children=" + (go.transform != null ? go.transform.childCount : 0)
                + " rootLayoutGroup=" + HasLayoutGroup(go.transform)
                + " rootSizeFitter=" + HasSizeFitter(go.transform)
                + " parentLayoutGroup=" + HasLayoutGroup(parent)
                + " parentSizeFitter=" + HasSizeFitter(parent)
                + " textGroupLayoutGroup="
                + (groupTransform != null ? HasLayoutGroup(groupTransform).ToString() : "<none>")
                + " textGroupSizeFitter="
                + (groupTransform != null ? HasSizeFitter(groupTransform).ToString() : "<none>")
                + " donor=" + (Alive(widget.TimerText) ? SafeName(widget.TimerText.gameObject) : "<none>"));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: site MapWarEventInfo hierarchy dump threw: " + e.Message);
        }
    }

    /// <summary>A <c>LayoutGroup</c> on <paramref name="t"/>: the component that would lay our container out.</summary>
    private static bool HasLayoutGroup(Transform? t)
    {
        try
        {
            if (t == null) return false;
            var go = t.gameObject;
            if (!Alive(go)) return false;
            return go!.GetComponent<LayoutGroup>() != null;
        }
        catch { return false; }
    }

    /// <summary>A <c>ContentSizeFitter</c> on <paramref name="t"/>: reported, never acted on.</summary>
    private static bool HasSizeFitter(Transform? t)
    {
        try
        {
            if (t == null) return false;
            var go = t.gameObject;
            if (!Alive(go)) return false;
            return go!.GetComponent<ContentSizeFitter>() != null;
        }
        catch { return false; }
    }

    private static Transform? CanvasRootOf(GameObject go)
    {
        try
        {
            var canvas = go.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            var root = canvas.rootCanvas;
            return root != null ? root.transform : canvas.transform;
        }
        catch { return null; }
    }

    /// <summary>
    /// <c>Instantiate</c> keeps persistent, inspector-wired <c>UnityEvent</c> calls, and those
    /// survive <c>RemoveAllListeners</c> — a clone that keeps them keeps driving the game's own
    /// objects. The same rule the settings panel learned; it is a rule about clones, not buttons.
    /// </summary>
    private static void SilenceInherited(GameObject clone)
    {
        try
        {
            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                // The TMP itself must keep working; everything else the donor brought along is a
                // component we did not ask for and cannot vouch for.
                if (behaviour.TryCast<TextMeshProUGUI>() != null) continue;
                try { behaviour.enabled = false; } catch { /* */ }
            }
        }
        catch { /* */ }
    }

    /// <summary>
    /// The one destroy in this capability, and it goes through the module's shared helper
    /// (<see cref="UnityUiRuntime.TryDestroy"/>): warn, fall back to a deferred
    /// <c>Destroy</c>, and <b>report whether it took</b>.
    ///
    /// <para>Why the return value matters here more than anywhere else in the module (R60
    /// finding 1). <c>DestroyImmediate</c> is the one Unity destroy that throws rather than
    /// deferring — it refuses to run from inside a handful of engine callbacks — and it is called
    /// from inside <c>Apply</c>, which runs inside the game's own <c>MapMenuMapper.Update</c>. A
    /// swallowed throw here does not lose a log line: this container is parented into the
    /// <i>vanilla</i> tree, so it is reachable by nothing but the reference the caller holds, and
    /// dropping that reference welds a frozen container with dead rows into the game's hierarchy
    /// where <c>DestroyRoot</c>, <c>DestroyByPrefix</c>, <c>Ui.RemoveAll</c> and Core Uninstall
    /// all walk straight past it. So the callers keep the reference until this returns true.</para>
    /// </summary>
    private static bool TryDestroy(GameObject? go) => UnityUiRuntime.TryDestroy(go, immediate: true);

    /// <summary>
    /// The same destroy, with the verdict kept rather than flattened. The two widgets below need
    /// it: a deferred destroy is a destroy, and treating it as a refusal is what made the common
    /// recovery path warn once a frame and re-throw for thirty of them (R61 finding 7).
    /// </summary>
    private static UiDestroyVerdict DestroyWithVerdict(GameObject? go) =>
        UnityUiRuntime.DestroyWithVerdict(go, immediate: true);

    private static bool Alive(UnityEngine.Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static string SafeName(GameObject? go)
    {
        try { return Alive(go) ? (go!.name ?? "") : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// A frame for <c>VanillaAttachRegistry</c>'s watch repair that is <b>not</b> a site watch.
    /// <c>Plugin.Frame</c> is Core's own static event, so this pins nothing the collectible
    /// generation does not already own, and it is given back at Uninstall with the table.
    /// </summary>
    private sealed class FrameHeartbeat : IDisposable
    {
        private Action? _tick;

        private FrameHeartbeat(Action tick)
        {
            _tick = tick;
            Plugin.Frame += tick;
        }

        internal static IDisposable? Install(Action tick) => tick == null ? null : new FrameHeartbeat(tick);

        public void Dispose()
        {
            var tick = _tick;
            _tick = null;
            if (tick != null) Plugin.Frame -= tick;
        }
    }

    /// <summary>The mod container, as the Core half sees it.</summary>
    private sealed class VanillaContainerWidget : IRuntimeWidget, IReleaseVerdict
    {
        private GameObject? _go;

        internal VanillaContainerWidget(GameObject go)
        {
            _go = go;
            Name = SafeName(go);
        }

        internal string Name { get; }

        /// <summary>
        /// What the last <c>Release</c> did. <c>Failed</c> until one has run, which is the answer
        /// that keeps the object being retried and so is the safe one to start from.
        /// </summary>
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        /// <inheritdoc />
        public UiDestroyVerdict LastRelease => _verdict;

        internal Transform? Transform
        {
            get { try { return Alive(_go) ? _go!.transform : null; } catch { return null; } }
        }

        public string Text { get => ""; set { } }

        public bool Visible
        {
            get { try { return Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (Alive(_go)) _go!.SetActive(value); } catch { /* */ } }
        }

        public bool BoolValue { get => false; set { } }

        public float FloatValue { get => 0f; set { } }

        public string StringValue { get => ""; set { } }

        public bool Alive => VanillaSiteRuntime.Alive(_go);

        public object? ClickEvent => null;

        public object? FloatEvent => null;

        public object? BoolEvent => null;

        public object? TextEvent => null;

        public object? PointerEnterEvent => null;

        public object? PointerExitEvent => null;

        /// <summary>
        /// Destroys our container and, with it, the cloned rows. Touches nothing vanilla: no
        /// sibling order to restore, no component to re-enable, no field to put back.
        ///
        /// <para>The reference is dropped <b>after</b> the destroy is confirmed, never before. A
        /// <c>Release</c> that threw leaves this widget reporting <see cref="Alive"/> — which is
        /// how <see cref="VanillaAttachHandle"/> learns to retry it on a later frame instead of
        /// forgetting an object nothing else in the process can reach (R60 finding 1).</para>
        /// </summary>
        public void Release()
        {
            var go = _go;
            if (go == null) return;
            if (!VanillaSiteRuntime.Alive(go))
            {
                _go = null;
                _verdict = UiDestroyVerdict.Destroyed;
                return;
            }

            // A destroy Unity has already queued is not queued again (R61 finding 7) — and that
            // rule lives in VanillaAttachHandle, in both of its Release() call sites: the sweep,
            // which re-reads LastRelease every frame, and ReleaseLocked, which makes the same
            // test before its first release (R63 finding 6). Not in a private copy here: two
            // implementations of one guard meant the handle's was dead code and any future widget
            // that forgot to copy this line got the defect back (R62 finding 5).
            _verdict = DestroyWithVerdict(go);
            if (_verdict == UiDestroyVerdict.Destroyed) _go = null;
        }
    }

    /// <summary>One cloned row inside a container.</summary>
    private sealed class VanillaRowWidget : IRuntimeWidget, IReleaseVerdict
    {
        private GameObject? _go;
        private TextMeshProUGUI? _tmp;

        internal VanillaRowWidget(GameObject go, TextMeshProUGUI tmp)
        {
            _go = go;
            _tmp = tmp;
        }

        /// <summary>
        /// What the last <c>Release</c> did. <c>Failed</c> until one has run, which is the answer
        /// that keeps the object being retried and so is the safe one to start from.
        /// </summary>
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

        /// <inheritdoc />
        public UiDestroyVerdict LastRelease => _verdict;

        public string Text
        {
            get { try { return Alive && _tmp != null ? (_tmp.text ?? "") : ""; } catch { return ""; } }
            set { try { if (Alive && _tmp != null) _tmp.text = value ?? ""; } catch { /* */ } }
        }

        public bool Visible
        {
            get { try { return VanillaSiteRuntime.Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (VanillaSiteRuntime.Alive(_go)) _go!.SetActive(value); } catch { /* */ } }
        }

        public bool BoolValue { get => false; set { } }

        public float FloatValue { get => 0f; set { } }

        public string StringValue { get => Text; set => Text = value; }

        public bool Alive => VanillaSiteRuntime.Alive(_go) && VanillaSiteRuntime.Alive(_tmp);

        public object? ClickEvent => null;

        public object? FloatEvent => null;

        public object? BoolEvent => null;

        public object? TextEvent => null;

        public object? PointerEnterEvent => null;

        public object? PointerExitEvent => null;

        /// <summary>Same rule as the container: the reference goes only once the destroy took,
        /// and a deferred destroy is followed rather than repeated — by the handle, which asks
        /// <see cref="LastRelease"/> both on every sweep and before each first release (R62
        /// finding 5, R63 finding 6), not by a copy of the guard here.</summary>
        public void Release()
        {
            var go = _go;
            if (go == null || !VanillaSiteRuntime.Alive(go))
            {
                _go = null;
                _tmp = null;
                _verdict = UiDestroyVerdict.Destroyed;
                return;
            }

            _verdict = DestroyWithVerdict(go);
            if (_verdict != UiDestroyVerdict.Destroyed) return;
            _go = null;
            _tmp = null;
        }
    }
}
#endif
