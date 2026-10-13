using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// Name-prefix contract for every GameObject this module creates. Spike step-0 swept
/// <c>progenitor-ui*</c> leftovers; the module uses one prefix so Core Uninstall and plugin
/// unload can <c>DestroyImmediate</c> by name without walking Unity types the tests cannot see.
/// The mod-owned canvas is not parented under <c>UICanvasBase</c> (scale and visibility go wrong
/// when it is parented into the vanilla tree).
/// </summary>
public static class UiOwnership
{
    /// <summary>Every mod-owned UI root starts with this. Uninstall destroys the lot.</summary>
    public const string Prefix = "progenitor-ui-";

    /// <summary>Used when <see cref="Ui.Panel"/> is called without a plugin id.</summary>
    public const string UnownedId = "unowned";

    /// <summary>
    /// Overlay canvas sorting. Same value as the effects overlay and evidence 78 / 88.
    ///
    /// <para>
    /// <b>Do not raise this above a vanilla canvas because the probe named one (job FUI31).</b>
    /// The interaction probe reports <c>canvasAbove=CanvasUnscaledMainMenu@9000</c> on the main
    /// menu and on the <c>ConnectingView</c> disconnect screen. That is information, not a defect,
    /// and the three facts behind it are why the number stays at 5000:
    /// </para>
    ///
    /// <para>
    /// <b>1. Order never produces a raycast hit; it only breaks ties between hits.</b> Read from
    /// this build's own metadata: <c>GraphicRegistry</c> keys its raycastable set by canvas
    /// and <c>GraphicRaycaster.Raycast</c> is
    /// handed only that canvas's own registered graphics; sorting order enters solely through
    /// <c>sortOrderPriority</c>, which <c>EventSystem</c>'s comparer uses
    /// to <em>sort</em> hits. A canvas above ours takes a click exactly where one of its own
    /// enabled raycast targets covers the pointer and nowhere else — which is now measured, not
    /// assumed: the probe's <c>overlapsPanel=</c> field is that read.
    /// </para>
    ///
    /// <para>
    /// <b>2. The canvas above us is the one that hosts the game's modals.</b>
    /// <c>MainMenuCanvasBase</c> is anchor points and popup prefabs —
    /// <c>MenuParent</c>, <c>VersionParent</c>, <c>PopupParent</c> and seven popup types, with no
    /// <c>Graphic</c>, <c>CanvasGroup</c> or <c>GraphicRaycaster</c> field of its own — and
    /// <c>GenericPopupSystem</c> parents every main-menu popup under it.
    /// Losing a click to a modal the game put up is correct behavior. A mod panel that outranks
    /// that canvas also outranks loading screens and the disconnect UI a player needs in order to
    /// leave, and this overlay is <c>DontDestroyOnLoad</c>, so it would keep outranking them
    /// across a scene load. The failure modes are not symmetric: below, a mod panel is degraded
    /// where a vanilla graphic overlaps it and Escape still closes it
    /// (<c>Ui.OnBootstrapKey</c>); above, a player is stuck on a screen they cannot dismiss.
    /// </para>
    ///
    /// <para>
    /// <b>3. No number here can be justified from code.</b> No ProjectM type assigns
    /// <c>Canvas.sortingOrder</c> anywhere in the game's metadata and no game constant names a canvas layer;
    /// <c>CanvasUnscaledMainMenu</c> does not appear in the game's metadata or string literals
    /// at all, because canvas orders and canvas
    /// composition are <em>serialized scene data</em>. Any value picked to sit above 9000 would be
    /// picked from one live sweep of two screens and would stop being right after a game update
    /// that reshuffles that data, silently.
    /// </para>
    /// </summary>
    public const int OverlaySorting = 5000;

    /// <summary>
    /// DontDestroyOnLoad root for one plugin: <c>progenitor-ui-&lt;pluginId&gt;</c>.
    /// Dots in a BepInEx GUID stay (Unity GO names allow them); path separators do not.
    /// </summary>
    public static string RootName(string? pluginId) => Prefix + Sanitize(pluginId);

    /// <summary>
    /// Container for one vanilla attachment: <c>progenitor-ui-&lt;pluginId&gt;-&lt;site&gt;</c>
    /// (job FUI36). It reads like a child of the plugin's root and
    /// <see cref="IsOwnedBy"/> agrees, but it is <b>not</b> under that root and not in
    /// DontDestroyOnLoad at all — it is parented beside a vanilla widget, so neither
    /// <c>DestroyRoot</c> (exact name) nor <c>DestroyByPrefix</c> (a DontDestroyOnLoad scan) can
    /// reach it. <see cref="VanillaAttachRegistry"/> is what sweeps it; the name is for the log
    /// line and for a human looking at the hierarchy.
    /// </summary>
    public static string AttachName(string? pluginId, string siteKey) =>
        RootName(pluginId) + "-" + (string.IsNullOrWhiteSpace(siteKey) ? "site" : siteKey);

    /// <summary>True when <paramref name="name"/> is this module's prefix (any plugin).</summary>
    public static bool IsOwned(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when this GameObject or any ancestor is owned. Panel overlay canvases are
    /// named <c>overlay-N</c> (no prefix); only the DontDestroyOnLoad root carries
    /// <see cref="Prefix"/>. A name-only <see cref="IsOwned"/> check therefore treats
    /// our Panels canvas (sorting 4999) as native.
    /// </summary>
    public static bool IsOwnedInHierarchy(string? name, IReadOnlyList<string>? ancestors)
    {
        if (IsOwned(name)) return true;
        if (ancestors == null) return false;
        for (int i = 0; i < ancestors.Count; i++)
        {
            if (IsOwned(ancestors[i])) return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="name"/> is this plugin's root or a child named under it.
    /// A BepInEx GUID that extends another (example.sample vs example.sample.extra) is not
    /// a child: '.' after the root is part of a longer plugin id, not a child separator.
    /// </summary>
    public static bool IsOwnedBy(string? name, string? pluginId)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string root = RootName(pluginId);
        if (name.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith(root, StringComparison.OrdinalIgnoreCase) && name.Length > root.Length)
        {
            char next = name[root.Length];
            return next is '-' or ' ' or '/';
        }

        return false;
    }

    /// <summary>
    /// Plugin GUID to a Unity GameObject name fragment. Empty becomes <see cref="UnownedId"/>.
    /// Strips characters Unity or a later path join would treat as separators.
    /// </summary>
    public static string Sanitize(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return UnownedId;
        var src = pluginId.Trim();
        var sb = new StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0')
                sb.Append('-');
            else if (char.IsControl(c))
                sb.Append('-');
            else
                sb.Append(c);
        }

        string result = sb.ToString();
        return result.Length == 0 ? UnownedId : result;
    }
}
