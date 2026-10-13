using System;
using System.IO;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The Dev bridge runtime refusal (threat model surface 10). <c>DevGate.Evaluate</c> is a pure
/// policy function, so the whole decision table is exercised here with no game, no BepInEx and no
/// socket. The token-file ACL helper takes its side effect as a delegate for the same reason.
/// </summary>
public class DevGateTests
{
    private const string TestServerCommandLine =
        @"VRisingServer.exe -persistentDataPath ""C:\GameModding\_testserver\save-data"" -serverName ""Progenitor Test"" -saveName ""test1""";

    private const string StrangerCommandLine =
        @"VRisingServer.exe -persistentDataPath ""D:\Servers\VRising\save-data"" -saveName ""world1""";

    /// <summary>Opt-in present, client process, loopback bind: the ordinary dev machine.</summary>
    private static DevGateInputs Client(string? marker = DevGate.MarkerText, string? env = null) =>
        new DevGateInputs
        {
            MarkerPath = @"C:\Game\BepInEx\config\progenitor-dev.enable",
            MarkerExists = marker != null,
            MarkerText = marker,
            EnvValue = env,
            IsServerProcess = false,
            BindAddress = "127.0.0.1",
            Port = 27341,
            Server = new ServerExposure(),
        };

    private static DevGateInputs Server(ServerExposure exposure, string? marker = DevGate.MarkerText, string? env = null)
    {
        var inputs = Client(marker, env);
        inputs.IsServerProcess = true;
        inputs.Port = 27342;
        inputs.Server = exposure;
        return inputs;
    }

    private static ServerExposure PrivateTestServer() => new ServerExposure
    {
        SettingsFound = true,
        SettingsPath = @"C:\GameModding\_testserver\save-data\Settings\ServerHostSettings.json",
        ListOnSteam = false,
        ListOnEOS = false,
        LanMode = false,
        PrivateGame = false,
        Port = 9976,
        SaveFolder = @"C:\GameModding\_testserver\save-data",
        ProcessPath = @"C:\GameModding\_testserver\server\VRisingServer.exe",
        CommandLine = TestServerCommandLine,
    };

    // ---- opt-in --------------------------------------------------------------------------------

    [Fact]
    public void NoMarkerAndNoEnv_IsRefused()
    {
        var decision = DevGate.Evaluate(Client(marker: null));

        Assert.False(decision.Allowed);
        Assert.Equal(DevOptInSource.None, decision.OptIn);
        Assert.Contains("no opt-in", decision.Reason);
        Assert.Contains("PROGENITOR_DEV=1", decision.Reason);
        Assert.Contains("docs/bridge.md", decision.Reason);

        // R28 finding 28.17: the refusal used to print the exact sentence a marker file needs, into
        // a log any third-party plugin in the same BepInEx install can read — it told the attacker
        // how to plant the opt-in. The sentence lives in the docs now.
        Assert.DoesNotContain(DevGate.MarkerText, decision.Reason);
        Assert.DoesNotContain(DevGate.MarkerText, DevGate.RefusedLine(decision));
    }

    [Fact]
    public void MarkerWithTheExactSentence_IsAccepted()
    {
        var decision = DevGate.Evaluate(Client());

        Assert.True(decision.Allowed);
        Assert.Equal(DevOptInSource.Marker, decision.OptIn);
        Assert.Equal("marker", decision.OptInLabel);
        Assert.Null(decision.Warning);
    }

    [Fact]
    public void MarkerSentenceMayBeSurroundedByOtherLines()
    {
        var body = "# enabled 2026-09-03 for the dev loop\r\n" + DevGate.MarkerText + "\r\n";

        Assert.True(DevGate.Evaluate(Client(marker: body)).Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("i understand this exposes a local control socket")]
    [InlineData("I understand this exposes a local control")]
    public void MarkerWithoutTheSentence_IsRefusedAndSaysSo(string body)
    {
        var decision = DevGate.Evaluate(Client(marker: body));

        Assert.False(decision.Allowed);
        Assert.Contains("does not contain the opt-in line", decision.Reason);
        Assert.DoesNotContain(DevGate.MarkerText, decision.Reason);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("", false)]
    public void EnvOptIn_OnlyTheExactValueOne(string env, bool allowed)
    {
        var decision = DevGate.Evaluate(Client(marker: null, env: env));

        Assert.Equal(allowed, decision.Allowed);
        if (allowed)
        {
            Assert.Equal(DevOptInSource.Env, decision.OptIn);
            Assert.Equal("env", decision.OptInLabel);
        }
    }

    [Fact]
    public void EnvWinsOverAMarkerThatDoesNotMatch()
    {
        // An extra instance shares the Steam install's config folder; it must be able to opt in
        // without rewriting a marker somebody else owns.
        var decision = DevGate.Evaluate(Client(marker: "nonsense", env: "1"));

        Assert.True(decision.Allowed);
        Assert.Equal(DevOptInSource.Env, decision.OptIn);
    }

    // ---- loopback ------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void LoopbackBindsAreAllowed(string address)
    {
        var inputs = Client();
        inputs.BindAddress = address;

        Assert.True(DevGate.Evaluate(inputs).Allowed);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("")]
    public void NonLoopbackBindIsRefused(string address)
    {
        var inputs = Client();
        inputs.BindAddress = address;

        var decision = DevGate.Evaluate(inputs);

        Assert.False(decision.Allowed);
        Assert.Contains("is not loopback", decision.Reason);
    }

    // ---- server exposure -----------------------------------------------------------------------

    [Fact]
    public void PrivateTestServerIsAllowedWithAWarning()
    {
        var decision = DevGate.Evaluate(Server(PrivateTestServer()));

        Assert.True(decision.Allowed);
        Assert.NotNull(decision.Warning);
        Assert.Contains("dedicated-server process", decision.Warning!);
        Assert.Contains("ListOnSteam=false", decision.Warning!);
    }

    [Fact]
    public void ServerListedOnSteamIsRefused()
    {
        var exposure = PrivateTestServer();
        exposure.ListOnSteam = true;

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.False(decision.Allowed);
        Assert.Contains("listed on a master server", decision.Reason);
        Assert.Contains("ListOnSteam=true", decision.Reason);
    }

    [Fact]
    public void ServerListedOnEosIsRefused()
    {
        var exposure = PrivateTestServer();
        exposure.ListOnEOS = true;

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.False(decision.Allowed);
        Assert.Contains("ListOnEOS=true", decision.Reason);
    }

    [Fact]
    public void ListedServerIsRefusedEvenWithTheEnvOptIn()
    {
        var exposure = PrivateTestServer();
        exposure.ListOnSteam = true;

        var decision = DevGate.Evaluate(Server(exposure, marker: null, env: "1"));

        Assert.False(decision.Allowed);
        Assert.Contains("listed on a master server", decision.Reason);
    }

    [Theory]
    [InlineData("203.0.113.4", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void ExplicitBindAddressDecidesPublicVsDefault(string? address, bool allowed)
    {
        // Stock V Rising has no bind-address setting and listens on every interface, so a wildcard
        // is the default rather than an operator decision; only a real interface address refuses.
        var exposure = PrivateTestServer();
        exposure.Address = address;

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.Equal(allowed, decision.Allowed);
        if (!allowed) Assert.Contains("non-loopback address", decision.Reason);
    }

    [Fact]
    public void UnknownSettingsOnAStrangerHostIsRefused()
    {
        var exposure = new ServerExposure
        {
            SettingsFound = false,
            SettingsPath = @"D:\Servers\VRising\save-data\Settings\ServerHostSettings.json",
            SaveFolder = @"D:\Servers\VRising\save-data",
            ProcessPath = @"D:\Servers\VRising\VRisingServer.exe",
            CommandLine = StrangerCommandLine,
        };

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.False(decision.Allowed);
        Assert.Contains("does not identify itself as a Progenitor test", decision.Reason);
        Assert.Contains("no ServerHostSettings.json was found", decision.Reason);
    }

    /// <summary>
    /// R28 finding 19, the headline. <c>ListOnSteam=false, ListOnEOS=false</c> means "not
    /// advertised", not "not reachable" — a whitelisted or password-protected community server on a
    /// public IP has exactly those values. And the private-instance test used to be conjoined with
    /// <c>!SettingsFound</c>, so finding <b>any</b> settings file waived it: an internet-facing
    /// server never had to look like one of ours at all. Dropping
    /// <c>progenitor-dev.enable</c> into its BepInEx config then handed set/spawn/command/reload on
    /// the live world to anything that could read the token.
    /// </summary>
    [Fact]
    public void AnUnlistedStrangerServerIsStillRefused()
    {
        var exposure = new ServerExposure
        {
            SettingsFound = true,
            SettingsPath = @"D:\Servers\VRising\save-data\Settings\ServerHostSettings.json",
            ListOnSteam = false,
            ListOnEOS = false,
            Port = 9876,
            SaveFolder = @"D:\Servers\VRising\save-data",
            ProcessPath = @"D:\Servers\VRising\VRisingServer.exe",
            CommandLine = StrangerCommandLine,
        };

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.False(decision.Allowed);
        Assert.Contains("does not identify itself as a Progenitor test", decision.Reason);
        Assert.Contains("not-listed is not private", decision.Reason);
    }

    /// <summary>
    /// R28 finding 19's fence: the user's live dedicated server is refused before every other test,
    /// the way <c>ExtraInstanceServerAuth</c> and <c>WorldSaveOps</c> already do it.
    /// </summary>
    [Theory]
    [InlineData(@"C:\VRisingServer\VRisingServer.exe", null)]
    [InlineData(@"C:\vrisingserver\VRisingServer.exe", null)]
    [InlineData(null, @"C:\VRisingServer\save-data")]
    public void TheLiveDedicatedServerIsRefusedBeforeAnythingElse(string? processPath, string? saveFolder)
    {
        var exposure = new ServerExposure
        {
            SettingsFound = true,
            ListOnSteam = false,
            ListOnEOS = false,
            Port = 9876,
            // Even with a _testserver-looking save name, the live root wins.
            SaveFolder = saveFolder ?? @"C:\GameModding\_testserver\save-data",
            ProcessPath = processPath ?? @"C:\GameModding\_testserver\server\VRisingServer.exe",
        };

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.False(decision.Allowed);
        Assert.Contains(DevGate.LiveServerRoot, decision.Reason);
    }

    [Theory]
    [InlineData(@"C:\VRisingServer", true)]
    [InlineData(@"C:\VRisingServer\save-data\Settings", true)]
    [InlineData(@"c:/vrisingserver/save-data", true)]
    [InlineData(@"C:\VRisingServerBackup", false)]
    [InlineData(@"C:\GameModding\_testserver", false)]
    [InlineData(null, false)]
    public void IsUnderLiveServerRootMatchesTheRootAndItsChildrenOnly(string? path, bool expected)
    {
        Assert.Equal(expected, DevGate.IsUnderLiveServerRoot(path));
    }

    /// <summary>
    /// R29 finding 6. The fence was a raw string prefix test — <c>Replace('/','\\')</c>,
    /// <c>TrimEnd('\\')</c>, <c>StartsWith(root + "\\", OrdinalIgnoreCase)</c> — and four ordinary
    /// path forms walked through it onto the user's live dedicated server. Every one of them is
    /// listed here; on the pre-fix code each of these <c>true</c> cases returns <c>false</c>.
    ///
    /// <para>The disk-resolution step is faked, so these run identically on a machine that does not
    /// have <c>C:\VRisingServer</c> (CI) and on one that does. The 8.3 case is deliberately given a
    /// resolver that cannot expand it — that is the "the volume would not tell us" case, and the
    /// fence still has to refuse.</para>
    /// </summary>
    [Theory]
    // extended-length prefix
    [InlineData(@"\\?\C:\VRisingServer\VRisingServer.exe", true)]
    [InlineData(@"\\?\C:\VRisingServerBackup\x.exe", false)]
    // device / UNC forms of an extended-length path
    [InlineData(@"\\?\UNC\localhost\C$\VRisingServer\save-data", true)]
    [InlineData(@"\\.\C:\VRisingServer\save-data", true)]
    // local admin share
    [InlineData(@"\\localhost\C$\VRisingServer\VRisingServer.exe", true)]
    [InlineData(@"\\127.0.0.1\C$\VRisingServer", true)]
    [InlineData(@"\\localhost\C$\GameModding\_testserver", false)]
    // a share on some OTHER host is not this machine's live server
    [InlineData(@"\\fileserver\C$\VRisingServer\x", false)]
    // dot segments
    [InlineData(@"C:\.\VRisingServer\VRisingServer.exe", true)]
    [InlineData(@"C:\GameModding\..\VRisingServer\save-data", true)]
    [InlineData(@"C:\VRisingServer\..\GameModding\_testserver", false)]
    // 8.3 alias that could not be expanded: refused on the root's own volume, allowed elsewhere
    [InlineData(@"C:\VRISIN~1\VRisingServer.exe", true)]
    [InlineData(@"D:\PROGRA~1\SomethingElse\VRisingServer.exe", false)]
    // trailing separators and case, which the old test already covered
    [InlineData(@"C:\VRisingServer\", true)]
    [InlineData(@"c:\vrisingserver\save-data\", true)]
    public void The_live_server_fence_is_not_bypassed_by_an_ordinary_path_form(string path, bool expected)
    {
        Assert.Equal(expected, LivePathFence.IsUnder(path, DevGate.LiveServerRoot, NoDiskResolution));
    }

    /// <summary>
    /// A junction or symlink pointing at the live server root: the string forms share nothing, so
    /// only resolving the link catches it. The resolver here stands in for
    /// <c>FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)</c>.
    /// </summary>
    [Fact]
    public void The_live_server_fence_follows_a_junction_to_its_target()
    {
        static string? Junction(string path)
        {
            const string link = @"D:\live";
            if (path.StartsWith(link + @"\", StringComparison.OrdinalIgnoreCase))
                return DevGate.LiveServerRoot + path.Substring(link.Length);
            if (path.Equals(link, StringComparison.OrdinalIgnoreCase)) return DevGate.LiveServerRoot;
            return null;
        }

        Assert.True(LivePathFence.IsUnder(@"D:\live", DevGate.LiveServerRoot, Junction));
        Assert.True(LivePathFence.IsUnder(@"D:\live\VRisingServer.exe", DevGate.LiveServerRoot, Junction));
        Assert.True(LivePathFence.IsUnder(@"D:\live\save-data\Settings", DevGate.LiveServerRoot, Junction));
        Assert.False(LivePathFence.IsUnder(@"D:\other\VRisingServer.exe", DevGate.LiveServerRoot, Junction));

        // Without the resolution step the same paths are invisible to the fence — which is the
        // whole of R29 finding 6's junction case.
        Assert.False(LivePathFence.IsUnder(@"D:\live\VRisingServer.exe", DevGate.LiveServerRoot, NoDiskResolution));
    }

    /// <summary>
    /// A short-name alias that the volume <i>can</i> expand resolves to the long form and is caught
    /// by the ordinary comparison rather than by the same-volume fallback.
    /// </summary>
    [Fact]
    public void An_expandable_short_name_resolves_to_its_long_form()
    {
        // A name that exists nowhere, so nothing but the fake resolver can expand it. (A real alias
        // of an existing directory is expanded by Path.GetFullPath itself, which is why the theory
        // above passes on a machine that has C:\VRisingServer and on one that does not.)
        static string? Expand(string path) =>
            path.Replace(@"C:\ALIAS~1", DevGate.LiveServerRoot, StringComparison.OrdinalIgnoreCase);

        Assert.True(LivePathFence.IsUnder(@"C:\ALIAS~1\VRisingServer.exe", DevGate.LiveServerRoot, Expand));
        Assert.False(LivePathFence.HasUnresolvedShortName(
            LivePathFence.Canonicalize(@"C:\ALIAS~1\VRisingServer.exe", Expand)));
        Assert.True(LivePathFence.HasUnresolvedShortName(
            LivePathFence.Canonicalize(@"C:\ALIAS~1\VRisingServer.exe", NoDiskResolution)));
    }

    /// <summary>Canonicalization is idempotent and produces one form for the equivalent spellings.</summary>
    [Theory]
    [InlineData(@"\\?\C:\VRisingServer\save-data", @"C:\VRisingServer\save-data")]
    [InlineData(@"\\localhost\C$\VRisingServer", @"C:\VRisingServer")]
    [InlineData(@"C:/VRisingServer/./save-data/", @"C:\VRisingServer\save-data")]
    [InlineData(@"""C:\VRisingServer\save-data""", @"C:\VRisingServer\save-data")]
    [InlineData(@"C:\a\b\..\..\VRisingServer", @"C:\VRisingServer")]
    public void Canonicalize_collapses_the_equivalent_spellings(string input, string expected)
    {
        var canonical = LivePathFence.Canonicalize(input, NoDiskResolution);
        Assert.Equal(expected, canonical, ignoreCase: true);
        Assert.Equal(canonical, LivePathFence.Canonicalize(canonical, NoDiskResolution), ignoreCase: true);
    }

    /// <summary>Stands in for the on-disk step: nothing exists, so nothing resolves.</summary>
    private static string? NoDiskResolution(string path) => null;

    /// <summary>
    /// R28 finding 28.13: <c>HasPrivateInstanceSignal</c> scanned the raw command line, so a bounded
    /// token anywhere in it qualified — <c>-logFile "D:\logs\_testserver.log"</c> or
    /// <c>-serverName "progenitor-hosted"</c> flipped a stranger's server into the allowed branch.
    /// Only the paths that decide where the world lives count.
    /// </summary>
    [Theory]
    [InlineData(@"VRisingServer.exe -persistentDataPath ""D:\Servers\VRising"" -logFile ""D:\logs\_testserver.log""")]
    [InlineData(@"VRisingServer.exe -persistentDataPath ""D:\Servers\VRising"" -serverName ""progenitor-hosted""")]
    [InlineData(@"VRisingServer.exe -persistentDataPath ""D:\Servers\VRising"" -x _testserver")]
    public void AStrangerServerCannotBorrowOurNameFromAnUnrelatedArgument(string commandLine)
    {
        var exposure = new ServerExposure
        {
            SettingsFound = false,
            SaveFolder = @"D:\Servers\VRising\save-data",
            ProcessPath = @"D:\Servers\VRising\VRisingServer.exe",
            CommandLine = commandLine,
        };

        Assert.False(DevGate.HasPrivateInstanceSignal(exposure));
        Assert.False(DevGate.Evaluate(Server(exposure)).Allowed);
    }

    [Fact]
    public void TheSaveNameArgumentStillIdentifiesAHostedChild()
    {
        var exposure = new ServerExposure
        {
            SettingsFound = false,
            SaveFolder = @"D:\Servers\VRising\save-data",
            ProcessPath = @"D:\Servers\VRising\VRisingServer.exe",
            CommandLine = @"VRisingServer.exe -saveName ""progenitor-hosted""",
        };

        Assert.True(DevGate.HasPrivateInstanceSignal(exposure));
    }

    [Theory]
    [InlineData(@"C:\GameModding\_testserver\save-data", null)]
    [InlineData(null, @"C:\Steam\VRising\CloudSaves\7656119\v4\progenitor-hosted")]
    public void UnknownSettingsIsAllowedForOurOwnInstances(string? saveFolder, string? hostedFolder)
    {
        var exposure = new ServerExposure
        {
            SettingsFound = false,
            SaveFolder = saveFolder ?? hostedFolder,
            ProcessPath = @"C:\Steam\VRising\VRising_Server\VRisingServer.exe",
        };

        var decision = DevGate.Evaluate(Server(exposure));

        Assert.True(decision.Allowed);
        Assert.Contains("test or hosted instance", decision.Warning!);
    }

    [Fact]
    public void TestServerBackupIsNotTheTestServer()
    {
        var exposure = new ServerExposure
        {
            SettingsFound = false,
            SaveFolder = @"D:\_testserverbackup\save-data",
            ProcessPath = @"D:\_testserverbackup\VRisingServer.exe",
        };

        Assert.False(DevGate.Evaluate(Server(exposure)).Allowed);
    }

    [Fact]
    public void ClientProcessNeverConsultsServerExposure()
    {
        var inputs = Client();
        inputs.Server = new ServerExposure { ListOnSteam = true, Address = "203.0.113.4" };

        Assert.True(DevGate.Evaluate(inputs).Allowed);
    }

    // ---- log lines -----------------------------------------------------------------------------

    [Fact]
    public void EnabledLineIsTheDocumentedText()
    {
        var decision = DevGate.Evaluate(Client());

        Assert.Equal(
            "Dev bridge: enabled (opt-in=marker, bind=127.0.0.1:27341, token acl=restricted)",
            DevGate.EnabledLine(decision, "127.0.0.1", 27341, DevGate.TokenAclRestricted));
    }

    [Fact]
    public void EnabledLineReportsTheEnvOptInAndADefaultAcl()
    {
        var decision = DevGate.Evaluate(Client(marker: null, env: "1"));

        Assert.Equal(
            "Dev bridge: enabled (opt-in=env, bind=127.0.0.1:27342, token acl=default)",
            DevGate.EnabledLine(decision, "127.0.0.1", 27342, DevGate.TokenAclDefault));
    }

    /// <summary>
    /// The gate's constants pinned to their literals exactly once (R28 finding 29's "expected value
    /// expressed as the constant under test"): the log-line tests above interpolate these same
    /// constants on both sides, so without this a renamed state would keep every assertion green.
    /// </summary>
    [Fact]
    public void The_gate_constants_are_pinned_to_their_literals()
    {
        Assert.Equal("restricted", DevGate.TokenAclRestricted);
        Assert.Equal("default", DevGate.TokenAclDefault);
        Assert.Equal("unverified", DevGate.TokenAclUnverified);
        Assert.Equal("progenitor-dev.enable", DevGate.MarkerFileName);
        Assert.Equal("PROGENITOR_DEV", DevGate.EnvVarName);
        Assert.Equal("1", DevGate.EnvVarValue);
        Assert.Equal("127.0.0.1", DevGate.LoopbackAddress);
        Assert.Equal("ServerHostSettings.json", DevGate.ServerHostSettingsFileName);
        Assert.Equal(@"C:\VRisingServer", DevGate.LiveServerRoot);
        Assert.Equal("I understand this exposes a local control socket", DevGate.MarkerText);
    }

    [Fact]
    public void RefusedLineWrapsTheReason()
    {
        var decision = DevGate.Evaluate(Client(marker: null));

        var line = DevGate.RefusedLine(decision);

        Assert.StartsWith("Dev bridge: refused (", line);
        Assert.EndsWith(")", line);
        Assert.Contains(decision.Reason, line);
    }

    // ---- token ACL -----------------------------------------------------------------------------

    /// <summary>
    /// R28 finding 24. "Restricted" used to be reported from the mere ABSENCE of an exception
    /// across nine reflection steps, so a provider that silently no-ops the DACL — or a file system
    /// that carries no ACLs at all — logged <c>token acl=restricted</c> on a world-readable file.
    /// The tightening is now read back, and a read-back that cannot confirm it says
    /// <c>unverified</c> and warns.
    /// </summary>
    [Fact]
    public void TokenAclReportsRestrictedOnlyWhenTheReadBackConfirmsIt()
    {
        const string path = @"C:\Game\BepInEx\config\progenitor-dev.token";
        var seen = "";
        var verified = "";

        var confirmed = DevGate.ApplyTokenAcl(path, p => seen = p, null, p => { verified = p; return null; });

        Assert.Equal(DevGate.TokenAclRestricted, confirmed);
        Assert.Equal(path, seen);
        Assert.Equal(path, verified);
    }

    [Fact]
    public void TokenAclIsUnverifiedWhenTheReadBackDisagreesOrCannotRun()
    {
        const string path = @"C:\Game\BepInEx\config\progenitor-dev.token";
        var message = "";

        // The DACL is still world-readable: the write "succeeded" and changed nothing.
        var reported = DevGate.ApplyTokenAcl(
            path, _ => { }, m => message = m, _ => "the DACL also grants BUILTIN\\Users");

        Assert.Equal(DevGate.TokenAclUnverified, reported);
        Assert.Contains("NOT confirmed by read-back", message);
        Assert.Contains("BUILTIN\\Users", message);

        // A read-back that throws is also not a confirmation.
        var threw = DevGate.ApplyTokenAcl(
            path, _ => { }, _ => { }, _ => throw new PlatformNotSupportedException("no ACLs here"));
        Assert.Equal(DevGate.TokenAclUnverified, threw);

        // And no read-back at all can never be "restricted".
        Assert.Equal(DevGate.TokenAclUnverified, DevGate.ApplyTokenAcl(path, _ => { }));
    }

    [Theory]
    [InlineData(typeof(PlatformNotSupportedException))]
    [InlineData(typeof(TypeLoadException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(MissingMethodException))]
    public void TokenAclFallsBackToDefaultWhenTheApiThrows(Type exceptionType)
    {
        var warnings = 0;
        var message = "";

        var state = DevGate.ApplyTokenAcl(
            @"C:\Game\BepInEx\config\progenitor-dev.token",
            _ => throw (Exception)Activator.CreateInstance(exceptionType)!,
            m => { warnings++; message = m; });

        Assert.Equal(DevGate.TokenAclDefault, state);
        Assert.Equal(1, warnings);
        Assert.Contains("not restricted", message);
        Assert.Contains(exceptionType.Name, message);
    }

    [Fact]
    public void TokenAclIsDefaultWhenThereIsNoFileOnDisk()
    {
        var warned = false;

        var state = DevGate.ApplyTokenAcl(null, _ => { }, _ => warned = true);

        Assert.Equal(DevGate.TokenAclDefault, state);
        Assert.True(warned);
    }

    /// <summary>
    /// The real reflection path, both halves, against a real file.
    ///
    /// <para>R28 finding 29.3: the old assertion was
    /// <c>state == TokenAclRestricted || state == TokenAclDefault</c> over a method with exactly two
    /// possible returns — the only test that exercised the reflection asserted nothing about whether
    /// the DACL changed, which was finding 24's gap. Now: if the run reports <c>restricted</c>, the
    /// read-back must independently agree, and if it reports anything else the read-back must say
    /// what is wrong. Off Windows both halves degrade together, which is also asserted.</para>
    /// </summary>
    [Fact]
    public void RestrictToCurrentUserIsConfirmedByReadingTheDaclBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "progenitor-devgate-" + Guid.NewGuid().ToString("N") + ".token");
        File.WriteAllText(path, "not-a-real-token");
        try
        {
            var warning = "";
            var state = DevGate.ApplyTokenAcl(
                path,
                DevGate.RestrictToCurrentUser,
                m => warning = m,
                DevGate.VerifyRestrictedToCurrentUser);

            Assert.Contains(state, new[] { DevGate.TokenAclRestricted, DevGate.TokenAclUnverified, DevGate.TokenAclDefault });
            Assert.Equal("not-a-real-token", File.ReadAllText(path));

            if (state == DevGate.TokenAclRestricted)
            {
                // The claim in the log line has to survive an independent second read.
                Assert.Null(DevGate.VerifyRestrictedToCurrentUser(path));
                Assert.Equal("", warning);
            }
            else
            {
                // Anything short of confirmed has to have said why.
                Assert.NotEqual("", warning);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* the ACL rewrite may have locked us out */ }
        }
    }

    /// <summary>
    /// An untouched file in a world-readable temp folder must NOT verify as restricted. Without
    /// this, <see cref="DevGate.VerifyRestrictedToCurrentUser"/> returning null unconditionally
    /// would pass every other ACL test in this file.
    /// </summary>
    [Fact]
    public void AnUntightenedFileDoesNotVerifyAsRestricted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "progenitor-devgate-loose-" + Guid.NewGuid().ToString("N") + ".token");
        File.WriteAllText(path, "not-a-real-token");
        try
        {
            string? problem;
            try { problem = DevGate.VerifyRestrictedToCurrentUser(path); }
            catch (Exception e) { problem = e.Message; }    // no ACL assemblies is also "not confirmed"

            Assert.NotNull(problem);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ---- reading the server's own configuration -------------------------------------------------

    [Fact]
    public void ServerHostSettingsJsonIsParsedForTheFlagsThatMatter()
    {
        // The staged test server's real file, trimmed.
        const string json = @"{
  ""Name"": ""Progenitor Test"",
  ""Port"": 9976,
  ""QueryPort"": 9977,
  ""ListOnSteam"": false,
  ""ListOnEOS"": false,
  ""LanMode"": true,
  ""Rcon"": { ""Enabled"": false },
  ""Authenticate"": true
}";
        var exposure = new ServerExposure();

        Assert.True(DevGate.ApplyServerHostSettings(json, exposure));
        Assert.False(exposure.ListOnSteam);
        Assert.False(exposure.ListOnEOS);
        Assert.True(exposure.LanMode);
        Assert.Equal(9976, exposure.Port);
        Assert.Null(exposure.Address);
    }

    [Fact]
    public void AWrapperListOnMasterServerFlagIsHonoured()
    {
        var exposure = new ServerExposure();

        Assert.True(DevGate.ApplyServerHostSettings(@"{""ListOnMasterServer"": true, ""Port"": 9876}", exposure));
        Assert.True(exposure.ListOnSteam);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void MalformedSettingsAreNotFound(string json)
    {
        Assert.False(DevGate.ApplyServerHostSettings(json, new ServerExposure()));
    }

    /// <summary>
    /// R28 finding 10. <c>ApplyServerHostSettings</c> returned true for <b>any</b> JSON object, so
    /// <c>SettingsFound</c> went true with both listing flags still null and <c>Evaluate</c>'s
    /// <c>== true</c> test was false — settings <i>missing</i> failed closed while settings
    /// <i>present but uninformative</i> failed open. Two reachable shapes: a stray JSON object named
    /// ServerHostSettings.json in the game root (not the file the server actually uses), and
    /// <c>"ListOnSteam": 1</c> or <c>"yes"</c>, which TryBool refuses and which therefore left the
    /// flag null. A file has to carry the keys the gate decides on, or the gate learned nothing.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData(@"{""Name"": ""x""}")]
    [InlineData(@"{""Port"": 9876}")]                              // no listing flag
    [InlineData(@"{""ListOnSteam"": false}")]                      // no port
    [InlineData(@"{""Port"": 9876, ""ListOnSteam"": 1}")]          // a number is not a bool
    [InlineData(@"{""Port"": 9876, ""ListOnSteam"": ""yes""}")]    // nor is "yes"
    public void AnUninformativeSettingsFileIsNotAReading(string json)
    {
        var exposure = new ServerExposure();
        Assert.False(DevGate.ApplyServerHostSettings(json, exposure));
    }

    [Fact]
    public void AnUninformativeSettingsFileDoesNotWaiveThePrivateInstanceTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-devgate-thin-" + Guid.NewGuid().ToString("N"));
        // SERVERDESC1: the game reads <persistentDataPath>\Settings, not the data folder's root.
        Directory.CreateDirectory(Path.Combine(root, "Settings"));
        File.WriteAllText(Path.Combine(root, "Settings", DevGate.ServerHostSettingsFileName), @"{""Name"": ""a stranger""}");
        try
        {
            var exposure = DevGate.ReadServerExposure(
                $"VRisingServer.exe -persistentDataPath \"{root}\" -saveName world1", null);

            Assert.False(exposure.SettingsFound);
            Assert.True(exposure.SettingsIncomplete);

            var decision = DevGate.Evaluate(Server(exposure));
            Assert.False(decision.Allowed);
            Assert.Contains($"carries none of {DevGate.RequiredSettingsKeys}", decision.Reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ReadServerExposureFollowsPersistentDataPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-devgate-" + Guid.NewGuid().ToString("N"));
        var settingsDir = Path.Combine(root, "Settings");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(
            Path.Combine(settingsDir, DevGate.ServerHostSettingsFileName),
            @"{""ListOnSteam"": true, ""Port"": 9876}");
        try
        {
            var commandLine = $"VRisingServer.exe -persistentDataPath \"{root}\" -saveName world1";

            var exposure = DevGate.ReadServerExposure(commandLine, null);

            Assert.True(exposure.SettingsFound);
            Assert.True(exposure.ListOnSteam);
            Assert.Equal(9876, exposure.Port);

            var decision = DevGate.Evaluate(Server(exposure));
            Assert.False(decision.Allowed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ReadServerExposureReportsWhatItTriedWhenNothingIsThere()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-devgate-missing-" + Guid.NewGuid().ToString("N"));

        var exposure = DevGate.ReadServerExposure($"VRisingServer.exe -persistentDataPath \"{root}\"", null);

        Assert.False(exposure.SettingsFound);
        Assert.Contains(DevGate.ServerHostSettingsFileName, exposure.SettingsPath);
    }

    [Fact]
    public void CommandLineAddressIsPickedUp()
    {
        var exposure = DevGate.ReadServerExposure("VRisingServer.exe -address 203.0.113.4 -gamePort 9876", null);

        Assert.Equal("203.0.113.4", exposure.Address);
        Assert.Equal(9876, exposure.Port);
        Assert.False(DevGate.Evaluate(Server(exposure)).Allowed);
    }

    [Theory]
    [InlineData(@"-saveName ""world one"" -x", "-saveName", "world one")]
    [InlineData("-saveName=world1", "-saveName", "world1")]
    [InlineData("-other world1", "-saveName", "")]
    [InlineData("-saveName", "-saveName", "")]
    public void CommandArgsAreParsedWithQuotesAndEquals(string commandLine, string flag, string expected)
    {
        var found = DevGate.TryGetCommandArg(commandLine, flag, out var value);

        Assert.Equal(expected.Length > 0, found);
        Assert.Equal(expected, value);
    }
}
