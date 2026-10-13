using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Job MODSPAGE1: every word the Mods-tab mod page shows, from the approved mock
/// (2026-09-13). Copy lives
/// here rather than on <c>InstallModsCopy</c>, which the Install tab owns.
///
/// <para><b>Glyphs.</b> Only WGL4 characters are used. The vanilla TMP font draws anything else as
/// the missing-glyph box — live evidence for <c>►</c>/<c>▾</c> is in docs/mods-menu.md — so the
/// mock's ♡ (U+2661, not in WGL4) is not used: an unpressed Like is the bare word and a pressed one
/// carries ♥ (U+2665, WGL4). The sidebar dots are ● / ○ (U+25CF / U+25CB, WGL4) because the kit has
/// no per-row color; see <see cref="ModsMenu"/> and the kit gaps in docs/mods-menu.md.</para>
/// </summary>
internal static class ModPageCopy
{
    // ---- status ---------------------------------------------------------------------------

    /// <summary>The status label is a label, never a button: <c>Status: Installed</c>.</summary>
    internal const string StatusPrefix = "Status: ";

    internal const string StatusInstalled = "Installed";
    internal const string StatusPendingLoad = "Pending restart · to load";
    internal const string StatusPendingUninstall = "Pending restart · to uninstall";

    internal static string StatusLine(string word) => StatusPrefix + (word ?? "");

    // ---- actions --------------------------------------------------------------------------

    internal const string Uninstall = "Uninstall";
    /// <summary>INSTALLCOLS1: the pending uninstall's button, the same words as the Install tab's (was "Reactivate").</summary>
    internal const string CancelUninstall = InstallModsCopy.CancelUninstall;
    internal const string CancelInstall = "Cancel install";

    // ---- side badges ----------------------------------------------------------------------

    internal const string BadgeClient = "Client";
    internal const string BadgeServer = "Server";

    /// <summary>A Progenitor package whose manifest requires the server half as well.</summary>
    internal const string BadgeServerRequired = "Server · required";

    // MODSPOLISH1: there is no "Unknown until checked" badge any more — see ModPageModel.Badges.

    // ---- the muted provenance block --------------------------------------------------------

    /// <summary>
    /// MODSPOLISH1: the one muted block under the head — the row's read-only sentence, what the
    /// server asks of it, and (server-only rows) why its loading is unconfirmed. Each sentence is
    /// given its full stop and they are joined with one space, so the page reads as a paragraph
    /// rather than as three full-size rows repeating the head. Empty when there is nothing to say.
    /// </summary>
    internal static string ProvenanceBlock(params string?[]? sentences)
    {
        if (sentences == null) return "";
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < sentences.Length; i++)
        {
            string sentence = (sentences[i] ?? "").Trim();
            if (sentence.Length == 0) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(sentence);
            char last = sentence[sentence.Length - 1];
            if (last != '.' && last != '!' && last != '?') text.Append('.');
        }

        return text.ToString();
    }

    // ---- the required-server warning ------------------------------------------------------

    /// <summary>Sidebar marker for a Progenitor mod whose server half is missing.</summary>
    internal const string WarningMarker = "(!)";

    /// <summary>
    /// The warning line's body, without the marker. KIT1 draws the marker as its own red cell in
    /// front of the line, so the body is what the text cell holds; the readback still reports
    /// <see cref="WarningServerHalfMissing"/>, which is the whole line as a reader would read it.
    /// </summary>
    internal const string WarningServerHalfMissingBody =
        "This mod needs its server component and only the client half is installed here."
        + " It will not work on the servers you join until the server has it too.";

    internal const string WarningServerHalfMissing = WarningMarker + " " + WarningServerHalfMissingBody;

    /// <summary>REMOTEINST1: the reverse warning — the server has this mod and this client does not.</summary>
    internal const string WarningClientHalfMissingBody =
        "The server has this mod and this client does not. It needs both sides, so it will not work here until the client half is installed.";

    internal const string WarningClientHalfMissing = WarningMarker + " " + WarningClientHalfMissingBody;

    // ---- sidebar state words ----------------------------------------------------------------

    // HEAD2 (user, 2026-09-13): `DotTone`, which mapped a state to the sidebar dot's color, is
    // gone with the dot itself — "lets not use the lights (yellow in this case) on the listing in
    // the menu on the left". `ModsMenu.SidebarDotOf` is now the one place that answers "no dot",
    // and nothing else asked for a tone. The two words below stay: they are the state names a
    // reader sees, not a color instruction.

    /// <summary>Yellow in the mock: installed, waiting for a restart to load. Kept as the readback word.</summary>
    internal const string DotPendingLoad = "pending";

    /// <summary>Orange in the mock: still loaded, removed at the next restart.</summary>
    internal const string DotPendingUninstall = "orange";

    // ---- prompts (SERVERINST2 shape: heading, body, yes, no) --------------------------------

    internal static string UninstallHeading(string name, string version) =>
        "Uninstall " + Named(name, version) + "?";

    /// <summary>REQRULE6: <c>Uninstall HotDemo 0.1.0 from this game and Progenitor Forms Test?</c></summary>
    internal static string UninstallHeadingFrom(string name, string version, string target) =>
        string.IsNullOrEmpty(target) ? UninstallHeading(name, version) : "Uninstall " + Named(name, version) + " from " + target + "?";

    /// <summary>An ordinary (non-hot) mod: the files go at the next restart and it stays loaded.</summary>
    internal const string UninstallBody =
        "Removed from your client at the next restart of V Rising. It stays loaded until then."
        + " Your settings for it are kept.";

    /// <summary>A hot-loaded Progenitor mod: the reloader drops it within a second.</summary>
    internal const string UninstallBodyHot =
        "Unloads now; nothing to restart. Your settings for it are kept.";

    /// <summary>Appended for a server-side mod that is also installed on the local server.</summary>
    internal const string UninstallBodyServerHalf =
        " Also removed from your local server at its next restart.";

    /// <summary>
    /// FWALLOW1: a plugin in <c>BepInEx/progenitor-reload</c> that no install ledger lists (a sample,
    /// an example, a copied build). The file is deleted now and the reloader unloads it.
    /// </summary>
    internal static string UninstallBodyReload(string fileName, IReadOnlyList<string>? alsoRemoves)
    {
        string file = string.IsNullOrWhiteSpace(fileName) ? "its file" : fileName.Trim();
        string body = "Installed outside Progenitor's Install tab. Deletes " + file
            + " from BepInEx/progenitor-reload; it unloads now, nothing to restart. Your settings for it are kept.";
        if (alsoRemoves != null && alsoRemoves.Count > 0)
            body += " The same file also holds " + string.Join(", ", alsoRemoves) + ", which go with it.";
        return body;
    }

    internal const string UninstallYes = "Yes, uninstall";
    internal const string UninstallNo = "No, keep it";

    internal static string ReactivateHeading(string name, string version) =>
        "Keep " + Named(name, version) + " installed?";

    internal const string ReactivateBody =
        "Cancels the pending removal. Nothing changes at the next restart.";

    internal const string ReactivateYes = "Yes, keep it";
    internal const string PromptNo = "No";

    internal static string CancelInstallHeading(string name, string version) =>
        "Cancel installing " + Named(name, version) + "?";

    internal const string CancelInstallBody =
        "The downloaded file is deleted; nothing loads at the next restart.";

    internal const string CancelInstallYes = "Yes, cancel";

    /// <summary>Shown instead of a prompt when no remover seam is wired (INSTALL2 owns it).</summary>
    internal const string ActionUnavailable =
        "This action is not available yet: nothing here can change the pending state.";

    /// <summary>SRVUNINST1: the page's sentence after an uninstall that marked the copy for the next start.</summary>
    internal const string RemovesAtRestartNote = "Removes at the next restart; it stays loaded until then.";

    /// <summary>
    /// F36 (live 2026-10-08): the page's sentence when the seam answered OK but changed nothing (no copy matched): never
    /// the success sentence.
    /// </summary>
    internal const string NothingChangedNote =
        "Nothing was changed: no installed copy of this mod matched its install record.";

    /// <summary>
    /// A seam result as the page's one sentence: its own message on a refusal, <see cref="NothingChangedNote"/> when it
    /// changed nothing (F36), else <paramref name="done"/>.
    /// </summary>
    internal static string SeamNote(Install.ServerInstallResult result, string done) =>
        result == null ? InstallModsCopy.RemoveFailed
        : !result.Ok ? (result.Message.Length > 0 ? result.Message : InstallModsCopy.RemoveFailed)
        : result.NoOp ? NothingChangedNote
        : done;

    /// <summary>The page's sentence after a confirmed Reactivate.</summary>
    internal const string ReactivatedNote = "The pending removal is canceled.";

    /// <summary>The page's sentence after a confirmed Cancel install.</summary>
    internal const string CancelledNote = "The pending install is canceled; nothing loads at the next restart.";

    /// <summary>No listing names this plugin, so there is no package page to open.</summary>
    internal const string NoStorePage = "No store page is known for this mod.";

    private static string Named(string? name, string? version)
    {
        string n = (name ?? "").Trim();
        string v = (version ?? "").Trim();
        return v.Length == 0 ? n : n + " " + v;
    }

    // ---- settings and the store link ------------------------------------------------------

    internal const string SettingsHeading = "Settings";

    /// <summary>
    /// SETTINGS1 stage 2: the store link's caption — a normal button in the mod header's button row,
    /// shown when a store url is known (live 2026-09-29 the KIT1 <c>»</c> icon read as a stray mark at
    /// the row's end). <see cref="StoreLinkTooltip"/> is what it does; the readback carries both plus
    /// the url.
    /// </summary>
    internal const string StoreLinkCaption = "Store page";

    internal const string StoreLinkTooltip = "Open the store page";

    internal const string NoProgenitorSettings =
        "This mod does not expose Progenitor settings."
        + " Configure it the way its author describes on the store page.";

    /// <summary>No listing is known for this plugin, so there is no README to render.</summary>
    internal const string NoDescription = "no description available";

}
