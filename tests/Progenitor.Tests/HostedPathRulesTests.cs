using System.IO;
using Progenitor.Hosted;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// HostedPathRules is the game-free hosted-staging surface (which server directories may be
/// staged, when a real directory may be recursive-deleted, and what counts as already staged).
/// Disk I/O, junctions, and the Start prefix live in Staging / HostedServerLauncher (not linked).
/// </summary>
public class HostedPathRulesTests
{
    private const string GameRoot = @"C:\FakeSteam\steamapps\common\VRising";
    private const string BundledServer = GameRoot + @"\VRising_Server";
    private const string ClientBepInEx = GameRoot + @"\BepInEx";

    // ---- TryValidateServerDirectory -----------------------------------------------------------

    [Fact]
    public void Bundled_server_under_game_root_is_allowed()
    {
        Assert.True(HostedPathRules.TryValidateServerDirectory(
            BundledServer, GameRoot, ClientBepInEx, out var reason), reason);
        Assert.Equal("", reason);
    }

    [Fact]
    public void Trailing_slash_and_mixed_case_on_bundled_server_are_allowed()
    {
        Assert.True(HostedPathRules.TryValidateServerDirectory(
            BundledServer + @"\", GameRoot, ClientBepInEx, out var a), a);
        Assert.True(HostedPathRules.TryValidateServerDirectory(
            GameRoot + @"\vrising_server", GameRoot, ClientBepInEx, out var b), b);
        Assert.True(HostedPathRules.TryValidateServerDirectory(
            BundledServer.Replace('\\', '/'), GameRoot, ClientBepInEx, out var c), c);
    }

    [Fact]
    public void Nested_folder_under_game_root_is_allowed()
    {
        Assert.True(HostedPathRules.TryValidateServerDirectory(
            GameRoot + @"\tools\nested-server", GameRoot, ClientBepInEx, out var reason), reason);
    }

    [Fact]
    public void Game_root_itself_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            GameRoot, GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("client game root", reason);
    }

    [Fact]
    public void Game_root_with_trailing_slash_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            GameRoot + @"\", GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("client game root", reason);
    }

    [Fact]
    public void Path_outside_game_root_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            @"D:\other\VRising_Server", GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("not strictly under the game root", reason);
    }

    [Fact]
    public void Sibling_of_game_root_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            @"C:\FakeSteam\steamapps\common\VRisingExtra", GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("not strictly under the game root", reason);
    }

    [Fact]
    public void Relative_escape_out_of_game_root_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            GameRoot + @"\VRising_Server\..\..\..\Windows", GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("not strictly under the game root", reason);
    }

    [Fact]
    public void Live_dedicated_server_root_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            HostedPathRules.ForbiddenLiveServerRoot, GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("live dedicated server", reason);
        Assert.Contains(HostedPathRules.ForbiddenLiveServerRoot, reason);
    }

    [Theory]
    [InlineData(@"C:\VRisingServer")]
    [InlineData(@"C:\VRisingServer\")]
    [InlineData(@"C:\vrisingSERVER")]
    [InlineData(@"C:\VRisingServer\BepInEx")]
    [InlineData(@"C:\VRisingServer\BepInEx\plugins")]
    [InlineData(@"C:/VRisingServer")]
    [InlineData(@"C:\VRisingServer\..\VRisingServer")]
    public void Any_path_under_live_dedicated_server_is_refused(string path)
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(path, GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("live dedicated server", reason);
        Assert.True(HostedPathRules.IsForbiddenLiveServerPath(path));
    }

    [Fact]
    public void Live_server_refusal_wins_even_if_game_root_were_that_folder()
    {
        // An install that somehow lives at C:\VRisingServer is still the forbidden path.
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            @"C:\VRisingServer\VRising_Server",
            HostedPathRules.ForbiddenLiveServerRoot,
            @"C:\VRisingServer\BepInEx",
            out var reason));
        Assert.Contains("live dedicated server", reason);
    }

    [Theory]
    [InlineData(@"C:\VRisingServerBackup")]
    [InlineData(@"C:\VRisingServerExtra")]
    [InlineData(@"C:\VRisingServer.exe")]
    public void Live_server_name_as_a_prefix_is_not_forbidden(string path)
    {
        Assert.False(HostedPathRules.IsForbiddenLiveServerPath(path));
    }

    [Fact]
    public void Client_BepInEx_folder_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            ClientBepInEx, GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("client's BepInEx", reason);
    }

    [Fact]
    public void Folder_inside_client_BepInEx_is_refused()
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            ClientBepInEx + @"\plugins", GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("client's BepInEx", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_server_dir_is_refused(string? serverDir)
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            serverDir, GameRoot, ClientBepInEx, out var reason));
        Assert.Contains("empty", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_game_root_is_refused(string? gameRoot)
    {
        Assert.False(HostedPathRules.TryValidateServerDirectory(
            BundledServer, gameRoot, ClientBepInEx, out var reason));
        Assert.Contains("game root is empty", reason);
    }

    [Fact]
    public void Client_BepInEx_is_optional_when_the_path_is_otherwise_safe()
    {
        Assert.True(HostedPathRules.TryValidateServerDirectory(
            BundledServer, GameRoot, clientBepInEx: null, out var reason), reason);
    }

    // ---- IsUnder / IsStrictlyUnder / PathsEqual -----------------------------------------------

    [Fact]
    public void IsUnder_includes_equality_IsStrictlyUnder_does_not()
    {
        Assert.True(HostedPathRules.IsUnder(GameRoot, GameRoot));
        Assert.False(HostedPathRules.IsStrictlyUnder(GameRoot, GameRoot));
        Assert.True(HostedPathRules.IsUnder(BundledServer, GameRoot));
        Assert.True(HostedPathRules.IsStrictlyUnder(BundledServer, GameRoot));
    }

    [Fact]
    public void PathsEqual_ignores_trailing_separators_and_case()
    {
        Assert.True(HostedPathRules.PathsEqual(GameRoot, GameRoot + @"\"));
        Assert.True(HostedPathRules.PathsEqual(GameRoot, GameRoot.ToLowerInvariant()));
        Assert.False(HostedPathRules.PathsEqual(GameRoot, BundledServer));
    }

    // ---- MayRecursiveDeleteRealDirectory ------------------------------------------------------

    [Fact]
    public void May_delete_a_marked_shared_folder_under_server_BepInEx()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", "plugins");
        Assert.True(HostedPathRules.MayRecursiveDeleteRealDirectory(
            dest, BundledServer, markerPresent: true, out var reason), reason);
    }

    [Fact]
    public void Must_not_delete_an_unmarked_shared_folder()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", "plugins");
        Assert.False(HostedPathRules.MayRecursiveDeleteRealDirectory(
            dest, BundledServer, markerPresent: false, out var reason));
        Assert.Contains(HostedPathRules.StagedCopyMarkerName, reason);
    }

    [Fact]
    public void Must_not_delete_config_even_with_a_marker()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", "config");
        Assert.False(HostedPathRules.MayRecursiveDeleteRealDirectory(
            dest, BundledServer, markerPresent: true, out var reason));
        Assert.Contains("unexpected path", reason);
    }

    [Fact]
    public void Must_not_delete_the_server_directory_itself()
    {
        Assert.False(HostedPathRules.MayRecursiveDeleteRealDirectory(
            BundledServer, BundledServer, markerPresent: true, out var reason));
        Assert.Contains("not inside serverDir", reason);
    }

    [Fact]
    public void Must_not_delete_a_path_outside_serverDir()
    {
        Assert.False(HostedPathRules.MayRecursiveDeleteRealDirectory(
            Path.Combine(GameRoot, "BepInEx", "plugins"),
            BundledServer,
            markerPresent: true,
            out var reason));
        Assert.Contains("not inside serverDir", reason);
    }

    [Fact]
    public void Must_not_delete_anything_under_the_live_dedicated_server()
    {
        var dest = Path.Combine(HostedPathRules.ForbiddenLiveServerRoot, "BepInEx", "plugins");
        Assert.False(HostedPathRules.MayRecursiveDeleteRealDirectory(
            dest, HostedPathRules.ForbiddenLiveServerRoot, markerPresent: true, out var reason));
        Assert.Contains("live dedicated server", reason);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("patchers")]
    [InlineData("unity-libs")]
    [InlineData("plugins")]
    [InlineData("progenitor-reload")]
    [InlineData("Plugins")]
    public void Shared_folder_names_are_recognized(string name)
    {
        Assert.True(HostedPathRules.IsSharedFolderName(name));
        var dest = Path.Combine(BundledServer, "BepInEx", name);
        Assert.True(HostedPathRules.MayRecursiveDeleteRealDirectory(
            dest, BundledServer, markerPresent: true, out var reason), reason);
    }

    // ---- SharedFolderLooksStaged --------------------------------------------------------------

    [Fact]
    public void Reparse_point_at_the_client_folder_counts_as_staged()
    {
        Assert.True(HostedPathRules.SharedFolderLooksStaged(
            destExists: true,
            destIsReparsePoint: true,
            destLinkPointsAtClientSource: true,
            markerPresent: false,
            markerContents: null,
            expectedStamp: "stamp-v1"));
    }

    [Fact]
    public void Reparse_point_pointing_elsewhere_is_not_staged()
    {
        Assert.False(HostedPathRules.SharedFolderLooksStaged(
            destExists: true,
            destIsReparsePoint: true,
            destLinkPointsAtClientSource: false,
            markerPresent: true,
            markerContents: "stamp-v1",
            expectedStamp: "stamp-v1"));
    }

    [Fact]
    public void Copy_with_matching_marker_stamp_counts_as_staged()
    {
        Assert.True(HostedPathRules.SharedFolderLooksStaged(
            destExists: true,
            destIsReparsePoint: false,
            destLinkPointsAtClientSource: false,
            markerPresent: true,
            markerContents: "stamp-v1",
            expectedStamp: "stamp-v1"));
    }

    [Fact]
    public void Copy_with_stale_marker_stamp_is_not_staged()
    {
        Assert.False(HostedPathRules.SharedFolderLooksStaged(
            destExists: true,
            destIsReparsePoint: false,
            destLinkPointsAtClientSource: false,
            markerPresent: true,
            markerContents: "stamp-old",
            expectedStamp: "stamp-v1"));
    }

    [Fact]
    public void Leftover_real_directory_without_marker_is_not_staged()
    {
        Assert.False(HostedPathRules.SharedFolderLooksStaged(
            destExists: true,
            destIsReparsePoint: false,
            destLinkPointsAtClientSource: false,
            markerPresent: false,
            markerContents: null,
            expectedStamp: "stamp-v1"));
    }

    [Fact]
    public void Missing_destination_is_not_staged()
    {
        Assert.False(HostedPathRules.SharedFolderLooksStaged(
            destExists: false,
            destIsReparsePoint: false,
            destLinkPointsAtClientSource: false,
            markerPresent: false,
            markerContents: null,
            expectedStamp: "stamp-v1"));
    }

    [Fact]
    public void Empty_expected_stamp_does_not_match_an_empty_marker()
    {
        Assert.False(HostedPathRules.SharedFolderLooksStaged(
            destExists: true,
            destIsReparsePoint: false,
            destLinkPointsAtClientSource: false,
            markerPresent: true,
            markerContents: "",
            expectedStamp: ""));
    }

    [Fact]
    public void Marker_file_path_is_the_dotfile_inside_the_folder()
    {
        var dest = Path.Combine(BundledServer, "BepInEx", "plugins");
        Assert.Equal(
            Path.Combine(dest, HostedPathRules.StagedCopyMarkerName),
            HostedPathRules.MarkerFilePath(dest));
        Assert.Equal(".progenitor-staged", HostedPathRules.StagedCopyMarkerName);
    }
}
