using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Items;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// PrSt version 2: presentation hints, show/enable rules and item lists — encoding, evaluation, validation, size
/// limits, old/new compatibility, server derivation from the page and the client's drawing.
/// </summary>
[Collection("PluginLogger")]
public sealed class ScopedSettingsPresentationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-presentation-" + Guid.NewGuid().ToString("N"));
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private ModSettingsView? _view;

    public ScopedSettingsPresentationTests()
    {
        Directory.CreateDirectory(_dir);
        UiApi.ResetForTests(); UiApi.SetRuntimeForTests(_runtime);
        UiInfoHandle.Scheduler = null;
        ScopedSettings.SetForUiTests(false, false);
        ModSettingsPages.ResetForTests();
        ItemListSettings.ResetForTests();
    }

    public void Dispose()
    {
        _view?.Release();
        UiApi.ResetForTests();
        ScopedSettings.SetForUiTests(false, false);
        ModSettingsPages.ResetForTests();
        ItemListSettings.ResetForTests();
        ModSettingsView.ItemCatalog = () => Array.Empty<ItemCatalogEntry>();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly Guid Gen = Guid.Parse("7b0e3f7e-3c35-4a5e-9a1d-1f1f2f3f4f5f");

    private static ScopedSettingRecord Record(string key, string type, string value, bool personal = false, params string[] choices) => new()
    {
        Id = "auto/Server/" + key, PluginId = "auto", ModName = "Autoloot", Version = "1.0",
        Section = "Server", Key = key, Help = "Help for " + key + ".", Type = type,
        Default = value, Value = value, Personal = personal, Editable = true, Choices = choices, MaxLength = 1024,
    };

    private static ScopedSettingPresentation Hints() => new()
    {
        Widget = ScopedWidget.Radio, Label = "What is picked up", SectionTitle = "Players without the client part",
        SectionOrder = 2, RowOrder = 7, Step = "0.5", Units = "m",
        ChoiceLabels = new[] { "Any drop that comes in range", "Only drops that land in range" },
        ShowIf = SettingCondition.Is("Server", "Enabled", true) & SettingCondition.ServerHasMod("satis").Resolved(_ => true),
        EnableIf = SettingCondition.AtLeast("Server", "Radius", 5),
    };

    private static ScopedSettingsPacket RowPacket(ScopedSettingRecord row) => new() { Kind = 2, Generation = Gen, Row = row };

    private static ScopedSettingRecord RoundTrip(ScopedSettingRecord row, byte version)
    {
        var bytes = ScopedSettingsWire.Encode(RowPacket(row), version);
        Assert.True(ScopedSettingsWire.TryDecode(bytes, true, out var decoded));
        Assert.Equal(version, decoded.WireVersion);
        return decoded.Row!;
    }

    // ---- encode/decode ----------------------------------------------------------------------------------------

    [Fact]
    public void Version_2_rows_round_trip_every_hint()
    {
        var row = Record("Pickup", "System.String", "ComesInRange", false, "ComesInRange", "LandsInRange");
        row.Presentation = Hints();
        var back = RoundTrip(row, ScopedSettingsWire.PresentationVersion).Presentation!;
        Assert.Equal(ScopedWidget.Radio, back.Widget);
        Assert.Equal("What is picked up", back.Label);
        Assert.Equal("Players without the client part", back.SectionTitle);
        Assert.Equal(2, back.SectionOrder);
        Assert.Equal(7, back.RowOrder);
        Assert.Equal("0.5", back.Step);
        Assert.Equal("m", back.Units);
        Assert.Equal(row.Presentation.ChoiceLabels, back.ChoiceLabels);
        Assert.Equal("(Server/Enabled == true && server has satis)", back.ShowIf!.ToString());
        Assert.Equal("Server/Radius >= 5", back.EnableIf!.ToString());
        Assert.True(back.ShowIf.Parts[1].Answer);
    }

    [Fact]
    public void Item_list_hints_round_trip()
    {
        var row = Record("Filter", "System.String", "");
        row.Presentation = new ScopedSettingPresentation
        {
            Widget = ScopedWidget.ItemList, AllowCategories = true, MaxEntries = 100, MaxCount = 999, CountLabel = "Most in bags",
            CategoryIds = new[] { "gems", "wood_planks" }, CategoryLabels = new[] { "Gems", "Wood planks" },
        };
        var back = RoundTrip(row, 2).Presentation!;
        Assert.Equal(ScopedWidget.ItemList, back.Widget);
        Assert.True(back.AllowCategories);
        Assert.Equal(100, back.MaxEntries);
        Assert.Equal(999, back.MaxCount);
        Assert.Equal("Most in bags", back.CountLabel);
        Assert.Equal(new[] { "gems", "wood_planks" }, back.CategoryIds);
        Assert.Equal(new[] { "Gems", "Wood planks" }, back.CategoryLabels);
    }

    [Fact]
    public void A_version_2_row_without_hints_decodes_with_none()
    {
        var back = RoundTrip(Record("Enabled", "System.Boolean", "true"), 2);
        Assert.Null(back.Presentation);
    }

    [Fact]
    public void Conditions_round_trip_and_reject_garbage()
    {
        var c = SettingCondition.Any(SettingCondition.IsNot("Server", "Share", "Even"),
            SettingCondition.All(SettingCondition.Below("Server", "Radius", 10.5), SettingCondition.Above("Server", "Radius", -1)));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) c.Write(w);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) Assert.Equal(c.ToString(), SettingCondition.Read(r).ToString());

        foreach (var bad in new[] { new byte[] { 0 }, new byte[] { 99 }, new byte[] { 7, 0 }, new byte[] { 9, 0, 0, 3 }, new byte[] { 1, 0, 0 } })
        {
            using var r = new BinaryReader(new MemoryStream(bad));
            var e = Assert.ThrowsAny<Exception>(() => SettingCondition.Read(r));
            Assert.True(e is IOException or InvalidDataException, e.GetType().Name);
        }
    }

    // ---- evaluation -------------------------------------------------------------------------------------------

    private static Func<SettingCondition, string?> Values(params (string Id, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Id, v => v.Value);
        return c => map.TryGetValue(c.Section + "/" + c.Key, out var v) ? v : null;
    }

    [Fact]
    public void Comparisons_read_numbers_booleans_and_text()
    {
        var read = Values(("S/On", "True"), ("S/Mode", "Popup"), ("S/Minutes", "15"), ("S/Scale", "0.5"));
        Assert.True(SettingCondition.Is("S", "On", true).Evaluate(read));
        Assert.False(SettingCondition.IsNot("S", "On", true).Evaluate(read));
        Assert.True(SettingCondition.Is("S", "Mode", "popup").Evaluate(read));
        Assert.True(SettingCondition.IsNot("S", "Mode", "Chat").Evaluate(read));
        Assert.True(SettingCondition.AtLeast("S", "Minutes", 15).Evaluate(read));
        Assert.False(SettingCondition.Above("S", "Minutes", 15).Evaluate(read));
        Assert.True(SettingCondition.AtMost("S", "Minutes", 15).Evaluate(read));
        Assert.False(SettingCondition.Below("S", "Minutes", 15).Evaluate(read));
        Assert.True(SettingCondition.Is("S", "Minutes", 15.0).Evaluate(read));
        Assert.True(SettingCondition.Is("S", "Scale", 0.5f).Evaluate(read));
        Assert.False(SettingCondition.Above("S", "Mode", 1).Evaluate(read)); // ordering needs a number on both sides
    }

    [Fact]
    public void A_missing_value_is_false_and_groups_combine()
    {
        var read = Values(("S/On", "true"), ("S/Mode", "Chat"));
        Assert.False(SettingCondition.Is("S", "Missing", "x").Evaluate(read));
        Assert.False(SettingCondition.IsNot("S", "Missing", "x").Evaluate(read));
        Assert.False(SettingCondition.Is("S", "On", true).Evaluate(_ => throw new InvalidOperationException()));
        Assert.True((SettingCondition.Is("S", "On", true) & SettingCondition.Is("S", "Mode", "Chat")).Evaluate(read));
        Assert.False((SettingCondition.Is("S", "On", false) & SettingCondition.Is("S", "Mode", "Chat")).Evaluate(read));
        Assert.True((SettingCondition.Is("S", "On", false) | SettingCondition.Is("S", "Mode", "Chat")).Evaluate(read));
        Assert.Equal(new[] { ("S", "On"), ("S", "Mode") },
            SettingCondition.All(SettingCondition.Is("S", "On", true), SettingCondition.Is("S", "Mode", "x")).References().ToArray());
    }

    [Fact]
    public void Server_facts_use_the_answer_sent_else_the_local_one_else_false()
    {
        var fact = SettingCondition.ServerHasMod("satis");
        Assert.False(fact.Evaluate(_ => null));
        Assert.True(fact.Evaluate(_ => null, id => id == "satis"));
        Assert.False(fact.Resolved(_ => false).Evaluate(_ => null, _ => true));
        Assert.True(fact.Resolved(_ => true).Evaluate(_ => null));
        Assert.Equal(new[] { "satis" }, (fact | SettingCondition.Is("S", "On", true)).ServerMods().ToArray());
    }

    // ---- validation -------------------------------------------------------------------------------------------

    [Fact]
    public void Conditions_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => SettingCondition.Is("S/x", "K", 1));
        Assert.Throws<ArgumentException>(() => SettingCondition.Is("", "K", 1));
        Assert.Throws<ArgumentException>(() => SettingCondition.AtLeast("S", "K", double.NaN));
        Assert.Throws<ArgumentException>(() => SettingCondition.Is("S", "K", new string('x', SettingCondition.MaxConstantBytes + 1)));
        Assert.Throws<ArgumentException>(() => SettingCondition.All());
        var sixteen = Enumerable.Range(0, 15).Select(i => SettingCondition.Is("S", "K" + i, i)).ToArray();
        Assert.NotNull(SettingCondition.Any(sixteen)); // 15 parts and the group: 16
        Assert.Throws<ArgumentException>(() => SettingCondition.Any(sixteen.Append(SettingCondition.Is("S", "K", 1)).ToArray()));
        // Alternating groups nest; same-kind groups flatten (chained & and repeated ShowWhen stay one level).
        var deep = SettingCondition.Is("S", "K", 1);
        for (int i = 0; i < 3; i++)
            deep = i % 2 == 0 ? SettingCondition.Any(deep, SettingCondition.Is("S", "A" + i, 1)) : SettingCondition.All(deep, SettingCondition.Is("S", "A" + i, 1));
        Assert.Throws<ArgumentException>(() => SettingCondition.All(deep, SettingCondition.Is("S", "Z", 1)));
        var chained = SettingCondition.Is("S", "K0", 0);
        for (int i = 1; i < 15; i++) chained = chained & SettingCondition.Is("S", "K" + i, i);
        Assert.Equal(15, chained.Parts.Count);
        Assert.All(chained.Parts, p => Assert.Equal(SettingCondition.Op.Equal, p.Kind));
    }

    [Fact]
    public void Pruning_keeps_the_rest_of_a_rule()
    {
        var local = SettingCondition.IsNot("Me", "Local", "x");
        var published = SettingCondition.Is("Server", "Enabled", true);
        var fact = SettingCondition.ServerHasMod("satis");
        bool Published(SettingCondition c) => c.Section == "Server";
        Assert.Same(fact, SettingCondition.Any(local, fact).Prune(Published));
        Assert.Equal("(Server/Enabled == true || server has satis)", SettingCondition.Any(local, published, fact).Prune(Published)!.ToString());
        Assert.Null(local.Prune(Published));
        Assert.Null(SettingCondition.All(local, SettingCondition.IsNot("Me", "Other", 1)).Prune(Published));
        var whole = SettingCondition.All(published, fact);
        Assert.Same(whole, whole.Prune(Published));
    }

    [Fact]
    public void A_server_fact_falls_back_to_the_servers_mod_list()
    {
        try
        {
            Assert.Null(ScopedSettingsRows.ServerFact("auto", "satis"));
            ScopedSettingsRows.ServerMods = guid => guid == "satis" ? true : guid == "unknown" ? null : false;
            Assert.True(ScopedSettingsRows.ServerFact("auto", "satis"));
            Assert.False(ScopedSettingsRows.ServerFact("auto", "other"));
            Assert.Null(ScopedSettingsRows.ServerFact("auto", "unknown"));
            // A client-only row's rule reads it through the page's local evaluation.
            var (enabled, _, _, _) = Autoloot();
            var registration = ModSettingsPages.Register(this, "auto", page => page.Section("Me")
                .Toggle(enabled).ShowWhen(SettingCondition.ServerHasMod("satis"))
                .Toggle(enabled).ShowWhen(SettingCondition.Any(SettingCondition.IsNot(enabled, true), SettingCondition.ServerHasMod("other"))));
            var items = registration.SectionSpecs[0].Items;
            Assert.True(items[0].Options.ShowIf!());
            Assert.False(items[1].Options.ShowIf!());
            enabled.Value = false;
            Assert.True(items[1].Options.ShowIf!());
            // An answer the server sent with a rule wins over the list.
            var row = Record("Hint", "System.Boolean", "true");
            row.Presentation = new ScopedSettingPresentation { ShowIf = SettingCondition.ServerHasMod("satis").Resolved(_ => false) };
            ScopedSettings.SetForUiTests(true, false, row);
            Assert.False(ScopedSettingsRows.ServerFact("auto", "satis"));
        }
        finally { ScopedSettingsRows.ServerMods = null; }
    }

    [Fact]
    public void Hints_that_overflow_drop_optional_parts_first()
    {
        var choices = Enumerable.Range(0, 100).Select(i => "c" + i).ToArray();
        var row = Record("Filter", "System.String", "", false, choices);
        row.Presentation = new ScopedSettingPresentation
        {
            Widget = ScopedWidget.ItemList, Label = "Pickup filter", SectionTitle = "Me", SectionOrder = 1, RowOrder = 4,
            AllowCategories = true, MaxEntries = 10, MaxCount = 50,
            CategoryIds = Enumerable.Range(0, 64).Select(i => "cat" + i).ToArray(),
            CategoryLabels = Enumerable.Range(0, 64).Select(_ => new string('n', 64)).ToArray(),
            ChoiceLabels = choices.Select(_ => new string('l', 60)).ToArray(),
            ShowIf = SettingCondition.Is("Server", "Enabled", true),
        };
        var packet = RowPacket(row);
        var bytes = ScopedSettingsWire.EncodeRowFitted(packet, 2, out int reduced);
        Assert.InRange(reduced, 1, ScopedSettingPresentation.MaxReduction);
        Assert.True(ScopedSettingsWire.TryDecode(bytes, true, out var decoded));
        var p = decoded.Row!.Presentation!;
        Assert.Equal((ScopedWidget.ItemList, "Pickup filter", "Me", 1, 4, 10, 50), (p.Widget, p.Label, p.SectionTitle, p.SectionOrder, p.RowOrder, p.MaxEntries, p.MaxCount));
        // Step by step: names, then categories, then choice labels, then rules.
        var full = new ScopedSettingPresentation { CategoryIds = new[] { "a" }, CategoryLabels = new[] { "A" }, ChoiceLabels = new[] { "x" }, ShowIf = SettingCondition.Is("S", "K", 1) };
        Assert.Equal("a", full.Reduced(1).CategoryLabels[0]);
        Assert.Empty(full.Reduced(2).CategoryIds);
        Assert.Single(full.Reduced(2).ChoiceLabels);
        Assert.Empty(full.Reduced(3).ChoiceLabels);
        Assert.NotNull(full.Reduced(3).ShowIf);
        Assert.Null(full.Reduced(4).ShowIf);
        // A small row is sent whole.
        var small = Record("Radius", "System.Int32", "1");
        small.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Slider };
        ScopedSettingsWire.EncodeRowFitted(RowPacket(small), 2, out reduced);
        Assert.Equal(0, reduced);
    }

    [Fact]
    public void The_registration_check_uses_the_largest_row_a_list_can_send()
    {
        var schema = Record("Filter", "System.String", "");
        var worst = ScopedSettingPresentation.WorstCaseListRow(schema);
        Assert.Equal(ItemLimitList.MaxEncodedBytes, worst.Value.Length);
        Assert.Equal(ScopedSettingsWire.MaxFieldBytes, worst.Help.Length);
        Assert.True(ScopedSettingsWire.Encode(RowPacket(worst), 2).Length <= ScopedSettingsWire.MaxPacketBytes);
        schema.Default = new string('1', 4096);
        schema.ModName = new string('m', 4096);
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(ScopedSettingPresentation.WorstCaseListRow(schema)), 2));
    }

    [Fact]
    public void A_long_list_is_answered_on_the_admin_channel_without_its_value()
    {
        string big = new string('9', 3000);
        var ok = ServerSettings.FitAnswer(new ServerSettingResult(true, big, ""));
        Assert.True(ok.Success);
        Assert.Equal("", ok.Value);
        var refused = ServerSettings.FitAnswer(new ServerSettingResult(false, big, "Counts are 0 to 10."));
        Assert.Equal(("", "Counts are 0 to 10."), (refused.Value, refused.Error));
        var small = new ServerSettingResult(true, "5=1", "");
        Assert.Equal(small.Value, ServerSettings.FitAnswer(small).Value);
        var bytes = ServerSettingsWire.Encode(new ServerSettingsMessage(Guid.NewGuid(), "auto/Server/Filter", "System.String", "", "", ok.Success, ok.Error), true);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Invalid_hints_are_refused_on_encode_and_decode()
    {
        var row = Record("Pickup", "System.String", "A", false, "A", "B");
        row.Presentation = new ScopedSettingPresentation { ChoiceLabels = new[] { "only one" } };
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(row), 2));
        row.Presentation = new ScopedSettingPresentation { Step = "-1" };
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(row), 2));
        row.Presentation = new ScopedSettingPresentation { CategoryIds = new[] { "bad id" }, CategoryLabels = new[] { "x" } };
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(row), 2));
        row.Presentation = new ScopedSettingPresentation { Label = new string('x', ScopedSettingPresentation.MaxLabelBytes + 1) };
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(row), 2));

        // A peer's row whose widget byte is unknown is dropped whole.
        row.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Toggle };
        var bytes = ScopedSettingsWire.Encode(RowPacket(row), 2);
        var tampered = (byte[])bytes.Clone();
        int widget = FindWidget(bytes);
        tampered[widget] = 200;
        Assert.False(ScopedSettingsWire.TryDecode(tampered, true, out _));
    }

    private static int FindWidget(byte[] bytes)
    {
        // The presentation block starts right after the version 1 row; encode the same row at version 1 to find it.
        Assert.True(ScopedSettingsWire.TryDecode(bytes, true, out var p));
        p.Row!.Presentation = null;
        return ScopedSettingsWire.Encode(RowPacket(p.Row), 1).Length + 1;
    }

    [Fact]
    public void Item_lists_parse_format_and_validate()
    {
        Assert.True(ItemLimitList.TryParse(" -1531666018=50 ; c:Gems=0 ", out var list, out _));
        Assert.Equal("-1531666018=50;c:Gems=0", list.ToString());
        Assert.Equal(50, list.MaxForItem(-1531666018));
        Assert.Equal(0, list.MaxForCategory("gems"));
        Assert.Null(list.MaxForItem(5));
        Assert.True(ItemLimitList.TryParse("", out var empty, out _));
        Assert.Empty(empty);

        var itemsOnly = new ItemListRules { MaxCount = 100 };
        Assert.False(ItemLimitList.TryParse("c:gems=1", itemsOnly, out _, out var why));
        Assert.Equal("This list takes items only.", why);
        Assert.False(ItemLimitList.TryParse("5=101", itemsOnly, out _, out why));
        Assert.StartsWith("Counts are 0 to 100", why);
        Assert.False(ItemLimitList.TryParse("5=1;5=2", out _, out why));
        Assert.Contains("listed twice", why);
        Assert.False(ItemLimitList.TryParse("c:Gems=1;c:gems=2", out _, out _));
        foreach (var bad in new[] { "5", "=3", "0=1", "x=1", "5=-1", "5=1;;", "c:bad id=1", "c:=1", "5=1.5" })
            Assert.False(ItemLimitList.TryParse(bad, out _, out _), bad);
        Assert.False(ItemLimitList.TryParse("1=1;2=2;3=3", new ItemListRules { MaxEntries = 2 }, out _, out why));
        Assert.Equal("At most 2 lines.", why);
        Assert.Throws<ArgumentException>(() => ItemLimitList.Of(new[] { ItemLimit.ForItem(1, 1), ItemLimit.ForItem(1, 2) }));
        Assert.Equal("1=1;2=3", ItemLimitList.Of(new[] { ItemLimit.ForItem(1, 1) }).With(ItemLimit.ForItem(2, 3)).ToString());
        Assert.Equal("2=3", ItemLimitList.Of(new[] { ItemLimit.ForItem(1, 1), ItemLimit.ForItem(2, 3) }).Without("i:1").ToString());
    }

    [Fact]
    public void The_server_checks_and_canonicalizes_every_item_list_write()
    {
        var rules = new ItemListRules { AllowCategories = true, MaxCount = 1000 };
        Assert.True(ItemListSettings.TryCanonical(rules, " 7=1 ;c:ore=5", out var canonical, out _));
        Assert.Equal("7=1;c:ore=5", canonical);
        Assert.False(ItemListSettings.TryCanonical(rules, "7=1001", out _, out _));
        using var lease = ItemListSettings.Register("auto/Server/Filter", rules);
        Assert.Throws<ArgumentException>(() => ItemListSettings.Register("auto/Server/Filter", rules));
        Assert.True(ItemListSettings.Has("auto/Server/Filter"));
        lease.Dispose();
        Assert.False(ItemListSettings.Has("auto/Server/Filter"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemListSettings.Register("x/y/z", new ItemListRules { MaxEntries = 257 }));
    }

    [Fact]
    public void Server_wide_item_list_edits_go_through_the_list_rules()
    {
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        try
        {
            var file = new ConfigFile(Path.Combine(_dir, "auto.cfg"), saveOnInit: true);
            var entry = file.Bind("Server", "Filter", "", "");
            ConfigSync.Register("auto", entry);
            using var lease = ItemListSettings.Register("auto/Server/Filter", new ItemListRules { MaxCount = 1000 });
            // A stored full list (over the admin channel's 2,048 bytes) does not stop the registration.
            entry.Value = string.Join(";", Enumerable.Range(0, 256).Select(i => (-2_000_000_000 - i) + "=999"));
            Assert.True(Encoding.UTF8.GetByteCount(entry.Value) > 2048);
            ServerSettings.Register("auto", entry).Dispose();
            entry.Value = "";
            lease.Dispose();
            using var rules = ItemListSettings.Register("auto/Server/Filter", new ItemListRules { MaxCount = 10 });
            using var edits = ServerSettings.Register("auto", entry);
            ServerSettingsMessage Write(string expected, string value) =>
                new(Guid.NewGuid(), "auto/Server/Filter", "System.String", expected, value, false, "");
            var refused = ServerSettings.Apply(Write("", "5=11"), true, true);
            Assert.False(refused.Success);
            Assert.StartsWith("Counts are 0 to 10", refused.Error);
            var ok = ServerSettings.Apply(Write("", " 5=3 ; 6=0"), true, true);
            Assert.True(ok.Success, ok.Error);
            Assert.Equal("5=3;6=0", entry.Value);
            // Longer than a text row's 256 characters, still one list: accepted.
            string many = string.Join(";", Enumerable.Range(1, 60).Select(i => (1_000_000 + i) + "=1"));
            Assert.True(ServerSettings.Apply(Write("5=3;6=0", many), true, true).Success);
        }
        finally
        {
            ServerSettings.Uninstall();
            ConfigHelper.UninstallAll();
            ConfigSync.ResetForTests();
        }
    }

    // ---- size limits ------------------------------------------------------------------------------------------

    [Fact]
    public void A_full_item_list_fits_the_field_and_a_longer_one_is_refused()
    {
        // 256 item lines with counts up to 999 always fit.
        var lines = Enumerable.Range(0, ItemLimitList.MaxEntries).Select(i => ItemLimit.ForItem(int.MinValue + 1 + i, 999)).ToList();
        var full = ItemLimitList.Of(lines);
        Assert.True(Encoding.UTF8.GetByteCount(full.ToString()) <= ItemLimitList.MaxEncodedBytes);
        Assert.Throws<ArgumentException>(() => ItemLimitList.Of(lines.Append(ItemLimit.ForItem(1, 1))));
        // Larger counts reach the byte limit first: refused, never split.
        var big = Enumerable.Range(0, ItemLimitList.MaxEntries).Select(i => ItemLimit.ForItem(int.MinValue + 1 + i, 1_000_000)).ToList();
        var error = Assert.Throws<ArgumentException>(() => ItemLimitList.Of(big));
        Assert.Contains("too long to share", error.Message);
        Assert.False(ItemLimitList.TryParse(string.Join(";", big), new ItemListRules { MaxCount = int.MaxValue }, out _, out _));
    }

    [Fact]
    public void A_worst_case_item_list_row_and_write_fit_one_packet()
    {
        var value = new string('9', ItemLimitList.MaxEncodedBytes);
        var row = Record("Filter", "System.String", value);
        row.Help = new string('h', 2048);
        row.Default = new string('1', 1024);
        row.MaxLength = ItemLimitList.MaxEncodedBytes;
        row.Presentation = new ScopedSettingPresentation
        {
            Widget = ScopedWidget.ItemList, Label = new string('l', ScopedSettingPresentation.MaxLabelBytes),
            SectionTitle = new string('s', ScopedSettingPresentation.MaxLabelBytes), AllowCategories = true, MaxEntries = 256, MaxCount = 1,
            CategoryIds = Enumerable.Range(0, ScopedSettingPresentation.MaxCategories).Select(i => "c" + i.ToString("D31")).ToArray(),
            CategoryLabels = Enumerable.Range(0, ScopedSettingPresentation.MaxCategories).Select(_ => new string('n', 48)).ToArray(),
        };
        var bytes = ScopedSettingsWire.Encode(RowPacket(row), 2);
        Assert.True(bytes.Length <= ScopedSettingsWire.MaxPacketBytes);
        // The client's write carries the shown list as the conflict token and the new one.
        var write = ScopedSettingsWire.Encode(new ScopedSettingsPacket
        {
            Kind = 5, Generation = Gen, RequestId = Guid.NewGuid(), Id = row.Id, Type = row.Type, Expected = value, Value = value,
        });
        Assert.True(write.Length <= ScopedSettingsWire.MaxPacketBytes);
    }

    [Fact]
    public void Oversize_hints_and_conditions_are_refused()
    {
        var row = Record("Filter", "System.String", "");
        row.Presentation = new ScopedSettingPresentation
        {
            CategoryIds = Enumerable.Range(0, 65).Select(i => "c" + i).ToArray(),
            CategoryLabels = Enumerable.Range(0, 65).Select(_ => "x").ToArray(),
        };
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(row), 2));
        var big = SettingCondition.Any(Enumerable.Range(0, 15)
            .Select(i => SettingCondition.Is(new string('s', 100), "K" + i, new string('v', 200))).ToArray());
        row.Presentation = new ScopedSettingPresentation { ShowIf = big };
        Assert.ThrowsAny<ArgumentException>(() => ScopedSettingsWire.Encode(RowPacket(row), 2));
    }

    // ---- old/new compatibility --------------------------------------------------------------------------------

    [Fact]
    public void Version_1_bytes_are_unchanged_by_hints_and_older_rows_decode_without_them()
    {
        var plain = Record("Radius", "System.Int32", "10");
        var withHints = Record("Radius", "System.Int32", "10");
        withHints.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Slider, Units = "m" };
        Assert.Equal(ScopedSettingsWire.Encode(RowPacket(plain)), ScopedSettingsWire.Encode(RowPacket(withHints), 1));
        Assert.Null(RoundTrip(withHints, 1).Presentation);

        // Every other kind is laid out as in version 1; only the version byte differs.
        foreach (byte kind in new byte[] { 1, 3, 4 })
        {
            var p = new ScopedSettingsPacket { Kind = kind, Generation = Gen, Count = kind == 4 ? 0 : 3, Id = kind == 4 ? "a/b/c" : "", Value = kind == 4 ? "x" : "" };
            var v1 = ScopedSettingsWire.Encode(p, 1);
            var v2 = ScopedSettingsWire.Encode(p, 2);
            Assert.Equal(v1.Skip(1), v2.Skip(1));
            Assert.Equal(2, v2[0]);
        }
    }

    [Fact]
    public void The_client_declares_version_2_and_keeps_hints_through_the_snapshot()
    {
        Assert.Equal(Handshake.Capability.ScopedSettingsPresentation,
            Handshake.SelfCapabilities & Handshake.Capability.ScopedSettingsPresentation);
        Assert.Equal(16u, (uint)Handshake.Capability.ScopedSettingsPresentation);

        var client = new ScopedSettingsClient(_ => true);
        var row = Record("Radius", "System.Int32", "10");
        row.Min = "1"; row.Max = "30";
        row.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Slider, Units = "m", Label = "Pickup range" };
        foreach (var packet in new[]
        {
            new ScopedSettingsPacket { Kind = 1, Generation = Gen, Count = 1, ServerName = "Test" },
            RowPacket(row),
            new ScopedSettingsPacket { Kind = 3, Generation = Gen, Count = 1 },
        })
        {
            Assert.True(ScopedSettingsWire.TryDecode(ScopedSettingsWire.Encode(packet, 2), true, out var decoded));
            client.Accept(decoded, 1);
        }
        var received = Assert.Single(client.Rows);
        Assert.Equal("Pickup range", received.Presentation!.Label);
        row.Presentation.Label = "changed after receipt";
        Assert.Equal("Pickup range", received.Presentation.Label);
    }

    [Fact]
    public void Rows_from_an_older_server_get_the_default_editors()
    {
        var radius = Record("Radius", "System.Int32", "10");
        radius.Min = "1"; radius.Max = "30";
        var spec = ScopedSettingsRows.Spec(RoundTrip(radius, 1));
        Assert.Equal(AdminSettingKind.Number, spec.Kind);
        Assert.Equal("Radius", spec.Label);
        var filter = Record("Filter", "System.String", "5=1");
        Assert.Equal(AdminSettingKind.Text, ScopedSettingsRows.Spec(RoundTrip(filter, 1)).Kind);
    }

    // ---- server derivation ------------------------------------------------------------------------------------

    private (ConfigEntry<bool> Enabled, ConfigEntry<int> Radius, ConfigEntry<string> Pickup, ConfigEntry<bool> Hint) Autoloot()
    {
        var file = new ConfigFile(Path.Combine(_dir, "auto-" + Guid.NewGuid().ToString("N") + ".cfg"), false);
        return (file.Bind("Server", "Enabled", true, "Players may use autoloot."),
            file.Bind("Server", "Radius", 10, "Metres."),
            file.Bind("Server", "Pickup", "ComesInRange", "Which drops."),
            file.Bind("Server", "NoClientHint", true, "Tell them."));
    }

    [Fact]
    public void Hints_come_from_the_page_row_that_places_the_setting()
    {
        var (enabled, radius, pickup, hint) = Autoloot();
        ModSettingsPages.Register(this, "auto", page => page
            .Section("Me")
                .Info("Note", () => "x")
            .Section("Server")
                .Toggle(enabled, label: "Autoloot for players", help: "Off: nothing is picked up for anyone.")
                .Slider(radius, min: 1, max: 30, step: 1, units: "m", label: "Pickup range")
                .Radio(pickup, new[] { "LandsInRange", "ComesInRange" }, new[] { "Only drops that land in range", "Any drop that comes in range" },
                    label: "What is picked up").EnableIf(enabled, true)
            .Section("Players without the client part")
                .Toggle(hint, label: "Tell them on joining").ShowWhen(SettingCondition.Is(enabled, true) & SettingCondition.ServerHasMod("satis")));

        ScopedSettingPresentation Derived(ScopedSettingRecord row, out string help)
        {
            Assert.True(ModSettingsPages.TryPlacement("auto", row.Id, out var item, out var title, out var so, out var ro));
            help = ScopedSettingPresentation.HelpFor(row.Help, item, null);
            return ScopedSettingPresentation.Derive(row, item, title, so, ro, null);
        }

        var p = Derived(Record("Enabled", "System.Boolean", "true"), out var help);
        Assert.Equal((ScopedWidget.Toggle, "Autoloot for players", "Server", 1, 1), (p.Widget, p.Label, p.SectionTitle, p.SectionOrder, p.RowOrder));
        Assert.Equal("Off: nothing is picked up for anyone.", help);

        p = Derived(Record("Radius", "System.Int32", "10"), out help);
        Assert.Equal((ScopedWidget.Slider, "m", "1", 2), (p.Widget, p.Units, p.Step, p.RowOrder));
        Assert.Equal("Help for Radius.", help); // no page help: the description

        // Labels follow the row's own choice order, whatever order the page listed them in.
        p = Derived(Record("Pickup", "System.String", "ComesInRange", false, "ComesInRange", "LandsInRange"), out _);
        Assert.Equal(ScopedWidget.Radio, p.Widget);
        Assert.Equal(new[] { "Any drop that comes in range", "Only drops that land in range" }, p.ChoiceLabels);
        Assert.Equal("Server/Enabled == true", p.EnableIf!.ToString());

        p = Derived(Record("NoClientHint", "System.Boolean", "true"), out _);
        Assert.Equal(("Players without the client part", 2), (p.SectionTitle, p.SectionOrder));
        Assert.Equal("(Server/Enabled == true && server has satis)", p.ShowIf!.ToString());
        Assert.False(ModSettingsPages.TryPlacement("auto", "auto/Server/Unplaced", out _, out _, out _, out _));
    }

    [Fact]
    public void Off_the_page_the_catalog_presentation_names_labels_and_group()
    {
        var row = Record("Share", "System.String", "Closest", false, "Closest", "Even");
        var p = ScopedSettingPresentation.Derive(row, null, "", -1, -1, new SettingPresentation
        {
            Label = "Several players in range", ChoiceLabels = new[] { "Closest player first", "Take turns (even)" },
            EditorHint = SettingsEditorHint.Dropdown, Group = "Server", Order = 4,
        });
        Assert.Equal((ScopedWidget.Dropdown, "Several players in range", "Server", 4), (p.Widget, p.Label, p.SectionTitle, p.RowOrder));
        Assert.Equal(new[] { "Closest player first", "Take turns (even)" }, p.ChoiceLabels);
        Assert.True(ScopedSettingPresentation.Derive(row, null, "", -1, -1, null).IsEmpty);
    }

    // ---- client drawing ---------------------------------------------------------------------------------------

    [Fact]
    public void Hints_choose_the_control_label_units_and_choice_words()
    {
        var radius = Record("Radius", "System.Int32", "10");
        radius.Min = "1"; radius.Max = "30";
        radius.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Slider, Units = "m", Label = "Pickup range" };
        var spec = ScopedSettingsRows.Spec(radius);
        Assert.Equal((AdminSettingKind.Slider, "Pickup range", "m", 1.0, 30.0, 1.0), (spec.Kind, spec.Label, spec.Units, spec.SliderMin, spec.SliderMax, spec.Step));
        Assert.Equal("12", ModSettingsView.AdminNumberText(11.6, spec));

        var pickup = Record("Pickup", "System.String", "ComesInRange", false, "ComesInRange", "LandsInRange");
        pickup.Presentation = new ScopedSettingPresentation { ChoiceLabels = new[] { "Any drop", "Only landed" } };
        spec = ScopedSettingsRows.Spec(pickup);
        Assert.True(spec.ChoiceRadio); // four or fewer choices: every option visible
        Assert.Equal(new[] { "Any drop", "Only landed" }, spec.ChoiceLabels);
        Assert.Equal("Any drop", spec.DisplayOf("ComesInRange"));
        pickup.Presentation.Widget = ScopedWidget.Dropdown;
        Assert.False(ScopedSettingsRows.Spec(pickup).ChoiceRadio);
        var five = Record("Five", "System.String", "a", false, "a", "b", "c", "d", "e");
        Assert.False(ScopedSettingsRows.Spec(five).ChoiceRadio);

        // A slider needs both bounds; without them it is the number field.
        var open = Record("Open", "System.Int32", "1");
        open.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Slider };
        Assert.Equal(AdminSettingKind.Number, ScopedSettingsRows.Spec(open).Kind);
        var scale = Record("Scale", "System.Single", "0.5");
        scale.Min = "0"; scale.Max = "1";
        scale.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.Slider, Step = "0.05" };
        spec = ScopedSettingsRows.Spec(scale);
        Assert.Equal(AdminSettingKind.Slider, spec.Kind);
        Assert.Equal("0.35", ModSettingsView.AdminNumberText(0.34, spec));
    }

    [Fact]
    public void Received_rows_are_grouped_and_ordered_as_the_page_named_them()
    {
        ScopedSettingRecord Placed(string key, string section, int so, int ro)
        {
            var r = Record(key, "System.Boolean", "true");
            r.Presentation = new ScopedSettingPresentation { SectionTitle = section, SectionOrder = so, RowOrder = ro };
            return r;
        }
        ScopedSettings.SetForUiTests(true, true,
            Placed("NoClientHint", "Players without the client part", 2, 9),
            Record("Unplaced", "System.Boolean", "true"),
            Placed("NoClientAutoloot", "Players without the client part", 2, 8),
            Placed("Radius", "Server", 1, 3),
            Placed("Enabled", "Server", 1, 2));
        var sections = ScopedSettingsRows.Merge("auto", Array.Empty<ModSettingsSection>());
        Assert.Equal(new[] { "Server", "Players without the client part" }, sections.Select(s => s.Title));
        Assert.Equal(new[] { "auto/Server/Enabled", "auto/Server/Radius", "auto/Server/Unplaced" }, sections[0].Items.Select(i => i.Id));
        Assert.Equal(new[] { "auto/Server/NoClientAutoloot", "auto/Server/NoClientHint" }, sections[1].Items.Select(i => i.Id));
        Assert.Equal(2, sections.Count);
    }

    [Fact]
    public void A_received_rule_is_evaluated_against_received_values()
    {
        var enabled = Record("Enabled", "System.Boolean", "false");
        var hint = Record("NoClientHint", "System.Boolean", "true");
        hint.Presentation = new ScopedSettingPresentation
        {
            ShowIf = SettingCondition.Is("Server", "Enabled", true),
            EnableIf = SettingCondition.ServerHasMod("satis").Resolved(_ => true),
        };
        ScopedSettings.SetForUiTests(true, true, enabled, hint);
        var item = ScopedSettingsRows.Item(hint);
        Assert.False(item.Options.ShowIf!());
        Assert.True(item.Options.EnableIf!());
        enabled.Value = "True";
        Assert.True(item.Options.ShowIf!());
        Assert.True(ScopedSettingsRows.ServerFact("auto", "satis"));
        Assert.Null(ScopedSettingsRows.ServerFact("auto", "other"));
    }

    [Fact]
    public void Per_player_rows_appear_on_a_mod_page_placed_or_not()
    {
        // Answer to "with the client DLL, do PerPlayer rows show on the mod's own page?": yes. Unplaced ones are added
        // after the page's sections under their section; placed ones are drawn where the page puts them.
        var (enabled, _, _, _) = Autoloot();
        var mine = Record("Autoloot", "System.Boolean", "true", personal: true);
        mine.Id = "auto/Autoloot/Autoloot"; mine.Section = "Autoloot";
        var caps = Record("UseSatisvamporyCaps", "System.Boolean", "true", personal: true);
        caps.Id = "auto/Autoloot/UseSatisvamporyCaps"; caps.Section = "Autoloot";
        ScopedSettings.SetForUiTests(true, false, mine, caps);
        var registration = ModSettingsPages.Register(this, "auto", page =>
        {
            page.Section("Me");
            page.AddScoped(mine.Id, SettingControl.Toggle, null, "Autoloot", "Pick up drops near you.", null, null);
            page.Section("Server").Toggle(enabled);
        });
        var merged = ScopedSettingsRows.Merge("auto", registration.Sections());
        Assert.Equal(new[] { "Me", "Server", "Autoloot" }, merged.Select(s => s.Title));
        var placed = Assert.Single(merged[0].Items);
        Assert.Equal(mine.Id, placed.Id);
        Assert.Equal("Autoloot", placed.Admin!.Label);
        Assert.True(placed.Admin.Personal);
        Assert.Equal(caps.Id, Assert.Single(merged[2].Items).Id);
        Assert.Equal(1, merged.SelectMany(s => s.Items).Count(i => i.Id == mine.Id));
        var groups = ModSettingsScopes.Regroup(merged);
        Assert.Contains(groups, g => g.Heading == "Your settings on this server");

        // Without a server providing it, the placed row is not drawn (it only claims its id).
        ScopedSettings.SetForUiTests(false, false);
        Assert.True(Assert.Single(registration.Sections()[0].Items).Options.ClaimOnly);
        Assert.DoesNotContain(ModSettingsLayout.Build(registration.Sections(), null, null), s => s.Title == "Me");
    }

    [Fact]
    public void A_placed_server_wide_list_offline_claims_its_cfg_row()
    {
        var file = new ConfigFile(Path.Combine(_dir, "offline.cfg"), false);
        var entry = file.Bind("Server", "Filter", "5=1", "");
        ScopedSettings.SetForUiTests(false, false);
        var registration = ModSettingsPages.Register(this, "auto", page => page.Section("Server")
            .AddScoped("auto/Server/Filter", null, null, "Pickup filter", null, null, null));
        var built = ModSettingsLayout.Build(registration.Sections(), null, new[] { SettingsRow.ForEntry("auto", entry, true) });
        Assert.Empty(built.SelectMany(s => s.Items)); // no raw text field holding the encoded list
        // Once a server provides it, the list row is drawn in its place.
        ScopedSettings.SetForUiTests(true, true, ListRecord("5=1", personal: false));
        built = ModSettingsLayout.Build(registration.Sections(), null, new[] { SettingsRow.ForEntry("auto", entry, true) });
        Assert.Equal(AdminSettingKind.ItemList, Assert.Single(built.SelectMany(s => s.Items)).Admin!.Kind);
    }

    [Fact]
    public void A_placed_server_wide_list_is_not_drawn_again_as_its_cfg_row()
    {
        var file = new ConfigFile(Path.Combine(_dir, "list.cfg"), false);
        var entry = file.Bind("Server", "Filter", "5=1", "");
        var record = ListRecord("5=1", personal: false);
        ScopedSettings.SetForUiTests(true, true, record);
        var registration = ModSettingsPages.Register(this, "auto", page => page.Section("Server")
            .AddScoped(record.Id, null, null, "Pickup filter", null, null, null));
        var built = ModSettingsLayout.Build(registration.Sections(), null, new[] { SettingsRow.ForEntry("auto", entry, true) });
        var only = Assert.Single(built.SelectMany(s => s.Items));
        Assert.Equal(AdminSettingKind.ItemList, only.Admin!.Kind);
        Assert.Equal("Pickup filter", only.Admin.Label);
    }

    [Fact]
    public void A_local_rule_reads_its_entry()
    {
        var (enabled, radius, _, hint) = Autoloot();
        var registration = ModSettingsPages.Register(this, "auto", page => page
            .Section("S")
                .Toggle(hint, showIf: () => true).ShowWhen(SettingCondition.Is(enabled, true))
                .Slider(radius).EnableWhen(SettingCondition.AtLeast(radius, 5)).EnableIf(enabled, true));
        var items = registration.SectionSpecs[0].Items;
        Assert.True(items[0].Options.ShowIf!());
        enabled.Value = false;
        Assert.False(items[0].Options.ShowIf!());
        Assert.False(items[1].Options.EnableIf!());
        enabled.Value = true;
        Assert.True(items[1].Options.EnableIf!());
        radius.Value = 3;
        Assert.False(items[1].Options.EnableIf!());
        Assert.Equal("(Server/Radius >= 5 && Server/Enabled == true)", items[1].Options.EnableWhen!.ToString());
        Assert.Throws<InvalidOperationException>(() => ModSettingsPages.Register(this, "auto2", page => page.ShowIf(enabled, true)));
    }

    // ---- item list editor -------------------------------------------------------------------------------------

    private ModSettingsView DrawList(ScopedSettingRecord record, bool admin = true)
    {
        ScopedSettings.SetForUiTests(true, admin, record);
        _view = new ModSettingsView("auto", ScopedSettingsRows.Merge("auto", Array.Empty<ModSettingsSection>()), "",
            () => ServerPartState.Running, () => new SettingsScopeContext
            {
                Connection = SettingConnection.Progenitor, ServerPart = ServerPartState.Running, CanEditServerWide = admin,
            });
        _view.Build(UiApi.Panel("Item list", this, "auto"), (_, _, _) => { });
        return _view;
    }

    private static ScopedSettingRecord ListRecord(string value, bool personal = true)
    {
        var r = Record("Filter", "System.String", value, personal);
        r.MaxLength = ItemLimitList.MaxEncodedBytes;
        r.Presentation = new ScopedSettingPresentation
        {
            Widget = ScopedWidget.ItemList, Label = "Pickup filter", AllowCategories = true, MaxEntries = 3, MaxCount = 100,
            CategoryIds = new[] { "gems" }, CategoryLabels = new[] { "Gems" },
        };
        return r;
    }

    [Fact]
    public void The_item_list_row_edits_through_the_picker_and_the_server()
    {
        ModSettingsView.ItemCatalog = () => new[]
        {
            new ItemCatalogEntry(11, "Item_Ingredient_Stone", "Stone", 500, 0, 0, false),
            new ItemCatalogEntry(12, "Item_Ingredient_Wood", "Lumber", 500, 0, 0, false),
            new ItemCatalogEntry(13, "Item_Unnamed", null, 1, 0, 0, false),
        };
        var record = ListRecord("11=5");
        var spec = ScopedSettingsRows.Spec(record);
        Assert.Equal(AdminSettingKind.ItemList, spec.Kind);
        Assert.Equal("1 item listed", spec.DisplayOf(record.Value));
        var view = DrawList(record);
        var editor = view.ItemListFor(record.Id)!;
        editor.Open();
        Assert.True(editor.IsOpen);
        Assert.Equal("Stone", editor.NameOf(ItemLimit.ForItem(11, 1)));
        Assert.Equal("Gems", editor.NameOf(ItemLimit.ForCategory("gems", 1)));
        Assert.Equal("Item 99", editor.NameOf(ItemLimit.ForItem(99, 1)));

        var entries = editor.PickerEntries();
        Assert.Equal(new[] { "Gems", "Lumber", "Stone" }, entries.Select(e => e.Label));
        Assert.Equal("Already listed", entries.Single(e => e.Id == "i:11").DisabledReason);

        Assert.True(editor.Add(new[] { "i:12", "c:gems" }));
        var request = Assert.Single(ScopedSettings.Requests);
        Assert.Equal(("11=5;12=0;c:gems=0", "11=5"), (request.Value, request.Expected));
        Assert.Equal("11=5;12=0;c:gems=0", record.Value);

        // The rules the server sent are checked before sending: a fourth line is one too many.
        Assert.False(editor.Add(new[] { "i:13" }));
        Assert.Single(ScopedSettings.Requests);
        Assert.True(editor.Commit(editor.Current.Without("i:12")));
        Assert.Equal("11=5;c:gems=0", record.Value);
        view.Release();
        Assert.False(editor.IsOpen);
        _view = null;
    }

    [Fact]
    public void Changes_made_while_the_server_answers_are_queued_not_lost()
    {
        string stored = "11=5";
        var answers = new List<Action>();
        var hints = ListRecord("").Presentation!;
        var spec = new AdminSettingSpec
        {
            Manifest = true, Personal = true, PluginId = "auto", SettingId = "auto/Server/Filter", Key = "auto/Server/Filter",
            Label = "Pickup filter", Kind = AdminSettingKind.ItemList, ListHints = hints, DefaultValue = "",
            Read = () => stored,
            SendExpected = (value, expected, done) =>
            {
                answers.Add(() => { stored = value; done(true, ""); });
                return new Lease();
            },
        };
        ScopedSettings.SetForUiTests(true, false); // connected: a manifest row is available
        var item = ModSettingsItem.ForAdmin(spec.SettingId, spec, new ModSettingOptions { Scope = SettingScope.Personal });
        _view = new ModSettingsView("auto", new[] { new ModSettingsSection("S", ModSettingsSectionKind.Group, new List<ModSettingsItem> { item }) }, "",
            () => ServerPartState.Running, () => new SettingsScopeContext { Connection = SettingConnection.Progenitor, ServerPart = ServerPartState.Running });
        _view.Build(UiApi.Panel("Queue", this, "auto"), (_, _, _) => { });
        var editor = _view.ItemListFor(spec.SettingId)!;
        Assert.True(editor.Add(new[] { "i:12" }));
        Assert.True(editor.Add(new[] { "i:13" }));                      // the first change is still waiting
        Assert.True(editor.Apply(l => ModSettingsView.ItemListEditor.WithCount(l, "i:11", 9)));
        Assert.Equal(2, editor.QueuedCount);
        Assert.Single(answers);
        answers[0]();                                                      // the server answers: the queue goes out together
        Assert.Equal(0, editor.QueuedCount);
        Assert.Equal(2, answers.Count);
        answers[1]();
        Assert.Equal("11=9;12=0;13=0", stored);
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }

    [Fact]
    public void A_count_limit_of_zero_means_zero_on_both_sides()
    {
        var record = ListRecord("11=0");
        record.Presentation!.MaxCount = 0;
        var view = DrawList(record);
        var editor = view.ItemListFor(record.Id)!;
        Assert.False(editor.Commit(ItemLimitList.Of(new[] { ItemLimit.ForItem(11, 1) })));
        Assert.Empty(ScopedSettings.Requests);
        Assert.False(ItemLimitList.TryParse("11=1", new ItemListRules { MaxCount = 0 }, out _, out _));
        Assert.True(editor.Add(new[] { "i:12" }));
        Assert.Equal("11=0;12=0", record.Value);
    }

    [Fact]
    public void A_resent_catalog_with_the_same_rows_is_not_a_page_rebuild()
    {
        var a = ListRecord("11=5");
        var b = ListRecord("12=9;13=1");
        b.Default = a.Default; // only the value differs
        Assert.Equal(ModsMenu.ScopedShape(new[] { a }), ModsMenu.ScopedShape(new[] { b }));
        b.Presentation!.Label = "Other";
        Assert.NotEqual(ModsMenu.ScopedShape(new[] { a }), ModsMenu.ScopedShape(new[] { b }));
        b = ListRecord("x"); b.Editable = false;
        Assert.NotEqual(ModsMenu.ScopedShape(new[] { a }), ModsMenu.ScopedShape(new[] { b }));
    }

    [Fact]
    public void A_server_wide_list_reads_as_its_size_for_a_player_who_is_not_an_admin()
    {
        var record = ListRecord("11=5;c:gems=1", personal: false);
        var view = DrawList(record, admin: false);
        Assert.Null(view.ItemListFor(record.Id)); // drawn read-only, no editor
        Assert.Equal("1 item, 1 category listed", ModSettingsView.ItemListSummary(record.Value));
        Assert.Equal("Nothing listed", ModSettingsView.ItemListSummary(""));
        Assert.Equal("Not readable", ModSettingsView.ItemListSummary("garbage"));
    }

    // ---- PICKFRONT1: stackable-only lists ---------------------------------------------------------------------

    private static ItemCatalogEntry[] StackCatalog() => new[]
    {
        new ItemCatalogEntry(11, "Item_Ingredient_Stone", "Stone", 500, 0, 0, false),
        new ItemCatalogEntry(12, "Item_Ingredient_Wood", "Lumber", 500, 0, 0, false),
        new ItemCatalogEntry(14, "Item_Weapon_Sword_T01", "Bone Sword", 1, 0, 0, false),
    };

    [Fact]
    public void The_stackable_only_flag_rides_version_3_and_older_clients_get_version_2_without_it()
    {
        var row = Record("Filter", "System.String", "");
        row.Presentation = new ScopedSettingPresentation { Widget = ScopedWidget.ItemList, MaxEntries = 10, MaxCount = 99, StackableOnly = true };
        Assert.True(RoundTrip(row, ScopedSettingsWire.ListFilterVersion).Presentation!.StackableOnly);
        var older = RoundTrip(row, ScopedSettingsWire.PresentationVersion).Presentation!;
        Assert.False(older.StackableOnly);
        Assert.Equal(10, older.MaxEntries);

        // One byte more than version 2, and an unknown flag bit is refused.
        int v2 = ScopedSettingsWire.Encode(RowPacket(row), ScopedSettingsWire.PresentationVersion).Length;
        var v3 = ScopedSettingsWire.Encode(RowPacket(row), ScopedSettingsWire.ListFilterVersion);
        Assert.Equal(v2 + 1, v3.Length);
        Assert.Equal(ScopedSettingPresentation.StackableOnlyFlag, v3[^1]);
        v3[^1] = 3;
        Assert.False(ScopedSettingsWire.TryDecode(v3, true, out _));
        Assert.False(new ScopedSettingPresentation { StackableOnly = true }.IsEmpty);

        // The worst-case list row (registration's check) still fits at version 3.
        var worst = ScopedSettingPresentation.WorstCaseListRow(ListRecord(""));
        ScopedSettingsWire.Encode(RowPacket(worst), ScopedSettingsWire.ListFilterVersion);

        // Who is sent which version: the capability decides.
        Assert.Equal(ScopedSettingsWire.ListFilterVersion, ScopedSettingsWire.RowVersion(true, true));
        Assert.Equal(ScopedSettingsWire.PresentationVersion, ScopedSettingsWire.RowVersion(true, false));
        Assert.Equal(ScopedSettingsWire.Version, ScopedSettingsWire.RowVersion(false, true));
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.ScopedSettingsListFilters) != 0);
        foreach (Handshake.Capability other in Enum.GetValues(typeof(Handshake.Capability)))
            if (other != Handshake.Capability.ScopedSettingsListFilters && other != Handshake.Capability.None)
                Assert.True((other & Handshake.Capability.ScopedSettingsListFilters) == 0, other + " shares the bit");
    }

    [Fact]
    public void The_server_refuses_an_item_that_does_not_stack_when_the_list_takes_stackables_only()
    {
        var catalog = StackCatalog().ToDictionary(e => e.PrefabGuid, e => e.MaxAmount);
        ItemListSettings.StackSize = guid => catalog.TryGetValue(guid, out int size) ? size : null;
        var rules = new ItemListRules { MaxCount = 100, StackableOnly = true };

        Assert.True(ItemListSettings.TryCanonical(rules, "11=5;12=0", "", out var canonical, out _));
        Assert.Equal("11=5;12=0", canonical);
        Assert.False(ItemListSettings.TryCanonical(rules, "11=5;14=0", "11=5", out _, out var error));
        Assert.Equal("The item 14 does not stack; this list takes stackable items only.", error);
        Assert.False(ItemListSettings.TryCanonical(rules, "99=0", "", out _, out error));
        Assert.Contains("could not be checked", error);
        // A line already stored is kept (an edit of another line is not refused for it).
        Assert.True(ItemListSettings.TryCanonical(rules, "14=3;11=0", "14=0", out _, out _));
        // Without the flag nothing is checked against the catalog.
        Assert.True(ItemListSettings.TryCanonical(new ItemListRules { MaxCount = 100 }, "14=0;99=0", "", out _, out _));
        // Reading a stored list never consults the catalog.
        Assert.True(ItemLimitList.TryParse("14=0;99=0", rules, out _, out _));
    }

    [Fact]
    public void A_stackable_only_picker_lists_only_items_that_stack()
    {
        ModSettingsView.ItemCatalog = StackCatalog;
        var record = ListRecord("");
        record.Presentation!.StackableOnly = true;
        var view = DrawList(record);
        var editor = view.ItemListFor(record.Id)!;
        editor.Open();
        Assert.Equal(new[] { "Gems", "Lumber", "Stone" }, editor.PickerEntries().Select(e => e.Label));
        // A key from elsewhere (the bridge) is refused before it is sent.
        Assert.False(editor.Add(new[] { "i:14" }));
        Assert.Empty(ScopedSettings.Requests);
        Assert.True(editor.Add(new[] { "i:11" }));
        Assert.Equal("11=0", record.Value);

        record.Presentation.StackableOnly = false;
        Assert.Contains("Bone Sword", editor.PickerEntries().Select(e => e.Label));
    }

    [Fact]
    public void The_list_window_picker_draws_over_the_list_window()
    {
        ModSettingsView.ItemCatalog = StackCatalog;
        var record = ListRecord("");
        var view = DrawList(record);
        var editor = view.ItemListFor(record.Id)!;
        editor.Open();
        editor.OpenPicker();
        Assert.True(editor.PickerOpen);
        int list = _runtime.SpotFor("Pickup filter").Sorting;
        int picker = _runtime.SpotFor("Add to Pickup filter").Sorting;
        Assert.True(picker > list, "picker " + picker + " over list " + list);
        editor.Open(); // Edit pressed again while the picker is open: the list is not raised over it
        Assert.True(_runtime.SpotFor("Add to Pickup filter").Sorting > _runtime.SpotFor("Pickup filter").Sorting);
    }
}
