using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of cloned-buff HUD name/description: key shape, apply decision,
/// fake-entry write (Header / description / UseParentNameAndDescription), and the
/// unregistered-key reason.
/// </summary>
public class BuffBarTextTests
{
    [Fact]
    public void NameKey_and_DescriptionKey_are_id_dot_name_and_id_dot_desc()
    {
        Assert.Equal("sample.nodrain.name", BuffBarText.NameKey("sample.nodrain"));
        Assert.Equal("sample.nodrain.desc", BuffBarText.DescriptionKey("sample.nodrain"));
        Assert.Equal(".name", BuffBarText.NameKeySuffix);
        Assert.Equal(".desc", PrefabWire.DescriptionKeySuffix);
    }

    [Fact]
    public void NameKey_rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => BuffBarText.NameKey(""));
        Assert.Throws<ArgumentException>(() => BuffBarText.NameKey(null!));
    }

    [Fact]
    public void ShouldApply_skips_server_items_and_empty_text()
    {
        Assert.True(BuffBarText.ShouldApply(false, PrefabKind.Buff, "No Drain", null, false, false));
        Assert.True(BuffBarText.ShouldApply(false, PrefabKind.Buff, null, "desc", false, false));
        Assert.True(BuffBarText.ShouldApply(false, PrefabKind.Buff, null, null, true, false));
        Assert.True(BuffBarText.ShouldApply(false, PrefabKind.Buff, null, null, false, true));
        Assert.False(BuffBarText.ShouldApply(true, PrefabKind.Buff, "No Drain", "desc", true, true));
        Assert.False(BuffBarText.ShouldApply(false, PrefabKind.Item, "No Drain", "desc", true, true));
        Assert.False(BuffBarText.ShouldApply(false, PrefabKind.Buff, null, null, false, false));
    }

    [Fact]
    public void ResolveKeys_writes_registered_keys_and_reports_unregistered()
    {
        BuffBarText.ResolveKeys(
            "sample.nodrain",
            localizedName: "No Drain",
            localizedDescription: "Idle blood drain is paused while this buff is active.",
            nameRegistered: true,
            descriptionRegistered: true,
            out var header,
            out var desc,
            out var unregistered);
        Assert.Equal("sample.nodrain.name", header);
        Assert.Equal("sample.nodrain.desc", desc);
        Assert.Null(unregistered);

        BuffBarText.ResolveKeys(
            "sample.nodrain",
            localizedName: "No Drain",
            localizedDescription: null,
            nameRegistered: false,
            descriptionRegistered: false,
            out header,
            out desc,
            out unregistered);
        Assert.Null(header);
        Assert.Null(desc);
        Assert.Equal("sample.nodrain.name", unregistered);
    }

    [Fact]
    public void WriteFakeEntries_sets_Header_description_and_clears_UseParent()
    {
        var entries = new List<BuffBarText.FakeEntry>
        {
            new()
            {
                HeaderKey = "haste",
                DescriptionKey = "haste.desc",
                ParameterIndex = 2,
                ParameterCount = 1,
                UseParentNameAndDescription = true,
            },
        };

        int written = BuffBarText.WriteFakeEntries(
            entries,
            "sample.nodrain.name",
            "sample.nodrain.desc",
            out var reason);
        Assert.Equal(1, written);
        Assert.Equal("", reason);
        Assert.Equal("sample.nodrain.name", entries[0].HeaderKey);
        Assert.Equal("sample.nodrain.desc", entries[0].DescriptionKey);
        Assert.Equal(0, entries[0].ParameterIndex);
        Assert.Equal(0, entries[0].ParameterCount);
        Assert.False(entries[0].UseParentNameAndDescription);
    }

    [Fact]
    public void WriteFakeEntries_Named_only_keeps_parent_description_and_UseParent()
    {
        var entries = new List<BuffBarText.FakeEntry>
        {
            new()
            {
                HeaderKey = "haste",
                DescriptionKey = "haste.desc",
                UseParentNameAndDescription = true,
            },
        };

        int written = BuffBarText.WriteFakeEntries(
            entries,
            "sample.nodrain.name",
            null,
            out var reason);
        Assert.Equal(1, written);
        Assert.Equal("", reason);
        Assert.Equal("sample.nodrain.name", entries[0].HeaderKey);
        Assert.Equal("haste.desc", entries[0].DescriptionKey);
        Assert.True(entries[0].UseParentNameAndDescription);
        Assert.Equal(
            "PrefabRegistry: Named without Described on 'sample.nodrain' (parent description kept; call Described or add id.desc)",
            BuffBarText.FormatNamedWithoutDescribed("sample.nodrain"));
        Assert.True(BuffBarText.ShouldLogNamedOnly(wroteHeader: true, wroteDescription: false, logNamedOnly: true));
        Assert.False(BuffBarText.ShouldLogNamedOnly(wroteHeader: true, wroteDescription: false, logNamedOnly: false));
        Assert.False(BuffBarText.ShouldLogNamedOnly(wroteHeader: true, wroteDescription: true, logNamedOnly: true));
        Assert.False(BuffBarText.ShouldLogNamedOnly(wroteHeader: false, wroteDescription: false, logNamedOnly: true));
    }

    [Fact]
    public void WriteFakeEntries_unreadable_no_entries_and_unregistered_reasons()
    {
        Assert.Equal(0, BuffBarText.WriteFakeEntries(null, "a", "b", out var reason));
        Assert.Equal(BuffBarText.UnreadableReason, reason);

        Assert.Equal(0, BuffBarText.WriteFakeEntries(new List<BuffBarText.FakeEntry>(), "a", "b", out reason));
        Assert.Equal(BuffBarText.NoEntriesReason, reason);

        var emptyKeys = new List<BuffBarText.FakeEntry> { new() { UseParentNameAndDescription = true } };
        Assert.Equal(0, BuffBarText.WriteFakeEntries(emptyKeys, null, null, out reason));
        Assert.Equal(BuffBarText.UnregisteredKeyReason, reason);
        Assert.True(emptyKeys[0].UseParentNameAndDescription);
    }

    [Fact]
    public void FormatApplied_is_the_client_log_line()
    {
        Assert.Equal(
            "PrefabRegistry: name/description -> ShowOnBuffBarData entries=1 on 'sample.nodrain'",
            BuffBarText.FormatApplied(1, "sample.nodrain"));
    }

    [Fact]
    public void FormatFailed_covers_unreadable_no_entries_and_unregistered()
    {
        Assert.Equal(
            "PrefabRegistry: name/description " + BuffBarText.UnreadableReason + " on 'sample.nodrain'",
            BuffBarText.FormatFailed(BuffBarText.UnreadableReason, "sample.nodrain"));
        Assert.Equal(
            "PrefabRegistry: name/description " + BuffBarText.NoEntriesReason + " on 'sample.nodrain'",
            BuffBarText.FormatFailed(BuffBarText.NoEntriesReason, "sample.nodrain"));
        Assert.Equal(
            "PrefabRegistry: name/description localization key unregistered ('sample.nodrain.name') on 'sample.nodrain'",
            BuffBarText.FormatFailed(BuffBarText.FormatUnregisteredReason("sample.nodrain.name"), "sample.nodrain"));
    }

    [Fact]
    public void Applicator_registers_buff_keys_then_writes_ShowOnBuffBarData()
    {
        var src = ReadContentFile("PrefabApplicator.cs");
        SourcePin.AssertUncommented(src, "BuffBarText.NameKey(def.Id)");
        SourcePin.AssertStatement(src, "BuffBarTextApply.TryApply(em, existing, def)");
        SourcePin.AssertStatement(src, "BuffBarTextApply.TryApply(em, clone, def)");
        int buffUpdate = src.IndexOf("sb.Append(\"kind=buff\")", StringComparison.Ordinal);
        Assert.True(buffUpdate >= 0);
        int register = src.IndexOf("RegisterLocalizedFields(def, BuffBarText.NameKey(def.Id)", buffUpdate, StringComparison.Ordinal);
        int apply = src.IndexOf("BuffBarTextApply.TryApply(em, existing, def)", buffUpdate, StringComparison.Ordinal);
        Assert.True(register > buffUpdate);
        Assert.True(apply > register);
    }

    [Fact]
    public void BuffBarTextApply_uses_the_Il2CppSystem_Object_read_and_clears_UseParent()
    {
        var src = ReadContentFile("BuffBarTextApply.cs");
        SourcePin.AssertUncommented(src, "BuffBarDataAccess.TryGet");
        SourcePin.AssertStatement(src, "entry.Header");
        SourcePin.AssertStatement(src, "entry.Description");
        SourcePin.AssertUncommented(src, "ShouldClearUseParent");
        SourcePin.AssertUncommented(src, "FormatNamedWithoutDescribed");
        SourcePin.AssertUncommented(src, "logNamedOnly");
        SourcePin.AssertUncommented(src, "ShouldLogNamedOnly");
        SourcePin.AssertUncommented(src, "RememberNamedOnly");
        SourcePin.AssertUncommented(src, "Localization.KeyFor");

        var access = ReadContentFile("BuffBarDataAccess.cs");
        SourcePin.AssertUncommented(access, "GetComponentObject<Il2CppSystem.Object>");
    }

    [Fact]
    public void Builder_stores_LocalizedName_and_LocalizedDescription()
    {
        var src = ReadContentFile("PrefabRegistry.cs");
        int start = src.IndexOf("public sealed class BuffDefinitionBuilder", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = src.IndexOf("// ---- registration", start, StringComparison.Ordinal);
        string builder = src.Substring(start, end - start);
        SourcePin.AssertStatement(builder, "private string? _localizedName;");
        SourcePin.AssertStatement(builder, "private string? _localizedDescription;");
        SourcePin.AssertStatement(builder, "_localizedName = localizedName;");
        SourcePin.AssertStatement(builder, "_localizedDescription = text;");
        Assert.Contains("id.name", builder, StringComparison.Ordinal);
        Assert.Contains("UseParentNameAndDescription", builder, StringComparison.Ordinal);
    }

    private static string ReadContentFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Content", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("src/Progenitor.Core/Content/" + fileName);
    }
}
