using System;

namespace Progenitor.Ui;

/// <summary>
/// Surface the tab strip builds onto. <see cref="IUiPanel"/> is adapted; tests supply a
/// recorder. No Unity types.
/// </summary>
internal interface IModsTabStripSurface
{
    void StyleNext(MenuRowTone tone);

    IUiButton Button(string text);
}

/// <summary><see cref="IUiPanel"/> adapter. Selected requests the native red tab highlight.</summary>
internal sealed class UiPanelTabStripSurface : IModsTabStripSurface
{
    private readonly IUiPanel _panel;

    public UiPanelTabStripSurface(IUiPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    public void StyleNext(MenuRowTone tone) => Ui.StyleMenuRow(_panel, tone);

    public IUiButton Button(string text) => _panel.Button(text);
}

/// <summary>
/// The workspace tab strip (four buttons since KEYS1) for the unused top inset (<see cref="MenuPanelLayout.TopInset"/>).
/// Pure builder: no static state. Selected tab is <see cref="MenuRowTone.Selected"/> —
/// mapped to native red highlight by the tab renderer.
/// </summary>
internal static class ModsTabStrip
{
    public const string ModsCaption = "Mods";
    public const string InstallModsCaption = "Install Mods";
    /// <summary>Three homes (decided 2026-10-06): the Configuration tab's new name, after Keybinds.</summary>
    public const string PreferencesCaption = "Preferences";

    /// <summary>KEYS1 (decided 2026-09-29): the key tab.</summary>
    public const string KeybindsCaption = "Keybinds";

    /// <summary>Three homes (decided 2026-10-06): how the joined server is set up; the last tab.</summary>
    public const string ServerCaption = "Server";

    /// <summary>
    /// Three homes (decided 2026-10-06): Mods · Install Mods · Keybinds · Preferences · Server. The Server tab is last:
    /// "server should be the last tab since it is generally only applicable to those who do server stuff."
    /// </summary>
    public static readonly ModsTab[] Order =
    {
        ModsTab.Mods,
        ModsTab.InstallMods,
        ModsTab.Keybinds,
        ModsTab.Preferences,
        ModsTab.Server,
    };

    /// <summary>Round 13: the strip's captions in <see cref="Order"/>; blanks when <paramref name="count"/> is another strip's.</summary>
    internal static string[] CaptionsFor(int count)
    {
        var captions = new string[Math.Max(1, count)];
        for (int i = 0; i < captions.Length; i++)
            captions[i] = count == Order.Length ? Caption(Order[i]) : "";
        return captions;
    }

    public static string Caption(ModsTab tab) => tab switch
    {
        ModsTab.InstallMods => InstallModsCaption,
        ModsTab.Preferences => PreferencesCaption,
        ModsTab.Keybinds => KeybindsCaption,
        ModsTab.Server => ServerCaption,
        _ => ModsCaption,
    };

    /// <summary>Selected state is rendered by the native tab donor; other tabs stay dark.</summary>
    public static MenuRowTone ToneFor(ModsTab tab, ModsTab selected) =>
        tab == selected ? MenuRowTone.Selected : MenuRowTone.Catalog;

    public static void Build(IUiPanel panel, ModsTab selected, Action<ModsTab> onSelect) =>
        Build(new UiPanelTabStripSurface(panel), selected, onSelect);

    /// <summary>
    /// Adds the tab buttons, one per <see cref="Order"/> entry. Each button's click callback selects that same tab
    /// (<see cref="Order"/>[i] → <paramref name="onSelect"/> of that tab). This builder
    /// never calls <c>Close</c> and holds no selection of its own. The listener is bound
    /// by button index, not by row index among all panel rows — a hidden title
    /// (<c>Title.SetActive(false)</c>) must not shift button i onto <see cref="Order"/>[i+1].
    /// </summary>
    public static void Build(IModsTabStripSurface surface, ModsTab selected, Action<ModsTab> onSelect)
    {
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        if (onSelect == null) throw new ArgumentNullException(nameof(onSelect));

        int buttons = 0;
        for (int i = 0; i < Order.Length; i++)
        {
            int index = ClickSlotAmongRows(rowIsButton: true, buttonRowsBeforeThis: buttons);
            ModsTab tab = Order[index];
            surface.StyleNext(ToneFor(tab, selected));
            IUiButton button = surface.Button(Caption(tab));
            button.OnClick(() => onSelect(Order[index]));
            buttons++;
        }
    }

    /// <summary>
    /// <see cref="Order"/> slot for a uGUI <c>onClick</c> on a strip row. Counts only
    /// button rows so a hidden title (or any non-button row) cannot bind button i
    /// to <see cref="Order"/>[i+1]. Removal of the button-row filter is the live
    /// Mods→Install / Install→Configuration advance.
    /// </summary>
    public static int ClickSlotAmongRows(bool rowIsButton, int buttonRowsBeforeThis)
        => rowIsButton && buttonRowsBeforeThis >= 0 && buttonRowsBeforeThis < Order.Length
            ? buttonRowsBeforeThis
            : -1;

    /// <summary>
    /// Center-anchored strip from a left-origin rect in the same space as the pointer
    /// (<c>mods_ui rects</c> / Unity screen). <see cref="HitIndex"/> then splits that
    /// frame into the three tab slots; overlapping row <c>RectTransform</c>s cannot
    /// steal a neighbor's center.
    /// </summary>
    public static MenuPaneGeometry GeometryFromLeftOrigin(float x, float y, float width, float height) =>
        new(width, height, x + width * 0.5f, y + height * 0.5f);

    /// <summary>
    /// Which strip button contains a center-anchored canvas-space point, or -1.
    /// A hit here is never a sidebar row: the strip is its own control list.
    /// Slots are <see cref="Order"/> left-to-right, not the last overlapping row
    /// (a too-wide neighbor used to turn a Mods click into Install).
    /// </summary>
    public static int HitIndex(in MenuPaneGeometry strip, float x, float y)
    {
        float halfH = strip.Height * 0.5f;
        if (y < strip.Y - halfH || y > strip.Y + halfH) return -1;
        for (int i = 0; i < Order.Length; i++)
        {
            var button = MenuPanelLayout.StripButton(strip, i, Order.Length);
            float halfW = button.Width * 0.5f;
            if (x >= button.X - halfW && x <= button.X + halfW) return i;
        }

        return -1;
    }

    /// <summary>False for a strip-button hit: that click must not map onto the sidebar.</summary>
    public static bool MapsToSidebarRow(int stripHit) => stripHit < 0;
}
