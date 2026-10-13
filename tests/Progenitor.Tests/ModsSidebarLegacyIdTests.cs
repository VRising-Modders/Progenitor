using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// MODSDUP1 (live 2026-10-07: the Mods tab listed "Progenitor Hot Demo 0.1.0" and a second "HotDemo"): the local test
/// server's ledger still held the install from before the id rename (package <c>Vendor-HotDemo</c>, plugin id and file
/// <c>&lt;vendor&gt;.progenitor.hotdemo</c>), which named no listed row and became its own ledger-only row.
/// </summary>
public sealed class ModsSidebarLegacyIdTests
{
    private const string Legacy = "examplevendor.progenitor.hotdemo";

    private static InstalledLedgerRow LegacyServerRow() => new(
        "ExampleVendor-HotDemo", "sha", "0.1.0", Legacy + ".dll", "package:ExampleVendor-HotDemo@0.1.0", "mod",
        InstalledState.ServerRoot, "BepInEx/progenitor-reload/" + Legacy + ".dll", Legacy);

    [Fact]
    public void A_pre_rename_ledger_row_folds_into_the_loaded_plugins_row()
    {
        var entries = new List<KeyValuePair<string, string>> { new("progenitor.hotdemo", "Progenitor Hot Demo") };
        var reload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["progenitor.hotdemo"] = "Progenitor.HotDemo.dll" };
        var model = ModMenuInventory.BuildSidebar(entries, Array.Empty<ModMenuInventoryRow>(), new[] { LegacyServerRow() }, "6.0.0", null, reload);

        var mine = model.Rows.Where(r => r.Group == ModMenuInventory.GroupMods).ToList();
        var row = Assert.Single(mine);
        Assert.Equal("progenitor.hotdemo", row.Id);
        Assert.Equal("Progenitor Hot Demo", row.Name);
        Assert.Single(row.Ledger);
        Assert.Equal(1, model.Shown);
        Assert.Equal(1, ModMenuInventory.LedgerRowsForPlugin(new[] { LegacyServerRow() }, "progenitor.hotdemo").Count);
    }

    [Fact]
    public void A_pre_rename_row_and_a_current_row_with_nothing_loaded_are_one_row()
    {
        var current = new InstalledLedgerRow("Progenitor-HotDemo", "sha", "0.1.0", "progenitor.hotdemo.dll",
            "package:Progenitor-HotDemo@0.1.0", "mod", InstalledState.ClientRoot, "", "progenitor.hotdemo");
        var rows = ModMenuInventory.LedgerOnlyRows(new[] { LegacyServerRow(), current });
        var only = Assert.Single(rows);
        Assert.Equal("progenitor.hotdemo", only.Id);
        Assert.Equal(2, only.Ledger.Count);
        // Other plugins are not folded: a different name stays its own row.
        Assert.Empty(ModMenuInventory.LedgerRowsForPlugin(new[] { LegacyServerRow() }, "progenitor.hotdemo2"));
    }
}
