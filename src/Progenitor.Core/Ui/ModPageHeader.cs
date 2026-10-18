using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// SETTINGS1 (docs/mod-settings.md): the run status the framework line of a mod page reports. The
/// first four are the user's decision of 2026-09-29; <see cref="NotOnClient"/> covers a row only the
/// joined server listed, which has nothing on this client to enable.
/// </summary>
internal enum ModRunStatus
{
    Enabled = 0,

    /// <summary>The player switched it off (stage 3: <see cref="ModSwitch"/>), and it is not running.</summary>
    Disabled = 1,

    /// <summary>Per-server mod pause (<see cref="ServerModPause"/>): unloaded while on a server that does not run it.</summary>
    Paused = 2,

    /// <summary>Written but not loaded yet, or still loaded and removed at the next restart.</summary>
    PendingRestart = 3,

    /// <summary>Only the server listed it; no copy on this client.</summary>
    NotOnClient = 4,

    /// <summary>Stage 3: a critical problem (<c>OnLoad</c> threw). Beats every other status but <see cref="NotOnClient"/>.</summary>
    FailedToLoad = 5,

    /// <summary>Stage 3: a warning that is not a conflict (a translation or content file did not load).</summary>
    LoadedWithProblems = 6,
}

/// <summary>Which processes a mod runs on, when anything says so.</summary>
internal enum ModSides
{
    Unknown = 0,
    Client = 1,
    Server = 2,
    Both = 3,
}

/// <summary>For a two-sided mod: is its server part running on the server this client is joined to.</summary>
internal enum ServerPartState
{
    /// <summary>Not known (not connected, no handshake, a one-sided mod). The line omits it.</summary>
    Unknown = 0,

    /// <summary>The server's handshake request named this mod: the server runs it.</summary>
    Running = 1,

    /// <summary>The mod is paused for this server, which is what a pause means.</summary>
    NotOnServer = 2,
}

/// <summary>The evidence one framework line is composed from. Plain data; see <see cref="ModFrameworkLine.Compose"/>.</summary>
internal sealed class ModFrameworkFacts
{
    internal string PluginId { get; init; } = "";

    /// <summary>The player's remembered on/off choice: the switch's position (stage 3: <see cref="ModSwitch"/>).</summary>
    internal bool EnabledIntent { get; init; } = true;

    /// <summary>Whether the switch can act for this mod (<see cref="ModSwitchView.Interactive"/>).</summary>
    internal bool SwitchAvailable { get; init; }

    /// <summary>Stage 3: why the switch cannot act; empty uses <see cref="ModFrameworkLine.SwitchUnavailableReason"/>.</summary>
    internal string SwitchReason { get; init; } = "";

    /// <summary>Stage 3: how the switch acts (live, restart, unavailable, refused, hidden).</summary>
    internal ModSwitchMode SwitchMode { get; init; } = ModSwitchMode.Unavailable;

    /// <summary>Stage 3: a change the switch (or an uninstall request) left for the next start.</summary>
    internal ModSwitchPending SwitchPending { get; init; }

    /// <summary>Stage 3: off by the player's choice and not running now (<see cref="ModSwitchView.Disabled"/>).</summary>
    internal bool SwitchedOff { get; init; }

    /// <summary>Stage 3: off because this dependency (its name) is off (<see cref="ModSwitchView.OffBecause"/>).</summary>
    internal string OffBecause { get; init; } = "";

    /// <summary>Stage 3: this mod's problems, worst first (<see cref="ModProblems.For"/>).</summary>
    internal IReadOnlyList<ModProblem> Problems { get; init; } = Array.Empty<ModProblem>();

    /// <summary>
    /// Stage 3: the mod declares it runs without its server part (<c>[Counterpart(Limited)]</c>) and
    /// the server part is known to be absent: the server-part cell says <c>… · limited</c>.
    /// </summary>
    internal bool Limited { get; set; }

    /// <summary>Stage 3: a failed hot-loadable mod can be loaded again now (<see cref="ModSwitch.CanRetry"/>).</summary>
    internal bool CanRetry { get; init; }

    internal bool OnClient { get; init; } = true;

    internal bool SharesServerSettings { get; init; }

    internal bool Paused { get; init; }

    internal bool Resuming { get; init; }

    internal ModPageState PageState { get; init; } = ModPageState.Installed;

    /// <summary>The version the loaded plugin reports, else the installed file's.</summary>
    internal string Version { get; init; } = "";

    internal ModSides Sides { get; init; }

    internal ServerPartState ServerPart { get; set; }

    /// <summary>The framework's action button (Uninstall / Reactivate / Cancel install), or null.</summary>
    internal string? ActionCaption { get; init; }

    internal LabelTone ActionTone { get; init; } = LabelTone.Normal;

    /// <summary>
    /// SRVUNINST1: why the action button cannot act (drawn disabled, not clickable); empty while it can.
    /// </summary>
    internal string ActionDisabledReason { get; private set; } = "";

    /// <summary>The same facts with the action button disabled for <paramref name="reason"/>.</summary>
    internal ModFrameworkFacts WithActionDisabled(string reason)
    {
        var copy = (ModFrameworkFacts)MemberwiseClone();
        copy.ActionDisabledReason = string.IsNullOrWhiteSpace(reason) ? InstallModsCopy.RemoveFailed : reason.Trim();
        return copy;
    }

    /// <summary>
    /// SETTINGS1 stage 2: a setting marked needs-restart on this mod's page differs from the value
    /// it had when the mod loaded, so the change takes effect only at the next start.
    /// </summary>
    internal bool SettingsPendingRestart { get; set; }

    /// <summary>
    /// COUNTERPART-SAMPLES: the same facts with the server part replaced — the page rewrites its status
    /// bar in place when the joined server's handshake evidence arrives or ends.
    /// </summary>
    internal ModFrameworkFacts WithServerPart(ServerPartState part, bool limited)
    {
        var copy = (ModFrameworkFacts)MemberwiseClone();
        copy.ServerPart = part;
        copy.Limited = limited;
        return copy;
    }

    /// <summary>
    /// INSTALLSIDES1: the Install tab's decision for this mod's listing — this client's cell and the
    /// picked server's — so the page states each side in the same words as the Install row. Null when
    /// the mod has no catalog listing (the page then shows what it always showed).
    /// </summary>
    internal SideCellsDecision? PerSide { get; private set; }

    /// <summary>INSTALLSIDES1: the picked server, as the Install tab's Server column names it.</summary>
    internal string ServerColumnName { get; private set; } = "";

    /// <summary>The same facts with the per-side cells attached.</summary>
    internal ModFrameworkFacts WithPerSide(SideCellsDecision? perSide, string serverColumnName)
    {
        var copy = (ModFrameworkFacts)MemberwiseClone();
        copy.PerSide = perSide;
        copy.ServerColumnName = serverColumnName ?? "";
        return copy;
    }

    /// <summary>The same facts with <see cref="SettingsPendingRestart"/> replaced.</summary>
    internal ModFrameworkFacts WithSettingsPendingRestart(bool pending)
    {
        var copy = (ModFrameworkFacts)MemberwiseClone();
        copy.SettingsPendingRestart = pending;
        return copy;
    }

}

/// <summary>
/// SETTINGS1: the top line of every mod page. Framework-owned: a mod's own header content is drawn
/// under it and has no way to change it (<see cref="ModMenuRegistration.Header"/>). It carries the
/// on/off switch, the run status, the installed version, the sides and — for a two-sided mod whose
/// server part is knowable — whether that part runs on this server. Game-free, so each status has a
/// test; <see cref="BuildRow"/> turns it into the one cell row the page draws.
/// </summary>
internal sealed class ModFrameworkLine
{
    // Cell keys. The status word, version and action reuse the head block's names so a reader that
    // addressed them there finds them here.
    internal const string CellSwitch = "switch";
    internal const string CellStatusWord = HeadBlockLayout.CellStatusWord;
    internal const string CellVersion = HeadBlockLayout.CellVersion;
    internal const string CellSides = HeadBlockLayout.CellBadge + "-0";
    internal const string CellServerPart = "server-part";

    /// <summary>INSTALLSIDES1: this client's side and the picked server's, in the Install row's words.</summary>
    internal const string CellClientSide = "side-client";

    /// <inheritdoc cref="CellClientSide"/>
    internal const string CellServerSide = "side-server";

    /// <summary>The Client cell's label on the page.</summary>
    internal const string ClientSideLabel = "Client";
    internal const string CellAction = HeadBlockLayout.CellAction;
    internal const string CellGap = HeadBlockLayout.CellGap;

    internal const string SwitchOnCaption = "On";
    internal const string SwitchOffCaption = "Off";

    /// <summary>Why the switch cannot act when nothing more specific is known. Carried in the readback.</summary>
    internal const string SwitchUnavailableReason = "This mod cannot be switched from here.";

    internal const string WordEnabled = "Enabled";
    internal const string WordDisabled = "Disabled";
    internal const string WordNotOnClient = "Not on this client";

    /// <summary>Stage 3: a critical problem.</summary>
    internal const string WordFailed = "Failed to load";

    /// <summary>Stage 3: a warning that is not a conflict.</summary>
    internal const string WordProblems = "Loaded with problems";

    /// <summary>Stage 3: the conflict chip's cell key.</summary>
    internal const string CellConflicts = "conflicts";

    /// <summary>SETTINGS1 stage 2: a needs-restart setting changed since the mod loaded.</summary>
    internal const string WordSettingsRestart = "Pending restart · settings changed";

    // SETTINGS1 stage 2 (user redesign 2026-09-29): the sides chip and the server part in plain words.
    internal const string SidesClient = "Client only";
    internal const string SidesServer = "Server only";
    internal const string SidesBoth = "Client and server";

    internal const string ServerPartRunning = "Server part running";
    internal const string ServerPartMissing = "Server part not on this server";

    /// <summary>The version cell's prefix: <c>v0.1.0</c>, plain text.</summary>
    internal const string VersionPrefix = "v";

    private ModFrameworkLine()
    {
    }

    internal string PluginId { get; private init; } = "";

    /// <summary>False for a row with nothing on this client to switch.</summary>
    internal bool SwitchVisible { get; private init; }

    internal bool SwitchOn { get; private init; }

    /// <summary>Stage 1: always false. The switch shows the real position and does nothing when pressed.</summary>
    internal bool SwitchInteractive { get; private init; }

    internal ModRunStatus Status { get; private init; }

    /// <summary>Stage 3: why the switch cannot act; empty while it can.</summary>
    internal string SwitchReason { get; private init; } = "";

    /// <summary>Stage 3: the conflict chip (<c>2 conflicts</c>), or empty.</summary>
    internal string ConflictChip { get; private init; } = "";

    internal string StatusWord { get; private init; } = "";

    internal LabelTone StatusTone { get; private init; }

    internal ModPageState PageState { get; private init; }

    internal string Version { get; private init; } = "";

    internal ModSides Sides { get; private init; }

    /// <summary><c>Client</c> / <c>Server</c> / <c>Both</c>, or empty when unknown (the cell is omitted).</summary>
    internal string SidesWord { get; private init; } = "";

    internal ServerPartState ServerPart { get; private init; }

    /// <summary>The server-part sentence, or empty when omitted (one-sided mod, or unknown).</summary>
    internal string ServerPartText { get; private init; } = "";

    internal string? ActionCaption { get; private init; }

    internal LabelTone ActionTone { get; private init; }

    /// <summary>SRVUNINST1: the action button can act (else it is drawn disabled and <see cref="ActionReason"/> says why).</summary>
    internal bool ActionEnabled => Facts.ActionDisabledReason.Length == 0;

    /// <summary>SRVUNINST1: why the action button is disabled; empty while it is enabled.</summary>
    internal string ActionReason => Facts.ActionDisabledReason;

    /// <summary>The evidence this line was composed from, so a page can recompose it in place.</summary>
    internal ModFrameworkFacts Facts { get; private init; } = new();

    /// <summary>
    /// Why a <see cref="ModRunStatus.PendingRestart"/> is owed: <c>load</c>, <c>uninstall</c> or
    /// <c>settings</c>; empty for any other status.
    /// </summary>
    internal string RestartReason { get; private init; } = "";

    /// <summary>The version as drawn: <c>v1.2.0</c>, or empty when unknown.</summary>
    internal string VersionText => VersionTextOf(Version);

    /// <summary><c>v</c> + the version, unless it already starts with one; empty stays empty.</summary>
    internal static string VersionTextOf(string? version)
    {
        string v = (version ?? "").Trim();
        if (v.Length == 0) return "";
        return v.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? v : VersionPrefix + v;
    }

    /// <summary>The readback names of <see cref="ModRunStatus"/>.</summary>
    internal static string NameOf(ModRunStatus status) => status switch
    {
        ModRunStatus.FailedToLoad => "failed",
        ModRunStatus.LoadedWithProblems => "problems",
        ModRunStatus.Disabled => "disabled",
        ModRunStatus.Paused => "paused",
        ModRunStatus.PendingRestart => "pending-restart",
        ModRunStatus.NotOnClient => "not-on-client",
        _ => "enabled",
    };

    internal static string NameOf(ModSides sides) => sides switch
    {
        ModSides.Client => "client",
        ModSides.Server => "server",
        ModSides.Both => "both",
        _ => "unknown",
    };

    internal static string NameOf(ServerPartState part) => part switch
    {
        ServerPartState.Running => "running",
        ServerPartState.NotOnServer => "not-on-server",
        _ => "unknown",
    };

    /// <summary>
    /// The status, in order: nothing on this client; a pause; a restart owed (a written-but-unloaded
    /// install or a pending removal, then — stage 2 — a needs-restart setting changed since load);
    /// the player's switch; else enabled. A pause wins over a restart because a paused mod is
    /// unloaded now, which is the more urgent fact about this session.
    /// </summary>
    /// <remarks>
    /// Stage 3 (docs/mod-settings.md, "Mod problems"): the worst problem first — <c>Failed to load</c>
    /// beats <c>Loaded with problems</c> beats everything below; a conflict never changes the word
    /// (it adds a chip). Then the player's switch: a mod the player turned off is <c>Disabled</c> even
    /// while a server would pause it. Then the pause, then a restart owed (an install or removal, a
    /// switch change for the next start, a needs-restart setting).
    /// </remarks>
    internal static ModRunStatus DecideStatus(ModFrameworkFacts facts)
    {
        if (facts == null) return ModRunStatus.Enabled;
        if (!facts.OnClient) return ModRunStatus.NotOnClient;
        var worst = ModProblems.WorstNonConflict(facts.Problems);
        if (worst?.Severity == ModProblemSeverity.Critical) return ModRunStatus.FailedToLoad;
        if (worst?.Severity == ModProblemSeverity.Warning) return ModRunStatus.LoadedWithProblems;
        if (!facts.EnabledIntent && facts.SwitchPending == ModSwitchPending.None) return ModRunStatus.Disabled;
        if (facts.Paused || facts.Resuming) return ModRunStatus.Paused;
        if (facts.PageState != ModPageState.Installed) return ModRunStatus.PendingRestart;
        if (facts.SwitchPending != ModSwitchPending.None) return ModRunStatus.PendingRestart;
        if (facts.SettingsPendingRestart) return ModRunStatus.PendingRestart;
        return ModRunStatus.Enabled;
    }

    /// <summary>Why a restart is owed, for the readback: <c>load</c>, <c>uninstall</c>, <c>settings</c> or empty.</summary>
    internal static string RestartReasonOf(ModRunStatus status, ModFrameworkFacts facts)
    {
        if (status != ModRunStatus.PendingRestart || facts == null) return "";
        return facts.PageState switch
        {
            ModPageState.PendingLoad => "load",
            ModPageState.PendingUninstall => "uninstall",
            _ => facts.SwitchPending switch
            {
                ModSwitchPending.TurnsOff => "switch-off",
                ModSwitchPending.TurnsOn => "switch-on",
                ModSwitchPending.Removed => "remove",
                _ => facts.SettingsPendingRestart ? "settings" : "",
            },
        };
    }

    /// <summary>
    /// The sides, from the strongest evidence first: a Progenitor manifest that requires both, then
    /// the listing's derived side, then this plugin's own network declaration (a client-only
    /// declaration is a client mod; one the server must have runs on both). Anything else is unknown.
    /// </summary>
    internal static ModSides DecideSides(
        PackageSide listingSide, bool requiresBoth, NetworkCompatibilityLevel? declared = null)
    {
        if (requiresBoth) return ModSides.Both;
        switch (listingSide)
        {
            case PackageSide.Both: return ModSides.Both;
            case PackageSide.Server: return ModSides.Server;
            case PackageSide.Client: return ModSides.Client;
        }

        return declared switch
        {
            NetworkCompatibilityLevel.ClientOnly => ModSides.Client,
            NetworkCompatibilityLevel.ServerMustHave => ModSides.Both,
            _ => ModSides.Unknown,
        };
    }

    /// <summary>
    /// Only a two-sided mod has a server part to report. A pause means the server does not run it;
    /// a handshake request that named it means the server does. Everything else is unknown and the
    /// line says nothing rather than guess.
    /// </summary>
    internal static ServerPartState DecideServerPart(ModSides sides, bool paused, bool serverAskedAboutIt, bool knownAbsent = false)
    {
        if (sides != ModSides.Both) return ServerPartState.Unknown;
        if (paused) return ServerPartState.NotOnServer;
        if (serverAskedAboutIt) return ServerPartState.Running;
        // Stage 3: the server sent its full handshake request and this mod was not in it.
        if (knownAbsent) return ServerPartState.NotOnServer;
        return ServerPartState.Unknown;
    }

    /// <summary>
    /// Stage 3: the sides with a counterpart declaration. A mod that declares a counterpart has a part
    /// on each side, whatever its (lowered) handshake level says.
    /// </summary>
    internal static ModSides DecideSides(
        PackageSide listingSide, bool requiresBoth, NetworkCompatibilityLevel? declared, bool hasCounterpart)
    {
        var sides = DecideSides(listingSide, requiresBoth, declared);
        return hasCounterpart && sides == ModSides.Unknown ? ModSides.Both : sides;
    }

    /// <summary>The status word for these facts: the one source the list, the page and the dialogs use.</summary>
    internal static string WordOf(ModRunStatus status, ModFrameworkFacts facts)
    {
        facts ??= new ModFrameworkFacts();
        if (status == ModRunStatus.PendingRestart && facts.PageState == ModPageState.Installed
            && facts.SwitchPending != ModSwitchPending.None)
            return ModSwitch.WordOf(facts.SwitchPending);
        if (status == ModRunStatus.Disabled && facts.OffBecause.Length > 0)
            return Progenitor.Plugins.ModSwitchWords.OffBecauseWord(facts.OffBecause);
        if (status == ModRunStatus.NotOnClient && facts.SharesServerSettings) return "Runs on this server";
        return WordOf(status, facts.PageState, facts.Resuming);
    }

    internal static string WordOf(ModRunStatus status, ModPageState pageState, bool resuming) => status switch
    {
        ModRunStatus.FailedToLoad => WordFailed,
        ModRunStatus.LoadedWithProblems => WordProblems,
        ModRunStatus.Disabled => WordDisabled,
        ModRunStatus.Paused => resuming ? ModMenuInventory.StatusResuming : ModMenuInventory.StatusPaused,
        ModRunStatus.PendingRestart => pageState == ModPageState.Installed
            ? WordSettingsRestart
            : ModPageStates.WordOf(pageState),
        ModRunStatus.NotOnClient => WordNotOnClient,
        _ => WordEnabled,
    };

    internal static LabelTone ToneOf(ModRunStatus status, ModFrameworkFacts facts)
    {
        facts ??= new ModFrameworkFacts();
        if (status == ModRunStatus.PendingRestart && facts.PageState == ModPageState.Installed)
        {
            if (facts.SwitchPending == ModSwitchPending.Removed) return LabelTone.Orange;
            if (facts.SwitchPending != ModSwitchPending.None) return LabelTone.Pending;
        }

        return ToneOf(status, facts.PageState);
    }

    internal static LabelTone ToneOf(ModRunStatus status, ModPageState pageState) => status switch
    {
        ModRunStatus.FailedToLoad => LabelTone.Danger,
        ModRunStatus.LoadedWithProblems => LabelTone.Warn,
        ModRunStatus.Disabled or ModRunStatus.NotOnClient => LabelTone.Muted,
        ModRunStatus.Paused => LabelTone.Warn,
        ModRunStatus.PendingRestart => pageState == ModPageState.Installed
            ? LabelTone.Pending
            : ModPagePane.StatusTone(pageState),
        _ => LabelTone.Ok,
    };

    internal static string SidesWordOf(ModSides sides) => sides switch
    {
        ModSides.Client => SidesClient,
        ModSides.Server => SidesServer,
        ModSides.Both => SidesBoth,
        _ => "",
    };

    internal static CellTone SidesTone(ModSides sides) => sides switch
    {
        ModSides.Client => CellTone.Client,
        ModSides.Server => CellTone.Server,
        ModSides.Both => CellTone.Both,
        _ => CellTone.Muted,
    };

    internal static string ServerPartTextOf(ServerPartState part) => part switch
    {
        ServerPartState.Running => ServerPartRunning,
        ServerPartState.NotOnServer => ServerPartMissing,
        _ => "",
    };

    /// <summary>Stage 3: <c>Server part not on this server · limited</c> for a mod that runs without it.</summary>
    internal static string ServerPartTextOf(ServerPartState part, bool limited) =>
        part == ServerPartState.NotOnServer && limited
            ? ServerPartMissing + " · " + Progenitor.Plugins.CounterpartPolicy.LimitedWord
            : ServerPartTextOf(part);

    internal static ModFrameworkLine Compose(ModFrameworkFacts facts)
    {
        facts ??= new ModFrameworkFacts();
        var status = DecideStatus(facts);
        // A mod running limited without its server part says so even when its sides are declared
        // as client only (the missing part is the reason it is limited).
        var part = facts.Sides == ModSides.Both || facts.Limited ? facts.ServerPart : ServerPartState.Unknown;
        return new ModFrameworkLine
        {
            Facts = facts,
            RestartReason = RestartReasonOf(status, facts),
            PluginId = facts.PluginId ?? "",
            SwitchVisible = status != ModRunStatus.NotOnClient && facts.SwitchMode != ModSwitchMode.Hidden,
            SwitchOn = facts.EnabledIntent,
            SwitchInteractive = facts.SwitchAvailable,
            SwitchReason = facts.SwitchAvailable
                ? ""
                : (string.IsNullOrWhiteSpace(facts.SwitchReason) ? SwitchUnavailableReason : facts.SwitchReason),
            ConflictChip = ModProblems.ConflictChip(ModProblems.ConflictCount(facts.Problems)),
            Status = status,
            StatusWord = WordOf(status, facts),
            StatusTone = ToneOf(status, facts),
            PageState = facts.PageState,
            Version = (facts.Version ?? "").Trim(),
            Sides = facts.Sides,
            SidesWord = SidesWordOf(facts.Sides),
            ServerPart = part,
            ServerPartText = ServerPartTextOf(part, facts.Limited),
            ActionCaption = string.IsNullOrEmpty(facts.ActionCaption) ? null : facts.ActionCaption,
            ActionTone = facts.ActionTone,
        };
    }

    /// <summary>The line as one reading, the way a log or a test reads it: <c>On · Enabled · v1.2.0 · Client and server · Server part running</c>.</summary>
    internal string Text
    {
        get
        {
            var parts = new List<string>(7);
            if (SwitchVisible) parts.Add(SwitchOn ? SwitchOnCaption : SwitchOffCaption);
            parts.Add(StatusWord);
            if (ConflictChip.Length > 0) parts.Add(ConflictChip);
            if (VersionText.Length > 0) parts.Add(VersionText);
            if (SidesWord.Length > 0) parts.Add(SidesWord);
            if (ServerPartText.Length > 0) parts.Add(ServerPartText);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The <c>page-framework</c> readback: every fact the line draws, by name.</summary>
    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["switch"] = new Dictionary<string, object?>
        {
            ["visible"] = SwitchVisible,
            ["on"] = SwitchOn,
            ["interactive"] = SwitchInteractive,
            ["reason"] = SwitchReason,
            ["mode"] = ModSwitchView.NameOf(Facts.SwitchMode),
            ["pending"] = ModSwitchView.NameOf(Facts.SwitchPending),
        },
        ["conflicts"] = ModProblems.ConflictCount(Facts.Problems),
        ["conflictChip"] = ConflictChip,
        ["problems"] = ProblemsReadback(),
        ["limited"] = Facts.Limited,
        ["status"] = NameOf(Status),
        ["word"] = StatusWord,
        ["tone"] = LabelTones.Name(StatusTone),
        ["restart"] = RestartReason,
        ["version"] = Version,
        ["versionText"] = VersionText,
        ["sidesWord"] = SidesWord,
        ["band"] = true,
        ["sides"] = NameOf(Sides),
        ["serverPart"] = NameOf(ServerPart),
        ["serverPartText"] = ServerPartText,
        // INSTALLSIDES1: the two side cells, in the Install row's words (null without a listing).
        ["sideClient"] = Facts.PerSide == null ? null : SideCells.ToWidget(Facts.PerSide.Client),
        ["sideServer"] = Facts.PerSide == null ? null : SideCells.ToWidget(Facts.PerSide.Server),
        ["serverColumn"] = Facts.ServerColumnName,
        ["action"] = ActionCaption ?? "",
        ["actionEnabled"] = ActionEnabled,
        ["actionReason"] = ActionReason,
        ["text"] = Text,
        ["owner"] = "framework",
    };

    private List<Dictionary<string, object?>> ProblemsReadback()
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var p in Facts.Problems) list.Add(p.Readback());
        return list;
    }

    /// <summary>The status bar's height: one ordinary row plus the band's padding.</summary>
    internal const float BarHeight = 48f;

    /// <summary>
    /// The one cell row the page draws: a solid band (<see cref="CellRowSpec.Band"/>) holding, left
    /// to right, the switch (a track and knob, <see cref="CellKind.Switch"/>; a
    /// <see cref="CellKind.Button"/> only once <see cref="SwitchInteractive"/> is true, so nothing
    /// offers a press that does nothing), the status word in its tone, the version as plain text
    /// (<c>v0.1.0</c> — never a mono cell, whose <c>mspace</c> run drew it letter-spaced live), the
    /// sides chip in plain words, then — right-aligned — the server part when it is known and the
    /// framework's action button.
    /// </summary>
    internal CellRowSpec BuildRow(string key, float rowWidth)
    {
        _ = rowWidth;
        var cells = new List<CellSpec>(8);
        if (SwitchVisible)
        {
            // Stage 3: always the track and knob; a switch that works also takes the click
            // (CellSpec.Clickable). One that cannot act stays a plain switch cell, so nothing offers
            // a press that does nothing.
            var sw = new CellSpec(CellKind.Switch, CellSwitch)
                .With(text: SwitchOn ? SwitchOnCaption : SwitchOffCaption,
                    tone: SwitchOn ? CellTone.Ok : CellTone.Muted);
            sw.Clickable = SwitchInteractive;
            sw.Width = CellWidth.Px(CellLayout.AutoWidth(sw));
            cells.Add(sw);
        }

        cells.Add(new CellSpec(CellKind.Text, CellStatusWord).With(
            text: StatusWord, tone: LabelTones.ToCellTone(StatusTone), fontSize: HeadBlockLayout.MetaFontSize));
        if (VersionText.Length > 0)
            cells.Add(new CellSpec(CellKind.Text, CellVersion).With(
                text: VersionText, tone: CellTone.Muted, fontSize: HeadBlockLayout.MetaFontSize));
        if (SidesWord.Length > 0)
        {
            var sides = new CellSpec(CellKind.Badge, CellSides).With(text: SidesWord, tone: SidesTone(Sides));
            sides.Width = CellWidth.Px(CellLayout.AutoWidth(sides));
            cells.Add(sides);
        }

        if (ConflictChip.Length > 0)
        {
            var chip = new CellSpec(CellKind.Badge, CellConflicts).With(text: ConflictChip, tone: CellTone.Warn);
            chip.Width = CellWidth.Px(CellLayout.AutoWidth(chip));
            cells.Add(chip);
        }

        cells.Add(new CellSpec(CellKind.Spacer, CellGap));
        // INSTALLSIDES1: the same two side cells as the Install row (words and tone; the page's own
        // action button stays the one action), the server one named as the picker names it.
        if (Facts.PerSide is { } perSide)
        {
            cells.Add(SideStateCell(CellClientSide, ClientSideLabel, perSide.Client));
            // Live (HotDemo, 2026-10-03): "Server · Progenitor Forms Test: Installed …" was cut at the cell's
            // cap. The band says "Server: Installed 0.1.0"; the server's name stays in the readback
            // (`serverColumn`) and on the Settings section's "Applies to everyone on …" line.
            cells.Add(SideStateCell(CellServerSide, ServerPicker.ServerWord, perSide.Server));
        }

        // A two-sided mod always carries the cell (hidden while nothing is known), so the page can
        // rewrite it in place when the server's handshake evidence arrives or ends.
        if (Facts.PerSide == null && (ServerPartText.Length > 0 || Facts.Sides == ModSides.Both))
        {
            var part = new CellSpec(CellKind.Muted, CellServerPart).With(
                text: ServerPartText, align: CellAlign.Right, fontSize: HeadBlockLayout.MetaFontSize,
                optional: !Facts.Limited);
            part.Visible = ServerPartText.Length > 0;
            cells.Add(part);
        }
        if (ActionCaption is { Length: > 0 } action)
            cells.Add(new CellSpec(CellKind.Button, CellAction).With(
                text: action, align: CellAlign.Right,
                // SRVUNINST1: a disabled action keeps its place, dim, like the Install tab's Admin only button.
                tone: ActionEnabled ? LabelTones.ToCellTone(ActionTone) : CellTone.Disabled));
        return new CellRowSpec(
            key, cells, height: BarHeight, rightInset: HeadBlockLayout.RightColumnInset, band: true);
    }

    /// <summary>One side's cell on the band: <c>Client: Installed 1.2.0</c>, in the side's tone, one line.</summary>
    private static CellSpec SideStateCell(string key, string label, SideCell side)
    {
        string text = label + ": " + side.Text.Replace("\n", " ");
        var spec = new CellSpec(CellKind.Muted, key).With(
            text: text, align: CellAlign.Right, fontSize: HeadBlockLayout.MetaFontSize, optional: true,
            tone: InstallModsPane.SideTone(side.Tone));
        spec.Width = CellWidth.Px(Math.Min(320f, CellLayout.AutoWidth(spec)));
        return spec;
    }
}

/// <summary>
/// SETTINGS1: the mod's own header area, under the framework line. The default is the title (large)
/// and one line of <c>by &lt;author&gt; · &lt;one-liner&gt;</c>; a mod replaces the whole area with
/// <see cref="ModMenuRegistration.Header"/>.
/// </summary>
internal static class ModHeaderDefault
{
    /// <summary>
    /// <c>by &lt;author&gt; · &lt;one-liner&gt;</c>, dropping whichever part is unknown; empty when both are.
    /// </summary>
    internal static string ByLine(string? author, string? oneLiner)
    {
        string a = (author ?? "").Trim();
        string o = (oneLiner ?? "").Trim();
        if (a.Length == 0) return o;
        if (o.Length == 0) return "by " + a;
        return "by " + a + " · " + o;
    }
}
