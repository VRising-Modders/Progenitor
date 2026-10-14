using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Bootstrap;
using Progenitor.Content;
using Progenitor.Localization;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of plugin-supplied buff icons: relative-path validation, the assets
/// probe order (same helper Localization uses), and the client-apply decision.
/// </summary>
public class PrefabIconTests
{
    [Fact]
    public void Normalize_trims_and_uses_forward_slashes()
    {
        Assert.Equal("icons/nodrain.png", PrefabIconPath.Normalize(@" icons\nodrain.png "));
    }

    [Theory]
    [InlineData("icons/nodrain.png")]
    [InlineData("icons\\nodrain.png")]
    [InlineData("a.png")]
    [InlineData("icons/sub/foo.png")]
    public void TryValidate_accepts_relative_png_paths(string path)
    {
        Assert.True(PrefabIconPath.TryValidate(path, out var error));
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_rejects_empty(string? path)
    {
        Assert.False(PrefabIconPath.TryValidate(path, out var error));
        Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\mods\icons\nodrain.png")]
    [InlineData("/absolute/icons/nodrain.png")]
    [InlineData(@"\absolute\icons\nodrain.png")]
    public void TryValidate_rejects_absolute_paths(string path)
    {
        Assert.False(PrefabIconPath.TryValidate(path, out var error));
        Assert.Contains("relative", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("absolute", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../icons/nodrain.png")]
    [InlineData("icons/../nodrain.png")]
    [InlineData(@"icons\..\secret.png")]
    [InlineData("..")]
    public void TryValidate_rejects_parent_segments(string path)
    {
        Assert.False(PrefabIconPath.TryValidate(path, out var error));
        Assert.Contains("..", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_throws_ArgumentException_on_absolute_or_parent()
    {
        Assert.Throws<ArgumentException>(() => PrefabIconPath.Validate(@"C:\x.png", "relativePath"));
        Assert.Throws<ArgumentException>(() => PrefabIconPath.Validate("../x.png", "relativePath"));
        PrefabIconPath.Validate("icons/nodrain.png", "relativePath");
    }

    [Fact]
    public void TryCombine_stays_under_the_asset_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-icon-tests", "assets");
        Assert.True(PrefabIconPath.TryCombine(root, "icons/nodrain.png", out var full, out var error));
        Assert.Equal("", error);
        Assert.StartsWith(Path.GetFullPath(root), full, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("nodrain.png", full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCombine_refuses_a_parent_escape_even_if_validation_were_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-icon-tests", "assets");
        Assert.False(PrefabIconPath.TryCombine(root, "../secret.png", out _, out var error));
        Assert.Contains("..", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldApply_skips_server_items_and_empty_path()
    {
        Assert.True(PrefabIconPath.ShouldApply(false, PrefabKind.Buff, "icons/nodrain.png"));
        Assert.False(PrefabIconPath.ShouldApply(true, PrefabKind.Buff, "icons/nodrain.png"));
        Assert.False(PrefabIconPath.ShouldApply(false, PrefabKind.Item, "icons/nodrain.png"));
        Assert.False(PrefabIconPath.ShouldApply(false, PrefabKind.Buff, null));
    }

    [Theory]
    [InlineData(false, PrefabKind.Item, "icons/robe.png", true)]
    [InlineData(true, PrefabKind.Item, "icons/robe.png", false)]
    [InlineData(false, PrefabKind.Buff, "icons/robe.png", false)]
    [InlineData(false, PrefabKind.Item, null, false)]
    [InlineData(false, PrefabKind.Item, "", false)]
    public void Item_icons_only_load_for_client_item_definitions(bool server, PrefabKind kind, string? path, bool expected)
    {
        Assert.Equal(expected, PrefabIconPath.ShouldApplyItem(server, kind, path));
    }

    [Fact]
    public void Item_icon_is_written_only_after_managed_data_is_cloned()
    {
        var src = ReadContentFile("PrefabApplicator.cs");
        int clone = src.IndexOf("var cloned = new ManagedItemData", StringComparison.Ordinal);
        int icon = src.IndexOf("BuffIconApply.TryApplyItem(cloned, def)", StringComparison.Ordinal);
        int insert = src.IndexOf("ManagedDataInsert.Set(map, key, cloned)", StringComparison.Ordinal);
        Assert.True(clone >= 0 && icon > clone && insert > icon);
        Assert.DoesNotContain("TryApplyItem(managed,", src, StringComparison.Ordinal);
        // The native ICloneable implementation returns its receiver, so wrapping that
        // pointer would mutate the donor's icon and localized text as well.
        Assert.DoesNotContain("managed.System_ICloneable_Clone()", src, StringComparison.Ordinal);
        string copy = src.Substring(clone, icon - clone);
        SourcePin.AssertUncommented(copy, "Icon = managed.Icon");
        SourcePin.AssertUncommented(copy, "Name = managed.Name");
        SourcePin.AssertUncommented(copy, "Description = managed.Description");
    }

    [Fact]
    public void FormatApplied_is_the_client_log_line()
    {
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' -> ShowOnBuffBarData entries=1 on 'sample.nodrain'",
            PrefabIconPath.FormatApplied("icons/nodrain.png", 1, "sample.nodrain"));
    }

    [Fact]
    public void FormatFailed_covers_the_three_named_reasons()
    {
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' file missing on 'sample.nodrain'",
            PrefabIconPath.FormatFailed("icons/nodrain.png", PrefabIconPath.FileMissingReason, "sample.nodrain"));
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' decode failed on 'sample.nodrain'",
            PrefabIconPath.FormatFailed("icons/nodrain.png", PrefabIconPath.DecodeFailedReason, "sample.nodrain"));
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' no ShowOnBuffBarData on the clone 'sample.nodrain'",
            PrefabIconPath.FormatFailed("icons/nodrain.png", PrefabIconPath.NoBuffBarDataReason, "sample.nodrain"));
    }

    [Fact]
    public void FormatFromCache_is_the_swap_time_client_log_line()
    {
        Assert.Equal(
            "PrefabRegistry: icon 'icons/nodrain.png' from cache on 'sample.nodrain'",
            PrefabIconPath.FormatFromCache("icons/nodrain.png", "sample.nodrain"));
        Assert.Equal("from cache", PrefabIconPath.FromCacheReason);
    }

    [Fact]
    public void Cache_key_matches_Bootstrap_IconCachePolicy()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47 };
        string key = PrefabIconPath.FormatCacheKey("progenitor.sample", "icons/nodrain.png", png);
        Assert.Equal(
            IconCachePolicy.FormatKey("progenitor.sample", "icons/nodrain.png", IconCachePolicy.HashBytes(png)),
            key);
        Assert.Equal(IconCachePolicy.HashBytes(png), PrefabIconPath.HashPng(png));
    }

    [Fact]
    public void Assets_probe_prefers_assembly_named_folder_over_sibling()
    {
        var nested = Path.Combine(@"C:\mods", "Progenitor.Sample", "assets");
        var sibling = Path.Combine(@"C:\mods", "assets");
        bool Exists(string path) =>
            string.Equals(path, nested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, sibling, StringComparison.OrdinalIgnoreCase);

        var chosen = LocalizationParser.ResolvePluginFolder(
            @"C:\mods", "Progenitor.Sample", Exists, LocalizationParser.AssetsFolderName);
        Assert.Equal(nested, chosen);
    }

    [Fact]
    public void Assets_probe_falls_back_to_sibling_when_named_folder_is_missing()
    {
        var sibling = Path.Combine(@"C:\mods", "assets");
        var chosen = LocalizationParser.ResolvePluginFolder(
            @"C:\mods",
            "Progenitor.Sample",
            path => string.Equals(path, sibling, StringComparison.OrdinalIgnoreCase),
            LocalizationParser.AssetsFolderName);
        Assert.Equal(sibling, chosen);
    }

    [Fact]
    public void Assets_probe_returns_null_when_neither_folder_exists()
    {
        Assert.Null(LocalizationParser.ResolvePluginFolder(
            @"C:\mods", "Progenitor.Sample", _ => false, LocalizationParser.AssetsFolderName));
    }

    [Fact]
    public void Assets_probe_named_folder_before_sibling()
    {
        var order = new List<string>();
        LocalizationParser.ResolvePluginFolder(@"C:\mods", "Foo", path =>
        {
            order.Add(path);
            return false;
        }, LocalizationParser.AssetsFolderName);
        Assert.Equal(new[]
        {
            Path.Combine(@"C:\mods", "Foo", "assets"),
            Path.Combine(@"C:\mods", "assets"),
        }, order);
    }

    [Fact]
    public void Localization_three_arg_overload_still_probes_localization()
    {
        var order = new List<string>();
        LocalizationParser.ResolvePluginFolder(@"C:\mods", "Foo", path =>
        {
            order.Add(path);
            return false;
        });
        Assert.Equal(new[]
        {
            Path.Combine(@"C:\mods", "Foo", "localization"),
            Path.Combine(@"C:\mods", "localization"),
        }, order);
    }

    [Fact]
    public void Holder_root_is_progenitor_content_icons_pluginId()
    {
        Assert.Equal("progenitor-content-icons-", ContentIconOwnership.Prefix);
        Assert.Equal(
            "progenitor-content-icons-progenitor.sample",
            ContentIconOwnership.RootName("progenitor.sample"));
        Assert.True(ContentIconOwnership.IsOwned("progenitor-content-icons-progenitor.sample"));
        Assert.True(ContentIconOwnership.IsOwnedBy(
            "progenitor-content-icons-progenitor.sample",
            "progenitor.sample"));
        Assert.False(ContentIconOwnership.IsOwnedBy(
            "progenitor-content-icons-progenitor.sample.extra",
            "progenitor.sample"));
        Assert.Equal("unowned", ContentIconOwnership.Sanitize(null));
    }

    [Fact]
    public void Applicator_calls_icon_apply_after_a_buff_clone_and_skips_it_on_items()
    {
        var src = ReadContentFile("PrefabApplicator.cs");
        SourcePin.AssertStatement(src, "BuffIconApply.TryApply(em, existing, def)");
        SourcePin.AssertStatement(src, "BuffIconApply.TryApply(em, clone, def)");
        int buffUpdate = src.IndexOf("sb.Append(\"kind=buff\")", StringComparison.Ordinal);
        Assert.True(buffUpdate >= 0);
        int applyOnUpdate = src.IndexOf("BuffIconApply.TryApply(em, existing, def)", buffUpdate, StringComparison.Ordinal);
        Assert.True(applyOnUpdate > buffUpdate);
    }

    [Fact]
    public void BuffIconApply_gates_on_ShouldApply_so_the_server_never_loads_a_png()
    {
        var src = ReadContentFile("BuffIconApply.cs");
        SourcePin.AssertUncommented(src, "PrefabIconPath.ShouldApply(Plugin.IsServerProcess, def.Kind, def.IconPath)");
        SourcePin.AssertUncommented(src, "BuffBarDataAccess.TryGet");
        Assert.Contains("ShowOnBuffBarData", src, StringComparison.Ordinal);
        var access = ReadContentFile("BuffBarDataAccess.cs");
        SourcePin.AssertUncommented(access, "GetComponentObject<Il2CppSystem.Object>"); // the managed System.Object form has no IL2CPP method store (evidence 102)
        SourcePin.AssertUncommented(src, "Il2CppStructArray<byte>");
        SourcePin.AssertUncommented(src, "ImageConversion.LoadImage");
        SourcePin.AssertUncommented(src, "Sprite.Create");
        SourcePin.AssertUncommented(src, "BootstrapLink.TryGetOrCreateSprite");
        SourcePin.AssertUncommented(src, "BootstrapLink.TryGetCachedSprite");
        SourcePin.AssertUncommented(src, "PrefabIconPath.FormatFromCache");
        SourcePin.AssertStatement(src, "PrefabLiveRefreshApply.AfterIconApplied");
        SourcePin.AssertUncommented(src, "ReleaseReference");
        Assert.DoesNotContain("DestroyLeftoverRoots", src, StringComparison.Ordinal);
        int uninstall = src.IndexOf("public static void Uninstall()", StringComparison.Ordinal);
        int apply = src.IndexOf("private static void Apply(", StringComparison.Ordinal);
        Assert.True(uninstall >= 0 && apply > uninstall);
        string uninstallBody = src.Substring(uninstall, apply - uninstall);
        Assert.DoesNotContain("DestroyUnity", uninstallBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DestroyImmediate", uninstallBody, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot[i].Dispose()", uninstallBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// FT: one self-check line per definition per Core generation, so two consecutive swaps
    /// can be compared by grepping the two instance ids. A dead pair logs the same line at
    /// Warning with <see cref="ContentIconOwnership.NotAliveSuffix"/>.
    /// </summary>
    [Fact]
    public void Icon_cache_self_check_line_is_stable()
    {
        Assert.Equal(
            "PrefabRegistry: icon cache check 'sample.nodrain' sprite=-1234 texture=-1235 alive=True",
            ContentIconOwnership.FormatIconCacheCheck("sample.nodrain", -1234, -1235, alive: true));
        string dead = ContentIconOwnership.FormatIconCacheCheck("sample.nodrain", 0, 0, alive: false);
        Assert.StartsWith(
            "PrefabRegistry: icon cache check 'sample.nodrain' sprite=0 texture=0 alive=False",
            dead, StringComparison.Ordinal);
        Assert.Contains(ContentIconOwnership.NotAliveSuffix, dead, StringComparison.Ordinal);
        Assert.Contains(ContentIconOwnership.IconCacheCheckToken, dead, StringComparison.Ordinal);
    }

    [Fact]
    public void Icon_apply_logs_the_cache_self_check_once_per_swap()
    {
        var src = ReadContentFile("BuffIconApply.cs");
        SourcePin.AssertUncommented(src, "ContentIconOwnership.FormatIconCacheCheck");
        SourcePin.AssertStatement(src, "LogCacheCheck(def, sprite)");
        SourcePin.AssertStatement(src, "if (!Checked.Add(def.Guid.GuidHash)) return;");

        // Uninstall clears the set so the first apply of the next generation logs it again.
        int uninstall = src.IndexOf("public static void Uninstall()", StringComparison.Ordinal);
        int apply = src.IndexOf("private static void Apply(", StringComparison.Ordinal);
        Assert.True(uninstall >= 0 && apply > uninstall);
        SourcePin.AssertStatement(src.Substring(uninstall, apply - uninstall), "Checked.Clear();");
    }

    /// <summary>
    /// The nudge writes the cached sprite in place. It must never decode a new one, never
    /// write a dead pair over a good one, and never replace or clear the Entries list —
    /// the instance's own managed ShowOnBuffBarData copy is all the HUD reads.
    /// </summary>
    [Fact]
    public void Icon_nudge_writes_the_cached_sprite_in_place_and_never_a_dead_one()
    {
        var src = ReadContentFile("BuffIconApply.cs");
        int nudge = src.IndexOf("public static void TryNudge", StringComparison.Ordinal);
        int apply = src.IndexOf("public static void TryApply", StringComparison.Ordinal);
        Assert.True(nudge >= 0 && apply > nudge);
        string body = src.Substring(nudge, apply - nudge);

        SourcePin.AssertUncommented(body, "lease.CurrentSprite");
        SourcePin.AssertUncommented(body, "BootstrapLink.TryGetCachedSprite");
        SourcePin.AssertStatement(body, "if (!IsAlive(sprite))");
        SourcePin.AssertStatement(body, "entry.Icon = sprite;");
        Assert.DoesNotContain("Sprite.Create", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDecodeSprite", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Texture2D", body, StringComparison.Ordinal);
        // Never swap the list itself out from under the HUD.
        Assert.DoesNotContain("data.Entries =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("entries.Clear", body, StringComparison.Ordinal);
        Assert.DoesNotContain("entries.Add", body, StringComparison.Ordinal);
        Assert.DoesNotContain("entries.RemoveAt", body, StringComparison.Ordinal);

        // The HUD copy helper only writes fields on existing entries.
        var access = ReadContentFile("BuffBarDataAccess.cs");
        SourcePin.AssertStatement(access, "d.Icon = s.Icon;");
        Assert.DoesNotContain("dst.Entries =", access, StringComparison.Ordinal);
        Assert.DoesNotContain("dstEntries.Clear", access, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginContent_BuffDefinitionBuilder_forwards_Icon()
    {
        var src = ReadRepoFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
        int start = src.IndexOf("public sealed class BuffDefinitionBuilder", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = src.IndexOf("partial class PluginCastles", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string builder = src.Substring(start, end - start);
        SourcePin.AssertStatement(builder, "public BuffDefinitionBuilder Icon(string relativePath)");
        SourcePin.AssertStatement(builder, "_inner.Icon(relativePath)");
        SourcePin.AssertStatement(builder, "public BuffDefinitionBuilder RefreshLiveInstances()");
        SourcePin.AssertStatement(builder, "_inner.RefreshLiveInstances()");
    }

    [Fact]
    public void Core_debug_buff_calls_Icon_with_the_embedded_png()
    {
        var src = ReadRepoFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
        SourcePin.AssertUncommented(src, "IconRelativePath = \"icons/debug.png\"");
        SourcePin.AssertStatement(src, ".Icon(IconRelativePath)");
        SourcePin.AssertStatement(src, ".Named(LocalizedName)");
        SourcePin.AssertStatement(src, ".Described(LocalizedDescription)");
        SourcePin.AssertStatement(src, ".RefreshLiveInstances()");
    }

    private static string ReadContentFile(string fileName) =>
        ReadRepoFile("src", "Progenitor.Core", "Content", fileName);

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidateParts = new string[parts.Length + 1];
            candidateParts[0] = dir.FullName;
            Array.Copy(parts, 0, candidateParts, 1, parts.Length);
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }
}
