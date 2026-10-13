using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Which donor a workspace tab was drawn from (<c>mods_ui tabs.stripTabSource</c>).</summary>
internal enum WorkspaceTabSource
{
    /// <summary>No strip built yet this Core generation.</summary>
    Unresolved = 0,

    /// <summary>In game: <c>FullscreenMenu.InnerTabGroup.EntryPrefab.Button</c>.</summary>
    FullscreenInner = 1,

    /// <summary>A hidden copy of that donor taken the first time the in-game menu was seen this generation.</summary>
    CachedTemplate = 2,

    /// <summary>A <c>FullscreenMenu_InnerTabEntry</c> already loaded in memory, found by a bounded object scan.</summary>
    ResidentInnerTab = 3,

    /// <summary>No native donor: the composed flat button (the look before TABS2).</summary>
    Flat = 4,
}

/// <summary>
/// TABS2 part B: the donor ladder for the Mods workspace tab strip, as pure decisions.
///
/// <para>
/// The in-game strip clones the game's own inner-tab button (the Spellbook tab). That donor is
/// reached through <c>FullscreenMenu</c>, which does not exist at the title screen, so a title
/// strip used to fall straight to flat buttons. The ladder adds two rungs between the in-game
/// donor and the flat button, both carrying the same native prefab: a hidden template copied
/// while the in-game menu was up earlier in this Core generation, and the prefab itself when it
/// is already resident. Ported from the title-chrome candidate (commit 1f63928c), whose cold
/// (resident) and warm (template) rungs passed live acceptance on 2026-09-13; that candidate's
/// owned framed reproduction is not ported, because it paints new colors and the palette belongs
/// to another session. The flat button stays the last rung.
/// </para>
///
/// <para>
/// <c>UnityUiRuntime.CloneWorkspaceTab</c> asks <see cref="Next"/> once per rung with the rungs
/// already tried marked unreachable, so the order the tests pin is the order that runs.
/// </para>
/// </summary>
internal static class WorkspaceTabLadder
{
    /// <summary>Resident-scan attempts per Core generation; a miss at the title is structural, not timing.</summary>
    internal const int MaxResidentScans = 3;

    /// <summary>Attempts per generation to take the template while the in-game menu is up.</summary>
    internal const int MaxTemplateCaptures = 3;

    internal const string NameUnresolved = "unresolved";
    internal const string NameFullscreenInner = "fullscreen-inner";
    internal const string NameCachedTemplate = "cached-template";
    internal const string NameResidentInnerTab = "resident-inner-tab";
    internal const string NameFlat = "flat";

    internal const string LogPrefix = "Mods workspace: tab source=";

    /// <summary>Source of the most recent strip tab built (production writes it; tests may too).</summary>
    internal static WorkspaceTabSource LastSource { get; private set; }

    private static WorkspaceTabSource _logged = (WorkspaceTabSource)(-1);

    /// <summary>
    /// Next rung given what is still worth trying. Every rung above <see cref="WorkspaceTabSource.Flat"/>
    /// draws the game's own framed inner tab; the flat button is always reachable.
    /// </summary>
    internal static WorkspaceTabSource Next(bool fullscreenReachable, bool templateUsable, bool residentReachable)
    {
        if (fullscreenReachable) return WorkspaceTabSource.FullscreenInner;
        if (templateUsable) return WorkspaceTabSource.CachedTemplate;
        if (residentReachable) return WorkspaceTabSource.ResidentInnerTab;
        return WorkspaceTabSource.Flat;
    }

    /// <summary>A cached template is used only while it is alive and every sprite it draws is still loaded.</summary>
    internal static bool TemplateUsable(bool alive, bool spritesResident) => alive && spritesResident;

    /// <summary>
    /// The same, plus every caption's TMP font asset and every graphic's material still loaded: a
    /// language switch or an unload can leave the sprites alive while a caption's font is gone.
    /// </summary>
    internal static bool TemplateUsable(bool alive, bool spritesResident, bool fontsResident, bool materialsResident) =>
        alive && spritesResident && fontsResident && materialsResident;

    internal static bool MayScanResident(int attempts) => attempts >= 0 && attempts < MaxResidentScans;

    /// <summary>
    /// One resident scan per strip build, shared by its tabs: a strip builds its four tabs in one
    /// frame, so a miss already taken on <paramref name="frame"/> is reused rather than spending
    /// one scan of the generation's budget per tab.
    /// </summary>
    internal static bool MayScanResident(int attempts, int lastMissFrame, int frame) =>
        MayScanResident(attempts) && lastMissFrame != frame;

    internal static bool MayCaptureTemplate(bool templateAlive, int attempts) =>
        !templateAlive && attempts >= 0 && attempts < MaxTemplateCaptures;

    /// <summary>Stable readback names (never enum text).</summary>
    internal static string Name(WorkspaceTabSource source) => source switch
    {
        WorkspaceTabSource.FullscreenInner => NameFullscreenInner,
        WorkspaceTabSource.CachedTemplate => NameCachedTemplate,
        WorkspaceTabSource.ResidentInnerTab => NameResidentInnerTab,
        WorkspaceTabSource.Flat => NameFlat,
        _ => NameUnresolved,
    };

    /// <summary>True when the source is one of the native-donor rungs.</summary>
    internal static bool IsNative(WorkspaceTabSource source) =>
        source is WorkspaceTabSource.FullscreenInner or WorkspaceTabSource.CachedTemplate or WorkspaceTabSource.ResidentInnerTab;

    /// <summary>
    /// Record the rung a tab was built from. Returns the log line when the source changed since the
    /// last line (one line per change, not per tab), else null.
    /// </summary>
    internal static string? Note(WorkspaceTabSource source)
    {
        LastSource = source;
        if (source == _logged) return null;
        _logged = source;
        return LogPrefix + Name(source);
    }

    /// <summary>Generation end: forget the recorded source.</summary>
    internal static void Reset()
    {
        LastSource = WorkspaceTabSource.Unresolved;
        _logged = (WorkspaceTabSource)(-1);
    }
}

/// <summary>
/// Hover against selected on the cloned native tab (review 2026-09-30: "Mods" and "Install Mods" both
/// drew red). The clone keeps the game's own state machine: <c>FullscreenMenu_InnerTabEntry.Refresh</c>
/// marks the selected tab with <c>ForceHighlight</c>, which is the Highlighted state, and a pointer over
/// any tab is the same Highlighted state, so a hovered tab looked exactly like the selected one. On an
/// unselected tab's own copy, each color-tint target's highlighted color is moved part of the way back
/// toward its normal color (<see cref="HoverShare"/>), so hover is a lighter state than selected; the
/// selected tab keeps the donor's full highlighted color. Sprite-swap targets are left as the donor has
/// them and reported (<c>Mods workspace: strip tab targets</c>). Game-free.
/// </summary>
internal static class WorkspaceTabHover
{
    /// <summary>How much of the way from normal to the donor's highlighted color a hover goes.</summary>
    internal const float HoverShare = 0.45f;

    internal static (float R, float G, float B, float A) HoverColor(
        (float R, float G, float B, float A) normal, (float R, float G, float B, float A) highlighted)
    {
        static float L(float a, float b) => a + (b - a) * HoverShare;
        return (L(normal.R, highlighted.R), L(normal.G, highlighted.G), L(normal.B, highlighted.B), L(normal.A, highlighted.A));
    }

    /// <summary>Only an unselected tab's hover is dimmed; the selected tab shows the donor's highlight.</summary>
    internal static bool DimsHover(bool selected) => !selected;
}

/// <summary>
/// TABCAPS1 (user report 2026-10-03: once, the workspace strip showed its four tab buttons with no
/// text in them). The strip's tabs are clones of a game tab whose caption is the clone's own TMP,
/// and nothing re-checked that TMP after the build. On every strip show the runtime reads each tab's
/// caption into an <see cref="Observation"/>; the faults here decide whether it is repaired, and the
/// same observation is the <c>mods_ui tabs</c> readback (<c>stripCaptions</c>). Game-free.
/// </summary>
internal static class WorkspaceTabCaptions
{
    /// <summary>A caption whose effective alpha is under this draws as nothing.</summary>
    internal const float MinAlpha = 0.05f;

    internal const string FaultNoLabel = "no-label";
    internal const string FaultInactive = "inactive";
    internal const string FaultDisabled = "disabled";
    internal const string FaultText = "text";
    internal const string FaultFont = "font";
    internal const string FaultGlyphs = "glyphs";
    internal const string FaultTransparent = "transparent";

    /// <summary>What one tab's caption looks like now.</summary>
    internal readonly struct Observation
    {
        internal Observation(
            string tab, string intended, bool labelFound, bool active, bool enabled, string text,
            bool fontAlive, bool glyphsPresent, float alpha, string font = "", int labels = 1)
        {
            Tab = tab ?? "";
            Intended = intended ?? "";
            LabelFound = labelFound;
            Active = active;
            Enabled = enabled;
            Text = text ?? "";
            FontAlive = fontAlive;
            GlyphsPresent = glyphsPresent;
            Alpha = alpha;
            Font = font ?? "";
            Labels = labels;
        }

        internal string Tab { get; }
        internal string Intended { get; }
        internal bool LabelFound { get; }
        internal bool Active { get; }
        internal bool Enabled { get; }
        internal string Text { get; }
        internal bool FontAlive { get; }
        internal bool GlyphsPresent { get; }
        internal float Alpha { get; }
        internal string Font { get; }
        internal int Labels { get; }
    }

    /// <summary>Every reason this caption would not show its intended words; empty when it does.</summary>
    internal static List<string> Faults(in Observation o)
    {
        var faults = new List<string>();
        if (o.Intended.Length == 0) return faults; // not a tab of the known strip: nothing to hold it to
        if (!o.LabelFound)
        {
            faults.Add(FaultNoLabel);
            return faults;
        }
        if (!o.Active) faults.Add(FaultInactive);
        if (!o.Enabled) faults.Add(FaultDisabled);
        if (!string.Equals(o.Text, o.Intended, System.StringComparison.Ordinal)) faults.Add(FaultText);
        if (!o.FontAlive) faults.Add(FaultFont);
        else if (!o.GlyphsPresent) faults.Add(FaultGlyphs);
        if (float.IsNaN(o.Alpha) || o.Alpha < MinAlpha) faults.Add(FaultTransparent);
        return faults;
    }

    /// <summary>The line a repair logs: <c>Ui: tab caption repaired &lt;tab&gt; (was '&lt;old&gt;')</c> and the faults.</summary>
    internal static string RepairLine(in Observation o, IReadOnlyList<string> faults) =>
        "Ui: tab caption repaired " + o.Tab + " (was '" + o.Text + "') faults=" + string.Join(",", faults)
        + " font=" + (o.Font.Length > 0 ? o.Font : "none") + " alpha=" + o.Alpha.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        + " source=" + WorkspaceTabLadder.Name(WorkspaceTabLadder.LastSource);

    /// <summary>One tab's readback: intended and rendered text, the parts that decide visibility, and the faults.</summary>
    internal static Dictionary<string, object?> Readback(in Observation o) => new(System.StringComparer.Ordinal)
    {
        ["tab"] = o.Tab,
        ["intended"] = o.Intended,
        ["text"] = o.Text,
        ["labelFound"] = o.LabelFound,
        ["labels"] = o.Labels,
        ["active"] = o.Active,
        ["enabled"] = o.Enabled,
        ["font"] = o.Font,
        ["fontAlive"] = o.FontAlive,
        ["glyphs"] = o.GlyphsPresent,
        ["alpha"] = System.Math.Round(o.Alpha, 3),
        ["faults"] = Faults(o),
        ["ok"] = Faults(o).Count == 0,
    };

    /// <summary>Repairs this Core generation (readback <c>stripCaptionRepairs</c>), and the last repair line.</summary>
    internal static int Repairs { get; private set; }

    internal static string LastRepair { get; private set; } = "";

    internal static void NoteRepair(string line)
    {
        Repairs++;
        LastRepair = line ?? "";
    }

    internal static void Reset()
    {
        Repairs = 0;
        LastRepair = "";
    }
}
