using System.Collections.Generic;
using Progenitor.Ui.GameArt;

namespace Progenitor.Ui;

/// <summary>
/// Settings organization (docs/settings-organization.md, "Three homes"): the link rows between the three homes. Each is
/// one settings-grid row: the sentence where a setting's name goes, and a key-cap button where its control goes
/// (<b>Server »</b>, <b>Mods »</b>, else <b>Open</b>). The bridge clicks <c>&lt;key&gt;/open</c>; the readback is
/// <c>page-link {target, landing, label}</c>. Game-free.
/// </summary>
internal static class SettingsLinks
{
    internal const string CellLabel = "label";
    internal const string CellOpen = "open";
    internal const string OpenCaption = "Open";

    /// <summary>Three homes: Progenitor's page's row to the Server tab (under On this server).</summary>
    internal const string ServerTabKey = "link-server";

    /// <summary>Three homes: the Preferences purpose line's two buttons.</summary>
    internal const string PurposeKey = "link-purpose";
    internal const string CellMods = "mods";
    internal const string CellServer = "server";

    internal const string TargetPreferences = "preferences";
    internal const string TargetProgenitorPage = "progenitor-page";
    internal const string TargetServerTab = "server-tab";
    internal const string TargetMods = "mods";
    internal const string LandingTop = "top";

    /// <summary>
    /// The link's button caption: <c>Server »</c> and <c>Mods »</c> name the tab they open (<c>»</c> is the Latin-1
    /// glyph the store link already draws; the game font is not known to have <c>›</c>), else <c>Open</c>.
    /// </summary>
    internal static string CaptionFor(string? target) => target switch
    {
        TargetServerTab => ModsTabStrip.ServerCaption + " »",
        TargetMods => ModsTabStrip.ModsCaption + " »",
        TargetPreferences => ModsTabStrip.PreferencesCaption + " »",
        _ => OpenCaption,
    };

    /// <summary>The link row: the sentence in the name column, the button at the control column's right edge.</summary>
    internal static CellRowSpec Row(string key, string text, string? caption = null) => new(
        key,
        new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellLabel).With(
                text: text ?? "", width: CellWidth.Fill(160f), fontSize: GameSettingsLook.NameText, vAlign: CellVAlign.Middle,
                wrap: CellWrap.Ellipsis),
            new CellSpec(CellKind.Button, CellOpen).With(text: caption ?? OpenCaption, align: CellAlign.Right, skin: CellSkin.KeyCap),
        },
        height: ModSettingsRowGrid.RowPitch, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: ModSettingsRowGrid.NameInset);

    /// <summary>
    /// SERVERLINK1 (requested 2026-10-07: "at the top, add a link to the progenitor MOD server side settings"): the
    /// Server tab's row under its header line that opens Progenitor's page at its On this server group.
    /// </summary>
    internal const string ProgenitorServerKey = "link-progenitor-server";

    internal const string ProgenitorServerText = "Progenitor's own server settings »";

    /// <summary>The same row when this player sees none of those settings (they are for admins): it opens the page's top.</summary>
    internal const string ProgenitorPageText = "Progenitor's page »";

    internal const string ProgenitorPageNote = "Progenitor's own server settings are for this server's admins.";

    /// <summary>The landing of a link that opens Progenitor's page at its On this server group.</summary>
    internal const string LandingOnThisServer = "on-this-server";

    internal const float TextLinkHeight = 30f;
    internal const float TextLinkFontSize = 16f;

    /// <summary>
    /// SERVERLINK1: a text link row: the caption as a link (<see cref="CellSkin.Link"/>: link blue, underlined, no box),
    /// as wide as its caption so only the words take the click, then an optional muted note.
    /// </summary>
    internal static CellRowSpec TextLink(string key, string caption, string? note = null)
    {
        string text = caption ?? "";
        float width = CellLayout.EstimateTextWidth(text, TextLinkFontSize) + 12f;
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Button, CellOpen).With(text: text, width: CellWidth.Px(width), minWidth: width, align: CellAlign.Left,
                fontSize: TextLinkFontSize, skin: CellSkin.Link),
        };
        if (!string.IsNullOrEmpty(note))
            cells.Add(new CellSpec(CellKind.Muted, CellLabel).With(text: note, width: CellWidth.Fill(80f), wrap: CellWrap.Ellipsis,
                vAlign: CellVAlign.Middle));
        else cells.Add(new CellSpec(CellKind.Spacer, "rest"));
        return new CellRowSpec(key, cells, height: TextLinkHeight, plain: true);
    }

    /// <summary>The <c>page-link</c> readback.</summary>
    internal static Dictionary<string, object?> Readback(string target, string landing, string label) => new()
    {
        ["target"] = target ?? "",
        ["landing"] = landing ?? LandingTop,
        ["label"] = label ?? "",
    };
}
