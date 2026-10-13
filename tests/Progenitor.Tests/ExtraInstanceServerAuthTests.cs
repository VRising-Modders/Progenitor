using Progenitor.Dev.Ops;
using Progenitor.Hosted;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the extra-instance Steam-skip band and the server enable rule.
/// The Harmony prefix on <c>ServerBootstrapSystem.Authenticate</c> lives behind
/// <c>!PROGENITOR_TESTS</c>; these tests cover the predicates that prefix consults.
/// </summary>
public class ExtraInstanceServerAuthTests
{
    private const ulong BandBase = 90000000000020000UL;
    private const int BandMax = 99;
    private const ulong GhostBase = 90000000000000001UL;
    private const ulong SteamId = 76561198000000000UL;

    private const string TestServerExe = @"C:\GameModding\_testserver\server\VRisingServer.exe";
    private const string TestServerSave = @"C:\GameModding\_testserver\server\save-data\Saves\v4\test1";
    private const string HostedSave = @"C:\FakeSteam\steamapps\common\VRising\CloudSaves\7656119\v4\progenitor-hosted";
    private const string HostedExe = @"C:\FakeSteam\steamapps\common\VRising\VRising_Server\VRisingServer.exe";
    private const string PublicSave = @"D:\Servers\VRising\save-data\Saves\v4\world1";
    private const string PublicExe = @"D:\Servers\VRising\VRisingServer.exe";

    // ---- band ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(BandBase)]
    [InlineData(BandBase + 1)]
    [InlineData(BandBase + BandMax)]
    public void Extra_instance_ids_are_in_band(ulong id)
    {
        Assert.True(ExtraInstanceServerAuth.IsInBand(id));
    }

    [Theory]
    [InlineData(BandBase - 1)]
    [InlineData(BandBase + BandMax + 1)]
    [InlineData(SteamId)]
    [InlineData(GhostBase)]
    [InlineData(90000000000010000UL)]
    [InlineData(0UL)]
    public void Outside_ids_are_not_in_band(ulong id)
    {
        Assert.False(ExtraInstanceServerAuth.IsInBand(id));
    }

    [Fact]
    public void Band_constants_match_the_documented_extra_client_window()
    {
        Assert.Equal(BandBase, ExtraInstanceServerAuth.BandBase);
        Assert.Equal(BandMax, ExtraInstanceServerAuth.BandMaxIndex);
        Assert.True(ExtraInstanceServerAuth.IsInBand(ExtraInstanceServerAuth.BandBase));
        Assert.True(ExtraInstanceServerAuth.IsInBand(
            ExtraInstanceServerAuth.BandBase + (ulong)ExtraInstanceServerAuth.BandMaxIndex));
    }

    // ---- enable rule --------------------------------------------------------------------------

    [Fact]
    public void Test_server_save_path_is_allowed()
    {
        Assert.True(ExtraInstanceServerAuth.TryAllowEnable(
            "Progenitor Test", TestServerSave, TestServerExe, null, out var reason), reason);
        Assert.Contains("_testserver", reason);
    }

    [Fact]
    public void Test_server_path_is_allowed_even_when_name_is_missing()
    {
        Assert.True(ExtraInstanceServerAuth.TryAllowEnable(
            null, TestServerSave, TestServerExe, null, out var reason), reason);
    }

    [Fact]
    public void Hosted_progenitor_hosted_save_path_is_allowed()
    {
        Assert.True(ExtraInstanceServerAuth.TryAllowEnable(
            "progenitor-hosted", HostedSave, HostedExe, null, out var reason), reason);
        Assert.Contains("progenitor-hosted", reason);
        Assert.DoesNotContain("CloudSaves", reason);
    }

    [Fact]
    public void Hosted_argv_is_allowed_before_the_save_folder_is_known()
    {
        var cmd = @"VRisingServer.exe -persistentDataPath C:\FakeSteam\steamapps\common\VRising\CloudSaves\7656119 -saveName progenitor-hosted";
        Assert.True(ExtraInstanceServerAuth.TryAllowEnable(
            null, null, HostedExe, cmd, out var reason), reason);
        Assert.Contains("progenitor-hosted", reason);
    }

    [Fact]
    public void Live_dedicated_process_is_refused_even_when_named_Progenitor()
    {
        var liveExe = HostedPathRules.ForbiddenLiveServerRoot + @"\VRisingServer.exe";
        var liveSave = HostedPathRules.ForbiddenLiveServerRoot + @"\save-data\Saves\v4\world";
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "Progenitor Test", liveSave, liveExe, null, out var reason));
        Assert.Contains(HostedPathRules.ForbiddenLiveServerRoot, reason);
    }

    [Fact]
    public void Live_dedicated_command_line_is_refused()
    {
        var cmd = @"VRisingServer.exe -persistentDataPath " + HostedPathRules.ForbiddenLiveServerRoot + @"\save-data";
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "Progenitor Test", PublicSave, PublicExe, cmd, out var reason));
        Assert.Contains(HostedPathRules.ForbiddenLiveServerRoot, reason);
    }

    [Fact]
    public void Name_containing_Progenitor_is_not_enough_without_a_test_or_hosted_path()
    {
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "Progenitor Fun", PublicSave, PublicExe, null, out var reason));
        Assert.Contains("name alone is not enough", reason);
    }

    [Fact]
    public void Generic_public_server_is_refused()
    {
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "V Rising Server", PublicSave, PublicExe, null, out var reason));
        Assert.Contains("_testserver", reason);
        Assert.Contains("progenitor-hosted", reason);
    }

    [Fact]
    public void HostedExe_plus_CloudSaves_without_progenitor_hosted_is_refused()
    {
        var save = @"C:\FakeSteam\steamapps\common\VRising\CloudSaves\7656119\v4\world1";
        var cmd = @"VRisingServer.exe -persistentDataPath C:\FakeSteam\steamapps\common\VRising\CloudSaves\7656119 -saveName world1";
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "Private Game", save, HostedExe, cmd, out var reason));
        Assert.Contains("progenitor-hosted", reason);
    }

    [Fact]
    public void CloudSaves_segment_alone_is_not_a_hosted_signal()
    {
        Assert.True(ExtraInstanceServerAuth.ContainsPathSegment(HostedSave, "CloudSaves"));
        Assert.False(ExtraInstanceServerAuth.IsHostedSaveName("world1"));
        Assert.False(ExtraInstanceServerAuth.IsHostedSaveName("CloudSaves"));
        Assert.True(ExtraInstanceServerAuth.IsHostedSaveName("progenitor-hosted"));
        Assert.True(ExtraInstanceServerAuth.IsHostedSaveName("Progenitor-Hosted"));
    }

    [Fact]
    public void SaveName_flag_on_the_command_line_is_read()
    {
        Assert.True(ExtraInstanceServerAuth.TryGetCommandArg(
            @"VRisingServer.exe -saveName progenitor-hosted -gamePort 9886",
            "-saveName",
            out var value));
        Assert.Equal("progenitor-hosted", value);
        Assert.True(ExtraInstanceServerAuth.TryGetCommandArg(
            @"VRisingServer.exe -serverName=progenitor-hosted",
            "-serverName",
            out value));
        Assert.Equal("progenitor-hosted", value);
        Assert.False(ExtraInstanceServerAuth.TryGetCommandArg(
            @"VRisingServer.exe -saveName world1",
            "-saveName",
            out value) && ExtraInstanceServerAuth.IsHostedSaveName(value));
    }

    [Fact]
    public void Empty_inputs_are_refused()
    {
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(null, null, null, null, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Testserverbackup_folder_is_not_the_staged_test_server()
    {
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "V Rising Server",
            @"C:\GameModding\_testserverbackup\server\save-data\Saves\v4\test1",
            @"C:\GameModding\_testserverbackup\server\VRisingServer.exe",
            null,
            out _));
    }

    [Fact]
    public void Live_server_sibling_is_not_the_live_root()
    {
        Assert.False(ExtraInstanceServerAuth.TryAllowEnable(
            "V Rising Server",
            @"C:\VRisingServerBackup\save-data\Saves\v4\world",
            @"C:\VRisingServerBackup\VRisingServer.exe",
            null,
            out var reason));
        Assert.DoesNotContain("live dedicated", reason);
    }

    [Theory]
    [InlineData("Progenitor Test")]
    [InlineData("progenitor-hosted")]
    [InlineData("My Progenitor LAN")]
    public void NameContainsProgenitor_accepts_the_known_labels(string name)
    {
        Assert.True(ExtraInstanceServerAuth.NameContainsProgenitor(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("V Rising Server")]
    [InlineData("Progression")]
    public void NameContainsProgenitor_rejects_other_labels(string? name)
    {
        Assert.False(ExtraInstanceServerAuth.NameContainsProgenitor(name));
    }

    [Fact]
    public void ContainsPathSegment_requires_a_whole_directory_name()
    {
        Assert.True(ExtraInstanceServerAuth.ContainsPathSegment(TestServerSave, "_testserver"));
        Assert.True(ExtraInstanceServerAuth.ContainsPathSegment(HostedSave, "CloudSaves"));
        Assert.False(ExtraInstanceServerAuth.ContainsPathSegment(
            @"C:\GameModding\_testserverbackup\server", "_testserver"));
        Assert.False(ExtraInstanceServerAuth.ContainsPathSegment(null, "_testserver"));
    }

    [Fact]
    public void Authenticate_prefix_omits_the_hail_wrapper()
    {
        Assert.False(ExtraInstanceServerAuth.PrefixDeclaresHailWrapper(
            ExtraInstanceServerAuth.AuthenticatePrefixParameterNames));
        Assert.Contains("__instance", ExtraInstanceServerAuth.AuthenticatePrefixParameterNames);
        Assert.Contains("platformUserId", ExtraInstanceServerAuth.AuthenticatePrefixParameterNames);
        Assert.Contains("netConnectionId", ExtraInstanceServerAuth.AuthenticatePrefixParameterNames);
        Assert.DoesNotContain("hailData", ExtraInstanceServerAuth.AuthenticatePrefixParameterNames);
        Assert.DoesNotContain("__args", ExtraInstanceServerAuth.AuthenticatePrefixParameterNames);
        Assert.False(ExtraInstanceServerAuth.ExtraInstanceIsBotWhenHailOmitted);
    }

    [Fact]
    public void PrefixDeclaresHailWrapper_detects_hailData_and_args()
    {
        Assert.True(ExtraInstanceServerAuth.PrefixDeclaresHailWrapper(new[] { "hailData", "platformUserId" }));
        Assert.True(ExtraInstanceServerAuth.PrefixDeclaresHailWrapper(new[] { "__instance", "__args" }));
        Assert.True(ExtraInstanceServerAuth.PrefixDeclaresHailWrapper(new[] { "HailMessageStruct_Client" }));
        Assert.False(ExtraInstanceServerAuth.PrefixDeclaresHailWrapper(new[] { "__instance", "platformUserId" }));
        Assert.False(ExtraInstanceServerAuth.PrefixDeclaresHailWrapper(null));
    }
}
