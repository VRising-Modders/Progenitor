using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job MODSPAGE1: the Mods-tab mod page from the approved mock
/// (internal research notes) — name, the
/// <c>version · released · by author</c> line with Like, the one-liner, the status label with its
/// action button and side badges, the required-server warning, the y/n prompt every action asks, and
/// the Settings section with the store link or the README.
///
/// <para>Game-free: the fake UI runtime, a fake install catalog, a synthetic install ledger, a fake
/// uninstall seam and a fake rating transport. Nothing here touches the game or the network.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class ModPageTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _game;

    public ModPageTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        ResetSeams();
        // ABOUTSRC1: these tests rewrite manifests between page opens; look at the disk every time.
        PackageManifestBeside.RevalidateMs = 0;
        _game = Path.Combine(Path.GetTempPath(), "progenitor-modspage1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx"));
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ResetSeams();
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        PackageManifestBeside.RevalidateMs = 2000;
        try { Directory.Delete(_game, true); } catch { }
    }

    private void ResetSeams()
    {
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.InstallLinkProbe = null;
        ModsMenu.InstalledRemover = null;
        ModsMenu.UninstallSeam = null;
        ModsMenu.ReloaderNamesProbe = null;
        ModsMenu.PluginFileProbe = null;
        PluginAboutStore.ResetForTests();
        ModHelpStore.ResetForTests();
        ModsMenu.HelpOpener = null;
        ModsMenu.HelpBepInExRootProbe = null;
        PackageManifestBeside.ForgetCache();
        ModsMenu.SetInstallCatalogForTests(null);
    }

    private string Bep => Path.Combine(_game, "BepInEx");

    /// <summary>
    /// ABOUTSRC1: a mod manager's install, <c>BepInEx/plugins/&lt;Owner&gt;-&lt;Name&gt;/&lt;Name&gt;.dll</c> with
    /// the package's <c>manifest.json</c> beside it. Returns the DLL path (the file is a placeholder).
    /// </summary>
    private string PlaceManagedPackage(string owner, string name, string version, string description = "")
    {
        string dir = Path.Combine(Bep, "plugins", owner + "-" + name);
        Directory.CreateDirectory(dir);
        string dll = Path.Combine(dir, name + ".dll");
        File.WriteAllBytes(dll, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = name,
                ["version_number"] = version,
                ["website_url"] = "",
                ["description"] = description,
                ["dependencies"] = Array.Empty<string>(),
            }));
        return dll;
    }

    // ---- the pure state rule ---------------------------------------------------------------

    /// <summary>
    /// The order of the evidence: the uninstall marker first, then whether the ledger names the
    /// plugin at all, then whether this process actually has it. A hot-loaded copy is loaded by
    /// definition, so it never reads as pending.
    /// What this would catch: reading the state off the ledger alone (every ledgered mod would read
    /// pending), or letting a hot install show a restart it does not owe.
    /// </summary>
    [Fact]
    public void The_state_is_the_marker_then_the_ledger_then_what_is_loaded()
    {
        Assert.Equal(
            ModPageState.PendingUninstall,
            ModPageStates.Decide(uninstallPending: true, ledgered: true, loadedHere: true, hot: true));
        Assert.Equal(
            ModPageState.PendingLoad,
            ModPageStates.Decide(uninstallPending: false, ledgered: true, loadedHere: false, hot: false));
        Assert.Equal(
            ModPageState.Installed,
            ModPageStates.Decide(uninstallPending: false, ledgered: true, loadedHere: false, hot: true));
        Assert.Equal(
            ModPageState.Installed,
            ModPageStates.Decide(uninstallPending: false, ledgered: false, loadedHere: true, hot: false));

        // The three readback names, the three status words and the two sidebar markers.
        Assert.Equal("installed", ModPageStates.Name(ModPageState.Installed));
        Assert.Equal("pendload", ModPageStates.Name(ModPageState.PendingLoad));
        Assert.Equal("penduninst", ModPageStates.Name(ModPageState.PendingUninstall));
        Assert.Equal("Installed", ModPageStates.WordOf(ModPageState.Installed));
        Assert.Equal("Pending restart · to load", ModPageStates.WordOf(ModPageState.PendingLoad));
        Assert.Equal("Pending restart · to uninstall", ModPageStates.WordOf(ModPageState.PendingUninstall));
        Assert.Equal("", ModPageStates.DotOf(ModPageState.Installed));
        Assert.NotEqual("", ModPageStates.DotOf(ModPageState.PendingLoad));
        Assert.NotEqual(
            ModPageStates.DotOf(ModPageState.PendingLoad), ModPageStates.DotOf(ModPageState.PendingUninstall));

        // Each state's action opens its own prompt.
        Assert.Equal(ModPromptKind.Uninstall, ModPageStates.PromptFor(ModPageState.Installed));
        Assert.Equal(ModPromptKind.CancelInstall, ModPageStates.PromptFor(ModPageState.PendingLoad));
        Assert.Equal(ModPromptKind.Reactivate, ModPageStates.PromptFor(ModPageState.PendingUninstall));
    }

    // ---- the head ---------------------------------------------------------------------------

    /// <summary>
    /// The installed page: the name, the <c>version · released · by author</c> line, the one-liner,
    /// the green word behind <c>Status:</c>, <b>Uninstall</b> under it and the side badge, with the
    /// store link and each row's readback.
    /// What this would catch: a status drawn as a button, the listing's version shown instead of the
    /// installed one, a missing release date or author, or a readback row whose state disagrees with
    /// the word the page drew.
    /// </summary>
    [Fact]
    public void The_installed_page_reports_the_listing_facts_the_status_word_and_Uninstall()
    {
        SeedCryptSnooze();
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");

        var rows = PageRows();
        Assert.Equal("CryptSnooze", Row(rows, ModPagePane.RowTitle).GetProperty(ModPagePane.RowTitle).GetString());
        var meta = Row(rows, ModPagePane.RowMeta).GetProperty(ModPagePane.RowMeta);
        Assert.Equal("2.0.4", meta.GetProperty("version").GetString());
        Assert.Equal("2025-05-18", meta.GetProperty("released").GetString());
        Assert.Equal("Corvane", meta.GetProperty("author").GetString());
        // SETTINGS1: the default mod header's second line is `by <author> · <one-liner>`.
        Assert.Equal(
            "by Corvane · A VRising mod that speeds up time while you sleep in your coffin.",
            Row(rows, ModPagePane.RowOneLiner).GetProperty(ModPagePane.RowOneLiner).GetString());

        var status = Row(rows, ModPagePane.RowStatus);
        Assert.Equal("installed", status.GetProperty(ModPagePane.RowStatus).GetProperty("state").GetString());
        Assert.Equal("enabled", status.GetProperty(ModPagePane.RowStatus).GetProperty("status").GetString());
        Assert.Equal(ModFrameworkLine.WordEnabled, status.GetProperty(ModPagePane.RowStatus).GetProperty("word").GetString());
        // SETTINGS1: the status is the colored word on the framework line — the page's first row,
        // which the mod cannot change — and the action button is at its right. The status is still a
        // label, never a button: the cell that holds it is a `text` cell.
        Assert.Equal("cellrow", status.GetProperty("kind").GetString());
        Assert.Equal(ModPagePane.FrameworkKey, status.GetProperty("label").GetString());
        Assert.Equal("ok", status.GetProperty(ModPagePane.RowStatus).GetProperty("tone").GetString());
        var statusCell = Cell(status, HeadBlockLayout.CellStatusWord);
        Assert.Equal("text", statusCell.GetProperty("kind").GetString());
        Assert.Equal(ModFrameworkLine.WordEnabled, statusCell.GetProperty("text").GetString());
        Assert.Equal("ok", statusCell.GetProperty("tone").GetString());
        var action = Row(rows, ModPagePane.RowAction);
        Assert.Equal("cellrow", action.GetProperty("kind").GetString());
        Assert.Equal(ModPageCopy.Uninstall, action.GetProperty(ModPagePane.RowAction).GetProperty("label").GetString());
        var actionCell = Cell(action, HeadBlockLayout.CellAction);
        Assert.Equal("button", actionCell.GetProperty("kind").GetString());
        Assert.Equal(ModPageCopy.Uninstall, actionCell.GetProperty("text").GetString());

        // SETTINGS1 stage 2: the sides chip says it in plain words.
        var badges = Row(rows, ModPagePane.RowBadges).GetProperty(ModPagePane.RowBadges);
        Assert.Equal(new[] { ModFrameworkLine.SidesServer }, Strings(badges));
        Assert.False(TryRow(rows, ModPagePane.RowWarning, out _), "an installed server mod has no warning line");
        Assert.Equal(
            "https://thunderstore.io/c/v-rising/p/Corvane/CryptSnooze/",
            Row(rows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink).GetProperty("url").GetString());

        // SETTINGS1: the version moved to the framework line (after the status word, with the
        // mock's `· ` separator in its own text); the mod area's meta row keeps the release date.
        // Stage 2: plain text `v2.0.4`, never a mono cell (drawn letter-spaced live).
        Assert.Equal("v2.0.4", Cell(status, HeadBlockLayout.CellVersion).GetProperty("text").GetString());
        Assert.Equal("text", Cell(status, HeadBlockLayout.CellVersion).GetProperty("kind").GetString());
        // MODPAGE2: the release date is a Details row.
        Assert.Equal("2025-05-18", Cell(RowLabelled(rows, ModPagePane.FactKeyPrefix + "released"), ModPagePane.CellFactValue).GetProperty("text").GetString());
        Assert.Equal("2.0.4 · 2025-05-18 · by Corvane", meta.GetProperty("line").GetString());
        // The title is drawn at the mock's display size, in its own cell, never clipped here.
        Assert.Equal(
            "CryptSnooze",
            Cell(Row(rows, ModPagePane.RowTitle), HeadBlockLayout.CellTitle).GetProperty("text").GetString());
        // The side badge is a colored pill, not a word in a text line (MODPAGE2: the Installs on row's value).
        var badgeCell = Cell(Row(rows, ModPagePane.RowBadges), ModFrameworkLine.CellSides);
        Assert.Equal("badge", badgeCell.GetProperty("kind").GetString());
        Assert.Equal("server", badgeCell.GetProperty("tone").GetString());
        // SETTINGS1 stage 2: the store link is a captioned Store page button in the header's button
        // row, and its readback says what it is and where it goes.
        var store = Row(rows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink);
        Assert.Equal("button", store.GetProperty("kind").GetString());
        Assert.Equal(ModPageCopy.StoreLinkTooltip, store.GetProperty("tooltip").GetString());
        Assert.Equal(ModPageCopy.StoreLinkCaption, Cell(Row(rows, ModPagePane.RowStoreLink), ModPagePane.CellStore).GetProperty("text").GetString());
    }

    /// <summary>
    /// A ledgered install nothing has loaded is listed (before MODSPAGE1 only loaded plugins were),
    /// reads the yellow state and offers <b>Cancel install</b>.
    /// What this would catch: a pending install missing from the sidebar, reading as Installed, or
    /// offering Uninstall for a file that never loaded.
    /// </summary>
    [Fact]
    public void A_pending_install_is_listed_with_the_yellow_state_and_offers_Cancel_install()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Listing("ExampleTeam", "PhialStore", "1.0.0", "Client"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-PhialStore", "example.phialstore.dll", "1.0.0", ServerInstallLedger.CategoryPending),
        };
        ModsMenu.Open();

        // The row exists although nothing loaded it, and its caption carries the pending marker.
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            var entry = Entry(doc.RootElement, "example.phialstore");
            Assert.Equal("progenitor", entry.GetProperty("provenance").GetString());
            Assert.Equal("pendload", entry.GetProperty("state").GetString());
            Assert.False(entry.GetProperty("warning").GetBoolean());
            // HEAD2: no state light on a sidebar row any more (the state is `state` here and the
            // page's `Status:` word), so `dot` is empty and the caption is still the plain name.
            Assert.Equal("", entry.GetProperty("dot").GetString());
            Assert.Equal("", entry.GetProperty("marker").GetString());
            Assert.Equal("PhialStore", entry.GetProperty("caption").GetString());
            // `status` keeps its own meaning: the inventory's word, empty for a row nothing loaded.
            Assert.Equal("", entry.GetProperty("status").GetString());
        }

        Assert.Contains(
            "PhialStore",
            JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list")),
            StringComparison.Ordinal);

        ModsMenu.OpenMod("example.phialstore");
        var rows = PageRows();
        Assert.Equal(
            "pendload", Row(rows, ModPagePane.RowStatus).GetProperty(ModPagePane.RowStatus).GetProperty("state").GetString());
        Assert.Equal(
            ModPageCopy.CancelInstall,
            Row(rows, ModPagePane.RowAction).GetProperty(ModPagePane.RowAction).GetProperty("label").GetString());

        // Its prompt cancels the install through the seam INSTALL2 owns, and only on the yes.
        var seam = new FakeSeam();
        ModsMenu.UninstallSeam = seam;
        ModsMenu.OpenMod("example.phialstore");
        Click(ModPageCopy.CancelInstall);
        Assert.Equal(0, seam.CancelInstallCalls);
        var prompt = PromptRows();
        Assert.Equal("cancel", prompt["heading"].GetProperty(ModPagePane.RowPrompt).GetProperty("kind").GetString());
        Assert.Equal(
            ModPageCopy.CancelInstallHeading("PhialStore", "1.0.0"), prompt["heading"].GetProperty("label").GetString());
        Assert.Equal(ModPageCopy.CancelInstallBody, prompt["body"].GetProperty("label").GetString());
        Click(ModPageCopy.CancelInstallYes);
        Assert.Equal(1, seam.CancelInstallCalls);
        Assert.Equal("example.phialstore", seam.LastPlugin);
        Assert.Equal(ModPageCopy.CancelledNote, ModsMenu.ModsActionNote);
    }

    /// <summary>
    /// With the seam wired, a confirmed Uninstall marks the mod instead of deleting its files now:
    /// the page turns orange and offers Reactivate, and the mod stays loaded until the restart.
    /// What this would catch: a seam-wired Uninstall that still deletes the loaded copy (the mod would
    /// vanish from a running game), or a marked mod that keeps offering Uninstall.
    /// </summary>
    [Fact]
    public void Uninstall_through_the_seam_marks_the_mod_and_the_page_turns_orange()
    {
        SeedCryptSnooze();
        var seam = new FakeSeam();
        ModsMenu.UninstallSeam = seam;
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        Click(ModPageCopy.Uninstall);
        Assert.Equal(0, seam.MarkCalls);
        Click(ModPageCopy.UninstallYes);

        Assert.Equal(1, seam.MarkCalls);
        Assert.False(seam.LastHot, "this copy is not hot-loaded, so its removal owes a restart");
        Assert.Equal(ModPageCopy.RemovesAtRestartNote, ModsMenu.ModsActionNote);
        var rows = PageRows();
        Assert.Equal(
            "penduninst",
            Row(rows, ModPagePane.RowStatus).GetProperty(ModPagePane.RowStatus).GetProperty("state").GetString());
        Assert.Equal(
            ModPageCopy.CancelUninstall,
            Row(rows, ModPagePane.RowAction).GetProperty(ModPagePane.RowAction).GetProperty("label").GetString());
    }

    /// <summary>
    /// Enter does not answer the Uninstall prompt (live 2026-10-10: one confirm press uninstalled a
    /// mod): after the Enter grace the prompt is still open, nothing was marked, and its Yes wears the
    /// danger tone. A click on Yes still uninstalls. Uninstall and Cancel install are the destructive
    /// kinds; Reactivate is not.
    /// What this would catch: the page opening its prompts without the danger flag.
    /// </summary>
    [Fact]
    public void Enter_does_not_answer_the_Uninstall_prompt()
    {
        Assert.True(ModPagePane.PromptIsDestructive(ModPromptKind.Uninstall));
        Assert.True(ModPagePane.PromptIsDestructive(ModPromptKind.CancelInstall));
        Assert.False(ModPagePane.PromptIsDestructive(ModPromptKind.Reactivate));

        long now = 10_000;
        UiDialog.Clock = () => now;
        try
        {
            SeedCryptSnooze();
            var seam = new FakeSeam();
            ModsMenu.UninstallSeam = seam;
            ModsMenu.Open();
            ModsMenu.OpenMod("example.cryptsnooze");
            Click(ModPageCopy.Uninstall);
            Assert.Equal(ModPromptKind.Uninstall, ModsMenu.ModPrompt);
            Assert.Equal("danger", Cell(PromptRows()["buttons"], UiPromptBlock.CellYes).GetProperty("tone").GetString());

            now += UiDialog.EnterGraceMs + 1;
            Assert.False(UiDialog.NotifyEnter(), "Enter must not answer the Uninstall prompt");
            Assert.Equal(0, seam.MarkCalls);
            Assert.Equal(ModPromptKind.Uninstall, ModsMenu.ModPrompt);
            Assert.True(UiDialog.Current is { IsOpen: true }, "the prompt is still on screen");

            Click(ModPageCopy.UninstallYes);
            Assert.Equal(1, seam.MarkCalls);
        }
        finally { UiDialog.Clock = () => Environment.TickCount64; }
    }

    /// <summary>
    /// Enter still answers a prompt that takes nothing away: the Reactivate prompt of a pending
    /// uninstall cancels the removal on Enter, and its Yes keeps the green tone.
    /// What this would catch: the danger flag spreading to every mod-page prompt.
    /// </summary>
    [Fact]
    public void Enter_still_answers_the_Reactivate_prompt()
    {
        long now = 10_000;
        UiDialog.Clock = () => now;
        try
        {
            SeedCryptSnooze();
            var seam = new FakeSeam { Pending = true };
            ModsMenu.UninstallSeam = seam;
            ModsMenu.Open();
            ModsMenu.OpenMod("example.cryptsnooze");
            Click(ModPageCopy.CancelUninstall);
            Assert.Equal(ModPromptKind.Reactivate, ModsMenu.ModPrompt);
            Assert.Equal("ok", Cell(PromptRows()["buttons"], UiPromptBlock.CellYes).GetProperty("tone").GetString());

            now += UiDialog.EnterGraceMs + 1;
            Assert.True(UiDialog.NotifyEnter());
            Assert.Equal(1, seam.CancelUninstallCalls);
            Assert.Equal(ModPromptKind.None, ModsMenu.ModPrompt);
        }
        finally { UiDialog.Clock = () => Environment.TickCount64; }
    }

    /// <summary>
    /// A pending uninstall: the orange state, <b>Reactivate</b>, and its prompt's yes cancels the
    /// removal through the seam INSTALL2 owns.
    /// What this would catch: a Reactivate that removes files instead of canceling, a prompt that
    /// claims a restart, or a sidebar row that still reads installed after the marker was written.
    /// </summary>
    [Fact]
    public void A_pending_uninstall_offers_Reactivate_and_its_prompt_cancels_the_removal()
    {
        SeedCryptSnooze();
        var seam = new FakeSeam { Pending = true };
        ModsMenu.UninstallSeam = seam;
        ModsMenu.Open();

        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            var entry = Entry(doc.RootElement, "example.cryptsnooze");
            Assert.Equal("penduninst", entry.GetProperty("state").GetString());
            // HEAD2: no orange light either — `state` says it and the page says it in words.
            Assert.Equal("", entry.GetProperty("dot").GetString());
            // The caption is the row's name and nothing else: no dot, and no glyph standing in for one.
            Assert.Equal(entry.GetProperty("name").GetString(), entry.GetProperty("caption").GetString());
        }

        ModsMenu.OpenMod("example.cryptsnooze");
        Assert.Equal(
            ModPageCopy.CancelUninstall,
            Row(PageRows(), ModPagePane.RowAction).GetProperty(ModPagePane.RowAction).GetProperty("label").GetString());

        // The action only asks; nothing is changed by opening the prompt.
        Click(ModPageCopy.CancelUninstall);
        Assert.Equal(0, seam.CancelUninstallCalls);
        var prompt = PromptRows();
        Assert.Equal("reactivate", prompt["heading"].GetProperty(ModPagePane.RowPrompt).GetProperty("kind").GetString());
        Assert.True(prompt["heading"].GetProperty(ModPagePane.RowPrompt).GetProperty("open").GetBoolean());
        Assert.Equal(
            ModPageCopy.ReactivateHeading("CryptSnooze", "2.0.4"), prompt["heading"].GetProperty("label").GetString());
        Assert.Equal(ModPageCopy.ReactivateBody, prompt["body"].GetProperty("label").GetString());
        // KIT1: the two captions are the buttons row's cells, and the yes wears the tone of the
        // state it restores (green for "keep it", not the removal's orange).
        var buttons = prompt["buttons"];
        Assert.Equal(ModPageCopy.ReactivateYes, Cell(buttons, UiPromptBlock.CellYes).GetProperty("text").GetString());
        Assert.Equal("ok", Cell(buttons, UiPromptBlock.CellYes).GetProperty("tone").GetString());
        Assert.Equal(ModPageCopy.PromptNo, Cell(buttons, UiPromptBlock.CellNo).GetProperty("text").GetString());
        Assert.Equal(ModPageCopy.ReactivateYes, buttons.GetProperty(ModPagePane.RowPrompt).GetProperty("yes").GetString());
        Assert.Equal(ModPageCopy.PromptNo, buttons.GetProperty(ModPagePane.RowPrompt).GetProperty("no").GetString());

        Click(ModPageCopy.ReactivateYes);
        Assert.Equal(1, seam.CancelUninstallCalls);
        Assert.Equal("example.cryptsnooze", seam.LastPlugin);
        Assert.Equal(ModPageCopy.ReactivatedNote, ModsMenu.ModsActionNote);
        Assert.Equal(ModPromptKind.None, ModsMenu.ModPrompt);
    }

    /// <summary>
    /// Every action asks first: the prompt names the mod, the version and what changes when, and
    /// <b>No</b> leaves the files alone. Without the seam INSTALL2 owns, the confirmed Uninstall is
    /// today's verified ledger-scoped removal — it deletes exactly the ledgered copies and nothing
    /// else.
    /// What removal breaks: the mod stays loaded in this process until it restarts, so the page says
    /// so rather than claiming it is gone; a file the ledger does not name — another tool's plugin, a
    /// config file, saved data — is never touched, so a mod removed and installed again comes back
    /// with its settings.
    /// </summary>
    [Fact]
    public void Uninstall_asks_before_it_removes_and_removes_only_the_ledgered_files()
    {
        byte[] bytes = { 3, 3, 3 };
        string sha = InstalledState.HashBytes(bytes);
        string reload = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "example.phialstore.dll"), bytes);
        string foreign = Write(Path.Combine(Bep, "plugins", "GroundPickupGlow.dll"), new byte[] { 9 });
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/example.phialstore.dll", sha, 3,
                ServerInstallLedger.CategoryMod, "package:example.phialstore@1.0.0", DateTime.UtcNow),
        }).Write(_game);
        ModsMenu.LedgerForTests = InstalledState.Scan(Bep, null).Ledger;
        Assert.NotEmpty(ModsMenu.LedgerForTests);
        Handshake.Provide("example.phialstore", "1.0.0");

        using var potion = ModsMenu.Register(new object(), "example.phialstore", "PhialStore");
        ModsMenu.Open();
        ModsMenu.OpenMod("example.phialstore");
        Assert.Equal(
            ModPageCopy.Uninstall,
            Row(PageRows(), ModPagePane.RowAction).GetProperty(ModPagePane.RowAction).GetProperty("label").GetString());

        // One click opens the prompt and removes nothing.
        Click(ModPageCopy.Uninstall);
        Assert.True(File.Exists(reload));
        var prompt = PromptRows();
        Assert.Equal("uninstall", prompt["heading"].GetProperty(ModPagePane.RowPrompt).GetProperty("kind").GetString());
        Assert.Equal(
            ModPageCopy.UninstallHeadingFrom("PhialStore", "1.0.0", "this game"), prompt["heading"].GetProperty("label").GetString());
        Assert.Contains(
            "at the next restart of V Rising", prompt["body"].GetProperty("label").GetString()!, StringComparison.Ordinal);
        Assert.Contains(
            "settings for it are kept", prompt["body"].GetProperty("label").GetString()!, StringComparison.Ordinal);

        // No keeps it: the prompt closes and the file is still there.
        Click(ModPageCopy.UninstallNo);
        Assert.True(File.Exists(reload));
        Assert.Equal(ModPromptKind.None, ModsMenu.ModPrompt);
        Assert.False(TryRow(PageRows(), ModPagePane.RowPrompt, out _), "No must close the prompt");

        // Yes removes exactly the ledgered copy and says what is still loaded.
        Click(ModPageCopy.Uninstall);
        Click(ModPageCopy.UninstallYes);
        Assert.False(File.Exists(reload));
        Assert.True(File.Exists(foreign));
        Assert.Equal(InstallModsCopy.RemovedNeedsRestart, ModsMenu.ModsActionNote);
        var after = ServerInstallLedger.TryRead(_game);
        Assert.NotNull(after);
        Assert.Empty(after!.Files);
    }

    /// <summary>
    /// A hot-loaded Progenitor mod owes no restart, and its prompt says so instead of promising one.
    /// What this would catch: the restart sentence on the hot lane, which is the one case where the
    /// mod is gone the moment it is removed.
    /// </summary>
    [Fact]
    public void A_hot_loaded_copy_says_it_unloads_now()
    {
        SeedCryptSnooze();
        ModsMenu.ReloaderNamesProbe = () => new[] { "Crypt Snooze" };
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        Click(ModPageCopy.Uninstall);
        string body = PromptRows()["body"].GetProperty("label").GetString() ?? "";
        Assert.Contains("Unloads now; nothing to restart.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("next restart of V Rising", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Live 2026-10-01: <c>mods_ui search text=Sample</c> on the Install tab left the list at 151
    /// listings. The bridge's search goes through <see cref="ModsMenu.ApplySearch"/>; on the Install
    /// tab it must filter the Install rows and the count.
    /// </summary>
    [Fact]
    public void The_bridge_search_filters_the_install_tab()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Listing("ExampleTeam", "SampleMod", "1.0.0", "Client", "Oakveil Update"));
        catalog.Entries.Add(Listing("ExampleTeam", "PhialStore", "1.0.0", "Client", "Oakveil Update"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.Open();
        ModsMenu.SelectTab("install");
        ModsMenu.DrainInstallIndexForTests();
        var pane = ModsMenu.InstallPaneForTests!;
        int before = pane.MatchCount.Shown;
        Assert.Equal(2, before);

        ModsMenu.ApplySearch("Sample");
        Assert.Equal("Sample", pane.Search);
        Assert.Equal(1, pane.MatchCount.Shown);
        Assert.Equal(1, ModsMenu.InstallPaneForTests!.MatchCount.Shown);

        ModsMenu.ApplySearch("");
        Assert.Equal(2, ModsMenu.InstallPaneForTests!.MatchCount.Shown);
    }

    // ---- the required-server warning --------------------------------------------------------

    /// <summary>
    /// Only a Progenitor manifest can require the server half, and the warning is shown only while
    /// the client half is here without it. The sidebar row carries the red <c>(!)</c> as well.
    /// What this would catch: raising the warning from a Thunderstore <c>Both</c> category (which no
    /// author declared as a requirement), or keeping it once the server half is installed.
    /// </summary>
    [Fact]
    public void The_server_half_warning_is_manifest_only_and_marks_the_sidebar_row()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Verified("ExampleTeam", "HuntsAndGates", "1.0.0", "example.huntsandgates"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0", ServerInstallLedger.CategoryMod),
        };
        Handshake.Provide("example.huntsandgates", "1.0.0");
        using var hunts = ModsMenu.Register(new object(), "example.huntsandgates", "HuntsAndGates");
        ModsMenu.Open();
        ModsMenu.OpenMod("example.huntsandgates");

        var rows = PageRows();
        var warningRow = Row(rows, ModPagePane.RowWarning);
        Assert.Equal(
            ModPageCopy.WarningServerHalfMissing,
            warningRow.GetProperty(ModPagePane.RowWarning).GetString());
        // KIT1: the marker is its own red cell in front of the wrapped body, so the body does not
        // repeat it and the color is named rather than implied.
        var markerCell = Cell(warningRow, HeadBlockLayout.CellWarningMarker);
        Assert.Equal(ModPageCopy.WarningMarker, markerCell.GetProperty("text").GetString());
        Assert.Equal("danger", markerCell.GetProperty("tone").GetString());
        var bodyCell = Cell(warningRow, HeadBlockLayout.CellWarning);
        Assert.Equal(ModPageCopy.WarningServerHalfMissingBody, bodyCell.GetProperty("text").GetString());
        Assert.Equal("orange", bodyCell.GetProperty("tone").GetString());
        // SETTINGS1: the framework line names the sides once — a manifest that requires the server
        // half is a two-sided mod.
        Assert.Equal(
            new[] { ModFrameworkLine.SidesBoth },
            Strings(Row(rows, ModPagePane.RowBadges).GetProperty(ModPagePane.RowBadges)));

        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            var entry = Entry(doc.RootElement, "example.huntsandgates");
            Assert.True(entry.GetProperty("warning").GetBoolean());
            Assert.Equal(ModPageCopy.WarningMarker, entry.GetProperty("marker").GetString());
            Assert.Equal("HuntsAndGates", entry.GetProperty("caption").GetString());
        }

        // The server half arrives: same package, a local-server ledger row as well.
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0", ServerInstallLedger.CategoryMod),
            Ledgered(
                "ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0",
                ServerInstallLedger.CategoryMod, InstalledState.ServerRoot),
        };
        ModsMenu.OpenMod("example.huntsandgates");
        Assert.False(TryRow(PageRows(), ModPagePane.RowWarning, out _), "the warning must go with the server half");
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.False(Entry(doc.RootElement, "example.huntsandgates").GetProperty("warning").GetBoolean());

        // An adapted listing tagged Server and Client is not a declaration: no warning, no marker.
        var plain = new PageCatalog();
        plain.Entries.Add(Listing("ExampleTeam", "HuntsAndGates", "1.0.0", "Server", "Client"));
        ModsMenu.SetInstallCatalogForTests(plain);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0", ServerInstallLedger.CategoryMod),
        };
        ModsMenu.OpenMod("example.huntsandgates");
        var adapted = PageRows();
        Assert.False(TryRow(adapted, ModPagePane.RowWarning, out _), "only a manifest can require both sides");
        // An adapted listing's guessed Both is still two-sided; only the warning needs the manifest.
        Assert.Equal(
            new[] { ModFrameworkLine.SidesBoth },
            Strings(Row(adapted, ModPagePane.RowBadges).GetProperty(ModPagePane.RowBadges)));
    }

    /// <summary>
    /// HEAD2 (user, 2026-09-13, live 3840×1080: "lets not use the lights (yellow in this case) on the
    /// listing in the menu on the left"): <b>no</b> sidebar row draws a state dot — not a pending
    /// load, not a pending uninstall, not an installed one — while the red <c>(!)</c> marker of a
    /// missing server half still does. The state is still named in the readback and still shown on
    /// the page, and the caption gets back the width the dot used to cost.
    /// What this would catch: the yellow and orange squares the live capture shows on the
    /// ExampleCommandFramework and AutoLatchGates rows, a dot removed from the drawn row but left in
    /// the readback (or the other way round), or the <c>(!)</c> warning removed with them.
    /// </summary>
    [Fact]
    public void No_sidebar_row_draws_a_state_dot_and_the_warning_marker_still_does()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Verified("ExampleTeam", "HuntsAndGates", "1.0.0", "example.huntsandgates"));
        catalog.Entries.Add(Listing("ExampleTeam", "PhialStore", "1.0.0", "Client"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0", ServerInstallLedger.CategoryMod),
            Ledgered("ExampleTeam-PhialStore", "example.phialstore.dll", "1.0.0", ServerInstallLedger.CategoryPending),
        };
        Handshake.Provide("example.huntsandgates", "1.0.0");
        using var hunts = ModsMenu.Register(new object(), "example.huntsandgates", "HuntsAndGates");
        ModsMenu.Open();

        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            int seen = 0;
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                Assert.Equal("", entry.GetProperty("dot").GetString());
                seen++;
            }

            Assert.True(seen >= 2, "both the pending install and the warned mod must be listed");
            // The states are still there to read, and the warned row still carries its marker.
            var pending = Entry(doc.RootElement, "example.phialstore");
            Assert.Equal("pendload", pending.GetProperty("state").GetString());
            Assert.Equal("", pending.GetProperty("marker").GetString());
            var warned = Entry(doc.RootElement, "example.huntsandgates");
            Assert.True(warned.GetProperty("warning").GetBoolean());
            Assert.Equal(ModPageCopy.WarningMarker, warned.GetProperty("marker").GetString());
        }

        // The drawn rows agree with the readback. MODLIST1: a list entry is a cell row with a status
        // diamond and no state light; the missing server half is an amber diamond whose second line
        // says so in words (the page keeps the red (!) warning), and a pending install is hollow.
        using (var list = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("list"))))
        {
            var entries = list.RootElement.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("modsListEntry", out _))
                .Select(r => r.GetProperty("modsListEntry"))
                .ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.False(e.TryGetProperty("dot", out _)));
            var warnedRow = Assert.Single(entries, e => e.GetProperty("id").GetString() == "example.huntsandgates");
            Assert.Equal("warning", warnedRow.GetProperty("diamond").GetString());
            Assert.Equal(ModsListLayout.ServerHalfMissingLine, warnedRow.GetProperty("secondLine").GetString());
            var pendingRow = Assert.Single(entries, e => e.GetProperty("id").GetString() == "example.phialstore");
            Assert.Equal("waiting", pendingRow.GetProperty("diamond").GetString());
            Assert.Equal(ModsListLayout.DiamondHollow, pendingRow.GetProperty("diamondShape").GetString());
            Assert.Equal(ModPageCopy.StatusPendingLoad, pendingRow.GetProperty("secondLine").GetString());
        }

        // And the caption gets the width the dot used to cost — the live overflow was a long name
        // running under the square — while a name too long for even that is still ellipsized.
        const float sidebarRow = 320f;
        Assert.Equal(sidebarRow, MenuRowStyle.CaptionWidthFor(sidebarRow, marker: false, dot: false));
        Assert.True(
            MenuRowStyle.CaptionWidthFor(sidebarRow, marker: false, dot: false)
            > MenuRowStyle.CaptionWidthFor(sidebarRow, marker: false, dot: true),
            "dropping the dot must give the caption more room, not the same");
        Assert.EndsWith("…", MenuRowStyle.MarkedCaption(new string('W', 120), sidebarRow, marker: false, dot: false));
    }

    // ---- settings and the README ------------------------------------------------------------

    /// <summary>
    /// The Settings section: the kit rows when the mod declares Progenitor settings, and otherwise
    /// the one sentence that says so plus the listing's README. A plugin no listing names says "no
    /// description available" rather than inventing one.
    /// What this would catch: the README drawn over a mod's own settings, the "does not expose"
    /// sentence on a mod that does, or an empty section for an unknown listing.
    /// </summary>
    [Fact]
    public void Settings_rows_replace_the_README_and_an_unknown_listing_says_so()
    {
        SeedCryptSnooze(readme: "# CryptSnooze\n\nSpeeds up time while you sleep.\n\n## Configuration\n\nEdit the cfg.");
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");

        var rows = PageRows();
        Assert.Equal("none", Row(rows, ModPagePane.RowSettings).GetProperty(ModPagePane.RowSettings).GetProperty("kind").GetString());
        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains(Escaped(ModPageCopy.NoProgenitorSettings), page, StringComparison.Ordinal);
        var readme = Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme);
        Assert.Equal("ready", readme.GetProperty("state").GetString());
        Assert.True(readme.GetProperty("blocks").GetInt32() >= 3);
        Assert.Contains("Speeds up time while you sleep.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(ModPageCopy.NoDescription, page, StringComparison.Ordinal);

        // A registered settings group makes it a Progenitor-settings page instead.
        var file = new ConfigFile(Path.Combine(_game, "withsettings.cfg"), saveOnInit: true);
        using var withSettings = ModsMenu.Register(new object(), "example.withsettings", "WithSettings");
        withSettings.Settings("Gameplay", file.Bind("Gameplay", "Speed", 6, "How much faster time passes."));
        ModsMenu.OpenMod("example.withsettings");
        var settingsRows = PageRows();
        Assert.Equal(
            "progenitor",
            Row(settingsRows, ModPagePane.RowSettings).GetProperty(ModPagePane.RowSettings).GetProperty("kind").GetString());
        string settingsPage = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains("Gameplay", settingsPage, StringComparison.Ordinal);
        // SETTINGS1: the group is a section drawn on the page, not a button to a separate pane.
        Assert.DoesNotContain("All settings and keybinds", settingsPage, StringComparison.Ordinal);
        Assert.Equal(
            "Gameplay",
            Row(settingsRows, ModSettingsView.RowSection).GetProperty(ModSettingsView.RowSection).GetProperty("title").GetString());
        Assert.Equal(
            "example.withsettings/Gameplay/Speed",
            Row(settingsRows, ModSettingsView.RowSetting).GetProperty(ModSettingsView.RowSetting).GetProperty("id").GetString());
        Assert.DoesNotContain(Escaped(ModPageCopy.NoProgenitorSettings), settingsPage, StringComparison.Ordinal);
        // MODPAGE2: About (the README) is drawn on every page, after the settings.
        Assert.True(TryRow(settingsRows, ModPagePane.RowReadme, out _), "About follows the settings");

        // No listing at all: the store link carries no url and the README line says so.
        ModsMenu.SetInstallCatalogForTests(new PageCatalog());
        using var unknown = ModsMenu.Register(new object(), "other.tool.mod", "GroundPickupGlow");
        ModsMenu.OpenMod("other.tool.mod");
        var unknownRows = PageRows();
        Assert.Equal(
            "unavailable",
            Row(unknownRows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("state").GetString());
        Assert.Equal(
            "", Row(unknownRows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink).GetProperty("url").GetString());
        Assert.Contains(
            ModPageCopy.NoDescription,
            JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")),
            StringComparison.Ordinal);
        // Another tool's file stays read-only: the status is drawn, the action button is not.
        Assert.False(TryRow(unknownRows, ModPagePane.RowAction, out _), "an other-tool row offers no action");
        Assert.True(TryRow(unknownRows, ModPagePane.RowStatus, out _));
    }

    /// <summary>
    /// MODPAGE2: a page without settings still draws the Details grid beside the description column, which reads the page's
    /// line at rest; every details row says what it is in the readback, and the settings fact replaces the empty section.
    /// What this would catch: the column only appearing on pages with settings (the Updates options' descriptions would have
    /// nowhere to show), or a details row drawn without its description.
    /// </summary>
    [Fact]
    public void A_page_without_settings_has_the_details_grid_and_its_description_column()
    {
        SeedCryptSnooze();
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        var rows = PageRows();
        var details = Row(rows, ModPagePane.RowDetails).GetProperty(ModPagePane.RowDetails);
        Assert.Equal("CryptSnooze", details.GetProperty("column").GetProperty("heading").GetString());
        Assert.Equal(ModPageDetails.ColumnIntro, details.GetProperty("column").GetProperty("body").GetString());
        Assert.Equal(ModPageSpacing.Margin, details.GetProperty("margin").GetSingle());
        Assert.NotNull(ModsMenu.ModPage!.Details);

        var settingsFact = RowLabelled(rows, ModPagePane.FactKeyPrefix + "settings");
        Assert.Equal(ModPagePane.NoSettingsFact, Cell(settingsFact, ModPagePane.CellFactValue).GetProperty("text").GetString());
        Assert.Equal(ModPageCopy.NoProgenitorSettings, settingsFact.GetProperty(ModPagePane.RowFact).GetProperty("description").GetString());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("label", out var label)) continue;
            if (!(label.GetString() ?? "").StartsWith(ModPagePane.FactKeyPrefix, StringComparison.Ordinal)) continue;
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty(ModPagePane.RowFact).GetProperty("description").GetString()));
        }
    }

    /// <summary>
    /// The Help button sits in the actions bar only while the mod ships a help file; pressing it opens that file's
    /// <c>file://</c> URI, pointing at it shows the tooltip and the description column, and the readback says where the
    /// file came from.
    /// What this would catch: a Help button drawn for a mod with no help, a click that opens a different file, or a
    /// readback that disagrees with the bar.
    /// </summary>
    [Fact]
    public void The_help_button_shows_only_with_help_and_opens_the_mods_file()
    {
        SeedCryptSnooze();
        string dll = PlaceManagedPackage("Corvane", "CryptSnooze", "2.0.4");
        ModsMenu.PluginFileProbe = id => string.Equals(id, "example.cryptsnooze", StringComparison.OrdinalIgnoreCase) ? dll : null;
        ModsMenu.HelpBepInExRootProbe = () => Bep;
        var opened = new List<string>();
        ModsMenu.HelpOpener = opened.Add;
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        var help = Row(PageRows(), ModPagePane.RowHelp).GetProperty(ModPagePane.RowHelp);
        Assert.False(help.GetProperty("shown").GetBoolean());
        Assert.Equal("none", help.GetProperty("source").GetString());
        Assert.DoesNotContain(ModPagePane.HelpButton().Key, Cells(RowLabelled(PageRows(), ModPagePane.FrameworkKey)));

        string index = Path.Combine(Path.GetDirectoryName(dll)!, "help", "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, "<html>help</html>");
        ModsMenu.OpenMod("example.cryptsnooze");
        var rows = PageRows();
        help = Row(rows, ModPagePane.RowHelp).GetProperty(ModPagePane.RowHelp);
        Assert.True(help.GetProperty("shown").GetBoolean());
        Assert.Equal("folder", help.GetProperty("source").GetString());
        Assert.Equal("help/index.html", help.GetProperty("file").GetString());
        Assert.Equal("Help", help.GetProperty("caption").GetString());
        Assert.Equal("Open CryptSnooze help in your browser", help.GetProperty("tooltip").GetString());
        var bar = RowLabelled(rows, ModPagePane.FrameworkKey);
        var button = Cell(bar, ModPagePane.CellHelp);
        Assert.Equal("button", button.GetProperty("kind").GetString());
        Assert.Equal("Help", button.GetProperty("text").GetString());
        // Help sits left of the action button, both at the bar's right end.
        var cells = Cells(bar);
        Assert.True(cells.IndexOf(ModPagePane.CellHelp) < cells.IndexOf(ModFrameworkLine.CellAction));

        Assert.True(UiCellRowHandle.TryHoverCell(ModPagePane.FrameworkKey, ModPagePane.CellHelp, true));
        Assert.True(ModsMenu.ModPage!.HelpTooltipShown);
        Assert.Equal(ModPageCopy.HelpCaption, ModsMenu.ModPage.Details!.Shown.Heading);
        Assert.True(UiCellRowHandle.TryHoverCell(ModPagePane.FrameworkKey, ModPagePane.CellHelp, false));
        Assert.False(ModsMenu.ModPage.HelpTooltipShown);

        Assert.Empty(opened);
        Assert.True(UiCellRowHandle.TryInvoke(ModPagePane.FrameworkKey + "/" + ModPagePane.CellHelp, out _));
        Assert.Equal(new[] { ModHelp.FileUri(index) }, opened.ToArray());
        Assert.Equal(ModHelp.FileUri(index), ModsMenu.LastHelpUri);
    }

    /// <summary>
    /// An embedded help page is extracted to <c>BepInEx/progenitor-cache/help/&lt;guid&gt;-&lt;hash&gt;/&lt;version&gt;/</c> when the button
    /// is pressed, not when the page is drawn; a failure to open is logged once and said on the page.
    /// What this would catch: writing to disk on every page build, or a silent failure.
    /// </summary>
    [Fact]
    public void Embedded_help_is_extracted_when_the_button_is_pressed()
    {
        SeedCryptSnooze();
        ModsMenu.HelpBepInExRootProbe = () => Bep;
        var opened = new List<string>();
        ModsMenu.HelpOpener = opened.Add;
        ModHelpStore.Note("example.cryptsnooze", new ModHelpFacts
        {
            Version = "2.0.4",
            Embedded = new[] { new ModHelpResource("help/index.html", System.Text.Encoding.UTF8.GetBytes("<html>e</html>")), new ModHelpResource("help/style.css", new byte[] { 1 }) },
        });
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        var help = Row(PageRows(), ModPagePane.RowHelp).GetProperty(ModPagePane.RowHelp);
        Assert.True(help.GetProperty("shown").GetBoolean());
        Assert.Equal("embedded", help.GetProperty("source").GetString());
        string folder = ModHelp.CacheFolder(Path.Combine(Bep, "progenitor-cache"), "example.cryptsnooze", "2.0.4");
        Assert.False(Directory.Exists(folder));

        Assert.True(UiCellRowHandle.TryInvoke(ModPagePane.FrameworkKey + "/" + ModPagePane.CellHelp, out _));
        Assert.True(File.Exists(Path.Combine(folder, "help", "index.html")));
        Assert.True(File.Exists(Path.Combine(folder, "help", "style.css")));
        Assert.Equal(ModHelp.FileUri(Path.Combine(folder, "help", "index.html")), Assert.Single(opened));

        // The opener throws: logged once, the page says so, and nothing is opened.
        ModsMenu.HelpOpener = _ => throw new InvalidOperationException("no browser");
        Assert.True(UiCellRowHandle.TryInvoke(ModPagePane.FrameworkKey + "/" + ModPagePane.CellHelp, out _));
        Assert.True(UiCellRowHandle.TryInvoke(ModPagePane.FrameworkKey + "/" + ModPagePane.CellHelp, out _));
        Assert.Single(_log.Warnings, w => w.Contains("could not open the help of example.cryptsnooze", StringComparison.Ordinal));
        Assert.Contains(ModPageCopy.HelpOpenFailed, JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);
    }

    private static List<string> Cells(JsonElement row) =>
        row.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("key").GetString() ?? "").ToList();

    /// <summary>
    /// The store link opens the listing's own package page and nothing else; a plugin with no listing
    /// says so instead of opening a guessed URL.
    /// What this would catch: building a store URL from the plugin id, or opening a non-http target.
    /// </summary>
    [Fact]
    public void The_store_link_opens_the_listings_package_page()
    {
        SeedCryptSnooze();
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        Assert.True(UiCellRowHandle.TryInvoke(ModPagePane.FactKeyPrefix + "store/" + ModPagePane.CellStore, out _));
        Assert.Equal("https://thunderstore.io/c/v-rising/p/Corvane/CryptSnooze/", ModsMenu.LastStoreUrl);

        // No listing, no url: the button is not drawn at all (stage 2), so there is nothing to open.
        ModsMenu.SetInstallCatalogForTests(new PageCatalog());
        using var unknown = ModsMenu.Register(new object(), "other.tool.mod", "GroundPickupGlow");
        ModsMenu.OpenMod("other.tool.mod");
        Assert.DoesNotContain(ModPageCopy.StoreLinkCaption, JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")).Replace("\"caption\":\"" + ModPageCopy.StoreLinkCaption + "\"", ""), StringComparison.Ordinal);
    }

    // ---- likes and ratings are removed (decided 2026-09-29) ------------------------

    /// <summary>
    /// Decided 2026-09-29 ("dont keep local likes or a rating"): no page draws a Like button or a
    /// rating, the readback has neither a <c>like</c> key nor a <c>rating</c>, and the old local likes file
    /// is deleted once at Core load — that file and nothing else.
    /// What this would catch: the control or a rating coming back, or a cleanup that reaches past the
    /// one file.
    /// </summary>
    [Fact]
    public void The_page_draws_no_Like_and_no_rating_and_the_old_likes_file_is_deleted()
    {
        SeedCryptSnooze();
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        var rows = PageRows();
        Assert.False(TryRow(rows, "like", out _), "no like readback key");
        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.DoesNotContain("\"rating\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Like", page, StringComparison.Ordinal);

        string root = Path.Combine(Path.GetTempPath(), "progenitor-likes-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, LegacyLikesCache.CacheFolder);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, LegacyLikesCache.FileName), "{}");
        File.WriteAllText(Path.Combine(cache, "index.json"), "[]");
        try
        {
            var lines = new List<string>();
            Assert.True(LegacyLikesCache.DeleteIfPresent(root, lines.Add));
            Assert.False(File.Exists(Path.Combine(cache, LegacyLikesCache.FileName)));
            Assert.True(File.Exists(Path.Combine(cache, "index.json")), "nothing else in the cache is touched");
            Assert.Single(lines);
            Assert.False(LegacyLikesCache.DeleteIfPresent(root, lines.Add), "a second load finds nothing");
            Assert.Single(lines);
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        string install = SourcePin.MethodBody(SourcePin.ReadFile("src", "Progenitor.Core", "Plugin.cs"), "internal static bool Install(");
        SourcePin.AssertUncommented(install, "Progenitor.Install.LegacyLikesCache.DeleteIfPresent(BepInEx.Paths.BepInExRootPath");
    }

    // ---- KIT1 gen 1 polish (live capture live-redesign-gen1-modspage.png) ----------------------

    /// <summary>
    /// The mod page does not let the pane draw its own centered title, because the head block is the
    /// page's name: live gen 1 showed `CryptSnooze` twice, once in the pane's title band and once at
    /// the top of the head. The overview page, which has no head block, keeps its title.
    /// What this would catch: the duplicate coming back (before MODSPAGE1 it was hidden by
    /// `IsRedundantPaneTitle`, which drops a body *label* equal to the pane title and cannot see a
    /// cell row), or a blanket suppression that leaves the overview page nameless.
    /// </summary>
    [Fact]
    public void The_mod_page_drops_the_panes_own_title_and_the_overview_keeps_it()
    {
        SeedCryptSnooze();
        ModsMenu.Open();
        // The overview has no head block, so nothing asked: it still draws the pane's title. (The
        // tab strip hides its own title through ConfigureMenuStrip, not through this ask.)
        Assert.Empty(_runtime.HiddenTitles);
        Assert.False(_runtime.TitleHiddenFor("Mods"), "the overview page keeps the pane title");

        ModsMenu.OpenMod("example.cryptsnooze");
        Assert.Single(_runtime.HiddenTitles);
        Assert.True(ModsMenu.ModPage!.PaneTitleHidden);
        Assert.True(
            _runtime.TitleHiddenFor(_runtime.HiddenTitles[0]),
            "the mod page must drop the pane's own title");

        // And the name is drawn exactly once, by the head block's title cell.
        var rows = PageRows();
        Assert.Equal(
            "CryptSnooze",
            Cell(Row(rows, ModPagePane.RowTitle), HeadBlockLayout.CellTitle).GetProperty("text").GetString());

        // Going back to the overview builds a pane that never asks, so no second ask is recorded.
        ModsMenu.Open();
        Assert.Single(_runtime.HiddenTitles);
        Assert.False(_runtime.TitleHiddenFor("Mods"));
    }

    /// <summary>
    /// The body under the head is left-aligned, the way the mock and the Configuration tab are: the
    /// **Settings** heading, the "does not expose" sentence and every README row sit against the left
    /// edge. Live gen 1 had all of them centered.
    /// What this would catch: the pane going back to the kit's centered default, which reads as a
    /// title page rather than a document.
    /// </summary>
    [Fact]
    public void The_body_rows_under_the_head_are_left_aligned()
    {
        SeedCryptSnooze();
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");

        Assert.Equal("left", ModPagePane.BodyAlignment);
        var rows = PageRows();

        // MODPAGE2: every text row of the page (title, by-line, headings, details, About) starts with the margin cell.
        int checked_ = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("label", out var label)) continue;
            string key = label.GetString() ?? "";
            if (!key.StartsWith(ModPagePane.HeadKey + "-", StringComparison.Ordinal) || !row.TryGetProperty("cells", out var cells)) continue;
            Assert.Equal(ModPageSpacing.LeadKey, cells[0].GetProperty("key").GetString());
            checked_++;
        }

        Assert.True(checked_ >= 6, "expected the title, by-line, headings, details and About rows, found " + checked_);
    }

    // ---- LINK1: the real like count, and the mods another tool installed ----------------------

    /// <summary>
    /// Job LINK1 item 1: the index's <c>rating_score</c> is the like count. It is a package-level
    /// field, it reaches the page's listing, and the control shows it — plus one only while this
    /// client's own press is unconfirmed. A score the provider did not publish stays null.
    /// What this would catch: reading the score off <c>versions[]</c> (every listing would read
    /// null), clamping a missing or negative value to 0 (the page would present "0" as the store's
    /// own figure), or a page that keeps showing the per-client stored count when the index carries
    /// the real one.
    /// </summary>
    [Fact]
    public void The_index_rating_score_is_parsed_but_shown_nowhere()
    {
        const string json = """
        [
          {"name":"CryptSnooze","full_name":"Corvane-CryptSnooze","owner":"Corvane",
           "package_url":"https://thunderstore.io/c/v-rising/p/Corvane/CryptSnooze/","rating_score":132,
           "versions":[{"version_number":"2.0.4","full_name":"Corvane-CryptSnooze-2.0.4",
             "description":"Speeds up time while you sleep.","download_url":"https://example.invalid/z.zip",
             "file_size":1234,"dependencies":[],"date_created":"2025-05-18T10:00:00Z","downloads":9,"is_active":true}]},
          {"name":"NoScore","full_name":"ExampleTeam-NoScore","owner":"ExampleTeam",
           "versions":[{"version_number":"1.0.0","dependencies":[]}]},
          {"name":"Negative","full_name":"ExampleTeam-Negative","owner":"ExampleTeam","rating_score":-3,
           "versions":[{"version_number":"1.0.0","dependencies":[]}]},
          {"name":"Huge","full_name":"ExampleTeam-Huge","owner":"ExampleTeam","rating_score":99999999999,
           "versions":[{"version_number":"1.0.0","dependencies":[]}]},
          {"name":"Text","full_name":"ExampleTeam-Text","owner":"ExampleTeam","rating_score":"41",
           "versions":[{"version_number":"1.0.0","dependencies":[]}]}
        ]
        """;

        Assert.True(
            ThunderstoreAdapter.TryLoadIndex(json, "2026-09-13T00:00:00Z", out var entries, out string error),
            error);
        Assert.Equal(5, entries.Count);
        Assert.Equal(132, entries[0].RatingScore);
        // A score the provider did not publish, one it published as nonsense, one outside int range
        // and one that is not a number are all "not published" — never a number the page would show.
        Assert.Null(entries[1].RatingScore);
        Assert.Null(entries[2].RatingScore);
        Assert.Null(entries[3].RatingScore);
        Assert.Null(entries[4].RatingScore);
        // An inspection re-stamps provenance and the manifest; it must not drop the score.
        Assert.Equal(
            132,
            entries[0]
                .WithInspection(CatalogProvenance.AdaptedListing, entries[0].Manifest, PackageSide.Client)
                .RatingScore);

        // Decided 2026-09-29: the score is still parsed but shown nowhere. The listing
        // readback says the ledger's own source found the listing, and carries no rating.
        var catalog = new PageCatalog();
        catalog.Entries.Add(Listing(
            "Corvane", "CryptSnooze", "2.0.4", "Server",
            description: "A VRising mod that speeds up time while you sleep in your coffin.",
            released: "2025-05-18T10:00:00Z",
            rating: 132));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("Corvane-CryptSnooze", "example.cryptsnooze.dll", "2.0.4", ServerInstallLedger.CategoryMod),
        };
        Handshake.Provide("example.cryptsnooze", "2.0.4");
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");

        var rows = PageRows();
        Assert.False(TryRow(rows, "like", out _));
        var reported = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("source", reported.GetProperty("matched").GetString());
        Assert.Equal("Corvane-CryptSnooze", reported.GetProperty("key").GetString());
        Assert.False(reported.TryGetProperty("rating", out _));
        // ABOUTSRC1: the ledger names this package at this listing's version, so the listing is shown.
        Assert.Equal("ledger", reported.GetProperty("verified").GetString());
        Assert.True(reported.GetProperty("shown").GetBoolean());
    }

    /// <summary>
    /// Job LINK1 item 2: the rules, in order. A package name that <em>is</em> the plugin GUID and an
    /// identity a prior inspection read out of the bytes are both real identities; a display name or
    /// an assembly stem that spells the package name (letters and digits only, case-insensitively) is
    /// only a name, and says so.
    /// What this would catch: a name match reported as an identity (it would then be allowed to drive
    /// Remove), a substring passing for a name ("BasinHover" resolving BasinHoverTips), punctuation
    /// blocking the match <c>ExampleTeam-BasinHoverTips</c> ↔ <c>BasinHoverTips</c>, or a plugin
    /// with nothing known about it matching the first listing in the index.
    /// </summary>
    [Fact]
    public void A_loaded_plugin_is_matched_by_id_then_the_identity_cache_then_its_name()
    {
        var entries = new List<CatalogEntry>
        {
            Listing("ExampleTeam", "BasinHoverTips", "1.0.19", "Client"),
            Listing("Corvane", "CryptSnooze", "2.0.4", "Server"),
            Listing("acme", "PhialStore", "1.2.0", "Client"),
        };

        // 1. the package name is the plugin GUID.
        var byId = PluginListingMatcher.Match(new LoadedPluginIdentity("PhialStore", "Phial Store", "1.2.0"), entries);
        Assert.Equal(ListingMatchKind.Id, byId.Kind);
        Assert.Equal("acme-PhialStore", byId.Entry!.Key);
        Assert.False(byId.NameOnly);
        Assert.Equal("id", byId.KindName);

        // 2. the inspected-identity cache answered for this GUID. It outranks the spelling, so a
        // plugin whose name also spells another listing still resolves to the inspected one.
        var byIdentity = PluginListingMatcher.Match(
            new LoadedPluginIdentity("example.cryptsnooze", "BasinHoverTips", "2.0.4"),
            entries,
            e => string.Equals(e.Key, "Corvane-CryptSnooze", StringComparison.Ordinal) ? "example.cryptsnooze" : null);
        Assert.Equal(ListingMatchKind.Identity, byIdentity.Kind);
        Assert.Equal("Corvane-CryptSnooze", byIdentity.Entry!.Key);
        Assert.False(byIdentity.NameOnly);

        // 3. the live case: a GUID nothing names, a display name that spells the package name.
        var byName = PluginListingMatcher.Match(
            new LoadedPluginIdentity("acme.PhialStore", "BasinHoverTips", "1.0.19"), entries);
        Assert.Equal(ListingMatchKind.Name, byName.Kind);
        Assert.Equal("ExampleTeam-BasinHoverTips", byName.Entry!.Key);
        Assert.True(byName.NameOnly);
        Assert.Equal("name", byName.KindName);

        // The full name spells it too, punctuation and casing aside.
        Assert.Equal(
            "ExampleTeam-BasinHoverTips",
            PluginListingMatcher.Match(
                new LoadedPluginIdentity("x.y", "example team BASINHOVERTIPS", ""), entries).Entry!.Key);

        // 4. the assembly file stem, when that is all this process knows.
        var byFile = PluginListingMatcher.Match(
            new LoadedPluginIdentity("x.y", "", "", "BasinHoverTips.dll"), entries);
        Assert.Equal(ListingMatchKind.Name, byFile.Kind);
        Assert.Equal("ExampleTeam-BasinHoverTips", byFile.Entry!.Key);

        // Nothing else matches: a prefix is not a spelling, and an unknown plugin stays unknown.
        Assert.Equal(
            ListingMatchKind.None,
            PluginListingMatcher.Match(new LoadedPluginIdentity("x.y", "BasinHover", "1.0.19"), entries).Kind);
        Assert.Equal(
            ListingMatchKind.None,
            PluginListingMatcher.Match(new LoadedPluginIdentity("no.such.mod", "Nothing Here", "1.0"), entries).Kind);
        Assert.Equal(ListingMatchKind.None, PluginListingMatcher.Match(default, entries).Kind);
        Assert.Equal(
            ListingMatchKind.None,
            PluginListingMatcher.Match(new LoadedPluginIdentity("x.y", "BasinHoverTips", ""), null).Kind);
        Assert.False(PluginListingMatcher.Match(default, entries).Resolved);

        // The readback names round-trip, and only `name` is display-only.
        Assert.Equal("none", ListingMatchKinds.NameOf(ListingMatchKind.None));
        Assert.Equal("source", ListingMatchKinds.NameOf(ListingMatchKind.Source));
        Assert.Equal(ListingMatchKind.Identity, ListingMatchKinds.Parse("identity"));
        Assert.Equal(ListingMatchKind.None, ListingMatchKinds.Parse("who knows"));
        Assert.True(ListingMatchKinds.IsDisplayOnly(ListingMatchKind.Name));
        Assert.False(ListingMatchKinds.IsDisplayOnly(ListingMatchKind.Source));
        Assert.False(ListingMatchKinds.IsDisplayOnly(ListingMatchKind.Identity));
        Assert.False(ListingMatchKinds.IsDisplayOnly(ListingMatchKind.Id));
    }

    /// <summary>
    /// Job LINK1 item 2, the ambiguous half: two owners publishing one package name are separated
    /// only by the loaded version, and only when that singles one out. Anything else is no match.
    /// What this would catch: taking the first of several owners (the page would credit the wrong
    /// author and link the wrong store page), or accepting a version neither owner published.
    /// </summary>
    [Fact]
    public void Two_owners_publishing_one_name_are_separated_only_by_the_loaded_version()
    {
        var twoOwners = new List<CatalogEntry>
        {
            Listing("ExampleTeam", "BasinHoverTips", "1.0.19", "Client"),
            Listing("Someone_Else", "BasinHoverTips", "0.9.0", "Client"),
        };

        // No version to separate them: no match, not the first one.
        Assert.Equal(
            ListingMatchKind.None,
            PluginListingMatcher.Match(new LoadedPluginIdentity("x.y", "BasinHoverTips", ""), twoOwners).Kind);

        // The loaded version picks the owner that published it.
        var picked = PluginListingMatcher.Match(
            new LoadedPluginIdentity("x.y", "BasinHoverTips", "1.0.19"), twoOwners);
        Assert.Equal(ListingMatchKind.Name, picked.Kind);
        Assert.Equal("ExampleTeam-BasinHoverTips", picked.Entry!.Key);
        Assert.Equal(
            "Someone_Else-BasinHoverTips",
            PluginListingMatcher.Match(new LoadedPluginIdentity("x.y", "BasinHoverTips", "0.9.0"), twoOwners).Entry!.Key);

        // A version neither of them published separates nothing.
        Assert.Equal(
            ListingMatchKind.None,
            PluginListingMatcher.Match(new LoadedPluginIdentity("x.y", "BasinHoverTips", "3.0.0"), twoOwners).Kind);

        // Both at the same version: still no guess.
        var sameVersion = new List<CatalogEntry>
        {
            Listing("ExampleTeam", "BasinHoverTips", "1.0.19", "Client"),
            Listing("Someone_Else", "BasinHoverTips", "1.0.19", "Client"),
        };
        Assert.Equal(
            ListingMatchKind.None,
            PluginListingMatcher.Match(new LoadedPluginIdentity("x.y", "BasinHoverTips", "1.0.19"), sameVersion).Kind);
    }

    /// <summary>
    /// Job LINK1 item 2, the cache: one row per loaded plugin GUID under
    /// <c>progenitor-cache/identity/loaded/</c>, carrying the confidence, and stale as soon as the
    /// loaded version changes.
    /// What this would catch: a remembered match surviving an update of the same GUID (the page would
    /// show the old package's description for new bytes), or a confidence that is not persisted, which
    /// would let a remembered name match come back as an identity.
    /// </summary>
    [Fact]
    public void A_remembered_name_match_is_keyed_by_the_loaded_version_and_keeps_its_confidence()
    {
        var cache = new LoadedListingIdentityCache(Bep);
        Assert.Null(cache.TryRead("acme.PhialStore"));
        Assert.True(cache.Write(new LoadedListingRecord(
            "acme.PhialStore", "1.0.19", "ExampleTeam-BasinHoverTips", ListingMatchKind.Name)));
        string path = Path.Combine(
            Bep, OrdinaryIdentityCache.CacheFolderName, OrdinaryIdentityCache.IdentityFolderName,
            LoadedListingIdentityCache.LoadedFolderName, "acme.PhialStore.json");
        Assert.True(File.Exists(path), "the row belongs under progenitor-cache/identity/loaded/");

        var read = cache.TryRead("acme.PhialStore");
        Assert.NotNull(read);
        Assert.Equal("ExampleTeam-BasinHoverTips", read!.Key);
        Assert.Equal(ListingMatchKind.Name, read.Kind);
        Assert.NotEqual("", read.MatchedUtc);
        // The same loaded copy reuses it; an updated one does not.
        Assert.True(read.IsFor("1.0.19"));
        Assert.False(read.IsFor("1.0.20"));
        Assert.False(read.IsFor(""));
        // A remembered "nothing names it" is an empty key, so a miss also costs one sweep only.
        Assert.True(cache.Write(new LoadedListingRecord("other.tool.mod", "2.1", "", ListingMatchKind.None)));
        Assert.Equal("", cache.TryRead("other.tool.mod")!.Key);
        Assert.Equal(ListingMatchKind.None, cache.TryRead("other.tool.mod")!.Kind);
        // A row with no GUID is not a row, and a memory-only store keeps no file at all.
        Assert.False(cache.Write(new LoadedListingRecord("", "1.0", "k", ListingMatchKind.Name)));
        var memory = new LoadedListingIdentityCache(null);
        Assert.Null(memory.Root);
        Assert.True(memory.Write(new LoadedListingRecord("a.b", "1.0", "O-N", ListingMatchKind.Name)));
        Assert.Equal("O-N", memory.TryRead("a.b")!.Key);
        memory.ClearMemory();
        Assert.Null(memory.TryRead("a.b"));
    }

    /// <summary>
    /// Job LINK1 item 3, narrowed by ABOUTSRC1: a mod another tool installed — BasinHoverTips, whose GUID
    /// is <c>acme.PhialStore</c> and which no ledger, identity record or listing id names — is still matched
    /// to its listing by name, and the readback says so, but nothing of the listing is shown until a package
    /// manifest beside the DLL names that package at that listing's version. Then the one-liner, README,
    /// author and store link are shown; a manifest of another version shows nothing again.
    /// What this would catch: a listing found by a spelling (another version, or another project with the
    /// same name) lending its README, attribution and side claims to these files, or a verified manifest no
    /// longer bringing the listing back.
    /// </summary>
    [Fact]
    public void A_name_matched_listing_is_shown_only_when_a_manifest_beside_the_DLL_names_that_version()
    {
        SeedBasinHoverTips();
        ModsMenu.Open();
        using var mod = ModsMenu.Register(new object(), "acme.PhialStore", "BasinHoverTips");
        ModsMenu.OpenMod("acme.PhialStore");

        var rows = PageRows();
        var listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("name", listing.GetProperty("matched").GetString());
        Assert.Equal("ExampleTeam-BasinHoverTips", listing.GetProperty("key").GetString());
        Assert.Equal("unverified", listing.GetProperty("verified").GetString());
        Assert.False(listing.GetProperty("shown").GetBoolean());
        Assert.Equal("1.0.19", listing.GetProperty("listingVersion").GetString());
        Assert.False(listing.TryGetProperty("rating", out _));
        Assert.False(TryRow(rows, ModPagePane.RowOneLiner, out _), "an unverified listing lends no one-liner");
        Assert.False(Row(rows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink).GetProperty("shown").GetBoolean(), "an unverified listing lends no store page");
        Assert.False(TryRow(rows, ModPagePane.RowBadges, out _), "an unverified listing lends no side claim");
        Assert.Equal("", Row(rows, ModPagePane.RowMeta).GetProperty(ModPagePane.RowMeta).GetProperty("author").GetString());
        Assert.Equal("none", Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.DoesNotContain("Hover a blood item", page, StringComparison.Ordinal);
        Assert.Contains(ModPageCopy.NoDescription, page, StringComparison.Ordinal);

        // A mod manager's folder: BepInEx/plugins/ExampleTeam-BasinHoverTips/manifest.json at the listing's version.
        string dll = PlaceManagedPackage("ExampleTeam", "BasinHoverTips", "1.0.19");
        ModsMenu.PluginFileProbe = id => string.Equals(id, "acme.PhialStore", StringComparison.OrdinalIgnoreCase) ? dll : null;
        ModsMenu.OpenMod("acme.PhialStore");
        rows = PageRows();
        listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("manifest", listing.GetProperty("verified").GetString());
        Assert.True(listing.GetProperty("shown").GetBoolean());
        var meta = Row(rows, ModPagePane.RowMeta).GetProperty(ModPagePane.RowMeta);
        Assert.Equal("1.0.19 · 2026-08-30 · by ExampleTeam", meta.GetProperty("line").GetString());
        Assert.Equal(
            "by ExampleTeam · Shows the blood pool of an item you hover.",
            Row(rows, ModPagePane.RowOneLiner).GetProperty(ModPagePane.RowOneLiner).GetString());
        Assert.Equal(
            "https://thunderstore.io/c/v-rising/p/ExampleTeam/BasinHoverTips/",
            Row(rows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink).GetProperty("url").GetString());
        var readme = Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme);
        Assert.Equal("ready", readme.GetProperty("state").GetString());
        Assert.Equal("listing", readme.GetProperty("source").GetString());
        Assert.Contains("Hover a blood item", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);

        // The same package at an older version (an update is available): the package facts stay, the README,
        // written for 1.0.19, does not.
        PlaceManagedPackage("ExampleTeam", "BasinHoverTips", "1.0.18");
        ModsMenu.OpenMod("acme.PhialStore");
        rows = PageRows();
        listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("manifest", listing.GetProperty("verified").GetString());
        Assert.False(listing.GetProperty("versionMatch").GetBoolean());
        Assert.Equal(
            "by ExampleTeam · Shows the blood pool of an item you hover.",
            Row(rows, ModPagePane.RowOneLiner).GetProperty(ModPagePane.RowOneLiner).GetString());
        Assert.Equal("none", Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
        Assert.DoesNotContain("Hover a blood item", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);

        // Another owner's manifest is another package: nothing is shown.
        string otherDll = PlaceManagedPackage("OtherTeam", "BasinHoverTips", "1.0.19");
        ModsMenu.PluginFileProbe = id => string.Equals(id, "acme.PhialStore", StringComparison.OrdinalIgnoreCase) ? otherDll : null;
        ModsMenu.OpenMod("acme.PhialStore");
        Assert.Equal(
            "unverified",
            Row(PageRows(), ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing).GetProperty("verified").GetString());

        // A plugin the index does not name at all is unchanged: no listing.
        using var unknown = ModsMenu.Register(new object(), "other.tool.mod", "GroundPickupGlow");
        ModsMenu.OpenMod("other.tool.mod");
        var unknownListing = Row(PageRows(), ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("none", unknownListing.GetProperty("matched").GetString());
        Assert.Equal("none", unknownListing.GetProperty("verified").GetString());
        Assert.Contains(ModPageCopy.NoDescription, JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);
    }

    /// <summary>
    /// Job LINK1 item 2, the rule that matters most: a name match is shown and never acted on. The
    /// page offers no action for it, nothing on it is captioned Uninstall, and neither the removal
    /// seam nor the ledger-scoped remover is reached.
    /// What removal on a name match would break: two authors ship a <c>BasinHoverTips</c>, or the
    /// player's copy came from an entirely different package that happens to spell the same name —
    /// Progenitor would then delete files it never wrote, on the strength of a spelling, and the
    /// dependency check would count a package as satisfied that is not installed at all.
    /// </summary>
    [Fact]
    public void A_name_match_shows_the_listing_but_never_removes_a_file()
    {
        SeedBasinHoverTips();
        int removals = 0;
        ModsMenu.InstalledRemover = _ =>
        {
            removals++;
            return new ServerInstallResult(true, "removed", new[] { "file" }, Array.Empty<string>(), false);
        };
        var seam = new FakeSeam();
        ModsMenu.UninstallSeam = seam;
        // Even a reloader that reports this package's name must not make a name match removable.
        ModsMenu.ReloaderNamesProbe = () => new[] { "BasinHoverTips" };
        ModsMenu.Open();
        using var mod = ModsMenu.Register(new object(), "acme.PhialStore", "BasinHoverTips");
        ModsMenu.OpenMod("acme.PhialStore");

        var rows = PageRows();
        Assert.Equal(
            "name",
            Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing).GetProperty("matched").GetString());
        Assert.True(TryRow(rows, ModPagePane.RowStatus, out _), "the status is still drawn");
        Assert.False(TryRow(rows, ModPagePane.RowAction, out _), "a name match must offer no action");

        // There is no control to press, so there is no route to a removal at all: the page carries
        // nothing captioned Uninstall for a reader — or a bridge click — to find.
        Assert.DoesNotContain(
            Escaped(ModPageCopy.Uninstall),
            JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")),
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(
            () => ModsMenu.ClickVisibleWidget("content", ModPageCopy.Uninstall, null, null));
        Assert.Equal(0, removals);
        Assert.Equal(0, seam.MarkCalls);
        Assert.Equal(0, seam.CancelInstallCalls);
        Assert.Equal(0, seam.CancelUninstallCalls);
    }

    // ---- ABOUTSRC1: where the About text comes from ------------------------------------------

    /// <summary>A plugin type that declares its About text (the <c>[ModAbout]</c> path).</summary>
    [ModAbout("Declared **about** text.")]
    private sealed class DeclaredAboutPlugin
    {
    }

    /// <summary>
    /// ABOUTSRC1, the live case: a 2.0 rework hot-loaded from <c>progenitor-reload</c>, which no install
    /// ledger names, has the GUID that a 1.x package's name spells. That listing is another version of
    /// another project: its README (attribution, a "server-only, nothing to install on the client"
    /// claim), one-liner, server badge and store page must not appear. The page shows the plugin's own
    /// embedded README; without one, the README beside its DLL (<c>&lt;dll&gt;.README.md</c>; a plain
    /// <c>README.md</c> in the shared reload folder belongs to no plugin); without that, its description.
    /// What this would catch: the listing matched by GUID or name being shown again for a file it does
    /// not describe, or the plugin's own text losing to it.
    /// </summary>
    [Fact]
    public void A_reload_folder_mod_with_a_same_id_listing_at_another_version_shows_its_own_text()
    {
        string reload = Path.Combine(Bep, "progenitor-reload");
        Directory.CreateDirectory(reload);
        string dll = Path.Combine(reload, "Rework.dll");
        File.WriteAllText(dll, "placeholder");
        ReloadFolderPlugins.ResetForTests();
        ModsMenu.ReloadFolderProbe = () => reload;
        ModsMenu.ReloadGuidsProbe = path => string.Equals(Path.GetFileName(path), "Rework.dll", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Rework" }
            : Array.Empty<string>();
        try
        {
            var catalog = new PageCatalog();
            catalog.Entries.Add(Listing(
                "OtherTeam", "Rework", "1.0.85", "Server",
                description: "The 1.x server mod.",
                released: "2025-01-02T00:00:00Z"));
            catalog.Readme = "# Rework 1.x\n\nMade by OtherTeam. Server-only, nothing to install on the client.";
            ModsMenu.SetInstallCatalogForTests(catalog);
            PluginAboutStore.Note("Rework", new PluginAboutFacts { Embedded = "# Rework 2.0\n\nA separate rework of the idea." });
            ModsMenu.Open();
            using var mod = ModsMenu.Register(new object(), "Rework", "Rework", "Rework 2.0 description.");
            ModsMenu.OpenMod("Rework");

            var rows = PageRows();
            var listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
            Assert.Equal("id", listing.GetProperty("matched").GetString());
            Assert.Equal("OtherTeam-Rework", listing.GetProperty("key").GetString());
            Assert.Equal("unverified", listing.GetProperty("verified").GetString());
            Assert.False(listing.GetProperty("shown").GetBoolean());
            Assert.Equal("embedded", Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
            Assert.False(Row(rows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink).GetProperty("shown").GetBoolean(), "the 1.x store page is not this mod's");
            Assert.False(TryRow(rows, ModPagePane.RowBadges, out _), "the 1.x side claim is not this mod's");
            Assert.Equal("", Row(rows, ModPagePane.RowMeta).GetProperty(ModPagePane.RowMeta).GetProperty("released").GetString());
            string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
            Assert.Contains("A separate rework of the idea.", page, StringComparison.Ordinal);
            Assert.DoesNotContain("Made by OtherTeam", page, StringComparison.Ordinal);
            Assert.DoesNotContain("by OtherTeam", page.Replace("Made by OtherTeam", ""), StringComparison.Ordinal);
            Assert.DoesNotContain("nothing to install on the client", page, StringComparison.Ordinal);
            Assert.DoesNotContain("The 1.x server mod.", page, StringComparison.Ordinal);

            // No embedded README: a README.md in the shared reload folder is nobody's, Rework.README.md is this one's.
            PluginAboutStore.ResetForTests();
            File.WriteAllText(Path.Combine(reload, "README.md"), "Shared folder text.");
            ModsMenu.OpenMod("Rework");
            Assert.Equal("description", Row(PageRows(), ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
            Assert.Contains("Rework 2.0 description.", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(reload, "Rework.README.md"), "Beside the DLL.");
            ModsMenu.OpenMod("Rework");
            Assert.Equal("beside", Row(PageRows(), ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
            page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
            Assert.Contains("Beside the DLL.", page, StringComparison.Ordinal);
            Assert.DoesNotContain("Shared folder text.", page, StringComparison.Ordinal);
        }
        finally
        {
            ModsMenu.ReloadFolderProbe = null;
            ModsMenu.ReloadGuidsProbe = null;
            ReloadFolderPlugins.ResetForTests();
        }
    }

    /// <summary>
    /// ABOUTSRC1: an install the ledger records at the listing's own version shows the listing's README, ahead of
    /// the plugin's embedded one. With an update available (the ledger names the package at an older version) the
    /// package is still verified: author, store page, sides and the uninstall sentence that depends on the side are
    /// unchanged, and only the README, written for the newer version, gives way to the plugin's own text.
    /// What this would catch: a real ledgered install losing its store README, an out-of-date install losing its
    /// package facts (badges, store page, the server-only uninstall wording), or the newer version's README being
    /// shown for the older files.
    /// </summary>
    [Fact]
    public void A_ledger_install_keeps_its_package_facts_with_an_update_and_the_README_only_at_its_version()
    {
        SeedCryptSnooze(readme: "# CryptSnooze\n\nSleep through the day in your coffin.");
        PluginAboutStore.Note("example.cryptsnooze", new PluginAboutFacts { Embedded = "Embedded CryptSnooze text." });
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");

        var rows = PageRows();
        var listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("ledger", listing.GetProperty("verified").GetString());
        Assert.True(listing.GetProperty("versionMatch").GetBoolean());
        Assert.Equal("listing", Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains("Sleep through the day in your coffin.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedded CryptSnooze text.", page, StringComparison.Ordinal);
        string sameVersionUninstall = ModsMenu.ModPage!.Model.PromptBody(ModPromptKind.Uninstall);
        var sameVersionBadges = Strings(Row(rows, ModPagePane.RowBadges).GetProperty(ModPagePane.RowBadges));

        // An update is available: the ledger names the package at 2.0.3, the listing is 2.0.4.
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("Corvane-CryptSnooze", "example.cryptsnooze.dll", "2.0.3", ServerInstallLedger.CategoryMod),
        };
        ModsMenu.OpenMod("example.cryptsnooze");
        rows = PageRows();
        listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("source", listing.GetProperty("matched").GetString());
        Assert.Equal("ledger", listing.GetProperty("verified").GetString());
        Assert.True(listing.GetProperty("shown").GetBoolean());
        Assert.False(listing.GetProperty("versionMatch").GetBoolean());
        Assert.Equal("embedded", Row(rows, ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
        Assert.Equal("Corvane", Row(rows, ModPagePane.RowMeta).GetProperty(ModPagePane.RowMeta).GetProperty("author").GetString());
        Assert.Equal("2.0.3", Row(rows, ModPagePane.RowMeta).GetProperty(ModPagePane.RowMeta).GetProperty("version").GetString());
        Assert.Equal(
            "https://thunderstore.io/c/v-rising/p/Corvane/CryptSnooze/",
            Row(rows, ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink).GetProperty("url").GetString());
        Assert.Equal(sameVersionBadges, Strings(Row(rows, ModPagePane.RowBadges).GetProperty(ModPagePane.RowBadges)));
        Assert.Equal(sameVersionUninstall, ModsMenu.ModPage!.Model.PromptBody(ModPromptKind.Uninstall));
        page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        Assert.Contains("Embedded CryptSnooze text.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Sleep through the day in your coffin.", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ABOUTSRC1: the sidebar's server-half warning reads the same rule. A two-sided package installed by the ledger
    /// at an older version than the listing still carries the red marker, because the package is verified.
    /// What this would catch: an update becoming available silently dropping the missing-server-half warning.
    /// </summary>
    [Fact]
    public void The_sidebar_warning_stays_on_a_verified_package_with_an_update_available()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Verified("ExampleTeam", "HuntsAndGates", "1.1.0", "example.huntsandgates"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0", ServerInstallLedger.CategoryMod),
        };
        Handshake.Provide("example.huntsandgates", "1.0.0");
        using var hunts = ModsMenu.Register(new object(), "example.huntsandgates", "HuntsAndGates");
        ModsMenu.Open();
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.True(Entry(doc.RootElement, "example.huntsandgates").GetProperty("warning").GetBoolean());
        ModsMenu.OpenMod("example.huntsandgates");
        Assert.Equal(ModPageCopy.WarningServerHalfMissing, Row(PageRows(), ModPagePane.RowWarning).GetProperty(ModPagePane.RowWarning).GetString());
    }

    /// <summary>
    /// ABOUTSRC1: the check reads the copy on this side. A plugin this client loaded, whose only ledger row is the
    /// local server's (a server-root row), is not verified by that row: the server's file is not the one loaded here.
    /// What this would catch: the local server's install lending its listing to a hand-copied client DLL.
    /// </summary>
    [Fact]
    public void A_server_root_ledger_row_does_not_verify_the_copy_this_client_loaded()
    {
        SeedCryptSnooze(readme: "# CryptSnooze\n\nSleep through the day in your coffin.");
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("Corvane-CryptSnooze", "example.cryptsnooze.dll", "2.0.4", ServerInstallLedger.CategoryMod, InstalledState.ServerRoot),
        };
        using var mod = ModsMenu.Register(new object(), "example.cryptsnooze", "CryptSnooze");
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        var rows = PageRows();
        var listing = Row(rows, ModsMenu.RowPageListing).GetProperty(ModsMenu.RowPageListing);
        Assert.Equal("unverified", listing.GetProperty("verified").GetString());
        Assert.False(listing.GetProperty("shown").GetBoolean());
        Assert.DoesNotContain("Sleep through the day in your coffin.", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);

        // The pure rule: this side's rows when there are any; none for a copy loaded here; the other root's only
        // for a mod this side does not have.
        var server = Ledgered("Corvane-CryptSnooze", "x.dll", "2.0.4", ServerInstallLedger.CategoryMod, InstalledState.ServerRoot);
        var client = Ledgered("Corvane-CryptSnooze", "x.dll", "2.0.3", ServerInstallLedger.CategoryMod);
        Assert.Equal(new[] { client }, InstalledListingCheck.LedgerOfThisSide(new[] { server, client }, true, InstalledState.ServerRoot));
        Assert.Empty(InstalledListingCheck.LedgerOfThisSide(new[] { server }, true, InstalledState.ServerRoot));
        Assert.Equal(new[] { server }, InstalledListingCheck.LedgerOfThisSide(new[] { server }, false, InstalledState.ServerRoot));
    }

    /// <summary>
    /// ABOUTSRC1, the plugin's own sources read from a real type: <see cref="PluginAboutReader.FromType"/>
    /// takes the embedded <c>README.md</c> resource of the type's assembly (this test assembly embeds
    /// <c>Fixtures/About/README.md</c>) and the type's <c>[ModAbout]</c>. The embedded README wins on the
    /// page; with only <c>[ModAbout]</c> the page shows that, rendered as Markdown.
    /// What this would catch: the resource convention (<c>README.md</c> or <c>*.README.md</c>) or the
    /// by-name attribute read breaking, or the order between them flipping.
    /// </summary>
    [Fact]
    public void The_embedded_README_and_the_ModAbout_attribute_are_the_plugins_own_About_text()
    {
        var facts = PluginAboutReader.FromType(typeof(DeclaredAboutPlugin));
        Assert.Contains("ships inside the test assembly", facts.Embedded, StringComparison.Ordinal);
        Assert.Equal("Declared **about** text.", facts.Declared);
        Assert.Equal("", PluginAboutReader.DeclaredAbout(typeof(ModPageTests)));

        ModsMenu.SetInstallCatalogForTests(new PageCatalog());
        PluginAboutStore.NoteFromType("example.embedded", typeof(DeclaredAboutPlugin));
        ModsMenu.Open();
        using var embedded = ModsMenu.Register(new object(), "example.embedded", "Embedded");
        ModsMenu.OpenMod("example.embedded");
        Assert.Equal("embedded", Row(PageRows(), ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme).GetProperty("source").GetString());
        Assert.Contains("ships inside the test assembly", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);

        PluginAboutStore.Note("example.declared", new PluginAboutFacts { Declared = PluginAboutReader.DeclaredAbout(typeof(DeclaredAboutPlugin)) });
        using var declared = ModsMenu.Register(new object(), "example.declared", "Declared", "Registered description.");
        ModsMenu.OpenMod("example.declared");
        var readme = Row(PageRows(), ModPagePane.RowReadme).GetProperty(ModPagePane.RowReadme);
        Assert.Equal("declared", readme.GetProperty("source").GetString());
        Assert.Equal("ready", readme.GetProperty("state").GetString());
        string page = JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"));
        // The listing README renderer: emphasis markers are not drawn as characters.
        Assert.Contains("Declared about text.", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// ABOUTSRC1, the pure rules: which resource is the embedded README, the listing check (package, then version),
    /// and the About order.
    /// What this would catch: another assembly's <c>*.README.md</c> (a merged dependency) being read, an owner
    /// mismatch passing as verified, a version mismatch losing the package or showing the README, or the order
    /// between the sources changing.
    /// </summary>
    [Fact]
    public void The_about_sources_and_the_listing_check_follow_their_rules()
    {
        Stream Open(string name) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("text of " + name));
        Assert.Equal("text of README.md", PluginAboutReader.EmbeddedReadme(new[] { "My.Mod.README.md", "README.md" }, Open, "My.Mod"));
        Assert.Equal("text of My.Mod.README.md", PluginAboutReader.EmbeddedReadme(new[] { "Dep.README.md", "My.Mod.README.md" }, Open, "My.Mod"));
        // A merged dependency's README is never the plugin's, however short its name.
        Assert.Equal("", PluginAboutReader.EmbeddedReadme(new[] { "A.README.md", "Dep.Lib.README.md" }, Open, "My.Mod"));
        Assert.Equal("", PluginAboutReader.EmbeddedReadme(new[] { "My.Mod.READMEmd", "notes.md", "README.md.bak" }, Open, "My.Mod"));
        // The read is bounded and sized to the stream, not a fixed buffer.
        string big = new string('x', PluginAboutReader.MaxChars + 10);
        Assert.Equal(PluginAboutReader.MaxChars, PluginAboutReader.ReadBounded(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(big))).Length);
        Assert.Equal("short", PluginAboutReader.ReadBounded(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("short"))));

        var entry = Listing("OtherTeam", "Rework", "1.0.85", "Server");
        InstalledLedgerRow Row(string source) => new("OtherTeam-Rework", "sha", "", "Rework.dll", source, ServerInstallLedger.CategoryMod, InstalledState.ClientRoot, "");
        void Is(ListingVerification v, bool same, ListingCheck check)
        {
            Assert.Equal(v, check.Verification);
            Assert.Equal(same, check.SameVersion);
        }

        Is(ListingVerification.None, false, InstalledListingCheck.Check(null, null, null));
        Is(ListingVerification.Unverified, false, InstalledListingCheck.Check(entry, null, null));
        Is(ListingVerification.Ledger, true, InstalledListingCheck.Check(entry, new[] { Row("package:OtherTeam-Rework@1.0.85") }, null));
        Is(ListingVerification.Ledger, true, InstalledListingCheck.Check(entry, new[] { Row("package:otherteam-rework@v1.0.85") }, null));
        // An update is available: the package is verified, the README is not this version's.
        Is(ListingVerification.Ledger, false, InstalledListingCheck.Check(entry, new[] { Row("package:OtherTeam-Rework@1.0.80") }, null));
        Is(ListingVerification.Unverified, false, InstalledListingCheck.Check(entry, new[] { Row("package:NewTeam-Rework@1.0.85") }, null));

        string good = PlaceManagedPackage("OtherTeam", "Rework", "1.0.85", "Manifest one-liner.");
        Is(ListingVerification.Manifest, true, InstalledListingCheck.Check(entry, null, PackageManifestBeside.TryRead(good)));
        string older = PlaceManagedPackage("OtherTeam", "Rework2", "1.0.0");
        Is(ListingVerification.Unverified, false, InstalledListingCheck.Check(entry, null, PackageManifestBeside.TryRead(older)));
        string otherOwner = PlaceManagedPackage("NewTeam", "Rework", "1.0.85");
        Is(ListingVerification.Unverified, false, InstalledListingCheck.Check(entry, null, PackageManifestBeside.TryRead(otherOwner)));

        var all = new PluginAboutFacts { Embedded = "e", Beside = "b", Declared = "d", Description = "s" };
        Assert.Equal(ModAboutSource.Listing, ModAboutText.Choose(true, "l", all).Source);
        Assert.Equal(ModAboutSource.Embedded, ModAboutText.Choose(false, "l", all).Source);
        Assert.Equal(ModAboutSource.Beside, ModAboutText.Choose(true, " ", new PluginAboutFacts { Beside = "b", Declared = "d" }).Source);
        Assert.Equal(ModAboutSource.Declared, ModAboutText.Choose(false, null, new PluginAboutFacts { Declared = "d", Description = "s" }).Source);
        Assert.Equal(ModAboutSource.Description, ModAboutText.Choose(false, null, new PluginAboutFacts { Description = "s" }).Source);
        Assert.Equal(ModAboutSource.None, ModAboutText.Choose(false, null, null).Source);
    }

    /// <summary>
    /// ABOUTSRC1: the package manifest in the layouts mod managers write. Beside the DLL; one folder up
    /// (<c>Owner-Name/Sub/X.dll</c>); under the mod's own subfolder named <c>plugins</c>; in an extracted
    /// <c>Owner-Name-1.2.3</c> folder. Not in the shared <c>BepInEx/plugins</c> folder, not above it, not in a folder
    /// whose name is not the package's, and not two folders up. A rewritten manifest is read again (the cache is
    /// stamped by the file).
    /// What this would catch: a mod-manager install whose DLL sits in a subfolder losing its package facts, a
    /// manifest in a shared folder being trusted for every plugin there, or the cache serving a stale version.
    /// </summary>
    [Fact]
    public void The_package_manifest_is_found_in_mod_manager_layouts_and_nowhere_else()
    {
        string root = Path.Combine(Bep, "plugins");
        string Manifest(string dir, string name, string version)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\",\"description\":\"d\"}");
            return dir;
        }

        string flat = Manifest(Path.Combine(root, "OtherTeam-Rework"), "Rework", "1.0.85");
        Assert.Equal("OtherTeam-Rework", PackageManifestBeside.TryRead(Path.Combine(flat, "Rework.dll"))?.Package);
        var sub = PackageManifestBeside.TryRead(Path.Combine(flat, "Sub", "Rework.dll"));
        Assert.Equal("OtherTeam-Rework", sub?.Package);
        Assert.Equal(flat, sub?.Folder);
        Assert.Equal("OtherTeam-Rework", PackageManifestBeside.TryRead(Path.Combine(flat, "plugins", "Rework.dll"))?.Package);
        Assert.Null(PackageManifestBeside.TryRead(Path.Combine(flat, "Sub", "Deeper", "Rework.dll")));

        string extracted = Manifest(Path.Combine(root, "OtherTeam-Tool-1.2.3"), "Tool", "1.2.3");
        var tool = PackageManifestBeside.TryRead(Path.Combine(extracted, "Tool.dll"));
        Assert.Equal("OtherTeam-Tool", tool?.Package);
        Assert.Equal("1.2.3", tool?.Version);
        Assert.Equal("", PackageManifestBeside.OwnerFromFolder("Tool-1.2.3", "Tool", "1.2.3"));
        Assert.Equal("OtherTeam", PackageManifestBeside.OwnerFromFolder("OtherTeam-Tool-1.2.3", "Tool", "1.2.3"));

        Manifest(Path.Combine(root, "Loose"), "Rework", "1.0.85");
        Assert.Null(PackageManifestBeside.TryRead(Path.Combine(root, "Loose", "Rework.dll")));
        Manifest(root, "Rework", "1.0.85");
        Assert.Null(PackageManifestBeside.TryRead(Path.Combine(root, "Rework.dll")));
        Assert.Null(PackageManifestBeside.TryRead(Path.Combine(root, "Unnamed", "Rework.dll")));
        Assert.True(PluginAboutReader.IsSharedFolder(root));
        Assert.True(PluginAboutReader.IsSharedFolder(Bep));
        Assert.False(PluginAboutReader.IsSharedFolder(Path.Combine(flat, "plugins")));

        // A rewritten manifest is read again.
        Manifest(flat, "Rework", "1.0.86");
        Assert.Equal("1.0.86", PackageManifestBeside.TryRead(Path.Combine(flat, "Rework.dll"))?.Version);

        // The package folder's README is the text beside a DLL in its subfolder.
        File.WriteAllText(Path.Combine(flat, "README.md"), "Package folder README.");
        Directory.CreateDirectory(Path.Combine(flat, "Sub"));
        Directory.CreateDirectory(Path.Combine(extracted, "Sub"));
        Assert.Equal("Package folder README.", PluginAboutReader.Beside(Path.Combine(flat, "Sub", "Rework.dll")));
        Assert.Equal("d", PluginAboutReader.Beside(Path.Combine(extracted, "Sub", "Tool.dll")));
    }

    /// <summary>
    /// ABOUTSRC1, security: plugin-supplied Markdown cannot inject TextMeshPro markup. The renderer keeps a code
    /// span's and a fence's text literally (it may contain <c>&lt;size=999&gt;</c>), so the page puts it into plain
    /// text cells, and a plain text cell is written through <see cref="UiRichText.Literal"/>, which gives every
    /// <c>&lt;</c> its own <c>noparse</c> run.
    /// What this would catch: an About cell becoming a linked or glyph cell whose write skips the escape, or the
    /// escape no longer covering a tag that a README's code span carries.
    /// </summary>
    [Fact]
    public void Plugin_about_text_cannot_inject_rich_text_tags()
    {
        ModsMenu.SetInstallCatalogForTests(new PageCatalog());
        PluginAboutStore.Note("example.inject", new PluginAboutFacts
        {
            Embedded = "Try `<size=999>big</size>` now.\n\n```\n<sprite=0><color=red>x\n```\n\n<b>raw</b> text",
        });
        ModsMenu.Open();
        using var mod = ModsMenu.Register(new object(), "example.inject", "Inject");
        ModsMenu.OpenMod("example.inject");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        var texts = new List<string>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("label", out var label) || !(label.GetString() ?? "").StartsWith("page-about-", StringComparison.Ordinal)) continue;
            var cell = Cell(row, "text");
            Assert.Equal("text", cell.GetProperty("kind").GetString());
            Assert.False(cell.TryGetProperty("linkify", out var linkify) && linkify.ValueKind == JsonValueKind.True, "an About cell is never a linked cell");
            texts.Add(cell.GetProperty("text").GetString() ?? "");
        }

        Assert.NotEmpty(texts);
        string all = string.Join("\n", texts);
        Assert.Contains("<size=999>", all, StringComparison.Ordinal);
        Assert.Contains("<sprite=0>", all, StringComparison.Ordinal);
        foreach (var text in texts)
        {
            // Every '<' the label receives sits inside its own noparse run, so no tag can open.
            string drawn = UiRichText.Literal(text);
            Assert.Equal(text.Split('<').Length - 1, CountOf(drawn, "<noparse><</noparse>"));
            Assert.DoesNotContain("<size", drawn.Replace("<noparse><</noparse>", ""), StringComparison.Ordinal);
            Assert.DoesNotContain("<sprite", drawn.Replace("<noparse><</noparse>", ""), StringComparison.Ordinal);
        }
    }

    private static int CountOf(string s, string part)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }

    // ---- MODSPOLISH1: the head's third row, and the muted provenance block -------------------

    /// <summary>
    /// MODSPOLISH1 (live, BasinHoverTips, 2026-09-13): the head block's third row exists only when
    /// it has something on it. A plugin no listing names has no one-liner and nothing known about
    /// the sides it runs on, so there is no third row at all — before this an
    /// <c>Unknown until checked</c> placeholder badge kept the row alive and the page drew an empty
    /// band between the meta line and the badge. A plugin with a listing keeps the row exactly
    /// where it was, one-liner and badge together.
    /// What this would catch: restoring the placeholder badge (the empty band comes back, because
    /// <see cref="HeadBlockLayout.Build"/> emits the row for a badge alone), or dropping the third
    /// row for a page that has a one-liner or a real side badge.
    /// </summary>
    [Fact]
    public void A_page_with_no_listing_draws_no_third_head_row_and_no_placeholder_badge()
    {
        ModsMenu.SetInstallCatalogForTests(new PageCatalog());
        ModsMenu.Open();
        using var unknown = ModsMenu.Register(new object(), "other.tool.mod", "GroundPickupGlow");
        ModsMenu.OpenMod("other.tool.mod");

        var rows = PageRows();
        // The head is still the title and the meta line; only the third row is gone.
        Assert.True(TryRow(rows, ModPagePane.RowTitle, out _));
        Assert.True(TryRow(rows, ModPagePane.RowMeta, out _));
        Assert.False(TryRow(rows, ModPagePane.RowOneLiner, out _), "there is no one-liner to draw");
        Assert.False(TryRow(rows, ModPagePane.RowBadges, out _), "nothing is known about the sides");
        // Not merely keyless: the cell row itself is not built, so no band is drawn for it.
        Assert.DoesNotContain(ModPagePane.HeadKey + "-byline", RowLabels(rows));
        Assert.Contains(ModPagePane.HeadKey + "-title", RowLabels(rows));
        // MENUKIT2 round C: a button row with nothing on it is hidden (it left a 60 px gap under the
        // title live); its readback (page-meta above) moves to the title row.
        Assert.DoesNotContain(ModPagePane.HeadKey + "-meta", RowLabels(rows));

        // A listing brings the row back, with the one-liner and the side badge on it as before (ABOUTSRC1: a
        // listing tied to these files, here by the package manifest beside the DLL).
        SeedBasinHoverTips();
        string dll = PlaceManagedPackage("ExampleTeam", "BasinHoverTips", "1.0.19");
        ModsMenu.PluginFileProbe = id => string.Equals(id, "acme.PhialStore", StringComparison.OrdinalIgnoreCase) ? dll : null;
        using var hover = ModsMenu.Register(new object(), "acme.PhialStore", "BasinHoverTips");
        ModsMenu.OpenMod("acme.PhialStore");
        var listed = PageRows();
        Assert.Contains(ModPagePane.HeadKey + "-byline", RowLabels(listed));
        Assert.Equal(
            "by ExampleTeam · Shows the blood pool of an item you hover.",
            Row(listed, ModPagePane.RowOneLiner).GetProperty(ModPagePane.RowOneLiner).GetString());
        Assert.Equal(
            new[] { ModFrameworkLine.SidesClient },
            Strings(Row(listed, ModPagePane.RowBadges).GetProperty(ModPagePane.RowBadges)));
    }

    /// <summary>
    /// MODSPOLISH1: the provenance under the head is one muted block, not three full-size rows that
    /// repeat it. The sidebar subtitle row (<c>1.0.19 · another tool</c>) is gone — the head's meta
    /// line already carries the version and the action button already says whether this menu may
    /// remove the file — and the read-only sentence and the server requirement are one muted note,
    /// each sentence given its full stop.
    /// What this would catch: the subtitle row coming back beside an identical meta line, or the
    /// provenance drawn as ordinary body labels again (the head's own facts, twice, at full size).
    /// </summary>
    [Fact]
    public void The_provenance_under_the_head_is_one_muted_block_without_the_subtitle_row()
    {
        ModsMenu.SetInstallCatalogForTests(new PageCatalog());
        // The inventory knows this plugin, so the row carries a server requirement as well as the
        // read-only sentence: both sentences, one row.
        Handshake.Provide("other.tool.mod", "2.1");
        ModsMenu.Open();
        using var unknown = ModsMenu.Register(new object(), "other.tool.mod", "GroundPickupGlow");
        ModsMenu.OpenMod("other.tool.mod");

        string expected = ModPageCopy.ProvenanceBlock(
            ModMenuInventory.OtherToolNote, "Server requirement unknown");
        Assert.Equal(
            "Installed by another tool — remove it with that tool. Server requirement unknown.",
            expected);
        // MODPAGE2: the Managed by row of Details: its first sentence as the value, the whole block in the column.
        var provenanceRows = PageRows();
        var labels = RowLabels(provenanceRows);
        string managedKey = ModPagePane.FactKeyPrefix + "managed";
        var provenance = RowLabelled(provenanceRows, managedKey);
        // Live (HotDemo, 2026-10-03): with no word from the server's handshake request the page says
        // nothing about the server requirement; the block is the provenance note alone. Live F24
        // (2026-10-07): another tool's plugin is an ordinary plugin, and with no loaded file to identify
        // (no chainloader here) its note is the real reason, not the generic other-tool line.
        string block = ModPageCopy.ProvenanceBlock(ModSwitch.ManagedElsewhereReason(Progenitor.PluginActions.PluginFileActions.WhyNoLocation));
        int stop = block.IndexOf(". ", StringComparison.Ordinal);
        Assert.Equal(stop > 0 ? block.Substring(0, stop + 1) : block,
            Cell(provenance, ModPagePane.CellFactValue).GetProperty("text").GetString());
        Assert.Equal(block, provenance.GetProperty(ModPagePane.RowFact).GetProperty("description").GetString());
        // Exactly one row says it, and the sidebar's own subtitle is not one of the page's rows.
        Assert.Single(labels, label => string.Equals(label, managedKey, StringComparison.Ordinal));
        string subtitle;
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            subtitle = Entry(doc.RootElement, "other.tool.mod").GetProperty("subtitle").GetString() ?? "";
        Assert.NotEqual("", subtitle);
        Assert.DoesNotContain(subtitle, labels);

        // A Progenitor-installed mod has no read-only sentence, and with no word from the server's
        // handshake request no requirement either (live HotDemo, 2026-10-03): no provenance row at all.
        SeedCryptSnooze();
        ModsMenu.OpenMod("example.cryptsnooze");
        var coffinRows = PageRows();
        Assert.DoesNotContain(ModPagePane.FactKeyPrefix + "managed", RowLabels(coffinRows));
        Assert.DoesNotContain(ModMenuInventory.RequirementUnknown, JsonSerializer.Serialize(coffinRows), StringComparison.Ordinal);
        Assert.DoesNotContain(ModMenuInventory.OtherToolNote, JsonSerializer.Serialize(coffinRows), StringComparison.Ordinal);

        // The joiner itself: empty in, empty out; a sentence that already ends in a stop keeps it.
        Assert.Equal("", ModPageCopy.ProvenanceBlock(null, "", "   "));
        Assert.Equal("One. Two.", ModPageCopy.ProvenanceBlock("One.", "Two"));
    }

    /// <summary>
    /// MODSPOLISH1, the <c>mods_ui rects</c> gap: <c>rects</c> finds a row by matching a TMP caption
    /// against the readback <c>label</c>, which a cell row's key is not, so it cannot measure the
    /// head block. This asserts the route that does work — every head-block row and every visible
    /// cell on it carries a <c>rect</c> in <c>widget_read</c> — so the geometry is readable without
    /// the op change (which belongs to <c>Dev/Ops/ModsUiOps.cs</c>, another job's file).
    /// What this would catch: a head-block row described without its rect, which would leave the
    /// page's geometry unreadable from either op.
    /// </summary>
    [Fact]
    public void Every_head_block_row_and_cell_carries_a_rect_in_widget_read()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Verified("ExampleTeam", "HuntsAndGates", "1.0.0", "example.huntsandgates"));
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("ExampleTeam-HuntsAndGates", "example.huntsandgates.dll", "1.0.0", ServerInstallLedger.CategoryMod),
        };
        ModsMenu.Open();
        using var hunts = ModsMenu.Register(new object(), "example.huntsandgates", "HuntsAndGates");
        ModsMenu.OpenMod("example.huntsandgates");

        var rows = PageRows();
        // SETTINGS1: the framework line, the framework's server-half warning under it, then the mod
        // area's title, meta and by-line rows.
        var seen = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("label", out var label)) continue;
            string key = label.GetString() ?? "";
            if (!key.StartsWith(ModPagePane.HeadKey + "-", StringComparison.Ordinal)
                && !string.Equals(key, ModPagePane.FrameworkKey, StringComparison.Ordinal)) continue;
            seen.Add(key);
            Assert.Equal("cellrow", row.GetProperty("kind").GetString());
            var rect = row.GetProperty("rect");
            Assert.NotEqual(JsonValueKind.Null, rect.ValueKind);
            Assert.True(rect.GetProperty("w").GetDouble() > 0, key + " has no width");
            Assert.True(rect.GetProperty("h").GetDouble() > 0, key + " has no height");
            foreach (var cell in row.GetProperty("cells").EnumerateArray())
            {
                if (!cell.GetProperty("visible").GetBoolean()) continue;
                Assert.NotEqual(JsonValueKind.Null, cell.GetProperty("rect").ValueKind);
            }
        }

        // MODPAGE2: the title and by-line, the actions bar, the banner under it, then the Details heading and its rows,
        // and About last.
        Assert.Equal(
            new[]
            {
                ModPagePane.HeadKey + "-title", ModPagePane.HeadKey + "-byline",
                ModPagePane.FrameworkKey, ModPagePane.HeadKey + "-warning",
                ModPagePane.HeadKey + "-section-details",
            },
            seen.Take(5).ToArray());
        Assert.Contains(ModPagePane.HeadKey + "-section-about", seen);
        Assert.True(seen.IndexOf(ModPagePane.HeadKey + "-section-about") > seen.IndexOf(ModPagePane.HeadKey + "-section-details"));
    }

    /// <summary>Every described row's <c>label</c>, in draw order.</summary>
    private static string[] RowLabels(JsonElement rows)
    {
        var labels = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (row.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                labels.Add(label.GetString() ?? "");
        }

        return labels.ToArray();
    }

    // ---- fixtures ---------------------------------------------------------------------------

    /// <summary>
    /// LINK1's live case: BasinHoverTips, installed by another tool under the GUID
    /// <c>acme.PhialStore</c>, with no ledger row and no identity record — only the listing.
    /// </summary>
    private void SeedBasinHoverTips()
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Listing(
            "ExampleTeam", "BasinHoverTips", "1.0.19", "Client",
            description: "Shows the blood pool of an item you hover.",
            released: "2026-08-30T09:00:00Z",
            rating: 12));
        catalog.Readme = "# BasinHoverTips\n\nHover a blood item to see its pool.";
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
    }

    /// <summary>The live client's own case: CryptSnooze, installed by Progenitor, listed by the index.</summary>
    private void SeedCryptSnooze(string? readme = null)
    {
        var catalog = new PageCatalog();
        catalog.Entries.Add(Listing(
            "Corvane", "CryptSnooze", "2.0.4", "Server",
            description: "A VRising mod that speeds up time while you sleep in your coffin.",
            released: "2025-05-18T10:00:00Z"));
        catalog.Readme = readme;
        ModsMenu.SetInstallCatalogForTests(catalog);
        ModsMenu.LedgerForTests = new[]
        {
            Ledgered("Corvane-CryptSnooze", "example.cryptsnooze.dll", "2.0.4", ServerInstallLedger.CategoryMod),
        };
        Handshake.Provide("example.cryptsnooze", "2.0.4");
    }

    private static InstalledLedgerRow Ledgered(
        string package,
        string fileName,
        string version,
        string category,
        string root = InstalledState.ClientRoot) =>
        new(package, "sha", version, fileName, "package:" + package + "@" + version, category, root, "");

    private static CatalogEntry Listing(
        string owner,
        string name,
        string version,
        string category,
        string? second = null,
        string? description = null,
        string? released = null,
        int? rating = null)
    {
        var categories = second == null ? new[] { category } : new[] { category, second };
        return new CatalogEntry(
            new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, owner, owner + "-" + name),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-13T00:00:00Z",
            displayName: name,
            author: owner,
            description: description ?? "",
            listingUrl: "https://thunderstore.io/c/v-rising/p/" + owner + "/" + name + "/",
            owner: owner,
            packageName: name,
            categories: categories,
            releasedUtc: released,
            ratingScore: rating);
    }

    /// <summary>A package whose own <c>progenitor.json</c> declares it is needed on both sides.</summary>
    private static CatalogEntry Verified(string owner, string name, string version, string pluginId) =>
        new(
            new ManifestMod(pluginId, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false, owner, owner + "-" + name, "sha256"),
            CatalogProvenance.ManifestVerified,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-13T00:00:00Z",
            displayName: name,
            author: owner,
            description: "Client UI for servant hunts; the server component feeds hunt state.",
            listingUrl: "https://thunderstore.io/c/v-rising/p/" + owner + "/" + name + "/",
            side: PackageSide.Both,
            owner: owner,
            packageName: name,
            releasedUtc: "2026-09-06T00:00:00Z");

    /// <summary>
    /// INSTALLUX2 item 1: the page's y/n prompt is the kit's modal dialog - its own panel in the
    /// middle of the screen - and nothing about the confirmation is drawn in the page's rows. The
    /// user, on the INSTALLUX1 build: "it expands like the old behavior asking me if I want to
    /// stall like this instead of popping up a dialogue."
    /// What removal breaks: the confirmation goes back to an in-row expansion under the head - the
    /// exact UX the user rejected - and, because the page and the box would share one panel, a
    /// click anywhere behind the prompt would still land on the page it is supposed to be blocking.
    /// </summary>
    [Fact]
    public void The_page_prompt_opens_as_a_dialog_and_draws_nothing_in_the_page()
    {
        SeedCryptSnooze();
        ModsMenu.UninstallSeam = new FakeSeam();
        ModsMenu.Open();
        var page = ModsMenu.OpenMod("example.cryptsnooze");

        // Nothing is open before the action is clicked.
        Assert.Null(UiDialog.Current);
        Assert.False(TryRow(PageRows(), ModPagePane.RowPrompt, out _), "no prompt before the action");

        Click(ModPageCopy.Uninstall);

        var dialog = ModsMenu.ModPage!.PromptDialog;
        Assert.NotNull(dialog);
        Assert.True(dialog!.IsOpen);
        Assert.Same(dialog, UiDialog.Current);
        Assert.Equal("uninstall", dialog.Kind);
        Assert.Equal("example.cryptsnooze", dialog.Key);
        // The whole point: a separate panel, not the page's.
        Assert.NotSame(page, dialog.Panel);

        // The readback keys are the ones the in-pane block published, plus `presentation`.
        var prompt = PromptRows();
        foreach (var row in prompt.Values)
        {
            var described = row.GetProperty(ModPagePane.RowPrompt);
            Assert.Equal("uninstall", described.GetProperty("kind").GetString());
            Assert.True(described.GetProperty("open").GetBoolean());
            Assert.Equal(ModPagePane.PromptPresentation, described.GetProperty("presentation").GetString());
        }

        // And the handles a bridge client clicks are unchanged: the buttons row is still
        // `modPrompt-<kind>-buttons` with `yes` / `no` cells.
        Assert.Equal(ModPagePane.RowPrompt + "-uninstall-buttons", prompt["buttons"].GetProperty("label").GetString());
        Assert.Equal(UiPromptBlock.CellYes, ModsMenu.ModPage!.PromptBlock!.YesKey);
        Assert.Equal(UiPromptBlock.CellNo, ModsMenu.ModPage!.PromptBlock!.NoKey);

        // Answering closes the dialog; the page is left with no prompt rows at all.
        Click(ModPageCopy.UninstallNo);
        Assert.Null(UiDialog.Current);
        Assert.False(dialog.IsOpen);
        Assert.Equal(ModPromptKind.None, ModsMenu.ModPrompt);
        Assert.False(TryRow(PageRows(), ModPagePane.RowPrompt, out _), "No must close the dialog");
    }

    /// <summary>
    /// INSTALLUX2 item 1: Escape answers the open dialog with No - it does not just hide it.
    /// What removal breaks: Escape sweeps every shown panel, so the box would leave the screen with
    /// no decision taken and the page's prompt state still set; the next time the page was drawn the
    /// confirmation would reappear out of nowhere, and <c>mods_ui status</c> would report a dialog
    /// nobody can see (the FUI29 failure).
    /// </summary>
    [Fact]
    public void Escape_answers_the_page_dialog_with_No()
    {
        SeedCryptSnooze();
        var seam = new FakeSeam();
        ModsMenu.UninstallSeam = seam;
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");
        Click(ModPageCopy.Uninstall);
        Assert.Equal(ModPromptKind.Uninstall, ModsMenu.ModPrompt);

        UiApi.PressKeyForTests(27, down: true);

        Assert.Null(UiDialog.Current);
        // No, not Yes: nothing was removed and nothing was marked.
        Assert.Equal(0, seam.MarkCalls);
        Assert.Equal(ModPromptKind.None, ModsMenu.ModPrompt);
    }

    /// <summary>
    /// INSTALLUX2 item 3: <c>mods_ui status</c> reports <c>dialog {open, key, kind}</c>, so a bridge
    /// client can tell a modal confirmation is holding the workspace before it clicks anything else.
    /// What removal breaks: the only evidence a dialog is open is a row in <c>widget_read</c>, so an
    /// automated run that reads status alone would drive clicks into a blocked page and see them
    /// silently do nothing.
    /// </summary>
    [Fact]
    public void Describe_workspace_reports_the_open_dialog()
    {
        SeedCryptSnooze();
        ModsMenu.UninstallSeam = new FakeSeam();
        ModsMenu.Open();
        ModsMenu.OpenMod("example.cryptsnooze");

        using (var closed = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            var dialog = closed.RootElement.GetProperty("dialog");
            Assert.False(dialog.GetProperty("open").GetBoolean());
            Assert.Equal("", dialog.GetProperty("key").GetString());
            Assert.Equal("none", dialog.GetProperty("kind").GetString());
        }

        Click(ModPageCopy.Uninstall);

        using (var open = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
        {
            var dialog = open.RootElement.GetProperty("dialog");
            Assert.True(dialog.GetProperty("open").GetBoolean());
            Assert.Equal("example.cryptsnooze", dialog.GetProperty("key").GetString());
            Assert.Equal("uninstall", dialog.GetProperty("kind").GetString());
        }
    }

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Click(string label)
    {
        var result = ModsMenu.ClickVisibleWidget("content", label, null, null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(
            doc.RootElement.GetProperty("invoked").GetInt32() >= 1,
            "no visible content widget is captioned " + label);
    }

    private JsonDocument? _rows;

    /// <summary>The content pane's rows, as the <c>mods_ui widget_read</c> reader sees them.</summary>
    private JsonElement PageRows()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static bool TryRow(JsonElement rows, string key, out JsonElement row)
    {
        foreach (var candidate in rows.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            if (!candidate.TryGetProperty(key, out _)) continue;
            row = candidate;
            return true;
        }

        row = default;
        return false;
    }

    private static JsonElement Row(JsonElement rows, string key) =>
        TryRow(rows, key, out var row) ? row : throw new Xunit.Sdk.XunitException("no page row carries " + key);

    /// <summary>
    /// The prompt rows by their <c>modPrompt.row</c> name. KIT1: three, not four — the kit's
    /// <see cref="UiPromptBlock"/> puts Yes and No on one right-aligned cell row, which is the mock's
    /// <c>justify-content:flex-end</c> pair rather than two stacked full-width buttons.
    /// </summary>
    private Dictionary<string, JsonElement> PromptRows()
    {
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in PageRows().EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty(ModPagePane.RowPrompt, out var prompt)) continue;
            found[prompt.GetProperty("row").GetString() ?? ""] = row;
        }

        Assert.Equal(new[] { "body", "buttons", "heading" }, found.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        // The pair is right-aligned on one row, Yes at the right edge.
        var buttons = found["buttons"];
        Assert.Equal("cellrow", buttons.GetProperty("kind").GetString());
        var yes = Cell(buttons, UiPromptBlock.CellYes);
        var no = Cell(buttons, UiPromptBlock.CellNo);
        Assert.Equal("button", yes.GetProperty("kind").GetString());
        Assert.Equal("button", no.GetProperty("kind").GetString());
        Assert.True(
            no.GetProperty("rect").GetProperty("x").GetDouble() < yes.GetProperty("rect").GetProperty("x").GetDouble(),
            "No must sit left of Yes");
        return found;
    }

    /// <summary>One cell of a described cell row, by its cell key.</summary>
    /// <summary>The described row whose <c>label</c> (a cell row's key) is <paramref name="label"/>.</summary>
    private static JsonElement RowLabelled(JsonElement rows, string label)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (row.TryGetProperty("label", out var l) && string.Equals(l.GetString(), label, StringComparison.Ordinal)) return row;
        }

        throw new Xunit.Sdk.XunitException("no row labeled " + label);
    }

    private static JsonElement Cell(JsonElement row, string cellKey)
    {
        foreach (var cell in row.GetProperty("cells").EnumerateArray())
        {
            if (string.Equals(cell.GetProperty("key").GetString(), cellKey, StringComparison.Ordinal))
                return cell;
        }

        throw new Xunit.Sdk.XunitException("the row carries no cell '" + cellKey + "'");
    }

    private static JsonElement Entry(JsonElement workspace, string id)
    {
        foreach (var entry in workspace.GetProperty("entries").EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        throw new Xunit.Sdk.XunitException("no sidebar entry for " + id);
    }

    private static string[] Strings(JsonElement array)
    {
        var list = new List<string>();
        foreach (var item in array.EnumerateArray()) list.Add(item.GetString() ?? "");
        return list.ToArray();
    }

    /// <summary>Readback is JSON, which escapes the copy's middle dots and arrows.</summary>
    private static string Escaped(string text) => JsonSerializer.Serialize(text).Trim('"');

    private sealed class FakeSeam : IModUninstallSeam
    {
        public bool Pending { get; set; }
        public int MarkCalls { get; private set; }
        public int CancelUninstallCalls { get; private set; }
        public int CancelInstallCalls { get; private set; }
        public bool LastHot { get; private set; }
        public string LastPlugin { get; private set; } = "";

        public bool IsUninstallPending(string pluginId) => Pending;

        public ServerInstallResult MarkUninstall(string pluginId, bool hot)
        {
            MarkCalls++;
            LastHot = hot;
            LastPlugin = pluginId;
            Pending = true;
            return new ServerInstallResult(true, "marked", new[] { "marker" }, Array.Empty<string>(), false);
        }

        public ServerInstallResult CancelUninstall(string pluginId)
        {
            CancelUninstallCalls++;
            LastPlugin = pluginId;
            Pending = false;
            return new ServerInstallResult(true, "cancelled", new[] { "marker" }, Array.Empty<string>(), false);
        }

        public ServerInstallResult CancelPendingInstall(string pluginId)
        {
            CancelInstallCalls++;
            LastPlugin = pluginId;
            return new ServerInstallResult(true, "cancelled", new[] { "file" }, Array.Empty<string>(), false);
        }
    }

    /// <summary>A catalog that answers from a fixed list and one shared README.</summary>
    private sealed class PageCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

        public string? Readme { get; set; }

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries;

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                entry = Entries[i];
                return true;
            }

            entry = null;
            return false;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) => new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(false, InstallReasons.ApprovalMissing, null, false);

        public ExecuteCallResult ExecuteToPending(string planHash) => new(false, InstallReasons.ApprovalMissing, null, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Documents(key));

        public bool TryGetCachedListingDocuments(string key, out ListingDocumentSet? documents)
        {
            if (Readme == null)
            {
                documents = null;
                return false;
            }

            documents = Documents(key);
            return true;
        }

        private ListingDocumentSet Documents(string key) =>
            new(key ?? "", "1.0.0", Readme != null, null, Readme ?? "", false, "none", "");
    }
}
